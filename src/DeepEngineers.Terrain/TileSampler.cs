using System;
using DeepEngineers.Wire;

namespace DeepEngineers.Terrain
{
    /// <summary>
 /// The first-pass depth-band material table (CONTRACT 3.1; Band i holds SN
 /// y in [LowerBoundSnY[i], LowerBoundSnY[i - 1]). The SE plugin resolves <see cref="Names"/> to voxel material
 /// indices once per session and passes them here.
 /// </summary>
    public sealed class MaterialBands
    {
        public static readonly double[] LowerBoundSnY = { 2.0, -40.0, -200.0, -500.0, -900.0, -1300.0, double.NegativeInfinity };
        public static readonly string[] Names = { "Grass", "Sand_02", "Stone_01", "Stone_02", "Stone_03", "Stone_04", "Stone_05" };
        public const int BandCount = 7;

        private readonly byte[] _idx = new byte[BandCount];

        /// <param name="sevenIndices">SE voxel material index per band, in the order of <see cref="Names"/>.</param>
        public MaterialBands(byte[] sevenIndices)
        {
            if (sevenIndices == null || sevenIndices.Length != BandCount) throw new ArgumentException("seven material indices expected", "sevenIndices");
            Array.Copy(sevenIndices, _idx, BandCount);
        }

        /// <summary>Band of an SN y (0 = Grass... 6 = Stone_05; NaN lands in band 6).</summary>
        public static int BandOf(double snY)
        {
            for (int i = 0; i < BandCount - 1; i++)
                if (snY >= LowerBoundSnY[i]) return i;
            return BandCount - 1;
        }

        /// <summary>SE material index for a sample at SN y.</summary>
        public byte For(double snY)
        {
            return _idx[BandOf(snY)];
        }

        /// <summary>
 /// S8a Stage E: the ores of the Subnautica map (<see cref="S8aCraterOreRule"/>: the planet's depth-rarity table by SN
 /// cube and the pockets under Subnautica's large deposits); null = the depth bands alone (S4, S8a off).
 /// </summary>
        public S8aCraterOreRule Ores { get; set; }

        /// <summary>SE material index for a solid sample at SN point <paramref name="sn"/>: an ore pocket's ore, else the depth band.</summary>
        public byte For(Vec3 sn)
        {
            S8aCraterOreRule o = Ores;
            if (o != null)
            {
                byte m = o.MaterialAt(sn.X, sn.Y, sn.Z);
                if (m != 255) return m;
            }
            return _idx[BandOf(sn.Y)];
        }

        /// <summary>The index of band <paramref name="band"/>.</summary>
        public byte IndexOfBand(int band)
        {
            return _idx[band];
        }
    }

    /// <summary>
 /// The VRage-free core of one tile's provider (CONTRACT 3.1 "Content mapping", "Classification", line walks).
 /// Tile voxel i at LOD L is the SE world point <c>tileMin + i·2^L</c> (no half voxel; mutant S4.M6 adds 0.5 m),
 /// mapped per sample through F (<see cref="SiteMap.SeToSn"/>), sampled from the field, and written as SE content in
 /// LOD-voxel units. Thread-safe and allocation-free; one reader guard per call.
 /// </summary>
    public sealed class TileSampler
    {
        public const int TileSize = 1024;
        public const int MaxLod = 10;
        /// <summary>Largest step of a line walk (metres).</summary>
        public const double LineStep = 16.0;

        private readonly ISnField _field;
        private readonly IGuardedField _guarded;
        private readonly SiteMap _map;
        private readonly Vec3 _min;
        private readonly MaterialBands _bands;

        public TileSampler(ISnField field, SiteMap map, Vec3 tileMinWorld, MaterialBands bands)
        {
            if (field == null) throw new ArgumentNullException("field");
            if (map == null) throw new ArgumentNullException("map");
            if (bands == null) throw new ArgumentNullException("bands");
            _field = field;
            _guarded = field as IGuardedField;
            _map = map;
            _min = tileMinWorld;
            _bands = bands;
        }

        public Vec3 TileMinWorld { get { return _min; } }
        public SiteMap Map { get { return _map; } }
        public ISnField Field { get { return _field; } }
        public MaterialBands Bands { get { return _bands; } }

