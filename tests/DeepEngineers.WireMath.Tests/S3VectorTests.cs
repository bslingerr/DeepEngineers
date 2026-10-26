using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>
 /// ROADMAP S3: the C# conventions and codecs (PoseMath, MeshConventions, ModelCodec in DeepEngineers.Wire) against the
 /// independent Python reference (tools/worldmap/wire_ref.py <c>s3vectors</c>, generated on the fly into tests/out/s3/,
 /// never committed): pose (o, q, s) from an SE matrix and P* with the entity scale k and its 8 corners, the affine-vs-F
 /// bound, the rigid-draw error, the cube model payload and its C3 messages byte for byte, model and material keys,
 /// MaterialDef and PartAdd payloads byte for byte, tangent w, armor UVs and pattern offsets.
 /// </summary>
    [TestClass]
    public class S3VectorTests
    {
        private static List<Dictionary<string, object>> _records;
        private static readonly object Gate = new object();

        private static List<Dictionary<string, object>> Records()
        {
            lock (Gate)
            {
                if (_records != null) return _records;
                string dir = RepoEnv.PathIn("tests", "out", "s3");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "s3-vectors.jsonl");
                string tmp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
                string output = RepoEnv.RunPython(Path.Combine("tools", "worldmap", "wire_ref.py"), "s3vectors", tmp);
                File.Copy(tmp, path, true);
                File.Delete(tmp);
                Console.WriteLine(output.Trim());
                var recs = new List<Dictionary<string, object>>();
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length > 0) recs.Add((Dictionary<string, object>)MiniJson.Parse(line));
                }
                _records = recs;
                return recs;
            }
        }

        private static IEnumerable<Dictionary<string, object>> Kind(string kind)
        {
            return Records().Where(r => J.S(r, "kind") == kind);
        }

        private static ulong U64(Dictionary<string, object> r, string key)
        {
            return ulong.Parse(J.S(r, key), CultureInfo.InvariantCulture);
        }

        private static byte[] Hex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(hex.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        private static string HexOf(byte[] b)
        {
            return string.Concat(b.Select(x => x.ToString("x2")));
        }

        private static List<Vec3> V3s(Dictionary<string, object> r, string key)
        {
            return J.Arr(r, key).Select(J.V3).ToList();
        }

        private static float F(object o) { return (float)(double)o; }

        [TestMethod]
        public void S3Vectors_FileHasEveryKind()
        {
            var counts = Records().GroupBy(r => J.S(r, "kind")).ToDictionary(g => g.Key, g => g.Count());
            Assert.AreEqual(1, counts["header"]);
            Assert.AreEqual(122, counts["pose_k"]);
            Assert.AreEqual(1, counts["mesh_cube"]);
            Assert.AreEqual(64, counts["tangent"]);
            Assert.AreEqual(12, counts["model_key"]);
            Assert.AreEqual(3, counts["material"]);
            Assert.AreEqual(16, counts["part_add"]);
            Assert.AreEqual(3, counts["cube_part_key"]);
            Assert.AreEqual(16, counts["uv"]);
        }

        [TestMethod]
        public void S3Vectors_PoseWithK_CornersBoundAndRigidError()
        {
            SiteMap site = SiteMap.SiteV1Test;
            double wo = 0, wq = 0, ws = 0, wc = 0, wb = 0, wr = 0, worstRatio = 0;
            foreach (var r in Kind("pose_k"))
            {
                SeMatrix m = J.Matrix(r, "se_matrix");
                Vec3 pivot = J.V3(r, "pivot_se");
                double k = J.D(r, "k");
                WireEntityPose p = PoseMath.FromSe(site, m, pivot, k);
                wo = Math.Max(wo, (p.O - J.V3(r, "o")).Length);
                wq = Math.Max(wq, Quat.RotationDistance(p.Q, J.Q4(r, "q")));
                ws = Math.Max(ws, Math.Abs(p.S - J.D(r, "s")));
                List<Vec3> corners = V3s(r, "corners_se"), sn = V3s(r, "corners_sn"), exact = V3s(r, "exact_sn");
                List<object> bound = J.Arr(r, "bound"), rigid = J.Arr(r, "rigid_err");
                Vec3[] mine = MeshConventions.AabbCorners(J.V3(r, "box_min"), J.V3(r, "box_max"));
                for (int i = 0; i < 8; i++)
                {
                    Assert.AreEqual(corners[i], mine[i], "corner order");
                    wc = Math.Max(wc, (PoseMath.Apply(p, corners[i]) - sn[i]).Length);
                    Vec3 w = m.Transform(corners[i] * k);
                    double b = PoseMath.AffineVsFBound(site, pivot, w);
                    wb = Math.Max(wb, Math.Abs(b - (double)bound[i]) / Math.Max(1e-12, (double)bound[i]));
                    wr = Math.Max(wr, Math.Abs(PoseMath.RigidDrawError(p.Q, p.S, p.K, corners[i]) - (double)rigid[i]));
                    Assert.IsTrue((site.SeToSn(w) - exact[i]).Length < 1e-6, "F of the corner");
                    worstRatio = Math.Max(worstRatio, (PoseMath.Apply(p, corners[i]) - site.SeToSn(w)).Length / (b + 1e-9));
                }
            }
            Console.WriteLine("pose_k: o " + wo + " q " + wq + " s " + ws + " corners " + wc + " bound(rel) " + wb + " rigid " + wr + " |Apply-F|/bound " + worstRatio);
            Assert.IsTrue(wo < 1e-6, "o (CONTRACT 2.9: 1e-6 m in doubles) " + wo);
            Assert.IsTrue(wq < 1e-9, "q as rotations " + wq);
            Assert.IsTrue(ws < 1e-12, "s " + ws);
            Assert.IsTrue(wc < 1e-6, "Apply(o, q, s, k) corners " + wc);
            Assert.IsTrue(wb < 1e-9, "bound, relative " + wb);
            Assert.IsTrue(wr < 1e-9, "rigid-draw error " + wr);
            Assert.IsTrue(worstRatio <= 1.0, "|Apply - F| within the bound " + worstRatio);
        }

        private static ModelGeometry CubeFrom(Dictionary<string, object> r)
        {
            List<object> pos = J.Arr(r, "positions"), nrm = J.Arr(r, "normals"), tan = J.Arr(r, "tangents"), bin = J.Arr(r, "binormals"), uv = J.Arr(r, "uvs");
            List<object> ix = J.Arr(r, "indices"), sm = J.Arr(r, "submeshes"), bounds = J.Arr(r, "bounds");
            ModelGeometry g = ModelGeometry.Allocate(pos.Count, ix.Count, sm.Count, (uint)J.I(r, "flags"));
            g.ModelKey = U64(r, "model_key");
            g.PatternScale = (float)J.D(r, "pattern_scale");
            for (int i = 0; i < 6; i++) g.Bounds[i] = F(bounds[i]);
            for (int i = 0; i < pos.Count; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    g.Positions[3 * i + c] = F(((List<object>)pos[i])[c]);
                    g.Normals[3 * i + c] = F(((List<object>)nrm[i])[c]);
                    g.Tangents[3 * i + c] = F(((List<object>)tan[i])[c]);
                    g.Binormals[3 * i + c] = F(((List<object>)bin[i])[c]);
                }
                g.Uvs[2 * i] = F(((List<object>)uv[i])[0]);
                g.Uvs[2 * i + 1] = F(((List<object>)uv[i])[1]);
            }
            for (int i = 0; i < ix.Count; i++) g.Indices[i] = (uint)(double)ix[i];
            for (int i = 0; i < sm.Count; i++)
            {
                var s = (List<object>)sm[i];
                g.Submeshes[i] = new SubmeshRange(ulong.Parse((string)s[0], CultureInfo.InvariantCulture), (uint)(double)s[1], (uint)(double)s[2], (uint)(double)s[3]);
            }
            return g;
        }

        [TestMethod]
        public void S3Vectors_CubeModel_PayloadAndMessagesByteForByte_WindingAndTangents()
        {
            var r = Kind("mesh_cube").Single();
            ModelGeometry g = CubeFrom(r);
            Assert.AreEqual(U64(r, "model_key"), ModelCodec.ModelKey(J.S(r, "path"), 0), "modelKey of the path");
            byte[] payload = ModelCodec.EncodePayload(g);
            Assert.AreEqual(J.S(r, "payload_hex"), HexOf(payload), "C3 model payload, Python == C#");
            Assert.AreEqual(U64(r, "payload_hash"), ModelCodec.PayloadHash(payload, 0, payload.Length));
            var enc = new ModelEncoder(g, J.I(r, "chunk_bytes"));
            List<object> msgs = J.Arr(r, "messages");
            Assert.AreEqual(msgs.Count, enc.MessageCount);
            for (int i = 0; i < msgs.Count; i++)
            {
                var mm = (List<object>)msgs[i];
                Assert.AreEqual((uint)(double)mm[0], enc.MessageType(i), "message type " + i);
                Assert.AreEqual((string)mm[1], HexOf(enc.MessagePayload(i)), "message payload " + i);
            }
            // Unity side: mirrored positions, (I0, I2, I1), winding, tangent w
            List<Vec3> up = V3s(r, "unity_positions");
            for (int i = 0; i < g.VertexCount; i++) Assert.AreEqual(up[i], MeshConventions.Mirror(g.Position(i)));
            var tris = new int[g.IndexCount];
            MeshConventions.MirrorTriangles(g.Indices, tris, g.VertexCount);
            List<object> ut = J.Arr(r, "unity_tris");
            for (int t = 0; t < ut.Count; t++)
            {
                var tt = (List<object>)ut[t];
                for (int c = 0; c < 3; c++) Assert.AreEqual((int)(double)tt[c], tris[3 * t + c]);
            }
            List<object> w = J.Arr(r, "winding"), w0 = J.Arr(r, "winding_no_swap");
            WindingStats ws = MeshConventions.MeasureWinding(g);
            Assert.AreEqual(new WindingStats((int)(double)w[0], (int)(double)w[1], (int)(double)w[2]), ws);
            int[] wire = Array.ConvertAll(g.Indices, x => (int)x);
            var mp = new float[g.Positions.Length];
            var mn = new float[g.Normals.Length];
            MeshConventions.MirrorXyz(g.Positions, mp);
            MeshConventions.MirrorXyz(g.Normals, mn);
            Assert.AreEqual(new WindingStats((int)(double)w0[0], (int)(double)w0[1], (int)(double)w0[2]), MeshConventions.MeasureWinding(mp, mn, wire));
            List<object> tw = J.Arr(r, "tangent_w");
            for (int i = 0; i < g.VertexCount; i++)
                Assert.AreEqual((float)(double)tw[i], MeshConventions.TangentW(MeshConventions.Mirror(g.Normal(i)), MeshConventions.Mirror(g.Tangent(i)), MeshConventions.Mirror(g.Binormal(i))));
        }

        [TestMethod]
        public void S3Vectors_TangentW()
        {
            foreach (var r in Kind("tangent"))
            {
                Vec3 n = J.V3(r, "n"), t = J.V3(r, "t"), b = J.V3(r, "b");
                Assert.AreEqual((float)J.D(r, "w"), MeshConventions.TangentW(MeshConventions.Mirror(n), MeshConventions.Mirror(t), MeshConventions.Mirror(b)));
                Assert.AreEqual((float)J.D(r, "se_sign"), MeshConventions.SeTangentSign(n, t, b));
            }
        }

        [TestMethod]
        public void S3Vectors_ModelAndMaterialKeys_MaterialDefByteForByte()
        {
            foreach (var r in Kind("model_key"))
            {
                float scale = (float)J.D(r, "scale");
                string text = ModelCodec.ModelKeyText(J.S(r, "path"), (uint)J.I(r, "lod"), scale);
                string want = J.S(r, "scale_text");
                Assert.IsTrue(want.Length == 0 ? !text.Contains("|scale=") : text.EndsWith("|scale=" + want, StringComparison.Ordinal), text + " vs " + want);
                Assert.AreEqual(U64(r, "key"), ModelCodec.ModelKey(J.S(r, "path"), (uint)J.I(r, "lod"), scale), text);
            }
            foreach (var r in Kind("material"))
            {
                var m = new MaterialDefinition
                {
                    DrawTechnique = (uint)J.I(r, "technique"),
                    Flags = (uint)J.I(r, "flags"),
                    ColorMetal = J.S(r, "cm"),
                    NormalGloss = J.S(r, "ng"),
                    AddMaps = J.S(r, "add"),
                    AlphaMask = J.S(r, "alpha"),
                };
                m.MaterialKey = m.ComputeKey();
                Assert.AreEqual(U64(r, "key"), m.MaterialKey, "materialKey");
                byte[] p = ModelCodec.EncodeMaterialDef(m);
                Assert.AreEqual(J.S(r, "payload_hex"), HexOf(p), "MaterialDef payload");
                MaterialDefinition back;
                string err;
                Assert.IsTrue(ModelCodec.TryDecodeMaterialDef(p, 0, p.Length, out back, out err), err);
                Assert.AreEqual(m.ColorMetal, back.ColorMetal);
                Assert.AreEqual(m.AlphaMask, back.AlphaMask);
            }
            foreach (var r in Kind("cube_part_key"))
                Assert.AreEqual(U64(r, "key"), ModelCodec.CubePartKey(J.I(r, "x"), J.I(r, "y"), J.I(r, "z"), J.I(r, "index")));
        }

        [TestMethod]
        public void S3Vectors_PartAddByteForByte()
        {
            foreach (var r in Kind("part_add"))
            {
                List<object> l = J.Arr(r, "local"), hsv = J.Arr(r, "hsv"), uv = J.Arr(r, "uv_offset");
                var rows = new double[12];
                for (int i = 0; i < 12; i++) rows[i] = (double)l[i];
                PartAdd pa = ModelCodec.MakePartAdd(U64(r, "entity_id"), U64(r, "part_key"), U64(r, "model_key"), SeMatrix.FromRows(rows),
                                                    new Vec3((double)hsv[0], (double)hsv[1], (double)hsv[2]), F(uv[0]), F(uv[1]),
                                                    (uint)J.I(r, "skin_id"), (uint)J.I(r, "flags"));
                Assert.AreEqual(J.S(r, "payload_hex"), HexOf(ModelCodec.ToBytes(pa)), "PartAdd payload");
                PartAdd back;
                byte[] bytes = Hex(J.S(r, "payload_hex"));
                Assert.IsTrue(ModelCodec.TryRead(bytes, 0, bytes.Length, out back));
                Assert.AreEqual(U64(r, "part_key"), back.partKey);
            }
        }

        [TestMethod]
        public void S3Vectors_ArmorUvAndPatternOffsets_BitExact()
        {
            foreach (var r in Kind("uv"))
            {
                float u, v, pu, pv, au, av;
                MeshConventions.ArmorPatternOffset(J.I(r, "ox"), J.I(r, "oy"), J.I(r, "px"), J.I(r, "py"), out u, out v);
                List<object> off = J.Arr(r, "offset"), offp = J.Arr(r, "offset_packed"), uv = J.Arr(r, "uv"), auv = J.Arr(r, "armor_uv");
                Assert.AreEqual(F(off[0]), u);
                Assert.AreEqual(F(off[1]), v);
                MeshConventions.ArmorPatternOffsetPacked((byte)J.I(r, "ox"), (byte)J.I(r, "oy"), (byte)J.I(r, "w7"), out pu, out pv);
                Assert.AreEqual(F(offp[0]), pu);
                Assert.AreEqual(F(offp[1]), pv);
                Assert.AreEqual((byte)J.I(r, "w7"), MeshConventions.PackPattern(J.I(r, "px"), J.I(r, "py")));
                MeshConventions.ArmorUv(F(uv[0]), F(uv[1]), (float)J.D(r, "pattern_scale"), u, v, out au, out av);
                Assert.AreEqual(F(auv[0]), au);
                Assert.AreEqual(F(auv[1]), av);
            }
        }
    }
}
