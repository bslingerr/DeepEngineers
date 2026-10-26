using System;
using System.Threading;

namespace DeepEngineers.Wire
{
    /// <summary>Outcome of one seqlock read attempt series.</summary>
    public enum SeqlockReadStatus
    {
        /// <summary>A coherent copy with a sequence different from the last one accepted.</summary>
        Fresh = 0,
        /// <summary>The sequence equals the last accepted one; the kept copy is current.</summary>
        Unchanged = 1,
        /// <summary>The writer kept the record busy (odd, or changed during the copy) for every try; the last good copy is kept.</summary>
        Busy = 2,
        /// <summary>`seq` is 0: nothing was ever published.</summary>
        Never = 3,
    }

    internal enum SeqlockMutant
    {
        None = 0,
        /// <summary>S0.M2: the writer skips the odd step (seq goes even → even after the payload).</summary>
        SkipOdd = 1,
    }

    /// <summary>
 /// Seqlock protocol of CONTRACT §5.3 over records whose first u32 (offset 0x00) is `seq`.
 /// Writer: `seq` odd (store, then fence), payload, `seq` even (release). Reader: read `seq` (acquire); if odd, pause
 /// and retry; copy; fence; accept only if `seq` is unchanged and non-zero; at most 16 tries per region per frame.
 /// One writer per record, ever. A restarted writer never resets `seq`: every write continues from the value found
 /// in the section (an odd value left by a writer that died mid-write stays odd until the next complete write).
 /// </summary>
    public static unsafe class Seqlock
    {
        /// <summary>CONTRACT §5.1/§5.3: ≤ 16 tries per region per frame.</summary>
        public const int MaxTries = 16;

        /// <summary>The current `seq` word (acquire).</summary>
        public static uint Current(byte* record)
        {
            return Volatile.Read(ref *(uint*)record);
        }

        /// <summary>
 /// The value a (re)started writer continues from: the word found in the section, never 0 by reset.
 /// The first value it publishes is <see cref="NextPublished"/>(found).
 /// </summary>
        public static uint ContinueFrom(byte* record)
        {
            return Current(record);
        }

        /// <summary>The even value a writer publishes after finding `found` (skips 0, which means "never written").</summary>
        public static uint NextPublished(uint found)
        {
            uint even = (found | 1u) + 1u;
            return even == 0 ? 2u : even;
        }

        /// <summary>Writer step 1: makes `seq` odd (continuing from the value found) and fences. Returns the odd value.</summary>
        public static uint BeginWrite(byte* record)
        {
            uint* seq = (uint*)record;
            uint odd = Volatile.Read(ref *seq) | 1u;
            Volatile.Write(ref *seq, odd);
            Thread.MemoryBarrier(); // the odd store is ordered before every payload store
            return odd;
        }

        /// <summary>Writer step 3: publishes the even value (release) after the payload was written in place.</summary>
        public static void EndWrite(byte* record, uint odd)
        {
            uint even = odd + 1u;
            if (even == 0) even = 2u;
            Volatile.Write(ref *(uint*)record, even);
        }

        /// <summary>Publishes a whole record. The `seq` field of <paramref name="value"/> is ignored.</summary>
        public static void Write<T>(byte* record, T value) where T : unmanaged
        {
            WriteCore(record, (byte*)&value, sizeof(T), SeqlockMutant.None);
        }

        /// <summary>Publishes `size` bytes from `source` (whose first 4 bytes, the seq slot, are ignored).</summary>
        public static void WriteBytes(byte* record, byte* source, int size)
        {
            WriteCore(record, source, size, SeqlockMutant.None);
        }

        /// <summary>Restart path of CONTRACT §5.2 / §6 creator rule 3: rewrites a record in place, continuing its `seq`.</summary>
        public static void RewriteInPlace<T>(byte* record, T value) where T : unmanaged
        {
            Write(record, value);
        }

        internal static void WriteCore(byte* record, byte* source, int size, SeqlockMutant mutant)
        {
            if (size < 4) throw new ArgumentOutOfRangeException("size", "a seqlock record starts with a u32 seq");
            int payload = size - 4;
            if (mutant == SeqlockMutant.SkipOdd)
            {
                uint found = Volatile.Read(ref *(uint*)record);
                Buffer.MemoryCopy(source + 4, record + 4, payload, payload);
                Thread.MemoryBarrier();
                uint even = (found & ~1u) + 2u;
                if (even == 0) even = 2u;
                Volatile.Write(ref *(uint*)record, even);
                return;
            }
            uint odd = BeginWrite(record);
            Buffer.MemoryCopy(source + 4, record + 4, payload, payload);
            EndWrite(record, odd);
        }

