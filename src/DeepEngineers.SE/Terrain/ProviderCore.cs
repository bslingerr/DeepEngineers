using System;
using System.Diagnostics;
using System.Threading;
using DeepEngineers.Terrain;

namespace DeepEngineers.SE.Terrain
{
    /// <summary>
 /// The VRage-free core of <c>SnTerrainProvider</c> (CONTRACT §3.1 member table): request flags, <c>detectOnly</c>,
 /// writes into the target arrays, box and line classification, close and the S4 mutant cell. The provider is a thin
 /// adapter that unpacks <c>MyVoxelDataRequest</c>/<c>MyStorageData</c> into plain arrays and ints and calls
 /// <see cref="Read"/>; tests/DeepEngineers.SE.Tests drives this class directly (S4.N3).
 /// <para>Thread-safe (SE reads one tile from many worker threads at once) and allocation-free: no field is written by
 /// a read except Interlocked counters. A <c>null</c> core is the inert provider (parameterless constructor,
 /// <c>ReadFrom</c>): every read answers empty, every box Disjoint, every line false. So does a closed core.</para>
 /// Constant values VERIFIED in the SE 1.210 decomp: <c>MyVoxelRequestFlags</c> (VRage/VRage.Voxels/
 /// MyVoxelRequestFlags.cs), <c>MyStorageDataTypeFlags</c> (Content 1, Material 2), <c>VRageMath.ContainmentType</c>
 /// (Disjoint 0, Contains 1, Intersects 2), <c>MyStorageData</c> linear index <c>x + y·StepY + z·StepZ</c>.
 /// </summary>
    internal sealed class ProviderCore
    {
        // MyVoxelRequestFlags
        public const int RfSurfaceMaterial = 1, RfConsiderContent = 2, RfForPhysics = 4, RfEmptyData = 8, RfFullContent = 0x10,
                         RfOneMaterial = 0x20, RfAdviseCache = 0x40, RfContentChecked = 0x80, RfContentCheckedDeep = 0x100,
                         RfUseNativeProvider = 0x200, RfPostprocess = 0x400;
        /// <summary>The five output bits the provider owns (cleared from the input, then set as proven).</summary>
        public const int RfOutputMask = RfEmptyData | RfFullContent | RfContentChecked | RfContentCheckedDeep | RfOneMaterial;
        // MyStorageDataTypeFlags
        public const int DtContent = 1, DtMaterial = 2;
        // VRageMath.ContainmentType
        public const int CtDisjoint = 0, CtContains = 1, CtIntersects = 2;
        public const byte ContentEmpty = 0, ContentFull = 255, MaterialNone = 255;
        /// <summary>The highest LOD a 1024³ tile has (its single provider leaf, VERIFIED MyOctreeStorage.ResetInternal).</summary>
        public const int MaxLod = 10;
        /// <summary>The S4 mutant cell is one 8 m physics cell.</summary>
        public const int MutantCellSize = 8;

        /// <summary>Every non-detectOnly ReadRange of every tile.</summary>
        public static readonly ReadStats Stats = new ReadStats();
        /// <summary>The ForPhysics subset, per call (SE reads one 8 m physics cell with two calls: content, then material).</summary>
        public static readonly ReadStats PhysicsStats = new ReadStats();
        /// <summary>
 /// Per 8 m physics cell: the ForPhysics content read plus the material read SE
 /// makes right after it on the same thread for the same range
 /// S4.A-T1(3)'s "&lt; 1 ms per 8 m physics cell" is judged on this.
 /// </summary>
        public static readonly ReadStats PhysicsCellStats = new ReadStats();
        /// <summary>Requests whose range did not fit the target array (never expected; the type is then not written).</summary>
        public static long BadTargets;
        /// <summary>ReadRange calls that threw inside the provider (the target may hold a partial write): the plugin turns the fail-safe on.</summary>
        public static long ReadErrors;
        private static int _badTargetLogged;

        // the content half of a physics cell read, waiting for its material half on this thread (allocation-free TLS)
        [ThreadStatic] private static bool _cellPending;
        [ThreadStatic] private static long _cellTicks;
        [ThreadStatic] private static int _cellLod, _cellX0, _cellY0, _cellZ0, _cellX1, _cellY1, _cellZ1;

