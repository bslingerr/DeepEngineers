using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S5 Subnautica side, Unity-free part 2 of 2 (compiled into DeepEngineers.SN.Tests): affine matrices in SE space
 // and their mirror into Unity (CONTRACT §2.5), the C2 subpart-row interpolation (§2.8: T lerp + slerp in SE space,
 // then mirrored once), the character bone math (rest locals and bind poses from SkinnedBind, SE bones from C1, the
 // generic retarget formula, the T-pose arm correction, the per-bone blend), the skinned vertex data and its
 // canonical weights, the skinned part build (worker thread) and the C1 character reader with its 4-tick history.
 // =================================================================================================================

 /// <summary>A transform as Unity sets it: localPosition, localRotation, localScale (doubles).</summary>
    internal struct Trs
    {
        public Vec3 P;
        public Quat Q;
        public Vec3 S;

        public static readonly Trs Identity = new Trs { P = Vec3.Zero, Q = Quat.Identity, S = new Vec3(1, 1, 1) };

        public Vec3 Apply(Vec3 v)
        {
            return P + Q.Rotate(new Vec3(v.X * S.X, v.Y * S.Y, v.Z * S.Z));
        }

        public bool Finite
        {
            get
            {
                return Ok(P.X) && Ok(P.Y) && Ok(P.Z) && Ok(Q.X) && Ok(Q.Y) && Ok(Q.Z) && Ok(Q.W) && Ok(S.X) && Ok(S.Y) && Ok(S.Z);
            }
        }

        private static bool Ok(double x) { return !double.IsNaN(x) && !double.IsInfinity(x); }

        /// <summary>Lerp P and S, slerp Q (shortest arc).</summary>
        public static Trs Blend(Trs a, Trs b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return new Trs
            {
                P = a.P + (b.P - a.P) * t,
                Q = PoseSampler.Slerp(a.Q, b.Q, t),
                S = a.S + (b.S - a.S) * t,
            };
        }
    }

    /// <summary>
 /// An affine map in the column-vector convention, p′ = L·p + T (doubles). An SE (VRage) matrix with rows Right, Up,
 /// Backward, Translation is L = [Right Up Backward] (as columns) and T = Translation; the SE row product
 /// <c>a × b</c> (a first, then b) is <c>b * a</c> here.
 /// </summary>
    internal struct Aff
    {
        public Mat3 L;
        public Vec3 T;

        public Aff(Mat3 l, Vec3 t) { L = l; T = t; }

        public static readonly Aff Identity = new Aff(Mat3.Identity, Vec3.Zero);

        public static Aff FromSeRows(float[] f, int off)
        {
            return new Aff(Mat3.FromColumns(new Vec3(f[off], f[off + 1], f[off + 2]), new Vec3(f[off + 3], f[off + 4], f[off + 5]), new Vec3(f[off + 6], f[off + 7], f[off + 8])),
                           new Vec3(f[off + 9], f[off + 10], f[off + 11]));
        }

        public static Aff FromSeRows(double[] f, int off)
        {
            return new Aff(Mat3.FromColumns(new Vec3(f[off], f[off + 1], f[off + 2]), new Vec3(f[off + 3], f[off + 4], f[off + 5]), new Vec3(f[off + 6], f[off + 7], f[off + 8])),
                           new Vec3(f[off + 9], f[off + 10], f[off + 11]));
        }

        public void ToSeRows(double[] dst, int off)
        {
            dst[off] = L.C0.X; dst[off + 1] = L.C0.Y; dst[off + 2] = L.C0.Z;
            dst[off + 3] = L.C1.X; dst[off + 4] = L.C1.Y; dst[off + 5] = L.C1.Z;
            dst[off + 6] = L.C2.X; dst[off + 7] = L.C2.Y; dst[off + 8] = L.C2.Z;
            dst[off + 9] = T.X; dst[off + 10] = T.Y; dst[off + 11] = T.Z;
        }

        public void ToSeRows(float[] dst, int off)
        {
            dst[off] = (float)L.C0.X; dst[off + 1] = (float)L.C0.Y; dst[off + 2] = (float)L.C0.Z;
            dst[off + 3] = (float)L.C1.X; dst[off + 4] = (float)L.C1.Y; dst[off + 5] = (float)L.C1.Z;
            dst[off + 6] = (float)L.C2.X; dst[off + 7] = (float)L.C2.Y; dst[off + 8] = (float)L.C2.Z;
            dst[off + 9] = (float)T.X; dst[off + 10] = (float)T.Y; dst[off + 11] = (float)T.Z;
        }

        public Vec3 Apply(Vec3 p) { return L.Multiply(p) + T; }

        public static Mat3 Mul(Mat3 a, Mat3 b) { return new Mat3(a.Multiply(b.C0), a.Multiply(b.C1), a.Multiply(b.C2)); }

        /// <summary>a ∘ b: b first, then a.</summary>
        public static Aff operator *(Aff a, Aff b) { return new Aff(Mul(a.L, b.L), a.L.Multiply(b.T) + a.T); }

        /// <summary>The SE row product <c>first × second</c> (VRage: apply first, then second).</summary>
        public static Aff SeProduct(Aff first, Aff second) { return second * first; }

        public Aff Inverse()
        {
            Mat3 inv = L.InverseTranspose().Transpose();
            return new Aff(inv, -inv.Multiply(T));
        }

        /// <summary>CONTRACT §2.5 mirror conjugation M·A·M, M·T with M = diag(1, 1, −1): SE-local → Unity-local.</summary>
        public Aff Mirrored()
        {
            Vec3 c0 = L.C0, c1 = L.C1, c2 = L.C2;
            return new Aff(Mat3.FromColumns(new Vec3(c0.X, c0.Y, -c0.Z), new Vec3(c1.X, c1.Y, -c1.Z), new Vec3(-c2.X, -c2.Y, c2.Z)), new Vec3(T.X, T.Y, -T.Z));
        }

        /// <summary>
 /// Position, rotation (orthogonal polar factor, w ≥ 0) and per-axis scale (column lengths; x negative for an
 /// improper matrix, which Unity draws with a negative localScale.x).
 /// </summary>
        public Trs Decompose()
        {
            double sx = L.C0.Length, sy = L.C1.Length, sz = L.C2.Length;
            double det = L.Determinant;
            if (det < 0) sx = -sx;
            if (Math.Abs(sx) < 1e-12 || sy < 1e-12 || sz < 1e-12) return new Trs { P = T, Q = Quat.Identity, S = new Vec3(sx, sy, sz) };
            var r = new Mat3(L.C0 * (1.0 / sx), L.C1 * (1.0 / sy), L.C2 * (1.0 / sz));
            // rigid rows (the usual case, float noise only): the quaternion directly; mild shear: the closest rotation
            double d01 = Math.Abs(Vec3.Dot(r.C0, r.C1)), d02 = Math.Abs(Vec3.Dot(r.C0, r.C2)), d12 = Math.Abs(Vec3.Dot(r.C1, r.C2));
            Mat3 rot = d01 < 1e-5 && d02 < 1e-5 && d12 < 1e-5 ? r : r.PolarRotation(1e-14, 30);
            return new Trs { P = T, Q = rot.ToQuat(), S = new Vec3(sx, sy, sz) };
        }

        public static Aff FromTrs(Trs t)
        {
            Vec3 cx = t.Q.Rotate(Vec3.UnitX) * t.S.X, cy = t.Q.Rotate(Vec3.UnitY) * t.S.Y, cz = t.Q.Rotate(Vec3.UnitZ) * t.S.Z;
            return new Aff(Mat3.FromColumns(cx, cy, cz), t.P);
        }
    }

    /// <summary>SE matrices → Unity transforms (CONTRACT §2.5: compose in SE space, then mirror once).</summary>
    internal static class MirrorXform
    {
        /// <summary>The Unity local transform of a child whose SE-local matrix (rows) is <paramref name="rows"/>.</summary>
        public static Trs UnityLocal(float[] rows, int off) { return Aff.FromSeRows(rows, off).Mirrored().Decompose(); }

        public static Trs UnityLocal(Aff se) { return se.Mirrored().Decompose(); }

        /// <summary>A rotation's mirror conjugate M·R·M as a quaternion: (−x, −y, z, w).</summary>
        public static Quat MirrorQ(Quat q) { return new Quat(-q.X, -q.Y, q.Z, q.W); }
    }

    /// <summary>
 /// C2 subpart rows between the camera bracket's two snapshots (CONTRACT §2.8): each row is split into T (lerp) and
 /// R (slerp) plus per-axis scale (lerp) in SE space; alpha 1 (or more) is the newer row exactly, alpha 0 the older.
 /// </summary>
    internal static class RowSampler
    {
        public static Aff Interpolate(float[] a, int ao, float[] b, int bo, double t)
        {
            if (t >= 1.0 || a == null) return Aff.FromSeRows(b, bo);
            if (t <= 0.0) return Aff.FromSeRows(a, ao);
            Aff fa = Aff.FromSeRows(a, ao), fb = Aff.FromSeRows(b, bo);
            Trs ta = Split(fa), tb = Split(fb);
            Trs r = Trs.Blend(ta, tb, t);
            return Aff.FromTrs(r);
        }

        /// <summary>SE-space split of a row (the same decomposition as Unity's, without the mirror).</summary>
        public static Trs Split(Aff a) { return a.Decompose(); }

        /// <summary>
 /// 8, §5.4 "C2 subpart table"): the snapshot at or
 /// before the newer tick and the older bracket snapshot, alpha clamped to [0, 1] (no extrapolation of rows); a row
 /// missing from the older snapshot takes the newer value; a row missing from the newer one holds the newest
 /// snapshot that still has it (sticky: SE dropped it past 2,048 rows); <paramref name="previous"/> (S5.M1) samples
 /// the snapshot before the newer tick, not interpolated. False when no snapshot in the history has the row.
 /// </summary>
        public static bool Sample(EntityHistory h, ulong owner, ulong key, CamBracket br, bool previous, out Aff se, out ulong tick, out double alpha, out PoseHow how)
        {
            se = Aff.Identity;
            tick = 0;
            alpha = 1;
            how = PoseHow.None;
            if (h == null || h.Count == 0) return false;
            EntitySnapshot b = br.Valid ? h.AtOrBefore(br.Newer) : h.Newest;
            if (b == null) return false;
            if (previous) b = h.Before(b.Tick) ?? b;
            int ib;
            if (!b.TryGetRow(owner, key, out ib))
            {
                for (int i = h.Count - 1; i >= 0; i--)
                {
                    EntitySnapshot s = h[i];
                    if (s.Tick > b.Tick || !s.TryGetRow(owner, key, out ib)) continue;
                    se = Aff.FromSeRows(s.RowLocal, ib * 12);
                    tick = s.Tick;
                    how = PoseHow.Held;
                    return true;
                }
                return false;
            }
            EntitySnapshot a = !previous && br.Valid && br.Older < br.Newer && b.Tick == br.Newer ? h.Exactly(br.Older) : null;
            int ia;
            if (a != null && a.TryGetRow(owner, key, out ia))
            {
                alpha = Math.Max(0, Math.Min(1, br.Alpha));
                se = Interpolate(a.RowLocal, ia * 12, b.RowLocal, ib * 12, alpha);
                how = PoseHow.Interpolated;
            }
            else
            {
                se = Aff.FromSeRows(b.RowLocal, ib * 12);
                how = previous ? PoseHow.Previous : (br.Valid && b.Tick == br.Newer ? PoseHow.Exact : PoseHow.Held);
            }
            tick = b.Tick;
            return true;
        }
    }

    // ---------------------------------------------------------------------------------------------------------- character math

 /// <summary>The character's bone math (CONTRACT §5.4 "Character"; canonical order canon1, parents before children).</summary>
    internal static class CharacterMath
    {
        /// <summary>
 /// Rest locals from a SkinnedBind (12 SE row floats per bone = the inverse of the bone's absolute bind matrix):
 /// abs = inverse(SkinnedBind), relative = abs × parentAbs⁻¹ (SE row order), conjugated by the mirror.
 /// <paramref name="absUnity"/> (optional) receives the mirrored absolute bind of each bone.
 /// </summary>
        public static void RestLocals(float[] bind, int[] parent, Trs[] dst, Aff[] absUnity = null)
        {
            int n = parent.Length;
            var abs = new Aff[n];
            for (int i = 0; i < n; i++)
            {
                abs[i] = Aff.FromSeRows(bind, i * 12).Inverse();
                Aff rel = parent[i] < 0 ? abs[i] : abs[parent[i]].Inverse() * abs[i];
                dst[i] = rel.Mirrored().Decompose();
                if (absUnity != null) absUnity[i] = abs[i].Mirrored();
            }
        }

        /// <summary>
 /// Unity bind poses (world→bone at bind, the renderer at the character Entity): the mirrored SkinnedBind, as 12
 /// doubles per bone in column-vector form (L columns, then T) — the Matrix4x4 is (L, T; 0 0 0 1).
 /// </summary>
        public static void Bindposes(float[] bind, int count, double[] dst)
        {
            for (int i = 0; i < count; i++) Aff.FromSeRows(bind, i * 12).Mirrored().ToSeRows(dst, i * 12);
        }

        /// <summary>
 /// SE's own pose (source SeBones): bone i local = its C1 absolute matrix relative to its parent's
 /// (abs_i × abs_parent⁻¹, SE row order), conjugated by the mirror.
 /// </summary>
        public static void SeBoneLocals(float[] absRows, int[] parent, Trs[] dst)
        {
            int n = parent.Length;
            for (int i = 0; i < n; i++)
            {
                Aff a = Aff.FromSeRows(absRows, i * 12);
                Aff rel = parent[i] < 0 ? a : Aff.FromSeRows(absRows, parent[i] * 12).Inverse() * a;
                dst[i] = rel.Mirrored().Decompose();
            }
        }

        /// <summary>Absolute transforms (relative to the tree root's parent) of a set of locals, parents first.</summary>
        public static void Absolute(Trs[] local, int[] parent, Aff[] dst)
        {
            for (int i = 0; i < parent.Length; i++)
            {
                Aff l = Aff.FromTrs(local[i]);
                dst[i] = parent[i] < 0 ? l : dst[parent[i]] * l;
            }
        }

        /// <summary>World rotations of a set of locals (rotation only), parents first.</summary>
        public static void WorldRotations(Trs[] local, int[] parent, Quat[] dst)
        {
            for (int i = 0; i < parent.Length; i++) dst[i] = parent[i] < 0 ? local[i].Q : (dst[parent[i]] * local[i].Q).Normalized();
        }

        /// <summary>
 /// Path G (generic retarget, CONTRACT §5.4): per mapped row R_se(t) = (R_sn(t) · R_sn,rest⁻¹) · R_se,rest in the
 /// character's frame, local = parent⁻¹ · world; unmapped rows keep their rest local; positions stay at rest.
 /// <paramref name="worldScratch"/> holds the world rotations as they are produced.
 /// </summary>
        public static void RetargetGeneric(Quat[] snNow, Quat[] snRest, bool[] mapped, Trs[] restLocal, Quat[] seRestWorld, int[] parent, Trs[] dst, Quat[] worldScratch)
        {
            for (int i = 0; i < parent.Length; i++)
            {
                Trs r = restLocal[i];
                Quat parentWorld = parent[i] < 0 ? Quat.Identity : worldScratch[parent[i]];
                Quat world;
                if (mapped[i])
                {
                    world = ((snNow[i] * snRest[i].Conjugate()) * seRestWorld[i]).Normalized();
                    r.Q = (parentWorld.Conjugate() * world).Normalized();
                }
                else world = (parentWorld * r.Q).Normalized();
                worldScratch[i] = world;
                dst[i] = r;
            }
        }

        /// <summary>
 /// The direction child of each row for <see cref="AlignRest"/>: a mapped descendant reached through unmapped rows,
 /// preferring the axial branch (human Spine, Chest, UpperChest, Neck, Head), else the branch with the most mapped
 /// rows; -1 when the row has no mapped descendant.
 /// </summary>
        public static int[] DirectionChildren(int[] parent, bool[] mapped, string[] human)
        {
            int n = parent.Length;
            var children = new List<int>[n];
            for (int i = 0; i < n; i++) children[i] = new List<int>();
            for (int i = 0; i < n; i++) if (parent[i] >= 0) children[parent[i]].Add(i);
            var subtree = new int[n];                       // mapped rows below i
            for (int i = n - 1; i >= 0; i--) if (parent[i] >= 0) subtree[parent[i]] += subtree[i] + (mapped[i] ? 1 : 0);
            var result = new int[n];
            var frontier = new List<int>();
            for (int i = 0; i < n; i++)
            {
                frontier.Clear();
                MappedFrontier(children, mapped, i, frontier);
                int best = -1, bestSize = -1;
                bool bestAxial = false;
                foreach (int c in frontier)
                {
                    bool axial = human != null && IsAxial(human[c]);
                    int size = subtree[c];
                    if (best < 0 || (axial && !bestAxial) || (axial == bestAxial && size > bestSize)) { best = c; bestAxial = axial; bestSize = size; }
                }
                result[i] = best;
            }
            return result;
        }

        private static bool IsAxial(string human) { return human == "Spine" || human == "Chest" || human == "UpperChest" || human == "Neck" || human == "Head"; }

        private static void MappedFrontier(List<int>[] children, bool[] mapped, int i, List<int> dst)
        {
            foreach (int c in children[i]) { if (mapped[c]) dst.Add(c); else MappedFrontier(children, mapped, c, dst); }
        }

        /// <summary>
 /// Path G rest correspondence. Both rests are bind poses; each mapped SE rest rotation is corrected by
 /// the shortest rotation taking its bind bone direction (towards its direction child) onto the source's bind
 /// bone direction, both in the common frame: R_se,rest' = FromTo(d_se, d_sn) * R_se,rest, so that
 /// (R_sn(t) * R_sn,rest^-1) * R_se,rest' points the SE bone where the source bone points. Rows without a usable
 /// direction (leaves, missing positions) inherit their parent's correction; unmapped rows are untouched.
 /// </summary>
        public static void AlignRest(Quat[] seRestWorld, Vec3[] sePos, Vec3[] snPos, bool[] snPosValid, bool[] mapped, int[] dirChild, int[] parent, Quat[] dst, Quat[] corrScratch)
        {
            for (int i = 0; i < parent.Length; i++)
            {
                Quat c = parent[i] >= 0 ? corrScratch[parent[i]] : Quat.Identity;
                int ch = dirChild[i];
                if (mapped[i] && ch >= 0 && mapped[ch] && snPosValid[i] && snPosValid[ch])
                {
                    Vec3 dSe = sePos[ch] - sePos[i], dSn = snPos[ch] - snPos[i];
                    if (dSe.Length > 1e-6 && dSn.Length > 1e-6) c = FromTo(dSe, dSn);
                }
                corrScratch[i] = c;
                dst[i] = mapped[i] ? (c * seRestWorld[i]).Normalized() : seRestWorld[i];
            }
        }

        /// <summary>The shortest rotation taking direction <paramref name="a"/> to <paramref name="b"/>.</summary>
        public static Quat FromTo(Vec3 a, Vec3 b)
        {
            Vec3 u = a.Normalized(), v = b.Normalized();
            double d = Vec3.Dot(u, v);
            if (d < -1 + 1e-12)
            {
                Vec3 axis = Vec3.Cross(Vec3.UnitX, u);
                if (axis.Length < 1e-6) axis = Vec3.Cross(Vec3.UnitY, u);
                axis = axis.Normalized();
                return new Quat(axis.X, axis.Y, axis.Z, 0);
            }
            Vec3 c = Vec3.Cross(u, v);
            return new Quat(c.X, c.Y, c.Z, 1 + d).Normalized();
        }

        /// <summary>
 /// The T-pose description of the SE rig (path H's rest-pose correction, HYPOTHESIS): SE's bind is an A-pose (upper
 /// arm about 45° down); each upper arm's local rotation is changed so that the arm (upper arm → forearm joint)
 /// points along its horizontal projection, keeping its lateral side; every other row keeps its rest local.
 /// </summary>
        public static Trs[] TPose(Trs[] restLocal, int[] parent, int upperL, int foreL, int upperR, int foreR)
        {
            var t = (Trs[])restLocal.Clone();
            RaiseArm(t, parent, upperL, foreL);
            RaiseArm(t, parent, upperR, foreR);
            return t;
        }

        private static void RaiseArm(Trs[] t, int[] parent, int upper, int fore)
        {
            if (upper < 0 || fore < 0) return;
            var abs = new Aff[parent.Length];
            Absolute(t, parent, abs);
            Vec3 d = abs[fore].T - abs[upper].T;
            var h = new Vec3(d.X, 0, d.Z);
            if (h.Length < 1e-9) return;
            Quat delta = FromTo(d, h);
            Quat absRot = abs[upper].Decompose().Q;
            Quat parentRot = parent[upper] < 0 ? Quat.Identity : abs[parent[upper]].Decompose().Q;
            Trs u = t[upper];
            u.Q = (parentRot.Conjugate() * (delta * absRot)).Normalized().WithNonNegativeW();
            t[upper] = u;
        }
    }

    /// <summary>
 /// The 0.25 s per-bone blend between animation sources (CONTRACT §5.4 "Character"): when the source changes, the
 /// last written locals are the start; the new source's locals are reached after <see cref="CharSourceRules.BlendSeconds"/>.
 /// </summary>
    internal sealed class SourceBlend
    {
        private readonly Trs[] _from, _last;
        private CharSource _source = CharSource.None;
        private double _since;
        private bool _hasLast;
        public long Switches;

        public SourceBlend(int bones)
        {
            _from = new Trs[bones];
            _last = new Trs[bones];
        }

        public CharSource Source { get { return _source; } }
        public double Weight { get { return CharSourceRules.BlendWeight(_since); } }

        /// <summary>Blends <paramref name="target"/> (the current source's locals) into <paramref name="dst"/>.</summary>
        public void Step(CharSource source, double dt, Trs[] target, Trs[] dst)
        {
            if (source != _source)
            {
                if (_hasLast) Array.Copy(_last, _from, _from.Length);
                else Array.Copy(target, _from, _from.Length);
                if (_source != CharSource.None) Switches++;
                _source = source;
                _since = 0;
            }
            else _since += dt > 0 ? dt : 0;
            double w = _hasLast ? CharSourceRules.BlendWeight(_since) : 1.0;
            for (int i = 0; i < dst.Length; i++) dst[i] = Trs.Blend(_from[i], target[i], w);
            Array.Copy(dst, _last, _last.Length);
            _hasLast = true;
        }

        public void Reset() { _source = CharSource.None; _hasLast = false; _since = 0; }
    }

    // ---------------------------------------------------------------------------------------------------------- skinned data

 /// <summary>Canonical vertex weights (CONTRACT §5.4: ≤ 4 weights per vertex, indices &lt; 78, renormalised to 1).</summary>
    internal static class SkinWeights
    {
        /// <summary>
 /// Cleans one vertex's 4 (index, weight) pairs in place: weights on an index ≥ <paramref name="bones"/>, NaN or
 /// negative are dropped (counted in <paramref name="dropped"/>); a vertex without weight goes to bone 0 with 1
 /// (counted in <paramref name="empty"/>); the rest are renormalised to sum 1.
 /// </summary>
        public static void Normalise(byte[] idx, float[] w, int v, int bones, ref int dropped, ref int empty)
        {
            int o = v * 4;
            double sum = 0;
            for (int k = 0; k < 4; k++)
            {
                float x = w[o + k];
                if (idx[o + k] >= bones || float.IsNaN(x) || float.IsInfinity(x) || x < 0)
                {
                    if (x != 0) dropped++;
                    w[o + k] = 0;
                    idx[o + k] = 0;
                    continue;
                }
                sum += x;
            }
            if (!(sum > 1e-9))
            {
                empty++;
                idx[o] = 0; w[o] = 1; w[o + 1] = w[o + 2] = w[o + 3] = 0;
                return;
            }
            for (int k = 0; k < 4; k++) w[o + k] = (float)(w[o + k] / sum);
        }
    }

    /// <summary>A character part ready for its SkinnedMeshRenderer (built on the mirror worker; uploaded on the main thread).</summary>
    internal sealed class SkinnedMeshData
    {
        public ulong ModelKey, BindKey;
        public int VertexCount, TriangleCount;
        /// <summary>Unity-local (mirrored) arrays: positions 3, normals 3, tangents 4, uv 2 per vertex.</summary>
        public float[] Pos, Nrm, Tan, Uv;
        public byte[] BoneIdx;
        public float[] Weights;
        /// <summary>Submesh groups (material key, Unity-order triangle indices).</summary>
        public readonly List<KeyValuePair<ulong, int[]>> Submeshes = new List<KeyValuePair<ulong, int[]>>();
        /// <summary>78 × 12 doubles, column-vector (L columns, T): the mirrored SkinnedBind (Unity bind poses).</summary>
        public double[] Bindposes;
        public Trs[] RestLocals;
        public bool Use32;
        public int DroppedWeights, EmptyWeights, Uncovered;
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public string Error;
    }

    internal static class SkinnedBuilder
    {
        /// <summary>
 /// The skinned part of a prepared model (mirrored, (I0, I2, I1), flipped as its winding demands) with the canonical
 /// weights and the bind's rest locals and bind poses. Worker thread.
 /// </summary>
        public static SkinnedMeshData Build(PreparedModel m, ulong bindKey, float[] bind, int[] parent)
        {
            var r = new SkinnedMeshData { ModelKey = m.Key, BindKey = bindKey, VertexCount = m.VertexCount, TriangleCount = m.TriangleCount };
            int n = parent.Length;
            if (m.BoneIdx == null || m.Weights == null) { r.Error = "model " + DeLog.Hex(m.Key) + " has no skin data (ModelBegin skinned flag clear)"; return r; }
            if (bind == null || bind.Length != n * 12) { r.Error = "SkinnedBind of " + (bind == null ? 0 : bind.Length / 12) + " bones (" + n + " expected)"; return r; }
            r.Pos = m.Pos;
            r.Nrm = m.Nrm;
            r.Tan = m.Tan;
            r.Uv = m.Uv;
            r.BoneIdx = (byte[])m.BoneIdx.Clone();
            r.Weights = (float[])m.Weights.Clone();
            for (int v = 0; v < m.VertexCount; v++) SkinWeights.Normalise(r.BoneIdx, r.Weights, v, n, ref r.DroppedWeights, ref r.EmptyWeights);
            int uncovered, overlap;
            foreach (KeyValuePair<ulong, List<int>> g in MeshBuilder.TriangleGroups(m.Submeshes, m.TriangleCount, out uncovered, out overlap))
            {
                if (g.Value.Count == 0) continue;
                var tris = new int[g.Value.Count * 3];
                int k = 0;
                foreach (int tri in g.Value) { tris[k++] = m.Tris[tri * 3]; tris[k++] = m.Tris[tri * 3 + 1]; tris[k++] = m.Tris[tri * 3 + 2]; }
                r.Submeshes.Add(new KeyValuePair<ulong, int[]>(g.Key, tris));
            }
            r.Uncovered = uncovered;
            r.Use32 = m.VertexCount > 65535;
            r.Bindposes = new double[n * 12];
            CharacterMath.Bindposes(bind, n, r.Bindposes);
            r.RestLocals = new Trs[n];
            CharacterMath.RestLocals(bind, parent, r.RestLocals);
            r.MinX = m.MinX; r.MinY = m.MinY; r.MinZ = m.MinZ; r.MaxX = m.MaxX; r.MaxY = m.MaxY; r.MaxZ = m.MaxZ;
            foreach (Trs t in r.RestLocals) if (!t.Finite) { r.Error = "SkinnedBind gives a non-finite rest pose (singular bind matrix)"; return r; }
            return r;
        }
    }

    // ---------------------------------------------------------------------------------------------------------- C1 character reader

 /// <summary>One C1 record as the character mirror uses it (fixed part and the canonical bones).</summary>
    internal sealed class CharFrame
    {
        public ulong Tick, CharacterId;
        public uint Seq, Flags, CameraMode, WorldEpoch, BoneCount, BonesVersion;
        public double Ox, Oy, Oz, Qx, Qy, Qz, Qw, S;
        public SwimWireState Swim;
        public bool SwimValid;
        /// <summary>S7a: the C1 locomotion extension (0xA0-0xAF) and its raw version byte (0 = an SE build before S7a).</summary>
        public LocoWireState Loco;
        public byte LocoVersion;
        /// <summary>boneCount = 78 (S7: or 79..160 with ExtraBones) and bonesVersion ≠ 0 (rows 0..77 in canonical order, SE model space).</summary>
        public bool BonesValid;
        public readonly float[] Bones = new float[S5Conv.CanonicalBones * 12];
        /// <summary>S7 ExtraBones (CONTRACT §5.4 "S7 conventions"): C1 rows 78..boneCount-1 (SE model space), <see cref="ExtraCount"/> of them.</summary>
        public readonly float[] Extra = new float[(SeFrameLimits.MaxBones - S5Conv.CanonicalBones) * 12];
        public int ExtraCount;
        /// <summary>The SeBones locals of <see cref="Bones"/> (computed once, on first use).</summary>
        public readonly Trs[] Locals = new Trs[S5Conv.CanonicalBones];
        public bool LocalsReady;

        public bool Has(uint f) { return (Flags & f) != 0; }
        public Vec3 O { get { return new Vec3(Ox, Oy, Oz); } }
        public Quat Q { get { return new Quat(Qx, Qy, Qz, Qw); } }

        public bool PoseFinite
        {
            get
            {
                double qn = Math.Sqrt(Qx * Qx + Qy * Qy + Qz * Qz + Qw * Qw);
                return !(double.IsNaN(Ox) || double.IsNaN(Oy) || double.IsNaN(Oz) || double.IsInfinity(Ox) || double.IsInfinity(Oy) || double.IsInfinity(Oz) ||
                         double.IsNaN(S) || !(S > 0) || S > 2 || !(qn > 0.5) || qn > 1.5 || Math.Abs(Ox) > 1e6 || Math.Abs(Oy) > 1e6 || Math.Abs(Oz) > 1e6);
            }
        }

        public void EnsureLocals(int[] parent)
        {
            if (LocalsReady || !BonesValid) return;
            CharacterMath.SeBoneLocals(Bones, parent, Locals);
            LocalsReady = true;
        }
    }

    /// <summary>
 /// The character's C1 reader (CONTRACT §5.4 C1: boneCount 78 in canon1 order, bonesVersion = the low 32 bits of the
 /// body modelKey): a seqlock copy of the fixed part and the first 78 bones whenever <c>seq</c> changed, kept in a
 /// 4-tick history like C2's so the character follows the camera's bracket. Allocation-free after construction.
 /// </summary>
    internal sealed unsafe class CharFrameReader
    {
        public const int Capacity = 4;
        // S7 (SkinnedAstronaut): the whole bone table (ExtraBones sends up to 160 rows; 7,936 B per new tick)
        public const int CopyBytes = SeFrameLimits.BonesOffset + SeFrameLimits.MaxBones * SeFrameLimits.BoneSize;

        private readonly byte* _c1;
        private readonly byte[] _buf = new byte[CopyBytes];
        private readonly CharFrame[] _s = new CharFrame[Capacity];
        private CharFrame _spare = new CharFrame();
        private uint _lastSeq;
        public int Count { get; private set; }
        public long Fresh, Busy, Resets, Invalid;

        public CharFrameReader(byte* c1)
        {
            if (c1 == null) throw new ArgumentNullException("c1");
            _c1 = c1;
        }

        public CharFrame this[int i] { get { return _s[i]; } }
        public CharFrame Newest { get { return Count > 0 ? _s[Count - 1] : null; } }

        public void Reset()
        {
            for (int i = 0; i < Count; i++) _s[i] = null;
            Count = 0;
            _lastSeq = Seqlock.Current(_c1);
            Resets++;
        }

        /// <summary>Copies the record when its seq changed; true when a new tick entered the history.</summary>
        public bool Poll()
        {
            uint cur = Seqlock.Current(_c1);
            if (cur == 0 || cur == _lastSeq) return false;
            uint seq;
            SeqlockReadStatus st;
            fixed (byte* b = _buf) st = Seqlock.TryReadBytes(_c1, b, CopyBytes, Seqlock.MaxTries, out seq);
            if (st != SeqlockReadStatus.Fresh) { if (st == SeqlockReadStatus.Busy) Busy++; return false; }
            _lastSeq = seq;
            Fresh++;
            CharFrame f = _spare ?? new CharFrame();
            _spare = null;
            fixed (byte* b = _buf)
            {
                SeFrame* h = (SeFrame*)b;
                f.Seq = seq;
                f.Tick = h->tick;
                f.Flags = h->flags;
                f.CameraMode = h->cameraMode;
                f.WorldEpoch = h->worldEpoch;
                f.CharacterId = h->characterId;
                f.Ox = h->charO.x; f.Oy = h->charO.y; f.Oz = h->charO.z;
                double qx = h->charQ.x, qy = h->charQ.y, qz = h->charQ.z, qw = h->charQ.w;
                double qn = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
                if (qn > 1e-9) { qx /= qn; qy /= qn; qz /= qn; qw /= qn; }
                f.Qx = qx; f.Qy = qy; f.Qz = qz; f.Qw = qw;
                f.S = h->charS == 0f ? 1.0 : h->charS;
                f.BoneCount = h->boneCount;
                f.BonesVersion = h->bonesVersion;
                SwimWireState sw;
                f.SwimValid = SwimWire.TryRead(ref *h, out sw);
                f.Swim = sw;
                LocoWireState lw;
                S7aLoco.TryRead(ref *h, out lw);
                f.Loco = lw;
                f.LocoVersion = S7aLoco.ReadVersion(ref *h);
                // S7: 78 (S5) or 79..160 with ExtraBones (rows 0..77 canon1, the rest extra; S7Bones.ValidC1BoneCount)
                f.BonesValid = S7Bones.ValidC1BoneCount(f.BoneCount) && f.BonesVersion != 0;
                f.LocalsReady = false;
                f.ExtraCount = 0;
                if (f.BonesValid)
                {
                    float* src = (float*)(b + SeFrameLimits.BonesOffset);
                    for (int i = 0; i < f.Bones.Length; i++)
                    {
                        float x = src[i];
                        if (float.IsNaN(x) || float.IsInfinity(x)) { f.BonesValid = false; Invalid++; break; }
                        f.Bones[i] = x;
                    }
                    int extra = S7Bones.ExtraCount(f.BoneCount);
                    float* ex = src + f.Bones.Length;
                    for (int i = 0; f.BonesValid && i < 12 * extra; i++)
                    {
                        float x = ex[i];
                        if (float.IsNaN(x) || float.IsInfinity(x)) { extra = 0; break; }
                        f.Extra[i] = x;
                    }
                    if (f.BonesValid) f.ExtraCount = extra;
                }
            }
            Push(f);
            return true;
        }

        private void Push(CharFrame f)
        {
            if (Count > 0)
            {
                CharFrame n = _s[Count - 1];
                if (f.Tick < n.Tick || f.WorldEpoch != n.WorldEpoch)
                {
                    _spare = _s[0];
                    for (int i = 0; i < Count; i++) _s[i] = null;
                    _s[0] = f;
                    Count = 1;
                    Resets++;
                    return;
                }
                if (f.Tick == n.Tick) { _spare = n; _s[Count - 1] = f; return; }
                if (Count == Capacity)
                {
                    _spare = _s[0];
                    for (int i = 1; i < Capacity; i++) _s[i - 1] = _s[i];
                    Count = Capacity - 1;
                }
            }
            _s[Count++] = f;
        }

        /// <summary>Test hook: pushes a frame built by the caller (the seqlock path is exercised by <see cref="Poll"/>).</summary>
        internal void PushForTest(CharFrame f) { Push(f); }

        public CharFrame AtOrBefore(ulong tick)
        {
            if (Count == 0) return null;
            for (int i = Count - 1; i >= 0; i--) if (_s[i].Tick <= tick) return _s[i];
            return _s[0];
        }

        public CharFrame Exactly(ulong tick)
        {
            for (int i = Count - 1; i >= 0; i--) if (_s[i].Tick == tick) return _s[i];
            return null;
        }

        public CharFrame Before(ulong tick)
        {
            for (int i = Count - 1; i >= 0; i--) if (_s[i].Tick < tick) return _s[i];
            return null;
        }
    }

    /// <summary>
 /// The character pose for one camera sample (CONTRACT §2.8 one bracket, one alpha): the entity pose (o lerp, q slerp,
 /// s lerp) and, when both bracket ticks carry valid bones of the same version, the per-bone SeBones locals blended
 /// with the same alpha; a missing older tick holds the newer one.
 /// </summary>
    internal static class CharSampler
    {
        public static bool Sample(CharFrameReader r, CamBracket br, bool previous, int[] parent, out CharFrame newer, out CharFrame older, out double alpha,
                                  out EntityPose pose, Trs[] bones, out bool bonesOk)
        {
            newer = older = null;
            alpha = 1;
            pose = default(EntityPose);
            bonesOk = false;
            if (r.Count == 0) return false;
            CharFrame b = br.Valid ? r.AtOrBefore(br.Newer) : r.Newest;
            if (b == null) return false;
            if (previous) b = r.Before(b.Tick) ?? b; // S5.M1: one snapshot behind the camera
            CharFrame a = br.Valid && !previous && br.Older < br.Newer && b.Tick == br.Newer ? r.Exactly(br.Older) : null;
            double t = br.Valid && a != null ? Math.Max(0, Math.Min(1, br.Alpha)) : 1;
            newer = b;
            older = a;
            alpha = t;
            pose = ToPose(b);
            if (a != null && a.PoseFinite && b.PoseFinite) pose = PoseSampler.Lerp(ToPose(a), pose, t);
            if (b.BonesValid)
            {
                b.EnsureLocals(parent);
                if (a != null && a.BonesValid && a.BonesVersion == b.BonesVersion && t < 1)
                {
                    a.EnsureLocals(parent);
                    for (int i = 0; i < bones.Length; i++) bones[i] = Trs.Blend(a.Locals[i], b.Locals[i], t);
                }
                else Array.Copy(b.Locals, bones, bones.Length);
                bonesOk = true;
            }
            return b.PoseFinite;
        }

        public static EntityPose ToPose(CharFrame f)
        {
            return new EntityPose { Id = f.CharacterId, Ox = f.Ox, Oy = f.Oy, Oz = f.Oz, Qx = f.Qx, Qy = f.Qy, Qz = f.Qz, Qw = f.Qw, S = f.S, K = 1.0 };
        }
    }
}
