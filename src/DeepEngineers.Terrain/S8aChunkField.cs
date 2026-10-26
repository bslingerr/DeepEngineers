using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DeepEngineers.Terrain
{
    /// <summary>One entity of a chunk (its native Subnautica pose; a baked prefab or an entity slot).</summary>
    public sealed class S8aChunkEntity
    {
        public int Index;
        public bool Slot;
        public string ClassId, Family;
        public int Level, SlotBiome, SlotAllowed;
        public double SlotDensity;
        public double X, Y, Z, Qx, Qy, Qz, Qw = 1.0, Sx = 1.0, Sy = 1.0, Sz = 1.0;
    }

    /// <summary>
 /// S8a planet v2, route B (docs/S8A-PLANET-V2.md sections 10 and 12): a chunk of the Subnautica map cut from the user's install
 /// (tools/planet/s8a_chunks.py build; derived data in the store, never shipped): Subnautica's own octree node bytes over a window,
 /// the column tops and the entity records. Read-only after <see cref="Load"/>; every lookup is a descent of one octree (no decode,
 /// no cache, any thread). The twin of s8a_chunks.Chunk.
 /// </summary>
    public sealed class S8aChunk
    {
        public const int S = 32;
        public const int NoneTop = int.MinValue;
        public const double TopUnit = 256.0;
        public int Nx, Ny, Nz, Ox0, Oy0, Oz0;
        public int SizeX, SizeY, SizeZ;
        public double X0, Z0, YFloor;
        public byte[] Nodes;
        public int[] Offsets, Counts;
        public double[] TopM;           // [z * SizeX + x], NaN without a surface
        public ulong BinFnv, IndexFnv;
        public readonly List<S8aChunkEntity> Entities = new List<S8aChunkEntity>();
        public readonly Dictionary<string, string> Head = new Dictionary<string, string>(StringComparer.Ordinal);
        public int Excluded;

        /// <summary>%LOCALAPPDATA%\DeepEngineers\terrain\chunks (s8a_chunks.store_dir).</summary>
        public static string DefaultFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepEngineers", "terrain", "chunks");
        }

        public static string BinName(string name) { return "chunk-" + name + "-v1.bin"; }
        public static string IndexName(string name) { return "chunk-" + name + "-v1.tsv"; }

        public static S8aChunk Load(string binPath, string indexPath)
        {
            var c = new S8aChunk();
            byte[] bin = File.ReadAllBytes(binPath);
            c.BinFnv = RingHash.Fnv(bin);
            if (bin.Length < 34 || Encoding.ASCII.GetString(bin, 0, 8) != "DECHUNK1") throw new InvalidDataException("not a chunk: " + binPath);
            int ver = BitConverter.ToUInt16(bin, 8);
            if (ver != 1) throw new InvalidDataException("chunk version " + ver + " (1 expected): " + binPath);
            c.Nx = BitConverter.ToInt32(bin, 10); c.Ny = BitConverter.ToInt32(bin, 14); c.Nz = BitConverter.ToInt32(bin, 18);
            c.Ox0 = BitConverter.ToInt32(bin, 22); c.Oy0 = BitConverter.ToInt32(bin, 26); c.Oz0 = BitConverter.ToInt32(bin, 30);
            int n = c.Nx * c.Ny * c.Nz;
            if (c.Nx <= 0 || c.Ny <= 0 || c.Nz <= 0 || n > 1 << 20) throw new InvalidDataException("chunk size " + c.Nx + " x " + c.Ny + " x " + c.Nz);
            int off = 34;
            c.Counts = new int[n];
            c.Offsets = new int[n];
            long total = 0;
            for (int k = 0; k < n; k++)
            {
                c.Counts[k] = BitConverter.ToUInt16(bin, off + 2 * k);
                c.Offsets[k] = (int)(total * 4);
                total += c.Counts[k];
            }
            off += 2 * n;
            int ln = BitConverter.ToInt32(bin, off);
            c.Nodes = Inflate(bin, off + 4, ln, (int)(total * 4));
            off += 4 + ln;
            c.SizeX = c.Nx * S; c.SizeY = c.Ny * S; c.SizeZ = c.Nz * S;
            int lt = BitConverter.ToInt32(bin, off);
            byte[] tops = Inflate(bin, off + 4, lt, c.SizeX * c.SizeZ * 4);
            c.TopM = new double[c.SizeX * c.SizeZ];
            for (int i = 0; i < c.TopM.Length; i++)
            {
                int t = BitConverter.ToInt32(tops, 4 * i);
                c.TopM[i] = t == NoneTop ? double.NaN : t / TopUnit;
            }
            c.X0 = -2048.0 + c.Ox0 * S;
            c.Z0 = -2048.0 + c.Oz0 * S;
            c.YFloor = -3040.0 + c.Oy0 * S;
            if (indexPath != null) c.ReadIndex(indexPath);
            c.BuildFast();
            return c;
        }

        // ================================================================================================= the fast path (S8a.NS.cost)
 // ns 20261024-113052: 287 ns per voxel at LOD 0 against SE's 41: 8 octree descents per sample (~60 %) and the heightmap's
 // Catmull-Rom per voxel (~15 %). The fast path gives the same values (the twin and the vectors pin them; S8aChunkFieldTests
 // compares the fast ServeBox with the per-voxel reference over random boxes): decoded octrees in a per-thread cache at LOD 0-1,
 // a voxel far over every top of its 4 x 4 columns (or under the stored floor) is saturated without a descent, boxes wholly in the
 // core over the local tops (or under the floor) are filled at once, and the heightmap is not read where the chunk's weight is 1.

        public const int Tile = 16, CacheSlots = 32;
        /// <summary>Per column: the highest voxel (index from the floor) that is not far water, -1 for none: over it every sample is -1.</summary>
        public int[] NonEmptyTop;
        /// <summary>Per column x, z: the highest <see cref="NonEmptyTop"/> of the columns x..x+1, z..z+1 (the trilinear's four; clamped).</summary>
        public int[] NonEmpty2;
        /// <summary>Per column: the lowest voxel that is not far rock (SizeY for none): under it every sample is +1; NonRock2 the four columns' lowest.</summary>
        public int[] NonRockBottom, NonRock2;
        public int TilesX, TilesZ;
        /// <summary>Per 16 x 16 columns padded by one: the highest non-empty voxel, the highest top (+inf where one is missing), the lowest top (-inf).</summary>
        public int[] TileNonEmpty, TileNonRock;
        public double[] TileTopMax, TileTopMin;
        public static long Decodes;

        private void BuildFast()
        {
            NonEmptyTop = new int[SizeX * SizeZ];
            for (int i = 0; i < NonEmptyTop.Length; i++) NonEmptyTop[i] = -1;
            NonRockBottom = new int[SizeX * SizeZ];
            for (int i = 0; i < NonRockBottom.Length; i++) NonRockBottom[i] = int.MaxValue;
            var b = new Block();
            for (int ix = 0; ix < Nx; ix++)
                for (int iz = 0; iz < Nz; iz++)
                    for (int iy = Ny - 1; iy >= 0; iy--)
                    {
                        int k = (ix * Ny + iy) * Nz + iz;
                        Decode(k, b);
                        for (int lz = 0; lz < S; lz++)
                            for (int lx = 0; lx < S; lx++)
                            {
                                int col = (iz * S + lz) * SizeX + ix * S + lx;
                                if (NonEmptyTop[col] >= 0) continue;
                                int top = -1;
                                if (b.Bad) top = S - 1;
                                else if (b.Uniform >= 0) top = b.Uniform != 0 ? S - 1 : -1;
                                else
                                    for (int ly = S - 1; ly >= 0; ly--)
                                        if (b.Code[lx + (ly << 5) + (lz << 10)] != 0) { top = ly; break; }
                                if (top >= 0) NonEmptyTop[col] = iy * S + top;
                            }
                    }
            for (int ix = 0; ix < Nx; ix++)
                for (int iz = 0; iz < Nz; iz++)
                    for (int iy = 0; iy < Ny; iy++)
                    {
                        int k = (ix * Ny + iy) * Nz + iz;
                        Decode(k, b);
                        for (int lz = 0; lz < S; lz++)
                            for (int lx = 0; lx < S; lx++)
                            {
                                int col = (iz * S + lz) * SizeX + ix * S + lx;
                                if (NonRockBottom[col] != int.MaxValue) continue;
                                int bot = -1;
                                if (b.Bad) bot = 0;
                                else if (b.Uniform >= 0) bot = b.Uniform != 253 ? 0 : -1;
                                else
                                    for (int ly = 0; ly < S; ly++)
                                        if (b.Code[lx + (ly << 5) + (lz << 10)] != 253) { bot = ly; break; }
                                if (bot >= 0) NonRockBottom[col] = iy * S + bot;
                            }
                    }
            for (int i = 0; i < NonRockBottom.Length; i++) if (NonRockBottom[i] == int.MaxValue) NonRockBottom[i] = SizeY;
            NonRock2 = new int[SizeX * SizeZ];
            NonEmpty2 = new int[SizeX * SizeZ];
            for (int z = 0; z < SizeZ; z++)
                for (int x = 0; x < SizeX; x++)
                {
                    int x1 = Math.Min(x + 1, SizeX - 1), z1 = Math.Min(z + 1, SizeZ - 1);
                    NonEmpty2[z * SizeX + x] = Math.Max(Math.Max(NonEmptyTop[z * SizeX + x], NonEmptyTop[z * SizeX + x1]), Math.Max(NonEmptyTop[z1 * SizeX + x], NonEmptyTop[z1 * SizeX + x1]));
                    NonRock2[z * SizeX + x] = Math.Min(Math.Min(NonRockBottom[z * SizeX + x], NonRockBottom[z * SizeX + x1]), Math.Min(NonRockBottom[z1 * SizeX + x], NonRockBottom[z1 * SizeX + x1]));
                }
            TilesX = (SizeX + Tile - 1) / Tile;
            TilesZ = (SizeZ + Tile - 1) / Tile;
            TileNonEmpty = new int[TilesX * TilesZ];
            TileNonRock = new int[TilesX * TilesZ];
            TileTopMax = new double[TilesX * TilesZ];
            TileTopMin = new double[TilesX * TilesZ];
            for (int tz = 0; tz < TilesZ; tz++)
                for (int tx = 0; tx < TilesX; tx++)
                {
                    int ne = -1, nr = int.MaxValue;
                    double mx = double.NegativeInfinity, mn = double.PositiveInfinity;
                    for (int z = Math.Max(0, tz * Tile - 1); z < Math.Min(SizeZ, (tz + 1) * Tile + 1); z++)
                        for (int x = Math.Max(0, tx * Tile - 1); x < Math.Min(SizeX, (tx + 1) * Tile + 1); x++)
                        {
                            ne = Math.Max(ne, NonEmptyTop[z * SizeX + x]);
                            nr = Math.Min(nr, NonRockBottom[z * SizeX + x]);
                            double t = TopM[z * SizeX + x];
                            mx = double.IsNaN(t) ? double.PositiveInfinity : Math.Max(mx, t);
                            mn = double.IsNaN(t) ? double.NegativeInfinity : Math.Min(mn, t);
                        }
                    TileNonEmpty[tz * TilesX + tx] = ne;
                    TileNonRock[tz * TilesX + tx] = nr;
                    TileTopMax[tz * TilesX + tx] = mx;
                    TileTopMin[tz * TilesX + tx] = mn;
                }
            Decodes = 0;
        }

        /// <summary>Over the columns of a chunk-local box (tiles padded by a column): the highest non-empty voxel, the highest and lowest top.</summary>
        public bool TopsOver(double x0, double x1, double z0, double z1, out int nonEmpty, out int nonRock, out double max, out double min)
        {
            nonEmpty = int.MaxValue;
            nonRock = int.MinValue;
            max = double.PositiveInfinity;
            min = double.NegativeInfinity;
            if (x1 < 0 || z1 < 0 || x0 > SizeX || z0 > SizeZ) return false;
            nonEmpty = -1;
            nonRock = int.MaxValue;
            max = double.NegativeInfinity;
            min = double.PositiveInfinity;
            int a0 = Clamp((int)Math.Floor(x0) / Tile, 0, TilesX - 1), a1 = Clamp((int)Math.Floor(x1) / Tile, 0, TilesX - 1);
            int b0 = Clamp((int)Math.Floor(z0) / Tile, 0, TilesZ - 1), b1 = Clamp((int)Math.Floor(z1) / Tile, 0, TilesZ - 1);
            for (int b = b0; b <= b1; b++)
                for (int a = a0; a <= a1; a++)
                {
                    nonEmpty = Math.Max(nonEmpty, TileNonEmpty[b * TilesX + a]);
                    nonRock = Math.Min(nonRock, TileNonRock[b * TilesX + a]);
                    max = Math.Max(max, TileTopMax[b * TilesX + a]);
                    min = Math.Min(min, TileTopMin[b * TilesX + a]);
                }
            return true;
        }

        private sealed class Block
        {
            public S8aChunk Owner;
            public int K = -1;
            public byte[] Code;          // per voxel (x + 32 y + 1024 z): 0 far water, 1..252 Subnautica's near density, 253 far rock
            public int Uniform = -1;     // a one-leaf octree's code (no array)
            public bool Bad;             // a density outside 1..252: the descent serves this octree
        }

        [ThreadStatic] private static Block[] _slots;

        /// <summary>A code's sample: 0 -> -1, 253 -> +1, a near density d -> (d - 126) / 126 (the very doubles <see cref="S8"/> computes).</summary>
        private static readonly double[] CodeS = BuildCodeS();

        private static double[] BuildCodeS()
        {
            var t = new double[256];
            for (int c = 0; c < 256; c++) t[c] = c == 0 ? -1.0 : c == 253 ? 1.0 : (c - 126.0) / 126.0;
            return t;
        }

        private static double OfCode(int c) { return CodeS[c]; }

        private Block SlotOf(int k)
        {
            Block[] slots = _slots ?? (_slots = new Block[CacheSlots]);
            Block b = slots[k & (CacheSlots - 1)];
            if (b == null) slots[k & (CacheSlots - 1)] = b = new Block();
            if (b.Owner != this || b.K != k) Decode(k, b);
            return b;
        }

        private void Decode(int k, Block b)
        {
            b.Owner = this;
            b.K = k;
            b.Bad = false;
            b.Uniform = -1;
            int cnt = Counts[k];
            if (cnt == 0) { b.Uniform = 0; return; }
            int o = Offsets[k];
            byte[] nb = Nodes;
            int fc0 = nb[o + 2] | (nb[o + 3] << 8);
            if (fc0 == 0 || fc0 + 8 > cnt)
            {
                int d0 = nb[o + 1];
                if (d0 > 252) { b.Bad = true; return; }
                b.Uniform = d0 != 0 ? d0 : nb[o] > 0 ? 253 : 0;
                return;
            }
            if (b.Code == null) b.Code = new byte[S * S * S];
            byte[] code = b.Code;
            var stack = new int[64 * 5];
            int sp = 0;
            stack[sp++] = 0; stack[sp++] = 0; stack[sp++] = 0; stack[sp++] = 0; stack[sp++] = S;
            while (sp > 0)
            {
                int size = stack[--sp], z0 = stack[--sp], y0 = stack[--sp], x0 = stack[--sp], node = stack[--sp];
                int q = o + 4 * node;
                int fc = nb[q + 2] | (nb[q + 3] << 8);
                if (fc == 0 || size == 1 || fc + 8 > cnt)
                {
                    int d = nb[q + 1];
                    if (d > 252) { b.Bad = true; return; }
                    byte v = (byte)(d != 0 ? d : nb[q] > 0 ? 253 : 0);
                    for (int z = z0; z < z0 + size; z++)
                        for (int y = y0; y < y0 + size; y++)
                        {
                            int i = x0 + (y << 5) + (z << 10);
                            for (int x = 0; x < size; x++) code[i + x] = v;
                        }
                    continue;
                }
                int h = size >> 1;
                if (sp + 40 > stack.Length) Array.Resize(ref stack, stack.Length * 2);
                for (int c = 0; c < 8; c++)
                {
                    stack[sp++] = fc + c;
                    stack[sp++] = x0 + h * ((c >> 2) & 1);
                    stack[sp++] = y0 + h * ((c >> 1) & 1);
                    stack[sp++] = z0 + h * (c & 1);
                    stack[sp++] = h;
                }
            }
            System.Threading.Interlocked.Increment(ref Decodes);
        }

        /// <summary><see cref="S8"/> through the per-thread decoded-octree cache (32 direct-mapped slots of 32 KB): the same value.</summary>
        public double S8Cached(int x, int y, int z)
        {
            if (y < 0) return 1.0;
            if (y >= SizeY || x < 0 || x >= SizeX || z < 0 || z >= SizeZ) return -1.0;
            int k = ((x >> 5) * Ny + (y >> 5)) * Nz + (z >> 5);
            Block[] slots = _slots ?? (_slots = new Block[CacheSlots]);
            Block b = slots[k & (CacheSlots - 1)];
            if (b == null) slots[k & (CacheSlots - 1)] = b = new Block();
            if (b.Owner != this || b.K != k) Decode(k, b);
            if (b.Uniform >= 0) return OfCode(b.Uniform);
            if (b.Bad) return S8(x, y, z);
            return OfCode(b.Code[(x & 31) + ((y & 31) << 5) + ((z & 31) << 10)]);
        }

        /// <summary>
 /// <see cref="Field"/> by the fast path (the same value): under the stored floor every sample is rock and over the highest
 /// non-empty voxel of the trilinear's four columns every sample is water, without a descent; else the trilinear through the cache
 /// (<paramref name="cached"/>, LOD 0-1) or the descents.
 /// </summary>
        public double FieldFast(double X, double Y, double Z, bool cached)
        {
            double gx = X - 0.5, gy = Y - 0.5, gz = Z - 0.5;
            double fi = Math.Floor(gx), fj = Math.Floor(gy), fk = Math.Floor(gz);
            int i0 = (int)fi, j0 = (int)fj, k0 = (int)fk;
            int ia = Clamp(i0, 0, SizeX - 1), ib = Clamp(i0 + 1, 0, SizeX - 1), ka = Clamp(k0, 0, SizeZ - 1), kb = Clamp(k0 + 1, 0, SizeZ - 1);
            double s;
            if (j0 + 1 < 0) s = 1.0;
            else
            {
                if (j0 > NonEmpty2[ka * SizeX + ia]) s = -1.0;
                else if (j0 + 1 < NonRock2[ka * SizeX + ia]) s = 1.0;
                else
                {
                    double fx = gx - fi, fy = gy - fj, fz = gz - fk;
                    int ja = j0, jb = j0 + 1;
                    double v000, v100, v010, v110, v001, v101, v011, v111;
                    Block blk = null;
                    if (cached && ja >= 0 && jb < SizeY && (ia >> 5) == (ib >> 5) && (ja >> 5) == (jb >> 5) && (ka >> 5) == (kb >> 5))
                    {
                        blk = SlotOf(((ia >> 5) * Ny + (ja >> 5)) * Nz + (ka >> 5));
                        if (blk.Bad) blk = null;
                    }
                    if (blk != null)
                    {
                        // the eight samples in one decoded octree (the common case): one slot lookup, eight table reads
                        if (blk.Uniform >= 0)
                            v000 = v100 = v010 = v110 = v001 = v101 = v011 = v111 = CodeS[blk.Uniform];
                        else
                        {
                            byte[] cd = blk.Code;
                            int b00 = (ia & 31) + ((ja & 31) << 5) + ((ka & 31) << 10);
                            int dxi = ib - ia, dyi = (jb - ja) << 5, dzi = (kb - ka) << 10;
                            v000 = CodeS[cd[b00]]; v100 = CodeS[cd[b00 + dxi]]; v010 = CodeS[cd[b00 + dyi]]; v110 = CodeS[cd[b00 + dxi + dyi]];
                            v001 = CodeS[cd[b00 + dzi]]; v101 = CodeS[cd[b00 + dxi + dzi]]; v011 = CodeS[cd[b00 + dyi + dzi]]; v111 = CodeS[cd[b00 + dxi + dyi + dzi]];
                        }
                    }
                    else if (cached)
                    {
                        v000 = S8Cached(ia, ja, ka); v100 = S8Cached(ib, ja, ka); v010 = S8Cached(ia, jb, ka); v110 = S8Cached(ib, jb, ka);
                        v001 = S8Cached(ia, ja, kb); v101 = S8Cached(ib, ja, kb); v011 = S8Cached(ia, jb, kb); v111 = S8Cached(ib, jb, kb);
                    }
                    else
                    {
                        v000 = S8(ia, ja, ka); v100 = S8(ib, ja, ka); v010 = S8(ia, jb, ka); v110 = S8(ib, jb, ka);
                        v001 = S8(ia, ja, kb); v101 = S8(ib, ja, kb); v011 = S8(ia, jb, kb); v111 = S8(ib, jb, kb);
                    }
                    double c00 = v000 + fx * (v100 - v000);
                    double c10 = v010 + fx * (v110 - v010);
                    double c01 = v001 + fx * (v101 - v001);
                    double c11 = v011 + fx * (v111 - v011);
                    double c0 = c00 + fy * (c10 - c00);
                    double c1 = c01 + fy * (c11 - c01);
                    s = c0 + fz * (c1 - c0);
                    if (s > -1.0 && s < 1.0) return s;
                }
            }
            double top = TopAt(X, Z);
            if (s >= 1.0) return double.IsNaN(top) || Y >= top ? 1.0 : Math.Max(1.0, top - Y);
            return !double.IsNaN(top) && Y > top ? -Math.Max(1.0, Y - top) : -1.0;
        }

        private static byte[] Inflate(byte[] src, int offset, int len, int expect)
        {
            var dst = new byte[expect];
            using (var ms = new MemoryStream(src, offset + 2, len - 2))           // the zlib header (2 bytes) before the deflate stream
            using (var inflate = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < expect)
                {
                    int r = inflate.Read(dst, got, expect - got);
                    if (r <= 0) throw new InvalidDataException("a chunk stream ends early (" + got + " of " + expect + " bytes)");
                    got += r;
                }
            }
            return dst;
        }

        private static double D(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
        private static int I(string s) { return int.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); }

        private void ReadIndex(string indexPath)
        {
            byte[] raw = File.ReadAllBytes(indexPath);
            IndexFnv = RingHash.Fnv(raw);
            string[] lines = Encoding.UTF8.GetString(raw).Split('\n');
            if (lines.Length == 0 || !lines[0].StartsWith("de-chunk-index ", StringComparison.Ordinal)) throw new InvalidDataException("not a chunk index: " + indexPath);
            foreach (string r in lines)
            {
                string ln = r.TrimEnd('\r');
                if (ln.Length == 0) continue;
                string[] f = ln.Split('\t');
                if (f[0] == "baked" && f.Length >= 14)
                {
                    Entities.Add(new S8aChunkEntity
                    {
                        Index = Entities.Count, ClassId = f[1], Family = f[2], Level = I(f[3]), X = D(f[4]), Y = D(f[5]), Z = D(f[6]),
                        Qx = D(f[7]), Qy = D(f[8]), Qz = D(f[9]), Qw = D(f[10]), Sx = D(f[11]), Sy = D(f[12]), Sz = D(f[13])
                    });
                }
                else if (f[0] == "slot" && f.Length >= 11)
                {
                    Entities.Add(new S8aChunkEntity
                    {
                        Index = Entities.Count, Slot = true, Family = "slot", SlotBiome = I(f[1]), SlotAllowed = I(f[2]), SlotDensity = D(f[3]),
                        X = D(f[4]), Y = D(f[5]), Z = D(f[6]), Qx = D(f[7]), Qy = D(f[8]), Qz = D(f[9]), Qw = D(f[10])
                    });
                }
                else if (f[0] == "excluded") Excluded++;
                else if (f.Length >= 2 && !Head.ContainsKey(f[0])) Head[f[0]] = string.Join("\t", f, 1, f.Length - 1);
            }
        }

        /// <summary>
 /// Subnautica's sample at an integer chunk voxel (x, z from the chunk's corner, y from its floor): near (density - 126) / 126,
 /// far +-1 by the type; below the stored range rock, above it water (s8a_chunks.Chunk.svals). One octree descent, decode_octree's
 /// leaf rule: firstChild 0, size 1, or children past the node count.
 /// </summary>
        public double S8(int x, int y, int z)
        {
            if (y < 0) return 1.0;
            if (y >= SizeY || x < 0 || x >= SizeX || z < 0 || z >= SizeZ) return -1.0;
            int ix = x >> 5, iy = y >> 5, iz = z >> 5;
            int k = (ix * Ny + iy) * Nz + iz;
            int cnt = Counts[k];
            if (cnt == 0) return -1.0;
            int b = Offsets[k];
            int lx = x & 31, ly = y & 31, lz = z & 31;
            int node = 0, x0 = 0, y0 = 0, z0 = 0, size = S;
            byte[] nb = Nodes;
            while (true)
            {
                int q = b + 4 * node;
                int fc = nb[q + 2] | (nb[q + 3] << 8);
                if (fc == 0 || size == 1 || fc + 8 > cnt)
                {
                    int d = nb[q + 1];
                    if (d != 0) return (d - 126.0) / 126.0;
                    return nb[q] > 0 ? 1.0 : -1.0;
                }
                int h = size >> 1;
                int c = 0;
                if (lx - x0 >= h) { c += 4; x0 += h; }
                if (ly - y0 >= h) { c += 2; y0 += h; }
                if (lz - z0 >= h) { c += 1; z0 += h; }
                node = fc + c;
                size = h;
            }
        }

        /// <summary>The Subnautica block type of a chunk voxel when it is solid (near from 126, far by its type), else 0; outside the stored range 0.</summary>
        public byte TypeAt(int x, int y, int z)
        {
            if (y < 0 || y >= SizeY || x < 0 || x >= SizeX || z < 0 || z >= SizeZ) return 0;
            int k = ((x >> 5) * Ny + (y >> 5)) * Nz + (z >> 5);
            int cnt = Counts[k];
            if (cnt == 0) return 0;
            int b = Offsets[k];
            int lx = x & 31, ly = y & 31, lz = z & 31;
            int node = 0, x0 = 0, y0 = 0, z0 = 0, size = S;
            byte[] nb = Nodes;
            while (true)
            {
                int q = b + 4 * node;
                int fc = nb[q + 2] | (nb[q + 3] << 8);
                if (fc == 0 || size == 1 || fc + 8 > cnt)
                {
                    int d = nb[q + 1];
                    bool solid = d != 0 ? d >= 126 : nb[q] > 0;
                    return solid ? nb[q] : (byte)0;
                }
                int h = size >> 1;
                int c = 0;
                if (lx - x0 >= h) { c += 4; x0 += h; }
                if (ly - y0 >= h) { c += 2; y0 += h; }
                if (lz - z0 >= h) { c += 1; z0 += h; }
                node = fc + c;
                size = h;
            }
        }

        /// <summary>The trilinear of the samples over the voxel centres (x, then y, then z), horizontally clamped (s8a_chunks.Chunk.tri).</summary>
        public double Trilinear(double X, double Y, double Z)
        {
            double gx = X - 0.5, gy = Y - 0.5, gz = Z - 0.5;
            double fi = Math.Floor(gx), fj = Math.Floor(gy), fk = Math.Floor(gz);
            double fx = gx - fi, fy = gy - fj, fz = gz - fk;
            int i0 = (int)fi, j0 = (int)fj, k0 = (int)fk;
            int ia = Clamp(i0, 0, SizeX - 1), ib = Clamp(i0 + 1, 0, SizeX - 1), ka = Clamp(k0, 0, SizeZ - 1), kb = Clamp(k0 + 1, 0, SizeZ - 1);
            int ja = j0, jb = j0 + 1;
            double v000 = S8(ia, ja, ka), v100 = S8(ib, ja, ka), v010 = S8(ia, jb, ka), v110 = S8(ib, jb, ka);
            double v001 = S8(ia, ja, kb), v101 = S8(ib, ja, kb), v011 = S8(ia, jb, kb), v111 = S8(ib, jb, kb);
            double c00 = v000 + fx * (v100 - v000);
            double c10 = v010 + fx * (v110 - v010);
            double c01 = v001 + fx * (v101 - v001);
            double c11 = v011 + fx * (v111 - v011);
            double c0 = c00 + fy * (c10 - c00);
            double c1 = c01 + fy * (c11 - c01);
            return c0 + fz * (c1 - c0);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        /// <summary>The column top (metres over the floor) bilinear over the column centres, NaN where a corner has none (s8a_chunks.Chunk.top_at).</summary>
        public double TopAt(double X, double Z)
        {
            double gx = X - 0.5, gz = Z - 0.5;
            double fi = Math.Min(Math.Max(Math.Floor(gx), 0.0), SizeX - 2.0), fk = Math.Min(Math.Max(Math.Floor(gz), 0.0), SizeZ - 2.0);
            double fx = Math.Min(Math.Max(gx - fi, 0.0), 1.0), fz = Math.Min(Math.Max(gz - fk, 0.0), 1.0);
            int i0 = (int)fi, k0 = (int)fk;
            int b = k0 * SizeX + i0;
            double t00 = TopM[b], t10 = TopM[b + 1], t01 = TopM[b + SizeX], t11 = TopM[b + SizeX + 1];
            double a = t00 + fx * (t10 - t00);
            double c = t01 + fx * (t11 - t01);
            return a + fz * (c - a);
        }

        /// <summary>
 /// The chunk's own signed distance (m, positive in rock) at a chunk-local point: the samples' near band, beyond it the vertical
 /// distance to the column top (rock under it, water over it), -1 under an overhang (s8a_chunks.Chunk.field).
 /// </summary>
        public double Field(double X, double Y, double Z)
        {
            double s = Trilinear(X, Y, Z);
            if (s > -1.0 && s < 1.0) return s;
            double top = TopAt(X, Z);
            if (s >= 1.0) return double.IsNaN(top) || Y >= top ? 1.0 : Math.Max(1.0, top - Y);
            return !double.IsNaN(top) && Y > top ? -Math.Max(1.0, Y - top) : -1.0;
        }
    }

    /// <summary>The sample's placement (sample-v1.txt, s8a_chunks.placement_text): anchor and frame (the twin's repr bits), turns, re-base, blend.</summary>
    public sealed class S8aSamplePlacement
    {
        public string Chunk = "gp1";
        public double SnX, SnZ;
        public double Ax, Ay, Az, Ex, Ey, Ez, Nx, Ny, Nz;
        public int Turns, Mirror;
        public double RebaseM, BlendM = 80.0, HalfM;
        public ulong ChunkFnv, IndexFnv, TextFnv;
        public string Text;

        public static string DefaultPath() { return Path.Combine(S8aChunk.DefaultFolder(), "sample-v1.txt"); }

        public static S8aSamplePlacement Parse(string text)
        {
            var p = new S8aSamplePlacement { Text = text, TextFnv = RingHash.FnvUtf8(text) };
            string[] lines = text.Split('\n');
            if (lines.Length == 0 || !lines[0].StartsWith("de-sample ", StringComparison.Ordinal)) throw new InvalidDataException("not a sample placement");
            foreach (string r in lines)
            {
                string[] f = r.TrimEnd('\r').Split(' ');
                switch (f[0])
                {
                    case "chunk": p.Chunk = f[1]; break;
                    case "chunkFnv": p.ChunkFnv = ulong.Parse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture); break;
                    case "indexFnv": p.IndexFnv = ulong.Parse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture); break;
                    case "sn": p.SnX = D(f[1]); p.SnZ = D(f[2]); break;
                    case "anchor": p.Ax = D(f[1]); p.Ay = D(f[2]); p.Az = D(f[3]); break;
                    case "east": p.Ex = D(f[1]); p.Ey = D(f[2]); p.Ez = D(f[3]); break;
                    case "north": p.Nx = D(f[1]); p.Ny = D(f[2]); p.Nz = D(f[3]); break;
                    case "turns": p.Turns = int.Parse(f[1], CultureInfo.InvariantCulture); break;
                    case "mirror": p.Mirror = int.Parse(f[1], CultureInfo.InvariantCulture); break;
                    case "rebaseM": p.RebaseM = D(f[1]); break;
                    case "blendM": p.BlendM = D(f[1]); break;
                    case "halfM": p.HalfM = D(f[1]); break;
                }
            }
            if (p.HalfM <= 0 || p.BlendM <= 0) throw new InvalidDataException("sample placement without halfM / blendM");
            return p;
        }

        private static double D(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
    }

    /// <summary>
 /// S8a planet v2, the sample (route B's first slice): one chunk placed through the F2 field at a fixed planet point, in the point's
 /// own tangent frame (gnomonic on the sea sphere: no stretching), a quarter turn, re-based, blended into SE's heightmap over the
 /// band inside its square. f(P) at planet-local points of the vanilla generator frame, positive in rock; the twin of
 /// s8a_chunks.Sample: IEEE double arithmetic in the twin's order, square roots only (vectors: tests/s8a/vectors/sample/).
 /// </summary>
    public sealed class S8aChunkField
    {
        private static readonly double[] TurnC = { 1.0, 0.0, -1.0, 0.0 }, TurnS = { 0.0, 1.0, 0.0, -1.0 };
        public readonly S8aChunk Chunk;
        public readonly S8aSamplePlacement Place;
        public readonly RingBase Base;
        private readonly double _c, _s;
        /// <summary>The zone: planet-local bounds (padded) and its altitude range; outside it the field equals the heightmap's.</summary>
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ, YLo, YHi;
        public const double ReachMarginM = 16.0;
        /// <summary>Every heightmap sample over the footprint lies on face 1's interior (the core may skip the heightmap: weight 1).</summary>
        public bool CoreOnFace { get; private set; }
        public static long BoxesFilled, VoxelsFast, VoxelsBand;

        public S8aChunkField(S8aChunk chunk, S8aSamplePlacement place, RingBase b)
        {
            Chunk = chunk;
            Place = place;
            Base = b;
            _c = TurnC[((place.Turns % 4) + 4) % 4];
            _s = TurnS[((place.Turns % 4) + 4) % 4];
            ComputeZone();
        }

        /// <summary>A sample identity for the store keys: fnv1a64(utf8 'sample|' + placement text x16 + '|' + chunk bin x16 + '|' + index x16).</summary>
        public ulong Identity()
        {
            return RingHash.FnvUtf8("sample|" + Place.TextFnv.ToString("x16", CultureInfo.InvariantCulture) + "|" + Chunk.BinFnv.ToString("x16", CultureInfo.InvariantCulture) + "|" +
                                    Chunk.IndexFnv.ToString("x16", CultureInfo.InvariantCulture));
        }

        /// <summary>Face-1 pixel coordinates of a planet-local point (MyCubemapHelpers face 1: tx = X / Z, ty = -Y / Z).</summary>
        public static void PxOfLocal(double px, double py, double pz, out double qx, out double qy)
        {
            double tx = px / pz;
            double ty = -py / pz;
            qx = (tx + 1.0) * 0.5 * (RingRules.Res - 1.0);
            qy = (ty + 1.0) * 0.5 * (RingRules.Res - 1.0);
        }

        /// <summary>Tangent coordinates (u, v on the sea sphere's gnomonic plane at the anchor), the altitude and P.A of a planet-local point.</summary>
        public void Tangent(double px, double py, double pz, out double u, out double v, out double y, out double d)
        {
            S8aSamplePlacement p = Place;
            d = px * p.Ax + py * p.Ay + pz * p.Az;
            double pe = px * p.Ex + py * p.Ey + pz * p.Ez;
            double pn = px * p.Nx + py * p.Ny + pz * p.Nz;
            u = RingRules.Rs * pe / d;
            v = RingRules.Rs * pn / d;
            y = Math.Sqrt(px * px + py * py + pz * pz) - RingRules.Rs;
        }

        /// <summary>Native offsets from the chunk's centre of tangent coordinates (the inverse of the entities' turn).</summary>
        public void Unturn(double u, double v, out double dx, out double dz)
        {
            dx = _c * u - _s * v;
            dz = _s * u + _c * v;
        }

        /// <summary>Tangent coordinates of native offsets (M_k: Unity's yaw of 90 k degrees does the same to a native rotation).</summary>
        public void Turn(double dx, double dz, out double u, out double v)
        {
            u = _c * dx + _s * dz;
            v = -_s * dx + _c * dz;
        }

        /// <summary>The planet-local point of tangent coordinates and an altitude (the inverse gnomonic).</summary>
        public void LocalOfTangent(double u, double v, double y, out double px, out double py, out double pz)
        {
            S8aSamplePlacement p = Place;
            double dx = p.Ax + (u / RingRules.Rs) * p.Ex + (v / RingRules.Rs) * p.Nx;
            double dy = p.Ay + (u / RingRules.Rs) * p.Ey + (v / RingRules.Rs) * p.Ny;
            double dz = p.Az + (u / RingRules.Rs) * p.Ez + (v / RingRules.Rs) * p.Nz;
            double n = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double r = RingRules.Rs + y;
            px = dx / n * r; py = dy / n * r; pz = dz / n * r;
        }

        /// <summary>A native Subnautica point of the chunk (an entity) -> planet-local: turned about the chunk's centre, re-based.</summary>
        public void NativeToLocal(double x, double y, double z, out double px, out double py, out double pz)
        {
            double u, v;
            Turn(x - (Chunk.X0 + Place.HalfM), z - (Chunk.Z0 + Place.HalfM), out u, out v);
            LocalOfTangent(u, v, y + Place.RebaseM, out px, out py, out pz);
        }

        /// <summary>The native point (SN coordinates of the real map, before the re-base) of a planet-local point.</summary>
        public void LocalToNative(double px, double py, double pz, out double x, out double y, out double z)
        {
            double u, v, yy, d, dx, dz;
            Tangent(px, py, pz, out u, out v, out yy, out d);
            Unturn(u, v, out dx, out dz);
            x = Chunk.X0 + Place.HalfM + dx;
            z = Chunk.Z0 + Place.HalfM + dz;
            y = yy - Place.RebaseM;
        }

        private static double Smooth(double e0, double e1, double x)
        {
            double t = Math.Min(Math.Max((x - e0) / (e1 - e0), 0.0), 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>
 /// The field at a planet-local point: SE's heightmap seabed - altitude outside the square; inside, the chunk's own field blended
 /// with it over the band (weight smoothstep(0, blend, the distance inside the square)). <paramref name="h"/>: the heightmap's seabed
 /// (NaN off face 1's interior), <paramref name="w"/>: the chunk's weight.
 /// </summary>
        public double Sample(double px, double py, double pz, out double h, out double w)
        {
            double u, v, y, d;
            Tangent(px, py, pz, out u, out v, out y, out d);
            double dx = _c * u - _s * v;
            double dz = _s * u + _c * v;
            double half = Place.HalfM;
            double e = Math.Min(half - Math.Abs(dx), half - Math.Abs(dz));
            double qx, qy;
            PxOfLocal(px, py, pz, out qx, out qy);
            int key;
            Base.ColumnPx(qx, qy, out h, out key);
            double fb = h - y;
            w = 0.0;
            if (!(e > 0.0 && d > 0.0)) return fb;
            double X = half + dx, Z = half + dz;
            double Y = (y - Place.RebaseM) - Chunk.YFloor;
            double fc = Chunk.Field(X, Y, Z);
            w = Smooth(0.0, Place.BlendM, e);
            return w * fc + (1.0 - w) * fb;
        }

        /// <summary>
 /// The content bytes served for a planet-local box (voxel (i, j, k) at (ax + i lod, ay + j lod, az + k lod)) into
 /// <paramref name="dst"/> at origin + i + j stepY + k stepZ (s8a_chunks.Sample.serve_box). Bit 0: every byte 0, bit 1: every byte 255.
 /// </summary>
        public int ServeBox(double ax, double ay, double az, int sx, int sy, int sz, double lod, byte[] dst, int origin, int stepY, int stepZ)
        {
            int fill = CoreFill(ax, ay, az, ax + (sx - 1) * lod, ay + (sy - 1) * lod, az + (sz - 1) * lod, lod);
            if (fill >= 0)
            {
                byte v = (byte)fill;
                for (int k = 0; k < sz; k++)
                    for (int j = 0; j < sy; j++)
                    {
                        int idx = origin + j * stepY + k * stepZ;
                        for (int i = 0; i < sx; i++) dst[idx + i] = v;
                    }
                System.Threading.Interlocked.Increment(ref BoxesFilled);
                return v == 0 ? 1 : 2;
            }
            bool cached = lod <= 2.0;
            bool allEmpty = true, allFull = true;
            long band = 0;
            for (int k = 0; k < sz; k++)
                for (int j = 0; j < sy; j++)
                {
                    int idx = origin + j * stepY + k * stepZ;
                    for (int i = 0; i < sx; i++, idx++)
                    {
                        double px = ax + i * lod, py = ay + j * lod, pz = az + k * lod;
                        double u, v, y, d;
                        Tangent(px, py, pz, out u, out v, out y, out d);
                        double dx = _c * u - _s * v;
                        double dz = _s * u + _c * v;
                        double half = Place.HalfM;
                        double e = Math.Min(half - Math.Abs(dx), half - Math.Abs(dz));
                        byte c;
                        if (CoreOnFace && e >= Place.BlendM && d > 0.0)
                        {
                            // the chunk's weight is 1: w fc + (1 - w) fb = fc (+-0 apart, the same byte)
                            c = RingField.Content(Chunk.FieldFast(half + dx, (y - Place.RebaseM) - Chunk.YFloor, half + dz, cached), lod);
                        }
                        else
                        {
                            band++;
                            double qx, qy, h;
                            int key;
                            PxOfLocal(px, py, pz, out qx, out qy);
                            Base.ColumnPx(qx, qy, out h, out key);
                            double f = h - y;
                            if (e > 0.0 && d > 0.0)
                            {
                                double fc = Chunk.FieldFast(half + dx, (y - Place.RebaseM) - Chunk.YFloor, half + dz, cached);
                                double w = Smooth(0.0, Place.BlendM, e);
                                f = w * fc + (1.0 - w) * f;
                            }
                            c = double.IsNaN(h) ? (byte)0 : RingField.Content(f, lod);
                        }
                        dst[idx] = c;
                        if (c != 0) allEmpty = false;
                        if (c != 255) allFull = false;
                    }
                }
            System.Threading.Interlocked.Add(ref VoxelsFast, (long)sx * sy * sz);
            System.Threading.Interlocked.Add(ref VoxelsBand, band);
            return (allEmpty ? 1 : 0) | (allFull ? 2 : 0);
        }

        /// <summary>
 /// A box wholly in the core (the chunk's weight 1 at every voxel) over the local tops by max(4 m, the LOD) + 1 m: every voxel's
 /// field is <= -lod (content 0); under the stored floor and the local tops by the LOD: >= lod (content 255); else -1.
 /// </summary>
        public int CoreFill(double ax, double ay, double az, double bx, double by, double bz, double lod)
        {
            if (!CoreOnFace) return -1;
            double emin = double.PositiveInfinity, xmin = double.PositiveInfinity, xmax = double.NegativeInfinity, zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
            double ymin = double.PositiveInfinity, ymax = double.NegativeInfinity;
            double half = Place.HalfM;
            for (int c = 0; c < 8; c++)
            {
                double u, v, y, d, dx, dz;
                Tangent((c & 1) == 0 ? ax : bx, (c & 2) == 0 ? ay : by, (c & 4) == 0 ? az : bz, out u, out v, out y, out d);
                if (!(d > 0)) return -1;
                Unturn(u, v, out dx, out dz);
                emin = Math.Min(emin, Math.Min(half - Math.Abs(dx), half - Math.Abs(dz)));
                xmin = Math.Min(xmin, dx); xmax = Math.Max(xmax, dx);
                zmin = Math.Min(zmin, dz); zmax = Math.Max(zmax, dz);
                ymin = Math.Min(ymin, y); ymax = Math.Max(ymax, y);
            }
            // the box's interior: the tangent map is near-linear over a box (<= 256 m: < 0.2 m off), padded by 2 m; the altitude by 1 m
            if (emin < Place.BlendM + 2.0) return -1;
            int ne, nr;
            double tmax, tmin;
            if (!Chunk.TopsOver(half + xmin - 3.0, half + xmax + 3.0, half + zmin - 3.0, half + zmax + 3.0, out ne, out nr, out tmax, out tmin)) return -1;
            double y0 = (ymin - 1.0 - Place.RebaseM) - Chunk.YFloor, y1 = (ymax + 1.0 - Place.RebaseM) - Chunk.YFloor;
            // water: every voxel's lower trilinear row over every non-empty voxel (all samples -1), and at least the LOD over every top
            if (y0 - 1.5 > ne && !double.IsInfinity(tmax) && y0 >= tmax + lod) return 0;
            // rock: every voxel's upper trilinear row under every column's lowest non-rock voxel (all samples +1; the stored floor's
 // rock too), and every top at least the LOD over the box
            if (!double.IsInfinity(tmin) && y1 + 1.5 < nr && tmin - y1 >= lod) return 255;
            return -1;
        }

        /// <summary>The reference: <see cref="Sample"/> per voxel (the twin's own order), for the tests' comparison with <see cref="ServeBox"/>.</summary>
        public int ServeBoxReference(double ax, double ay, double az, int sx, int sy, int sz, double lod, byte[] dst, int origin, int stepY, int stepZ)
        {
            bool allEmpty = true, allFull = true;
            for (int k = 0; k < sz; k++)
                for (int j = 0; j < sy; j++)
                {
                    int idx = origin + j * stepY + k * stepZ;
                    for (int i = 0; i < sx; i++, idx++)
                    {
                        double h, w;
                        double f = Sample(ax + i * lod, ay + j * lod, az + k * lod, out h, out w);
                        byte c = double.IsNaN(h) ? (byte)0 : RingField.Content(f, lod);
                        dst[idx] = c;
                        if (c != 0) allEmpty = false;
                        if (c != 255) allFull = false;
                    }
                }
            return (allEmpty ? 1 : 0) | (allFull ? 2 : 0);
        }

        private void ComputeZone()
        {
            double half = Place.HalfM;
            double hmin = double.PositiveInfinity, hmax = double.NegativeInfinity;
            bool anyNaN = false;
            for (int a = 0; a <= 8; a++)
                for (int b = 0; b <= 8; b++)
                {
                    double px, py, pz, qx, qy, h;
                    int key;
                    LocalOfTangent(-half + 2.0 * half * a / 8.0, -half + 2.0 * half * b / 8.0, 0.0, out px, out py, out pz);
                    PxOfLocal(px, py, pz, out qx, out qy);
                    Base.ColumnPx(qx, qy, out h, out key);
                    if (double.IsNaN(h)) { anyNaN = true; continue; }
                    hmin = Math.Min(hmin, h);
                    hmax = Math.Max(hmax, h);
                }
            if (double.IsInfinity(hmin)) throw new InvalidDataException("the sample's footprint is off face 1's interior");
            CoreOnFace = !anyNaN;
            YLo = Math.Min(Chunk.YFloor + Place.RebaseM, hmin) - ReachMarginM;
            YHi = Math.Max(Chunk.YFloor + Chunk.SizeY + Place.RebaseM, hmax) + ReachMarginM;
            MinX = MinY = MinZ = double.PositiveInfinity;
            MaxX = MaxY = MaxZ = double.NegativeInfinity;
            for (int su = -1; su <= 1; su += 2)
                for (int sv = -1; sv <= 1; sv += 2)
                    for (int c = 0; c < 2; c++)
                    {
                        double px, py, pz;
                        LocalOfTangent(su * half, sv * half, c == 0 ? YLo : YHi, out px, out py, out pz);
                        MinX = Math.Min(MinX, px); MaxX = Math.Max(MaxX, px);
                        MinY = Math.Min(MinY, py); MaxY = Math.Max(MaxY, py);
                        MinZ = Math.Min(MinZ, pz); MaxZ = Math.Max(MaxZ, pz);
                    }
            MinX -= ReachMarginM; MinY -= ReachMarginM; MinZ -= ReachMarginM;
            MaxX += ReachMarginM; MaxY += ReachMarginM; MaxZ += ReachMarginM;
        }

        /// <summary>
 /// Whether a planet-local box may differ from the heightmap: it meets the zone's bounds, and its corners' native footprint and
 /// altitudes meet the square (padded by <paramref name="pad"/>) and the altitude range. Deterministic, a few dozen flops.
 /// </summary>
        public bool Touches(double ax, double ay, double az, double bx, double by, double bz, double pad)
        {
            if (bx < MinX || ax > MaxX || by < MinY || ay > MaxY || bz < MinZ || az > MaxZ) return false;
            double dmin = double.PositiveInfinity, dmax = double.NegativeInfinity, zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
            double ymin = double.PositiveInfinity, ymax = double.NegativeInfinity;
            for (int c = 0; c < 8; c++)
            {
                double u, v, y, d, dx, dz;
                Tangent((c & 1) == 0 ? ax : bx, (c & 2) == 0 ? ay : by, (c & 4) == 0 ? az : bz, out u, out v, out y, out d);
                if (!(d > 0)) return false;
                Unturn(u, v, out dx, out dz);
                dmin = Math.Min(dmin, dx); dmax = Math.Max(dmax, dx);
                zmin = Math.Min(zmin, dz); zmax = Math.Max(zmax, dz);
                ymin = Math.Min(ymin, y); ymax = Math.Max(ymax, y);
            }
            double half = Place.HalfM + pad;
            if (dmax < -half || dmin > half || zmax < -half || zmin > half) return false;
            return ymax >= YLo - pad && ymin <= YHi + pad;
        }

        /// <summary>The field's top surface at a planet-local column direction (the first rock from fromY down: 0.25 m steps, then bisection), NaN without rock.</summary>
        public double TopAlong(double dirX, double dirY, double dirZ, double fromY, double toY)
        {
            double n = Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
            dirX /= n; dirY /= n; dirZ /= n;
            double h, w;
            double prev = fromY;
            if (Sample(dirX * (RingRules.Rs + prev), dirY * (RingRules.Rs + prev), dirZ * (RingRules.Rs + prev), out h, out w) > 0) return fromY;
            for (double y = fromY - 0.25; y >= toY; y -= 0.25)
            {
                if (Sample(dirX * (RingRules.Rs + y), dirY * (RingRules.Rs + y), dirZ * (RingRules.Rs + y), out h, out w) > 0)
                {
                    double lo = y, hi = prev;
                    for (int it = 0; it < 12; it++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (Sample(dirX * (RingRules.Rs + mid), dirY * (RingRules.Rs + mid), dirZ * (RingRules.Rs + mid), out h, out w) > 0) lo = mid; else hi = mid;
                    }
                    return 0.5 * (lo + hi);
                }
                prev = y;
            }
            return double.NaN;
        }

        /// <summary>The chunk's own top (SN y after the re-base, no blend) at a native column, NaN outside the chunk.</summary>
        public double NativeTop(double x, double z)
        {
            double X = x - Chunk.X0, Z = z - Chunk.Z0;
            if (X < 0 || Z < 0 || X > Chunk.SizeX || Z > Chunk.SizeZ) return double.NaN;
            return Chunk.TopAt(X, Z) + Chunk.YFloor + Place.RebaseM;
        }
    }
}