        /// <summary>SE content byte of a distance in LOD-voxel units: <c>(byte)((d·0.5f + 0.5f)·255f)</c> (MyPlanetShapeProvider's expression; d = 0 gives 127).</summary>
        public static byte ContentByte(float d)
        {
            if (!(d > -1f)) return 0;              // NaN and -1 and below
            if (d >= 1f) return 255;
            return (byte)((d * 0.5f + 0.5f) * 255f);
        }

        /// <summary>LOD rule: all 8 stencil samples far and of one sign → ±1 at every LOD; otherwise clamp(D / 2^L, -1, 1).</summary>
        public static float LodDistance(float d0, bool allFarSameSign, int lod)
        {
            if (allFarSameSign) return d0 >= 0f ? 1f : -1f;
            float d = lod == 0 ? d0 : d0 / (1 << lod);
            if (d > 1f) return 1f;
            if (d < -1f) return -1f;
            return d;
        }

        /// <summary>The SE world point of tile voxel (x, y, z) at LOD <paramref name="lod"/> (mutant S4.M6 included).</summary>
        public Vec3 SamplePoint(int lod, int x, int y, int z)
        {
            double s = 1 << lod;
            double h = TerrainMutants.HalfVoxelShift ? 0.5 : 0.0;
            return new Vec3(_min.X + x * s + h, _min.Y + y * s + h, _min.Z + z * s + h);
        }

        /// <summary>
 /// Content of the inclusive LOD box into <c>dst[baseIndex + dx + dy·strideY + dz·strideZ]</c> (d = p - min).
 /// <paramref name="allEmpty"/>/<paramref name="allFull"/>: every written byte is 0 / 255 (both true for an empty
 /// box). While the source is closing every byte is 0.
 /// </summary>
        public void ReadContent(int lod, int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
                                byte[] dst, int baseIndex, int strideY, int strideZ, out bool allEmpty, out bool allFull)
        {
            allEmpty = true;
            allFull = true;
            if (maxX < minX || maxY < minY || maxZ < minZ) return;
            CheckArgs(lod, minX, minY, minZ, maxX, maxY, maxZ, dst, baseIndex, strideY, strideZ);
            bool guarded = false;
            if (_guarded != null)
            {
                guarded = _guarded.BeginRequest();
                if (!guarded)
                {
                    Fill(minX, minY, minZ, maxX, maxY, maxZ, dst, baseIndex, strideY, strideZ, 0);
                    allFull = false;
                    return;
                }
            }
            try
            {
                double s = 1 << lod;
                double h = TerrainMutants.HalfVoxelShift ? 0.5 : 0.0;
                var cache = new LeafCache();
                bool empty = true, full = true;
                for (int z = minZ; z <= maxZ; z++)
                {
                    double wz = _min.Z + z * s + h;
                    int iz = baseIndex + (z - minZ) * strideZ;
                    for (int y = minY; y <= maxY; y++)
                    {
                        double wy = _min.Y + y * s + h;
                        int iy = iz + (y - minY) * strideY;
                        for (int x = minX; x <= maxX; x++)
                        {
                            Vec3 p = _map.SeToSn(new Vec3(_min.X + x * s + h, wy, wz));
                            bool same;
                            float d0 = guarded ? _guarded.SampleUnguarded(p.X, p.Y, p.Z, out same, ref cache) : _field.Sample(p.X, p.Y, p.Z, out same);
                            byte b = ContentByte(LodDistance(d0, same, lod));
                            dst[iy + (x - minX)] = b;
                            if (b != 0) empty = false;
                            if (b != 255) full = false;
                        }
                    }
                }
                allEmpty = empty;
                allFull = full;
            }
            finally
            {
                if (guarded) _guarded.EndRequest();
            }
        }

