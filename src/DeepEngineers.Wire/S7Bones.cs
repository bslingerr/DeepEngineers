using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7 "skinned astronaut" (CONTRACT §5.4 "S7 conventions", "Character bones (S7; S7Bones)"; ROADMAP S7.N6): the bone
 // records with ExtraBones, game-independent and pure. With CharacterAdd flags bit 2 (ExtraBones) every bone record of
 // one character (CharacterAdd.boneCount, C3 SkinnedBind.boneCount, C1 boneCount) carries Model.Bones.Length rows
 // (81 for both astronauts, <= 160): rows 0..77 are canon1 (the S5 meaning: vertex indices, retarget, hierarchy), rows
 // 78.. the model's non-canonical bones in Model.Bones order (format bones.order); no vertex weights them, so the skin
 // is the S5 skin. Subnautica poses rows 78.. flat under the character's Entity from their absolute matrices, through
 // the CONTRACT §2.5 conjugation Unity = Mirror * M^T * Mirror (format bones.unity), rows 0..77 as S5 does. The
 // component-wise mirror (S7.M4) is kept here too, as the seeded failure the S7.N6 position check must catch.
 // Python twin: tests/s7/analyze_s7.py --selftest (row order, conjugation, weight check).
 // =================================================================================================================

 /// <summary>The S7 bone rows (CONTRACT §5.4 "S7 conventions", <c>format bones.order</c> and <c>bones.unity</c>).</summary>
    public static class S7Bones
    {
        /// <summary>Rows 0..77: the canonical skeleton canon1 (S5).</summary>
        public const int CanonicalRows = MirrorSkeleton.BoneCount;
        /// <summary>Model.Bones.Length of both SE astronauts (VERIFIED SE_astronaut.mwm, male and female).</summary>
        public const int AstronautBones = S7Conventions.BoneLimits.AstronautBones;
        /// <summary>The C1 bone table's capacity (<c>SeFrameLimits.MaxBones</c>).</summary>
        public const int MaxBones = S7Conventions.BoneLimits.MaxBones;
        /// <summary>CharacterAdd <c>flags</c> bit 2.</summary>
        public const uint ExtraBonesFlag = S7Conventions.CharacterFlagsExt.ExtraBones;
        /// <summary>S7.M9 (seeded failure): the bone rows truncated to this many.</summary>
        public const int MutantTruncatedCount = 64;
        /// <summary>S7.N6: the largest |SE bone (mapped) - Subnautica bone| in metres.</summary>
        public const double PositionToleranceM = 0.01;
        /// <summary>S7.N6 tearing: every vertex's weights sum to 1 within this.</summary>
        public const double WeightSumTolerance = 1e-4;

        /// <summary>True when CharacterAdd <paramref name="characterFlags"/> carries ExtraBones.</summary>
        public static bool HasExtraBones(uint characterFlags) { return (characterFlags & ExtraBonesFlag) != 0; }

        /// <summary>The flags with ExtraBones set (the other bits untouched).</summary>
        public static uint WithExtraBones(uint characterFlags) { return characterFlags | ExtraBonesFlag; }

        /// <summary>
 /// A C1 <c>boneCount</c> Subnautica takes: 78 (S5's export, the canonical rows only) or 79..160 (ExtraBones: rows
 /// 0..77 canonical, the rest extra). Anything else is not a valid character pose (0 = no bones yet).
 /// </summary>
        public static bool ValidC1BoneCount(uint boneCount)
        {
            return boneCount >= CanonicalRows && boneCount <= MaxBones;
        }

        /// <summary>The extra rows of a valid count (0 for S5's 78).</summary>
        public static int ExtraCount(uint boneCount)
        {
            return ValidC1BoneCount(boneCount) ? (int)boneCount - CanonicalRows : 0;
        }

        /// <summary>
 /// The row order of <c>format bones.order</c> for a model's bones (<c>MyModelBone.Name</c>, <c>Parent</c>):
 /// <paramref name="rowToModel"/>[r] = the model bone index of row r, rows 0..77 canon1 (by name, with canon1's
 /// parents: Wire <see cref="SkeletonMap"/>), rows 78.. the non-canonical bones in model order. False (with the
 /// reason S5's export is kept for) when the skeleton does not map to canon1 or has more than 160 bones.
 /// </summary>
        public static bool TryRowOrder(IList<string> names, IList<int> parents, out int[] rowToModel, out string why)
        {
            rowToModel = null;
            why = null;
            if (names == null || parents == null) { why = "no bones"; return false; }
            if (names.Count > MaxBones) { why = "the model has " + names.Count + " bones (more than " + MaxBones + ")"; return false; }
            SkeletonMap map;
            if (!SkeletonMap.TryBuild(names, parents, out map, out why)) return false;
            rowToModel = RowOrder(map);
            return true;
        }

        /// <summary>The row order of a built canon1 map (rows 0..77 canonical, then <see cref="ExtraRows(SkeletonMap)"/>).</summary>
        public static int[] RowOrder(SkeletonMap map)
        {
            if (map == null) throw new ArgumentNullException("map");
            int[] extra = ExtraRows(map);
            var rows = new int[CanonicalRows + extra.Length];
            Array.Copy(map.CanonicalToModel, rows, CanonicalRows);
            Array.Copy(extra, 0, rows, CanonicalRows, extra.Length);
            return rows;
        }

        /// <summary>The model indices of the non-canonical bones in <c>Model.Bones</c> order (rows 78.. of <c>format bones.order</c>).</summary>
        public static int[] ExtraRows(SkeletonMap map)
        {
            if (map == null) throw new ArgumentNullException("map");
            var list = new List<int>(map.NonCanonicalBones);
            int[] m2c = map.ModelToCanonical;
            for (int i = 0; i < m2c.Length; i++) if (m2c[i] < 0) list.Add(i);
            return list.ToArray();
        }

        /// <summary>The same order from bone names alone (canonical names first in canon1 order, the others in model order); null when a canonical name is missing.</summary>
        public static int[] ExtraRows(IList<string> names)
        {
            if (names == null) return null;
            var canon = new bool[names.Count];
            for (int c = 0; c < CanonicalRows; c++)
            {
                int hit = -1;
                for (int i = 0; i < names.Count; i++) if (string.Equals(names[i], MirrorSkeleton.BoneName(c), StringComparison.Ordinal)) { hit = i; break; }
                if (hit < 0) return null;
                canon[hit] = true;
            }
            var list = new List<int>();
            for (int i = 0; i < names.Count; i++) if (!canon[i]) list.Add(i);
            return list.ToArray();
        }

        /// <summary>The row count actually sent: <paramref name="modelBones"/>, or 64 under S7.M9 (bones truncated).</summary>
        public static int SentCount(int modelBones, bool mutantTruncate)
        {
            if (modelBones < 0) return 0;
            return mutantTruncate ? Math.Min(modelBones, MutantTruncatedCount) : modelBones;
        }

        /// <summary>Copies rows (12 floats each, SE rows Right, Up, Backward, Translation) from model order into row order.</summary>
        public static void PickRows(float[] modelRows, int[] rowToModel, int count, float[] dst)
        {
            if (modelRows == null || rowToModel == null || dst == null) throw new ArgumentNullException(modelRows == null ? "modelRows" : rowToModel == null ? "rowToModel" : "dst");
            if (count > rowToModel.Length || dst.Length < 12 * count) throw new ArgumentException("count " + count + " exceeds the row order or the destination");
            for (int r = 0; r < count; r++)
            {
                int m = rowToModel[r];
                if (m < 0 || 12 * m + 12 > modelRows.Length) throw new ArgumentException("row " + r + " maps to model bone " + m + " outside the input");
                Array.Copy(modelRows, 12 * m, dst, 12 * r, 12);
            }
        }

        // ------------------------------------------------------------------------------------------ format bones.unity

 /// <summary>
 /// The CONTRACT §2.5 conjugation of one SE row matrix (12 floats: Right, Up, Backward, Translation; row-vector
 /// convention): the column-form Unity matrix <c>U = Mirror · Mᵀ · Mirror</c>, Mirror = diag(1, 1, −1), returned as
 /// its three columns and its translation. A product of SE matrices maps to the product of the conjugates
 /// (<c>U(A × B) = U(B) · U(A)</c> in column form), which is why bone chains stay consistent.
 /// </summary>
        public static void Conjugate(float[] rows, int off, out Vec3 c0, out Vec3 c1, out Vec3 c2, out Vec3 t)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            // Mᵀ has the SE rows as columns; Mirror on the left negates every column's z, on the right negates column 2
            c0 = new Vec3(rows[off], rows[off + 1], -rows[off + 2]);
            c1 = new Vec3(rows[off + 3], rows[off + 4], -rows[off + 5]);
            c2 = new Vec3(-rows[off + 6], -rows[off + 7], rows[off + 8]);
            t = new Vec3(rows[off + 9], rows[off + 10], -rows[off + 11]);
        }

        /// <summary>
 /// A bone posed flat (<c>format bones.unity</c>): Unity localPosition, localRotation (w ≥ 0) and localScale under
 /// the character's Entity from the bone's absolute SE matrix. A matrix with a negative determinant reports a
 /// negative x scale (as the S5 decomposition does).
 /// </summary>
        public static void UnityLocal(float[] rows, int off, out Vec3 position, out Quat rotation, out Vec3 scale)
        {
            Vec3 c0, c1, c2;
            Conjugate(rows, off, out c0, out c1, out c2, out position);
            Decompose(c0, c1, c2, out rotation, out scale);
        }

        /// <summary>
 /// S7.M4's wrong mirror ("component-wise instead of conjugated"): the SE matrix split into translation and rotation
 /// in SE space, then each component flipped on its own: position (x, y, −z), quaternion (x, y, −z, w). The right
 /// conjugate is (−x, −y, z, w), so the component-wise one is its inverse: a bone chain posed with it bends the
 /// wrong way and the children's positions leave the S7.N6 tolerance.
 /// </summary>
        public static void ComponentWiseLocal(float[] rows, int off, out Vec3 position, out Quat rotation, out Vec3 scale)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            var r = new Vec3(rows[off], rows[off + 1], rows[off + 2]);
            var u = new Vec3(rows[off + 3], rows[off + 4], rows[off + 5]);
            var b = new Vec3(rows[off + 6], rows[off + 7], rows[off + 8]);
            Quat q;
            Decompose(r, u, b, out q, out scale);
            rotation = new Quat(q.X, q.Y, -q.Z, q.W).WithNonNegativeW();
            position = new Vec3(rows[off + 9], rows[off + 10], -rows[off + 11]);
        }

        /// <summary>The mirror of a point (CONTRACT §2.5: z negated).</summary>
        public static Vec3 MirrorPoint(Vec3 p) { return new Vec3(p.X, p.Y, -p.Z); }

        /// <summary>The conjugate of a rotation as a quaternion: Mirror·R·Mirror = (−x, −y, z, w).</summary>
        public static Quat MirrorRotation(Quat q) { return new Quat(-q.X, -q.Y, q.Z, q.W).WithNonNegativeW(); }

        private static void Decompose(Vec3 c0, Vec3 c1, Vec3 c2, out Quat rotation, out Vec3 scale)
        {
            double sx = c0.Length, sy = c1.Length, sz = c2.Length;
            double det = Vec3.Dot(c0, Vec3.Cross(c1, c2));
            if (det < 0) sx = -sx;
            if (Math.Abs(sx) < 1e-12 || sy < 1e-12 || sz < 1e-12)
            {
                rotation = Quat.Identity;
                scale = new Vec3(sx, sy, sz);
                return;
            }
            var m = new Mat3(c0 * (1.0 / sx), c1 * (1.0 / sy), c2 * (1.0 / sz));
            double d01 = Math.Abs(Vec3.Dot(m.C0, m.C1)), d02 = Math.Abs(Vec3.Dot(m.C0, m.C2)), d12 = Math.Abs(Vec3.Dot(m.C1, m.C2));
            Mat3 rot = d01 < 1e-5 && d02 < 1e-5 && d12 < 1e-5 ? m : m.PolarRotation(1e-14, 30);
            rotation = rot.ToQuat();
            scale = new Vec3(sx, sy, sz);
        }

        /// <summary>The SE-world point of a bone: the bone's absolute translation through the character's world matrix (SE rows).</summary>
        public static Vec3 WorldPoint(SeMatrix characterWorld, float[] boneRows, int off)
        {
            if (boneRows == null) throw new ArgumentNullException("boneRows");
            return characterWorld.Transform(new Vec3(boneRows[off + 9], boneRows[off + 10], boneRows[off + 11]));
        }

        // ------------------------------------------------------------------------------------------ S7.N6 tearing

 /// <summary>
 /// The skin check of S7.N6 ("no tearing"): over <paramref name="vertexCount"/> vertices of 4 influences each, the
 /// vertices whose weights do not sum to 1 within <paramref name="tolerance"/> (<paramref name="badSum"/>) and
 /// those with a non-zero weight on an index at or beyond <paramref name="boneCount"/> (<paramref name="badIndex"/>);
 /// <paramref name="maxIndex"/> is the largest weighted index. True when both counts are 0.
 /// </summary>
        public static bool CheckWeights(byte[] indices, float[] weights, int vertexCount, int boneCount, double tolerance,
                                        out int badSum, out int badIndex, out int maxIndex)
        {
            badSum = badIndex = 0;
            maxIndex = -1;
            if (indices == null || weights == null) throw new ArgumentNullException(indices == null ? "indices" : "weights");
            if (indices.Length < 4 * vertexCount || weights.Length < 4 * vertexCount) throw new ArgumentException("fewer than 4 influences per vertex");
            for (int v = 0; v < vertexCount; v++)
            {
                double sum = 0;
                bool outOfRange = false;
                for (int k = 0; k < 4; k++)
                {
                    float w = weights[4 * v + k];
                    if (float.IsNaN(w) || float.IsInfinity(w)) { sum = double.NaN; continue; }
                    sum += w;
                    if (w != 0f)
                    {
                        int i = indices[4 * v + k];
                        if (i > maxIndex) maxIndex = i;
                        if (i >= boneCount) outOfRange = true;
                    }
                }
                if (double.IsNaN(sum) || Math.Abs(sum - 1.0) > tolerance) badSum++;
                if (outOfRange) badIndex++;
            }
            return badSum == 0 && badIndex == 0;
        }

        // ------------------------------------------------------------------------------------------ logs

 /// <summary>The <c>DE.S7 bones</c> line of one exported model (CONTRACT: asserted for every character model of the session).</summary>
        public static string BonesLine(string model, int modelBones, int sent, bool extraBones, string why)
        {
            var sb = new StringBuilder(160);
            sb.Append("DE.S7 bones ").Append(string.IsNullOrEmpty(model) ? "?" : model).Append(' ').Append(modelBones.ToString(CultureInfo.InvariantCulture));
            sb.Append(" sent=").Append(sent.ToString(CultureInfo.InvariantCulture));
            sb.Append(extraBones ? " ExtraBones" : " canon1-only");
            sb.Append(sent == modelBones ? " boneCount==Model.Bones.Length PASS" : " boneCount!=Model.Bones.Length");
            if (!string.IsNullOrEmpty(why)) sb.Append(" (").Append(why).Append(')');
            return sb.ToString();
        }
    }
}
