using System;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S5 chunking on the Subnautica side (CONTRACT §5.4 "Chunk key and consumer rules"; ARCHITECTURE §5.8): one Unity
 // Mesh per (entity, cell, materialKey, colour key, flags & (NoShadows | MetalnessColorable)); cell = floor(T / (8·g))
 // per axis of the PartAdd translation (g = 2.5 m large, 0.5 m small: 20 m / 4 m cells, today's CellKey.Of, float32
 // arithmetic as there); colour key = ColorMaskHSV quantised to 1e-4 (today's ColourRules.Key, bit for bit); 16-bit
 // indices up to 65,535 vertices, else UInt32. Classes 1-4 without Dynamic are chunked; Dynamic parts, tools and
 // floating objects are their own renderers sharing a Mesh per (modelKey, materialKey, colour key). A dirty cell is
 // rebuilt only once the frame's C4 drain reached the head it read at its start, or after 10 frames. Python twin:
 // tools/worldmap/wire_ref.py (s5_cell, s5_colour_key), compared by S5VectorTests.
 // =================================================================================================================

 /// <summary>A chunk cell of an entity: 8×8×8 blocks.</summary>
    public struct ChunkCell : IEquatable<ChunkCell>
    {
        public readonly int X, Y, Z;

        public ChunkCell(int x, int y, int z) { X = x; Y = y; Z = z; }

        public bool Equals(ChunkCell o) { return X == o.X && Y == o.Y && Z == o.Z; }
        public override bool Equals(object obj) { return obj is ChunkCell && Equals((ChunkCell)obj); }
        public override int GetHashCode() { return X * 73856093 ^ Y * 19349663 ^ Z * 83492791; }
        public override string ToString()
        {
            return X.ToString(CultureInfo.InvariantCulture) + "." + Y.ToString(CultureInfo.InvariantCulture) + "." + Z.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The key of one chunk mesh (CONTRACT §5.4).</summary>
    public struct ChunkKey : IEquatable<ChunkKey>
    {
        public readonly ulong EntityId;
        public readonly ChunkCell Cell;
        public readonly ulong MaterialKey;
        public readonly long ColourKey;
        /// <summary><c>PartAdd.flags &amp; (NoShadows | MetalnessColorable)</c> (0 for legacy parts).</summary>
        public readonly uint Flags;

        public ChunkKey(ulong entityId, ChunkCell cell, ulong materialKey, long colourKey, uint flags)
        {
            EntityId = entityId; Cell = cell; MaterialKey = materialKey; ColourKey = colourKey; Flags = flags;
        }

        public bool Equals(ChunkKey o)
        {
            return EntityId == o.EntityId && Cell.Equals(o.Cell) && MaterialKey == o.MaterialKey && ColourKey == o.ColourKey && Flags == o.Flags;
        }

        public override bool Equals(object obj) { return obj is ChunkKey && Equals((ChunkKey)obj); }

        public override int GetHashCode()
        {
            return EntityId.GetHashCode() ^ (Cell.GetHashCode() * 31) ^ (MaterialKey.GetHashCode() * 17) ^ ColourKey.GetHashCode() ^ ((int)Flags * 7919);
        }

        public override string ToString()
        {
            return "chunk." + EntityId.ToString("x", CultureInfo.InvariantCulture) + "." + Cell + ".m" + MaterialKey.ToString("x", CultureInfo.InvariantCulture)
                + ".c" + ColourKey.ToString("x", CultureInfo.InvariantCulture) + ".f" + Flags.ToString("x", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The Mesh shared by every own-renderer part (Dynamic, tool, floating object) of one model, material and colour.</summary>
    public struct SharedMeshKey : IEquatable<SharedMeshKey>
    {
        public readonly ulong ModelKey;
        public readonly ulong MaterialKey;
        public readonly long ColourKey;

        public SharedMeshKey(ulong modelKey, ulong materialKey, long colourKey) { ModelKey = modelKey; MaterialKey = materialKey; ColourKey = colourKey; }

        public bool Equals(SharedMeshKey o) { return ModelKey == o.ModelKey && MaterialKey == o.MaterialKey && ColourKey == o.ColourKey; }
        public override bool Equals(object obj) { return obj is SharedMeshKey && Equals((SharedMeshKey)obj); }
        public override int GetHashCode() { return ModelKey.GetHashCode() ^ (MaterialKey.GetHashCode() * 17) ^ ColourKey.GetHashCode(); }
    }

    /// <summary>How a consumer draws a PartAdd.</summary>
    public enum PartDrawKind
    {
        /// <summary>Not drawn (rejected version, unknown class).</summary>
        None = 0,
        /// <summary>Merged into its cell's chunk meshes (classes 1-4 without Dynamic; every legacy part).</summary>
        Chunk = 1,
        /// <summary>Its own MeshRenderer child posed from C2 (Dynamic subpart, tool).</summary>
        OwnChild = 2,
        /// <summary>A floating object: its own MeshRenderer child of its Entity (identity local).</summary>
        Floating = 3,
        /// <summary>A skinned character part (SkinnedMeshRenderer on the canonical bone tree).</summary>
        Skinned = 4,
    }

    /// <summary>Chunk cells, keys, index format and rebuild gating (Subnautica side, CONTRACT §5.4).</summary>
    public static class ChunkMath
    {
        /// <summary>Blocks per chunk side.</summary>
        public const int BlocksPerChunk = 8;
        /// <summary>Block sizes, m (<c>targetClass</c> 1 large, 2 small).</summary>
        public const float LargeBlockSize = 2.5f, SmallBlockSize = 0.5f;
        /// <summary>Cell sizes, m: 8 × 2.5 and 8 × 0.5 (exact in float32).</summary>
        public const float LargeCellSize = 20f, SmallCellSize = 4f;
        /// <summary>A chunk mesh with more vertices needs <c>IndexFormat.UInt32</c>.</summary>
        public const int MaxUInt16Vertices = 65535;
        /// <summary>The flag bits that split chunks.</summary>
        public const uint ChunkFlagMask = MirrorPartFlags.NoShadows | MirrorPartFlags.MetalnessColorable;
        /// <summary>Worker build queue limit (dirty-newest, then nearest; coalesced beyond).</summary>
        public const int MaxQueuedBuilds = 256;
        /// <summary>A dirty cell waits for a complete C4 drain at most this many frames.</summary>
        public const int MaxDeferFrames = 10;
        /// <summary>SE's EdgeViewDistance: edge-only chunks may be culled beyond 130 m (large) / 35 m (small).</summary>
        public const float EdgeViewDistanceLarge = 130f, EdgeViewDistanceSmall = 35f;

        /// <summary>2.5 m for <see cref="GridClass.LargeGrid"/>, 0.5 m otherwise (S3's rule: anything not small is large).</summary>
        public static float BlockSize(uint targetClass) { return targetClass == GridClass.SmallGrid ? SmallBlockSize : LargeBlockSize; }

        /// <summary>20 m (large) or 4 m (small).</summary>
        public static float CellSize(uint targetClass) { return targetClass == GridClass.SmallGrid ? SmallCellSize : LargeCellSize; }

        /// <summary>SE's edge view distance for the class.</summary>
        public static float EdgeViewDistance(uint targetClass) { return targetClass == GridClass.SmallGrid ? EdgeViewDistanceSmall : EdgeViewDistanceLarge; }

        /// <summary>
 /// The cell of a part translation: <c>(int)Math.Floor(t / cellSize)</c> per axis with a float32 quotient, exactly
 /// as the S3 consumer's <c>CellKey.Of</c>.
 /// </summary>
        public static ChunkCell CellOf(float tx, float ty, float tz, uint targetClass)
        {
            float c = CellSize(targetClass);
            float qx = tx / c, qy = ty / c, qz = tz / c;
            return new ChunkCell((int)Math.Floor(qx), (int)Math.Floor(qy), (int)Math.Floor(qz));
        }

        /// <summary>The cell of a PartAdd (its <c>local</c> translation, floats 9-11).</summary>
        public static unsafe ChunkCell CellOf(PartAdd p, uint targetClass)
        {
            return CellOf(p.local.e[9], p.local.e[10], p.local.e[11], targetClass);
        }

        /// <summary>The SE-local minimum corner of a cell (a natural chunk origin).</summary>
        public static Vec3 CellOrigin(ChunkCell c, uint targetClass)
        {
            double s = CellSize(targetClass);
            return new Vec3(c.X * s, c.Y * s, c.Z * s);
        }

        /// <summary>
 /// The colour key: each of H, S, V clamped to [−2, 2] (NaN → 0), times 10,000, rounded half to even, + 20,000, packed
 /// as <c>(h &lt;&lt; 32) | (s &lt;&lt; 16) | v</c> (the S3 consumer's <c>ColourRules.Key</c>, bit for bit).
 /// </summary>
        public static long ColourKey(float h, float s, float v)
        {
            long a = (long)Math.Round(Clamp(h) * 10000.0) + 20000;
            long b = (long)Math.Round(Clamp(s) * 10000.0) + 20000;
            long c = (long)Math.Round(Clamp(v) * 10000.0) + 20000;
            return (a << 32) | (b << 16) | c;
        }

        /// <summary>The colour key of a PartAdd's <c>colorMaskHSV</c>.</summary>
        public static long ColourKey(PartAdd p) { return ColourKey(p.colorMaskHSV.x, p.colorMaskHSV.y, p.colorMaskHSV.z); }

        private static float Clamp(float x)
        {
            if (float.IsNaN(x)) return 0f;
            return x < -2f ? -2f : (x > 2f ? 2f : x);
        }

        /// <summary>The chunk-splitting flags of a part (0 for legacy and rejected records).</summary>
        public static uint ChunkFlags(uint partFlags)
        {
            return MirrorConventions.Classify(partFlags) == MirrorRecordVersion.Version1 ? partFlags & ChunkFlagMask : 0u;
        }

        /// <summary>How a part is drawn, from its flags alone.</summary>
        public static PartDrawKind DrawKind(uint partFlags)
        {
            MirrorRecordVersion v = MirrorConventions.Classify(partFlags);
            if (v == MirrorRecordVersion.Legacy) return PartDrawKind.Chunk;
            if (v != MirrorRecordVersion.Version1) return PartDrawKind.None;
            uint c = MirrorConventions.EffectivePartClass(partFlags);
            switch (c)
            {
                case MirrorPartClass.CubePart:
                case MirrorPartClass.Edge:
                case MirrorPartClass.FatBlock:
                case MirrorPartClass.Subpart:
                    return (partFlags & MirrorPartFlags.Dynamic) != 0 ? PartDrawKind.OwnChild : PartDrawKind.Chunk;
                case MirrorPartClass.Tool: return PartDrawKind.OwnChild;
                case MirrorPartClass.FloatingObject: return PartDrawKind.Floating;
                case MirrorPartClass.CharacterBody: return PartDrawKind.Skinned;
                default: return PartDrawKind.None;
            }
        }

        /// <summary>True for parts merged into chunk meshes.</summary>
        public static bool IsChunked(uint partFlags) { return DrawKind(partFlags) == PartDrawKind.Chunk; }

        /// <summary>The chunk key of one material of a chunked part.</summary>
        public static ChunkKey KeyOf(ulong entityId, PartAdd p, uint targetClass, ulong materialKey)
        {
            return new ChunkKey(entityId, CellOf(p, targetClass), materialKey, ColourKey(p), ChunkFlags(p.flags));
        }

        /// <summary>True when a mesh of this many vertices needs 32-bit indices (above 65,535).</summary>
        public static bool NeedsUInt32Indices(int vertexCount) { return vertexCount > MaxUInt16Vertices; }

        /// <summary>
 /// The rebuild gate: build a dirty cell when this frame's C4 drain reached the head it read at its start (no
 /// half-applied edit is drawn), or after <see cref="MaxDeferFrames"/> frames regardless.
 /// </summary>
        public static bool ShouldRebuild(bool drainReachedStartHead, int framesSinceDirty)
        {
            return drainReachedStartHead || framesSinceDirty >= MaxDeferFrames;
        }
    }
}
