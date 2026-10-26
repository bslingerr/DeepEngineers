using System;
using System.Globalization;
using System.Text;

// C3/C4 record codecs of docs/CONTRACT.md §5.4 over the frozen layout-1.0 records of Records.cs (ROADMAP S3). Nothing here
// changes a record: the codecs only fill and read the existing structs and the C3 model payload. Producer side (SE
// loader thread): ModelGeometry -> ModelEncoder -> ByteRing C3, one message per TryWrite. Consumer side (Subnautica main
// thread): ByteRing -> C3ModelAssembler (copies chunk bytes only) -> AssembledModel; the hash check and the decode run on a
// worker thread (AssembledModel.TryDecode). C4 messages are fixed records: ModelCodec.ToBytes/TryRead plus the Make*
// helpers. No Unity or VRage types. Python twin: tools/worldmap/wire_ref.py (model_payload, material_def_payload,
// model_key, material_key), checked bit for bit by tests/DeepEngineers.WireMath.Tests/S3VectorTests.cs.
namespace DeepEngineers.Wire
{
    /// <summary>One C3 model payload submesh (24 B on the wire): a material over a range of triangles.</summary>
    public struct SubmeshRange : IEquatable<SubmeshRange>
    {
        public ulong MaterialKey;
        /// <summary>First triangle (not index) of the range, in <c>MyModel.Triangles</c> order (<c>MyMesh.TriStart</c>).</summary>
        public uint TriStart;
        public uint TriCount;
        /// <summary>The numeric <c>VRageRender.Import.MyMeshDrawTechnique</c> value (<see cref="SeDrawTechnique"/>).</summary>
        public uint DrawTechnique;

        public SubmeshRange(ulong materialKey, uint triStart, uint triCount, uint drawTechnique)
        {
            MaterialKey = materialKey; TriStart = triStart; TriCount = triCount; DrawTechnique = drawTechnique;
        }

        public bool Equals(SubmeshRange o)
        {
            return MaterialKey == o.MaterialKey && TriStart == o.TriStart && TriCount == o.TriCount && DrawTechnique == o.DrawTechnique;
        }

        public override bool Equals(object obj) { return obj is SubmeshRange && Equals((SubmeshRange)obj); }

        public override int GetHashCode()
        {
            return MaterialKey.GetHashCode() ^ (int)(TriStart * 397u) ^ (int)(TriCount * 7919u) ^ (int)DrawTechnique;
        }
    }

    /// <summary>
 /// Values of SE's <c>VRageRender.Import.MyMeshDrawTechnique</c>
 /// C3 <c>drawTechnique</c> fields carry these numbers as u32.
 /// Not part of the layout hash (SE's own enumeration, not one of CONTRACT §5.4).
 /// </summary>
    public static class SeDrawTechnique
    {
        public const uint Mesh = 0, VoxelsDebris = 1, VoxelMap = 2, AlphaMasked = 3, AlphaMaskedSingleSided = 4, Foliage = 5,
            Decal = 6, DecalNoPremult = 7, DecalCutout = 8, Holo = 9, VoxelMapSingle = 10, VoxelMapMulti = 11, Skinned = 12,
            MeshInstanced = 13, MeshInstancedSkinned = 14, Glass = 15, MeshInstancedGeneric = 16, MeshInstancedGenericMasked = 17,
            Atmosphere = 18, CloudLayer = 19, Shield = 20, ShieldLit = 21;

        /// <summary>SE's exporter treats these as glass (GLASS, HOLO, SHIELD, SHIELD_LIT; MyCubeGrid export).</summary>
        public static bool IsGlassLike(uint t) { return t == Glass || t == Holo || t == Shield || t == ShieldLit; }

        /// <summary>Alpha-tested techniques (MARMO_ALPHA_CLIP on the Subnautica side).</summary>
        public static bool IsAlphaMasked(uint t)
        {
            return t == AlphaMasked || t == AlphaMaskedSingleSided || t == Foliage || t == MeshInstancedGenericMasked;
        }
    }

    /// <summary>
 /// A model as it crosses C3: SE model space, SE-handed (CONTRACT §2.5), arrays packed per component. Positions,
 /// normals, tangents and binormals are 3 floats per vertex, UVs 2 (raw <c>MyModel.TexCoords</c>: the
 /// <c>uv / patternScale + uvOffset</c> transform is the consumer's, per part), bone indices 4 bytes and bone weights
 /// 4 floats per vertex (skinned only). Indices are <c>MyModel.Triangles</c> order (I0, I1, I2) as u32.
 /// </summary>
    public sealed class ModelGeometry
    {
        public ulong ModelKey;
        public uint Lod;
        /// <summary><see cref="ModelFlags"/>: Skinned (bit 0), HasTangents (bit 1).</summary>
        public uint Flags;
        /// <summary>ModelBegin.bounds: (minX, minY, minZ, maxX, maxY, maxZ), SE model space.</summary>
        public float[] Bounds = new float[6];
        /// <summary><c>MyModel.PatternScale</c> (armor pattern tiling; 1 for most models).</summary>
        public float PatternScale = 1f;
        public int VertexCount;
        public float[] Positions;
        public float[] Normals;
        public float[] Tangents;
        public float[] Binormals;
        public float[] Uvs;
        public byte[] BoneIndices;
        public float[] BoneWeights;
        public uint[] Indices;
        public SubmeshRange[] Submeshes;

        public bool Skinned { get { return (Flags & ModelFlags.Skinned) != 0; } }
        public bool HasTangents { get { return (Flags & ModelFlags.HasTangents) != 0; } }
        public int IndexCount { get { return Indices == null ? 0 : Indices.Length; } }
        public int TriangleCount { get { return IndexCount / 3; } }
        public int SubmeshCount { get { return Submeshes == null ? 0 : Submeshes.Length; } }

        /// <summary>Arrays of the right sizes, zeroed (submeshes default), flags as given, pattern scale 1.</summary>
        public static ModelGeometry Allocate(int vertexCount, int indexCount, int submeshCount, uint flags)
        {
            if (vertexCount < 0 || indexCount < 0 || submeshCount < 0) throw new ArgumentOutOfRangeException("vertexCount");
            var g = new ModelGeometry
            {
                Flags = flags,
                VertexCount = vertexCount,
                Positions = new float[3 * vertexCount],
                Normals = new float[3 * vertexCount],
                Tangents = new float[3 * vertexCount],
                Binormals = new float[3 * vertexCount],
                Uvs = new float[2 * vertexCount],
                Indices = new uint[indexCount],
                Submeshes = new SubmeshRange[submeshCount],
            };
            if ((flags & ModelFlags.Skinned) != 0)
            {
                g.BoneIndices = new byte[4 * vertexCount];
                g.BoneWeights = new float[4 * vertexCount];
            }
            return g;
        }

