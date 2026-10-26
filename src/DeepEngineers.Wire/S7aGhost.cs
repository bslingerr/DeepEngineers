using System;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// The S7a placement ghost's records (CONTRACT §5.4 "S7a conventions", "Placement ghost"; layout 1.0 unchanged): SE's
 /// block-placement gizmo travels as an ordinary one-part grid through the existing C4 GridAdd / PartAdd records and
 /// one C2 entity row, under the fixed synthetic id <see cref="EntityId"/> (<c>S7aConventions.Ghost.EntityId</c>,
 /// "DEG" 0x0144454700000001) and the GridAdd flag bit 3 <c>Ghost</c> (<c>S7aConventions.GridFlagsExt.Ghost</c>,
 /// outside S5's <see cref="MirrorGridFlags"/>). The C2 row's <c>flags</c> carry bit 2 <c>ghostShown</c> (SE's
 /// <c>m_showGizmoCube</c>) and bit 3 <c>ghostAllowed</c> (<c>m_buildAllowed</c>) on the ghost's row only; they mean
 /// nothing on any other row. A consumer recognises the ghost by the id or by the GridAdd flag (version nibble 1);
 /// S6 builds no proxy for it (SE sends no SkinSet for the ghost; the marker is exposed for S6 by
 /// <see cref="IsGhostGrid"/>), and the S5 rows and analyzers skip it. Allocation-free except the records themselves.
 /// </summary>
    public static class S7aGhost
    {
        /// <summary>The ghost's entity id (<c>S7aConventions.Ghost.EntityId</c>).</summary>
        public const ulong EntityId = S7aConventions.Ghost.EntityId;

        /// <summary>The part key of the block model part (<c>S7aConventions.Ghost.PartKey</c>).</summary>
        public const ulong ModelPartKey = S7aConventions.Ghost.PartKey;

        /// <summary>At most this many cube parts (keys 1…n) for a definition without a block model (armor).</summary>
        public const int MaxParts = S7aConventions.Ghost.MaxParts;

        /// <summary>GridAdd <c>flags</c> of the ghost: Version1 | Ghost.</summary>
        public const uint GridFlags = MirrorGridFlags.Version1 | S7aConventions.GridFlagsExt.Ghost;

        /// <summary>
 /// The ghost's GridAdd: id <see cref="EntityId"/>, <paramref name="targetClass"/> 1 (large) or 2 (small), flags
 /// Version1 | Ghost, parent 0, the stream's world epoch.
 /// </summary>
        public static GridAdd MakeGridAdd(uint targetClass, uint worldEpoch)
        {
            if (targetClass != GridClass.LargeGrid && targetClass != GridClass.SmallGrid)
                throw new ArgumentOutOfRangeException("targetClass", "1 (large) or 2 (small)");
            return ModelCodec.MakeGridAdd(EntityId, targetClass, GridFlags, 0UL, worldEpoch);
        }

        /// <summary>
 /// The block model part: key 1, flags Version1 | FatBlock | NoShadows, <c>local</c> = translation(ModelOffset)
 /// (SE draws the model at rotation(m_worldMatrixAdd) and translation(m_worldMatrixAdd) +
 /// TransformNormal(ModelOffset, m_worldMatrixAdd)), colour 0, uv offset 0, skin 0.
 /// </summary>
        public static PartAdd MakeModelPart(ulong modelKey, Vec3 modelOffset)
        {
            var local = new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, modelOffset);
            return ModelCodec.MakePartAdd(EntityId, ModelPartKey, modelKey, local, Vec3.Zero, 0f, 0f, 0u,
                MirrorConventions.PartFlags(MirrorPartClass.FatBlock, MirrorPartFlags.NoShadows));
        }

        /// <summary>
 /// One cube part of a definition without a block model (keys 1…<see cref="MaxParts"/>): class CubePart |
 /// NoShadows, <c>local</c> = the 12 rows <c>MyCubeGrid.GetCubeParts</c> returned in the block frame (Right, Up,
 /// Backward, Translation), colour 0. NoShadows: the ghost never casts shadows (Subnautica forces them off too).
 /// </summary>
        public static PartAdd MakeCubePart(ulong partKey, ulong modelKey, float[] localRows)
        {
            if (partKey < 1 || partKey > (ulong)MaxParts) throw new ArgumentOutOfRangeException("partKey", "1.." + MaxParts);
            if (localRows == null || localRows.Length != 12) throw new ArgumentException("12 floats (Right, Up, Backward, Translation)", "localRows");
            for (int i = 0; i < 12; i++)
                if (float.IsNaN(localRows[i]) || float.IsInfinity(localRows[i])) throw new ArgumentException("non-finite matrix", "localRows");
            return ModelCodec.MakePartAdd(EntityId, partKey, modelKey, SeMatrix.FromRows(localRows), Vec3.Zero, 0f, 0f, 0u,
                MirrorConventions.PartFlags(MirrorPartClass.CubePart, MirrorPartFlags.NoShadows));
        }

        /// <summary>The ghost row's C2 <c>flags</c>: ghostShown (bit 2) and ghostAllowed (bit 3).</summary>
        public static uint C2Flags(bool shown, bool allowed)
        {
            return (shown ? S7aConventions.C2FlagsExt.GhostShown : 0u) | (allowed ? S7aConventions.C2FlagsExt.GhostAllowed : 0u);
        }

        /// <summary>
 /// True for the placement ghost: the id <see cref="EntityId"/>, or a version-1 GridAdd with the <c>Ghost</c> bit.
 /// A legacy (nibble 0) or rejected record's bit 3 means nothing.
 /// </summary>
        public static bool IsGhostGrid(ulong id, uint gridFlags)
        {
            if (id == EntityId) return true;
            return MirrorConventions.Classify(gridFlags) == MirrorRecordVersion.Version1 && (gridFlags & S7aConventions.GridFlagsExt.Ghost) != 0;
        }

        /// <summary>The ghost row's bit 2: SE shows the gizmo cube (<c>m_showGizmoCube</c>).</summary>
        public static bool Shown(uint c2Flags) { return (c2Flags & S7aConventions.C2FlagsExt.GhostShown) != 0; }

        /// <summary>The ghost row's bit 3: SE would place the block here (<c>m_buildAllowed</c>).</summary>
        public static bool Allowed(uint c2Flags) { return (c2Flags & S7aConventions.C2FlagsExt.GhostAllowed) != 0; }

        /// <summary>The GridAdd target class of an SE cube size (<c>MyCubeSize.Large</c> = 0 → 1, Small = 1 → 2).</summary>
        public static uint TargetClassOf(bool large) { return large ? GridClass.LargeGrid : GridClass.SmallGrid; }
    }
}
