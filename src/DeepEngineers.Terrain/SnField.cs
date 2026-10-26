using System;

namespace DeepEngineers.Terrain
{
    /// <summary>Conservative content class of a region (CONTRACT 3.1 "Classification").</summary>
    public enum ContentClass
    {
        /// <summary>Every sample that can reach the region is far empty or has no data: content 0 everywhere.</summary>
        Empty,
        /// <summary>Every such sample is far solid: content 255 everywhere.</summary>
        Full,
        /// <summary>Anything else (the honest answer whenever not proven uniform).</summary>
        Mixed,
    }

    /// <summary>A signed-distance field in SN world space (metres; positive inside the terrain).</summary>
    public interface ISnField
    {
        /// <summary>
 /// D(p): the trilinear interpolation of the samples around the SN point p (CONTRACT 3.1 "Content mapping").
 /// <paramref name="allFarSameSign"/> is true when the 8 stencil samples are all far and of one sign (then D = ±1).
 /// </summary>
        float Sample(double snX, double snY, double snZ, out bool allFarSameSign);

        /// <summary>Conservative class of every sample within 1 m (on every axis) of the SN box [lo, hi].</summary>
        ContentClass ClassifySn(double loX, double loY, double loZ, double hiX, double hiY, double hiZ);
    }

    /// <summary>
 /// A field that holds a reader guard across a whole request (the tile sampler enters once per call instead of once
 /// per sample). Internal seam between <see cref="TileSampler"/> and <see cref="SnField"/>.
 /// </summary>
    internal interface IGuardedField
    {
        bool BeginRequest();
        void EndRequest();
        float SampleUnguarded(double snX, double snY, double snZ, out bool allFarSameSign, ref LeafCache cache);
        ContentClass ClassifyUnguarded(double loX, double loY, double loZ, double hiX, double hiY, double hiZ);
    }

    /// <summary>
 /// Subnautica's terrain as a field: samples s(v) at P(v) = origin + v + <see cref="SampleOffset"/> (voxel centre,
 /// HYPOTHESIS decided by S4.A-T2), trilinear in between. Thread-safe, allocation-free.
 /// </summary>
    public sealed class SnField : ISnField, IGuardedField
    {
        /// <summary>Sample offset inside the voxel (CONTRACT 3.1: o = (0.5, 0.5, 0.5), HYPOTHESIS). Python twin: sn_field.SAMPLE_OFFSET.</summary>
        public const double SampleOffset = 0.5;

        private readonly SnOctreeSource _src;

        public SnField(SnOctreeSource src)
        {
            if (src == null) throw new ArgumentNullException("src");
            _src = src;
        }

        public SnOctreeSource Source { get { return _src; } }

        public float Sample(double snX, double snY, double snZ, out bool allFarSameSign)
        {
            if (!_src.BeginRead())
            {
                allFarSameSign = true;
                return -1f;
            }
            try { return SampleCore(snX, snY, snZ, out allFarSameSign); }
            finally { _src.EndRead(); }
        }

        public ContentClass ClassifySn(double loX, double loY, double loZ, double hiX, double hiY, double hiZ)
        {
            bool guarded = _src.BeginRead();
            try { return Classify(loX, loY, loZ, hiX, hiY, hiZ, guarded); }
            finally { if (guarded) _src.EndRead(); }
        }

        bool IGuardedField.BeginRequest() { return _src.BeginRead(); }

        void IGuardedField.EndRequest() { _src.EndRead(); }

        float IGuardedField.SampleUnguarded(double snX, double snY, double snZ, out bool allFarSameSign, ref LeafCache cache)
        {
            return SampleCached(snX, snY, snZ, out allFarSameSign, ref cache);
        }

        ContentClass IGuardedField.ClassifyUnguarded(double loX, double loY, double loZ, double hiX, double hiY, double hiZ)
        {
            return Classify(loX, loY, loZ, hiX, hiY, hiZ, true);
        }

        /// <summary>Sample without the guard (the caller holds <see cref="SnOctreeSource.BeginRead"/>).</summary>
        internal float SampleCore(double snX, double snY, double snZ, out bool allFarSameSign)
        {
            var cache = new LeafCache();
            return SampleCached(snX, snY, snZ, out allFarSameSign, ref cache);
        }

