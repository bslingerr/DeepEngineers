using System;
using System.Threading;

namespace DeepEngineers.Wire
{
    /// <summary>Producer-side result of a ring write.</summary>
    public enum RingWriteStatus
    {
        Ok = 0,
        /// <summary>No room now; nothing was written (C3/C4/C12: re-queue; C11: drop and count; C8: overflow queue).</summary>
        Full = 1,
        /// <summary>The message is larger than half the ring (byte rings) or not one record (C8); never written.</summary>
        TooLarge = 2,
        /// <summary>Type 0 is reserved for the pad record.</summary>
        InvalidType = 3,
        /// <summary>The counters are impossible (the tail is ahead of the head, or more than a ring is in flight):
 /// the peer broke the protocol (e.g. zeroed a head). Nothing written; log it and wait for a re-sync.</summary>
        Corrupt = 4,
    }

    /// <summary>Consumer-side result of a ring read.</summary>
    public enum RingReadStatus
    {
        Ok = 0,
        Empty = 1,
        /// <summary>The counters or the framing are impossible (head behind tail, more than a ring in flight,
 /// a message crossing the end or the head). Nothing was consumed; re-synchronise (tail:= head) and ask for a resend.</summary>
        Corrupt = 2,
        /// <summary>The caller's buffer is smaller than the message; nothing was consumed.</summary>
        BufferTooSmall = 3,
    }

    /// <summary>
 /// SPSC fixed-record ring (C8 SnDamage, CONTRACT §5.3): slot = counter mod capacity; full when head − tail =
 /// capacity. Head and tail are u64 record counters in the ring-control page. The producer keeps its head locally
 /// (it owns the word), loaded once from the section at construction: a restarted producer continues from the head it
 /// finds and never zeroes it. Construct ring objects after the creator/attach step.
 /// </summary>
    public sealed unsafe class EntryRing
    {
        private readonly ulong* _head;
        private readonly ulong* _tail;
        private readonly byte* _slots;
        private readonly ulong _capacity;
        private readonly int _recordSize;
        private ulong _producerHead;
        private ulong _consumerTail;

        public long CorruptCount { get; private set; }

        /// <summary>The C8 ring of a mapped section (layout 1.0: 1,024 records of 128 B).</summary>
        public EntryRing(byte* sectionBase, RingIndex ring)
            : this(RingControl.HeadPtr(sectionBase, ring), RingControl.TailPtr(sectionBase, ring),
                   RingControl.DataPtr(sectionBase, ring), RingControl.Capacity(ring),
                   Layout.Region(Layout.RingRegion(ring)).RecordSize)
        {
            if (Layout.Region(Layout.RingRegion(ring)).Kind != RegionKind.RecordRing)
                throw new ArgumentException("ring " + ring + " is not a fixed-record ring");
        }

        /// <summary>A ring over arbitrary memory (tests, stand-ins).</summary>
        public EntryRing(ulong* head, ulong* tail, byte* slots, long capacityRecords, int recordSize)
        {
            if (head == null || tail == null || slots == null) throw new ArgumentNullException();
            if (capacityRecords <= 0) throw new ArgumentOutOfRangeException("capacityRecords");
            if (recordSize <= 0) throw new ArgumentOutOfRangeException("recordSize");
            _head = head; _tail = tail; _slots = slots; _capacity = (ulong)capacityRecords; _recordSize = recordSize;
            _producerHead = Volatile.Read(ref *head);
            _consumerTail = Volatile.Read(ref *tail);
        }

        public long Capacity { get { return (long)_capacity; } }
        public int RecordSize { get { return _recordSize; } }
        public ulong Head { get { return Volatile.Read(ref *_head); } }
        public ulong Tail { get { return Volatile.Read(ref *_tail); } }

        /// <summary>Records in flight as seen now (0 when the counters are inconsistent).</summary>
        public long Count
        {
            get
            {
                ulong h = Head, t = Tail;
                ulong n = h - t;
                return n > _capacity ? 0 : (long)n;
            }
        }

        /// <summary>Free slots as the producer sees them now.</summary>
        public long Free
        {
            get
            {
                ulong used = _producerHead - Volatile.Read(ref *_tail);
                return used >= _capacity ? 0 : (long)(_capacity - used);
            }
        }

        // ---- producer ----

