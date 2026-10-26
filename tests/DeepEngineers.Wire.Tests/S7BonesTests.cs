using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7 skinned astronaut, Wire part (CONTRACT §5.4 "S7 conventions": <c>format bones.order</c>, <c>bones.unity</c>,
 /// CharacterFlagsExt.ExtraBones; ROADMAP S7.N6, S7.M4, S7.M9): the constants against S7Conventions and the layout's
 /// character flags, the row order of an 81-bone model (canon1 then the extras in model order), the C1 count rule, the
 /// conjugation (a homomorphism, so chains stay consistent) and the component-wise mirror S7.M4 must be caught by,
 /// the truncation of S7.M9 and the tearing check.
 /// </summary>
    [TestClass]
    public class S7BonesTests
    {
        private static readonly string[] Extras = { "SE_RigExtraA", "SE_RigExtraB", "SE_RigExtraC" };

        /// <summary>An 81-bone model: canon1 with three non-canonical bones interleaved (indices 5, 40, 80), parents valid.</summary>
        private static void Model81(out List<string> names, out List<int> parents)
        {
            names = new List<string>();
            parents = new List<int>();
            var canonIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            int extra = 0;
            for (int c = 0; c < MirrorSkeleton.BoneCount; c++)
            {
                if (names.Count == 5 || names.Count == 40)
                {
                    names.Add(Extras[extra++]);
                    parents.Add(canonIndex[MirrorSkeleton.BoneName(2)]);   // under the pelvis (already added at that point)
                }
                string n = MirrorSkeleton.BoneName(c);
                canonIndex[n] = names.Count;
                names.Add(n);
                string p = MirrorSkeleton.ParentName(c);
                parents.Add(p == null ? -1 : canonIndex[p]);
            }
            names.Add(Extras[extra]);
            parents.Add(canonIndex[MirrorSkeleton.BoneName(13)]);   // under the head
        }

        [TestMethod]
        public void Constants_MatchTheConventionsAndStayOutOfTheLayoutBits()
        {
            Assert.AreEqual(78, S7Bones.CanonicalRows);
            Assert.AreEqual(MirrorLimits.CanonicalBones, S7Bones.CanonicalRows);
            Assert.AreEqual(81, S7Bones.AstronautBones);
            Assert.AreEqual(S7Conventions.BoneLimits.AstronautBones, S7Bones.AstronautBones);
            Assert.AreEqual(160, S7Bones.MaxBones);
            Assert.AreEqual(SeFrameLimits.MaxBones, S7Bones.MaxBones, "C1 carries up to 160 bones (layout 1.0)");
            Assert.AreEqual(4u, S7Bones.ExtraBonesFlag);
            Assert.AreEqual(S7Conventions.CharacterFlagsExt.ExtraBones, S7Bones.ExtraBonesFlag);
            Assert.AreEqual(0u, S7Bones.ExtraBonesFlag & (MirrorCharacterFlags.Player | MirrorCharacterFlags.Fallback | 0xF0000000u),
                            "ExtraBones must not overlap Player, Fallback or the version nibble");
            uint f = MirrorCharacterFlags.Version1 | MirrorCharacterFlags.Player;
            Assert.IsFalse(S7Bones.HasExtraBones(f));
            Assert.IsTrue(S7Bones.HasExtraBones(S7Bones.WithExtraBones(f)));
            Assert.AreEqual(f, S7Bones.WithExtraBones(f) & ~S7Bones.ExtraBonesFlag);
            Assert.AreEqual(64, S7Bones.MutantTruncatedCount);
        }

        [TestMethod]
        public void RowOrder_81Bones_Canon1ThenExtrasInModelOrder()
        {
            List<string> names;
            List<int> parents;
            Model81(out names, out parents);
            Assert.AreEqual(81, names.Count);
            int[] rows;
            string why;
            Assert.IsTrue(S7Bones.TryRowOrder(names, parents, out rows, out why), why);
            Assert.AreEqual(81, rows.Length);
            for (int c = 0; c < 78; c++) Assert.AreEqual(MirrorSkeleton.BoneName(c), names[rows[c]], "row " + c);
            CollectionAssert.AreEqual(Extras, rows.Skip(78).Select(i => names[i]).ToArray(), "rows 78..80 = the extras in Model.Bones order");
            CollectionAssert.AreEqual(new[] { 5, 40, 80 }, rows.Skip(78).ToArray());
            Assert.AreEqual(81, rows.Distinct().Count(), "every model bone exactly once");
            CollectionAssert.AreEqual(rows.Skip(78).ToArray(), S7Bones.ExtraRows(names), "the name-only order agrees");
            // the pure canon1 model: no extras
            var canon = Enumerable.Range(0, 78).Select(MirrorSkeleton.BoneName).ToList();
            var cp = Enumerable.Range(0, 78).Select(MirrorSkeleton.Parent).ToList();
            Assert.IsTrue(S7Bones.TryRowOrder(canon, cp, out rows, out why), why);
            Assert.AreEqual(78, rows.Length);
            Assert.AreEqual(0, S7Bones.ExtraRows(canon).Length);
        }

        [TestMethod]
        public void RowOrder_Refuses_MissingCanonicalBone_TooManyBones_Duplicates()
        {
            List<string> names;
            List<int> parents;
            Model81(out names, out parents);
            int[] rows;
            string why;
            var missing = new List<string>(names);
            missing[missing.IndexOf("SE_RigHead")] = "SE_RigHeadRenamed";
            Assert.IsFalse(S7Bones.TryRowOrder(missing, parents, out rows, out why));
            StringAssert.Contains(why, "SE_RigHead");
            Assert.IsNull(S7Bones.ExtraRows(missing));
            var dup = new List<string>(names);
            dup[80] = dup[79];
            Assert.IsFalse(S7Bones.TryRowOrder(dup, parents, out rows, out why));
            var many = new List<string>(names);
            var mp = new List<int>(parents);
            for (int i = 0; i < 80; i++) { many.Add("SE_Pad" + i); mp.Add(0); }
            Assert.AreEqual(161, many.Count);
            Assert.IsFalse(S7Bones.TryRowOrder(many, mp, out rows, out why));
            StringAssert.Contains(why, "160");
        }

        [TestMethod]
        public void C1Count_78Or79To160_AndTheExtras()
        {
            Assert.IsFalse(S7Bones.ValidC1BoneCount(0));
            Assert.IsFalse(S7Bones.ValidC1BoneCount(64), "S7.M9's truncated rows are not a valid pose");
            Assert.IsFalse(S7Bones.ValidC1BoneCount(77));
            Assert.IsTrue(S7Bones.ValidC1BoneCount(78));
            Assert.IsTrue(S7Bones.ValidC1BoneCount(81));
            Assert.IsTrue(S7Bones.ValidC1BoneCount(160));
            Assert.IsFalse(S7Bones.ValidC1BoneCount(161));
            Assert.AreEqual(0, S7Bones.ExtraCount(78));
            Assert.AreEqual(3, S7Bones.ExtraCount(81));
            Assert.AreEqual(0, S7Bones.ExtraCount(64));
            Assert.AreEqual(81, S7Bones.SentCount(81, false));
            Assert.AreEqual(64, S7Bones.SentCount(81, true));
            Assert.AreEqual(10, S7Bones.SentCount(10, true));
        }

        [TestMethod]
        public void PickRows_CopiesModelRowsIntoRowOrder()
        {
            var model = new float[12 * 4];
            for (int i = 0; i < model.Length; i++) model[i] = i;
            var dst = new float[12 * 3];
            S7Bones.PickRows(model, new[] { 3, 0, 2 }, 3, dst);
            Assert.AreEqual(36f, dst[0]);
            Assert.AreEqual(0f, dst[12]);
            Assert.AreEqual(24f + 11f, dst[35]);
            Assert.ThrowsException<ArgumentException>(() => S7Bones.PickRows(model, new[] { 4 }, 1, dst));
        }

        // ------------------------------------------------------------------------------------------ format bones.unity

        private static float[] SeRows(Quat q, Vec3 t)
        {
            // SE (row-vector) rows of the rotation q (column form R) are the columns of R: Right = R·x, Up = R·y, Backward = R·z
            Vec3 r = q.Rotate(Vec3.UnitX), u = q.Rotate(Vec3.UnitY), b = q.Rotate(Vec3.UnitZ);
            return new[] { (float)r.X, (float)r.Y, (float)r.Z, (float)u.X, (float)u.Y, (float)u.Z, (float)b.X, (float)b.Y, (float)b.Z, (float)t.X, (float)t.Y, (float)t.Z };
        }

        /// <summary>SE row product a × b (apply a, then b).</summary>
        private static float[] SeMul(float[] a, float[] b)
        {
            var o = new float[12];
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = i == 3 ? b[9 + j] : 0.0;
                    for (int k = 0; k < 3; k++) s += a[3 * i + k] * b[3 * k + j];
                    o[3 * i + j] = (float)s;
                }
            return o;
        }

        private static Quat Axis(double ax, double ay, double az, double deg)
        {
            double h = deg * Math.PI / 360.0;
            var v = new Vec3(ax, ay, az).Normalized();
            return new Quat(v.X * Math.Sin(h), v.Y * Math.Sin(h), v.Z * Math.Sin(h), Math.Cos(h));
        }

        [TestMethod]
        public void Conjugate_IsMirrorTimesTransposeTimesMirror()
        {
            float[] m = SeRows(Axis(0.3, 1, -0.2, 47), new Vec3(0.4, 1.2, -0.7));
            Vec3 c0, c1, c2, t;
            S7Bones.Conjugate(m, 0, out c0, out c1, out c2, out t);
            // explicit: U[i][j] = Mi * M[j][i] * Mj with Mirror = diag(1, 1, -1)
            double[] mir = { 1, 1, -1 };
            Vec3[] cols = { c0, c1, c2 };
            for (int j = 0; j < 3; j++)
            {
                double[] col = { cols[j].X, cols[j].Y, cols[j].Z };
                for (int i = 0; i < 3; i++) Assert.AreEqual(mir[i] * m[3 * j + i] * mir[j], col[i], 1e-7, "U[" + i + "][" + j + "]");
            }
            Assert.AreEqual(m[9], t.X, 1e-7);
            Assert.AreEqual(m[10], t.Y, 1e-7);
            Assert.AreEqual(-m[11], t.Z, 1e-7);
            Vec3 p, s;
            Quat q;
            S7Bones.UnityLocal(m, 0, out p, out q, out s);
            Assert.AreEqual(1.0, s.X, 1e-6);
            Assert.AreEqual(1.0, s.Y, 1e-6);
            Assert.AreEqual(1.0, s.Z, 1e-6);
            Quat want = S7Bones.MirrorRotation(Axis(0.3, 1, -0.2, 47));
            Assert.IsTrue(Quat.RotationDistance(want, q) < 1e-6, "conjugated rotation = (-x, -y, z, w)");
        }

        /// <summary>
 /// A parent and a child bone (SE absolute matrices child_abs = child_rel × parent_abs): posing both flat with the
 /// conjugation gives the child's Unity position = the mirrored SE position (the 1 cm check of S7.N6 holds); the
 /// component-wise mirror (S7.M4) composed through the parent's rotation leaves it by far more than 1 cm.
 /// </summary>
        [TestMethod]
        public void ChainPositions_ConjugationHolds_ComponentWiseIsCaught()
        {
            float[] parentAbs = SeRows(Axis(0.2, 0.9, 0.4, 70), new Vec3(0.1, 1.0, 0.05));
            float[] childRel = SeRows(Axis(1, 0, 0.3, 35), new Vec3(0.0, 0.45, 0.12));
            float[] childAbs = SeMul(childRel, parentAbs);
            Vec3 seChild = new Vec3(childAbs[9], childAbs[10], childAbs[11]);

            // conjugated parent local, then the child's relative conjugated, composed in Unity
            Vec3 pp, ps, cp, cs;
            Quat pq, cq;
            S7Bones.UnityLocal(parentAbs, 0, out pp, out pq, out ps);
            S7Bones.UnityLocal(childRel, 0, out cp, out cq, out cs);
            Vec3 unityChild = pp + pq.Rotate(cp);
            Vec3 want = S7Bones.MirrorPoint(seChild);
            Assert.IsTrue((unityChild - want).Length < 1e-5, "conjugated chain: " + (unityChild - want).Length);
            // flat (format bones.unity): the absolute matrix directly
            Vec3 fp, fs;
            Quat fq;
            S7Bones.UnityLocal(childAbs, 0, out fp, out fq, out fs);
            Assert.IsTrue((fp - want).Length < 1e-6);
            Quat chain = pq * cq;
            Assert.IsTrue(Quat.RotationDistance(chain, fq) < 1e-5, "flat and chained rotations agree");

            // S7.M4: component-wise
            Vec3 mpp, mps, mcp, mcs;
            Quat mpq, mcq;
            S7Bones.ComponentWiseLocal(parentAbs, 0, out mpp, out mpq, out mps);
            S7Bones.ComponentWiseLocal(childRel, 0, out mcp, out mcq, out mcs);
            Vec3 wrong = mpp + mpq.Rotate(mcp);
            Assert.IsTrue((wrong - want).Length > 10 * S7Bones.PositionToleranceM, "S7.M4 must leave the 1 cm tolerance: " + (wrong - want).Length);
            // a root bone alone (no parent rotation in the way) keeps its position: the check needs chains, which S7.N6 has
            Assert.IsTrue((mpp - pp).Length < 1e-9);
        }

        [TestMethod]
        public void UnityLocal_ImproperMatrix_NegativeXScale()
        {
            float[] m = SeRows(Quat.Identity, new Vec3(1, 2, 3));
            m[0] = -1f;   // Right negated: determinant -1
            Vec3 p, s;
            Quat q;
            S7Bones.UnityLocal(m, 0, out p, out q, out s);
            Assert.IsTrue(s.X < 0);
            Assert.AreEqual(-3.0, p.Z, 1e-9);
        }

        [TestMethod]
        public void WorldPoint_GoesThroughTheCharacterMatrix()
        {
            var world = new SeMatrix(new Vec3(0, 0, -1), new Vec3(0, 1, 0), new Vec3(1, 0, 0), new Vec3(10, 20, 30));
            float[] bone = SeRows(Quat.Identity, new Vec3(1, 2, 3));
            Vec3 w = S7Bones.WorldPoint(world, bone, 0);
            Assert.AreEqual(10 + 3, w.X, 1e-9);
            Assert.AreEqual(22, w.Y, 1e-9);
            Assert.AreEqual(30 - 1, w.Z, 1e-9);
        }

        // ------------------------------------------------------------------------------------------ tearing

        [TestMethod]
        public void CheckWeights_SumsAndIndices()
        {
            byte[] idx = { 0, 1, 2, 3, 10, 0, 0, 0, 80, 0, 0, 0, 77, 76, 0, 0 };
            float[] w = { 0.5f, 0.25f, 0.25f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0.6f, 0.3f, 0f, 0f };
            int badSum, badIndex, maxIndex;
            Assert.IsFalse(S7Bones.CheckWeights(idx, w, 4, 78, S7Bones.WeightSumTolerance, out badSum, out badIndex, out maxIndex));
            Assert.AreEqual(1, badSum, "vertex 3 sums to 0.9");
            Assert.AreEqual(1, badIndex, "vertex 2 weights bone 80 of 78");
            Assert.AreEqual(80, maxIndex);
            Assert.IsFalse(S7Bones.CheckWeights(idx, w, 4, 81, S7Bones.WeightSumTolerance, out badSum, out badIndex, out maxIndex));
            Assert.AreEqual(0, badIndex, "with 81 rows bone 80 is in range");
            Assert.IsTrue(S7Bones.CheckWeights(idx, w, 2, 78, S7Bones.WeightSumTolerance, out badSum, out badIndex, out maxIndex));
            // a zero weight on an out-of-range index is not a tear (unused slot)
            Assert.IsTrue(S7Bones.CheckWeights(new byte[] { 0, 200, 0, 0 }, new[] { 1f, 0f, 0f, 0f }, 1, 78, 1e-4, out badSum, out badIndex, out maxIndex));
        }

        [TestMethod]
        public void BonesLine_NamesModelCountAndVerdict()
        {
            Assert.AreEqual("DE.S7 bones Models\\Characters\\Astronaut\\SE_astronaut.mwm 81 sent=81 ExtraBones boneCount==Model.Bones.Length PASS",
                            S7Bones.BonesLine("Models\\Characters\\Astronaut\\SE_astronaut.mwm", 81, 81, true, null));
            StringAssert.Contains(S7Bones.BonesLine("x", 81, 64, true, "S7.M9"), "boneCount!=Model.Bones.Length (S7.M9)");
        }
    }
}
