using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DeepEngineers.Terrain
{
    /// <summary>Solid/empty neighbour pairs of a batch raster (sn_octree_batch.py's crossing statistics).</summary>
    public sealed class SnCrossings
    {
        /// <summary>Axis-neighbour voxel pairs inside the batch whose solid flags differ.</summary>
        public long Total;
        /// <summary>... with no distance on either side (both far or no data).</summary>
        public long NoDistance;
        /// <summary>... with a near-surface distance on both sides.</summary>
        public long BothDistance;
        /// <summary>... of those, the two distances have different signs.</summary>
        public long SignConsistent;
    }

    /// <summary>
 /// Summary of one 160 m batch raster (S4.A-T0). The oracle's JSON (tools/sn-octrees/sn_octree_batch.py --at0) has
 /// the same keys plus "format", "version", "iso", "oracle", "oracleSha256", which <see cref="FromJson"/> ignores.
 /// </summary>
    public sealed class SnBatchSummary
    {
        public int[] Batch = new int[3];
        /// <summary>SN world position of the batch's minimum corner (metres).</summary>
        public int[] WorldMin = new int[3];
        public bool FilePresent;
        public long FileBytes;
        /// <summary>The file exists but is treated as missing (version &lt; 4, counts not consuming it exactly, I/O error).</summary>
        public bool Bad;
        /// <summary>Leaf count per leaf size (1..32).</summary>
        public SortedDictionary<int, long> LeafSizes = new SortedDictionary<int, long>();
        /// <summary>Near-surface (density != 0) leaf count per leaf size.</summary>
        public SortedDictionary<int, long> NearSizes = new SortedDictionary<int, long>();
        /// <summary>Solid voxels (<see cref="SnOctreeFormat.IsSolid"/>).</summary>
        public long Solid;
        /// <summary>Near-surface voxels (density != 0).</summary>
        public long Near;
        public SnCrossings Crossings = new SnCrossings();
        public string TypeSha256, DensitySha256, LeafSizeSha256;

        public string ToJson()
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"batch\":[").Append(Batch[0]).Append(',').Append(Batch[1]).Append(',').Append(Batch[2]).Append("],");
            sb.Append("\"worldMin\":[").Append(WorldMin[0]).Append(',').Append(WorldMin[1]).Append(',').Append(WorldMin[2]).Append("],");
            sb.Append("\"filePresent\":").Append(FilePresent ? "true" : "false").Append(',');
            sb.Append("\"fileBytes\":").Append(FileBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"bad\":").Append(Bad ? "true" : "false").Append(',');
            sb.Append("\"leafSizes\":");
            AppendSizes(sb, LeafSizes);
            sb.Append(",\"nearSizes\":");
            AppendSizes(sb, NearSizes);
            sb.Append(",\"solid\":").Append(Solid.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"near\":").Append(Near.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"crossings\":{\"total\":").Append(Crossings.Total.ToString(CultureInfo.InvariantCulture))
              .Append(",\"noDistance\":").Append(Crossings.NoDistance.ToString(CultureInfo.InvariantCulture))
              .Append(",\"bothDistance\":").Append(Crossings.BothDistance.ToString(CultureInfo.InvariantCulture))
              .Append(",\"signConsistent\":").Append(Crossings.SignConsistent.ToString(CultureInfo.InvariantCulture)).Append('}');
            sb.Append(",\"typeSha256\":").Append(TerrainJson.Quote(TypeSha256));
            sb.Append(",\"densitySha256\":").Append(TerrainJson.Quote(DensitySha256));
            sb.Append(",\"leafSizeSha256\":").Append(TerrainJson.Quote(LeafSizeSha256));
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendSizes(StringBuilder sb, SortedDictionary<int, long> d)
        {
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<int, long> kv in d)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append("\":").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append('}');
        }

        /// <summary>Parses a summary (this class's JSON or the oracle's at0-&lt;b&gt;.json; unknown keys ignored).</summary>
        public static SnBatchSummary FromJson(string json)
        {
            var d = TerrainJson.Parse(json) as Dictionary<string, object>;
            if (d == null) throw new FormatException("summary is not a JSON object");
            var s = new SnBatchSummary();
            s.Batch = Ints(d["batch"]);
            s.WorldMin = Ints(d["worldMin"]);
            s.FilePresent = (bool)d["filePresent"];
            s.FileBytes = TerrainJson.AsLong(d["fileBytes"]);
            object bad;
            s.Bad = d.TryGetValue("bad", out bad) && bad is bool && (bool)bad;
            s.LeafSizes = Sizes(d["leafSizes"]);
            s.NearSizes = Sizes(d["nearSizes"]);
            s.Solid = TerrainJson.AsLong(d["solid"]);
            s.Near = TerrainJson.AsLong(d["near"]);
            var c = (Dictionary<string, object>)d["crossings"];
            s.Crossings = new SnCrossings
            {
                Total = TerrainJson.AsLong(c["total"]),
                NoDistance = TerrainJson.AsLong(c["noDistance"]),
                BothDistance = TerrainJson.AsLong(c["bothDistance"]),
                SignConsistent = TerrainJson.AsLong(c["signConsistent"]),
            };
            s.TypeSha256 = (string)d["typeSha256"];
            s.DensitySha256 = (string)d["densitySha256"];
            s.LeafSizeSha256 = (string)d["leafSizeSha256"];
            return s;
        }

        private static int[] Ints(object o)
        {
            var l = (List<object>)o;
            var r = new int[l.Count];
            for (int i = 0; i < r.Length; i++) r[i] = (int)TerrainJson.AsLong(l[i]);
            return r;
        }

        private static SortedDictionary<int, long> Sizes(object o)
        {
            var r = new SortedDictionary<int, long>();
            foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)o)
                r[int.Parse(kv.Key, CultureInfo.InvariantCulture)] = TerrainJson.AsLong(kv.Value);
            return r;
        }

        /// <summary>Field-by-field equality; <paramref name="diff"/> names the first difference.</summary>
        public bool SameAs(SnBatchSummary o, out string diff)
        {
            diff = null;
            if (o == null) { diff = "other is null"; return false; }
            if (!Eq(Batch, o.Batch)) { diff = "batch"; return false; }
            if (!Eq(WorldMin, o.WorldMin)) { diff = "worldMin"; return false; }
            if (FilePresent != o.FilePresent) { diff = "filePresent " + FilePresent + " vs " + o.FilePresent; return false; }
            if (FileBytes != o.FileBytes) { diff = "fileBytes " + FileBytes + " vs " + o.FileBytes; return false; }
            if (Bad != o.Bad) { diff = "bad " + Bad + " vs " + o.Bad; return false; }
            if (!Eq(LeafSizes, o.LeafSizes)) { diff = "leafSizes"; return false; }
            if (!Eq(NearSizes, o.NearSizes)) { diff = "nearSizes"; return false; }
            if (Solid != o.Solid) { diff = "solid " + Solid + " vs " + o.Solid; return false; }
            if (Near != o.Near) { diff = "near " + Near + " vs " + o.Near; return false; }
            if (Crossings.Total != o.Crossings.Total || Crossings.NoDistance != o.Crossings.NoDistance ||
                Crossings.BothDistance != o.Crossings.BothDistance || Crossings.SignConsistent != o.Crossings.SignConsistent)
            {
                diff = "crossings";
                return false;
            }
            if (TypeSha256 != o.TypeSha256) { diff = "typeSha256"; return false; }
            if (DensitySha256 != o.DensitySha256) { diff = "densitySha256"; return false; }
            if (LeafSizeSha256 != o.LeafSizeSha256) { diff = "leafSizeSha256"; return false; }
            return true;
        }

        private static bool Eq(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool Eq(SortedDictionary<int, long> a, SortedDictionary<int, long> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<int, long> kv in a)
            {
                long v;
                if (!b.TryGetValue(kv.Key, out v) || v != kv.Value) return false;
            }
            return true;
        }
    }

    /// <summary>
 /// S4.A-T0: rasterises one 160 m batch at 1 m from a <see cref="SnOctreeSource"/>, exactly as
 /// tools/sn-octrees/sn_octree_batch.py does (octrees in x-outer, y, z-inner order; a node is a leaf when
 /// firstChild == 0 or size == 1; arrays indexed (x·N + y)·N + z; no data = type 0, density 0, leaf size 0).
 /// </summary>
    public static unsafe class SnBatchRaster
    {
        public const int N = SnOctreeFormat.BatchVoxels;

        /// <summary>
 /// Fills type/density/leafSize (each N³ bytes) for batch (bx, by, bz) and its summary. False only when the source
 /// is closing (arrays then cleared). A missing or bad file rasterises as no data.
 /// </summary>
        public static bool Rasterize(SnOctreeSource src, int bx, int by, int bz, byte[] type, byte[] density, byte[] leafSize, out SnBatchSummary summary)
        {
            if (src == null) throw new ArgumentNullException("src");
            const int n3 = N * N * N;
            if (type == null || density == null || leafSize == null || type.Length != n3 || density.Length != n3 || leafSize.Length != n3)
                throw new ArgumentException("type, density and leafSize must each hold " + n3 + " bytes");
            if (!SnOctreeFormat.BatchInRange(bx, by, bz)) throw new ArgumentOutOfRangeException("bx", "batch outside 26 x 20 x 26");
            Array.Clear(type, 0, n3);
            Array.Clear(density, 0, n3);
            Array.Clear(leafSize, 0, n3);
            summary = new SnBatchSummary();
            summary.Batch = new[] { bx, by, bz };
            summary.WorldMin = new[] { SnOctreeFormat.OriginX + bx * N, SnOctreeFormat.OriginY + by * N, SnOctreeFormat.OriginZ + bz * N };
            string path = Path.Combine(src.OctreeDir, SnOctreeFormat.BatchFileName(bx, by, bz));
            var fi = new FileInfo(path);
            summary.FilePresent = fi.Exists;
            summary.FileBytes = fi.Exists ? fi.Length : 0;
            var leafCount = new long[SnOctreeFormat.OctreeSize + 1];
            var nearCount = new long[SnOctreeFormat.OctreeSize + 1];
            if (!src.BeginRead()) return false;
            try
            {
                int ready = src.EnsureIndexed(SnOctreeFormat.BatchSlot(bx, by, bz));
                summary.Bad = ready == SnOctreeSource.StateBad || ready == SnOctreeSource.StateIoRetry;
                if (ready == 1)
                {
                    const int K = SnOctreeFormat.OctreesPerBatch;
                    for (int ix = 0; ix < K; ix++)
                        for (int iy = 0; iy < K; iy++)
                            for (int iz = 0; iz < K; iz++)
                            {
                                int ox = bx * K + ix, oy = by * K + iy, oz = bz * K + iz;
                                if (!SnOctreeFormat.OctreeInWorld(ox, oy, oz)) continue;
                                byte* nodes;
                                int count;
                                if (!src.TryGetNodes(ox, oy, oz, out nodes, out count)) continue;
                                Walk(nodes, count, 0, ix * 32, iy * 32, iz * 32, 32, type, density, leafSize, leafCount, nearCount);
                            }
                }
            }
            finally
            {
                src.EndRead();
            }
            if (TerrainMutants.OneVoxelOffset) { ShiftX(type); ShiftX(density); ShiftX(leafSize); }
            if (TerrainMutants.SwapXZ) { SwapXZ(type); SwapXZ(density); SwapXZ(leafSize); }
            for (int s = 1; s <= SnOctreeFormat.OctreeSize; s++)
            {
                if (leafCount[s] != 0) summary.LeafSizes[s] = leafCount[s];
                if (nearCount[s] != 0) summary.NearSizes[s] = nearCount[s];
            }
            Summarise(type, density, summary);
            summary.TypeSha256 = Sha256Hex(type);
            summary.DensitySha256 = Sha256Hex(density);
            summary.LeafSizeSha256 = Sha256Hex(leafSize);
            return true;
        }

        private static void Walk(byte* nodes, int count, int node, int x, int y, int z, int size,
                                 byte[] type, byte[] density, byte[] leafSize, long[] leafCount, long[] nearCount)
        {
            byte* q = nodes + node * SnOctreeFormat.BytesPerNode;
            int fc = q[2] | (q[3] << 8);
            if (fc == 0 || size == 1 || fc + 8 > count)
            {
                byte t = q[0], d = q[1];
                leafCount[size]++;
                if (d != 0) nearCount[size]++;
                for (int i = x; i < x + size; i++)
                    for (int j = y; j < y + size; j++)
                    {
                        int row = (i * N + j) * N;
                        for (int k = z; k < z + size; k++)
                        {
                            type[row + k] = t;
                            density[row + k] = d;
                            leafSize[row + k] = (byte)size;
                        }
                    }
                return;
            }
            int h = size >> 1;
            for (int c = 0; c < 8; c++)
            {
                int xb = (c >> 2) & 1, yb = (c >> 1) & 1, zb = c & 1;
                Walk(nodes, count, fc + SnOctreeFormat.ChildIndex(xb, yb, zb), x + xb * h, y + yb * h, z + zb * h, h,
                     type, density, leafSize, leafCount, nearCount);
            }
        }

        /// <summary>Solid and near counts and the crossing statistics of a raster (sn_octree_batch.py's definitions).</summary>
        internal static void Summarise(byte[] type, byte[] density, SnBatchSummary s)
        {
            long solid = 0, near = 0;
            for (int i = 0; i < type.Length; i++)
            {
                if (SnOctreeFormat.IsSolid(type[i], density[i])) solid++;
                if (density[i] != 0) near++;
            }
            s.Solid = solid;
            s.Near = near;
            var c = new SnCrossings();
            int[] step = { N * N, N, 1 };
            for (int axis = 0; axis < 3; axis++)
            {
                int st = step[axis];
                for (int x = 0; x < N; x++)
                    for (int y = 0; y < N; y++)
                        for (int z = 0; z < N; z++)
                        {
                            int along = axis == 0 ? x : axis == 1 ? y : z;
                            if (along == N - 1) continue;
                            int a = (x * N + y) * N + z, b = a + st;
                            bool sa = SnOctreeFormat.IsSolid(type[a], density[a]), sb = SnOctreeFormat.IsSolid(type[b], density[b]);
                            if (sa == sb) continue;
                            c.Total++;
                            bool fa = density[a] == 0, fb = density[b] == 0;
                            if (fa && fb) c.NoDistance++;
                            else if (!fa && !fb)
                            {
                                c.BothDistance++;
                                if (Math.Sign(density[a] - SnOctreeFormat.NearZero) != Math.Sign(density[b] - SnOctreeFormat.NearZero)) c.SignConsistent++;
                            }
                        }
            }
            s.Crossings = c;
        }

        private static void ShiftX(byte[] a)
        {
            const int plane = N * N;
            Buffer.BlockCopy(a, plane, a, 0, (N - 1) * plane);
            Array.Clear(a, (N - 1) * plane, plane);
        }

        private static void SwapXZ(byte[] a)
        {
            for (int x = 0; x < N; x++)
                for (int y = 0; y < N; y++)
                    for (int z = x + 1; z < N; z++)
                    {
                        int i = (x * N + y) * N + z, j = (z * N + y) * N + x;
                        byte t = a[i];
                        a[i] = a[j];
                        a[j] = t;
                    }
        }

        public static string Sha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
