using System;
using System.Runtime.InteropServices;
using System.Text;

// Wire records of docs/CONTRACT.md §5.4 (layout 1.0). Every record is StructLayout(Explicit, Pack = 1) with an explicit
// Size and a FieldOffset per field, so Marshal.SizeOf == sizeof == the CONTRACT size on.NET Framework and Mono alike.
// Field names are CONTRACT's names verbatim (camelCase on purpose: they are also the names in the layout-hash text);
// the only renames are `ω` -> `omega` (the canonical text is ASCII) and the u16-prefixed strings, which are split into
// `<name>Length` (u16) + `<name>` (bytes) inside fixed-size records. Pads are explicit private fields named
// `pad_<hex offset>`; Layout.cs holds the declarative field table that the layout hash and Layout.Validate use.
#pragma warning disable 0169, 0649 // explicit pads are never read or written on purpose

namespace DeepEngineers.Wire
{
    // ---------------------------------------------------------------------------------------------------------------
 // Small fixed arrays (CONTRACT `f32 x[n]`, `i32 x[n]`, `u8 x[n]`).
 // ---------------------------------------------------------------------------------------------------------------

 /// <summary>f32[2].</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8, Pack = 1)]
    public struct F32x2
    {
        [FieldOffset(0)] public float x;
        [FieldOffset(4)] public float y;
        public F32x2(float x, float y) { this.x = x; this.y = y; }
    }

    /// <summary>f32[3] (positions, directions, colours, HSV).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 12, Pack = 1)]
    public struct F32x3
    {
        [FieldOffset(0)] public float x;
        [FieldOffset(4)] public float y;
        [FieldOffset(8)] public float z;
        public F32x3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    /// <summary>f32[4]; quaternions are (x, y, z, w) (CONTRACT §6).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct F32x4
    {
        [FieldOffset(0)] public float x;
        [FieldOffset(4)] public float y;
        [FieldOffset(8)] public float z;
        [FieldOffset(12)] public float w;
        public F32x4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }

    /// <summary>f32[6].</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24, Pack = 1)]
    public unsafe struct F32x6
    {
        [FieldOffset(0)] public fixed float e[6];
        public float this[int i]
        {
            get { if ((uint)i >= 6u) throw new IndexOutOfRangeException(); return e[i]; }
            set { if ((uint)i >= 6u) throw new IndexOutOfRangeException(); e[i] = value; }
        }
    }

    /// <summary>f32[9] (3×3 rows).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 36, Pack = 1)]
    public unsafe struct F32x9
    {
        [FieldOffset(0)] public fixed float e[9];
        public float this[int i]
        {
            get { if ((uint)i >= 9u) throw new IndexOutOfRangeException(); return e[i]; }
            set { if ((uint)i >= 9u) throw new IndexOutOfRangeException(); e[i] = value; }
        }
    }

    /// <summary>f32[12]: an SE matrix as rows Right, Up, Backward, Translation (CONTRACT §6).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48, Pack = 1)]
    public unsafe struct F32x12
    {
        [FieldOffset(0)] public fixed float e[12];
        public float this[int i]
        {
            get { if ((uint)i >= 12u) throw new IndexOutOfRangeException(); return e[i]; }
            set { if ((uint)i >= 12u) throw new IndexOutOfRangeException(); e[i] = value; }
        }
    }

    /// <summary>i32[3].</summary>
    [StructLayout(LayoutKind.Explicit, Size = 12, Pack = 1)]
    public struct I32x3
    {
        [FieldOffset(0)] public int x;
        [FieldOffset(4)] public int y;
        [FieldOffset(8)] public int z;
        public I32x3(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
    }