        private readonly TileSampler _sampler;
        private Action _release;
        private int _closed;
        private int _cellX = int.MinValue, _cellY, _cellZ;

        public readonly int I, J, K;
        public readonly long EntityId;

        /// <param name="sampler">the tile's sampler (never null)</param>
 /// <param name="release">called once by <see cref="Close"/> (the provider passes the shared source's Release)</param>
        public ProviderCore(TileSampler sampler, int i, int j, int k, long entityId, Action release)
        {
            if (sampler == null) throw new ArgumentNullException("sampler");
            _sampler = sampler;
            _release = release;
            I = i; J = j; K = k;
            EntityId = entityId;
        }

        public TileSampler Sampler { get { return _sampler; } }
        public bool IsClosed { get { return Volatile.Read(ref _closed) != 0; } }

        /// <summary>Idempotent: the first call marks the core closed and runs the release callback once.</summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Action r = Interlocked.Exchange(ref _release, null);
            if (r == null) return;
            try { r(); }
            catch (Exception e) { DeLog.Info("DE.Terrain WARN provider " + I + "," + J + "," + K + " release failed " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>The tile-local LOD-0 min corner of the S4.M3/M8/M8b cell (8 voxels per side), set by the tile creator.</summary>
        public void SetMutantCell(int x0, int y0, int z0)
        {
            _cellY = y0;
            _cellZ = z0;
            Volatile.Write(ref _cellX, x0);
        }

        public bool TryGetMutantCell(out int x0, out int y0, out int z0)
        {
            x0 = Volatile.Read(ref _cellX);
            y0 = _cellY;
            z0 = _cellZ;
            return x0 != int.MinValue;
        }

        private bool TouchesMutantCell(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            int cx, cy, cz;
            if (!TryGetMutantCell(out cx, out cy, out cz)) return false;
            return x0 <= cx + MutantCellSize - 1 && x1 >= cx && y0 <= cy + MutantCellSize - 1 && y1 >= cy && z0 <= cz + MutantCellSize - 1 && z1 >= cz;
        }

        private bool ContainsMutantCellCentre(int x0, int y0, int z0, int x1, int y1, int z1)
        {
            int cx, cy, cz;
            if (!TryGetMutantCell(out cx, out cy, out cz)) return false;
            int h = MutantCellSize / 2;
            return x0 <= cx + h && x1 >= cx + h && y0 <= cy + h && y1 >= cy + h && z0 <= cz + h && z1 >= cz + h;
        }

        // ======================================================================================================= read

 /// <summary>
 /// <c>IMyStorageDataProvider.ReadRange(ref MyVoxelDataRequest, bool detectOnly)</c> without VRage types. Range
 /// <paramref name="minX"/>…<paramref name="maxZ"/> is inclusive in LOD <paramref name="lod"/> coordinates of this
 /// tile; sample (x, y, z) goes to <c>array[baseIndex + (x − minX) + (y − minY)·sY + (z − minZ)·sZ]</c>.
 /// <paramref name="content"/>/<paramref name="material"/> are the target's arrays for the requested types (null
 /// when the type is not requested or the target has no array for it: nothing is written then). With
 /// <paramref name="detectOnly"/> the arrays are never touched. Returns the output <c>Flags</c>:
 /// <c>requestFlags &amp; ~RfOutputMask</c>, plus (request path) <c>EmptyData</c> iff something was written and every
 /// written value is its empty value (content 0, material 255) and <c>FullContent</c> iff content was written and
 /// every content byte is 255; (detectOnly) <c>EmptyData|ContentChecked</c> when the box classifies Empty,
 /// <c>FullContent|ContentChecked</c> when it classifies Full and content is requested, otherwise none.
 /// </summary>
        public static int Read(ProviderCore core, int lod, int minX, int minY, int minZ, int maxX, int maxY, int maxZ,
                               int requestedData, int requestFlags, byte[] content, byte[] material, int baseIndex, int sY, int sZ, bool detectOnly)
        {
            int flags = requestFlags & ~RfOutputMask;
            if (lod < 0 || lod > MaxLod || maxX < minX || maxY < minY || maxZ < minZ) return flags; // SE forwards lod ≤ 10 only (VERIFIED ReadRangeInternal)
            if (detectOnly) return flags | Detect(core, lod, minX, minY, minZ, maxX, maxY, maxZ, requestedData);

            long t0 = Stopwatch.GetTimestamp();
            int nx = maxX - minX + 1, ny = maxY - minY + 1, nz = maxZ - minZ + 1;
            bool contentOk = content != null && Fits(content, baseIndex, nx, ny, nz, sY, sZ);
            bool materialOk = material != null && Fits(material, baseIndex, nx, ny, nz, sY, sZ);
            if ((content != null && !contentOk) || (material != null && !materialOk)) BadTarget(lod, minX, minY, minZ, maxX, maxY, maxZ, baseIndex, sY, sZ);

            bool wrote = false, allEmpty = true, contentFull = false;
            if (core == null || core.IsClosed)
            {
                if (contentOk) { Fill(content, baseIndex, nx, ny, nz, sY, sZ, ContentEmpty); wrote = true; }
                if (materialOk) { Fill(material, baseIndex, nx, ny, nz, sY, sZ, MaterialNone); wrote = true; }
            }
            else
            {
                if (contentOk)
                {
                    bool e, f;
                    core._sampler.ReadContent(lod, minX, minY, minZ, maxX, maxY, maxZ, content, baseIndex, sY, sZ, out e, out f);
                    wrote = true;
                    allEmpty &= e;
                    contentFull = f;
                }
                if (materialOk)
                {
                    bool e;
                    core._sampler.ReadMaterial(lod, minX, minY, minZ, maxX, maxY, maxZ, material, baseIndex, sY, sZ, out e);
                    wrote = true;
                    allEmpty &= e;
                }
                if (S4Mutants.EmptyDataOnSurface && wrote &&
                    core.ContainsMutantCellCentre(minX << lod, minY << lod, minZ << lod, ((maxX + 1) << lod) - 1, ((maxY + 1) << lod) - 1, ((maxZ + 1) << lod) - 1))
                {
                    allEmpty = true;     // S4.M8: a lie, the bytes stay as written
                    contentFull = false;
                }
            }
            if (wrote && allEmpty) flags |= RfEmptyData;
            else if (contentOk && contentFull) flags |= RfFullContent;

            long dt = Stopwatch.GetTimestamp() - t0;
            Stats.Add(dt);
            if ((requestFlags & RfForPhysics) != 0)
            {
                PhysicsStats.Add(dt);
                PairPhysicsCell(lod, minX, minY, minZ, maxX, maxY, maxZ, requestedData, (flags & RfEmptyData) != 0, dt);
            }
            return flags;
        }

        /// <summary>
 /// Pairs SE's two physics reads of one cell (content, then material, same thread, same range) into one
 /// <see cref="PhysicsCellStats"/> sample. A content read that answered EmptyData is a whole cell (SE skips the
 /// material read); a content read whose material read never came is counted alone when the thread's next content
 /// read arrives; an unpaired material read, or one call with both types, is a cell by itself.
 /// </summary>
        private static void PairPhysicsCell(int lod, int x0, int y0, int z0, int x1, int y1, int z1, int requestedData, bool emptyData, long dt)
        {
            bool c = (requestedData & DtContent) != 0, m = (requestedData & DtMaterial) != 0;
            if (c && !m)
            {
                if (_cellPending) PhysicsCellStats.Add(_cellTicks);
                if (emptyData)
                {
                    _cellPending = false;
                    PhysicsCellStats.Add(dt);
                    return;
                }
                _cellPending = true;
                _cellTicks = dt;
                _cellLod = lod; _cellX0 = x0; _cellY0 = y0; _cellZ0 = z0; _cellX1 = x1; _cellY1 = y1; _cellZ1 = z1;
                return;
            }
            if (m && !c && _cellPending && _cellLod == lod && _cellX0 == x0 && _cellY0 == y0 && _cellZ0 == z0 && _cellX1 == x1 && _cellY1 == y1 && _cellZ1 == z1)
            {
                _cellPending = false;
                PhysicsCellStats.Add(_cellTicks + dt);
                return;
            }
            PhysicsCellStats.Add(dt);
        }

        /// <summary>Counts a ReadRange that threw inside the provider (<see cref="ReadErrors"/>).</summary>
        public static void CountReadError()
        {
            Interlocked.Increment(ref ReadErrors);
        }

        /// <summary>World load: the statistics and error counters start again for each world.</summary>
        public static void ResetStats()
        {
            Stats.Reset();
            PhysicsStats.Reset();
            PhysicsCellStats.Reset();
            Interlocked.Exchange(ref BadTargets, 0);
            Interlocked.Exchange(ref ReadErrors, 0);
            Interlocked.Exchange(ref _badTargetLogged, 0);
        }

        private static int Detect(ProviderCore core, int lod, int minX, int minY, int minZ, int maxX, int maxY, int maxZ, int requestedData)
        {
            if (core == null || core.IsClosed) return RfEmptyData | RfContentChecked;
            int x0 = minX << lod, y0 = minY << lod, z0 = minZ << lod;
            int x1 = ((maxX + 1) << lod) - 1, y1 = ((maxY + 1) << lod) - 1, z1 = ((maxZ + 1) << lod) - 1;
            if (S4Mutants.EmptyDataDetectOnly && core.TouchesMutantCell(x0, y0, z0, x1, y1, z1)) return RfEmptyData | RfContentChecked; // S4.M8b
            ContentClass c = core._sampler.Classify(x0, y0, z0, x1, y1, z1);
            if (c == ContentClass.Empty) return RfEmptyData | RfContentChecked;
            if (c == ContentClass.Full && (requestedData & DtContent) != 0) return RfFullContent | RfContentChecked;
            return 0;
        }

        // ================================================================================================== intersect

 /// <summary>
 /// <c>Intersect(BoundingBoxI box, int lod)</c>: the box is in LOD-0 storage voxels, inclusive (VERIFIED callers);
 /// <paramref name="lod"/> is only the caller's meshing LOD and is ignored. Empty → Disjoint, Full → Contains,
 /// Mixed → Intersects; never Disjoint/Contains unless the core's classification proves it.
 /// </summary>
        public static int IntersectBox(ProviderCore core, int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            if (core == null || core.IsClosed) return CtDisjoint;
            if (maxX < minX || maxY < minY || maxZ < minZ) return CtDisjoint;
            if (S4Mutants.IntersectHole && core.TouchesMutantCell(minX, minY, minZ, maxX, maxY, maxZ)) return CtDisjoint; // S4.M3
            ContentClass c = core._sampler.Classify(minX, minY, minZ, maxX, maxY, maxZ);
            return c == ContentClass.Empty ? CtDisjoint : c == ContentClass.Full ? CtContains : CtIntersects;
        }

        /// <summary>
 /// <c>Intersect(ref LineD, out start, out end)</c> in storage-local metres (LOD-0 voxel units from tileMin):
 /// false when every ≤ 16 m step of the clipped line classifies Empty; otherwise true with the first and last
 /// non-empty step's ends as fractions of the given line.
 /// </summary>
        public static bool IntersectLine(ProviderCore core, double fx, double fy, double fz, double tx, double ty, double tz, out double start, out double end)
        {
            start = 0;
            end = 0;
            if (core == null || core.IsClosed) return false;
            double cf, ct;
            return core._sampler.IntersectLine(fx, fy, fz, tx, ty, tz, out start, out end, out cf, out ct);
        }

        // ================================================================================================== helpers

        private static bool Fits(byte[] a, int baseIndex, int nx, int ny, int nz, int sY, int sZ)
        {
            if (baseIndex < 0 || (ny > 1 && sY <= 0) || (nz > 1 && sZ <= 0)) return false;
            long last = (long)baseIndex + (nx - 1) + (long)(ny - 1) * sY + (long)(nz - 1) * sZ;
            return last < a.Length;
        }

        private static void Fill(byte[] a, int baseIndex, int nx, int ny, int nz, int sY, int sZ, byte v)
        {
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                {
                    int row = baseIndex + y * sY + z * sZ;
                    for (int x = 0; x < nx; x++) a[row + x] = v;
                }
        }

        private static void BadTarget(int lod, int minX, int minY, int minZ, int maxX, int maxY, int maxZ, int baseIndex, int sY, int sZ)
        {
            Interlocked.Increment(ref BadTargets);
            if (Interlocked.Exchange(ref _badTargetLogged, 1) != 0) return;
            DeLog.Info("DE.Terrain WARN target lod=" + lod + " range=(" + minX + "," + minY + "," + minZ + ")..(" + maxX + "," + maxY + "," + maxZ +
                       ") base=" + baseIndex + " sY=" + sY + " sZ=" + sZ + ": the range does not fit the target array; that type is not written (logged once)");
        }
    }

    /// <summary>
 /// Lock-free ReadRange timing (Interlocked counters; CONTRACT §3.1 "Threading and budget"): count, sum, max and a
 /// log-linear µs histogram (10 µs bins to 1 ms, 100 µs bins to 10 ms, 1 ms bins to 100 ms, then overflow) for the p50/p99
 /// of <c>DE.Terrain STATS</c> (each percentile is its bin's upper edge).
 /// </summary>
    internal sealed class ReadStats
    {
        private const int Bins = 100 + 90 + 90 + 1;
        private readonly long[] _bins = new long[Bins];
        private long _calls, _sumTicks, _maxTicks;

        public long Calls { get { return Interlocked.Read(ref _calls); } }
        public double MaxUs { get { return Interlocked.Read(ref _maxTicks) * 1e6 / Stopwatch.Frequency; } }
        public double MeanUs { get { long c = Calls; return c == 0 ? 0 : Interlocked.Read(ref _sumTicks) * 1e6 / Stopwatch.Frequency / c; } }

        public void Add(long ticks)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Add(ref _sumTicks, ticks);
            long m;
            while (ticks > (m = Interlocked.Read(ref _maxTicks)))
                if (Interlocked.CompareExchange(ref _maxTicks, ticks, m) == m) break;
            double us = ticks * 1e6 / Stopwatch.Frequency;
            Interlocked.Increment(ref _bins[Bin(us)]);
        }

        public static int Bin(double us)
        {
            if (us < 1000) return (int)(us / 10);
            if (us < 10000) return 100 + (int)((us - 1000) / 100);
            if (us < 100000) return 190 + (int)((us - 10000) / 1000);
            return Bins - 1;
        }

        public static double BinUpperUs(int b)
        {
            if (b < 100) return (b + 1) * 10.0;
            if (b < 190) return 1000 + (b - 99) * 100.0;
            if (b < 280) return 10000 + (b - 189) * 1000.0;
            return double.PositiveInfinity;
        }

        /// <summary>The upper edge of the bin holding quantile <paramref name="q"/> (0..1); 0 without calls.</summary>
        public double PercentileUs(double q)
        {
            long n = 0;
            var snap = new long[Bins];
            for (int i = 0; i < Bins; i++) { snap[i] = Interlocked.Read(ref _bins[i]); n += snap[i]; }
            if (n == 0) return 0;
            long target = (long)Math.Ceiling(q * n);
            if (target < 1) target = 1;
            long acc = 0;
            for (int i = 0; i < Bins; i++)
            {
                acc += snap[i];
                if (acc >= target) return i == Bins - 1 ? MaxUs : BinUpperUs(i);
            }
            return MaxUs;
        }

        public void Reset()
        {
            for (int i = 0; i < Bins; i++) Interlocked.Exchange(ref _bins[i], 0);
            Interlocked.Exchange(ref _calls, 0);
            Interlocked.Exchange(ref _sumTicks, 0);
            Interlocked.Exchange(ref _maxTicks, 0);
        }
    }

