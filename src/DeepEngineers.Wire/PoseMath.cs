using System;
using System.Globalization;

// Entity poses for ROADMAP S3 (docs/CONTRACT.md §2.3, §2.5; ARCHITECTURE §5.4, §5.8): the wire pose (o, q, s) plus the
// uniform entity scale k, its Frame/Entity split in Unity, the renderer matrix S·R(q)·k (sheared when the hull tilts), the
// rigid-draw error a scaled Frame removes (S3.M7), the gap-1 fallback (per-chunk rigid frames with K baked into the
// vertices) and the bound of the affine pose against the exact map F. WireMath.Pose / Apply are the WM-1-tested core;
// this file adds k and the Unity hierarchy around them. Python twin: tools/worldmap/wire_ref.py (sn_apply_k,
// affine_vs_f_bound, rigid_draw_error), used by tests/s3/check_placement.py.
namespace DeepEngineers.Wire
{
    /// <summary>
 /// A C2 entity pose: SN-world origin o, Unity rotation q (w ≥ 0), horizontal map scale s, uniform entity scale k
 /// (SE <c>PositionComp.Scale</c>, 1 for grids). An SE-local point l is drawn at <c>o + S·(q·(k·Mirror(l)))</c>.
 /// </summary>
    public struct WireEntityPose
    {
        public readonly Vec3 O;
        public readonly Quat Q;
        public readonly double S;
        public readonly double K;

        public WireEntityPose(Vec3 o, Quat q, double s, double k) { O = o; Q = q; S = s; K = k; }

        public WireEntityPose(WirePose p, double k) { O = p.O; Q = p.Q; S = p.S; K = k; }

        public WirePose Wire { get { return new WirePose(O, Q, S); } }

        /// <summary>The pose as the float32 wire carries it (each field rounded to float32).</summary>
        public WireEntityPose RoundToFloat32() { return new WireEntityPose(O.RoundToFloat32(), Q.RoundToFloat32(), (float)S, (float)K); }

