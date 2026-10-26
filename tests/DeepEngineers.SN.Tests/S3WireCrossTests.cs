using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S3 integration: the Subnautica plugin's own Unity-free S3 code (MirrorWire's ModelAssembler / ModelPayload /
 /// MaterialDefData, MeshBuilder) against the shared Wire code the SE side uses (ModelCodec's ModelEncoder and
 /// EncodeMaterialDef, MeshConventions' UnityMeshBuilder and MeasureWinding, PoseMath.Apply). Wire is itself checked
 /// byte for byte against tools/worldmap/wire_ref.py (S3VectorTests), so these tests chain the SN plugin to the
 /// independent Python reference: the same model bytes are read back identically, the same mirrored mesh comes out
 /// (triangle for triangle, winding included), one winding rule (CONTRACT §2.5's census rule) decides the flip, and the
 /// Frame/Entity hierarchy equals PoseMath.Apply.
 /// </summary>
    [TestClass]
    public class S3WireCrossTests
    {
        private const ulong MatA = 0xA1A1A1A1A1A1A1A1UL;
        private const ulong MatB = 0xB2B2B2B2B2B2B2B2UL;

        /// <summary>
 /// A 2.5 m cube in SE's MyModel.Triangles convention (SE-space Cross(p1 − p0, p2 − p0) along the outward normal),
 /// tangents and binormals, distinct UVs, two materials (faces 0-2 MatA, 3-5 MatB), patternScale 2.
 /// </summary>
        private static ModelGeometry Cube(ulong key)
        {
            var normals = new[] { new Vec3(1, 0, 0), new Vec3(-1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1), new Vec3(0, 0, -1) };
            ModelGeometry g = ModelGeometry.Allocate(24, 36, 2, ModelFlags.HasTangents);
            g.ModelKey = key;
            g.PatternScale = 2f;
            const double h = 1.25;
            int v = 0, ix = 0;
            for (int f = 0; f < 6; f++)
            {
                Vec3 n = normals[f];
                Vec3 t = Math.Abs(n.Y) > 0.5 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
                Vec3 b = Vec3.Cross(n, t);
                Vec3 c = n * h;
                Vec3[] corners = { c - t * h - b * h, c + t * h - b * h, c + t * h + b * h, c - t * h + b * h };
                for (int i = 0; i < 4; i++, v++)
                {
                    Set(g.Positions, v, corners[i]); Set(g.Normals, v, n); Set(g.Tangents, v, t); Set(g.Binormals, v, b);
                    g.Uvs[2 * v] = (float)(0.1 * f + 0.05 * (i & 1)); g.Uvs[2 * v + 1] = (float)(0.2 + 0.07 * i);
                }
                int b0 = 4 * f;
                foreach (int k in new[] { b0, b0 + 1, b0 + 2, b0, b0 + 2, b0 + 3 }) g.Indices[ix++] = (uint)k;
            }
            g.Submeshes[0] = new SubmeshRange(MatA, 0, 6, SeDrawTechnique.Mesh);
            g.Submeshes[1] = new SubmeshRange(MatB, 6, 6, SeDrawTechnique.Mesh);
            g.ComputeBounds();
            return g;
        }

        private static void Set(float[] a, int i, Vec3 v) { a[3 * i] = (float)v.X; a[3 * i + 1] = (float)v.Y; a[3 * i + 2] = (float)v.Z; }

        private static unsafe C3Outcome Feed(ModelAssembler a, uint type, byte[] payload)
        {
            fixed (byte* p = payload) return a.Feed(type, p, payload.Length);
        }

        /// <summary>A rigid SE part matrix (rows Right, Up, Backward, Translation) from a rotation about a tilted axis.</summary>
        private static float[] PartRows(double deg, Vec3 axis, Vec3 t)
        {
            Quat q = MirrorPoseMath.AngleAxis(deg, axis);
            // SE row vectors: the images of the unit axes (rotation as a column operator: R·e_i)
            Vec3 r = q.Rotate(new Vec3(1, 0, 0)), u = q.Rotate(new Vec3(0, 1, 0)), b = q.Rotate(new Vec3(0, 0, 1));
            return new[] { (float)r.X, (float)r.Y, (float)r.Z, (float)u.X, (float)u.Y, (float)u.Z, (float)b.X, (float)b.Y, (float)b.Z, (float)t.X, (float)t.Y, (float)t.Z };
        }

        /// <summary>A triangle as a winding-preserving key: positions rounded to 0.1 mm, rotated to start at the smallest.</summary>
        private static string TriKey(Vec3 a, Vec3 b, Vec3 c)
        {
            string[] k = { PKey(a), PKey(b), PKey(c) };
            int s = 0;
            for (int i = 1; i < 3; i++) if (string.CompareOrdinal(k[i], k[s]) < 0) s = i;
            return k[s] + "|" + k[(s + 1) % 3] + "|" + k[(s + 2) % 3];
        }

        private static string PKey(Vec3 p)
        {
            var ci = CultureInfo.InvariantCulture;
            return Math.Round(p.X, 4).ToString("0.0000", ci) + "," + Math.Round(p.Y, 4).ToString("0.0000", ci) + "," + Math.Round(p.Z, 4).ToString("0.0000", ci);
        }

        // ------------------------------------------------------------------------------------- C3 bytes

        [TestMethod]
        public void S3Cross_WireEncoderChunks_ReadBackBySnAssemblerAndParser_Identically()
        {
            ModelGeometry g = Cube(0x5E5E000000000001UL);
            var enc = new ModelEncoder(g, 300);          // several chunks
            Assert.IsTrue(enc.ChunkCount > 3, "the payload is split");
            var asm = new ModelAssembler();
            CompletedModel done = null;
            for (int i = 0; i < enc.MessageCount; i++)
            {
                C3Outcome o = Feed(asm, enc.MessageType(i), enc.MessagePayload(i));
                Assert.AreNotEqual(C3Outcome.Error, o, asm.LastError);
                if (o == C3Outcome.ModelComplete) done = asm.Completed;
            }
            Assert.IsNotNull(done, "ModelEnd closed the model (FNV-1a 64 of Wire's chunks = SN's own hash)");
            Assert.AreEqual(0, asm.Misparse);
            Assert.AreEqual(enc.End.payloadHash, done.Hash);
            Assert.AreEqual(ModelCodec.PayloadHash(enc.Payload, 0, enc.Payload.Length), done.Hash);
            ulong workerHash;
            Assert.IsTrue(ModelPayload.HashMatches(done, out workerHash), "the SN worker's payloadHash check (CONTRACT §5.4) accepts Wire's chunks");
            Assert.AreEqual(ModelCodec.PayloadHash(enc.Payload, 0, enc.Payload.Length), workerHash);
            ModelData m;
            string err;
            Assert.IsTrue(ModelPayload.TryParse(done.Begin, done.Payload, done.Length, out m, out err), err);
            Assert.AreEqual(g.VertexCount, m.VertexCount);
            Assert.AreEqual(g.IndexCount, m.IndexCount);
            Assert.AreEqual(g.PatternScale, m.PatternScale);
            for (int i = 0; i < g.VertexCount * 3; i++)
            {
                Assert.AreEqual(g.Positions[i], m.Pos[i]); Assert.AreEqual(g.Normals[i], m.Nrm[i]);
                Assert.AreEqual(g.Tangents[i], m.Tan[i]); Assert.AreEqual(g.Binormals[i], m.Bin[i]);
            }
            for (int i = 0; i < g.VertexCount * 2; i++) Assert.AreEqual(g.Uvs[i], m.Uv[i]);
            for (int i = 0; i < g.IndexCount; i++) Assert.AreEqual((int)g.Indices[i], m.Indices[i]);
            for (int s = 0; s < g.SubmeshCount; s++)
            {
                Assert.AreEqual(g.Submeshes[s].MaterialKey, m.Submeshes[s].MaterialKey);
                Assert.AreEqual((int)g.Submeshes[s].TriStart, m.Submeshes[s].TriStart);
                Assert.AreEqual((int)g.Submeshes[s].TriCount, m.Submeshes[s].TriCount);
                Assert.AreEqual(g.Submeshes[s].DrawTechnique, m.Submeshes[s].DrawTechnique);
            }
            Assert.AreEqual(g.Bounds[0], m.BMinX); Assert.AreEqual(g.Bounds[5], m.BMaxZ);

            // MaterialDef: Wire's encoder, SN's parser (strings with non-ASCII characters and an empty alpha mask)
            var def = new MaterialDefinition
            {
                MaterialKey = ModelCodec.MaterialKey(0, 0, @"C:\SE\Content\Textures\armor_cm.dds", @"C:\SE\nörmal_ng.dds", "x_add.dds", ""),
                DrawTechnique = SeDrawTechnique.Mesh, Flags = 0,
                ColorMetal = @"C:\SE\Content\Textures\armor_cm.dds", NormalGloss = @"C:\SE\nörmal_ng.dds", AddMaps = "x_add.dds", AlphaMask = "",
            };
            byte[] md = ModelCodec.EncodeMaterialDef(def);
            Assert.AreEqual(C3Outcome.MaterialDefined, Feed(asm, C3Type.MaterialDef, md), asm.LastError);
            MaterialDefData got = asm.CompletedMaterial;
            Assert.AreEqual(def.MaterialKey, got.Key);
            Assert.AreEqual(def.ColorMetal, got.ColorMetal); Assert.AreEqual(def.NormalGloss, got.NormalGloss);
            Assert.AreEqual(def.AddMaps, got.AddMaps); Assert.AreEqual(def.AlphaMask, got.AlphaMask);
        }

        // ------------------------------------------------------------------------------------- mesh

        [TestMethod]
        public void S3Cross_SnMeshBuilder_EqualsWireUnityMeshBuilder_TriangleForTriangle()
        {
            ModelGeometry g = Cube(0x5E5E000000000002UL);
            byte[] payload = ModelCodec.EncodePayload(g);
            ModelBegin begin = ModelCodec.MakeBegin(g, ModelCodec.DefaultChunkBytes);
            ModelData m;
            string err;
            Assert.IsTrue(ModelPayload.TryParse(begin, payload, payload.Length, out m, out err), err);
            PreparedModel pm = MeshBuilder.Prepare(m, true, true);
            WindingStats ws = MeshConventions.MeasureWinding(g);
            Assert.AreEqual(ws.Fraction, pm.RawAgreement, 1e-12, "one C3 measure");
            Assert.AreEqual(ws.Degenerate, pm.Degenerate);
            Assert.AreEqual(ws.ShouldFlip, pm.Flipped);

            float[] rows = PartRows(37.0, new Vec3(0.3, 1.0, -0.4), new Vec3(2.5, -1.25, 5.0));
            const float offU = 0.25f, offV = 0.5f;
            var part = new PartRecord { EntityId = 1, PartKey = 7, ModelKey = g.ModelKey, OffU = offU, OffV = offV, H = 0.1f, S = 0.2f, V = 0.3f };
            Array.Copy(rows, part.Local, 12);
            var input = new CellBuildInput { GridId = 1, Cell = new CellKey(0, 0, 0), Options = new BuildOptions() };
            input.Parts.Add(new PartBuild { Part = part, Model = pm });
            CellBuildResult sn = MeshBuilder.BuildCell(input);

            var wb = new UnityMeshBuilder();
            wb.AddPart(g, SeMatrix.FromRows(rows), offU, offV, ws.ShouldFlip);
            UnityMeshData wire = wb.Build();

            // triangles (with their winding) and per-corner attributes, as multisets keyed by position
            var wireTris = new Dictionary<string, int>();
            var wireAttr = new Dictionary<string, string>();
            for (int t = 0; t < wire.Triangles.Length; t += 3)
            {
                Vec3[] p = new Vec3[3];
                for (int k = 0; k < 3; k++)
                {
                    int i = wire.Triangles[t + k];
                    p[k] = new Vec3(wire.Positions[3 * i], wire.Positions[3 * i + 1], wire.Positions[3 * i + 2]);
                    wireAttr[PKey(p[k]) + "/" + Uv(wire.Uv[2 * i], wire.Uv[2 * i + 1])] =
                        Dir(wire.Normals[3 * i], wire.Normals[3 * i + 1], wire.Normals[3 * i + 2]) + " " +
                        Dir(wire.Tangents[4 * i], wire.Tangents[4 * i + 1], wire.Tangents[4 * i + 2]) + " w" + wire.Tangents[4 * i + 3];
                }
                string key = TriKey(p[0], p[1], p[2]);
                wireTris[key] = (wireTris.ContainsKey(key) ? wireTris[key] : 0) + 1;
            }
            int snTris = 0, attrsChecked = 0;
            foreach (ChunkMeshData c in sn.Chunks)
            {
                for (int t = 0; t < c.IndexCount; t += 3)
                {
                    Vec3[] p = new Vec3[3];
                    for (int k = 0; k < 3; k++)
                    {
                        MirrorVertex v = c.Vertices[c.Use32 ? c.Indices32[t + k] : c.Indices16[t + k]];
                        p[k] = new Vec3(v.Px, v.Py, v.Pz);
                        string ak = PKey(p[k]) + "/" + Uv(v.U, v.V);
                        Assert.IsTrue(wireAttr.ContainsKey(ak), "SN vertex " + ak + " (position + uv/patternScale + offset) is a Wire vertex");
                        Assert.AreEqual(wireAttr[ak], Dir(v.Nx, v.Ny, v.Nz) + " " + Dir(v.Tx, v.Ty, v.Tz) + " w" + v.Tw, "normal, tangent and w at " + ak);
                        attrsChecked++;
                    }
                    string key = TriKey(p[0], p[1], p[2]);
                    Assert.IsTrue(wireTris.ContainsKey(key) && wireTris[key] > 0, "SN triangle " + key + " drawn with the same winding by Wire");
                    wireTris[key]--;
                    snTris++;
                }
            }
            Assert.AreEqual(wire.Triangles.Length / 3, snTris, "same triangle count");
            Assert.AreEqual(12 * 3, attrsChecked, "every corner of the 12 triangles checked");
            Assert.AreEqual(1.0, wire.Winding.Fraction, 1e-12);
            Assert.AreEqual(1.0, sn.MinAgreement, 1e-12);
            // materials: Wire's submeshes and SN's chunks group the same triangles
            Assert.AreEqual(2, wire.Submeshes.Length);
            Assert.AreEqual(2, sn.Chunks.Count);
        }

        private static string Uv(float u, float v)
        {
            var ci = CultureInfo.InvariantCulture;
            return Math.Round(u, 5).ToString("0.00000", ci) + "," + Math.Round(v, 5).ToString("0.00000", ci);
        }

        private static string Dir(double x, double y, double z)
        {
            var ci = CultureInfo.InvariantCulture;
            return Math.Round(x, 4).ToString("0.0000", ci) + "," + Math.Round(y, 4).ToString("0.0000", ci) + "," + Math.Round(z, 4).ToString("0.0000", ci);
        }

        [TestMethod]
        public void S3Cross_WindingRule_SnPrepareEqualsWireMeasure_DegenerateSliverAndReversed()
        {
            // 5 triangles in SE space: 2 agreeing, 1 reversed, 1 zero-area, 1 whose normals are at ~88° to its face (a
 // sliver the census rule leaves out: |cos| < 0.05)
            ModelGeometry g = ModelGeometry.Allocate(15, 15, 1, ModelFlags.HasTangents);
            g.ModelKey = 0x5E5E000000000003UL;
            Vec3 up = new Vec3(0, 0, 1);
            Vec3 tilted = new Vec3(Math.Cos(Math.PI / 180 * 88), 0, Math.Sin(Math.PI / 180 * 88));
            Vec3 side = new Vec3(Math.Sin(Math.PI / 180 * 88), 0, Math.Cos(Math.PI / 180 * 88)); // ~88° from +z
            Vec3[][] tri =
            {
                new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) },      // SE Cross = +z: agrees
                new[] { new Vec3(2, 0, 0), new Vec3(3, 0, 0), new Vec3(2, 1, 0) },      // agrees
                new[] { new Vec3(4, 0, 0), new Vec3(4, 1, 0), new Vec3(5, 0, 0) },      // reversed
                new[] { new Vec3(6, 0, 0), new Vec3(6, 0, 0), new Vec3(6, 0, 0) },      // zero area
                new[] { new Vec3(8, 0, 0), new Vec3(9, 0, 0), new Vec3(8, 1, 0) },      // normals nearly in-plane
            };
            for (int t = 0; t < 5; t++)
            {
                Vec3 n = t == 4 ? side : up;
                for (int k = 0; k < 3; k++)
                {
                    int i = 3 * t + k;
                    Set(g.Positions, i, tri[t][k]); Set(g.Normals, i, n); Set(g.Tangents, i, new Vec3(1, 0, 0)); Set(g.Binormals, i, Vec3.Cross(n, new Vec3(1, 0, 0)));
                    g.Indices[i] = (uint)i;
                }
            }
            g.Submeshes[0] = new SubmeshRange(MatA, 0, 5, SeDrawTechnique.Mesh);
            g.ComputeBounds();
            WindingStats ws = MeshConventions.MeasureWinding(g);
            Assert.AreEqual(new WindingStats(2, 1, 2), ws, "census rule: zero area and the in-plane-normal sliver excluded");

            byte[] payload = ModelCodec.EncodePayload(g);
            ModelData m;
            string err;
            Assert.IsTrue(ModelPayload.TryParse(ModelCodec.MakeBegin(g, ModelCodec.DefaultChunkBytes), payload, payload.Length, out m, out err), err);
            PreparedModel pm = MeshBuilder.Prepare(m, true, true);
            Assert.AreEqual(ws.Fraction, pm.RawAgreement, 1e-12, "the SN plugin's S3.C3 number is Wire's");
            Assert.AreEqual(ws.Degenerate, pm.Degenerate);
            Assert.IsFalse(pm.Flipped);
            GC.KeepAlive(tilted);
        }

        // ------------------------------------------------------------------------------------- pose

        [TestMethod]
        public void S3Cross_FrameEntityHierarchy_EqualsPoseMathApply()
        {
            var rnd = new Random(53);
            for (int n = 0; n < 200; n++)
            {
                Quat q = MirrorPoseMath.AngleAxis(rnd.NextDouble() * 360, new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5 + 1e-3, rnd.NextDouble() - 0.5));
                double s = 0.99 + rnd.NextDouble() * 0.07;
                double k = n % 4 == 0 ? 1.5 : 1.0;
                var o = new Vec3(rnd.NextDouble() * 3000 - 1500, -rnd.NextDouble() * 1500, rnd.NextDouble() * 3000 - 1500);
                var p = new EntityPose { Id = 1, Ox = o.X, Oy = o.Y, Oz = o.Z, Qx = q.X, Qy = q.Y, Qz = q.Z, Qw = q.W, S = s, K = k };
                var wp = new WireEntityPose(o, q, s, k);
                double[] mtx = MirrorPoseMath.HierarchyMatrix(p);
                for (int i = 0; i < 4; i++)
                {
                    var l = new Vec3(rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 20 - 10);
                    Vec3 shared = PoseMath.Apply(wp, l);
                    Assert.IsTrue((shared - MirrorPoseMath.ApplySeLocal(p, l)).Length < 1e-9, "SN ApplySeLocal = PoseMath.Apply");
                    Assert.IsTrue((shared - MirrorPoseMath.Transform(mtx, MeshConventions.Mirror(l))).Length < 1e-9, "Frame/Entity matrix on Mirror(l)");
                }
            }
        }
    }
}