    /// <summary>The outcome of <see cref="ProviderSelfCheck.Run"/>.</summary>
    internal struct SelfCheckResult
    {
        public int Boxes, Requests, DetectChecks, LineChecks, Violations;
        public int Disjoint, Contains, Intersects;
        public string FirstViolation;
        public bool Pass { get { return Violations == 0 && Boxes > 0; } }
    }

    /// <summary>
 /// The provider invariants of S4.N3, shared by the unit tests and the in-game self-check (<c>de.terrain n3</c>), on
 /// random boxes around a focus point of one tile (LOD 0, 1 and 2):
 /// <list type="number">
 /// <item><c>IntersectBox</c> Disjoint ⇒ every content byte of the box reads 0 (and every material 255); Contains ⇒ every
 /// content byte 255 and the box inside the tile (never Disjoint/Contains on a non-uniform range);</item>
 /// <item>request path: the five output bits are cleared from the input flags and the others kept; <c>EmptyData</c>
 /// exactly when every written byte is empty, <c>FullContent</c> exactly when every content byte is 255;</item>
 /// <item><c>detectOnly</c>: the arrays passed are never touched (sentinel check); <c>EmptyData</c> only on a box whose
 /// bytes all read empty; <c>FullContent</c> only on one whose content bytes all read 255;</item>
 /// <item><c>IntersectLine</c> false ⇒ the line's voxels read empty along it (sampled every 2 m).</item>
 /// </list>
 /// Allocates its buffers once per run.
 /// </summary>
    internal static class ProviderSelfCheck
    {
        public const int MaxExtent = 24;

