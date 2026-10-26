using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S3 "First block", SE producer, game-independent parts (ROADMAP S3 "SE plugin (S3b)"): the armor pattern UV math
 /// (SE's shader formula, and SE's own GetTextureOffset being always 0), model/material keys, the C3 model payload
 /// and its chunking/hash, MaterialDef strings, the C4 records, the per-tick budgeted pump (back-pressure, budget,
 /// order, drops) alone and into a real byte ring with wraps, the part diff and debounce, the stream gate (hello ack,
 /// resend), the lab placement, the de.s3 syntax (C11 and chat), the S3.M4 mutant, the JSONL pose row, the link's
 /// C2/C3/C4 writes in a real section, the relay-with-reply used by h=raycast, and everything on the wire checked byte
 /// for byte against tests/wire/layout_py.py and tools/worldmap/wire_ref.py through s3_oracle.py (python -I).
 /// </summary>
    [TestClass]
    public unsafe class S3Tests
    {
        public TestContext TestContext { get; set; }

        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); }

        // ================================================================================================== helpers

 /// <summary>A 2.5 m cube (24 vertices, 12 triangles, 2 submeshes) in SE model space with UVs and a tangent frame.</summary>
        internal static ExportedModel Cube(ulong modelKey = 0x1122334455667788UL, float h = 1.25f)
        {
            var pos = new List<float>(); var nrm = new List<float>(); var tan = new List<float>(); var bin = new List<float>(); var uv = new List<float>();
            var idx = new List<int>();
            int[][] axes = { new[] { 0, 1, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 } };
            foreach (int[] a in axes)
                foreach (int sign in new[] { 1, -1 })
                {
                    int b = pos.Count / 3;
                    for (int c = 0; c < 4; c++)
                    {
                        float[] p = new float[3], n = new float[3], t = new float[3], bn = new float[3];
                        float u = (c & 1) != 0 ? 1 : 0, v = (c & 2) != 0 ? 1 : 0;
                        p[a[0]] = sign * h; p[a[1]] = (2 * u - 1) * h * sign; p[a[2]] = (2 * v - 1) * h;
                        n[a[0]] = sign; t[a[1]] = sign; bn[a[2]] = 1;
                        pos.AddRange(p); nrm.AddRange(n); tan.AddRange(t); bin.AddRange(bn); uv.Add(u); uv.Add(v);
                    }
                    idx.AddRange(new[] { b, b + 1, b + 3, b, b + 3, b + 2 });
                }
            return new ExportedModel
            {
                AssetName = @"Models\Test\Cube.mwm",
                ModelKey = modelKey,
                PatternScale = 4f,
                VertexCount = pos.Count / 3,
                SeVertexCount = pos.Count / 3,
                SeTriangleCount = idx.Count / 3,
                Positions = pos.ToArray(), Normals = nrm.ToArray(), Tangents = tan.ToArray(), Binormals = bin.ToArray(), Uvs = uv.ToArray(),
                Indices = idx.ToArray(),
                HasTangents = true,
                Submeshes = new[]
                {
                    new ExportedSubmesh { MaterialKey = 0xAAAAUL, TriStart = 0, TriCount = 8, DrawTechnique = 0 },
                    new ExportedSubmesh { MaterialKey = 0xBBBBUL, TriStart = 8, TriCount = 4, DrawTechnique = 3 },
                },
                Materials = new MaterialInfo[0],
            };
        }

        internal static PartSnapshot Part(ulong modelKey, float tx, float ty, float tz, float rot = 0f, float hue = 0f)
        {
            var p = new PartSnapshot
            {
                ModelKey = modelKey,
                AssetName = @"Models\Test\Cube.mwm",
                ModelScale = 1f,
                Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, tx, ty, tz },
                H = hue, S = -1f, V = 0f,
                UvU = 0.25f, UvV = 0.5f,
            };
            p.PartKey = PartDiff.PartKey(modelKey, tx, ty, tz, rot);
            return p;
        }

        private sealed class FakeSink : IMessageSink
        {
            public long Capacity = long.MaxValue;
            public long Used;
            public RingWriteStatus Force = RingWriteStatus.Ok;
            public readonly List<KeyValuePair<uint, byte[]>> Got = new List<KeyValuePair<uint, byte[]>>();
            public int Calls;
            public RingWriteStatus TryWrite(uint type, byte[] payload, int count)
            {
                Calls++;
                if (Force != RingWriteStatus.Ok) return Force;
                if (count > 1000) return RingWriteStatus.TooLarge;
                if (Used + count > Capacity) return RingWriteStatus.Full;
                Used += count;
                var b = new byte[count];
                Buffer.BlockCopy(payload, 0, b, 0, count);
                Got.Add(new KeyValuePair<uint, byte[]>(type, b));
                return RingWriteStatus.Ok;
            }
        }

        private sealed class RingSink : IMessageSink
        {
            public ByteRing Ring;
            public RingWriteStatus TryWrite(uint type, byte[] payload, int count) { return Ring.TryWrite(type, payload, 0, count); }
        }

        private static OutMessage Msg(int n, int size, int group = 0)
        {
            var b = new byte[size];
            for (int i = 0; i < size; i++) b[i] = (byte)(n * 31 + i);
            return new OutMessage(1, b, group, "m" + n);
        }

        private static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

        private static T Rd<T>(byte[] b) where T : unmanaged
        {
            T r;
            Assert.IsTrue(ModelCodec.TryRead(b, 0, b.Length, out r), typeof(T).Name + " size " + b.Length);
            return r;
        }

        private static byte[] Payload(ExportedModel m) { return ModelCodec.EncodePayload(S3Codec.ToGeometry(m)); }
        private static string HexKey(ulong v) { return "0x" + v.ToString("x16", CultureInfo.InvariantCulture); }
        private static string G(double v) { return v.ToString("G17", CultureInfo.InvariantCulture); }

        private static void Floats(StringBuilder sb, IEnumerable<float> fs)
        {
            sb.Append('[');
            bool first = true;
            foreach (float f in fs) { if (!first) sb.Append(','); first = false; sb.Append(G(f)); }
            sb.Append(']');
        }

        // ============================================================================================ UV offsets

        [TestMethod]
        public void UvOffset_IsSeShaderFormula_AndSeGetTextureOffsetIsAlwaysZero()
        {
            // SetTextureOffset(new Vector4UByte(x, y, z, w)) then the renderer's arithmetic: (x / z, y / w)
            for (int z = 1; z <= 16; z++)
                for (int w = 1; w <= 16; w++)
                    for (int x = 0; x < z; x++)
                        for (int y = 0; y < w; y++)
                        {
                            byte w5, w6, w7;
                            PartUv.Pack((byte)x, (byte)y, (byte)z, (byte)w, out w5, out w6, out w7);
                            float u, v;
                            PartUv.Offset(w5, w6, w7, out u, out v);
                            Assert.AreEqual((float)x / z, u, "u of " + x + "/" + z);
                            Assert.AreEqual((float)y / w, v, "v of " + y + "/" + w);
                        }
            // SquarePlate: PatternSize 4 x 2 times PatternScale 4 = 16 x 8 tiles; offsets 5 and 3
            byte a5, a6, a7;
            PartUv.Pack(5, 3, 16, 8, out a5, out a6, out a7);
            float ou, ov;
            PartUv.Offset(a5, a6, a7, out ou, out ov);
            Assert.AreEqual(5f / 16f, ou);
            Assert.AreEqual(3f / 8f, ov);
            float ru, rv;
            PartUv.Apply(1f, 0.5f, 4f, ou, ov, out ru, out rv);
            Assert.AreEqual(0.25f + 5f / 16f, ru, 1e-7f, "uv / patternScale + offset");
            Assert.AreEqual(0.125f + 3f / 8f, rv, 1e-7f);
            // the decompiled MyCubeInstanceData.GetTextureOffset divides by ((b >> 4) & 0x10), always 0 for a byte
            for (int b = 0; b < 256; b++) Assert.AreEqual(0f, PartUv.LegacyGetTextureOffset((byte)b), "byte " + b);
        }

        // ============================================================================================ keys

        [TestMethod]
        public void Keys_ModelAndMaterial_NormalisedAndDistinct()
        {
            ulong a = S3Keys.ModelKey(@"Models\Cubes\Large\Armor\SquarePlate.mwm", 0, 1f);
            Assert.AreEqual(a, S3Keys.ModelKey(" models/cubes/large/armor/squareplate.MWM ", 0, 1f), "case, slashes and blanks do not matter");
            Assert.AreNotEqual(a, S3Keys.ModelKey(@"Models\Cubes\Large\Armor\SquarePlate.mwm", 1, 1f), "the LOD is part of the key");
            Assert.AreNotEqual(a, S3Keys.ModelKey(@"Models\Cubes\Large\Armor\SquarePlate.mwm", 0, 0.5f), "a rescaled model is another model");
            Assert.AreEqual(@"models\cubes\large\armor\squareplate.mwm|lod0", S3Keys.ModelKeyText(@"Models\Cubes\Large\Armor\SquarePlate.mwm", 0, 1f));
            Assert.AreEqual(Fnv.Of(@"models\cubes\large\armor\squareplate.mwm|lod0"), a);
            Assert.AreEqual(0xAF63DC4C8601EC8CUL, Fnv.Of("a"), "FNV-1a 64 test vector");
            ulong m1 = S3Keys.MaterialKey(@"Textures\a_cm.dds", @"Textures\a_ng.dds", @"Textures\a_add.dds", null, 0, 0);
            Assert.AreEqual(m1, S3Keys.MaterialKey(@"textures/A_CM.dds", @"Textures\a_ng.dds", @"Textures\a_add.dds", "", 0, 0));
            Assert.AreNotEqual(m1, S3Keys.MaterialKey(@"Textures\a_cm.dds", @"Textures\a_ng.dds", @"Textures\a_add.dds", null, 3, 0), "technique matters");
            ulong k1 = PartDiff.PartKey(a, 0, 0, 0, 0.5f), k2 = PartDiff.PartKey(a, 2.5f, 0, 0, 0.5f), k3 = PartDiff.PartKey(a, 0, 0, 0, 0.25f);
            Assert.IsTrue(k1 != k2 && k1 != k3 && k2 != k3, "placement and rotation change the part key");
            Assert.AreEqual(k1, PartDiff.PartKey(a, 0, 0, 0, 0.5f), "stable");
        }

        // ============================================================================================ C3

        [TestMethod]
        public void ModelPayload_EncodeDecode_Chunks_Hash()
        {
            ExportedModel m = Cube();
            Assert.IsNull(m.Validate());
            byte[] payload = Payload(m);
            Assert.AreEqual(24 * 56 + 36 * 4 + 2 * 24, payload.Length, "56 B vertices, u32 indices, 24 B submeshes");
            ModelGeometry d;
            string err;
            Assert.IsTrue(ModelCodec.TryDecodePayload(ModelCodec.MakeBegin(S3Codec.ToGeometry(m), 1 << 20), payload, 0, payload.Length, out d, out err), err);
            for (int i = 0; i < 24; i++)
            {
                Assert.AreEqual(m.Positions[3 * i + 2], d.Positions[3 * i + 2]);
                Assert.AreEqual(m.Normals[3 * i], d.Normals[3 * i]);
                Assert.AreEqual(m.Tangents[3 * i + 1], d.Tangents[3 * i + 1]);
                Assert.AreEqual(m.Binormals[3 * i + 2], d.Binormals[3 * i + 2]);
                Assert.AreEqual(m.Uvs[2 * i + 1], d.Uvs[2 * i + 1]);
            }
            for (int i = 0; i < 36; i++) Assert.AreEqual((uint)m.Indices[i], d.Indices[i], "indices in MyModel.Triangles order, unchanged");
            Assert.AreEqual(0xBBBBUL, d.Submeshes[1].MaterialKey);
            Assert.AreEqual(8u, d.Submeshes[1].TriStart);
            Assert.AreEqual(3u, d.Submeshes[1].DrawTechnique);

            foreach (int chunk in new[] { 100, 1000, payload.Length, 1 << 20 })
            {
                long total;
                List<OutMessage> msgs = S3Codec.ModelMessages(m, chunk, 7, out total);
                Assert.AreEqual((long)payload.Length, total);
                int chunks = (payload.Length + chunk - 1) / chunk;
                Assert.AreEqual(chunks + 2, msgs.Count, "Begin + " + chunks + " chunks + End");
                Assert.IsTrue(msgs.All(x => x.Group == 7), "one group");
                ModelBegin b = Rd<ModelBegin>(msgs[0].Payload);
                Assert.AreEqual(C3Type.ModelBegin, msgs[0].Type);
                Assert.AreEqual(64, msgs[0].Payload.Length);
                Assert.IsTrue(b.modelKey == m.ModelKey && b.lod == 0 && b.vertexCount == 24 && b.indexCount == 36 && b.submeshCount == 2, "counts");
                Assert.IsTrue(b.flags == ModelFlags.HasTangents && b.patternScale == 4f && b.chunkCount == (uint)chunks && b.totalBytes == (uint)payload.Length, "flags, pattern, chunks, total");
                Assert.IsTrue(b.bounds[0] == -1.25f && b.bounds[4] == 1.25f, "bounds min x / max y");
                var joined = new List<byte>();
                for (int c = 0; c < chunks; c++)
                {
                    OutMessage cm = msgs[1 + c];
                    ModelChunkHeader h;
                    fixed (byte* hp = cm.Payload) h = *(ModelChunkHeader*)hp;
                    Assert.IsTrue(cm.Type == C3Type.ModelChunk && h.modelKey == m.ModelKey && h.chunkIndex == (uint)c && h.bytes == (uint)(cm.Payload.Length - 16) && h.bytes <= (uint)chunk);
                    joined.AddRange(cm.Payload.Skip(16));
                }
                CollectionAssert.AreEqual(payload, joined.ToArray(), "chunks concatenate to the payload");
                ModelEnd e = Rd<ModelEnd>(msgs[msgs.Count - 1].Payload);
                Assert.IsTrue(e.modelKey == m.ModelKey && e.payloadHash == Fnv.Add(Fnv.Offset, payload, 0, payload.Length), "payloadHash = FNV-1a 64 of the payload");
            }
            long ignored;
            try { S3Codec.ModelMessages(m, C3Type.MaxChunkBytes + 1, 1, out ignored); Assert.Fail("chunks above 4 MiB refused"); }
            catch (ArgumentOutOfRangeException) { }
            m.Indices[5] = 99;
            Assert.IsNotNull(m.Validate(), "index out of range caught");
            try { S3Codec.ModelMessages(m, 1 << 20, 1, out ignored); Assert.Fail("a bad model is not encoded"); }
            catch (ArgumentException) { }
        }

        [TestMethod]
        public void MaterialDef_FourU16PrefixedStrings()
        {
            var mi = new MaterialInfo
            {
                Key = 0x0102030405060708UL, Name = "SquarePlate", DrawTechnique = 3, Flags = MaterialFlags.AlphaMasked,
                ColorMetal = @"C:\SE\Content\Textures\Models\Cubes\armor\large_square_plate_cm.dds",
                NormalGloss = @"C:\SE\Content\Textures\Models\Cubes\armor\large_square_plate_ng.dds",
                AddMaps = "", AlphaMask = "é",
            };
            OutMessage msg = S3Codec.MaterialDef(mi);
            Assert.AreEqual(C3Type.MaterialDef, msg.Type);
            MaterialDefinition dm;
            string err;
            Assert.IsTrue(ModelCodec.TryDecodeMaterialDef(msg.Payload, 0, msg.Payload.Length, out dm, out err), err);
            Assert.IsTrue(dm.MaterialKey == mi.Key && dm.DrawTechnique == 3 && dm.Flags == MaterialFlags.AlphaMasked);
            CollectionAssert.AreEqual(new[] { mi.ColorMetal, mi.NormalGloss, "", "é" }, new[] { dm.ColorMetal, dm.NormalGloss, dm.AddMaps, dm.AlphaMask }, "cm, ng, add, alpha in order");
            Assert.AreEqual(16 + 2 + mi.ColorMetal.Length + 2 + mi.NormalGloss.Length + 2 + 0 + 2 + 2, msg.Payload.Length, "UTF-8 (é is 2 bytes), no padding between strings");
        }

        // ============================================================================================ C4

        [TestMethod]
        public void C4Records_SizesAndFields()
        {
            PartSnapshot p = Part(0x55UL, 2.5f, 0, -2.5f, 0.75f, 0.3f);
            p.SkinId = 42;
            OutMessage add = S3Codec.PartAddMsg(123456789012345L, p);
            Assert.AreEqual(C4Type.PartAdd, add.Type);
            Assert.AreEqual(100, add.Payload.Length, "PartAdd payload 100 B (CONTRACT)");
            Assert.AreEqual(112L, ByteRing.FramedSize(add.Payload.Length), "112 B framed and padded");
            PartAdd a = Rd<PartAdd>(add.Payload);
            Assert.IsTrue(a.entityId == 123456789012345UL && a.partKey == p.PartKey && a.modelKey == 0x55UL && a.skinId == 42 && a.flags == 0);
            Assert.IsTrue(a.local[9] == 2.5f && a.local[11] == -2.5f && a.local[0] == 1f, "local rows Right, Up, Backward, Translation");
            Assert.IsTrue(a.colorMaskHSV.x == 0.3f && a.colorMaskHSV.y == -1f && a.uvOffset.x == 0.25f && a.uvOffset.y == 0.5f);
            Assert.AreEqual(16, S3Codec.PartRemoveMsg(1, 2).Payload.Length);
            Assert.AreEqual(32, S3Codec.PartRecolorMsg(1, p).Payload.Length);
            Assert.AreEqual(8, S3Codec.EntityRemoveMsg(1).Payload.Length);
            Assert.AreEqual(8, S3Codec.ClearAllMsg(3, 4).Payload.Length);
            OutMessage ga = S3Codec.GridAddMsg(77, true, 0, 9);
            GridAdd g = Rd<GridAdd>(ga.Payload);
            Assert.IsTrue(ga.Payload.Length == 32 && g.entityId == 77 && g.targetClass == GridClass.LargeGrid && g.flags == 0 && g.parentEntityId == 0 && g.worldEpoch == 9);
            ClearAll c = Rd<ClearAll>(S3Codec.ClearAllMsg(3, 4).Payload);
            Assert.IsTrue(c.worldEpoch == 3 && c.linkEpoch == 4);
        }

        // ============================================================================================ pump

        [TestMethod]
        public void Pump_BackPressure_Budget_Order_Drops()
        {
            var q = new OutboundQueue("T");
            for (int i = 0; i < 10; i++) q.Enqueue(Msg(i, 100));
            Assert.AreEqual(10L, q.Enqueued);
            var sink = new FakeSink { Capacity = 350 };
            long t = 0;
            Func<long> clock = () => t;
            PumpResult r = q.Pump(sink, new PumpBudget(2.0, 100, 0), clock);
            Assert.AreEqual(PumpStop.Full, r.Stop, "ring full");
            Assert.AreEqual(3, r.Written);
            Assert.AreEqual(7, q.Count, "the rest stays queued");
            Assert.AreEqual(3L, q.Done);
            Assert.AreEqual(1L, q.FullEvents);
            sink.Capacity = long.MaxValue;
            r = q.Pump(sink, new PumpBudget(2.0, 4, 0), clock);
            Assert.AreEqual(PumpStop.Budget, r.Stop, "message budget");
            Assert.AreEqual(4, r.Written);
            // time budget: each clock read advances 1 ms of Stopwatch ticks
            long msTicks = Stopwatch.Frequency / 1000;
            Func<long> slow = () => { t += msTicks; return t; };
            r = q.Pump(sink, new PumpBudget(2.0, 100, 0), slow);
            Assert.AreEqual(PumpStop.Budget, r.Stop, "2 ms budget");
            Assert.IsTrue(r.Written >= 1 && r.Written < 3, "at least one per tick, then the clock stops it: " + r.Written);
            r = q.Pump(sink, new PumpBudget(2.0, 100, 0), clock);
            Assert.AreEqual(PumpStop.Empty, r.Stop);
            Assert.AreEqual(10, sink.Got.Count, "everything written once");
            for (int i = 0; i < 10; i++) CollectionAssert.AreEqual(Msg(i, 100).Payload, sink.Got[i].Value, "order kept: " + i);
            Assert.AreEqual(0L, q.PendingBytes);
            Assert.AreEqual(10L, q.Done);

            // a message that can never fit drops the rest of its group (one model), not the next group
            q.Enqueue(Msg(20, 10, 5)); q.Enqueue(Msg(21, 5000, 6)); q.Enqueue(Msg(22, 10, 6)); q.Enqueue(Msg(23, 10, 6)); q.Enqueue(Msg(24, 10, 7));
            sink.Got.Clear();
            r = q.Pump(sink, new PumpBudget(2.0, 100, 0), clock);
            Assert.AreEqual(3, r.Dropped, "the TooLarge message and its group");
            Assert.AreEqual(2, sink.Got.Count, "groups 5 and 7 written");
            Assert.AreEqual(3L, q.DroppedTooLarge);
            Assert.AreEqual(q.Enqueued, q.Done);

            // corrupt counters: stop, keep everything
            q.Enqueue(Msg(30, 10));
            sink.Force = RingWriteStatus.Corrupt;
            r = q.Pump(sink, new PumpBudget(2.0, 100, 0), clock);
            Assert.IsTrue(r.Stop == PumpStop.Corrupt && q.Count == 1);
            r = q.Pump(null, new PumpBudget(2.0, 100, 0), clock);
            Assert.IsTrue(r.Stop == PumpStop.Held && q.Count == 1, "no sink: held");
            q.Clear();
            Assert.IsTrue(q.Count == 0 && q.PendingBytes == 0 && q.Done == q.Enqueued, "Clear counts as done");
        }

        [TestMethod]
        public void Pump_IntoARealByteRing_WithWrapsAndBackPressure()
        {
            const int cap = 4096;
            IntPtr mem = Marshal.AllocHGlobal(cap + 16);
            try
            {
                var head = (ulong*)mem;
                var tail = (ulong*)(mem + 8);
                *head = 0; *tail = 0;
                var data = (byte*)(mem + 16);
                var producer = new ByteRing(head, tail, data, cap);
                var consumer = new ByteRing(head, tail, data, cap);
                var sink = new RingSink { Ring = producer };
                var q = new OutboundQueue("C3");
                var rnd = new Random(3);
                var sent = new List<byte[]>();
                for (int i = 0; i < 200; i++)
                {
                    OutMessage m = Msg(i, 1 + rnd.Next(700));
                    q.Enqueue(m);
                    sent.Add(m.Payload);
                }
                var got = new List<byte[]>();
                var buf = new byte[cap];
                int ticks = 0, fulls = 0;
                while ((q.Count > 0 || got.Count < sent.Count) && ticks < 10000)
                {
                    ticks++;
                    PumpResult r = q.Pump(sink, new PumpBudget(2.0, 8, 0), Stopwatch.GetTimestamp);
                    if (r.Stop == PumpStop.Full) fulls++;
                    // the consumer drains only every other tick (back-pressure builds up)
                    if (ticks % 2 == 0)
                    {
                        uint type; int n;
                        while (consumer.TryRead(out type, buf, out n) == RingReadStatus.Ok)
                        {
                            Assert.AreEqual(1u, type);
                            got.Add(buf.Take(n).ToArray());
                        }
                    }
                }
                Assert.AreEqual(sent.Count, got.Count, "every message arrived");
                for (int i = 0; i < sent.Count; i++) CollectionAssert.AreEqual(sent[i], got[i], "message " + i + " intact and in order");
                Assert.IsTrue(fulls > 0, "the ring filled at least once (back-pressure exercised)");
                Assert.IsTrue(*head > (ulong)cap * 4, "the ring wrapped several times");
                Assert.AreEqual(0L, consumer.CorruptCount);
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        // ============================================================================================ parts

        [TestMethod]
        public void PartDiff_AddsRemovesRecolors_AndDebounce()
        {
            var held = new Dictionary<ulong, PartSnapshot>();
            var now = new Dictionary<ulong, PartSnapshot>();
            PartSnapshot a = Part(1, 0, 0, 0), b = Part(1, 2.5f, 0, 0), c = Part(2, 0, 0, 0);
            now[a.PartKey] = a; now[b.PartKey] = b;
            var adds = new List<PartSnapshot>(); var removes = new List<PartSnapshot>(); var recolors = new List<PartSnapshot>();
            PartDiff.Compute(held, now, adds, removes, recolors);
            Assert.IsTrue(adds.Count == 2 && removes.Count == 0 && recolors.Count == 0);

            held = new Dictionary<ulong, PartSnapshot>(now);
            now.Remove(a.PartKey);
            now[c.PartKey] = c;
            PartSnapshot b2 = b; b2.H = 0.5f;
            now[b.PartKey] = b2;
            PartDiff.Compute(held, now, adds, removes, recolors);
            Assert.IsTrue(adds.Count == 1 && adds[0].PartKey == c.PartKey, "c added");
            Assert.IsTrue(removes.Count == 1 && removes[0].PartKey == a.PartKey, "a removed");
            Assert.IsTrue(recolors.Count == 1 && recolors[0].PartKey == b.PartKey && recolors[0].H == 0.5f, "b recoloured");

            held = new Dictionary<ulong, PartSnapshot>(now);
            PartSnapshot c2 = c; c2.UvU = 0.75f;
            now[c.PartKey] = c2;
            PartDiff.Compute(held, now, adds, removes, recolors);
            Assert.IsTrue(adds.Count == 1 && removes.Count == 1 && adds[0].UvU == 0.75f, "a non-colour change is remove + add");

            // debounce: two equal reads in a row before anything is applied
            var tr = new PartSetTracker();
            var r1 = new Dictionary<ulong, PartSnapshot> { { a.PartKey, a } };
            Assert.IsFalse(tr.Offer(r1, 2), "first read: not yet stable");
            Assert.IsTrue(tr.Unsettled(2));
            var r2 = new Dictionary<ulong, PartSnapshot> { { a.PartKey, a }, { b.PartKey, b } };
            Assert.IsFalse(tr.Offer(r2, 2), "changed read: the count restarts");
            Assert.IsTrue(tr.Offer(new Dictionary<ulong, PartSnapshot>(r2), 2), "same read twice: apply");
            tr.Commit(r2);
            Assert.IsFalse(tr.Offer(new Dictionary<ulong, PartSnapshot>(r2), 2), "equal to what is held: nothing to do");
            Assert.IsFalse(tr.Unsettled(2));
            tr.ForgetHeld();
            Assert.AreEqual(0, tr.Held.Count);
            Assert.IsFalse(tr.Offer(new Dictionary<ulong, PartSnapshot>(r2), 2));
            Assert.IsTrue(tr.Offer(new Dictionary<ulong, PartSnapshot>(r2), 2), "after a resend the same set is sent again");
            Assert.AreNotEqual(PartDiff.Signature(r1), PartDiff.Signature(r2));
        }

        // ============================================================================================ stream gate

        [TestMethod]
        public void StreamGate_WaitsForTrustedAck_AndRestartsOnEveryIdentityChange()
        {
            var g = new StreamGate();
            g.Tick(false, 1, 0, 0, 1, 1, 1, 0, 100);
            Assert.IsFalse(g.Streaming, "not linked");
            g.Tick(true, 1, 1, 0, 1, 0, 0, 0, 100);
            Assert.IsFalse(g.Streaming, "no hello");
            g.Tick(true, 1, 1, 0, 1, 1, 1, 0, 100);
            Assert.IsFalse(g.Streaming, "an ack in the C7 of the frame that saw the event is not trusted");
            g.Tick(true, 1, 1, 0, 1, 1, 1, 0, 101);
            Assert.IsFalse(g.Streaming);
            g.Tick(true, 1, 1, 0, 1, 1, 0, 0, 102);
            Assert.IsFalse(g.Streaming, "trusted frame, but no ack");
            g.Tick(true, 1, 1, 0, 1, 1, 1, 0, 103);
            Assert.IsTrue(g.Streaming, "ack after 2 frames");
            Assert.AreEqual("first hello 1", g.TakeResend());
            Assert.IsNull(g.TakeResend(), "taken once");
            g.Tick(true, 1, 1, 0, 1, 1, 1, 0, 104);
            Assert.IsTrue(g.Streaming && g.TakeResend() == null, "steady");

            g.Tick(true, 1, 2, 0, 1, 1, 1, 0, 105);
            Assert.IsFalse(g.Streaming, "new Subnautica generation: wait again");
            g.Tick(true, 1, 2, 0, 1, 1, 1, 0, 107);
            Assert.IsTrue(g.Streaming);
            Assert.AreEqual("new Subnautica generation", g.TakeResend());

            g.Tick(true, 1, 2, 1, 1, 1, 1, 0, 108);
            Assert.IsFalse(g.Streaming, "link epoch 1");
            g.Tick(true, 1, 2, 1, 1, 1, 1, 0, 111);
            Assert.IsFalse(g.Streaming, "linkAckEpoch still 0");
            g.Tick(true, 1, 2, 1, 1, 1, 1, 1, 112);
            Assert.IsTrue(g.Streaming && g.TakeResend() == "link epoch 1");

            g.Tick(true, 2, 2, 1, 1, 1, 1, 1, 113);
            Assert.IsFalse(g.Streaming, "SE re-attached");
            g.Tick(true, 2, 2, 1, 1, 1, 1, 1, 115);
            Assert.IsTrue(g.Streaming && g.TakeResend() == "SE re-attached to the section");

            g.Tick(true, 2, 2, 1, 2, 2, 1, 1, 116);
            Assert.IsFalse(g.Streaming, "new world: ack of the old hello is not enough");
            g.Tick(true, 2, 2, 1, 2, 2, 2, 1, 118);
            Assert.IsTrue(g.Streaming && g.TakeResend() == "world epoch 2");
            Assert.AreEqual(5L, g.Starts);
            g.Tick(false, 0, 0, 0, 0, 0, 0, 0, 0);
            Assert.IsFalse(g.Streaming, "link down resets");
        }

        // ============================================================================================ placement

        [TestMethod]
        public void Placement_GridMatrixMapsTheBlockCentreToTheSeabedPoint()
        {
            SiteMap site = SiteMap.SiteV1Test;
            foreach (double yaw in new[] { 0.0, 90.0, 33.0 })
            {
                SeMatrix m = S3Placement.GridMatrix(site, S3Defaults.LabX, -153.5, S3Defaults.LabZ, yaw);
                Vec3 sn = site.SeToSn(m.Translation);
                Assert.IsTrue((sn - new Vec3(S3Defaults.LabX, -153.5, S3Defaults.LabZ)).Length < 1e-6, "F(T) = (x, h, z): " + sn);
                Assert.AreEqual(1.0, m.Determinant, 1e-12, "proper rotation");
                Vec3 upSn = site.DirSeToSn(m.Translation, m.Up);
                Assert.IsTrue((upSn - Vec3.UnitY).Length < 1e-12, "grid up = SN up: " + upSn);
                Vec3 fwdSn = site.DirSeToSn(m.Translation, m.Forward);
                Assert.IsTrue((fwdSn - S1Math.HeadingSn(yaw)).Length < 1e-12, "grid forward = SN heading " + yaw);
                // the wire pose with P* at the block centre: o = (x, h, z), q = the yaw about SN +Y
                WirePose p = WireMath.Pose(site, m, m.Translation);
                Assert.IsTrue((p.O - new Vec3(S3Defaults.LabX, -153.5, S3Defaults.LabZ)).Length < 1e-6);
                Quat yq = Quat.LookRotation(S1Math.HeadingSn(yaw), Vec3.UnitY);
                Assert.IsTrue(Quat.RotationDistance(p.Q, yq) < 1e-9, "q = yaw rotation");
                // a buried half: the block's bottom face centre is 1.25 m below h in SN
                Vec3 bottom = WireMath.Apply(p, new Vec3(0, -1.25, 0));
                Assert.AreEqual(-153.5 - 1.25, bottom.Y, 1e-9);
            }
            Assert.AreEqual(1, S3Placement.Cells("single").Count);
            List<int[]> plate = S3Placement.Cells("floor", 3, 3);
            Assert.AreEqual(9, plate.Count);
            Assert.IsTrue(plate.All(c => c[1] == 0 && Math.Abs(c[0]) <= 1 && Math.Abs(c[2]) <= 1) && plate.Any(c => c[0] == 0 && c[2] == 0), "3x3 around the origin");
        }

        // ============================================================================================ de.s3 syntax

        private static IDictionary<string, object> Args(string json) { return (IDictionary<string, object>)MiniJson.Parse(json); }

        [TestMethod]
        public void Args_C11AndChat()
        {
            S3PlaceRequest r;
            string why;
            Assert.IsTrue(S3Args.TryParsePlace(Args("{}"), out r, out why), why);
            Assert.IsTrue(r.X == S3Defaults.LabX && r.Z == S3Defaults.LabZ && r.H == S3Defaults.LabSeabedY && r.HFromLabConfig && !r.Raycast, "lab defaults");
            Assert.IsTrue(r.Block == "LargeBlockArmorBlock" && r.Plate == "single" && r.Marker && r.ColorHsv == null && !r.Export && r.Cells().Count == 1);
            Assert.IsTrue(S3Args.TryParsePlace(Args("{\"place\":[-1000,-150,-1100],\"h\":-153.25,\"plate\":\"3x3\",\"yawDeg\":90,\"color\":[0.5,0.2,-0.1],\"marker\":false}"), out r, out why), why);
            Assert.IsTrue(r.X == -1000 && r.Z == -1100 && r.H == -153.25 && !r.HFromLabConfig && r.Plate == "floor" && r.Cells().Count == 9 && r.YawDeg == 90 && !r.Marker && r.ColorHsv[2] == -0.1, "flat form");
            Assert.IsTrue(S3Args.TryParsePlace(Args("{\"place\":[-1000,-150,-1100],\"useY\":true}"), out r, out why) && r.H == -150, "useY");
            Assert.IsTrue(S3Args.TryParsePlace(Args("{\"place\":[5,6],\"h\":\"raycast\"}"), out r, out why) && r.X == 5 && r.Z == 6 && r.Raycast && double.IsNaN(r.H), "raycast");
            Assert.AreEqual("raycast (Subnautica)", r.HSource);
            r.FallBackToLabConfig("harness attached");
            Assert.IsTrue(!r.Raycast && r.HFromLabConfig && r.H == S3Defaults.LabSeabedY && r.HSource.Contains("raycast unavailable: harness attached"), r.HSource);
            Assert.IsTrue(S3Args.TryParsePlace(Args("{\"h\":\"-152\"}"), out r, out why) && r.H == -152, "h as a string number");
            // the launcher's form (src/DeepEngineers.Launcher/launcher.py S3Runner.place): the block centre is exactly sn
            Assert.IsTrue(S3Args.TryParsePlace(Args("{\"place\":{\"sn\":[-1029,-148.75,-1116],\"yawDeg\":30,\"label\":\"plate\",\"plate\":{\"rows\":3,\"cols\":3,\"orientation\":\"wall\"},\"colorHsv\":[0.6,0.3,0.1],\"export\":true}}"), out r, out why), why);
            Assert.IsTrue(r.X == -1029 && r.H == -148.75 && r.Z == -1116 && r.HFromPoint && !r.HFromLabConfig && r.YawDeg == 30 && r.Label == "plate" && r.Export && r.ColorHsv[0] == 0.6, "object form");
            Assert.AreEqual("wall", r.Plate);
            List<int[]> wall = r.Cells();
            Assert.AreEqual(9, wall.Count);
            Assert.IsTrue(wall.All(c => c[2] == 0 && Math.Abs(c[0]) <= 1 && c[1] >= 0 && c[1] <= 2) && wall.Any(c => c[0] == 0 && c[1] == 0), "a wall rising from the requested block");
            Assert.AreEqual("argument (place.sn)", r.HSource);
            Assert.IsTrue(S3Args.TryParsePlace(Args("{\"place\":{\"sn\":[1,2,3],\"glyph\":true}}"), out r, out why) && r.Plate == "single" && r.Marker, "glyph (S3a stand-in only) ignored");
            Assert.IsFalse(S3Args.TryParsePlace(Args("{\"place\":{\"sn\":[1,2]}}"), out r, out why));
            Assert.IsFalse(S3Args.TryParsePlace(Args("{\"place\":{\"sn\":[1,2,3],\"plate\":{\"rows\":3,\"cols\":3,\"orientation\":\"roof\"}}}"), out r, out why));
            Assert.IsFalse(S3Args.TryParsePlace(Args("{\"place\":[1]}"), out r, out why));
            Assert.IsFalse(S3Args.TryParsePlace(Args("{\"h\":\"deep\"}"), out r, out why));
            Assert.IsFalse(S3Args.TryParsePlace(Args("{\"plate\":\"2x2\"}"), out r, out why));
            Assert.IsFalse(S3Args.TryParsePlace(Args("{\"color\":[2,0,0]}"), out r, out why));
            Assert.AreEqual("status", S3Args.ActionOf(Args("{\"status\":true}")));
            Assert.AreEqual("status", S3Args.ActionOf(Args("{\"state\":true}")), "the launcher's de.s3 {state: true}");
            Assert.AreEqual("remove", S3Args.ActionOf(Args("{\"remove\":true}")));
            Assert.AreEqual("export", S3Args.ActionOf(Args("{\"export\":true}")));
            Assert.AreEqual("trace", S3Args.ActionOf(Args("{\"trace\":5}")));
            Assert.AreEqual("gps", S3Args.ActionOf(Args("{\"gps\":\"on\"}")));
            Assert.AreEqual(true, S3Args.OnOff(Args("{\"gps\":\"on\"}"), "gps"));
            Assert.AreEqual(false, S3Args.OnOff(Args("{\"gps\":\"off\"}"), "gps"));
            Assert.AreEqual("pixel", S3Args.ActionOf(Args("{\"pixel\":{\"corner\":3},\"label\":\"n3.level.c3\"}")));
            Assert.AreEqual("place", S3Args.ActionOf(Args("{\"place\":{\"sn\":[1,2,3],\"export\":true}}")), "a place object wins");
            Assert.AreEqual("place", S3Args.ActionOf(Args("{\"place\":[1,2]}")));

            string usage;
            Dictionary<string, object> a = S3Args.ParseChat("s3 place -1029 -1116 h=-153 plate=3x3 yaw=90 color=0.5,0.2,0.1".Split(' '), out usage);
            Assert.IsNotNull(a, usage);
            Assert.IsTrue(S3Args.TryParsePlace(a, out r, out why), why);
            Assert.IsTrue(r.X == -1029 && r.Z == -1116 && r.H == -153 && r.Plate == "floor" && r.YawDeg == 90 && r.ColorHsv[0] == 0.5, "chat place");
            a = S3Args.ParseChat("s3 -1029 -150 -1116 h=raycast plate=wall".Split(' '), out usage);
            Assert.IsTrue(S3Args.TryParsePlace(a, out r, out why) && r.Raycast && r.X == -1029 && r.Z == -1116 && r.Plate == "wall");
            a = S3Args.ParseChat("s3 place=-1,-2 h=-3".Split(' '), out usage);
            Assert.IsTrue(S3Args.TryParsePlace(a, out r, out why) && r.X == -1 && r.Z == -2 && r.H == -3, "PowerShell-safe key=value place");
            a = S3Args.ParseChat(new[] { "s3" }, out usage);
            Assert.IsTrue(a != null && a.Count == 0 && S3Args.ActionOf(a) == "place", "bare /de s3 places at the lab spot");
            Assert.AreEqual(true, S3Args.ParseChat("s3 status".Split(' '), out usage)["status"]);
            Assert.AreEqual(5.0, S3Args.ParseChat("s3 trace 5".Split(' '), out usage)["trace"]);
            Assert.AreEqual("on", S3Args.ParseChat("s3 gps on".Split(' '), out usage)["gps"]);
            Assert.AreEqual(3.0, MiniJson.GetNumber(MiniJson.GetObject(S3Args.ParseChat("s3 pixel 3".Split(' '), out usage), "pixel"), "corner"));
            Assert.IsNull(S3Args.ParseChat("s3 pixel 9".Split(' '), out usage));
            Assert.IsNull(S3Args.ParseChat("s3 place 1".Split(' '), out usage));
            Assert.IsNull(S3Args.ParseChat("s3 status now".Split(' '), out usage));
            Assert.IsNotNull(usage);

            double h;
            Assert.IsTrue(S3Args.TryProbeHeight(Args("{\"ok\":true,\"seabedY\":-156.5}"), out h, out why) && h == -156.5);
            Assert.AreEqual(S3Args.Probe.Pending, S3Args.ReadProbe(Args("{\"ok\":true,\"pending\":true}"), out h, out why), "collider not built yet: ask again");
            Assert.AreEqual(S3Args.Probe.Height, S3Args.ReadProbe(Args("{\"ok\":true,\"pending\":false,\"seabedY\":-151,\"collider\":\"t\",\"layer\":30}"), out h, out why));
            Assert.AreEqual(-151.0, h);
            Dictionary<string, object> pa = S3Args.ProbeArgs(-1029, -1116);
            Assert.IsTrue((string)pa["action"] == "probe" && (double)pa["x"] == -1029 && (double)pa["z"] == -1116, "Subnautica's de.s3 {action: probe, x, z}");
            Assert.IsFalse(S3Args.TryProbeHeight(Args("{\"ok\":false,\"error\":\"no collider\"}"), out h, out why) || !why.Contains("no collider"));
            Assert.IsFalse(S3Args.TryProbeHeight(null, out h, out why));
            Assert.IsFalse(S3Args.TryProbeHeight(Args("{\"ok\":true}"), out h, out why));

            // de.motion {kind: yaw} (S3.N2): 0.5 Hz, +-30 deg, 10 s, back at 0
            YawPlan yp = YawPlan.Parse(Args("{\"kind\":\"yaw\",\"amplitudeDeg\":30,\"hz\":0.5,\"seconds\":10,\"label\":\"n2.yaw\"}"), out why);
            Assert.IsNotNull(yp, why);
            Assert.AreEqual(600, yp.Ticks);
            Assert.AreEqual(0.0, yp.AngleAt(0), 1e-12);
            Assert.AreEqual(30.0, yp.AngleAt(30), 1e-9, "a quarter period (0.5 s) at 0.5 Hz");
            Assert.AreEqual(-30.0, yp.AngleAt(90), 1e-9);
            Assert.AreEqual(0.0, yp.AngleAt(600), 0.0, "ends exactly at the start heading");
            Assert.IsNull(YawPlan.Parse(Args("{\"hz\":0}"), out why));
            // the corner order of the pixel action and the S3.C2 records (bit 0 max x, bit 1 max y, bit 2 max z)
            Vec3 bmin, bmax;
            S3Placement.CellBounds(wall, 2.5, out bmin, out bmax);
            Assert.IsTrue(bmin.X == -3.75 && bmax.X == 3.75 && bmin.Y == -1.25 && bmax.Y == 6.25 && bmin.Z == -1.25 && bmax.Z == 1.25, "wall box");
            Vec3 c5 = S3Placement.Corner(bmin, bmax, 5);
            Assert.IsTrue(c5.X == 3.75 && c5.Y == -1.25 && c5.Z == 1.25);
        }

        // ============================================================================================ mutants

        [TestMethod]
        public void Mutant_S3M4_Registry()
        {
            string r = Mutants.Set("S3.M4", true);
            Assert.IsTrue(r.StartsWith("S3.M4 ON", StringComparison.Ordinal), r);
            Assert.IsTrue(S3Mutants.PoseOneTickLag && Mutants.Active.Contains("S3.M4"));
            Assert.IsTrue(S1Mutants.CameraFromPluginUpdate && Mutants.Active.Contains("S1.M2"), "the whole snapshot late: C1 through S1.M2's path");
            Assert.IsTrue(Mutants.Set("s3.m4", false).StartsWith("S3.M4 OFF", StringComparison.Ordinal));
            Assert.IsFalse(S3Mutants.PoseOneTickLag);
            Assert.IsFalse(S1Mutants.CameraFromPluginUpdate);
            Assert.IsTrue(Mutants.Set("S3.M1", true).StartsWith("REFUSE", StringComparison.Ordinal), "S3.M1 is a Subnautica-side mutant");
            Mutants.Set("S3.M4", true);
            Mutants.ClearAll();
            Assert.IsFalse(S3Mutants.PoseOneTickLag, "ClearAll clears S3");
        }

        // ============================================================================================ pose row

        private static string PoseRowFor(SiteMap site, double yaw, Vec3 camOffsetSn, bool lagged, out SeMatrix m, out Vec3 pivot)
        {
            m = S3Placement.GridMatrix(site, S3Defaults.LabX, -153.5, S3Defaults.LabZ, yaw);
            var bmin = new Vec3(-1.25, -1.25, -1.25);
            var bmax = new Vec3(1.25, 1.25, 1.25);
            Vec3 cam = site.SnToSe(new Vec3(S3Defaults.LabX, -153.5, S3Defaults.LabZ) + camOffsetSn);
            pivot = WireMath.PivotNearest(m, bmin, bmax, cam);
            WirePose p = WireMath.Pose(site, m, pivot);
            return S3Rows.Pose("2026-10-16T00:00:00Z", 4242, 987654321, m, pivot, cam,
                               new F32x3((float)p.O.X, (float)p.O.Y, (float)p.O.Z), new F32x4((float)p.Q.X, (float)p.Q.Y, (float)p.Q.Z, (float)p.Q.W),
                               (float)p.S, 1f, bmin, bmax, lagged, "first", site);
        }

        [TestMethod]
        public void PoseRow_FormatParses()
        {
            SeMatrix m;
            Vec3 pivot;
            string row = PoseRowFor(SiteMap.SiteV1Test, 30, new Vec3(6, 0.3, 0), false, out m, out pivot);
            var o = (IDictionary<string, object>)MiniJson.Parse(row);
            Assert.AreEqual("s3.pose", o["ev"], "the s3/1 log contract of tests/s3 (analyze_s3.py, check_placement.py)");
            Assert.AreEqual(4242.0, o["tick"]);
            Assert.AreEqual(987654321.0, o["entityId"]);
            Assert.AreEqual(987654321.0, o["gridId"]);
            Assert.AreEqual(6, ((List<object>)o["bounds"]).Count, "bounds = local min, max");
            var site = (IDictionary<string, object>)o["site"];
            Assert.AreEqual(SiteMap.SiteV1Test.Rs, (double)site["Rs"]);
            Assert.AreEqual(SiteMap.SiteV1Test.C.X, (double)((List<object>)site["C"])[0]);
            var wire = (IDictionary<string, object>)o["wire"];
            Assert.AreEqual(((List<object>)o["o"])[1], ((List<object>)wire["o"])[1]);
            var sm = (List<object>)o["seMatrix"];
            Assert.AreEqual(12, sm.Count);
            Assert.AreEqual(m.Translation.X, (double)sm[9], 0.0, "G17 round trip of the SE translation");
            Assert.AreEqual(m.Backward.Z, (double)sm[8], 0.0);
            var pv = (List<object>)o["pivot"];
            Assert.AreEqual(pivot.Y, (double)pv[1], 0.0);
            Assert.AreEqual(3, ((List<object>)o["o"]).Count);
            Assert.AreEqual(4, ((List<object>)o["q"]).Count);
            Assert.IsTrue(o.ContainsKey("s") && o.ContainsKey("k") && o.ContainsKey("camSe") && o.ContainsKey("aabbMin") && o.ContainsKey("aabbMax"));
            Assert.AreEqual(false, o["lagged"]);

            // s3.place (S3.C1 totals) and s3.gps (S3.N2) in the same contract
            WirePose wp = WireMath.Pose(SiteMap.SiteV1Test, m, pivot);
            string place = S3Rows.Place("2026-10-16T00:00:00Z", 4243, 987654321, new Vec3(-1029, -153.5, -1116), 30, "argument", m, pivot,
                                        new WireEntityPose(wp, 1.0), new Vec3(-1.25, -1.25, -1.25), new Vec3(1.25, 1.25, 1.25), 6, 54, 48, 1, SiteMap.SiteV1Test);
            var pl = (IDictionary<string, object>)MiniJson.Parse(place);
            Assert.AreEqual("s3.place", pl["ev"]);
            Assert.IsTrue((double)pl["gridId"] == 987654321 && (double)pl["parts"] == 6 && (double)pl["verts"] == 54 && (double)pl["tris"] == 48 && (double)pl["headingDeg"] == 30);
            Assert.AreEqual(12, ((List<object>)pl["seMatrix"]).Count);
            Assert.AreEqual(3, ((List<object>)pl["labSn"]).Count);
            Assert.AreEqual((double)(float)wp.S, (double)pl["s"], "the float32 wire value");
            string gps = S3Rows.Gps("2026-10-16T00:00:00Z", 4244, 123456789012, 987654321, 1280.25, 719.5, true, m.Translation);
            var gp = (IDictionary<string, object>)MiniJson.Parse(gps);
            Assert.AreEqual("s3.gps", gp["ev"]);
            Assert.IsTrue((double)gp["tick"] == 4244 && (double)gp["qpc"] == 123456789012 && (double)((List<object>)gp["px"])[0] == 1280.25 && (bool)gp["inFront"]);
        }

        // ============================================================================================ python oracle

        [TestMethod]
        public void Oracle_RecordsKeysUvModelAndPoses_AgreeWithLayoutPyAndWireRef()
        {
            var sb = new StringBuilder(1 << 16);
            sb.Append("{\"site\":\"v1\",\"keys\":[");
            string[] texts =
            {
                S3Keys.ModelKeyText(@"Models\Cubes\Large\Armor\SquarePlate.mwm", 0, 1f),
                S3Keys.ModelKeyText(@"Models\Cubes\Large\Armor\SquarePlate.mwm", 0, 0.5f),
                S3Keys.MaterialKeyText(@"Textures\Models\Cubes\armor\large_square_plate_cm.dds", @"Textures\Models\Cubes\armor\large_square_plate_ng.dds",
                                       @"Textures\Models\Cubes\armor\large_square_plate_add.dds", null, 0, 0),
            };
            ulong[] keys = { S3Keys.ModelKey(@"Models\Cubes\Large\Armor\SquarePlate.mwm", 0, 1f), S3Keys.ModelKey(@"Models\Cubes\Large\Armor\SquarePlate.mwm", 0, 0.5f),
                             S3Keys.MaterialKey(@"Textures\Models\Cubes\armor\large_square_plate_cm.dds", @"Textures\Models\Cubes\armor\large_square_plate_ng.dds",
                                                @"Textures\Models\Cubes\armor\large_square_plate_add.dds", null, 0, 0) };
            for (int i = 0; i < texts.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"text\":"); JsonText.AppendString(sb, texts[i]); sb.Append(",\"key\":\"").Append(HexKey(keys[i])).Append("\"}");
            }
            sb.Append("],\"uv\":[");
            var rnd = new Random(7);
            for (int i = 0; i < 64; i++)
            {
                byte w5 = (byte)rnd.Next(16), w6 = (byte)rnd.Next(16), w7 = (byte)rnd.Next(256);
                float u, v;
                PartUv.Offset(w5, w6, w7, out u, out v);
                if (i > 0) sb.Append(',');
                sb.Append("{\"w5\":").Append(w5).Append(",\"w6\":").Append(w6).Append(",\"w7\":").Append(w7).Append(",\"u\":").Append(G(u)).Append(",\"v\":").Append(G(v)).Append('}');
            }
            sb.Append("],\"records\":[");
            PartSnapshot p = Part(keys[0], 2.5f, -2.5f, 0f, 0.125f, 0.33f);
            p.SkinId = 7;
            OutMessage pa = S3Codec.PartAddMsg(123456789012345L, p);
            sb.Append("{\"record\":\"PartAdd\",\"fields\":{\"entityId\":\"").Append(HexKey(123456789012345UL)).Append("\",\"partKey\":\"").Append(HexKey(p.PartKey))
              .Append("\",\"modelKey\":\"").Append(HexKey(p.ModelKey)).Append("\",\"local\":");
            Floats(sb, p.Local);
            sb.Append(",\"colorMaskHSV\":"); Floats(sb, new[] { p.H, p.S, p.V });
            sb.Append(",\"uvOffset\":"); Floats(sb, new[] { p.UvU, p.UvV });
            sb.Append(",\"skinId\":7,\"flags\":0},\"hex\":\"").Append(Hex(pa.Payload)).Append("\"}");
            sb.Append(",{\"record\":\"GridAdd\",\"fields\":{\"entityId\":\"").Append(HexKey(77)).Append("\",\"targetClass\":1,\"flags\":0,\"parentEntityId\":\"0x0\",\"worldEpoch\":9},\"hex\":\"")
              .Append(Hex(S3Codec.GridAddMsg(77, true, 0, 9).Payload)).Append("\"}");
            sb.Append(",{\"record\":\"PartRemove\",\"fields\":{\"entityId\":\"").Append(HexKey(77)).Append("\",\"partKey\":\"").Append(HexKey(p.PartKey)).Append("\"},\"hex\":\"")
              .Append(Hex(S3Codec.PartRemoveMsg(77, p.PartKey).Payload)).Append("\"}");
            sb.Append(",{\"record\":\"PartRecolor\",\"fields\":{\"entityId\":\"").Append(HexKey(77)).Append("\",\"partKey\":\"").Append(HexKey(p.PartKey)).Append("\",\"hsv\":");
            Floats(sb, new[] { p.H, p.S, p.V });
            sb.Append("},\"hex\":\"").Append(Hex(S3Codec.PartRecolorMsg(77, p).Payload)).Append("\"}");
            sb.Append(",{\"record\":\"EntityRemove\",\"fields\":{\"entityId\":\"").Append(HexKey(77)).Append("\"},\"hex\":\"").Append(Hex(S3Codec.EntityRemoveMsg(77).Payload)).Append("\"}");
            sb.Append(",{\"record\":\"ClearAll\",\"fields\":{\"worldEpoch\":3,\"linkEpoch\":4},\"hex\":\"").Append(Hex(S3Codec.ClearAllMsg(3, 4).Payload)).Append("\"}");
            sb.Append("],\"model\":");
            ExportedModel cube = Cube(keys[0]);
            long cubeBytes;
            List<OutMessage> msgs = S3Codec.ModelMessages(cube, 256, 1, out cubeBytes);
            sb.Append("{\"modelKey\":\"").Append(HexKey(cube.ModelKey)).Append("\",\"patternScale\":").Append(G(cube.PatternScale)).Append(",\"hasTangents\":true,\"positions\":");
            Floats(sb, cube.Positions); sb.Append(",\"normals\":"); Floats(sb, cube.Normals); sb.Append(",\"tangents\":"); Floats(sb, cube.Tangents);
            sb.Append(",\"binormals\":"); Floats(sb, cube.Binormals); sb.Append(",\"uvs\":"); Floats(sb, cube.Uvs);
            sb.Append(",\"indices\":[").Append(string.Join(",", cube.Indices.Select(i => i.ToString(CultureInfo.InvariantCulture)))).Append("],\"submeshes\":[");
            for (int i = 0; i < cube.Submeshes.Length; i++)
            {
                ExportedSubmesh s = cube.Submeshes[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"materialKey\":\"").Append(HexKey(s.MaterialKey)).Append("\",\"triStart\":").Append(s.TriStart).Append(",\"triCount\":").Append(s.TriCount).Append(",\"drawTechnique\":").Append(s.DrawTechnique).Append('}');
            }
            sb.Append("],\"chunkBytes\":256,\"messages\":[");
            for (int i = 0; i < msgs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"type\":").Append(msgs[i].Type).Append(",\"hex\":\"").Append(Hex(msgs[i].Payload)).Append("\"}");
            }
            sb.Append("]},\"material\":");
            var mi = new MaterialInfo { Key = keys[2], Name = "SquarePlate", DrawTechnique = 0, Flags = 0, ColorMetal = @"C:\Content\x_cm.dds", NormalGloss = @"C:\Content\x_ng.dds", AddMaps = @"C:\Content\x_add.dds", AlphaMask = "" };
            OutMessage md = S3Codec.MaterialDef(mi);
            sb.Append("{\"materialKey\":\"").Append(HexKey(mi.Key)).Append("\",\"drawTechnique\":0,\"flags\":0,\"strings\":[");
            JsonText.AppendString(sb, mi.ColorMetal); sb.Append(','); JsonText.AppendString(sb, mi.NormalGloss); sb.Append(',');
            JsonText.AppendString(sb, mi.AddMaps); sb.Append(','); JsonText.AppendString(sb, mi.AlphaMask);
            sb.Append("],\"hex\":\"").Append(Hex(md.Payload)).Append("\"},\"poses\":[");
            var cams = new[] { new Vec3(0, 0.3, -6), new Vec3(6, 0.3, 0), new Vec3(0, 10, 0), new Vec3(0, 1, -40), new Vec3(0.2, 0.1, 0.3), new Vec3(-120, 30, 80) };
            int k = 0;
            foreach (double yaw in new[] { 0.0, 37.0 })
                foreach (Vec3 c in cams)
                {
                    SeMatrix m;
                    Vec3 pivot;
                    if (k++ > 0) sb.Append(',');
                    sb.Append(PoseRowFor(SiteMap.SiteV1Test, yaw, c, false, out m, out pivot));
                }
            sb.Append("]}");

            string dir = PyOracle.PathIn("tests", "out", "se-s3");
            Directory.CreateDirectory(dir);
            string inPath = Path.Combine(dir, "s3-oracle-in.json");
            string json = sb.ToString();
            File.WriteAllText(inPath, json, new UTF8Encoding(false));
            string stdout = PyOracle.Run(Path.Combine("tests", "DeepEngineers.SE.Tests", "s3_oracle.py"), inPath);
            TestContext.WriteLine(stdout);
            Assert.IsTrue(stdout.Contains("S3 ORACLE OK"), stdout);

            // the oracle measures something: one flipped PartAdd byte (the last byte of flags) and one pose offset by 1 cm go red
            string badPart = Hex(pa.Payload);
            badPart = badPart.Substring(0, badPart.Length - 2) + "01";
            string bad = json.Replace(Hex(pa.Payload), badPart);
            int oAt = bad.IndexOf("\"o\":[", StringComparison.Ordinal) + 5;
            int comma = bad.IndexOf(',', oAt);
            double o0 = double.Parse(bad.Substring(oAt, comma - oAt), CultureInfo.InvariantCulture);
            bad = bad.Substring(0, oAt) + G(o0 + 0.01) + bad.Substring(comma);
            string badPath = Path.Combine(dir, "s3-oracle-seeded.json");
            File.WriteAllText(badPath, bad, new UTF8Encoding(false));
            try
            {
                string o2 = PyOracle.Run(Path.Combine("tests", "DeepEngineers.SE.Tests", "s3_oracle.py"), badPath);
                Assert.Fail("seeded errors not caught: " + o2);
            }
            catch (InvalidOperationException e)
            {
                TestContext.WriteLine(e.Message);
                Assert.IsTrue(e.Message.Contains("S3 ORACLE FAIL") && e.Message.Contains("PartAdd bytes differ") && e.Message.Contains("o ["), e.Message);
            }
        }

        // ============================================================================================ link

        private sealed class LinkC3 : IMessageSink
        {
            public Link L;
            public bool C3;
            public RingWriteStatus TryWrite(uint type, byte[] payload, int count) { return C3 ? L.WriteC3(type, payload, count) : L.WriteC4(type, payload, count); }
        }

        [TestMethod]
        public void Link_WritesC2C3C4_InARealSection_AndCallsTheAfterC1Hook()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                Assert.AreEqual(RingWriteStatus.Full, link.WriteC3(C3Type.ModelEnd, new byte[16], 16), "not linked: nothing written, the caller keeps it");
                Assert.IsFalse(link.WriteSeEntities(1, 1, new SeEntity[1], 1));
                var input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    byte* b = sn.Base;
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(LinkState.Up, link.State);

                    // C3 and C4 through the budgeted pump; Subnautica's consumers read them back
                    var c3q = new OutboundQueue("C3");
                    var c4q = new OutboundQueue("C4");
                    ExportedModel cube = Cube();
                    byte[] payload = Payload(cube);
                    long cubeBytes;
                    c3q.Enqueue(S3Codec.MaterialDef(new MaterialInfo { Key = 0xAAAAUL, Name = "a", ColorMetal = "x" }));
                    c3q.EnqueueRange(S3Codec.ModelMessages(cube, 512, 1, out cubeBytes));
                    c4q.Enqueue(S3Codec.ClearAllMsg(1, 0));
                    c4q.Enqueue(S3Codec.GridAddMsg(55, true, 0, 1));
                    c4q.Enqueue(S3Codec.PartAddMsg(55, Part(cube.ModelKey, 0, 0, 0)));
                    PumpResult r4 = c4q.Pump(new LinkC3 { L = link, C3 = false }, new PumpBudget(1, 100, 0), Stopwatch.GetTimestamp);
                    PumpResult r3 = c3q.Pump(new LinkC3 { L = link, C3 = true }, new PumpBudget(2, 100, 0), Stopwatch.GetTimestamp);
                    Assert.IsTrue(r4.Stop == PumpStop.Empty && r4.Written == 3 && r3.Stop == PumpStop.Empty && r3.Written == c3q.TotalWritten, "all written");
                    var snC3 = new ByteRing(b, RingIndex.C3SeModels);
                    var snC4 = new ByteRing(b, RingIndex.C4SeParts);
                    var buf = new byte[1 << 16];
                    uint type; int n;
                    var c4types = new List<uint>();
                    while (snC4.TryRead(out type, buf, out n) == RingReadStatus.Ok) c4types.Add(type);
                    CollectionAssert.AreEqual(new[] { C4Type.ClearAll, C4Type.GridAdd, C4Type.PartAdd }, c4types, "C4 order");
                    var joined = new List<byte>();
                    var c3types = new List<uint>();
                    while (snC3.TryRead(out type, buf, out n) == RingReadStatus.Ok)
                    {
                        c3types.Add(type);
                        if (type == C3Type.ModelChunk) joined.AddRange(buf.Skip(16).Take(n - 16));
                    }
                    Assert.AreEqual(C3Type.MaterialDef, c3types[0], "material first");
                    Assert.AreEqual(C3Type.ModelBegin, c3types[1]);
                    Assert.AreEqual(C3Type.ModelEnd, c3types[c3types.Count - 1]);
                    CollectionAssert.AreEqual(payload, joined.ToArray(), "the model payload arrives intact");
                    ulong h3, t3, h4, t4;
                    link.RingCounters(out h3, out t3, out h4, out t4);
                    Assert.IsTrue(h3 > 0 && h4 > 0 && t3 == h3 && t4 == h4, "counters: consumed to the head");

                    // C2 for the C1 tick, written by the before-C1 hook: C2(N) is published before C1(N) (review
 // finding 10: a reader that saw C1(N) must find C2(N))
                    ulong hooked = 0;
                    long hookedQpc = 0;
                    ulong c1TickInHook = ulong.MaxValue;
                    byte* c1 = Layout.RegionPtr(b, RegionId.C1SeFrame);
                    link.BeforeSeFrame = (tick, qpc) =>
                    {
                        hooked = tick;
                        hookedQpc = qpc;
                        c1TickInHook = ((SeFrame*)c1)->tick;
                        var e = new SeEntity { entityId = 55, kind = SeEntityKind.RootGrid, flags = SeEntityFlags.Static, groupId = 55, o = new F32x3(-1029, -153.5f, -1116), q = new F32x4(0, 0, 0, 1), s = 1.0025f, k = 1, partsVersion = 3 };
                        Assert.IsTrue(link.WriteSeEntities(tick, 1, new[] { e }, 1));
                    };
                    var frame = new SeFrame { tick = 777, worldEpoch = 1, qpcCapture = 123456789 };
                    Assert.IsTrue(link.WriteSeFrame(ref frame));
                    Assert.AreEqual(777UL, hooked, "the hook gets C1's tick");
                    Assert.AreEqual(123456789L, hookedQpc, "and its qpcCapture");
                    Assert.AreNotEqual(777UL, c1TickInHook, "C2 is written before C1: C1 still held the previous tick inside the hook");
                    Assert.AreEqual(777UL, ((SeFrame*)c1)->tick, "C1 written after the hook");
                    byte* c2 = Layout.RegionPtr(b, RegionId.C2SeEntities);
                    uint seq;
                    Assert.IsTrue(Seqlock.ReadBegin(c2, out seq), "C2 published (even, non-zero)");
                    var hdr = *(SeEntitiesHeader*)c2;
                    var ent = *(SeEntity*)(c2 + SeEntitiesLimits.EntitiesOffset);
                    Assert.IsTrue(Seqlock.ReadEnd(c2, seq));
                    Assert.IsTrue(hdr.entityCount == 1 && hdr.tick == 777 && hdr.worldEpoch == 1 && hdr.subpartCount == 0 && hdr.lightCount == 0, "C2 header, same tick as C1");
                    Assert.IsTrue(ent.entityId == 55 && ent.kind == SeEntityKind.RootGrid && ent.flags == SeEntityFlags.Static && ent.o.y == -153.5f && ent.s == 1.0025f && ent.partsVersion == 3, "C2 entity");
                    link.BeforeSeFrame = (tick, qpc) => { throw new InvalidOperationException("boom"); };
                    Assert.IsTrue(link.WriteSeFrame(ref frame), "a throwing hook never breaks C1");
                    Assert.IsTrue(rig.Has("DE.Error before-C1 hook"));
                    try { link.WriteSeEntities(1, 1, new SeEntity[1], 2); Assert.Fail("count beyond the array refused"); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
        }

        [TestMethod]
        public void DevChannel_RelayForReply_CallsBackWithTheReply_OrNullOnTimeout()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var dev = new DevChannel(link) { ShowChat = (s, t) => rig.Chat.Add(t) };
                string why;
                IDictionary<string, object> got = null;
                int calls = 0;
                Assert.IsFalse(dev.RelayForReply("de.s3", null, rig.Now, x => { calls++; got = x; }, out why), "not linked");
                var input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    byte* b = sn.Base;
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv, dev: dev);
                    hv.DevControl = DevControl.HarnessAttached;
                    Assert.IsFalse(dev.RelayForReply("de.s3", null, rig.Now, x => { calls++; got = x; }, out why), "harness attached: SE cannot write CtlToSn");
                    Assert.IsTrue(why.Contains("harness"), why);
                    hv.DevControl = 0;
                    Dictionary<string, object> args = S3Args.ProbeArgs(-1029.0, -1116.0);
                    Assert.IsTrue(dev.RelayForReply("de.s3", args, rig.Now, x => { calls++; got = x; }, out why), why);
                    var toSn = new ByteRing(b, RingIndex.C11cCtlToSn);
                    var fromSn = new ByteRing(b, RingIndex.C11dCtlFromSn);
                    var buf = new byte[4096];
                    uint type; int n;
                    Assert.AreEqual(RingReadStatus.Ok, toSn.TryRead(out type, buf, out n));
                    var req = (IDictionary<string, object>)MiniJson.Parse(Encoding.UTF8.GetString(buf, 0, n));
                    Assert.AreEqual("de.s3", MiniJson.GetString(req, "cmd"));
                    Assert.AreEqual(-1029.0, MiniJson.GetNumber(MiniJson.GetObject(req, "args"), "x"));
                    Assert.AreEqual("probe", MiniJson.GetString(MiniJson.GetObject(req, "args"), "action"));
                    byte[] reply = Encoding.UTF8.GetBytes("{\"id\":" + MiniJson.GetNumber(req, "id") + ",\"ok\":true,\"seabedY\":-156.25}");
                    fromSn.TryWrite(C11Type.Json, reply, 0, reply.Length);
                    rig.Run(link, ref input, 50, hv, dev: dev);
                    Assert.AreEqual(1, calls, "called once");
                    double h;
                    Assert.IsTrue(S3Args.TryProbeHeight(got, out h, out why) && h == -156.25, "the parsed reply reaches the handler");

                    calls = 0; got = new Dictionary<string, object>();
                    Assert.IsTrue(dev.RelayForReply("de.s3", args, rig.Now, x => { calls++; got = x; }, out why), why);
                    rig.Run(link, ref input, 31000, hv, dev: dev);
                    Assert.IsTrue(calls == 1 && got == null, "timeout: the handler gets null");
                }
            }
        }

        [TestMethod]
        public void ChatVerb_S3_BuildsTheC11Arguments()
        {
            using (var rig = new Rig(TestContext))
            {
                var calls = new List<KeyValuePair<string, IDictionary<string, object>>>();
                var dev = new DevChannel(new Link(() => true, Rig.Name(), Rig.MutexName()))
                {
                    ShowChat = (s, t) => rig.Chat.Add(t),
                    ExtraCommand = (cmd, args, via) =>
                    {
                        calls.Add(new KeyValuePair<string, IDictionary<string, object>>(cmd, args));
                        return new Dictionary<string, object> { { "ok", true }, { "line", "stub " + cmd } };
                    },
                };
                Assert.IsTrue(dev.HandleChat("/de s3 place -1029 -1116 h=-153 plate=3x3", rig.Now));
                Assert.IsTrue(dev.HandleChat("/de s3 status", rig.Now));
                Assert.IsTrue(dev.HandleChat("/de s3 place 1", rig.Now), "bad syntax is still ours");
                Assert.AreEqual(2, calls.Count, "the bad one shows the usage only");
                Assert.AreEqual("de.s3", calls[0].Key);
                S3PlaceRequest r;
                string why;
                Assert.IsTrue(S3Args.TryParsePlace(calls[0].Value, out r, out why) && r.H == -153 && r.Plate == "floor" && r.Cells().Count == 9);
                Assert.AreEqual("status", S3Args.ActionOf(calls[1].Value));
                Assert.IsTrue(rig.Chat.Any(c => c.StartsWith("usage: /de s3", StringComparison.Ordinal)));
                Assert.IsTrue(dev.HandleChat("/de help", rig.Now) && rig.Chat.Any(c => c.StartsWith("/de s3", StringComparison.Ordinal)), "help lists /de s3");
            }
        }
    }
}
