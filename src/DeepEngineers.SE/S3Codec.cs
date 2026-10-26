using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The SE producer's view of Wire's C3/C4 codecs (DeepEngineers.Wire ModelCodec, built by wire-s3 for slice S3): the
 /// exporter's <see cref="ExportedModel"/> / <see cref="MaterialInfo"/> / <see cref="PartSnapshot"/> into
 /// <see cref="OutMessage"/>s for the budgeted rings. Game-independent (compiled into tests/DeepEngineers.SE.Tests,
 /// where s3_oracle.py checks the bytes against tests/wire/layout_py.py). Conventions (CONTRACT §5.4, ModelCodec): the
 /// model payload is the concatenation of every ModelChunk's data bytes (vertices 56 B, u32 indices in
 /// <c>MyModel.Triangles</c> order, 24 B submeshes), <c>payloadHash</c> = FNV-1a 64 over it; a model's messages are
 /// queued back to back with one group id; MaterialDef = header + four u16-prefixed UTF-8 strings; C4 flags are 0 from the
 /// S3 path (no GridAdd or PartAdd flag bit is defined in layout 1.0; "static" travels in C2 SeEntity.flags) and carry the
 /// S5 mirror conventions (class, bits, version nibble; CONTRACT §5.4 "C1–C4 conventions for S5") from the S5 path.
 /// </summary>
    internal static class S3Codec
    {
        /// <summary>The model as Wire's geometry (flags HasTangents when the exporter found tangents; bounds computed).</summary>
        public static ModelGeometry ToGeometry(ExportedModel m)
        {
            if (m == null) throw new ArgumentNullException("m");
            int ni = m.Indices != null ? m.Indices.Length : 0;
            int ns = m.Submeshes != null ? m.Submeshes.Length : 0;
            bool skinned = m.BoneIndices != null && m.BoneWeights != null; // S5: skinned models (76 B vertices)
            ModelGeometry g = ModelGeometry.Allocate(m.VertexCount, ni, ns, (m.HasTangents ? ModelFlags.HasTangents : 0u) | (skinned ? ModelFlags.Skinned : 0u));
            g.ModelKey = m.ModelKey;
            g.Lod = (uint)m.Lod;
            g.PatternScale = m.PatternScale;
            Copy(m.Positions, g.Positions, "positions");
            Copy(m.Normals, g.Normals, "normals");
            Copy(m.Tangents, g.Tangents, "tangents");
            Copy(m.Binormals, g.Binormals, "binormals");
            Copy(m.Uvs, g.Uvs, "uvs");
            if (skinned)
            {
                if (m.BoneIndices.Length != g.BoneIndices.Length) throw new ArgumentException("bone indices length " + m.BoneIndices.Length + " != " + g.BoneIndices.Length);
                Buffer.BlockCopy(m.BoneIndices, 0, g.BoneIndices, 0, m.BoneIndices.Length);
                Copy(m.BoneWeights, g.BoneWeights, "bone weights");
            }
            for (int i = 0; i < ni; i++)
            {
                if (m.Indices[i] < 0) throw new ArgumentException("negative index at " + i);
                g.Indices[i] = (uint)m.Indices[i];
            }
            for (int i = 0; i < ns; i++)
            {
                ExportedSubmesh s = m.Submeshes[i];
                if (s.TriStart < 0 || s.TriCount < 0) throw new ArgumentException("negative submesh range " + i);
                g.Submeshes[i] = new SubmeshRange(s.MaterialKey, (uint)s.TriStart, (uint)s.TriCount, s.DrawTechnique);
            }
            g.ComputeBounds();
            return g;
        }

        private static void Copy(float[] src, float[] dst, string what)
        {
            if (src == null || src.Length != dst.Length) throw new ArgumentException(what + " length " + (src == null ? -1 : src.Length) + " != " + dst.Length);
            Buffer.BlockCopy(src, 0, dst, 0, src.Length * sizeof(float));
        }

        /// <summary>ModelBegin, ModelChunk × n, ModelEnd of one model (Wire's ModelEncoder), all in message group <paramref name="group"/>.</summary>
        public static List<OutMessage> ModelMessages(ExportedModel m, int chunkBytes, int group, out long payloadBytes)
        {
            var enc = new ModelEncoder(ToGeometry(m), chunkBytes);
            payloadBytes = enc.TotalBytes;
            var list = new List<OutMessage>(enc.MessageCount);
            for (int i = 0; i < enc.MessageCount; i++)
            {
                uint t = enc.MessageType(i);
                string label = (t == C3Type.ModelBegin ? "ModelBegin " : t == C3Type.ModelEnd ? "ModelEnd " : "ModelChunk " + (i - 1) + "/" + enc.ChunkCount + " ") + m.AssetName;
                list.Add(new OutMessage(t, enc.MessagePayload(i), group, label));
            }
            // S5: a skinned model's SkinnedBind right after its ModelEnd, in the same group (CONTRACT §5.4 C3: "SkinnedBind may come anywhere")
            if (m.SkinnedBind != null) list.Add(SkinnedBindMsg(m.ModelKey, m.SkinnedBind, group, m.AssetName));
            return list;
        }

        /// <summary>S5: C3 SkinnedBind (bind matrices in canonical order, SE model space).</summary>
        public static OutMessage SkinnedBindMsg(ulong modelKey, float[] rows, int group, string label)
        {
            if (rows == null || rows.Length % 12 != 0) throw new ArgumentException("SkinnedBind rows must be 12 floats per bone");
            byte[] payload = ModelCodec.EncodeSkinnedBind(new SkinnedBindDefinition { ModelKey = modelKey, BoneCount = rows.Length / 12, Rows = rows });
            return new OutMessage(C3Type.SkinnedBind, payload, group, "SkinnedBind " + label);
        }

        /// <summary>C3 MaterialDef (absolute texture paths; empty = none).</summary>
        public static OutMessage MaterialDef(MaterialInfo mat)
        {
            var d = new MaterialDefinition
            {
                MaterialKey = mat.Key,
                DrawTechnique = mat.DrawTechnique,
                Flags = mat.Flags,
                ColorMetal = mat.ColorMetal ?? string.Empty,
                NormalGloss = mat.NormalGloss ?? string.Empty,
                AddMaps = mat.AddMaps ?? string.Empty,
                AlphaMask = mat.AlphaMask ?? string.Empty,
            };
            return new OutMessage(C3Type.MaterialDef, ModelCodec.EncodeMaterialDef(d), 0, "MaterialDef " + mat.Name);
        }

        // ------------------------------------------------------------------------------------------------- C4

        public static OutMessage ClearAllMsg(uint worldEpoch, uint linkEpoch)
        {
            return new OutMessage(C4Type.ClearAll, ModelCodec.ToBytes(ModelCodec.MakeClearAll(worldEpoch, linkEpoch)), 0, "ClearAll");
        }

        public static OutMessage GridAddMsg(long entityId, bool largeGrid, long parentEntityId, uint worldEpoch)
        {
            return GridAddMsg(entityId, largeGrid, parentEntityId, worldEpoch, 0u);
        }

        /// <summary>GridAdd with flags (S5: S5Conv.Grid(...), the version nibble set; S3: 0).</summary>
        public static OutMessage GridAddMsg(long entityId, bool largeGrid, long parentEntityId, uint worldEpoch, uint flags)
        {
            GridAdd g = ModelCodec.MakeGridAdd((ulong)entityId, largeGrid ? GridClass.LargeGrid : GridClass.SmallGrid, flags, (ulong)parentEntityId, worldEpoch);
            return new OutMessage(C4Type.GridAdd, ModelCodec.ToBytes(g), 0, "GridAdd " + entityId) { Entity = entityId };
        }

        public static OutMessage EntityRemoveMsg(long entityId)
        {
            return new OutMessage(C4Type.EntityRemove, ModelCodec.ToBytes(ModelCodec.MakeEntityRemove((ulong)entityId)), 0, "EntityRemove " + entityId) { Entity = entityId };
        }

        /// <summary>PartAdd; the flags are the snapshot's (0 from the S3 path; the S5 class, bits and version nibble from S5).</summary>
        public static OutMessage PartAddMsg(long entityId, PartSnapshot p)
        {
            if (p.Local == null || p.Local.Length != 12) throw new ArgumentException("part matrix must be 12 floats");
            PartAdd a = ModelCodec.MakePartAdd((ulong)entityId, p.PartKey, p.ModelKey, SeMatrix.FromRows(p.Local), new Vec3(p.H, p.S, p.V), p.UvU, p.UvV, p.SkinId, p.Flags);
            return new OutMessage(C4Type.PartAdd, ModelCodec.ToBytes(a), 0, "PartAdd") { Entity = entityId };
        }

        public static OutMessage PartRemoveMsg(long entityId, ulong partKey)
        {
            return new OutMessage(C4Type.PartRemove, ModelCodec.ToBytes(ModelCodec.MakePartRemove((ulong)entityId, partKey)), 0, "PartRemove") { Entity = entityId };
        }

        public static OutMessage PartRecolorMsg(long entityId, PartSnapshot p)
        {
            return new OutMessage(C4Type.PartRecolor, ModelCodec.ToBytes(ModelCodec.MakePartRecolor((ulong)entityId, p.PartKey, new Vec3(p.H, p.S, p.V))), 0, "PartRecolor") { Entity = entityId };
        }

        /// <summary>S5: C4 CharacterAdd {entityId = C1 characterId, modelKey = the body model, flags, boneCount 78}.</summary>
        public static OutMessage CharacterAddMsg(long entityId, ulong bodyModelKey, uint flags, uint boneCount)
        {
            var c = new CharacterAdd { entityId = (ulong)entityId, modelKey = bodyModelKey, flags = flags, boneCount = boneCount };
            return new OutMessage(C4Type.CharacterAdd, ModelCodec.ToBytes(c), 0, "CharacterAdd " + entityId) { Entity = entityId };
        }
    }
}
