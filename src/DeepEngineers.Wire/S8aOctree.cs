using System;
using System.IO;
using System.Text;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;
using WindowC = DeepEngineers.Wire.S8aConventions.Window;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S8a converter core, part 1: Subnautica's compiled octrees, written by the engine's own rules
 // Inverse of the S4 reader (src/DeepEngineers.Terrain/SnOctreeSource.cs), proven against
 // C1: tests/DeepEngineers.Terrain.Tests/S8aRoundTripTests.cs). Python twin
 // tools/sn-octrees/sn_octree_write.py (the same bytes for the same raster; vectors tests/s8a/vectors/octree.tsv).
 //
 // Raster convention (this API, both twins): a 32³ octree raster is two byte arrays of 32,768 entries, type and
 // density, index x + 32·y + 1024·z (x fastest, as SE's MyStorageData), x/y/z the octree-local voxel (0..31).
 // =================================================================================================================

 /// <summary>The octree codec: 32³ raster ⇄ the engine's 4-byte node array (type, density, u16 firstChild).</summary>
    public static class S8aOctree
    {
        public const int Size = 32;
        public const int Voxels = Size * Size * Size;
        /// <summary>Nodes of a fully subdivided 32³ octree (1 + 8 + 64 + 512 + 4,096 + 32,768).</summary>
        public const int MaxNodes = 37449;
        public const int BytesPerNode = 4;
        public const int MaxBytes = MaxNodes * BytesPerNode;

        [ThreadStatic] private static S8aOctreeEncoder _encoder;

        /// <summary>Raster index of the octree-local voxel (x, y, z): <c>x + 32·y + 1024·z</c>.</summary>
        public static int Index(int x, int y, int z) { return x + (y << 5) + (z << 10); }

        /// <summary>OctNode.IsBelowSurface: far (density 0) is solid iff type &gt; 0; near iff density &gt;= 126.</summary>
        public static bool IsBelowSurface(byte type, byte density) { return density == 0 ? type > 0 : density >= SnWorld.NearZero; }

        /// <summary>
 /// Encodes a 32³ raster into the engine's node array ("octree.write"; a new array of <c>4·nodeCount</c> bytes).
 /// Uses a per-thread <see cref="S8aOctreeEncoder"/>; for an allocation-free loop hold your own encoder.
 /// </summary>
        public static byte[] EncodeOctree(byte[] type32, byte[] density32)
        {
            S8aOctreeEncoder e = _encoder ?? (_encoder = new S8aOctreeEncoder());
            return e.Encode(type32, density32);
        }

        /// <summary>
 /// Rasterises <paramref name="nodeCount"/> nodes at <paramref name="offset"/> into the two 32³ arrays. A node whose
 /// firstChild is 0, or whose children would lie past the node count, is a leaf (as the S4 reader); 0 nodes = far empty.
 /// </summary>
        public static void DecodeOctree(byte[] nodes, int offset, int nodeCount, byte[] type32, byte[] density32)
        {
            if (type32 == null || type32.Length < Voxels) throw new ArgumentException("type32 needs 32,768 entries");
            if (density32 == null || density32.Length < Voxels) throw new ArgumentException("density32 needs 32,768 entries");
            if (nodeCount <= 0)
            {
                Array.Clear(type32, 0, Voxels);
                Array.Clear(density32, 0, Voxels);
                return;
            }
            if (nodes == null || offset < 0 || (long)offset + (long)nodeCount * BytesPerNode > nodes.Length)
                throw new ArgumentException("node range outside the array");
            Fill(nodes, offset, nodeCount, 0, 0, 0, 0, Size, type32, density32);
        }

        private static void Fill(byte[] nodes, int offset, int n, int node, int x0, int y0, int z0, int size, byte[] t, byte[] d)
        {
            int q = offset + node * BytesPerNode;
            byte type = nodes[q], den = nodes[q + 1];
            int fc = nodes[q + 2] | (nodes[q + 3] << 8);
            if (fc == 0 || size == 1 || fc + 8 > n)
            {
                for (int z = z0; z < z0 + size; z++)
                    for (int y = y0; y < y0 + size; y++)
                    {
                        int i = x0 + (y << 5) + (z << 10);
                        for (int x = 0; x < size; x++)
                        {
                            t[i + x] = type;
                            d[i + x] = den;
                        }
                    }
                return;
            }
            int h = size >> 1;
            for (int c = 0; c < 8; c++)
                Fill(nodes, offset, n, fc + c, x0 + h * ((c >> 2) & 1), y0 + h * ((c >> 1) & 1), z0 + h * (c & 1), h, t, d);
        }

        /// <summary>A one-node octree (a uniform 32 m cube): <c>[type, density, 0, 0]</c>.</summary>
        public static byte[] SingleNode(byte type, byte density) { return new[] { type, density, (byte)0, (byte)0 }; }

        /// <summary>
 /// The writer-rule census of one node array (ROADMAP S8a.C1 <c>.rules</c>, the contract's octree_rules.py): the layout is
 /// CompactOctree.SetInternal's pre-order (8-node blocks from id 1, the count consumed exactly), every interior node equals
 /// CacheDownsampledType of its children, no interior node is one OctNode.CanCollapse would collapse.
 /// </summary>
        public static void CheckRules(byte[] nodes, int offset, int nodeCount, S8aRuleStats stats)
        {
            if (stats == null) throw new ArgumentNullException("stats");
            stats.Octrees++;
            if (nodeCount <= 0) { stats.EmptyOctrees++; return; }
            int next = 1;
            CheckNode(nodes, offset, nodeCount, 0, ref next, stats, 0);
            if (next != nodeCount) stats.CountBad++;
        }

        private static void CheckNode(byte[] nodes, int offset, int n, int node, ref int next, S8aRuleStats st, int depth)
        {
            int q = offset + node * BytesPerNode;
            int fc = nodes[q + 2] | (nodes[q + 3] << 8);
            if (fc == 0) return;
            if (depth >= 5 || fc + 8 > n) { st.LayoutBad++; return; }
            if (fc != next) st.LayoutBad++;
            next = fc + 8;
            byte[] ct = st.Ct, cd = st.Cd;
            bool[] cl = st.Cl;
            for (int c = 0; c < 8; c++)
            {
                int qc = offset + (fc + c) * BytesPerNode;
                ct[c] = nodes[qc];
                cd[c] = nodes[qc + 1];
                cl[c] = (nodes[qc + 2] | (nodes[qc + 3] << 8)) == 0;
            }
            // the checks of this node first: the recursion below reuses the scratch arrays
            st.Interior++;
            byte t, d;
            S8aOctreeEncoder.Downsample(ct, cd, out t, out d);
            if (t == nodes[q] && d == nodes[q + 1]) st.DownsampleOk++;
            else st.DownsampleBad++;
            int childId;
            if (S8aOctreeEncoder.CanCollapse(ct, cd, cl, out childId)) st.Collapsible++;
            for (int c = 0; c < 8; c++) CheckNode(nodes, offset, n, fc + c, ref next, st, depth + 1);
        }
    }

    /// <summary>Counters of <see cref="S8aOctree.CheckRules"/> (S8a.C1 <c>.rules</c>: DownsampleBad, Collapsible, LayoutBad, CountBad all 0).</summary>
    public sealed class S8aRuleStats
    {
        public long Octrees, EmptyOctrees, Interior, DownsampleOk, DownsampleBad, Collapsible, LayoutBad, CountBad;
        internal readonly byte[] Ct = new byte[8], Cd = new byte[8];
        internal readonly bool[] Cl = new bool[8];

        public bool Clean { get { return DownsampleBad == 0 && Collapsible == 0 && LayoutBad == 0 && CountBad == 0; } }

        public void Add(S8aRuleStats o)
        {
            Octrees += o.Octrees; EmptyOctrees += o.EmptyOctrees; Interior += o.Interior; DownsampleOk += o.DownsampleOk;
            DownsampleBad += o.DownsampleBad; Collapsible += o.Collapsible; LayoutBad += o.LayoutBad; CountBad += o.CountBad;
        }

        public override string ToString()
        {
            return "octrees " + Octrees + " empty " + EmptyOctrees + " interior " + Interior + " downsampleOk " + DownsampleOk +
                   " downsampleBad " + DownsampleBad + " collapsible " + Collapsible + " layoutBad " + LayoutBad + " countBad " + CountBad;
        }
    }

    /// <summary>
 /// Seeded failures of the Wire converter core (offline; ROADMAP "### S8a" mutants). The SE plugin's S8aMutants may
 /// forward <c>de.mutant S8a.M13</c> here; the launcher's offline c1 part sets it through <c>DEEPENG_S8A_MUTANT</c> or the
 /// Python twin's <c>--mutant S8a.M13</c>.
 /// </summary>
    public static class S8aWireMutants
    {
        /// <summary>S8a.M13: interior nodes take child 0's value (no CacheDownsampledType). Red rows: S8a.C1.rules, S8a.C1.roundtrip.</summary>
        public static volatile bool M13Child0Interior;
        /// <summary>S8a.M30 (planet v2, ores as voxels): the converter writes every ore voxel as plain rock (the fallback type). Red rows: S8a.NO.voxel, S8a.NO.look.</summary>
        public static volatile bool M30OresAsRock;
        /// <summary>S8a.M31: the overlay writes nothing (the map's surface keeps its own types over a vein). Red rows: S8a.NV.detector, S8a.NV.mined.</summary>
        public static volatile bool M31OverlayOff;

        public static bool Set(string id, bool on)
        {
            switch (id)
            {
                case "S8a.M13": M13Child0Interior = on; return true;
                case "S8a.M30": M30OresAsRock = on; return true;
                case "S8a.M31": M31OverlayOff = on; return true;
                default: return false;
            }
        }

        public static string Active
        {
            get
            {
                var l = new System.Collections.Generic.List<string>(3);
                if (M13Child0Interior) l.Add("S8a.M13");
                if (M30OresAsRock) l.Add("S8a.M30");
                if (M31OverlayOff) l.Add("S8a.M31");
                return string.Join(",", l.ToArray());
            }
        }

        public static void ClearAll() { M13Child0Interior = false; M30OresAsRock = false; M31OverlayOff = false; }
    }

    /// <summary>
 /// A reusable octree encoder (one per thread; not thread-safe): the levels of the bottom-up build are preallocated
 /// (≈ 5 KB), so an <see cref="Encode(byte[], byte[], byte[], int)"/> into a caller buffer allocates nothing.
 /// Rules ("octree.write"): a node whose 8 children are leaves, not mixed below/above the surface, with all near
 /// children equal collapses into a leaf with the first near child's value (child 0's when none is near); otherwise it
 /// is interior with CacheDownsampledType (density = integer mean of the near children, else 0 with child 0's type;
 /// type = ComputeMostCommonChildType, the engine's tie rule included). Layout: CompactOctree.SetInternal (pre-order,
 /// each interior node's 8 children a block allocated from id 1 on, recursing child 0 first).
 /// </summary>
    public sealed class S8aOctreeEncoder
    {
        // level L (1..5) holds (32 >> L)³ nodes; index nx + n·ny + n²·nz
        private readonly byte[][] _t = new byte[6][];
        private readonly byte[][] _d = new byte[6][];
        private readonly bool[][] _leaf = new bool[6][];
        private readonly byte[] _ct = new byte[8], _cd = new byte[8];
        private readonly bool[] _cl = new bool[8];
        private byte[] _rt, _rd;

        /// <summary>Interior nodes of the last encode.</summary>
        public int LastInterior { get; private set; }
        /// <summary>Nodes of the last encode.</summary>
        public int LastNodes { get; private set; }

        public S8aOctreeEncoder()
        {
            for (int l = 1; l <= 5; l++)
            {
                int n = S8aOctree.Size >> l;
                _t[l] = new byte[n * n * n];
                _d[l] = new byte[n * n * n];
                _leaf[l] = new bool[n * n * n];
            }
        }

        /// <summary>Encodes into a new array of exactly <c>4·nodes</c> bytes.</summary>
        public byte[] Encode(byte[] type32, byte[] density32)
        {
            var buf = new byte[S8aOctree.MaxBytes];
            int n = Encode(type32, density32, buf, 0);
            var r = new byte[n * S8aOctree.BytesPerNode];
            Buffer.BlockCopy(buf, 0, r, 0, r.Length);
            return r;
        }

        /// <summary>
 /// Encodes into <paramref name="dest"/> at <paramref name="destOffset"/> (room for up to <see cref="S8aOctree.MaxBytes"/>
 /// needed: checked) and returns the node count (1..37,449). No allocation.
 /// </summary>
        public int Encode(byte[] type32, byte[] density32, byte[] dest, int destOffset)
        {
            if (type32 == null || type32.Length < S8aOctree.Voxels) throw new ArgumentException("type32 needs 32,768 entries");
            if (density32 == null || density32.Length < S8aOctree.Voxels) throw new ArgumentException("density32 needs 32,768 entries");
            if (dest == null || destOffset < 0) throw new ArgumentException("dest");

            // uniform raster: one leaf (every level collapses into the same value)
            byte t0 = type32[0], d0 = density32[0];
            bool uniform = true;
            for (int i = 1; i < S8aOctree.Voxels; i++)
                if (type32[i] != t0 || density32[i] != d0) { uniform = false; break; }
            if (uniform)
            {
                if (dest.Length - destOffset < S8aOctree.BytesPerNode) throw new ArgumentException("dest too small");
                dest[destOffset] = t0;
                dest[destOffset + 1] = d0;
                dest[destOffset + 2] = 0;
                dest[destOffset + 3] = 0;
                LastInterior = 0;
                LastNodes = 1;
                return 1;
            }

            bool m13 = S8aWireMutants.M13Child0Interior;
            int interior = 0;
            for (int l = 1; l <= 5; l++)
            {
                int n = S8aOctree.Size >> l, n2 = n << 1;
                byte[] pt = l == 1 ? type32 : _t[l - 1], pd = l == 1 ? density32 : _d[l - 1];
                bool[] pl = l == 1 ? null : _leaf[l - 1];
                byte[] ot = _t[l], od = _d[l];
                bool[] ol = _leaf[l];
                for (int nz = 0; nz < n; nz++)
                    for (int ny = 0; ny < n; ny++)
                        for (int nx = 0; nx < n; nx++)
                        {
                            for (int c = 0; c < 8; c++)
                            {
                                int ci = (2 * nx + ((c >> 2) & 1)) + n2 * ((2 * ny + ((c >> 1) & 1)) + n2 * (2 * nz + (c & 1)));
                                _ct[c] = pt[ci];
                                _cd[c] = pd[ci];
                                _cl[c] = pl == null || pl[ci];
                            }
                            int o = nx + n * (ny + n * nz);
                            int childId;
                            if (CanCollapse(_ct, _cd, _cl, out childId))
                            {
                                ot[o] = _ct[childId];
                                od[o] = _cd[childId];
                                ol[o] = true;
                            }
                            else
                            {
                                ol[o] = false;
                                interior++;
                                if (m13)
                                {
                                    ot[o] = _ct[0];
                                    od[o] = _cd[0];
                                }
                                else
                                {
                                    byte t, d;
                                    Downsample(_ct, _cd, out t, out d);
                                    ot[o] = t;
                                    od[o] = d;
                                }
                            }
                        }
            }
            int nodes = 1 + 8 * interior;
            if ((long)dest.Length - destOffset < (long)nodes * S8aOctree.BytesPerNode) throw new ArgumentException("dest too small for " + nodes + " nodes");
            _rt = type32;
            _rd = density32;
            int next = 1;
            Emit(5, 0, 0, ref next, dest, destOffset);
            _rt = _rd = null;
            LastInterior = interior;
            LastNodes = next;
            return next;
        }

        private void Emit(int level, int idx, int nodeId, ref int next, byte[] dest, int off)
        {
            int q = off + nodeId * S8aOctree.BytesPerNode;
            if (level == 0)
            {
                dest[q] = _rt[idx];
                dest[q + 1] = _rd[idx];
                dest[q + 2] = 0;
                dest[q + 3] = 0;
                return;
            }
            dest[q] = _t[level][idx];
            dest[q + 1] = _d[level][idx];
            if (_leaf[level][idx])
            {
                dest[q + 2] = 0;
                dest[q + 3] = 0;
                return;
            }
            int fc = next;
            next += 8;
            dest[q + 2] = (byte)(fc & 0xFF);
            dest[q + 3] = (byte)(fc >> 8);
            int n = S8aOctree.Size >> level, n2 = n << 1;
            int nx = idx % n, ny = (idx / n) % n, nz = idx / (n * n);
            for (int c = 0; c < 8; c++)
            {
                int ci = (2 * nx + ((c >> 2) & 1)) + n2 * ((2 * ny + ((c >> 1) & 1)) + n2 * (2 * nz + (c & 1)));
                Emit(level - 1, ci, fc + c, ref next, dest, off);
            }
        }

        /// <summary>OctNode.CanCollapse over 8 children (values and leaf flags); <paramref name="childId"/> = the value's child.</summary>
        public static bool CanCollapse(byte[] ct, byte[] cd, bool[] cl, out int childId)
        {
            for (int i = 0; i < 8; i++)
                if (!cl[i]) { childId = -1; return false; }
            bool anyOut = false, anyIn = false;
            for (int j = 0; j < 8; j++)
            {
                bool b = S8aOctree.IsBelowSurface(ct[j], cd[j]);
                anyOut |= !b;
                anyIn |= b;
                if (anyOut & anyIn) { childId = -1; return false; }
            }
            int first = -1;
            for (int k = 0; k < 8; k++)
                if (cd[k] != 0) { first = k; break; }
            if (first < 0) { childId = 0; return true; }
            for (int l = 0; l < 8; l++)
                if (cd[l] != 0 && (ct[l] != ct[first] || cd[l] != cd[first])) { childId = first; return false; }
            childId = first;
            return true;
        }

        /// <summary>OctNode.CacheDownsampledType over 8 children (with ComputeMostCommonChildType's exact tie rule).</summary>
        public static void Downsample(byte[] ct, byte[] cd, out byte type, out byte density)
        {
            int sum = 0, cnt = 0;
            for (int i = 0; i < 8; i++)
                if (cd[i] != 0) { sum += cd[i]; cnt++; }
            if (cnt == 0)
            {
                density = 0;
                type = ct[0];
                return;
            }
            density = (byte)(sum / cnt);
            type = MostCommonChildType(ct, cd);
        }

        /// <summary>
 /// OctNode.ComputeMostCommonChildType: each near-and-below child (type &gt; 0, density &gt;= 126) counts for the first
 /// child j ≤ i of its type; the winner starts as child 0's type with count 0 and k = 1..7 replaces it when its count is
 /// larger, or equal with a larger type (child 0's own count is never compared: the engine's quirk, kept).
 /// </summary>
        public static unsafe byte MostCommonChildType(byte[] ct, byte[] cd)
        {
            int* a = stackalloc int[8];
            for (int i = 0; i < 8; i++) a[i] = 0;
            for (int i = 0; i < 8; i++)
            {
                if (!(ct[i] > 0 && cd[i] >= SnWorld.NearZero)) continue;
                for (int j = 0; j <= i; j++)
                {
                    if (ct[i] == ct[j])
                    {
                        a[j]++;
                        break;
                    }
                }
            }
            int num = 0;
            byte b = ct[0];
            for (int k = 1; k < 8; k++)
            {
                if (a[k] > num || (a[k] == num && ct[k] > b))
                {
                    num = a[k];
                    b = ct[k];
                }
            }
            return b;
        }
    }

    /// <summary>
 /// One Subnautica batch file ("octree.batch"): int32 version (≥ 4; 4 written), then the batch's octrees in file order
 /// (x-outer, y, z-inner, <see cref="OctreeIndex"/>), each a u16 node count then the nodes. A crater id
 /// (<see cref="IsCraterId"/>) stores only the octrees inside the install's octree bounds (128 × 100 × 128), as
 /// LargeWorldStreamer.SaveCompactOctrees (CheckRoot) and the S4 reader; a window id stores all 125 (read by the
 /// Subnautica loader prefix, CONTRACT "window.loader"). Parse keeps a reference to the file's bytes; replaced octrees
 /// live in their own arrays. Not thread-safe.
 /// </summary>
    public sealed class S8aBatchFile
    {
        public const int OctreesPerBatch = SnWorld.OctreesPerBatch * SnWorld.OctreesPerBatch * SnWorld.OctreesPerBatch;

        public readonly int Bx, By, Bz;
        /// <summary>The file's version (4 for every file this core writes).</summary>
        public int Version { get; private set; }

        private byte[] _src;                                   // parsed file (read only)
        private readonly int[] _off = new int[OctreesPerBatch];
        private readonly int[] _cnt = new int[OctreesPerBatch];
        private readonly byte[][] _own = new byte[OctreesPerBatch][];   // replaced octrees (exactly 4·count bytes)
        private readonly bool[] _stored = new bool[OctreesPerBatch];

        private S8aBatchFile(int bx, int by, int bz)
        {
            Bx = bx;
            By = by;
            Bz = bz;
            Version = SnWorld.FileVersion;
            for (int i = 0; i < OctreesPerBatch; i++) _stored[i] = IsStored(bx, by, bz, i);
        }

        /// <summary>The crater box: bx and bz in [CraterMin, CraterMax] (0..25), the install's batch ids (CONTRACT §3.3).</summary>
        public static bool IsCraterId(int bx, int by, int bz)
        {
            return bx >= WindowC.CraterMin && bx <= WindowC.CraterMax && bz >= WindowC.CraterMin && bz <= WindowC.CraterMax;
        }

        public bool IsCrater { get { return IsCraterId(Bx, By, Bz); } }

        /// <summary>In-batch octree (ix, iy, iz) → file order index (<c>(ix·5 + iy)·5 + iz</c>).</summary>
        public static int OctreeIndex(int ix, int iy, int iz) { return (ix * SnWorld.OctreesPerBatch + iy) * SnWorld.OctreesPerBatch + iz; }

        /// <summary>File order index → in-batch octree (ix, iy, iz).</summary>
        public static void OctreeCoords(int i, out int ix, out int iy, out int iz)
        {
            iz = i % SnWorld.OctreesPerBatch;
            iy = (i / SnWorld.OctreesPerBatch) % SnWorld.OctreesPerBatch;
            ix = i / (SnWorld.OctreesPerBatch * SnWorld.OctreesPerBatch);
        }

        /// <summary>Whether octree i is stored in the file of batch (bx, by, bz): always for a window id; inside the bounds for a crater id.</summary>
        public static bool IsStored(int bx, int by, int bz, int i)
        {
            if (!IsCraterId(bx, by, bz)) return true;
            int ix, iy, iz;
            OctreeCoords(i, out ix, out iy, out iz);
            int ox = bx * SnWorld.OctreesPerBatch + ix, oy = by * SnWorld.OctreesPerBatch + iy, oz = bz * SnWorld.OctreesPerBatch + iz;
            return (uint)ox < SnWorld.OctreesX && (uint)oy < SnWorld.OctreesY && (uint)oz < SnWorld.OctreesZ;
        }

        /// <summary>Octrees stored in a file of this id (125 for window ids; fewer at the crater's +x/+z edges).</summary>
        public static int StoredCount(int bx, int by, int bz)
        {
            int k = 0;
            for (int i = 0; i < OctreesPerBatch; i++) if (IsStored(bx, by, bz, i)) k++;
            return k;
        }

        /// <summary>A batch with every stored octree one far-empty node (water).</summary>
        public static S8aBatchFile CreateEmpty(int bx, int by, int bz)
        {
            return CreateUniform(bx, by, bz, 0, 0);
        }

        /// <summary>A batch with every stored octree one node (type, density).</summary>
        public static S8aBatchFile CreateUniform(int bx, int by, int bz, byte type, byte density)
        {
            var f = new S8aBatchFile(bx, by, bz);
            for (int i = 0; i < OctreesPerBatch; i++)
                if (f._stored[i]) f.SetUniform(i, type, density);
            return f;
        }

        /// <summary>Parses a batch file of id (bx, by, bz); throws <see cref="FormatException"/> on a bad file.</summary>
        public static S8aBatchFile Parse(byte[] file, int bx, int by, int bz)
        {
            S8aBatchFile f;
            string error;
            if (!TryParse(file, file == null ? 0 : file.Length, bx, by, bz, out f, out error)) throw new FormatException(error);
            return f;
        }

        /// <summary>
 /// Parses the first <paramref name="length"/> bytes of <paramref name="file"/> (kept by reference: do not change it while
 /// the result is in use). False + error when the version is &lt; 4, a count runs past the end, or bytes are left over.
 /// </summary>
        public static bool TryParse(byte[] file, int length, int bx, int by, int bz, out S8aBatchFile result, out string error)
        {
            result = null;
            error = null;
            if (file == null || length < 4 || length > file.Length) { error = "short file (" + length + " bytes)"; return false; }
            int version = file[0] | (file[1] << 8) | (file[2] << 16) | (file[3] << 24);
            if (version < SnWorld.FileVersion) { error = "version " + version + " < " + SnWorld.FileVersion; return false; }
            var f = new S8aBatchFile(bx, by, bz) { Version = version, _src = file };
            long off = 4;
            for (int i = 0; i < OctreesPerBatch; i++)
            {
                if (!f._stored[i]) continue;
                if (off + 2 > length) { error = "truncated at octree " + i; return false; }
                int n = file[off] | (file[off + 1] << 8);
                off += 2;
                if (off + (long)n * S8aOctree.BytesPerNode > length) { error = "truncated nodes at octree " + i; return false; }
                f._off[i] = (int)off;
                f._cnt[i] = n;
                off += (long)n * S8aOctree.BytesPerNode;
            }
            if (off != length) { error = "not consumed exactly (" + off + " of " + length + " bytes)"; return false; }
            result = f;
            return true;
        }

        /// <summary>Whether octree i is stored in this file.</summary>
        public bool Has(int i) { return (uint)i < OctreesPerBatch && _stored[i]; }

        public int NodeCount(int i) { return Has(i) ? _cnt[i] : 0; }

        /// <summary>The node bytes of octree i without copying (array, offset, node count); count 0 when not stored.</summary>
        public void GetOctree(int i, out byte[] array, out int offset, out int nodeCount)
        {
            if (!Has(i)) { array = null; offset = 0; nodeCount = 0; return; }
            if (_own[i] != null) { array = _own[i]; offset = 0; }
            else { array = _src; offset = _off[i]; }
            nodeCount = _cnt[i];
        }

        /// <summary>A copy of octree i's node bytes (null when the octree is not stored in this file).</summary>
        public byte[] Octree(int i)
        {
            byte[] a;
            int off, n;
            GetOctree(i, out a, out off, out n);
            if (!Has(i)) return null;
            var r = new byte[n * S8aOctree.BytesPerNode];
            if (n > 0) Buffer.BlockCopy(a, off, r, 0, r.Length);
            return r;
        }

        /// <summary>Rasterises octree i (a far-empty raster when it is not stored or has no nodes).</summary>
        public void DecodeOctree(int i, byte[] type32, byte[] density32)
        {
            byte[] a;
            int off, n;
            GetOctree(i, out a, out off, out n);
            S8aOctree.DecodeOctree(a, off, n, type32, density32);
        }

        /// <summary>Replaces octree i by <paramref name="nodes"/> (4·count bytes, kept by reference).</summary>
        public void SetOctree(int i, byte[] nodes)
        {
            if (!Has(i)) throw new ArgumentOutOfRangeException("i", "octree " + i + " is not stored in batch " + Bx + "-" + By + "-" + Bz);
            if (nodes == null || (nodes.Length & 3) != 0 || nodes.Length / 4 > ushort.MaxValue) throw new ArgumentException("nodes must be 4·count bytes, count ≤ 65,535");
            _own[i] = nodes;
            _cnt[i] = nodes.Length / 4;
        }

        /// <summary>Replaces octree i by a copy of <paramref name="nodeCount"/> nodes at <paramref name="offset"/>.</summary>
        public void SetOctree(int i, byte[] nodes, int offset, int nodeCount)
        {
            if (nodeCount < 0 || nodeCount > ushort.MaxValue) throw new ArgumentOutOfRangeException("nodeCount");
            var copy = new byte[nodeCount * S8aOctree.BytesPerNode];
            if (nodeCount > 0) Buffer.BlockCopy(nodes, offset, copy, 0, copy.Length);
            SetOctree(i, copy);
        }

        /// <summary>Replaces octree i by one node (type, density).</summary>
        public void SetUniform(int i, byte type, byte density) { SetOctree(i, S8aOctree.SingleNode(type, density)); }

        /// <summary>Every stored octree is one far-empty node (or has no nodes): water.</summary>
        public bool AllEmpty { get { return AllOne(false); } }

        /// <summary>Every stored octree is one far-solid node: rock.</summary>
        public bool AllSolid { get { return AllOne(true); } }

        private bool AllOne(bool solid)
        {
            for (int i = 0; i < OctreesPerBatch; i++)
            {
                if (!_stored[i]) continue;
                byte[] a;
                int off, n;
                GetOctree(i, out a, out off, out n);
                if (n == 0) { if (solid) return false; continue; }
                if (n != 1) return false;
                byte t = a[off], d = a[off + 1];
                if (d != 0) return false;
                if (solid ? t == 0 : t != 0) return false;
            }
            return true;
        }

        /// <summary>Bytes of <see cref="Serialize"/>.</summary>
        public int SerializedLength
        {
            get
            {
                long len = 4;
                for (int i = 0; i < OctreesPerBatch; i++)
                    if (_stored[i]) len += 2 + (long)_cnt[i] * S8aOctree.BytesPerNode;
                if (len > int.MaxValue) throw new InvalidOperationException("batch too large");
                return (int)len;
            }
        }

        /// <summary>The file bytes (version 4 when this core built or changed the batch; the parsed version otherwise).</summary>
        public byte[] Serialize()
        {
            var r = new byte[SerializedLength];
            SerializeTo(r, 0);
            return r;
        }

        /// <summary>Writes the file bytes at <paramref name="offset"/>; returns the length.</summary>
        public int SerializeTo(byte[] dest, int offset)
        {
            int len = SerializedLength;
            if (dest == null || offset < 0 || (long)offset + len > dest.Length) throw new ArgumentException("dest too small");
            int p = offset;
            int v = Version;
            dest[p++] = (byte)v; dest[p++] = (byte)(v >> 8); dest[p++] = (byte)(v >> 16); dest[p++] = (byte)(v >> 24);
            for (int i = 0; i < OctreesPerBatch; i++)
            {
                if (!_stored[i]) continue;
                byte[] a;
                int off, n;
                GetOctree(i, out a, out off, out n);
                dest[p++] = (byte)n;
                dest[p++] = (byte)(n >> 8);
                int bytes = n * S8aOctree.BytesPerNode;
                if (bytes > 0) Buffer.BlockCopy(a, off, dest, p, bytes);
                p += bytes;
            }
            return p - offset;
        }

        /// <summary>Writes the file bytes to a stream (no full-file buffer).</summary>
        public void WriteTo(Stream s)
        {
            var hdr = new byte[4];
            int v = Version;
            hdr[0] = (byte)v; hdr[1] = (byte)(v >> 8); hdr[2] = (byte)(v >> 16); hdr[3] = (byte)(v >> 24);
            s.Write(hdr, 0, 4);
            for (int i = 0; i < OctreesPerBatch; i++)
            {
                if (!_stored[i]) continue;
                byte[] a;
                int off, n;
                GetOctree(i, out a, out off, out n);
                hdr[0] = (byte)n;
                hdr[1] = (byte)(n >> 8);
                s.Write(hdr, 0, 2);
                if (n > 0) s.Write(a, off, n * S8aOctree.BytesPerNode);
            }
        }

        /// <summary>FNV-1a 64 of the file bytes (the value the store's manifests and notices carry), without a full-file buffer.</summary>
        public ulong Fnv()
        {
            ulong h = 0xcbf29ce484222325UL;
            int v = Version;
            h = Step(h, (byte)v); h = Step(h, (byte)(v >> 8)); h = Step(h, (byte)(v >> 16)); h = Step(h, (byte)(v >> 24));
            for (int i = 0; i < OctreesPerBatch; i++)
            {
                if (!_stored[i]) continue;
                byte[] a;
                int off, n;
                GetOctree(i, out a, out off, out n);
                h = Step(h, (byte)n);
                h = Step(h, (byte)(n >> 8));
                int end = off + n * S8aOctree.BytesPerNode;
                for (int k = off; k < end; k++) h = Step(h, a[k]);
            }
            return h;
        }

        private static ulong Step(ulong h, byte b) { return (h ^ b) * 0x100000001b3UL; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("batch ").Append(Bx).Append('-').Append(By).Append('-').Append(Bz).Append(IsCrater ? " crater" : " window");
            sb.Append(" v").Append(Version).Append(' ').Append(SerializedLength).Append(" bytes");
            return sb.ToString();
        }
    }
}