        public override string ToString()
        {
            return "o=" + O + " q=" + Q + " s=" + S.ToString("R", CultureInfo.InvariantCulture) + " k=" + K.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
 /// The Unity hierarchy of a mirrored entity (CONTRACT §2.5): Frame (root) position o, rotation identity, scale (s, 1, s);
 /// Entity (child) position 0, rotation q, scale (k, k, k). Chunks, subparts and lights are children of the Entity.
 /// </summary>
    public struct FrameEntitySplit
    {
        public readonly Vec3 FramePosition;
        public readonly Vec3 FrameScale;
        public readonly Quat EntityRotation;
        public readonly Vec3 EntityScale;

        public FrameEntitySplit(Vec3 framePosition, Vec3 frameScale, Quat entityRotation, Vec3 entityScale)
        {
            FramePosition = framePosition; FrameScale = frameScale; EntityRotation = entityRotation; EntityScale = entityScale;
        }

        /// <summary>Frame rotation is always the identity.</summary>
        public Quat FrameRotation { get { return Quat.Identity; } }
        /// <summary>Entity local position is always 0.</summary>
        public Vec3 EntityPosition { get { return Vec3.Zero; } }
    }

    /// <summary>An affine map x → Linear·x + Translation (column convention, doubles): a Unity <c>localToWorldMatrix</c> without the last row.</summary>
    public struct Affine3
    {
        public readonly Mat3 Linear;
        public readonly Vec3 Translation;

        public Affine3(Mat3 linear, Vec3 translation) { Linear = linear; Translation = translation; }

        public static readonly Affine3 Identity = new Affine3(Mat3.Identity, Vec3.Zero);

        public Vec3 TransformPoint(Vec3 p) { return Linear.Multiply(p) + Translation; }

        public Vec3 TransformVector(Vec3 v) { return Linear.Multiply(v); }

        /// <summary>Unity <c>Matrix4x4.TRS(position, rotation, scale)</c> in doubles: T·R·S.</summary>
        public static Affine3 Trs(Vec3 position, Quat rotation, Vec3 scale)
        {
            Vec3 cx = rotation.Rotate(Vec3.UnitX) * scale.X;
            Vec3 cy = rotation.Rotate(Vec3.UnitY) * scale.Y;
            Vec3 cz = rotation.Rotate(Vec3.UnitZ) * scale.Z;
            return new Affine3(new Mat3(cx, cy, cz), position);
        }

        /// <summary><c>parent * child</c> (apply the child first), as Unity composes <c>localToWorldMatrix</c>.</summary>
        public static Affine3 operator *(Affine3 parent, Affine3 child)
        {
            Mat3 l = new Mat3(parent.Linear.Multiply(child.Linear.C0), parent.Linear.Multiply(child.Linear.C1), parent.Linear.Multiply(child.Linear.C2));
            return new Affine3(l, parent.TransformPoint(child.Translation));
        }
    }

    /// <summary>Pose helpers around <see cref="WireMath.Pose"/> and <see cref="WireMath.Apply(Vec3, Quat, double, Vec3)"/> (ROADMAP S3).</summary>
    public static class PoseMath
    {
        // ------------------------------------------------------------------------------------------------ SE side

 /// <summary>
 /// The C2 pose of an SE world matrix (orthonormal rows) linearised at the pivot P* (CONTRACT §2.3, §2.4), with the
 /// entity's uniform scale k (SE <c>PositionComp.Scale</c>; 1 for grids).
 /// </summary>
        public static WireEntityPose FromSe(SiteMap site, SeMatrix world, Vec3 pivot, double k = 1.0)
        {
            if (!(k > 0) || double.IsInfinity(k)) throw new ArgumentOutOfRangeException("k", "k must be positive and finite");
            return new WireEntityPose(WireMath.Pose(site, world, pivot), k);
        }

        /// <summary>
 /// For a world matrix whose rows carry a uniform scale (|Right| = |Up| = |Backward| = k): k is the mean row length,
 /// the rows are normalised, then <see cref="FromSe"/>. Rows of unequal length (non-uniform scale) are refused:
 /// the wire has no non-uniform entity scale.
 /// </summary>
        public static WireEntityPose FromSeScaled(SiteMap site, SeMatrix worldWithScale, Vec3 pivot, double relTolerance = 1e-5)
        {
            double a = worldWithScale.Right.Length, b = worldWithScale.Up.Length, c = worldWithScale.Backward.Length;
            double k = (a + b + c) / 3.0;
            if (!(k > 0) || Math.Abs(a - k) > relTolerance * k || Math.Abs(b - k) > relTolerance * k || Math.Abs(c - k) > relTolerance * k)
                throw new ArgumentException("the matrix rows are not uniformly scaled (" + a + ", " + b + ", " + c + ")");
            var unit = new SeMatrix(worldWithScale.Right / a, worldWithScale.Up / b, worldWithScale.Backward / c, worldWithScale.Translation);
            return FromSe(site, unit, pivot, k);
        }

        /// <summary>Writes o, q, s, k into a C2 entity record (float32; q with w ≥ 0 as built).</summary>
        public static void WriteWire(ref SeEntity e, WireEntityPose p)
        {
            Quat q = p.Q.WithNonNegativeW();
            e.o = new F32x3((float)p.O.X, (float)p.O.Y, (float)p.O.Z);
            e.q = new F32x4((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
            e.s = (float)p.S;
            e.k = (float)p.K;
        }

        /// <summary>Reads o, q, s, k from a C2 entity record (q normalised: float32 wire values).</summary>
        public static WireEntityPose ReadWire(SeEntity e)
        {
            var q = new Quat(e.q.x, e.q.y, e.q.z, e.q.w);
            double n = q.Length;
            if (n > 0) q = new Quat(q.X / n, q.Y / n, q.Z / n, q.W / n);
            return new WireEntityPose(new Vec3(e.o.x, e.o.y, e.o.z), q, e.s, e.k);
        }

        // ------------------------------------------------------------------------------------------------ SN side

 /// <summary>SE-local point (entity space) to SN world: <c>o + S·(q·(k·Mirror(l)))</c>.</summary>
        public static Vec3 Apply(WireEntityPose p, Vec3 l)
        {
            return WireMath.Apply(p.O, p.Q, p.S, l * p.K);
        }

        /// <summary>SE-local offset to SN world: <c>S·(q·(k·Mirror(d)))</c>.</summary>
        public static Vec3 ApplyDir(WireEntityPose p, Vec3 d)
        {
            return WireMath.ApplyDir(p.Q, p.S, d * p.K);
        }

        /// <summary>Inverse of <see cref="Apply"/>: <c>Mirror(q⁻¹·S⁻¹·(w − o)) / k</c>.</summary>
        public static Vec3 ToLocal(WireEntityPose p, Vec3 w)
        {
            return WireMath.ToLocal(p.O, p.Q, p.S, w) / p.K;
        }

        /// <summary>The Frame/Entity split of a pose (CONTRACT §2.5): set exactly these four Unity values every frame.</summary>
        public static FrameEntitySplit Split(WireEntityPose p)
        {
            return new FrameEntitySplit(p.O, new Vec3(p.S, 1.0, p.S), p.Q, new Vec3(p.K, p.K, p.K));
        }

        /// <summary>The Entity's <c>localToWorldMatrix</c> under its Frame: T(o)·diag(s, 1, s)·R(q)·k (sheared when q tilts).</summary>
        public static Affine3 EntityMatrix(WireEntityPose p)
        {
            FrameEntitySplit f = Split(p);
            return Affine3.Trs(f.FramePosition, Quat.Identity, f.FrameScale) * Affine3.Trs(Vec3.Zero, f.EntityRotation, f.EntityScale);
        }

        /// <summary>
 /// The <c>localToWorldMatrix</c> of a unit-scale child of the Entity (a chunk at <c>Mirror(chunkOriginSe)</c>, a subpart):
 /// <see cref="EntityMatrix"/>·T(childPositionUnity)·R(childRotation).
 /// </summary>
        public static Affine3 ChildMatrix(WireEntityPose p, Vec3 childPositionUnity, Quat childRotation)
        {
            return EntityMatrix(p) * Affine3.Trs(childPositionUnity, childRotation, new Vec3(1, 1, 1));
        }

        /// <summary>
 /// The rigid draw (no scaled Frame: T(o)·R(q)·k; the S3.M7 mutant). It differs from <see cref="Apply"/> by
 /// <see cref="RigidDrawError"/>.
 /// </summary>
        public static Affine3 RigidMatrix(WireEntityPose p)
        {
            return Affine3.Trs(p.O, p.Q, new Vec3(p.K, p.K, p.K));
        }

        /// <summary>
 /// How far the rigid draw puts an SE-local point l from where it belongs: <c>|(S − I)·(q·(k·Mirror(l)))|</c> =
 /// <c>(s − 1)·</c>|horizontal part of q·k·Mirror(l)|. What S3.G1-E2 must measure under the S3.M7 mutant.
 /// </summary>
        public static double RigidDrawError(Quat q, double s, double k, Vec3 l)
        {
            Vec3 r = q.Rotate(WireMath.Mirror(l * k));
            return Math.Abs(s - 1.0) * Math.Sqrt(r.X * r.X + r.Z * r.Z);
        }

        /// <summary>Largest <see cref="RigidDrawError"/> over the 8 corners of an SE-local box.</summary>
        public static double MaxRigidDrawError(WireEntityPose p, Vec3 min, Vec3 max)
        {
            double worst = 0;
            for (int i = 0; i < 8; i++) worst = Math.Max(worst, RigidDrawError(p.Q, p.S, p.K, MeshConventions.AabbCorner(min, max, i)));
            return worst;
        }

        /// <summary>
 /// The shear of the renderer matrix S·R(q): the largest |cos| between two of its columns (0 when q keeps the axes
 /// level or vertical, about (s − 1)·sin 2α/2 for a tilt α). For logs and the S3.G1-E2 coverage.
 /// </summary>
        public static double Shear(Quat q, double s)
        {
            Vec3 a = WireMath.ScaleH(q.Rotate(Vec3.UnitX), s), b = WireMath.ScaleH(q.Rotate(Vec3.UnitY), s), c = WireMath.ScaleH(q.Rotate(Vec3.UnitZ), s);
            return Math.Max(Math.Abs(Cos(a, b)), Math.Max(Math.Abs(Cos(a, c)), Math.Abs(Cos(b, c))));
        }

        private static double Cos(Vec3 a, Vec3 b) { return Vec3.Dot(a, b) / (a.Length * b.Length); }

        // ------------------------------------------------------------------------------------------------ gap-1 fallback (K baked)

 /// <summary>
 /// Fallback of ARCHITECTURE §5.4 (if S3.G1-E2 fails): a unit-scale chunk root at <see cref="ChunkRoot"/> with rotation
 /// q, and its vertices baked as <c>K(q, s)·(k·Mirror(l − c))</c>, exact for the (q, s) they were baked with:
 /// <c>root + q·vertex = Apply(p, l)</c>. Re-bake when q or s changes.
 /// </summary>
        public static Vec3 KBakedVertex(WireEntityPose p, Vec3 chunkCentreSe, Vec3 lSe)
        {
            return WireMath.K(p.Q, p.S, WireMath.Mirror((lSe - chunkCentreSe) * p.K));
        }

        /// <summary>The SN-world position of a K-baked chunk root: <c>Apply(p, c)</c>.</summary>
        public static Vec3 ChunkRoot(WireEntityPose p, Vec3 chunkCentreSe) { return Apply(p, chunkCentreSe); }

        // ------------------------------------------------------------------------------------------------ affine vs F

 /// <summary>The angle θ of an SE point from the site anchor U0 (θ = atan2(√(le² + ln²), lu)).</summary>
        public static double AngleFromAnchor(SiteMap site, Vec3 pointSe)
        {
            if (site == null) throw new ArgumentNullException("site");
            Vec3 v = pointSe - site.C;
            double le = Vec3.Dot(v, site.E0), lu = Vec3.Dot(v, site.U0), ln = Vec3.Dot(v, site.N0);
            return Math.Atan2(Math.Sqrt(le * le + ln * ln), lu);
        }

        /// <summary>
 /// The bound of the affine pose against the exact map F for a point at distance d from the pivot (ROADMAP S3.C2;
 /// wire_ref.py <c>affine_vs_f_bound</c>): <c>max(s, 1)·d²/(√3·(r − d)) + (θ/sin θ − 1)·s·d</c> with r = |P* − C|,
 /// s = Rs/r and θ the anchor angle (the larger of the pivot's and the point's). The first term is the curvature of
 /// the sphere (its horizontal part is in SN metres, hence max(s, 1); without it the ratio reaches 1.014 with the
 /// pivot away from the entity origin), the second the transverse anisotropy of the azimuthal map.
 /// </summary>
        public static double AffineVsFBound(double d, double r, double s, double theta)
        {
            if (d < 0 || !(r > d)) throw new ArgumentOutOfRangeException("d", "0 <= d < r");
            double aniso = theta > 1e-8 ? theta / Math.Sin(theta) - 1.0 : theta * theta / 6.0;
            return Math.Max(s, 1.0) * d * d / (Math.Sqrt(3.0) * (r - d)) + aniso * s * d;
        }

        /// <summary><see cref="AffineVsFBound(double, double, double, double)"/> for an SE point and the pivot of its pose.</summary>
        public static double AffineVsFBound(SiteMap site, Vec3 pivot, Vec3 pointSe)
        {
            if (site == null) throw new ArgumentNullException("site");
            double r = (pivot - site.C).Length;
            double theta = Math.Max(AngleFromAnchor(site, pivot), AngleFromAnchor(site, pointSe));
            return AffineVsFBound((pointSe - pivot).Length, r, site.Rs / r, theta);
        }
    }
}
