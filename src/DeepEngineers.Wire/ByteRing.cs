using System;
using System.Threading;

namespace DeepEngineers.Wire
{
    internal enum ByteRingMutant
    {
        None = 0,
        /// <summary>S0.M3: at the wrap the producer jumps to the ring start without writing the type-0 pad record.</summary>
        SkipPad = 1,
    }

    /// <summary>
 /// SPSC byte ring of CONTRACT §5.3 (C3, C4, C11a-d, C12). Capacity = the region size (a power of two). Messages are
 /// 8-byte aligned: {u32 type, u32 payloadBytes} + payload, padded to 8 bytes with zeros. A message that would cross
 /// the end is preceded by a pad record (type 0, payloadBytes = bytes left to the end − 8); the consumer, seeing
 /// type 0, skips to the ring start. Messages larger than half the ring are rejected by the producer. Head (producer)
 /// and tail (consumer) are u64 byte counters in the ring-control page; the producer keeps its head locally (it owns
 /// the word) and a restarted producer continues from the head it finds: nobody zeroes a head.
 /// Construct ring objects after the creator/attach step. One producer thread and one consumer thread per ring.
 /// </summary>
    public sealed unsafe class ByteRing
    {
        public const int HeaderBytes = 8;
        public const int Alignment = 8;
        public const uint PadType = 0;

        private readonly ulong* _head;
        private readonly ulong* _tail;
        private readonly byte* _data;
        private readonly long _capacity;
        private readonly ulong _mask;
        private ulong _producerHead;
        private ulong _consumerTail;
        private ulong _peekNext;
        private bool _peeked;

        internal ByteRingMutant Mutant;

        /// <summary>Messages dropped by TryWriteOrDrop (the C11 reply policy: "dropped and counted").</summary>
        public long Dropped { get; private set; }
        /// <summary>Corrupt reads seen by this consumer.</summary>
        public long CorruptCount { get; private set; }

        /// <summary>A byte ring of a mapped section.</summary>
        public ByteRing(byte* sectionBase, RingIndex ring)
            : this(RingControl.HeadPtr(sectionBase, ring), RingControl.TailPtr(sectionBase, ring),
                   RingControl.DataPtr(sectionBase, ring), Layout.Region(Layout.RingRegion(ring)).Size)
        {
            if (Layout.Region(Layout.RingRegion(ring)).Kind != RegionKind.ByteRing)
                throw new ArgumentException("ring " + ring + " is not a byte ring");
        }

        /// <summary>A byte ring over arbitrary memory (tests, stand-ins). Capacity: a power of two ≥ 32.</summary>
        public ByteRing(ulong* head, ulong* tail, byte* data, long capacity)
        {
            if (head == null || tail == null || data == null) throw new ArgumentNullException();
            if (capacity < 32 || (capacity & (capacity - 1)) != 0) throw new ArgumentOutOfRangeException("capacity", "a power of two >= 32");
            _head = head; _tail = tail; _data = data; _capacity = capacity; _mask = (ulong)capacity - 1;
            _producerHead = Volatile.Read(ref *head);
            _consumerTail = Volatile.Read(ref *tail);
        }

        public long Capacity { get { return _capacity; } }
        /// <summary>Largest framed message (header + padded payload): half the ring.</summary>
        public long MaxMessageBytes { get { return _capacity / 2; } }
        /// <summary>Largest payload accepted.</summary>
        public long MaxPayloadBytes { get { return _capacity / 2 - HeaderBytes; } }
        public ulong Head { get { return Volatile.Read(ref *_head); } }
        public ulong Tail { get { return Volatile.Read(ref *_tail); } }
        public ulong ProducerHead { get { return _producerHead; } }
        public ulong ConsumerTail { get { return _consumerTail; } }

        /// <summary>Bytes in flight now (0 when the counters are inconsistent).</summary>
        public long Used
        {
            get
            {
                ulong n = Head - Tail;
                return n > (ulong)_capacity ? 0 : (long)n;
            }
        }

