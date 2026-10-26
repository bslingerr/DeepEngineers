using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S10 round 3 milestone E (docs/S10-BRIEF.md "## Round 3"): the allocation cuts leave every result byte-identical.
 /// The mirror's cell build (worker scratch reuse, the per-model triangle groups) and the texture repack (level 0 written
 /// into the mip chain) are run with <see cref="S10AllocCuts"/> off and on over the same inputs, several times in a row
 /// (reused accumulators keep their old capacity), and every output field is compared.
 /// </summary>
    [TestClass]
    public class S10SnTests
    {
        private const ulong MatA = 0xA1, MatB = 0xB2, MatC = 0xC3;

        /// <summary>A model of <paramref name="tris"/> random triangles with overlapping and partial submesh ranges (some
 /// triangles uncovered, some in two ranges), so TriangleGroups' first-wins and material-0 rules both run.</summary>
        private static PreparedModel RandomModel(ulong key, int tris, int seed)
        {
            var rnd = new Random(seed);
            int nv = tris * 3;
            var m = new ModelData
            {
                Key = key, VertexCount = nv, IndexCount = nv, PatternScale = 1 + (float)rnd.NextDouble(),
                Pos = new float[nv * 3], Nrm = new float[nv * 3], Tan = new float[nv * 3], Bin = new float[nv * 3], Uv = new float[nv * 2], Indices = new int[nv],
                Submeshes = new[]
                {
                    new SubmeshInfo { MaterialKey = MatA, TriStart = 0, TriCount = tris / 2 },
                    new SubmeshInfo { MaterialKey = MatB, TriStart = tris / 3, TriCount = tris / 2 },       // overlaps A
                    new SubmeshInfo { MaterialKey = MatC, TriStart = tris - tris / 8, TriCount = tris / 16 }, // leaves a gap
                },
            };
            for (int i = 0; i < nv * 3; i++)
            {
                m.Pos[i] = (float)(rnd.NextDouble() * 5 - 2.5);
                m.Nrm[i] = (float)(rnd.NextDouble() * 2 - 1);
                m.Tan[i] = (float)(rnd.NextDouble() * 2 - 1);
                m.Bin[i] = (float)(rnd.NextDouble() * 2 - 1);
            }
            for (int i = 0; i < nv * 2; i++) m.Uv[i] = (float)rnd.NextDouble();
            for (int i = 0; i < nv; i++) m.Indices[i] = rnd.Next(nv); // shared and repeated vertices: the remap runs
            return MeshBuilder.Prepare(m, true, true);
        }

        private static PartRecord Part(ulong id, ulong model, int seed, bool improper)
        {
            var rnd = new Random(seed);
            double a = rnd.NextDouble() * Math.PI * 2;
            float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            var p = new PartRecord { EntityId = 1, PartKey = id, ModelKey = model, OffU = (float)rnd.NextDouble(), OffV = (float)rnd.NextDouble(),
                                     H = rnd.Next(3) * 0.25f, S = 0.5f, V = 0.1f };
            float[] rows = { c, 0, -s, 0, improper ? -1 : 1, 0, s, 0, c, (float)(rnd.NextDouble() * 20), (float)(rnd.NextDouble() * 20), (float)(rnd.NextDouble() * 20) };
            Array.Copy(rows, p.Local, 12);
            return p;
        }

        private static CellBuildInput Input(List<PreparedModel> models, int parts, int seed)
        {
            var rnd = new Random(seed);
            var input = new CellBuildInput { GridId = 77, Cell = new CellKey(1, 2, 3), Version = seed, Options = new BuildOptions() };
            for (int i = 0; i < parts; i++)
            {
                PreparedModel pm = models[rnd.Next(models.Count)];
                input.Parts.Add(new PartBuild { Part = Part((ulong)(i + 1), pm.Key, seed * 1000 + i, rnd.Next(5) == 0), Model = pm });
            }
            return input;
        }

        private static string F(float v) { return BitConverter.ToInt32(BitConverter.GetBytes(v), 0).ToString("x8", CultureInfo.InvariantCulture); }
        private static string D(double v) { return BitConverter.DoubleToInt64Bits(v).ToString("x16", CultureInfo.InvariantCulture); }

        /// <summary>Every field of a build result, floats by their bits.</summary>
        private static string Dump(CellBuildResult r)
        {
            var sb = new StringBuilder();
            sb.Append(r.GridId).Append('|').Append(r.Cell).Append('|').Append(r.Version).Append('|').Append(r.Vertices).Append('|').Append(r.Triangles).Append('|')
              .Append(r.Parts).Append('|').Append(r.ModelVertices).Append('|').Append(r.ModelTriangles).Append('|').Append(r.UncoveredTriangles).Append('|')
              .Append(r.OverlapTriangles).Append('|').Append(D(r.MinAgreement)).Append('|').Append(r.Error).Append('\n');
            foreach (ChunkMeshData c in r.Chunks)
            {
                sb.Append(c.Key).Append(' ').Append(c.VertexCount).Append(' ').Append(c.IndexCount).Append(' ').Append(c.Parts).Append(' ').Append(D(c.Agreement))
                  .Append(' ').Append(c.Degenerate).Append(' ').Append(F(c.H)).Append(F(c.S)).Append(F(c.V)).Append(' ')
                  .Append(F(c.MinX)).Append(F(c.MinY)).Append(F(c.MinZ)).Append(F(c.MaxX)).Append(F(c.MaxY)).Append(F(c.MaxZ)).Append(' ').Append(c.Vertices.Length).Append('\n');
                for (int i = 0; i < c.VertexCount; i++)
                {
                    MirrorVertex v = c.Vertices[i];
                    sb.Append(F(v.Px)).Append(F(v.Py)).Append(F(v.Pz)).Append(F(v.Nx)).Append(F(v.Ny)).Append(F(v.Nz)).Append(F(v.Tx)).Append(F(v.Ty)).Append(F(v.Tz))
                      .Append(F(v.Tw)).Append(F(v.U)).Append(F(v.V)).Append(';');
                }
                sb.Append('\n');
                if (c.Use32) foreach (int i in c.Indices32) sb.Append(i).Append(',');
                else foreach (ushort i in c.Indices16) sb.Append(i).Append(',');
                sb.Append('\n');
                foreach (PlacementProbe p in c.Probes)
                    sb.Append(p.Valid).Append(p.Vertex).Append(D(p.Score)).Append(D(p.SeLocal.X)).Append(D(p.SeLocal.Y)).Append(D(p.SeLocal.Z))
                      .Append(D(p.Drawn.X)).Append(D(p.Drawn.Y)).Append(D(p.Drawn.Z)).Append(';');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        [TestMethod]
        public void S10E_BuildCell_CutsOnAndOff_ByteIdentical()
        {
            bool was = S10AllocCuts.On;
            try
            {
                var models = new List<PreparedModel> { RandomModel(11, 60, 1), RandomModel(12, 7, 2), RandomModel(13, 300, 3) };
                // big cells first, then small ones: the reused accumulators come back with stale capacity and stale probes
                int[][] plan = { new[] { 40, 101 }, new[] { 3, 102 }, new[] { 25, 103 }, new[] { 1, 104 }, new[] { 60, 105 } };
                var off = new List<string>();
                S10AllocCuts.On = false;
                foreach (int[] p in plan) off.Add(Dump(MeshBuilder.BuildCell(Input(models, p[0], p[1]))));
                S10AllocCuts.On = true;
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < plan.Length; i++)
                        Assert.AreEqual(off[i], Dump(MeshBuilder.BuildCell(Input(models, plan[i][0], plan[i][1]))), "cell " + i + " pass " + pass + ": the cuts change nothing");
                Assert.IsTrue(MeshBuilder.S10GroupsReused > 0, "the triangle groups are reused across parts");
                // a model whose submeshes are replaced gets its groups again (the cache checks its inputs)
                PreparedModel m = models[0];
                CellBuildResult before = MeshBuilder.BuildCell(Input(models, 10, 200));
                m.Submeshes = new[] { new SubmeshInfo { MaterialKey = MatC, TriStart = 0, TriCount = m.TriangleCount } };
                S10AllocCuts.On = false;
                string fresh = Dump(MeshBuilder.BuildCell(Input(models, 10, 200)));
                S10AllocCuts.On = true;
                Assert.AreEqual(fresh, Dump(MeshBuilder.BuildCell(Input(models, 10, 200))), "new submeshes: new groups");
                Assert.AreNotEqual(Dump(before), fresh);
            }
            finally { S10AllocCuts.On = was; }
        }

        [TestMethod]
        public void S10E_Repack_CutsOnAndOff_ByteIdentical()
        {
            bool was = S10AllocCuts.On;
            try
            {
                foreach (int[] size in new[] { new[] { 64, 32 }, new[] { 5, 3 }, new[] { 1, 1 }, new[] { 16, 16 } })
                {
                    int w = size[0], h = size[1], n = w * h;
                    var rnd = new Random(w * 31 + h);
                    var cm = new byte[n * 4]; var ng = new byte[n * 4]; var add = new byte[n * 4];
                    rnd.NextBytes(cm); rnd.NextBytes(ng); rnd.NextBytes(add);
                    foreach (bool flip in new[] { false, true })
                        foreach (bool withNg in new[] { false, true })
                        {
                            S10AllocCuts.On = false;
                            DerivedMaps a = TextureRepack.Build(cm, withNg ? ng : null, withNg ? add : null, w, h, flip);
                            S10AllocCuts.On = true;
                            DerivedMaps b = TextureRepack.Build(cm, withNg ? ng : null, withNg ? add : null, w, h, flip);
                            string what = w + "x" + h + " flip=" + flip + " ng=" + withNg;
                            Assert.AreEqual(a.MipCount, b.MipCount, what);
                            CollectionAssert.AreEqual(a.Spec, b.Spec, what + " spec");
                            CollectionAssert.AreEqual(a.Bump, b.Bump, what + " bump");
                            CollectionAssert.AreEqual(a.Mask, b.Mask, what + " mask");
                            CollectionAssert.AreEqual(a.Illum, b.Illum, what + " illum");
                            Assert.AreEqual(a.EmissiveMax, b.EmissiveMax);
                            Assert.AreEqual(a.PaintMax, b.PaintMax);
                            Assert.AreEqual(a.MetalMean, b.MetalMean);
                        }
                }
            }
            finally { S10AllocCuts.On = was; }
        }
    }
}