        /// <summary>
 /// Material of the inclusive LOD box: 255 where the content byte is 0, else the depth band of the sample's SN y (or,
 /// with <see cref="MaterialBands.Ores"/>, the ore pocket the sample lies in).
 /// <paramref name="allEmpty"/>: every written byte is 255.
 /// </summary>
        public void ReadMaterial(int lod, int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
                                 byte[] dst, int baseIndex, int strideY, int strideZ, out bool allEmpty)
        {
            allEmpty = true;
            if (maxX < minX || maxY < minY || maxZ < minZ) return;
            CheckArgs(lod, minX, minY, minZ, maxX, maxY, maxZ, dst, baseIndex, strideY, strideZ);
            bool guarded = false;
            if (_guarded != null)
            {
                guarded = _guarded.BeginRequest();
                if (!guarded)
                {
                    Fill(minX, minY, minZ, maxX, maxY, maxZ, dst, baseIndex, strideY, strideZ, 255);
                    return;
                }
            }
            try
            {
                double s = 1 << lod;
                double h = TerrainMutants.HalfVoxelShift ? 0.5 : 0.0;
                var cache = new LeafCache();
                bool empty = true;
                for (int z = minZ; z <= maxZ; z++)
                {
                    double wz = _min.Z + z * s + h;
                    int iz = baseIndex + (z - minZ) * strideZ;
                    for (int y = minY; y <= maxY; y++)
                    {
                        double wy = _min.Y + y * s + h;
                        int iy = iz + (y - minY) * strideY;
                        for (int x = minX; x <= maxX; x++)
                        {
                            Vec3 p = _map.SeToSn(new Vec3(_min.X + x * s + h, wy, wz));
                            bool same;
                            float d0 = guarded ? _guarded.SampleUnguarded(p.X, p.Y, p.Z, out same, ref cache) : _field.Sample(p.X, p.Y, p.Z, out same);
                            byte m = ContentByte(LodDistance(d0, same, lod)) == 0 ? (byte)255 : _bands.For(p);
                            dst[iy + (x - minX)] = m;
                            if (m != 255) empty = false;
                        }
                    }
                }
                allEmpty = empty;
            }
            finally
            {
                if (guarded) _guarded.EndRequest();
            }
        }

        /// <summary>
 /// Conservative class of the inclusive LOD-0 box (CONTRACT 3.1 "Classification"): the part outside
 /// [0, 1023]³ is empty for SE (a box reaching it is never Full); the 8 corners of the in-tile part are mapped
 /// through F, their SN AABB is expanded by L²/(2·rMin) + 0.01 m (L = largest extent, rMin = distance from the
 /// planet centre to the box, never more than the smallest corner distance), and the field classifies every
 /// sample within 1 m of it.
 /// </summary>
        public ContentClass Classify(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            if (maxX < minX || maxY < minY || maxZ < minZ) return ContentClass.Empty;
            bool outside = minX < 0 || minY < 0 || minZ < 0 || maxX > TileSize - 1 || maxY > TileSize - 1 || maxZ > TileSize - 1;
            int x0 = Math.Max(minX, 0), y0 = Math.Max(minY, 0), z0 = Math.Max(minZ, 0);
            int x1 = Math.Min(maxX, TileSize - 1), y1 = Math.Min(maxY, TileSize - 1), z1 = Math.Min(maxZ, TileSize - 1);
            if (x1 < x0 || y1 < y0 || z1 < z0) return ContentClass.Empty;
            double h = TerrainMutants.HalfVoxelShift ? 0.5 : 0.0;
            double ax = _min.X + x0 + h, ay = _min.Y + y0 + h, az = _min.Z + z0 + h;
            double bx = _min.X + x1 + h, by = _min.Y + y1 + h, bz = _min.Z + z1 + h;
            double loX = double.PositiveInfinity, loY = double.PositiveInfinity, loZ = double.PositiveInfinity;
            double hiX = double.NegativeInfinity, hiY = double.NegativeInfinity, hiZ = double.NegativeInfinity;
            for (int c = 0; c < 8; c++)
            {
                Vec3 p = _map.SeToSn(new Vec3((c & 1) == 0 ? ax : bx, (c & 2) == 0 ? ay : by, (c & 4) == 0 ? az : bz));
                if (p.X < loX) loX = p.X;
                if (p.Y < loY) loY = p.Y;
                if (p.Z < loZ) loZ = p.Z;
                if (p.X > hiX) hiX = p.X;
                if (p.Y > hiY) hiY = p.Y;
                if (p.Z > hiZ) hiZ = p.Z;
            }
            double rMin = DistanceToBox(_map.C, ax, ay, az, bx, by, bz);
            double l = Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
            double e = (rMin > 1.0 ? l * l / (2.0 * rMin) : l) + 0.01;
            ContentClass cls = _field.ClassifySn(loX - e, loY - e, loZ - e, hiX + e, hiY + e, hiZ + e);
            if (cls == ContentClass.Full && outside) return ContentClass.Mixed;
            return cls;
        }

        /// <summary>The expansion Classify applies to a box of largest extent <paramref name="extent"/> at distance <paramref name="rMin"/> from the centre.</summary>
        public static double CurvatureMargin(double extent, double rMin)
        {
            return (rMin > 1.0 ? extent * extent / (2.0 * rMin) : extent) + 0.01;
        }