        /// <summary>Header + payload, padded to 8.</summary>
        public static long FramedSize(long payloadBytes)
        {
            return HeaderBytes + ((payloadBytes + (Alignment - 1)) & ~(long)(Alignment - 1));
        }

        // ------------------------------------------------------------------------------------------------- producer

 /// <summary>Writes one message, or nothing. The producer tries once; on Full the caller re-queues (C3, C4, C12).</summary>
        public RingWriteStatus TryWrite(uint type, byte* payload, int payloadBytes)
        {
            return TryWrite(type, payload, payloadBytes, null, 0);
        }

        /// <summary>Writes one message whose payload is part1 followed by part2 (e.g. a ModelChunk header and its bytes).</summary>
        public RingWriteStatus TryWrite(uint type, byte* part1, int length1, byte* part2, int length2)
        {
            if (type == PadType) return RingWriteStatus.InvalidType;
            if (length1 < 0 || length2 < 0) throw new ArgumentOutOfRangeException("length");
            if ((length1 > 0 && part1 == null) || (length2 > 0 && part2 == null)) throw new ArgumentNullException("part");
            long payloadBytes = (long)length1 + length2;
            long need = FramedSize(payloadBytes);
            if (need > _capacity / 2 || payloadBytes > uint.MaxValue) return RingWriteStatus.TooLarge;

            ulong h = _producerHead;
            ulong t = Volatile.Read(ref *_tail);
            ulong used = h - t;
            if (used > (ulong)_capacity || (h & (Alignment - 1)) != 0)
            {
                CorruptCount++; // impossible counters (or a misaligned head found in the section): never overwrite
                return RingWriteStatus.Corrupt;
            }
            long off = (long)(h & _mask);
            long contiguous = _capacity - off;
            long total = need;
            bool pad = need > contiguous;
            if (pad) total = contiguous + need;
            if (used + (ulong)total > (ulong)_capacity) return RingWriteStatus.Full;

            if (pad)
            {
                if (Mutant != ByteRingMutant.SkipPad)
                {
                    byte* p0 = _data + off;
                    *(uint*)p0 = PadType;
                    *(uint*)(p0 + 4) = (uint)(contiguous - HeaderBytes);
                }
                off = 0;
            }

            byte* p = _data + off;
            *(uint*)p = type;
            *(uint*)(p + 4) = (uint)payloadBytes;
            if (length1 > 0) Buffer.MemoryCopy(part1, p + HeaderBytes, length1, length1);
            if (length2 > 0) Buffer.MemoryCopy(part2, p + HeaderBytes + length1, length2, length2);
            for (long i = HeaderBytes + payloadBytes; i < need; i++) p[i] = 0; // deterministic padding

            h += (ulong)total;
            Volatile.Write(ref *_head, h); // release: the message (and pad) are complete before the head moves
            _producerHead = h;
            return RingWriteStatus.Ok;
        }

        public RingWriteStatus TryWrite(uint type, byte[] payload, int offset, int count)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (offset < 0 || count < 0 || offset + count > payload.Length) throw new ArgumentOutOfRangeException("count");
            fixed (byte* p = payload) return TryWrite(type, count == 0 ? null : p + offset, count, null, 0);
        }

        public RingWriteStatus TryWrite<T>(uint type, T payload) where T : unmanaged
        {
            return TryWrite(type, (byte*)&payload, sizeof(T), null, 0);
        }

        /// <summary>A fixed record followed by variable bytes (ModelChunk, SkinnedBind, BatchStatics...).</summary>
        public RingWriteStatus TryWrite<T>(uint type, T head, byte* tail, int tailBytes) where T : unmanaged
        {
            return TryWrite(type, (byte*)&head, sizeof(T), tail, tailBytes);
        }

        /// <summary>The C11 reply policy: a message that does not fit is dropped and counted (a plugin never waits).</summary>
        public RingWriteStatus TryWriteOrDrop(uint type, byte* payload, int payloadBytes)
        {
            RingWriteStatus st = TryWrite(type, payload, payloadBytes, null, 0);
            if (st != RingWriteStatus.Ok) Dropped++;
            return st;
        }

