using System;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// The wire conventions of docs/CONTRACT.md section 2.3, as one shared C# copy loaded by both plugins
 /// (DeepEngineers.Wire.dll). Port of tools/worldmap/wire_ref.py, which is the oracle (WM-1 vectors,
 /// tests/DeepEngineers.WireMath.Tests, in-game subsets through <see cref="SelfTest"/>). No Unity or VRage types:
 /// plain doubles in <see cref="Vec3"/>, <see cref="Quat"/>, <see cref="Mat3"/> and <see cref="SeMatrix"/>.
 /// <para>SE plugin (doubles): <see cref="Pose"/>, <see cref="Camera"/>, <see cref="Vel"/>, <see cref="AngVel"/>,
 /// <see cref="PivotNearest"/>, <see cref="StaticToSe"/>; with <see cref="SiteMap"/> for F, F^-1 and mapdir.</para>
 /// <para>SN plugin: <see cref="Mirror"/>, <see cref="MirrorTri"/>, <see cref="Apply"/>, <see cref="ToLocal"/>,
 /// <see cref="K"/>, <see cref="PrescaleBox"/>, <see cref="PartLocalRotation"/>.</para>
 /// </summary>
    public static class WireMath
    {
        // ------------------------------------------------------------ entity-local data (Subnautica side)

 /// <summary>The only handedness change on entity-local data: SE-local (x, y, z) to Unity-local (x, y, -z); its own inverse.</summary>
        public static Vec3 Mirror(Vec3 v)
        {
            return new Vec3(v.X, v.Y, -v.Z);
        }

        /// <summary><c>MyModel.Triangles</c> (I0, I1, I2) to the Unity triangle (I0, I2, I1).</summary>
        public static void MirrorTri(int i0, int i1, int i2, out int u0, out int u1, out int u2)
        {
            u0 = i0;
            u1 = i2;
            u2 = i1;
        }

        /// <summary>Applies <see cref="MirrorTri"/> to a whole index list (length a multiple of 3), writing into <paramref name="dst"/> (may be the same array).</summary>
        public static void MirrorTris(int[] src, int[] dst)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (dst == null) throw new ArgumentNullException("dst");
            if (src.Length % 3 != 0 || dst.Length < src.Length) throw new ArgumentException("index lists must hold whole triangles");
            for (int i = 0; i < src.Length; i += 3)
            {
                int a = src[i], b = src[i + 1], c = src[i + 2];
                dst[i] = a;
                dst[i + 1] = c;
                dst[i + 2] = b;
            }
        }

        /// <summary>S*v with S = diag(s, 1, s): the horizontal Jacobian scale (SN metres per SE metre).</summary>
        public static Vec3 ScaleH(Vec3 v, double s)
        {
            return new Vec3(v.X * s, v.Y, v.Z * s);
        }

        /// <summary>
 /// SE-local point to SN world: <c>o + S*(q*Mirror(l))</c>, what the Frame (position o, scale (s, 1, s)) /
 /// Entity (rotation q) hierarchy realises in Unity.
 /// </summary>
        public static Vec3 Apply(Vec3 o, Quat q, double s, Vec3 l)
        {
            return o + ScaleH(q.Rotate(Mirror(l)), s);
        }

        public static Vec3 Apply(WirePose p, Vec3 l)
        {
            return Apply(p.O, p.Q, p.S, l);
        }

        /// <summary>SE-local direction (or offset) to SN world: <c>S*(q*Mirror(d))</c> (not normalised).</summary>
        public static Vec3 ApplyDir(Quat q, double s, Vec3 d)
        {
            return ScaleH(q.Rotate(Mirror(d)), s);
        }

        /// <summary>SN world point to SE-local point, the inverse of <see cref="Apply"/>: <c>Mirror(q^-1*S^-1*(w - o))</c>.</summary>
        public static Vec3 ToLocal(Vec3 o, Quat q, double s, Vec3 w)
        {
            Vec3 d = w - o;
            return Mirror(q.Conjugate().Rotate(new Vec3(d.X / s, d.Y, d.Z / s)));
        }

        public static Vec3 ToLocal(WirePose p, Vec3 w)
        {
            return ToLocal(p.O, p.Q, p.S, w);
        }

        /// <summary>
 /// <c>K(q, s)*v = q^-1*S*(q*v)</c> in entity-local Unity coordinates: scales the part of v that is horizontal in
 /// SN by s (= s*v - (s - 1)*u*(u.v) with u = q^-1*y).
 /// </summary>
        public static Vec3 K(Quat q, double s, Vec3 v)
        {
            return q.Conjugate().Rotate(ScaleH(q.Rotate(v), s));
        }

        /// <summary>
 /// Proxy collider pre-scaling (CONTRACT 2.6) for one SE-local box (centre b, half extents h along the SE-local
 /// unit axes e0, e1, e2, a right-handed set) of a section whose unit-scale root is posed at
 /// <c>Apply(o, q, s, cSec)</c> with rotation q: centre = K*Mirror(b - cSec); half extent_i = h_i*|K*Mirror(e_i)|;
 /// child rotation = the polar (orthonormalised) frame of the columns (K*Mirror(e0), K*Mirror(e1), -K*Mirror(e2)).
 /// The third column is negated because Mirror makes the box frame left-handed and a box is symmetric under an
 /// axis flip. K is symmetric positive definite, so this rotation equals the rigid frame (Mirror(e0), Mirror(e1),
 /// -Mirror(e2)) to rounding; grid-aligned boxes get the identity (the box sits on the section root).
 /// </summary>
        public static ProxyBox PrescaleBox(Quat q, double s, Vec3 cSec, Vec3 b, Vec3 h, Vec3 e0, Vec3 e1, Vec3 e2)
        {
            Vec3 centre = K(q, s, Mirror(b - cSec));
            Vec3 a0 = Mirror(e0), a1 = Mirror(e1), a2 = Mirror(e2);
            Vec3 k0 = K(q, s, a0), k1 = K(q, s, a1), k2 = K(q, s, a2);
            var half = new Vec3(h.X * k0.Length, h.Y * k1.Length, h.Z * k2.Length);
            Mat3 r = new Mat3(k0, k1, -k2).PolarRotation();
            return new ProxyBox(centre, half, r.ToQuat(), a0, a1, a2);
        }

        /// <summary>
 /// A cube part, subpart or dummy matrix (SE-local rows) as a Unity child transform (CONTRACT 2.5):
 /// localPosition = Mirror(T).
 /// </summary>
        public static Vec3 PartLocalPosition(SeMatrix part)
        {
            return Mirror(part.Translation);
        }

        /// <summary>
 /// A cube part, subpart or dummy matrix (SE-local rows) as a Unity child transform (CONTRACT 2.5):
 /// localRotation = LookRotation(Mirror(Forward), Mirror(Up)). Entity-local only: never apply this to a wire
 /// pose q, which is already a Unity rotation (that would be the "double mirror" mutant).
 /// </summary>
        public static Quat PartLocalRotation(SeMatrix part)
        {
            return Quat.LookRotation(Mirror(part.Forward), Mirror(part.Up));
        }

        // ------------------------------------------------------------ world-level data (SE side, doubles)

 /// <summary>
 /// Pivot policy (CONTRACT 2.4): the point of the entity (group) box [bmin, bmax] (SE-local of m) nearest to the
 /// SE camera; the camera itself when it is inside.
 /// </summary>
        public static Vec3 PivotNearest(SeMatrix m, Vec3 bmin, Vec3 bmax, Vec3 camera)
        {
            Vec3 l = m.InverseTransformPoint(camera);
            var c = new Vec3(Clamp(l.X, bmin.X, bmax.X), Clamp(l.Y, bmin.Y, bmax.Y), Clamp(l.Z, bmin.Z, bmax.Z));
            return m.Transform(c);
        }

        /// <summary>
 /// Pivot policy for a group (CONTRACT 2.4): the point of the members' oriented boxes nearest to the camera
 /// (member i: SE world matrix <paramref name="matrices"/>[i], SE-local box [bmins[i], bmaxs[i]]); the camera
 /// itself when it is inside one. Ties keep the lowest index. No allocation.
 /// </summary>
        public static Vec3 PivotNearest(SeMatrix[] matrices, Vec3[] bmins, Vec3[] bmaxs, int count, Vec3 camera)
        {
            if (matrices == null) throw new ArgumentNullException("matrices");
            if (bmins == null) throw new ArgumentNullException("bmins");
            if (bmaxs == null) throw new ArgumentNullException("bmaxs");
            if (count < 1 || count > matrices.Length || count > bmins.Length || count > bmaxs.Length)
                throw new ArgumentOutOfRangeException("count");
            Vec3 best = PivotNearest(matrices[0], bmins[0], bmaxs[0], camera);
            double bestD = (best - camera).LengthSquared;
            for (int i = 1; i < count; i++)
            {
                Vec3 p = PivotNearest(matrices[i], bmins[i], bmaxs[i], camera);
                double d = (p - camera).LengthSquared;
                if (d < bestD) { best = p; bestD = d; }
            }
            return best;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            return Math.Min(Math.Max(v, lo), hi);
        }

        /// <summary>SE vertical FOV (radians) to Unity <c>Camera.fieldOfView</c> (vertical, degrees): x 180/pi (CONTRACT 2.1).</summary>
        public static double VerticalFovDegrees(double seVerticalFovRadians)
        {
            return seVerticalFovRadians * (180.0 / Math.PI);
        }

        /// <summary>
 /// The wire pose of an SE world matrix m (orthonormal rows; Forward and Up are used) linearised at the pivot
 /// P*: <c>s = Rs/|P* - C|</c>; <c>o = F(P*) + J(P*)*(m.T - P*)</c> with <c>J*w = S*mapdir_P*(w)</c>;
 /// <c>q = LookRotation(mapdir_P*(Forward), mapdir_P*(Up))</c>, w &gt;= 0.
 /// </summary>
        public static WirePose Pose(SiteMap site, SeMatrix m, Vec3 pivot)
        {
            if (site == null) throw new ArgumentNullException("site");
            double s = site.Rs / (pivot - site.C).Length;
            Vec3 e, u, n;
            site.FrameAt(pivot, out e, out u, out n);
            Vec3 o = site.SeToSn(pivot) + ScaleH(site.DirSeToSn(e, u, n, m.Translation - pivot), s);
            Quat q = Quat.LookRotation(site.DirSeToSn(e, u, n, m.Forward), site.DirSeToSn(e, u, n, m.Up));
            return new WirePose(o, q, s);
        }

        /// <summary>The camera is mapped exactly and never scaled: position F(T), rotation from mapdir at T.</summary>
        public static CameraPose Camera(SiteMap site, SeMatrix m)
        {
            if (site == null) throw new ArgumentNullException("site");
            Vec3 p = m.Translation;
            Vec3 e, u, n;
            site.FrameAt(p, out e, out u, out n);
            return new CameraPose(site.SeToSn(p), Quat.LookRotation(site.DirSeToSn(e, u, n, m.Forward), site.DirSeToSn(e, u, n, m.Up)));
        }

        /// <summary>Linear velocity to SN: <c>S*mapdir_P*(v)</c>.</summary>
        public static Vec3 Vel(SiteMap site, Vec3 pivot, Vec3 v)
        {
            if (site == null) throw new ArgumentNullException("site");
            double s = site.Rs / (pivot - site.C).Length;
            return ScaleH(site.DirSeToSn(pivot, v), s);
        }

        /// <summary>
 /// Angular velocity to SN: <c>-mapdir_P*(omega)</c> (a pseudovector under an improper map; Unity's numeric
 /// AngleAxis sense).
 /// </summary>
        public static Vec3 AngVel(SiteMap site, Vec3 pivot, Vec3 omega)
        {
            if (site == null) throw new ArgumentNullException("site");
            return -site.DirSeToSn(pivot, omega);
        }

        /// <summary>
 /// staticAxes(q) (CONTRACT 2.3/2.7): SE axes of a Subnautica static primitive at its converted SE centre,
 /// rows Right = mapdir^-1(q*x), Up = mapdir^-1(q*y), Backward = mapdir^-1(q*(-z)); Translation = the centre.
 /// The mirror is the negated z: the rows have determinant +1. q is normalised first (float32 wire values).
 /// </summary>
        public static SeMatrix StaticAxes(SiteMap site, Vec3 centreSe, Quat q)
        {
            if (site == null) throw new ArgumentNullException("site");
            return StaticAxesUnit(site, centreSe, q.Normalized());
        }

        private static SeMatrix StaticAxesUnit(SiteMap site, Vec3 centreSe, Quat qn)
        {
            Vec3 e, u, n;
            site.FrameAt(centreSe, out e, out u, out n);
            Vec3 right = site.DirSnToSe(e, u, n, qn.Rotate(Vec3.UnitX));
            Vec3 up = site.DirSnToSe(e, u, n, qn.Rotate(Vec3.UnitY));
            Vec3 backward = site.DirSnToSe(e, u, n, qn.Rotate(new Vec3(0.0, 0.0, -1.0)));
            return new SeMatrix(right, up, backward, centreSe);
        }

        /// <summary>
 /// A Subnautica static box primitive (SN-world centre, Unity rotation, half extents) to SE (CONTRACT 2.7):
 /// centre F^-1(c), axes <see cref="StaticAxes"/>, half extent_i = h_i*|S^-1*(q*e_i)| (horizontal extents divided
 /// by s at the centre; shear dropped), s = Rs/|centre - C|.
 /// </summary>
        public static StaticBoxSe StaticToSe(SiteMap site, Vec3 centreSn, Quat qSn, Vec3 halfSn)
        {
            if (site == null) throw new ArgumentNullException("site");
            Quat qn = qSn.Normalized();
            Vec3 p = site.SnToSe(centreSn);
            double s = site.Rs / (p - site.C).Length;
            SeMatrix axes = StaticAxesUnit(site, p, qn);
            double inv = 1.0 / s;
            var half = new Vec3(
                halfSn.X * ScaleH(qn.Rotate(Vec3.UnitX), inv).Length,
                halfSn.Y * ScaleH(qn.Rotate(Vec3.UnitY), inv).Length,
                halfSn.Z * ScaleH(qn.Rotate(Vec3.UnitZ), inv).Length);
            return new StaticBoxSe(axes, s, axes.Determinant, half);
        }
    }
}