        /// <summary>Reader step 1 (for custom table readers): false when odd (busy) or 0 (never written).</summary>
        public static bool ReadBegin(byte* record, out uint seq)
        {
            seq = Volatile.Read(ref *(uint*)record);
            return seq != 0 && (seq & 1u) == 0;
        }

        /// <summary>Reader step 3 (for custom table readers): fence, then true when `seq` is still the value of ReadBegin.</summary>
        public static bool ReadEnd(byte* record, uint seq)
        {
            Thread.MemoryBarrier(); // every payload load completes before seq is read again
            return Volatile.Read(ref *(uint*)record) == seq;
        }

        /// <summary>
 /// Copies a coherent snapshot of `size` bytes into `destination` (≤ maxTries attempts). On Busy/Never the
 /// destination content is unspecified; keep your last good copy elsewhere (or use SeqlockReader).
 /// </summary>
        public static SeqlockReadStatus TryReadBytes(byte* record, byte* destination, int size, int maxTries, out uint seq)
        {
            if (size < 4) throw new ArgumentOutOfRangeException("size");
            if (maxTries < 1) maxTries = 1;
            for (int i = 0; i < maxTries; i++)
            {
                uint s1 = Volatile.Read(ref *(uint*)record);
                if (s1 == 0) { seq = 0; return SeqlockReadStatus.Never; }
                if ((s1 & 1u) != 0) { Thread.SpinWait(4); continue; }
                Buffer.MemoryCopy(record, destination, size, size);
                Thread.MemoryBarrier();
                uint s2 = Volatile.Read(ref *(uint*)record);
                if (s1 == s2) { *(uint*)destination = s1; seq = s1; return SeqlockReadStatus.Fresh; }
            }
            seq = 0;
            return SeqlockReadStatus.Busy;
        }

        /// <summary>Typed TryReadBytes: copies into <paramref name="value"/> only when coherent.</summary>
        public static SeqlockReadStatus TryRead<T>(byte* record, ref T value, out uint seq, int maxTries = MaxTries) where T : unmanaged
        {
            T tmp;
            SeqlockReadStatus st = TryReadBytes(record, (byte*)&tmp, sizeof(T), maxTries, out seq);
            if (st == SeqlockReadStatus.Fresh) value = tmp;
            return st;
        }
    }

    /// <summary>
 /// Per-region seqlock reader that keeps the last coherent copy (CONTRACT §5.1: "reader retries ≤ 16 times, else
 /// keeps its last coherent copy"). Not thread-safe; one per consumer thread. Call Reset when the peer generation
 /// changes or the section is re-opened (a new section starts its sequence again).
 /// </summary>
    public sealed unsafe class SeqlockReader<T> where T : unmanaged
    {
        private readonly byte* _record;
        private readonly int _maxTries;
        private T _last;
        private uint _lastSeq;
        private bool _hasValue;

        public long FreshCount { get; private set; }
        public long UnchangedCount { get; private set; }
        public long BusyCount { get; private set; }

        public SeqlockReader(byte* record, int maxTries = Seqlock.MaxTries)
        {
            if (record == null) throw new ArgumentNullException("record");
            _record = record;
            _maxTries = maxTries < 1 ? 1 : maxTries;
        }

        /// <summary>The last coherent copy (default until HasValue).</summary>
        public T Value { get { return _last; } }
        public bool HasValue { get { return _hasValue; } }
        public uint LastSeq { get { return _lastSeq; } }

        public SeqlockReadStatus Poll()
        {
            uint now = Volatile.Read(ref *(uint*)_record);
            if (_hasValue && now == _lastSeq) { UnchangedCount++; return SeqlockReadStatus.Unchanged; }
            T tmp;
            uint seq;
            SeqlockReadStatus st = Seqlock.TryReadBytes(_record, (byte*)&tmp, sizeof(T), _maxTries, out seq);
            if (st == SeqlockReadStatus.Fresh)
            {
                if (_hasValue && seq == _lastSeq) { UnchangedCount++; return SeqlockReadStatus.Unchanged; }
                _last = tmp; _lastSeq = seq; _hasValue = true; FreshCount++;
                return SeqlockReadStatus.Fresh;
            }
            if (st == SeqlockReadStatus.Busy) BusyCount++;
            return st;
        }

        public void Reset()
        {
            _last = default(T); _lastSeq = 0; _hasValue = false;
        }
    }
}
