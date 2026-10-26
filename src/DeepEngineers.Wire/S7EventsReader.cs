using System;
using C2Events = DeepEngineers.Wire.S7Conventions.C2Events;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// A reader of C2 bytes beyond the S5 tables (the S7 light rows, the effect-event window), driven by Subnautica's C2
 /// table reader (SN <c>MirrorWire.EntityTableReader</c>, B2 hook): <see cref="CopyInWindow"/> runs inside the seqlock
 /// read window (it may run several times when a read is retried; a torn copy is simply overwritten), and
 /// <see cref="Commit"/> once after a coherent read with the header's tick. Main thread of Subnautica; never throws on
 /// bad data (it counts it).
 /// </summary>
    public unsafe interface IC2ExtensionReader
    {
        void CopyInWindow(byte* region);
        void Commit(ulong headerTick);
    }

    /// <summary>
 /// Subnautica's half of the event window (CONTRACT §5.4 "C2 effect events", "Reader"): copies the header extension and
 /// <c>eventCount</c> rows in the C2 seqlock window; after a coherent read, only when <c>c2ext.version</c> = 1, the
 /// <c>Events</c> flag is set and <c>tickLow</c> matches, applies every row whose seq is past the last applied seq of the
 /// same <c>eventStream</c>, in seq order, into a fixed pending queue that the effect player drains each frame. A gap
 /// (<c>eventFirstSeq</c> &gt; last + 1) adds to <see cref="Missed"/>; a new stream resets the last seq to
 /// <c>eventFirstSeq − 1</c>. No allocation after construction.
 /// </summary>
    public sealed unsafe class S7EventReader : IC2ExtensionReader
    {
        /// <summary>Events waiting for the player (beyond: dropped, counted <see cref="Overflow"/>).</summary>
        public const int PendingCapacity = 2048;

        private readonly object _lock = new object();
        private C2Ext _ext;
        private readonly S7EventRow[] _copy = new S7EventRow[C2Events.MaxRows];
        private int _copyCount;

        private readonly S7EventRow[] _pending = new S7EventRow[PendingCapacity];
        private int _pHead, _pCount;

        private bool _haveStream;
        private uint _stream, _lastSeq;

        /// <summary>Events lost: seq gaps (Subnautica more than 8 SE ticks behind) — <c>eventsMissed</c>.</summary>
        public long Missed;
        /// <summary>Events queued for the player (applied once each).</summary>
        public long Queued;
        /// <summary>Pending queue full: events dropped here.</summary>
        public long Overflow;
        public long StreamResets, Commits, NoExtension, StaleExtension, BadCount, BadRows;
        /// <summary>Rows seen again in a later window (normal: the window overlaps 8 ticks).</summary>
        public long AlreadyApplied;
        /// <summary>The newest valid extension (also for the lights reader: <c>lightsFound</c>).</summary>
        public C2Ext LastExt { get { lock (_lock) return _ext; } }
        public bool LastExtValid { get; private set; }
        public ulong LastHeaderTick { get; private set; }
        public uint LastSeq { get { lock (_lock) return _lastSeq; } }
        public uint CurrentStream { get { lock (_lock) return _stream; } }

        public void CopyInWindow(byte* region)
        {
            if (region == null) return;
            _ext = *(C2Ext*)(region + S7Conventions.C2Ext.Offset);
            int n = _ext.eventCount;
            if (n > C2Events.MaxRows) n = 0; // torn or malformed: Commit refuses it (count checked again)
            _copyCount = n;
            if (n > 0)
            {
                fixed (S7EventRow* dst = _copy)
                    Buffer.MemoryCopy(region + C2Events.Offset, dst, (long)C2Events.MaxRows * C2Events.RowSize, (long)n * C2Events.RowSize);
            }
        }

        public void Commit(ulong headerTick)
        {
            lock (_lock)
            {
                Commits++;
                LastHeaderTick = headerTick;
                if (_ext.version != S7Conventions.C2Ext.Version) { NoExtension++; LastExtValid = false; return; }
                if (_ext.tickLow != (uint)headerTick) { StaleExtension++; LastExtValid = false; return; }
                LastExtValid = true;
                if (!_ext.HasEvents) return;
                if (_ext.eventCount > C2Events.MaxRows || _ext.eventCount != _copyCount) { BadCount++; return; }
                Apply(_ext.eventStream, _ext.eventFirstSeq, _copy, _copyCount);
            }
        }

        /// <summary>
 /// The seq rule on one window (also the unit-test entry): rows are expected consecutive from
 /// <paramref name="firstSeq"/>; rows at or before the last applied seq are skipped; a gap counts missed events.
 /// </summary>
        public void Apply(uint stream, uint firstSeq, S7EventRow[] rows, int count)
        {
            lock (_lock)
            {
                if (!_haveStream || stream != _stream)
                {
                    if (_haveStream) StreamResets++;
                    _haveStream = true;
                    _stream = stream;
                    _lastSeq = firstSeq - 1;
                }
                if (Ahead(firstSeq, _lastSeq + 1))
                {
                    Missed += (long)(uint)(firstSeq - (_lastSeq + 1));
                    _lastSeq = firstSeq - 1;
                }
                for (int i = 0; i < count; i++)
                {
                    S7EventRow r = rows[i];
                    if (!Ahead(r.seq, _lastSeq)) { AlreadyApplied++; continue; }
                    if (Ahead(r.seq, _lastSeq + 1)) Missed += (long)(uint)(r.seq - (_lastSeq + 1)); // a hole inside the window (never by contract)
                    _lastSeq = r.seq;
                    if (!r.IsValid) { BadRows++; continue; }
                    if (_pCount == PendingCapacity) { Overflow++; continue; }
                    _pending[(_pHead + _pCount) % PendingCapacity] = r;
                    _pCount++;
                    Queued++;
                }
            }
        }

        /// <summary>The next event for the player, oldest first.</summary>
        public bool TryDequeue(out S7EventRow row)
        {
            lock (_lock)
            {
                if (_pCount == 0) { row = default(S7EventRow); return false; }
                row = _pending[_pHead];
                _pHead = (_pHead + 1) % PendingCapacity;
                _pCount--;
                return true;
            }
        }

        public int PendingCount { get { lock (_lock) return _pCount; } }

        /// <summary>A new SE generation or a world teardown: the stream is learnt again, pending events dropped.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _haveStream = false;
                _stream = 0;
                _lastSeq = 0;
                _pHead = _pCount = 0;
                _copyCount = 0;
                LastExtValid = false;
            }
        }

        /// <summary>a is after b in u32 sequence space (wrap-safe).</summary>
        private static bool Ahead(uint a, uint b) { return (int)(a - b) > 0; }
    }
}
