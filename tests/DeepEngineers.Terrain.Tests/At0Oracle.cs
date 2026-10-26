using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>One batch exported by the Python oracle (tools/sn-octrees/sn_octree_batch.py --at0).</summary>
    internal sealed class At0Data
    {
        public const int N = SnOctreeFormat.BatchVoxels;
        public int Bx, By, Bz;
        public bool FilePresent, Bad;
        public long FileBytes;
        public int SampleOffsetMilli;
        public byte[] Type, Density, LeafSize;
        public SnBatchSummary Summary;
        public string OracleSha256;
        public float[] Iso;           // x, y, z triplets, batch-local SN metres

        public string Stem { get { return "at0-" + Bx + "-" + By + "-" + Bz; } }

        public static At0Data Load(string dir, int bx, int by, int bz)
        {
            var a = new At0Data { Bx = bx, By = by, Bz = bz };
            string stem = Path.Combine(dir, a.Stem);
            byte[] raw = File.ReadAllBytes(stem + ".bin");
            if (raw.Length != 48 + 3 * N * N * N || Encoding.ASCII.GetString(raw, 0, 5) != "DEAT0")
                throw new InvalidDataException("bad DEAT0 file " + stem + ".bin");
            using (var r = new BinaryReader(new MemoryStream(raw, 8, 40)))
            {
                uint version = r.ReadUInt32();
                int hbx = r.ReadInt32(), hby = r.ReadInt32(), hbz = r.ReadInt32();
                uint n = r.ReadUInt32();
                uint flags = r.ReadUInt32();
                a.FileBytes = (long)r.ReadUInt64();
                a.SampleOffsetMilli = (int)r.ReadUInt32();
                if (version != 1 || n != N || hbx != bx || hby != by || hbz != bz)
                    throw new InvalidDataException("DEAT0 header mismatch in " + stem + ".bin");
                a.FilePresent = (flags & 1) != 0;
                a.Bad = (flags & 2) != 0;
            }
            int k = N * N * N;
            a.Type = new byte[k];
            a.Density = new byte[k];
            a.LeafSize = new byte[k];
            Buffer.BlockCopy(raw, 48, a.Type, 0, k);
            Buffer.BlockCopy(raw, 48 + k, a.Density, 0, k);
            Buffer.BlockCopy(raw, 48 + 2 * k, a.LeafSize, 0, k);
            string json = File.ReadAllText(stem + ".json");
            a.Summary = SnBatchSummary.FromJson(json);
            var d = (Dictionary<string, object>)TerrainJson.Parse(json);
            a.OracleSha256 = d.ContainsKey("oracleSha256") ? (string)d["oracleSha256"] : null;
            byte[] iso = File.ReadAllBytes(stem + "-iso.bin");
            a.Iso = new float[iso.Length / 4];
            Buffer.BlockCopy(iso, 0, a.Iso, 0, a.Iso.Length * 4);
            return a;
        }

        /// <summary>True when the three files exist and were written by the current oracle script.</summary>
        public static bool Fresh(string dir, int bx, int by, int bz, string oracleSha)
        {
            string stem = Path.Combine(dir, "at0-" + bx + "-" + by + "-" + bz);
            if (!File.Exists(stem + ".bin") || !File.Exists(stem + ".json") || !File.Exists(stem + "-iso.bin")) return false;
            try
            {
                var d = (Dictionary<string, object>)TerrainJson.Parse(File.ReadAllText(stem + ".json"));
                return d.ContainsKey("oracleSha256") && (string)d["oracleSha256"] == oracleSha;
            }
            catch (FormatException) { return false; }
        }

        public static string FileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream f = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(f);
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }

    /// <summary>
 /// The oracle's field over one batch, in sample-index space u (sample k at u = k; SN batch-local metre = u + 0.5):
 /// D(u) = trilinear interpolation of s (clean rule, no mutant), and its gradient. Written for the test only; it
 /// does not use the library's SnField (the thing under test).
 /// </summary>
    internal sealed class OracleField
    {
        public const int N = SnOctreeFormat.BatchVoxels;
        public readonly double[] S;
        public readonly bool[] Far;

        public OracleField(byte[] type, byte[] density)
        {
            S = new double[type.Length];
            Far = new bool[type.Length];
            for (int i = 0; i < S.Length; i++)
            {
                byte t = type[i], d = density[i];
                Far[i] = d == 0;
                S[i] = d == 0 ? (t > 0 ? 1.0 : -1.0) : (d - 126.0) / 126.0;
            }
        }

        private static int Idx(int x, int y, int z) { return (x * N + y) * N + z; }

        private static void Cell(double u, out int c, out double f)
        {
            double fl = Math.Floor(u);
            c = (int)fl;
            if (c < 0) c = 0;
            if (c > N - 2) c = N - 2;
            f = u - c;
        }

        public double D(double ux, double uy, double uz)
        {
            int cx, cy, cz;
            double fx, fy, fz;
            Cell(ux, out cx, out fx);
            Cell(uy, out cy, out fy);
            Cell(uz, out cz, out fz);
            double s000 = S[Idx(cx, cy, cz)], s100 = S[Idx(cx + 1, cy, cz)], s010 = S[Idx(cx, cy + 1, cz)], s110 = S[Idx(cx + 1, cy + 1, cz)];
            double s001 = S[Idx(cx, cy, cz + 1)], s101 = S[Idx(cx + 1, cy, cz + 1)], s011 = S[Idx(cx, cy + 1, cz + 1)], s111 = S[Idx(cx + 1, cy + 1, cz + 1)];
            double c00 = s000 + (s100 - s000) * fx, c10 = s010 + (s110 - s010) * fx;
            double c01 = s001 + (s101 - s001) * fx, c11 = s011 + (s111 - s011) * fx;
            double c0 = c00 + (c10 - c00) * fy, c1 = c01 + (c11 - c01) * fy;
            return c0 + (c1 - c0) * fz;
        }

        public void Grad(double ux, double uy, double uz, out double gx, out double gy, out double gz)
        {
            int cx, cy, cz;
            double fx, fy, fz;
            Cell(ux, out cx, out fx);
            Cell(uy, out cy, out fy);
            Cell(uz, out cz, out fz);
            double s000 = S[Idx(cx, cy, cz)], s100 = S[Idx(cx + 1, cy, cz)], s010 = S[Idx(cx, cy + 1, cz)], s110 = S[Idx(cx + 1, cy + 1, cz)];
            double s001 = S[Idx(cx, cy, cz + 1)], s101 = S[Idx(cx + 1, cy, cz + 1)], s011 = S[Idx(cx, cy + 1, cz + 1)], s111 = S[Idx(cx + 1, cy + 1, cz + 1)];
            gx = Lerp(Lerp(s100 - s000, s110 - s010, fy), Lerp(s101 - s001, s111 - s011, fy), fz);
            gy = Lerp(Lerp(s010 - s000, s110 - s100, fx), Lerp(s011 - s001, s111 - s101, fx), fz);
            gz = Lerp(Lerp(s001 - s000, s101 - s100, fx), Lerp(s011 - s010, s111 - s110, fx), fy);
        }

        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        /// <summary>
 /// The A-T0 iso metric (same definition in tests/s4/at0_compare.py): the zero of D on the axis line through the
 /// edge start A (searched over t in [-1, 2], u kept in [0, N - 1]; D is piecewise linear there with breaks at
 /// integer u; a zero is a strict sign change or a point where D == 0 exactly) nearest to the content crossing
 /// tq; e = |t* - tq| · |∂D/∂axis| / |∇D| at the zero (the along-edge error turned into a first-order distance to
 /// the surface). False (e = 1) when D has no zero there.
 /// </summary>
        public bool EdgeError(double ax, double ay, double az, int axis, double tq, out double e)
        {
            double a = axis == 0 ? ax : axis == 1 ? ay : az;
            double lo = Math.Max(-1.0, -a), hi = Math.Min(2.0, (N - 1) - a);
            double best;
            if (!NearestZero(ax, ay, az, axis, lo, hi, tq, out best)) { e = 1.0; return false; }
            e = Scaled(ax, ay, az, axis, best, Math.Abs(best - tq));
            return true;
        }

        /// <summary>
 /// For an edge [A, A + axis] whose end values of D straddle 0 while the content does not cross on it: the oracle
 /// zero on the edge, its distance to the nearer edge end, scaled like <see cref="EdgeError"/>.
 /// </summary>
        public double MissedError(double ax, double ay, double az, int axis)
        {
            double zero;
            if (!NearestZero(ax, ay, az, axis, 0.0, 1.0, 0.5, out zero)) return 0.0;
            return Scaled(ax, ay, az, axis, zero, Math.Min(zero, 1.0 - zero));
        }

        /// <summary>The zero of D on t in [lo, hi] nearest to tq (strict sign changes between break points, or exact zeros).</summary>
        private bool NearestZero(double ax, double ay, double az, int axis, double lo, double hi, double tq, out double best)
        {
            double a = axis == 0 ? ax : axis == 1 ? ay : az;
            best = double.NaN;
            double bestDist = double.PositiveInfinity;
            if (!(hi >= lo)) return false;
            double tPrev = lo, dPrev = DAt(ax, ay, az, axis, lo);
            if (dPrev == 0) { best = lo; bestDist = Math.Abs(lo - tq); }
            int kStart = (int)Math.Floor(a + lo) + 1;
            for (int k = kStart; ; k++)
            {
                double t = k - a;
                bool last = !(t < hi);
                if (last) t = hi;
                if (t > tPrev)
                {
                    double d = DAt(ax, ay, az, axis, t);
                    double z = double.NaN;
                    if (d == 0) z = t;
                    else if ((dPrev > 0 && d < 0) || (dPrev < 0 && d > 0)) z = tPrev + (t - tPrev) * dPrev / (dPrev - d);
                    if (!double.IsNaN(z))
                    {
                        double dist = Math.Abs(z - tq);
                        if (dist < bestDist) { bestDist = dist; best = z; }
                    }
                    tPrev = t;
                    dPrev = d;
                }
                if (last) break;
            }
            return !double.IsNaN(best);
        }

        private double Scaled(double ax, double ay, double az, int axis, double tz, double dist)
        {
            double px = ax + (axis == 0 ? tz : 0), py = ay + (axis == 1 ? tz : 0), pz = az + (axis == 2 ? tz : 0);
            double gx, gy, gz;
            Grad(px, py, pz, out gx, out gy, out gz);
            double gn = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            double ga = Math.Abs(axis == 0 ? gx : axis == 1 ? gy : gz);
            return gn > 1e-12 ? dist * ga / gn : dist;
        }

        private double DAt(double ax, double ay, double az, int axis, double t)
        {
            return D(ax + (axis == 0 ? t : 0), ay + (axis == 1 ? t : 0), az + (axis == 2 ? t : 0));
        }

        /// <summary>D = 0 crossings on the sample-grid edges, sorted like the oracle's -iso.bin (float32 x, y, z, batch-local metres).</summary>
        public float[] SampleGridCrossings()
        {
            var pts = new List<float[]>();
            for (int axis = 0; axis < 3; axis++)
                for (int x = 0; x < N; x++)
                    for (int y = 0; y < N; y++)
                        for (int z = 0; z < N; z++)
                        {
                            int bx = x + (axis == 0 ? 1 : 0), by = y + (axis == 1 ? 1 : 0), bz = z + (axis == 2 ? 1 : 0);
                            if (bx >= N || by >= N || bz >= N) continue;
                            double sa = S[Idx(x, y, z)], sb = S[Idx(bx, by, bz)];
                            if ((sa >= 0) == (sb >= 0)) continue;
                            double t = sa / (sa - sb);
                            double px = x + 0.5, py = y + 0.5, pz = z + 0.5;
                            if (axis == 0) px += t; else if (axis == 1) py += t; else pz += t;
                            pts.Add(new[] { (float)px, (float)py, (float)pz });
                        }
            pts.Sort((p, q) => p[0] != q[0] ? p[0].CompareTo(q[0]) : p[1] != q[1] ? p[1].CompareTo(q[1]) : p[2].CompareTo(q[2]));
            var r = new float[pts.Count * 3];
            for (int i = 0; i < pts.Count; i++) { r[3 * i] = pts[i][0]; r[3 * i + 1] = pts[i][1]; r[3 * i + 2] = pts[i][2]; }
            return r;
        }
    }
}