        public Vec3 Position(int i) { return new Vec3(Positions[3 * i], Positions[3 * i + 1], Positions[3 * i + 2]); }
        public Vec3 Normal(int i) { return new Vec3(Normals[3 * i], Normals[3 * i + 1], Normals[3 * i + 2]); }
        public Vec3 Tangent(int i) { return new Vec3(Tangents[3 * i], Tangents[3 * i + 1], Tangents[3 * i + 2]); }
        public Vec3 Binormal(int i) { return new Vec3(Binormals[3 * i], Binormals[3 * i + 1], Binormals[3 * i + 2]); }
        public Vec3 BoundsMin { get { return new Vec3(Bounds[0], Bounds[1], Bounds[2]); } }
        public Vec3 BoundsMax { get { return new Vec3(Bounds[3], Bounds[4], Bounds[5]); } }

        /// <summary>Sets <see cref="Bounds"/> to the AABB of the positions (zeros for an empty model).</summary>
        public void ComputeBounds()
        {
            if (Bounds == null || Bounds.Length != 6) Bounds = new float[6];
            if (VertexCount == 0)
            {
                for (int i = 0; i < 6; i++) Bounds[i] = 0f;
                return;
            }
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, z0 = float.PositiveInfinity;
            float x1 = float.NegativeInfinity, y1 = float.NegativeInfinity, z1 = float.NegativeInfinity;
            for (int i = 0; i < VertexCount; i++)
            {
                float x = Positions[3 * i], y = Positions[3 * i + 1], z = Positions[3 * i + 2];
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
                if (z < z0) z0 = z;
                if (z > z1) z1 = z;
            }
            Bounds[0] = x0; Bounds[1] = y0; Bounds[2] = z0; Bounds[3] = x1; Bounds[4] = y1; Bounds[5] = z1;
        }

        /// <summary>Structural checks a consumer relies on (sizes, index range, submesh ranges, finite bounds, pattern scale).</summary>
        public bool Validate(out string error)
        {
            error = null;
            if ((Flags & ~(ModelFlags.Skinned | ModelFlags.HasTangents)) != 0)
            {
                error = "unknown ModelBegin flags 0x" + Flags.ToString("x");
                return false;
            }
            int v = VertexCount;
            if (v < 0) { error = "negative vertexCount"; return false; }
            if (!Len(Positions, 3L * v) || !Len(Normals, 3L * v) || !Len(Tangents, 3L * v) || !Len(Binormals, 3L * v) || !Len(Uvs, 2L * v))
            {
                error = "vertex array sizes do not match vertexCount " + v;
                return false;
            }
            if (Skinned && (BoneIndices == null || BoneIndices.LongLength != 4L * v || !Len(BoneWeights, 4L * v)))
            {
                error = "skinned model without 4 bone indices and 4 weights per vertex";
                return false;
            }
            if (Indices == null || Indices.Length % 3 != 0) { error = "index count is not a multiple of 3"; return false; }
            for (int i = 0; i < Indices.Length; i++)
            {
                if (Indices[i] >= (uint)v)
                {
                    error = "index " + Indices[i] + " at " + i + " >= vertexCount " + v;
                    return false;
                }
            }
            uint tris = (uint)(Indices.Length / 3);
            if (Submeshes == null) { error = "submesh table missing"; return false; }
            for (int i = 0; i < Submeshes.Length; i++)
            {
                SubmeshRange s = Submeshes[i];
                if ((ulong)s.TriStart + s.TriCount > tris)
                {
                    error = "submesh " + i + " [" + s.TriStart + "+" + s.TriCount + "] beyond " + tris + " triangles";
                    return false;
                }
            }
            if (Bounds == null || Bounds.Length != 6) { error = "bounds must be 6 floats"; return false; }
            for (int i = 0; i < 6; i++)
            {
                if (!IsFinite(Bounds[i])) { error = "bounds not finite"; return false; }
            }
            if (v > 0 && (Bounds[0] > Bounds[3] || Bounds[1] > Bounds[4] || Bounds[2] > Bounds[5]))
            {
                error = "bounds min > max";
                return false;
            }
            if (!IsFinite(PatternScale) || !(PatternScale > 0f)) { error = "patternScale must be finite and > 0"; return false; }
            return true;
        }

        private static bool Len(float[] a, long n) { return a != null && a.LongLength == n; }

        internal static bool IsFinite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
    }

    /// <summary>C3 MaterialDef content: header fields and the four absolute texture paths (empty = none).</summary>
    public sealed class MaterialDefinition
    {
        public ulong MaterialKey;
        /// <summary>The numeric <c>MyMeshDrawTechnique</c> (<see cref="SeDrawTechnique"/>).</summary>
        public uint DrawTechnique;
        /// <summary><see cref="MaterialFlags"/>: GlassCw (bit 0), GlassCcw (bit 1), AlphaMasked (bit 2).</summary>
        public uint Flags;
        /// <summary><c>ColorMetalTexture</c> (_cm), absolute path.</summary>
        public string ColorMetal = string.Empty;
        /// <summary><c>NormalGlossTexture</c> (_ng), absolute path.</summary>
        public string NormalGloss = string.Empty;
        /// <summary><c>AddMapsTexture</c> (_add), absolute path.</summary>
        public string AddMaps = string.Empty;
        /// <summary><c>AlphamaskTexture</c>, absolute path.</summary>
        public string AlphaMask = string.Empty;

        /// <summary>The content key (<see cref="ModelCodec.MaterialKey"/>) of this definition's technique, flags and paths.</summary>
        public ulong ComputeKey() { return ModelCodec.MaterialKey(DrawTechnique, Flags, ColorMetal, NormalGloss, AddMaps, AlphaMask); }
    }

    /// <summary>C3 SkinnedBind content: the bind matrices (12 floats each, SE model-space rows) of a skinned model.</summary>
    public sealed class SkinnedBindDefinition
    {
        public ulong ModelKey;
        public int BoneCount;
        /// <summary>12 × BoneCount floats: rows Right, Up, Backward, Translation per bone.</summary>
        public float[] Rows;
    }

    /// <summary>Encode/decode helpers for the C3 and C4 records (CONTRACT §5.4) and the C3 model payload.</summary>
    public static unsafe class ModelCodec
    {
        /// <summary>Default chunk data size of <see cref="ModelEncoder"/> (≤ <see cref="C3Type.MaxChunkBytes"/>).</summary>
        public const int DefaultChunkBytes = 1 << 20;
        public const int VertexStride = 56;
        public const int SkinnedVertexStride = 76;
        public const int IndexStride = 4;
        public const int SubmeshStride = 24;
        public const int ModelChunkHeaderBytes = 16;
        public const int MaterialDefHeaderBytes = 16;
        public const int SkinnedBindHeaderBytes = 16;
        public const int BoneBytes = 48;
        /// <summary>Consumer sanity limit for one assembled model (not a wire rule: models are chunked).</summary>
        public const long DefaultMaxModelBytes = 256L << 20;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        // ------------------------------------------------------------------------------------------------ keys

