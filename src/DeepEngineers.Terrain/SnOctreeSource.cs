using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace DeepEngineers.Terrain
{
    /// <summary>Counters of a <see cref="SnOctreeSource"/> (Interlocked; a snapshot).</summary>
    public sealed class SnSourceStats
    {
        /// <summary>Batches looked at (mapped, missing or bad).</summary>
        public long BatchesIndexed;
        /// <summary>Batch files memory-mapped (once each per source).</summary>
        public long BatchesMapped;
        public long BytesMapped;
        /// <summary>
 /// Batches treated as missing for the rest of the session: a version &lt; 4, node counts that do not consume the
 /// file exactly, or an I/O error that persisted through every retry (<see cref="SnOctreeSource.IoMaxAttempts"/>).
 /// </summary>
        public long BadBatches;
        /// <summary>Failed attempts to open or map a batch file (each one an I/O exception; retried with a backoff).</summary>
        public long IoFailures;
        /// <summary>Batches waiting for an I/O retry now.</summary>
        public long IoRetrying;
        /// <summary>Batches whose retry succeeded after at least one I/O failure.</summary>
        public long IoRecovered;
        /// <summary>
 /// Reads (samples, octree states, node lookups) answered "no data" because their batch was waiting for an I/O
 /// retry: SE may have cached that answer (an empty cell), so the plugin turns the fail-safe on (CONTRACT §7).
 /// </summary>
        public long FailedReads;
    }

    /// <summary>
 /// Two most recent leaves (or no-data regions) of one reader call, as world-voxel cubes [X0, X0 + Size)³. A stack
 /// value owned by the caller; valid only while the caller holds the source's read guard.
 /// </summary>
    internal unsafe struct LeafCache
    {
        /// <summary>The previous stencil of <see cref="SnField"/> (base voxel and the 8 samples, index x + 2y + 4z).</summary>
        public int SX, SY, SZ;
        public bool SValid;
        public fixed double SVal[8];
        public fixed byte SFar[8];

        private int _ax, _ay, _az, _as, _aLeaf;
        private byte _aType, _aDen;
        private int _bx, _by, _bz, _bs, _bLeaf;
        private byte _bType, _bDen;

        public bool TryGet(int vx, int vy, int vz, out byte type, out byte density, out int leafSize)
        {
            if (_as != 0 && (uint)(vx - _ax) < (uint)_as && (uint)(vy - _ay) < (uint)_as && (uint)(vz - _az) < (uint)_as)
            {
                type = _aType;
                density = _aDen;
                leafSize = _aLeaf;
                return true;
            }
            if (_bs != 0 && (uint)(vx - _bx) < (uint)_bs && (uint)(vy - _by) < (uint)_bs && (uint)(vz - _bz) < (uint)_bs)
            {
                type = _bType;
                density = _bDen;
                leafSize = _bLeaf;
                return true;
            }
            type = density = 0;
            leafSize = 0;
            return false;
        }

        /// <summary>Remembers a cube of side <paramref name="size"/> (leafSize 0 = no data there); the older entry is dropped.</summary>
        public void Put(int x0, int y0, int z0, int size, byte type, byte density, int leafSize)
        {
            _bx = _ax; _by = _ay; _bz = _az; _bs = _as; _bLeaf = _aLeaf; _bType = _aType; _bDen = _aDen;
            _ax = x0; _ay = y0; _az = z0; _as = size; _aLeaf = leafSize; _aType = type; _aDen = density;
        }
    }

    /// <summary>
 /// The shared, ref-counted reader of Subnautica's CompiledOctreesCache (CONTRACT 3.1 "Threading and budget").
 /// Batch files are memory-mapped read-only on first touch (shared read/write/delete, so Subnautica keeps reading
 /// them) and indexed lazily into preallocated per-octree tables (node offset, node count, state: ~11 MB for the
 /// 1,638,400 octrees). Thread-safe; no allocation in the steady state (only a batch's map and view, once).
 /// Unmapping: the last <see cref="Release"/> sets the closing flag, waits until no reader is inside mapped memory,
 /// then unmaps; afterwards reads answer "no data" and unindexed octrees classify as <see cref="OctreeState.Mixed"/>.
 /// <para>I/O failures: an exception while opening or mapping a batch file (a sharing
 /// violation, an antivirus scan, address space) is not a missing file. The batch then waits for a retry
 /// (<see cref="IoRetryBaseMs"/> · 2^(attempt − 1), at most <see cref="IoMaxAttempts"/> attempts, made by the next
 /// read that touches it after the delay or by <see cref="SnPrefetcher"/>); reads in between answer "no data" and are
 /// counted in <see cref="FailedReads"/>; after the last attempt the batch is bad for the session
 /// (<c>DE.Terrain BATCH-BAD … io …</c>). A file whose content is malformed is bad at once (no retry).</para>
 /// </summary>
    public sealed unsafe class SnOctreeSource
    {
        private const int ReadyUntouched = 0, ReadyData = 1, ReadyMissing = 2, ReadyBad = 3, ReadyIoRetry = 4;
        /// <summary>Ready codes of <see cref="ReadyOf"/> (0 untouched, 1 data, 2 no file, 3 bad, 4 waiting for an I/O retry).</summary>
        public const int StateUntouched = ReadyUntouched, StateData = ReadyData, StateMissing = ReadyMissing, StateBad = ReadyBad, StateIoRetry = ReadyIoRetry;
        private const int LockStripes = 64;
        private const int PageBytes = 4096;

        private readonly string _dir;
        private int _refs;
        private int _closing;
        private int _inFlight;
        private int _unmapped;

        private readonly int[] _ready = new int[SnOctreeFormat.BatchCount];
        private readonly MemoryMappedFile[] _mmf = new MemoryMappedFile[SnOctreeFormat.BatchCount];
        private readonly MemoryMappedViewAccessor[] _view = new MemoryMappedViewAccessor[SnOctreeFormat.BatchCount];
        private readonly IntPtr[] _base = new IntPtr[SnOctreeFormat.BatchCount];
        private readonly long[] _len = new long[SnOctreeFormat.BatchCount];
        private readonly long[] _retryAt = new long[SnOctreeFormat.BatchCount];      // Stopwatch ticks
        private readonly byte[] _ioAttempts = new byte[SnOctreeFormat.BatchCount];
        private readonly object[] _locks = new object[LockStripes];

        private readonly int[] _nodeOff = new int[SnOctreeFormat.OctreeCount];
        private readonly ushort[] _nodeCount = new ushort[SnOctreeFormat.OctreeCount];
        private readonly sbyte[] _state = new sbyte[SnOctreeFormat.OctreeCount];

        private long _batchesIndexed, _batchesMapped, _bytesMapped, _badBatches;
        private long _ioFailures, _ioRetrying, _ioRecovered, _failedReads;
        private int _pageSink;
        private int _ioRetryBaseMs = 250, _ioMaxAttempts = 6;

        private SnOctreeSource(string dir)
        {
            _dir = dir;
            _refs = 1;
            for (int i = 0; i < LockStripes; i++) _locks[i] = new object();
        }

        /// <summary>Opens a source over a CompiledOctreesCache folder (refcount 1; nothing mapped yet). Null + error when the folder is missing.</summary>
        public static SnOctreeSource Open(string octreeDir, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(octreeDir) || !Directory.Exists(octreeDir))
                {
                    error = "octree folder missing: " + (octreeDir ?? "(null)");
                    return null;
                }
                return new SnOctreeSource(Path.GetFullPath(octreeDir));
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        public string OctreeDir { get { return _dir; } }

        /// <summary>First I/O retry delay in ms (doubled per failed attempt). Default 250.</summary>
        public int IoRetryBaseMs { get { return _ioRetryBaseMs; } set { _ioRetryBaseMs = Math.Max(1, value); } }

        /// <summary>Attempts to open a batch file before it is bad for the session (1..250). Default 6 (≈ 7.75 s of retries).</summary>
        public int IoMaxAttempts { get { return _ioMaxAttempts; } set { _ioMaxAttempts = Math.Max(1, Math.Min(250, value)); } }

        /// <summary>Batches bad for the session (cheap; Interlocked).</summary>
        public long BadBatchCount { get { return Interlocked.Read(ref _badBatches); } }

        /// <summary>Reads answered "no data" because their batch waited for an I/O retry (cheap; Interlocked).</summary>
        public long FailedReads { get { return Interlocked.Read(ref _failedReads); } }

        public bool IsClosing { get { return Volatile.Read(ref _closing) != 0; } }

        /// <summary>Current reference count (diagnostics).</summary>
        public int RefCount { get { return Volatile.Read(ref _refs); } }

        /// <summary>Takes one more reference. Throws <see cref="ObjectDisposedException"/> once the source was released for good.</summary>
        public void AddRef()
        {
            while (true)
            {
                int r = Volatile.Read(ref _refs);
                if (r <= 0 || IsClosing) throw new ObjectDisposedException("SnOctreeSource");
                if (Interlocked.CompareExchange(ref _refs, r + 1, r) == r) return;
            }
        }

        /// <summary>Drops one reference; the last one closes (waits for in-flight readers, then unmaps every view).</summary>
        public void Release()
        {
            while (true)
            {
                int r = Volatile.Read(ref _refs);
                if (r <= 0) return;                      // extra Release: ignored
                if (Interlocked.CompareExchange(ref _refs, r - 1, r) != r) continue;
                if (r - 1 > 0) return;
                break;
            }
            Interlocked.Exchange(ref _closing, 1);
            var spin = new SpinWait();
            while (Volatile.Read(ref _inFlight) != 0) spin.SpinOnce();
            if (Interlocked.Exchange(ref _unmapped, 1) != 0) return;
            for (int b = 0; b < SnOctreeFormat.BatchCount; b++) Unmap(b);
        }

        private void Unmap(int b)
        {
            MemoryMappedViewAccessor v = _view[b];
            MemoryMappedFile f = _mmf[b];
            _view[b] = null;
            _mmf[b] = null;
            _base[b] = IntPtr.Zero;
            if (v != null)
            {
                try { v.SafeMemoryMappedViewHandle.ReleasePointer(); } catch (Exception) { }
                try { v.Dispose(); } catch (Exception) { }
            }
            if (f != null)
            {
                try { f.Dispose(); } catch (Exception) { }
            }
        }

        /// <summary>Enters mapped memory (false when closing: touch nothing, answer empty). Pair with <see cref="EndRead"/>.</summary>
        internal bool BeginRead()
        {
            Interlocked.Increment(ref _inFlight);
            if (Volatile.Read(ref _closing) != 0)
            {
                Interlocked.Decrement(ref _inFlight);
                return false;
            }
            return true;
        }

        internal void EndRead()
        {
            Interlocked.Decrement(ref _inFlight);
        }

        public SnSourceStats GetStats()
        {
            return new SnSourceStats
            {
                BatchesIndexed = Interlocked.Read(ref _batchesIndexed),
                BatchesMapped = Interlocked.Read(ref _batchesMapped),
                BytesMapped = Interlocked.Read(ref _bytesMapped),
                BadBatches = Interlocked.Read(ref _badBatches),
                IoFailures = Interlocked.Read(ref _ioFailures),
                IoRetrying = Interlocked.Read(ref _ioRetrying),
                IoRecovered = Interlocked.Read(ref _ioRecovered),
                FailedReads = Interlocked.Read(ref _failedReads),
            };
        }

        /// <summary>The ready code of batch slot <paramref name="slot"/> (<see cref="StateUntouched"/> … <see cref="StateIoRetry"/>); does not index.</summary>
        public int ReadyOf(int slot)
        {
            return Volatile.Read(ref _ready[slot]);
        }

        /// <summary>When a batch waiting for an I/O retry may be retried (Stopwatch ticks); 0 otherwise.</summary>
        public long RetryAtTicks(int slot)
        {
            return Volatile.Read(ref _ready[slot]) == ReadyIoRetry ? Volatile.Read(ref _retryAt[slot]) : 0;
        }

        /// <summary>
 /// Background prefetch: indexes batch slot <paramref name="slot"/>
 /// under the read guard (a due I/O retry is attempted) and, with <paramref name="fault"/>, touches one byte of
 /// every 4 KB page of its mapped file so later reads (the physics step's blocking shape requests among them) do
 /// not fault it in. Returns the ready code, or −1 while closing.
 /// </summary>
        public int Prefetch(int slot, bool fault, out long pagesTouched) { return Prefetch(slot, fault, out pagesTouched, null, null); }

        /// <summary>As above; <paramref name="indexBegin"/> / <paramref name="indexEnd"/> run around the indexing, which holds the
 /// batch's stripe lock (the prefetcher leaves Windows background mode there, so a reader waiting on that
 /// lock never waits behind very-low-priority I/O: 2.0 and 2.</summary>
        public int Prefetch(int slot, bool fault, out long pagesTouched, Action indexBegin, Action indexEnd)
        {
            pagesTouched = 0;
            if (slot < 0 || slot >= SnOctreeFormat.BatchCount) return ReadyMissing;
            if (!BeginRead()) return -1;
            try
            {
                int r;
                if (indexBegin != null) indexBegin();
                try { r = EnsureIndexed(slot); }
                finally { if (indexEnd != null) indexEnd(); }
                if (r == ReadyData && fault)
                {
                    byte* p = (byte*)_base[slot];
                    long len = _len[slot];
                    int sum = 0;
                    for (long o = 0; o < len; o += PageBytes)
                    {
                        sum += p[o];
                        pagesTouched++;
                    }
                    Interlocked.Add(ref _pageSink, sum);
                }
                return r;
            }
            finally { EndRead(); }
        }

        /// <summary>
 /// Class of the octree (ox, oy, oz) (octree indices, 0..127 x 0..99 x 0..127); outside the world = Missing.
 /// Indexes its batch on first touch; while closing, an unindexed batch answers <see cref="OctreeState.Mixed"/>.
 /// </summary>
        public OctreeState GetOctreeState(int ox, int oy, int oz)
        {
            if (!SnOctreeFormat.OctreeInWorld(ox, oy, oz)) return OctreeState.Missing;
            int b = SnOctreeFormat.BatchSlot(ox / SnOctreeFormat.OctreesPerBatch, oy / SnOctreeFormat.OctreesPerBatch, oz / SnOctreeFormat.OctreesPerBatch);
            int r = Volatile.Read(ref _ready[b]);
            if (r == ReadyUntouched || r == ReadyIoRetry)
            {
                if (!BeginRead()) return OctreeState.Mixed;
                try { r = EnsureIndexed(b); }
                finally { EndRead(); }
            }
            if (r == ReadyIoRetry)
            {
                Interlocked.Increment(ref _failedReads);
                return OctreeState.Missing;
            }
            return (OctreeState)_state[SnOctreeFormat.OctreeSlot(ox, oy, oz)];
        }

        /// <summary>
 /// State of an in-world octree; the caller holds a read guard (<paramref name="guarded"/> true) or not (then an
 /// unindexed batch answers Mixed without indexing).
 /// </summary>
        internal OctreeState StateCore(int ox, int oy, int oz, bool guarded)
        {
            int b = SnOctreeFormat.BatchSlot(ox / SnOctreeFormat.OctreesPerBatch, oy / SnOctreeFormat.OctreesPerBatch, oz / SnOctreeFormat.OctreesPerBatch);
            int r = Volatile.Read(ref _ready[b]);
            if (r == ReadyUntouched || r == ReadyIoRetry)
            {
                if (!guarded) return OctreeState.Mixed;
                r = EnsureIndexed(b);
            }
            if (r == ReadyIoRetry)
            {
                // the tables of a batch being retried are not read (a retry may be writing them under its lock)
                Interlocked.Increment(ref _failedReads);
                return OctreeState.Missing;
            }
            return (OctreeState)_state[SnOctreeFormat.OctreeSlot(ox, oy, oz)];
        }

        /// <summary>
 /// The leaf holding world voxel (vx, vy, vz) (indices 0..4095 x 0..3199 x 0..4095). False = no data (missing
 /// batch, empty octree, outside the world, or closing): treated as a far empty voxel.
 /// </summary>
        public bool TrySampleVoxel(int vx, int vy, int vz, out byte type, out byte density, out int leafSize)
        {
            if (!BeginRead())
            {
                type = density = 0;
                leafSize = 0;
                return false;
            }
            try { return SampleCore(vx, vy, vz, out type, out density, out leafSize); }
            finally { EndRead(); }
        }

        /// <summary><see cref="TrySampleVoxel"/> without the guard: the caller holds <see cref="BeginRead"/>.</summary>
        internal bool SampleCore(int vx, int vy, int vz, out byte type, out byte density, out int leafSize)
        {
            var cache = new LeafCache();
            return SampleCached(vx, vy, vz, ref cache, out type, out density, out leafSize);
        }

        /// <summary>
 /// <see cref="SampleCore(int, int, int, out byte, out byte, out int)"/> through a caller-owned two-entry cache of the
 /// last leaves (or no-data regions) found. Valid only while the caller holds the guard; a stack value, so no
 /// allocation and no thread-local access.
 /// </summary>
        internal bool SampleCached(int vx, int vy, int vz, ref LeafCache cache, out byte type, out byte density, out int leafSize)
        {
            if (TerrainMutants.OneVoxelOffset) vx += 1;
            if (TerrainMutants.SwapXZ && vx >= 0 && vz >= 0)
            {
                int bx = vx / SnOctreeFormat.BatchVoxels, bz = vz / SnOctreeFormat.BatchVoxels;
                int lx = vx - bx * SnOctreeFormat.BatchVoxels, lz = vz - bz * SnOctreeFormat.BatchVoxels;
                vx = bx * SnOctreeFormat.BatchVoxels + lz;
                vz = bz * SnOctreeFormat.BatchVoxels + lx;
            }
            if ((uint)vx >= SnOctreeFormat.VoxelsX || (uint)vy >= SnOctreeFormat.VoxelsY || (uint)vz >= SnOctreeFormat.VoxelsZ)
            {
                type = density = 0;
                leafSize = 0;
                return false;
            }
            if (cache.TryGet(vx, vy, vz, out type, out density, out leafSize)) return leafSize != 0;
            int ox = vx >> 5, oy = vy >> 5, oz = vz >> 5;
            int b = SnOctreeFormat.BatchSlot(ox / SnOctreeFormat.OctreesPerBatch, oy / SnOctreeFormat.OctreesPerBatch, oz / SnOctreeFormat.OctreesPerBatch);
            int r = Volatile.Read(ref _ready[b]);
            if (r == ReadyUntouched || r == ReadyIoRetry) r = EnsureIndexed(b);
            if (r != ReadyData)
            {
                // a missing or bad batch: the whole 160 m batch has no data (one waiting for an I/O retry: counted)
                if (r == ReadyIoRetry) Interlocked.Increment(ref _failedReads);
                const int K = SnOctreeFormat.OctreesPerBatch * SnOctreeFormat.OctreeSize;
                cache.Put(vx - vx % K, vy - vy % K, vz - vz % K, K, 0, 0, 0);
                return false;
            }
            int slot = SnOctreeFormat.OctreeSlot(ox, oy, oz);
            int n = _nodeCount[slot];
            if (n == 0)
            {
                cache.Put(vx & ~31, vy & ~31, vz & ~31, SnOctreeFormat.OctreeSize, 0, 0, 0);
                return false;
            }
            byte* p = (byte*)_base[b] + _nodeOff[slot];
            int node = 0, size = SnOctreeFormat.OctreeSize;
            int x = vx & 31, y = vy & 31, z = vz & 31;
            bool zyx = TerrainMutants.ChildOrderZYX;
            while (true)
            {
                byte* q = p + node * SnOctreeFormat.BytesPerNode;
                int fc = q[2] | (q[3] << 8);
                if (fc == 0 || size == 1 || fc + 8 > n)
                {
                    type = q[0];
                    density = q[1];
                    leafSize = size;
                    cache.Put(vx - x, vy - y, vz - z, size, type, density, size);
                    return true;
                }
                int h = size >> 1;
                int xb = x >= h ? 1 : 0, yb = y >= h ? 1 : 0, zb = z >= h ? 1 : 0;
                node = fc + (zyx ? (zb << 2) | (yb << 1) | xb : (xb << 2) | (yb << 1) | zb);   // SnOctreeFormat.ChildIndex
                x -= xb * h;
                y -= yb * h;
                z -= zb * h;
                size = h;
            }
        }

        /// <summary>The node array of an in-world octree (caller holds the guard); false when it has no nodes or no file.</summary>
        internal bool TryGetNodes(int ox, int oy, int oz, out byte* nodes, out int count)
        {
            nodes = null;
            count = 0;
            int b = SnOctreeFormat.BatchSlot(ox / SnOctreeFormat.OctreesPerBatch, oy / SnOctreeFormat.OctreesPerBatch, oz / SnOctreeFormat.OctreesPerBatch);
            int r = Volatile.Read(ref _ready[b]);
            if (r == ReadyUntouched || r == ReadyIoRetry) r = EnsureIndexed(b);
            if (r != ReadyData)
            {
                if (r == ReadyIoRetry) Interlocked.Increment(ref _failedReads);
                return false;
            }
            int slot = SnOctreeFormat.OctreeSlot(ox, oy, oz);
            count = _nodeCount[slot];
            if (count == 0) return false;
            nodes = (byte*)_base[b] + _nodeOff[slot];
            return true;
        }

        /// <summary>
 /// Class of the leaves of an in-world octree that overlap the octree-local voxel box [x0..x1] x [y0..y1] x
 /// [z0..z1] (inclusive, clipped to 0..31; the caller holds the guard): Empty when every such leaf is far empty
 /// (or the octree has no data), Solid when every one is far solid, else Mixed (a near-surface leaf, or both).
 /// Refines a Mixed octree state; exact, since a sample's value is its leaf's.
 /// </summary>
        internal OctreeState ClassifyLeaves(int ox, int oy, int oz, int x0, int y0, int z0, int x1, int y1, int z1)
        {
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (z0 < 0) z0 = 0;
            if (x1 > 31) x1 = 31;
            if (y1 > 31) y1 = 31;
            if (z1 > 31) z1 = 31;
            if (x1 < x0 || y1 < y0 || z1 < z0) return OctreeState.Empty;
            byte* nodes;
            int count;
            if (!TryGetNodes(ox, oy, oz, out nodes, out count)) return OctreeState.Empty;
            int flags = 0;                                   // 1 = empty leaf seen, 2 = solid leaf seen, 4 = mixed
            LeafWalk(nodes, count, 0, 0, 0, 0, SnOctreeFormat.OctreeSize, x0, y0, z0, x1, y1, z1, ref flags);
            if ((flags & 4) != 0 || flags == 3) return OctreeState.Mixed;
            return flags == 2 ? OctreeState.Solid : OctreeState.Empty;
        }

        private static void LeafWalk(byte* nodes, int count, int node, int nx, int ny, int nz, int size,
                                     int x0, int y0, int z0, int x1, int y1, int z1, ref int flags)
        {
            if (nx > x1 || ny > y1 || nz > z1 || nx + size - 1 < x0 || ny + size - 1 < y0 || nz + size - 1 < z0) return;
            byte* q = nodes + node * SnOctreeFormat.BytesPerNode;
            int fc = q[2] | (q[3] << 8);
            if (fc == 0 || size == 1 || fc + 8 > count)
            {
                if (q[1] != 0) flags |= 4;
                else flags |= q[0] > 0 ? 2 : 1;
                return;
            }
            int h = size >> 1;
            for (int c = 0; c < 8 && (flags & 4) == 0 && flags != 3; c++)
            {
                int xb = (c >> 2) & 1, yb = (c >> 1) & 1, zb = c & 1;
                LeafWalk(nodes, count, fc + SnOctreeFormat.ChildIndex(xb, yb, zb), nx + xb * h, ny + yb * h, nz + zb * h, h,
                         x0, y0, z0, x1, y1, z1, ref flags);
            }
        }

        /// <summary>
 /// Indexes batch slot b if needed (caller holds the guard), or retries one waiting for an I/O retry whose delay
 /// has passed. Returns the ready code (<see cref="ReadyIoRetry"/> while the batch waits: no data for now).
 /// </summary>
        internal int EnsureIndexed(int b)
        {
            int r = Volatile.Read(ref _ready[b]);
            if (r != ReadyUntouched && r != ReadyIoRetry) return r;
            if (r == ReadyIoRetry && Stopwatch.GetTimestamp() < Volatile.Read(ref _retryAt[b])) return r;
            lock (_locks[b & (LockStripes - 1)])
            {
                r = _ready[b];
                if (r != ReadyUntouched && r != ReadyIoRetry) return r;
                bool retry = r == ReadyIoRetry;
                if (retry && Stopwatch.GetTimestamp() < Volatile.Read(ref _retryAt[b])) return r;
                string why;
                r = IndexBatch(b, out why);
                if (r == ReadyIoRetry)
                {
                    int attempt = ++_ioAttempts[b];
                    Interlocked.Increment(ref _ioFailures);
                    if (attempt >= _ioMaxAttempts)
                    {
                        r = ReadyBad;
                        if (retry) Interlocked.Decrement(ref _ioRetrying);
                        Interlocked.Increment(ref _badBatches);
                        TerrainLog.Write("DE.Terrain BATCH-BAD " + BatchName(b) + " io " + why + " (after " + attempt + " attempts; no data for the rest of the session)");
                    }
                    else
                    {
                        long delayMs = (long)_ioRetryBaseMs << Math.Min(attempt - 1, 20);
                        Volatile.Write(ref _retryAt[b], Stopwatch.GetTimestamp() + delayMs * Stopwatch.Frequency / 1000);
                        if (!retry) Interlocked.Increment(ref _ioRetrying);
                        TerrainLog.Write("DE.Terrain BATCH-IO " + BatchName(b) + " attempt " + attempt + "/" + _ioMaxAttempts + " " + why +
                                         " (no data until the retry in " + delayMs + " ms)");
                    }
                }
                else if (retry)
                {
                    Interlocked.Decrement(ref _ioRetrying);
                    if (r == ReadyData)
                    {
                        Interlocked.Increment(ref _ioRecovered);
                        TerrainLog.Write("DE.Terrain BATCH-IO " + BatchName(b) + " recovered after " + _ioAttempts[b] + " failed attempt(s)");
                    }
                }
                if (!retry) Interlocked.Increment(ref _batchesIndexed);
                Volatile.Write(ref _ready[b], r);
                return r;
            }
        }

        private static string BatchName(int b)
        {
            int bz = b % SnOctreeFormat.BatchesZ;
            int by = (b / SnOctreeFormat.BatchesZ) % SnOctreeFormat.BatchesY;
            int bx = b / (SnOctreeFormat.BatchesZ * SnOctreeFormat.BatchesY);
            return SnOctreeFormat.BatchFileName(bx, by, bz);
        }

        /// <summary>Ready code of a batch (0 untouched, 1 data, 2 no file, 3 bad, 4 waiting for an I/O retry); does not index.</summary>
        internal int BatchReady(int bx, int by, int bz)
        {
            return Volatile.Read(ref _ready[SnOctreeFormat.BatchSlot(bx, by, bz)]);
        }

        /// <summary>
 /// Maps and walks batch slot b (under its stripe lock). Returns <see cref="ReadyData"/>, <see cref="ReadyMissing"/>
 /// (no file), <see cref="ReadyBad"/> (malformed: logged and counted here) or <see cref="ReadyIoRetry"/> (an I/O
 /// exception, <paramref name="ioWhy"/>: the caller counts, logs and schedules the retry).
 /// </summary>
        private int IndexBatch(int b, out string ioWhy)
        {
            ioWhy = null;
            int bz = b % SnOctreeFormat.BatchesZ;
            int by = (b / SnOctreeFormat.BatchesZ) % SnOctreeFormat.BatchesY;
            int bx = b / (SnOctreeFormat.BatchesZ * SnOctreeFormat.BatchesY);
            string name = SnOctreeFormat.BatchFileName(bx, by, bz);
            string path = Path.Combine(_dir, name);
            if (!File.Exists(path))
            {
                MarkMissing(bx, by, bz);
                return ReadyMissing;
            }
            FileStream fs = null;
            MemoryMappedFile mmf = null;
            MemoryMappedViewAccessor view = null;
            bool pointerTaken = false;
            string bad = null;
            bool io = false;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
                long len = fs.Length;
                if (len < 4)
                {
                    bad = "short file (" + len + " bytes)";
                }
                else
                {
#if NETFRAMEWORK
                    mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, null, HandleInheritability.None, false);
#else
                    mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
#endif
                    fs = null;                           // owned by the map now
                    view = mmf.CreateViewAccessor(0, len, MemoryMappedFileAccess.Read);
                    byte* p = null;
                    view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                    pointerTaken = true;
                    p += view.PointerOffset;
                    bad = Walk(p, len, bx, by, bz);
                    if (bad == null)
                    {
                        _mmf[b] = mmf;
                        _view[b] = view;
                        _base[b] = (IntPtr)p;
                        _len[b] = len;
                        Interlocked.Increment(ref _batchesMapped);
                        Interlocked.Add(ref _bytesMapped, len);
                        return ReadyData;
                    }
                }
            }
            catch (Exception e)
            {
                bad = e.GetType().Name + ": " + e.Message;
                io = true;
            }
            if (view != null)
            {
                if (pointerTaken) { try { view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch (Exception) { } }
                try { view.Dispose(); } catch (Exception) { }
            }
            if (mmf != null) { try { mmf.Dispose(); } catch (Exception) { } }
            if (fs != null) { try { fs.Dispose(); } catch (Exception) { } }
            MarkMissing(bx, by, bz);
            if (io)
            {
                ioWhy = bad;
                return ReadyIoRetry;
            }
            Interlocked.Increment(ref _badBatches);
            TerrainLog.Write("DE.Terrain BATCH-BAD " + name + " " + bad);
            return ReadyBad;
        }

        /// <summary>Walks the version and the node counts; fills the octree tables. Returns null when the file is consumed exactly.</summary>
        private string Walk(byte* p, long len, int bx, int by, int bz)
        {
            int version = p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24);
            if (version < SnOctreeFormat.MinFileVersion) return "version " + version + " < " + SnOctreeFormat.MinFileVersion;
            long off = 4;
            const int K = SnOctreeFormat.OctreesPerBatch;
            for (int ix = 0; ix < K; ix++)
                for (int iy = 0; iy < K; iy++)
                    for (int iz = 0; iz < K; iz++)
                    {
                        int ox = bx * K + ix, oy = by * K + iy, oz = bz * K + iz;
                        if (!SnOctreeFormat.OctreeInWorld(ox, oy, oz)) continue;
                        if (off + 2 > len) return "truncated at octree " + ix + "," + iy + "," + iz;
                        int n = p[off] | (p[off + 1] << 8);
                        off += 2;
                        if (off + (long)n * SnOctreeFormat.BytesPerNode > len) return "truncated nodes at octree " + ix + "," + iy + "," + iz;
                        int slot = SnOctreeFormat.OctreeSlot(ox, oy, oz);
                        _nodeOff[slot] = (int)off;
                        _nodeCount[slot] = (ushort)n;
                        _state[slot] = (sbyte)ClassifyOctree(p + off, n);
                        off += (long)n * SnOctreeFormat.BytesPerNode;
                    }
            if (off != len) return "not consumed exactly (" + off + " of " + len + " bytes)";
            return null;
        }

        /// <summary>sn_octree_stats.py's classes: 0 nodes or one far empty node = Empty; one far solid node = Solid; else Mixed.</summary>
        private static OctreeState ClassifyOctree(byte* nodes, int n)
        {
            if (n == 0) return OctreeState.Empty;
            if (n == 1)
            {
                if (nodes[1] == 0) return nodes[0] > 0 ? OctreeState.Solid : OctreeState.Empty;
                return OctreeState.Mixed;
            }
            return OctreeState.Mixed;
        }

        private void MarkMissing(int bx, int by, int bz)
        {
            const int K = SnOctreeFormat.OctreesPerBatch;
            for (int ix = 0; ix < K; ix++)
                for (int iy = 0; iy < K; iy++)
                    for (int iz = 0; iz < K; iz++)
                    {
                        int ox = bx * K + ix, oy = by * K + iy, oz = bz * K + iz;
                        if (!SnOctreeFormat.OctreeInWorld(ox, oy, oz)) continue;
                        int slot = SnOctreeFormat.OctreeSlot(ox, oy, oz);
                        _nodeCount[slot] = 0;
                        _nodeOff[slot] = 0;
                        _state[slot] = (sbyte)OctreeState.Missing;
                    }
        }
    }
}
