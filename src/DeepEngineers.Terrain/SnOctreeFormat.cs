using System;
using System.Text;
using System.Threading;

namespace DeepEngineers.Terrain
{
    /// <summary>
 /// Subnautica's compiled terrain octrees
 /// Independent of tools/sn-octrees/sn_octree_batch.py (the oracle):
 /// the two share no code, only these facts.
 /// </summary>
    public static class SnOctreeFormat
    {
        public const int VoxelsX = 4096, VoxelsY = 3200, VoxelsZ = 4096;
        public const int OctreeSize = 32;
        public const int OctreesX = 128, OctreesY = 100, OctreesZ = 128;
        public const int OctreesPerBatch = 5;
        /// <summary>Voxels per batch side (5 octrees of 32 voxels = 160 m).</summary>
        public const int BatchVoxels = 160;
        public const int BatchesX = 26, BatchesY = 20, BatchesZ = 26;
        /// <summary>SN world position of voxel (0, 0, 0)'s minimum corner (metres).</summary>
        public const int OriginX = -2048, OriginY = -3040, OriginZ = -2048;
        /// <summary>Lowest batch file version Subnautica loads (BatchOctrees.LoadOctrees; all files on this PC are 4).</summary>
        public const int MinFileVersion = 4;
        public const int BytesPerNode = 4;
        /// <summary>Density of a zero signed distance: (density - 126) / 126 voxels, solid iff density &gt;= 126.</summary>
        public const int NearZero = 126;

        public const int OctreeCount = OctreesX * OctreesY * OctreesZ;
        public const int BatchCount = BatchesX * BatchesY * BatchesZ;

        /// <summary><c>compiled-batch-{bx}-{by}-{bz}.optoctrees</c> (BatchOctreesStreamer.GetPath).</summary>
        public static string BatchFileName(int bx, int by, int bz)
        {
            return "compiled-batch-" + bx + "-" + by + "-" + bz + ".optoctrees";
        }

        /// <summary>
 /// Child slot of the octant (xbit, ybit, zbit) (each 0 or 1, 1 = upper half): <c>4·x + 2·y + z</c>
 /// (Octree.GetNodeId, OctNode.ChildDX/DY/DZ). Mutant S4.M1d: <c>4·z + 2·y + x</c>.
 /// </summary>
        public static int ChildIndex(int xbit, int ybit, int zbit)
        {
            if (TerrainMutants.ChildOrderZYX) return (zbit << 2) | (ybit << 1) | xbit;
            return (xbit << 2) | (ybit << 1) | zbit;
        }

        /// <summary>Solid test of one voxel (OctNode.IsBelowSurface): far (density 0) is solid iff type &gt; 0; near iff density &gt;= 126.</summary>
        public static bool IsSolid(byte type, byte density)
        {
            return density == 0 ? type > 0 : density >= NearZero;
        }

        /// <summary>
 /// The sample s(v) of CONTRACT 3.1 "Content mapping": far voxel +1 if solid else -1; near voxel
 /// (density - 126) / 126 (voxels = metres, positive inside). No data (missing batch, empty octree, outside the
 /// world) is a far empty voxel: <c>Sample(0, 0) = -1</c>. Mutant S4.M1a negates the result.
 /// </summary>
        public static float Sample(byte type, byte density)
        {
            float s = density == 0 ? (type > 0 ? 1f : -1f) : (density - NearZero) / (float)NearZero;
            return TerrainMutants.SignFlip ? -s : s;
        }

        /// <summary>Sample as a double (the field interpolates in doubles); same rule and mutant as <see cref="Sample"/>.</summary>
        internal static double SampleD(byte type, byte density)
        {
            double s = density == 0 ? (type > 0 ? 1.0 : -1.0) : (density - NearZero) / (double)NearZero;
            return TerrainMutants.SignFlip ? -s : s;
        }

        internal static int BatchSlot(int bx, int by, int bz)
        {
            return (bx * BatchesY + by) * BatchesZ + bz;
        }