        public static SelfCheckResult Run(ProviderCore core, int seed, int boxes, int focusX, int focusY, int focusZ, int spread)
        {
            var r = new SelfCheckResult();
            var rnd = new Random(seed);
            int n = (MaxExtent + 1) * (MaxExtent + 1) * (MaxExtent + 1);
            byte[] content = new byte[n], material = new byte[n], sc = new byte[n], sm = new byte[n];
            for (int b = 0; b < boxes; b++)
            {
                int lod = b % 5 == 4 ? 2 : b % 5 == 3 ? 1 : 0;
                int ex = 1 + rnd.Next(MaxExtent), ey = 1 + rnd.Next(MaxExtent), ez = 1 + rnd.Next(MaxExtent);
                int cx = focusX + rnd.Next(-spread, spread + 1), cy = focusY + rnd.Next(-spread, spread + 1), cz = focusZ + rnd.Next(-spread, spread + 1);
                // LOD-L box of extent e (LOD voxels) around the focus, clamped to the tile in LOD-L coordinates
                int lim = (TileSampler.TileSize >> lod) - 1;
                int x0 = Clamp((cx >> lod) - ex / 2, 0, lim), y0 = Clamp((cy >> lod) - ey / 2, 0, lim), z0 = Clamp((cz >> lod) - ez / 2, 0, lim);
                int x1 = Math.Min(x0 + ex - 1, lim), y1 = Math.Min(y0 + ey - 1, lim), z1 = Math.Min(z0 + ez - 1, lim);
                int nx = x1 - x0 + 1, ny = y1 - y0 + 1, nz = z1 - z0 + 1;
                r.Boxes++;

                // (2) request path, random input flags
                int inFlags = rnd.Next(0, 0x800) & ~ProviderCore.RfForPhysics; // keep the physics timing clean
                int flags = ProviderCore.Read(core, lod, x0, y0, z0, x1, y1, z1, ProviderCore.DtContent | ProviderCore.DtMaterial, inFlags,
                                              content, material, 0, nx, nx * ny, false);
                r.Requests++;
                bool allZero = true, allFull = true, matNone = true;
                for (int i = 0; i < nx * ny * nz; i++)
                {
                    if (content[i] != 0) allZero = false;
                    if (content[i] != 255) allFull = false;
                    if (material[i] != 255) matNone = false;
                }
                bool empty = allZero && matNone;
                if ((flags & ~ProviderCore.RfOutputMask) != (inFlags & ~ProviderCore.RfOutputMask)) Violation(ref r, "request flags: input bits not kept", lod, x0, y0, z0, x1, y1, z1);
                if (((flags & ProviderCore.RfEmptyData) != 0) != empty) Violation(ref r, "request EmptyData=" + ((flags & ProviderCore.RfEmptyData) != 0) + " but bytes empty=" + empty, lod, x0, y0, z0, x1, y1, z1);
                if (((flags & ProviderCore.RfFullContent) != 0) != allFull) Violation(ref r, "request FullContent=" + ((flags & ProviderCore.RfFullContent) != 0) + " but content full=" + allFull, lod, x0, y0, z0, x1, y1, z1);
                if ((flags & (ProviderCore.RfContentChecked | ProviderCore.RfContentCheckedDeep | ProviderCore.RfOneMaterial)) != 0) Violation(ref r, "request set a bit it does not own", lod, x0, y0, z0, x1, y1, z1);

                // (1) Intersect on the LOD-0 box of the same voxels
                int bx0 = x0 << lod, by0 = y0 << lod, bz0 = z0 << lod, bx1 = ((x1 + 1) << lod) - 1, by1 = ((y1 + 1) << lod) - 1, bz1 = ((z1 + 1) << lod) - 1;
                if (lod == 0)
                {
                    int ct = ProviderCore.IntersectBox(core, bx0, by0, bz0, bx1, by1, bz1);
                    if (ct == ProviderCore.CtDisjoint) { r.Disjoint++; if (!allZero) Violation(ref r, "Disjoint on a box with content", lod, x0, y0, z0, x1, y1, z1); }
                    else if (ct == ProviderCore.CtContains) { r.Contains++; if (!allFull) Violation(ref r, "Contains on a box not full", lod, x0, y0, z0, x1, y1, z1); }
                    else r.Intersects++;
                }

                // (3) detectOnly with sentinel arrays
                for (int i = 0; i < n; i++) { sc[i] = 0xA5; sm[i] = 0x5A; }
                int dflags = ProviderCore.Read(core, lod, x0, y0, z0, x1, y1, z1, ProviderCore.DtContent, inFlags, sc, sm, 0, nx, nx * ny, true);
                r.DetectChecks++;
                for (int i = 0; i < n; i++)
                    if (sc[i] != 0xA5 || sm[i] != 0x5A) { Violation(ref r, "detectOnly touched the target", lod, x0, y0, z0, x1, y1, z1); break; }
                if ((dflags & ~ProviderCore.RfOutputMask) != (inFlags & ~ProviderCore.RfOutputMask)) Violation(ref r, "detectOnly flags: input bits not kept", lod, x0, y0, z0, x1, y1, z1);
                if ((dflags & ProviderCore.RfEmptyData) != 0 && !allZero) Violation(ref r, "detectOnly EmptyData on a box with content", lod, x0, y0, z0, x1, y1, z1);
                if ((dflags & ProviderCore.RfFullContent) != 0 && !allFull) Violation(ref r, "detectOnly FullContent on a box not full", lod, x0, y0, z0, x1, y1, z1);

                // (4) a vertical line through the box
                if (b % 4 == 0)
                {
                    double lx = bx0 + (bx1 - bx0) * 0.5 + 0.25, lz = bz0 + (bz1 - bz0) * 0.5 + 0.25;
                    double s, e;
                    bool hit = ProviderCore.IntersectLine(core, lx, by1 + 0.5, lz, lx, by0 - 0.5, lz, out s, out e);
                    r.LineChecks++;
                    if (!hit && !LineEmpty(core, lx, by0 - 0.5, by1 + 0.5, lz, content, material))
                        Violation(ref r, "IntersectLine false over content", 0, bx0, by0, bz0, bx1, by1, bz1);
                    if (hit && (s < 0 || e > 1 || s > e)) Violation(ref r, "IntersectLine offsets out of [0,1]", 0, bx0, by0, bz0, bx1, by1, bz1);
                }
            }
            return r;
        }