 /// <summary>Path normalisation of the keys: trimmed, '/' to '\', lower-case (invariant culture).</summary>
        public static string NormalizeModelPath(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            return path.Trim().Replace('/', '\\').ToLowerInvariant();
        }

        /// <summary>
 /// C3 <c>modelKey</c>: FNV-1a 64 of the UTF-8 text <c>NormalizeModelPath(path) + "|lod" + lod</c>, plus
 /// <c>"|scale=" + scale.ToString("R")</c> when the model is drawn rescaled (scale ≠ 1: PartAdd has no scale field, so
 /// the vertices carry it and the key must differ), e.g. <c>models\cubes\large\armorcube.mwm|lod0</c>. Keys are opaque
 /// to the consumer; this is the SE plugin's definition (S3Keys) and the stand-ins' (wire_ref.py <c>model_key</c>).
 /// </summary>
        public static ulong ModelKey(string path, uint lod, float scale = 1f)
        {
            return Fnv(ModelKeyText(path, lod, scale));
        }

        /// <summary>The text <see cref="ModelKey"/> hashes (for logs).</summary>
        public static string ModelKeyText(string path, uint lod, float scale = 1f)
        {
            string text = NormalizeModelPath(path) + "|lod" + lod.ToString(CultureInfo.InvariantCulture);
            if (scale != 1f) text += "|scale=" + scale.ToString("R", CultureInfo.InvariantCulture);
            return text;
        }

        /// <summary>
 /// C3 <c>materialKey</c> as a content key: FNV-1a 64 of the UTF-8 text
 /// <c>cm + "|" + ng + "|" + add + "|" + alphamask + "|" + technique + "|" + flags</c>, paths through
 /// <see cref="NormalizeModelPath"/> (null = empty), numbers in decimal. Equal content gives equal keys, so one
 /// MaterialDef serves every model that uses the material (the SE plugin's S3Keys; wire_ref.py <c>material_key</c>).
 /// </summary>
        public static ulong MaterialKey(uint drawTechnique, uint flags, string colorMetal, string normalGloss, string addMaps, string alphaMask)
        {
            return Fnv(MaterialKeyText(drawTechnique, flags, colorMetal, normalGloss, addMaps, alphaMask));
        }

        /// <summary>The text <see cref="MaterialKey"/> hashes (for logs).</summary>
        public static string MaterialKeyText(uint drawTechnique, uint flags, string colorMetal, string normalGloss, string addMaps, string alphaMask)
        {
            return NormalizeModelPath(colorMetal ?? string.Empty) + "|" + NormalizeModelPath(normalGloss ?? string.Empty) + "|"
                + NormalizeModelPath(addMaps ?? string.Empty) + "|" + NormalizeModelPath(alphaMask ?? string.Empty) + "|"
                + drawTechnique.ToString(CultureInfo.InvariantCulture) + "|" + flags.ToString(CultureInfo.InvariantCulture);
        }

