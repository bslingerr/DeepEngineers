using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>Models for the S3 tests (C# twins of wire_ref.py cube_model; the WireMath tests compare them byte for byte).</summary>
    internal static class S3TestModels
    {
        /// <summary>
 /// The procedural box of wire_ref.cube_model: half extent h, 4 vertices per face (u along a1, v along a2,
 /// uv = ((i + 1)/2, (j + 1)/2)), tangent = a1, binormal = a2, triangles in the MyModel.Triangles convention
 /// (cross(V1 − V0, V2 − V0) outward), one submesh.
 /// </summary>
        public static ModelGeometry Cube(float h = 1.25f, ulong materialKey = 0, uint technique = 0, float patternScale = 1f,
                                         string path = "de/s3/cube_2.5m.mwm")
        {
            ModelGeometry g = ModelGeometry.Allocate(24, 36, 1, ModelFlags.HasTangents);
            g.ModelKey = ModelCodec.ModelKey(path, 0);
            g.PatternScale = patternScale;
            int v = 0, t = 0;
            int[,] ij = { { -1, -1 }, { 1, -1 }, { 1, 1 }, { -1, 1 } };
            for (int axis = 0; axis < 3; axis++)
            {
                for (int si = 0; si < 2; si++)
                {
                    float sign = si == 0 ? -1f : 1f;
                    var n = new float[3];
                    var a1 = new float[3];
                    var a2 = new float[3];
                    n[axis] = sign;
                    a1[(axis + 1) % 3] = 1f;
                    a2[(axis + 2) % 3] = 1f;
                    int b = v;
                    for (int c = 0; c < 4; c++)
                    {
                        int i = ij[c, 0], j = ij[c, 1];
                        for (int k = 0; k < 3; k++)
                        {
                            g.Positions[3 * v + k] = h * (n[k] + i * a1[k] + j * a2[k]);
                            g.Normals[3 * v + k] = n[k];
                            g.Tangents[3 * v + k] = a1[k];
                            g.Binormals[3 * v + k] = a2[k];
                        }
                        g.Uvs[2 * v] = (i + 1) / 2f;
                        g.Uvs[2 * v + 1] = (j + 1) / 2f;
                        v++;
                    }
                    int[,] tris = { { 0, 1, 2 }, { 0, 2, 3 } };
                    for (int q = 0; q < 2; q++)
                    {
                        int i0 = b + tris[q, 0], i1 = b + tris[q, 1], i2 = b + tris[q, 2];
                        Vec3 pa = g.Position(i0), pb = g.Position(i1), pc = g.Position(i2);
                        if (Vec3.Dot(Vec3.Cross(pb - pa, pc - pa), new Vec3(n[0], n[1], n[2])) < 0)
                        {
                            int x = i1; i1 = i2; i2 = x;
                        }
                        g.Indices[t++] = (uint)i0;
                        g.Indices[t++] = (uint)i1;
                        g.Indices[t++] = (uint)i2;
                    }
                }
            }
            g.Submeshes[0] = new SubmeshRange(materialKey, 0, 12, technique);
            g.ComputeBounds();
            return g;
        }

        /// <summary>A random model with every float bit pattern allowed in vertex data (bit-exactness of the codec).</summary>
        public static ModelGeometry Random(Random rnd, int vertices, int triangles, int submeshes, bool skinned)
        {
            ModelGeometry g = ModelGeometry.Allocate(vertices, 3 * triangles, submeshes,
                                                     ModelFlags.HasTangents | (skinned ? ModelFlags.Skinned : 0u));
            g.ModelKey = ((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next();
            g.Lod = (uint)rnd.Next(0, 4);
            g.PatternScale = rnd.Next(2) == 0 ? 1f : 4f;
            var bits = new byte[4];
            Action<float[]> fill = a =>
            {
                for (int i = 0; i < a.Length; i++)
                {
                    rnd.NextBytes(bits);
                    a[i] = BitConverter.ToSingle(bits, 0);   // any pattern: NaNs, denormals, -0
                }
            };
            fill(g.Positions);
            fill(g.Normals);
            fill(g.Tangents);
            fill(g.Binormals);
            fill(g.Uvs);
            if (skinned)
            {
                rnd.NextBytes(g.BoneIndices);
                fill(g.BoneWeights);
            }
            for (int i = 0; i < g.Indices.Length; i++) g.Indices[i] = (uint)rnd.Next(0, Math.Max(1, vertices));
            for (int s = 0; s < submeshes; s++)
            {
                uint start = (uint)rnd.Next(0, triangles + 1);
                uint count = (uint)rnd.Next(0, triangles - (int)start + 1);
                g.Submeshes[s] = new SubmeshRange(((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next(), start, count, (uint)rnd.Next(0, 22));
            }
            for (int i = 0; i < 3; i++)
            {
                g.Bounds[i] = -(float)rnd.NextDouble() * 10f;
                g.Bounds[i + 3] = (float)rnd.NextDouble() * 10f;
            }
            return g;
        }

        public static void AssertSameBits(ModelGeometry a, ModelGeometry b)
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(a.ModelKey, b.ModelKey, "modelKey");
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(a.Lod, b.Lod, "lod");
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(a.Flags, b.Flags, "flags");
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(a.VertexCount, b.VertexCount, "vertexCount");
            Bits(a.Bounds, b.Bounds, "bounds");
            Bits(new[] { a.PatternScale }, new[] { b.PatternScale }, "patternScale");
            Bits(a.Positions, b.Positions, "positions");
            Bits(a.Normals, b.Normals, "normals");
            Bits(a.Tangents, b.Tangents, "tangents");
            Bits(a.Binormals, b.Binormals, "binormals");
            Bits(a.Uvs, b.Uvs, "uvs");
            if (a.Skinned)
            {
                Microsoft.VisualStudio.TestTools.UnitTesting.CollectionAssert.AreEqual(a.BoneIndices, b.BoneIndices, "boneIndices");
                Bits(a.BoneWeights, b.BoneWeights, "boneWeights");
            }
            Microsoft.VisualStudio.TestTools.UnitTesting.CollectionAssert.AreEqual(a.Indices, b.Indices, "indices");
            Microsoft.VisualStudio.TestTools.UnitTesting.CollectionAssert.AreEqual(a.Submeshes, b.Submeshes, "submeshes");
        }

        public static void Bits(float[] a, float[] b, string what)
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(a.Length, b.Length, what + " length");
            for (int i = 0; i < a.Length; i++)
            {
                int x = BitConverter.ToInt32(BitConverter.GetBytes(a[i]), 0), y = BitConverter.ToInt32(BitConverter.GetBytes(b[i]), 0);
                if (x != y) Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Fail(what + "[" + i + "] bits 0x" + x.ToString("x8") + " != 0x" + y.ToString("x8"));
            }
        }

        /// <summary>A rigid SE matrix: rotation by angle about a unit axis (right-hand rule, VRage Rodrigues), then translation.</summary>
        public static SeMatrix Rigid(Vec3 axis, double angle, Vec3 translation)
        {
            Vec3 a = axis.Normalized();
            Func<Vec3, Vec3> rot = v => v * Math.Cos(angle) + Vec3.Cross(a, v) * Math.Sin(angle) + a * (Vec3.Dot(a, v) * (1 - Math.Cos(angle)));
            return new SeMatrix(rot(Vec3.UnitX), rot(Vec3.UnitY), rot(Vec3.UnitZ), translation);
        }
    }

    /// <summary>
 /// S3 record codecs (CONTRACT §5.4 C3/C4 over the frozen layout 1.0): model payload round trips bit for bit, chunking,
 /// the encoder through a real byte ring into the assembler, MaterialDef/SkinnedBind/PartAdd and the other C4 records,
 /// and every rejection the consumer relies on.
 /// </summary>
    [TestClass]
    public unsafe class S3CodecTests
    {
        private static ModelGeometry Decode(ModelEncoder enc)
        {
            ModelGeometry g;
            string err;
            Assert.IsTrue(ModelCodec.TryDecodePayload(enc.Begin, enc.Payload, 0, enc.Payload.Length, out g, out err), err);
            return g;
        }

        [TestMethod]
        public void Payload_Cube_RoundTripBitExact_AndSizes()
        {
            ModelGeometry cube = S3TestModels.Cube();
            string err;
            Assert.IsTrue(cube.Validate(out err), err);
            byte[] p = ModelCodec.EncodePayload(cube);
            Assert.AreEqual(24 * 56 + 36 * 4 + 24, p.Length, "vertices 56 B, u32 indices, 24 B submeshes");
            Assert.AreEqual(ModelCodec.PayloadBytes(cube), (long)p.Length);
            var enc = new ModelEncoder(cube, 256);
            S3TestModels.AssertSameBits(cube, Decode(enc));
            // the vertex layout at the CONTRACT offsets: pos 0x00, normal 0x0C, tangent 0x18, binormal 0x24, uv 0x30
            fixed (byte* b = p)
            {
                float* v1 = (float*)(b + 56);
                Assert.AreEqual(cube.Positions[3], v1[0]);
                Assert.AreEqual(cube.Normals[3 + 2], *(float*)(b + 56 + 0x0C + 8));
                Assert.AreEqual(cube.Tangents[3 + 1], *(float*)(b + 56 + 0x18 + 4));
                Assert.AreEqual(cube.Binormals[3 + 2], *(float*)(b + 56 + 0x24 + 8));
                Assert.AreEqual(cube.Uvs[2 + 1], *(float*)(b + 56 + 0x30 + 4));
                uint* ix = (uint*)(b + 24 * 56);
                Assert.AreEqual(cube.Indices[5], ix[5]);
                byte* sm = b + 24 * 56 + 36 * 4;
                Assert.AreEqual(cube.Submeshes[0].MaterialKey, *(ulong*)sm);
                Assert.AreEqual(12u, *(uint*)(sm + 12), "triCount");
                Assert.AreEqual(0u, *(uint*)(sm + 20), "pad");
            }
        }

        [TestMethod]
        public void Payload_RandomBitsAndSkinned_RoundTripBitExact()
        {
            var rnd = new Random(30030001);
            for (int round = 0; round < 40; round++)
            {
                bool skinned = round % 2 == 1;
                ModelGeometry g = S3TestModels.Random(rnd, rnd.Next(1, 300), rnd.Next(0, 200), rnd.Next(0, 5), skinned);
                var enc = new ModelEncoder(g, 1 + rnd.Next(4096));
                Assert.AreEqual((long)ModelCodec.PayloadBytes(g), enc.TotalBytes);
                Assert.AreEqual(skinned ? 76 : 56, ModelCodec.StrideFor(g.Flags));
                S3TestModels.AssertSameBits(g, Decode(enc));
            }
        }

        [TestMethod]
        public void Encoder_ChunksCoverThePayloadInOrder_AndEndCarriesTheHash()
        {
            var rnd = new Random(30030002);
            ModelGeometry g = S3TestModels.Random(rnd, 500, 400, 3, false);
            const int chunk = 3000;
            var enc = new ModelEncoder(g, chunk);
            long total = enc.TotalBytes;
            Assert.AreEqual((int)((total + chunk - 1) / chunk), enc.ChunkCount);
            Assert.AreEqual(enc.ChunkCount + 2, enc.MessageCount);
            Assert.AreEqual(C3Type.ModelBegin, enc.MessageType(0));
            Assert.AreEqual(C3Type.ModelEnd, enc.MessageType(enc.MessageCount - 1));
            var all = new List<byte>();
            for (int i = 1; i <= enc.ChunkCount; i++)
            {
                Assert.AreEqual(C3Type.ModelChunk, enc.MessageType(i));
                byte[] m = enc.MessagePayload(i);
                ModelChunkHeader h;
                Assert.IsTrue(ModelCodec.TryRead(m, 0, 16, out h));
                Assert.AreEqual(g.ModelKey, h.modelKey);
                Assert.AreEqual((uint)(i - 1), h.chunkIndex);
                Assert.AreEqual((uint)(m.Length - 16), h.bytes);
                Assert.IsTrue(h.bytes <= chunk && h.bytes > 0);
                Assert.AreEqual(m.Length, enc.MessagePayloadBytes(i));
                for (int k = 16; k < m.Length; k++) all.Add(m[k]);
            }
            CollectionAssert.AreEqual(enc.Payload, all.ToArray(), "chunks concatenate to the payload");
            ModelEnd e;
            Assert.IsTrue(ModelCodec.TryRead(enc.MessagePayload(enc.MessageCount - 1), 0, 16, out e));
            Assert.AreEqual(LayoutHash.Fnv1a64(enc.Payload), e.payloadHash, "FNV-1a 64 over the concatenated chunk payloads");
            ModelBegin b;
            Assert.IsTrue(ModelCodec.TryRead(enc.MessagePayload(0), 0, 64, out b));
            Assert.AreEqual((uint)total, b.totalBytes);
            Assert.AreEqual((uint)enc.ChunkCount, b.chunkCount);
            Assert.AreEqual(g.PatternScale, b.patternScale);
            Assert.AreEqual(g.Bounds[3], b.bounds[3], "bounds: min xyz then max xyz");
        }

        [TestMethod]
        public void Encoder_ThroughARing_ToTheAssembler_WithMaterialsInBetween()
        {
            var rnd = new Random(30030003);
            var models = new List<ModelGeometry> { S3TestModels.Cube(), S3TestModels.Random(rnd, 900, 700, 2, false), S3TestModels.Random(rnd, 300, 100, 1, true),
                                                   ModelGeometry.Allocate(0, 0, 0, 0) };
            models[3].ModelKey = 42;
            var mat = new MaterialDefinition { DrawTechnique = SeDrawTechnique.Mesh, ColorMetal = @"C:\SE\large_edges_cm.dds", NormalGloss = @"C:\SE\large_edges_ng.dds" };
            mat.MaterialKey = mat.ComputeKey();
            byte[] matBytes = ModelCodec.EncodeMaterialDef(mat);
            using (var mem = new TestRingMemory(64 * 1024))
            {
                ByteRing ring = mem.NewByteRing();
                var asm = new C3ModelAssembler();
                var got = new List<ModelGeometry>();
                int materials = 0;
                foreach (ModelGeometry g in models)
                {
                    var enc = new ModelEncoder(g, 5000);
                    int fulls = 0;
                    bool matSent = false;
                    while (!enc.Done)
                    {
                        RingWriteStatus st = enc.TryWriteNext(ring);
                        if (st == RingWriteStatus.Ok && !matSent && enc.Next == 2)
                        {
                            // a MaterialDef between two chunks of a model does not disturb it
                            Assert.AreEqual(RingWriteStatus.Ok, ring.TryWrite(C3Type.MaterialDef, matBytes, 0, matBytes.Length));
                            matSent = true;
                        }
                        if (st == RingWriteStatus.Full)
                        {
                            fulls++;
                            Drain(ring, asm, got, ref materials);
                        }
                        else Assert.AreEqual(RingWriteStatus.Ok, st);
                    }
                    Drain(ring, asm, got, ref materials);
                    Assert.IsTrue(fulls < 1000);
                }
                Assert.AreEqual(models.Count, got.Count);
                for (int i = 0; i < models.Count; i++) S3TestModels.AssertSameBits(models[i], got[i]);
                Assert.IsTrue(materials >= 1);
                Assert.AreEqual(0L, asm.Errors, asm.LastError);
                Assert.AreEqual(mat.ColorMetal, asm.LastMaterial.ColorMetal);
                Assert.AreEqual(mat.MaterialKey, asm.LastMaterial.MaterialKey);
            }
        }

        private static void Drain(ByteRing ring, C3ModelAssembler asm, List<ModelGeometry> got, ref int materials)
        {
            uint t;
            byte* p;
            int n;
            while (ring.TryPeek(out t, out p, out n) == RingReadStatus.Ok)
            {
                C3Event ev = asm.Feed(t, p, n);
                ring.Advance();
                Assert.AreNotEqual(C3Event.Error, ev, asm.LastError);
                if (ev == C3Event.Material) materials++;
                if (ev == C3Event.ModelComplete)
                {
                    AssembledModel m = asm.TakeModel();
                    Assert.IsTrue(m.HashMatches());
                    ModelGeometry g;
                    string err;
                    Assert.IsTrue(m.TryDecode(out g, out err), err);
                    got.Add(g);
                }
            }
        }

        [TestMethod]
        public void Encoder_ChunkLargerThanHalfTheRing_IsTooLarge_AndNotAdvanced()
        {
            ModelGeometry g = S3TestModels.Cube();
            var enc = new ModelEncoder(g, 1000);
            using (var mem = new TestRingMemory(1024))
            {
                ByteRing ring = mem.NewByteRing();
                Assert.AreEqual(RingWriteStatus.Ok, enc.TryWriteNext(ring), "ModelBegin fits");
                Assert.AreEqual(RingWriteStatus.TooLarge, enc.TryWriteNext(ring), "a 1000 B chunk is above half of 1 KiB");
                Assert.AreEqual(1, enc.Next);
            }
            Assert.AreEqual(ModelCodec.ChunkCount(0, 7), 0);
            Assert.AreEqual(ModelCodec.ChunkCount(7, 7), 1);
            Assert.AreEqual(ModelCodec.ChunkCount(8, 7), 2);
        }

        private static C3Event Feed(C3ModelAssembler a, ModelEncoder e, int i)
        {
            byte[] p = e.MessagePayload(i);
            return a.Feed(e.MessageType(i), p, 0, p.Length);
        }

        [TestMethod]
        public void Assembler_RejectsWhatNoCorrectProducerSends()
        {
            var rnd = new Random(30030004);
            ModelGeometry g = S3TestModels.Random(rnd, 100, 50, 1, false);
            var e = new ModelEncoder(g, 700);
            Assert.IsTrue(e.ChunkCount >= 3);
            var a = new C3ModelAssembler();
            Assert.AreEqual(C3Event.Error, Feed(a, e, 1), "chunk without ModelBegin");
            Assert.AreEqual(C3Event.Progress, Feed(a, e, 0));
            Assert.AreEqual(C3Event.Error, Feed(a, e, 2), "chunk 1 before chunk 0");
            Assert.IsFalse(a.InModel, "an error drops the open model");
            Assert.AreEqual(C3Event.Progress, Feed(a, e, 0));
            Assert.AreEqual(C3Event.Progress, Feed(a, e, 1));
            Assert.AreEqual(C3Event.Error, Feed(a, e, e.MessageCount - 1), "ModelEnd with chunks missing");
            // wrong key inside a model
            Assert.AreEqual(C3Event.Progress, Feed(a, e, 0));
            byte[] c0 = e.MessagePayload(1);
            c0[0] ^= 1;
            Assert.AreEqual(C3Event.Error, a.Feed(C3Type.ModelChunk, c0, 0, c0.Length));
            // a bytes field that disagrees with the payload size
            Assert.AreEqual(C3Event.Progress, Feed(a, e, 0));
            byte[] c1 = e.MessagePayload(1);
            c1[12] ^= 1;
            Assert.AreEqual(C3Event.Error, a.Feed(C3Type.ModelChunk, c1, 0, c1.Length));
            // ModelBegin whose totalBytes disagrees with its counts
            byte[] b = e.MessagePayload(0);
            b[0x3C] ^= 8;
            Assert.AreEqual(C3Event.Error, a.Feed(C3Type.ModelBegin, b, 0, b.Length));
            // ModelBegin of the wrong size, unknown type
            Assert.AreEqual(C3Event.Error, a.Feed(C3Type.ModelBegin, new byte[60], 0, 60));
            Assert.AreEqual(C3Event.Error, a.Feed(77, new byte[8], 0, 8));
            // a corrupted payload byte passes the assembler (no hashing on the main thread) and fails the worker's hash check
            Assert.AreEqual(C3Event.Progress, Feed(a, e, 0));
            for (int i = 1; i <= e.ChunkCount; i++)
            {
                byte[] c = e.MessagePayload(i);
                if (i == 2) c[40] ^= 0x10;
                Assert.AreEqual(C3Event.Progress, a.Feed(C3Type.ModelChunk, c, 0, c.Length));
            }
            Assert.AreEqual(C3Event.ModelComplete, Feed(a, e, e.MessageCount - 1));
            AssembledModel m = a.TakeModel();
            Assert.IsNull(a.TakeModel(), "TakeModel hands a model once");
            Assert.IsFalse(m.HashMatches());
            ModelGeometry bad;
            string err;
            Assert.IsFalse(m.TryDecode(out bad, out err));
            StringAssert.Contains(err, "hash");
            Assert.IsTrue(a.Errors >= 7);
        }

        [TestMethod]
        public void Assembler_ResyncOrphans_AndAbandonedModels()
        {
            var rnd = new Random(30030005);
            var e1 = new ModelEncoder(S3TestModels.Random(rnd, 80, 40, 1, false), 500);
            var e2 = new ModelEncoder(S3TestModels.Cube(), 500);
            var a = new C3ModelAssembler();
            Assert.AreEqual(C3Event.Progress, Feed(a, e1, 0));
            Assert.AreEqual(C3Event.Progress, Feed(a, e1, 1));
            a.Resync();
            Assert.AreEqual(C3Event.Orphan, Feed(a, e1, 2), "chunks of the dropped model after a re-sync are orphans");
            Assert.AreEqual(C3Event.Orphan, Feed(a, e1, e1.MessageCount - 1));
            Assert.AreEqual(0L, a.Errors);
            Assert.AreEqual(2L, a.Orphans);
            Assert.AreEqual(C3Event.Progress, Feed(a, e1, 0));
            Assert.AreEqual(C3Event.Progress, Feed(a, e2, 0), "a new ModelBegin abandons the open model");
            Assert.AreEqual(1L, a.Abandoned);
            for (int i = 1; i < e2.MessageCount - 1; i++) Assert.AreEqual(C3Event.Progress, Feed(a, e2, i));
            Assert.AreEqual(C3Event.ModelComplete, Feed(a, e2, e2.MessageCount - 1));
            ModelGeometry g;
            string err;
            Assert.IsTrue(a.TakeModel().TryDecode(out g, out err), err);
            S3TestModels.AssertSameBits(S3TestModels.Cube(), g);
            Assert.AreEqual(C3Event.Error, Feed(a, e1, 1), "once synced again, a stray chunk is an error");
        }

        [TestMethod]
        public void DecodeValidation_IndexRange_SubmeshRange_Pad()
        {
            ModelGeometry cube = S3TestModels.Cube();
            var enc = new ModelEncoder(cube, 4096);
            byte[] p = (byte[])enc.Payload.Clone();
            ModelGeometry g;
            string err;
            // index 24 (= vertexCount) in the first triangle
            int ix0 = 24 * 56;
            byte[] q = (byte[])p.Clone();
            q[ix0] = 24;
            Assert.IsFalse(ModelCodec.TryDecodePayload(enc.Begin, q, 0, q.Length, out g, out err));
            StringAssert.Contains(err, "vertexCount");
            // submesh triCount 13 > 12 triangles
            q = (byte[])p.Clone();
            q[24 * 56 + 36 * 4 + 12] = 13;
            Assert.IsFalse(ModelCodec.TryDecodePayload(enc.Begin, q, 0, q.Length, out g, out err));
            StringAssert.Contains(err, "beyond");
            // submesh pad not zero
            q = (byte[])p.Clone();
            q[24 * 56 + 36 * 4 + 20] = 1;
            Assert.IsFalse(ModelCodec.TryDecodePayload(enc.Begin, q, 0, q.Length, out g, out err));
            // payload shorter than totalBytes
            Assert.IsFalse(ModelCodec.TryDecodePayload(enc.Begin, p, 0, p.Length - 1, out g, out err));
            // geometry that cannot be encoded
            ModelGeometry broken = S3TestModels.Cube();
            broken.Indices[0] = 99;
            Assert.IsFalse(broken.Validate(out err));
            try { ModelCodec.EncodePayload(broken); Assert.Fail("an invalid model must not encode"); }
            catch (ArgumentException) { }
            broken = S3TestModels.Cube();
            broken.PatternScale = 0f;
            Assert.IsFalse(broken.Validate(out err));
            broken = S3TestModels.Cube();
            broken.Flags |= 8;
            Assert.IsFalse(broken.Validate(out err));
        }

        [TestMethod]
        public void MaterialDef_RoundTrip_Utf8_AndRejections()
        {
            var m = new MaterialDefinition
            {
                DrawTechnique = SeDrawTechnique.Glass,
                Flags = MaterialFlags.GlassCw | MaterialFlags.AlphaMasked,
                ColorMetal = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Textures\Models\Cubes\large_edges_cm.dds",
                NormalGloss = @"D:\Mods\Fenêtre\vitre_ng.dds",
                AddMaps = "",
                AlphaMask = @"E:\日本\mask.dds",
            };
            m.MaterialKey = m.ComputeKey();
            byte[] p = ModelCodec.EncodeMaterialDef(m);
            Assert.AreEqual(m.MaterialKey, BitConverter.ToUInt64(p, 0));
            Assert.AreEqual(SeDrawTechnique.Glass, BitConverter.ToUInt32(p, 8));
            Assert.AreEqual(m.Flags, BitConverter.ToUInt32(p, 12));
            Assert.AreEqual((ushort)System.Text.Encoding.UTF8.GetByteCount(m.ColorMetal), BitConverter.ToUInt16(p, 16), "u16 length prefix at 0x10");
            MaterialDefinition d;
            string err;
            Assert.IsTrue(ModelCodec.TryDecodeMaterialDef(p, 0, p.Length, out d, out err), err);
            Assert.AreEqual(m.MaterialKey, d.MaterialKey);
            Assert.AreEqual(m.DrawTechnique, d.DrawTechnique);
            Assert.AreEqual(m.Flags, d.Flags);
            Assert.AreEqual(m.ColorMetal, d.ColorMetal);
            Assert.AreEqual(m.NormalGloss, d.NormalGloss);
            Assert.AreEqual("", d.AddMaps);
            Assert.AreEqual(m.AlphaMask, d.AlphaMask);
            CollectionAssert.AreEqual(p, ModelCodec.EncodeMaterialDef(d), "re-encoding is byte identical");
            Assert.IsFalse(ModelCodec.TryDecodeMaterialDef(p, 0, p.Length - 1, out d, out err), "truncated");
            var longer = new byte[p.Length + 1];
            Buffer.BlockCopy(p, 0, longer, 0, p.Length);
            Assert.IsFalse(ModelCodec.TryDecodeMaterialDef(longer, 0, longer.Length, out d, out err), "trailing bytes");
            byte[] badUtf8 = (byte[])p.Clone();
            badUtf8[18] = 0xFF;
            Assert.IsFalse(ModelCodec.TryDecodeMaterialDef(badUtf8, 0, badUtf8.Length, out d, out err), "not UTF-8");
            byte[] badFlags = (byte[])p.Clone();
            badFlags[12] |= 0x80;
            Assert.IsFalse(ModelCodec.TryDecodeMaterialDef(badFlags, 0, badFlags.Length, out d, out err), "unknown flag");
            var empty = new MaterialDefinition { MaterialKey = 7 };
            byte[] e = ModelCodec.EncodeMaterialDef(empty);
            Assert.AreEqual(16 + 8, e.Length, "four empty strings: four u16 zero lengths");
            Assert.IsTrue(ModelCodec.TryDecodeMaterialDef(e, 0, e.Length, out d, out err), err);
        }

        [TestMethod]
        public void SkinnedBind_RoundTrip()
        {
            var s = new SkinnedBindDefinition { ModelKey = 0x1234567890abcdefUL, BoneCount = 81, Rows = new float[81 * 12] };
            for (int i = 0; i < s.Rows.Length; i++) s.Rows[i] = i * 0.25f - 3f;
            byte[] p = ModelCodec.EncodeSkinnedBind(s);
            Assert.AreEqual(16 + 81 * 48, p.Length);
            var a = new C3ModelAssembler();
            Assert.AreEqual(C3Event.SkinnedBind, a.Feed(C3Type.SkinnedBind, p, 0, p.Length));
            Assert.AreEqual(81, a.LastSkinnedBind.BoneCount);
            S3TestModels.Bits(s.Rows, a.LastSkinnedBind.Rows, "bind rows");
            Assert.AreEqual(C3Event.Error, a.Feed(C3Type.SkinnedBind, p, 0, p.Length - 4));
        }

        [TestMethod]
        public void C4Records_PartAdd_FieldOffsetsAndRoundTrip()
        {
            SeMatrix local = S3TestModels.Rigid(new Vec3(0.2, 1, -0.4), 0.7, new Vec3(2.5, -5, 7.5));
            float u, v;
            MeshConventions.ArmorPatternOffset(1, 2, 3, 4, out u, out v);
            PartAdd pa = ModelCodec.MakePartAdd(0x0102030405060708UL, ModelCodec.CubePartKey(-1, 2, -3, 1), 0xAABBCCDDEEFF0011UL, local,
                                                new Vec3(0.5, -0.25, 0.75), u, v, 9, 3);
            byte[] p = ModelCodec.ToBytes(pa);
            Assert.AreEqual(100, p.Length, "PartAdd: 100 B payload");
            Assert.AreEqual(112L, ByteRing.FramedSize(p.Length), "108 B framed, 112 B padded");
            Assert.AreEqual(0x0102030405060708UL, BitConverter.ToUInt64(p, 0x00));
            Assert.AreEqual(ModelCodec.CubePartKey(-1, 2, -3, 1), BitConverter.ToUInt64(p, 0x08));
            Assert.AreEqual(0xAABBCCDDEEFF0011UL, BitConverter.ToUInt64(p, 0x10));
            Assert.AreEqual((float)local.Right.X, BitConverter.ToSingle(p, 0x18), "local[0] = Right.X");
            Assert.AreEqual((float)local.Up.Y, BitConverter.ToSingle(p, 0x18 + 4 * 4), "local[4] = Up.Y");
            Assert.AreEqual((float)local.Backward.Z, BitConverter.ToSingle(p, 0x18 + 8 * 4), "local[8] = Backward.Z");
            Assert.AreEqual(7.5f, BitConverter.ToSingle(p, 0x18 + 11 * 4), "local[11] = Translation.Z");
            Assert.AreEqual(0.5f, BitConverter.ToSingle(p, 0x48));
            Assert.AreEqual(-0.25f, BitConverter.ToSingle(p, 0x4C));
            Assert.AreEqual(0.75f, BitConverter.ToSingle(p, 0x50));
            Assert.AreEqual(1f / 3f, BitConverter.ToSingle(p, 0x54), "uvOffset u = offsetX / patternX");
            Assert.AreEqual(0.5f, BitConverter.ToSingle(p, 0x58), "uvOffset v = offsetY / patternY");
            Assert.AreEqual(9u, BitConverter.ToUInt32(p, 0x5C));
            Assert.AreEqual(3u, BitConverter.ToUInt32(p, 0x60));
            PartAdd back;
            Assert.IsTrue(ModelCodec.TryRead(p, 0, p.Length, out back));
            CollectionAssert.AreEqual(p, ModelCodec.ToBytes(back));
            SeMatrix l2 = ModelCodec.PartLocal(back);
            Assert.IsTrue(Vec3.MaxAbsDiff(l2.Translation, local.Translation) < 1e-6);
            Assert.IsTrue(Vec3.MaxAbsDiff(l2.Up, local.Up) < 1e-7);
            Assert.AreEqual(0.75, ModelCodec.PartColorMaskHsv(back).Z);
            Assert.IsFalse(ModelCodec.TryRead(p, 0, 99, out back), "a PartAdd is exactly 100 B");
        }

        [TestMethod]
        public void C4Records_OtherRecords_SizesAndRoundTrips()
        {
            byte[] r = ModelCodec.ToBytes(ModelCodec.MakePartRemove(5, 6));
            Assert.AreEqual(16, r.Length);
            PartRemove pr;
            Assert.IsTrue(ModelCodec.TryRead(r, 0, 16, out pr));
            Assert.AreEqual(5UL, pr.entityId);
            Assert.AreEqual(6UL, pr.partKey);
            byte[] ga = ModelCodec.ToBytes(ModelCodec.MakeGridAdd(11, GridClass.LargeGrid, 0, 0, 3));
            Assert.AreEqual(32, ga.Length);
            Assert.AreEqual(3u, BitConverter.ToUInt32(ga, 0x18), "worldEpoch at 0x18");
            Assert.AreEqual(GridClass.LargeGrid, BitConverter.ToUInt32(ga, 0x08));
            Assert.AreEqual(8, ModelCodec.ToBytes(ModelCodec.MakeEntityRemove(11)).Length);
            byte[] rc = ModelCodec.ToBytes(ModelCodec.MakePartRecolor(1, 2, new Vec3(0.1, 0.2, 0.3)));
            Assert.AreEqual(32, rc.Length);
            Assert.AreEqual(0.2f, BitConverter.ToSingle(rc, 0x14));
            Assert.AreEqual(0u, BitConverter.ToUInt32(rc, 0x1C), "pad");
            byte[] ca = ModelCodec.ToBytes(ModelCodec.MakeClearAll(4, 9));
            Assert.AreEqual(8, ca.Length);
            Assert.AreEqual(9u, BitConverter.ToUInt32(ca, 4));
            ClearAll c;
            Assert.IsFalse(ModelCodec.TryRead(ca, 0, 7, out c));
            Assert.IsTrue(ModelCodec.TryRead(ca, 0, 8, out c));
            Assert.AreEqual(4u, c.worldEpoch);
            Assert.AreEqual(64, ModelCodec.ToBytes(new ModelBegin()).Length);
            Assert.AreEqual(16, ModelCodec.ToBytes(new ModelEnd()).Length);
            Assert.AreEqual(16, ModelCodec.ToBytes(new ModelChunkHeader()).Length);
        }

        [TestMethod]
        public void Keys_ModelPathNormalisation_LodAndMaterialContent()
        {
            ulong a = ModelCodec.ModelKey(@"Models\Cubes\Large\ArmorCube.mwm", 0);
            Assert.AreEqual(a, ModelCodec.ModelKey("models/cubes/large/armorcube.mwm", 0), "case and separators do not matter");
            Assert.AreEqual(a, ModelCodec.ModelKey("  Models\\Cubes\\Large\\ArmorCube.mwm ", 0));
            Assert.AreNotEqual(a, ModelCodec.ModelKey(@"Models\Cubes\Large\ArmorCube.mwm", 1), "the LOD is part of the key");
            Assert.AreEqual(LayoutHash.Fnv1a64(System.Text.Encoding.UTF8.GetBytes(@"models\cubes\large\armorcube.mwm|lod0")), a);
            Assert.AreEqual(@"models\cubes\small\armorcube.mwm|lod0|scale=0.5", ModelCodec.ModelKeyText(@"Models\Cubes\Small\ArmorCube.mwm", 0, 0.5f));
            Assert.AreEqual(a, ModelCodec.ModelKey(@"Models\Cubes\Large\ArmorCube.mwm", 0, 1f), "scale 1 adds nothing");
            Assert.AreNotEqual(a, ModelCodec.ModelKey(@"Models\Cubes\Large\ArmorCube.mwm", 0, 0.2f), "a rescaled model is another model");
            Assert.AreEqual(@"c:\a_cm.dds|c:\a_ng.dds|||0|4", ModelCodec.MaterialKeyText(0, 4, "C:/A_cm.dds", @"C:\A_ng.dds", null, ""));
            ulong m1 = ModelCodec.MaterialKey(0, 0, @"C:\A_cm.dds", @"C:\A_ng.dds", @"C:\A_add.dds", "");
            Assert.AreEqual(m1, ModelCodec.MaterialKey(0, 0, "c:/a_cm.dds", "C:\\A_NG.dds", "C:\\A_add.dds", null));
            Assert.AreNotEqual(m1, ModelCodec.MaterialKey(SeDrawTechnique.Glass, 0, @"C:\A_cm.dds", @"C:\A_ng.dds", @"C:\A_add.dds", ""));
            Assert.AreNotEqual(m1, ModelCodec.MaterialKey(0, MaterialFlags.AlphaMasked, @"C:\A_cm.dds", @"C:\A_ng.dds", @"C:\A_add.dds", ""));
            Assert.AreNotEqual(m1, ModelCodec.MaterialKey(0, 0, @"C:\A_ng.dds", @"C:\A_cm.dds", @"C:\A_add.dds", ""), "slots are ordered");
            Assert.AreEqual(0x0001FFFF0002FFFFUL, ModelCodec.CubePartKey(-1, 2, -1, 1), "x, y, z as i16, then the part index");
        }
    }
}