        private static bool LineEmpty(ProviderCore core, double x, double yLo, double yHi, double z, byte[] c, byte[] m)
        {
            int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
            for (double y = Math.Max(0, Math.Floor(yLo)); y <= Math.Min(TileSampler.TileSize - 1, Math.Ceiling(yHi)); y += 2)
            {
                int iy = (int)y;
                int x0 = Math.Max(ix, 0), z0 = Math.Max(iz, 0), x1 = Math.Min(ix + 1, TileSampler.TileSize - 1), z1 = Math.Min(iz + 1, TileSampler.TileSize - 1);
                int y1 = Math.Min(iy + 1, TileSampler.TileSize - 1);
                int nx = x1 - x0 + 1, ny = y1 - iy + 1;
                ProviderCore.Read(core, 0, x0, iy, z0, x1, y1, z1, ProviderCore.DtContent, 0, c, null, 0, nx, nx * ny, false);
                int cnt = nx * ny * (z1 - z0 + 1);
                for (int i = 0; i < cnt; i++) if (c[i] != 0) return false;
            }
            return true;
        }

        private static void Violation(ref SelfCheckResult r, string what, int lod, int x0, int y0, int z0, int x1, int y1, int z1)
        {
            r.Violations++;
            if (r.FirstViolation == null)
                r.FirstViolation = what + " (lod " + lod + " box (" + x0 + "," + y0 + "," + z0 + ")..(" + x1 + "," + y1 + "," + z1 + "))";
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