        public RingWriteStatus TryWriteOrDrop(uint type, byte[] payload, int offset, int count)
        {
            RingWriteStatus st = TryWrite(type, payload, offset, count);
            if (st != RingWriteStatus.Ok) Dropped++;
            return st;
        }

        /// <summary>Free bytes as the producer sees them now (a message also needs its pad when it would cross the end).</summary>
        public long Free
        {
            get
            {
                ulong used = _producerHead - Volatile.Read(ref *_tail);
                return used > (ulong)_capacity ? 0 : _capacity - (long)used;
            }
        }

        // ------------------------------------------------------------------------------------------------- consumer

 /// <summary>
 /// Looks at the next message without consuming it (pad records are skipped and consumed). The payload pointer
 /// points into the ring and stays valid until Advance. Corrupt: nothing consumed; call ResyncTail.
 /// </summary>
        public RingReadStatus TryPeek(out uint type, out byte* payload, out int payloadBytes)
        {
            type = 0; payload = null; payloadBytes = 0;
            _peeked = false;
            for (int guard = 0; guard < 2; guard++)
            {
                ulong t = _consumerTail;
                ulong h = Volatile.Read(ref *_head);
                if (h == t) return RingReadStatus.Empty;
                ulong avail = h - t;
                if (avail > (ulong)_capacity || (avail & (Alignment - 1)) != 0 || (t & (Alignment - 1)) != 0 || avail < HeaderBytes)
                    return Corrupt();
                long off = (long)(t & _mask);
                byte* p = _data + off;
                uint ty = *(uint*)p;
                uint len = *(uint*)(p + 4);
                if (ty == PadType)
                {
                    long skip = _capacity - off;
                    if (off == 0 || (ulong)skip > avail) return Corrupt();
                    t += (ulong)skip;
                    Volatile.Write(ref *_tail, t);
                    _consumerTail = t;
                    continue;
                }
                long need = FramedSize(len);
                if (need > _capacity - off || (ulong)need > avail || need > _capacity / 2) return Corrupt();
                type = ty; payload = p + HeaderBytes; payloadBytes = (int)len;
                _peekNext = t + (ulong)need;
                _peeked = true;
                return RingReadStatus.Ok;
            }
            return Corrupt(); // two pads in a row
        }

        /// <summary>Consumes the message returned by the last successful TryPeek.</summary>
        public void Advance()
        {
            if (!_peeked) throw new InvalidOperationException("Advance without a successful TryPeek");
            _peeked = false;
            Volatile.Write(ref *_tail, _peekNext); // release: the payload was read before the space is given back
            _consumerTail = _peekNext;
        }

        /// <summary>Copies the next message into <paramref name="buffer"/> and consumes it.</summary>
        public RingReadStatus TryRead(out uint type, byte[] buffer, out int payloadBytes)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            byte* src;
            RingReadStatus st = TryPeek(out type, out src, out payloadBytes);
            if (st != RingReadStatus.Ok) return st;
            if (payloadBytes > buffer.Length) { _peeked = false; return RingReadStatus.BufferTooSmall; }
            if (payloadBytes > 0) fixed (byte* d = buffer) Buffer.MemoryCopy(src, d, buffer.Length, payloadBytes);
            Advance();
            return RingReadStatus.Ok;
        }

        /// <summary>Consumer re-synchronisation (CONTRACT §5.2): tail:= head, dropping in-flight data. Never writes the head.</summary>
        public void ResyncTail()
        {
            ulong h = Volatile.Read(ref *_head);
            Volatile.Write(ref *_tail, h);
            _consumerTail = h;
            _peeked = false;
        }

        private RingReadStatus Corrupt()
        {
            CorruptCount++;
            _peeked = false;
            return RingReadStatus.Corrupt;
        }
    }
}
