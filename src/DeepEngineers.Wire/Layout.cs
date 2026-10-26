using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// Region ids of the C0 region table. CONTRACT §5.2 names the regions but gives them no numeric id, kind code,
 /// record size or version; layout 1.0 defines them here (and in the layout-hash text, which pins them):
 /// id = channel number × 16 + part (C11 a..d = parts 0..3); the ring-control page is C0 part 1.
 /// </summary>
    public enum RegionId : uint
    {
        C0Header = 0x00,
        RingControl = 0x01,
        C1SeFrame = 0x10,
        C2SeEntities = 0x20,
        C3SeModels = 0x30,
        C4SeParts = 0x40,
        C5SeState = 0x50,
        C6SeTime = 0x60,
        C7SnStatus = 0x70,
        C8SnDamage = 0x80,
        C9SnCreatures = 0x90,
        C10Capture = 0xA0,
        C11aCtlToSe = 0xB0,
        C11bCtlFromSe = 0xB1,
        C11cCtlToSn = 0xB2,
        C11dCtlFromSn = 0xB3,
        C12SnStatics = 0xC0,
    }

    /// <summary>Region kind codes (C0 region table `kind`), from the "Kind" column of CONTRACT §5.1.</summary>
    public enum RegionKind : uint
    {
        Header = 1,
        RingControl = 2,
        Seqlock = 3,
        SeqlockTable = 4,
        SeqlockPair = 5,
        RecordRing = 6,
        ByteRing = 7,
    }

    /// <summary>Ring index r in the ring-control page (CONTRACT §5.2 order: C3, C4, C8, C12, C11a, C11b, C11c, C11d).</summary>
    public enum RingIndex
    {
        C3SeModels = 0,
        C4SeParts = 1,
        C8SnDamage = 2,
        C12SnStatics = 3,
        C11aCtlToSe = 4,
        C11bCtlFromSe = 5,
        C11cCtlToSn = 6,
        C11dCtlFromSn = 7,
    }

    /// <summary>One row of the region map.</summary>
    public sealed class RegionDef
    {
        public RegionId Id { get; }
        public RegionKind Kind { get; }
        public long Offset { get; }
        public long Size { get; }
        /// <summary>Size of the region's primary record: the fixed record for seqlocks, the row for tables and the
 /// record ring, the per-ring entry for the control page, 8 (framing header and alignment) for byte rings.</summary>
        public int RecordSize { get; }
        public uint Version { get; }
        public string Name { get; }

        public RegionDef(RegionId id, RegionKind kind, long offset, long size, int recordSize, uint version, string name)
        {
            Id = id; Kind = kind; Offset = offset; Size = size; RecordSize = recordSize; Version = version; Name = name;
        }

        public override string ToString()
        {
            return Name + " id=0x" + ((uint)Id).ToString("x") + " kind=" + Kind + " @0x" + Offset.ToString("x") + " size=0x" + Size.ToString("x");
        }
    }

    /// <summary>One field of a record in the declarative layout table (the source of the layout-hash text).</summary>
    public sealed class FieldDef
    {
        public string Name { get; }
        public int Offset { get; }
        public int Size { get; }
        /// <summary>u8 u16 u32 u64 i16 i32 i64 f32 f64, or an array `t[n]`.</summary>
        public string Type { get; }
        public bool IsPad { get; }
        /// <summary>The C# member that holds this field (pads: `pad_&lt;hex offset&gt;`).</summary>
        public string Member { get; }

        public FieldDef(string name, int offset, int size, string type, bool isPad, string member)
        {
            Name = name; Offset = offset; Size = size; Type = type; IsPad = isPad; Member = member;
        }
    }

    /// <summary>One record of the declarative layout table.</summary>
    public sealed class RecordDef
    {
        public string Name { get; }
        public Type ClrType { get; }
        public int Size { get; }
        public IReadOnlyList<FieldDef> Fields { get; }

        public RecordDef(string name, Type clrType, int size, FieldDef[] fields)
        {
            Name = name; ClrType = clrType; Size = size; Fields = fields;
        }
    }

    /// <summary>
 /// A record, or an array of records, at a fixed offset inside a region (CONTRACT §5.4: the C1 bones at 0x100, the C2
 /// entity/subpart/light tables, the C9 records at 0x40, the C10 result at +0x800, and every region's main record).
 /// Pinned by the `table` lines of the layout-hash text.
 /// </summary>
    public sealed class TableDef
    {
        public RegionId Region { get; }
        public string Name { get; }
        /// <summary>Offset from the region start.</summary>
        public int Offset { get; }
        public int Count { get; }
        public int Stride { get; }
        public string Record { get; }

        public TableDef(RegionId region, string name, int offset, int count, int stride, string record)
        {
            Region = region; Name = name; Offset = offset; Count = count; Stride = stride; Record = record;
        }
    }

    /// <summary>A byte-ring message type and the record its payload starts with (pinned by the `message` lines).</summary>
    public sealed class MessageDef
    {
        public RegionId Region { get; }
        public uint Type { get; }
        public string Record { get; }

        public MessageDef(RegionId region, uint type, string record) { Region = region; Type = type; Record = record; }
    }

    /// <summary>One enumeration value, flag bit or protocol limit of CONTRACT §5.4 (pinned by the `const` lines).</summary>
    public sealed class ConstDef
    {
        public string Group { get; }
        public string Name { get; }
        public ulong Value { get; }

        public ConstDef(string group, string name, ulong value) { Group = group; Name = name; Value = value; }
    }

    /// <summary>
 /// Layout 1.0 of the shared section `Local\DeepEngineers_v1` (docs/CONTRACT.md §5.2 region map, §5.4 records).
 /// Single source for offsets in C#; the layout hash (LayoutHash) is generated from the two tables below and
 /// Validate proves the record structs match them on the running runtime (.NET Framework or Unity Mono).
 /// </summary>
    public static unsafe class Layout
    {
        public const ushort Major = 1;
        public const ushort Minor = 0;
        public const long SectionSize = 0x5000000;
        public const int HeaderSize = 0x1000;
        public const int HeaderRecordSize = 0x100;
        public const int RegionTableOffset = 0x100;
        public const int RegionEntrySize = 32;
        public const int RingControlOffset = 0x1000;
        public const int RingControlStride = 0x80;
        public const int RingTailOffset = 0x40;
        public const int RingCount = 8;
        /// <summary>CONTRACT §5.1 C8: Subnautica's overflow queue holds up to 4,096 records, then drops the oldest.</summary>
        public const int C8OverflowCapacity = 4096;

        private static readonly RegionDef[] RegionsById =
        {
            new RegionDef(RegionId.C0Header,      RegionKind.Header,       0x0000000, 0x1000,    0x100, 1, "C0 Header"),
            new RegionDef(RegionId.RingControl,   RegionKind.RingControl,  0x0001000, 0x1000,    0x80,  1, "Ring control"),
            new RegionDef(RegionId.C1SeFrame,     RegionKind.Seqlock,      0x0010000, 0x2000,    0x100, 1, "C1 SeFrame"),
            new RegionDef(RegionId.C2SeEntities,  RegionKind.SeqlockTable, 0x0020000, 0x80000,   96,    1, "C2 SeEntities"),
            new RegionDef(RegionId.C3SeModels,    RegionKind.ByteRing,     0x1000000, 0x4000000, 8,     1, "C3 SeModels"),
            new RegionDef(RegionId.C4SeParts,     RegionKind.ByteRing,     0x0600000, 0x800000,  8,     1, "C4 SeParts"),
            new RegionDef(RegionId.C5SeState,     RegionKind.Seqlock,      0x0002000, 0x1000,    256,   1, "C5 SeState"),
            new RegionDef(RegionId.C6SeTime,      RegionKind.Seqlock,      0x0003000, 0x1000,    64,    1, "C6 SeTime"),
            new RegionDef(RegionId.C7SnStatus,    RegionKind.Seqlock,      0x0004000, 0x1000,    256,   1, "C7 SnStatus"),
            new RegionDef(RegionId.C8SnDamage,    RegionKind.RecordRing,   0x00B0000, 0x20000,   128,   1, "C8 SnDamage"),
            new RegionDef(RegionId.C9SnCreatures, RegionKind.SeqlockTable, 0x00A0000, 0x4000,    48,    1, "C9 SnCreatures"),
            new RegionDef(RegionId.C10Capture,    RegionKind.SeqlockPair,  0x0005000, 0x1000,    256,   1, "C10 Capture"),
            new RegionDef(RegionId.C11aCtlToSe,   RegionKind.ByteRing,     0x0100000, 0x40000,   8,     1, "C11a CtlToSe"),
            new RegionDef(RegionId.C11bCtlFromSe, RegionKind.ByteRing,     0x0140000, 0x40000,   8,     1, "C11b CtlFromSe"),
            new RegionDef(RegionId.C11cCtlToSn,   RegionKind.ByteRing,     0x0180000, 0x40000,   8,     1, "C11c CtlToSn"),
            new RegionDef(RegionId.C11dCtlFromSn, RegionKind.ByteRing,     0x01C0000, 0x40000,   8,     1, "C11d CtlFromSn"),
            new RegionDef(RegionId.C12SnStatics,  RegionKind.ByteRing,     0x0200000, 0x400000,  8,     1, "C12 SnStatics"),
        };

        private static readonly RegionId[] RingRegions =
        {
            RegionId.C3SeModels, RegionId.C4SeParts, RegionId.C8SnDamage, RegionId.C12SnStatics,
            RegionId.C11aCtlToSe, RegionId.C11bCtlFromSe, RegionId.C11cCtlToSn, RegionId.C11dCtlFromSn,
        };

        /// <summary>Every region of the table, in id order (also the order of the C0 region table).</summary>
        public static IReadOnlyList<RegionDef> Regions { get { return RegionsById; } }

        public static int RegionCount { get { return RegionsById.Length; } }

        public static RegionDef Region(RegionId id)
        {
            for (int i = 0; i < RegionsById.Length; i++) if (RegionsById[i].Id == id) return RegionsById[i];
            throw new ArgumentOutOfRangeException("id", "unknown region id 0x" + ((uint)id).ToString("x"));
        }

        public static RegionId RingRegion(RingIndex ring)
        {
            int r = (int)ring;
            if (r < 0 || r >= RingCount) throw new ArgumentOutOfRangeException("ring");
            return RingRegions[r];
        }

        public static long HeadOffset(RingIndex ring) { return RingControlOffset + (long)(int)ring * RingControlStride; }
        public static long TailOffset(RingIndex ring) { return HeadOffset(ring) + RingTailOffset; }

        // ---------------------------------------------------------------------------------------------------------
 // Declarative field table (CONTRACT §5.4). The canonical layout-hash text is built from this table only.
 // ---------------------------------------------------------------------------------------------------------

        private static FieldDef F(string name, int off, string type)
        {
            return new FieldDef(name, off, ScalarSize(type), type, false, name);
        }

        private static FieldDef A(string name, int off, string elem, int count)
        {
            return new FieldDef(name, off, ScalarSize(elem) * count, elem + "[" + count + "]", false, name);
        }

        /// <summary>CONTRACT "u32 pad" / "u16 pad" → that scalar type; CONTRACT "0x60-0x7F pad" → u8[n].</summary>
        private static FieldDef P(int off, int size, string type)
        {
            string member = "pad_" + off.ToString("x");
            return new FieldDef("pad", off, size, type ?? "u8[" + size + "]", true, member);
        }

        private static FieldDef P(int off, int size) { return P(off, size, null); }

        internal static int ScalarSize(string type)
        {
            switch (type)
            {
                case "u8": case "i8": return 1;
                case "u16": case "i16": return 2;
                case "u32": case "i32": case "f32": return 4;
                case "u64": case "i64": case "f64": return 8;
                default: throw new ArgumentException("unknown scalar type " + type);
            }
        }

        private static RecordDef R<T>(int size, params FieldDef[] fields) where T : struct
        {
            return new RecordDef(typeof(T).Name, typeof(T), size, fields);
        }

        private static readonly RecordDef[] RecordTable =
        {
            R<SectionHeader>(0x100,
                F("magic", 0x000, "u32"), F("layoutMajor", 0x004, "u16"), F("layoutMinor", 0x006, "u16"),
                F("byteOrderMark", 0x008, "u32"), F("headerSize", 0x00C, "u32"), F("sectionSize", 0x010, "u64"),
                F("layoutHash", 0x018, "u64"), F("wireVersion", 0x020, "u32"), F("siteVersion", 0x024, "u32"),
                F("damageScaleVersion", 0x028, "u32"), F("regionCount", 0x02C, "u32"), F("qpcFrequency", 0x030, "u64"),
                F("devControl", 0x038, "u32"), P(0x03C, 4, "u32"),
                F("snPid", 0x040, "u32"), F("snGeneration", 0x044, "u32"), F("snHeartbeatMs", 0x048, "u64"),
                F("snPhase", 0x050, "u32"), F("snFlags", 0x054, "u32"), F("snHwnd", 0x058, "u64"), P(0x060, 0x20),
                F("sePid", 0x080, "u32"), F("seGeneration", 0x084, "u32"), F("seHeartbeatMs", 0x088, "u64"),
                F("sePhase", 0x090, "u32"), F("seFlags", 0x094, "u32"), F("seHwnd", 0x098, "u64"),
                F("seLoadStep", 0x0A0, "u32"), P(0x0A4, 0x5C)),
            R<RegionEntry>(32,
                F("id", 0x00, "u32"), F("kind", 0x04, "u32"), F("offset", 0x08, "u64"), F("size", 0x10, "u64"),
                F("recordSize", 0x18, "u32"), F("version", 0x1C, "u32")),
            R<RingControlEntry>(0x80,
                F("head", 0x00, "u64"), P(0x08, 0x38), F("tail", 0x40, "u64"), P(0x48, 0x38)),
            R<SeFrame>(0x100,
                F("seq", 0x00, "u32"), F("flags", 0x04, "u32"), F("tick", 0x08, "u64"), F("qpcCapture", 0x10, "i64"),
                F("simTimeSec", 0x18, "f64"), F("simSpeed", 0x20, "f32"), F("worldEpoch", 0x24, "u32"),
                A("camPos", 0x28, "f32", 3), A("camRot", 0x34, "f32", 4), F("vFovDeg", 0x44, "f32"),
                F("near", 0x48, "f32"), F("aspect", 0x4C, "f32"), F("cameraMode", 0x50, "u32"), P(0x54, 4, "u32"),
                F("characterId", 0x58, "u64"), A("charO", 0x60, "f32", 3), A("charQ", 0x6C, "f32", 4),
                F("charS", 0x7C, "f32"), F("boneCount", 0x80, "u32"), F("bonesVersion", 0x84, "u32"),
                A("pelvisSn", 0x88, "f32", 3), P(0x94, 0x6C)),
            R<SeBone>(48,
                A("rows", 0x00, "f32", 12)),
            R<SeEntitiesHeader>(0x40,
                F("seq", 0x00, "u32"), F("entityCount", 0x04, "u32"), F("tick", 0x08, "u64"),
                F("worldEpoch", 0x10, "u32"), F("subpartCount", 0x14, "u32"), F("lightCount", 0x18, "u32"),
                P(0x1C, 0x24)),
            R<SeEntity>(96,
                F("entityId", 0x00, "u64"), F("kind", 0x08, "u32"), F("flags", 0x0C, "u32"), F("groupId", 0x10, "u64"),
                A("o", 0x18, "f32", 3), A("q", 0x24, "f32", 4), F("s", 0x34, "f32"), F("k", 0x38, "f32"),
                A("v", 0x3C, "f32", 3), A("omega", 0x48, "f32", 3), F("partsVersion", 0x54, "u32"),
                F("skinVersion", 0x58, "u32"), P(0x5C, 4, "u32")),
            R<SeSubpart>(64,
                F("ownerId", 0x00, "u64"), F("partKey", 0x08, "u64"), A("local", 0x10, "f32", 12)),
            R<SeLight>(64,
                F("ownerId", 0x00, "u64"), A("pos", 0x08, "f32", 3), A("dir", 0x14, "f32", 3),
                A("colour", 0x20, "f32", 3), F("range", 0x2C, "f32"), F("intensity", 0x30, "f32"),
                F("coneDeg", 0x34, "f32"), F("flags", 0x38, "u32"), P(0x3C, 4, "u32")),
            R<SeState>(256,
                F("seq", 0x00, "u32"), F("flags", 0x04, "u32"), F("tick", 0x08, "u64"), F("viewState", 0x10, "u32"),
                F("approachMode", 0x14, "u32"), F("displayMode", 0x18, "u32"), F("overlays", 0x1C, "u32"),
                F("zone", 0x20, "u32"), F("worldEpoch", 0x24, "u32"), F("perfMode", 0x28, "u32"),
                F("targetFps", 0x2C, "u32"), F("snBusTarget", 0x30, "f32"), F("crossfadeMs", 0x34, "f32"),
                F("helloEpoch", 0x38, "u32"), F("linkEpoch", 0x3C, "u32"), F("Rs", 0x40, "f64"),
                F("siteHash", 0x48, "u64"), F("siteVersion", 0x50, "u32"), P(0x54, 4, "u32"),
                A("seRect", 0x58, "i32", 4), F("camSnY", 0x68, "f32"), A("camVelSn", 0x6C, "f32", 3),
                F("crossingTick", 0x78, "u64"), P(0x80, 0x80)),
            R<SeTime>(64,
                F("seq", 0x00, "u32"), F("jumpSeq", 0x04, "u32"), F("tick", 0x08, "u64"),
                F("hourAngleRad", 0x10, "f64"), F("sunElevationRad", 0x18, "f32"), A("sunDirSn", 0x1C, "f32", 3),
                F("dayLengthSec", 0x28, "f32"), F("simSpeed", 0x2C, "f32"), F("paused", 0x30, "u32"),
                F("flags", 0x34, "u32"), F("sunriseHourAngleRad", 0x38, "f32"), P(0x3C, 4, "u32")),
            R<SnStatus>(256,
                F("seq", 0x00, "u32"), F("flags", 0x04, "u32"), F("snPhase", 0x08, "u32"),
                F("perfModeApplied", 0x0C, "u32"), F("snFrame", 0x10, "u64"), F("presentedSeTick", 0x18, "u64"),
                F("areaBuiltSeTick", 0x20, "u64"), A("areaBuiltPos", 0x28, "f32", 3), F("helloAckEpoch", 0x34, "u32"),
                F("linkAckEpoch", 0x38, "u32"), F("mirroredEntityCount", 0x3C, "u32"), F("pendingParts", 0x40, "u32"),
                F("damageDropped", 0x44, "u32"), F("c3BytesDrained", 0x48, "u64"), F("damageOverflowDepth", 0x50, "u32"),
                F("frameMsP50", 0x54, "f32"), F("frameMsP99", 0x58, "f32"), F("hostKind", 0x5C, "u32"),
                F("snHwnd", 0x60, "u64"), A("snRect", 0x68, "i32", 4), F("capabilities", 0x78, "u32"),
                F("vSyncCountApplied", 0x7C, "u32"), F("targetFrameRateApplied", 0x80, "i32"),
                F("fixedDeltaTime", 0x84, "f32"), F("resamplesLastFrame", 0x88, "u32"), P(0x8C, 0x74)),
            R<DamageRecord>(128,
                F("dedupSeq", 0x00, "u32"), F("snGeneration", 0x04, "u32"), F("unit", 0x08, "u32"),
                F("amount", 0x0C, "f32"), F("seScaleExpected", 0x10, "f32"), F("snDamageType", 0x14, "u16"),
                F("flags", 0x16, "u16"), F("targetKind", 0x18, "u32"), F("sectionId", 0x1C, "u32"),
                F("targetEntityId", 0x20, "u64"), F("rootEntityId", 0x28, "u64"), F("snapshotTick", 0x30, "u64"),
                A("localHit", 0x38, "f32", 3), A("snWorldHit", 0x44, "f32", 3), A("attackDirSn", 0x50, "f32", 3),
                F("dealerTechType", 0x5C, "i32"), F("dealerInstanceId", 0x60, "u32"), F("worldEpoch", 0x64, "u32"),
                F("snTimeSec", 0x68, "f64"), P(0x70, 0x10)),
            R<SnCreaturesHeader>(0x40,
                F("seq", 0x00, "u32"), F("count", 0x04, "u32"), F("snFrame", 0x08, "u64"), P(0x10, 0x30)),
            R<SnCreature>(48,
                F("instanceId", 0x00, "u32"), F("techType", 0x04, "i32"), A("posSn", 0x08, "f32", 3),
                A("velSn", 0x14, "f32", 3), F("aggression", 0x20, "f32"), F("flags", 0x24, "u32"),
                F("targetEntityId", 0x28, "u64")),
            R<CaptureRequest>(256,
                F("seq", 0x00, "u32"), F("reqId", 0x04, "u32"), F("kind", 0x08, "u32"), P(0x0C, 4, "u32"),
                F("atSeTick", 0x10, "u64"), F("pathLength", 0x18, "u16"), A("path", 0x1A, "u8", CaptureRequest.PathCapacity)),
            R<CaptureResult>(256,
                F("seq", 0x00, "u32"), F("reqId", 0x04, "u32"), F("status", 0x08, "u32"), F("alpha", 0x0C, "f32"),
                F("snFrame", 0x10, "u64"), F("seTickUsed", 0x18, "u64"), F("pathLength", 0x20, "u16"),
                A("path", 0x22, "u8", CaptureResult.PathCapacity)),
            R<MessageHeader>(8,
                F("type", 0x00, "u32"), F("payloadBytes", 0x04, "u32")),
            R<ModelBegin>(64,
                F("modelKey", 0x00, "u64"), F("lod", 0x08, "u32"), F("vertexCount", 0x0C, "u32"),
                F("indexCount", 0x10, "u32"), F("submeshCount", 0x14, "u32"), F("flags", 0x18, "u32"),
                A("bounds", 0x1C, "f32", 6), F("patternScale", 0x34, "f32"), F("chunkCount", 0x38, "u32"),
                F("totalBytes", 0x3C, "u32")),
            R<ModelChunkHeader>(16,
                F("modelKey", 0x00, "u64"), F("chunkIndex", 0x08, "u32"), F("bytes", 0x0C, "u32")),
            R<ModelEnd>(16,
                F("modelKey", 0x00, "u64"), F("payloadHash", 0x08, "u64")),
            R<MaterialDefHeader>(16,
                F("materialKey", 0x00, "u64"), F("drawTechnique", 0x08, "u32"), F("flags", 0x0C, "u32")),
            R<SkinnedBindHeader>(16,
                F("modelKey", 0x00, "u64"), F("boneCount", 0x08, "u32"), P(0x0C, 4, "u32")),
            R<ModelVertex>(56,
                A("pos", 0x00, "f32", 3), A("normal", 0x0C, "f32", 3), A("tangent", 0x18, "f32", 3),
                A("binormal", 0x24, "f32", 3), A("uv", 0x30, "f32", 2)),
            R<ModelVertexSkinned>(76,
                A("pos", 0x00, "f32", 3), A("normal", 0x0C, "f32", 3), A("tangent", 0x18, "f32", 3),
                A("binormal", 0x24, "f32", 3), A("uv", 0x30, "f32", 2), A("boneIndex", 0x38, "u8", 4),
                A("weight", 0x3C, "f32", 4)),
            R<ModelSubmesh>(24,
                F("materialKey", 0x00, "u64"), F("triStart", 0x08, "u32"), F("triCount", 0x0C, "u32"),
                F("drawTechnique", 0x10, "u32"), P(0x14, 4, "u32")),
            R<GridAdd>(32,
                F("entityId", 0x00, "u64"), F("targetClass", 0x08, "u32"), F("flags", 0x0C, "u32"),
                F("parentEntityId", 0x10, "u64"), F("worldEpoch", 0x18, "u32"), P(0x1C, 4, "u32")),
            R<EntityRemove>(8,
                F("entityId", 0x00, "u64")),
            R<PartAdd>(100,
                F("entityId", 0x00, "u64"), F("partKey", 0x08, "u64"), F("modelKey", 0x10, "u64"),
                A("local", 0x18, "f32", 12), A("colorMaskHSV", 0x48, "f32", 3), A("uvOffset", 0x54, "f32", 2),
                F("skinId", 0x5C, "u32"), F("flags", 0x60, "u32")),
            R<PartRemove>(16,
                F("entityId", 0x00, "u64"), F("partKey", 0x08, "u64")),
            R<PartRecolor>(32,
                F("entityId", 0x00, "u64"), F("partKey", 0x08, "u64"), A("hsv", 0x10, "f32", 3), P(0x1C, 4, "u32")),
            R<SkinSetHeader>(16,
                F("entityId", 0x00, "u64"), F("skinVersion", 0x08, "u32"), F("sectionCount", 0x0C, "u32")),
            R<SkinSection>(24,
                F("sectionId", 0x00, "u32"), A("centre", 0x04, "f32", 3), F("boxCount", 0x10, "u32"),
                F("kind", 0x14, "u32")),
            R<SkinBox>(64,
                A("centre", 0x00, "f32", 3), A("half", 0x0C, "f32", 3), A("axes", 0x18, "f32", 9),
                F("flags", 0x3C, "u32")),
            R<RoomSetHeader>(16,
                F("entityId", 0x00, "u64"), F("count", 0x08, "u32"), P(0x0C, 4, "u32")),
            R<RoomBox>(24,
                A("min", 0x00, "i32", 3), A("max", 0x0C, "i32", 3)),
            R<CharacterAdd>(24,
                F("entityId", 0x00, "u64"), F("modelKey", 0x08, "u64"), F("flags", 0x10, "u32"),
                F("boneCount", 0x14, "u32")),
            R<ClearAll>(8,
                F("worldEpoch", 0x00, "u32"), F("linkEpoch", 0x04, "u32")),
            R<BatchStaticsHeader>(16,
                F("bx", 0x00, "i16"), F("by", 0x02, "i16"), F("bz", 0x04, "i16"), P(0x06, 2, "u16"),
                F("epoch", 0x08, "u32"), F("count", 0x0C, "u32")),
            R<StaticPrimitive>(48,
                F("kind", 0x00, "u8"), F("flags", 0x01, "u8"), P(0x02, 2, "u16"), A("centreSn", 0x04, "f32", 3),
                A("qSn", 0x10, "f32", 4), A("size", 0x20, "f32", 3), F("classIdHash", 0x2C, "u32")),
            R<BatchClear>(16,
                F("bx", 0x00, "i16"), F("by", 0x02, "i16"), F("bz", 0x04, "i16"), P(0x06, 2, "u16"),
                F("epoch", 0x08, "u32"), P(0x0C, 4, "u32")),
        };

        /// <summary>Every record of the field table (declaration order; the hash text sorts by name).</summary>
        public static IReadOnlyList<RecordDef> Records { get { return RecordTable; } }

        public static RecordDef Record(string name)
        {
            for (int i = 0; i < RecordTable.Length; i++) if (RecordTable[i].Name == name) return RecordTable[i];
            throw new ArgumentOutOfRangeException("name", "unknown record " + name);
        }

        // ---------------------------------------------------------------------------------------------------------
 // Placement tables: which record sits where inside each region, which record each byte-ring
 // message type carries, the ring-control order, and every enumeration and flag value. All of them are in the
 // layout-hash text, so two builds that differ in any of them fail validation step 3 against each other.
 // ---------------------------------------------------------------------------------------------------------

        private static readonly TableDef[] TableTable =
        {
            new TableDef(RegionId.C0Header, "header", 0, 1, HeaderRecordSize, "SectionHeader"),
            new TableDef(RegionId.C0Header, "regionTable", RegionTableOffset, RegionsById.Length, RegionEntrySize, "RegionEntry"),
            new TableDef(RegionId.RingControl, "entries", 0, RingCount, RingControlStride, "RingControlEntry"),
            new TableDef(RegionId.C1SeFrame, "frame", 0, 1, 0x100, "SeFrame"),
            new TableDef(RegionId.C1SeFrame, "bones", SeFrameLimits.BonesOffset, SeFrameLimits.MaxBones, SeFrameLimits.BoneSize, "SeBone"),
            new TableDef(RegionId.C2SeEntities, "header", 0, 1, 0x40, "SeEntitiesHeader"),
            new TableDef(RegionId.C2SeEntities, "entities", SeEntitiesLimits.EntitiesOffset, SeEntitiesLimits.MaxEntities, SeEntitiesLimits.EntitySize, "SeEntity"),
            new TableDef(RegionId.C2SeEntities, "subparts", SeEntitiesLimits.SubpartsOffset, SeEntitiesLimits.MaxSubparts, SeEntitiesLimits.SubpartSize, "SeSubpart"),
            new TableDef(RegionId.C2SeEntities, "lights", SeEntitiesLimits.LightsOffset, SeEntitiesLimits.MaxLights, SeEntitiesLimits.LightSize, "SeLight"),
            new TableDef(RegionId.C5SeState, "state", 0, 1, 256, "SeState"),
            new TableDef(RegionId.C6SeTime, "time", 0, 1, 64, "SeTime"),
            new TableDef(RegionId.C7SnStatus, "status", 0, 1, 256, "SnStatus"),
            new TableDef(RegionId.C8SnDamage, "records", 0, 1024, 128, "DamageRecord"),
            new TableDef(RegionId.C9SnCreatures, "header", 0, 1, 0x40, "SnCreaturesHeader"),
            new TableDef(RegionId.C9SnCreatures, "records", SnCreaturesLimits.RecordsOffset, SnCreaturesLimits.MaxCreatures, SnCreaturesLimits.RecordSize, "SnCreature"),
            new TableDef(RegionId.C10Capture, "request", CaptureLayout.RequestOffset, 1, 256, "CaptureRequest"),
            new TableDef(RegionId.C10Capture, "result", CaptureLayout.ResultOffset, 1, 256, "CaptureResult"),
        };

        private static readonly MessageDef[] MessageTable =
        {
            new MessageDef(RegionId.C3SeModels, C3Type.ModelBegin, "ModelBegin"),
            new MessageDef(RegionId.C3SeModels, C3Type.ModelChunk, "ModelChunkHeader"),
            new MessageDef(RegionId.C3SeModels, C3Type.ModelEnd, "ModelEnd"),
            new MessageDef(RegionId.C3SeModels, C3Type.MaterialDef, "MaterialDefHeader"),
            new MessageDef(RegionId.C3SeModels, C3Type.SkinnedBind, "SkinnedBindHeader"),
            new MessageDef(RegionId.C4SeParts, C4Type.GridAdd, "GridAdd"),
            new MessageDef(RegionId.C4SeParts, C4Type.EntityRemove, "EntityRemove"),
            new MessageDef(RegionId.C4SeParts, C4Type.PartAdd, "PartAdd"),
            new MessageDef(RegionId.C4SeParts, C4Type.PartRemove, "PartRemove"),
            new MessageDef(RegionId.C4SeParts, C4Type.PartRecolor, "PartRecolor"),
            new MessageDef(RegionId.C4SeParts, C4Type.SkinSet, "SkinSetHeader"),
            new MessageDef(RegionId.C4SeParts, C4Type.RoomSet, "RoomSetHeader"),
            new MessageDef(RegionId.C4SeParts, C4Type.CharacterAdd, "CharacterAdd"),
            new MessageDef(RegionId.C4SeParts, C4Type.ClearAll, "ClearAll"),
            new MessageDef(RegionId.C12SnStatics, C12Type.BatchStatics, "BatchStaticsHeader"),
            new MessageDef(RegionId.C12SnStatics, C12Type.BatchClear, "BatchClear"),
        };

        /// <summary>
 /// The classes of Records.cs whose `const` members are wire values (enumerations, flag bits, message types,
 /// C3Type.MaxChunkBytes). Every literal field of each class becomes a `const` line, so adding or changing a
 /// value changes the hash. The table limits (SeFrameLimits, SeEntitiesLimits,...) are pinned by `table` lines.
 /// </summary>
        public static readonly Type[] ConstantGroups =
        {
            typeof(SnPhase), typeof(SePhase), typeof(SnFlags), typeof(SeFlags), typeof(DevControl), typeof(SeFrameFlags),
            typeof(CameraMode), typeof(SeEntityKind), typeof(SeEntityFlags), typeof(SeLightFlags), typeof(SeStateFlags),
            typeof(ViewState), typeof(ApproachMode), typeof(DisplayMode), typeof(Overlays), typeof(Zone), typeof(PerfMode),
            typeof(SeTimeFlags), typeof(SnStatusFlags), typeof(HostKind), typeof(Capabilities), typeof(DamageUnit),
            typeof(DamageTargetKind), typeof(DamageFlags), typeof(SnCreatureFlags), typeof(CaptureKind), typeof(CaptureStatus),
            typeof(C3Type), typeof(ModelFlags), typeof(MaterialFlags), typeof(C4Type), typeof(GridClass),
            typeof(SkinSectionKind), typeof(C11Type), typeof(C12Type), typeof(StaticKind),
        };

        private static ConstDef[] _constants;

        /// <summary>Fixed records and arrays inside regions (declaration order; the hash text sorts them).</summary>
        public static IReadOnlyList<TableDef> Tables { get { return TableTable; } }

        /// <summary>Byte-ring message types and their payload records.</summary>
        public static IReadOnlyList<MessageDef> Messages { get { return MessageTable; } }

        /// <summary>Ring index r → region, in the CONTRACT §5.2 order C3, C4, C8, C12, C11a, C11b, C11c, C11d.</summary>
        public static IReadOnlyList<RegionId> RingOrder { get { return RingRegions; } }

        /// <summary>Every literal of <see cref="ConstantGroups"/> (group = class name, name = member name).</summary>
        public static IReadOnlyList<ConstDef> Constants
        {
            get
            {
                ConstDef[] c = _constants;
                if (c != null) return c;
                var list = new List<ConstDef>();
                foreach (Type t in ConstantGroups)
                {
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (!f.IsLiteral) continue;
                        list.Add(new ConstDef(t.Name, f.Name, Convert.ToUInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
                c = list.ToArray();
                _constants = c;
                return c;
            }
        }

        /// <summary>Seqlock records: `seq` is their first u32 (0x00).</summary>
        public static readonly Type[] SeqlockRecordTypes =
        {
            typeof(SeFrame), typeof(SeEntitiesHeader), typeof(SeState), typeof(SeTime), typeof(SnStatus),
            typeof(SnCreaturesHeader), typeof(CaptureRequest), typeof(CaptureResult),
        };

        // ---------------------------------------------------------------------------------------------------------
 // Validation (CONTRACT §6 validation step 3, run at load on both sides and by S0.N1).
 // ---------------------------------------------------------------------------------------------------------

        private static string _validated; // null = not run; "" = OK; else the error

 /// <summary>
 /// Asserts that every record struct matches the CONTRACT size and every field offset/size of the table, that
 /// the fields tile each record exactly, and that the region map is consistent. Throws
 /// InvalidOperationException naming the record (or region) on the first mismatch.
 /// </summary>
        public static void Validate()
        {
            string error;
            if (!TryValidate(out error)) throw new InvalidOperationException(error);
        }

        /// <summary>Non-throwing Validate; the result is computed once per process.</summary>
        public static bool TryValidate(out string error)
        {
            string v = _validated;
            if (v == null)
            {
                try { v = ValidateCore(); }
                catch (Exception e) { v = "Layout: validation threw " + e.GetType().Name + ": " + e.Message; }
                _validated = v;
            }
            error = v.Length == 0 ? null : v;
            return error == null;
        }

        /// <summary>
 /// The managed size of a blittable struct on the running runtime (what sizeof(T) / Unsafe.SizeOf&lt;T&gt; return):
 /// the element stride of a pinned two-element array. Non-generic on purpose (no MakeGenericMethod over the
 /// `unmanaged` constraint at load time on Unity's Mono).
 /// </summary>
        public static int ManagedSize(Type t)
        {
            Array array = Array.CreateInstance(t, 2);
            GCHandle h = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                return (int)(Marshal.UnsafeAddrOfPinnedArrayElement(array, 1).ToInt64() -
                             Marshal.UnsafeAddrOfPinnedArrayElement(array, 0).ToInt64());
            }
            finally { h.Free(); }
        }

        private static string ValidateCore()
        {
            // records
            var names = new HashSet<string>();
            foreach (RecordDef r in RecordTable)
            {
                if (!names.Add(r.Name)) return "Layout: record " + r.Name + " declared twice";
                if (r.Name != r.ClrType.Name) return "Layout: record " + r.Name + " is bound to type " + r.ClrType.Name;
                int marshal = Marshal.SizeOf(r.ClrType);
                if (marshal != r.Size) return "Layout: record " + r.Name + " Marshal.SizeOf " + marshal + " != " + r.Size;
                int managed = ManagedSize(r.ClrType);
                if (managed != r.Size) return "Layout: record " + r.Name + " sizeof " + managed + " != " + r.Size;
                int next = 0;
                foreach (FieldDef f in r.Fields)
                {
                    if (f.Offset != next)
                        return "Layout: record " + r.Name + " field " + f.Name + " at 0x" + f.Offset.ToString("x") +
                               " leaves a gap or overlap (expected 0x" + next.ToString("x") + ")";
                    next = f.Offset + f.Size;
                    FieldInfo fi = r.ClrType.GetField(f.Member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi == null) return "Layout: record " + r.Name + " has no member " + f.Member;
                    long off = Marshal.OffsetOf(r.ClrType, f.Member).ToInt64();
                    if (off != f.Offset)
                        return "Layout: record " + r.Name + " field " + f.Member + " offset 0x" + off.ToString("x") +
                               " != 0x" + f.Offset.ToString("x");
                    int fsize = FieldStorageSize(fi);
                    if (fsize != f.Size)
                        return "Layout: record " + r.Name + " field " + f.Member + " size " + fsize + " != " + f.Size;
                }
                if (next != r.Size) return "Layout: record " + r.Name + " fields end at 0x" + next.ToString("x") + " != size 0x" + r.Size.ToString("x");
            }
            foreach (Type t in SeqlockRecordTypes)
            {
                if (Marshal.OffsetOf(t, "seq").ToInt64() != 0) return "Layout: record " + t.Name + " seq is not at 0x00";
            }

            // regions
            uint prevId = 0;
            for (int i = 0; i < RegionsById.Length; i++)
            {
                RegionDef g = RegionsById[i];
                if (i > 0 && (uint)g.Id <= prevId) return "Layout: region " + g.Name + " is not in id order";
                prevId = (uint)g.Id;
                if (g.Offset < 0 || g.Size <= 0 || g.Offset + g.Size > SectionSize) return "Layout: region " + g.Name + " outside the section";
                if ((g.Offset & 0xFFF) != 0) return "Layout: region " + g.Name + " is not page aligned";
                for (int j = 0; j < RegionsById.Length; j++)
                {
                    if (j == i) continue;
                    RegionDef h = RegionsById[j];
                    if (g.Offset < h.Offset + h.Size && h.Offset < g.Offset + g.Size) return "Layout: region " + g.Name + " overlaps " + h.Name;
                }
                if (g.Kind == RegionKind.ByteRing || g.Kind == RegionKind.RecordRing)
                {
                    if ((g.Size & (g.Size - 1)) != 0) return "Layout: ring " + g.Name + " capacity is not a power of two";
                    if (g.Size % g.RecordSize != 0) return "Layout: ring " + g.Name + " size is not a multiple of its record size";
                }
            }
            if (RegionTableOffset + RegionsById.Length * RegionEntrySize > HeaderSize) return "Layout: region table does not fit the header";
            if (RingCount * RingControlStride > Region(RegionId.RingControl).Size) return "Layout: ring control entries do not fit their page";
            if (Region(RegionId.RingControl).Offset != RingControlOffset) return "Layout: ring control page offset";
            for (int r = 0; r < RingCount; r++)
            {
                RegionKind k = Region(RingRegion((RingIndex)r)).Kind;
                if (k != RegionKind.ByteRing && k != RegionKind.RecordRing) return "Layout: ring index " + r + " is not a ring region";
            }
#pragma warning disable 0162 // constant conditions: kept as executable documentation of the CONTRACT arithmetic
            if (SeFrameLimits.BonesOffset + SeFrameLimits.MaxBones * SeFrameLimits.BoneSize > 0x1F00) return "Layout: C1 bones exceed 0x1F00";
            if (SeEntitiesLimits.EntitiesOffset + SeEntitiesLimits.MaxEntities * SeEntitiesLimits.EntitySize != SeEntitiesLimits.SubpartsOffset) return "Layout: C2 subparts offset";
            if (SeEntitiesLimits.SubpartsOffset + SeEntitiesLimits.MaxSubparts * SeEntitiesLimits.SubpartSize != SeEntitiesLimits.LightsOffset) return "Layout: C2 lights offset";
            if (SeEntitiesLimits.LightsOffset + SeEntitiesLimits.MaxLights * SeEntitiesLimits.LightSize != SeEntitiesLimits.End) return "Layout: C2 end";
#pragma warning restore 0162
            if (SeEntitiesLimits.End > Region(RegionId.C2SeEntities).Size) return "Layout: C2 table exceeds its region";
            if (SnCreaturesLimits.RecordsOffset + SnCreaturesLimits.MaxCreatures * SnCreaturesLimits.RecordSize > Region(RegionId.C9SnCreatures).Size) return "Layout: C9 table exceeds its region";
            if (Region(RegionId.C8SnDamage).Size / Region(RegionId.C8SnDamage).RecordSize != 1024) return "Layout: C8 capacity is not 1,024 records";

            // placement tables
            foreach (TableDef t in TableTable)
            {
                RecordDef rec = null;
                foreach (RecordDef r in RecordTable) if (r.Name == t.Record) rec = r;
                if (rec == null) return "Layout: table " + t.Name + " names an unknown record " + t.Record;
                if (rec.Size != t.Stride) return "Layout: table " + t.Name + " stride " + t.Stride + " != sizeof " + t.Record + " " + rec.Size;
                if (t.Count < 1 || t.Offset < 0) return "Layout: table " + t.Name + " count/offset";
                RegionDef g = Region(t.Region);
                if (g.Kind == RegionKind.ByteRing) return "Layout: table " + t.Name + " inside a byte ring";
                if ((long)t.Offset + (long)t.Count * t.Stride > g.Size) return "Layout: table " + g.Name + "." + t.Name + " exceeds its region";
                foreach (TableDef u in TableTable)
                {
                    if (ReferenceEquals(u, t) || u.Region != t.Region) continue;
                    long a0 = t.Offset, a1 = t.Offset + (long)t.Count * t.Stride, b0 = u.Offset, b1 = u.Offset + (long)u.Count * u.Stride;
                    if (a0 < b1 && b0 < a1) return "Layout: tables " + t.Name + " and " + u.Name + " overlap in " + g.Name;
                }
            }
            foreach (RegionDef g in RegionsById)
            {
                if (g.Kind == RegionKind.ByteRing) continue;
                // a record at offset 0, and a table whose row is the region's recordSize (the row of a seqlock table)
                bool atZero = false, row = false;
                foreach (TableDef t in TableTable)
                {
                    if (t.Region != g.Id) continue;
                    if (t.Offset == 0) atZero = true;
                    if (t.Stride == g.RecordSize) row = true;
                }
                if (!atZero || !row) return "Layout: region " + g.Name + " needs a table at offset 0 and one whose stride is its recordSize";
            }
            foreach (MessageDef m in MessageTable)
            {
                bool known = false;
                foreach (RecordDef r in RecordTable) if (r.Name == m.Record) known = true;
                if (!known) return "Layout: message " + m.Type + " names an unknown record " + m.Record;
                if (Region(m.Region).Kind != RegionKind.ByteRing) return "Layout: message " + m.Record + " outside a byte ring";
                if (m.Type == 0) return "Layout: message type 0 is the pad record";
            }
            var constKeys = new HashSet<string>();
            foreach (ConstDef c in Constants)
                if (!constKeys.Add(c.Group + "." + c.Name)) return "Layout: constant " + c.Group + "." + c.Name + " twice";

            if (LayoutHash.Compiled != LayoutHash.PinnedLayout10)
                return "Layout: compiled layout hash " + LayoutHash.Format(LayoutHash.Compiled) + " != pinned " + LayoutHash.Format(LayoutHash.PinnedLayout10);
            return string.Empty;
        }

        private static int FieldStorageSize(FieldInfo fi)
        {
            // Fixed buffers: the compiler emits a nested struct of the buffer's full size.
            return Marshal.SizeOf(fi.FieldType);
        }

        /// <summary>Pointer to a region's first byte.</summary>
        public static byte* RegionPtr(byte* sectionBase, RegionId id)
        {
            return sectionBase + Region(id).Offset;
        }
    }
}