        private static double DistanceToBox(Vec3 c, double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = c.X < ax ? ax - c.X : c.X > bx ? c.X - bx : 0.0;
            double dy = c.Y < ay ? ay - c.Y : c.Y > by ? c.Y - by : 0.0;
            double dz = c.Z < az ? az - c.Z : c.Z > bz ? c.Z - bz : 0.0;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>
 /// Conservative line test in tile-local metres (LOD-0 voxel units from tileMin): the line is clipped to
 /// [0, 1024]³ (<paramref name="clipFrom"/>/<paramref name="clipTo"/>, fractions of the given line) and walked in
 /// steps of at most 16 m; each step's voxel box is classified. False when every step is Empty (open water);
 /// otherwise <paramref name="start"/>/<paramref name="end"/> are the first and last non-empty step's ends as
 /// fractions of the given line.
 /// </summary>
        public bool IntersectLine(double fx, double fy, double fz, double tx, double ty, double tz,
                                  out double start, out double end, out double clipFrom, out double clipTo)
        {
            start = end = clipFrom = clipTo = 0.0;
            if (double.IsNaN(fx + fy + fz + tx + ty + tz) || double.IsInfinity(fx + fy + fz + tx + ty + tz)) return false;
            double dx = tx - fx, dy = ty - fy, dz = tz - fz;
            double t0 = 0.0, t1 = 1.0;
            if (!Clip(fx, dx, ref t0, ref t1) || !Clip(fy, dy, ref t0, ref t1) || !Clip(fz, dz, ref t0, ref t1)) return false;
            clipFrom = t0;
            clipTo = t1;
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz) * (t1 - t0);
            int n = Math.Max(1, (int)Math.Ceiling(len / LineStep));
            bool found = false;
            double first = 0.0, last = 0.0;
            for (int k = 0; k < n; k++)
            {
                double ta = t0 + (t1 - t0) * k / n, tb = t0 + (t1 - t0) * (k + 1) / n;
                double ax = fx + dx * ta, ay = fy + dy * ta, az = fz + dz * ta;
                double bx = fx + dx * tb, by = fy + dy * tb, bz = fz + dz * tb;
                ContentClass c = Classify(
                    (int)Math.Floor(Math.Min(ax, bx)), (int)Math.Floor(Math.Min(ay, by)), (int)Math.Floor(Math.Min(az, bz)),
                    (int)Math.Ceiling(Math.Max(ax, bx)), (int)Math.Ceiling(Math.Max(ay, by)), (int)Math.Ceiling(Math.Max(az, bz)));
                if (c == ContentClass.Empty) continue;
                if (!found) { first = ta; found = true; }
                last = tb;
            }
            if (!found) return false;
            start = first;
            end = last;
            return true;
        }

        private static bool Clip(double p, double d, ref double t0, ref double t1)
        {
            const double lo = 0.0, hi = TileSize;
            if (Math.Abs(d) < 1e-300) return p >= lo && p <= hi;
            double ta = (lo - p) / d, tb = (hi - p) / d;
            if (ta > tb) { double t = ta; ta = tb; tb = t; }
            if (ta > t0) t0 = ta;
            if (tb < t1) t1 = tb;
            return t0 <= t1;
        }

        private static void CheckArgs(int lod, int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
                                      byte[] dst, int baseIndex, int strideY, int strideZ)
        {
            if ((uint)lod > MaxLod) throw new ArgumentOutOfRangeException("lod", "LOD 0..10");
            if (dst == null) throw new ArgumentNullException("dst");
            if (baseIndex < 0 || strideY < 0 || strideZ < 0) throw new ArgumentOutOfRangeException("baseIndex", "negative index or stride");
            long last = baseIndex + (long)(maxX - minX) + (long)(maxY - minY) * strideY + (long)(maxZ - minZ) * strideZ;
            if (last >= dst.Length) throw new ArgumentException("destination too small for the box", "dst");
        }

        private static void Fill(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, byte[] dst, int baseIndex, int strideY, int strideZ, byte v)
        {
            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                {
                    int i = baseIndex + (z - minZ) * strideZ + (y - minY) * strideY;
                    for (int x = minX; x <= maxX; x++) dst[i + (x - minX)] = v;
                }
        }
    }
}
