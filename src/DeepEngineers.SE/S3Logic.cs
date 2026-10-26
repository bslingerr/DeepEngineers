using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    // Slice S3 "First block", SE side: the game-independent parts (compiled into tests/DeepEngineers.SE.Tests too).
 // Model and part data in SE space as the exporter reads them, the part diff, the per-tick budgeted ring pump, the
 // stream gate (hello ack, resend), the lab placement, the de.s3 argument syntax and the JSONL row formats. The
 // game-facing code is in ModelLoader.cs, ArmorParts.cs, MaterialExport.cs, GridMirrorProducer.cs and S3Commands.cs;
 // the C3/C4 records are built with Wire's ModelCodec through S3Codec.cs.

 /// <summary>S3 numbers in one place (ROADMAP S3; CONTRACT §5.1 C3/C4 rows).</summary>
    internal static class S3Defaults
    {
        /// <summary>The grandReef lab spot of the sessions (S1.WM-2 point): SN x, z.</summary>
        public const double LabX = -1029.0, LabZ = -1116.0;
        /// <summary>
 /// Lab config seabed height at the lab spot (HYPOTHESIS: the S1/S2 real runs put the seabed "at about −150 to
 /// −160 m" below the −150 m camera point). Used only when de.s3 gets no <c>h</c>; the S3 session passes the
 /// Subnautica layer-30 raycast instead.
 /// </summary>
        public const double LabSeabedY = -155.0;
        public const string Block = "LargeBlockArmorBlock";
        /// <summary>C3 chunk size (CONTRACT: chunks ≤ 4 MiB; a message never exceeds half the ring).</summary>
        public const int ChunkBytes = 1 << 20;
        /// <summary>C3 serialisation budget per SE tick (ROADMAP S3 "about 2 ms"; ARCHITECTURE §5.8).</summary>
        public const double C3BudgetMs = 2.0;
        public const int C3MaxMessagesPerTick = 256;
        /// <summary>C4 is small; one try per message, a separate 1 ms budget.</summary>
        public const double C4BudgetMs = 1.0;
        public const int C4MaxMessagesPerTick = 2048;
        /// <summary>Parts are re-read every this many ticks (and every tick after a block event until they settle).</summary>
        public const int PartPollTicks = 30;
        /// <summary>After a block event (or a grid registration or stream start) the parts are read every tick for this long.</summary>
        public const int FastPollTicks = 60;
        /// <summary>A part set is sent only after it read the same on this many consecutive polls (one tick apart).</summary>
        public const int PartStablePolls = 2;
        /// <summary>
 /// After an attach, a new Subnautica generation or a link epoch, an ack in C7 is trusted only once C7's snFrame
 /// advanced this much (the frame that saw the event may still carry the previous ack).
 /// </summary>
        public const ulong AckTrustFrames = 2;
        /// <summary>Pose rows are written at most this often while nothing is traced, unless the pose changed (ms).</summary>
        public const ulong PoseRowIntervalMs = 1000;
        /// <summary>At most this many pose rows per second when the pose changes every tick (outside a trace).</summary>
        public const int PoseRowsPerSecond = 10;
    }

    // ===================================================================================================== model data

 /// <summary>One SE mesh material as the C3 MaterialDef carries it, plus the raw names for SE's colour export.</summary>
    internal sealed class MaterialInfo
    {
        public ulong Key;
        public string Name;
        public uint DrawTechnique;
        /// <summary>Wire.MaterialFlags (glassCw, glassCcw, alphaMasked).</summary>
        public uint Flags;
        /// <summary>Absolute resolved paths (empty when the model names none or the file is missing).</summary>
        public string ColorMetal = "", NormalGloss = "", AddMaps = "", AlphaMask = "";
        /// <summary>The names as the model stores them (Content-relative for vanilla), for MyRenderProxy.RenderColoredTextures.</summary>
        public string ColorMetalRaw, NormalGlossRaw, AddMapsRaw, AlphaMaskRaw;
        /// <summary>Raw names that did not resolve to a file (logged).</summary>
        public string Missing = "";
    }

    internal struct ExportedSubmesh
    {
        public ulong MaterialKey;
        public int TriStart, TriCount;
        public uint DrawTechnique;
        /// <summary>S5: the SE mesh material name (<c>MyMeshMaterial.Name</c>): skins and the character split match on it.</summary>
        public string MaterialName;
    }

    /// <summary>
 /// One SE model in SE model space (LOD0, full detail), as streamed in C3 (CONTRACT §2.5, §5.4): per vertex position,
 /// normal, tangent, binormal and UV as SE stores them; triangles in <c>MyModel.Triangles</c> order (I0, I1, I2 as SE
 /// holds them, i.e. the file's second and third index swapped); submeshes from <c>GetMeshList</c> ranges.
 /// </summary>
    internal sealed class ExportedModel
    {
        public string AssetName;
        public ulong ModelKey;
        public int Lod;
        public float PatternScale = 1f;
        /// <summary>Scale applied to the positions (SE's MyModel.ScaleFactor of the part's model; 1 for armor).</summary>
        public float Scale = 1f;
        public int VertexCount;
        public float[] Positions, Normals, Tangents, Binormals, Uvs;
        public int[] Indices;
        public ExportedSubmesh[] Submeshes;
        public MaterialInfo[] Materials;
        /// <summary>S5 skinned models (ModelBegin flag skinned, 76 B vertices): 4 canonical bone indices and 4 weights per vertex.</summary>
        public byte[] BoneIndices;
        public float[] BoneWeights;
        /// <summary>S5: the C3 SkinnedBind rows (canonical order, 12 floats per bone; SE model space), or null.</summary>
        public float[] SkinnedBind;
        public bool HasTangents;
        public string TangentSource = "none";
        /// <summary>S3.C1 reference counts: <c>MyModel.GetVerticesCount</c> and <c>GetTrianglesCount</c>.</summary>
        public int SeVertexCount, SeTriangleCount;
        public double LoadMs;

        public int TriangleCount { get { return Indices == null ? 0 : Indices.Length / 3; } }

        /// <summary>Axis-aligned bounds of the positions: min x, y, z, max x, y, z (model space).</summary>
        public float[] Bounds()
        {
            if (Positions == null || VertexCount == 0) return new float[6];
            var b = new float[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < VertexCount; i++)
                for (int k = 0; k < 3; k++)
                {
                    float v = Positions[3 * i + k];
                    if (v < b[k]) b[k] = v;
                    if (v > b[3 + k]) b[3 + k] = v;
                }
            return b;
        }

        /// <summary>Consistency of the arrays and ranges; null when fine.</summary>
        public string Validate()
        {
            if (VertexCount <= 0) return "no vertices";
            if (Positions == null || Positions.Length != 3 * VertexCount) return "positions length";
            if (Normals == null || Normals.Length != 3 * VertexCount) return "normals length";
            if (Tangents == null || Tangents.Length != 3 * VertexCount) return "tangents length";
            if (Binormals == null || Binormals.Length != 3 * VertexCount) return "binormals length";
            if (Uvs == null || Uvs.Length != 2 * VertexCount) return "uvs length";
            if (Indices == null || Indices.Length == 0 || Indices.Length % 3 != 0) return "indices length";
            for (int i = 0; i < Indices.Length; i++)
                if ((uint)Indices[i] >= (uint)VertexCount) return "index " + Indices[i] + " out of range at " + i;
            if (Submeshes == null || Submeshes.Length == 0) return "no submeshes";
            if ((BoneIndices == null) != (BoneWeights == null)) return "skinning arrays incomplete";
            if (BoneIndices != null && (BoneIndices.Length != 4 * VertexCount || BoneWeights.Length != 4 * VertexCount)) return "skinning arrays length";
            if (SkinnedBind != null && (SkinnedBind.Length == 0 || SkinnedBind.Length % 12 != 0)) return "SkinnedBind length";
            int tris = TriangleCount;
            foreach (ExportedSubmesh s in Submeshes)
                if (s.TriStart < 0 || s.TriCount < 0 || s.TriStart + s.TriCount > tris) return "submesh range " + s.TriStart + "+" + s.TriCount + " > " + tris;
            return null;
        }
    }

    // ===================================================================================================== parts

 /// <summary>One armor part (a <c>MyCubePart</c> of <c>RenderData.Cells[*].CubeParts</c>) as C4 PartAdd carries it.</summary>
    internal struct PartSnapshot
    {
        public ulong PartKey;
        public ulong ModelKey;
        public string AssetName;
        public float ModelScale;
        /// <summary>Grid-local SE matrix rows Right, Up, Backward, Translation (<c>InstanceData.LocalMatrix</c>, M11…M43).</summary>
        public float[] Local;
        public float H, S, V;
        public float UvU, UvV;
        public uint SkinId;
        /// <summary>SE's own <c>GetTextureOffset(0/1)</c> (logged for comparison; always 0 in this SE build, see <see cref="PartUv"/>).</summary>
        public float LegacyU, LegacyV;
        /// <summary>PartAdd.flags: 0 from the S3 producer; from S5 the class, the flag bits and the version nibble (S5Conv).</summary>
        public uint Flags;

        public bool SameExceptColour(PartSnapshot o)
        {
            if (PartKey != o.PartKey || ModelKey != o.ModelKey || UvU != o.UvU || UvV != o.UvV || SkinId != o.SkinId || Flags != o.Flags) return false;
            if (Local == null || o.Local == null || Local.Length != 12 || o.Local.Length != 12) return false;
            for (int i = 0; i < 12; i++) if (Local[i] != o.Local[i]) return false;
            return true;
        }

        public bool SameColour(PartSnapshot o) { return H == o.H && S == o.S && V == o.V; }
    }

    /// <summary>
 /// The armor pattern UV offset of a part. SE's renderer (Content\Shaders\Geometry\VertexTemplateBase.hlsli, the
 /// USE_DEFORMED_CUBE_INSTANCING branch) reads the W bytes of the instance's packed bones 5, 6, 7: offsetU = w5,
 /// offsetV = w6, patternU = 1 + ((w7 &gt;&gt; 4) &amp; 0xF), patternV = 1 + (w7 &amp; 0xF), and draws
 /// <c>uv / PatternScale + (offsetU / patternU, offsetV / patternV)</c> (the division by PatternScale is applied to the
 /// mesh texcoords at load, VRage.Render11 MyMeshes). <c>MyCubeInstanceData.SetTextureOffset(Vector4UByte(x, y, z, w))</c>
 /// packs w5 = x, w6 = y, w7 = (w − 1) | ((z − 1) &lt;&lt; 4). SE's own
 /// <c>GetTextureOffset(index)</c> computes the divisor as <c>(b &gt;&gt; 4) &amp; 0x10</c> of a byte, which is always 0,
 /// so it returns 0 for every part.
 /// The wire therefore carries the renderer's value: PartAdd.uvOffset = (offsetU / patternU, offsetV / patternV), and
 /// ModelBegin.patternScale; the consumer draws <c>uv / patternScale + uvOffset</c>.
 /// </summary>
    internal static class PartUv
    {
        public static void Offset(byte w5, byte w6, byte w7, out float u, out float v)
        {
            MeshConventions.ArmorPatternOffsetPacked(w5, w6, w7, out u, out v);
        }

        /// <summary><c>SetTextureOffset(new Vector4UByte(x, y, z, w))</c>: the three bytes it stores (w5, w6, w7).</summary>
        public static void Pack(byte x, byte y, byte z, byte w, out byte w5, out byte w6, out byte w7)
        {
            w5 = x;
            w6 = y;
            w7 = (byte)((w - 1) | ((z - 1) << 4));
        }

        /// <summary>The decompiled <c>MyCubeInstanceData.GetTextureOffset</c> arithmetic on one byte (for the record: always 0).</summary>
        public static float LegacyGetTextureOffset(byte b)
        {
            int num = b & 0xF;
            int num2 = (b >> 4) & 0x10;
            return num2 != 0 ? (float)(num / num2) : 0f;
        }

        /// <summary>The UV the consumer draws for a stored UV (the renderer's formula).</summary>
        public static void Apply(float u, float v, float patternScale, float offU, float offV, out float ru, out float rv)
        {
            MeshConventions.ArmorUv(u, v, patternScale, offU, offV, out ru, out rv);
        }
    }

    /// <summary>Part keys and the diff between what Subnautica holds and the current part set.</summary>
    internal static class PartDiff
    {
        /// <summary>
 /// partKey = FNV-1a 64 over (modelKey u64, the packed ortho matrix of the instance: translation xyz and the packed
 /// rotation in w, 4 × f32 little-endian). Stable across re-reads and recolours; unique per (model, placement) in a
 /// grid.
 /// </summary>
        public static ulong PartKey(ulong modelKey, float tx, float ty, float tz, float packedRotation)
        {
            ulong h = Fnv.Offset;
            h = Fnv.AddU64(h, modelKey);
            h = Fnv.AddF32(h, tx);
            h = Fnv.AddF32(h, ty);
            h = Fnv.AddF32(h, tz);
            h = Fnv.AddF32(h, packedRotation);
            return h;
        }

        /// <summary>
 /// adds: in <paramref name="now"/> only, or changed other than colour (sent as remove + add); removes: in
 /// <paramref name="held"/> only, or changed; recolors: same part, new HSV. Results are sorted by part key.
 /// </summary>
        public static void Compute(Dictionary<ulong, PartSnapshot> held, Dictionary<ulong, PartSnapshot> now,
                                   List<PartSnapshot> adds, List<PartSnapshot> removes, List<PartSnapshot> recolors)
        {
            adds.Clear(); removes.Clear(); recolors.Clear();
            foreach (var kv in now)
            {
                PartSnapshot old;
                if (!held.TryGetValue(kv.Key, out old)) { adds.Add(kv.Value); continue; }
                if (!kv.Value.SameExceptColour(old)) { removes.Add(old); adds.Add(kv.Value); continue; }
                if (!kv.Value.SameColour(old)) recolors.Add(kv.Value);
            }
            foreach (var kv in held)
                if (!now.ContainsKey(kv.Key)) removes.Add(kv.Value);
            Comparison<PartSnapshot> byKey = (a, b) => a.PartKey.CompareTo(b.PartKey);
            adds.Sort(byKey); removes.Sort(byKey); recolors.Sort(byKey);
        }

        /// <summary>Order-independent content signature of a part set (debounce: equal signatures on consecutive polls).</summary>
        public static ulong Signature(Dictionary<ulong, PartSnapshot> set)
        {
            ulong sum = 0, x = 0;
            foreach (var kv in set)
            {
                PartSnapshot p = kv.Value;
                ulong h = Fnv.AddU64(Fnv.Offset, p.PartKey);
                h = Fnv.AddU64(h, p.ModelKey);
                h = Fnv.AddF32(h, p.H); h = Fnv.AddF32(h, p.S); h = Fnv.AddF32(h, p.V);
                h = Fnv.AddF32(h, p.UvU); h = Fnv.AddF32(h, p.UvV);
                h = Fnv.AddU64(h, p.SkinId);
                if (p.Flags != 0) h = Fnv.AddU64(h, p.Flags);
                if (p.Local != null) for (int i = 0; i < p.Local.Length; i++) h = Fnv.AddF32(h, p.Local[i]);
                sum += h;
                x ^= h * 0x9E3779B97F4A7C15UL;
            }
            return sum ^ (x << 1) ^ (ulong)set.Count;
        }
    }

    /// <summary>
 /// The part set of one grid with a debounce: a read is applied only after it read the same on
 /// <see cref="S3Defaults.PartStablePolls"/> consecutive polls (SE rebuilds parts in a parallel dirty-region update;
 /// a read in the middle of one would send a remove/add flicker).
 /// </summary>
    internal sealed class PartSetTracker
    {
        private ulong _lastSig;
        private int _stable;
        private bool _hasLast;
        private ulong _heldSig = PartDiff.Signature(new Dictionary<ulong, PartSnapshot>());

        public Dictionary<ulong, PartSnapshot> Held = new Dictionary<ulong, PartSnapshot>();
        public int Polls { get; private set; }
        public int Applied { get; private set; }

        /// <summary>Feeds one read; true when it is stable and differs from <see cref="Held"/> (the caller sends the diff and calls <see cref="Commit"/>).</summary>
        public bool Offer(Dictionary<ulong, PartSnapshot> read, int stablePolls)
        {
            Polls++;
            ulong sig = PartDiff.Signature(read);
            if (_hasLast && sig == _lastSig) _stable++;
            else { _stable = 1; _lastSig = sig; _hasLast = true; }
            if (_stable < stablePolls) return false;
            return sig != _heldSig;
        }

        /// <summary>True while the last reads have not settled (the caller polls again next tick).</summary>
        public bool Unsettled(int stablePolls) { return _hasLast && _stable < stablePolls; }

        public void Commit(Dictionary<ulong, PartSnapshot> read)
        {
            Held = new Dictionary<ulong, PartSnapshot>(read);
            _heldSig = PartDiff.Signature(Held);
            Applied++;
        }

        /// <summary>Subnautica dropped everything (resend): nothing is held any more.</summary>
        public void ForgetHeld()
        {
            Held = new Dictionary<ulong, PartSnapshot>();
            _heldSig = PartDiff.Signature(Held);
            _hasLast = false;
            _stable = 0;
        }
    }

    /// <summary>FNV-1a 64 (CONTRACT §6 constants), incremental.</summary>
    internal static class Fnv
    {
        public const ulong Offset = 0xcbf29ce484222325UL, Prime = 0x100000001b3UL;

        public static ulong Add(ulong h, byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++) h = (h ^ data[offset + i]) * Prime;
            return h;
        }

        public static ulong AddU64(ulong h, ulong v)
        {
            for (int i = 0; i < 8; i++) { h = (h ^ (byte)v) * Prime; v >>= 8; }
            return h;
        }

        public static unsafe ulong AddF32(ulong h, float f)
        {
            uint u = *(uint*)&f;
            for (int i = 0; i < 4; i++) { h = (h ^ (byte)u) * Prime; u >>= 8; }
            return h;
        }

        public static ulong Of(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            return Add(Offset, b, 0, b.Length);
        }
    }

    /// <summary>The keys of models and materials (C3), as Wire's ModelCodec defines them. The consumer treats both as opaque ids.</summary>
    internal static class S3Keys
    {
        /// <summary>Lower-case, forward slashes to backslashes, trimmed (SE's asset names are Content-relative or absolute).</summary>
        public static string NormalisePath(string path)
        {
            return ModelCodec.NormalizeModelPath(path ?? "");
        }

        /// <summary>
 /// modelKey = FNV-1a 64 of the UTF-8 text <c>&lt;normalised path&gt;|lod&lt;n&gt;</c> (CONTRACT §5.4 "of the lower-case
 /// normalised path and the LOD"), plus <c>|scale=&lt;R&gt;</c> when the part's model is rescaled (≠ 1), because
 /// PartAdd has no scale field and the vertices then carry the scale.
 /// </summary>
        public static ulong ModelKey(string assetPath, int lod, float scale)
        {
            return ModelCodec.ModelKey(assetPath ?? "", (uint)lod, scale);
        }

        public static string ModelKeyText(string assetPath, int lod, float scale)
        {
            return ModelCodec.ModelKeyText(assetPath ?? "", (uint)lod, scale);
        }

        /// <summary>
 /// materialKey = FNV-1a 64 of <c>cm|ng|add|alpha|technique|flags</c> (normalised raw texture names): SE's exporter
 /// identifies a material by its texture set (MyExportModel.Material.EqualsMaterialWise); colour is per part.
 /// </summary>
        public static ulong MaterialKey(string cm, string ng, string add, string alpha, uint technique, uint flags)
        {
            return ModelCodec.MaterialKey(technique, flags, cm, ng, add, alpha);
        }

        public static string MaterialKeyText(string cm, string ng, string add, string alpha, uint technique, uint flags)
        {
            return ModelCodec.MaterialKeyText(technique, flags, cm, ng, add, alpha);
        }
    }

    // ===================================================================================================== ring pump

 /// <summary>One framed byte-ring message to write (payload bytes only; the ring adds the 8-byte header).</summary>
    internal struct OutMessage
    {
        public uint Type;
        public byte[] Payload;
        /// <summary>Messages of one model share a group (≠ 0): a message that can never be written drops the rest of its group.</summary>
        public int Group;
        public string Label;
        /// <summary>S5: the entity the message belongs to (0 = none), so its queued messages can be purged before an EntityRemove.</summary>
        public long Entity;

        public OutMessage(uint type, byte[] payload, int group, string label)
        {
            Type = type; Payload = payload; Group = group; Label = label; Entity = 0;
        }
    }

    /// <summary>A producer end of a byte ring (the link's C3/C4 rings, or a test sink).</summary>
    internal interface IMessageSink
    {
        RingWriteStatus TryWrite(uint type, byte[] payload, int count);
    }

    internal struct PumpBudget
    {
        public double MaxMs;
        public int MaxMessages;
        public long MaxBytes;

        public PumpBudget(double maxMs, int maxMessages, long maxBytes)
        {
            MaxMs = maxMs; MaxMessages = maxMessages; MaxBytes = maxBytes;
        }
    }

    internal enum PumpStop
    {
        /// <summary>Everything queued was written.</summary>
        Empty = 0,
        /// <summary>The ring had no room: the message stays at the head of the queue and is retried next tick (back-pressure).</summary>
        Full = 1,
        /// <summary>The tick's time, message or byte budget was used up; the rest goes next tick.</summary>
        Budget = 2,
        /// <summary>The ring reported impossible counters (a peer broke the protocol); nothing written until a re-attach.</summary>
        Corrupt = 3,
        /// <summary>No sink (not linked); everything stays queued.</summary>
        Held = 4,
    }

    internal struct PumpResult
    {
        public int Written;
        public long Bytes;
        public int Dropped;
        public PumpStop Stop;
        public double Ms;
    }

    /// <summary>
 /// FIFO of messages for one ring with a per-tick budget (ROADMAP S3: "a few models or about 2 ms; back-pressure when
 /// the ring is full, resumed next tick; never block"). Each message is tried once per tick (CONTRACT §5.1: "producer
 /// tries once; no room → stays queued"); order is preserved; the first message of a tick is always attempted, so
 /// progress never depends on the budget. Update thread only.
 /// </summary>
    internal sealed class OutboundQueue
    {
        private readonly LinkedList<OutMessage> _q = new LinkedList<OutMessage>();
        private readonly string _name;

        public OutboundQueue(string name) { _name = name; }

        public string Name { get { return _name; } }
        public int Count { get { return _q.Count; } }
        public long PendingBytes { get; private set; }
        public long TotalWritten { get; private set; }
        public long TotalBytes { get; private set; }
        public long FullEvents { get; private set; }
        public long BudgetStops { get; private set; }
        public long DroppedTooLarge { get; private set; }
        /// <summary>Messages ever queued (monotonic): the n-th queued message has left the queue once <see cref="Done"/> ≥ n.</summary>
        public long Enqueued { get; private set; }
        /// <summary>Messages ever removed: written, dropped or cleared (monotonic; FIFO, so removal order = queue order).</summary>
        public long Done { get; private set; }

        public void Enqueue(OutMessage m)
        {
            _q.AddLast(m);
            PendingBytes += m.Payload != null ? m.Payload.Length : 0;
            Enqueued++;
        }

        public void EnqueueRange(IList<OutMessage> ms)
        {
            for (int i = 0; i < ms.Count; i++) Enqueue(ms[i]);
        }

        public void Clear()
        {
            Done += _q.Count;
            _q.Clear();
            PendingBytes = 0;
        }

        /// <summary>Writes from the head while the budget lasts. <paramref name="clock"/> returns Stopwatch ticks.</summary>
        public PumpResult Pump(IMessageSink sink, PumpBudget budget, Func<long> clock)
        {
            var r = new PumpResult { Stop = PumpStop.Empty };
            if (sink == null) { r.Stop = _q.Count > 0 ? PumpStop.Held : PumpStop.Empty; return r; }
            long t0 = clock();
            long limit = (long)(budget.MaxMs * Stopwatch.Frequency / 1000.0);
            while (_q.Count > 0)
            {
                if (r.Written > 0 &&
                    (r.Written >= budget.MaxMessages || (budget.MaxBytes > 0 && r.Bytes >= budget.MaxBytes) || clock() - t0 >= limit))
                {
                    r.Stop = PumpStop.Budget;
                    BudgetStops++;
                    break;
                }
                OutMessage m = _q.First.Value;
                int n = m.Payload != null ? m.Payload.Length : 0;
                RingWriteStatus st = sink.TryWrite(m.Type, m.Payload, n);
                if (st == RingWriteStatus.Ok)
                {
                    _q.RemoveFirst();
                    Done++;
                    PendingBytes -= n;
                    r.Written++;
                    r.Bytes += n;
                    TotalWritten++;
                    TotalBytes += n;
                    continue;
                }
                if (st == RingWriteStatus.Full) { r.Stop = PumpStop.Full; FullEvents++; break; }
                if (st == RingWriteStatus.Corrupt) { r.Stop = PumpStop.Corrupt; break; }
                // TooLarge or InvalidType: this message can never be written; drop it and the rest of its group
                int group = m.Group;
                _q.RemoveFirst();
                Done++;
                PendingBytes -= n;
                r.Dropped++;
                DroppedTooLarge++;
                while (group != 0 && _q.Count > 0 && _q.First.Value.Group == group)
                {
                    PendingBytes -= _q.First.Value.Payload != null ? _q.First.Value.Payload.Length : 0;
                    _q.RemoveFirst();
                    Done++;
                    r.Dropped++;
                    DroppedTooLarge++;
                }
            }
            r.Ms = (clock() - t0) * 1000.0 / Stopwatch.Frequency;
            return r;
        }
    }

    // ===================================================================================================== ack gate

 /// <summary>
 /// When the producer may stream and when it must resend everything (CONTRACT §6 "Hello", "Link epoch", "Restart
 /// detection"): SE streams C3/C4 only after Subnautica acknowledged the hello; after the first hello, an attach, a new
 /// Subnautica generation, a link epoch bump or a new world, the C7 ack is trusted only once C7's snFrame advanced
 /// <see cref="S3Defaults.AckTrustFrames"/> frames and both helloAckEpoch and linkAckEpoch equal SE's values; the
 /// stream then (re)starts with ClearAll and everything is sent again (the caller takes <see cref="TakeResend"/>).
 /// A link-epoch recovery always resends (the contract allows "continue" for a short stall with equal counters; a
 /// resend is always correct after ClearAll and costs one block in S3). Game-independent.
 /// </summary>
    internal sealed class StreamGate
    {
        private bool _known;
        private long _attaches, _newPeers;
        private uint _linkEpoch, _worldEpoch, _helloEpoch;
        private ulong _trustFromFrame;
        private bool _waitingTrust;
        private string _pendingReason;
        private string _resendReason;

        /// <summary>True while the stream runs; false while waiting for an ack.</summary>
        public bool Streaming { get; private set; }
        public long Starts { get; private set; }
        /// <summary>Why the gate is waiting (logs).</summary>
        public string Waiting { get; private set; }

        public void Reset()
        {
            _known = false;
            Streaming = false;
            _waitingTrust = false;
            _pendingReason = null;
            _resendReason = null;
            Waiting = "not linked or no hello";
        }

        /// <summary>One tick. <paramref name="linked"/>: link up with a C7 value; the identity counters come from the link.</summary>
        public void Tick(bool linked, long attaches, long newPeers, uint linkEpoch, uint worldEpoch, uint helloEpoch,
                         uint helloAckEpoch, uint linkAckEpoch, ulong snFrame)
        {
            if (!linked || helloEpoch == 0)
            {
                if (Streaming || _known) Reset();
                Waiting = !linked ? "link down" : "no hello published";
                return;
            }
            string why = null;
            if (!_known) why = "first hello " + helloEpoch;
            else if (attaches != _attaches) why = "SE re-attached to the section";
            else if (newPeers != _newPeers) why = "new Subnautica generation";
            else if (linkEpoch != _linkEpoch) why = "link epoch " + linkEpoch;
            else if (worldEpoch != _worldEpoch) why = "world epoch " + worldEpoch;
            else if (helloEpoch != _helloEpoch) why = "hello epoch " + helloEpoch;
            if (why != null)
            {
                _known = true;
                _attaches = attaches; _newPeers = newPeers; _linkEpoch = linkEpoch; _worldEpoch = worldEpoch; _helloEpoch = helloEpoch;
                Streaming = false;
                _waitingTrust = true;
                _trustFromFrame = snFrame + S3Defaults.AckTrustFrames;
                _pendingReason = why;
            }
            if (Streaming) return;
            if (_waitingTrust && snFrame < _trustFromFrame) { Waiting = "C7 snFrame " + snFrame + " < " + _trustFromFrame; return; }
            _waitingTrust = false;
            if (helloAckEpoch != helloEpoch) { Waiting = "helloAckEpoch " + helloAckEpoch + " != " + helloEpoch; return; }
            if (linkAckEpoch != linkEpoch) { Waiting = "linkAckEpoch " + linkAckEpoch + " != " + linkEpoch; return; }
            Streaming = true;
            Waiting = null;
            Starts++;
            _resendReason = _pendingReason ?? "restart";
            _pendingReason = null;
        }

        /// <summary>The reason of the pending (re)start, once (the caller sends ClearAll and everything); null otherwise.</summary>
        public string TakeResend()
        {
            if (!Streaming) return null;
            string r = _resendReason;
            _resendReason = null;
            return r;
        }
    }

    // ===================================================================================================== consumer audit

 /// <summary>
 /// SE's check of Subnautica's C7 counters once a stream is complete: a corrupt-framing re-sync on the Subnautica side drops records for good unless SE
 /// resends, and none of the <see cref="StreamGate"/> triggers (attach, peer, epochs) fires for it. When the stream is
 /// idle (nothing queued or held, parts settled), both rings are drained (consumer tails at the producer heads) and a
 /// C7 at least <see cref="FreshFrames"/> Subnautica frames newer than the drained rings is read, the counters must
 /// match what SE sent: <c>c3BytesDrained</c> grows exactly as SE's written C3 bytes (payload + 8-B header per
 /// message) from the baseline taken at the first such moment of the stream, <c>mirroredEntityCount</c> equals the
 /// grids SE sent, <c>pendingParts</c> is at most the parts whose model SE could not load (Subnautica also counts 1 while
 /// it knows records were lost). A mismatch that lasts <see cref="MismatchMs"/> returns the reason (the caller restarts
 /// the stream: ClearAll and everything), at most once per <see cref="BackoffMs"/>. Game-independent.
 /// </summary>
    internal sealed class ConsumerAudit
    {
        public const ulong MismatchMs = 2000;
        public const ulong BackoffMs = 10000;
        public const ulong FreshFrames = 2;

        private bool _armed, _quiet;
        private long _baseDelta;
        private ulong _quietFrame, _mismatchSinceMs, _lastResendMs;

        public long Resends { get; private set; }
        public long Checks { get; private set; }
        public string LastReason { get; private set; }
        /// <summary>The mismatch seen now (null when the counters agree or nothing is checked).</summary>
        public string Pending { get; private set; }

        /// <summary>A stream (re)start: the next idle moment takes a new baseline.</summary>
        public void Reset()
        {
            _armed = false;
            _quiet = false;
            _mismatchSinceMs = 0;
            Pending = null;
        }

        /// <summary>
 /// One tick. <paramref name="idle"/>: streaming, the (re)send complete, SE's C3/C4 queues empty, C3 not held, the
 /// parts settled; <paramref name="drained"/>: both consumer tails at the producer heads; <paramref name="seC3Bytes"/>:
 /// Σ (payload + 8) of the C3 messages SE wrote; <paramref name="seGrids"/>: grids whose GridAdd SE wrote;
 /// <paramref name="expectPending"/>: parts whose model SE could not load. Returns the resend reason, or null.
 /// </summary>
        public string Tick(ulong nowMs, bool idle, bool drained, long seC3Bytes, uint seGrids, uint expectPending,
                           ulong snFrame, ulong c3BytesDrained, uint mirroredEntityCount, uint pendingParts)
        {
            if (!idle || !drained)
            {
                _quiet = false;
                _mismatchSinceMs = 0;
                Pending = null;
                return null;
            }
            if (!_quiet)
            {
                _quiet = true;
                _quietFrame = snFrame;
                return null;
            }
            if (snFrame < _quietFrame + FreshFrames) return null; // C7 not yet newer than the drained rings
            Checks++;
            long delta = seC3Bytes - (long)c3BytesDrained;
            if (!_armed)
            {
                _armed = true;
                _baseDelta = delta;
            }
            string why = null;
            if (delta > _baseDelta)
                why = "C3: Subnautica drained " + (delta - _baseDelta) + " B fewer than SE wrote since the stream completed (c3BytesDrained " + c3BytesDrained + ")";
            else if (mirroredEntityCount != seGrids)
                why = "C7 mirroredEntityCount " + mirroredEntityCount + " != the " + seGrids + " grid(s) SE sent";
            else if (pendingParts > expectPending)
                why = "C7 pendingParts " + pendingParts + " > " + expectPending + " (parts whose model SE could not load) with both rings drained";
            Pending = why;
            if (why == null)
            {
                _mismatchSinceMs = 0;
                if (delta < _baseDelta) _baseDelta = delta; // Subnautica counted more (an earlier resend drained late): new baseline
                return null;
            }
            if (_mismatchSinceMs == 0) { _mismatchSinceMs = nowMs == 0 ? 1 : nowMs; return null; }
            if (nowMs - _mismatchSinceMs < MismatchMs) return null;
            if (_lastResendMs != 0 && nowMs - _lastResendMs < BackoffMs) return null;
            _lastResendMs = nowMs == 0 ? 1 : nowMs;
            Resends++;
            LastReason = why;
            Reset();
            return why;
        }
    }

    // ===================================================================================================== placement

 /// <summary>The lab grid placement (ROADMAP S3 "SE plugin (S3b)").</summary>
    internal static class S3Placement
    {
        /// <summary>
 /// SE world matrix of the grid whose block (0, 0, 0) is centred on SN point (x, h, z): T = F⁻¹(x, h, z) (the block
 /// centre at the seabed height, so the lower half is buried); Up = mapdir⁻¹(SN +Y); Forward = mapdir⁻¹ of the SN
 /// heading <paramref name="yawDeg"/> (0 = north +Z, 90 = east +X), both at T; Right = Forward × Up. The entity
 /// rotation q is then the yaw alone at T.
 /// </summary>
        public static SeMatrix GridMatrix(SiteMap site, double x, double h, double z, double yawDeg)
        {
            return S1Math.SpectatorAt(site, new Vec3(x, h, z), S1Math.HeadingSn(yawDeg), Vec3.UnitY);
        }

        /// <summary>
 /// Block cells: "single" → (0, 0, 0); "3x3" or a floor of rows × cols → (i, 0, k) around the origin; a wall
 /// (orientation "wall") → (i, j, 0) with i around 0 along the grid's Right and j = 0 … rows − 1 upward, so the requested
 /// point is the centre of the bottom-middle block and the wall faces the grid's Forward (the heading).
 /// </summary>
        public static List<int[]> Cells(string plate, int rows = 3, int cols = 3)
        {
            var cells = new List<int[]>();
            string p = (plate ?? "single").ToLowerInvariant();
            if (p == "single") { cells.Add(new[] { 0, 0, 0 }); return cells; }
            int r = Math.Max(1, rows), c = Math.Max(1, cols);
            int i0 = -(c - 1) / 2;
            if (p == "wall")
            {
                for (int j = 0; j < r; j++)
                    for (int i = 0; i < c; i++)
                        cells.Add(new[] { i0 + i, j, 0 });
                return cells;
            }
            int k0 = -(r - 1) / 2;
            for (int k = 0; k < r; k++)
                for (int i = 0; i < c; i++)
                    cells.Add(new[] { i0 + i, 0, k0 + k });
            return cells;
        }

        // ------------------------------------------------------------------------------------------------- the S7 room

 /// <summary>The outer edge of the S7 room fixture in blocks (a hollow shell around a 3 × 3 × 3 interior).</summary>
        public const int RoomEdge = 5;

        /// <summary>
 /// The S7 room fixture: a hollow light-armor
 /// shell of 5 × 5 × 5 large blocks around a 3 × 3 × 3 interior, every block 1 × 1 × 1 (definition ids VERIFIED in
 /// the player's <c>Content\Data\CubeBlocks\*.sbc</c>, read-only: <c>CubeBlocks_Armor.sbc</c>, <c>_Windows.sbc</c>,
 /// <c>_Energy.sbc</c>, <c>_Logistics.sbc</c>, <c>_Doors.sbc</c>, <c>_Lights.sbc</c>; none carries a DLC tag). SN
 /// offsets (i east, j up, k north) in blocks from block (0, 0, 0), the requested point; the grid-local cell is
 /// (i, j, −k) because the grid's Forward is the heading (yaw 0 = SN north = grid-local −Z):
 /// <list type="bullet">
 /// <item>shell: <c>CubeBlock/LargeBlockArmorBlock</c> (IsAirTight true), except</item>
 /// <item>(2, 2, 4) the window: <c>CubeBlock/WindowRound</c> (IsAirTight true; glass on its Front and Back, the porthole
 /// looks north);</item>
 /// <item>(1, 0, 1) a battery in the floor: <c>BatteryBlock/LargeBlockBatteryBlock</c>
 /// </item>
 /// <item>(1, 1, 3) the Air Vent <c>AirVent/</c> (empty subtype; its Back against the west wall): without a gas block
 /// SE builds no airtight room at all (<c>MyGridGasSystem.ShouldPressurize</c>; <c>MyAirVent.CanPressurizeRoom</c> is
 /// true, powered or not);</item>
 /// <item>(3, 1, 3) a door <c>Door/</c> (empty subtype) inside the room, in the north-east floor corner: toggled by
 /// <c>de.interior {action: door}</c> it makes the gas system recompute without changing the room;</item>
 /// <item>(3, 3, 2) the Interior Light <c>InteriorLight/SmallLight</c>, its Back on the cell's west face, facing the
 /// east inner wall about 2.5 m away (default radius 3.6 m: a light 3.75 m away, one cell further west, would not
 /// reach the wall);</item>
 /// <item>(1, 3, 1), (1, 3, 3), (2, 3, 1) three spotlights <c>ReflectorLight/LargeBlockFrontLight</c> on the ceiling
 /// shining down on the west floor
 /// with the character light up to four shadowed spot rows, S7.M18's subject.</item>
 /// </list>
 /// The camera points of the launcher (interior centre (2, 2, 2), the window wall, 2 m outside the south wall) stay
 /// free. Orientation names are <c>Base6Directions.Direction</c> names (Forward = −Z, Right = +X, Up = +Y).
 /// </summary>
        public static List<RoomBlock> RoomLayout()
        {
            var l = new List<RoomBlock>();
            var special = new Dictionary<string, RoomBlock>(StringComparer.Ordinal)
            {
                { "2,2,4", new RoomBlock("window", "MyObjectBuilder_CubeBlock/WindowRound", "Forward", "Up") },
                { "1,0,1", new RoomBlock("battery", "MyObjectBuilder_BatteryBlock/LargeBlockBatteryBlock", "Forward", "Up") },
                { "1,1,3", new RoomBlock("vent", "MyObjectBuilder_AirVent/", "Right", "Up") },
                { "3,1,3", new RoomBlock("door", "MyObjectBuilder_Door/", "Forward", "Up") },
                { "3,3,2", new RoomBlock("light", "MyObjectBuilder_InteriorLight/SmallLight", "Right", "Up") },
                { "1,3,1", new RoomBlock("spot", "MyObjectBuilder_ReflectorLight/LargeBlockFrontLight", "Down", "Forward") },
                { "1,3,3", new RoomBlock("spot", "MyObjectBuilder_ReflectorLight/LargeBlockFrontLight", "Down", "Forward") },
                { "2,3,1", new RoomBlock("spot", "MyObjectBuilder_ReflectorLight/LargeBlockFrontLight", "Down", "Forward") },
            };
            int e = RoomEdge - 1;
            for (int i = 0; i <= e; i++)
                for (int j = 0; j <= e; j++)
                    for (int k = 0; k <= e; k++)
                    {
                        string key = i + "," + j + "," + k;
                        bool shell = i == 0 || j == 0 || k == 0 || i == e || j == e || k == e;
                        RoomBlock b;
                        if (special.TryGetValue(key, out b)) b = b.At(i, j, k);
                        else if (shell) b = new RoomBlock("shell", "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock", "Forward", "Up").At(i, j, k);
                        else continue;
                        l.Add(b);
                    }
            return l;
        }

        /// <summary>The grid-local cells of <see cref="RoomLayout"/> (bounds and the reply).</summary>
        public static List<int[]> RoomCells()
        {
            var cells = new List<int[]>();
            foreach (RoomBlock b in RoomLayout()) cells.Add(b.Cell);
            return cells;
        }

        /// <summary>The grid-local box of cube cells (cell centres at cell × gridSize): min, max.</summary>
        public static void CellBounds(List<int[]> cells, double gridSize, out Vec3 min, out Vec3 max)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (int[] c in cells)
            {
                x0 = Math.Min(x0, c[0]); y0 = Math.Min(y0, c[1]); z0 = Math.Min(z0, c[2]);
                x1 = Math.Max(x1, c[0]); y1 = Math.Max(y1, c[1]); z1 = Math.Max(z1, c[2]);
            }
            double h = gridSize / 2;
            min = new Vec3(x0 * gridSize - h, y0 * gridSize - h, z0 * gridSize - h);
            max = new Vec3(x1 * gridSize + h, y1 * gridSize + h, z1 * gridSize + h);
        }

        /// <summary>SE-local corner i of a box: bit 0 → max x, bit 1 → max y, bit 2 → max z (MeshConventions.AabbCorner).</summary>
        public static Vec3 Corner(Vec3 min, Vec3 max, int i)
        {
            return new Vec3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
        }
    }

    /// <summary>One block of the S7 room fixture (<see cref="S3Placement.RoomLayout"/>): role, definition id, orientation, cells.</summary>
    internal sealed class RoomBlock
    {
        public readonly string Role, DefinitionId, Forward, Up;
        /// <summary>SN offsets in blocks (i east, j up, k north) and the grid-local cell (i, j, −k).</summary>
        public int[] Sn, Cell;

        public RoomBlock(string role, string definitionId, string forward, string up)
        {
            Role = role; DefinitionId = definitionId; Forward = forward; Up = up;
        }

        public RoomBlock At(int i, int j, int k)
        {
            return new RoomBlock(Role, DefinitionId, Forward, Up) { Sn = new[] { i, j, k }, Cell = new[] { i, j, -k } };
        }
    }

    /// <summary>
 /// <c>de.motion {kind: "yaw", amplitudeDeg, hz, seconds}</c> (S3.N2, ROADMAP "a scripted 0.5 Hz ±30° yaw sine"): the
 /// spectator turns about SN up at its position, yaw(k) = amplitude·sin(2π·hz·k/60) for SE update k = 0 … ticks, then
 /// back to the start heading (the last sample is exactly 0). Game-independent.
 /// </summary>
    internal sealed class YawPlan
    {
        public double AmplitudeDeg = 30, Hz = 0.5, Seconds = 10;
        public string Label = "yaw";

        public int Ticks { get { return (int)Math.Round(Seconds * 60.0); } }

        public double AngleAt(int k)
        {
            if (k >= Ticks) return 0.0;
            return AmplitudeDeg * Math.Sin(2.0 * Math.PI * Hz * k / 60.0);
        }

        public static YawPlan Parse(IDictionary<string, object> args, out string why)
        {
            why = null;
            var p = new YawPlan();
            p.AmplitudeDeg = MiniJson.GetNumber(args, "amplitudeDeg") ?? p.AmplitudeDeg;
            p.Hz = MiniJson.GetNumber(args, "hz") ?? p.Hz;
            p.Seconds = MiniJson.GetNumber(args, "seconds") ?? p.Seconds;
            p.Label = MiniJson.GetString(args, "label") ?? p.Label;
            if (!(Math.Abs(p.AmplitudeDeg) <= 90) || !(p.Hz > 0 && p.Hz <= 10) || !(p.Seconds > 0 && p.Seconds <= 600))
            {
                why = "de.motion yaw needs amplitudeDeg in [-90, 90], hz in (0, 10], seconds in (0, 600]";
                return null;
            }
            return p;
        }
    }

    // ===================================================================================================== de.s3 args

 /// <summary>A parsed <c>de.s3</c> placement request.</summary>
    internal sealed class S3PlaceRequest
    {
        public double X = S3Defaults.LabX, Z = S3Defaults.LabZ;
        /// <summary>The block centre height (SN y): the seabed. NaN while <see cref="Raycast"/> waits for Subnautica.</summary>
        public double H = double.NaN;
        public bool Raycast;
        public bool HFromLabConfig;
        /// <summary>The centre came as one SN point (<c>place.sn</c>): h is its y.</summary>
        public bool HFromPoint;
        public string Block = S3Defaults.Block;
        /// <summary>"single", "3x3" (a floor), "floor", "wall" (with <see cref="Rows"/> × <see cref="Cols"/>) or "room".</summary>
        public string Plate = "single";
        public int Rows = 3, Cols = 3;
        public double YawDeg;
        /// <summary>SE ColorMaskHSV of the blocks (hue 0…1, saturation and value offsets −1…1); null = SE's default.</summary>
        public double[] ColorHsv;
        /// <summary>Add the GPS marker at the block centre (S3.N2).</summary>
        public bool Marker = true;
        /// <summary>Ask SE's renderer for the coloured texture of the parts' material once the models are loaded (S3.C13).</summary>
        public bool Export;
        public string Label;
        /// <summary>The y of a three-number <c>place</c> list (used for h only with <c>useY: true</c>).</summary>
        public double PlaceY = double.NaN;
        /// <summary>Why h = "raycast" fell back to the lab config (null when it did not).</summary>
        public string RaycastFailure;

        public List<int[]> Cells() { return Plate == "room" ? S3Placement.RoomCells() : S3Placement.Cells(Plate, Rows, Cols); }

        public string HSource
        {
            get
            {
                if (Raycast) return "raycast (Subnautica)";
                if (HFromLabConfig)
                    return "lab config (HYPOTHESIS " + S3Defaults.LabSeabedY.ToString(CultureInfo.InvariantCulture) + ")" +
                           (RaycastFailure != null ? "; raycast unavailable: " + RaycastFailure : "");
                return HFromPoint ? "argument (place.sn)" : "argument";
            }
        }

        /// <summary>h = "raycast" could not be answered: use the lab config value (ROADMAP S3: "h from a Subnautica raycast … or from the lab config").</summary>
        public void FallBackToLabConfig(string why)
        {
            Raycast = false;
            HFromLabConfig = true;
            H = S3Defaults.LabSeabedY;
            RaycastFailure = why;
        }
    }

    /// <summary>
 /// The <c>de.s3</c> command syntax (C11 args and the <c>/de s3</c> chat verb). One action per command:
 /// <list type="bullet">
 /// <item>place: <c>{place: {sn: [x, y, z], yawDeg, label, plate: "3x3" | {rows, cols, orientation: "wall" | "floor"},
 /// colorHsv: [h, s, v], export: true, block, marker}}</c> (the launcher's form: the block centre is exactly sn), or the
 /// flat form <c>{place: [x, y, z] | [x, z], h: number | "raycast", plate, yawDeg, color: [h, s, v], marker, export,
 /// block}</c>; x, z default to the grandReef lab spot; h absent → the lab config value
 /// <see cref="S3Defaults.LabSeabedY"/>; "raycast" asks Subnautica (<c>de.s3 {action: "probe", x, z}</c> over the chat
 /// relay). A place object wins over the other actions' keys;</item>
 /// <item><c>{status: true}</c> (or <c>state</c>), <c>{remove: true}</c>, <c>{export: true}</c> (SE's colour export of
 /// the mirrored parts' materials, S3.C13), <c>{trace: seconds}</c> (s3.pose and s3.gps rows every tick for that long),
 /// <c>{gps: "on" | "off"}</c> (s3.gps rows every frame until off, S3.N2), <c>{pixel: {corner: 0…7} | {sn: [x, y, z]},
 /// label}</c> (SE's pixel of a corner of the placed block or of an SN point, S3.N3).</item>
 /// </list>
 /// Chat: <c>/de s3 [place] [x z | x y z] [h=-153|raycast] [plate=3x3|wall] [yaw=0] [color=h,s,v] | status | remove |
 /// export | trace [seconds] | gps on|off | pixel &lt;corner&gt;</c>; key=value tokens anywhere (PowerShell-safe).
 /// </summary>
    internal static class S3Args
    {
        public const string ChatUsage = "/de s3 mirror [near|all|<entityId>] | /de s3 [place] [x z | x y z] [h=<y>|raycast] [plate=3x3|wall] [yaw=<deg>] [color=h,s,v] [export=true] [block=<subtype>] | status | remove | export | trace [s] | gps on|off | pixel <corner 0-7>";

        public static string ActionOf(IDictionary<string, object> args)
        {
            if (args == null) return "place";
            if (args.ContainsKey("place")) return "place";
            if (args.ContainsKey("mirror")) return "mirror";
            if (MiniJson.GetBool(args, "status") == true || MiniJson.GetBool(args, "state") == true) return "status";
            if (MiniJson.GetBool(args, "remove") == true) return "remove";
            if (args.ContainsKey("trace")) return "trace";
            if (args.ContainsKey("gps")) return "gps";
            if (args.ContainsKey("pixel")) return "pixel";
            if (MiniJson.GetBool(args, "export") == true) return "export";
            return "place";
        }

        /// <summary>The placement request of C11 args; false with <paramref name="why"/> on bad input.</summary>
        public static bool TryParsePlace(IDictionary<string, object> args, out S3PlaceRequest req, out string why)
        {
            req = new S3PlaceRequest();
            why = null;
            if (args == null) { req.HFromLabConfig = true; req.H = S3Defaults.LabSeabedY; return true; }
            object v;
            IDictionary<string, object> obj = null;
            bool hSet = false;
            if (args.TryGetValue("place", out v) && v != null)
            {
                obj = v as IDictionary<string, object>;
                if (obj != null)
                {
                    object sn;
                    if (obj.TryGetValue("sn", out sn) && sn != null)
                    {
                        double[] p = Numbers(sn);
                        if (p == null || p.Length != 3) { why = "place.sn must be [x, y, z] (Subnautica metres: the block centre)"; return false; }
                        req.X = p[0]; req.H = p[1]; req.Z = p[2];
                        req.HFromPoint = true;
                        hSet = true;
                    }
                }
                else
                {
                    double[] p = Numbers(v);
                    if (p == null || (p.Length != 2 && p.Length != 3)) { why = "place must be [x, y, z], [x, z] or {sn: [x, y, z], ...} (Subnautica metres)"; return false; }
                    req.X = p[0];
                    req.Z = p[p.Length - 1];
                    if (p.Length == 3) req.PlaceY = p[1];
                }
            }
            // options: inside the place object or at the top level (the object wins)
            Func<string, object> opt = key =>
            {
                object o;
                if (obj != null && obj.TryGetValue(key, out o) && o != null) return o;
                if (args.TryGetValue(key, out o) && o != null) return o;
                return null;
            };
            if (obj == null)
            {
                double? x = MiniJson.GetNumber(args, "x"), z = MiniJson.GetNumber(args, "z");
                if (x.HasValue) req.X = x.Value;
                if (z.HasValue) req.Z = z.Value;
            }
            object hv = opt("h");
            if (!hSet && hv != null)
            {
                string s = hv as string;
                if (s != null && s.Trim().Equals("raycast", StringComparison.OrdinalIgnoreCase)) req.Raycast = true;
                else
                {
                    double h;
                    if (hv is double) h = (double)hv;
                    else if (s == null || !Num(s, out h)) { why = "h must be a number (SN metres) or \"raycast\""; return false; }
                    req.H = h;
                }
                hSet = true;
            }
            if (!hSet)
            {
                if (!double.IsNaN(req.PlaceY) && MiniJson.GetBool(args, "useY") == true) req.H = req.PlaceY;
                else { req.HFromLabConfig = true; req.H = S3Defaults.LabSeabedY; }
            }

            object bv = opt("block");
            if (bv is string && ((string)bv).Trim().Length > 0) req.Block = ((string)bv).Trim();
            object pv = opt("plate");
            if (pv != null)
            {
                var po = pv as IDictionary<string, object>;
                if (po != null)
                {
                    double? rows = MiniJson.GetNumber(po, "rows"), cols = MiniJson.GetNumber(po, "cols");
                    string orient = (MiniJson.GetString(po, "orientation") ?? "floor").ToLowerInvariant();
                    if (orient == "room")
                    {
                        // review F1: the S7 room fixture (its size is fixed: 5 x 5 x 5 around a 3 x 3 x 3 interior)
                        req.Plate = "room";
                        req.Rows = req.Cols = S3Placement.RoomEdge;
                    }
                    else
                    {
                        if (orient != "wall" && orient != "floor") { why = "plate.orientation must be \"wall\", \"floor\" or \"room\""; return false; }
                        req.Rows = (int)(rows ?? 3); req.Cols = (int)(cols ?? 3);
                        // S11 hook: up to 16, so a 1 x 14 large wall (35 m) crosses the x2.5 sea dragon's 31 m grab class
                        if (req.Rows < 1 || req.Rows > 16 || req.Cols < 1 || req.Cols > 16) { why = "plate rows and cols must be 1..16"; return false; }
                        req.Plate = orient;
                    }
                }
                else
                {
                    string plate = (pv as string ?? Convert.ToString(pv, CultureInfo.InvariantCulture)).Trim().ToLowerInvariant();
                    if (plate == "3x3") { req.Plate = "floor"; req.Rows = req.Cols = 3; }
                    else if (plate == "wall") { req.Plate = "wall"; req.Rows = req.Cols = 3; }
                    else if (plate == "single") req.Plate = "single";
                    else if (plate == "room") { req.Plate = "room"; req.Rows = req.Cols = S3Placement.RoomEdge; }
                    else { why = "plate must be \"3x3\", \"wall\", \"single\", \"room\" or {rows, cols, orientation}"; return false; }
                }
            }
            double? yaw = Number(opt("yawDeg")) ?? Number(opt("yaw")) ?? Number(opt("headingDeg"));
            if (yaw.HasValue) req.YawDeg = yaw.Value;
            object cv = opt("colorHsv") ?? opt("color");
            if (cv != null)
            {
                double[] c = Numbers(cv);
                if (c == null || c.Length != 3 || c[0] < 0 || c[0] > 1 || c[1] < -1 || c[1] > 1 || c[2] < -1 || c[2] > 1)
                { why = "color must be [h, s, v] in SE's ColorMaskHSV convention (h 0..1, s and v -1..1)"; return false; }
                req.ColorHsv = c;
            }
            bool? marker = Bool(opt("marker"));
            if (!marker.HasValue && obj != null) marker = MiniJson.GetBool(obj, "gps");
            if (marker.HasValue) req.Marker = marker.Value;
            bool? export = Bool(opt("export"));
            if (export.HasValue) req.Export = export.Value;
            object lv = opt("label");
            if (lv != null) req.Label = Convert.ToString(lv, CultureInfo.InvariantCulture);
            foreach (double d in new[] { req.X, req.Z, req.YawDeg })
                if (double.IsNaN(d) || double.IsInfinity(d)) { why = "non-finite number"; return false; }
            if (!req.Raycast && (double.IsNaN(req.H) || double.IsInfinity(req.H))) { why = "non-finite h"; return false; }
            return true;
        }

        /// <summary>argv[0] is "s3"; returns the C11 args, or null with <paramref name="usage"/> set.</summary>
        public static Dictionary<string, object> ParseChat(string[] argv, out string usage)
        {
            usage = null;
            var a = new Dictionary<string, object>(StringComparer.Ordinal);
            var words = new List<string>();
            for (int i = 1; i < argv.Length; i++)
            {
                string t = argv[i];
                int eq = t.IndexOf('=');
                if (eq > 0)
                {
                    string key = t.Substring(0, eq).Trim();
                    string val = t.Substring(eq + 1).Trim();
                    if (key == "yaw") key = "yawDeg";
                    if (key == "place" || key == "color" || key == "colorHsv")
                    {
                        double[] nums = SplitNumbers(val);
                        if (nums == null) { usage = ChatUsage; return null; }
                        var l = new List<object>();
                        foreach (double d in nums) l.Add(d);
                        a[key] = l;
                    }
                    else a[key] = Value(val);
                }
                else words.Add(t);
            }
            int w = 0;
            if (words.Count > 0)
            {
                string w0 = words[0].ToLowerInvariant();
                switch (w0)
                {
                    case "mirror":
                        if (words.Count > 2) { usage = ChatUsage; return null; }
                        a["mirror"] = words.Count == 2 ? words[1] : "near";
                        return a;
                    case "status":
                    case "remove":
                    case "export":
                        if (words.Count > 1) { usage = ChatUsage; return null; }
                        a[w0] = true;
                        return a;
                    case "trace":
                    {
                        double s = 10;
                        if (words.Count > 2 || (words.Count == 2 && !Num(words[1], out s))) { usage = ChatUsage; return null; }
                        a["trace"] = s;
                        return a;
                    }
                    case "gps":
                        if (words.Count != 2 || !(words[1].Equals("on", StringComparison.OrdinalIgnoreCase) || words[1].Equals("off", StringComparison.OrdinalIgnoreCase)))
                        { usage = ChatUsage; return null; }
                        a["gps"] = words[1].ToLowerInvariant();
                        return a;
                    case "pixel":
                    {
                        double c;
                        if (words.Count != 2 || !Num(words[1], out c) || c < 0 || c > 7) { usage = ChatUsage; return null; }
                        a["pixel"] = new Dictionary<string, object>(StringComparer.Ordinal) { { "corner", c } };
                        return a;
                    }
                    case "place":
                        w = 1;
                        break;
                }
            }
            int left = words.Count - w;
            if (left == 2 || left == 3)
            {
                var l = new List<object>();
                for (int i = w; i < words.Count; i++)
                {
                    double d;
                    if (!Num(words[i], out d)) { usage = ChatUsage; return null; }
                    l.Add(d);
                }
                a["place"] = l;
            }
            else if (left != 0) { usage = ChatUsage; return null; }
            return a;
        }

        /// <summary>The <c>gps</c> action's switch: on/true/1 → true, off/false/0 → false, else null.</summary>
        public static bool? OnOff(IDictionary<string, object> args, string key)
        {
            return MiniJson.GetBool(args, key);
        }

        private static double? Number(object v)
        {
            if (v == null) return null;
            if (v is double) return (double)v;
            double d;
            var s = v as string;
            return s != null && Num(s, out d) ? (double?)d : null;
        }

        private static bool? Bool(object v)
        {
            if (v == null) return null;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            var s = v as string;
            if (s == null) return null;
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "on" || s == "1") return true;
            if (s == "false" || s == "off" || s == "0") return false;
            return null;
        }

        private static double[] Numbers(object v)
        {
            var l = v as List<object>;
            if (l == null)
            {
                var s = v as string;
                return s != null ? SplitNumbers(s) : null;
            }
            var r = new double[l.Count];
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i] is double) r[i] = (double)l[i];
                else if (!(l[i] is string) || !Num((string)l[i], out r[i])) return null;
            }
            return r;
        }

        private static double[] SplitNumbers(string s)
        {
            string[] parts = (s ?? "").Trim().Trim('[', ']').Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            var r = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++) if (!Num(parts[i], out r[i])) return null;
            return r;
        }

        private static object Value(string s)
        {
            double d;
            if (Num(s, out d)) return d;
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            return s;
        }

        private static bool Num(string s, out double d)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        /// <summary>
 /// The Subnautica command that answers h = "raycast" (sn-s3, LabScene: <c>de.s3 {action: "probe", x, z, fromY?,
 /// y?, warp?}</c>, a layer-30 ray down at (x, z); reply <c>{ok, pending: false, seabedY, collider, layer}</c>, or
 /// <c>{ok, pending: true}</c> while the terrain collider there is not built yet).
 /// </summary>
        public static Dictionary<string, object> ProbeArgs(double x, double z)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { "action", "probe" }, { "x", x }, { "z", z } };
        }

        /// <summary>The outcome of one probe reply.</summary>
        public enum Probe { Height, Pending, Failed }

        /// <summary>Reads a probe reply: Height with <paramref name="h"/> = seabedY; Pending (ask again); Failed with <paramref name="why"/>.</summary>
        public static Probe ReadProbe(IDictionary<string, object> reply, out double h, out string why)
        {
            h = double.NaN;
            why = null;
            if (reply == null) { why = "no reply from Subnautica (timeout)"; return Probe.Failed; }
            if (MiniJson.GetBool(reply, "ok") == false) { why = "Subnautica: " + (MiniJson.GetString(reply, "error") ?? "probe failed"); return Probe.Failed; }
            double? v = MiniJson.GetNumber(reply, "seabedY");
            if (v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)) { h = v.Value; return Probe.Height; }
            if (MiniJson.GetBool(reply, "pending") == true) { why = "the terrain collider at the point is not built yet"; return Probe.Pending; }
            why = "the reply carries no seabedY";
            return Probe.Failed;
        }

        public static bool TryProbeHeight(IDictionary<string, object> reply, out double h, out string why)
        {
            return ReadProbe(reply, out h, out why) == Probe.Height;
        }
    }

    // ===================================================================================================== rows

 /// <summary>
 /// The S3 JSONL rows of se-*.jsonl in the "s3/1" log contract of tests/s3/analyze_s3.py and check_placement.py
 /// (<c>{"t", "ev": "s3.&lt;name&gt;", …}</c>, payload top-level). Doubles are G17 (round trip); wire floats are written
 /// as the double value of the float. <c>s3.pose</c> is the S3.C2 input: <c>tick</c> (the C2 tick that carried the
 /// pose), <c>entityId</c>/<c>gridId</c>, <c>seMatrix</c> = the grid's SE world matrix as 12 doubles in rows Right, Up,
 /// Backward, Translation, <c>pivot</c> = P* (SE world, doubles), <c>k</c>, <c>site</c> {C, Rs}, the wire values
 /// <c>o</c>/<c>q</c>/<c>s</c> (also as <c>wire</c>), <c>camSe</c>, the grid's local box <c>bounds</c> [min, max]
 /// (also <c>aabbMin</c>/<c>aabbMax</c>), <c>lagged</c> (S3.M4: then the row is the previous tick's matrix and P*,
 /// exactly what C2 carried) and <c>reason</c> (first, changed, periodic, trace). A pose row holds until the next one
 /// (rows are written when the wire pose changes, at most 10 per second outside a trace, every tick while tracing;
 /// the s3.stats row counts the skipped ones).
 /// </summary>
    internal static class S3Rows
    {
        public static string Pose(string utc, ulong tick, long entityId, SeMatrix m, Vec3 pivot, Vec3 camSe, F32x3 o, F32x4 q, float s, float k,
                                  Vec3 aabbMin, Vec3 aabbMax, bool lagged, string reason, SiteMap site)
        {
            var sb = new StringBuilder(900);
            Head(sb, utc, "pose");
            sb.Append(",\"tick\":").Append(tick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"entityId\":").Append(entityId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"gridId\":").Append(entityId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"seMatrix\":[");
            Vec(sb, m.Right); sb.Append(','); Vec(sb, m.Up); sb.Append(','); Vec(sb, m.Backward); sb.Append(','); Vec(sb, m.Translation);
            sb.Append("],\"pivot\":["); Vec(sb, pivot);
            sb.Append("],\"camSe\":["); Vec(sb, camSe);
            sb.Append("],\"o\":[").Append(G(o.x)).Append(',').Append(G(o.y)).Append(',').Append(G(o.z));
            sb.Append("],\"q\":[").Append(G(q.x)).Append(',').Append(G(q.y)).Append(',').Append(G(q.z)).Append(',').Append(G(q.w));
            sb.Append("],\"s\":").Append(G(s)).Append(",\"k\":").Append(G(k));
            sb.Append(",\"wire\":{\"o\":[").Append(G(o.x)).Append(',').Append(G(o.y)).Append(',').Append(G(o.z));
            sb.Append("],\"q\":[").Append(G(q.x)).Append(',').Append(G(q.y)).Append(',').Append(G(q.z)).Append(',').Append(G(q.w));
            sb.Append("],\"s\":").Append(G(s)).Append(",\"k\":").Append(G(k)).Append('}');
            if (site != null)
            {
                sb.Append(",\"site\":{\"C\":["); Vec(sb, site.C);
                sb.Append("],\"Rs\":").Append(G(site.Rs)).Append('}');
            }
            sb.Append(",\"bounds\":["); Vec(sb, aabbMin); sb.Append(','); Vec(sb, aabbMax);
            sb.Append("],\"aabbMin\":["); Vec(sb, aabbMin);
            sb.Append("],\"aabbMax\":["); Vec(sb, aabbMax);
            sb.Append("],\"lagged\":").Append(lagged ? "true" : "false");
            sb.Append(",\"reason\":"); JsonText.AppendString(sb, reason ?? "");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
 /// s3.place (S3.C1, and a pose for S3.C2's fallback): the grid, the requested lab point and heading, its SE matrix,
 /// P* and the pose at the time of the row, its local box, and the totals of its parts (verts/tris = the sum over
 /// the parts of their model's vertex and triangle counts, what Subnautica's "DE.Mirror built" counts).
 /// </summary>
        public static string Place(string utc, ulong tick, long gridId, Vec3 labSn, double headingDeg, string hSource, SeMatrix m, Vec3 pivot,
                                   WireEntityPose pose, Vec3 aabbMin, Vec3 aabbMax, int parts, long verts, long tris, int models, SiteMap site)
        {
            var sb = new StringBuilder(900);
            Head(sb, utc, "place");
            sb.Append(",\"tick\":").Append(tick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"gridId\":").Append(gridId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"entityId\":").Append(gridId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"labSn\":["); Vec(sb, labSn);
            sb.Append("],\"headingDeg\":").Append(G(headingDeg));
            sb.Append(",\"hSource\":"); JsonText.AppendString(sb, hSource ?? "");
            sb.Append(",\"seMatrix\":[");
            Vec(sb, m.Right); sb.Append(','); Vec(sb, m.Up); sb.Append(','); Vec(sb, m.Backward); sb.Append(','); Vec(sb, m.Translation);
            sb.Append("],\"pivot\":["); Vec(sb, pivot);
            WireEntityPose w = pose.RoundToFloat32();
            sb.Append("],\"o\":["); Vec(sb, w.O);
            sb.Append("],\"q\":[").Append(G(w.Q.X)).Append(',').Append(G(w.Q.Y)).Append(',').Append(G(w.Q.Z)).Append(',').Append(G(w.Q.W));
            sb.Append("],\"s\":").Append(G(w.S)).Append(",\"k\":").Append(G(w.K));
            if (site != null)
            {
                sb.Append(",\"site\":{\"C\":["); Vec(sb, site.C);
                sb.Append("],\"Rs\":").Append(G(site.Rs)).Append('}');
            }
            sb.Append(",\"bounds\":["); Vec(sb, aabbMin); sb.Append(','); Vec(sb, aabbMax);
            sb.Append("],\"parts\":").Append(parts).Append(",\"verts\":").Append(verts).Append(",\"tris\":").Append(tris).Append(",\"models\":").Append(models);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>s3.gps (S3.N2): the SE camera's pixel (top-left, SE window pixels) of the GPS point (the block centre) in the frame of tick/qpc.</summary>
        public static string Gps(string utc, ulong tick, long qpc, long gridId, double px, double py, bool inFront, Vec3 pointSe)
        {
            var sb = new StringBuilder(256);
            Head(sb, utc, "gps");
            sb.Append(",\"tick\":").Append(tick.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"qpc\":").Append(qpc.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"gridId\":").Append(gridId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"px\":[").Append(G(px)).Append(',').Append(G(py)).Append(']');
            sb.Append(",\"inFront\":").Append(inFront ? "true" : "false");
            sb.Append(",\"pointSe\":["); Vec(sb, pointSe);
            sb.Append("]}");
            return sb.ToString();
        }

        public static string Model(string utc, ExportedModel m, int chunks, long bytes)
        {
            var sb = new StringBuilder(768);
            Head(sb, utc, "model");
            sb.Append(",\"modelKey\":"); JsonText.AppendString(sb, Hex(m.ModelKey));
            sb.Append(",\"asset\":"); JsonText.AppendString(sb, m.AssetName);
            sb.Append(",\"verts\":").Append(m.VertexCount).Append(",\"tris\":").Append(m.TriangleCount);
            sb.Append(",\"seVerts\":").Append(m.SeVertexCount).Append(",\"seTris\":").Append(m.SeTriangleCount);
            sb.Append(",\"submeshes\":").Append(m.Submeshes != null ? m.Submeshes.Length : 0);
            sb.Append(",\"patternScale\":").Append(G(m.PatternScale)).Append(",\"scale\":").Append(G(m.Scale));
            sb.Append(",\"tangents\":"); JsonText.AppendString(sb, m.TangentSource);
            sb.Append(",\"chunks\":").Append(chunks).Append(",\"bytes\":").Append(bytes);
            sb.Append(",\"loadMs\":").Append(G(Math.Round(m.LoadMs, 3)));
            float[] b = m.Bounds();
            sb.Append(",\"bounds\":[");
            for (int i = 0; i < 6; i++) { if (i > 0) sb.Append(','); sb.Append(G(b[i])); }
            sb.Append("],\"materials\":[");
            if (m.Materials != null)
                for (int i = 0; i < m.Materials.Length; i++)
                {
                    MaterialInfo mi = m.Materials[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"key\":"); JsonText.AppendString(sb, Hex(mi.Key));
                    sb.Append(",\"name\":"); JsonText.AppendString(sb, mi.Name);
                    sb.Append(",\"technique\":").Append(mi.DrawTechnique).Append(",\"flags\":").Append(mi.Flags);
                    sb.Append(",\"cm\":"); JsonText.AppendString(sb, mi.ColorMetal);
                    sb.Append(",\"ng\":"); JsonText.AppendString(sb, mi.NormalGloss);
                    sb.Append(",\"add\":"); JsonText.AppendString(sb, mi.AddMaps);
                    sb.Append(",\"alpha\":"); JsonText.AppendString(sb, mi.AlphaMask);
                    sb.Append(",\"missing\":"); JsonText.AppendString(sb, mi.Missing);
                    sb.Append('}');
                }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string Part(string utc, string op, long entityId, PartSnapshot p)
        {
            var sb = new StringBuilder(512);
            Head(sb, utc, "part");
            sb.Append(",\"op\":"); JsonText.AppendString(sb, op);
            sb.Append(",\"entityId\":").Append(entityId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"partKey\":"); JsonText.AppendString(sb, Hex(p.PartKey));
            sb.Append(",\"modelKey\":"); JsonText.AppendString(sb, Hex(p.ModelKey));
            sb.Append(",\"asset\":"); JsonText.AppendString(sb, p.AssetName);
            sb.Append(",\"local\":[");
            if (p.Local != null) for (int i = 0; i < p.Local.Length; i++) { if (i > 0) sb.Append(','); sb.Append(G(p.Local[i])); }
            sb.Append("],\"hsv\":[").Append(G(p.H)).Append(',').Append(G(p.S)).Append(',').Append(G(p.V));
            sb.Append("],\"uvOffset\":[").Append(G(p.UvU)).Append(',').Append(G(p.UvV));
            sb.Append("],\"seGetTextureOffset\":[").Append(G(p.LegacyU)).Append(',').Append(G(p.LegacyV));
            sb.Append("],\"skinId\":").Append(p.SkinId).Append('}');
            return sb.ToString();
        }

        /// <summary>Starts a row: <c>{"t": utc, "ev": "s3.&lt;name&gt;"</c>.</summary>
        public static void Head(StringBuilder sb, string utc, string name)
        {
            sb.Append("{\"t\":"); JsonText.AppendString(sb, utc);
            sb.Append(",\"ev\":"); JsonText.AppendString(sb, "s3." + name);
        }

        public static string Hex(ulong v) { return "0x" + v.ToString("x16", CultureInfo.InvariantCulture); }

        public static void Vec(StringBuilder sb, Vec3 v)
        {
            sb.Append(G(v.X)).Append(',').Append(G(v.Y)).Append(',').Append(G(v.Z));
        }

        /// <summary>Round-trip double text (G17), "null" for non-finite values.</summary>
        public static string G(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("G17", CultureInfo.InvariantCulture);
        }
    }
}
