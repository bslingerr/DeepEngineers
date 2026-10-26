using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S5 Unity-free parts of the Subnautica plugin (ROADMAP S5 SN plugin; CONTRACT §5.4 "C1–C4 conventions for S5",
 /// §2.5, §2.8): the conventions as this plugin reads them (pinned against the contract text and Wire), canon1, the
 /// part roles and the S5 record decode (GridAdd, PartAdd classes and Dynamic, CharacterAdd, SkinnedBind, skinned
 /// payloads, C2 subpart rows), the chunk bookkeeping (chunk key bits, index format, drain gate, build queue, cell
 /// index), the mirror/affine math against Wire's InstanceMath, the row interpolation at the bracket, the character
 /// bone math (rest locals, bind poses, SE bones, skinning, path G, T-pose, blend), the C1 character reader, the
 /// source and display rules, S5.M4/M5, statistics and switches.
 /// </summary>
    [TestClass]
    public class S5Tests
    {
        private const ulong Model = 0x1122334455667788UL;
        private const ulong MatA = 0xA000000000000001UL;

        // ------------------------------------------------------------------------------------- helpers

        private static unsafe byte[] Bytes<T>(T v) where T : unmanaged
        {
            var a = new byte[sizeof(T)];
            fixed (byte* p = a) *(T*)p = v;
            return a;
        }

        private static unsafe C4Outcome Feed(MirrorState s, uint type, byte[] payload)
        {
            ulong id;
            fixed (byte* p = payload) return s.Feed(type, p, payload.Length, out id);
        }

        private static unsafe C3Outcome Feed(ModelAssembler a, uint type, byte[] payload)
        {
            fixed (byte* p = payload) return a.Feed(type, p, payload.Length);
        }

        private static PartAdd Part(ulong entity, ulong key, uint flags, float tx, float ty, float tz, float h = 0f, ulong model = Model)
        {
            var p = new PartAdd { entityId = entity, partKey = key, modelKey = model, colorMaskHSV = new F32x3(h, 0.5f, 0.1f), flags = flags };
            float[] rows = { 1, 0, 0, 0, 1, 0, 0, 0, 1, tx, ty, tz };
            for (int i = 0; i < 12; i++) p.local[i] = rows[i];
            return p;
        }

        private static GridAdd Grid(ulong id, uint flags, ulong parent = 0, uint targetClass = 1)
        {
            return new GridAdd { entityId = id, targetClass = targetClass, flags = flags, parentEntityId = parent, worldEpoch = 0 };
        }

        private static uint PF(uint cls, uint extra = 0) { return MirrorConventions.PartFlags(cls, extra); }

        private static Quat AxisAngle(Vec3 axis, double rad)
        {
            Vec3 a = axis.Normalized();
            double s = Math.Sin(rad / 2);
            return new Quat(a.X * s, a.Y * s, a.Z * s, Math.Cos(rad / 2));
        }

        /// <summary>SE rows (12 floats) of a rotation q (SE space) and translation t.</summary>
        private static float[] Rows(Quat q, Vec3 t, double scale = 1)
        {
            Vec3 r = q.Rotate(Vec3.UnitX) * scale, u = q.Rotate(Vec3.UnitY) * scale, b = q.Rotate(Vec3.UnitZ) * scale;
            return new[] { (float)r.X, (float)r.Y, (float)r.Z, (float)u.X, (float)u.Y, (float)u.Z, (float)b.X, (float)b.Y, (float)b.Z, (float)t.X, (float)t.Y, (float)t.Z };
        }

        private static SeMatrix Se(float[] rows, int off = 0) { return SeMatrix.FromRows(rows, off); }

        private static void Near(Vec3 a, Vec3 b, double tol, string what)
        {
            Assert.IsTrue((a - b).Length <= tol, what + ": " + a + " vs " + b + " (|d| " + (a - b).Length + ")");
        }

        private static void NearQ(Quat a, Quat b, double tol, string what)
        {
            Assert.IsTrue(Quat.RotationDistance(a, b) <= tol, what + ": " + a + " vs " + b);
        }

        private static void NearAff(Aff a, Aff b, double tol, string what)
        {
            Near(a.L.C0, b.L.C0, tol, what + " c0");
            Near(a.L.C1, b.L.C1, tol, what + " c1");
            Near(a.L.C2, b.L.C2, tol, what + " c2");
            Near(a.T, b.T, tol, what + " t");
        }

        // ------------------------------------------------------------------------------------- conventions

        [TestMethod]
        public void S5Conventions_CanonicalText_PinnedHash_WireAgrees_FlagsInsideFields()
        {
            string text = S5Conv.CanonicalText();
            byte[] ascii = Encoding.ASCII.GetBytes(text);
            Assert.AreEqual(1539, ascii.Length, "1,539 bytes (CONTRACT §5.4)");
            Assert.AreEqual(37, text.Split('\n').Length - 1, "37 lines, '\\n' after each");
            Assert.AreEqual(S5Conv.ConventionsHash, LayoutHash.Fnv1a64Ascii(text), "FNV-1a 64 = 0x31dfd6f9befdef5d");
            Assert.AreEqual(MirrorConventions.CanonicalText(), text, "the plugin's spelling equals Wire's");
            Assert.AreEqual(MirrorConventions.PinnedHash, S5Conv.ConventionsHash);
            // every class value inside the class field, every flag outside it and outside the version nibble
            foreach (uint c in new[] { S5Conv.ClassCubePart, S5Conv.ClassEdge, S5Conv.ClassFatBlock, S5Conv.ClassSubpart, S5Conv.ClassCharacterBody, S5Conv.ClassTool, S5Conv.ClassFloatingObject })
                Assert.AreEqual(c, c & S5Conv.ClassMask);
            foreach (uint f in new[] { S5Conv.Dynamic, S5Conv.HiddenInFirstPerson, S5Conv.BuildStage, S5Conv.Deformed, S5Conv.NoShadows, S5Conv.MetalnessColorable })
            {
                Assert.AreEqual(0u, f & S5Conv.ClassMask, "flag " + f + " outside the class field");
                Assert.AreEqual(0u, f & S5Conv.VersionMask, "flag " + f + " outside the nibble");
            }
            Assert.AreEqual(1u, S5Conv.Version(S5Conv.PartVersion1));
            Assert.AreEqual(S5Conv.PartVersion1, S5Conv.PartVersion1 & S5Conv.VersionMask);
            Assert.AreEqual(1u, S5Conv.Version(S5Conv.GridVersion1));
            Assert.AreEqual(1u, S5Conv.Version(S5Conv.CharVersion1));
            Assert.AreEqual(0u, (S5Conv.GridStatic | S5Conv.GridSubgrid | S5Conv.GridFloatingObject) & S5Conv.VersionMask);
            Assert.AreEqual(0xfded4532u, S5Conv.SkinId("clean_armor"), "contract vector skinId('clean_armor')");
            Assert.AreEqual(S5Conv.SkinId("Clean_Armor"), S5Conv.SkinId("clean_armor"), "lower-cased");
            Assert.AreEqual(0u, S5Conv.SkinId(null));
        }

        [TestMethod]
        public void S5Skeleton_Embedded_78Rows_PinnedHash_HumanColumn_EqualsWire()
        {
            string err;
            CanonSkeleton sk = CanonSkeleton.Embedded(out err);
            Assert.IsNotNull(sk, err);
            Assert.AreEqual(78, sk.Count);
            Assert.AreEqual(S5Conv.SkeletonHash, sk.Hash, "columns 1-3 hash 0x995765282e7d4f1d");
            Assert.AreEqual(0, sk.MissingRequiredHuman().Count, "all 15 required human bones mapped");
            Assert.AreEqual("SE_RigSpine2", sk.Bones[sk.HumanIndex("Spine")].SeBone);
            Assert.AreEqual("SE_RigSpine4", sk.Bones[sk.HumanIndex("Chest")].SeBone);
            Assert.AreEqual("SE_RigRibcage", sk.Bones[sk.HumanIndex("UpperChest")].SeBone);
            // snBone filled from the S5-2a real run's s5.rig row (player_view, generic rig): path G available
            Assert.AreEqual(0, sk.MissingRequiredSnBones().Count, "all 15 required bones have a Subnautica bone");
            Assert.AreEqual("hips", sk.Bones[sk.HumanIndex("Hips")].SnBone);
            Assert.AreEqual("spine_1", sk.Bones[sk.HumanIndex("Spine")].SnBone);
            Assert.AreEqual("elbow_L", sk.Bones[sk.HumanIndex("LeftLowerArm")].SnBone);
            Assert.AreEqual("hand_R_pinky_tip_rig", sk.Bones[sk.IndexOf("SE_RigR_Little_3")].SnBone);
            Assert.IsFalse(sk.Bones[sk.IndexOf("SE_RigSpine1")].HasSnBone, "SE's six spine bones onto Subnautica's four: Spine1 stays unmapped");
            Assert.IsFalse(sk.Bones[0].HasSnBone, "root rows and dummies have no Subnautica bone ('-')");
            for (int i = 0; i < sk.Count; i++)
            {
                Assert.AreEqual(MirrorSkeleton.BoneName(i), sk.Bones[i].SeBone, "row " + i + " name equals Wire's table");
                Assert.AreEqual(MirrorSkeleton.Parent(i), sk.Parents[i], "row " + i + " parent equals Wire's table");
                Assert.IsTrue(sk.Parents[i] < i, "parents first");
            }
            Assert.AreEqual(MirrorSkeleton.HeadDummy, sk.IndexOf("HeadDummy"), "the first-person offset bone");
            Assert.AreEqual(MirrorSkeleton.Pelvis, sk.IndexOf("SE_RigPelvis"));
            Assert.AreEqual(MirrorSkeleton.Head, sk.IndexOf("SE_RigHead"));
            Assert.AreEqual(MirrorSkeleton.RightPalm, sk.IndexOf("SE_RigRPalm"));
        }

        [TestMethod]
        public void S5Skeleton_Parse_RefusesBadTables()
        {
            CanonSkeleton s;
            string e;
            string head = "# c\n" + CanonSkeleton.Header + "\n";
            Assert.IsTrue(CanonSkeleton.TryParse(head + "0\tA\t-1\t-\t-\troot\t?\n1\tB\t0\tA\tHips\thuman\tpelvis\n", out s, out e), e);
            Assert.AreEqual(2, s.Count);
            Assert.IsTrue(s.Bones[1].HasSnBone);
            Assert.IsFalse(CanonSkeleton.TryParse("index\tseBone\n0\tA\t-1\t-\t-\troot\t?\n", out s, out e), "bad header");
            Assert.IsFalse(CanonSkeleton.TryParse(head + "0\tA\t1\t-\t-\troot\t?\n", out s, out e), "parent after child");
            Assert.IsFalse(CanonSkeleton.TryParse(head + "0\tA\t-1\t-\t-\troot\t?\n1\tA\t0\tA\t-\tfollow\t?\n", out s, out e), "duplicate name");
            Assert.IsFalse(CanonSkeleton.TryParse(head + "0\tA\t-1\t-\t-\troot\t?\n1\tB\t0\tX\t-\tfollow\t?\n", out s, out e), "seParent mismatch");
            Assert.IsFalse(CanonSkeleton.TryParse(head + "0\tA\t-1\t-\t-\n", out s, out e), "5 columns");
        }

        // ------------------------------------------------------------------------------------- roles and C4

        [TestMethod]
        public void S5PartRoles_VersionNibble_Classes_DynamicAndChunkBits()
        {
            uint cls;
            Assert.AreEqual(PartRole.Chunk, S5PartRules.Role(0u, out cls), "nibble 0: an S3 chunked cube part");
            Assert.AreEqual(S5Conv.ClassCubePart, cls);
            Assert.AreEqual(0u, S5PartRules.ChunkBits(S5Conv.NoShadows), "S3 parts carry no chunk bits");
            for (uint c = 1; c <= 4; c++)
            {
                Assert.AreEqual(PartRole.Chunk, S5PartRules.Role(PF(c), out cls), "class " + c);
                Assert.AreEqual(c, cls);
                Assert.AreEqual(PartRole.Child, S5PartRules.Role(PF(c, S5Conv.Dynamic), out cls), "class " + c + " Dynamic");
            }
            Assert.AreEqual(PartRole.Skinned, S5PartRules.Role(PF(5, S5Conv.HiddenInFirstPerson), out cls));
            Assert.AreEqual(PartRole.Child, S5PartRules.Role(PF(6, S5Conv.Dynamic), out cls));
            Assert.AreEqual(PartRole.Child, S5PartRules.Role(PF(7), out cls));
            Assert.AreEqual(PartRole.Chunk, S5PartRules.Role(S5Conv.PartVersion1, out cls), "version 1 class 0 = CubePart");
            Assert.AreEqual(PartRole.Rejected, S5PartRules.Role(S5Conv.PartVersion1 | 9u, out cls), "class 9 undefined");
            Assert.AreEqual(PartRole.Rejected, S5PartRules.Role(2u << 28 | 1u, out cls), "nibble 2");
            Assert.AreEqual(S5Conv.NoShadows | S5Conv.MetalnessColorable, S5PartRules.ChunkBits(PF(2, S5Conv.NoShadows | S5Conv.MetalnessColorable | S5Conv.BuildStage)));
            Assert.IsTrue(S5PartRules.VersionAccepted(0) && S5PartRules.VersionAccepted(S5Conv.GridVersion1) && !S5PartRules.VersionAccepted(15u << 28));
        }

        [TestMethod]
        public void S5State_GridsSubgridsChildrenCellIndex_Rejected()
        {
            var s = new MirrorState();
            long serial = s.ChangeSerial;
            Assert.AreEqual(C4Outcome.GridAdded, Feed(s, C4Type.GridAdd, Bytes(Grid(1, S5Conv.GridVersion1 | S5Conv.GridStatic))));
            Assert.AreEqual(C4Outcome.GridAdded, Feed(s, C4Type.GridAdd, Bytes(Grid(2, S5Conv.GridVersion1 | S5Conv.GridSubgrid, parent: 1, targetClass: 2))));
            Assert.IsTrue(s.ChangeSerial > serial);
            Assert.IsTrue(s.Grids[2].IsSubgrid && !s.Grids[1].IsSubgrid);
            Assert.AreEqual(4f, s.Grids[2].CellSize, "small subgrid: 4 m cells");
            // chunked, dynamic (child), recolour, replace into another cell, remove
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 10, PF(1), 1, 1, 1)));
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 11, PF(3), 25, 0, 0)));
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 12, PF(4, S5Conv.Dynamic), 2, 0, 0)));
            GridRecord g = s.Grids[1];
            Assert.AreEqual(3, g.Parts.Count);
            Assert.AreEqual(2, g.CellParts.Count, "the Dynamic subpart is not in a cell");
            Assert.IsTrue(g.CellParts[new CellKey(0, 0, 0)].Contains(10) && g.CellParts[new CellKey(1, 0, 0)].Contains(11));
            Assert.IsTrue(g.ChildrenDirty);
            Assert.IsFalse(g.Parts[12].Chunked);
            Assert.AreEqual(PartRole.Child, g.Parts[12].Role);
            g.ChildrenDirty = false;
            g.DirtyCells.Clear();
            Feed(s, C4Type.PartRecolor, Bytes(new PartRecolor { entityId = 1, partKey = 12, hsv = new F32x3(0.3f, 0.2f, 0.1f) }));
            Assert.IsTrue(g.ChildrenDirty, "a recoloured child re-made");
            Assert.AreEqual(0, g.DirtyCells.Count, "no chunk rebuilt for a child");
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 10, PF(1), 41, 0, 0)));
            Assert.IsTrue(g.DirtyCells.Contains(new CellKey(0, 0, 0)) && g.DirtyCells.Contains(new CellKey(2, 0, 0)), "old and new cells dirty");
            Assert.IsFalse(g.CellParts.ContainsKey(new CellKey(0, 0, 0)), "the emptied cell left the index");
            Feed(s, C4Type.PartRemove, Bytes(new PartRemove { entityId = 1, partKey = 11 }));
            Assert.IsFalse(g.CellParts.ContainsKey(new CellKey(1, 0, 0)));
            // the Dynamic re-send of a static subpart: a replace (same key) that leaves its cell
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 10, PF(4, S5Conv.Dynamic), 41, 0, 0)));
            Assert.IsFalse(g.CellParts.ContainsKey(new CellKey(2, 0, 0)), "now a child");
            Assert.IsTrue(g.DirtyCells.Contains(new CellKey(2, 0, 0)), "its old chunk rebuilt without it");
            // re-parent keeps the parts
            Feed(s, C4Type.GridAdd, Bytes(Grid(2, S5Conv.GridVersion1 | S5Conv.GridSubgrid, parent: 7, targetClass: 2)));
            Assert.IsTrue(s.Grids[2].ParentChanged);
            // rejected nibble: grid and its parts dropped, counted
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.GridAdd, Bytes(Grid(3, 3u << 28))));
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.PartAdd, Bytes(Part(3, 1, PF(1), 0, 0, 0))));
            Assert.IsFalse(s.Orphans.ContainsKey(3), "never an orphan");
            Assert.AreEqual(1, s.ConventionsRejected);
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.PartAdd, Bytes(Part(1, 99, S5Conv.PartVersion1 | 12u, 0, 0, 0))));
            Assert.AreEqual(2, s.ConventionsRejected);
            Assert.IsFalse(g.Parts.ContainsKey(99));
            // a cell-size change re-indexes every part
            Feed(s, C4Type.PartAdd, Bytes(Part(1, 20, PF(1), 10, 0, 0)));
            Feed(s, C4Type.GridAdd, Bytes(Grid(1, S5Conv.GridVersion1, targetClass: 2)));
            Assert.IsTrue(g.CellParts.ContainsKey(new CellKey(2, 0, 0)), "10 m / 4 m cells");
        }

        [TestMethod]
        public void S5State_CharacterAdd_PartsOrphansRemoveClearAll_C7()
        {
            var s = new MirrorState();
            var legacy = new CharacterAdd { entityId = 50, modelKey = 0xAAAA0000BBBBUL, flags = 0, boneCount = 78 };
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.CharacterAdd, Bytes(legacy)), "version 0: ignored as in S3");
            Assert.AreEqual(0, s.Characters.Count);
            // the body before its CharacterAdd: an orphan, adopted
            Feed(s, C4Type.PartAdd, Bytes(Part(50, S5Conv.CharPartBody, PF(5, S5Conv.MetalnessColorable), 0, 0, 0, model: 0x77)));
            Assert.AreEqual(1, s.Orphans[50].Count);
            var add = new CharacterAdd { entityId = 50, modelKey = 0x1234567890ABCDEFUL, flags = MirrorConventions.CharacterFlags(true), boneCount = 78 };
            Assert.AreEqual(C4Outcome.CharacterAdded, Feed(s, C4Type.CharacterAdd, Bytes(add)));
            CharacterRecord c = s.Characters[50];
            Assert.IsTrue(c.Fallback);
            Assert.AreEqual(0x90ABCDEFu, c.BonesVersion, "low 32 bits of the body modelKey");
            Assert.AreEqual(1, c.Parts.Count, "orphan adopted");
            Feed(s, C4Type.PartAdd, Bytes(Part(50, S5Conv.CharPartHead1st, PF(5, S5Conv.HiddenInFirstPerson | S5Conv.MetalnessColorable), 0, 0, 0, model: 0x78)));
            Assert.AreEqual(2, c.Parts.Count);
            Assert.AreEqual(PartRole.Skinned, c.Parts[S5Conv.CharPartHead1st].Role);
            Assert.AreEqual(2, s.PartCount);
            Feed(s, C4Type.PartRecolor, Bytes(new PartRecolor { entityId = 50, partKey = S5Conv.CharPartBody, hsv = new F32x3(0.5f, 0.5f, 0.5f) }));
            Assert.AreEqual(0.5f, c.Parts[S5Conv.CharPartBody].H);
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.CharacterAdd, Bytes(new CharacterAdd { entityId = 51, flags = 4u << 28 })), "nibble 4 rejected");
            Assert.AreEqual(1, s.ConventionsRejected);
            Assert.AreEqual(C4Outcome.GridRemoved, Feed(s, C4Type.EntityRemove, Bytes(new EntityRemove { entityId = 50 })));
            Assert.AreEqual(0, s.Characters.Count);
            Feed(s, C4Type.CharacterAdd, Bytes(add));
            Feed(s, C4Type.ClearAll, Bytes(new ClearAll { worldEpoch = 1, linkEpoch = 1 }));
            Assert.AreEqual(0, s.Characters.Count, "ClearAll clears the character");
        }

        [TestMethod]
        public unsafe void S5C3_SkinnedBind_And_SkinnedPayload_Decode()
        {
            var a = new ModelAssembler();
            var rows = new float[78 * 12];
            for (int i = 0; i < 78; i++) { rows[i * 12] = 1; rows[i * 12 + 4] = 1; rows[i * 12 + 8] = 1; rows[i * 12 + 10] = i * 0.01f; }
            byte[] bind = ModelCodec.EncodeSkinnedBind(new SkinnedBindDefinition { ModelKey = Model, BoneCount = 78, Rows = rows });
            Assert.AreEqual(C3Outcome.SkinnedBind, Feed(a, C3Type.SkinnedBind, bind));
            Assert.AreEqual(Model, a.CompletedBind.ModelKey);
            Assert.AreEqual(78 * 12, a.CompletedBind.Rows.Length);
            Assert.AreEqual(0.77f, a.CompletedBind.Rows[77 * 12 + 10], 1e-6f);
            var cut = new byte[bind.Length - 4];
            Buffer.BlockCopy(bind, 0, cut, 0, cut.Length);
            Assert.AreEqual(C3Outcome.Error, Feed(a, C3Type.SkinnedBind, cut), "size mismatch refused");
            rows[5] = float.NaN;
            Assert.AreEqual(C3Outcome.Error, Feed(a, C3Type.SkinnedBind, ModelCodec.EncodeSkinnedBind(new SkinnedBindDefinition { ModelKey = Model, BoneCount = 78, Rows = rows })), "non-finite refused");

            ModelData m = SkinnedTriangle(out ModelBegin b, out byte[] payload);
            Assert.IsTrue(m.Skinned);
            CollectionAssert.AreEqual(new byte[] { 2, 3, 0, 0, 13, 0, 0, 0, 52, 2, 0, 0 }, m.BoneIdx);
            Assert.AreEqual(0.75f, m.Weights[0]);
            Assert.AreEqual(0.25f, m.Weights[1]);
        }

        /// <summary>A skinned 3-vertex model (76-B vertices) parsed by the plugin's payload parser.</summary>
        private static unsafe ModelData SkinnedTriangle(out ModelBegin begin, out byte[] payload)
        {
            const int v = 3;
            payload = new byte[v * 76 + 3 * 4 + 24];
            float[][] pos = { new float[] { 0, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 0, 1, 0 } };
            byte[][] idx = { new byte[] { 2, 3, 0, 0 }, new byte[] { 13, 0, 0, 0 }, new byte[] { 52, 2, 0, 0 } };
            float[][] w = { new[] { 0.75f, 0.25f, 0, 0 }, new[] { 1f, 0, 0, 0 }, new[] { 0.5f, 0.5f, 0, 0 } };
            fixed (byte* p = payload)
            {
                for (int i = 0; i < v; i++)
                {
                    float* f = (float*)(p + i * 76);
                    f[0] = pos[i][0]; f[1] = pos[i][1]; f[2] = pos[i][2];
                    f[5] = 1; // normal +z (SE)
                    f[6] = 1; // tangent +x
                    f[10] = 1; // binormal +y
                    byte* bi = p + i * 76 + 56;
                    for (int k = 0; k < 4; k++) bi[k] = idx[i][k];
                    float* bw = (float*)(bi + 4);
                    for (int k = 0; k < 4; k++) bw[k] = w[i][k];
                }
                uint* ix = (uint*)(p + v * 76);
                ix[0] = 0; ix[1] = 1; ix[2] = 2;
                byte* sm = p + v * 76 + 12;
                *(ulong*)sm = MatA; *(uint*)(sm + 8) = 0; *(uint*)(sm + 12) = 1;
            }
            begin = new ModelBegin { modelKey = Model, vertexCount = v, indexCount = 3, submeshCount = 1, flags = ModelFlags.Skinned, chunkCount = 1, totalBytes = (uint)payload.Length };
            ModelData m;
            string error;
            Assert.IsTrue(ModelPayload.TryParse(begin, payload, payload.Length, out m, out error), error);
            return m;
        }

        // ------------------------------------------------------------------------------------- chunks

        [TestMethod]
        public void S5Chunks_KeyBits_SplitChunks_DynamicSkipped_IndexFormat()
        {
            ModelData cube = ProceduralCube.Model(Model, 1.25f, MatA);
            PreparedModel m = MeshBuilder.Prepare(cube, true, true);
            var input = new CellBuildInput { GridId = 1, Cell = new CellKey(0, 0, 0), Version = 1, Options = new BuildOptions() };
            Func<ulong, uint, float, PartRecord> part = (key, flags, x) =>
            {
                uint cls;
                var p = new PartRecord { EntityId = 1, PartKey = key, ModelKey = Model, Flags = flags, Role = S5PartRules.Role(flags, out cls), Class = cls };
                p.Local[0] = 1; p.Local[4] = 1; p.Local[8] = 1; p.Local[9] = x;
                return p;
            };
            input.Parts.Add(new PartBuild { Part = part(1, PF(1), 0), Model = m });
            input.Parts.Add(new PartBuild { Part = part(2, PF(2, S5Conv.NoShadows), 3), Model = m });
            input.Parts.Add(new PartBuild { Part = part(3, PF(4, S5Conv.Dynamic), 6), Model = m });
            CellBuildResult r = MeshBuilder.BuildCell(input);
            Assert.AreEqual(2, r.Chunks.Count, "NoShadows parts form their own chunk; the Dynamic part is not chunked");
            Assert.AreEqual(2, r.Parts);
            Assert.AreEqual(0u, r.Chunks[0].Key.Bits);
            Assert.IsTrue(r.Chunks[1].Key.NoShadows);
            Assert.AreEqual(r.Chunks[0].Key.ToString() + ".b100", r.Chunks[1].Key.ToString(), "bits only appear when set");
            Assert.AreNotEqual(r.Chunks[0].Key, r.Chunks[1].Key);
            Assert.IsFalse(r.Chunks[0].Use32);

            // > 65,535 referenced vertices: UInt32 indices (CONTRACT §5.4)
            int tris = 21846; // 65,538 vertices
            var big = new ModelData
            {
                Key = 2, VertexCount = tris * 3, IndexCount = tris * 3, SubmeshCount = 1, PatternScale = 1,
                Pos = new float[tris * 9], Nrm = new float[tris * 9], Tan = new float[tris * 9], Bin = new float[tris * 9], Uv = new float[tris * 6],
                Indices = new int[tris * 3], Submeshes = new[] { new SubmeshInfo { MaterialKey = MatA, TriStart = 0, TriCount = tris } },
            };
            for (int i = 0; i < tris * 3; i++)
            {
                big.Indices[i] = i;
                int t = i / 3, k = i % 3;
                big.Pos[i * 3] = t * 0.01f + (k == 1 ? 0.005f : 0f);
                big.Pos[i * 3 + 1] = k == 2 ? 0.005f : 0f;
                big.Nrm[i * 3 + 2] = -1;
            }
            var bin = new CellBuildInput { GridId = 1, Cell = new CellKey(0, 0, 0), Version = 2, Options = new BuildOptions() };
            bin.Parts.Add(new PartBuild { Part = part(9, PF(1), 0), Model = MeshBuilder.Prepare(big, true, true) });
            CellBuildResult rb = MeshBuilder.BuildCell(bin);
            Assert.AreEqual(1, rb.Chunks.Count);
            Assert.AreEqual(tris * 3, rb.Chunks[0].VertexCount);
            Assert.IsTrue(rb.Chunks[0].Use32 && rb.Chunks[0].Indices16 == null, "UInt32 above 65,535 vertices");
            Assert.AreEqual(ChunkMath.MaxUInt16Vertices, 65535);
        }

        [TestMethod]
        public void S5Chunks_DrainGate_TenFramesRegardless()
        {
            var g = new DrainGate();
            Assert.IsTrue(g.Allow(true));
            for (int i = 1; i < DrainGate.MaxDeferredFrames; i++) Assert.IsFalse(g.Allow(false), "frame " + i + " waits for a complete drain");
            Assert.IsTrue(g.Allow(false), "the 10th frame builds regardless");
            Assert.AreEqual(1, g.ForcedFrames);
            Assert.IsFalse(g.Allow(false), "counting starts again");
            Assert.IsTrue(g.Allow(true), "a complete drain resets the count");
            Assert.AreEqual(ChunkMath.MaxDeferFrames, DrainGate.MaxDeferredFrames);
        }

        [TestMethod]
        public void S5Chunks_BuildQueue_DirtyNewestThenNearest_OneInFlight_Cap256()
        {
            var q = new CellBuildQueue();
            q.BeginFrame();
            Func<int, long, double, CellCandidate> c = (x, f, d) => new CellCandidate { Key = new GridCell(1, new CellKey(x, 0, 0)), DirtyFrame = f, Distance = d };
            q.Offer(c(0, 5, 100));
            q.Offer(c(1, 7, 300));
            q.Offer(c(2, 7, 10));
            q.Offer(c(3, 5, 1));
            List<CellCandidate> p = q.Pick();
            CollectionAssert.AreEqual(new[] { 2, 1, 3, 0 }, p.ConvertAll(x => x.Key.Cell.X), "newest dirty frame first, then nearest");
            q.MarkPosted(p[0].Key);
            Assert.IsTrue(q.IsInFlight(new GridCell(1, new CellKey(2, 0, 0))));
            q.BeginFrame();
            q.Offer(c(2, 9, 0));
            Assert.AreEqual(0, q.Pick().Count, "one build in flight per cell");
            Assert.AreEqual(1, q.SkippedInFlight);
            q.MarkDone(new GridCell(1, new CellKey(2, 0, 0)));
            q.BeginFrame();
            for (int i = 0; i < 300; i++) q.Offer(c(100 + i, 1, i));
            Assert.AreEqual(CellBuildQueue.MaxInFlight, q.Pick().Count, "≤ 256 queued");
            Assert.AreEqual(44, q.Capped);
            Assert.AreEqual(ChunkMath.MaxQueuedBuilds, CellBuildQueue.MaxInFlight);

            var d = new DirtyCellSet();
            DirtyCellSet.CurrentFrame = 41;
            d.Add(new CellKey(1, 1, 1));
            DirtyCellSet.CurrentFrame = 42;
            d.Add(new CellKey(2, 2, 2));
            d.Add(new CellKey(1, 1, 1));
            Assert.AreEqual(42, d.FrameOf(new CellKey(1, 1, 1)), "re-dirtied: newest frame");
            Assert.AreEqual(long.MinValue, d.FrameOf(new CellKey(9, 9, 9)));
            int n = 0;
            foreach (CellKey k in d) n++;
            Assert.AreEqual(2, n);
        }

        // ------------------------------------------------------------------------------------- mirror math and rows

        [TestMethod]
        public void S5Math_UnityLocal_EqualsPartTransformAndWireToUnity()
        {
            var rnd = new Random(5);
            for (int k = 0; k < 20; k++)
            {
                Quat q = AxisAngle(new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5), rnd.NextDouble() * 6);
                var t = new Vec3(rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 20 - 10, rnd.NextDouble() * 20 - 10);
                float[] rows = Rows(q, t);
                Trs u = MirrorXform.UnityLocal(rows, 0);
                // the S3 pass's mirrored part matrix (columns of M·A·M, M·T)
                var a = new double[9];
                var tt = new double[3];
                MeshBuilder.PartTransform(rows, true, a, tt);
                Aff fromTrs = Aff.FromTrs(u);
                Near(fromTrs.L.C0, new Vec3(a[0], a[1], a[2]), 1e-6, "c0");
                Near(fromTrs.L.C1, new Vec3(a[3], a[4], a[5]), 1e-6, "c1");
                Near(fromTrs.L.C2, new Vec3(a[6], a[7], a[8]), 1e-6, "c2");
                Near(u.P, new Vec3(tt[0], tt[1], tt[2]), 1e-9, "t");
                NearQ(u.Q, MirrorXform.MirrorQ(q), 1e-6, "rotation = (−x, −y, z, w)");
                UnityTrs w = InstanceMath.ToUnity(Se(rows));
                Near(u.P, w.Position, 1e-9, "Wire position");
                NearQ(u.Q, w.Rotation, 1e-6, "Wire rotation");
                Near(u.S, w.Scale, 1e-6, "Wire scale");
            }
            // round trip SE → Unity → SE and the row product
            float[] r1 = Rows(AxisAngle(new Vec3(1, 2, 3), 0.7), new Vec3(1, 2, 3)), r2 = Rows(AxisAngle(new Vec3(0, 1, 0), 1.1), new Vec3(-4, 0, 2));
            Aff x = Aff.FromSeRows(r1, 0), y = Aff.FromSeRows(r2, 0);
            Aff xy = Aff.SeProduct(x, y);
            SeMatrix wxy = InstanceMath.Multiply(Se(r1), Se(r2));
            Near(xy.Apply(new Vec3(0.3, -0.2, 0.9)), wxy.Transform(new Vec3(0.3, -0.2, 0.9)), 1e-6, "SE row product (a then b)");
            NearAff(x * x.Inverse(), Aff.Identity, 1e-9, "inverse");
            Trs improper = new Aff(Mat3.FromColumns(new Vec3(-1, 0, 0), Vec3.UnitY, Vec3.UnitZ), Vec3.Zero).Decompose();
            Assert.IsTrue(improper.S.X < 0, "an improper matrix keeps a negative x scale");
        }

        [TestMethod]
        public void S5Rows_Interpolation_Alpha1Exact_MissingOlderTakesNewer_EqualsWire()
        {
            float[] a = Rows(AxisAngle(new Vec3(0, 1, 0), 0.2), new Vec3(0, 0, 0));
            float[] b = Rows(AxisAngle(new Vec3(0, 1, 0), 0.6), new Vec3(2, 4, -6));
            Aff at1 = RowSampler.Interpolate(a, 0, b, 0, 1.0);
            NearAff(at1, Aff.FromSeRows(b, 0), 0, "alpha 1: the newer row exactly (S5.N3)");
            NearAff(RowSampler.Interpolate(a, 0, b, 0, 0.0), Aff.FromSeRows(a, 0), 0, "alpha 0: the older row");
            NearAff(RowSampler.Interpolate(null, 0, b, 0, 0.3), Aff.FromSeRows(b, 0), 0, "no older row: the newer one");
            Aff half = RowSampler.Interpolate(a, 0, b, 0, 0.5);
            Near(half.T, new Vec3(1, 2, -3), 1e-6, "T lerp");
            NearQ(half.Decompose().Q, AxisAngle(new Vec3(0, 1, 0), 0.4), 1e-6, "R slerp");
            SeMatrix w = InstanceMath.Interpolate(Se(a), Se(b), 0.5);
            Near(half.T, w.Translation, 1e-9, "Wire T");
            Near(half.L.C0, w.Right, 1e-6, "Wire R");
            Near(half.L.C2, w.Backward, 1e-6, "Wire B");
        }

        [TestMethod]
        public unsafe void S5C2_SubpartRows_ReadInTheSameSeqlockWindow_InvalidRefused()
        {
            var buf = new byte[SeEntitiesLimits.SubpartsOffset + 4 * SeEntitiesLimits.SubpartSize];
            fixed (byte* b = buf)
            {
                var r = new EntityTableReader(b);
                SeEntitiesHeader* hdr = (SeEntitiesHeader*)b;
                hdr->entityCount = 1;
                hdr->subpartCount = 3;
                hdr->tick = 9;
                SeEntity* e = (SeEntity*)(b + SeEntitiesLimits.EntitiesOffset);
                e[0].entityId = 1; e[0].q = new F32x4(0, 0, 0, 1); e[0].s = 1; e[0].k = 1;
                SeSubpart* sp = (SeSubpart*)(b + SeEntitiesLimits.SubpartsOffset);
                float[] rows = Rows(AxisAngle(new Vec3(0, 0, 1), 0.5), new Vec3(1, 2, 3));
                for (int j = 0; j < 12; j++) { sp[0].local[j] = rows[j]; sp[2].local[j] = rows[j]; }
                sp[0].ownerId = 1; sp[0].partKey = 2;           // a subgrid row (root 1, subgrid 2)
                sp[1].ownerId = 1; sp[1].partKey = 3; sp[1].local[4] = float.NaN;
                sp[2].ownerId = 2; sp[2].partKey = 0xABC;       // a dynamic subpart of the subgrid
                hdr->seq = 2;
                Assert.AreEqual(SeqlockReadStatus.Fresh, r.Poll());
                EntitySnapshot s = r.History.Newest;
                Assert.AreEqual(2, s.RowCount, "the NaN row is refused");
                Assert.AreEqual(1, r.RowsInvalid);
                int i;
                Assert.IsTrue(s.TryGetRow(1, 2, out i));
                Assert.AreEqual(3f, s.RowLocal[i * 12 + 11]);
                Assert.IsTrue(s.TryGetRow(2, 0xABC, out i));
                Assert.IsFalse(s.TryGetRow(1, 3, out i));
                Assert.IsFalse(s.TryGetRow(2, 2, out i), "rows are keyed by (owner, key)");
            }
        }

        /// </summary>
        private static EntitySnapshot Snap(EntityHistory h, ulong tick, params Tuple<ulong, ulong, float>[] rows)
        {
            EntitySnapshot s = h.Rent();
            s.Tick = tick;
            s.Count = 1;
            s.Poses[0] = new EntityPose { Id = 1, Qw = 1, S = 1, K = 1 };
            s.RowCount = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                s.RowOwner[i] = rows[i].Item1;
                s.RowKey[i] = rows[i].Item2;
                float[] r = Rows(Quat.Identity, new Vec3(rows[i].Item3, 0, 0));
                Array.Copy(r, 0, s.RowLocal, i * 12, 12);
            }
            h.Push(s);
            return s;
        }

        [TestMethod]
        public void S5Rows_SampleAtBracket_Alpha1_Sticky_PreviousMutant()
        {
            var h = new EntityHistory();
            Snap(h, 10, Tuple.Create(1UL, 2UL, 0f), Tuple.Create(1UL, 3UL, 5f));
            Snap(h, 11, Tuple.Create(1UL, 2UL, 1f), Tuple.Create(1UL, 4UL, 7f));
            Snap(h, 12, Tuple.Create(1UL, 2UL, 2f));
            Aff se;
            ulong tick;
            double alpha;
            PoseHow how;
            var br = new CamBracket { Valid = true, Older = 11, Newer = 12, Alpha = 0.25 };
            Assert.IsTrue(RowSampler.Sample(h, 1, 2, br, false, out se, out tick, out alpha, out how));
            Assert.AreEqual(1.25, se.T.X, 1e-9, "interpolated with the camera's alpha");
            Assert.AreEqual(PoseHow.Interpolated, how);
            Assert.AreEqual(12UL, tick);
            br.Alpha = 1;
            RowSampler.Sample(h, 1, 2, br, false, out se, out tick, out alpha, out how);
            Assert.AreEqual(2.0, se.T.X, 0, "alpha 1: SE's row of the newer tick exactly (S5.N3)");
            br.Alpha = 1.6;
            RowSampler.Sample(h, 1, 2, br, false, out se, out tick, out alpha, out how);
            Assert.AreEqual(2.0, se.T.X, 0, "rows are not extrapolated");
            Assert.AreEqual(1.0, alpha);
            br.Alpha = 0.5;
            Assert.IsTrue(RowSampler.Sample(h, 1, 4, br, false, out se, out tick, out alpha, out how), "dropped from the newest table: held");
            Assert.AreEqual(7.0, se.T.X);
            Assert.AreEqual(PoseHow.Held, how);
            Assert.AreEqual(11UL, tick);
            Assert.IsTrue(RowSampler.Sample(h, 1, 3, br, false, out se, out tick, out alpha, out how), "sticky over two ticks");
            Assert.AreEqual(5.0, se.T.X);
            Assert.IsFalse(RowSampler.Sample(h, 1, 99, br, false, out se, out tick, out alpha, out how), "never seen");
            // a row missing from the older bracket snapshot takes the newer value
            Snap(h, 13, Tuple.Create(1UL, 2UL, 3f), Tuple.Create(1UL, 5UL, 9f));
            br = new CamBracket { Valid = true, Older = 12, Newer = 13, Alpha = 0.5 };
            RowSampler.Sample(h, 1, 5, br, false, out se, out tick, out alpha, out how);
            Assert.AreEqual(9.0, se.T.X);
            Assert.AreEqual(PoseHow.Exact, how);
            // S5.M1: the snapshot before the newer tick, not interpolated
            RowSampler.Sample(h, 1, 2, br, true, out se, out tick, out alpha, out how);
            Assert.AreEqual(2.0, se.T.X, 0, "one tick behind");
            Assert.AreEqual(PoseHow.Previous, how);
            Assert.AreEqual(12UL, tick);
            // no camera sample: the newest
            RowSampler.Sample(h, 1, 2, default(CamBracket), false, out se, out tick, out alpha, out how);
            Assert.AreEqual(3.0, se.T.X);
        }

        // ------------------------------------------------------------------------------------- character math

 /// <summary>A 4-bone chain (root, pelvis, spine, hand) with rotated, offset absolute binds (SE rows).</summary>
        private static void Chain(out int[] parents, out float[] absBind, out float[] skinnedBind)
        {
            parents = new[] { -1, 0, 1, 2 };
            var abs = new Aff[4];
            Quat[] q = { Quat.Identity, AxisAngle(new Vec3(0, 1, 0), 0.3), AxisAngle(new Vec3(1, 0, 0), 0.4), AxisAngle(new Vec3(0, 0, 1), -0.7) };
            Vec3[] t = { new Vec3(0, 0, 0), new Vec3(0, 1, 0.1), new Vec3(0.05, 1.4, 0), new Vec3(-0.5, 1.2, -0.2) };
            absBind = new float[48];
            skinnedBind = new float[48];
            for (int i = 0; i < 4; i++)
            {
                float[] r = Rows(q[i], t[i]);
                Array.Copy(r, 0, absBind, i * 12, 12);
                Aff.FromSeRows(r, 0).Inverse().ToSeRows(skinnedBind, i * 12);
            }
        }

        [TestMethod]
        public void S5Character_RestLocals_Bindposes_SeBones_SkinningIdentityAndPose()
        {
            int[] parents;
            float[] abs, bind;
            Chain(out parents, out abs, out bind);
            var rest = new Trs[4];
            var absUnity = new Aff[4];
            CharacterMath.RestLocals(bind, parents, rest, absUnity);
            var world = new Aff[4];
            CharacterMath.Absolute(rest, parents, world);
            for (int i = 0; i < 4; i++) NearAff(world[i], Aff.FromSeRows(abs, i * 12).Mirrored(), 1e-6, "bone " + i + ": composed rest locals = mirrored absolute bind");
            var bp = new double[48];
            CharacterMath.Bindposes(bind, 4, bp);
            for (int i = 0; i < 4; i++) NearAff(world[i] * Aff.FromSeRows(bp, i * 12), Aff.Identity, 1e-6, "bone × bindpose = identity at rest");
            // SeBones with C1 = the bind: the rest locals
            var se = new Trs[4];
            CharacterMath.SeBoneLocals(abs, parents, se);
            for (int i = 0; i < 4; i++) { Near(se[i].P, rest[i].P, 1e-6, "P" + i); NearQ(se[i].Q, rest[i].Q, 1e-6, "Q" + i); }
            // posed: the hand turned; a vertex weighted 1 on it skins like SE (abs_new × bind applied in SE space), mirrored
            float[] posed = (float[])abs.Clone();
            Array.Copy(Rows(AxisAngle(new Vec3(0, 0, 1), 0.9), new Vec3(-0.6, 1.1, -0.1)), 0, posed, 36, 12);
            CharacterMath.SeBoneLocals(posed, parents, se);
            CharacterMath.Absolute(se, parents, world);
            var v = new Vec3(-0.55, 1.15, -0.25);
            Vec3 unity = (world[3] * Aff.FromSeRows(bp, 36)).Apply(WireMath.Mirror(v));
            Vec3 seSkinned = Aff.SeProduct(Aff.FromSeRows(bind, 36), Aff.FromSeRows(posed, 36)).Apply(v);
            Near(unity, WireMath.Mirror(seSkinned), 1e-5, "Unity skinning = mirrored SE skinning");
            // SkinPointSe of Wire agrees on the same vertex
            var bindM = new List<SeMatrix>(); var absM = new List<SeMatrix>();
            for (int i = 0; i < 4; i++) { bindM.Add(Se(bind, i * 12)); absM.Add(Se(posed, i * 12)); }
            Vec3 wire = SkinMath.SkinPointSe(v, new byte[] { 3, 0, 0, 0 }, new float[] { 1, 0, 0, 0 }, 0, bindM, absM);
            Near(wire, seSkinned, 1e-5, "Wire SkinMath.SkinPointSe");
        }

        [TestMethod]
        public void S5Character_PathG_AlignRest_APoseArmOntoTPoseSource_DirectionChildren()
        {
            // review F10: a 4-row arm (collarbone > upper arm > forearm > hand); SE bind = A-pose (45 deg down), source bind = T-pose
            int[] parents = { -1, 0, 1, 2 };
            double c = Math.Cos(Math.PI / 4), s = Math.Sin(Math.PI / 4);
            var sePos = new[] { new Vec3(0, 1.4, 0), new Vec3(0.2, 1.4, 0), new Vec3(0.2 + 0.3 * c, 1.4 - 0.3 * s, 0), new Vec3(0.2 + 0.55 * c, 1.4 - 0.55 * s, 0) };
            var snPos = new[] { new Vec3(0, 1.4, 0), new Vec3(0.2, 1.4, 0), new Vec3(0.5, 1.4, 0), new Vec3(0.75, 1.4, 0) };
            var mapped = new[] { true, true, true, true };
            var valid = new[] { true, true, true, true };
            Quat a = AxisAngle(new Vec3(0, 0, 1), -Math.PI / 4);
            var seRest = new[] { Quat.Identity, a, a, a };
            int[] dir = CharacterMath.DirectionChildren(parents, mapped, new[] { "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand" });
            CollectionAssert.AreEqual(new[] { 1, 2, 3, -1 }, dir, "each row points at its mapped child; the hand has none");
            var aligned = new Quat[4];
            var scratch = new Quat[4];
            CharacterMath.AlignRest(seRest, sePos, snPos, valid, mapped, dir, parents, aligned, scratch);
            Vec3 turned = (aligned[1] * seRest[1].Conjugate()).Rotate(sePos[2] - sePos[1]);
            Near(turned.Normalized(), new Vec3(1, 0, 0), 1e-9, "the upper arm's bind direction is turned onto the source's");
            NearQ(scratch[3], scratch[2], 1e-12, "a leaf inherits its parent's correction");
            NearQ(aligned[0], seRest[0], 1e-12, "collarbone: both binds point +x already, no correction");
            // the axial preference: pelvis (0) > left thigh (1, mapped), > spine1 (2, unmapped) > spine2 (3, mapped, Spine)
            int[] dir2 = CharacterMath.DirectionChildren(new[] { -1, 0, 0, 2 }, new[] { true, true, false, true }, new[] { "Hips", "LeftUpperLeg", "-", "Spine" });
            Assert.AreEqual(3, dir2[0], "the pelvis follows the spine branch through the unmapped Spine1, not the thigh");
            Assert.AreEqual(-1, dir2[1]);
            Assert.AreEqual(3, dir2[2], "an unmapped row's frontier is its mapped descendant");
        }

        [TestMethod]
        public void S5Character_PathG_RestGivesRest_SourceRotationCarriesOver()
        {
            int[] parents;
            float[] abs, bind;
            Chain(out parents, out abs, out bind);
            var rest = new Trs[4];
            CharacterMath.RestLocals(bind, parents, rest);
            var seRestWorld = new Quat[4];
            CharacterMath.WorldRotations(rest, parents, seRestWorld);
            var snRest = new[] { Quat.Identity, AxisAngle(new Vec3(1, 1, 0), 0.5), AxisAngle(new Vec3(0, 1, 1), -0.2), AxisAngle(new Vec3(1, 0, 0), 1.0) };
            var mapped = new[] { false, true, true, true };
            var dst = new Trs[4];
            var scratch = new Quat[4];
            CharacterMath.RetargetGeneric((Quat[])snRest.Clone(), snRest, mapped, rest, seRestWorld, parents, dst, scratch);
            for (int i = 0; i < 4; i++) NearQ(dst[i].Q, rest[i].Q, 1e-9, "source at rest: SE at rest, row " + i);
            Quat delta = AxisAngle(new Vec3(0, 0, 1), 0.8);
            var now = (Quat[])snRest.Clone();
            now[2] = delta * snRest[2];
            CharacterMath.RetargetGeneric(now, snRest, mapped, rest, seRestWorld, parents, dst, scratch);
            NearQ(scratch[2], (delta * seRestWorld[2]).Normalized(), 1e-9, "the source's world delta applied to the SE bone");
            NearQ(scratch[3], seRestWorld[3], 1e-9, "a mapped child at rest keeps its world rotation");
            Near(dst[2].P, rest[2].P, 0, "positions stay at rest");
        }

        [TestMethod]
        public void S5Character_TPose_RaisesUpperArmsToHorizontal()
        {
            string err;
            CanonSkeleton sk = CanonSkeleton.Embedded(out err);
            // an A-pose: each upper arm 45° down from its collarbone
            var rest = new Trs[sk.Count];
            for (int i = 0; i < rest.Length; i++) rest[i] = new Trs { P = new Vec3(0, 0.1, 0), Q = Quat.Identity, S = new Vec3(1, 1, 1) };
            int ul = sk.IndexOf("SE_RigLUpperarm"), fl = sk.IndexOf("SE_RigLForearm1"), ur = sk.IndexOf("SE_RigRUpperarm"), fr = sk.IndexOf("SE_RigRForearm1");
            rest[fl] = new Trs { P = new Vec3(-0.3, -0.3, 0.02), Q = Quat.Identity, S = new Vec3(1, 1, 1) };
            rest[fr] = new Trs { P = new Vec3(0.3, -0.3, 0.02), Q = Quat.Identity, S = new Vec3(1, 1, 1) };
            Trs[] t = CharacterMath.TPose(rest, sk.Parents, ul, fl, ur, fr);
            var abs = new Aff[sk.Count];
            CharacterMath.Absolute(t, sk.Parents, abs);
            Assert.AreEqual(abs[ul].T.Y, abs[fl].T.Y, 1e-9, "left forearm joint level with the shoulder");
            Assert.AreEqual(abs[ur].T.Y, abs[fr].T.Y, 1e-9, "right forearm joint level with the shoulder");
            Assert.IsTrue(abs[fl].T.X < abs[ul].T.X && abs[fr].T.X > abs[ur].T.X, "each arm stays on its side");
            Assert.AreEqual((rest[fl].P).Length, (abs[fl].T - abs[ul].T).Length, 1e-9, "arm length kept");
            for (int i = 0; i < sk.Count; i++) if (i != ul && i != ur) NearQ(t[i].Q, rest[i].Q, 0, "only the upper arms change");
            NearQ(CharacterMath.FromTo(new Vec3(1, 0, 0), new Vec3(0, 1, 0)), AxisAngle(new Vec3(0, 0, 1), Math.PI / 2), 1e-12, "FromTo");
            Near(CharacterMath.FromTo(new Vec3(1, 0, 0), new Vec3(-1, 0, 0)).Rotate(new Vec3(1, 0, 0)), new Vec3(-1, 0, 0), 1e-12, "FromTo opposite");
        }

        [TestMethod]
        public void S5Character_SourceBlend_QuarterSecond()
        {
            var b = new SourceBlend(1);
            var a = new[] { new Trs { P = new Vec3(0, 0, 0), Q = Quat.Identity, S = new Vec3(1, 1, 1) } };
            var c = new[] { new Trs { P = new Vec3(1, 0, 0), Q = AxisAngle(new Vec3(0, 1, 0), 1), S = new Vec3(1, 1, 1) } };
            var dst = new Trs[1];
            b.Step(CharSource.SeBones, 0.016, a, dst);
            Near(dst[0].P, Vec3.Zero, 0, "first source taken at once");
            b.Step(CharSource.Retarget, 0.016, c, dst);
            Near(dst[0].P, Vec3.Zero, 1e-12, "a switch starts from the last written pose");
            b.Step(CharSource.Retarget, 0.125, c, dst);
            Near(dst[0].P, new Vec3(0.5, 0, 0), 1e-9, "half way after 0.125 s");
            b.Step(CharSource.Retarget, 0.2, c, dst);
            Near(dst[0].P, new Vec3(1, 0, 0), 0, "done after 0.25 s");
            Assert.AreEqual(1, b.Switches);
            Assert.AreEqual(MirrorBudgets.SourceBlendSeconds, CharSourceRules.BlendSeconds);
        }

        [TestMethod]
        public void S5Character_Weights_Normalised_DroppedEmpty_And_SkinnedBuild()
        {
            var idx = new byte[] { 3, 200, 5, 0, 9, 0, 0, 0 };
            var w = new[] { 2f, 1f, float.NaN, 0f, 0f, 0f, 0f, 0f };
            int dropped = 0, empty = 0;
            SkinWeights.Normalise(idx, w, 0, 78, ref dropped, ref empty);
            SkinWeights.Normalise(idx, w, 1, 78, ref dropped, ref empty);
            Assert.AreEqual(2, dropped, "index 200 and NaN dropped");
            Assert.AreEqual(1, empty);
            Assert.AreEqual(1f, w[0], 1e-6f);
            Assert.AreEqual(0f, w[1]);
            Assert.AreEqual(0, idx[4]);
            Assert.AreEqual(1f, w[4], "an empty vertex goes to bone 0");

            ModelBegin b;
            byte[] payload;
            ModelData md = SkinnedTriangle(out b, out payload);
            PreparedModel pm = MeshBuilder.Prepare(md, true, true);
            string err;
            CanonSkeleton sk = CanonSkeleton.Embedded(out err);
            var bind = new float[78 * 12];
            for (int i = 0; i < 78; i++) Array.Copy(Rows(Quat.Identity, new Vec3(0, -0.01 * i, 0)), 0, bind, i * 12, 12);
            SkinnedMeshData d = SkinnedBuilder.Build(pm, Model, bind, sk.Parents);
            Assert.IsNull(d.Error, d.Error);
            Assert.AreEqual(1, d.Submeshes.Count);
            Assert.AreEqual(MatA, d.Submeshes[0].Key);
            Assert.AreEqual(3, d.Submeshes[0].Value.Length);
            Assert.AreEqual(78 * 12, d.Bindposes.Length);
            Assert.AreEqual(78, d.RestLocals.Length);
            Assert.IsFalse(d.Use32);
            Assert.AreEqual(-0f, (float)d.Pos[2], "positions mirrored (z of vertex 0 is 0)");
            SkinnedMeshData bad = SkinnedBuilder.Build(pm, Model, new float[12], sk.Parents);
            Assert.IsNotNull(bad.Error, "a bind of the wrong size is refused");
            PreparedModel rigid = MeshBuilder.Prepare(ProceduralCube.Model(Model, 1, MatA), true, true);
            Assert.IsNotNull(SkinnedBuilder.Build(rigid, Model, bind, sk.Parents).Error, "a model without skin data is refused");
        }

        // ------------------------------------------------------------------------------------- C1 character reader

        private static unsafe void WriteC1(byte* rec, uint seq, ulong tick, ulong charId, Vec3 o, double yaw, uint bonesVersion, float boneY, byte swimFlags = 0, byte mode = 0)
        {
            SeFrame f = default(SeFrame);
            f.tick = tick;
            f.flags = SeFrameFlags.InWorld | SeFrameFlags.CharacterPresent;
            f.cameraMode = CameraMode.ThirdPerson;
            f.characterId = charId;
            f.charO = new F32x3((float)o.X, (float)o.Y, (float)o.Z);
            Quat q = AxisAngle(Vec3.UnitY, yaw);
            f.charQ = new F32x4((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
            f.charS = 1f;
            f.boneCount = 78;
            f.bonesVersion = bonesVersion;
            var sw = new SwimWireState { Version = 1, Flags = swimFlags, Mode = mode, O2 = 255 };
            SwimWire.Write(ref f, ref sw);
            *(SeFrame*)rec = f;
            float* bones = (float*)(rec + SeFrameLimits.BonesOffset);
            for (int i = 0; i < 78; i++)
            {
                float[] r = Rows(Quat.Identity, new Vec3(0, boneY * (i + 1), 0));
                for (int j = 0; j < 12; j++) bones[i * 12 + j] = r[j];
            }
            *(uint*)rec = seq;
        }

        [TestMethod]
        public unsafe void S5C1_CharacterReader_HistoryBracketAlpha_BonesBlend_Swim()
        {
            string err;
            CanonSkeleton sk = CanonSkeleton.Embedded(out err);
            var buf = new byte[CharFrameReader.CopyBytes];
            fixed (byte* b = buf)
            {
                var r = new CharFrameReader(b);
                Assert.IsFalse(r.Poll(), "never written");
                WriteC1(b, 2, 100, 7, new Vec3(0, -50, 0), 0, 0xBEEF, 0.1f);
                Assert.IsTrue(r.Poll());
                Assert.IsFalse(r.Poll(), "unchanged seq");
                WriteC1(b, 4, 101, 7, new Vec3(1, -50, 0), 0.2, 0xBEEF, 0.2f, SwimWireFlags.InWater, (byte)SwimMode.Swim);
                Assert.IsTrue(r.Poll());
                Assert.AreEqual(2, r.Count);
                CharFrame n = r.Newest;
                Assert.IsTrue(n.BonesValid && n.SwimValid);
                Assert.AreEqual((byte)SwimMode.Swim, n.Swim.Mode);
                var bones = new Trs[78];
                CharFrame newer, older;
                double alpha;
                EntityPose pose;
                bool bonesOk;
                var br = new CamBracket { Valid = true, Older = 100, Newer = 101, Alpha = 0.5 };
                Assert.IsTrue(CharSampler.Sample(r, br, false, sk.Parents, out newer, out older, out alpha, out pose, bones, out bonesOk));
                Assert.AreEqual(101UL, newer.Tick);
                Assert.AreEqual(100UL, older.Tick);
                Assert.AreEqual(0.5, pose.Ox, 1e-6, "o lerped with the camera's alpha");
                NearQ(pose.Q, AxisAngle(Vec3.UnitY, 0.1), 1e-6, "q slerped");
                Assert.IsTrue(bonesOk);
                // bone 0 (root) local = its absolute: y = 0.15 at alpha 0.5 (0.1 and 0.2)
                Assert.AreEqual(0.15, bones[0].P.Y, 1e-6, "bones blended with the same alpha");
                br.Alpha = 1;
                CharSampler.Sample(r, br, false, sk.Parents, out newer, out older, out alpha, out pose, bones, out bonesOk);
                Assert.AreEqual(1.0, pose.Ox, 1e-6, "alpha 1: the newer pose exactly");
                Assert.AreEqual(0.2, bones[0].P.Y, 1e-6);
                // a different bonesVersion in the older tick: the newer bones held
                WriteC1(b, 6, 102, 7, new Vec3(2, -50, 0), 0.2, 0xCAFE, 0.3f);
                r.Poll();
                br = new CamBracket { Valid = true, Older = 101, Newer = 102, Alpha = 0.5 };
                CharSampler.Sample(r, br, false, sk.Parents, out newer, out older, out alpha, out pose, bones, out bonesOk);
                Assert.AreEqual(0.3, bones[0].P.Y, 1e-6, "bones of another version are not blended");
                // tick backwards restarts the history
                WriteC1(b, 8, 50, 7, Vec3.Zero, 0, 0xCAFE, 0.3f);
                r.Poll();
                Assert.AreEqual(1, r.Count);
                // no bones
                WriteC1(b, 10, 51, 7, Vec3.Zero, 0, 0, 0.3f);
                r.Poll();
                Assert.IsFalse(r.Newest.BonesValid, "bonesVersion 0 = no bones");
            }
        }

        [TestMethod]
        public void S5Character_SourceAndDisplayRules()
        {
            var i = new CharSourceInputs { BonesValid = true, SwimValid = true, SwimFlags = SwimWireFlags.InWater, SwimMode = (byte)SwimMode.Swim, RetargetReady = true };
            Assert.AreEqual(CharSource.Retarget, CharSourceRules.Select(ref i));
            i.SwimMode = (byte)SwimMode.Surface;
            Assert.AreEqual(CharSource.Retarget, CharSourceRules.Select(ref i), "surface swimming retargets");
            i.ToolHeld = true;
            // S13 (point 6) replaced the rule (SE's bones): the legs swim, SE's upper body holds the item
            Assert.AreEqual(CharSource.Blend, CharSourceRules.Select(ref i), "a gun or tool in hand while swimming: the swim legs under SE's arms (S13)");
            i.ToolHeld = false;
            i.SwimFlags |= SwimWireFlags.Grounded;
            Assert.AreEqual(CharSource.SeBones, CharSourceRules.Select(ref i), "grounded: SE's bones");
            i.SwimFlags = SwimWireFlags.InWater;
            i.SwimMode = (byte)SwimMode.Fly;
            Assert.AreEqual(CharSource.SeBones, CharSourceRules.Select(ref i), "jetpack: SE's bones");
            i.SwimMode = (byte)SwimMode.Swim;
            i.Seated = true;
            Assert.AreEqual(CharSource.SeBones, CharSourceRules.Select(ref i), "seated");
            i.Seated = false;
            i.RetargetReady = false;
            Assert.AreEqual(CharSource.SeBones, CharSourceRules.Select(ref i), "no retarget path: SE's bones while swimming");
            i.BonesValid = false;
            Assert.AreEqual(CharSource.Bind, CharSourceRules.Select(ref i));
            i.SwimValid = false;
            i.RetargetReady = true;
            Assert.AreEqual(CharSource.Bind, CharSourceRules.Select(ref i), "no extension: no retarget");

            uint head = PF(5, S5Conv.HiddenInFirstPerson), body = PF(5);
            Assert.IsFalse(CharSourceRules.PartDrawn(head, CameraMode.FirstPerson, FpBody.Se), "S5.N7b: head hidden in first person");
            Assert.IsTrue(CharSourceRules.PartDrawn(body, CameraMode.FirstPerson, FpBody.Se));
            Assert.IsTrue(CharSourceRules.PartDrawn(head, CameraMode.ThirdPerson, FpBody.Se), "drawn in third person");
            Assert.IsFalse(CharSourceRules.PartDrawn(body, CameraMode.FirstPerson, FpBody.Diver), "diver: no SE body in first person");
            Assert.IsTrue(CharSourceRules.PartDrawn(body, CameraMode.ThirdPerson, FpBody.Diver));
            Assert.AreEqual(0.5, CharSourceRules.BlendWeight(0.125), 1e-12);
            Assert.AreEqual(1.0, CharSourceRules.BlendWeight(1));
            Assert.AreEqual(0.0, CharSourceRules.BlendWeight(-1));
        }

        // ------------------------------------------------------------------------------------- switches, mutants, stats

        [TestMethod]
        public void S5PerfMutants_FullInSpace_CamerasOnInTrack()
        {
            var i = new RecipeInputs { C5Valid = true, PeerAlive = true, PerfMode = PerfMode.Idle, ViewState = ViewState.Space };
            RecipeDecision d = PerfRecipeRules.Select(ref i);
            S5PerfMutants.Apply(ref d, ref i, false, false);
            Assert.AreEqual(PerfMode.Idle, d.Recipe, "off: unchanged");
            S5PerfMutants.Apply(ref d, ref i, true, false);
            Assert.AreEqual(PerfMode.Full, d.Recipe, "S5.M4: Full in Space");
            Assert.IsTrue(d.CamerasOn && !d.IdleCause && !d.BelowNormal);
            StringAssert.Contains(d.Why, "S5.M4");
            i.PerfMode = PerfMode.Track;
            i.ViewState = ViewState.Approach;
            d = PerfRecipeRules.Select(ref i);
            S5PerfMutants.Apply(ref d, ref i, true, false);
            Assert.AreEqual(PerfMode.Track, d.Recipe, "S5.M4 only in Space");
            Assert.IsFalse(d.CamerasOn);
            S5PerfMutants.Apply(ref d, ref i, false, true);
            Assert.IsTrue(d.CamerasOn, "S5.M5: cameras on in Track");
            Assert.AreEqual(PerfMode.Track, d.Recipe);
        }

        [TestMethod]
        public void S5Switches_Stats_Sample_Mutants()
        {
            FpBody f;
            Assert.IsTrue(S5Switches.TryParseFpBody(" SE ", out f) && f == FpBody.Se);
            Assert.IsTrue(S5Switches.TryParseFpBody("diver", out f) && f == FpBody.Diver);
            Assert.IsFalse(S5Switches.TryParseFpBody("arms", out f));
            Assert.AreEqual("se", S5Switches.Name(FpBody.Se));

            var s = new FrameStats();
            for (int i = 1; i <= 100; i++) s.Add(i);
            double p50, p99, max;
            int n;
            s.Take(out p50, out p99, out max, out n);
            Assert.AreEqual(100, n);
            Assert.AreEqual(50, p50);
            Assert.AreEqual(99, p99);
            Assert.AreEqual(100, max);
            Assert.AreEqual(0, s.Count, "the window restarts");

            List<ulong> a = PartSample.Pick(new ulong[] { 5, 1, 9, 3, 7 }, 3);
            List<ulong> b = PartSample.Pick(new ulong[] { 9, 7, 5, 3, 1 }, 3);
            CollectionAssert.AreEqual(a, b, "the sample does not depend on the order");
            Assert.AreEqual(3, a.Count);
            Assert.AreNotEqual(PartSample.Mix(1), PartSample.Mix(2));

            foreach (string m in new[] { DevMutants.S5PreviousSnapshot, DevMutants.S5FlipOff, DevMutants.S5FullInSpace, DevMutants.S5CamerasOnInTrack })
                Assert.IsTrue(DevMutants.IsKnown(m), m);
            Assert.AreEqual("S5.M1", DevMutants.S5PreviousSnapshot);
            Assert.AreEqual("S5.M2", DevMutants.S5FlipOff);
        }

        [TestMethod]
        public void S5Scale_5000Blocks_CellIndex_Queue_EveryTriangleOnce()
        {
            var s = new MirrorState();
            Feed(s, C4Type.GridAdd, Bytes(Grid(1, S5Conv.GridVersion1 | S5Conv.GridStatic)));
            ulong key = 0;
            for (int x = 0; x < 17; x++)
                for (int y = 0; y < 17; y++)
                    for (int z = 0; z < 17; z++)
                        Feed(s, C4Type.PartAdd, Bytes(Part(1, ++key, PF(1), x * 2.5f, y * 2.5f, -z * 2.5f, h: (x % 3) * 0.1f)));
            GridRecord g = s.Grids[1];
            Assert.AreEqual(4913, g.Parts.Count);
            int indexed = 0;
            foreach (HashSet<ulong> set in g.CellParts.Values) indexed += set.Count;
            Assert.AreEqual(4913, indexed, "every chunked part in exactly one cell");
            Assert.AreEqual(3 * 3 * 3, g.CellParts.Count, "42.5 m of blocks in 20 m cells (z negative: floor)");
            Assert.AreEqual(27, g.DirtyCells.Count);

            PreparedModel m = MeshBuilder.Prepare(ProceduralCube.Model(Model, 1.25f, MatA), true, true);
            var q = new CellBuildQueue();
            q.BeginFrame();
            foreach (CellKey c in g.DirtyCells) q.Offer(new CellCandidate { Key = new GridCell(1, c), DirtyFrame = g.DirtyCells.FrameOf(c), Distance = c.X + c.Y + c.Z });
            List<CellCandidate> picks = q.Pick();
            Assert.AreEqual(27, picks.Count);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long tris = 0;
            int chunks = 0;
            foreach (CellCandidate c in picks)
            {
                var input = new CellBuildInput { GridId = 1, Cell = c.Key.Cell, Version = 1, Options = new BuildOptions() };
                foreach (ulong pk in g.CellParts[c.Key.Cell]) input.Parts.Add(new PartBuild { Part = g.Parts[pk].Clone(), Model = m });
                CellBuildResult r = MeshBuilder.BuildCell(input);
                tris += r.Triangles;
                chunks += r.Chunks.Count;
                Assert.AreEqual(1.0, r.MinAgreement, 1e-12, "winding kept at scale");
                foreach (ChunkMeshData cm in r.Chunks) Assert.IsFalse(cm.Use32, "a cell of ≤ 512 cubes stays 16-bit");
            }
            sw.Stop();
            Assert.AreEqual(4913L * 12, tris, "each model triangle exactly once");
            Assert.AreEqual(18 * 3 + 9 * 1, chunks, "one chunk per cell × colour: 3 paint columns, the last x cell holds column 16 only");
            Assert.IsTrue(sw.ElapsedMilliseconds < 10000, "worker build time at scale: " + sw.ElapsedMilliseconds + " ms");
        }

        [TestMethod]
        public void S5Prepare_M2_FlipSkipped_MeasureKept()
        {
            // a model whose triangles all face inwards once mirrored: the census flips it, S5.M2 does not
            ModelData cube = ProceduralCube.Model(Model, 1, MatA);
            for (int i = 0; i < cube.Indices.Length; i += 3) { int t = cube.Indices[i + 1]; cube.Indices[i + 1] = cube.Indices[i + 2]; cube.Indices[i + 2] = t; }
            PreparedModel on = MeshBuilder.Prepare(cube, true, true);
            PreparedModel off = MeshBuilder.Prepare(cube, true, true, false);
            Assert.IsTrue(on.Flipped && !on.FlipSkipped);
            Assert.IsTrue(!off.Flipped && off.FlipSkipped, "S5.M2: flip skipped");
            Assert.AreEqual(on.RawAgreement, off.RawAgreement, "the measure is logged either way");
            StringAssert.Contains(off.Describe(), "flipSkipped=true");
        }
    }
}
