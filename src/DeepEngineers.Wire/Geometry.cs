using System;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // Small double-precision value types for the wire math (docs/CONTRACT.md section 2). No Unity or VRage types:
 // both plugins convert at their edge (SE: VRageMath.MatrixD rows; Subnautica: UnityEngine.Vector3/Quaternion).
 // Conventions follow tools/worldmap/wire_ref.py exactly; operation order mirrors the Python where it matters.

 /// <summary>Immutable 3-vector of doubles.</summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0.0, 0.0, 0.0);
        public static readonly Vec3 UnitX = new Vec3(1.0, 0.0, 0.0);
        public static readonly Vec3 UnitY = new Vec3(0.0, 1.0, 0.0);
        public static readonly Vec3 UnitZ = new Vec3(0.0, 0.0, 1.0);

        public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static Vec3 operator -(Vec3 a) { return new Vec3(-a.X, -a.Y, -a.Z); }
        public static Vec3 operator *(Vec3 a, double k) { return new Vec3(a.X * k, a.Y * k, a.Z * k); }
        public static Vec3 operator *(double k, Vec3 a) { return new Vec3(a.X * k, a.Y * k, a.Z * k); }
        public static Vec3 operator /(Vec3 a, double k) { return new Vec3(a.X / k, a.Y / k, a.Z / k); }

        public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

        public static Vec3 Cross(Vec3 a, Vec3 b)
        {
            return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        public double LengthSquared { get { return X * X + Y * Y + Z * Z; } }
        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }

        /// <summary>v / |v| (component-wise division, as worldmap_ref.unit).</summary>
        public Vec3 Normalized()
        {
            double n = Length;
            return new Vec3(X / n, Y / n, Z / n);
        }

        public static double Distance(Vec3 a, Vec3 b) { return (a - b).Length; }

        /// <summary>Largest absolute component difference.</summary>
        public static double MaxAbsDiff(Vec3 a, Vec3 b)
        {
            return Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));
        }

        public double this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0: return X;
                    case 1: return Y;
                    case 2: return Z;
                    default: throw new ArgumentOutOfRangeException("i");
                }
            }
        }

        /// <summary>Each component rounded to IEEE float32 (what the wire carries), kept as doubles.</summary>
        public Vec3 RoundToFloat32() { return new Vec3((float)X, (float)Y, (float)Z); }

        public bool IsFinite
        {
            get { return !(double.IsNaN(X) || double.IsInfinity(X) || double.IsNaN(Y) || double.IsInfinity(Y) || double.IsNaN(Z) || double.IsInfinity(Z)); }
        }

        public bool Equals(Vec3 o) { return X.Equals(o.X) && Y.Equals(o.Y) && Z.Equals(o.Z); }
        public override bool Equals(object obj) { return obj is Vec3 && Equals((Vec3)obj); }
        public override int GetHashCode() { return X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919); }

        public override string ToString()
        {
            return "(" + X.ToString("R", CultureInfo.InvariantCulture) + ", " + Y.ToString("R", CultureInfo.InvariantCulture)
                + ", " + Z.ToString("R", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
 /// Immutable quaternion in Unity's layout and convention: (x, y, z, w), Hamilton product, <c>a * b</c> applies b
 /// first, <c>q * v</c> rotates v. Wire rotations have w &gt;= 0 (CONTRACT section 2.3).
 /// </summary>
    public struct Quat : IEquatable<Quat>
    {
        public readonly double X, Y, Z, W;

        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0.0, 0.0, 0.0, 1.0);

        public Quat Conjugate() { return new Quat(-X, -Y, -Z, W); }

        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z + W * W); } }

        public Quat Normalized()
        {
            double n = Length;
            return new Quat(X / n, Y / n, Z / n, W / n);
        }

        /// <summary>The same rotation with w &gt;= 0 (the wire convention).</summary>
        public Quat WithNonNegativeW() { return W < 0 ? new Quat(-X, -Y, -Z, -W) : this; }

        /// <summary>Unity <c>a * b</c>: apply b, then a.</summary>
        public static Quat operator *(Quat a, Quat b)
        {
            return new Quat(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        /// <summary>Unity <c>q * v</c> for a unit quaternion (wire_ref.qrot).</summary>
        public Vec3 Rotate(Vec3 v)
        {
            var u = new Vec3(X, Y, Z);
            Vec3 t = Vec3.Cross(u, v) * 2.0;
            return v + t * W + Vec3.Cross(u, t);
        }

        /// <summary>
 /// Rotation whose columns are the images of x, y, z (an orthonormal, right-handed set in Unity's numeric
 /// sense), normalised, w &gt;= 0 (wire_ref.quat_from_cols, branch for branch).
 /// </summary>
        public static Quat FromColumns(Vec3 cx, Vec3 cy, Vec3 cz)
        {
            double m00 = cx.X, m01 = cy.X, m02 = cz.X;
            double m10 = cx.Y, m11 = cy.Y, m12 = cz.Y;
            double m20 = cx.Z, m21 = cy.Z, m22 = cz.Z;
            double tr = m00 + m11 + m22;
            double x, y, z, w, s;
            if (tr > 0)
            {
                s = Math.Sqrt(tr + 1.0) * 2;
                x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s; w = 0.25 * s;
            }
            else if (m00 > m11 && m00 > m22)
            {
                s = Math.Sqrt(1.0 + m00 - m11 - m22) * 2;
                x = 0.25 * s; y = (m01 + m10) / s; z = (m02 + m20) / s; w = (m21 - m12) / s;
            }
            else if (m11 > m22)
            {
                s = Math.Sqrt(1.0 + m11 - m00 - m22) * 2;
                x = (m01 + m10) / s; y = 0.25 * s; z = (m12 + m21) / s; w = (m02 - m20) / s;
            }
            else
            {
                s = Math.Sqrt(1.0 + m22 - m00 - m11) * 2;
                x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25 * s; w = (m10 - m01) / s;
            }
            double n = Math.Sqrt(x * x + y * y + z * z + w * w);
            var q = new Quat(x / n, y / n, z / n, w / n);
            return q.WithNonNegativeW();
        }

        /// <summary>
 /// Unity <c>Quaternion.LookRotation(forward, upwards)</c> rebuilt in doubles (the Unity one is native code):
 /// local +Z to forward, local +Y to the orthogonalised up, local +X to Cross(up, forward).
 /// LookRotation(+Z, +Y) is the identity.
 /// </summary>
        public static Quat LookRotation(Vec3 forward, Vec3 up)
        {
            Vec3 z = forward.Normalized();
            Vec3 x = Vec3.Cross(up, z).Normalized();
            Vec3 y = Vec3.Cross(z, x);
            return FromColumns(x, y, z);
        }

        /// <summary>Distance between two unit quaternions as rotations: min(|a - b|, |a + b|) over the four components.</summary>
        public static double RotationDistance(Quat a, Quat b)
        {
            double d1 = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z) + (a.W - b.W) * (a.W - b.W));
            double d2 = Math.Sqrt((a.X + b.X) * (a.X + b.X) + (a.Y + b.Y) * (a.Y + b.Y) + (a.Z + b.Z) * (a.Z + b.Z) + (a.W + b.W) * (a.W + b.W));
            return Math.Min(d1, d2);
        }

        public double this[int i]
        {
            get
            {
                switch (i)
                {
                    case 0: return X;
                    case 1: return Y;
                    case 2: return Z;
                    case 3: return W;
                    default: throw new ArgumentOutOfRangeException("i");
                }
            }
        }

        public Quat RoundToFloat32() { return new Quat((float)X, (float)Y, (float)Z, (float)W); }

        public bool Equals(Quat o) { return X.Equals(o.X) && Y.Equals(o.Y) && Z.Equals(o.Z) && W.Equals(o.W); }
        public override bool Equals(object obj) { return obj is Quat && Equals((Quat)obj); }
        public override int GetHashCode() { return X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919) ^ (W.GetHashCode() * 104729); }

        public override string ToString()
        {
            return "(" + X.ToString("R", CultureInfo.InvariantCulture) + ", " + Y.ToString("R", CultureInfo.InvariantCulture)
                + ", " + Z.ToString("R", CultureInfo.InvariantCulture) + ", " + W.ToString("R", CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>Immutable 3x3 matrix of doubles kept as its three columns (column-vector convention: M*v = C0 v.x + C1 v.y + C2 v.z).</summary>
    public struct Mat3
    {
        public readonly Vec3 C0, C1, C2;

        public Mat3(Vec3 c0, Vec3 c1, Vec3 c2) { C0 = c0; C1 = c1; C2 = c2; }

        public static readonly Mat3 Identity = new Mat3(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);

        public static Mat3 FromColumns(Vec3 c0, Vec3 c1, Vec3 c2) { return new Mat3(c0, c1, c2); }

        public static Mat3 FromRows(Vec3 r0, Vec3 r1, Vec3 r2)
        {
            return new Mat3(new Vec3(r0.X, r1.X, r2.X), new Vec3(r0.Y, r1.Y, r2.Y), new Vec3(r0.Z, r1.Z, r2.Z));
        }

        public Vec3 Column(int i)
        {
            switch (i)
            {
                case 0: return C0;
                case 1: return C1;
                case 2: return C2;
                default: throw new ArgumentOutOfRangeException("i");
            }
        }

        public Vec3 Multiply(Vec3 v) { return C0 * v.X + C1 * v.Y + C2 * v.Z; }

        public Mat3 Transpose() { return FromRows(C0, C1, C2); }

        /// <summary>det = C0. (C1 x C2).</summary>
        public double Determinant { get { return Vec3.Dot(C0, Vec3.Cross(C1, C2)); } }

        /// <summary>M^-T: cofactor columns divided by det (wire_ref.inverse_transpose_cols).</summary>
        public Mat3 InverseTranspose()
        {
            double d = Determinant;
            return new Mat3(Vec3.Cross(C1, C2) * (1.0 / d), Vec3.Cross(C2, C0) * (1.0 / d), Vec3.Cross(C0, C1) * (1.0 / d));
        }

        /// <summary>
 /// Orthogonal factor R of the polar decomposition M = R P (P symmetric positive definite), for det M &gt; 0:
 /// Newton iteration X &lt;- (X + X^-T)/2 until no entry changes by more than <paramref name="tolerance"/>
 /// (wire_ref.polar_rotation; quadratic convergence, a handful of steps for near-orthogonal input).
 /// </summary>
        public Mat3 PolarRotation(double tolerance = 1e-15, int maxIterations = 60)
        {
            Mat3 x = this;
            for (int i = 0; i < maxIterations; i++)
            {
                Mat3 y = x.InverseTranspose();
                var xn = new Mat3((x.C0 + y.C0) * 0.5, (x.C1 + y.C1) * 0.5, (x.C2 + y.C2) * 0.5);
                double d = Math.Max(Vec3.MaxAbsDiff(xn.C0, x.C0), Math.Max(Vec3.MaxAbsDiff(xn.C1, x.C1), Vec3.MaxAbsDiff(xn.C2, x.C2)));
                x = xn;
                if (d <= tolerance) break;
            }
            return x;
        }

        public Quat ToQuat() { return Quat.FromColumns(C0, C1, C2); }
    }

    /// <summary>
 /// An SE (VRage) matrix as its rows: Right (M11..M13), Up (M21..M23), Backward (M31..M33), Translation (M41..M43).
 /// VRage is right-handed, Y up, Forward = -Backward = -Z; an SE-local point l maps to
 /// l.x*Right + l.y*Up + l.z*Backward + Translation (CONTRACT sections 2.3 and 6: 12 numbers, row order).
 /// </summary>
    public struct SeMatrix
    {
        public readonly Vec3 Right, Up, Backward, Translation;

        public SeMatrix(Vec3 right, Vec3 up, Vec3 backward, Vec3 translation)
        {
            Right = right; Up = up; Backward = backward; Translation = translation;
        }

        public Vec3 Forward { get { return -Backward; } }

        /// <summary>Reads 12 doubles in wire order (Right, Up, Backward, Translation) starting at <paramref name="offset"/>.</summary>
        public static SeMatrix FromRows(double[] m, int offset = 0)
        {
            if (m == null) throw new ArgumentNullException("m");
            if (offset < 0 || m.Length - offset < 12) throw new ArgumentException("need 12 values from the offset");
            return new SeMatrix(
                new Vec3(m[offset + 0], m[offset + 1], m[offset + 2]),
                new Vec3(m[offset + 3], m[offset + 4], m[offset + 5]),
                new Vec3(m[offset + 6], m[offset + 7], m[offset + 8]),
                new Vec3(m[offset + 9], m[offset + 10], m[offset + 11]));
        }

        /// <summary>Reads 12 floats in wire order (Right, Up, Backward, Translation) starting at <paramref name="offset"/>.</summary>
        public static SeMatrix FromRows(float[] m, int offset = 0)
        {
            if (m == null) throw new ArgumentNullException("m");
            if (offset < 0 || m.Length - offset < 12) throw new ArgumentException("need 12 values from the offset");
            return new SeMatrix(
                new Vec3(m[offset + 0], m[offset + 1], m[offset + 2]),
                new Vec3(m[offset + 3], m[offset + 4], m[offset + 5]),
                new Vec3(m[offset + 6], m[offset + 7], m[offset + 8]),
                new Vec3(m[offset + 9], m[offset + 10], m[offset + 11]));
        }

        /// <summary>Writes the 12 rows as floats in wire order.</summary>
        public void CopyRowsTo(float[] dst, int offset = 0)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            if (offset < 0 || dst.Length - offset < 12) throw new ArgumentException("need room for 12 values");
            dst[offset + 0] = (float)Right.X; dst[offset + 1] = (float)Right.Y; dst[offset + 2] = (float)Right.Z;
            dst[offset + 3] = (float)Up.X; dst[offset + 4] = (float)Up.Y; dst[offset + 5] = (float)Up.Z;
            dst[offset + 6] = (float)Backward.X; dst[offset + 7] = (float)Backward.Y; dst[offset + 8] = (float)Backward.Z;
            dst[offset + 9] = (float)Translation.X; dst[offset + 10] = (float)Translation.Y; dst[offset + 11] = (float)Translation.Z;
        }

        /// <summary>SE-local point to the parent space (VRage <c>Vector3D.Transform(l, M)</c>).</summary>
        public Vec3 Transform(Vec3 l)
        {
            return Right * l.X + Up * l.Y + Backward * l.Z + Translation;
        }

        /// <summary>Inverse of <see cref="Transform"/> for an orthonormal rotation part.</summary>
        public Vec3 InverseTransformPoint(Vec3 p)
        {
            Vec3 d = p - Translation;
            return new Vec3(Vec3.Dot(d, Right), Vec3.Dot(d, Up), Vec3.Dot(d, Backward));
        }

        /// <summary>det of the rows (Right, Up, Backward): +1 for a proper (right-handed) SE rotation.</summary>
        public double Determinant { get { return Vec3.Dot(Right, Vec3.Cross(Up, Backward)); } }
    }

    /// <summary>A wire pose (CONTRACT 2.3): SN-world origin o, Unity rotation q (w &gt;= 0), horizontal scale s.</summary>
    public struct WirePose
    {
        public readonly Vec3 O;
        public readonly Quat Q;
        public readonly double S;

        public WirePose(Vec3 o, Quat q, double s) { O = o; Q = q; S = s; }

        public override string ToString() { return "o=" + O + " q=" + Q + " s=" + S.ToString("R", CultureInfo.InvariantCulture); }
    }

    /// <summary>A wire camera pose: SN-world position F(P) and Unity rotation (never scaled).</summary>
    public struct CameraPose
    {
        public readonly Vec3 Position;
        public readonly Quat Rotation;

        public CameraPose(Vec3 position, Quat rotation) { Position = position; Rotation = rotation; }
    }

    /// <summary>
 /// A pre-scaled proxy BoxCollider (CONTRACT 2.6), in the section root's local Unity frame: centre, half extents
 /// (Unity BoxCollider size = 2 * HalfExtents) and the child rotation (identity for grid-aligned boxes).
 /// </summary>
    public struct ProxyBox
    {
        public readonly Vec3 Centre;
        public readonly Vec3 HalfExtents;
        public readonly Quat Rotation;
        public readonly Vec3 AxisU0, AxisU1, AxisU2;

        public ProxyBox(Vec3 centre, Vec3 halfExtents, Quat rotation, Vec3 axisU0, Vec3 axisU1, Vec3 axisU2)
        {
            Centre = centre; HalfExtents = halfExtents; Rotation = rotation; AxisU0 = axisU0; AxisU1 = axisU1; AxisU2 = axisU2;
        }

        /// <summary>
 /// Corner (sx, sy, sz in {-1, +1}, the signs of the SE box axes e0, e1, e2) of the collider in the section root's
 /// local frame. The child's local z is -Mirror(e2), so the SE sign sz becomes -sz on the child's z axis.
 /// </summary>
        public Vec3 CornerSectionLocal(int sx, int sy, int sz)
        {
            return Centre + Rotation.Rotate(new Vec3(sx * HalfExtents.X, sy * HalfExtents.Y, -sz * HalfExtents.Z));
        }
    }

    /// <summary>A Subnautica static box primitive converted to SE (CONTRACT 2.7): SE centre, scale, axes, half extents.</summary>
    public struct StaticBoxSe
    {
        /// <summary>Rows Right, Up, Backward = staticAxes(q); Translation = the SE centre F^-1(c).</summary>
        public readonly SeMatrix Matrix;
        public readonly double S;
        public readonly double Determinant;
        public readonly Vec3 HalfExtents;

        public StaticBoxSe(SeMatrix matrix, double s, double determinant, Vec3 halfExtents)
        {
            Matrix = matrix; S = s; Determinant = determinant; HalfExtents = halfExtents;
        }

        public Vec3 Centre { get { return Matrix.Translation; } }
    }
}
