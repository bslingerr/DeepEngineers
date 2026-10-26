using System;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// The site map F (docs/CONTRACT.md sections 2.3 and 3): "sea-level azimuthal equidistant + altitude" between SE
 /// world (VRage, right-handed, Y up, doubles, planet-centred sphere) and SN world (Unity, left-handed, Y up, flat
 /// tile). C# port of tools/worldmap/worldmap_ref.py <c>Site</c>, operation for operation; the Python file is the
 /// oracle (WM-1). Used by the SE plugin only, always in doubles.
 /// <list type="bullet">
 /// <item>SN y = |P - C| - Rs (altitude above the Water Mod sea sphere);</item>
 /// <item>SN horizontal = Rs * theta along the azimuth from U0, then yaw psi about +Y and offset O.</item>
 /// </list>
 /// The anchor frame (E0, U0, N0) is orthonormal with E0 x N0 = U0; SN axes: x = east, y = up, z = north.
 /// </summary>
    public sealed class SiteMap
    {
        /// <summary>Planet centre (SE world, doubles): <c>planet.PositionComp.GetPosition</c> at run time.</summary>
        public readonly Vec3 C;
        /// <summary>Sea radius: <c>WaterModAPI.GetPhysical(planet).Item2</c> at run time (site v1: 60000.00390625).</summary>
        public readonly double Rs;
        public readonly Vec3 U0, N0, E0;
        /// <summary>Yaw of SN north relative to N0 in radians (positive = clockwise seen from above).</summary>
        public readonly double Psi;
        /// <summary>SN coordinates of the anchor point.</summary>
        public readonly Vec3 O;

        private readonly double _cosPsi, _sinPsi;

        /// <param name="c">planet centre, SE world</param>
 /// <param name="rs">sea radius</param>
 /// <param name="u0">anchor up (normalised here)</param>
 /// <param name="n0">anchor north (orthogonalised against U0 and normalised here)</param>
 /// <param name="psiDeg">yaw in degrees</param>
 /// <param name="o">SN coordinates of the anchor</param>
        public SiteMap(Vec3 c, double rs, Vec3 u0, Vec3 n0, double psiDeg, Vec3 o)
        {
            if (!(rs > 0) || double.IsInfinity(rs)) throw new ArgumentOutOfRangeException("rs", "sea radius must be positive and finite");
            C = c;
            Rs = rs;
            U0 = u0.Normalized();
            Vec3 n = n0 - U0 * Vec3.Dot(n0, U0);
            N0 = n.Normalized();
            E0 = Vec3.Cross(N0, U0);          // E = N x U, so E x N = U
            Psi = psiDeg * (Math.PI / 180.0);
            O = o;
            _cosPsi = Math.Cos(Psi);
            _sinPsi = Math.Sin(Psi);
        }

        /// <summary>worldmap_ref.EXAMPLE: the site of gap-1's numbers (Rs 63.5 km, U0 = +Z, N0 = -Y, E0 = -X).</summary>
        public static readonly SiteMap Example = new SiteMap(
            new Vec3(-1000000.0, 250000.0, 1500000.0), 63500.0, new Vec3(0, 0, 1), new Vec3(0, -1, 0), 0.0, Vec3.Zero);

        /// <summary>The site-v1 map (<see cref="SiteV1"/> constants) around a runtime planet centre and sea radius.</summary>
        public static SiteMap ForSiteV1(Vec3 planetCentre, double rs)
        {
            return new SiteMap(planetCentre, rs, SiteV1.AnchorU0, SiteV1.AnchorN0, SiteV1.AnchorPsiDeg, SiteV1.AnchorO);
        }

        /// <summary>The site-v1 map with the frozen sea radius <see cref="SiteV1.RsExpected"/>.</summary>
        public static SiteMap ForSiteV1(Vec3 planetCentre)
        {
            return ForSiteV1(planetCentre, SiteV1.RsExpected);
        }

        /// <summary>site_v1.SITE_V1: site v1 around the test centre (WM-1 vectors, in-game subsets).</summary>
        public static readonly SiteMap SiteV1Test = ForSiteV1(SiteV1.TestC);

        private static double Hypot(double a, double b) { return Math.Sqrt(a * a + b * b); }

        /// <summary>Yaw in the SN horizontal plane (x east, z north); sign +1 for SE to SN, -1 for the inverse.</summary>
        private void Yaw(double x, double z, int sign, out double xo, out double zo)
        {
            double c = _cosPsi, s = sign > 0 ? _sinPsi : -_sinPsi;
            xo = c * x + s * z;
            zo = -s * x + c * z;
        }

        /// <summary>F: SE world point to SN world point.</summary>
        public Vec3 SeToSn(Vec3 p)
        {
            Vec3 v = p - C;
            double le = Vec3.Dot(v, E0), lu = Vec3.Dot(v, U0), ln = Vec3.Dot(v, N0);
            double h = Hypot(le, ln);
            double r = Math.Sqrt(le * le + lu * lu + ln * ln);
            double theta = Math.Atan2(h, lu);
            double rho = Rs * theta;
            double x = 0.0, z = 0.0;
            if (h > 0)
            {
                x = rho * le / h;
                z = rho * ln / h;
            }
            double xy, zy;
            Yaw(x, z, +1, out xy, out zy);
            return new Vec3(xy + O.X, r - Rs + O.Y, zy + O.Z);
        }

        /// <summary>F^-1: SN world point to SE world point (closed form).</summary>
        public Vec3 SnToSe(Vec3 q)
        {
            double x, z;
            Yaw(q.X - O.X, q.Z - O.Z, -1, out x, out z);
            double y = q.Y - O.Y;
            double rho = Hypot(x, z);
            double theta = rho / Rs;
            double r = Rs + y;
            Vec3 t = rho > 0 ? E0 * (x / rho) + N0 * (z / rho) : Vec3.Zero;
            Vec3 u = U0 * Math.Cos(theta) + t * Math.Sin(theta);
            return C + u * r;
        }

        /// <summary>
 /// The carried local frame at P: the anchor frame transported along the great circle from the anchor to P
 /// (U = radial up at P). Orthonormal; E x N = U.
 /// </summary>
        public void FrameAt(Vec3 p, out Vec3 e, out Vec3 u, out Vec3 n)
        {
            Vec3 v = p - C;
            double r = v.Length;
            u = v * (1.0 / r);
            double le = Vec3.Dot(v, E0), ln = Vec3.Dot(v, N0);
            double h = Hypot(le, ln);
            if (h == 0)
            {
                e = E0;
                n = N0;
                return;
            }
            Vec3 t = E0 * (le / h) + N0 * (ln / h);
            double theta = Math.Atan2(h, Vec3.Dot(v, U0));
            double c = Math.Cos(theta), s = Math.Sin(theta);
            Vec3 k = t * (c - 1.0) + U0 * (-s);   // rotate tangent vectors in the plane (t, U0) by theta
            e = E0 + k * Vec3.Dot(E0, t);
            n = N0 + k * Vec3.Dot(N0, t);
        }

        /// <summary>mapdir at P using a frame from <see cref="FrameAt"/>: w to (w.E, w.U, w.N) after yaw.</summary>
        public Vec3 DirSeToSn(Vec3 e, Vec3 u, Vec3 n, Vec3 w)
        {
            double x, z;
            Yaw(Vec3.Dot(w, e), Vec3.Dot(w, n), +1, out x, out z);
            return new Vec3(x, Vec3.Dot(w, u), z);
        }

        /// <summary>mapdir^-1 at P using a frame from <see cref="FrameAt"/>.</summary>
        public Vec3 DirSnToSe(Vec3 e, Vec3 u, Vec3 n, Vec3 d)
        {
            double x, z;
            Yaw(d.X, d.Z, -1, out x, out z);
            return e * x + u * d.Y + n * z;
        }

        /// <summary>mapdir_P: SE world direction at P to SN world direction (orthonormal, determinant -1).</summary>
        public Vec3 DirSeToSn(Vec3 p, Vec3 w)
        {
            Vec3 e, u, n;
            FrameAt(p, out e, out u, out n);
            return DirSeToSn(e, u, n, w);
        }

        /// <summary>mapdir_P^-1: SN world direction to SE world direction at the SE point P.</summary>
        public Vec3 DirSnToSe(Vec3 p, Vec3 d)
        {
            Vec3 e, u, n;
            FrameAt(p, out e, out u, out n);
            return DirSnToSe(e, u, n, d);
        }

        /// <summary>SN metres per SE metre horizontally at P: s = Rs / |P - C|.</summary>
        public double ScaleAt(Vec3 p)
        {
            return Rs / (p - C).Length;
        }
    }
}