        private static ulong Fnv(string text)
        {
            return LayoutHash.Fnv1a64(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>FNV-1a 64 of a byte range (ModelEnd.payloadHash: over the concatenated chunk payloads).</summary>
        public static ulong PayloadHash(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (offset < 0 || count < 0 || offset > data.Length - count) throw new ArgumentOutOfRangeException("count");
            if (count == 0) return LayoutHash.FnvOffsetBasis;
            fixed (byte* p = data) return LayoutHash.Fnv1a64Continue(LayoutHash.FnvOffsetBasis, p + offset, count);
        }

        // ------------------------------------------------------------------------------------------------ model payload

        public static int StrideFor(uint flags) { return (flags & ModelFlags.Skinned) != 0 ? SkinnedVertexStride : VertexStride; }

        /// <summary>Bytes of a model payload: vertices, then u32 indices, then 24-byte submeshes (CONTRACT §5.4).</summary>
        public static long PayloadBytes(long vertexCount, long indexCount, long submeshCount, uint flags)
        {
            return vertexCount * StrideFor(flags) + indexCount * IndexStride + submeshCount * SubmeshStride;
        }

        public static long PayloadBytes(ModelGeometry g)
        {
            if (g == null) throw new ArgumentNullException("g");
            return PayloadBytes(g.VertexCount, g.IndexCount, g.SubmeshCount, g.Flags);
        }

        /// <summary>Chunks of at most <paramref name="maxChunkBytes"/> data bytes for a payload (0 for an empty payload).</summary>
        public static int ChunkCount(long totalBytes, int maxChunkBytes)
        {
            if (maxChunkBytes <= 0) throw new ArgumentOutOfRangeException("maxChunkBytes");
            if (totalBytes < 0) throw new ArgumentOutOfRangeException("totalBytes");
            long n = (totalBytes + maxChunkBytes - 1) / maxChunkBytes;
            if (n > int.MaxValue) throw new ArgumentOutOfRangeException("totalBytes", "too many chunks");
            return (int)n;
        }

        /// <summary>The model payload (bit exact: every float is copied as its IEEE bits). Validates the geometry first.</summary>
        public static byte[] EncodePayload(ModelGeometry g)
        {
            if (g == null) throw new ArgumentNullException("g");
            string err;
            if (!g.Validate(out err)) throw new ArgumentException("invalid model: " + err);
            long total = PayloadBytes(g);
            if (total > int.MaxValue) throw new ArgumentException("payload larger than 2 GiB");
            var buf = new byte[total];
            int stride = StrideFor(g.Flags);
            bool skinned = g.Skinned;
            fixed (byte* b = buf)
            {
                byte* p = b;
                for (int i = 0; i < g.VertexCount; i++)
                {
                    float* f = (float*)p;
                    f[0] = g.Positions[3 * i]; f[1] = g.Positions[3 * i + 1]; f[2] = g.Positions[3 * i + 2];
                    f[3] = g.Normals[3 * i]; f[4] = g.Normals[3 * i + 1]; f[5] = g.Normals[3 * i + 2];
                    f[6] = g.Tangents[3 * i]; f[7] = g.Tangents[3 * i + 1]; f[8] = g.Tangents[3 * i + 2];
                    f[9] = g.Binormals[3 * i]; f[10] = g.Binormals[3 * i + 1]; f[11] = g.Binormals[3 * i + 2];
                    f[12] = g.Uvs[2 * i]; f[13] = g.Uvs[2 * i + 1];
                    if (skinned)
                    {
                        p[0x38] = g.BoneIndices[4 * i]; p[0x39] = g.BoneIndices[4 * i + 1];
                        p[0x3A] = g.BoneIndices[4 * i + 2]; p[0x3B] = g.BoneIndices[4 * i + 3];
                        float* w = (float*)(p + 0x3C);
                        w[0] = g.BoneWeights[4 * i]; w[1] = g.BoneWeights[4 * i + 1];
                        w[2] = g.BoneWeights[4 * i + 2]; w[3] = g.BoneWeights[4 * i + 3];
                    }
                    p += stride;
                }
                uint* ix = (uint*)p;
                for (int i = 0; i < g.IndexCount; i++) ix[i] = g.Indices[i];
                p += (long)g.IndexCount * IndexStride;
                for (int i = 0; i < g.SubmeshCount; i++)
                {
                    SubmeshRange s = g.Submeshes[i];
                    *(ulong*)p = s.MaterialKey;
                    *(uint*)(p + 8) = s.TriStart;
                    *(uint*)(p + 12) = s.TriCount;
                    *(uint*)(p + 16) = s.DrawTechnique;
                    *(uint*)(p + 20) = 0u;
                    p += SubmeshStride;
                }
            }
            return buf;
        }

        /// <summary>The ModelBegin record of a model for a given chunk size.</summary>
        public static ModelBegin MakeBegin(ModelGeometry g, int maxChunkBytes)
        {
            if (g == null) throw new ArgumentNullException("g");
            long total = PayloadBytes(g);
            if (total > uint.MaxValue) throw new ArgumentException("payload larger than 4 GiB");
            var b = new ModelBegin
            {
                modelKey = g.ModelKey,
                lod = g.Lod,
                vertexCount = (uint)g.VertexCount,
                indexCount = (uint)g.IndexCount,
                submeshCount = (uint)g.SubmeshCount,
                flags = g.Flags,
                patternScale = g.PatternScale,
                chunkCount = (uint)ChunkCount(total, maxChunkBytes),
                totalBytes = (uint)total,
            };
            for (int i = 0; i < 6; i++) b.bounds[i] = g.Bounds[i];
            return b;
        }

        /// <summary>
 /// Checks a ModelBegin on its own: known flags, counts consistent with totalBytes, a chunk count possible for the
 /// payload (≥ 1 when totalBytes &gt; 0, totalBytes ≤ chunkCount × 4 MiB, no more chunks than bytes), index count a
 /// multiple of 3, finite bounds, patternScale &gt; 0, totalBytes ≤ <paramref name="maxModelBytes"/>.
 /// </summary>
        public static bool ValidateBegin(ModelBegin b, long maxModelBytes, out string error)
        {
            error = null;
            if ((b.flags & ~(ModelFlags.Skinned | ModelFlags.HasTangents)) != 0) { error = "unknown flags 0x" + b.flags.ToString("x"); return false; }
            long expect = PayloadBytes(b.vertexCount, b.indexCount, b.submeshCount, b.flags);
            if (expect != b.totalBytes) { error = "totalBytes " + b.totalBytes + " != " + expect + " implied by the counts"; return false; }
            if (b.totalBytes > maxModelBytes) { error = "totalBytes " + b.totalBytes + " above the limit " + maxModelBytes; return false; }
            if (b.indexCount % 3 != 0) { error = "indexCount not a multiple of 3"; return false; }
            if (b.totalBytes > 0 && b.chunkCount == 0) { error = "chunkCount 0 for a non-empty payload"; return false; }
            if (b.totalBytes == 0 && b.chunkCount != 0) { error = "chunks for an empty payload"; return false; }
            if ((ulong)b.chunkCount * (ulong)C3Type.MaxChunkBytes < b.totalBytes) { error = "chunkCount too small for 4 MiB chunks"; return false; }
            if (b.chunkCount > b.totalBytes) { error = "more chunks than payload bytes"; return false; }
            for (int i = 0; i < 6; i++)
            {
                if (!ModelGeometry.IsFinite(b.bounds[i])) { error = "bounds not finite"; return false; }
            }
            if (!ModelGeometry.IsFinite(b.patternScale) || !(b.patternScale > 0f)) { error = "patternScale must be finite and > 0"; return false; }
            return true;
        }

        /// <summary>
 /// Decodes a complete payload against its ModelBegin (sizes, zero submesh pads, then
 /// <see cref="ModelGeometry.Validate"/>). The hash is the caller's (<see cref="AssembledModel.TryDecode"/>).
 /// </summary>
        public static bool TryDecodePayload(ModelBegin b, byte[] payload, int offset, int count, out ModelGeometry g, out string error)
        {
            g = null;
            if (payload == null) throw new ArgumentNullException("payload");
            if (offset < 0 || count < 0 || offset > payload.Length - count) throw new ArgumentOutOfRangeException("count");
            if (!ValidateBegin(b, long.MaxValue, out error)) return false;
            if (count != b.totalBytes) { error = "payload " + count + " B != totalBytes " + b.totalBytes; return false; }
            var m = ModelGeometry.Allocate((int)b.vertexCount, (int)b.indexCount, (int)b.submeshCount, b.flags);
            m.ModelKey = b.modelKey;
            m.Lod = b.lod;
            m.PatternScale = b.patternScale;
            for (int i = 0; i < 6; i++) m.Bounds[i] = b.bounds[i];
            int stride = StrideFor(b.flags);
            bool skinned = m.Skinned;
            if (count > 0)
            {
                fixed (byte* b0 = payload)
                {
                    byte* p = b0 + offset;
                    for (int i = 0; i < m.VertexCount; i++)
                    {
                        float* f = (float*)p;
                        m.Positions[3 * i] = f[0]; m.Positions[3 * i + 1] = f[1]; m.Positions[3 * i + 2] = f[2];
                        m.Normals[3 * i] = f[3]; m.Normals[3 * i + 1] = f[4]; m.Normals[3 * i + 2] = f[5];
                        m.Tangents[3 * i] = f[6]; m.Tangents[3 * i + 1] = f[7]; m.Tangents[3 * i + 2] = f[8];
                        m.Binormals[3 * i] = f[9]; m.Binormals[3 * i + 1] = f[10]; m.Binormals[3 * i + 2] = f[11];
                        m.Uvs[2 * i] = f[12]; m.Uvs[2 * i + 1] = f[13];
                        if (skinned)
                        {
                            m.BoneIndices[4 * i] = p[0x38]; m.BoneIndices[4 * i + 1] = p[0x39];
                            m.BoneIndices[4 * i + 2] = p[0x3A]; m.BoneIndices[4 * i + 3] = p[0x3B];
                            float* w = (float*)(p + 0x3C);
                            m.BoneWeights[4 * i] = w[0]; m.BoneWeights[4 * i + 1] = w[1];
                            m.BoneWeights[4 * i + 2] = w[2]; m.BoneWeights[4 * i + 3] = w[3];
                        }
                        p += stride;
                    }
                    uint* ix = (uint*)p;
                    for (int i = 0; i < m.IndexCount; i++) m.Indices[i] = ix[i];
                    p += (long)m.IndexCount * IndexStride;
                    for (int i = 0; i < m.SubmeshCount; i++)
                    {
                        m.Submeshes[i] = new SubmeshRange(*(ulong*)p, *(uint*)(p + 8), *(uint*)(p + 12), *(uint*)(p + 16));
                        if (*(uint*)(p + 20) != 0u) { error = "submesh " + i + " pad is not zero"; return false; }
                        p += SubmeshStride;
                    }
                }
            }
            if (!m.Validate(out error)) return false;
            g = m;
            return true;
        }

        // ------------------------------------------------------------------------------------------------ MaterialDef

 /// <summary>MaterialDef payload: the 16-byte header, then four u16-prefixed UTF-8 strings (cm, ng, add, alphamask), no pads.</summary>
        public static byte[] EncodeMaterialDef(MaterialDefinition m)
        {
            if (m == null) throw new ArgumentNullException("m");
            if ((m.Flags & ~(MaterialFlags.GlassCw | MaterialFlags.GlassCcw | MaterialFlags.AlphaMasked)) != 0)
                throw new ArgumentException("unknown MaterialDef flags 0x" + m.Flags.ToString("x"));
            string[] s = { m.ColorMetal ?? string.Empty, m.NormalGloss ?? string.Empty, m.AddMaps ?? string.Empty, m.AlphaMask ?? string.Empty };
            var bytes = new byte[4][];
            int total = MaterialDefHeaderBytes;
            for (int i = 0; i < 4; i++)
            {
                if (s[i].IndexOf('\0') >= 0) throw new ArgumentException("texture path with a NUL character");
                bytes[i] = StrictUtf8.GetBytes(s[i]);
                if (bytes[i].Length > ushort.MaxValue) throw new ArgumentException("texture path longer than 65535 UTF-8 bytes");
                total += 2 + bytes[i].Length;
            }
            var buf = new byte[total];
            fixed (byte* b = buf)
            {
                *(ulong*)b = m.MaterialKey;
                *(uint*)(b + 8) = m.DrawTechnique;
                *(uint*)(b + 12) = m.Flags;
                int o = MaterialDefHeaderBytes;
                for (int i = 0; i < 4; i++)
                {
                    *(ushort*)(b + o) = (ushort)bytes[i].Length;
                    o += 2;
                    for (int k = 0; k < bytes[i].Length; k++) b[o + k] = bytes[i][k];
                    o += bytes[i].Length;
                }
            }
            return buf;
        }

        /// <summary>Decodes a MaterialDef payload: exact length (no trailing bytes), strict UTF-8, known flags, no NUL.</summary>
        public static bool TryDecodeMaterialDef(byte* p, int bytes, out MaterialDefinition m, out string error)
        {
            m = null;
            error = null;
            if (bytes < MaterialDefHeaderBytes + 8) { error = "MaterialDef shorter than its header and four string lengths (" + bytes + " B)"; return false; }
            if (p == null) throw new ArgumentNullException("p");
            var d = new MaterialDefinition { MaterialKey = *(ulong*)p, DrawTechnique = *(uint*)(p + 8), Flags = *(uint*)(p + 12) };
            if ((d.Flags & ~(MaterialFlags.GlassCw | MaterialFlags.GlassCcw | MaterialFlags.AlphaMasked)) != 0)
            {
                error = "unknown MaterialDef flags 0x" + d.Flags.ToString("x");
                return false;
            }
            int o = MaterialDefHeaderBytes;
            var s = new string[4];
            for (int i = 0; i < 4; i++)
            {
                if (bytes - o < 2) { error = "MaterialDef string " + i + " length missing"; return false; }
                int n = *(ushort*)(p + o);
                o += 2;
                if (bytes - o < n) { error = "MaterialDef string " + i + " truncated"; return false; }
                if (n == 0) s[i] = string.Empty;
                else
                {
                    var tmp = new byte[n];
                    for (int k = 0; k < n; k++) tmp[k] = p[o + k];
                    try { s[i] = StrictUtf8.GetString(tmp); }
                    catch (DecoderFallbackException) { error = "MaterialDef string " + i + " is not UTF-8"; return false; }
                    if (s[i].IndexOf('\0') >= 0) { error = "MaterialDef string " + i + " has a NUL"; return false; }
                }
                o += n;
            }
            if (o != bytes) { error = "MaterialDef has " + (bytes - o) + " trailing bytes"; return false; }
            d.ColorMetal = s[0];
            d.NormalGloss = s[1];
            d.AddMaps = s[2];
            d.AlphaMask = s[3];
            m = d;
            return true;
        }

        public static bool TryDecodeMaterialDef(byte[] payload, int offset, int count, out MaterialDefinition m, out string error)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (offset < 0 || count < 0 || offset > payload.Length - count) throw new ArgumentOutOfRangeException("count");
            if (count == 0) return TryDecodeMaterialDef(null, 0, out m, out error);
            fixed (byte* p = payload) return TryDecodeMaterialDef(p + offset, count, out m, out error);
        }

        // ------------------------------------------------------------------------------------------------ SkinnedBind

        public static byte[] EncodeSkinnedBind(SkinnedBindDefinition s)
        {
            if (s == null) throw new ArgumentNullException("s");
            if (s.BoneCount < 0 || s.BoneCount > SeFrameLimits.MaxBones || s.Rows == null || s.Rows.Length != 12 * s.BoneCount)
                throw new ArgumentException("1..160 bones with 12 floats each");
            var buf = new byte[SkinnedBindHeaderBytes + BoneBytes * s.BoneCount];
            fixed (byte* b = buf)
            {
                *(ulong*)b = s.ModelKey;
                *(uint*)(b + 8) = (uint)s.BoneCount;
                *(uint*)(b + 12) = 0u;
                float* f = (float*)(b + SkinnedBindHeaderBytes);
                for (int i = 0; i < s.Rows.Length; i++) f[i] = s.Rows[i];
            }
            return buf;
        }

        public static bool TryDecodeSkinnedBind(byte* p, int bytes, out SkinnedBindDefinition s, out string error)
        {
            s = null;
            error = null;
            if (bytes < SkinnedBindHeaderBytes) { error = "SkinnedBind shorter than its header"; return false; }
            if (p == null) throw new ArgumentNullException("p");
            uint n = *(uint*)(p + 8);
            if (*(uint*)(p + 12) != 0u) { error = "SkinnedBind pad is not zero"; return false; }
            if (n > SeFrameLimits.MaxBones) { error = "SkinnedBind boneCount " + n + " above " + SeFrameLimits.MaxBones; return false; }
            if (bytes != SkinnedBindHeaderBytes + BoneBytes * (long)n) { error = "SkinnedBind size " + bytes + " != 16 + 48 x " + n; return false; }
            var d = new SkinnedBindDefinition { ModelKey = *(ulong*)p, BoneCount = (int)n, Rows = new float[12 * n] };
            float* f = (float*)(p + SkinnedBindHeaderBytes);
            for (int i = 0; i < d.Rows.Length; i++) d.Rows[i] = f[i];
            s = d;
            return true;
        }

        // ------------------------------------------------------------------------------------------------ fixed records (C3 headers, C4)

 /// <summary>The bytes of a fixed record (its struct size; pads as stored, zero in a new record).</summary>
        public static byte[] ToBytes<T>(T record) where T : unmanaged
        {
            var buf = new byte[sizeof(T)];
            fixed (byte* b = buf) *(T*)b = record;
            return buf;
        }

        /// <summary>Reads a fixed record whose payload must be exactly its struct size (CONTRACT §5.4 sizes).</summary>
        public static bool TryRead<T>(byte* payload, int payloadBytes, out T record) where T : unmanaged
        {
            record = default(T);
            if (payload == null || payloadBytes != sizeof(T)) return false;
            record = *(T*)payload;
            return true;
        }

        public static bool TryRead<T>(byte[] payload, int offset, int count, out T record) where T : unmanaged
        {
            record = default(T);
            if (payload == null) throw new ArgumentNullException("payload");
            if (offset < 0 || count < 0 || offset > payload.Length - count) throw new ArgumentOutOfRangeException("count");
            if (count != sizeof(T)) return false;
            fixed (byte* p = payload) record = *(T*)(p + offset);
            return true;
        }

        /// <summary>An SE matrix (rows Right, Up, Backward, Translation) as the 12 wire floats.</summary>
        public static F32x12 Rows(SeMatrix m)
        {
            var r = new F32x12();
            r.e[0] = (float)m.Right.X; r.e[1] = (float)m.Right.Y; r.e[2] = (float)m.Right.Z;
            r.e[3] = (float)m.Up.X; r.e[4] = (float)m.Up.Y; r.e[5] = (float)m.Up.Z;
            r.e[6] = (float)m.Backward.X; r.e[7] = (float)m.Backward.Y; r.e[8] = (float)m.Backward.Z;
            r.e[9] = (float)m.Translation.X; r.e[10] = (float)m.Translation.Y; r.e[11] = (float)m.Translation.Z;
            return r;
        }

        /// <summary>The 12 wire floats as an SE matrix (doubles).</summary>
        public static SeMatrix Matrix(F32x12 r)
        {
            return new SeMatrix(new Vec3(r.e[0], r.e[1], r.e[2]), new Vec3(r.e[3], r.e[4], r.e[5]),
                                new Vec3(r.e[6], r.e[7], r.e[8]), new Vec3(r.e[9], r.e[10], r.e[11]));
        }

        /// <summary>
 /// C4 PartAdd. <paramref name="local"/> is the part's grid-local SE matrix (<c>MyCubePart.InstanceData.LocalMatrix</c>,
 /// rigid); <paramref name="colorMaskHsv"/> is <c>InstanceData.ColorMaskHSV.xyz</c>; the UV offset is the part's
 /// armor pattern offset (<see cref="MeshConventions.ArmorPatternOffset(int, int, int, int, out float, out float)"/>; 0
 /// for models without a pattern).
 /// </summary>
        public static PartAdd MakePartAdd(ulong entityId, ulong partKey, ulong modelKey, SeMatrix local, Vec3 colorMaskHsv,
                                          float uvOffsetU, float uvOffsetV, uint skinId = 0, uint flags = 0)
        {
            return new PartAdd
            {
                entityId = entityId,
                partKey = partKey,
                modelKey = modelKey,
                local = Rows(local),
                colorMaskHSV = new F32x3((float)colorMaskHsv.X, (float)colorMaskHsv.Y, (float)colorMaskHsv.Z),
                uvOffset = new F32x2(uvOffsetU, uvOffsetV),
                skinId = skinId,
                flags = flags,
            };
        }

        public static SeMatrix PartLocal(PartAdd p) { return Matrix(p.local); }

        public static Vec3 PartColorMaskHsv(PartAdd p) { return new Vec3(p.colorMaskHSV.x, p.colorMaskHSV.y, p.colorMaskHSV.z); }

        public static PartRemove MakePartRemove(ulong entityId, ulong partKey)
        {
            return new PartRemove { entityId = entityId, partKey = partKey };
        }

        public static GridAdd MakeGridAdd(ulong entityId, uint targetClass, uint flags, ulong parentEntityId, uint worldEpoch)
        {
            return new GridAdd { entityId = entityId, targetClass = targetClass, flags = flags, parentEntityId = parentEntityId, worldEpoch = worldEpoch };
        }

        public static EntityRemove MakeEntityRemove(ulong entityId) { return new EntityRemove { entityId = entityId }; }

        public static PartRecolor MakePartRecolor(ulong entityId, ulong partKey, Vec3 hsv)
        {
            return new PartRecolor { entityId = entityId, partKey = partKey, hsv = new F32x3((float)hsv.X, (float)hsv.Y, (float)hsv.Z) };
        }

        public static ClearAll MakeClearAll(uint worldEpoch, uint linkEpoch) { return new ClearAll { worldEpoch = worldEpoch, linkEpoch = linkEpoch }; }

        /// <summary>
 /// A deterministic part key for a cube part: the block's grid cell (x, y, z as i16) and the part index within the
 /// block, packed in bits 0-15 x, 16-31 y, 32-47 z, 48-63 index. Opaque to the consumer (wire_ref.py <c>cube_part_key</c>).
 /// </summary>
        public static ulong CubePartKey(int cellX, int cellY, int cellZ, int partIndex)
        {
            return (ulong)(ushort)(short)cellX | ((ulong)(ushort)(short)cellY << 16) | ((ulong)(ushort)(short)cellZ << 32)
                | ((ulong)(ushort)partIndex << 48);
        }
    }

