using System;
using System.Globalization;
using System.Runtime.InteropServices;
using C2S8a = DeepEngineers.Wire.S8aConventions.C2S8a;
using C2S8aFlags = DeepEngineers.Wire.S8aConventions.C2S8aFlags;
using C7S8a = DeepEngineers.Wire.S8aConventions.C7S8a;
using NoticeKind = DeepEngineers.Wire.S8aConventions.NoticeKind;
using QueueC = DeepEngineers.Wire.S8aConventions.Queue;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S8a change feed (CONTRACT §5.4 "S8a conventions": the C2 S8a header at region offset 0x50000, the notice rows from
 // 0x50040, the C7 S8a block at SnStatus 0x8C; formats feed.ack, feed.resync). Layout 1.0 unchanged: the bytes sit past
 // S7's event window inside the C2 region (0x80000) and inside SnStatus.pad_8c. Reliability without a ring: SE repeats
 // every unacknowledged notice until C7 ackSeq covers it; the level state lives in the store's manifests; a full queue
 // turns into a resync. Python twin: tools/planet/s8a_ref.py (byte vectors tests/s8a/vectors/feed.tsv).
 // =================================================================================================================

 /// <summary>One terrain notice (32 B; CONTRACT §5.4 "Notice row").</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32, Pack = 1)]
    public unsafe struct S8aNotice
    {
        [FieldOffset(0x00)] public uint seq;
        /// <summary><see cref="S8aConventions.NoticeKind"/>.</summary>
        [FieldOffset(0x04)] public byte kind;
        /// <summary><see cref="S8aConventions.NoticeLayer"/> (0 for ChartStep).</summary>
        [FieldOffset(0x05)] public byte layer;
        /// <summary><see cref="S8aConventions.NoticeFlags"/>.</summary>
        [FieldOffset(0x06)] public ushort flags;
        /// <summary>Batch id; ChartStep: T in batches (bx, bz), by 0.</summary>
        [FieldOffset(0x08)] public short bx;
        [FieldOffset(0x0A)] public short by;
        [FieldOffset(0x0C)] public short bz;
        [FieldOffset(0x0E)] private ushort pad_0e;
        /// <summary>The file's version; ChartStep: the new chartSeq; StoreReset: the layer's new generation.</summary>
        [FieldOffset(0x10)] public uint version;
        /// <summary>Batch-local voxel box of the change (0..159), inclusive.</summary>
        [FieldOffset(0x14)] public byte minX;
        [FieldOffset(0x15)] public byte minY;
        [FieldOffset(0x16)] public byte minZ;
        [FieldOffset(0x17)] public byte maxX;
        [FieldOffset(0x18)] public byte maxY;
        [FieldOffset(0x19)] public byte maxZ;
        /// <summary>Octrees rewritten (≤ 125).</summary>
        [FieldOffset(0x1A)] public ushort octrees;
        /// <summary>Subnautica voxels whose value changed.</summary>
        [FieldOffset(0x1C)] public uint cells;

        /// <summary>A rewritten batch file (version, box, octrees, cells); flags from <see cref="S8aConventions.NoticeFlags"/>.</summary>
        public static S8aNotice BatchWritten(byte layer, int bx, int by, int bz, uint version, ushort flags,
                                             int minX, int minY, int minZ, int maxX, int maxY, int maxZ, int octrees, uint cells)
        {
            var n = new S8aNotice { kind = NoticeKind.BatchWritten, layer = layer, flags = flags, version = version };
            n.SetId(bx, by, bz);
            n.SetBox(minX, minY, minZ, maxX, maxY, maxZ);
            n.octrees = (ushort)Math.Max(0, Math.Min(125, octrees));
            n.cells = cells;
            return n;
        }

        /// <summary>A batch with no file (water): the full box.</summary>
        public static S8aNotice BatchCleared(byte layer, int bx, int by, int bz, uint version, ushort flags)
        {
            var n = new S8aNotice { kind = NoticeKind.BatchCleared, layer = layer, flags = flags, version = version };
            n.SetId(bx, by, bz);
            n.SetBox(0, 0, 0, 159, 159, 159);
            return n;
        }

        /// <summary>A chart step: T = (tx, tz) batches, the new chartSeq in <see cref="version"/>.</summary>
        public static S8aNotice ChartStep(uint chartSeq, int tx, int tz)
        {
            var n = new S8aNotice { kind = NoticeKind.ChartStep, layer = 0, version = chartSeq };
            n.SetId(tx, 0, tz);
            return n;
        }

        /// <summary>A store reset of a layer (new storeKey, new overlay or new chartKey): the layer's new generation.</summary>
        public static S8aNotice StoreReset(byte layer, uint generation)
        {
            return new S8aNotice { kind = NoticeKind.StoreReset, layer = layer, version = generation };
        }

        public void SetId(int x, int y, int z)
        {
            bx = (short)x;
            by = (short)y;
            bz = (short)z;
        }

        /// <summary>Sets the batch-local box, each value clamped to 0..159.</summary>
        public void SetBox(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            minX = Clamp(x0); minY = Clamp(y0); minZ = Clamp(z0);
            maxX = Clamp(x1); maxY = Clamp(y1); maxZ = Clamp(z1);
        }

        private static byte Clamp(int v) { return (byte)(v < 0 ? 0 : v > 159 ? 159 : v); }

        public bool IsValid
        {
            get
            {
                if (kind < NoticeKind.BatchWritten || kind > NoticeKind.StoreReset) return false;
                if (kind == NoticeKind.BatchWritten || kind == NoticeKind.BatchCleared)
                    return layer == S8aConventions.NoticeLayer.Drill || layer == S8aConventions.NoticeLayer.Window;
                if (kind == NoticeKind.StoreReset) return layer == S8aConventions.NoticeLayer.Drill || layer == S8aConventions.NoticeLayer.Window;
                return layer == 0;
            }
        }

        public void WriteTo(byte* dst) { *(S8aNotice*)dst = this; }

        public static S8aNotice ReadFrom(byte* src) { return *(S8aNotice*)src; }

        public byte[] ToBytes()
        {
            var b = new byte[C2S8a.RowSize];
            fixed (byte* p = b) WriteTo(p);
            return b;
        }

        public static S8aNotice FromBytes(byte[] b, int offset)
        {
            if (b == null || offset < 0 || offset + C2S8a.RowSize > b.Length) throw new ArgumentException("32 bytes needed");
            fixed (byte* p = &b[offset]) return ReadFrom(p);
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "seq {0} kind {1} layer {2} flags {3} id {4},{5},{6} v{7} box {8},{9},{10}-{11},{12},{13} octrees {14} cells {15}",
                seq, kind, layer, flags, bx, by, bz, version, minX, minY, minZ, maxX, maxY, maxZ, octrees, cells);
        }
    }

    /// <summary>The C2 S8a header (64 B at C2 region offset 0x50000; CONTRACT §5.4 "C2 S8a header").</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64, Pack = 1)]
    public unsafe struct S8aHeader
    {
        /// <summary>1; 0 = not written (an SE before S8a, or S8a off): Subnautica ignores the block.</summary>
        [FieldOffset(0x00)] public byte version;
        /// <summary><see cref="S8aConventions.C2S8aFlags"/>.</summary>
        [FieldOffset(0x01)] public byte flags;
        [FieldOffset(0x02)] public ushort rowCount;
        [FieldOffset(0x04)] public uint firstSeq;
        [FieldOffset(0x08)] public uint stream;
        [FieldOffset(0x0C)] public uint dropped;
        [FieldOffset(0x10)] public uint tickLow;
        [FieldOffset(0x14)] public uint resyncEpoch;
        [FieldOffset(0x18)] public ulong storeKey;
        [FieldOffset(0x20)] public uint overlayGen;
        [FieldOffset(0x24)] public uint chartSeq;
        /// <summary>"biome.names" index; 255 = none.</summary>
        [FieldOffset(0x28)] public byte cameraBiome;
        [FieldOffset(0x29)] private byte pad_29;
        [FieldOffset(0x2A)] public ushort nearPendingCount;
        /// <summary>Distance from the camera down to SE's surface (NaN unknown).</summary>
        [FieldOffset(0x2C)] public float seabedBelowCamM;
        [FieldOffset(0x30)] public uint windowGen;
        [FieldOffset(0x34)] public uint drillGen;
        [FieldOffset(0x38)] public ulong chartKey;

        public bool Has(byte flag) { return (flags & flag) != 0; }

        public void WriteTo(byte* c2Region) { *(S8aHeader*)(c2Region + C2S8a.Offset) = this; }

        public static S8aHeader ReadFrom(byte* c2Region) { return *(S8aHeader*)(c2Region + C2S8a.Offset); }

        /// <summary>
 /// A header from a coherent C2 read: true when version is 1, <c>tickLow</c> equals the low 32 bits of the C2 header's
 /// tick (an older SE generation's block is stale) and rowCount ≤ 1,024.
 /// </summary>
        public static bool TryRead(byte* c2Region, ulong headerTick, out S8aHeader h)
        {
            h = ReadFrom(c2Region);
            return h.version == C2S8a.Version && h.tickLow == (uint)headerTick && h.rowCount <= C2S8a.MaxRows;
        }

        /// <summary>Zeroes the header (S8a off: version 0; Subnautica ignores the block).</summary>
        public static void WriteOff(byte* c2Region)
        {
            byte* p = c2Region + C2S8a.Offset;
            for (int i = 0; i < C2S8a.HeaderSize; i++) p[i] = 0;
        }

        public byte[] ToBytes()
        {
            var b = new byte[C2S8a.HeaderSize];
            fixed (byte* p = b) *(S8aHeader*)p = this;
            return b;
        }

        public static S8aHeader FromBytes(byte[] b, int offset)
        {
            if (b == null || offset < 0 || offset + C2S8a.HeaderSize > b.Length) throw new ArgumentException("64 bytes needed");
            fixed (byte* p = &b[offset]) return *(S8aHeader*)p;
        }
    }

    /// <summary>The C7 S8a block (48 B at SnStatus 0x8C, inside <c>pad_8c</c>; CONTRACT §5.4 "C7 S8a block"). Offsets relative to 0x8C.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48, Pack = 1)]
    public unsafe struct S8aC7
    {
        /// <summary>1; 0 = a Subnautica plugin without S8a (SE then writes no rows).</summary>
        [FieldOffset(0x00)] public byte version;
        /// <summary><see cref="S8aConventions.C7S8aFlags"/>.</summary>
        [FieldOffset(0x01)] public byte flags;
        [FieldOffset(0x02)] public ushort reloadsPending;
        /// <summary>Highest contiguous seq received and scheduled (same stream).</summary>
        [FieldOffset(0x04)] public uint ackSeq;
        /// <summary>Highest contiguous seq whose batch is loaded and whose box is built at the collision level.</summary>
        [FieldOffset(0x08)] public uint appliedSeq;
        [FieldOffset(0x0C)] public uint stream;
        [FieldOffset(0x10)] public uint resyncEpoch;
        [FieldOffset(0x14)] public uint chartSeqApplied;
        [FieldOffset(0x18)] public uint batchesServed;
        [FieldOffset(0x1C)] public uint reloadFailures;
        [FieldOffset(0x20)] public ushort lastLatencyMs;
        [FieldOffset(0x22)] public ushort nearPendingSeen;
        [FieldOffset(0x24)] public ulong storeKey;
        [FieldOffset(0x2C)] private uint pad_2c;

        public bool Has(byte flag) { return (flags & flag) != 0; }

        /// <summary>Writes the block into a SnStatus record at absolute offset 0x8C.</summary>
        public void WriteInto(byte* snStatus) { *(S8aC7*)(snStatus + C7S8a.Offset) = this; }

        /// <summary>Writes the block into <paramref name="status"/> (Subnautica's <c>FillStatus(ref SnStatus)</c>).</summary>
        public void WriteInto(ref SnStatus status)
        {
            fixed (SnStatus* p = &status) WriteInto((byte*)p);
        }

        public static S8aC7 ReadFrom(byte* snStatus) { return *(S8aC7*)(snStatus + C7S8a.Offset); }

        public static S8aC7 ReadFrom(ref SnStatus status)
        {
            fixed (SnStatus* p = &status) return ReadFrom((byte*)p);
        }

        public byte[] ToBytes()
        {
            var b = new byte[C7S8a.Size];
            fixed (byte* p = b) *(S8aC7*)p = this;
            return b;
        }

        public static S8aC7 FromBytes(byte[] b, int offset)
        {
            if (b == null || offset < 0 || offset + C7S8a.Size > b.Length) throw new ArgumentException("48 bytes needed");
            fixed (byte* p = &b[offset]) return *(S8aC7*)p;
        }
    }

    /// <summary>
 /// SE's notice queue and the C2 S8a write ("feed.ack", "feed.resync"). <see cref="TryEnqueue"/> from any thread (the
 /// drill converter, the window worker, the game thread); <see cref="WriteInto(byte*, ulong, in S8aHeader)"/> on the game
 /// thread inside the C2 seqlock write; <see cref="OnAck(uint, uint, uint)"/> after each C7 read. Notices get consecutive
 /// seq from 1 per stream; the rows of a C2 write are the oldest unacknowledged notices, at most
 /// <see cref="S8aConventions.C2S8a.MaxRows"/>, of which at most <see cref="S8aConventions.C2S8a.MaxPerTick"/> never
 /// written before. A full queue (<see cref="S8aConventions.Queue.MaxPending"/>) drops every queued notice into
 /// <see cref="Dropped"/>, bumps <see cref="ResyncEpoch"/> and raises Resync until Subnautica's C7 echoes the epoch;
 /// Subnautica then re-reads the manifests (the level state). No allocation after construction.
 /// </summary>
    public sealed unsafe class S8aFeedWriter
    {
        private readonly object _lock = new object();
        private readonly S8aNotice[] _q = new S8aNotice[QueueC.MaxPending];
        private int _head, _count;
        private uint _nextSeq = 1;
        private uint _sentUpTo;
        private uint _stream;
        private uint _dropped;
        private uint _resyncEpoch;
        private bool _resyncPending;
        private long _enqueued, _acked, _overflows, _invalid, _writes, _rowsWritten;

        public S8aFeedWriter(uint stream) { _stream = stream; }

        public uint Stream { get { lock (_lock) return _stream; } }
        /// <summary>Notices dropped by a full queue since the stream began (<c>s8a.dropped</c>).</summary>
        public uint Dropped { get { lock (_lock) return _dropped; } }
        /// <summary>Unacknowledged notices queued.</summary>
        public int Pending { get { lock (_lock) return _count; } }
        public uint ResyncEpoch { get { lock (_lock) return _resyncEpoch; } }
        /// <summary>Resync raised and not yet echoed by Subnautica's C7 <c>resyncEpoch</c>.</summary>
        public bool ResyncPending { get { lock (_lock) return _resyncPending; } }
        /// <summary>seq of the oldest queued notice (the next seq when the queue is empty).</summary>
        public uint FirstSeq { get { lock (_lock) return _count > 0 ? _q[_head].seq : _nextSeq; } }
        /// <summary>The seq the next notice gets.</summary>
        public uint NextSeq { get { lock (_lock) return _nextSeq; } }
        /// <summary>Highest seq ever written into C2 in this stream (0 none).</summary>
        public uint SentUpTo { get { lock (_lock) return _sentUpTo; } }
        public long Enqueued { get { lock (_lock) return _enqueued; } }
        public long Acked { get { lock (_lock) return _acked; } }
        public long Overflows { get { lock (_lock) return _overflows; } }
        public long Invalid { get { lock (_lock) return _invalid; } }
        public long Writes { get { lock (_lock) return _writes; } }
        public long RowsWritten { get { lock (_lock) return _rowsWritten; } }

        /// <summary>
 /// Queues a notice (its seq is assigned here and returned in <paramref name="seq"/>). False only for a malformed notice
 /// (kind or layer out of range: counted in <see cref="Invalid"/>). A full queue first drops every queued notice
 /// (counted in <see cref="Dropped"/>) and raises a resync.
 /// </summary>
        public bool TryEnqueue(in S8aNotice notice, out uint seq)
        {
            lock (_lock)
            {
                if (!notice.IsValid) { _invalid++; seq = 0; return false; }
                if (_count == _q.Length)
                {
                    _dropped += (uint)_count;
                    _head = 0;
                    _count = 0;
                    _resyncEpoch++;
                    _resyncPending = true;
                    _overflows++;
                }
                S8aNotice n = notice;
                n.seq = _nextSeq++;
                _q[(_head + _count) % _q.Length] = n;
                _count++;
                _enqueued++;
                seq = n.seq;
                return true;
            }
        }

        /// <summary><see cref="TryEnqueue(in S8aNotice, out uint)"/> without the seq.</summary>
        public bool TryEnqueue(in S8aNotice notice)
        {
            uint seq;
            return TryEnqueue(notice, out seq);
        }

        /// <summary>
 /// Writes the S8a header and the rows into the C2 region (inside the C2 seqlock write). The caller's
 /// <paramref name="state"/> carries flags, storeKey, overlayGen, chartSeq, cameraBiome, nearPendingCount, seabedBelowCamM,
 /// windowGen, drillGen and chartKey; this writer sets version 1, rowCount, firstSeq, stream, dropped, tickLow (low 32
 /// bits of <paramref name="tick"/>), resyncEpoch and ORs in Resync while one is pending. Returns the rows written.
 /// </summary>
        public int WriteInto(byte* c2Region, ulong tick, in S8aHeader state)
        {
            return WriteInto(c2Region, tick, state, true);
        }

        /// <summary>
 /// <see cref="WriteInto(byte*, ulong, in S8aHeader)"/>; with <paramref name="writeRows"/> false (C7 <c>c7s8a.version</c> is
 /// not 1: a Subnautica without S8a) the header is written with rowCount 0 and nothing counts as sent.
 /// </summary>
        public int WriteInto(byte* c2Region, ulong tick, in S8aHeader state, bool writeRows)
        {
            if (c2Region == null) return 0;
            lock (_lock)
            {
                int n = 0;
                if (writeRows && _count > 0)
                {
                    uint first = _q[_head].seq;
                    int alreadySent = 0;
                    if (_sentUpTo != 0 && (int)(_sentUpTo - first) >= 0) alreadySent = (int)Math.Min((long)(_sentUpTo - first) + 1, _count);
                    n = Math.Min(Math.Min(_count, C2S8a.MaxRows), alreadySent + C2S8a.MaxPerTick);
                    byte* rows = c2Region + C2S8a.RowsOffset;
                    for (int i = 0; i < n; i++)
                        *(S8aNotice*)(rows + i * C2S8a.RowSize) = _q[(_head + i) % _q.Length];
                    uint last = _q[(_head + n - 1) % _q.Length].seq;
                    if (_sentUpTo == 0 || (int)(last - _sentUpTo) > 0) _sentUpTo = last;
                }
                S8aHeader h = state;
                h.version = C2S8a.Version;
                h.rowCount = (ushort)n;
                h.firstSeq = _count > 0 ? _q[_head].seq : _nextSeq;
                h.stream = _stream;
                h.dropped = _dropped;
                h.tickLow = (uint)tick;
                h.resyncEpoch = _resyncEpoch;
                if (_resyncPending) h.flags |= C2S8aFlags.Resync;
                else h.flags = (byte)(h.flags & ~C2S8aFlags.Resync);
                h.WriteTo(c2Region);
                _writes++;
                _rowsWritten += n;
                return n;
            }
        }

        /// <summary>
 /// C7's acknowledgement: removes the queued notices with seq ≤ <paramref name="ackSeq"/> when <paramref name="stream"/>
 /// is this writer's stream (wrap-safe; an ack beyond the last seq given out removes nothing extra).
 /// </summary>
        public void OnAck(uint stream, uint ackSeq)
        {
            lock (_lock)
            {
                if (stream != _stream) return;
                while (_count > 0)
                {
                    uint s = _q[_head].seq;
                    if ((int)(ackSeq - s) < 0) break;
                    _head = (_head + 1) % _q.Length;
                    _count--;
                    _acked++;
                }
            }
        }

        /// <summary><see cref="OnAck(uint, uint)"/> plus the resync echo: Resync clears once C7 <c>resyncEpoch</c> equals ours.</summary>
        public void OnAck(uint stream, uint ackSeq, uint resyncEpochSeen)
        {
            OnAck(stream, ackSeq);
            lock (_lock)
            {
                if (stream == _stream && _resyncPending && resyncEpochSeen == _resyncEpoch) _resyncPending = false;
            }
        }

        /// <summary>Forces a resync (a world change or the host's own decision): epoch + 1, Resync raised.</summary>
        public void RaiseResync()
        {
            lock (_lock)
            {
                _resyncEpoch++;
                _resyncPending = true;
            }
        }

        /// <summary>
 /// A new stream (a new world, an SE generation, a new Subnautica generation or link epoch): seq restarts at 1, nothing
 /// counts as sent, <see cref="Dropped"/> restarts at 0. With <paramref name="keepPending"/> the queued notices are kept
 /// in order and renumbered from 1 (SE "resends from the oldest unacknowledged notice"); else they are discarded.
 /// </summary>
        public void Restart(uint stream, bool keepPending)
        {
            lock (_lock)
            {
                _stream = stream;
                _sentUpTo = 0;
                _dropped = 0;
                if (!keepPending)
                {
                    _head = 0;
                    _count = 0;
                }
                uint s = 1;
                for (int i = 0; i < _count; i++) _q[(_head + i) % _q.Length].seq = s++;
                _nextSeq = s;
            }
        }

        /// <summary>Copies the queued notices (oldest first) into <paramref name="dst"/>; returns the count copied.</summary>
        public int CopyPending(S8aNotice[] dst)
        {
            if (dst == null) return 0;
            lock (_lock)
            {
                int n = Math.Min(dst.Length, _count);
                for (int i = 0; i < n; i++) dst[i] = _q[(_head + i) % _q.Length];
                return n;
            }
        }
    }

    /// <summary>
 /// Subnautica's half of the feed: an <see cref="IC2ExtensionReader"/> registered with the C2 table reader (SN
 /// <c>MirrorWire</c>, B4 hook). <see cref="CopyInWindow"/> copies the header and <c>rowCount</c> rows inside the seqlock
 /// window; <see cref="Commit"/> (after a coherent read) checks version 1, <c>tickLow</c> and the row count, then takes
 /// every row whose seq is past the last taken seq of the same stream, in order, into a fixed queue the host drains
 /// (<see cref="TryDequeue"/>). A new stream, a gap (SE dropped notices) or a raised Resync is reported to the host
 /// (<see cref="TakeResyncNeeded"/>): it then re-reads both manifests. No allocation after construction.
 /// </summary>
    public sealed unsafe class S8aFeedReader : IC2ExtensionReader
    {
        /// <summary>Notices waiting for the host (beyond: dropped here, counted, and a resync is needed).</summary>
        public const int PendingCapacity = QueueC.MaxPending;

        private readonly object _lock = new object();
        private S8aHeader _hdr;
        private readonly S8aNotice[] _copy = new S8aNotice[C2S8a.MaxRows];
        private int _copyCount;
        private readonly S8aNotice[] _pending = new S8aNotice[PendingCapacity];
        private int _pHead, _pCount;
        private bool _haveStream;
        private uint _stream, _lastSeq, _lastResyncEpoch;
        private bool _resyncNeeded;
        private S8aHeader _last;
        private bool _lastValid;

        public long Commits, NoBlock, Stale, BadCount, BadRows, Missed, Overflow, Queued, AlreadyTaken, StreamChanges, Resyncs;

        /// <summary>The newest valid header (flags, keys, chartSeq, cameraBiome, nearPendingCount, …).</summary>
        public S8aHeader LastHeader { get { lock (_lock) return _last; } }
        public bool LastHeaderValid { get { lock (_lock) return _lastValid; } }
        private ulong _lastTick, _chartChangeTick;
        private long _chartChanges;
        // N8.snap 35 of 63): two changes close together left the first one's bracket unsnapped (only the
 // last change tick was kept); the last RecentChanges change ticks are checked now
        private const int RecentChanges = 16;
        private readonly ulong[] _changeTicks = new ulong[RecentChanges];
        /// <summary>The C2 tick of the last valid header.</summary>
        public ulong LastHeaderTick { get { lock (_lock) return _lastTick; } }
        /// <summary>The C2 tick at which the header's <c>chartSeq</c> last changed (0: never); "chart.apply": Subnautica never interpolates across it.</summary>
        public ulong ChartChangeTick { get { lock (_lock) return _chartChangeTick; } }
        public long ChartChanges { get { lock (_lock) return _chartChanges; } }
        /// <summary>True when a chart change lies in (olderTick, newerTick]: a pose bracket across it must snap to the newer tick.</summary>
        public bool ChartChangedBetween(ulong olderTick, ulong newerTick)
        {
            ulong t;
            lock (_lock)
            {
                for (int i = 0; i < RecentChanges; i++)
                {
                    t = _changeTicks[i];
                    if (t != 0 && olderTick < t && t <= newerTick) return true;
                }
            }
            return false;
        }
        /// <summary>The stream the taken seqs refer to (C7 <c>stream</c>).</summary>
        public uint CurrentStream { get { lock (_lock) return _stream; } }
        /// <summary>Highest contiguous seq taken into the queue (the host acknowledges up to it once scheduled).</summary>
        public uint ReceivedSeq { get { lock (_lock) return _lastSeq; } }
        public int PendingCount { get { lock (_lock) return _pCount; } }

        public void CopyInWindow(byte* region)
        {
            if (region == null) return;
            _hdr = S8aHeader.ReadFrom(region);
            int n = _hdr.version == C2S8a.Version ? _hdr.rowCount : 0;
            if (n > C2S8a.MaxRows) n = 0;
            _copyCount = n;
            if (n > 0)
            {
                fixed (S8aNotice* dst = _copy)
                    Buffer.MemoryCopy(region + C2S8a.RowsOffset, dst, (long)C2S8a.MaxRows * C2S8a.RowSize, (long)n * C2S8a.RowSize);
            }
        }

        public void Commit(ulong headerTick)
        {
            lock (_lock)
            {
                Commits++;
                if (_hdr.version != C2S8a.Version) { NoBlock++; _lastValid = false; return; }
                if (_hdr.tickLow != (uint)headerTick) { Stale++; _lastValid = false; return; }
                if (_hdr.rowCount > C2S8a.MaxRows || _hdr.rowCount != _copyCount) { BadCount++; return; }
                for (int i = 0; i < _copyCount; i++)
                    if (_copy[i].seq != _hdr.firstSeq + (uint)i) { BadRows++; return; }
                if (_lastValid && _hdr.chartSeq != _last.chartSeq) { _chartChangeTick = headerTick; _changeTicks[(int)(_chartChanges % RecentChanges)] = headerTick; _chartChanges++; }
                _lastTick = headerTick;
                _last = _hdr;
                _lastValid = true;
                if (_hdr.Has(C2S8aFlags.Resync) && _hdr.resyncEpoch != _lastResyncEpoch)
                {
                    _lastResyncEpoch = _hdr.resyncEpoch;
                    _resyncNeeded = true;
                    Resyncs++;
                }
                ApplyLocked(_hdr.stream, _hdr.firstSeq, _copy, _copyCount);
            }
        }

        /// <summary>The seq rule on one coherent read (also the unit-test entry).</summary>
        public void Apply(uint stream, uint firstSeq, S8aNotice[] rows, int count)
        {
            lock (_lock) ApplyLocked(stream, firstSeq, rows, count);
        }

        private void ApplyLocked(uint stream, uint firstSeq, S8aNotice[] rows, int count)
        {
            if (!_haveStream || stream != _stream)
            {
                if (_haveStream) { StreamChanges++; _resyncNeeded = true; }
                _haveStream = true;
                _stream = stream;
                _lastSeq = firstSeq - 1;
                _pHead = _pCount = 0;
            }
            if (Ahead(firstSeq, _lastSeq + 1))
            {
                Missed += (long)(uint)(firstSeq - (_lastSeq + 1));
                _lastSeq = firstSeq - 1;
                _resyncNeeded = true;
            }
            for (int i = 0; i < count; i++)
            {
                S8aNotice r = rows[i];
                if (!Ahead(r.seq, _lastSeq)) { AlreadyTaken++; continue; }
                if (r.seq != _lastSeq + 1) { Missed += (long)(uint)(r.seq - (_lastSeq + 1)); _resyncNeeded = true; }
                if (_pCount == PendingCapacity) { Overflow++; _resyncNeeded = true; break; }
                _lastSeq = r.seq;
                if (!r.IsValid) { BadRows++; continue; }
                _pending[(_pHead + _pCount) % PendingCapacity] = r;
                _pCount++;
                Queued++;
            }
        }

        /// <summary>The next notice for the host, oldest first.</summary>
        public bool TryDequeue(out S8aNotice n)
        {
            lock (_lock)
            {
                if (_pCount == 0) { n = default(S8aNotice); return false; }
                n = _pending[_pHead];
                _pHead = (_pHead + 1) % PendingCapacity;
                _pCount--;
                return true;
            }
        }

        /// <summary>True once after a new stream, a seq gap, a queue overflow or a new Resync epoch: re-read both manifests.</summary>
        public bool TakeResyncNeeded()
        {
            lock (_lock)
            {
                bool r = _resyncNeeded;
                _resyncNeeded = false;
                return r;
            }
        }

        /// <summary>The resync epoch to echo in C7 (the last one seen with Resync).</summary>
        public uint ResyncEpochSeen { get { lock (_lock) return _lastResyncEpoch; } }

        /// <summary>A new SE generation or a world teardown: the stream is learnt again, pending notices dropped.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _haveStream = false;
                _stream = 0;
                _lastSeq = 0;
                _pHead = _pCount = 0;
                _copyCount = 0;
                _lastValid = false;
                _resyncNeeded = false;
            }
        }

        private static bool Ahead(uint a, uint b) { return (int)(a - b) > 0; }
    }
}
