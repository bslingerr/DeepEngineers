using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S3 mesh conventions (CONTRACT §2.5): mirror and (I0, I2, I1) on a known cube (normals agree), the winding measure and
 /// the automatic flip, the seeded mutants S3.M1 (mirror dropped) and S3.M2 (swap dropped), tangent w, AABB corners,
 /// armor UVs and pattern offsets, and the worker-thread build pass of Unity arrays.
 /// </summary>
    [TestClass]
    public class S3MeshTests
    {
        private static int[] UnityTris(ModelGeometry g)
        {
            var t = new int[g.IndexCount];
            MeshConventions.MirrorTriangles(g.Indices, t, g.VertexCount);
            return t;
        }

        private static float[] Mirrored(float[] a)
        {
            var m = new float[a.Length];
            MeshConventions.MirrorXyz(a, m);
            return m;
        }

        [TestMethod]
        public void Cube_MirrorAndSwap_EveryTriangleAgreesWithItsNormals()
        {
            ModelGeometry cube = S3TestModels.Cube();
            // the SE convention of the fixture: cross(V1 - V0, V2 - V0) is outward in SE space (MyModel.Triangles)
            for (int t = 0; t < 12; t++)
            {
                Vec3 a = cube.Position((int)cube.Indices[3 * t]), b = cube.Position((int)cube.Indices[3 * t + 1]), c = cube.Position((int)cube.Indices[3 * t + 2]);
                Assert.IsTrue(Vec3.Dot(Vec3.Cross(b - a, c - a), cube.Normal((int)cube.Indices[3 * t])) > 0, "fixture triangle " + t);
            }
            int[] tris = UnityTris(cube);
            for (int i = 0; i < tris.Length; i += 3)
            {
                Assert.AreEqual((int)cube.Indices[i], tris[i]);
                Assert.AreEqual((int)cube.Indices[i + 2], tris[i + 1], "Unity gets (I0, I2, I1)");
                Assert.AreEqual((int)cube.Indices[i + 1], tris[i + 2]);
            }
            WindingStats w = MeshConventions.MeasureWinding(Mirrored(cube.Positions), Mirrored(cube.Normals), tris);
            Assert.AreEqual(new WindingStats(12, 0, 0), w);
            Assert.IsTrue(w.Passes);
            Assert.IsFalse(w.ShouldFlip);
            Assert.AreEqual(w, MeshConventions.MeasureWinding(cube), "the allocation-free measure agrees");
        }

        [TestMethod]
        public void Mutants_M1MirrorDropped_M2SwapDropped_AreNearZero_AndFlipRestores()
        {
            ModelGeometry cube = S3TestModels.Cube();
            int[] tris = UnityTris(cube);
            // S3.M2: the swap dropped (wire order drawn as is, mirrored positions)
            int[] wireOrder = Array.ConvertAll(cube.Indices, x => (int)x);
            WindingStats m2 = MeshConventions.MeasureWinding(Mirrored(cube.Positions), Mirrored(cube.Normals), wireOrder);
            Assert.AreEqual(0.0, m2.Fraction, "S3.M2: S3.C3 near 0 %");
            Assert.IsTrue(m2.ShouldFlip, "below 1 %: the automatic flip");
            MeshConventions.FlipTriangles(wireOrder);
            Assert.AreEqual(new WindingStats(12, 0, 0), MeshConventions.MeasureWinding(Mirrored(cube.Positions), Mirrored(cube.Normals), wireOrder));
            Assert.AreEqual(m2.Flipped(), new WindingStats(12, 0, 0));
            // S3.M1: the mirror dropped (positions and normals in SE space, Unity order)
            WindingStats m1 = MeshConventions.MeasureWinding(cube.Positions, cube.Normals, tris);
            Assert.AreEqual(0.0, m1.Fraction, "S3.M1: S3.C3 red");
            // half wrong: ~50 % (normals or mirror inconsistent) is neither a pass nor a flip
            int[] half = (int[])tris.Clone();
            MeshConventions.FlipTriangles(half, 0, 18);
            WindingStats h = MeshConventions.MeasureWinding(Mirrored(cube.Positions), Mirrored(cube.Normals), half);
            Assert.AreEqual(0.5, h.Fraction);
            Assert.IsFalse(h.Passes);
            Assert.IsFalse(h.ShouldFlip);
        }

        [TestMethod]
        public void Winding_DegenerateTrianglesAreCountedApart()
        {
            float[] p = { 0, 0, 0, 1, 0, 0, 2, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0 };
            float[] n = { 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1 };
            int[] t = { 0, 1, 2, 3, 4, 5 };   // collinear; then cross((0,1,0),(1,0,0)) = (0,0,-1) along n
            WindingStats w = MeshConventions.MeasureWinding(p, n, t);
            Assert.AreEqual(new WindingStats(1, 0, 1), w);
            Assert.AreEqual(1.0, w.Fraction);
            Assert.AreEqual(1.0, new WindingStats(0, 0, 3).Fraction, "nothing measurable: nothing to flip");
            Assert.IsFalse(new WindingStats(0, 0, 3).ShouldFlip);
        }

        [TestMethod]
        public void MirrorTriangles_RejectsIndicesBeyondTheVertices()
        {
            try
            {
                MeshConventions.MirrorTriangles(new uint[] { 0, 1, 3 }, new int[3], 3);
                Assert.Fail("index 3 with 3 vertices");
            }
            catch (ArgumentOutOfRangeException) { }
        }

        [TestMethod]
        public void TangentW_EqualsSeOwnSign_TheMirrorDoesNotChangeIt()
        {
            var rnd = new Random(30030010);
            for (int i = 0; i < 20000; i++)
            {
                Vec3 n = RandomUnit(rnd), t = RandomUnit(rnd), b = RandomUnit(rnd);
                float w = MeshConventions.TangentW(WireMath.Mirror(n), WireMath.Mirror(t), WireMath.Mirror(b));
                Assert.AreEqual(MeshConventions.SeTangentSign(n, t, b), w, "frame " + i);
                // Unity rebuilds the binormal as Cross(n', t') * w: it lies on the mirrored SE binormal's side
                Vec3 rebuilt = Vec3.Cross(WireMath.Mirror(n), WireMath.Mirror(t)) * w;
                Assert.IsTrue(Vec3.Dot(rebuilt, WireMath.Mirror(b)) >= 0);
            }
            Assert.AreEqual(1f, MeshConventions.TangentW(Vec3.Zero, Vec3.Zero, Vec3.Zero), "zero: +1 (SE: not < 0)");
            Assert.AreEqual(-1, MeshConventions.SeNormalMapGreenSign);
        }

        private static Vec3 RandomUnit(Random rnd)
        {
            while (true)
            {
                var v = new Vec3(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1);
                double l = v.Length;
                if (l > 0.1 && l <= 1) return v / l;
            }
        }

        [TestMethod]
        public void AabbCorners_OrderAndMirror()
        {
            var min = new Vec3(-1, -2, -3);
            var max = new Vec3(4, 5, 6);
            Vec3[] c = MeshConventions.AabbCorners(min, max);
            Assert.AreEqual(min, c[0]);
            Assert.AreEqual(max, c[7]);
            Assert.AreEqual(new Vec3(4, -2, -3), c[1], "bit 0 -> max x");
            Assert.AreEqual(new Vec3(-1, 5, -3), c[2], "bit 1 -> max y");
            Assert.AreEqual(new Vec3(-1, -2, 6), c[4], "bit 2 -> max z");
            Vec3 umin, umax;
            MeshConventions.MirrorAabb(min, max, out umin, out umax);
            Assert.AreEqual(new Vec3(-1, -2, -6), umin);
            Assert.AreEqual(new Vec3(4, 5, 3), umax);
            SeMatrix part = S3TestModels.Rigid(new Vec3(0, 1, 0), Math.PI / 2, new Vec3(10, 0, 0));
            Vec3[] pc = MeshConventions.PartCorners(part, min, max);
            Assert.IsTrue(Vec3.MaxAbsDiff(pc[7], part.Transform(max)) < 1e-12);
        }

        [TestMethod]
        public void ArmorPatternOffset_PackedDecodingEqualsTheOffsetOverThePattern()
        {
            for (int px = 1; px <= 16; px++)
                for (int py = 1; py <= 16; py++)
                {
                    byte w7 = MeshConventions.PackPattern(px, py);
                    for (int ox = 0; ox < 16; ox++)
                        for (int oy = 0; oy < 16; oy += 3)
                        {
                            float u1, v1, u2, v2;
                            MeshConventions.ArmorPatternOffset(ox, oy, px, py, out u1, out v1);
                            MeshConventions.ArmorPatternOffsetPacked((byte)ox, (byte)oy, w7, out u2, out v2);
                            Assert.AreEqual(u1, u2);
                            Assert.AreEqual(v1, v2);
                            Assert.AreEqual((float)ox / px, u1);
                        }
                }
            float u, v;
            MeshConventions.ArmorUv(0.5f, 0.25f, 2f, 0.25f, 0.5f, out u, out v);
            Assert.AreEqual(0.5f, u, "uv / patternScale + offset");
            Assert.AreEqual(0.625f, v);
            MeshConventions.ArmorUv(0.5f, 0.25f, 0f, 0f, 0f, out u, out v);
            Assert.AreEqual(0.5f, u, "a non-positive pattern scale counts as 1");
        }

        [TestMethod]
        public void Builder_PartMatrixInSeSpace_ThenMirror_NormalsAgree_UvsTangentsSubmeshes()
        {
            ulong matA = 0xA, matB = 0xB;
            ModelGeometry cube = S3TestModels.Cube(1.25f, matA, SeDrawTechnique.Mesh, 2f);
            ModelGeometry cube2 = S3TestModels.Cube(0.5f, matB, SeDrawTechnique.AlphaMasked, 1f, "other.mwm");
            cube2.Submeshes = new[] { new SubmeshRange(matB, 0, 8, SeDrawTechnique.AlphaMasked) };   // 4 triangles uncovered
            var origin = new Vec3(5, 0, -2.5);
            SeMatrix p1 = S3TestModels.Rigid(new Vec3(1, 2, 3), 0.9, new Vec3(5, 2.5, -2.5));
            SeMatrix p2 = S3TestModels.Rigid(new Vec3(0, 1, 0), Math.PI, new Vec3(7.5, 0, 0));
            var b = new UnityMeshBuilder(origin);
            b.AddPart(cube, p1, 0.25f, 0.5f, false);
            b.AddPart(cube2, ModelCodec.MakePartAdd(1, 2, cube2.ModelKey, p2, Vec3.Zero, 0f, 0f), false);
            UnityMeshData d = b.Build();
            Assert.AreEqual(48, d.VertexCount);
            Assert.AreEqual(2, d.Parts);
            Assert.AreEqual(4, d.UncoveredTriangles);
            Assert.AreEqual(3, d.Submeshes.Length, "matA, matB, then key 0 for the uncovered triangles");
            Assert.AreEqual(matA, d.Submeshes[0].MaterialKey);
            Assert.AreEqual(36, d.Submeshes[0].IndexCount);
            Assert.AreEqual(matB, d.Submeshes[1].MaterialKey);
            Assert.AreEqual(SeDrawTechnique.AlphaMasked, d.Submeshes[1].DrawTechnique);
            Assert.AreEqual(24, d.Submeshes[1].IndexCount);
            Assert.AreEqual(0UL, d.Submeshes[2].MaterialKey);
            Assert.AreEqual(60, d.Submeshes[2].IndexStart);
            Assert.AreEqual(new WindingStats(24, 0, 0), d.Winding, "rigid part matrices keep every triangle agreeing");
            for (int i = 0; i < 24; i++)
            {
                Vec3 want = WireMath.Mirror(p1.Transform(cube.Position(i)) - origin);
                Vec3 got = new Vec3(d.Positions[3 * i], d.Positions[3 * i + 1], d.Positions[3 * i + 2]);
                Assert.IsTrue(Vec3.MaxAbsDiff(want, got) < 1e-5, "position " + i);
                Vec3 nWant = WireMath.Mirror(p1.Transform(cube.Normal(i)) - p1.Translation);
                Vec3 nGot = new Vec3(d.Normals[3 * i], d.Normals[3 * i + 1], d.Normals[3 * i + 2]);
                Assert.IsTrue(Vec3.MaxAbsDiff(nWant, nGot) < 1e-6, "normal " + i);
                Assert.AreEqual(MeshConventions.SeTangentSign(cube.Normal(i), cube.Tangent(i), cube.Binormal(i)), d.Tangents[4 * i + 3], "tangent w " + i);
                Assert.AreEqual(cube.Uvs[2 * i] / 2f + 0.25f, d.Uv[2 * i], "u / patternScale + offset");
                Assert.AreEqual(cube.Uvs[2 * i + 1] / 2f + 0.5f, d.Uv[2 * i + 1]);
            }
            // the same part flipped by the winding rule is drawn in wire order: every triangle disagrees
            var bf = new UnityMeshBuilder(origin);
            bf.AddPart(cube, p1, 0f, 0f, true);
            UnityMeshData f = bf.Build();
            Assert.AreEqual(new WindingStats(0, 12, 0), f.Winding);
            Assert.AreEqual(1, f.FlippedParts);
            // bounds of the output in Unity-local space
            Vec3 mn, mx;
            MeshConventions.Bounds(d.Positions, d.VertexCount, out mn, out mx);
            Assert.AreEqual(mn, d.BoundsMin);
            Assert.AreEqual(mx, d.BoundsMax);
        }
    }
}