    /// <summary>What <see cref="C3ModelAssembler.Feed(uint, byte*, int)"/> did with one C3 message.</summary>
    public enum C3Event
    {
        /// <summary>Accepted (ModelBegin or a ModelChunk); nothing complete yet.</summary>
        Progress = 0,
        /// <summary>A ModelEnd closed a model: take it with <see cref="C3ModelAssembler.TakeModel"/>.</summary>
        ModelComplete = 1,
        /// <summary>A MaterialDef: <see cref="C3ModelAssembler.LastMaterial"/>.</summary>
        Material = 2,
        /// <summary>A SkinnedBind: <see cref="C3ModelAssembler.LastSkinnedBind"/>.</summary>
        SkinnedBind = 3,
        /// <summary>A chunk or end after <see cref="C3ModelAssembler.Resync"/> whose ModelBegin was dropped (expected, not an error).</summary>
        Orphan = 4,
        /// <summary>A message no correct producer sends (<see cref="C3ModelAssembler.LastError"/>); an open model is dropped.</summary>
        Error = 5,
    }

    /// <summary>A model whose chunks all arrived; decode (and hash-check) it off the main thread.</summary>
    public sealed class AssembledModel
    {
        public readonly ModelBegin Begin;
        public readonly byte[] Payload;
        public readonly ulong ExpectedHash;