        /// <summary>Sample without the guard, through the caller's leaf cache (valid for one guarded call).</summary>
        internal unsafe float SampleCached(double snX, double snY, double snZ, out bool allFarSameSign, ref LeafCache cache)
        {
            double ux = snX - SnOctreeFormat.OriginX - SampleOffset;
            double uy = snY - SnOctreeFormat.OriginY - SampleOffset;
            double uz = snZ - SnOctreeFormat.OriginZ - SampleOffset;
            double fx0 = Math.Floor(ux), fy0 = Math.Floor(uy), fz0 = Math.Floor(uz);
            // far outside the world: every sample is "no data"
            if (fx0 < -2 || fy0 < -2 || fz0 < -2 || fx0 > SnOctreeFormat.VoxelsX + 1 || fy0 > SnOctreeFormat.VoxelsY + 1 || fz0 > SnOctreeFormat.VoxelsZ + 1)
            {
                allFarSameSign = true;
                return -1f;
            }
            int x0 = (int)fx0, y0 = (int)fy0, z0 = (int)fz0;
            double fx = ux - fx0, fy = uy - fy0, fz = uz - fz0;
            // the previous stencil of this call (consecutive SE samples move Subnautica's stencil by about one voxel):
 // reuse its overlapping corners, fetch the others (corner index x + 2y + 4z)
            int dx = x0 - cache.SX, dy = y0 - cache.SY, dz = z0 - cache.SZ;
            bool reuse = cache.SValid && (uint)(dx + 1) <= 2u && (uint)(dy + 1) <= 2u && (uint)(dz + 1) <= 2u;
            double* v = stackalloc double[8];
            byte* fr = stackalloc byte[8];
            for (int c = 0; c < 8; c++)
            {
                int i = c & 1, j = (c >> 1) & 1, k = c >> 2;
                if (reuse)
                {
                    int pi = i + dx, pj = j + dy, pk = k + dz;
                    if ((uint)pi <= 1u && (uint)pj <= 1u && (uint)pk <= 1u)
                    {
                        int pc = pi | (pj << 1) | (pk << 2);
                        v[c] = cache.SVal[pc];
                        fr[c] = cache.SFar[pc];
                        continue;
                    }
                }
                bool far;
                v[c] = S(x0 + i, y0 + j, z0 + k, out far, ref cache);
                fr[c] = far ? (byte)1 : (byte)0;
            }
            bool allFar = true;
            int pos = 0, neg = 0;
            for (int c = 0; c < 8; c++)
            {
                cache.SVal[c] = v[c];
                cache.SFar[c] = fr[c];
                if (fr[c] == 0) allFar = false;
                if (v[c] > 0) pos++;
                else neg++;
            }
            cache.SX = x0;
            cache.SY = y0;
            cache.SZ = z0;
            cache.SValid = true;
            allFarSameSign = allFar && (pos == 0 || neg == 0);
            return (float)Trilinear(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], fx, fy, fz);
        }

        /// <summary>
 /// The trilinear form shared with sn_field.py (lerp(a, b, t) = a + (b - a)·t; x first, then y, then z).
 /// </summary>
        public static double Trilinear(double s000, double s100, double s010, double s110, double s001, double s101, double s011, double s111,
                                       double fx, double fy, double fz)
        {
            double c00 = s000 + (s100 - s000) * fx;
            double c10 = s010 + (s110 - s010) * fx;
            double c01 = s001 + (s101 - s001) * fx;
            double c11 = s011 + (s111 - s011) * fx;
            double c0 = c00 + (c10 - c00) * fy;
            double c1 = c01 + (c11 - c01) * fy;
            return c0 + (c1 - c0) * fz;
        }

        /// <summary>s(v) of world voxel v; no data = far empty (-1).</summary>
        private double S(int vx, int vy, int vz, out bool far, ref LeafCache cache)
        {
            byte t, d;
            int leaf;
            if (!_src.SampleCached(vx, vy, vz, ref cache, out t, out d, out leaf))
            {
                far = true;
                return TerrainMutants.SignFlip ? 1.0 : -1.0;
            }
            far = d == 0;
            return SnOctreeFormat.SampleD(t, d);
        }

