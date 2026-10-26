using System;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S5 instance and pose math (CONTRACT §2.3, §2.5, §2.8, §5.4 "C1–C4 conventions for S5"): SE row matrices composed
 // in SE space (VRage row order: child × parent × …), the subgrid relative matrix of the C2 subpart table
 // (subWorld × rootWorld⁻¹ in doubles), the Dynamic-subpart threshold, the §2.8 interpolation of a C2 row or a bone
 // (translation lerp + rotation slerp in SE space, alpha 1 = exactly the newer value), and the one mirror conjugation
 // that turns an SE-local matrix into a Unity child transform or a Unity Matrix4x4 (bindposes): Unity linear =
 // D·A·D with D = diag(1, 1, −1), i.e. columns (Mirror(Right), Mirror(Up), −Mirror(Backward)) and position
 // Mirror(Translation). MeshConventions/WireMath stay the single source of the mirror itself (WireMath.Mirror) and of
 // the winding rule. No Unity or VRage types; no allocation. Python twin: tools/worldmap/wire_ref.py (se_mul,
 // se_inverse, s5_relative, s5_to_unity, s5_interpolate), compared by S5VectorTests.
 // =================================================================================================================

 /// <summary>A Unity local transform (position, rotation, lossless per-axis scale) of a mirrored SE-local matrix.</summary>
    public struct UnityTrs
    {
        public readonly Vec3 Position;
        public readonly Quat Rotation;
        public readonly Vec3 Scale;

        public UnityTrs(Vec3 position, Quat rotation, Vec3 scale) { Position = position; Rotation = rotation; Scale = scale; }

        /// <summary>Unity <c>Matrix4x4.TRS</c> in doubles (column convention).</summary>
        public Affine3 Matrix { get { return Affine3.Trs(Position, Rotation, Scale); } }

        public override string ToString() { return "p=" + Position + " r=" + Rotation + " s=" + Scale; }
    }

    /// <summary>SE row-matrix algebra, the S5 relative poses, interpolation and the Unity conjugation.</summary>
    public static class InstanceMath
    {
        /// <summary>The identity SE matrix.</summary>
        public static readonly SeMatrix Identity = new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, Vec3.Zero);

        /// <summary>CONTRACT §5.4 "Dynamic subparts": a composed-matrix change above 1e-4 m …</summary>
        public const double DynamicMetres = 1e-4;
        /// <summary>… or 1e-4 rad marks a subpart Dynamic.</summary>
        public const double DynamicRadians = 1e-4;

        // ------------------------------------------------------------------------------------------------ SE algebra

 /// <summary>The direction part of <see cref="SeMatrix.Transform"/>: <c>d.x·Right + d.y·Up + d.z·Backward</c>.</summary>
        public static Vec3 TransformDir(SeMatrix m, Vec3 d) { return m.Right * d.X + m.Up * d.Y + m.Backward * d.Z; }

        /// <summary>VRage <c>a * b</c>: apply a, then b (a child's local matrix times its parent's: the child in the parent's parent space).</summary>
        public static SeMatrix Multiply(SeMatrix a, SeMatrix b)
        {
            return new SeMatrix(TransformDir(b, a.Right), TransformDir(b, a.Up), TransformDir(b, a.Backward), b.Transform(a.Translation));
        }

        /// <summary>The general affine inverse (rows may carry scale or shear): <c>p = (p′ − T)·A⁻¹</c>. Throws when singular.</summary>
        public static SeMatrix Invert(SeMatrix m)
        {
            Vec3 r = m.Right, u = m.Up, b = m.Backward;
            double det = Vec3.Dot(r, Vec3.Cross(u, b));
            if (!(Math.Abs(det) > 1e-300) || double.IsNaN(det) || double.IsInfinity(det)) throw new ArgumentException("singular SE matrix", "m");
            // The columns of A⁻¹ (A has rows r, u, b) are (u×b, b×r, r×u)/det; its rows are their i-th components.
            Vec3 c0 = Vec3.Cross(u, b), c1 = Vec3.Cross(b, r), c2 = Vec3.Cross(r, u);
            var ir = new Vec3(c0.X / det, c1.X / det, c2.X / det);
            var iu = new Vec3(c0.Y / det, c1.Y / det, c2.Y / det);
            var ib = new Vec3(c0.Z / det, c1.Z / det, c2.Z / det);
            Vec3 t = m.Translation;
            Vec3 it = -(ir * t.X + iu * t.Y + ib * t.Z);
            return new SeMatrix(ir, iu, ib, it);
        }

        /// <summary>The inverse of a rigid matrix (orthonormal rows): transposed rotation, <c>T′ = −(T·R, T·U, T·B)</c>.</summary>
        public static SeMatrix InvertRigid(SeMatrix m)
        {
            var ir = new Vec3(m.Right.X, m.Up.X, m.Backward.X);
            var iu = new Vec3(m.Right.Y, m.Up.Y, m.Backward.Y);
            var ib = new Vec3(m.Right.Z, m.Up.Z, m.Backward.Z);
            Vec3 t = m.Translation;
            return new SeMatrix(ir, iu, ib, new Vec3(-Vec3.Dot(t, m.Right), -Vec3.Dot(t, m.Up), -Vec3.Dot(t, m.Backward)));
        }

        /// <summary>
 /// <c>child × parent⁻¹</c> for an orthonormal parent, in doubles, the translation difference taken before the
 /// projection (world matrices carry planet-scale translations): the C2 subgrid row (<c>subWorld × rootWorld⁻¹</c>),
 /// and a bone's local matrix (<c>abs_i × abs_parent⁻¹</c>) when the parent is rigid.
 /// </summary>
        public static SeMatrix Relative(SeMatrix child, SeMatrix parent)
        {
            Vec3 d = child.Translation - parent.Translation;
            return new SeMatrix(Project(child.Right, parent), Project(child.Up, parent), Project(child.Backward, parent), Project(d, parent));
        }

        /// <summary><c>child × parent⁻¹</c> for any invertible parent (bones with scale): <see cref="Multiply"/>(child, <see cref="Invert"/>(parent)).</summary>
        public static SeMatrix RelativeGeneral(SeMatrix child, SeMatrix parent) { return Multiply(child, Invert(parent)); }

        private static Vec3 Project(Vec3 v, SeMatrix p) { return new Vec3(Vec3.Dot(v, p.Right), Vec3.Dot(v, p.Up), Vec3.Dot(v, p.Backward)); }

        /// <summary>Row lengths (|Right|, |Up|, |Backward|).</summary>
        public static Vec3 RowScales(SeMatrix m) { return new Vec3(m.Right.Length, m.Up.Length, m.Backward.Length); }

        /// <summary>
 /// The rotation of an SE matrix as a quaternion in SE space (<c>q·x = Right/|Right|</c>, <c>q·y = Up/|Up|</c>,
 /// <c>q·z = Backward/|Backward|</c>; Hamilton, w ≥ 0; <see cref="Quat.FromColumns"/>). Rows must be orthogonal.
 /// </summary>
        public static Quat Rotation(SeMatrix m)
        {
            return Quat.FromColumns(m.Right / m.Right.Length, m.Up / m.Up.Length, m.Backward / m.Backward.Length);
        }

        /// <summary>The SE matrix of a rotation (SE space), per-row scale and translation (inverse of <see cref="Rotation"/>/<see cref="RowScales"/>).</summary>
        public static SeMatrix FromRotation(Quat q, Vec3 rowScale, Vec3 translation)
        {
            return new SeMatrix(q.Rotate(Vec3.UnitX) * rowScale.X, q.Rotate(Vec3.UnitY) * rowScale.Y, q.Rotate(Vec3.UnitZ) * rowScale.Z, translation);
        }

        /// <summary>The rotation angle between two orthogonal SE matrices, radians: <c>4·asin(d/2)</c> with d the quaternion distance (accurate near 0).</summary>
        public static double RotationAngle(SeMatrix a, SeMatrix b)
        {
            double d = Quat.RotationDistance(Rotation(a), Rotation(b));
            return 4.0 * Math.Asin(Math.Min(1.0, d / 2.0));
        }

        /// <summary>
 /// CONTRACT §5.4 "Dynamic subparts": true when the composed matrix moved more than <paramref name="metres"/>
 /// (translation) or turned more than <paramref name="radians"/> since <paramref name="sent"/>.
 /// </summary>
        public static bool HasMoved(SeMatrix sent, SeMatrix now, double metres = DynamicMetres, double radians = DynamicRadians)
        {
            if ((now.Translation - sent.Translation).Length > metres) return true;
            return RotationAngle(sent, now) > radians;
        }

        // ------------------------------------------------------------------------------------------------ interpolation (§2.8)

 /// <summary>
 /// CONTRACT §2.8 for a C2 row or a bone: translation lerp, rotation slerp (shortest arc) and row-scale lerp in SE
 /// space. <paramref name="alpha"/> ≥ 1 returns <paramref name="newer"/> exactly (N3: alpha 1 is SE's pose), ≤ 0
 /// returns <paramref name="older"/> exactly. Mirror once afterwards (<see cref="ToUnity"/>).
 /// </summary>
        public static SeMatrix Interpolate(SeMatrix older, SeMatrix newer, double alpha)
        {
            if (alpha >= 1.0) return newer;
            if (!(alpha > 0.0)) return older;
            Vec3 t = older.Translation + (newer.Translation - older.Translation) * alpha;
            Vec3 sa = RowScales(older), sb = RowScales(newer);
            Vec3 s = sa + (sb - sa) * alpha;
            Quat q = Slerp(Rotation(older), Rotation(newer), alpha);
            return FromRotation(q, s, t);
        }

        /// <summary>Spherical interpolation of unit quaternions along the shorter arc (normalised lerp when nearly equal).</summary>
        public static Quat Slerp(Quat a, Quat b, double t)
        {
            double d = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (d < 0) { b = new Quat(-b.X, -b.Y, -b.Z, -b.W); d = -d; }
            double wa, wb;
            if (d > 0.9995)
            {
                wa = 1.0 - t; wb = t;
            }
            else
            {
                double th = Math.Acos(d), sn = Math.Sin(th);
                wa = Math.Sin((1.0 - t) * th) / sn;
                wb = Math.Sin(t * th) / sn;
            }
            var q = new Quat(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
            return q.Normalized();
        }

        // ------------------------------------------------------------------------------------------------ Unity conjugation

 /// <summary>
 /// An SE-local matrix as a Unity column-convention affine: linear columns (Mirror(Right), Mirror(Up),
 /// −Mirror(Backward)), translation Mirror(Translation). For any child under a mirrored parent:
 /// <c>ToUnityAffine(a)·Mirror(l) = Mirror(a.Transform(l))</c>, and it is a homomorphism:
 /// <c>ToUnityAffine(Multiply(child, parent)) = ToUnityAffine(parent)·ToUnityAffine(child)</c> (Unity composes
 /// <c>parent.localToWorld × child.local</c>). Bindposes: <c>ToUnityAffine(SkinnedBind_i)</c>.
 /// </summary>
        public static Affine3 ToUnityAffine(SeMatrix m)
        {
            Vec3 b = WireMath.Mirror(m.Backward);
            return new Affine3(new Mat3(WireMath.Mirror(m.Right), WireMath.Mirror(m.Up), new Vec3(-b.X, -b.Y, -b.Z)), WireMath.Mirror(m.Translation));
        }

        /// <summary>
 /// An SE-local matrix with orthogonal rows as a Unity local transform (<see cref="ToUnityAffine"/> decomposed):
 /// position Mirror(T), rotation = the frame (Mirror(R̂), Mirror(Û), −Mirror(B̂)) (w ≥ 0), scale = the row lengths.
 /// What a Dynamic subpart, a subgrid child, a tool or a bone gets as <c>localPosition/localRotation/localScale</c>.
 /// </summary>
        public static UnityTrs ToUnity(SeMatrix m)
        {
            double sx = m.Right.Length, sy = m.Up.Length, sz = m.Backward.Length;
            Vec3 x = WireMath.Mirror(m.Right / sx), y = WireMath.Mirror(m.Up / sy), z = WireMath.Mirror(m.Backward / sz);
            Quat q = Quat.FromColumns(x, y, new Vec3(-z.X, -z.Y, -z.Z));
            return new UnityTrs(WireMath.Mirror(m.Translation), q, new Vec3(sx, sy, sz));
        }

        /// <summary>Inverse of <see cref="ToUnity"/>: the SE-local matrix of a Unity local transform.</summary>
        public static SeMatrix FromUnity(UnityTrs t)
        {
            Vec3 x = t.Rotation.Rotate(Vec3.UnitX), y = t.Rotation.Rotate(Vec3.UnitY), z = t.Rotation.Rotate(Vec3.UnitZ);
            Vec3 bz = WireMath.Mirror(z);
            return new SeMatrix(WireMath.Mirror(x) * t.Scale.X, WireMath.Mirror(y) * t.Scale.Y, new Vec3(-bz.X, -bz.Y, -bz.Z) * t.Scale.Z,
                WireMath.Mirror(t.Position));
        }

        /// <summary>
 /// <see cref="ToUnityAffine"/> as the 16 floats of a Unity <c>Matrix4x4</c> in column-major order (m00, m10, m20, m30,
 /// m01, …, m33): <c>new Matrix4x4(new Vector4(d[0..3]), new Vector4(d[4..7]), new Vector4(d[8..11]), new Vector4(d[12..15]))</c>.
 /// No allocation.
 /// </summary>
        public static void ToUnityColumnMajor(SeMatrix m, float[] dst, int offset)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            if (offset < 0 || dst.Length - offset < 16) throw new ArgumentException("need room for 16 floats");
            Affine3 a = ToUnityAffine(m);
            Put(dst, offset, a.Linear.C0, 0f);
            Put(dst, offset + 4, a.Linear.C1, 0f);
            Put(dst, offset + 8, a.Linear.C2, 0f);
            Put(dst, offset + 12, a.Translation, 1f);
        }

        private static void Put(float[] d, int o, Vec3 v, float w)
        {
            d[o] = (float)v.X; d[o + 1] = (float)v.Y; d[o + 2] = (float)v.Z; d[o + 3] = w;
        }

        // ------------------------------------------------------------------------------------------------ C2 subpart rows

 /// <summary>A C2 subpart-table row (subgrid, Dynamic subpart or held tool; CONTRACT §5.4 "C2 subpart table").</summary>
        public static SeSubpart MakeSubpartRow(ulong ownerId, ulong partKey, SeMatrix local)
        {
            return new SeSubpart { ownerId = ownerId, partKey = partKey, local = ModelCodec.Rows(local) };
        }

        /// <summary>The SE-local matrix of a C2 row (doubles from the float32 wire).</summary>
        public static SeMatrix RowLocal(SeSubpart row) { return ModelCodec.Matrix(row.local); }

        /// <summary>The C2 row of a subgrid: <see cref="Relative"/>(subgrid world, root world), composed in doubles each tick.</summary>
        public static SeSubpart SubgridRow(ulong rootId, ulong subgridId, SeMatrix subgridWorld, SeMatrix rootWorld)
        {
            return MakeSubpartRow(rootId, subgridId, Relative(subgridWorld, rootWorld));
        }
    }
}