    /// <summary>i32[4] (window rectangles: left, top, right, bottom).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct I32x4
    {
        [FieldOffset(0)] public int x;
        [FieldOffset(4)] public int y;
        [FieldOffset(8)] public int z;
        [FieldOffset(12)] public int w;
        public I32x4(int x, int y, int z, int w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }

    /// <summary>u8[4].</summary>
    [StructLayout(LayoutKind.Explicit, Size = 4, Pack = 1)]
    public struct U8x4
    {
        [FieldOffset(0)] public byte x;
        [FieldOffset(1)] public byte y;
        [FieldOffset(2)] public byte z;
        [FieldOffset(3)] public byte w;
    }

    // ---------------------------------------------------------------------------------------------------------------
 // C0 header (0x000-0x0FF; the region table follows at 0x100) and the ring-control page.
 // ---------------------------------------------------------------------------------------------------------------

 /// <summary>C0 header incl. the SN block (0x040) and the SE block (0x080). Each side writes only its own block.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x100, Pack = 1)]
    public unsafe struct SectionHeader
    {
        [FieldOffset(0x000)] public uint magic;
        [FieldOffset(0x004)] public ushort layoutMajor;
        [FieldOffset(0x006)] public ushort layoutMinor;
        [FieldOffset(0x008)] public uint byteOrderMark;
        [FieldOffset(0x00C)] public uint headerSize;
        [FieldOffset(0x010)] public ulong sectionSize;
        [FieldOffset(0x018)] public ulong layoutHash;
        [FieldOffset(0x020)] public uint wireVersion;
        [FieldOffset(0x024)] public uint siteVersion;
        [FieldOffset(0x028)] public uint damageScaleVersion;
        [FieldOffset(0x02C)] public uint regionCount;
        [FieldOffset(0x030)] public ulong qpcFrequency;
        [FieldOffset(0x038)] public uint devControl;
        [FieldOffset(0x03C)] private uint pad_3c;
        // SN block
        [FieldOffset(0x040)] public uint snPid;
        [FieldOffset(0x044)] public uint snGeneration;
        [FieldOffset(0x048)] public ulong snHeartbeatMs;
        [FieldOffset(0x050)] public uint snPhase;
        [FieldOffset(0x054)] public uint snFlags;
        [FieldOffset(0x058)] public ulong snHwnd;
        [FieldOffset(0x060)] private fixed byte pad_60[0x20];
        // SE block
        [FieldOffset(0x080)] public uint sePid;
        [FieldOffset(0x084)] public uint seGeneration;
        [FieldOffset(0x088)] public ulong seHeartbeatMs;
        [FieldOffset(0x090)] public uint sePhase;
        [FieldOffset(0x094)] public uint seFlags;
        [FieldOffset(0x098)] public ulong seHwnd;
        [FieldOffset(0x0A0)] public uint seLoadStep;
        [FieldOffset(0x0A4)] private fixed byte pad_a4[0x5C];
    }

    /// <summary>One region-table entry (C0 0x100 + i × 32).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32, Pack = 1)]
    public struct RegionEntry
    {
        [FieldOffset(0x00)] public uint id;
        [FieldOffset(0x04)] public uint kind;
        [FieldOffset(0x08)] public ulong offset;
        [FieldOffset(0x10)] public ulong size;
        [FieldOffset(0x18)] public uint recordSize;
        [FieldOffset(0x1C)] public uint version;
    }

    /// <summary>One ring's control entry (0x1000 + r × 0x80): head and tail on separate 64-byte lines.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x80, Pack = 1)]
    public unsafe struct RingControlEntry
    {
        [FieldOffset(0x00)] public ulong head;
        [FieldOffset(0x08)] private fixed byte pad_8[0x38];
        [FieldOffset(0x40)] public ulong tail;
        [FieldOffset(0x48)] private fixed byte pad_48[0x38];
    }

    // ---------------------------------------------------------------------------------------------------------------
 // Seqlock records (seq at 0x00).
 // ---------------------------------------------------------------------------------------------------------------

 /// <summary>C1 SeFrame fixed part (0x000-0x0FF); bones (SeBone × boneCount) follow at 0x100.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x100, Pack = 1)]
    public unsafe struct SeFrame
    {
        [FieldOffset(0x00)] public uint seq;
        [FieldOffset(0x04)] public uint flags;
        [FieldOffset(0x08)] public ulong tick;
        [FieldOffset(0x10)] public long qpcCapture;
        [FieldOffset(0x18)] public double simTimeSec;
        [FieldOffset(0x20)] public float simSpeed;
        [FieldOffset(0x24)] public uint worldEpoch;
        [FieldOffset(0x28)] public F32x3 camPos;
        [FieldOffset(0x34)] public F32x4 camRot;
        [FieldOffset(0x44)] public float vFovDeg;
        [FieldOffset(0x48)] public float near;
        [FieldOffset(0x4C)] public float aspect;
        [FieldOffset(0x50)] public uint cameraMode;
        [FieldOffset(0x54)] private uint pad_54;
        [FieldOffset(0x58)] public ulong characterId;
        [FieldOffset(0x60)] public F32x3 charO;
        [FieldOffset(0x6C)] public F32x4 charQ;
        [FieldOffset(0x7C)] public float charS;
        [FieldOffset(0x80)] public uint boneCount;
        [FieldOffset(0x84)] public uint bonesVersion;
        [FieldOffset(0x88)] public F32x3 pelvisSn;
        [FieldOffset(0x94)] private fixed byte pad_94[0x6C];
    }

    /// <summary>C1 bone: 12 f32, SE model-space rows. Also the C3 SkinnedBind bind matrix.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48, Pack = 1)]
    public struct SeBone
    {
        [FieldOffset(0x00)] public F32x12 rows;
    }

    /// <summary>C2 SeEntities header (0x00-0x3F); entities at 0x40, subparts at 0x18040, lights at 0x38040.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x40, Pack = 1)]
    public unsafe struct SeEntitiesHeader
    {
        [FieldOffset(0x00)] public uint seq;
        [FieldOffset(0x04)] public uint entityCount;
        [FieldOffset(0x08)] public ulong tick;
        [FieldOffset(0x10)] public uint worldEpoch;
        [FieldOffset(0x14)] public uint subpartCount;
        [FieldOffset(0x18)] public uint lightCount;
        [FieldOffset(0x1C)] private fixed byte pad_1c[0x24];
    }

    /// <summary>C2 entity (96 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 96, Pack = 1)]
    public struct SeEntity
    {
        [FieldOffset(0x00)] public ulong entityId;
        [FieldOffset(0x08)] public uint kind;
        [FieldOffset(0x0C)] public uint flags;
        [FieldOffset(0x10)] public ulong groupId;
        [FieldOffset(0x18)] public F32x3 o;
        [FieldOffset(0x24)] public F32x4 q;
        [FieldOffset(0x34)] public float s;
        [FieldOffset(0x38)] public float k;
        [FieldOffset(0x3C)] public F32x3 v;
        /// <summary>CONTRACT `ω`.</summary>
        [FieldOffset(0x48)] public F32x3 omega;
        [FieldOffset(0x54)] public uint partsVersion;
        [FieldOffset(0x58)] public uint skinVersion;
        [FieldOffset(0x5C)] private uint pad_5c;
    }

    /// <summary>C2 subpart (64 B); a subgrid is a subpart with ownerId = its root grid.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64, Pack = 1)]
    public struct SeSubpart
    {
        [FieldOffset(0x00)] public ulong ownerId;
        [FieldOffset(0x08)] public ulong partKey;
        [FieldOffset(0x10)] public F32x12 local;
    }

    /// <summary>C2 light (64 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64, Pack = 1)]
    public struct SeLight
    {
        [FieldOffset(0x00)] public ulong ownerId;
        [FieldOffset(0x08)] public F32x3 pos;
        [FieldOffset(0x14)] public F32x3 dir;
        [FieldOffset(0x20)] public F32x3 colour;
        [FieldOffset(0x2C)] public float range;
        [FieldOffset(0x30)] public float intensity;
        [FieldOffset(0x34)] public float coneDeg;
        [FieldOffset(0x38)] public uint flags;
        [FieldOffset(0x3C)] private uint pad_3c;
    }

    /// <summary>C5 SeState (256 B), incl. the hello fields (Rs, siteHash, siteVersion, helloEpoch) and linkEpoch.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256, Pack = 1)]
    public unsafe struct SeState
    {
        [FieldOffset(0x00)] public uint seq;
        [FieldOffset(0x04)] public uint flags;
        [FieldOffset(0x08)] public ulong tick;
        [FieldOffset(0x10)] public uint viewState;
        [FieldOffset(0x14)] public uint approachMode;
        [FieldOffset(0x18)] public uint displayMode;
        [FieldOffset(0x1C)] public uint overlays;
        [FieldOffset(0x20)] public uint zone;
        [FieldOffset(0x24)] public uint worldEpoch;
        [FieldOffset(0x28)] public uint perfMode;
        [FieldOffset(0x2C)] public uint targetFps;
        [FieldOffset(0x30)] public float snBusTarget;
        [FieldOffset(0x34)] public float crossfadeMs;
        [FieldOffset(0x38)] public uint helloEpoch;
        [FieldOffset(0x3C)] public uint linkEpoch;
        [FieldOffset(0x40)] public double Rs;
        [FieldOffset(0x48)] public ulong siteHash;
        [FieldOffset(0x50)] public uint siteVersion;
        [FieldOffset(0x54)] private uint pad_54;
        [FieldOffset(0x58)] public I32x4 seRect;
        [FieldOffset(0x68)] public float camSnY;
        [FieldOffset(0x6C)] public F32x3 camVelSn;
        [FieldOffset(0x78)] public ulong crossingTick;
        [FieldOffset(0x80)] private fixed byte pad_80[0x80];
    }

    /// <summary>C6 SeTime (64 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64, Pack = 1)]
    public struct SeTime
    {
        [FieldOffset(0x00)] public uint seq;
        [FieldOffset(0x04)] public uint jumpSeq;
        [FieldOffset(0x08)] public ulong tick;
        [FieldOffset(0x10)] public double hourAngleRad;
        [FieldOffset(0x18)] public float sunElevationRad;
        [FieldOffset(0x1C)] public F32x3 sunDirSn;
        [FieldOffset(0x28)] public float dayLengthSec;
        [FieldOffset(0x2C)] public float simSpeed;
        [FieldOffset(0x30)] public uint paused;
        [FieldOffset(0x34)] public uint flags;
        [FieldOffset(0x38)] public float sunriseHourAngleRad;
        [FieldOffset(0x3C)] private uint pad_3c;
    }

    /// <summary>C7 SnStatus (256 B), incl. the hello ack (helloAckEpoch, hostKind, capabilities).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256, Pack = 1)]
    public unsafe struct SnStatus
    {
        [FieldOffset(0x00)] public uint seq;
        [FieldOffset(0x04)] public uint flags;
        [FieldOffset(0x08)] public uint snPhase;
        [FieldOffset(0x0C)] public uint perfModeApplied;
        [FieldOffset(0x10)] public ulong snFrame;
        [FieldOffset(0x18)] public ulong presentedSeTick;
        [FieldOffset(0x20)] public ulong areaBuiltSeTick;
        [FieldOffset(0x28)] public F32x3 areaBuiltPos;
        [FieldOffset(0x34)] public uint helloAckEpoch;
        [FieldOffset(0x38)] public uint linkAckEpoch;
        [FieldOffset(0x3C)] public uint mirroredEntityCount;
        [FieldOffset(0x40)] public uint pendingParts;
        [FieldOffset(0x44)] public uint damageDropped;
        [FieldOffset(0x48)] public ulong c3BytesDrained;
        [FieldOffset(0x50)] public uint damageOverflowDepth;
        [FieldOffset(0x54)] public float frameMsP50;
        [FieldOffset(0x58)] public float frameMsP99;
        [FieldOffset(0x5C)] public uint hostKind;
        [FieldOffset(0x60)] public ulong snHwnd;
        [FieldOffset(0x68)] public I32x4 snRect;
        [FieldOffset(0x78)] public uint capabilities;
        [FieldOffset(0x7C)] public uint vSyncCountApplied;
        [FieldOffset(0x80)] public int targetFrameRateApplied;
        [FieldOffset(0x84)] public float fixedDeltaTime;
        [FieldOffset(0x88)] public uint resamplesLastFrame;
        [FieldOffset(0x8C)] private fixed byte pad_8c[0x74];
    }

    /// <summary>C8 damage record (128 B), a slot of the SPSC fixed-record ring.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128, Pack = 1)]
    public unsafe struct DamageRecord
    {
        [FieldOffset(0x00)] public uint dedupSeq;
        [FieldOffset(0x04)] public uint snGeneration;
        [FieldOffset(0x08)] public uint unit;
        [FieldOffset(0x0C)] public float amount;
        [FieldOffset(0x10)] public float seScaleExpected;
        [FieldOffset(0x14)] public ushort snDamageType;
        [FieldOffset(0x16)] public ushort flags;
        [FieldOffset(0x18)] public uint targetKind;
        [FieldOffset(0x1C)] public uint sectionId;
        [FieldOffset(0x20)] public ulong targetEntityId;
        [FieldOffset(0x28)] public ulong rootEntityId;
        [FieldOffset(0x30)] public ulong snapshotTick;
        [FieldOffset(0x38)] public F32x3 localHit;
        [FieldOffset(0x44)] public F32x3 snWorldHit;
        [FieldOffset(0x50)] public F32x3 attackDirSn;
        [FieldOffset(0x5C)] public int dealerTechType;
        [FieldOffset(0x60)] public uint dealerInstanceId;
        [FieldOffset(0x64)] public uint worldEpoch;
        [FieldOffset(0x68)] public double snTimeSec;
        [FieldOffset(0x70)] private fixed byte pad_70[0x10];
    }

    /// <summary>C9 SnCreatures header (0x00-0x3F); creature records (48 B) follow at 0x40.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 0x40, Pack = 1)]
    public unsafe struct SnCreaturesHeader
    {
        [FieldOffset(0x00)] public uint seq;
        [FieldOffset(0x04)] public uint count;
        [FieldOffset(0x08)] public ulong snFrame;
        [FieldOffset(0x10)] private fixed byte pad_10[0x30];
    }

    /// <summary>C9 creature record (48 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48, Pack = 1)]
    public struct SnCreature
    {
        [FieldOffset(0x00)] public uint instanceId;
        [FieldOffset(0x04)] public int techType;
        [FieldOffset(0x08)] public F32x3 posSn;
        [FieldOffset(0x14)] public F32x3 velSn;
        [FieldOffset(0x20)] public float aggression;
        [FieldOffset(0x24)] public uint flags;
        [FieldOffset(0x28)] public ulong targetEntityId;
    }

    /// <summary>C10 capture request (+0x000, 256 B). The path is a u16-length-prefixed UTF-8 string (≤ 230 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256, Pack = 1)]
    public unsafe struct CaptureRequest
    {
        public const int PathCapacity = 230;
        [FieldOffset(0x00)] public uint seq;
        [FieldOffset(0x04)] public uint reqId;
        [FieldOffset(0x08)] public uint kind;
        [FieldOffset(0x0C)] private uint pad_c;
        [FieldOffset(0x10)] public ulong atSeTick;
        [FieldOffset(0x18)] public ushort pathLength;
        [FieldOffset(0x1A)] public fixed byte path[PathCapacity];

        /// <summary>Stores a UTF-8 path; false (record unchanged) when it does not fit.</summary>
        public bool SetPath(string value)
        {
            fixed (byte* p = path) { return WireStrings.TryStore(value, p, PathCapacity, ref pathLength); }
        }

        public string GetPath()
        {
            fixed (byte* p = path) { return WireStrings.Load(p, PathCapacity, pathLength); }
        }
    }

    /// <summary>C10 capture result (+0x800, 256 B). The path is a u16-length-prefixed UTF-8 string (≤ 222 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256, Pack = 1)]
    public unsafe struct CaptureResult
    {
        public const int PathCapacity = 222;
        [FieldOffset(0x00)] public uint seq;
        [FieldOffset(0x04)] public uint reqId;
        [FieldOffset(0x08)] public uint status;
        [FieldOffset(0x0C)] public float alpha;
        [FieldOffset(0x10)] public ulong snFrame;
        [FieldOffset(0x18)] public ulong seTickUsed;
        [FieldOffset(0x20)] public ushort pathLength;
        [FieldOffset(0x22)] public fixed byte path[PathCapacity];

        public bool SetPath(string value)
        {
            fixed (byte* p = path) { return WireStrings.TryStore(value, p, PathCapacity, ref pathLength); }
        }

        public string GetPath()
        {
            fixed (byte* p = path) { return WireStrings.Load(p, PathCapacity, pathLength); }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
 // Byte-ring framing (C3, C4, C11, C12) and message payloads.
 // ---------------------------------------------------------------------------------------------------------------

 /// <summary>Byte-ring message header {u32 type, u32 payloadBytes}; payload follows, padded to 8 bytes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8, Pack = 1)]
    public struct MessageHeader
    {
        [FieldOffset(0x00)] public uint type;
        [FieldOffset(0x04)] public uint payloadBytes;
    }

    /// <summary>C3 type 1 ModelBegin (64 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64, Pack = 1)]
    public struct ModelBegin
    {
        [FieldOffset(0x00)] public ulong modelKey;
        [FieldOffset(0x08)] public uint lod;
        [FieldOffset(0x0C)] public uint vertexCount;
        [FieldOffset(0x10)] public uint indexCount;
        [FieldOffset(0x14)] public uint submeshCount;
        [FieldOffset(0x18)] public uint flags;
        [FieldOffset(0x1C)] public F32x6 bounds;
        [FieldOffset(0x34)] public float patternScale;
        [FieldOffset(0x38)] public uint chunkCount;
        [FieldOffset(0x3C)] public uint totalBytes;
    }

    /// <summary>C3 type 2 ModelChunk header (16 B); `bytes` payload bytes follow at 0x10.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct ModelChunkHeader
    {
        [FieldOffset(0x00)] public ulong modelKey;
        [FieldOffset(0x08)] public uint chunkIndex;
        [FieldOffset(0x0C)] public uint bytes;
    }

    /// <summary>C3 type 3 ModelEnd (16 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct ModelEnd
    {
        [FieldOffset(0x00)] public ulong modelKey;
        [FieldOffset(0x08)] public ulong payloadHash;
    }

    /// <summary>C3 type 4 MaterialDef header (16 B); four u16-prefixed UTF-8 strings follow at 0x10.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct MaterialDefHeader
    {
        [FieldOffset(0x00)] public ulong materialKey;
        [FieldOffset(0x08)] public uint drawTechnique;
        [FieldOffset(0x0C)] public uint flags;
    }

    /// <summary>C3 type 5 SkinnedBind header (16 B); boneCount × SeBone follow at 0x10.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct SkinnedBindHeader
    {
        [FieldOffset(0x00)] public ulong modelKey;
        [FieldOffset(0x08)] public uint boneCount;
        [FieldOffset(0x0C)] private uint pad_c;
    }

    /// <summary>C3 model payload vertex (56 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 56, Pack = 1)]
    public struct ModelVertex
    {
        [FieldOffset(0x00)] public F32x3 pos;
        [FieldOffset(0x0C)] public F32x3 normal;
        [FieldOffset(0x18)] public F32x3 tangent;
        [FieldOffset(0x24)] public F32x3 binormal;
        [FieldOffset(0x30)] public F32x2 uv;
    }

    /// <summary>C3 model payload vertex of a skinned model (76 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 76, Pack = 1)]
    public struct ModelVertexSkinned
    {
        [FieldOffset(0x00)] public F32x3 pos;
        [FieldOffset(0x0C)] public F32x3 normal;
        [FieldOffset(0x18)] public F32x3 tangent;
        [FieldOffset(0x24)] public F32x3 binormal;
        [FieldOffset(0x30)] public F32x2 uv;
        [FieldOffset(0x38)] public U8x4 boneIndex;
        [FieldOffset(0x3C)] public F32x4 weight;
    }

    /// <summary>C3 model payload submesh (24 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24, Pack = 1)]
    public struct ModelSubmesh
    {
        [FieldOffset(0x00)] public ulong materialKey;
        [FieldOffset(0x08)] public uint triStart;
        [FieldOffset(0x0C)] public uint triCount;
        [FieldOffset(0x10)] public uint drawTechnique;
        [FieldOffset(0x14)] private uint pad_14;
    }

    /// <summary>C4 type 1 GridAdd (32 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32, Pack = 1)]
    public struct GridAdd
    {
        [FieldOffset(0x00)] public ulong entityId;
        [FieldOffset(0x08)] public uint targetClass;
        [FieldOffset(0x0C)] public uint flags;
        [FieldOffset(0x10)] public ulong parentEntityId;
        [FieldOffset(0x18)] public uint worldEpoch;
        [FieldOffset(0x1C)] private uint pad_1c;
    }

    /// <summary>C4 type 2 EntityRemove (8 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8, Pack = 1)]
    public struct EntityRemove
    {
        [FieldOffset(0x00)] public ulong entityId;
    }

    /// <summary>C4 type 3 PartAdd (100 B payload, 108 B framed, 112 B padded).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 100, Pack = 1)]
    public struct PartAdd
    {
        [FieldOffset(0x00)] public ulong entityId;
        [FieldOffset(0x08)] public ulong partKey;
        [FieldOffset(0x10)] public ulong modelKey;
        [FieldOffset(0x18)] public F32x12 local;
        [FieldOffset(0x48)] public F32x3 colorMaskHSV;
        [FieldOffset(0x54)] public F32x2 uvOffset;
        [FieldOffset(0x5C)] public uint skinId;
        [FieldOffset(0x60)] public uint flags;
    }

    /// <summary>C4 type 4 PartRemove (16 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct PartRemove
    {
        [FieldOffset(0x00)] public ulong entityId;
        [FieldOffset(0x08)] public ulong partKey;
    }

    /// <summary>C4 type 5 PartRecolor (32 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32, Pack = 1)]
    public struct PartRecolor
    {
        [FieldOffset(0x00)] public ulong entityId;
        [FieldOffset(0x08)] public ulong partKey;
        [FieldOffset(0x10)] public F32x3 hsv;
        [FieldOffset(0x1C)] private uint pad_1c;
    }

    /// <summary>C4 type 6 SkinSet header (16 B); sectionCount × (SkinSection + boxCount × SkinBox) follow.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct SkinSetHeader
    {
        [FieldOffset(0x00)] public ulong entityId;
        [FieldOffset(0x08)] public uint skinVersion;
        [FieldOffset(0x0C)] public uint sectionCount;
    }

    /// <summary>C4 SkinSet section header (24 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24, Pack = 1)]
    public struct SkinSection
    {
        [FieldOffset(0x00)] public uint sectionId;
        [FieldOffset(0x04)] public F32x3 centre;
        [FieldOffset(0x10)] public uint boxCount;
        [FieldOffset(0x14)] public uint kind;
    }

    /// <summary>C4 SkinSet box (64 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64, Pack = 1)]
    public struct SkinBox
    {
        [FieldOffset(0x00)] public F32x3 centre;
        [FieldOffset(0x0C)] public F32x3 half;
        [FieldOffset(0x18)] public F32x9 axes;
        [FieldOffset(0x3C)] public uint flags;
    }

    /// <summary>C4 type 7 RoomSet header (16 B); count × RoomBox follow.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct RoomSetHeader
    {
        [FieldOffset(0x00)] public ulong entityId;
        [FieldOffset(0x08)] public uint count;
        [FieldOffset(0x0C)] private uint pad_c;
    }

    /// <summary>C4 RoomSet airtight-room AABB in grid cells (24 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24, Pack = 1)]
    public struct RoomBox
    {
        [FieldOffset(0x00)] public I32x3 min;
        [FieldOffset(0x0C)] public I32x3 max;
    }

    /// <summary>C4 type 8 CharacterAdd (24 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24, Pack = 1)]
    public struct CharacterAdd
    {
        [FieldOffset(0x00)] public ulong entityId;
        [FieldOffset(0x08)] public ulong modelKey;
        [FieldOffset(0x10)] public uint flags;
        [FieldOffset(0x14)] public uint boneCount;
    }

    /// <summary>C4 type 9 ClearAll (8 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 8, Pack = 1)]
    public struct ClearAll
    {
        [FieldOffset(0x00)] public uint worldEpoch;
        [FieldOffset(0x04)] public uint linkEpoch;
    }

    /// <summary>C12 type 1 BatchStatics header (16 B); count × StaticPrimitive follow at 0x10.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct BatchStaticsHeader
    {
        [FieldOffset(0x00)] public short bx;
        [FieldOffset(0x02)] public short by;
        [FieldOffset(0x04)] public short bz;
        [FieldOffset(0x06)] private ushort pad_6;
        [FieldOffset(0x08)] public uint epoch;
        [FieldOffset(0x0C)] public uint count;
    }

    /// <summary>C12 static primitive (48 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48, Pack = 1)]
    public struct StaticPrimitive
    {
        [FieldOffset(0x00)] public byte kind;
        [FieldOffset(0x01)] public byte flags;
        [FieldOffset(0x02)] private ushort pad_2;
        [FieldOffset(0x04)] public F32x3 centreSn;
        [FieldOffset(0x10)] public F32x4 qSn;
        [FieldOffset(0x20)] public F32x3 size;
        [FieldOffset(0x2C)] public uint classIdHash;
    }

    /// <summary>C12 type 2 BatchClear (16 B).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16, Pack = 1)]
    public struct BatchClear
    {
        [FieldOffset(0x00)] public short bx;
        [FieldOffset(0x02)] public short by;
        [FieldOffset(0x04)] public short bz;
        [FieldOffset(0x06)] private ushort pad_6;
        [FieldOffset(0x08)] public uint epoch;
        [FieldOffset(0x0C)] private uint pad_c;
    }

    // ---------------------------------------------------------------------------------------------------------------
 // Enumerations and flag bits (CONTRACT §5.4). Flags listed in CONTRACT as "(a, b, c)" without bit numbers take
 // bits 0, 1, 2 in the listed order.
 // ---------------------------------------------------------------------------------------------------------------

 /// <summary>C0 snPhase. Loading phases: 1-3.</summary>
    public static class SnPhase
    {
        public const uint None = 0, Booting = 1, Menu = 2, LoadingSave = 3, ReadyIdle = 4, WarmingUp = 5, Active = 6, Quitting = 7;
        public static bool IsLoading(uint phase) { return phase >= Booting && phase <= LoadingSave; }
        public static string Name(uint phase)
        {
            switch (phase)
            {
                case None: return "None"; case Booting: return "Booting"; case Menu: return "Menu";
                case LoadingSave: return "LoadingSave"; case ReadyIdle: return "ReadyIdle"; case WarmingUp: return "WarmingUp";
                case Active: return "Active"; case Quitting: return "Quitting"; default: return "Unknown(" + phase + ")";
            }
        }
    }

    /// <summary>C0 sePhase. Loading phases: 2 and 4.</summary>
    public static class SePhase
    {
        public const uint None = 0, Menu = 1, LoadingWorld = 2, InWorld = 3, Saving = 4, Exiting = 5;
        public static bool IsLoading(uint phase) { return phase == LoadingWorld || phase == Saving; }
        public static string Name(uint phase)
        {
            switch (phase)
            {
                case None: return "None"; case Menu: return "Menu"; case LoadingWorld: return "LoadingWorld";
                case InWorld: return "InWorld"; case Saving: return "Saving"; case Exiting: return "Exiting";
                default: return "Unknown(" + phase + ")";
            }
        }
    }

    /// <summary>C0 snFlags.</summary>
    public static class SnFlags { public const uint BridgeMode = 1u << 0, GhostingDisabled = 1u << 1, StylesApplied = 1u << 2; }

    /// <summary>C0 seFlags.</summary>
    public static class SeFlags { public const uint GateOpen = 1u << 0, TerrainUp = 1u << 1, DisplayRefused = 1u << 2, FailSafeActive = 1u << 3; }

    /// <summary>C0 devControl (0x038): bit 0 a Python harness produces CtlToSe/CtlToSn; bit 1 SE relays /de to CtlToSn. Exclusive.</summary>
    public static class DevControl { public const uint HarnessAttached = 1u << 0, SeChatRelay = 1u << 1; }

    /// <summary>C1 flags.</summary>
    public static class SeFrameFlags
    {
        public const uint InWorld = 1u << 0, Paused = 1u << 1, FirstPerson = 1u << 2, CameraInAirtightRoom = 1u << 3,
            Teleported = 1u << 4, CharacterPresent = 1u << 5, CharacterDead = 1u << 6, CharacterSeated = 1u << 7,
            CharacterSwimming = 1u << 8;
    }

    /// <summary>C1 cameraMode.</summary>
    public static class CameraMode { public const uint Other = 0, FirstPerson = 1, ThirdPerson = 2, Cockpit = 3, Spectator = 4; }

    /// <summary>C1 bones: at most 160 (the astronaut has 81), 48 B each, from 0x100, ending ≤ 0x1F00.</summary>
    public static class SeFrameLimits { public const int BonesOffset = 0x100, MaxBones = 160, BoneSize = 48; }

    /// <summary>C2 entity kind and flags; table limits and offsets.</summary>
    public static class SeEntityKind { public const uint RootGrid = 1, Character = 2, FloatingObject = 3; }
    public static class SeEntityFlags { public const uint Static = 1u << 0, InCameraGroup = 1u << 1; }
    public static class SeLightFlags { public const uint Spot = 1u << 0, Shadows = 1u << 1, On = 1u << 2; }
    public static class SeEntitiesLimits
    {
        public const int EntitiesOffset = 0x40, MaxEntities = 1024, EntitySize = 96;
        public const int SubpartsOffset = 0x18040, MaxSubparts = 2048, SubpartSize = 64;
        public const int LightsOffset = 0x38040, MaxLights = 64, LightSize = 64;
        public const int End = 0x39040;
    }

    /// <summary>C5 flags and enumerations.</summary>
    public static class SeStateFlags
    {
        public const uint SeForeground = 1u << 0, SePaused = 1u << 1, SeExiting = 1u << 2, SeeThrough = 1u << 3,
            CameraInAirtightRoom = 1u << 4, MarginsOn = 1u << 5, ZOrderOk = 1u << 6, SeMixActive = 1u << 7,
            GuiScreenOpen = 1u << 8;
    }
    public static class ViewState { public const uint Space = 0, Approach = 1, Underwater = 2, Surfacing = 3; }
    public static class ApproachMode { public const uint None = 0, Track = 1, Prime = 2; }
    public static class DisplayMode { public const uint F = 0, APrime = 1, C = 2; }
    public static class Overlays { public const uint Paused = 1u << 0, Unfocused = 1u << 1, PeerStalled = 1u << 2, PeerExited = 1u << 3; }
    public static class Zone { public const uint Core = 0, TileVoid = 1, OuterVoid = 2, Far = 3, OffSite = 4; }
    public static class PerfMode { public const uint Idle = 0, Track = 1, Full = 2; }

    /// <summary>C6 flags.</summary>
    public static class SeTimeFlags { public const uint TimeSyncValid = 1u << 0, SkyOnly = 1u << 1; }

    /// <summary>C7 flags, hostKind and capabilities.</summary>
    public static class SnStatusFlags
    {
        public const uint WorldLoaded = 1u << 0, StreamingSettled = 1u << 1, CombatEnabled = 1u << 2,
            WireSelfTestPass = 1u << 3, AreaBuilt = 1u << 4, RectOk = 1u << 5, UiHidden = 1u << 6, Frozen = 1u << 7,
            RunInBackgroundForced = 1u << 8, TimeSyncApplied = 1u << 9, DamageSelfTestPass = 1u << 10;
    }
    public static class HostKind { public const uint Subnautica1 = 1; }
    public static class Capabilities
    {
        public const uint UnderwaterPicture = 1u << 0, CreaturesAndDamage = 1u << 1, Statics = 1u << 2,
            TimeOfDay = 1u << 3, Capture = 1u << 4;
    }

    /// <summary>C8 unit, target kind, flags; ring capacity.</summary>
    public static class DamageUnit { public const uint SnHp = 1; }
    public static class DamageTargetKind { public const uint GridSection = 1, Character = 2; }
    public static class DamageFlags { public const ushort MouthContact = 1 << 0, Radius = 1 << 1, FallbackHitPoint = 1 << 2, FrameSubgrid = 1 << 3; }

    /// <summary>C9 creature flags and table limits.</summary>
    public static class SnCreatureFlags { public const uint Hunting = 1u << 0, TargetIsMirror = 1u << 1; }
    public static class SnCreaturesLimits { public const int RecordsOffset = 0x40, MaxCreatures = 256, RecordSize = 48; }

    /// <summary>C10 kinds, statuses and offsets inside the region.</summary>
    public static class CaptureKind { public const uint NextFramePng = 1, AtSeTick = 2; }
    public static class CaptureStatus { public const uint Pending = 0, Ok = 1, Failed = 2; }
    public static class CaptureLayout { public const int RequestOffset = 0x000, ResultOffset = 0x800; }

    /// <summary>C3 message types and flags. Type 0 is the ring pad record on every byte ring.</summary>
    public static class C3Type
    {
        public const uint Pad = 0, ModelBegin = 1, ModelChunk = 2, ModelEnd = 3, MaterialDef = 4, SkinnedBind = 5;
        /// <summary>CONTRACT §5.1: chunks ≤ 4 MiB.</summary>
        public const int MaxChunkBytes = 4 << 20;
    }
    public static class ModelFlags { public const uint Skinned = 1u << 0, HasTangents = 1u << 1; }
    public static class MaterialFlags { public const uint GlassCw = 1u << 0, GlassCcw = 1u << 1, AlphaMasked = 1u << 2; }

    /// <summary>C4 message types and enumerations.</summary>
    public static class C4Type
    {
        public const uint Pad = 0, GridAdd = 1, EntityRemove = 2, PartAdd = 3, PartRemove = 4, PartRecolor = 5,
            SkinSet = 6, RoomSet = 7, CharacterAdd = 8, ClearAll = 9;
    }
    public static class GridClass { public const uint LargeGrid = 1, SmallGrid = 2; }
    public static class SkinSectionKind { public const uint Grid = 1, Character = 2; }

    /// <summary>C11 message type (JSON line, UTF-8).</summary>
    public static class C11Type { public const uint Pad = 0, Json = 1; }

    /// <summary>C12 message types and primitive kinds.</summary>
    public static class C12Type { public const uint Pad = 0, BatchStatics = 1, BatchClear = 2; }
    public static class StaticKind { public const byte Box = 1, Capsule = 2, Sphere = 3, MeshObb = 4; }

    /// <summary>u16-length-prefixed UTF-8 strings inside fixed-size records.</summary>
    public static unsafe class WireStrings
    {
        public static bool TryStore(string value, byte* dst, int capacity, ref ushort length)
        {
            if (value == null) value = string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > capacity || bytes.Length > ushort.MaxValue) return false;
            for (int i = 0; i < bytes.Length; i++) dst[i] = bytes[i];
            for (int i = bytes.Length; i < capacity; i++) dst[i] = 0;
            length = (ushort)bytes.Length;
            return true;
        }

        public static string Load(byte* src, int capacity, ushort length)
        {
            int n = length > capacity ? capacity : length;
            if (n == 0) return string.Empty;
            byte[] bytes = new byte[n];
            for (int i = 0; i < n; i++) bytes[i] = src[i];
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Bytes a u16-prefixed string takes inside a variable-length payload (MaterialDef).</summary>
        public static int EncodedSize(string value)
        {
            return 2 + Encoding.UTF8.GetByteCount(value ?? string.Empty);
        }
    }
}