        internal static int OctreeSlot(int ox, int oy, int oz)
        {
            return (ox * OctreesY + oy) * OctreesZ + oz;
        }

        internal static bool OctreeInWorld(int ox, int oy, int oz)
        {
            return (uint)ox < OctreesX && (uint)oy < OctreesY && (uint)oz < OctreesZ;
        }

        internal static bool BatchInRange(int bx, int by, int bz)
        {
            return (uint)bx < BatchesX && (uint)by < BatchesY && (uint)bz < BatchesZ;
        }
    }

    /// <summary>Class of one 32 m octree (the same classes as sn_octree_stats.py's state map).</summary>
    public enum OctreeState : sbyte
    {
        /// <summary>No batch file (the plug: empty for SE), a bad batch, or outside the SN world.</summary>
        Missing = -1,
        /// <summary>Node count 0, or one far empty node.</summary>
        Empty = 0,
        /// <summary>One far solid node.</summary>
        Solid = 1,
        /// <summary>Anything else: a near-surface root leaf, or subdivided (conservative).</summary>
        Mixed = 2,
    }

    /// <summary>
 /// Seeded failures of the core (dev/test only; ROADMAP S4 "Seeded failures"). The SE plugin refuses S4.M1a-d in a
 /// game (offline A-T0 only); S4.M6 is set through the plugin's S4Mutants.
 /// </summary>
    public static class TerrainMutants
    {
        /// <summary>S4.M1a: the sample's sign is flipped.</summary>
        public static volatile bool SignFlip;
        /// <summary>S4.M1b: the reader returns voxel x + 1 for voxel x.</summary>
        public static volatile bool OneVoxelOffset;
        /// <summary>S4.M1c: the reader swaps x and z inside each batch.</summary>
        public static volatile bool SwapXZ;
        /// <summary>S4.M1d: child order 4·z + 2·y + x.</summary>
        public static volatile bool ChildOrderZYX;
        /// <summary>S4.M6: tile samples taken 0.5 m off (+0.5 on every axis of the SE sample point).</summary>
        public static volatile bool HalfVoxelShift;

        public static void ClearAll()
        {
            SignFlip = false;
            OneVoxelOffset = false;
            SwapXZ = false;
            ChildOrderZYX = false;
            HalfVoxelShift = false;
        }

        /// <summary>Comma-separated ids of the active mutants ("" when none).</summary>
        public static string Active
        {
            get
            {
                var sb = new StringBuilder();
                if (SignFlip) Append(sb, "S4.M1a");
                if (OneVoxelOffset) Append(sb, "S4.M1b");
                if (SwapXZ) Append(sb, "S4.M1c");
                if (ChildOrderZYX) Append(sb, "S4.M1d");
                if (HalfVoxelShift) Append(sb, "S4.M6");
                return sb.ToString();
            }
        }

        /// <summary>Sets one core mutant by id (S4.M1a..d, S4.M6); false when the id is not a core mutant.</summary>
        public static bool Set(string id, bool on)
        {
            switch (id)
            {
                case "S4.M1a": SignFlip = on; return true;
                case "S4.M1b": OneVoxelOffset = on; return true;
                case "S4.M1c": SwapXZ = on; return true;
                case "S4.M1d": ChildOrderZYX = on; return true;
                case "S4.M6": HalfVoxelShift = on; return true;
                default: return false;
            }
        }

        private static void Append(StringBuilder sb, string id)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(id);
        }
    }

    /// <summary>
 /// Log sink of the core (the SE plugin points it at its own log; tests at the console). Lines start with
 /// "DE.Terrain ". A throwing sink is ignored.
 /// </summary>
    public static class TerrainLog
    {
        private static Action<string> _sink;

        public static Action<string> Sink
        {
            get { return Volatile.Read(ref _sink); }
            set { Volatile.Write(ref _sink, value); }
        }

        internal static void Write(string line)
        {
            Action<string> s = Sink;
            if (s == null) return;
            try { s(line); }
            catch (Exception) { }
        }
    }
}
