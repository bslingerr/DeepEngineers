using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S5 stream recording (`.derec`, tests only; CONTRACT §5.4 "Stream recording"): the SE plugin's dev-build recorder
 // (`de.s5 record {name, c1, c2}`) writes what it put on the wire, the fake producer replays it (`--replay <name>`) or
 // writes synthesised ships in the same format (`--synth 500|2000|5000`, flag synthetic). Files live under
 // %LOCALAPPDATA%\DeepEngineers\recordings\<name>.derec (+ <name>.json), never in the repository or a game folder.
 // Little-endian. Header 64 B; records = a 24 B header {u16 kind, u16 pad, u32 bytes, u64 seTick, i64 qpc} + bytes,
 // padded to 8. This file is the codec only (no thread, no queue: the SE recorder owns its bounded queue and
 // background thread). Python twin: tools/worldmap/wire_ref.py (derec_header, derec_record), compared by S5VectorTests.
 // =================================================================================================================

 /// <summary>The 64-byte <c>.derec</c> header.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64, Pack = 1)]
    public unsafe struct DerecHeader
    {
        [FieldOffset(0x00)] public ulong magic;
        [FieldOffset(0x08)] public uint formatVersion;
        [FieldOffset(0x0C)] public uint headerBytes;
        [FieldOffset(0x10)] public ulong layoutHash;
        [FieldOffset(0x18)] public ulong conventionsHash;
        [FieldOffset(0x20)] public uint wireVersion;
        /// <summary>SE build as an integer (1210014); 0 = synthetic.</summary>
        [FieldOffset(0x24)] public uint seVersion;
        [FieldOffset(0x28)] public ulong createdUnixMs;
        /// <summary><see cref="Derec.FlagHasC1"/>, <see cref="Derec.FlagHasC2"/>, <see cref="Derec.FlagSynthetic"/>.</summary>
        [FieldOffset(0x30)] public uint flags;
        [FieldOffset(0x34)] public uint worldEpoch;
        [FieldOffset(0x38)] public ulong reserved;
    }

    /// <summary>The 24-byte header of one <c>.derec</c> record.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24, Pack = 1)]
    public struct DerecRecordHeader
    {
        [FieldOffset(0x00)] public ushort kind;
        [FieldOffset(0x02)] public ushort pad;
        [FieldOffset(0x04)] public uint bytes;
        [FieldOffset(0x08)] public ulong seTick;
        [FieldOffset(0x10)] public long qpc;
    }

    /// <summary>Constants and helpers of the <c>.derec</c> format.</summary>
    public static unsafe class Derec
    {
        /// <summary>"DEREC001" as the little-endian u64 at offset 0.</summary>
        public const ulong Magic = 0x3130304345524544UL;
        public const string MagicText = "DEREC001";
        public const uint FormatVersion = 1;
        public const int HeaderBytes = 64;
        public const int RecordHeaderBytes = 24;
        /// <summary>A C3 message exactly as written ({u32 type, u32 payloadBytes} + payload, unpadded).</summary>
        public const ushort KindC3 = 1;
        /// <summary>A C4 message exactly as written.</summary>
        public const ushort KindC4 = 2;
        /// <summary>A packed C2 snapshot: the 0x40 header, entityCount × 96 B, subpartCount × 64 B, lightCount × 64 B.</summary>
        public const ushort KindC2 = 3;
        /// <summary>A C1 record: 0x100 B + boneCount × 48 B.</summary>
        public const ushort KindC1 = 4;
        /// <summary>A marker: one UTF-8 JSON object, e.g. <c>{"ev":"edit","kind":"grind"}</c>.</summary>
        public const ushort KindMarker = 5;
        /// <summary>End of recording (0 bytes); a file without it is replayed to its last whole record.</summary>
        public const ushort KindEnd = 0xFFFF;
        public const uint FlagHasC1 = 1u << 0, FlagHasC2 = 1u << 1, FlagSynthetic = 1u << 2;
        /// <summary>Reader sanity limit for one record body (the largest is a C3 chunk message: 4 MiB + 24 B).</summary>
        public const int MaxRecordBytes = 64 << 20;

        /// <summary>Bytes rounded up to a multiple of 8.</summary>
        public static int Padded(int bytes) { return (bytes + 7) & ~7; }

        /// <summary>A header for a new recording with this build's layout hash, conventions hash and wire version.</summary>
        public static DerecHeader CreateHeader(uint seVersion, ulong createdUnixMs, uint flags, uint worldEpoch)
        {
            return new DerecHeader
            {
                magic = Magic,
                formatVersion = FormatVersion,
                headerBytes = HeaderBytes,
                layoutHash = LayoutHash.Compiled,
                conventionsHash = MirrorConventions.Hash,
                wireVersion = WireInfo.WireVersion,
                seVersion = seVersion,
                createdUnixMs = createdUnixMs,
                flags = flags,
                worldEpoch = worldEpoch,
                reserved = 0,
            };
        }

        /// <summary>Checks magic, format version and header size; true when a reader can continue.</summary>
        public static bool Validate(DerecHeader h, out string error)
        {
            error = null;
            if (h.magic != Magic) { error = "not a .derec file (magic)"; return false; }
            if (h.formatVersion != FormatVersion) { error = ".derec formatVersion " + h.formatVersion + " (expected " + FormatVersion + ")"; return false; }
            if (h.headerBytes < HeaderBytes || h.headerBytes > 4096 || (h.headerBytes & 7) != 0) { error = ".derec headerBytes " + h.headerBytes; return false; }
            return true;
        }

        /// <summary>A recording name: 1-64 characters of [A-Za-z0-9_.-], not starting with '.', no path parts.</summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name[0] == '.') return false;
            foreach (char c in name)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>%LOCALAPPDATA%\DeepEngineers\recordings (derived data; never the repository or a game folder).</summary>
        public static string DefaultDirectory()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(local, "DeepEngineers"), "recordings");
        }

        /// <summary>&lt;dir&gt;\&lt;name&gt;.derec (the name must be valid).</summary>
        public static string RecordingPath(string directory, string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("invalid recording name", "name");
            return Path.Combine(directory, name + ".derec");
        }

        /// <summary>&lt;dir&gt;\&lt;name&gt;.json (the sidecar).</summary>
        public static string SidecarPath(string directory, string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("invalid recording name", "name");
            return Path.Combine(directory, name + ".json");
        }

        /// <summary>Bytes of a packed C2 snapshot.</summary>
        public static int C2SnapshotBytes(uint entityCount, uint subpartCount, uint lightCount)
        {
            return SeEntitiesLimits.EntitiesOffset + (int)entityCount * SeEntitiesLimits.EntitySize
                + (int)subpartCount * SeEntitiesLimits.SubpartSize + (int)lightCount * SeEntitiesLimits.LightSize;
        }

        /// <summary>Bytes of a C1 record with its bones.</summary>
        public static int C1RecordBytes(uint boneCount) { return SeFrameLimits.BonesOffset + (int)boneCount * SeFrameLimits.BoneSize; }

        /// <summary>
 /// Packs a consistent C2 copy (the region layout: header, entities at 0x40, subparts at 0x18040, lights at
 /// 0x38040) into <paramref name="dst"/>; returns the bytes written, or −1 when a count is above its table or
 /// <paramref name="dst"/> is too small.
 /// </summary>
        public static int PackC2(byte* region, byte[] dst)
        {
            if (region == null) throw new ArgumentNullException("region");
            if (dst == null) throw new ArgumentNullException("dst");
            var h = *(SeEntitiesHeader*)region;
            if (h.entityCount > SeEntitiesLimits.MaxEntities || h.subpartCount > SeEntitiesLimits.MaxSubparts || h.lightCount > SeEntitiesLimits.MaxLights) return -1;
            int n = C2SnapshotBytes(h.entityCount, h.subpartCount, h.lightCount);
            if (dst.Length < n) return -1;
            int e = (int)h.entityCount * SeEntitiesLimits.EntitySize, s = (int)h.subpartCount * SeEntitiesLimits.SubpartSize;
            int l = (int)h.lightCount * SeEntitiesLimits.LightSize;
            Marshal.Copy((IntPtr)region, dst, 0, SeEntitiesLimits.EntitiesOffset + e);
            Marshal.Copy((IntPtr)(region + SeEntitiesLimits.SubpartsOffset), dst, SeEntitiesLimits.EntitiesOffset + e, s);
            Marshal.Copy((IntPtr)(region + SeEntitiesLimits.LightsOffset), dst, SeEntitiesLimits.EntitiesOffset + e + s, l);
            return n;
        }

        /// <summary>Unpacks a packed C2 snapshot into the region layout (tables beyond the counts are left as they are).</summary>
        public static bool UnpackC2(byte[] packed, int offset, int count, byte* region)
        {
            if (packed == null) throw new ArgumentNullException("packed");
            if (region == null) throw new ArgumentNullException("region");
            if (offset < 0 || count < SeEntitiesLimits.EntitiesOffset || offset > packed.Length - count) return false;
            SeEntitiesHeader h;
            fixed (byte* p = packed) h = *(SeEntitiesHeader*)(p + offset);
            if (h.entityCount > SeEntitiesLimits.MaxEntities || h.subpartCount > SeEntitiesLimits.MaxSubparts || h.lightCount > SeEntitiesLimits.MaxLights) return false;
            if (count != C2SnapshotBytes(h.entityCount, h.subpartCount, h.lightCount)) return false;
            int e = (int)h.entityCount * SeEntitiesLimits.EntitySize, s = (int)h.subpartCount * SeEntitiesLimits.SubpartSize;
            int l = (int)h.lightCount * SeEntitiesLimits.LightSize;
            Marshal.Copy(packed, offset, (IntPtr)region, SeEntitiesLimits.EntitiesOffset + e);
            Marshal.Copy(packed, offset + SeEntitiesLimits.EntitiesOffset + e, (IntPtr)(region + SeEntitiesLimits.SubpartsOffset), s);
            Marshal.Copy(packed, offset + SeEntitiesLimits.EntitiesOffset + e + s, (IntPtr)(region + SeEntitiesLimits.LightsOffset), l);
            return true;
        }
    }

    /// <summary>Writes a <c>.derec</c> stream (not thread-safe: one writer thread).</summary>
    public sealed unsafe class DerecWriter : IDisposable
    {
        private readonly Stream _s;
        private readonly bool _own;
        private readonly byte[] _hdr = new byte[Derec.RecordHeaderBytes];
        private static readonly byte[] Zeros = new byte[8];
        private bool _ended;

        public DerecHeader Header { get; private set; }
        /// <summary>Records written (end included) and bytes written (header included).</summary>
        public long Records { get; private set; }
        public long Bytes { get; private set; }
        /// <summary>Records per kind 1..5.</summary>
        public readonly long[] PerKind = new long[6];

        public DerecWriter(Stream stream, DerecHeader header, bool ownStream = true)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            string error;
            if (!Derec.Validate(header, out error)) throw new ArgumentException(error, "header");
            _s = stream;
            _own = ownStream;
            Header = header;
            var b = new byte[header.headerBytes];
            fixed (byte* p = b) *(DerecHeader*)p = header;
            _s.Write(b, 0, b.Length);
            Bytes = b.Length;
        }

        /// <summary>One record: the 24 B header, the bytes, zero padding to 8.</summary>
        public void Write(ushort kind, ulong seTick, long qpc, byte[] data, int offset, int count)
        {
            if (_ended) throw new InvalidOperationException("recording already ended");
            if (count < 0 || count > Derec.MaxRecordBytes) throw new ArgumentOutOfRangeException("count");
            if (count > 0 && (data == null || offset < 0 || offset > data.Length - count)) throw new ArgumentOutOfRangeException("offset");
            fixed (byte* h = _hdr)
            {
                var rh = new DerecRecordHeader { kind = kind, pad = 0, bytes = (uint)count, seTick = seTick, qpc = qpc };
                *(DerecRecordHeader*)h = rh;
            }
            _s.Write(_hdr, 0, _hdr.Length);
            if (count > 0) _s.Write(data, offset, count);
            int pad = Derec.Padded(count) - count;
            if (pad > 0) _s.Write(Zeros, 0, pad);
            Records++;
            Bytes += Derec.RecordHeaderBytes + count + pad;
            if (kind >= 1 && kind <= 5) PerKind[kind]++;
            if (kind == Derec.KindEnd) _ended = true;
        }

        /// <summary>A C3 or C4 message as written: {u32 type, u32 payloadBytes} + payload.</summary>
        public void WriteMessage(ushort kind, ulong seTick, long qpc, uint type, byte[] payload, int offset, int count)
        {
            if (kind != Derec.KindC3 && kind != Derec.KindC4) throw new ArgumentOutOfRangeException("kind", "C3 or C4");
            var buf = new byte[8 + count];
            fixed (byte* b = buf) { *(uint*)b = type; *(uint*)(b + 4) = (uint)count; }
            if (count > 0) Buffer.BlockCopy(payload, offset, buf, 8, count);
            Write(kind, seTick, qpc, buf, 0, buf.Length);
        }

        /// <summary>A marker record (one JSON object, UTF-8).</summary>
        public void WriteMarker(ulong seTick, long qpc, string json)
        {
            byte[] b = Encoding.UTF8.GetBytes(json ?? "{}");
            Write(Derec.KindMarker, seTick, qpc, b, 0, b.Length);
        }

        /// <summary>The end record (once); then flushes.</summary>
        public void WriteEnd(ulong seTick, long qpc)
        {
            if (_ended) return;
            Write(Derec.KindEnd, seTick, qpc, null, 0, 0);
            _s.Flush();
        }

        public void Dispose()
        {
            if (_own) _s.Dispose();
        }
    }

    /// <summary>Reads a <c>.derec</c> stream record by record.</summary>
    public sealed unsafe class DerecReader : IDisposable
    {
        private readonly Stream _s;
        private readonly bool _own;
        private readonly byte[] _hdr = new byte[Derec.RecordHeaderBytes];

        public DerecHeader Header { get; private set; }
        /// <summary>The end record was read.</summary>
        public bool SawEnd { get; private set; }
        /// <summary>The stream stopped inside a record (replayed to the last whole record).</summary>
        public bool Truncated { get; private set; }
        /// <summary>Whole records read (end included).</summary>
        public long Records { get; private set; }

        /// <summary>Reads and validates the header; throws <see cref="InvalidDataException"/> when it is not a.derec v1.</summary>
        public DerecReader(Stream stream, bool ownStream = true)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            _s = stream;
            _own = ownStream;
            var b = new byte[Derec.HeaderBytes];
            if (ReadFully(b, Derec.HeaderBytes) != Derec.HeaderBytes) throw new InvalidDataException(".derec shorter than its header");
            DerecHeader h;
            fixed (byte* p = b) h = *(DerecHeader*)p;
            string error;
            if (!Derec.Validate(h, out error)) throw new InvalidDataException(error);
            int extra = (int)h.headerBytes - Derec.HeaderBytes;
            if (extra > 0 && ReadFully(new byte[extra], extra) != extra) throw new InvalidDataException(".derec shorter than its header");
            Header = h;
        }

        /// <summary>
 /// The next whole record (body without padding); false at the end record, at the end of the stream, or when the
 /// stream stops inside a record (<see cref="Truncated"/>) or a record is malformed (also Truncated).
 /// </summary>
        public bool TryNext(out DerecRecordHeader header, out byte[] body)
        {
            header = default(DerecRecordHeader);
            body = null;
            if (SawEnd) return false;
            int got = ReadFully(_hdr, _hdr.Length);
            if (got == 0) return false;
            if (got != _hdr.Length) { Truncated = true; return false; }
            fixed (byte* p = _hdr) header = *(DerecRecordHeader*)p;
            if (header.bytes > Derec.MaxRecordBytes || header.pad != 0) { Truncated = true; return false; }
            int n = (int)header.bytes, padded = Derec.Padded(n);
            var buf = new byte[padded];
            if (ReadFully(buf, padded) != padded) { Truncated = true; return false; }
            if (header.kind == Derec.KindEnd) { SawEnd = true; Records++; return false; }
            if (padded != n) Array.Resize(ref buf, n);
            body = buf;
            Records++;
            return true;
        }

        private int ReadFully(byte[] b, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = _s.Read(b, off, n - off);
                if (r <= 0) break;
                off += r;
            }
            return off;
        }

        public void Dispose()
        {
            if (_own) _s.Dispose();
        }
    }
}
