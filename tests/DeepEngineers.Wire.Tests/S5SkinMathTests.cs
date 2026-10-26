using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S5 skinned-mesh codec support (CONTRACT §5.4 "Character"): the bone map by name and its fallback reasons, the weight
 /// remap rules, binds and C1 bone locals, the Unity skinning route against the SE reference, and the split by material.
 /// </summary>
    [TestClass]
    public class S5SkinMathTests
    {
        /// <summary>A model skeleton: canon1 in a shuffled order plus non-canonical bones (a chain under the left forearm, a mesh node under the root).</summary>
        internal static void ModelSkeleton(Random rnd, out List<string> names, out List<int> parents)
        {
            var n = new List<string>();
            var pn = new List<string>();
            for (int i = 0; i < MirrorSkeleton.BoneCount; i++) { n.Add(MirrorSkeleton.BoneName(i)); pn.Add(MirrorSkeleton.ParentName(i)); }
            n.Add("DE_Extra_A"); pn.Add("SE_RigLForearm1");
            n.Add("DE_Extra_B"); pn.Add("DE_Extra_A");
            n.Add("DE_MeshNode"); pn.Add("RootNode");
            int[] order = Enumerable.Range(0, n.Count).OrderBy(_ => rnd.Next()).ToArray();
            names = order.Select(i => n[i]).ToList();
            var pos = new Dictionary<string, int>();
            for (int i = 0; i < names.Count; i++) pos[names[i]] = i;
            parents = order.Select(i => pn[i] == null ? -1 : pos[pn[i]]).ToList();
        }

        private static SkeletonMap Map(List<string> names, List<int> parents)
        {
            SkeletonMap map;
            string error;
            Assert.IsTrue(SkeletonMap.TryBuild(names, parents, out map, out error), error);
            return map;
        }

        [TestMethod]
        public void BoneMap_ByNameWithAncestors()
        {
            var rnd = new Random(11);
            List<string> names;
            List<int> parents;
            ModelSkeleton(rnd, out names, out parents);
            SkeletonMap map = Map(names, parents);
            Assert.AreEqual(81, map.ModelBoneCount);
            Assert.AreEqual(3, map.NonCanonicalBones);
            for (int c = 0; c < MirrorSkeleton.BoneCount; c++)
            {
                Assert.AreEqual(MirrorSkeleton.BoneName(c), names[map.CanonicalToModel[c]]);
                Assert.AreEqual(c, map.ModelToCanonical[map.CanonicalToModel[c]]);
                Assert.AreEqual(c, map.ModelToCanonicalAncestor[map.CanonicalToModel[c]]);
            }
            int forearm = MirrorSkeleton.IndexOf("SE_RigLForearm1");
            Assert.AreEqual(forearm, map.ModelToCanonicalAncestor[names.IndexOf("DE_Extra_A")]);
            Assert.AreEqual(forearm, map.ModelToCanonicalAncestor[names.IndexOf("DE_Extra_B")], "nearest canonical ancestor through a chain");
            Assert.AreEqual(0, map.ModelToCanonicalAncestor[names.IndexOf("DE_MeshNode")]);
            Assert.AreEqual(-1, map.ModelToCanonical[names.IndexOf("DE_MeshNode")]);
            StringAssert.Contains(SkinMath.Describe(map), "nonCanonical=3");
        }

        [TestMethod]
        public void BoneMap_FallbackReasons()
        {
            var rnd = new Random(12);
            List<string> names;
            List<int> parents;
            ModelSkeleton(rnd, out names, out parents);
            SkeletonMap map;
            string error;

            var missing = new List<string>(names);
            missing[names.IndexOf("SE_RigHead")] = "SE_RigHeadX";
            Assert.IsFalse(SkeletonMap.TryBuild(missing, parents, out map, out error));
            StringAssert.Contains(error, "canonical bone missing: SE_RigHead");

            var wrongParent = new List<int>(parents);
            wrongParent[names.IndexOf("SE_RigRPalm")] = names.IndexOf("SE_RigLPalm");
            Assert.IsFalse(SkeletonMap.TryBuild(names, wrongParent, out map, out error));
            StringAssert.Contains(error, "SE_RigRPalm has parent SE_RigLPalm");

            var dup = new List<string>(names);
            dup[names.IndexOf("DE_MeshNode")] = "HeadDummy";
            Assert.IsFalse(SkeletonMap.TryBuild(dup, parents, out map, out error));
            StringAssert.Contains(error, "repeated");

            var bad = new List<int>(parents);
            bad[0] = 500;
            Assert.IsFalse(SkeletonMap.TryBuild(names, bad, out map, out error));

            var cyc = new List<int>(parents);
            int a = names.IndexOf("DE_Extra_A"), b = names.IndexOf("DE_Extra_B"), m = names.IndexOf("DE_MeshNode");
            cyc[a] = b; cyc[b] = m; cyc[m] = a;
            Assert.IsFalse(SkeletonMap.TryBuild(names, cyc, out map, out error));
            StringAssert.Contains(error, "cyclic");

            Assert.IsFalse(SkeletonMap.TryBuild(names, parents.Take(10).ToList(), out map, out error));
        }

        [TestMethod]
        public void Weights_RemapRules()
        {
            var rnd = new Random(13);
            List<string> names;
            List<int> parents;
            ModelSkeleton(rnd, out names, out parents);
            SkeletonMap map = Map(names, parents);
            var ob = new byte[4];
            var ow = new float[4];
            Func<string, byte> mi = n => (byte)names.IndexOf(n);
            int forearm = MirrorSkeleton.IndexOf("SE_RigLForearm1");

            // Duplicates (through non-canonical bones) are summed; the result is sorted by weight.
            Assert.IsTrue(SkinMath.RemapInfluences(map, new[] { mi("DE_Extra_B"), mi("SE_RigHead"), mi("DE_Extra_A"), mi("SE_RigLForearm1") },
                new[] { 0.2f, 0.3f, 0.2f, 0.3f }, 0, 4, ob, ow, 0));
            CollectionAssert.AreEqual(new byte[] { (byte)forearm, (byte)MirrorSkeleton.Head, 0, 0 }, ob);
            Assert.AreEqual((float)(0.7 / (0.2f + 0.3f + 0.2f + 0.3f) * 1.0), ow[0], 1e-6f);
            Assert.AreEqual(1.0, ow.Sum(), 1e-6);
            Assert.AreEqual(0f, ow[2]);

            // Zero, negative and NaN weights are dropped; renormalised.
            Assert.IsTrue(SkinMath.RemapInfluences(map, new[] { mi("SE_RigHead"), mi("SE_RigNeck"), mi("HeadDummy"), mi("SE_RigPelvis") },
                new[] { 0f, -1f, float.NaN, 0.5f }, 0, 4, ob, ow, 0));
            Assert.AreEqual((byte)MirrorSkeleton.Pelvis, ob[0]);
            Assert.AreEqual(1f, ow[0]);

            // No positive weight: the root, weight 1.
            Assert.IsTrue(SkinMath.RemapInfluences(map, new byte[] { 1, 2, 3, 4 }, new float[4], 0, 4, ob, ow, 0));
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, ob);
            CollectionAssert.AreEqual(new[] { 1f, 0f, 0f, 0f }, ow);

            // 6 influences: the 4 largest kept, ties to the lower canonical index.
            var src = new[] { mi("SE_RigHead"), mi("SE_RigNeck"), mi("SE_RigPelvis"), mi("SE_RigLThigh"), mi("SE_RigRThigh"), mi("SE_RigLCalf") };
            Assert.IsTrue(SkinMath.RemapInfluences(map, src, new[] { 0.1f, 0.3f, 0.2f, 0.2f, 0.05f, 0.15f }, 0, 6, ob, ow, 0));
            CollectionAssert.AreEqual(new byte[] { 12, 2, 3, 4 }, ob, "neck 0.3, pelvis 0.2 (index 2 before thigh 3), thigh 0.2, calf 0.15");
            Assert.AreEqual(1.0, ow.Sum(), 1e-6);
            Assert.AreEqual(0.3f / 0.85f, ow[0], 1e-6f);

            // A bone outside the model: refused, nothing written.
            ob[0] = 99;
            Assert.IsFalse(SkinMath.RemapInfluences(map, new byte[] { 200, 0, 0, 0 }, new[] { 1f, 0f, 0f, 0f }, 0, 4, ob, ow, 0));
            Assert.AreEqual(99, ob[0]);
        }

        internal static void Binds(Random rnd, int n, out SeMatrix[] local, out SeMatrix[] posed)
        {
            local = new SeMatrix[n];
            posed = new SeMatrix[n];
            for (int i = 0; i < n; i++) { local[i] = S5InstanceMathTests.RandomRigid(rnd, 0.3); posed[i] = S5InstanceMathTests.RandomRigid(rnd, 1.5); }
        }

        [TestMethod]
        public void Binds_SkinningAndBoneLocals()
        {
            var rnd = new Random(14);
            List<string> names;
            List<int> parents;
            ModelSkeleton(rnd, out names, out parents);
            SkeletonMap map = Map(names, parents);
            SeMatrix[] local, posed;
            Binds(rnd, names.Count, out local, out posed);
            var abs = new SeMatrix[names.Count];
            SkinMath.AbsoluteFromLocal(local, parents, abs);
            for (int i = 0; i < names.Count; i++)
            {
                SeMatrix expect = parents[i] < 0 ? local[i] : InstanceMath.Multiply(local[i], abs[parents[i]]);
                Assert.IsTrue(S5InstanceMathTests.MaxDiff(abs[i], expect) < 1e-12);
            }
            SkinnedBindDefinition sb = SkinMath.MakeSkinnedBind(77, map, abs);
            Assert.AreEqual(78, sb.BoneCount);
            Assert.AreEqual(78 * 12, sb.Rows.Length);
            byte[] encoded = ModelCodec.EncodeSkinnedBind(sb);
            Assert.AreEqual(16 + 78 * 48, encoded.Length);
            var bind = new SeMatrix[78];
            for (int c = 0; c < 78; c++)
            {
                bind[c] = SeMatrix.FromRows(sb.Rows, 12 * c);
                Assert.IsTrue(S5InstanceMathTests.MaxDiff(InstanceMath.Multiply(bind[c], abs[map.CanonicalToModel[c]]), InstanceMath.Identity) < 1e-5,
                    "bind x abs = identity (float32 rows)");
            }

            // C1 bones in canonical order, and their parent-relative locals.
            var modelRows = new float[12 * names.Count];
            for (int i = 0; i < names.Count; i++) posed[i].CopyRowsTo(modelRows, 12 * i);
            var c1 = new float[2 + 78 * 12];
            SkinMath.PickCanonical(map, modelRows, c1, 2);
            var c1m = new SeMatrix[78];
            for (int c = 0; c < 78; c++)
            {
                c1m[c] = SeMatrix.FromRows(c1, 2 + 12 * c);
                Assert.IsTrue(S5InstanceMathTests.MaxDiff(c1m[c], posed[map.CanonicalToModel[c]]) < 1e-6);
            }
            var locals = new SeMatrix[78];
            SkinMath.BoneLocals(c1, 2, locals);
            var locals2 = new SeMatrix[78];
            SkinMath.BoneLocals(c1m, locals2);
            for (int c = 0; c < 78; c++)
            {
                int p = MirrorSkeleton.Parent(c);
                SeMatrix back = p < 0 ? locals[c] : InstanceMath.Multiply(locals[c], c1m[p]);
                Assert.IsTrue(S5InstanceMathTests.MaxDiff(back, c1m[c]) < 1e-9, "local x parentAbs = abs");
                Assert.IsTrue(S5InstanceMathTests.MaxDiff(locals[c], locals2[c]) < 1e-12);
            }

            // Unity route: bones posed from conjugated locals down the tree, bindposes = conjugated binds.
            var world = new Affine3[78];
            for (int c = 0; c < 78; c++)
            {
                int p = MirrorSkeleton.Parent(c);
                Affine3 l = InstanceMath.ToUnityAffine(locals[c]);
                world[c] = p < 0 ? l : world[p] * l;
            }
            for (int i = 0; i < 200; i++)
            {
                var v = S5InstanceMathTests.RandomPoint(rnd, 1.2);
                var bones = new byte[] { (byte)rnd.Next(78), (byte)rnd.Next(78), (byte)rnd.Next(78), (byte)rnd.Next(78) };
                var w = new float[] { 0.4f, 0.3f, 0.2f, 0.1f };
                Vec3 se = SkinMath.SkinPointSe(v, bones, w, 0, bind, c1m);
                Vec3 unity = Vec3.Zero;
                for (int k = 0; k < 4; k++)
                    unity = unity + (world[bones[k]] * InstanceMath.ToUnityAffine(bind[bones[k]])).TransformPoint(WireMath.Mirror(v)) * w[k];
                Assert.IsTrue((unity - WireMath.Mirror(se)).Length < 1e-9, "Unity skinning = mirrored SE skinning");
            }
            // Rest pose: the abs binds as bones give the bind-pose vertex back.
            var absCanon = new SeMatrix[78];
            for (int c = 0; c < 78; c++) absCanon[c] = abs[map.CanonicalToModel[c]];
            var rest = SkinMath.SkinPointSe(new Vec3(0.3, 1.1, -0.2), new byte[] { 13, 2, 0, 0 }, new[] { 0.5f, 0.5f, 0f, 0f }, 0, bind, absCanon);
            Assert.IsTrue((rest - new Vec3(0.3, 1.1, -0.2)).Length < 1e-5);
        }

        [TestMethod]
        public void AbsoluteFromLocal_AnyOrderAndCycles()
        {
            var l = new[] { InstanceMath.Identity, new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(1, 0, 0)), new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(0, 2, 0)) };
            var abs = new SeMatrix[3];
            SkinMath.AbsoluteFromLocal(l, new[] { 2, -1, 1 }, abs);
            Assert.AreEqual(new Vec3(1, 2, 0), abs[0].Translation, "child listed before its parents");
            MirrorConventionsTests.AssertThrows<ArgumentException>(() => SkinMath.AbsoluteFromLocal(l, new[] { 1, 2, 0 }, abs));
        }

        internal static ModelGeometry CharacterModel(Random rnd)
        {
            ModelGeometry g = ModelGeometry.Allocate(14, 36, 4, ModelFlags.Skinned | ModelFlags.HasTangents);
            for (int i = 0; i < g.Positions.Length; i++) g.Positions[i] = (float)(rnd.NextDouble() * 2 - 1);
            for (int i = 0; i < g.Normals.Length; i++) { g.Normals[i] = (float)rnd.NextDouble(); g.Tangents[i] = (float)rnd.NextDouble(); g.Binormals[i] = (float)rnd.NextDouble(); }
            for (int i = 0; i < g.Uvs.Length; i++) g.Uvs[i] = (float)rnd.NextDouble();
            for (int i = 0; i < g.BoneIndices.Length; i++) { g.BoneIndices[i] = (byte)rnd.Next(78); g.BoneWeights[i] = (i & 3) == 0 ? 1f : 0f; }
            for (int i = 0; i < 36; i++) g.Indices[i] = (uint)rnd.Next(14);
            g.Submeshes[0] = new SubmeshRange(1, 0, 4, 0);
            g.Submeshes[1] = new SubmeshRange(2, 4, 3, 0);
            g.Submeshes[2] = new SubmeshRange(3, 6, 3, 15);   // overlaps triangle 6 (owned by submesh 1)
            g.Submeshes[3] = new SubmeshRange(4, 9, 1, 0);    // triangles 10, 11 are uncovered
            g.ModelKey = 99;
            g.ComputeBounds();
            return g;
        }

        private static List<string> Triangles(ModelGeometry g, int start, int count)
        {
            var list = new List<string>();
            for (int t = start; t < start + count; t++)
            {
                var s = new System.Text.StringBuilder();
                for (int e = 0; e < 3; e++)
                {
                    int v = (int)g.Indices[3 * t + e];
                    s.Append(g.Positions[3 * v]).Append(',').Append(g.Positions[3 * v + 1]).Append(',').Append(g.Positions[3 * v + 2]).Append(';');
                }
                list.Add(s.ToString());
            }
            return list;
        }

        [TestMethod]
        public void Split_ByMaterialKeepsCoverageAndCompactsVertices()
        {
            var rnd = new Random(15);
            ModelGeometry src = CharacterModel(rnd);
            ModelGeometry body, head;
            SkinMath.SplitByMaterial(src, new[] { false, true, false, false }, out body, out head);
            string err;
            Assert.IsTrue(body.Validate(out err), err);
            Assert.IsTrue(head.Validate(out err), err);
            Assert.AreEqual(12, body.TriangleCount + head.TriangleCount, "every triangle exactly once");
            Assert.AreEqual(1, head.SubmeshCount);
            Assert.AreEqual(3u, head.Submeshes[0].TriCount, "submesh 1 owns 4, 5, 6");
            Assert.AreEqual(2UL, head.Submeshes[0].MaterialKey);
            CollectionAssert.AreEqual(Triangles(src, 4, 3), Triangles(head, 0, 3));
            Assert.AreEqual(3, body.SubmeshCount);
            Assert.AreEqual(new SubmeshRange(1, 0, 4, 0), body.Submeshes[0]);
            Assert.AreEqual(new SubmeshRange(3, 4, 2, 15), body.Submeshes[1], "submesh 2 keeps only 7, 8");
            Assert.AreEqual(new SubmeshRange(4, 6, 1, 0), body.Submeshes[2]);
            Assert.AreEqual(9, body.TriangleCount, "7 covered + 2 uncovered after the submeshes");
            CollectionAssert.AreEqual(Triangles(src, 10, 2), Triangles(body, 7, 2));
            foreach (ModelGeometry p in new[] { body, head })
            {
                var used = new bool[p.VertexCount];
                foreach (uint i in p.Indices) used[i] = true;
                Assert.IsTrue(used.All(u => u), "only the vertices the part uses");
                Assert.AreEqual(99UL, p.ModelKey);
                Assert.IsTrue(p.Skinned);
            }
            SkinMath.SplitByMaterial(src, new[] { false, false, false, false }, out body, out head);
            Assert.IsNull(head);
            Assert.AreEqual(12, body.TriangleCount);
            SkinMath.SplitByMaterial(src, new[] { true, true, true, true }, out body, out head);
            Assert.AreEqual(2, body.TriangleCount, "the uncovered triangles stay with the body");
            Assert.AreEqual(0, body.SubmeshCount);
            Assert.AreEqual(10, head.TriangleCount);
            MirrorConventionsTests.AssertThrows<ArgumentException>(() => SkinMath.SplitByMaterial(src, new bool[3], out body, out head));
        }

        [TestMethod]
        public void RemapModelWeights_InPlace()
        {
            var rnd = new Random(16);
            List<string> names;
            List<int> parents;
            ModelSkeleton(rnd, out names, out parents);
            SkeletonMap map = Map(names, parents);
            ModelGeometry g = CharacterModel(rnd);
            for (int i = 0; i < g.BoneIndices.Length; i++) g.BoneIndices[i] = (byte)rnd.Next(names.Count);
            string err;
            Assert.IsTrue(SkinMath.RemapModelWeights(g, map, out err), err);
            for (int v = 0; v < g.VertexCount; v++)
            {
                Assert.AreEqual(1.0, g.BoneWeights[4 * v] + g.BoneWeights[4 * v + 1] + g.BoneWeights[4 * v + 2] + g.BoneWeights[4 * v + 3], 1e-6);
                for (int k = 0; k < 4; k++) Assert.IsTrue(g.BoneIndices[4 * v + k] < 78);
            }
            g.BoneIndices[0] = 250;
            g.BoneWeights[0] = 1f;
            Assert.IsFalse(SkinMath.RemapModelWeights(g, map, out err));
            Assert.IsFalse(SkinMath.RemapModelWeights(ModelGeometry.Allocate(1, 0, 0, 0), map, out err));
        }
    }
}
