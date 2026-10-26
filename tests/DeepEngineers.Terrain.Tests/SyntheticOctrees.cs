using System;
using System.Collections.Generic;
using System.IO;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// Writes synthetic batch files in Subnautica's format (CONTRACT 3.1 facts) from dense rasters, so the reader can be
 /// checked against known content without the install. Independent of the reader: children are laid out in the
 /// clean order 4·x + 2·y + z, uniform cubes collapse into leaves, an octree with no data has 0 nodes.
 /// </summary>
    internal static class SyntheticOctrees
    {
        public const int N = SnOctreeFormat.BatchVoxels;

        public static int Idx(int x, int y, int z) { return (x * N + y) * N + z; }

        /// <summary>Subnautica's density encoding: floor(dist·126) + 126 for |dist| &lt; 1 (metres inside), else 0 (far).</summary>
        public static byte EncodeDensity(double dist)
        {
            if (!(Math.Abs(dist) < 1.0)) return 0;
            int d = (int)Math.Floor(dist * 126.0) + 126;
            return (byte)Math.Max(1, Math.Min(251, d));
        }

        /// <summary>
 /// A dense batch raster from a signed distance (metres inside, at voxel centres k + 0.5, batch-local) and a
 /// material function; voxels where <paramref name="noData"/> is true stay type 0, density 0.
 /// </summary>
        public static void Fill(byte[] type, byte[] density, Func<double, double, double, double> dist, Func<int, int, int, byte> material,
                                Func<int, int, int, bool> noData = null)
        {
            for (int x = 0; x < N; x++)
                for (int y = 0; y < N; y++)
                    for (int z = 0; z < N; z++)
                    {
                        int i = Idx(x, y, z);
                        if (noData != null && noData(x, y, z)) { type[i] = 0; density[i] = 0; continue; }
                        double dd = dist(x + 0.5, y + 0.5, z + 0.5);
                        byte den = EncodeDensity(dd);
                        density[i] = den;
                        type[i] = den != 0 || dd > 0 ? material(x, y, z) : (byte)0;
                    }
        }

        /// <summary>Encodes a batch file: int32 version, then per in-world octree (x-outer, y, z-inner) u16 count + nodes.</summary>
        public static byte[] EncodeBatch(int bx, int by, int bz, byte[] type, byte[] density, int version = SnOctreeFormat.MinFileVersion)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(version);
            const int K = SnOctreeFormat.OctreesPerBatch;
            for (int ix = 0; ix < K; ix++)
                for (int iy = 0; iy < K; iy++)
                    for (int iz = 0; iz < K; iz++)
                    {
                        int ox = bx * K + ix, oy = by * K + iy, oz = bz * K + iz;
                        if (ox >= SnOctreeFormat.OctreesX || oy >= SnOctreeFormat.OctreesY || oz >= SnOctreeFormat.OctreesZ) continue;
                        List<byte[]> nodes = EncodeOctree(type, density, ix * 32, iy * 32, iz * 32);
                        w.Write((ushort)nodes.Count);
                        foreach (byte[] n in nodes) w.Write(n);
                    }
            w.Flush();
            return ms.ToArray();
        }

        private static List<byte[]> EncodeOctree(byte[] type, byte[] density, int x0, int y0, int z0)
        {
            var nodes = new List<byte[]>();
            if (AllNoData(type, density, x0, y0, z0, 32)) return nodes;
            nodes.Add(null);
            var queue = new Queue<int[]>();
            queue.Enqueue(new[] { 0, x0, y0, z0, 32 });
            while (queue.Count > 0)
            {
                int[] q = queue.Dequeue();
                int id = q[0], x = q[1], y = q[2], z = q[3], size = q[4];
                int i0 = Idx(x, y, z);
                if (size == 1 || Uniform(type, density, x, y, z, size))
                {
                    nodes[id] = new byte[] { type[i0], density[i0], 0, 0 };
                    continue;
                }
                int fc = nodes.Count;
                if (fc + 8 > 65535) throw new InvalidOperationException("octree too large for the u16 format");
                for (int c = 0; c < 8; c++) nodes.Add(null);
                nodes[id] = new byte[] { type[i0], density[i0], (byte)(fc & 0xFF), (byte)(fc >> 8) };
                int h = size / 2;
                for (int c = 0; c < 8; c++)
                    queue.Enqueue(new[] { fc + c, x + ((c >> 2) & 1) * h, y + ((c >> 1) & 1) * h, z + (c & 1) * h, h });
            }
            return nodes;
        }

        private static bool Uniform(byte[] type, byte[] density, int x0, int y0, int z0, int size)
        {
            int i0 = Idx(x0, y0, z0);
            byte t = type[i0], d = density[i0];
            for (int x = x0; x < x0 + size; x++)
                for (int y = y0; y < y0 + size; y++)
                    for (int z = z0; z < z0 + size; z++)
                    {
                        int i = Idx(x, y, z);
                        if (type[i] != t || density[i] != d) return false;
                    }
            return true;
        }

        private static bool AllNoData(byte[] type, byte[] density, int x0, int y0, int z0, int size)
        {
            for (int x = x0; x < x0 + size; x++)
                for (int y = y0; y < y0 + size; y++)
                    for (int z = z0; z < z0 + size; z++)
                    {
                        int i = Idx(x, y, z);
                        if (type[i] != 0 || density[i] != 0) return false;
                    }
            return true;
        }

        /// <summary>
 /// The standard asymmetric test surface: y_s(x, z) = 80 + 0.21·x − 0.07·z + 6·sin(x / 17) (batch-local metres),
 /// solid below, distance measured along the normal; materials 7 below local y 60, else 9. The octree at
 /// (4, *, 4) has no data (a "missing" corner inside the file).
 /// </summary>
        public static void StandardSurface(byte[] type, byte[] density)
        {
            Fill(type, density,
                 (x, y, z) =>
                 {
                     double ys = 80.0 + 0.21 * x - 0.07 * z + 6.0 * Math.Sin(x / 17.0);
                     double gx = 0.21 + 6.0 / 17.0 * Math.Cos(x / 17.0), gz = -0.07;
                     return (ys - y) / Math.Sqrt(1.0 + gx * gx + gz * gz);
                 },
                 (x, y, z) => y < 60 ? (byte)7 : (byte)9,
                 (x, y, z) => x >= 128 && z >= 128);
        }

        /// <summary>Writes one batch file into <paramref name="dir"/>; returns its path.</summary>
        public static string WriteBatch(string dir, int bx, int by, int bz, byte[] bytes)
        {
            string p = Path.Combine(dir, SnOctreeFormat.BatchFileName(bx, by, bz));
            File.WriteAllBytes(p, bytes);
            return p;
        }
    }
}