        public RingWriteStatus TryWrite(byte* record, int size)
        {
            if (size != _recordSize) return RingWriteStatus.TooLarge;
            ulong h = _producerHead;
            ulong t = Volatile.Read(ref *_tail);
            ulong used = h - t;
            if (used > _capacity) { CorruptCount++; return RingWriteStatus.Corrupt; } // t > h or too much in flight: never overwrite
            if (used == _capacity) return RingWriteStatus.Full;
            byte* slot = _slots + (long)(h % _capacity) * _recordSize;
            Buffer.MemoryCopy(record, slot, _recordSize, _recordSize);
            h++;
            Volatile.Write(ref *_head, h); // release: the slot is complete before the head moves
            _producerHead = h;
            return RingWriteStatus.Ok;
        }

        public RingWriteStatus TryWrite<T>(T record) where T : unmanaged
        {
            return TryWrite((byte*)&record, sizeof(T));
        }

        // ---- consumer ----

        public RingReadStatus TryRead(byte* destination, int size)
        {
            if (size < _recordSize) return RingReadStatus.BufferTooSmall;
            ulong t = _consumerTail;
            ulong h = Volatile.Read(ref *_head);
            if (h == t) return RingReadStatus.Empty;
            if (h - t > _capacity) { CorruptCount++; return RingReadStatus.Corrupt; }
            byte* slot = _slots + (long)(t % _capacity) * _recordSize;
            Buffer.MemoryCopy(slot, destination, size, _recordSize);
            t++;
            Volatile.Write(ref *_tail, t); // release: the copy is complete before the slot is given back
            _consumerTail = t;
            return RingReadStatus.Ok;
        }

        public RingReadStatus TryRead<T>(out T record) where T : unmanaged
        {
            T tmp;
            RingReadStatus st = TryRead((byte*)&tmp, sizeof(T));
            record = st == RingReadStatus.Ok ? tmp : default(T);
            return st;
        }

        /// <summary>Consumer re-synchronisation (CONTRACT §5.2): tail:= head, dropping in-flight records. Never writes the head.</summary>
        public void ResyncTail()
        {
            ulong h = Volatile.Read(ref *_head);
            Volatile.Write(ref *_tail, h);
            _consumerTail = h;
        }

        /// <summary>The producer's local head (what it will publish next).</summary>
        public ulong ProducerHead { get { return _producerHead; } }
        public ulong ConsumerTail { get { return _consumerTail; } }
    }

    /// <summary>
 /// The C8 producer-side overflow queue (CONTRACT §5.1): records that do not fit wait here (FIFO) and are retried
 /// next frame; at most <see cref="Capacity"/> (4,096) records, beyond that the oldest is dropped and counted
 /// (C7 `damageDropped`). Single-threaded (Subnautica main thread).
 /// </summary>
    public sealed class OverflowQueue<T> where T : unmanaged
    {
        private readonly T[] _items;
        private int _first;
        private int _count;

        public OverflowQueue(int capacity = Layout.C8OverflowCapacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException("capacity");
            _items = new T[capacity];
        }

        public int Capacity { get { return _items.Length; } }
        public int Count { get { return _count; } }
        /// <summary>Records dropped (oldest first) because the queue was full; C7 `damageDropped`.</summary>
        public long Dropped { get; private set; }

        /// <summary>Appends; when full, drops the oldest record first and counts it.</summary>
        public void Enqueue(T item)
        {
            if (_count == _items.Length)
            {
                _first = (_first + 1) % _items.Length;
                _count--;
                Dropped++;
            }
            _items[(_first + _count) % _items.Length] = item;
            _count++;
        }

        public bool TryPeek(out T item)
        {
            if (_count == 0) { item = default(T); return false; }
            item = _items[_first];
            return true;
        }

        public bool TryDequeue(out T item)
        {
            if (!TryPeek(out item)) return false;
            _items[_first] = default(T);
            _first = (_first + 1) % _items.Length;
            _count--;
            return true;
        }

        /// <summary>Moves queued records into the ring in FIFO order until it is full; returns how many moved.</summary>
        public int FlushTo(EntryRing ring)
        {
            int moved = 0;
            T item;
            while (TryPeek(out item))
            {
                if (ring.TryWrite(item) != RingWriteStatus.Ok) break;
                TryDequeue(out item);
                moved++;
            }
            return moved;
        }

        /// <summary>
 /// Producer step for one new record: flushes the queue first (order is kept), then writes the record or queues
 /// it. Returns Ok when the record reached the ring, Full when it was queued.
 /// </summary>
        public RingWriteStatus WriteOrQueue(EntryRing ring, T item)
        {
            FlushTo(ring);
            RingWriteStatus st = RingWriteStatus.Full;
            if (_count == 0)
            {
                st = ring.TryWrite(item);
                if (st == RingWriteStatus.Ok) return st;
            }
            Enqueue(item);
            return st == RingWriteStatus.Corrupt ? st : RingWriteStatus.Full;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _first = 0; _count = 0;
        }
    }
}
