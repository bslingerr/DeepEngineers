using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// ROADMAP S5: the C# S5 conventions and math (MirrorConventions, MirrorSkeleton, InstanceMath, SkinMath, ChunkMath,
 /// StreamRecording) against the independent Python reading (tools/worldmap/wire_ref.py <c>s5vectors -</c>, generated on
 /// the fly, never committed; the S3VectorTests pattern): conventions text and hash, keys, flags, SE matrix algebra,
 /// the subgrid relative row, the Unity conjugation, interpolation, bone map, weights, binds, C1 bone locals, skinning,
 /// split payloads byte for byte, chunk cells, colour keys, character records and.derec bytes.
 /// </summary>
    [TestClass]
    public class S5VectorTests
    {
        private static List<VRec> _records;
        private static readonly object Gate = new object();

        /// <summary>One vector record: kind name=value … (d = double bits, s = UTF-8 hex, x = bytes, L = list, else an integer).</summary>
        private sealed class VRec
        {
            public string Kind;
            public readonly Dictionary<string, string> F = new Dictionary<string, string>(StringComparer.Ordinal);

            private string Raw(string k)
            {
                string v;
                if (!F.TryGetValue(k, out v)) throw new KeyNotFoundException(Kind + "." + k);
                return v;
            }

            public ulong U64(string k) { return ulong.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public long I64(string k) { return long.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public int I(string k) { return int.Parse(Raw(k), CultureInfo.InvariantCulture); }
            public double D(string k) { return Dbl(Raw(k)); }
            public string S(string k) { return Encoding.UTF8.GetString(Hex(Raw(k).Substring(1))); }
            public byte[] X(string k) { return Hex(Raw(k).Substring(1)); }

            private string[] Items(string k)
            {
                string v = Raw(k);
                if (v[0] != 'L') throw new FormatException(Kind + "." + k + " is not a list");
                return v.Length == 1 ? new string[0] : v.Substring(1).Split(',');
            }

            public double[] Ds(string k) { return Items(k).Select(Dbl).ToArray(); }
            public float[] Fs(string k) { return Items(k).Select(x => (float)Dbl(x)).ToArray(); }
            public int[] Is(string k) { return Items(k).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray(); }
            public ulong[] U64s(string k) { return Items(k).Select(x => ulong.Parse(x, CultureInfo.InvariantCulture)).ToArray(); }
            public SeMatrix M(string k) { return SeMatrix.FromRows(Ds(k), 0); }
            public SeMatrix[] Ms(string k)
            {
                double[] d = Ds(k);
                var m = new SeMatrix[d.Length / 12];
                for (int i = 0; i < m.Length; i++) m[i] = SeMatrix.FromRows(d, 12 * i);
                return m;
            }
            public Vec3 V(string k) { double[] d = Ds(k); return new Vec3(d[0], d[1], d[2]); }

            private static double Dbl(string s)
            {
                if (s[0] != 'd') throw new FormatException("not a double: " + s);
                return BitConverter.Int64BitsToDouble((long)ulong.Parse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }

        private static byte[] Hex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(hex.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        private static string HexOf(byte[] b) { return string.Concat(b.Select(x => x.ToString("x2", CultureInfo.InvariantCulture))); }

        private static List<VRec> Records()
        {
            lock (Gate)
            {
                if (_records != null) return _records;
                string text = S5Repo.RunPython(Path.Combine("tools", "worldmap", "wire_ref.py"), "s5vectors", "-");
                var recs = new List<VRec>();
                foreach (string raw in text.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    string[] tok = line.Split(' ');
                    var r = new VRec { Kind = tok[0] };
                    for (int i = 1; i < tok.Length; i++)
                    {
                        int eq = tok[i].IndexOf('=');
                        r.F[tok[i].Substring(0, eq)] = tok[i].Substring(eq + 1);
                    }
                    recs.Add(r);
                }
                Console.WriteLine("S5 vectors: " + recs.Count + " records");
                _records = recs;
                return recs;
            }
        }

        private static IEnumerable<VRec> Kind(string kind) { return Records().Where(r => r.Kind == kind); }

        private static double MaxDiff(SeMatrix a, SeMatrix b) { return S5InstanceMathTests.MaxDiff(a, b); }

        private static SeMatrix RowsOfFloats(float[] f, int offset) { return SeMatrix.FromRows(f, offset); }

        [TestMethod]
        public void S5Vectors_FileHasEveryKind()
        {
            var counts = Records().GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count());
            foreach (string k in new[] { "conv", "skeleton", "model_key", "part_keys", "fat_key", "subpart_key", "skin_id", "part_flags", "version",
                                         "relative", "algebra", "unity", "interp", "skeleton_case", "weights", "skeleton_bad", "split", "cell",
                                         "colour", "character", "derec" })
                Assert.IsTrue(counts.ContainsKey(k) && counts[k] > 0, "missing kind " + k);
        }

        [TestMethod]
        public void S5Vectors_ConventionsAndSkeleton()
        {
            VRec c = Kind("conv").Single();
            Assert.AreEqual(c.S("text"), MirrorConventions.CanonicalText(), "Python and C# canonical texts");
            Assert.AreEqual(c.U64("hash"), MirrorConventions.Hash);
            Assert.AreEqual(MirrorConventions.PinnedHash, c.U64("hash"));
            Assert.AreEqual(37, c.I("lines"));
            Assert.AreEqual(1539, c.I("bytes"));
            VRec s = Kind("skeleton").Single();
            Assert.AreEqual(s.S("text"), MirrorSkeleton.SkeletonText());
            Assert.AreEqual(s.U64("hash"), MirrorSkeleton.Hash);
            Assert.AreEqual(78, s.I("rows"));
        }

        [TestMethod]
        public void S5Vectors_Keys()
        {
            foreach (VRec r in Kind("model_key"))
            {
                string skin = r.S("skin");
                string text = MirrorConventions.ModelKeyText(r.S("path"), (uint)r.I("lod"), (float)r.D("scale"), skin, (ulong)r.I("char"));
                Assert.AreEqual(r.S("text"), text);
                Assert.AreEqual(r.U64("key"), MirrorConventions.ModelKey(r.S("path"), (uint)r.I("lod"), (float)r.D("scale"), skin, (ulong)r.I("char")));
            }
            foreach (VRec r in Kind("part_keys"))
            {
                double[] t = r.Ds("t");
                float rot = (float)r.D("rot");
                Assert.AreEqual(r.U64("cube"), MirrorConventions.CubePartKey(r.U64("mk"), (float)t[0], (float)t[1], (float)t[2], rot));
                Assert.AreEqual(r.U64("edge"), MirrorConventions.EdgePartKey(r.U64("mk"), (float)t[0], (float)t[1], (float)t[2], rot));
            }
            foreach (VRec r in Kind("fat_key"))
            {
                Assert.AreEqual(r.S("text"), MirrorConventions.FatPartKeyText(r.I("x"), r.I("y"), r.I("z")));
                Assert.AreEqual(r.U64("key"), MirrorConventions.FatPartKey(r.I("x"), r.I("y"), r.I("z")));
            }
            foreach (VRec r in Kind("subpart_key"))
            {
                Assert.AreEqual(r.S("text"), MirrorConventions.SubpartKeyText(r.U64("fat"), r.S("path")));
                Assert.AreEqual(r.U64("key"), MirrorConventions.SubpartKey(r.U64("fat"), r.S("path")));
                Assert.AreEqual(r.U64("key"), MirrorConventions.SubpartKey(r.U64("fat"), r.S("path").Split('/')));
            }
            foreach (VRec r in Kind("skin_id"))
                Assert.AreEqual((uint)r.U64("id"), MirrorConventions.SkinId(r.S("skin")), r.S("skin"));
        }

        [TestMethod]
        public void S5Vectors_FlagsAndDrawKinds()
        {
            foreach (VRec r in Kind("part_flags"))
                Assert.AreEqual((uint)r.U64("flags"), MirrorConventions.PartFlags((uint)r.I("cls"), (uint)r.I("extra")));
            foreach (VRec r in Kind("version"))
            {
                uint f = (uint)r.U64("flags");
                Assert.AreEqual(r.I("version"), (int)MirrorConventions.Classify(f), "flags 0x" + f.ToString("x"));
                Assert.AreEqual((uint)r.I("cls"), MirrorConventions.EffectivePartClass(f));
                Assert.AreEqual(r.I("draw"), (int)ChunkMath.DrawKind(f));
                Assert.AreEqual((uint)r.I("chunk_flags"), ChunkMath.ChunkFlags(f));
            }
        }

        [TestMethod]
        public void S5Vectors_SeAlgebraRelativeUnityInterpolation()
        {
            foreach (VRec r in Kind("relative"))
            {
                SeMatrix rel = InstanceMath.Relative(r.M("child"), r.M("parent"));
                Assert.IsTrue(MaxDiff(rel, r.M("rel")) <= 1e-9, "relative " + MaxDiff(rel, r.M("rel")));
            }
            foreach (VRec r in Kind("algebra"))
            {
                Assert.IsTrue(MaxDiff(InstanceMath.Multiply(r.M("a"), r.M("b")), r.M("ab")) <= 1e-12);
                Assert.IsTrue(MaxDiff(InstanceMath.Invert(r.M("a")), r.M("inv")) <= 1e-12);
            }
            foreach (VRec r in Kind("unity"))
            {
                SeMatrix m = r.M("m");
                UnityTrs t = InstanceMath.ToUnity(m);
                double[] rot = r.Ds("rot");
                Assert.IsTrue(Vec3.MaxAbsDiff(t.Position, r.V("pos")) <= 1e-12);
                Assert.IsTrue(Quat.RotationDistance(t.Rotation, new Quat(rot[0], rot[1], rot[2], rot[3])) <= 1e-12);
                Assert.IsTrue(Vec3.MaxAbsDiff(t.Scale, r.V("scale")) <= 1e-12);
                double[] a = r.Ds("affine");
                Affine3 u = InstanceMath.ToUnityAffine(m);
                Assert.AreEqual(new Vec3(a[0], a[1], a[2]), u.Linear.C0);
                Assert.AreEqual(new Vec3(a[3], a[4], a[5]), u.Linear.C1);
                Assert.AreEqual(new Vec3(a[6], a[7], a[8]), u.Linear.C2);
                Assert.AreEqual(new Vec3(a[9], a[10], a[11]), u.Translation);
            }
            foreach (VRec r in Kind("interp"))
            {
                double alpha = r.D("alpha");
                SeMatrix got = InstanceMath.Interpolate(r.M("a"), r.M("b"), alpha);
                Assert.IsTrue(MaxDiff(got, r.M("r")) <= 1e-12, "interp alpha " + alpha + ": " + MaxDiff(got, r.M("r")));
                if (alpha >= 1) Assert.AreEqual(0.0, MaxDiff(got, r.M("b")));
            }
        }

        [TestMethod]
        public void S5Vectors_SkeletonBindsBonesWeightsSkinning()
        {
            VRec sc = Kind("skeleton_case").Single();
            List<string> names = sc.S("names").Split('|').ToList();
            List<int> parents = sc.Is("parents").ToList();
            SkeletonMap map;
            string error;
            Assert.IsTrue(SkeletonMap.TryBuild(names, parents, out map, out error), error);
            CollectionAssert.AreEqual(sc.Is("c2m"), map.CanonicalToModel);
            CollectionAssert.AreEqual(sc.Is("anc"), map.ModelToCanonicalAncestor);
            SeMatrix[] local = sc.Ms("local"), posed = sc.Ms("posed");
            var abs = new SeMatrix[names.Count];
            SkinMath.AbsoluteFromLocal(local, parents, abs);
            float[] bind = SkinMath.SkinnedBindRows(map, abs);
            float[] pyBind = sc.Fs("bind");
            Assert.AreEqual(pyBind.Length, bind.Length);
            double worstBind = 0;
            for (int i = 0; i < bind.Length; i++) worstBind = Math.Max(worstBind, Math.Abs(bind[i] - pyBind[i]) / Math.Max(1.0, Math.Abs(pyBind[i])));
            Assert.IsTrue(worstBind <= 1e-6, "SkinnedBind rows " + worstBind);

            var modelRows = new float[12 * names.Count];
            for (int i = 0; i < names.Count; i++) posed[i].CopyRowsTo(modelRows, 12 * i);
            var c1 = new float[78 * 12];
            SkinMath.PickCanonical(map, modelRows, c1, 0);
            CollectionAssert.AreEqual(sc.Fs("c1"), c1, "C1 bones in canonical order");
            var locals = new SeMatrix[78];
            SkinMath.BoneLocals(c1, 0, locals);
            SeMatrix[] pyLocals = sc.Ms("locals");
            for (int i = 0; i < 78; i++) Assert.IsTrue(MaxDiff(locals[i], pyLocals[i]) <= 1e-12, "bone local " + i);

            var bindM = new SeMatrix[78];
            var poseM = new SeMatrix[78];
            for (int c = 0; c < 78; c++) { bindM[c] = RowsOfFloats(pyBind, 12 * c); poseM[c] = RowsOfFloats(c1, 12 * c); }
            var ob = new byte[4];
            var ow = new float[4];
            foreach (VRec w in Kind("weights"))
            {
                int[] idx = w.Is("idx");
                float[] wt = w.Fs("w");
                Assert.IsTrue(SkinMath.RemapInfluences(map, idx.Select(x => (byte)x).ToArray(), wt, 0, 4, ob, ow, 0));
                CollectionAssert.AreEqual(w.Is("cidx").Select(x => (byte)x).ToArray(), ob);
                CollectionAssert.AreEqual(w.Fs("cw"), ow, "weights bit for bit");
                Vec3 se = SkinMath.SkinPointSe(w.V("v"), ob, ow, 0, bindM, poseM);
                Assert.IsTrue(Vec3.MaxAbsDiff(se, w.V("skinned")) <= 1e-12, "SE skinning reference");
            }
            foreach (VRec b in Kind("skeleton_bad"))
            {
                Assert.IsFalse(SkeletonMap.TryBuild(b.S("names").Split('|').ToList(), b.Is("parents").ToList(), out map, out error));
                StringAssert.Contains(error, b.S("reason") == "missing" ? "missing" : "has parent");
            }
        }

        [TestMethod]
        public void S5Vectors_SplitPayloadsByteForByte()
        {
            foreach (VRec r in Kind("split"))
            {
                float[] pos = r.Fs("positions");
                int nv = pos.Length / 3;
                uint[] idx = r.Is("indices").Select(x => (uint)x).ToArray();
                ulong[] sub = r.U64s("submeshes");
                ModelGeometry g = ModelGeometry.Allocate(nv, idx.Length, sub.Length / 4, ModelFlags.Skinned | ModelFlags.HasTangents);
                g.ModelKey = r.U64("src_key");
                Array.Copy(pos, g.Positions, pos.Length);
                Array.Copy(r.Fs("normals"), g.Normals, g.Normals.Length);
                Array.Copy(r.Fs("tangents"), g.Tangents, g.Tangents.Length);
                Array.Copy(r.Fs("binormals"), g.Binormals, g.Binormals.Length);
                Array.Copy(r.Fs("uvs"), g.Uvs, g.Uvs.Length);
                Array.Copy(r.Is("bones").Select(x => (byte)x).ToArray(), g.BoneIndices, g.BoneIndices.Length);
                Array.Copy(r.Fs("weights"), g.BoneWeights, g.BoneWeights.Length);
                Array.Copy(idx, g.Indices, idx.Length);
                for (int s = 0; s < g.SubmeshCount; s++) g.Submeshes[s] = new SubmeshRange(sub[4 * s], (uint)sub[4 * s + 1], (uint)sub[4 * s + 2], (uint)sub[4 * s + 3]);
                g.ComputeBounds();
                bool[] head = r.Is("head").Select(x => x != 0).ToArray();
                ModelGeometry body, headM;
                SkinMath.SplitByMaterial(g, head, out body, out headM);
                Assert.AreEqual(HexOf(r.X("body")), body == null ? "" : HexOf(ModelCodec.EncodePayload(body)), "body payload");
                Assert.AreEqual(HexOf(r.X("head_payload")), headM == null ? "" : HexOf(ModelCodec.EncodePayload(headM)), "head payload");
                if (body != null) CollectionAssert.AreEqual(r.Fs("body_bounds"), body.Bounds);
                if (headM != null) CollectionAssert.AreEqual(r.Fs("head_bounds"), headM.Bounds);
            }
        }

        [TestMethod]
        public void S5Vectors_CellsColoursCharacterDerec()
        {
            foreach (VRec r in Kind("cell"))
            {
                float[] t = r.Fs("t");
                int[] cell = r.Is("cell");
                Assert.AreEqual(new ChunkCell(cell[0], cell[1], cell[2]), ChunkMath.CellOf(t[0], t[1], t[2], (uint)r.I("cls")),
                    "t " + t[0].ToString("R") + " class " + r.I("cls"));
            }
            foreach (VRec r in Kind("colour"))
            {
                float[] h = r.Fs("hsv");
                Assert.AreEqual(r.I64("key"), ChunkMath.ColourKey(h[0], h[1], h[2]));
            }
            foreach (VRec r in Kind("character"))
            {
                ulong cid = r.U64("cid"), mk = r.U64("mk"), part = (ulong)r.I("part");
                Assert.AreEqual(HexOf(r.X("add")), HexOf(ModelCodec.ToBytes(MirrorConventions.MakeCharacterAdd(cid, mk, r.I("fallback") != 0))));
                double[] hsv = r.Ds("hsv");
                PartAdd p = MirrorConventions.MakeCharacterPart(cid, part, mk, new Vec3(hsv[0], hsv[1], hsv[2]), (uint)r.U64("skin"));
                Assert.AreEqual(HexOf(r.X("part_payload")), HexOf(ModelCodec.ToBytes(p)));
            }
            VRec d = Kind("derec").Single();
            DerecHeader h0 = Derec.CreateHeader((uint)d.U64("se_version"), d.U64("created"), (uint)d.I("flags"), (uint)d.I("epoch"));
            var ms = new MemoryStream();
            using (var w = new DerecWriter(ms, h0, false))
            {
                Assert.AreEqual(HexOf(d.X("header")), HexOf(ms.ToArray()), ".derec header");
                byte[] data = d.X("data");
                long before = ms.Length;
                w.Write((ushort)d.I("kind"), d.U64("tick"), d.I64("qpc"), data, 0, data.Length);
                Assert.AreEqual(HexOf(d.X("record")), HexOf(ms.ToArray().Skip((int)before).ToArray()), ".derec record");
                before = ms.Length;
                w.WriteEnd(123456790, 99);
                Assert.AreEqual(HexOf(d.X("end")), HexOf(ms.ToArray().Skip((int)before).ToArray()), ".derec end");
            }
        }

    }
}