        public AssembledModel(ModelBegin begin, byte[] payload, ulong expectedHash)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            Begin = begin; Payload = payload; ExpectedHash = expectedHash;
        }

        public ulong ModelKey { get { return Begin.modelKey; } }

        /// <summary>FNV-1a 64 of the payload equals ModelEnd.payloadHash.</summary>
        public bool HashMatches() { return ModelCodec.PayloadHash(Payload, 0, Payload.Length) == ExpectedHash; }

        /// <summary>Worker-thread step: hash check, then <see cref="ModelCodec.TryDecodePayload"/>.</summary>
        public bool TryDecode(out ModelGeometry g, out string error)
        {
            g = null;
            ulong h = ModelCodec.PayloadHash(Payload, 0, Payload.Length);
            if (h != ExpectedHash)
            {
                error = "payload hash 0x" + h.ToString("x16") + " != ModelEnd 0x" + ExpectedHash.ToString("x16");
                return false;
            }
            return ModelCodec.TryDecodePayload(Begin, Payload, 0, Payload.Length, out g, out error);
        }
    }

    /// <summary>
 /// Producer of one model's C3 messages: ModelBegin, ModelChunk 0..n-1 (payload in order, each ≤ the chunk size),
 /// ModelEnd (FNV-1a 64 of the whole payload). A producer writes them in order and never interleaves another model's
 /// messages; MaterialDef/SkinnedBind may go before, between or after. Each <see cref="TryWriteNext"/> is one try-once
 /// ring write: on Full the caller keeps the encoder and resumes later (CONTRACT §5.1 C3). The payload is built in the
 /// constructor (SE loader thread).
 /// </summary>
    public sealed unsafe class ModelEncoder
    {
        private readonly byte[] _payload;
        private readonly int _chunkBytes;
        private int _next;

