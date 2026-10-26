using System;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Terrain
{
    /// <summary>S8a Stage F2: the ring field's hashes (tools/planet/s8a_ring3d.py fnv64, mix64, hash_u; s8a_brushes._hash).</summary>
    public static class RingHash
    {
        public const ulong FnvOffset = 0xcbf29ce484222325UL, FnvPrime = 0x100000001b3UL;
        public const ulong M1 = 0x9E3779B97F4A7C15UL, M2 = 0xC2B2AE3D27D4EB4FUL, M3 = 0x165667B19E3779F9UL;
        public const ulong F1 = 0xBF58476D1CE4E5B9UL, F2 = 0x94D049BB133111EBUL;
        /// <summary>2^-53.</summary>
        public const double U53 = 1.0 / 9007199254740992.0;

        public static ulong Fnv(byte[] data, ulong h = FnvOffset)
        {
            unchecked
            {
                for (int i = 0; i < data.Length; i++) h = (h ^ data[i]) * FnvPrime;
            }
            return h;
        }

        public static ulong Fnv(byte[] data, int offset, int count, ulong h)
        {
            unchecked
            {
                for (int i = offset; i < offset + count; i++) h = (h ^ data[i]) * FnvPrime;
            }
            return h;
        }

        public static ulong FnvUtf8(string s) { return Fnv(Encoding.UTF8.GetBytes(s)); }

        public static ulong Mix64(ulong h)
        {
            unchecked
            {
                h ^= h >> 30;
                h *= F1;
                h ^= h >> 27;
                h *= F2;
                h ^= h >> 31;
            }
            return h;
        }

        /// <summary>FNV-1a 64 over the int64 little-endian bytes of the values (s8a_brushes._hash).</summary>
        public static ulong Ints(long a, long b, long c, long d)
        {
            ulong h = FnvOffset;
            h = Long(h, a);
            h = Long(h, b);
            h = Long(h, c);
            return Long(h, d);
        }

        private static ulong Long(ulong h, long v)
        {
            unchecked
            {
                ulong u = (ulong)v;
                for (int k = 0; k < 8; k++)
                {
                    h = (h ^ (u & 0xFF)) * FnvPrime;
                    u >>= 8;
                }
            }
            return h;
        }

        /// <summary>Uniform [0, 1) of four integers: the top 53 bits of <see cref="Ints"/> (hash_u).</summary>
        public static double U(long a, long b, long c, long d) { return (Ints(a, b, c, d) >> 11) * U53; }
    }

    /// <summary>
 /// S8a Stage F2: fBm and ridged 3D gradient noise for the ring's 3D rules (s8a_ring3d.Noise over sculpt_de4546b.gradient_noise:
 /// the Perlin layout over a splitmix-hashed lattice; per octave a seed and an offset from FNV of "seed|name|octave"). Plain
 /// double arithmetic in the twin's order.
 /// </summary>
    public sealed class RingNoise
    {
        private static readonly double[] GX = { 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0 };
        private static readonly double[] GY = { 1, 1, -1, -1, 0, 0, 0, 0, 1, -1, 1, -1 };
        private static readonly double[] GZ = { 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1 };

        private readonly ulong[] _seeds;
        private readonly double[] _ox, _oy, _oz;
        public int Octaves { get; private set; }

        public RingNoise(long seed, string name, int octaves)
        {
            Octaves = octaves;
            _seeds = new ulong[octaves];
            _ox = new double[octaves];
            _oy = new double[octaves];
            _oz = new double[octaves];
            for (int o = 0; o < octaves; o++)
            {
                ulong h = RingHash.FnvUtf8(seed.ToString(CultureInfo.InvariantCulture) + "|" + name + "|" + o.ToString(CultureInfo.InvariantCulture));
                _seeds[o] = h >> 1;
                _ox[o] = Offset(h, 0);
                _oy[o] = Offset(h, 1);
                _oz[o] = Offset(h, 2);
            }
        }

        private static double Offset(ulong h, int k)
        {
            ulong m = unchecked((ulong)(k + 1) * RingHash.M3);
            return -1e4 + 2e4 * ((RingHash.Mix64(h ^ m) >> 11) * RingHash.U53);
        }

        private static double G(long ix, long iy, long iz, ulong seed, double dx, double dy, double dz)
        {
            ulong h = unchecked(((ulong)ix * RingHash.M1) ^ ((ulong)iy * RingHash.M2) ^ ((ulong)iz * RingHash.M3) ^ seed);
            int k = (int)(RingHash.Mix64(h) % 12UL);
            return GX[k] * dx + GY[k] * dy + GZ[k] * dz;
        }

        /// <summary>One gradient noise value, about [-1, 1] (sculpt_de4546b.gradient_noise).</summary>
        public static double Gradient(double x, double y, double z, ulong seed)
        {
            double fx0 = Math.Floor(x), fy0 = Math.Floor(y), fz0 = Math.Floor(z);
            double fx = x - fx0, fy = y - fy0, fz = z - fz0;
            long ix = (long)fx0, iy = (long)fy0, iz = (long)fz0;
            double u = fx * fx * fx * (fx * (fx * 6.0 - 15.0) + 10.0);
            double v = fy * fy * fy * (fy * (fy * 6.0 - 15.0) + 10.0);
            double w = fz * fz * fz * (fz * (fz * 6.0 - 15.0) + 10.0);
            double n000 = G(ix, iy, iz, seed, fx, fy, fz);
            double n100 = G(ix + 1, iy, iz, seed, fx - 1, fy, fz);
            double n010 = G(ix, iy + 1, iz, seed, fx, fy - 1, fz);
            double n110 = G(ix + 1, iy + 1, iz, seed, fx - 1, fy - 1, fz);
            double n001 = G(ix, iy, iz + 1, seed, fx, fy, fz - 1);
            double n101 = G(ix + 1, iy, iz + 1, seed, fx - 1, fy, fz - 1);
            double n011 = G(ix, iy + 1, iz + 1, seed, fx, fy - 1, fz - 1);
            double n111 = G(ix + 1, iy + 1, iz + 1, seed, fx - 1, fy - 1, fz - 1);
            double x00 = n000 + u * (n100 - n000);
            double x10 = n010 + u * (n110 - n010);
            double x01 = n001 + u * (n101 - n001);
            double x11 = n011 + u * (n111 - n011);
            double y0 = x00 + v * (x10 - x00);
            double y1 = x01 + v * (x11 - x01);
            return y0 + w * (y1 - y0);
        }

        public double Fbm(double px, double py, double pz, double wavelength)
        {
            double total = 0.0, amp = 1.0, f = 1.0 / wavelength, norm = 0.0;
            for (int o = 0; o < Octaves; o++)
            {
                total = total + amp * Gradient(px * f + _ox[o], py * f + _oy[o], pz * f + _oz[o], _seeds[o]);
                norm = norm + amp;
                amp = amp * 0.5;
                f = f * 2.0;
            }
            return total / norm;
        }

        public double Ridged(double px, double py, double pz, double wavelength)
        {
            double total = 0.0, amp = 1.0, f = 1.0 / wavelength, norm = 0.0;
            for (int o = 0; o < Octaves; o++)
            {
                double n = 1.0 - Math.Abs(Gradient(px * f + _ox[o], py * f + _oy[o], pz * f + _oz[o], _seeds[o]));
                total = total + amp * n * n;
                norm = norm + amp;
                amp = amp * 0.5;
                f = f * 2.0;
            }
            return total / norm;
        }
    }
}
