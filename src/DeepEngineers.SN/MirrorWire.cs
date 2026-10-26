using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // S3 "first block", Subnautica side, Unity-free part 1 of 4 (compiled into DeepEngineers.SN.Tests):
 // the C3 model stream assembler (ModelBegin / ModelChunk / ModelEnd, MaterialDef), the model payload parser, the C4
 // grid and part state (GridAdd / EntityRemove / PartAdd / PartRemove / PartRecolor / ClearAll) and the C2 entity
 // pose table with its 4-snapshot history and the pose sampler that follows the camera's bracket (CONTRACT §2.8,
 // §5.4; ARCHITECTURE §5.8). Nothing here touches Unity: the main thread copies bytes out of the rings, the worker
 // thread parses and builds, the Unity side uploads.

 /// <summary>One C3 submesh record (24 B): the material and the triangle range (in triangles, not indices).</summary>
    internal struct SubmeshInfo
    {
        public ulong MaterialKey;
        public int TriStart, TriCount;
        public uint DrawTechnique;
    }

    /// <summary>
 /// A parsed C3 model (CONTRACT §5.4 "Model payload"): vertices of 56 B (pos, normal, tangent, binormal, uv) or 76 B
 /// (skinned), then u32 indices in <c>MyModel.Triangles</c> order, then 24-B submeshes. SE model space, SE-handed.
 /// </summary>
    internal sealed class ModelData
    {
        public ulong Key;
        public uint Lod, Flags;
        public int VertexCount, IndexCount, SubmeshCount;
        public float PatternScale;
        public float BMinX, BMinY, BMinZ, BMaxX, BMaxY, BMaxZ;
        public bool Skinned;
        public float[] Pos, Nrm, Tan, Bin; // 3 per vertex
        public float[] Uv;                  // 2 per vertex
        public int[] Indices;               // IndexCount (I0, I1, I2 per triangle, MyModel.Triangles order)
        public SubmeshInfo[] Submeshes;
        public ulong PayloadHash;
        /// <summary>S5: skinned models only (76-B vertices): 4 canonical bone indices and 4 weights per vertex, as received.</summary>
        public byte[] BoneIdx;
        public float[] Weights;

        public int TriangleCount { get { return IndexCount / 3; } }
    }

    /// <summary>The model payload layout and its parser (Unity-free; runs on the mirror worker thread).</summary>
    internal static unsafe class ModelPayload
    {
        public const int VertexBytes = 56;
        public const int SkinnedVertexBytes = 76;
        public const int IndexBytes = 4;
        public const int SubmeshBytes = 24;

        public static long ExpectedBytes(long vertexCount, long indexCount, long submeshCount, bool skinned)
        {
            return vertexCount * (skinned ? SkinnedVertexBytes : VertexBytes) + indexCount * IndexBytes + submeshCount * SubmeshBytes;
        }

        /// <summary>
 /// Parses a complete payload (the concatenated ModelChunk bytes) against its ModelBegin. Every count, index and
 /// submesh range is checked; a payload that does not match the declared counts exactly is refused.
 /// </summary>
        public static bool TryParse(ModelBegin b, byte[] data, int length, out ModelData m, out string error)
        {
            m = null;
            error = null;
            bool skinned = (b.flags & ModelFlags.Skinned) != 0;
            if (b.vertexCount == 0 || b.indexCount == 0) { error = "vertexCount or indexCount is 0"; return false; }
            if (b.indexCount % 3 != 0) { error = "indexCount " + b.indexCount + " is not a multiple of 3"; return false; }
            if (b.vertexCount > 16u << 20 || b.indexCount > 64u << 20 || b.submeshCount > 4096) { error = "counts beyond the plugin's limits"; return false; }
            long expected = ExpectedBytes(b.vertexCount, b.indexCount, b.submeshCount, skinned);
            if (data == null || length < 0 || length > data.Length) { error = "no payload"; return false; }
            if (expected != length)
            {
                error = "payload " + length + " B != " + expected + " B for " + b.vertexCount + " vertices, " + b.indexCount + " indices, " +
                        b.submeshCount + " submeshes" + (skinned ? " (skinned)" : "");
                return false;
            }
            int v = (int)b.vertexCount, n = (int)b.indexCount, s = (int)b.submeshCount;
            var r = new ModelData
            {
                Key = b.modelKey, Lod = b.lod, Flags = b.flags, VertexCount = v, IndexCount = n, SubmeshCount = s,
                PatternScale = b.patternScale, Skinned = skinned,
                BMinX = b.bounds[0], BMinY = b.bounds[1], BMinZ = b.bounds[2], BMaxX = b.bounds[3], BMaxY = b.bounds[4], BMaxZ = b.bounds[5],
                Pos = new float[v * 3], Nrm = new float[v * 3], Tan = new float[v * 3], Bin = new float[v * 3], Uv = new float[v * 2],
                Indices = new int[n], Submeshes = new SubmeshInfo[s],
            };
            int stride = skinned ? SkinnedVertexBytes : VertexBytes;
            if (skinned) { r.BoneIdx = new byte[v * 4]; r.Weights = new float[v * 4]; }
            fixed (byte* p0 = data)
            {
                for (int i = 0; i < v; i++)
                {
                    float* f = (float*)(p0 + (long)i * stride);
                    int o3 = i * 3, o2 = i * 2;
                    r.Pos[o3] = f[0]; r.Pos[o3 + 1] = f[1]; r.Pos[o3 + 2] = f[2];
                    r.Nrm[o3] = f[3]; r.Nrm[o3 + 1] = f[4]; r.Nrm[o3 + 2] = f[5];
                    r.Tan[o3] = f[6]; r.Tan[o3 + 1] = f[7]; r.Tan[o3 + 2] = f[8];
                    r.Bin[o3] = f[9]; r.Bin[o3 + 1] = f[10]; r.Bin[o3 + 2] = f[11];
                    r.Uv[o2] = f[12]; r.Uv[o2 + 1] = f[13];
                    if (skinned)
                    {
                        // S5 (CONTRACT §5.4 "Model payload"): u8 boneIndex[4] at +56, f32 weight[4] at +60
                        byte* bi = p0 + (long)i * stride + 56;
                        float* bw = (float*)(bi + 4);
                        for (int k = 0; k < 4; k++) { r.BoneIdx[i * 4 + k] = bi[k]; r.Weights[i * 4 + k] = bw[k]; }
                    }
                }
                for (int i = 0; i < r.Pos.Length; i++)
                {
                    float x = r.Pos[i];
                    if (float.IsNaN(x) || float.IsInfinity(x)) { error = "non-finite vertex position at vertex " + (i / 3); return false; }
                }
                uint* idx = (uint*)(p0 + (long)v * stride);
                for (int i = 0; i < n; i++)
                {
                    uint k = idx[i];
                    if (k >= (uint)v) { error = "index " + k + " at " + i + " >= vertexCount " + v; return false; }
                    r.Indices[i] = (int)k;
                }
                byte* sm = p0 + (long)v * stride + (long)n * IndexBytes;
                int tris = n / 3;
                for (int i = 0; i < s; i++)
                {
                    byte* q = sm + i * SubmeshBytes;
                    var info = new SubmeshInfo
                    {
                        MaterialKey = *(ulong*)q,
                        TriStart = (int)*(uint*)(q + 8),
                        TriCount = (int)*(uint*)(q + 12),
                        DrawTechnique = *(uint*)(q + 16),
                    };
                    if (info.TriStart < 0 || info.TriCount < 0 || (long)info.TriStart + info.TriCount > tris)
                    {
                        error = "submesh " + i + " triangles [" + info.TriStart + ", +" + info.TriCount + ") outside " + tris + " triangles";
                        return false;
                    }
                    r.Submeshes[i] = info;
                }
            }
            m = r;
            return true;
        }

        /// <summary>FNV-1a 64 over a byte range (the ModelEnd convention: over the concatenated chunk payloads).</summary>
        public static ulong Hash(byte[] data, int offset, int count, ulong h)
        {
            if (count <= 0) return h;
            fixed (byte* p = data) return LayoutHash.Fnv1a64Continue(h, p + offset, count);
        }

        /// <summary>
 /// The payloadHash check of a completed model. Worker thread. Synthetic S0 models
 /// (no payload kept) are not hashed and pass.
 /// </summary>
        public static bool HashMatches(CompletedModel c, out ulong actual)
        {
            actual = 0;
            if (c == null) return false;
            if (c.Synthetic || c.Payload == null) return true;
            actual = Hash(c.Payload, 0, c.Length, LayoutHash.FnvOffsetBasis);
            return actual == c.Hash;
        }

        /// <summary>
 /// The submesh material keys of a complete payload, read from its 24-B submesh table only (cheap enough for the
 /// main thread: what C7 pendingParts needs before the worker has parsed the model); null when the payload does not
 /// match the counts (the worker then refuses the model).
 /// </summary>
        public static ulong[] SubmeshMaterialKeys(ModelBegin b, byte[] data, int length)
        {
            bool skinned = (b.flags & ModelFlags.Skinned) != 0;
            if (data == null || b.submeshCount > 4096) return null;
            long expected = ExpectedBytes(b.vertexCount, b.indexCount, b.submeshCount, skinned);
            if (expected != length || length > data.Length) return null;
            long off = (long)b.vertexCount * (skinned ? SkinnedVertexBytes : VertexBytes) + (long)b.indexCount * IndexBytes;
            var keys = new ulong[b.submeshCount];
            for (int i = 0; i < keys.Length; i++) keys[i] = BitConverter.ToUInt64(data, (int)(off + (long)i * SubmeshBytes));
            return keys;
        }
    }

    /// <summary>A C3 MaterialDef (CONTRACT §5.4): key, draw technique, flags and four absolute texture paths.</summary>
    internal sealed class MaterialDefData
    {
        public ulong Key;
        public uint DrawTechnique, Flags;
        public string ColorMetal = "", NormalGloss = "", AddMaps = "", AlphaMask = "";

        public static unsafe bool TryParse(byte* p, int bytes, out MaterialDefData def, out string error)
        {
            def = null;
            error = null;
            if (bytes < 16 + 8) { error = "MaterialDef of " + bytes + " B (needs the 16-B header and four u16 lengths)"; return false; }
            var d = new MaterialDefData { Key = *(ulong*)p, DrawTechnique = *(uint*)(p + 8), Flags = *(uint*)(p + 12) };
            int off = 16;
            var s = new string[4];
            for (int i = 0; i < 4; i++)
            {
                if (off + 2 > bytes) { error = "MaterialDef string " + i + " length beyond the payload"; return false; }
                int len = *(ushort*)(p + off);
                off += 2;
                if (off + len > bytes) { error = "MaterialDef string " + i + " (" + len + " B) beyond the payload"; return false; }
                s[i] = len == 0 ? "" : Encoding.UTF8.GetString(ToArray(p + off, len));
                off += len;
            }
            d.ColorMetal = s[0]; d.NormalGloss = s[1]; d.AddMaps = s[2]; d.AlphaMask = s[3];
            def = d;
            return true;
        }

        private static unsafe byte[] ToArray(byte* p, int n)
        {
            var a = new byte[n];
            for (int i = 0; i < n; i++) a[i] = p[i];
            return a;
        }

        public string Describe()
        {
            return "material=" + DeLog.Hex(Key) + " technique=" + DrawTechnique + " flags=" + DeLog.Hex(Flags) + " cm='" + ColorMetal +
                   "' ng='" + NormalGloss + "' add='" + AddMaps + "' alpha='" + AlphaMask + "'";
        }
    }

    internal enum C3Outcome
    {
        None = 0,
        /// <summary>A model finished (see <see cref="ModelAssembler.Completed"/>).</summary>
        ModelComplete = 1,
        /// <summary>A MaterialDef was parsed (see <see cref="ModelAssembler.CompletedMaterial"/>).</summary>
        MaterialDefined = 2,
        /// <summary>A message that cannot come from a correct producer (counted; <see cref="ModelAssembler.LastError"/>).</summary>
        Error = 3,
        /// <summary>A valid message this slice does not use, or data dropped by a re-sync.</summary>
        Ignored = 4,
        /// <summary>S5: a SkinnedBind was parsed (see <see cref="ModelAssembler.CompletedBind"/>).</summary>
        SkinnedBind = 5,
    }

    /// <summary>One finished model: its ModelBegin and the concatenated chunk payload (or, for S0 synthetic traffic, none).</summary>
    internal sealed class CompletedModel
    {
        public ModelBegin Begin;
        public byte[] Payload;
        public int Length;
        /// <summary>vertexCount 0: the S0 stand-ins' synthetic bytes (not a mesh; complete, never built).</summary>
        public bool Synthetic;
        /// <summary>ModelEnd.payloadHash as declared by the producer; checked against the payload on the worker (<see cref="ModelPayload.HashMatches"/>).</summary>
        public ulong Hash;
    }

    /// <summary>
 /// C3 consumer state machine (CONTRACT §5.1 C3, §5.4): ModelBegin opens a model, its ModelChunks must follow in
 /// order with the right key, and ModelEnd closes it when every chunk and every declared byte arrived. The chunk bytes
 /// are only copied here (main thread); the FNV-1a 64 of the concatenated chunk payloads is compared with
 /// <c>payloadHash</c> on the worker, whose mismatches the consumer reports through <see cref="NoteHashMismatch"/>. MaterialDef is parsed whole. A
 /// model whose vertexCount is 0 is the S0 stand-ins' synthetic traffic: assembled and counted, never hashed or kept
 /// (the S0 convention hashed the bodies without their 16-B stamps). After a ring re-sync, chunks and ends seen before
 /// the next ModelBegin are orphans, not errors. Pointers passed to <see cref="Feed"/> are only read during the call.
 /// </summary>
    internal sealed unsafe class ModelAssembler
    {
        /// <summary>Largest model the plugin accepts (a large cockpit is ~2 MB; CONTRACT bounds a message to half the ring).</summary>
        public const long MaxModelBytes = 256L << 20;

        private bool _active, _synced = true;
        private ModelBegin _begin;
        private byte[] _buf;
        private int _filled;
        private uint _next;

        public long Messages, Bytes, Misparse, Abandoned, Orphans, HashMismatch, Synthetic, ModelsCompleted, MaterialsDefined;

        /// <summary>The worker found a completed model's payload hash wrong (counted as a misparse too).</summary>
        public void NoteHashMismatch(string what)
        {
            HashMismatch++;
            Misparse++;
            LastError = what;
        }
        public string LastError { get; private set; }
        public CompletedModel Completed { get; private set; }
        public MaterialDefData CompletedMaterial { get; private set; }
        /// <summary>S5: the SkinnedBind of the last <see cref="C3Outcome.SkinnedBind"/> (Wire's codec).</summary>
        public SkinnedBindDefinition CompletedBind { get; private set; }
        public long BindsDefined;
        public bool InModel { get { return _active; } }

        /// <summary>The consumer tail moved to the head (new SE generation, link epoch): the model in flight is dropped.</summary>
        public void OnResync()
        {
            if (_active) Abandoned++;
            _active = false;
            _buf = null;
            _synced = false;
        }

        private C3Outcome Bad(string what)
        {
            Misparse++;
            LastError = what;
            return C3Outcome.Error;
        }

        public C3Outcome Feed(uint type, byte* p, int bytes)
        {
            Messages++;
            Bytes += bytes + ByteRing.HeaderBytes;
            Completed = null;
            CompletedMaterial = null;
            CompletedBind = null;
            switch (type)
            {
                case C3Type.ModelBegin:
                {
                    if (bytes != sizeof(ModelBegin)) return Bad("ModelBegin of " + bytes + " B (64 expected)");
                    ModelBegin b = *(ModelBegin*)p;
                    if (b.totalBytes > MaxModelBytes) return Bad("ModelBegin " + DeLog.Hex(b.modelKey) + " totalBytes " + b.totalBytes + " beyond the plugin's limit");
                    if (_active) Abandoned++;
                    _begin = b;
                    _active = true;
                    _synced = true;
                    _filled = 0;
                    _next = 0;
                    bool synthetic = b.vertexCount == 0;
                    _buf = synthetic ? null : new byte[b.totalBytes];
                    return C3Outcome.None;
                }
                case C3Type.ModelChunk:
                {
                    if (bytes < sizeof(ModelChunkHeader)) return Bad("ModelChunk of " + bytes + " B");
                    ModelChunkHeader h = *(ModelChunkHeader*)p;
                    int data = bytes - sizeof(ModelChunkHeader);
                    if (h.bytes != (uint)data) return Bad("ModelChunk bytes field " + h.bytes + " != " + data);
                    if (!_active)
                    {
                        if (!_synced) { Orphans++; return C3Outcome.Ignored; }
                        return Bad("ModelChunk " + DeLog.Hex(h.modelKey) + "/" + h.chunkIndex + " without ModelBegin");
                    }
                    if (h.modelKey != _begin.modelKey || h.chunkIndex != _next)
                    {
                        _active = false;
                        _synced = false; // the rest of this model's chunks are orphans, not new errors
                        return Bad("ModelChunk " + DeLog.Hex(h.modelKey) + "/" + h.chunkIndex + " out of order (expected " + DeLog.Hex(_begin.modelKey) + "/" + _next + ")");
                    }
                    if ((long)_filled + data > _begin.totalBytes)
                    {
                        _active = false;
                        _synced = false;
                        return Bad("ModelChunk " + DeLog.Hex(h.modelKey) + "/" + h.chunkIndex + " beyond totalBytes " + _begin.totalBytes);
                    }
                    byte* src = p + sizeof(ModelChunkHeader);
                    if (_buf != null && data > 0)
                    {
                        // a copy only: the hash is checked on the worker (CONTRACT §5.4)
                        fixed (byte* dst = _buf) Buffer.MemoryCopy(src, dst + _filled, _buf.Length - _filled, data);
                    }
                    _filled += data;
                    _next++;
                    return C3Outcome.None;
                }
                case C3Type.ModelEnd:
                {
                    if (bytes != sizeof(ModelEnd)) return Bad("ModelEnd of " + bytes + " B (16 expected)");
                    ModelEnd e = *(ModelEnd*)p;
                    if (!_active)
                    {
                        if (!_synced) { Orphans++; return C3Outcome.Ignored; }
                        return Bad("ModelEnd " + DeLog.Hex(e.modelKey) + " without ModelBegin");
                    }
                    _active = false;
                    if (e.modelKey != _begin.modelKey) return Bad("ModelEnd " + DeLog.Hex(e.modelKey) + " closes " + DeLog.Hex(_begin.modelKey));
                    if (_next != _begin.chunkCount || _filled != _begin.totalBytes)
                        return Bad("ModelEnd " + DeLog.Hex(e.modelKey) + " incomplete: chunks " + _next + "/" + _begin.chunkCount + ", bytes " + _filled + "/" + _begin.totalBytes);
                    bool synthetic = _buf == null;
                    if (synthetic) Synthetic++;
                    ModelsCompleted++;
                    Completed = new CompletedModel { Begin = _begin, Payload = _buf, Length = _filled, Synthetic = synthetic, Hash = e.payloadHash };
                    _buf = null;
                    return C3Outcome.ModelComplete;
                }
                case C3Type.MaterialDef:
                {
                    MaterialDefData def;
                    string error;
                    if (!MaterialDefData.TryParse(p, bytes, out def, out error)) return Bad(error);
                    MaterialsDefined++;
                    CompletedMaterial = def;
                    return C3Outcome.MaterialDefined;
                }
                case C3Type.SkinnedBind:
                {
                    // S5 (CONTRACT §5.4 "Character"): the character models' bind matrices, kept by model key
                    SkinnedBindDefinition sb;
                    string error;
                    if (!ModelCodec.TryDecodeSkinnedBind(p, bytes, out sb, out error)) return Bad(error);
                    for (int i = 0; i < sb.Rows.Length; i++)
                        if (float.IsNaN(sb.Rows[i]) || float.IsInfinity(sb.Rows[i])) return Bad("SkinnedBind " + DeLog.Hex(sb.ModelKey) + " non-finite matrix");
                    BindsDefined++;
                    CompletedBind = sb;
                    return C3Outcome.SkinnedBind;
                }
                default:
                    return Bad("unknown C3 type " + type);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------- C4

 /// <summary>A chunk cell of a grid (8×8×8 blocks: 20 m on large grids, 4 m on small ones), from the part translation.</summary>
    internal struct CellKey : IEquatable<CellKey>
    {
        public readonly int X, Y, Z;
        /// <summary>S7: the interior class of the cell's parts (S7Conventions.InteriorClass; 0 Exterior = every pre-S7 cell), so
 /// Inside, Boundary and Exterior parts never share a chunk (CONTRACT §5.4 "Interior chunks"; InteriorRules.cs).</summary>
        public readonly byte C;
        public CellKey(int x, int y, int z) { X = x; Y = y; Z = z; C = 0; }
        public CellKey(int x, int y, int z, byte c) { X = x; Y = y; Z = z; C = c; }
        public CellKey WithClass(byte c) { return new CellKey(X, Y, Z, c); }
        public bool Equals(CellKey o) { return X == o.X && Y == o.Y && Z == o.Z && C == o.C; }
        public override bool Equals(object obj) { return obj is CellKey && Equals((CellKey)obj); }
        public override int GetHashCode() { return X * 73856093 ^ Y * 19349663 ^ Z * 83492791 ^ C * 2654435; }
        public override string ToString() { return X.ToString(CultureInfo.InvariantCulture) + "." + Y.ToString(CultureInfo.InvariantCulture) + "." + Z.ToString(CultureInfo.InvariantCulture) + (C == 0 ? "" : C == 2 ? ".in" : ".bd"); }

        public static CellKey Of(float tx, float ty, float tz, float cellSize)
        {
            return new CellKey((int)Math.Floor(tx / cellSize), (int)Math.Floor(ty / cellSize), (int)Math.Floor(tz / cellSize));
        }
    }

    /// <summary>One C4 PartAdd (as last received; PartRecolor updates the colour).</summary>
    internal sealed class PartRecord
    {
        public ulong EntityId, PartKey, ModelKey;
        /// <summary>SE-local rows Right, Up, Backward, Translation (12 floats).</summary>
        public readonly float[] Local = new float[12];
        public float H, S, V;
        public float OffU, OffV;
        public uint SkinId, Flags;
        public CellKey Cell;
        /// <summary>S5 (CONTRACT §5.4 "PartAdd (S5)"): how the part is drawn and its class (1 for a version-0 S3 part).</summary>
        public PartRole Role;
        public uint Class = S5Conv.ClassCubePart;

        /// <summary>Merged into its cell's chunks (classes 1–4 without Dynamic, every S3 part).</summary>
        public bool Chunked { get { return Role == PartRole.Chunk; } }

        /// <summary>The flag bits of the chunk key (NoShadows | MetalnessColorable; 0 for an S3 part).</summary>
        public uint ChunkBits { get { return S5PartRules.ChunkBits(Flags); } }

        public PartRecord Clone()
        {
            var c = (PartRecord)MemberwiseClone();
            Array.Copy(Local, c.Local, 12);
            return c;
        }

        /// <summary>The colour as a key (ColorMaskHSV quantised to 1e-4).</summary>
        public long ColourKey { get { return ColourRules.Key(H, S, V); } }
    }

    /// <summary>One mirrored entity (C4 GridAdd) and its parts.</summary>
    internal sealed class GridRecord
    {
        public ulong Id, ParentId;
        public uint TargetClass, Flags, WorldEpoch;
        public readonly Dictionary<ulong, PartRecord> Parts = new Dictionary<ulong, PartRecord>();
        /// <summary>Cells whose chunks must be rebuilt (with the frame each was dirtied in: S5's dirty-newest order).</summary>
        public readonly DirtyCellSet DirtyCells = new DirtyCellSet();
        /// <summary>S5: the chunked parts of each cell (a cell build reads its own parts, not every part of the grid).</summary>
        public readonly Dictionary<CellKey, HashSet<ulong>> CellParts = new Dictionary<CellKey, HashSet<ulong>>();
        /// <summary>S5: a Child part (Dynamic, tool, floating object) was added, removed, replaced or recoloured.</summary>
        public bool ChildrenDirty;
        /// <summary>S5: the last GridAdd changed parentEntityId (re-parent: the view moves, its meshes stay).</summary>
        public bool ParentChanged;
        public int Version;
        /// <summary>S7: the entity's airtight-room boxes (C4 RoomSet; null = none): the chunk class of its parts (InteriorChunks).</summary>
        public CellBox[] Rooms;

        /// <summary>8 blocks per chunk side: 20 m on large grids (2.5 m blocks), 4 m on small grids (0.5 m blocks).</summary>
        public float CellSize { get { return TargetClass == GridClass.SmallGrid ? 4f : 20f; } }

        /// <summary>S5: a subgrid (GridAdd parentEntityId = its physical hierarchy's root), posed from C2's subpart table.</summary>
        public bool IsSubgrid { get { return ParentId != 0 && ParentId != Id; } }

        /// <summary>Adds or replaces a part: chunked parts enter their cell's index (old and new cells dirtied), others mark the children dirty.</summary>
        public void PutPart(PartRecord pr)
        {
            PartRecord old;
            if (Parts.TryGetValue(pr.PartKey, out old)) Unindex(old);
            Parts[pr.PartKey] = pr;
            if (pr.Chunked)
            {
                pr.Cell = InteriorChunks.CellOf(this, pr.Local[9], pr.Local[10], pr.Local[11]); // S7: the cell with its interior class
                HashSet<ulong> set;
                if (!CellParts.TryGetValue(pr.Cell, out set)) { set = new HashSet<ulong>(); CellParts[pr.Cell] = set; }
                set.Add(pr.PartKey);
                DirtyCells.Add(pr.Cell);
            }
            else ChildrenDirty = true;
            Version++;
        }

        public bool RemovePart(ulong key)
        {
            PartRecord old;
            if (!Parts.TryGetValue(key, out old)) return false;
            Parts.Remove(key);
            Unindex(old);
            Version++;
            return true;
        }

        /// <summary>A recoloured part: its cell (chunked) or the children (others) are rebuilt.</summary>
        public void Touch(PartRecord pr)
        {
            if (pr.Chunked) DirtyCells.Add(pr.Cell);
            else ChildrenDirty = true;
            Version++;
        }

        private void Unindex(PartRecord old)
        {
            if (old.Chunked)
            {
                HashSet<ulong> set;
                if (CellParts.TryGetValue(old.Cell, out set)) { set.Remove(old.PartKey); if (set.Count == 0) CellParts.Remove(old.Cell); }
                DirtyCells.Add(old.Cell);
            }
            else ChildrenDirty = true;
        }

        /// <summary>Every cell with parts marked dirty (rebuild all).</summary>
        public void DirtyAll()
        {
            foreach (CellKey c in CellParts.Keys) DirtyCells.Add(c);
            ChildrenDirty = true;
        }
    }

    /// <summary>S5: the player's character (C4 CharacterAdd with the version nibble 1) and its parts (CONTRACT §5.4 "Character").</summary>
    internal sealed class CharacterRecord
    {
        public ulong Id, ModelKey;
        public uint Flags, BoneCount;
        public readonly Dictionary<ulong, PartRecord> Parts = new Dictionary<ulong, PartRecord>();
        public int Version;

        public bool Fallback { get { return (Flags & S5Conv.CharFallback) != 0; } }
        /// <summary>The C1 bonesVersion that matches this character (the low 32 bits of the body modelKey).</summary>
        public uint BonesVersion { get { return (uint)(ModelKey & 0xFFFFFFFFUL); } }
    }

    internal enum C4Outcome
    {
        None = 0, GridAdded = 1, GridRemoved = 2, PartChanged = 3, ClearAll = 4, Ignored = 5, Error = 6,
        /// <summary>S5: a version-1 CharacterAdd registered (or replaced) the player's character.</summary>
        CharacterAdded = 7,
    }

    /// <summary>Colour key and paint helpers that need no Unity type.</summary>
    internal static class ColourRules
    {
        public static long Key(float h, float s, float v)
        {
            long a = (long)Math.Round(Clamp(h, -2f, 2f) * 10000.0) + 20000;
            long b = (long)Math.Round(Clamp(s, -2f, 2f) * 10000.0) + 20000;
            long c = (long)Math.Round(Clamp(v, -2f, 2f) * 10000.0) + 20000;
            return (a << 32) | (b << 16) | c;
        }

        private static float Clamp(float x, float lo, float hi)
        {
            if (float.IsNaN(x)) return 0f;
            return x < lo ? lo : (x > hi ? hi : x);
        }
    }

    /// <summary>
 /// C4 consumer state (CONTRACT §5.4 C4): grids by entity id, their parts, and the parts that arrived before their
 /// GridAdd (orphans, kept until the grid arrives). Counters as the S0 stand-ins define them for C7:
 /// <c>mirroredEntityCount</c> = grids currently known; <c>pendingParts</c> = parts whose model or materials are not
 /// complete yet (computed by the caller from <see cref="ForEachPart"/>). Records of another world epoch than the
 /// acknowledged hello are dropped (GridAdd carries the epoch), and so are the part records of a grid whose GridAdd was
 /// dropped. Records lost to
 /// a corrupt-framing re-sync (<see cref="NoteLoss"/>) keep <see cref="LostSinceClearAll"/> set until the next ClearAll,
 /// which C7 pendingParts reports so that SE's consumer audit resends (finding 5). SkinSet, RoomSet and CharacterAdd
 /// are later slices.
 /// </summary>
    internal sealed unsafe class MirrorState
    {
        public readonly Dictionary<ulong, GridRecord> Grids = new Dictionary<ulong, GridRecord>();
        public readonly Dictionary<ulong, Dictionary<ulong, PartRecord>> Orphans = new Dictionary<ulong, Dictionary<ulong, PartRecord>>();
        /// <summary>Grids whose GridAdd the epoch filter dropped (their part records are dropped too); cleared by ClearAll.</summary>
        public readonly HashSet<ulong> DroppedGrids = new HashSet<ulong>();
        /// <summary>S5: the player's character(s) known by a version-1 CharacterAdd (CONTRACT §5.4 "Character").</summary>
        public readonly Dictionary<ulong, CharacterRecord> Characters = new Dictionary<ulong, CharacterRecord>();
        /// <summary>S6: the C4 type 6 SkinSet store (CONTRACT §5.4 "C4 type 6 SkinSet (S6)"); the S6 host listens to it.</summary>
        public readonly SkinStore Skins = new SkinStore();
        /// <summary>S7: the C4 type 7 RoomSet store (CONTRACT §5.4 "C4 RoomSet (S7)"; InteriorRules.cs).</summary>
        public readonly InteriorStore Rooms = new InteriorStore();
        /// <summary>Review F4: one climbing serial for every character record change, so a re-sent character (same id, same
 /// model) never repeats a Version the mirror already applied.</summary>
        public int CharacterSerial;
        /// <summary>The acknowledged hello epoch (0: accept every GridAdd).</summary>
        public uint AcceptEpoch;
        /// <summary>S5: bumped by every accepted C4 record (the consumer's cached C7 pendingParts is recomputed only after a change).</summary>
        public long ChangeSerial;

        public long Messages, Misparse, Ignored, ClearAlls, EpochDropped, OrphanParts, Recolors, Removes, Losses;
        /// <summary>S5: records whose version nibble is neither 0 nor 1, or version-1 parts of an undefined class (ignored, not errors).</summary>
        public long ConventionsRejected;
        public string LastError { get; private set; }
        public ClearAll LastClearAll;

        /// <summary>Records were lost (a corrupt-framing re-sync of C3 or C4) since the last ClearAll: the mirror may be incomplete.</summary>
        public bool LostSinceClearAll { get; private set; }

        /// <summary>A consumer tail was re-synchronised over unread records (corrupt framing): reported until the next ClearAll.</summary>
        public void NoteLoss()
        {
            Losses++;
            LostSinceClearAll = true;
        }

        public int PartCount
        {
            get
            {
                int n = 0;
                foreach (GridRecord g in Grids.Values) n += g.Parts.Count;
                foreach (CharacterRecord c in Characters.Values) n += c.Parts.Count;
                foreach (Dictionary<ulong, PartRecord> o in Orphans.Values) n += o.Count;
                return n;
            }
        }

        private C4Outcome Bad(string what)
        {
            Misparse++;
            LastError = what;
            return C4Outcome.Error;
        }

        /// <summary>Everything dropped (ClearAll, new SE generation, new world epoch): the loss flag goes too.</summary>
        public void Clear()
        {
            Grids.Clear();
            Orphans.Clear();
            DroppedGrids.Clear();
            Characters.Clear();
            Skins.Clear(); // S6: ClearAll, new SE generation, new world epoch
            Rooms.Clear(); // S7
            LostSinceClearAll = false;
            ChangeSerial++;
        }

        /// <summary>Every part, adopted or orphan (orphan parts are always pending).</summary>
        public void ForEachPart(Action<PartRecord, bool> visit)
        {
            foreach (GridRecord g in Grids.Values) foreach (PartRecord p in g.Parts.Values) visit(p, false);
            foreach (Dictionary<ulong, PartRecord> o in Orphans.Values) foreach (PartRecord p in o.Values) visit(p, true);
        }

        public C4Outcome Feed(uint type, byte* p, int bytes, out ulong entityId)
        {
            Messages++;
            entityId = 0;
            switch (type)
            {
                case C4Type.GridAdd:
                {
                    if (bytes != sizeof(GridAdd)) return Bad("GridAdd of " + bytes + " B");
                    GridAdd a = *(GridAdd*)p;
                    entityId = a.entityId;
                    if (AcceptEpoch != 0 && a.worldEpoch != AcceptEpoch)
                    {
                        EpochDropped++;
                        DroppedGrids.Add(a.entityId);
                        Orphans.Remove(a.entityId);
                        return C4Outcome.Ignored;
                    }
                    if (!S5PartRules.VersionAccepted(a.flags))
                    {
                        // S5 version nibble neither 0 nor 1: ignored and counted; its parts are dropped with it (never orphans)
                        ConventionsRejected++;
                        DroppedGrids.Add(a.entityId);
                        Orphans.Remove(a.entityId);
                        return C4Outcome.Ignored;
                    }
                    DroppedGrids.Remove(a.entityId);
                    ChangeSerial++;
                    GridRecord g;
                    bool known = Grids.TryGetValue(a.entityId, out g);
                    if (!known)
                    {
                        g = new GridRecord { Id = a.entityId, Rooms = Rooms.BoxesOf(a.entityId) }; // S7: a set that came first
                        Grids[a.entityId] = g;
                    }
                    // S5: a GridAdd for a known id is a re-parent or flag change; parts and meshes are kept
                    if (known && g.ParentId != a.parentEntityId) g.ParentChanged = true;
                    bool cellSizeChanged = known && g.TargetClass != a.targetClass;
                    g.TargetClass = a.targetClass;
                    g.Flags = a.flags;
                    g.ParentId = a.parentEntityId;
                    g.WorldEpoch = a.worldEpoch;
                    if (cellSizeChanged)
                    {
                        var all = new List<PartRecord>(g.Parts.Values);
                        g.Parts.Clear();
                        g.CellParts.Clear();
                        foreach (PartRecord pr in all) g.PutPart(pr);
                    }
                    Dictionary<ulong, PartRecord> orphans;
                    if (Orphans.TryGetValue(a.entityId, out orphans))
                    {
                        Orphans.Remove(a.entityId);
                        foreach (PartRecord pr in orphans.Values) g.PutPart(pr);
                    }
                    g.Version++;
                    return C4Outcome.GridAdded;
                }
                case C4Type.EntityRemove:
                {
                    if (bytes != sizeof(EntityRemove)) return Bad("EntityRemove of " + bytes + " B");
                    entityId = ((EntityRemove*)p)->entityId;
                    Grids.Remove(entityId);
                    Characters.Remove(entityId);
                    Orphans.Remove(entityId);
                    Skins.Remove(entityId); // S6: the skin goes with its entity (its version restarts at 1)
                    Rooms.Remove(entityId); // S7: its room set too
                    DroppedGrids.Remove(entityId);
                    Removes++;
                    ChangeSerial++;
                    return C4Outcome.GridRemoved;
                }
                case C4Type.PartAdd:
                {
                    if (bytes != sizeof(PartAdd)) return Bad("PartAdd of " + bytes + " B (100 expected)");
                    PartAdd a = *(PartAdd*)p;
                    entityId = a.entityId;
                    if (DroppedGrids.Contains(a.entityId)) { EpochDropped++; return C4Outcome.Ignored; }
                    uint cls;
                    PartRole role = S5PartRules.Role(a.flags, out cls);
                    if (role == PartRole.Rejected) { ConventionsRejected++; return C4Outcome.Ignored; }
                    var pr = new PartRecord
                    {
                        EntityId = a.entityId, PartKey = a.partKey, ModelKey = a.modelKey,
                        H = a.colorMaskHSV.x, S = a.colorMaskHSV.y, V = a.colorMaskHSV.z,
                        OffU = a.uvOffset.x, OffV = a.uvOffset.y, SkinId = a.skinId, Flags = a.flags,
                        Role = role, Class = cls,
                    };
                    for (int i = 0; i < 12; i++)
                    {
                        float x = a.local[i];
                        if (float.IsNaN(x) || float.IsInfinity(x)) return Bad("PartAdd " + DeLog.Hex(a.partKey) + " non-finite matrix");
                        pr.Local[i] = x;
                    }
                    ChangeSerial++;
                    GridRecord g;
                    CharacterRecord ch;
                    if (Grids.TryGetValue(a.entityId, out g)) g.PutPart(pr);
                    else if (Characters.TryGetValue(a.entityId, out ch)) { ch.Parts[a.partKey] = pr; ch.Version = ++CharacterSerial; }
                    else
                    {
                        Dictionary<ulong, PartRecord> o;
                        if (!Orphans.TryGetValue(a.entityId, out o)) { o = new Dictionary<ulong, PartRecord>(); Orphans[a.entityId] = o; }
                        o[a.partKey] = pr;
                        OrphanParts++;
                    }
                    return C4Outcome.PartChanged;
                }
                case C4Type.PartRemove:
                {
                    if (bytes != sizeof(PartRemove)) return Bad("PartRemove of " + bytes + " B");
                    PartRemove r = *(PartRemove*)p;
                    entityId = r.entityId;
                    if (DroppedGrids.Contains(r.entityId)) { EpochDropped++; return C4Outcome.Ignored; }
                    ChangeSerial++;
                    GridRecord g;
                    CharacterRecord ch;
                    if (Grids.TryGetValue(r.entityId, out g)) g.RemovePart(r.partKey);
                    else if (Characters.TryGetValue(r.entityId, out ch)) { if (ch.Parts.Remove(r.partKey)) ch.Version = ++CharacterSerial; }
                    else
                    {
                        Dictionary<ulong, PartRecord> o;
                        if (Orphans.TryGetValue(r.entityId, out o)) o.Remove(r.partKey);
                    }
                    return C4Outcome.PartChanged;
                }
                case C4Type.PartRecolor:
                {
                    if (bytes != sizeof(PartRecolor)) return Bad("PartRecolor of " + bytes + " B");
                    PartRecolor c = *(PartRecolor*)p;
                    entityId = c.entityId;
                    if (DroppedGrids.Contains(c.entityId)) { EpochDropped++; return C4Outcome.Ignored; }
                    Recolors++;
                    ChangeSerial++;
                    GridRecord g;
                    CharacterRecord ch;
                    PartRecord pr;
                    if (Grids.TryGetValue(c.entityId, out g) && g.Parts.TryGetValue(c.partKey, out pr))
                    {
                        pr.H = c.hsv.x; pr.S = c.hsv.y; pr.V = c.hsv.z;
                        g.Touch(pr);
                    }
                    else if (Characters.TryGetValue(c.entityId, out ch) && ch.Parts.TryGetValue(c.partKey, out pr))
                    {
                        pr.H = c.hsv.x; pr.S = c.hsv.y; pr.V = c.hsv.z;
                        ch.Version = ++CharacterSerial;
                    }
                    else
                    {
                        Dictionary<ulong, PartRecord> o;
                        if (Orphans.TryGetValue(c.entityId, out o) && o.TryGetValue(c.partKey, out pr)) { pr.H = c.hsv.x; pr.S = c.hsv.y; pr.V = c.hsv.z; }
                    }
                    return C4Outcome.PartChanged;
                }
                case C4Type.ClearAll:
                {
                    if (bytes != sizeof(ClearAll)) return Bad("ClearAll of " + bytes + " B");
                    LastClearAll = *(ClearAll*)p;
                    ClearAlls++;
                    Clear();
                    return C4Outcome.ClearAll;
                }
                case C4Type.CharacterAdd:
                {
                    // S5 (CONTRACT §5.4 "Character"): version 0 = an S3 producer (ignored as before); 1 = the player's character
                    if (bytes != sizeof(CharacterAdd)) return Bad("CharacterAdd of " + bytes + " B");
                    CharacterAdd a = *(CharacterAdd*)p;
                    entityId = a.entityId;
                    uint v = S5Conv.Version(a.flags);
                    if (v == 0) { Ignored++; return C4Outcome.Ignored; }
                    if (v != 1) { ConventionsRejected++; return C4Outcome.Ignored; }
                    ChangeSerial++;
                    CharacterRecord ch;
                    if (!Characters.TryGetValue(a.entityId, out ch)) { ch = new CharacterRecord { Id = a.entityId }; Characters[a.entityId] = ch; }
                    if (ch.ModelKey != 0 && ch.ModelKey != a.modelKey) ch.Parts.Clear(); // a new model: its parts follow (SE sends EntityRemove first anyway)
                    ch.ModelKey = a.modelKey;
                    ch.Flags = a.flags;
                    ch.BoneCount = a.boneCount;
                    Dictionary<ulong, PartRecord> orphans;
                    if (Orphans.TryGetValue(a.entityId, out orphans))
                    {
                        Orphans.Remove(a.entityId);
                        foreach (PartRecord pr in orphans.Values) ch.Parts[pr.PartKey] = pr;
                    }
                    ch.Version = ++CharacterSerial;
                    return C4Outcome.CharacterAdded;
                }
                case C4Type.SkinSet:
                    // S6: decoded into the skin store (stale/bad/epoch-dropped counted there); nothing for the drawn mirror to do
                    Skins.Feed(p, bytes, DroppedGrids, out entityId);
                    return C4Outcome.Ignored;
                case C4Type.RoomSet:
                {
                    // S7: decoded into the room store; the entity's parts are re-classified (dirtied cells rebuild)
                    string roomError;
                    C4Outcome ro = Rooms.Feed(p, bytes, Grids, out entityId, out roomError);
                    if (ro == C4Outcome.Error) return Bad(roomError);
                    if (ro == C4Outcome.Ignored) { ConventionsRejected++; return ro; }
                    ChangeSerial++;
                    return C4Outcome.Ignored; // no part record changed: nothing for the edit log
                }
                default:
                    return Bad("unknown C4 type " + type);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------- C2

 /// <summary>One C2 entity pose in SN world space: (o, q, s, k) (CONTRACT §2.8).</summary>
    internal struct EntityPose
    {
        public ulong Id;
        public uint Kind, Flags;
        public double Ox, Oy, Oz;
        public double Qx, Qy, Qz, Qw;
        public double S, K;

        public bool Finite
        {
            get
            {
                return !(double.IsNaN(Ox) || double.IsNaN(Oy) || double.IsNaN(Oz) || double.IsNaN(Qx) || double.IsNaN(Qy) || double.IsNaN(Qz) ||
                         double.IsNaN(Qw) || double.IsNaN(S) || double.IsNaN(K) || double.IsInfinity(Ox) || double.IsInfinity(Oy) ||
                         double.IsInfinity(Oz) || double.IsInfinity(S) || double.IsInfinity(K) || Math.Abs(Ox) > 1e6 || Math.Abs(Oy) > 1e6 ||
                         Math.Abs(Oz) > 1e6 || S <= 0 || S > 2 || K <= 0 || K > 100);
            }
        }

        public Vec3 O { get { return new Vec3(Ox, Oy, Oz); } }
        public Quat Q { get { return new Quat(Qx, Qy, Qz, Qw); } }

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            return "o=" + Ox.ToString("R", ci) + "," + Oy.ToString("R", ci) + "," + Oz.ToString("R", ci) +
                   " q=" + Qx.ToString("R", ci) + "," + Qy.ToString("R", ci) + "," + Qz.ToString("R", ci) + "," + Qw.ToString("R", ci) +
                   " s=" + S.ToString("R", ci) + " k=" + K.ToString("R", ci);
        }
    }

    /// <summary>S5: the key of a C2 subpart row (ownerId, partKey).</summary>
    internal struct RowId : IEquatable<RowId>
    {
        public readonly ulong Owner, Key;
        public RowId(ulong owner, ulong key) { Owner = owner; Key = key; }
        public bool Equals(RowId o) { return Owner == o.Owner && Key == o.Key; }
        public override bool Equals(object obj) { return obj is RowId && Equals((RowId)obj); }
        public override int GetHashCode() { return (Owner.GetHashCode() * 486187739) ^ Key.GetHashCode(); }
    }

    /// <summary>One coherent C2 table copy: entities and (S5) the subpart rows; lights are a later slice.</summary>
    internal sealed class EntitySnapshot
    {
        public ulong Tick;
        public uint WorldEpoch, Seq;
        public int Count;
        public readonly EntityPose[] Poses = new EntityPose[SeEntitiesLimits.MaxEntities];
        private readonly Dictionary<ulong, int> _index = new Dictionary<ulong, int>();
        /// <summary>S5 (CONTRACT §5.</summary>
        public int RowCount;
        public readonly ulong[] RowOwner = new ulong[SeEntitiesLimits.MaxSubparts];
        public readonly ulong[] RowKey = new ulong[SeEntitiesLimits.MaxSubparts];
        public readonly float[] RowLocal = new float[SeEntitiesLimits.MaxSubparts * 12];
        private readonly Dictionary<RowId, int> _rowIndex = new Dictionary<RowId, int>();

        public void Reindex()
        {
            _index.Clear();
            for (int i = 0; i < Count; i++) _index[Poses[i].Id] = i;
            _rowIndex.Clear();
            for (int i = 0; i < RowCount; i++) _rowIndex[new RowId(RowOwner[i], RowKey[i])] = i;
        }

        /// </summary>
        public bool TryGetRow(ulong owner, ulong key, out int index)
        {
            return _rowIndex.TryGetValue(new RowId(owner, key), out index);
        }

        public bool TryGet(ulong id, out EntityPose pose)
        {
            int i;
            if (_index.TryGetValue(id, out i)) { pose = Poses[i]; return true; }
            pose = default(EntityPose);
            return false;
        }
    }

    /// <summary>
 /// The last <see cref="Capacity"/> C2 snapshots, oldest first, keyed by SE tick (the same tick as C1's for the same
 /// SE frame, CONTRACT §5.1 C2). A tick that goes backwards or a world epoch change restarts the history.
 /// </summary>
    internal sealed class EntityHistory
    {
        public const int Capacity = 4;
        private readonly EntitySnapshot[] _s = new EntitySnapshot[Capacity];
        private EntitySnapshot _spare = new EntitySnapshot();
        public int Count { get; private set; }
        public long Resets;

        public EntitySnapshot this[int i] { get { return _s[i]; } }
        public EntitySnapshot Newest { get { return Count > 0 ? _s[Count - 1] : null; } }

        /// <summary>A recycled snapshot to fill before <see cref="Push"/>.</summary>
        public EntitySnapshot Rent()
        {
            EntitySnapshot s = _spare ?? new EntitySnapshot();
            _spare = null;
            return s;
        }

        public void Push(EntitySnapshot s)
        {
            s.Reindex();
            if (Count > 0)
            {
                EntitySnapshot n = _s[Count - 1];
                if (s.Tick < n.Tick || s.WorldEpoch != n.WorldEpoch) { Restart(s); Resets++; return; }
                if (s.Tick == n.Tick) { _spare = n; _s[Count - 1] = s; return; }
                if (Count == Capacity)
                {
                    _spare = _s[0];
                    for (int i = 1; i < Capacity; i++) _s[i - 1] = _s[i];
                    Count = Capacity - 1;
                }
            }
            _s[Count++] = s;
        }

        private void Restart(EntitySnapshot s)
        {
            if (Count > 0) _spare = _s[0];
            for (int i = 0; i < Count; i++) _s[i] = null;
            _s[0] = s;
            Count = 1;
        }

        public void Clear()
        {
            for (int i = 0; i < Count; i++) _s[i] = null;
            Count = 0;
        }

        /// <summary>The newest snapshot whose tick is ≤ <paramref name="tick"/>; the oldest one when every snapshot is newer.</summary>
        public EntitySnapshot AtOrBefore(ulong tick)
        {
            if (Count == 0) return null;
            for (int i = Count - 1; i >= 0; i--) if (_s[i].Tick <= tick) return _s[i];
            return _s[0];
        }

        /// <summary>The snapshot with exactly this tick, or null.</summary>
        public EntitySnapshot Exactly(ulong tick)
        {
            for (int i = Count - 1; i >= 0; i--) if (_s[i].Tick == tick) return _s[i];
            return null;
        }

        /// <summary>The newest snapshot strictly older than <paramref name="tick"/>, or null.</summary>
        public EntitySnapshot Before(ulong tick)
        {
            for (int i = Count - 1; i >= 0; i--) if (_s[i].Tick < tick) return _s[i];
            return null;
        }
    }

    /// <summary>What the camera used for one render: the bracket ticks and the alpha (FrameReader.CamSample).</summary>
    internal struct CamBracket
    {
        public bool Valid;
        public ulong Older, Newer;
        public double Alpha;
        /// <summary>S5 review F1 (camera-group lock): the camera orientation's tick, and the camera's interpolation offset
 /// P(bracket) - P(newest). Entities that move with the camera (the grid whose group holds it, the seated or
 /// first-person character) are posed at NewestTick and translated by (DPx, DPy, DPz), so camera and ride share
 /// one tick (ship-render 6.7 #2; S5.N4).</summary>
        public ulong NewestTick;
        public double DPx, DPy, DPz;

        public static CamBracket From(CamSample s)
        {
            if (s.Outcome == SampleOutcome.None) return default(CamBracket);
            return new CamBracket { Valid = true, Older = s.OlderTick, Newer = s.PresentedTick, Alpha = s.Alpha, NewestTick = s.NewestTick,
                                    DPx = s.Px - s.NewestPx, DPy = s.Py - s.NewestPy, DPz = s.Pz - s.NewestPz };
        }

        /// <summary>The same frame's bracket locked at the camera orientation's tick (alpha 1 at NewestTick).</summary>
        public CamBracket Locked()
        {
            return new CamBracket { Valid = Valid, Older = NewestTick > 0 ? NewestTick - 1 : 0, Newer = NewestTick, Alpha = 1.0, NewestTick = NewestTick, DPx = DPx, DPy = DPy, DPz = DPz };
        }
    }

    /// <summary>How a pose was obtained (counted; S3.M4 is "Previous").</summary>
    internal enum PoseHow { None = 0, Interpolated = 1, Exact = 2, Held = 3, Extrapolated = 4, Newest = 5, Previous = 6 }

    /// <summary>
 /// Entity poses for one render (ARCHITECTURE §5.8 "Interpolation"; CONTRACT §2.8): every entity with the camera's
 /// alpha between the snapshots of the camera's bracket ticks (lerp o, slerp q, lerp s; k of the newer); past the
 /// newest snapshot by at most one tick, o is extrapolated with the camera's factor; a missing tick holds the newest
 /// snapshot at or before it. Without a camera sample the newest snapshot is used. Seeded failure S3.M4: the snapshot
 /// before the bracket's newer tick, not interpolated (one-tick lag). Unity-free.
 /// </summary>
    internal static class PoseSampler
    {
        public static bool Sample(EntityHistory h, ulong id, CamBracket br, bool previousSnapshot, out EntityPose pose, out ulong tickUsed, out PoseHow how)
        {
            pose = default(EntityPose);
            tickUsed = 0;
            how = PoseHow.None;
            if (h.Count == 0) return false;
            if (!br.Valid)
            {
                for (int i = h.Count - 1; i >= 0; i--)
                {
                    if (h[i].TryGet(id, out pose)) { tickUsed = h[i].Tick; how = PoseHow.Newest; return true; }
                }
                return false;
            }
            EntitySnapshot b = h.AtOrBefore(br.Newer);
            if (b == null) return false;
            if (previousSnapshot)
            {
                EntitySnapshot prev = h.Before(b.Tick) ?? b;
                if (!prev.TryGet(id, out pose)) return false;
                tickUsed = prev.Tick;
                how = PoseHow.Previous;
                return true;
            }
            EntityPose pb;
            if (!b.TryGet(id, out pb)) return false;
            bool exactNewer = b.Tick == br.Newer;
            EntitySnapshot a = br.Older < br.Newer ? h.Exactly(br.Older) : null;
            EntityPose pa;
            if (exactNewer && a != null && a.TryGet(id, out pa))
            {
                if (br.Alpha > 1.0)
                {
                    double k = Math.Min(br.Alpha - 1.0, 1.0);
                    pose = pb;
                    pose.Ox = pb.Ox + (pb.Ox - pa.Ox) * k;
                    pose.Oy = pb.Oy + (pb.Oy - pa.Oy) * k;
                    pose.Oz = pb.Oz + (pb.Oz - pa.Oz) * k;
                    tickUsed = b.Tick;
                    how = PoseHow.Extrapolated;
                    return true;
                }
                double t = br.Alpha < 0 ? 0 : br.Alpha;
                pose = Lerp(pa, pb, t);
                tickUsed = b.Tick;
                how = PoseHow.Interpolated;
                return true;
            }
            pose = pb;
            tickUsed = b.Tick;
            how = exactNewer ? PoseHow.Exact : PoseHow.Held;
            return true;
        }

        /// <summary>o and s lerped, q slerped along the shortest arc (w ≥ 0 kept), k of the newer.</summary>
        public static EntityPose Lerp(EntityPose a, EntityPose b, double t)
        {
            EntityPose r = b;
            r.Ox = a.Ox + (b.Ox - a.Ox) * t;
            r.Oy = a.Oy + (b.Oy - a.Oy) * t;
            r.Oz = a.Oz + (b.Oz - a.Oz) * t;
            r.S = a.S + (b.S - a.S) * t;
            Quat q = Slerp(a.Q, b.Q, t);
            r.Qx = q.X; r.Qy = q.Y; r.Qz = q.Z; r.Qw = q.W;
            return r;
        }

        public static Quat Slerp(Quat a, Quat b, double t)
        {
            double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (dot < 0) { b = new Quat(-b.X, -b.Y, -b.Z, -b.W); dot = -dot; }
            double wa, wb;
            if (dot > 1.0 - 1e-9)
            {
                wa = 1 - t; wb = t;
            }
            else
            {
                double th = Math.Acos(Math.Min(1.0, dot));
                double sn = Math.Sin(th);
                wa = Math.Sin((1 - t) * th) / sn;
                wb = Math.Sin(t * th) / sn;
            }
            var q = new Quat(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
            return q.Normalized();
        }
    }

    /// <summary>
 /// C2 consumer: a seqlock table read (CONTRACT §5.3: read the header's seq, copy the header and entityCount
 /// records, fence, accept when seq is unchanged; ≤ 16 tries; keep the last coherent copy) into the
 /// <see cref="EntityHistory"/>. Unity-free (tests drive it over a managed buffer).
 /// </summary>
    internal sealed unsafe class EntityTableReader
    {
        private readonly byte* _region;
        private uint _lastSeq;
        // header + entities, then (S5) the subpart rows at their own offset, copied in the same seqlock window
        private readonly byte[] _copy = new byte[SeEntitiesLimits.SubpartsOffset + SeEntitiesLimits.MaxSubparts * SeEntitiesLimits.SubpartSize];

        public readonly EntityHistory History = new EntityHistory();
        /// <summary>
 /// S7 (B2 hook; CONTRACT §5.4 "C2 effect events" "Reader", "C2 lights (S7)"): readers of the C2 bytes beyond the S5
 /// tables (the S7 light rows, the effect-event window and the header extension), copied inside the same seqlock
 /// window and committed after a coherent read. Main thread; added and removed by the S7 hosts.
 /// </summary>
        public readonly List<IC2ExtensionReader> Extensions = new List<IC2ExtensionReader>(2);
        public long ExtensionErrors;
        public long Fresh, Busy, Invalid, Truncated, Incoherent, PairRereads;
        /// <summary>S5: subpart rows refused (non-finite) and tables longer than 2,048 rows.</summary>
        public long RowsInvalid, RowsTruncated;
        public bool EverWritten { get; private set; }

        /// <summary>CONTRACT §5.1 C2: re-reads allowed when C2's newest tick is behind the C1 tick the camera used.</summary>
        public const int PairRereads_Max = 4;

        /// <summary>
 /// The C1/C2 pair rule: polls C2, and while its newest
 /// tick is older than <paramref name="c1Tick"/> (the camera's bracket) re-reads up to 4 more times. SE writes C2
 /// before C1 of the same tick, so a reader that saw C1(N) normally finds C2(N) at once. False (counted
 /// <see cref="Incoherent"/>) when C2 stays behind: the sampler then holds the newest snapshot at or before the tick.
 /// </summary>
        public bool PollPair(ulong c1Tick)
        {
            Poll();
            for (int i = 0; i < PairRereads_Max; i++)
            {
                EntitySnapshot n = History.Newest;
                if (n != null && n.Tick >= c1Tick) return true;
                PairRereads++;
                System.Threading.Thread.SpinWait(8);
                Poll();
            }
            EntitySnapshot last = History.Newest;
            if (last != null && last.Tick >= c1Tick) return true;
            Incoherent++;
            return false;
        }

        public EntityTableReader(byte* region)
        {
            if (region == null) throw new ArgumentNullException("region");
            _region = region;
        }

        /// <summary>New SE generation: history dropped; the content present now is ignored until its seq changes.</summary>
        public void Reset()
        {
            History.Clear();
            _lastSeq = Seqlock.Current(_region);
        }

        public SeqlockReadStatus Poll()
        {
            uint cur = Seqlock.Current(_region);
            if (cur == 0) return SeqlockReadStatus.Never;
            if (cur == _lastSeq) return SeqlockReadStatus.Unchanged;
            for (int attempt = 0; attempt < Seqlock.MaxTries; attempt++)
            {
                uint seq;
                if (!Seqlock.ReadBegin(_region, out seq))
                {
                    if (seq == 0) return SeqlockReadStatus.Never;
                    System.Threading.Thread.SpinWait(4);
                    continue;
                }
                uint count = *(uint*)(_region + 0x04);
                uint rowCount = *(uint*)(_region + 0x14);
                int n = (int)Math.Min(count, (uint)SeEntitiesLimits.MaxEntities);
                int rows = (int)Math.Min(rowCount, (uint)SeEntitiesLimits.MaxSubparts);
                int bytes = SeEntitiesLimits.EntitiesOffset + n * SeEntitiesLimits.EntitySize;
                fixed (byte* dst = _copy)
                {
                    Buffer.MemoryCopy(_region, dst, _copy.Length, bytes);
                    if (rows > 0)
                        Buffer.MemoryCopy(_region + SeEntitiesLimits.SubpartsOffset, dst + SeEntitiesLimits.SubpartsOffset, _copy.Length - SeEntitiesLimits.SubpartsOffset,
                                          rows * SeEntitiesLimits.SubpartSize);
                }
                for (int x = 0; x < Extensions.Count; x++)
                {
                    try { Extensions[x].CopyInWindow(_region); } // S7: light rows, event window, pad_1c (same window)
                    catch (Exception) { ExtensionErrors++; }
                }
                if (!Seqlock.ReadEnd(_region, seq)) continue;
                _lastSeq = seq;
                EverWritten = true;
                if (count > SeEntitiesLimits.MaxEntities) Truncated++;
                if (rowCount > SeEntitiesLimits.MaxSubparts) RowsTruncated++;
                Parse(seq, n, rows);
                if (Extensions.Count > 0)
                {
                    ulong headerTick;
                    fixed (byte* c = _copy) headerTick = ((SeEntitiesHeader*)c)->tick;
                    for (int x = 0; x < Extensions.Count; x++)
                    {
                        try { Extensions[x].Commit(headerTick); }
                        catch (Exception) { ExtensionErrors++; }
                    }
                }
                Fresh++;
                return SeqlockReadStatus.Fresh;
            }
            Busy++;
            return SeqlockReadStatus.Busy;
        }

        private void Parse(uint seq, int n, int rows = 0)
        {
            EntitySnapshot s = History.Rent();
            fixed (byte* c = _copy)
            {
                SeEntitiesHeader* hdr = (SeEntitiesHeader*)c;
                s.Tick = hdr->tick;
                s.WorldEpoch = hdr->worldEpoch;
                s.Seq = seq;
                int k = 0;
                for (int i = 0; i < n; i++)
                {
                    SeEntity* e = (SeEntity*)(c + SeEntitiesLimits.EntitiesOffset + i * SeEntitiesLimits.EntitySize);
                    var p = new EntityPose
                    {
                        Id = e->entityId, Kind = e->kind, Flags = e->flags,
                        Ox = e->o.x, Oy = e->o.y, Oz = e->o.z,
                        Qx = e->q.x, Qy = e->q.y, Qz = e->q.z, Qw = e->q.w,
                        S = e->s, K = e->k == 0f ? 1.0 : e->k,
                    };
                    double qn = Math.Sqrt(p.Qx * p.Qx + p.Qy * p.Qy + p.Qz * p.Qz + p.Qw * p.Qw);
                    if (qn < 0.5 || qn > 1.5 || !p.Finite) { Invalid++; continue; }
                    p.Qx /= qn; p.Qy /= qn; p.Qz /= qn; p.Qw /= qn;
                    s.Poses[k++] = p;
                }
                s.Count = k;
                // S5 subpart rows (subgrids, dynamic subparts, the held tool): finite rows only
                int r = 0;
                for (int i = 0; i < rows; i++)
                {
                    SeSubpart* sp = (SeSubpart*)(c + SeEntitiesLimits.SubpartsOffset + i * SeEntitiesLimits.SubpartSize);
                    bool ok = true;
                    for (int j = 0; j < 12; j++)
                    {
                        float x = sp->local.e[j];
                        if (float.IsNaN(x) || float.IsInfinity(x) || Math.Abs(x) > 1e6f) { ok = false; break; }
                        s.RowLocal[r * 12 + j] = x;
                    }
                    if (!ok) { RowsInvalid++; continue; }
                    s.RowOwner[r] = sp->ownerId;
                    s.RowKey[r] = sp->partKey;
                    r++;
                }
                s.RowCount = r;
            }
            History.Push(s);
        }
    }
}
