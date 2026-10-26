using System;
using System.Collections.Generic;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S6 collision skin math (CONTRACT §2.6, §2.7, §5.4 "C4 type 6 SkinSet (S6)"), pure and shared by both plugins:
 // * SE side: the fixed section lattice (anchored at the grid's cell 0; 6 cells large, 32 small), the split of merged
 // box cells at the lattice and into ≤ 3 (large) / ≤ 16 (small) cells per axis, model-collision pieces as OBBs
 // placed as MyCubeBlockCollector.AddShapesCustom places them and split so no half extent exceeds 4 m, the section
 // order and content hash, and a deterministic stand-in for MyVoxelSegmentation(Simple).
 // * SN side: the refresh rule (ρ_max, u = q⁻¹·ŷ, 1 cm), the subgrid placement (q·R(rel), rel·c_sec), the hit point
 // frames (SN world → SE-local of the grid or subgrid), the character capsule from C1, the round-robin budget of
 // 256 shapes per physics step, and the predicted corner error of a K-pre-scaled box (the S6.G1-E3 oracle).
 // No Unity or VRage types. Python twin: tools/worldmap/wire_ref.py (s6_*), compared by S6VectorTests. The name
 // "SkinMath" belongs to S5's skinned-mesh math (SkinMath.cs); this file is the collision skin.
 // =================================================================================================================

 /// <summary>An inclusive range of grid cells (SE <c>Vector3I</c> indices).</summary>
    public struct CellRange : IEquatable<CellRange>
    {
        public int MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        public CellRange(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        /// <summary>One cell.</summary>
        public static CellRange Cell(int x, int y, int z) { return new CellRange(x, y, z, x, y, z); }

        public int SizeX { get { return MaxX - MinX + 1; } }
        public int SizeY { get { return MaxY - MinY + 1; } }
        public int SizeZ { get { return MaxZ - MinZ + 1; } }

        /// <summary>Max ≥ Min on every axis.</summary>
        public bool IsValid { get { return MaxX >= MinX && MaxY >= MinY && MaxZ >= MinZ; } }

        /// <summary>Number of cells (0 when invalid).</summary>
        public long CellCount { get { return IsValid ? (long)SizeX * SizeY * SizeZ : 0; } }

        public bool Contains(int x, int y, int z) { return x >= MinX && x <= MaxX && y >= MinY && y <= MaxY && z >= MinZ && z <= MaxZ; }

        public bool Equals(CellRange o)
        {
            return MinX == o.MinX && MinY == o.MinY && MinZ == o.MinZ && MaxX == o.MaxX && MaxY == o.MaxY && MaxZ == o.MaxZ;
        }

        public override bool Equals(object obj) { return obj is CellRange && Equals((CellRange)obj); }

        public override int GetHashCode()
        {
            unchecked { return ((((MinX * 397 ^ MinY) * 397 ^ MinZ) * 397 ^ MaxX) * 397 ^ MaxY) * 397 ^ MaxZ; }
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "[{0},{1},{2}..{3},{4},{5}]", MinX, MinY, MinZ, MaxX, MaxY, MaxZ);
        }
    }

    /// <summary>
 /// One box of a collision skin in the SE-local frame of its grid or subgrid (metres): centre, half extents along the
 /// box axes, the axes as rows <c>E0, E1, E2</c> (= <c>SkinBox.axes</c>; orthonormal and right-handed, exactly the
 /// identity rows for a grid-aligned box) and <c>SkinBox.flags</c>.
 /// </summary>
    public struct SkinBoxData
    {
        public Vec3 Centre, Half, E0, E1, E2;
        public uint Flags;

        /// <summary>A grid-aligned box (identity rows).</summary>
        public static SkinBoxData Aligned(Vec3 centre, Vec3 half, uint flags)
        {
            return new SkinBoxData { Centre = centre, Half = half, E0 = Vec3.UnitX, E1 = Vec3.UnitY, E2 = Vec3.UnitZ, Flags = flags };
        }

        /// <summary>The source nibble (<see cref="CombatConventions.SkinBoxSource"/>).</summary>
        public uint Source { get { return (Flags & CombatConventions.SkinBoxFlags.SourceMask) >> (int)CombatConventions.SkinBoxFlags.SourceShift; } }

        /// <summary>Flag bit 0.</summary>
        public bool IsRotated { get { return (Flags & CombatConventions.SkinBoxFlags.Rotated) != 0; } }

        /// <summary>The axes are exactly the identity rows.</summary>
        public bool HasIdentityAxes
        {
            get
            {
                return E0.X == 1.0 && E0.Y == 0.0 && E0.Z == 0.0 && E1.X == 0.0 && E1.Y == 1.0 && E1.Z == 0.0
                    && E2.X == 0.0 && E2.Y == 0.0 && E2.Z == 1.0;
            }
        }

        /// <summary>The SE-local point at box coordinates (a, b, c) (multiples of the half extents along E0, E1, E2).</summary>
        public Vec3 PointAt(double a, double b, double c)
        {
            return Centre + E0 * (a * Half.X) + E1 * (b * Half.Y) + E2 * (c * Half.Z);
        }

        public override string ToString()
        {
            return "c=" + Centre + " h=" + Half + " e0=" + E0 + " e1=" + E1 + " e2=" + E2 + " flags=0x" + Flags.ToString("x", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>One section of a skin: its lattice id and centre <c>c_sec</c> (SE-local metres), kind, boxes and content hash.</summary>
    public sealed class SkinSectionData
    {
        public uint SectionId;
        public Vec3 Centre;
        public uint Kind = SkinSectionKind.Grid;
        public readonly List<SkinBoxData> Boxes = new List<SkinBoxData>();
        /// <summary>FNV-1a 64 of the section's 24 + 64·n encoded bytes (<see cref="CollisionSkinMath.SectionContentHash"/>).</summary>
        public ulong ContentHash;

        public override string ToString()
        {
            return "section 0x" + SectionId.ToString("x8", CultureInfo.InvariantCulture) + " c=" + Centre + " boxes=" + Boxes.Count +
                   " hash=0x" + ContentHash.ToString("x16", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The S6 collision skin math (see the file header).</summary>
    public static class CollisionSkinMath
    {
        /// <summary>The largest half extent of a box, metres (<see cref="CombatConventions.Limits.MaxHalfExtentMm"/>).</summary>
        public const double MaxHalfExtent = CombatConventions.Limits.MaxHalfExtentMm / 1000.0;
        /// <summary>The refresh threshold, metres (<see cref="CombatConventions.Limits.RefreshMm"/>).</summary>
        public const double RefreshMetres = CombatConventions.Limits.RefreshMm / 1000.0;
        /// <summary>Section indices per axis lie in [<see cref="MinSectionIndex"/>, <see cref="MaxSectionIndex"/>].</summary>
        public const int MinSectionIndex = -CombatConventions.SkinLattice.SectionIdBias, MaxSectionIndex = CombatConventions.SkinLattice.SectionIdBias - 1;
        /// <summary>A model-collision OBB never gets a half extent below this (a flat convex piece), metres.</summary>
        public const double MinObbHalfExtent = 0.005;
        /// <summary>Character capsule radius and height, metres.</summary>
        public const double CapsuleRadius = CombatConventions.Limits.CapsuleRadiusMm / 1000.0, CapsuleHeight = CombatConventions.Limits.CapsuleHeightMm / 1000.0;

        /// <summary>The C1 fallback capsule centre (SE model space of the character) when the bones are not valid.</summary>
        public static readonly Vec3 FallbackCapsuleCentre = new Vec3(0.0, 0.9, 0.0);

        // ------------------------------------------------------------------------------------------------ lattice

 /// <summary>Cells per section axis: 6 (large grid, 15 m) or 32 (small grid, 16 m).</summary>
        public static int SectionCells(bool largeGrid)
        {
            return largeGrid ? CombatConventions.SkinLattice.SectionCellsLarge : CombatConventions.SkinLattice.SectionCellsSmall;
        }

        /// <summary>Cells per box axis: 3 (large grid, half extent 3.75 m) or 16 (small grid, 4.0 m).</summary>
        public static int MaxBoxCells(bool largeGrid)
        {
            return largeGrid ? CombatConventions.SkinLattice.MaxBoxCellsLarge : CombatConventions.SkinLattice.MaxBoxCellsSmall;
        }

        /// <summary>
 /// Rounds half away from zero, as <c>Math.Round(x, MidpointRounding.AwayFromZero)</c> on.NET Framework (the
 /// fraction split, exact; VRage <c>Vector3I.Round</c>), implemented here so Mono gives the same result. Throws
 /// <see cref="OverflowException"/> outside the int range or for NaN.
 /// </summary>
        public static int RoundAway(double x)
        {
            double t = Math.Truncate(x);
            double f = x - t;
            if (f >= 0.5) t += 1.0;
            else if (f <= -0.5) t -= 1.0;
            if (!(t >= int.MinValue && t <= int.MaxValue)) throw new OverflowException("RoundAway(" + x.ToString("R", CultureInfo.InvariantCulture) + ")");
            return (int)t;
        }

        /// <summary>Floor division (rounds towards −∞) for <paramref name="b"/> &gt; 0.</summary>
        public static int FloorDiv(int a, int b)
        {
            if (b <= 0) throw new ArgumentOutOfRangeException("b", "b must be > 0");
            int q = a / b;
            if (a % b != 0 && a < 0) q--;
            return q;
        }

        /// <summary>
 /// The section of a box centre (SE-local metres): <c>i = FloorDiv(RoundAway(c / GridSize), SectionCells)</c> per
 /// axis. False when a component is not finite or an index falls outside [−512, 511] (±7.7 km large, ±8.2 km small).
 /// </summary>
        public static bool TrySectionIndex(Vec3 centreSe, double gridSize, bool largeGrid, out int ix, out int iy, out int iz)
        {
            ix = iy = iz = 0;
            if (!(gridSize > 0) || !centreSe.IsFinite) return false;
            int s = SectionCells(largeGrid);
            double lim = (MaxSectionIndex + 2.0) * s;
            double fx = centreSe.X / gridSize, fy = centreSe.Y / gridSize, fz = centreSe.Z / gridSize;
            if (Math.Abs(fx) > lim || Math.Abs(fy) > lim || Math.Abs(fz) > lim) return false;
            ix = FloorDiv(RoundAway(fx), s);
            iy = FloorDiv(RoundAway(fy), s);
            iz = FloorDiv(RoundAway(fz), s);
            return InRange(ix) && InRange(iy) && InRange(iz);
        }

        private static bool InRange(int i) { return i >= MinSectionIndex && i <= MaxSectionIndex; }

        /// <summary>The lattice id <c>((ix+512)&lt;&lt;20)|((iy+512)&lt;&lt;10)|(iz+512)</c>. Throws outside [−512, 511].</summary>
        public static uint SectionId(int ix, int iy, int iz)
        {
            if (!InRange(ix) || !InRange(iy) || !InRange(iz)) throw new ArgumentOutOfRangeException("ix", "section index outside [-512, 511]");
            const int bias = CombatConventions.SkinLattice.SectionIdBias, bits = CombatConventions.SkinLattice.SectionIdBits;
            return (uint)(((ix + bias) << (2 * bits)) | ((iy + bias) << bits) | (iz + bias));
        }

        /// <summary>The inverse of <see cref="SectionId"/> (the top two bits are ignored).</summary>
        public static void SectionIndexOf(uint sectionId, out int ix, out int iy, out int iz)
        {
            const int bias = CombatConventions.SkinLattice.SectionIdBias, bits = CombatConventions.SkinLattice.SectionIdBits;
            const uint mask = (1u << bits) - 1u;
            ix = (int)((sectionId >> (2 * bits)) & mask) - bias;
            iy = (int)((sectionId >> bits) & mask) - bias;
            iz = (int)(sectionId & mask) - bias;
        }

        /// <summary><c>c_sec = (i·SectionCells + (SectionCells − 1)/2)·GridSize</c> per axis (SE-local metres).</summary>
        public static Vec3 SectionCentre(int ix, int iy, int iz, double gridSize, bool largeGrid)
        {
            int s = SectionCells(largeGrid);
            double h = (s - 1) / 2.0;
            return new Vec3(((double)ix * s + h) * gridSize, ((double)iy * s + h) * gridSize, ((double)iz * s + h) * gridSize);
        }

        /// <summary><see cref="SectionCentre(int,int,int,double,bool)"/> of a section id.</summary>
        public static Vec3 SectionCentre(uint sectionId, double gridSize, bool largeGrid)
        {
            int ix, iy, iz;
            SectionIndexOf(sectionId, out ix, out iy, out iz);
            return SectionCentre(ix, iy, iz, gridSize, largeGrid);
        }

        // ------------------------------------------------------------------------------------------------ boxes

 /// <summary>
 /// Splits a merged cell range at the section lattice planes, then each piece into at most
 /// <see cref="MaxBoxCells"/> cells per axis, as equal as possible (larger pieces first). Appends the pieces to
 /// <paramref name="output"/> in z, then y, then x order of their minimum cell.
 /// </summary>
        public static void SplitSegment(CellRange segment, bool largeGrid, List<CellRange> output)
        {
            if (output == null) throw new ArgumentNullException("output");
            if (!segment.IsValid) throw new ArgumentException("invalid cell range " + segment, "segment");
            int s = SectionCells(largeGrid), m = MaxBoxCells(largeGrid);
            List<int> xs = SplitAxis(segment.MinX, segment.MaxX, s, m);
            List<int> ys = SplitAxis(segment.MinY, segment.MaxY, s, m);
            List<int> zs = SplitAxis(segment.MinZ, segment.MaxZ, s, m);
            for (int k = 0; k < zs.Count; k += 2)
                for (int j = 0; j < ys.Count; j += 2)
                    for (int i = 0; i < xs.Count; i += 2)
                        output.Add(new CellRange(xs[i], ys[j], zs[k], xs[i + 1], ys[j + 1], zs[k + 1]));
        }

        /// <summary>Pairs (lo, hi) of one axis: cut at multiples of <paramref name="s"/>, then ≤ <paramref name="m"/> cells, larger first.</summary>
        private static List<int> SplitAxis(int min, int max, int s, int m)
        {
            var r = new List<int>();
            long start = min;
            while (start <= max)
            {
                long latticeEnd = ((long)FloorDiv((int)start, s) + 1) * s - 1;
                long end = Math.Min(max, latticeEnd);
                long len = end - start + 1;
                long n = (len + m - 1) / m;
                long baseLen = len / n, extra = len % n;
                long p = start;
                for (long i = 0; i < n; i++)
                {
                    long l = baseLen + (i < extra ? 1 : 0);
                    r.Add((int)p);
                    r.Add((int)(p + l - 1));
                    p += l;
                }
                start = end + 1;
            }
            return r;
        }

        /// <summary>
 /// A cell range as a box: centre <c>(min + max)/2·GridSize</c>, half <c>(max − min + 1)·GridSize/2</c>, identity
 /// rows, flags <c>Segment &lt;&lt; 8</c> (= 0).
 /// </summary>
        public static SkinBoxData CellBox(CellRange r, double gridSize)
        {
            var c = new Vec3((r.MinX + (double)r.MaxX) / 2.0 * gridSize, (r.MinY + (double)r.MaxY) / 2.0 * gridSize, (r.MinZ + (double)r.MaxZ) / 2.0 * gridSize);
            var h = new Vec3((r.MaxX - (double)r.MinX + 1.0) * gridSize / 2.0, (r.MaxY - (double)r.MinY + 1.0) * gridSize / 2.0, (r.MaxZ - (double)r.MinZ + 1.0) * gridSize / 2.0);
            return SkinBoxData.Aligned(c, h, CombatConventions.SkinBoxSource.Segment << (int)CombatConventions.SkinBoxFlags.SourceShift);
        }

        /// <summary>
 /// One convex piece of a block's model collision as an OBB, placed as <c>MyCubeBlockCollector.AddShapesCustom</c>
 /// places it (<c>HkConvexTransformShape(shape, translation, block.Orientation quaternion, ScaleFactor)</c>: p′ =
 /// translation + R·(scale·p), R mapping x, y, z to the orientation rows Right, Up, Backward): centre = translation
 /// + R·(scale·(min + max)/2); half = scale·|max − min|/2 (at least <see cref="MinObbHalfExtent"/>); axes = the rows,
 /// orthonormalised and right-handed. A signed axis permutation (every 90° block orientation) is a grid-aligned
 /// box: its half extents are permuted onto identity rows and the Rotated flag stays clear. Flags:
 /// <c>ModelShapeObb &lt;&lt; 8</c>, plus Rotated when the rows are not the identity. Not split
 /// (<see cref="SplitObb"/>). Throws on non-finite input or <paramref name="scale"/> ≤ 0.
 /// </summary>
        public static SkinBoxData ObbFromModelAabb(Vec3 aabbMin, Vec3 aabbMax, SeMatrix blockOrientation, Vec3 translation, double scale)
        {
            if (!aabbMin.IsFinite || !aabbMax.IsFinite || !translation.IsFinite || !(scale > 0) || double.IsInfinity(scale))
                throw new ArgumentException("non-finite model AABB, translation or scale");
            Vec3 r = blockOrientation.Right, u = blockOrientation.Up, b = blockOrientation.Backward;
            if (!r.IsFinite || !u.IsFinite || !b.IsFinite || r.Length < 1e-6 || u.Length < 1e-6 || b.Length < 1e-6)
                throw new ArgumentException("degenerate block orientation");
            Vec3 cm = (aabbMin + aabbMax) * (0.5 * scale);
            var hm = new Vec3(Math.Max(MinObbHalfExtent, Math.Abs(aabbMax.X - aabbMin.X) * 0.5 * scale),
                              Math.Max(MinObbHalfExtent, Math.Abs(aabbMax.Y - aabbMin.Y) * 0.5 * scale),
                              Math.Max(MinObbHalfExtent, Math.Abs(aabbMax.Z - aabbMin.Z) * 0.5 * scale));
            Vec3 centre = translation + r * cm.X + u * cm.Y + b * cm.Z;
            uint source = CombatConventions.SkinBoxSource.ModelShapeObb << (int)CombatConventions.SkinBoxFlags.SourceShift;

            int ar, au, ab;
            if (AxisOf(r, out ar) && AxisOf(u, out au) && AxisOf(b, out ab) && ar != au && au != ab && ar != ab)
            {
                double[] h = new double[3];
                h[ar] = hm.X; h[au] = hm.Y; h[ab] = hm.Z;
                return SkinBoxData.Aligned(centre, new Vec3(h[0], h[1], h[2]), source);
            }
            Vec3 e0 = r.Normalized();
            Vec3 e1 = (u - e0 * Vec3.Dot(u, e0));
            if (e1.Length < 1e-9) throw new ArgumentException("degenerate block orientation");
            e1 = e1.Normalized();
            Vec3 e2 = Vec3.Cross(e0, e1);
            return new SkinBoxData { Centre = centre, Half = hm, E0 = e0, E1 = e1, E2 = e2, Flags = source | CombatConventions.SkinBoxFlags.Rotated };
        }

        /// <summary>True when <paramref name="v"/> is ±a unit axis (within 1e-6); <paramref name="axis"/> = 0, 1 or 2.</summary>
        private static bool AxisOf(Vec3 v, out int axis)
        {
            const double e = 1e-6;
            axis = -1;
            double ax = Math.Abs(v.X), ay = Math.Abs(v.Y), az = Math.Abs(v.Z);
            if (Math.Abs(ax - 1.0) <= e && ay <= e && az <= e) axis = 0;
            else if (Math.Abs(ay - 1.0) <= e && ax <= e && az <= e) axis = 1;
            else if (Math.Abs(az - 1.0) <= e && ax <= e && ay <= e) axis = 2;
            return axis >= 0;
        }

        /// <summary>
 /// Splits a box into equal pieces along every axis whose half extent exceeds <paramref name="maxHalf"/>
 /// (n = ⌈h / maxHalf⌉ pieces of half h/n), keeping axes and flags; appends them (x fastest, then y, then z).
 /// </summary>
        public static void SplitObb(SkinBoxData box, double maxHalf, List<SkinBoxData> output)
        {
            if (output == null) throw new ArgumentNullException("output");
            if (!(maxHalf > 0)) throw new ArgumentOutOfRangeException("maxHalf");
            int nx = Pieces(box.Half.X, maxHalf), ny = Pieces(box.Half.Y, maxHalf), nz = Pieces(box.Half.Z, maxHalf);
            if (nx == 1 && ny == 1 && nz == 1) { output.Add(box); return; }
            double hx = box.Half.X / nx, hy = box.Half.Y / ny, hz = box.Half.Z / nz;
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        double ox = -box.Half.X + (2 * i + 1) * hx, oy = -box.Half.Y + (2 * j + 1) * hy, oz = -box.Half.Z + (2 * k + 1) * hz;
                        output.Add(new SkinBoxData
                        {
                            Centre = box.Centre + box.E0 * ox + box.E1 * oy + box.E2 * oz,
                            Half = new Vec3(hx, hy, hz),
                            E0 = box.E0, E1 = box.E1, E2 = box.E2, Flags = box.Flags,
                        });
                    }
        }

        private static int Pieces(double h, double maxHalf)
        {
            if (!(h > maxHalf)) return 1;
            double n = Math.Ceiling(h / maxHalf);
            if (n > 1 << 20) throw new ArgumentException("box too large to split");
            return (int)n;
        }

        // ------------------------------------------------------------------------------------------------ segmentation stand-in

 /// <summary>
 /// A deterministic, pure stand-in for SE's <c>MyVoxelSegmentation</c> (<c>Simple</c>, 0 merge iterations), used
 /// when that type is missing: maximal x runs per (y, z), then runs with the same x extent merged along y, then
 /// rectangles with the same x and y extents merged along z. The union is exactly the input cells (duplicates
 /// ignored) and the boxes never overlap. Appends to <paramref name="output"/> sorted by minimum cell (z, y, x).
 /// </summary>
        public static void MergeCellsGreedy(IList<I32x3> cells, List<CellRange> output)
        {
            if (cells == null) throw new ArgumentNullException("cells");
            if (output == null) throw new ArgumentNullException("output");
            var keys = new List<I32x3>(cells.Count);
            var seen = new HashSet<long>();
            foreach (I32x3 c in cells) if (seen.Add(CellKey(c.x, c.y, c.z))) keys.Add(c);
            keys.Sort(CompareZyx);

            // x runs per (y, z)
            var runs = new List<CellRange>();
            for (int i = 0; i < keys.Count;)
            {
                int j = i;
                while (j + 1 < keys.Count && keys[j + 1].z == keys[i].z && keys[j + 1].y == keys[i].y && keys[j + 1].x == keys[j].x + 1) j++;
                runs.Add(new CellRange(keys[i].x, keys[i].y, keys[i].z, keys[j].x, keys[i].y, keys[i].z));
                i = j + 1;
            }

            // equal x runs along y (same z)
            List<CellRange> rects = MergeAlong(runs, true);
            // equal rectangles along z
            List<CellRange> boxes = MergeAlong(rects, false);
            boxes.Sort(CompareMinZyx);
            output.AddRange(boxes);
        }

        /// <summary>Merges ranges with equal extents on the other axes that touch along y (<paramref name="alongY"/>) or z.</summary>
        private static List<CellRange> MergeAlong(List<CellRange> input, bool alongY)
        {
            var groups = new Dictionary<CellRange, List<CellRange>>();
            var order = new List<CellRange>();
            foreach (CellRange r in input)
            {
                // the group key: the range with the merge axis collapsed to 0
                CellRange key = alongY ? new CellRange(r.MinX, 0, r.MinZ, r.MaxX, 0, r.MaxZ) : new CellRange(r.MinX, r.MinY, 0, r.MaxX, r.MaxY, 0);
                List<CellRange> g;
                if (!groups.TryGetValue(key, out g)) { g = new List<CellRange>(); groups.Add(key, g); order.Add(key); }
                g.Add(r);
            }
            var output = new List<CellRange>(input.Count);
            foreach (CellRange key in order)
            {
                List<CellRange> g = groups[key];
                g.Sort((a, b) => alongY ? a.MinY.CompareTo(b.MinY) : a.MinZ.CompareTo(b.MinZ));
                CellRange cur = g[0];
                for (int i = 1; i < g.Count; i++)
                {
                    CellRange n = g[i];
                    if (alongY && n.MinY == cur.MaxY + 1) cur.MaxY = n.MaxY;
                    else if (!alongY && n.MinZ == cur.MaxZ + 1) cur.MaxZ = n.MaxZ;
                    else { output.Add(cur); cur = n; }
                }
                output.Add(cur);
            }
            return output;
        }

        private static int CompareZyx(I32x3 a, I32x3 b)
        {
            int c = a.z.CompareTo(b.z);
            if (c != 0) return c;
            c = a.y.CompareTo(b.y);
            return c != 0 ? c : a.x.CompareTo(b.x);
        }

        private static int CompareMinZyx(CellRange a, CellRange b)
        {
            int c = a.MinZ.CompareTo(b.MinZ);
            if (c != 0) return c;
            c = a.MinY.CompareTo(b.MinY);
            if (c != 0) return c;
            c = a.MinX.CompareTo(b.MinX);
            if (c != 0) return c;
            // equal minima only for overlapping inputs: keep a total order
            c = a.MaxZ.CompareTo(b.MaxZ);
            if (c != 0) return c;
            c = a.MaxY.CompareTo(b.MaxY);
            return c != 0 ? c : a.MaxX.CompareTo(b.MaxX);
        }

        /// <summary>Packs a cell (|component| &lt; 2^20) into a long.</summary>
        private static long CellKey(int x, int y, int z)
        {
            const int b = 1 << 20;
            if (x <= -b || x >= b || y <= -b || y >= b || z <= -b || z >= b) throw new ArgumentOutOfRangeException("cell", "cell index beyond ±2^20");
            return ((long)(x + b) << 42) | ((long)(y + b) << 21) | (long)(z + b);
        }

        /// <summary>True when the union of <paramref name="boxes"/> is exactly the set <paramref name="cells"/> and no two boxes overlap.</summary>
        public static bool CheckCoverage(IList<I32x3> cells, IList<CellRange> boxes, out string error)
        {
            if (cells == null) throw new ArgumentNullException("cells");
            if (boxes == null) throw new ArgumentNullException("boxes");
            var want = new HashSet<long>();
            foreach (I32x3 c in cells) want.Add(CellKey(c.x, c.y, c.z));
            var got = new HashSet<long>();
            foreach (CellRange r in boxes)
            {
                if (!r.IsValid) { error = "invalid box " + r; return false; }
                for (int z = r.MinZ; z <= r.MaxZ; z++)
                    for (int y = r.MinY; y <= r.MaxY; y++)
                        for (int x = r.MinX; x <= r.MaxX; x++)
                        {
                            long k = CellKey(x, y, z);
                            if (!want.Contains(k)) { error = string.Format(CultureInfo.InvariantCulture, "box {0} covers ({1},{2},{3}) outside the cells", r, x, y, z); return false; }
                            if (!got.Add(k)) { error = string.Format(CultureInfo.InvariantCulture, "overlap at ({0},{1},{2}) in box {3}", x, y, z, r); return false; }
                        }
            }
            if (got.Count != want.Count)
            {
                foreach (I32x3 c in cells)
                    if (!got.Contains(CellKey(c.x, c.y, c.z)))
                    {
                        error = string.Format(CultureInfo.InvariantCulture, "cell ({0},{1},{2}) not covered", c.x, c.y, c.z);
                        return false;
                    }
            }
            error = null;
            return true;
        }

        // ------------------------------------------------------------------------------------------------ sections

 /// <summary>
 /// The sections of a skin (CONTRACT §5.4): every segment is split (<see cref="SplitSegment"/>) and every OBB split
 /// (<see cref="SplitObb"/>, 4 m); each piece goes to the section of its centre (<see cref="TrySectionIndex"/>);
 /// sections ascend by id; in a section the Segment boxes come first, ordered by their minimum cell (z, y, x), then
 /// the OBB pieces in input order (the caller passes OBBs by block position z, y, x and shape index). Centres are
 /// the lattice centres; <see cref="SkinSectionData.ContentHash"/> is set. Pieces whose centre falls outside the
 /// lattice range are dropped (see the overload's count).
 /// </summary>
        public static List<SkinSectionData> BuildSections(IList<CellRange> segments, IList<SkinBoxData> obbs, double gridSize, bool largeGrid)
        {
            int dropped;
            return BuildSections(segments, obbs, gridSize, largeGrid, out dropped);
        }

        /// <summary><see cref="BuildSections(IList{CellRange}, IList{SkinBoxData}, double, bool)"/>, counting the dropped pieces.</summary>
        public static List<SkinSectionData> BuildSections(IList<CellRange> segments, IList<SkinBoxData> obbs, double gridSize, bool largeGrid, out int dropped)
        {
            if (!(gridSize > 0)) throw new ArgumentOutOfRangeException("gridSize");
            dropped = 0;
            var cellPieces = new SortedDictionary<uint, List<CellRange>>();
            var obbPieces = new SortedDictionary<uint, List<SkinBoxData>>();
            var pieces = new List<CellRange>();
            if (segments != null)
                foreach (CellRange seg in segments)
                {
                    pieces.Clear();
                    SplitSegment(seg, largeGrid, pieces);
                    foreach (CellRange p in pieces)
                    {
                        int ix, iy, iz;
                        if (!TrySectionIndex(CellBox(p, gridSize).Centre, gridSize, largeGrid, out ix, out iy, out iz)) { dropped++; continue; }
                        uint id = SectionId(ix, iy, iz);
                        List<CellRange> l;
                        if (!cellPieces.TryGetValue(id, out l)) { l = new List<CellRange>(); cellPieces.Add(id, l); }
                        l.Add(p);
                    }
                }
            var split = new List<SkinBoxData>();
            if (obbs != null)
                foreach (SkinBoxData obb in obbs)
                {
                    split.Clear();
                    SplitObb(obb, MaxHalfExtent, split);
                    foreach (SkinBoxData p in split)
                    {
                        int ix, iy, iz;
                        if (!TrySectionIndex(p.Centre, gridSize, largeGrid, out ix, out iy, out iz)) { dropped++; continue; }
                        uint id = SectionId(ix, iy, iz);
                        List<SkinBoxData> l;
                        if (!obbPieces.TryGetValue(id, out l)) { l = new List<SkinBoxData>(); obbPieces.Add(id, l); }
                        l.Add(p);
                    }
                }
            var ids = new SortedSet<uint>(cellPieces.Keys);
            ids.UnionWith(obbPieces.Keys);
            var result = new List<SkinSectionData>(ids.Count);
            foreach (uint id in ids)
            {
                var sec = new SkinSectionData { SectionId = id, Centre = SectionCentre(id, gridSize, largeGrid), Kind = SkinSectionKind.Grid };
                List<CellRange> cl;
                if (cellPieces.TryGetValue(id, out cl))
                {
                    cl.Sort(CompareMinZyx);
                    foreach (CellRange r in cl) sec.Boxes.Add(CellBox(r, gridSize));
                }
                List<SkinBoxData> ol;
                if (obbPieces.TryGetValue(id, out ol)) sec.Boxes.AddRange(ol);
                sec.ContentHash = SectionContentHash(sec);
                result.Add(sec);
            }
            return result;
        }

        /// <summary>FNV-1a 64 of the section's encoded bytes (its 24 B header and n × 64 B boxes, as in a SkinSet).</summary>
        public static ulong SectionContentHash(SkinSectionData s)
        {
            if (s == null) throw new ArgumentNullException("s");
            return LayoutHash.Fnv1a64(SkinCodec.SectionBytes(s));
        }

        /// <summary><c>ρ_max</c> = max over the section's boxes of <c>|c_box − c_sec| + |h_box|</c> (0 for no box).</summary>
        public static double RhoMax(SkinSectionData s)
        {
            if (s == null) throw new ArgumentNullException("s");
            double r = 0;
            foreach (SkinBoxData b in s.Boxes) r = Math.Max(r, (b.Centre - s.Centre).Length + b.Half.Length);
            return r;
        }

        // ------------------------------------------------------------------------------------------------ Subnautica side: refresh, placement, hit point

 /// <summary><c>u = q⁻¹·ŷ</c>: SN-world up in the section root's frame (q = the root's rotation).</summary>
        public static Vec3 UpInSection(Quat sectionRotation)
        {
            return sectionRotation.Conjugate().Rotate(Vec3.UnitY);
        }

        /// <summary>
 /// CONTRACT §2.6 refresh rule: true when <c>|s − s_applied|·ρ_max</c> or <c>|s − 1|·|u − u_applied|·ρ_max</c>
 /// exceeds 1 cm. (The K change it bounds moves a box point by at most |Δs|·ρ + 2·|s − 1|·|Δu|·ρ.)
 /// </summary>
        public static bool NeedsRefresh(double sApplied, Vec3 uApplied, double s, Vec3 u, double rhoMax)
        {
            if (Math.Abs(s - sApplied) * rhoMax > RefreshMetres) return true;
            return Math.Abs(s - 1.0) * (u - uApplied).Length * rhoMax > RefreshMetres;
        }

        /// <summary>
 /// The subgrid Frame's Unity-local rotation <c>R(rel) = LookRotation(Mirror(rel.Forward), Mirror(rel.Up))</c>
 /// (<see cref="WireMath.PartLocalRotation"/>); the identity for <see cref="InstanceMath.Identity"/>.
 /// </summary>
        public static Quat SubgridRotation(SeMatrix rel)
        {
            return WireMath.PartLocalRotation(rel);
        }

        /// <summary>The section root's SN-world position: <c>Apply(o, q, s, rel·c_sec)</c> (rel = identity for a root grid).</summary>
        public static Vec3 SectionRootPosition(Vec3 o, Quat q, double s, SeMatrix rel, Vec3 cSec)
        {
            return WireMath.Apply(o, q, s, rel.Transform(cSec));
        }

        /// <summary>The section root's SN-world rotation: <c>q·R(rel)</c> (normalised, w ≥ 0); boxes are pre-scaled with K of it.</summary>
        public static Quat SectionRootRotation(Quat q, SeMatrix rel)
        {
            return (q * SubgridRotation(rel)).Normalized().WithNonNegativeW();
        }

        /// <summary>
 /// One skin box as a pre-scaled proxy collider on its section root (CONTRACT §2.6): <see cref="WireMath.PrescaleBox"/>
 /// with the root's rotation (<see cref="SectionRootRotation"/>, <c>q·R(rel)</c> for a subgrid), the section centre
 /// and the box in its own SE-local frame. BoxCollider <c>center</c> = <see cref="ProxyBox.Centre"/>, <c>size</c> =
 /// 2·<see cref="ProxyBox.HalfExtents"/>; a box with <see cref="SkinBoxData.IsRotated"/> goes on a unit-scale child
 /// with <see cref="ProxyBox.Rotation"/> (identity otherwise).
 /// </summary>
        public static ProxyBox Prescale(Quat rootRotation, double s, Vec3 cSec, SkinBoxData box)
        {
            return WireMath.PrescaleBox(rootRotation, s, cSec, box.Centre, box.Half, box.E0, box.E1, box.E2);
        }

        /// <summary>
 /// The C8 <c>localHit</c> (CONTRACT §2.7): <c>ToLocal(o, q, s, w)</c> with the pose that placed the section at the
 /// last physics step, then, for a subgrid section, <c>InvertRigid(rel)</c> into the subgrid's frame.
 /// </summary>
        public static Vec3 HitLocal(Vec3 o, Quat q, double s, SeMatrix rel, bool subgrid, Vec3 wSn)
        {
            Vec3 l = WireMath.ToLocal(o, q, s, wSn);
            return subgrid ? InstanceMath.InvertRigid(rel).Transform(l) : l;
        }

        /// <summary>The inverse of <see cref="HitLocal"/>: an SE-local point of the grid (or subgrid) to SN world.</summary>
        public static Vec3 LocalToWorld(Vec3 o, Quat q, double s, SeMatrix rel, bool subgrid, Vec3 local)
        {
            return WireMath.Apply(o, q, s, subgrid ? rel.Transform(local) : local);
        }

        /// <summary>
 /// The character section's capsule in the character's SE model space (CONTRACT §2.6): centre = the canonical
 /// pelvis bone's translation (<see cref="MirrorSkeleton.Pelvis"/>), axis = unit pelvis → head
 /// (<see cref="MirrorSkeleton.Head"/>), from the C1 bone rows (12 floats per canonical bone: Right, Up,
 /// Backward, Translation); else (bones not valid, too few, not finite or head within 1 mm of the pelvis)
 /// (0, 0.9, 0) and (0, 1, 0).
 /// </summary>
        public static void CharacterCapsule(float[] canonBoneRows, bool bonesValid, out Vec3 centreSe, out Vec3 axisSe)
        {
            const int p = MirrorSkeleton.Pelvis * 12 + 9, h = MirrorSkeleton.Head * 12 + 9;
            if (bonesValid && canonBoneRows != null && canonBoneRows.Length >= h + 3)
            {
                var pel = new Vec3(canonBoneRows[p], canonBoneRows[p + 1], canonBoneRows[p + 2]);
                var head = new Vec3(canonBoneRows[h], canonBoneRows[h + 1], canonBoneRows[h + 2]);
                Vec3 d = head - pel;
                if (pel.IsFinite && head.IsFinite && d.Length > 1e-3)
                {
                    centreSe = pel;
                    axisSe = d / d.Length;
                    return;
                }
            }
            centreSe = FallbackCapsuleCentre;
            axisSe = Vec3.UnitY;
        }

        /// <summary>
 /// The character capsule in SN world, for a capsule with <c>direction</c> Y on a unit-scale child: centre =
 /// <c>Apply(o, q, s, centreSe)</c>; rotation = the frame whose +Y is the unit <c>S·q·Mirror(axisSe)</c> and
 /// whose +Z is <c>q·ẑ</c> orthogonalised against it (<c>q·x̂</c> when parallel), w ≥ 0.
 /// </summary>
        public static void CharacterCapsuleWorld(Vec3 o, Quat q, double s, Vec3 centreSe, Vec3 axisSe, out Vec3 centreSn, out Quat rotationSn)
        {
            centreSn = WireMath.Apply(o, q, s, centreSe);
            Vec3 y = WireMath.ApplyDir(q, s, axisSe);
            y = y.Length > 1e-12 ? y.Normalized() : q.Rotate(Vec3.UnitY);
            Vec3 z = q.Rotate(Vec3.UnitZ);
            z = z - y * Vec3.Dot(z, y);
            if (z.Length < 1e-6)
            {
                z = q.Rotate(Vec3.UnitX);
                z = z - y * Vec3.Dot(z, y);
            }
            z = z.Normalized();
            Vec3 x = Vec3.Cross(y, z);
            rotationSn = Quat.FromColumns(x, y, z);
        }

        // ------------------------------------------------------------------------------------------------ S6.G1-E3 oracle

 /// <summary>
 /// The worst corner deviation (metres) of one K-pre-scaled box (<see cref="WireMath.PrescaleBox"/>: half extents
 /// <paramref name="h"/> along the SE-local axes e0, e1, e2) from the exact affine, on a unit-scale root with
 /// rotation <paramref name="q"/> at scale <paramref name="s"/>: max over the 8 corners of
 /// |placed corner − K·Mirror(corner offset)|. Independent of o, c_sec and the box centre (the centre is exact).
 /// 0 (to rounding) for a level hull with level/vertical box axes; the <c>wire_ref.py s6bound</c> tool takes the
 /// worst of it over random headings, azimuths and axes at a depth and tilt.
 /// </summary>
        public static double PrescaleCornerError(Quat q, double s, Vec3 h, Vec3 e0, Vec3 e1, Vec3 e2)
        {
            ProxyBox pb = WireMath.PrescaleBox(q, s, Vec3.Zero, Vec3.Zero, h, e0, e1, e2);
            double worst = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vec3 d = e0 * (sx * h.X) + e1 * (sy * h.Y) + e2 * (sz * h.Z);
                        Vec3 exact = WireMath.K(q, s, WireMath.Mirror(d));
                        worst = Math.Max(worst, (pb.CornerSectionLocal(sx, sy, sz) - exact).Length);
                    }
            return worst;
        }

        /// <summary>The tilt of a hull, degrees: the angle between its up <c>q·ŷ</c> and SN-world up.</summary>
        public static double TiltDegrees(Quat q)
        {
            double c = Vec3.Dot(q.Rotate(Vec3.UnitY), Vec3.UnitY);
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, c))) * (180.0 / Math.PI);
        }
    }

    /// <summary>
 /// The round-robin budget of collider operations per physics step (CONTRACT §2.6: at most 256 shape creations or
 /// refreshes per step, round robin; a 1,000-box skin takes 4 steps).
 /// </summary>
    public static class ShapeBudget
    {
        /// <summary>
 /// Grants this step's shapes: starting at <paramref name="cursor"/> (mod <paramref name="count"/>), each work item
 /// i in turn gets <c>min(pending[i], remaining)</c> into <paramref name="grant"/>[i] (others 0) until the
 /// <paramref name="budget"/> is spent or every item was visited once. Returns the cursor for the next step: the
 /// item that was cut short, else the one after the last item served (the input cursor when nothing is pending).
 /// </summary>
        public static int Plan(int[] pending, int count, int cursor, int budget, int[] grant)
        {
            if (pending == null) throw new ArgumentNullException("pending");
            if (grant == null) throw new ArgumentNullException("grant");
            if (count < 0 || count > pending.Length || count > grant.Length) throw new ArgumentOutOfRangeException("count");
            for (int i = 0; i < count; i++) grant[i] = 0;
            if (count == 0) return 0;
            int start = ((cursor % count) + count) % count;
            int remaining = Math.Max(0, budget);
            int next = start;
            bool any = false;
            for (int k = 0; k < count && remaining > 0; k++)
            {
                int i = (start + k) % count;
                int want = Math.Max(0, pending[i]);
                if (want == 0) continue;
                int g = Math.Min(want, remaining);
                grant[i] = g;
                remaining -= g;
                any = true;
                next = g < want ? i : (i + 1) % count;
            }
            return any ? next : start;
        }
    }
}