        public readonly ModelBegin Begin;
        public readonly ModelEnd End;
        public readonly int ChunkCount;

        public ModelEncoder(ModelGeometry g, int maxChunkBytes = ModelCodec.DefaultChunkBytes)
        {
            if (g == null) throw new ArgumentNullException("g");
            if (maxChunkBytes < 1 || maxChunkBytes > C3Type.MaxChunkBytes) throw new ArgumentOutOfRangeException("maxChunkBytes", "1 .. 4 MiB");
            _payload = ModelCodec.EncodePayload(g);
            _chunkBytes = maxChunkBytes;
            Begin = ModelCodec.MakeBegin(g, maxChunkBytes);
            ChunkCount = (int)Begin.chunkCount;
            End = new ModelEnd { modelKey = g.ModelKey, payloadHash = ModelCodec.PayloadHash(_payload, 0, _payload.Length) };
        }

        public ulong ModelKey { get { return Begin.modelKey; } }
        /// <summary>The concatenated payload (do not modify).</summary>
        public byte[] Payload { get { return _payload; } }
        public long TotalBytes { get { return _payload.LongLength; } }
        /// <summary>ModelBegin + chunks + ModelEnd.</summary>
        public int MessageCount { get { return ChunkCount + 2; } }
        /// <summary>Index of the message <see cref="TryWriteNext"/> writes next.</summary>
        public int Next { get { return _next; } }
        public bool Done { get { return _next >= MessageCount; } }

        /// <summary>Ring bytes the remaining messages take (framed and padded; a wrap pad is not counted).</summary>
        public long RemainingFramedBytes
        {
            get
            {
                long n = 0;
                for (int i = _next; i < MessageCount; i++) n += ByteRing.FramedSize(MessagePayloadBytes(i));
                return n;
            }
        }

        public uint MessageType(int i)
        {
            if (i < 0 || i >= MessageCount) throw new ArgumentOutOfRangeException("i");
            return i == 0 ? C3Type.ModelBegin : (i == MessageCount - 1 ? C3Type.ModelEnd : C3Type.ModelChunk);
        }

        private void ChunkRange(int chunk, out int offset, out int count)
        {
            long off = (long)chunk * _chunkBytes;
            offset = (int)off;
            count = (int)Math.Min(_chunkBytes, _payload.LongLength - off);
        }

        public int MessagePayloadBytes(int i)
        {
            uint t = MessageType(i);
            if (t == C3Type.ModelBegin) return sizeof(ModelBegin);
            if (t == C3Type.ModelEnd) return sizeof(ModelEnd);
            int off, cnt;
            ChunkRange(i - 1, out off, out cnt);
            return ModelCodec.ModelChunkHeaderBytes + cnt;
        }

        /// <summary>The payload bytes of message i (allocates; tests, stand-ins, logs).</summary>
        public byte[] MessagePayload(int i)
        {
            uint t = MessageType(i);
            if (t == C3Type.ModelBegin) return ModelCodec.ToBytes(Begin);
            if (t == C3Type.ModelEnd) return ModelCodec.ToBytes(End);
            int off, cnt;
            ChunkRange(i - 1, out off, out cnt);
            var buf = new byte[ModelCodec.ModelChunkHeaderBytes + cnt];
            var h = new ModelChunkHeader { modelKey = Begin.modelKey, chunkIndex = (uint)(i - 1), bytes = (uint)cnt };
            fixed (byte* b = buf) *(ModelChunkHeader*)b = h;
            Buffer.BlockCopy(_payload, off, buf, ModelCodec.ModelChunkHeaderBytes, cnt);
            return buf;
        }

        /// <summary>Writes message i (no allocation). TooLarge: the chunk does not fit half this ring (choose a smaller chunk size).</summary>
        public RingWriteStatus TryWrite(ByteRing ring, int i)
        {
            if (ring == null) throw new ArgumentNullException("ring");
            uint t = MessageType(i);
            if (t == C3Type.ModelBegin) return ring.TryWrite(C3Type.ModelBegin, Begin);
            if (t == C3Type.ModelEnd) return ring.TryWrite(C3Type.ModelEnd, End);
            int off, cnt;
            ChunkRange(i - 1, out off, out cnt);
            var h = new ModelChunkHeader { modelKey = Begin.modelKey, chunkIndex = (uint)(i - 1), bytes = (uint)cnt };
            fixed (byte* p = _payload) return ring.TryWrite(C3Type.ModelChunk, h, p + off, cnt);
        }

        /// <summary>Writes the next message and advances only on Ok. Ok without writing when <see cref="Done"/>.</summary>
        public RingWriteStatus TryWriteNext(ByteRing ring)
        {
            if (Done) return RingWriteStatus.Ok;
            RingWriteStatus st = TryWrite(ring, _next);
            if (st == RingWriteStatus.Ok) _next++;
            return st;
        }

        /// <summary>Starts over at ModelBegin (after a resend request or a producer-side reset).</summary>
        public void Rewind() { _next = 0; }
    }

    /// <summary>
 /// Consumer of the C3 stream (Subnautica main thread): validates framing and order, copies chunk bytes into one buffer
 /// per model and hands a complete <see cref="AssembledModel"/> on ModelEnd. It never hashes or decodes (a worker does).
 /// A new ModelBegin while a model is open abandons the open one (counted). After <see cref="Resync"/>, chunks and ends
 /// without their ModelBegin are orphans until the next ModelBegin. MaterialDef and SkinnedBind may arrive at any time,
 /// also inside a model's message run.
 /// </summary>
    public sealed unsafe class C3ModelAssembler
    {
        private readonly long _maxModelBytes;
        private bool _open;
        private bool _synced = true;
        private ModelBegin _begin;
        private byte[] _buf;
        private long _filled;
        private uint _nextChunk;
        private AssembledModel _done;