        private ContentClass Classify(double loX, double loY, double loZ, double hiX, double hiY, double hiZ, bool guarded)
        {
            if (double.IsNaN(loX + loY + loZ + hiX + hiY + hiZ)) return ContentClass.Mixed;
            // samples v with P(v) within 1 m of the box: v in [lo - 1 - origin - o, hi + 1 - origin - o]
            int vx0 = CeilIdx(loX - 1.0 - SnOctreeFormat.OriginX - SampleOffset);
            int vy0 = CeilIdx(loY - 1.0 - SnOctreeFormat.OriginY - SampleOffset);
            int vz0 = CeilIdx(loZ - 1.0 - SnOctreeFormat.OriginZ - SampleOffset);
            int vx1 = FloorIdx(hiX + 1.0 - SnOctreeFormat.OriginX - SampleOffset);
            int vy1 = FloorIdx(hiY + 1.0 - SnOctreeFormat.OriginY - SampleOffset);
            int vz1 = FloorIdx(hiZ + 1.0 - SnOctreeFormat.OriginZ - SampleOffset);
            if (vx1 < vx0 || vy1 < vy0 || vz1 < vz0) return ContentClass.Empty;   // no sample can reach the box
            bool hasEmpty = false, hasSolid = false;
            // parts outside the SN world are "no data" samples
            if (vx0 < 0 || vy0 < 0 || vz0 < 0 || vx1 >= SnOctreeFormat.VoxelsX || vy1 >= SnOctreeFormat.VoxelsY || vz1 >= SnOctreeFormat.VoxelsZ)
                hasEmpty = true;
            int cx0 = Math.Max(vx0, 0), cy0 = Math.Max(vy0, 0), cz0 = Math.Max(vz0, 0);
            int cx1 = Math.Min(vx1, SnOctreeFormat.VoxelsX - 1), cy1 = Math.Min(vy1, SnOctreeFormat.VoxelsY - 1), cz1 = Math.Min(vz1, SnOctreeFormat.VoxelsZ - 1);
            if (cx1 < cx0 || cy1 < cy0 || cz1 < cz0) return ContentClass.Empty;     // entirely outside the world
            for (int ox = cx0 >> 5; ox <= cx1 >> 5; ox++)
                for (int oy = cy0 >> 5; oy <= cy1 >> 5; oy++)
                    for (int oz = cz0 >> 5; oz <= cz1 >> 5; oz++)
                    {
                        OctreeState st = _src.StateCore(ox, oy, oz, guarded);
                        // a Mixed octree is refined to the leaves the samples fall in (needs the guard: mapped memory)
                        if (st == OctreeState.Mixed && guarded)
                            st = _src.ClassifyLeaves(ox, oy, oz, cx0 - (ox << 5), cy0 - (oy << 5), cz0 - (oz << 5),
                                                     cx1 - (ox << 5), cy1 - (oy << 5), cz1 - (oz << 5));
                        switch (st)
                        {
                            case OctreeState.Mixed: return ContentClass.Mixed;
                            case OctreeState.Solid: hasSolid = true; break;
                            default: hasEmpty = true; break;
                        }
                        if (hasEmpty && hasSolid) return ContentClass.Mixed;
                    }
            if (hasSolid) return hasEmpty ? ContentClass.Mixed : ContentClass.Full;
            return ContentClass.Empty;
        }

        private static int CeilIdx(double v)
        {
            if (v < -1e6) return -1000000;
            if (v > 1e6) return 1000000;
            return (int)Math.Ceiling(v);
        }

        private static int FloorIdx(double v)
        {
            if (v < -1e6) return -1000000;
            if (v > 1e6) return 1000000;
            return (int)Math.Floor(v);
        }
    }

    /// <summary>
 /// S4.A-T0b: a flat SN plane at <c>planeSnY</c>, solid below, with the untruncated distance D = planeSnY - y (no
 /// samples: an exact field to check the tile sampler's positions, LOD scaling and bytes against).
 /// </summary>
    public sealed class SyntheticPlaneField : ISnField
    {
        /// <summary>Largest LOD voxel the classification must cover (2^10 m: D/2^L saturates beyond it at every LOD).</summary>
        private const double SaturationMargin = 1024.0;

        public readonly double PlaneSnY;

        public SyntheticPlaneField(double planeSnY)
        {
            PlaneSnY = planeSnY;
        }

        public float Sample(double snX, double snY, double snZ, out bool allFarSameSign)
        {
            allFarSameSign = false;
            return (float)(PlaneSnY - snY);
        }

        /// <summary>Empty when the box lies at least 2^10 m + 1 m above the plane, Full when as far below, else Mixed.</summary>
        public ContentClass ClassifySn(double loX, double loY, double loZ, double hiX, double hiY, double hiZ)
        {
            if (loY - 1.0 >= PlaneSnY + SaturationMargin) return ContentClass.Empty;
            if (hiY + 1.0 <= PlaneSnY - SaturationMargin) return ContentClass.Full;
            return ContentClass.Mixed;
        }
    }
}
