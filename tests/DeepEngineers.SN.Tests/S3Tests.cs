using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S3 Unity-free parts of the Subnautica plugin (ROADMAP S3 SN plugin; CONTRACT §2.3, §2.5, §2.8, §5.4 C2/C3/C4/C7;
 /// ARCHITECTURE §5.4, §5.8, §5.13): the C3 assembler (order, hash, synthetic S0 traffic, re-sync orphans), the model
 /// payload parser, the C4 state and its C7 counters, the C2 pose table and the sampler that follows the camera's
 /// bracket (S3.M4), the mirrored mesh build with the (I0, I2, I1) rewrite and the winding measure (S3.M1, S3.M2),
 /// part matrices composed in SE space, the Frame/Entity math against WireMath.Apply (and the rigid-frame error the
 /// shear test must see under S3.M7), the upload scheduler, the DDS header parser and mip skipping, the texture repack
 /// and the paint rules, the lab plan and the capture list, options and arguments, the worker thread.
 /// </summary>
    [TestClass]
    public class S3Tests
    {
        private const ulong ModelKey = 0x1122334455667788UL;
        private const ulong MatA = 0xA000000000000001UL;
        private const ulong MatB = 0xB000000000000002UL;

        // ------------------------------------------------------------------------------------- fixtures

 /// <summary>
 /// A 2.5 m cube in SE's MyModel.Triangles convention: per face (normal n, tangent t, binormal b = n × t), quad
 /// corners c ± t·h ± b·h, triangles (0, 1, 2), (0, 2, 3) whose SE-space Cross(p1 − p0, p2 − p0) = 4h²·n points
 /// outwards (gap-1 §2.3: Triangles order points outwards; the file order is the other way round).
 /// </summary>
        private static void Cube(float h, out float[] pos, out float[] nrm, out float[] tan, out float[] bin, out float[] uv, out int[] idx)
        {
            var normals = new[] { new Vec3(1, 0, 0), new Vec3(-1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, -1, 0), new Vec3(0, 0, 1), new Vec3(0, 0, -1) };
            var p = new List<float>(); var n = new List<float>(); var t = new List<float>(); var b = new List<float>(); var u = new List<float>(); var ix = new List<int>();
            foreach (Vec3 nn in normals)
            {
                Vec3 tt = Math.Abs(nn.Y) > 0.5 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
                Vec3 bb = Vec3.Cross(nn, tt);
                Vec3 c = nn * h;
                var corners = new[] { c - tt * h - bb * h, c + tt * h - bb * h, c + tt * h + bb * h, c - tt * h + bb * h };
                var uvs = new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };
                int baseV = p.Count / 3;
                for (int i = 0; i < 4; i++)
                {
                    p.Add((float)corners[i].X); p.Add((float)corners[i].Y); p.Add((float)corners[i].Z);
                    n.Add((float)nn.X); n.Add((float)nn.Y); n.Add((float)nn.Z);
                    t.Add((float)tt.X); t.Add((float)tt.Y); t.Add((float)tt.Z);
                    b.Add((float)bb.X); b.Add((float)bb.Y); b.Add((float)bb.Z);
                    u.Add(uvs[i * 2]); u.Add(uvs[i * 2 + 1]);
                }
                ix.AddRange(new[] { baseV, baseV + 1, baseV + 2, baseV, baseV + 2, baseV + 3 });
            }
            pos = p.ToArray(); nrm = n.ToArray(); tan = t.ToArray(); bin = b.ToArray(); uv = u.ToArray(); idx = ix.ToArray();
        }

        /// <summary>The C3 payload of the cube: 24 vertices × 56 B, 36 u32 indices, submeshes (material, triStart, triCount).</summary>
        private static byte[] CubePayload(out ModelBegin begin, float patternScale = 1f, params SubmeshInfo[] subs)
        {
            float[] pos, nrm, tan, bin, uv;
            int[] idx;
            Cube(1.25f, out pos, out nrm, out tan, out bin, out uv, out idx);
            if (subs == null || subs.Length == 0) subs = new[] { new SubmeshInfo { MaterialKey = MatA, TriStart = 0, TriCount = 12 } };
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            for (int i = 0; i < 24; i++)
            {
                for (int k = 0; k < 3; k++) w.Write(pos[i * 3 + k]);
                for (int k = 0; k < 3; k++) w.Write(nrm[i * 3 + k]);
                for (int k = 0; k < 3; k++) w.Write(tan[i * 3 + k]);
                for (int k = 0; k < 3; k++) w.Write(bin[i * 3 + k]);
                w.Write(uv[i * 2]); w.Write(uv[i * 2 + 1]);
            }
            foreach (int i in idx) w.Write((uint)i);
            foreach (SubmeshInfo s in subs) { w.Write(s.MaterialKey); w.Write((uint)s.TriStart); w.Write((uint)s.TriCount); w.Write(s.DrawTechnique); w.Write(0u); }
            w.Flush();
            byte[] data = ms.ToArray();
            begin = new ModelBegin
            {
                modelKey = ModelKey, lod = 0, vertexCount = 24, indexCount = 36, submeshCount = (uint)subs.Length, flags = ModelFlags.HasTangents,
                patternScale = patternScale, chunkCount = 1, totalBytes = (uint)data.Length,
            };
            begin.bounds[0] = -1.25f; begin.bounds[1] = -1.25f; begin.bounds[2] = -1.25f;
            begin.bounds[3] = 1.25f; begin.bounds[4] = 1.25f; begin.bounds[5] = 1.25f;
            return data;
        }

        private static ModelData ParsedCube(float patternScale = 1f, params SubmeshInfo[] subs)
        {
            ModelBegin b;
            byte[] data = CubePayload(out b, patternScale, subs);
            ModelData m;
            string error;
            Assert.IsTrue(ModelPayload.TryParse(b, data, data.Length, out m, out error), error);
            return m;
        }

        private static unsafe byte[] Bytes<T>(T v) where T : unmanaged
        {
            var a = new byte[sizeof(T)];
            fixed (byte* p = a) *(T*)p = v;
            return a;
        }

        private static byte[] Prefix(byte[] a, int n)
        {
            var r = new byte[n];
            Buffer.BlockCopy(a, 0, r, 0, n);
            return r;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (byte[] p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }

        private static unsafe C3Outcome Feed(ModelAssembler a, uint type, byte[] payload)
        {
            fixed (byte* p = payload) return a.Feed(type, p, payload.Length);
        }

        private static unsafe C4Outcome Feed(MirrorState s, uint type, byte[] payload)
        {
            ulong id;
            fixed (byte* p = payload) return s.Feed(type, p, payload.Length, out id);
        }

        private static byte[] Chunk(ulong key, uint index, byte[] data, int off, int len)
        {
            var d = new byte[len];
            Buffer.BlockCopy(data, off, d, 0, len);
            return Concat(Bytes(new ModelChunkHeader { modelKey = key, chunkIndex = index, bytes = (uint)len }), d);
        }

        private static byte[] MaterialDefPayload(ulong key, params string[] paths)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(key); w.Write(0u); w.Write(0u);
            for (int i = 0; i < 4; i++)
            {
                byte[] s = System.Text.Encoding.UTF8.GetBytes(i < paths.Length ? paths[i] : "");
                w.Write((ushort)s.Length);
                w.Write(s);
            }
            w.Flush();
            return ms.ToArray();
        }

        // ------------------------------------------------------------------------------------- C3

        [TestMethod]
        public void S3Model_PayloadParse_CountsValuesAndRefusals()
        {
            ModelData m = ParsedCube(2f);
            Assert.AreEqual(24, m.VertexCount); Assert.AreEqual(36, m.IndexCount); Assert.AreEqual(12, m.TriangleCount);
            Assert.AreEqual(1, m.Submeshes.Length); Assert.AreEqual(MatA, m.Submeshes[0].MaterialKey);
            Assert.AreEqual(2f, m.PatternScale);
            Assert.AreEqual(1.25f, m.Pos[0]); Assert.AreEqual(1f, m.Nrm[0]);
            Assert.AreEqual(-1.25f, m.BMinX); Assert.AreEqual(1.25f, m.BMaxZ);

            ModelBegin b;
            byte[] data = CubePayload(out b);
            ModelData bad;
            string error;
            Assert.IsFalse(ModelPayload.TryParse(b, data, data.Length - 4, out bad, out error), "short payload refused");
            StringAssert.Contains(error, "payload");
            byte[] badIndex = (byte[])data.Clone();
            BitConverter.GetBytes(24u).CopyTo(badIndex, 24 * 56); // first index = vertexCount
            Assert.IsFalse(ModelPayload.TryParse(b, badIndex, badIndex.Length, out bad, out error));
            StringAssert.Contains(error, "index");
            ModelBegin b2;
            byte[] range = CubePayload(out b2, 1f, new SubmeshInfo { MaterialKey = MatA, TriStart = 6, TriCount = 7 });
            Assert.IsFalse(ModelPayload.TryParse(b2, range, range.Length, out bad, out error));
            StringAssert.Contains(error, "submesh");
            ModelBegin b3 = b;
            b3.indexCount = 35;
            Assert.IsFalse(ModelPayload.TryParse(b3, data, data.Length, out bad, out error), "indexCount not a multiple of 3");
        }

        [TestMethod]
        public void S3Assembler_ChunksInOrder_HashChecked_MaterialDef_SyntheticAndOrphans()
        {
            ModelBegin b;
            byte[] data = CubePayload(out b);
            int split = 500;
            b.chunkCount = 2;
            var a = new ModelAssembler();
            ulong hash = LayoutHash.Fnv1a64(data);
            Assert.AreEqual(C3Outcome.None, Feed(a, C3Type.ModelBegin, Bytes(b)));
            Assert.AreEqual(C3Outcome.None, Feed(a, C3Type.ModelChunk, Chunk(ModelKey, 0, data, 0, split)));
            Assert.AreEqual(C3Outcome.None, Feed(a, C3Type.ModelChunk, Chunk(ModelKey, 1, data, split, data.Length - split)));
            Assert.AreEqual(C3Outcome.ModelComplete, Feed(a, C3Type.ModelEnd, Bytes(new ModelEnd { modelKey = ModelKey, payloadHash = hash })));
            Assert.IsNotNull(a.Completed);
            Assert.IsFalse(a.Completed.Synthetic);
            CollectionAssert.AreEqual(data, a.Completed.Payload, "the concatenated chunk payloads are the model payload");
            Assert.AreEqual(data.Length + 64 + 2 * 16 + 16 + 4 * 8, a.Bytes, "c3BytesDrained counts payload + 8-B header per message (S0 stand-in convention)");
            ulong actual;
            Assert.IsTrue(ModelPayload.HashMatches(a.Completed, out actual), "the worker's payloadHash check passes");
            Assert.AreEqual(hash, actual);
            CollectionAssert.AreEqual(new[] { MatA }, ModelPayload.SubmeshMaterialKeys(a.Completed.Begin, a.Completed.Payload, a.Completed.Length),
                                      "the submesh material keys straight from the payload's table (C7 pendingParts before the worker parsed it)");

            // hash mismatch: the main thread only copies; the model completes and the worker's
 // check refuses it (the consumer then counts it through NoteHashMismatch)
            Feed(a, C3Type.ModelBegin, Bytes(b));
            Feed(a, C3Type.ModelChunk, Chunk(ModelKey, 0, data, 0, split));
            Feed(a, C3Type.ModelChunk, Chunk(ModelKey, 1, data, split, data.Length - split));
            Assert.AreEqual(C3Outcome.ModelComplete, Feed(a, C3Type.ModelEnd, Bytes(new ModelEnd { modelKey = ModelKey, payloadHash = hash ^ 1 })));
            Assert.AreEqual(0, a.HashMismatch, "no hash on the main thread");
            Assert.IsFalse(ModelPayload.HashMatches(a.Completed, out actual), "the worker's check refuses it");
            Assert.AreEqual(hash, actual);
            long misBefore = a.Misparse;
            a.NoteHashMismatch("worker: payloadHash");
            Assert.AreEqual(1, a.HashMismatch);
            Assert.AreEqual(misBefore + 1, a.Misparse);

            // out of order: one error, then the rest of the model is orphaned (not more errors)
            long mis = a.Misparse;
            Feed(a, C3Type.ModelBegin, Bytes(b));
            Assert.AreEqual(C3Outcome.Error, Feed(a, C3Type.ModelChunk, Chunk(ModelKey, 1, data, split, data.Length - split)));
            Assert.AreEqual(C3Outcome.Ignored, Feed(a, C3Type.ModelEnd, Bytes(new ModelEnd { modelKey = ModelKey, payloadHash = hash })));
            Assert.AreEqual(mis + 1, a.Misparse);

            // after a ring re-sync, chunks and ends before the next ModelBegin are orphans
            a.OnResync();
            Assert.AreEqual(C3Outcome.Ignored, Feed(a, C3Type.ModelChunk, Chunk(ModelKey, 1, data, split, data.Length - split)));
            Assert.AreEqual(C3Outcome.Ignored, Feed(a, C3Type.ModelEnd, Bytes(new ModelEnd { modelKey = ModelKey, payloadHash = hash })));
            Assert.AreEqual(3, a.Orphans, "the out-of-order model's end, then the two after the re-sync");

            // S0 synthetic traffic: vertexCount 0, stamped chunks, hash over bodies only: complete, never hashed or kept
            var syn = new ModelBegin { modelKey = 7, vertexCount = 0, chunkCount = 1, totalBytes = 48 };
            Feed(a, C3Type.ModelBegin, Bytes(syn));
            Feed(a, C3Type.ModelChunk, Chunk(7, 0, new byte[48], 0, 48));
            Assert.AreEqual(C3Outcome.ModelComplete, Feed(a, C3Type.ModelEnd, Bytes(new ModelEnd { modelKey = 7, payloadHash = 12345 })));
            Assert.IsTrue(a.Completed.Synthetic);
            Assert.IsNull(a.Completed.Payload);

            // MaterialDef with four strings
            Assert.AreEqual(C3Outcome.MaterialDefined, Feed(a, C3Type.MaterialDef, MaterialDefPayload(MatA, @"C:\x\a_cm.dds", @"C:\x\a_ng.dds", @"C:\x\a_add.dds")));
            Assert.AreEqual(MatA, a.CompletedMaterial.Key);
            Assert.AreEqual(@"C:\x\a_ng.dds", a.CompletedMaterial.NormalGloss);
            Assert.AreEqual("", a.CompletedMaterial.AlphaMask);
            byte[] cut = MaterialDefPayload(MatA, "abc");
            Assert.AreEqual(C3Outcome.Error, Feed(a, C3Type.MaterialDef, Prefix(cut, cut.Length - 3)), "truncated strings refused");
            Assert.AreEqual(C3Outcome.Error, Feed(a, 99, new byte[8]), "unknown type");
        }

        // ------------------------------------------------------------------------------------- C4

        private static PartAdd Part(ulong entity, ulong partKey, float tx, float ty, float tz, float h = 0f)
        {
            var p = new PartAdd { entityId = entity, partKey = partKey, modelKey = ModelKey, colorMaskHSV = new F32x3(h, 0.5f, 0.1f), uvOffset = new F32x2(0.25f, 0.5f) };
            float[] rows = { 1, 0, 0, 0, 1, 0, 0, 0, 1, tx, ty, tz };
            for (int i = 0; i < 12; i++) p.local[i] = rows[i];
            return p;
        }

        [TestMethod]
        public void S3State_C4_GridsPartsOrphansRecolorRemoveClearAll_Epoch()
        {
            var s = new MirrorState { AcceptEpoch = 3 };
            Assert.AreEqual(C4Outcome.PartChanged, Feed(s, C4Type.PartAdd, Bytes(Part(100, 1, 0, 0, 0))), "a part before its grid is kept as an orphan");
            Assert.AreEqual(0, s.Grids.Count);
            Assert.AreEqual(1, s.PartCount);
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.GridAdd, Bytes(new GridAdd { entityId = 200, targetClass = GridClass.LargeGrid, worldEpoch = 2 })), "other epoch dropped");
            Assert.AreEqual(1, s.EpochDropped);
            Assert.AreEqual(C4Outcome.GridAdded, Feed(s, C4Type.GridAdd, Bytes(new GridAdd { entityId = 100, targetClass = GridClass.LargeGrid, worldEpoch = 3 })));
            GridRecord g = s.Grids[100];
            Assert.AreEqual(1, g.Parts.Count, "orphan adopted");
            Assert.AreEqual(0, s.Orphans.Count);
            Assert.IsTrue(g.DirtyCells.Contains(new CellKey(0, 0, 0)));
            Feed(s, C4Type.PartAdd, Bytes(Part(100, 2, 22.5f, 0, -2.5f)));
            Assert.IsTrue(g.DirtyCells.Contains(new CellKey(1, 0, -1)), "20 m cells on a large grid");
            g.DirtyCells.Clear();
            Feed(s, C4Type.PartRecolor, Bytes(new PartRecolor { entityId = 100, partKey = 2, hsv = new F32x3(0.5f, 0.2f, 0.3f) }));
            Assert.AreEqual(0.5f, g.Parts[2].H);
            Assert.IsTrue(g.DirtyCells.Contains(new CellKey(1, 0, -1)));
            Feed(s, C4Type.PartRemove, Bytes(new PartRemove { entityId = 100, partKey = 1 }));
            Assert.AreEqual(1, g.Parts.Count);
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.SkinSet, new byte[16]), "later slices: counted, not errors");
            Assert.AreEqual(C4Outcome.Error, Feed(s, C4Type.PartAdd, new byte[99]));
            Feed(s, C4Type.GridAdd, Bytes(new GridAdd { entityId = 101, targetClass = GridClass.SmallGrid, worldEpoch = 3 }));
            Assert.AreEqual(4f, s.Grids[101].CellSize, "4 m cells on a small grid");
            Assert.AreEqual(2, s.Grids.Count, "C7 mirroredEntityCount = grids known");
            Feed(s, C4Type.EntityRemove, Bytes(new EntityRemove { entityId = 101 }));
            Assert.AreEqual(1, s.Grids.Count);
            Assert.AreEqual(C4Outcome.ClearAll, Feed(s, C4Type.ClearAll, Bytes(new ClearAll { worldEpoch = 3, linkEpoch = 9 })));
            Assert.AreEqual(0, s.Grids.Count);
            Assert.AreEqual(9u, s.LastClearAll.linkEpoch);
            int pending = 0;
            Feed(s, C4Type.PartAdd, Bytes(Part(300, 1, 0, 0, 0)));
            s.ForEachPart((p, orphan) => { if (orphan) pending++; });
            Assert.AreEqual(1, pending, "orphan parts count as pending");
        }

        /// <summary>
 /// End to end without Unity (the consumer's drain loop over real ByteRings, small enough to wrap with pad records):
 /// a chunked cube model, its MaterialDef, GridAdd and a rotated PartAdd, a C2 pose; then prepare, build, and the
 /// built vertices placed by the Frame/Entity math equal WireMath.Apply of the SE-local part vertex.
 /// </summary>
        [TestMethod]
        public unsafe void S3Stream_ThroughByteRings_ModelPartPose_EndToEnd()
        {
            const int cap = 1024; // the stream (about 1.8 KB) fills it and wraps
            var c3buf = new byte[cap];
            var c4buf = new byte[cap];
            var words = new ulong[4];
            var asm = new ModelAssembler();
            var st = new MirrorState { AcceptEpoch = 5 };
            ModelBegin b;
            byte[] data = CubePayload(out b);
            int chunk = 300;
            b.chunkCount = (uint)((data.Length + chunk - 1) / chunk);
            int completed = 0, materials = 0, wraps = 0;
            fixed (byte* c3 = c3buf) fixed (byte* c4 = c4buf) fixed (ulong* w = words)
            {
                var p3 = new ByteRing(&w[0], &w[1], c3, cap);
                var r3 = new ByteRing(&w[0], &w[1], c3, cap);
                var p4 = new ByteRing(&w[2], &w[3], c4, cap);
                var r4 = new ByteRing(&w[2], &w[3], c4, cap);
                var queue = new List<KeyValuePair<uint, byte[]>>();
                queue.Add(new KeyValuePair<uint, byte[]>(C3Type.MaterialDef, MaterialDefPayload(MatA, @"C:\x\a_cm.dds", @"C:\x\a_ng.dds", @"C:\x\a_add.dds")));
                queue.Add(new KeyValuePair<uint, byte[]>(C3Type.ModelBegin, Bytes(b)));
                for (uint i = 0; i < b.chunkCount; i++)
                {
                    int off = (int)i * chunk;
                    queue.Add(new KeyValuePair<uint, byte[]>(C3Type.ModelChunk, Chunk(ModelKey, i, data, off, Math.Min(chunk, data.Length - off))));
                }
                queue.Add(new KeyValuePair<uint, byte[]>(C3Type.ModelEnd, Bytes(new ModelEnd { modelKey = ModelKey, payloadHash = LayoutHash.Fnv1a64(data) })));
                ulong lastOff = 0;
                Action drain = () =>
                {
                    for (;;)
                    {
                        uint type; byte* pl; int n;
                        RingReadStatus rs = r3.TryPeek(out type, out pl, out n);
                        if (rs == RingReadStatus.Empty) break;
                        Assert.AreEqual(RingReadStatus.Ok, rs);
                        C3Outcome o = asm.Feed(type, pl, n);
                        r3.Advance();
                        Assert.AreNotEqual(C3Outcome.Error, o, asm.LastError);
                        if (o == C3Outcome.ModelComplete) completed++;
                        if (o == C3Outcome.MaterialDefined) materials++;
                    }
                };
                foreach (KeyValuePair<uint, byte[]> m in queue)
                {
                    // the producer tries once; when the ring is full, the consumer drains (as the plugin does each frame)
                    while (p3.TryWrite(m.Key, m.Value, 0, m.Value.Length) == RingWriteStatus.Full) drain();
                    ulong off = p3.ProducerHead % cap;
                    if (off < lastOff) wraps++;
                    lastOff = off;
                }
                drain();
                Assert.AreEqual(1, completed);
                Assert.AreEqual(1, materials);
                Assert.IsTrue(wraps > 0, "the ring wrapped (pad records crossed)");
                CollectionAssert.AreEqual(data, asm.Completed.Payload);

                // C4: SE part rotated 90 degrees about SE +X, at (2.5, 0, -2.5)
                float[] rows0 = { 1, 0, 0, 0, 0, 1, 0, -1, 0, 2.5f, 0, -2.5f };
                PartAdd pa = Part(77, 1, 0, 0, 0);
                for (int i = 0; i < 12; i++) pa.local[i] = rows0[i];
                byte[] ga = Bytes(new GridAdd { entityId = 77, targetClass = GridClass.LargeGrid, worldEpoch = 5 });
                Assert.AreEqual(RingWriteStatus.Ok, p4.TryWrite(C4Type.GridAdd, ga, 0, ga.Length));
                byte[] pab = Bytes(pa);
                Assert.AreEqual(RingWriteStatus.Ok, p4.TryWrite(C4Type.PartAdd, pab, 0, pab.Length));
                for (;;)
                {
                    uint type; byte* pl; int n; ulong id;
                    if (r4.TryPeek(out type, out pl, out n) != RingReadStatus.Ok) break;
                    Assert.AreNotEqual(C4Outcome.Error, st.Feed(type, pl, n, out id), st.LastError);
                    r4.Advance();
                }
            }
            Assert.AreEqual(1, st.Grids.Count);
            PartRecord part = st.Grids[77].Parts[1];
            float[] rows = part.Local;

            ModelData md;
            string error;
            Assert.IsTrue(ModelPayload.TryParse(asm.Completed.Begin, asm.Completed.Payload, asm.Completed.Length, out md, out error), error);
            PreparedModel pm = MeshBuilder.Prepare(md, true, true);
            var input = new CellBuildInput { GridId = 77, Cell = part.Cell, Options = new BuildOptions() };
            input.Parts.Add(new PartBuild { Part = part.Clone(), Model = pm });
            ChunkMeshData c = MeshBuilder.BuildCell(input).Chunks[0];
            Assert.AreEqual(1.0, c.Agreement, 1e-12);

            // the C2 pose of entity 77 and the Unity hierarchy vs WireMath.Apply(o, q, s, part(l))
            EntityPose pose = Pose(new Vec3(-1029.0, -151.25, -1116.0), MirrorPoseMath.AngleAxis(23, new Vec3(0.3, 1, 0.2)), 1.0025, 1.0);
            SeMatrix pmx = SeMatrix.FromRows(rows);
            double worst = 0;
            for (int i = 0; i < md.VertexCount; i++)
            {
                var l = new Vec3(md.Pos[i * 3], md.Pos[i * 3 + 1], md.Pos[i * 3 + 2]);
                Vec3 expected = WireMath.Apply(pose.O, pose.Q, pose.S, pmx.Transform(l));
                double best = double.MaxValue;
                for (int k = 0; k < c.VertexCount; k++)
                {
                    MirrorVertex v = c.Vertices[k];
                    double d = (MirrorPoseMath.ApplyUnityLocal(pose, new Vec3(v.Px, v.Py, v.Pz)) - expected).Length;
                    if (d < best) best = d;
                }
                worst = Math.Max(worst, best);
            }
            Assert.IsTrue(worst < 1e-5, "every SE vertex lands where WireMath.Apply puts it (" + worst + " m)");
        }

        // ------------------------------------------------------------------------------------- mesh

        [TestMethod]
        public void S3Mesh_IndexRewrite_I0I2I1_InPlaceAndMutant()
        {
            int[] src = { 0, 1, 2, 3, 4, 5 };
            var dst = new int[6];
            MeshBuilder.RewriteTriangles(src, dst, true);
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 3, 5, 4 }, dst);
            MeshBuilder.RewriteTriangles(src, dst, false);
            CollectionAssert.AreEqual(src, dst, "S3.M2: the rewrite dropped");
            MeshBuilder.RewriteTriangles(src, src, true);
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 3, 5, 4 }, src, "in place");
            int[] wire = { 0, 1, 2 };
            WireMath.MirrorTris(wire, wire);
            CollectionAssert.AreEqual(new[] { 0, 2, 1 }, wire, "same rule as WireMath.MirrorTri");
        }

        [TestMethod]
        public void S3Mesh_Winding_TrianglesConvention_MirrorAndSwap_Agree_MutantsFlipAndAreCaught()
        {
            ModelData m = ParsedCube();
            PreparedModel ok = MeshBuilder.Prepare(m, true, true);
            Assert.AreEqual(1.0, ok.RawAgreement, 1e-12, "S3.C3: mirror + (I0, I2, I1) agrees on every triangle");
            Assert.IsFalse(ok.Flipped);
            Assert.AreEqual(0, ok.Degenerate);
            for (int i = 0; i < ok.VertexCount; i++) Assert.AreEqual(-1f, ok.Tan[i * 4 + 3], "w = sign(dot(Cross(n', t'), b')) = -1 after the mirror");
            Assert.AreEqual(-m.Pos[2], ok.Pos[2], "z negated");

            PreparedModel m2 = MeshBuilder.Prepare(m, true, false);
            Assert.AreEqual(0.0, m2.RawAgreement, 1e-12, "S3.M2: the raw measure is 0 % (C3 red)");
            Assert.IsTrue(m2.Flipped, "the automatic flip below 1 % engages");
            int deg;
            Assert.AreEqual(1.0, MeshBuilder.Agreement(m2.Pos, m2.Nrm, m2.Tris, 0, m2.TriangleCount, out deg), 1e-12, "after the flip the mesh draws right");

            PreparedModel m1 = MeshBuilder.Prepare(m, false, true);
            Assert.AreEqual(0.0, m1.RawAgreement, 1e-12, "S3.M1: no mirror -> the swap alone inverts every triangle (C3 red)");
            Assert.IsTrue(m1.Flipped);
            Assert.AreEqual(m.Pos[2], m1.Pos[2], "S3.M1 leaves z");
        }

        private static PartRecord PartRec(float[] rows, float offU = 0, float offV = 0, float h = 0)
        {
            var p = new PartRecord { EntityId = 1, PartKey = 1, ModelKey = ModelKey, OffU = offU, OffV = offV, H = h, S = 0.5f, V = 0.1f };
            Array.Copy(rows, p.Local, 12);
            return p;
        }

        [TestMethod]
        public void S3Mesh_BuildCell_PartComposedInSeSpaceThenMirrored_UvRules_Improper()
        {
            ModelData m = ParsedCube(2f);
            PreparedModel pm = MeshBuilder.Prepare(m, true, true);
            // SE part: 90 degrees about SE +Y (Right = (0,0,-1), Up = (0,1,0), Backward = (1,0,0)), translated
            float[] rows = { 0, 0, -1, 0, 1, 0, 1, 0, 0, 2.5f, 5f, -7.5f };
            var input = new CellBuildInput { GridId = 9, Cell = new CellKey(0, 0, -1), Options = new BuildOptions() };
            input.Parts.Add(new PartBuild { Part = PartRec(rows, 0.25f, 0.5f), Model = pm });
            CellBuildResult r = MeshBuilder.BuildCell(input);
            Assert.AreEqual(1, r.Chunks.Count);
            ChunkMeshData c = r.Chunks[0];
            Assert.AreEqual(24, c.VertexCount); Assert.AreEqual(12, c.TriangleCount);
            Assert.IsFalse(c.Use32);
            Assert.AreEqual(1.0, c.Agreement, 1e-12, "per-chunk C3 after the part transform");
            SeMatrix part = SeMatrix.FromRows(rows);
            for (int i = 0; i < 24; i++)
            {
                // independent: SE vertex -> part matrix in SE space -> Mirror once
                var l = new Vec3(m.Pos[i * 3], m.Pos[i * 3 + 1], m.Pos[i * 3 + 2]);
                Vec3 expected = WireMath.Mirror(part.Transform(l));
                bool found = false;
                for (int k = 0; k < c.VertexCount && !found; k++)
                {
                    MirrorVertex v = c.Vertices[k];
                    if (Math.Abs(v.Px - expected.X) < 1e-5 && Math.Abs(v.Py - expected.Y) < 1e-5 && Math.Abs(v.Pz - expected.Z) < 1e-5) found = true;
                }
                Assert.IsTrue(found, "vertex " + i + " at Mirror(part(l)) " + expected);
            }
            // exporter UV rule: uv / patternScale + offset
            MirrorVertex v0 = c.Vertices[0];
            Assert.IsTrue(Math.Abs(v0.U - (m.Uv[0] / 2f + 0.25f)) < 1e-6 && Math.Abs(v0.V - (m.Uv[1] / 2f + 0.5f)) < 1e-6);
            float cu, cv;
            MeshBuilder.ChunkUv(0.5f, 0.5f, 2f, 0.25f, 0.5f, new BuildOptions { Uv = UvRule.Renderer }, out cu, out cv);
            Assert.AreEqual(0.75f, cu); Assert.AreEqual(1f, cv);
            MeshBuilder.ChunkUv(0.5f, 0.25f, 0f, 0f, 0f, new BuildOptions { VFlip = true }, out cu, out cv);
            Assert.AreEqual(0.5f, cu, "patternScale 0 taken as 1"); Assert.AreEqual(0.75f, cv, "vFlip");
            // bounds in Unity-local space, SE z mirrored
            Assert.AreEqual(-7.5f + 1.25f, -c.MinZ, 1e-5);

            // an improper part matrix (det -1): triangles reversed, w negated, winding still right
            float[] mirrorRows = { -1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
            var inp2 = new CellBuildInput { GridId = 9, Cell = new CellKey(0, 0, 0), Options = new BuildOptions() };
            inp2.Parts.Add(new PartBuild { Part = PartRec(mirrorRows), Model = pm });
            ChunkMeshData c2 = MeshBuilder.BuildCell(inp2).Chunks[0];
            Assert.AreEqual(1.0, c2.Agreement, 1e-12);
            Assert.AreEqual(1f, c2.Vertices[0].Tw);
        }

        [TestMethod]
        public void S3Mesh_BuildCell_ChunksPerMaterialAndColour_ThirtyTwoBitIndices()
        {
            ModelData m = ParsedCube(1f, new SubmeshInfo { MaterialKey = MatA, TriStart = 0, TriCount = 6 }, new SubmeshInfo { MaterialKey = MatB, TriStart = 6, TriCount = 6 });
            PreparedModel pm = MeshBuilder.Prepare(m, true, true);
            float[] at0 = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }, at1 = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 2.5f, 0, 0 };
            var input = new CellBuildInput { GridId = 9, Options = new BuildOptions() };
            input.Parts.Add(new PartBuild { Part = PartRec(at0, h: 0f), Model = pm });
            input.Parts.Add(new PartBuild { Part = PartRec(at1, h: 0f), Model = pm });
            input.Parts.Add(new PartBuild { Part = PartRec(at1, h: 0.5f), Model = pm });
            CellBuildResult r = MeshBuilder.BuildCell(input);
            Assert.AreEqual(4, r.Chunks.Count, "2 materials x 2 colours");
            Assert.AreEqual(3, r.Parts);
            int tris = 0;
            foreach (ChunkMeshData c in r.Chunks) { tris += c.TriangleCount; Assert.AreEqual(1.0, c.Agreement, 1e-12); }
            Assert.AreEqual(36, tris);
            ChunkMeshData two = r.Chunks.Find(c => c.Key.MaterialKey == MatA && c.Parts == 2);
            Assert.IsNotNull(two, "the two same-colour parts share one chunk per material");
            Assert.AreEqual(24, two.VertexCount, "only referenced vertices are copied (12 per part per material)");

            // > 65,535 vertices: 32-bit indices
            int nt = 22000;
            var big = new ModelData
            {
                Key = 5, VertexCount = nt * 3, IndexCount = nt * 3, PatternScale = 1,
                Pos = new float[nt * 9], Nrm = new float[nt * 9], Tan = new float[nt * 9], Bin = new float[nt * 9], Uv = new float[nt * 6], Indices = new int[nt * 3],
                Submeshes = new[] { new SubmeshInfo { MaterialKey = MatA, TriStart = 0, TriCount = nt } },
            };
            for (int t = 0; t < nt; t++)
            {
                // SE-space triangle (0,0,t) (1,0,t) (0,1,t), normal +z: Cross agrees in SE space (Triangles convention)
                float[] p = { 0, 0, t, 1, 0, t, 0, 1, t };
                for (int k = 0; k < 9; k++) big.Pos[t * 9 + k] = p[k];
                for (int k = 0; k < 3; k++) { big.Nrm[t * 9 + k * 3 + 2] = 1; big.Tan[t * 9 + k * 3] = 1; big.Bin[t * 9 + k * 3 + 1] = 1; }
                big.Indices[t * 3] = t * 3; big.Indices[t * 3 + 1] = t * 3 + 1; big.Indices[t * 3 + 2] = t * 3 + 2;
            }
            PreparedModel pb = MeshBuilder.Prepare(big, true, true);
            Assert.AreEqual(1.0, pb.RawAgreement, 1e-12);
            var bi = new CellBuildInput { GridId = 1, Options = new BuildOptions() };
            bi.Parts.Add(new PartBuild { Part = PartRec(at0), Model = pb });
            ChunkMeshData bc = MeshBuilder.BuildCell(bi).Chunks[0];
            Assert.IsTrue(bc.Use32);
            Assert.AreEqual(nt * 3, bc.VertexCount);
        }

        // ------------------------------------------------------------------------------------- pose math

        private static EntityPose Pose(Vec3 o, Quat q, double s, double k)
        {
            return new EntityPose { Id = 1, Ox = o.X, Oy = o.Y, Oz = o.Z, Qx = q.X, Qy = q.Y, Qz = q.Z, Qw = q.W, S = s, K = k };
        }

        [TestMethod]
        public void S3Pose_FrameEntityHierarchy_EqualsWireApply_WithK()
        {
            var rnd = new Random(31);
            for (int n = 0; n < 200; n++)
            {
                Quat q = MirrorPoseMath.AngleAxis(rnd.NextDouble() * 360, new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5 + 1e-3));
                double s = 0.99 + rnd.NextDouble() * 0.07;
                double k = n % 3 == 0 ? 2.0 : 1.0;
                EntityPose p = Pose(new Vec3(rnd.NextDouble() * 2000 - 1000, -rnd.NextDouble() * 1500, rnd.NextDouble() * 2000 - 1000), q, s, k);
                double[] m = MirrorPoseMath.HierarchyMatrix(p);
                for (int i = 0; i < 4; i++)
                {
                    var l = new Vec3(rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 20 - 10);
                    Vec3 wire = WireMath.Apply(p.O, q, s, l * k);
                    Vec3 viaMatrix = MirrorPoseMath.Transform(m, WireMath.Mirror(l));
                    Vec3 viaUnity = MirrorPoseMath.ApplyUnityLocal(p, WireMath.Mirror(l));
                    Assert.IsTrue((wire - viaMatrix).Length < 1e-9, "T(o)·S·R(q)·k on Mirror(l) = WireMath.Apply(o, q, s, k·l)");
                    Assert.IsTrue((wire - viaUnity).Length < 1e-9);
                    Assert.IsTrue((wire - MirrorPoseMath.ApplySeLocal(p, l)).Length < 1e-9);
                }
            }
        }

        [TestMethod]
        public void S3Pose_ShearCheck_RigidFrameDeviationIsTheRigidDrawError()
        {
            // S3.G1-E2 / S3.M7: a rotating Entity under a (s, 1, s) Frame vs the same drawn with a rigid Frame (s = 1)
            double s = 1.0345, worstRigid = 0, worstSheared = 0;
            Vec3[] corners = MirrorPoseMath.Corners(new Vec3(-0.5, -0.5, -0.5), new Vec3(0.5, 0.5, 0.5));
            for (int deg = 0; deg < 360; deg++)
            {
                Quat q = MirrorPoseMath.AngleAxis(deg, new Vec3(1, 1, 0));
                EntityPose p = Pose(new Vec3(10, -100, 20), q, s, 1);
                EntityPose rigid = Pose(new Vec3(10, -100, 20), q, 1.0, 1);
                double[] m = MirrorPoseMath.HierarchyMatrix(p), mr = MirrorPoseMath.HierarchyMatrix(rigid);
                var actual = new Vec3[8];
                var actualRigid = new Vec3[8];
                for (int i = 0; i < 8; i++) { actual[i] = MirrorPoseMath.Transform(m, corners[i]); actualRigid[i] = MirrorPoseMath.Transform(mr, corners[i]); }
                worstSheared = Math.Max(worstSheared, MirrorPoseMath.MaxDeviation(actual, corners, p));
                worstRigid = Math.Max(worstRigid, MirrorPoseMath.MaxDeviation(actualRigid, corners, p));
            }
            Assert.IsTrue(worstSheared < 1e-9, "the sheared hierarchy is the emulation");
            Assert.IsTrue(worstRigid > 1e-2, "the rigid draw misses by about (s - 1)·|corner| (" + worstRigid + " m): the check measures something");
            Assert.IsTrue(worstRigid < (s - 1) * Math.Sqrt(0.75) + 1e-9);
        }

        // ------------------------------------------------------------------------------------- C2

        private static void History(EntityHistory h, params ulong[] ticks)
        {
            foreach (ulong t in ticks)
            {
                EntitySnapshot s = h.Rent();
                s.Tick = t;
                s.WorldEpoch = 1;
                s.Count = 1;
                s.Poses[0] = Pose(new Vec3(t, 0, 0), MirrorPoseMath.AngleAxis(t, Vec3.UnitY), 1.0 + t * 1e-3, 1);
                s.Poses[0].Id = 42;
                h.Push(s);
            }
        }

        [TestMethod]
        public void S3PoseSampler_FollowsTheCameraBracket_HoldsExtrapolates_M4LagsOneTick()
        {
            var h = new EntityHistory();
            History(h, 10, 11, 12);
            EntityPose p;
            ulong used;
            PoseHow how;
            Assert.IsTrue(PoseSampler.Sample(h, 42, new CamBracket { Valid = true, Older = 11, Newer = 12, Alpha = 0.25 }, false, out p, out used, out how));
            Assert.AreEqual(PoseHow.Interpolated, how);
            Assert.AreEqual(11.25, p.Ox, 1e-12, "o lerped with the camera's alpha");
            Assert.AreEqual(1.01125, p.S, 1e-12, "s lerped");
            Assert.IsTrue(Quat.RotationDistance(p.Q, MirrorPoseMath.AngleAxis(11.25, Vec3.UnitY)) < 1e-9, "q slerped");
            Assert.AreEqual(12UL, used);

            Assert.IsTrue(PoseSampler.Sample(h, 42, new CamBracket { Valid = true, Older = 11, Newer = 12, Alpha = 1.5 }, false, out p, out used, out how));
            Assert.AreEqual(PoseHow.Extrapolated, how);
            Assert.AreEqual(12.5, p.Ox, 1e-12, "extrapolated with the camera's factor");

            Assert.IsTrue(PoseSampler.Sample(h, 42, new CamBracket { Valid = true, Older = 13, Newer = 14, Alpha = 0.5 }, false, out p, out used, out how));
            Assert.AreEqual(PoseHow.Held, how, "a tick C2 has not published yet holds the newest at or before it");
            Assert.AreEqual(12.0, p.Ox);

            Assert.IsTrue(PoseSampler.Sample(h, 42, new CamBracket { Valid = true, Older = 11, Newer = 12, Alpha = 0.25 }, true, out p, out used, out how));
            Assert.AreEqual(PoseHow.Previous, how, "S3.M4");
            Assert.AreEqual(11.0, p.Ox, "S3.M4 draws the previous snapshot, not interpolated");

            Assert.IsTrue(PoseSampler.Sample(h, 42, default(CamBracket), false, out p, out used, out how));
            Assert.AreEqual(PoseHow.Newest, how);
            Assert.AreEqual(12.0, p.Ox);
            Assert.IsFalse(PoseSampler.Sample(h, 43, default(CamBracket), false, out p, out used, out how), "unknown entity");

            History(h, 13, 14);
            Assert.AreEqual(EntityHistory.Capacity, h.Count, "4 snapshots kept");
            Assert.AreEqual(11UL, h[0].Tick);
            History(h, 5);
            Assert.AreEqual(1, h.Count, "tick backwards restarts");
            Assert.AreEqual(1, h.Resets);
        }

        [TestMethod]
        public unsafe void S3EntityTable_SeqlockRead_FreshUnchangedBusyNever()
        {
            var buf = new byte[SeEntitiesLimits.EntitiesOffset + 2 * SeEntitiesLimits.EntitySize];
            fixed (byte* b = buf)
            {
                var r = new EntityTableReader(b);
                Assert.AreEqual(SeqlockReadStatus.Never, r.Poll());
                SeEntitiesHeader* hdr = (SeEntitiesHeader*)b;
                hdr->entityCount = 2;
                hdr->tick = 77;
                hdr->worldEpoch = 4;
                SeEntity* e = (SeEntity*)(b + SeEntitiesLimits.EntitiesOffset);
                e[0].entityId = 9; e[0].o = new F32x3(1, -150, 2); e[0].q = new F32x4(0, 0, 0, 1.2f); e[0].s = 1.0025f; e[0].k = 0f;
                e[1].entityId = 10; e[1].o = new F32x3(float.NaN, 0, 0); e[1].q = new F32x4(0, 0, 0, 1); e[1].s = 1; e[1].k = 1;
                hdr->seq = 3;
                Assert.AreEqual(SeqlockReadStatus.Busy, r.Poll(), "odd seq: the writer is busy");
                hdr->seq = 4;
                Assert.AreEqual(SeqlockReadStatus.Fresh, r.Poll());
                Assert.AreEqual(SeqlockReadStatus.Unchanged, r.Poll());
                EntitySnapshot s = r.History.Newest;
                Assert.AreEqual(77UL, s.Tick);
                Assert.AreEqual(1, s.Count, "the NaN pose is refused");
                Assert.AreEqual(1, r.Invalid);
                EntityPose p;
                Assert.IsTrue(s.TryGet(9, out p));
                Assert.AreEqual(1.0, p.Qw, 1e-12, "q normalised");
                Assert.AreEqual(1.0, p.K, "k 0 read as 1 (CONTRACT: default 1)");
                Assert.AreEqual(-150.0, p.Oy);
                r.Reset();
                Assert.AreEqual(0, r.History.Count);
                Assert.AreEqual(SeqlockReadStatus.Unchanged, r.Poll(), "after a reset the old content is not taken for new data");
            }
        }

        // ------------------------------------------------------------------------------------- scheduler, worker

        [TestMethod]
        public void S3Upload_Scheduler_UrgentJobsRunBeforeTheBacklog()
        {
            //: an edited cell's upload goes before the meshes and textures already queued
            long clock = 0;
            var s = new UploadScheduler(() => clock, 1000) { BudgetMs = 2.0 };
            var order = new List<string>();
            Func<string, long, Func<bool>> job = (name, ms) => () => { order.Add(name); clock += ms; return true; };
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "m1", Run = job("m1", 1) });
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "m2", Run = job("m2", 1) });
            s.EnqueueUrgent(new UploadJob { Kind = UploadKind.Mesh, Name = "edit", Run = job("edit", 1) });
            Assert.AreEqual(3, s.Pending);
            Assert.AreEqual(2, s.RunFrame(1));
            CollectionAssert.AreEqual(new[] { "edit", "m1" }, order, "the urgent job first, then the backlog in order");
            Assert.AreEqual(1, s.UrgentJobs);
            s.Clear();
            Assert.AreEqual(0, s.Pending);
        }

        [TestMethod]
        public void S3Upload_Scheduler_Budget_Order_Compress_Failures_MultiStep()
        {
            long clock = 0;
            var s = new UploadScheduler(() => clock, 1000) { BudgetMs = 2.0, CompressEveryFrames = 3 };
            var order = new List<string>();
            Func<string, long, Func<bool>> job = (name, ms) => () => { order.Add(name); clock += ms; return true; };
            s.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "t1", Run = job("t1", 1) });
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "m1", Run = job("m1", 1) });
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "m2", Run = job("m2", 1) });
            s.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "t2", Run = job("t2", 1) });
            Assert.AreEqual(2, s.RunFrame(1), "two 1-ms jobs fill the 2-ms budget");
            CollectionAssert.AreEqual(new[] { "m1", "m2" }, order, "meshes first");
            Assert.AreEqual(2, s.RunFrame(2));
            Assert.AreEqual(0, s.OverBudgetFrames);
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "big", Run = job("big", 5) });
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "after", Run = job("after", 1) });
            Assert.AreEqual(1, s.RunFrame(3), "the first job always runs, the next waits");
            Assert.AreEqual(1, s.OverBudgetFrames);
            Assert.AreEqual(1, s.RunFrame(4));
            // compress: at most once every 3 frames, only as the first job of a frame
            int compressed = 0;
            for (int i = 0; i < 3; i++) s.Enqueue(new UploadJob { Kind = UploadKind.Compress, Name = "c" + i, Run = () => { compressed++; return true; } });
            s.Enqueue(new UploadJob { Kind = UploadKind.Texture, Name = "t3", Run = job("t3", 1) });
            s.RunFrame(10);
            Assert.AreEqual(0, compressed, "a texture ran first this frame");
            s.RunFrame(11);
            Assert.AreEqual(1, compressed);
            s.RunFrame(12); s.RunFrame(13);
            Assert.AreEqual(1, compressed, "not before 3 frames");
            s.RunFrame(14);
            Assert.AreEqual(2, compressed);
            // a throwing job is dropped and counted; a multi-step job continues next frame
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "boom", Run = () => { throw new InvalidOperationException("x"); } });
            s.RunFrame(20);
            Assert.AreEqual(1, s.Failures);
            StringAssert.Contains(s.LastError, "boom");
            int steps = 0;
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "multi", Run = () => ++steps >= 3 });
            s.Enqueue(new UploadJob { Kind = UploadKind.Mesh, Name = "next", Run = job("next", 0) });
            s.RunFrame(21); s.RunFrame(22);
            Assert.AreEqual(2, steps);
            Assert.IsFalse(order.Contains("next"), "the queue waits behind the multi-step job");
            s.RunFrame(23);
            Assert.IsTrue(order.Contains("next"));
        }

        [TestMethod]
        public void S3Worker_RunsJobsOffThread_ReportsFailures()
        {
            using (var w = new MirrorWorker())
            {
                int mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                int jobThread = -1;
                w.Post("ok", () => { jobThread = System.Threading.Thread.CurrentThread.ManagedThreadId; return "done"; });
                w.Post("bad", () => { throw new InvalidDataException("bad data"); });
                var results = new List<object>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (results.Count < 2 && sw.ElapsedMilliseconds < 5000)
                {
                    object o;
                    if (w.TryTake(out o)) results.Add(o); else System.Threading.Thread.Sleep(5);
                }
                Assert.AreEqual(2, results.Count);
                Assert.AreEqual("done", results[0]);
                var f = results[1] as WorkerFailure;
                Assert.IsNotNull(f);
                Assert.AreEqual("bad", f.Name);
                Assert.AreNotEqual(mainThread, jobThread);
                Assert.AreEqual(1, w.Failed);
            }
        }

        // ------------------------------------------------------------------------------------- DDS

        private static byte[] DdsHeader(int w, int h, int mips, uint dxgi, string fourcc = "DX10", uint bits = 0, uint rMask = 0, uint gMask = 0, uint bMask = 0, uint aMask = 0, uint caps2 = 0)
        {
            var b = new byte[148];
            Action<int, uint> put = (o, v) => BitConverter.GetBytes(v).CopyTo(b, o);
            put(0, 0x20534444); put(4, 124); put(8, 0xa1007); put(12, (uint)h); put(16, (uint)w); put(28, (uint)mips);
            put(76, 32);
            if (fourcc != null)
            {
                put(80, 4);
                for (int i = 0; i < 4; i++) b[84 + i] = (byte)fourcc[i];
            }
            else
            {
                put(80, 0x41); put(88, bits); put(92, rMask); put(96, gMask); put(100, bMask); put(104, aMask);
            }
            put(108, 0x401008); put(112, caps2);
            if (fourcc == "DX10") { put(128, dxgi); put(132, 3); put(140, 1); }
            return b;
        }

        [TestMethod]
        public void S3Dds_Bc7Srgb_2048x1024_SizesAndSkipTo1024()
        {
            byte[] h = DdsHeader(2048, 1024, 12, 99);
            DdsInfo d;
            string error;
            Assert.IsTrue(DdsReader.TryParse(h, h.Length, 2796388, out d, out error), error);
            Assert.AreEqual(DdsFormat.BC7, d.Format);
            Assert.IsTrue(d.SrgbDeclared);
            Assert.AreEqual(148, d.DataOffset);
            Assert.AreEqual(2097152, d.MipBytes(0), "512 x 256 blocks of 16 B");
            Assert.AreEqual(2796388, d.MipOffset(12), "the real large_edges_cm.dds size: header + 12 levels (VERIFIED in probe-ship-render §2.2)");
            Assert.AreEqual(16, d.MipBytes(11), "a 1x1 level is one whole block");
            SkipPlan p = DdsReader.PlanSkip(d, 1024);
            Assert.AreEqual(1, p.FirstLevel); Assert.AreEqual(1024, p.Width); Assert.AreEqual(512, p.Height); Assert.AreEqual(11, p.MipCount);
            Assert.AreEqual(148 + 2097152, p.Offset);
            Assert.AreEqual(2796388 - 148 - 2097152, p.Length);
            Assert.IsTrue(DdsReader.SampleAsSrgb(d, TexRole.AddMaps), "a DX10 file says it itself: _add as 99 is decoded, as SE's view decodes it");
            Assert.IsFalse(DdsReader.SampleAsSrgb(Parse(DdsHeader(2048, 1024, 12, 98), 2796388), TexRole.ColorMetal), "98 is linear even for a colour map");

            DdsInfo big = Parse(DdsHeader(8192, 4096, 14, 99), 44739428);
            SkipPlan pb = DdsReader.PlanSkip(big, 1024);
            Assert.AreEqual(3, pb.FirstLevel); Assert.AreEqual(1024, pb.Width); Assert.AreEqual(512, pb.Height); Assert.AreEqual(11, pb.MipCount);
            Assert.AreEqual(44739428, big.MipOffset(14), "large_heavy_square_plate_cm.dds size");

            DdsInfo one = Parse(DdsHeader(2048, 2048, 1, 99), 148 + 512 * 512 * 16);
            Assert.AreEqual(0, DdsReader.PlanSkip(one, 1024).FirstLevel, "no mips: nothing to skip");
        }

        private static DdsInfo Parse(byte[] h, long len)
        {
            DdsInfo d;
            string error;
            Assert.IsTrue(DdsReader.TryParse(h, h.Length, len, out d, out error), error);
            return d;
        }

        [TestMethod]
        public void S3Dds_LegacyAndUncompressed_AndRefusals()
        {
            DdsInfo dxt5 = Parse(DdsHeader(256, 256, 9, 0, "DXT5"), 1 << 20);
            Assert.AreEqual(DdsFormat.BC3, dxt5.Format);
            Assert.AreEqual(128, dxt5.DataOffset);
            Assert.IsTrue(DdsReader.SampleAsSrgb(dxt5, TexRole.ColorMetal), "legacy colour map: sRGB");
            Assert.IsFalse(DdsReader.SampleAsSrgb(dxt5, TexRole.NormalGloss), "legacy other maps: linear");
            Assert.AreEqual(DdsFormat.BC4, Parse(DdsHeader(64, 64, 1, 0, "BC4U"), 1 << 20).Format);
            DdsInfo rgba = Parse(DdsHeader(4, 4, 1, 0, null, 32, 0xff, 0xff00, 0xff0000, 0xff000000), 128 + 64);
            Assert.AreEqual(DdsFormat.RGBA8, rgba.Format);
            Assert.AreEqual(64, rgba.MipBytes(0));
            Assert.AreEqual(DdsFormat.BGRA8, Parse(DdsHeader(4, 4, 1, 0, null, 32, 0xff0000, 0xff00, 0xff, 0xff000000), 128 + 64).Format);
            Assert.AreEqual(DdsFormat.R8, Parse(DdsHeader(4, 4, 1, 0, null, 8, 0xff), 128 + 16).Format);

            DdsInfo d;
            string error;
            byte[] h = DdsHeader(2048, 1024, 12, 99);
            Assert.IsFalse(DdsReader.TryParse(h, h.Length, 1000, out d, out error), "file shorter than its chain");
            StringAssert.Contains(error, "shorter");
            byte[] cube = DdsHeader(64, 64, 1, 99, "DX10", caps2: 0x200);
            Assert.IsFalse(DdsReader.TryParse(cube, cube.Length, 1 << 20, out d, out error));
            byte[] magic = DdsHeader(64, 64, 1, 99);
            magic[0] = 0;
            Assert.IsFalse(DdsReader.TryParse(magic, magic.Length, 1 << 20, out d, out error));
            byte[] unknown = DdsHeader(64, 64, 1, 2);
            Assert.IsFalse(DdsReader.TryParse(unknown, unknown.Length, 1 << 20, out d, out error));
            StringAssert.Contains(error, "unsupported");
            Assert.IsFalse(DdsReader.TryParse(h, 100, 1 << 22, out d, out error), "truncated header");
        }

        [TestMethod]
        public void S3Dds_TryRead_ReadsOnlyThePlannedLevels()
        {
            string path = Path.Combine(Path.GetTempPath(), "de-s3-test-" + Guid.NewGuid().ToString("N") + ".dds");
            try
            {
                byte[] h = DdsHeader(16, 8, 5, 98);
                DdsInfo d = Parse(h, 148 + 4096);
                var file = new byte[d.MipOffset(5)];
                Buffer.BlockCopy(h, 0, file, 0, 148);
                for (int level = 0; level < 5; level++)
                    for (long i = d.MipOffset(level); i < d.MipOffset(level) + d.MipBytes(level); i++) file[i] = (byte)(level + 1);
                File.WriteAllBytes(path, file);
                DdsInfo info;
                SkipPlan plan;
                byte[] data;
                string error;
                Assert.IsTrue(DdsReader.TryRead(path, 8, false, out info, out plan, out data, out error), error);
                Assert.AreEqual(1, plan.FirstLevel);
                Assert.AreEqual(8, plan.Width); Assert.AreEqual(4, plan.Height);
                Assert.AreEqual(2, data[0], "starts at level 1");
                Assert.AreEqual(plan.Length, data.Length);
                Assert.IsTrue(DdsReader.TryRead(path, 8, true, out info, out plan, out data, out error), error);
                Assert.AreEqual(d.MipBytes(1), data.Length, "level k only (the GPU conversion input)");
                Assert.IsFalse(DdsReader.TryRead(path + ".missing", 8, false, out info, out plan, out data, out error));
            }
            finally
            {
                try { File.Delete(path); } catch (Exception) { }
            }
        }

        [TestMethod]
        public void S3Dds_RealSeArmorHeader_WhenInstalled()
        {
            string p = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Content\Textures\Models\Cubes\armor\large_edges_ng.dds";
            if (!File.Exists(p)) { Assert.Inconclusive("SE not installed here"); return; }
            var head = new byte[148];
            long len;
            using (var fs = File.OpenRead(p)) { fs.Read(head, 0, 148); len = fs.Length; }
            DdsInfo d = Parse(head, len);
            Assert.AreEqual(DdsFormat.BC7, d.Format);
            Assert.IsFalse(d.SrgbDeclared, "_ng is BC7_UNORM (98)");
            Assert.AreEqual(len, d.MipOffset(d.MipCount));
        }

        // ------------------------------------------------------------------------------------- repack, paint

        [TestMethod]
        public void S3Repack_SpecBumpMaskIllum_Layout_And_Mips()
        {
            int w = 4, h = 2, n = w * h;
            var cm = new byte[n * 4]; var ng = new byte[n * 4]; var add = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                cm[i * 4] = 255; cm[i * 4 + 1] = 128; cm[i * 4 + 2] = 0; cm[i * 4 + 3] = (byte)(i == 0 ? 255 : 0); // metal on texel 0
                ng[i * 4] = 10; ng[i * 4 + 1] = 200; ng[i * 4 + 2] = 255; ng[i * 4 + 3] = 77;
                add[i * 4] = 255; add[i * 4 + 1] = (byte)(i == 1 ? 255 : 0); add[i * 4 + 2] = 0; add[i * 4 + 3] = 255;
            }
            DerivedMaps d = TextureRepack.Build(cm, ng, add, w, h, false);
            Assert.AreEqual(3, d.MipCount, "4x2 -> 2x1 -> 1x1");
            Assert.AreEqual(TextureRepack.ChainBytes(4, 2, 3), d.Spec.Length);
            // _BumpMap: X -> A, Y -> G, R = 255
            Assert.AreEqual(255, d.Bump[0]); Assert.AreEqual(200, d.Bump[1]); Assert.AreEqual(10, d.Bump[3]);
            // _SpecTex: metal texel = its colour, dielectric = 0.04 (sRGB-encoded), alpha = gloss
            Assert.AreEqual(255, d.Spec[0]);
            Assert.AreEqual(TextureRepack.ToByte(TextureRepack.LinearToSrgb(0.04)), d.Spec[4]);
            Assert.AreEqual(77, d.Spec[3]);
            // _MultiColorMask: paint on the dielectric texel, none on the full metal
            Assert.AreEqual(0, d.Mask[0], "metal 1.0 removes the paint (SE's reduction above 0.4)");
            Assert.AreEqual(255, d.Mask[4]);
            Assert.AreEqual(1f, TextureRepack.PaintOnMetal(1f, 0.4f));
            Assert.AreEqual(0.5f, TextureRepack.PaintOnMetal(1f, 0.55f), 1e-6);
            Assert.AreEqual(0f, TextureRepack.PaintOnMetal(1f, 0.7f), 1e-6);
            // _Illum: cm x (add.g - 1/255) on texel 1 only
            Assert.AreEqual(0, d.Illum[0]);
            Assert.IsTrue(d.Illum[4] > 250);
            Assert.IsTrue(d.EmissiveMax > 0.99f);
            DerivedMaps flipped = TextureRepack.Build(cm, ng, null, w, h, true);
            Assert.AreEqual(55, flipped.Bump[1], "normalFlipY inverts G");
            Assert.AreEqual(0, flipped.Mask[4], "no _add: no paint");
            DerivedMaps flat = TextureRepack.Build(cm, null, null, w, h, false);
            Assert.AreEqual(128, flat.Bump[3]); Assert.AreEqual(128, flat.Spec[3]);
            // mip averaging: linear channels by bytes, sRGB channels in linear
            byte[] level0 = { 0, 0, 0, 0, 255, 255, 255, 255 };
            int mips;
            byte[] lin = TextureRepack.BuildMips(level0, 2, 1, false, out mips);
            Assert.AreEqual(2, mips);
            Assert.AreEqual(128, lin[8]);
            byte[] srgb = TextureRepack.BuildMips(level0, 2, 1, true, out mips);
            Assert.AreEqual(TextureRepack.ToByte(TextureRepack.LinearToSrgb(0.5)), srgb[8], "sRGB RGB averaged on decoded values");
            Assert.AreEqual(128, srgb[11], "alpha stays linear");
            Assert.AreEqual(11, TextureRepack.FullMipCount(1024, 512));
        }

        [TestMethod]
        public void S3Paint_ColorCustomizerSpecRule_And_SeGreyColourisation()
        {
            Rgba white = PaintRules.SpecularFor(new Rgba(1, 1, 1, 1));
            Assert.AreEqual(1f, white.R, 1e-6);
            Rgba black = PaintRules.SpecularFor(new Rgba(0, 0, 0, 1));
            Assert.AreEqual(0.39f, black.R, 1e-6, "lerp(black, dark 0.39, 1)");
            Rgba red = PaintRules.SpecularFor(new Rgba(1, 0, 0, 1));
            float lum = 0.299f, tl = 0.39f + 0.61f * lum, k = 1 - 0.5f * lum;
            Assert.AreEqual(1 + (tl - 1) * k, red.R, 1e-6);
            Assert.AreEqual(tl * k, red.G, 1e-6);

            Rgba neutral = PaintRules.MaterialColour(0f, 0f, 0f);
            Assert.AreEqual(1f, neutral.R, 1e-4, "key (0, 0, 0) leaves SE's grey: _Color white");
            Assert.AreEqual(1f, neutral.B, 1e-4);
            Rgba pureRed = PaintRules.MaterialColour(0f, 1f, 0f);
            Assert.AreEqual(1f, pureRed.R, 1e-4);
            Assert.AreEqual(0f, pureRed.G, 1e-6);
            Rgba brighter = PaintRules.MaterialColour(0f, 0f, 0.2f);
            Assert.IsTrue(brighter.R > 1f, "a value offset above the grey gives a factor above 1");
            double r, g, b;
            PaintRules.HsvToRgb(1.0 / 3.0, 1, 1, out r, out g, out b);
            Assert.AreEqual(0, r, 1e-12); Assert.AreEqual(1, g, 1e-12); Assert.AreEqual(0, b, 1e-12);
            Assert.AreEqual(0.5, TextureRepack.LinearToSrgb(TextureRepack.SrgbToLinear(0.5)), 1e-12);
            Assert.AreNotEqual(ColourRules.Key(0.1f, 0.2f, 0.3f), ColourRules.Key(0.1f, 0.2f, 0.31f));
            Assert.AreEqual(ColourRules.Key(0.1f, 0.2f, 0.3f), ColourRules.Key(0.10001f, 0.2f, 0.3f), "quantised to 1e-4");
        }

        // ------------------------------------------------------------------------------------- lab, args, options, mutants

        [TestMethod]
        public void S3Lab_PlanPoses_SeamothSpot_YawPitch()
        {
            var b = new Vec3(-1029, -152, -1116);
            LabPlan p = LabPlan.Make(b, -152, 0, LabPlan.SeamothSideM);
            Assert.IsTrue((p.SeamothSpot - new Vec3(-1023, -152, -1116)).Length < 1e-9, "heading 0: right = +X, 6 m");
            Vec3 pos;
            Quat q;
            Assert.IsTrue(p.TryPose("P1", out pos, out q));
            Assert.AreEqual(-1116 - 6.0, pos.Z, 1e-9, "6 m in front (F = +Z)");
            Assert.AreEqual(-151.0, pos.Y, 1e-9);
            Assert.AreEqual(-1026.0, pos.X, 1e-9, "centred between the block and the Seamoth");
            Vec3 f = q.Rotate(Vec3.UnitZ);
            Assert.IsTrue(f.Z > 0.98, "looks along F");
            Assert.IsTrue(p.TryPose("p4", out pos, out q));
            Assert.AreEqual(-1116 - 40.0, pos.Z, 1e-9);
            Assert.IsTrue(p.TryPose("P2", out pos, out q));
            Assert.AreEqual(-151.7, pos.Y, 1e-9, "0.3 m above the seabed");
            Assert.AreEqual(-1035.0, pos.X, 1e-9, "6 m to the side away from the Seamoth");
            Assert.IsTrue(p.TryPose("P3", out pos, out q));
            Assert.AreEqual(-142.0, pos.Y, 1e-9);
            Assert.IsTrue(q.Rotate(Vec3.UnitZ).Y < -0.999, "looks straight down");
            Assert.IsTrue(q.Rotate(Vec3.UnitY).Z > 0.999, "F up on screen");
            double yaw, pitch;
            LabPlan.YawPitch(q, out yaw, out pitch);
            Assert.AreEqual(-90, pitch, 1e-6);
            Assert.IsFalse(p.TryPose("P9", out pos, out q));
            LabPlan east = LabPlan.Make(b, -152, 90, 6);
            Assert.IsTrue((east.SeamothSpot - new Vec3(-1029, -152, -1122)).Length < 1e-9, "heading 90: F = +X, right = -Z");
        }

        [TestMethod]
        public void S3Lab_CaptureList_RepeatsBurstsErrors()
        {
            List<CaptureStep> s;
            string error;
            Assert.IsTrue(CaptureList.TryParse("P1,P2,P3,P3+1s,P4", out s, out error), error);
            Assert.AreEqual(5, s.Count);
            Assert.AreEqual("P3b", s[3].Label);
            Assert.IsTrue(s[3].Repeat);
            Assert.AreEqual(1.0, s[3].MinGapS);
            Assert.IsTrue(CaptureList.TryParse(new List<object> { "p3*3" }, out s, out error), error);
            Assert.AreEqual(3, s.Count);
            Assert.AreEqual("P3_000", s[0].Label); Assert.IsFalse(s[0].Repeat); Assert.IsTrue(s[2].Repeat);
            Assert.IsTrue(CaptureList.TryParse(null, out s, out error));
            Assert.AreEqual(4, s.Count, "default: P1..P4");
            Assert.IsFalse(CaptureList.TryParse("P1,P3+1s", out s, out error), "a repeat must follow its own pose");
            Assert.IsFalse(CaptureList.TryParse("P5", out s, out error));
            Assert.IsFalse(CaptureList.TryParse("P3*0", out s, out error));
        }

        [TestMethod]
        public void S3Args_Options_Mutants()
        {
            var argv = new Dictionary<string, object> { { "argv", new List<object> { "probe", "x=-1029", "z=-1116.5", "seamoth=true", "name=lab" } } };
            Dictionary<string, object> a = S3Args.ToDict(argv);
            Assert.AreEqual("probe", a["action"]);
            Assert.AreEqual(-1116.5, S3Args.Num(a, "z", 0));
            Assert.IsTrue(S3Args.Flag(a, "seamoth", false));
            Assert.AreEqual("lab", S3Args.Str(a, "name", null));
            double x, y, z;
            Assert.IsTrue(S3Args.TryVec(new List<object> { -1029.0, -1116.0 }, out x, out y, out z));
            Assert.IsTrue(double.IsNaN(y)); Assert.AreEqual(-1116.0, z);
            Assert.IsTrue(S3Args.TryVec("1,2,3", out x, out y, out z)); Assert.AreEqual(2.0, y);
            Assert.IsTrue(S3Args.TryVec(new Dictionary<string, object> { { "x", 1.0 }, { "z", 3.0 } }, out x, out y, out z));
            Assert.IsFalse(S3Args.TryVec("a,b", out x, out y, out z));

            var o = new S3Options();
            bool rebuild, reload;
            string error;
            Assert.IsTrue(o.TryApply(new Dictionary<string, object> { { "uv", "renderer" }, { "vFlip", true } }, out rebuild, out reload, out error), error);
            Assert.IsTrue(rebuild); Assert.IsFalse(reload);
            Assert.AreEqual(UvRule.Renderer, o.Uv);
            Assert.IsTrue(o.TryApply(new Dictionary<string, object> { { "normalFlipY", "on" }, { "maxTextureSide", 512.0 } }, out rebuild, out reload, out error), error);
            Assert.IsTrue(reload);
            Assert.AreEqual(512, o.MaxTextureSide);
            Assert.IsFalse(o.TryApply(new Dictionary<string, object> { { "uv", "sideways" } }, out rebuild, out reload, out error));

            string err;
            try
            {
                Assert.IsTrue(DevMutants.Set(DevMutants.S3TriSwapDropped, true, out err), err);
                Assert.IsTrue(DevMutants.Set(DevMutants.S3PreviousSnapshot, true, out err), err);
                S3MutantSet m = S3MutantSet.Current();
                Assert.IsTrue(m.M2 && m.M4 && !m.M1);
                Assert.AreEqual("S3.M2,S3.M4", m.Describe());
                BuildOptions b = o.Build(m);
                Assert.IsFalse(b.SwapTris); Assert.IsTrue(b.MirrorZ);
                foreach (string name in new[] { "S3.M1", "S3.M3", "S3.M5", "S3.M6", "S3.M7", "S3.M8" }) Assert.IsTrue(DevMutants.IsKnown(name), name);
            }
            finally
            {
                DevMutants.Set(DevMutants.S3TriSwapDropped, false, out err);
                DevMutants.Set(DevMutants.S3PreviousSnapshot, false, out err);
            }
            Assert.AreEqual("none", S3MutantSet.Current().Describe());
            Assert.AreEqual(48, Marshal.SizeOf(typeof(MirrorVertex)), "the uploaded vertex is 48 B");
        }
    }
}