        public C3ModelAssembler(long maxModelBytes = ModelCodec.DefaultMaxModelBytes)
        {
            if (maxModelBytes < 0) throw new ArgumentOutOfRangeException("maxModelBytes");
            _maxModelBytes = maxModelBytes;
        }

        public long Messages { get; private set; }
        public long BytesFed { get; private set; }
        public long ModelsCompleted { get; private set; }
        public long Materials { get; private set; }
        public long SkinnedBinds { get; private set; }
        public long Errors { get; private set; }
        public long Abandoned { get; private set; }
        public long Orphans { get; private set; }
        public string LastError { get; private set; }
        public MaterialDefinition LastMaterial { get; private set; }
        public SkinnedBindDefinition LastSkinnedBind { get; private set; }
        /// <summary>A model is open (ModelBegin seen, ModelEnd not yet).</summary>
        public bool InModel { get { return _open; } }
        public ulong OpenModelKey { get { return _open ? _begin.modelKey : 0UL; } }

        /// <summary>The model completed by the last <see cref="C3Event.ModelComplete"/> (once; null afterwards).</summary>
        public AssembledModel TakeModel()
        {
            AssembledModel m = _done;
            _done = null;
            return m;
        }

        /// <summary>Consumer re-sync (CONTRACT §5.2): the open model is dropped; orphans are expected until a ModelBegin.</summary>
        public void Resync()
        {
            _open = false;
            _buf = null;
            _synced = false;
        }

        public C3Event Feed(uint type, byte[] payload, int offset, int count)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (offset < 0 || count < 0 || offset > payload.Length - count) throw new ArgumentOutOfRangeException("count");
            if (count == 0) return Feed(type, null, 0);
            fixed (byte* p = payload) return Feed(type, p + offset, count);
        }

        public C3Event Feed(uint type, byte* payload, int payloadBytes)
        {
            Messages++;
            if (payloadBytes < 0) return Fail("negative payload size");
            BytesFed += payloadBytes;
            if (payloadBytes > 0 && payload == null) throw new ArgumentNullException("payload");
            string err;
            switch (type)
            {
                case C3Type.ModelBegin:
                {
                    ModelBegin b;
                    if (!ModelCodec.TryRead(payload, payloadBytes, out b)) return Fail("ModelBegin size " + payloadBytes);
                    if (!ModelCodec.ValidateBegin(b, _maxModelBytes, out err)) return Fail("ModelBegin key=0x" + b.modelKey.ToString("x16") + ": " + err);
                    if (_open) Abandoned++;
                    _begin = b;
                    _buf = new byte[b.totalBytes];
                    _filled = 0;
                    _nextChunk = 0;
                    _open = true;
                    _synced = true;
                    return C3Event.Progress;
                }
                case C3Type.ModelChunk:
                {
                    if (payloadBytes < ModelCodec.ModelChunkHeaderBytes) return Fail("ModelChunk size " + payloadBytes);
                    var h = *(ModelChunkHeader*)payload;
                    int data = payloadBytes - ModelCodec.ModelChunkHeaderBytes;
                    if (h.bytes != (uint)data) return Fail("ModelChunk bytes field " + h.bytes + " != " + data);
                    if (data > C3Type.MaxChunkBytes) return Fail("ModelChunk of " + data + " B above 4 MiB");
                    if (!_open)
                    {
                        if (!_synced) { Orphans++; return C3Event.Orphan; }
                        return Fail("ModelChunk without ModelBegin key=0x" + h.modelKey.ToString("x16"));
                    }
                    if (h.modelKey != _begin.modelKey)
                        return Fail("ModelChunk key 0x" + h.modelKey.ToString("x16") + " inside model 0x" + _begin.modelKey.ToString("x16"));
                    if (h.chunkIndex != _nextChunk) return Fail("ModelChunk index " + h.chunkIndex + ", expected " + _nextChunk);
                    if (_nextChunk >= _begin.chunkCount) return Fail("more chunks than chunkCount " + _begin.chunkCount);
                    if (data == 0) return Fail("empty ModelChunk");
                    if (_filled + data > _buf.LongLength) return Fail("chunks exceed totalBytes " + _begin.totalBytes);
                    fixed (byte* d = _buf) Buffer.MemoryCopy(payload + ModelCodec.ModelChunkHeaderBytes, d + _filled, _buf.LongLength - _filled, data);
                    _filled += data;
                    _nextChunk++;
                    return C3Event.Progress;
                }
                case C3Type.ModelEnd:
                {
                    ModelEnd e;
                    if (!ModelCodec.TryRead(payload, payloadBytes, out e)) return Fail("ModelEnd size " + payloadBytes);
                    if (!_open)
                    {
                        if (!_synced) { Orphans++; return C3Event.Orphan; }
                        return Fail("ModelEnd without ModelBegin key=0x" + e.modelKey.ToString("x16"));
                    }
                    if (e.modelKey != _begin.modelKey)
                        return Fail("ModelEnd key 0x" + e.modelKey.ToString("x16") + " inside model 0x" + _begin.modelKey.ToString("x16"));
                    if (_nextChunk != _begin.chunkCount || _filled != _buf.LongLength)
                        return Fail("ModelEnd after " + _nextChunk + "/" + _begin.chunkCount + " chunks, " + _filled + "/" + _begin.totalBytes + " B");
                    _done = new AssembledModel(_begin, _buf, e.payloadHash);
                    _open = false;
                    _buf = null;
                    ModelsCompleted++;
                    return C3Event.ModelComplete;
                }
                case C3Type.MaterialDef:
                {
                    MaterialDefinition m;
                    if (!ModelCodec.TryDecodeMaterialDef(payload, payloadBytes, out m, out err)) return FailKeepModel(err);
                    LastMaterial = m;
                    Materials++;
                    return C3Event.Material;
                }
                case C3Type.SkinnedBind:
                {
                    SkinnedBindDefinition s;
                    if (!ModelCodec.TryDecodeSkinnedBind(payload, payloadBytes, out s, out err)) return FailKeepModel(err);
                    LastSkinnedBind = s;
                    SkinnedBinds++;
                    return C3Event.SkinnedBind;
                }
                default:
                    return Fail("unknown C3 type " + type);
            }
        }

        private C3Event Fail(string why)
        {
            Errors++;
            LastError = why;
            _open = false;
            _buf = null;
            return C3Event.Error;
        }

        /// <summary>A bad MaterialDef/SkinnedBind is not part of the open model's message run: count it, keep the model.</summary>
        private C3Event FailKeepModel(string why)
        {
            Errors++;
            LastError = why;
            return C3Event.Error;
        }
    }
}
