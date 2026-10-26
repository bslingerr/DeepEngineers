using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S5 conventions pin (CONTRACT §5.2 "Layout decision for S5", §5.4 "C1–C4 conventions for S5"): the canonical text
 /// byte for byte and its hash 0x31dfd6f9befdef5d, the canonical skeleton recomputed from its data file
 /// (0x995765282e7d4f1d) and the generated C# table equal to it column for column, every flag inside its field, the
 /// version nibble, the key helpers and the CONTRACT example vectors, and layout 1.0 (and the S5a swim extension)
 /// unchanged.
 /// </summary>
    [TestClass]
    public unsafe class MirrorConventionsTests
    {
        /// <summary>CONTRACT §5.4, copied by hand (the contract's code block).</summary>
        private static readonly string[] ContractText =
        {
            "ext s5 mirror 1",
            "const MirrorCharacterFlags Player 1",
            "const MirrorCharacterFlags Fallback 2",
            "const MirrorCharacterFlags Version1 268435456",
            "const MirrorCharacterPart Body 1",
            "const MirrorCharacterPart Head1st 2",
            "const MirrorCharacterPart Tool 3",
            "const MirrorGridFlags Static 1",
            "const MirrorGridFlags Subgrid 2",
            "const MirrorGridFlags FloatingObject 4",
            "const MirrorGridFlags Version1 268435456",
            "const MirrorLimits MaxSkinWeights 4",
            "const MirrorLimits CanonicalBones 78",
            "const MirrorPartClass CubePart 1",
            "const MirrorPartClass Edge 2",
            "const MirrorPartClass FatBlock 3",
            "const MirrorPartClass Subpart 4",
            "const MirrorPartClass CharacterBody 5",
            "const MirrorPartClass Tool 6",
            "const MirrorPartClass FloatingObject 7",
            "const MirrorPartFlags ClassMask 15",
            "const MirrorPartFlags Dynamic 16",
            "const MirrorPartFlags HiddenInFirstPerson 32",
            "const MirrorPartFlags BuildStage 64",
            "const MirrorPartFlags Deformed 128",
            "const MirrorPartFlags NoShadows 256",
            "const MirrorPartFlags MetalnessColorable 512",
            "const MirrorPartFlags Version1 268435456",
            "const MirrorPartFlags VersionMask 4026531840",
            "format modelKey <path>|lod<n>[|scale=<s>][|skin=<skin>][|char=body|char=head1st]",
            "format partKey.cube fnv1a64(u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)",
            "format partKey.edge fnv1a64(u32 2, u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)",
            "format partKey.fat fnv1a64(utf8 fat|<minX>,<minY>,<minZ>)",
            "format partKey.subpart fnv1a64(utf8 sub|<fatKey hex16>|<name>[/<name>])",
            "format partKey.floating 1",
            "format skinId fnv1a32(utf8 lower(<skin>)) or 0",
            "skeleton canon1 78 0x995765282e7d4f1d",
        };

        private const string Armor = @"models\cubes\large\armorcube.mwm";

        // ------------------------------------------------------------------------------------------------ pins

        [TestMethod]
        public void CanonicalText_IsTheContractTextByteForByte()
        {
            string expected = string.Join("\n", ContractText) + "\n";
            string text = MirrorConventions.CanonicalText();
            Assert.AreEqual(expected, text, "MirrorConventions.CanonicalText drifted from CONTRACT §5.4");
            Assert.AreEqual(MirrorConventions.CanonicalLines, 37);
            Assert.AreEqual(MirrorConventions.CanonicalBytes, 1539);
            Assert.AreEqual(37, text.Split('\n').Length - 1, "lines");
            Assert.AreEqual(1539, Encoding.ASCII.GetByteCount(text), "bytes");
            foreach (char c in text) Assert.IsTrue(c == '\n' || (c >= 0x20 && c < 0x7F), "ASCII only");
        }

        [TestMethod]
        public void Hash_IsPinned()
        {
            Assert.AreEqual(0x31dfd6f9befdef5dUL, MirrorConventions.PinnedHash);
            Assert.AreEqual(MirrorConventions.PinnedHash, MirrorConventions.Hash,
                "conventions hash changed: bump 'ext s5 mirror <n>' and the version nibble, update both pins and MODLOG");
            Assert.AreEqual(MirrorConventions.PinnedHash, LayoutHash.Fnv1a64Ascii(string.Join("\n", ContractText) + "\n"));
        }

        [TestMethod]
        public void Skeleton_HashRecomputedFromTheDataFile()
        {
            List<string[]> rows = S5Repo.SkeletonRows();
            Assert.AreEqual(MirrorSkeleton.BoneCount, rows.Count, "78 rows");
            var sb = new StringBuilder();
            foreach (string[] r in rows) sb.Append("bone ").Append(r[0]).Append(' ').Append(r[1]).Append(' ').Append(r[2]).Append('\n');
            ulong h = LayoutHash.Fnv1a64(Encoding.UTF8.GetBytes(sb.ToString()));
            Assert.AreEqual(MirrorSkeleton.PinnedHash, h, "SeSkeleton.canon1.tsv columns 1-3 changed: a re-pin of the S5 conventions");
            Assert.AreEqual(0x995765282e7d4f1dUL, MirrorSkeleton.Hash);
            Assert.AreEqual(sb.ToString(), MirrorSkeleton.SkeletonText());
        }

        [TestMethod]
        public void Skeleton_GeneratedTableEqualsTheDataFile()
        {
            List<string[]> rows = S5Repo.SkeletonRows();
            for (int i = 0; i < rows.Count; i++)
                Assert.AreEqual(string.Join("\t", rows[i]), MirrorSkeleton.TsvRow(i),
                    "row " + i + " differs: regenerate with python -I tools/worldmap/wire_ref.py s5skeleton-cs src/DeepEngineers.Wire/MirrorSkeleton.Canon1.cs");
        }

        [TestMethod]
        public void Skeleton_Structure()
        {
            for (int i = 0; i < MirrorSkeleton.BoneCount; i++)
            {
                int p = MirrorSkeleton.Parent(i);
                Assert.IsTrue(p < i, "parent precedes child at " + i);
                Assert.AreEqual(i == 0, p < 0, "only row 0 is the root");
                Assert.AreEqual(i, MirrorSkeleton.IndexOf(MirrorSkeleton.BoneName(i)));
                if (MirrorSkeleton.Human(i) != null) Assert.AreEqual(i, MirrorSkeleton.IndexOfHuman(MirrorSkeleton.Human(i)));
            }
            Assert.IsTrue(MirrorSkeleton.MapsAllRequiredHumanBones(), "15 required human bones");
            Assert.AreEqual(15, MirrorSkeleton.RequiredHumanBones.Length);
            Assert.AreEqual("SE_RigPelvis", MirrorSkeleton.BoneName(MirrorSkeleton.Pelvis));
            Assert.AreEqual("SE_RigHead", MirrorSkeleton.BoneName(MirrorSkeleton.Head));
            Assert.AreEqual("HeadDummy", MirrorSkeleton.BoneName(MirrorSkeleton.HeadDummy));
            Assert.AreEqual("SE_RigLPalm", MirrorSkeleton.BoneName(MirrorSkeleton.LeftPalm));
            Assert.AreEqual("SE_RigRPalm", MirrorSkeleton.BoneName(MirrorSkeleton.RightPalm));
            Assert.AreEqual("SE_main_root", MirrorSkeleton.BoneName(MirrorSkeleton.MainRoot));
            Assert.AreEqual(MirrorSkeleton.IndexOf("SE_RigSpine2"), MirrorSkeleton.IndexOfHuman("Spine"));
            Assert.AreEqual(MirrorSkeleton.IndexOf("SE_RigSpine4"), MirrorSkeleton.IndexOfHuman("Chest"));
            Assert.AreEqual(MirrorSkeleton.IndexOf("SE_RigRibcage"), MirrorSkeleton.IndexOfHuman("UpperChest"));
            Assert.AreEqual(MirrorBoneRole.Follow, MirrorSkeleton.Role(MirrorSkeleton.IndexOf("SE_RigSpine1")));
            Assert.AreEqual(MirrorBoneRole.Follow, MirrorSkeleton.Role(MirrorSkeleton.IndexOf("SE_RigSpine3")));
            Assert.AreEqual(MirrorBoneRole.Root, MirrorSkeleton.Role(0));
            Assert.AreEqual(-1, MirrorSkeleton.IndexOf("NotABone"));
            // snBone filled from the S5-2a real run (Subnautica's generic player_view rig); '-' reads as null
            Assert.AreEqual("hips", MirrorSkeleton.SnBone(MirrorSkeleton.Pelvis), "pelvis maps to Subnautica's hips");
            Assert.AreEqual("head_rig", MirrorSkeleton.SnBone(MirrorSkeleton.IndexOf("SE_RigHead")));
            Assert.AreEqual("hand_R", MirrorSkeleton.SnBone(MirrorSkeleton.RightPalm));
            Assert.IsNull(MirrorSkeleton.SnBone(MirrorSkeleton.HeadDummy), "a dummy has no Subnautica bone ('-' reads as null)");
            Assert.IsNull(MirrorSkeleton.SnBone(0), "the root row has no Subnautica bone");
        }

        // ------------------------------------------------------------------------------------------------ fields

        [TestMethod]
        public void Flags_StayInsideTheirFields()
        {
            uint[] partBits =
            {
                MirrorPartFlags.Dynamic, MirrorPartFlags.HiddenInFirstPerson, MirrorPartFlags.BuildStage, MirrorPartFlags.Deformed,
                MirrorPartFlags.NoShadows, MirrorPartFlags.MetalnessColorable,
            };
            uint seen = 0;
            foreach (uint b in partBits)
            {
                Assert.AreEqual(1, PopCount(b), "single bit");
                Assert.AreEqual(0u, b & MirrorPartFlags.ClassMask, "outside the class field");
                Assert.AreEqual(0u, b & MirrorPartFlags.VersionMask, "outside the version nibble");
                Assert.AreEqual(0u, b & seen, "distinct");
                seen |= b;
            }
            for (uint c = MirrorPartClass.CubePart; c <= MirrorPartClass.FloatingObject; c++)
                Assert.AreEqual(c, c & MirrorPartFlags.ClassMask, "class inside its field");
            Assert.AreEqual(0xF0000000u, MirrorPartFlags.VersionMask);
            Assert.AreEqual(1u << MirrorConventions.VersionShift, MirrorPartFlags.Version1);
            Assert.AreEqual(MirrorPartFlags.Version1, MirrorGridFlags.Version1);
            Assert.AreEqual(MirrorPartFlags.Version1, MirrorCharacterFlags.Version1);
            Assert.AreEqual(MirrorPartFlags.Version1, MirrorPartFlags.Version1 & MirrorPartFlags.VersionMask);
            uint grid = MirrorGridFlags.Static | MirrorGridFlags.Subgrid | MirrorGridFlags.FloatingObject;
            Assert.AreEqual(3, PopCount(grid));
            Assert.AreEqual(0u, grid & MirrorPartFlags.VersionMask);
            uint chr = MirrorCharacterFlags.Player | MirrorCharacterFlags.Fallback;
            Assert.AreEqual(2, PopCount(chr));
            Assert.AreEqual(0u, chr & MirrorPartFlags.VersionMask);
            Assert.AreEqual(1UL, MirrorCharacterPart.Body);
            Assert.AreEqual(2UL, MirrorCharacterPart.Head1st);
            Assert.AreEqual(3UL, MirrorCharacterPart.Tool);
        }

        [TestMethod]
        public void Limits_FitLayout10()
        {
            var v = new ModelVertexSkinned();
            Assert.AreEqual(MirrorLimits.MaxSkinWeights, sizeof(U8x4), "4 bone indices per 76 B vertex");
            Assert.AreEqual(MirrorLimits.MaxSkinWeights * 4, sizeof(F32x4), "4 weights per 76 B vertex");
            Assert.AreEqual(76, sizeof(ModelVertexSkinned));
            GC.KeepAlive(v);
            Assert.IsTrue(MirrorLimits.CanonicalBones <= SeFrameLimits.MaxBones);
            Assert.IsTrue(SeFrameLimits.BonesOffset + MirrorLimits.CanonicalBones * SeFrameLimits.BoneSize <= 0x1F00, "C1 bones end <= 0x1F00");
            Assert.AreEqual(4000, Derec.C1RecordBytes(78));
            Assert.AreEqual(MirrorBudgets.MaxEntities, 1024);
            Assert.AreEqual(MirrorBudgets.MaxSubpartRows, 2048);
        }

        [TestMethod]
        public void Layout10_AndSwimExtensionUnchanged()
        {
            Assert.AreEqual(0x4f3a35f6fc1eaf31UL, LayoutHash.Compiled, "S5 needs no layout 1.1 (CONTRACT §5.2)");
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled);
            Assert.AreEqual(0x832ba93ca25bc307UL, SwimWire.Hash, "the S5a C1 swim extension keeps its pin");
            Assert.AreEqual(0x94, SwimWire.Offset);
            Assert.AreEqual(1, (int)Layout.Major);
            Assert.AreEqual(0, (int)Layout.Minor);
            Assert.AreEqual(100, sizeof(PartAdd));
            Assert.AreEqual(32, sizeof(GridAdd));
            Assert.AreEqual(24, sizeof(CharacterAdd));
            Assert.AreEqual(64, sizeof(SeSubpart));
            Assert.AreEqual(0x5C, (int)Marshal.OffsetOf(typeof(PartAdd), "skinId"));
            Assert.AreEqual(0x60, (int)Marshal.OffsetOf(typeof(PartAdd), "flags"));
            Assert.AreEqual(0x0C, (int)Marshal.OffsetOf(typeof(GridAdd), "flags"));
            Assert.AreEqual(0x10, (int)Marshal.OffsetOf(typeof(GridAdd), "parentEntityId"));
            Assert.AreEqual(0x10, (int)Marshal.OffsetOf(typeof(CharacterAdd), "flags"));
            Assert.AreEqual(0x14, (int)Marshal.OffsetOf(typeof(CharacterAdd), "boneCount"));
            Assert.AreEqual(0x84, (int)Marshal.OffsetOf(typeof(SeFrame), "bonesVersion"));
        }

        // ------------------------------------------------------------------------------------------------ version nibble

        [TestMethod]
        public void VersionNibble_Classifies()
        {
            Assert.AreEqual(MirrorRecordVersion.Legacy, MirrorConventions.Classify(0));
            Assert.AreEqual(MirrorRecordVersion.Legacy, MirrorConventions.Classify(0x0FFFFFFF));
            Assert.AreEqual(MirrorRecordVersion.Version1, MirrorConventions.Classify(MirrorPartFlags.Version1));
            Assert.AreEqual(MirrorRecordVersion.Version1, MirrorConventions.Classify(MirrorPartFlags.Version1 | 0x0FFFFFFF));
            for (uint v = 2; v < 16; v++)
                Assert.AreEqual(MirrorRecordVersion.Rejected, MirrorConventions.Classify(v << 28), "nibble " + v);
            Assert.AreEqual(MirrorPartClass.CubePart, MirrorConventions.EffectivePartClass(5), "legacy = cube part whatever the low bits");
            Assert.AreEqual(MirrorPartClass.CubePart, MirrorConventions.EffectivePartClass(MirrorPartFlags.Version1), "v1 class 0 = cube part");
            Assert.AreEqual(MirrorPartClass.Subpart, MirrorConventions.EffectivePartClass(MirrorConventions.PartFlags(MirrorPartClass.Subpart, MirrorPartFlags.Dynamic)));
            Assert.AreEqual(9u, MirrorConventions.EffectivePartClass(MirrorPartFlags.Version1 | 9));
            Assert.IsFalse(MirrorConventions.IsKnownPartClass(9));
            Assert.AreEqual(0u, MirrorConventions.EffectivePartClass(2u << 28));
            Assert.IsTrue(MirrorConventions.HasPartFlag(MirrorConventions.PartFlags(MirrorPartClass.Edge, MirrorPartFlags.NoShadows), MirrorPartFlags.NoShadows));
            Assert.IsFalse(MirrorConventions.HasPartFlag(MirrorPartFlags.NoShadows, MirrorPartFlags.NoShadows), "a legacy record has no S5 bits");
        }

        [TestMethod]
        public void FlagBuilders()
        {
            Assert.AreEqual(0x10000000u | 4u | 16u, MirrorConventions.PartFlags(MirrorPartClass.Subpart, MirrorPartFlags.Dynamic));
            AssertThrows<ArgumentOutOfRangeException>(() => MirrorConventions.PartFlags(0));
            AssertThrows<ArgumentOutOfRangeException>(() => MirrorConventions.PartFlags(8));
            AssertThrows<ArgumentException>(() => MirrorConventions.PartFlags(MirrorPartClass.CubePart, 2));
            AssertThrows<ArgumentException>(() => MirrorConventions.PartFlags(MirrorPartClass.CubePart, MirrorPartFlags.Version1));
            Assert.AreEqual(MirrorGridFlags.Version1 | MirrorGridFlags.Static | MirrorGridFlags.Subgrid, MirrorConventions.GridFlags(true, true));
            Assert.AreEqual(MirrorGridFlags.Version1 | MirrorGridFlags.FloatingObject, MirrorConventions.GridFlags(false, false, true));
            Assert.AreEqual(MirrorCharacterFlags.Version1 | MirrorCharacterFlags.Player, MirrorConventions.CharacterFlags(false));
            Assert.AreEqual(MirrorCharacterFlags.Version1 | MirrorCharacterFlags.Player | MirrorCharacterFlags.Fallback, MirrorConventions.CharacterFlags(true));
            Assert.AreEqual("v1 subpart dynamic", MirrorConventions.DescribePartFlags(MirrorConventions.PartFlags(MirrorPartClass.Subpart, MirrorPartFlags.Dynamic)));
            Assert.AreEqual("legacy", MirrorConventions.DescribePartFlags(0));
            Assert.AreEqual("rejected(v3)", MirrorConventions.DescribePartFlags(3u << 28));
        }

        // ------------------------------------------------------------------------------------------------ keys

        [TestMethod]
        public void Keys_ContractExampleVectors()
        {
            ulong fat = MirrorConventions.FatPartKey(-3, 0, 12);
            Assert.AreEqual("fat|-3,0,12", MirrorConventions.FatPartKeyText(-3, 0, 12));
            Assert.AreEqual(0x5148adb433ae18cfUL, fat);
            Assert.AreEqual("sub|5148adb433ae18cf|DoorLeft", MirrorConventions.SubpartKeyText(fat, "DoorLeft"));
            Assert.AreEqual(0x09b78ce1c6718cc7UL, MirrorConventions.SubpartKey(fat, "DoorLeft"));
            Assert.AreEqual(0xf4a128d807ca725cUL, MirrorConventions.SubpartKey(fat, new[] { "TurretBase", "TurretBarrel" }));
            Assert.AreEqual(0xf4a128d807ca725cUL, MirrorConventions.SubpartKey(fat, "TurretBase/TurretBarrel"));
            Assert.AreEqual(0xfded4532u, MirrorConventions.SkinId("clean_armor"));
            Assert.AreEqual(0xfded4532u, MirrorConventions.SkinId("Clean_Armor"), "lower-cased");
            Assert.AreEqual(0u, MirrorConventions.SkinId(null));
            Assert.AreEqual(0u, MirrorConventions.SkinId(""));
            // The real text has a backslash before "armorcube"; see Keys_HandedOutVectorsHashABelCharacter.
            ulong mk = MirrorConventions.ModelKey(Armor, 0);
            Assert.AreEqual(0xff3f6e15d5fff26aUL, mk);
            Assert.AreEqual(0xeb6d37388d73d6d4UL, MirrorConventions.CubePartKey(mk, 2.5f, 0f, -5f, 0f));
            Assert.AreEqual(0x3dab9da5885325a6UL, MirrorConventions.EdgePartKey(mk, 2.5f, 0f, -5f, 0f));
        }

        [TestMethod]
        public void Keys_HandedOutVectorsHashABelCharacter()
        {
            // The modelKey/cube/edge vectors first handed to the S5 builders (0xf18ee13d1b3e0dec, 0xb59d1fc7857bd192,
 // 0xd5f1574eb7e10570) hash "models\cubes\large" + BEL + "rmorcube.mwm|lod0": a shell turned "\a" into 0x07.
            string bel = "models\\cubes\\large\armorcube.mwm|lod0";
            Assert.AreEqual(7, (int)bel[18]);
            ulong mk = LayoutHash.Fnv1a64(Encoding.UTF8.GetBytes(bel));
            Assert.AreEqual(0xf18ee13d1b3e0decUL, mk);
            Assert.AreEqual(0xb59d1fc7857bd192UL, MirrorConventions.CubePartKey(mk, 2.5f, 0f, -5f, 0f));
            Assert.AreEqual(0xd5f1574eb7e10570UL, MirrorConventions.EdgePartKey(mk, 2.5f, 0f, -5f, 0f));
        }

        [TestMethod]
        public void Keys_ModelKeyText()
        {
            Assert.AreEqual(ModelCodec.ModelKey("Models/Cubes/Large/ArmorCube.mwm", 0), MirrorConventions.ModelKey("Models/Cubes/Large/ArmorCube.mwm", 0), "S3 compatible");
            Assert.AreEqual(ModelCodec.ModelKey("a.mwm", 1, 0.2f), MirrorConventions.ModelKey("a.mwm", 1, 0.2f));
            Assert.AreEqual(@"models\cubes\large\armorcube.mwm|lod0|skin=clean_armor", MirrorConventions.ModelKeyText(Armor, 0, 1f, "Clean_Armor"));
            Assert.AreEqual(@"models\characters\astronaut\se_astronaut.mwm|lod0|char=body",
                MirrorConventions.ModelKeyText(@"Models\Characters\Astronaut\SE_astronaut.mwm", 0, 1f, null, MirrorCharacterPart.Body));
            Assert.AreEqual(@"models\characters\astronaut\se_astronaut.mwm|lod0|skin=neon|char=head1st",
                MirrorConventions.ModelKeyText(@"Models\Characters\Astronaut\SE_astronaut.mwm", 0, 1f, "Neon", MirrorCharacterPart.Head1st));
            Assert.AreEqual("a.mwm|lod0|scale=0.2|skin=x", MirrorConventions.ModelKeyText("a.mwm", 0, 0.2f, "X"));
            AssertThrows<ArgumentOutOfRangeException>(() => MirrorConventions.ModelKeyText("a.mwm", 0, 1f, null, MirrorCharacterPart.Tool));
        }

        [TestMethod]
        public void Keys_PartKeysHashTheLittleEndianBytes()
        {
            var rnd = new Random(55);
            for (int i = 0; i < 50; i++)
            {
                ulong mk = (ulong)rnd.Next() << 33 ^ (ulong)rnd.Next();
                float x = (float)(rnd.NextDouble() * 400 - 200), y = (float)(rnd.NextDouble() * 400 - 200), z = (float)(rnd.NextDouble() * 400 - 200);
                float w = rnd.Next(0, 64);
                var cube = new List<byte>();
                cube.AddRange(BitConverter.GetBytes(mk));
                foreach (float f in new[] { x, y, z, w }) cube.AddRange(BitConverter.GetBytes(f));
                Assert.AreEqual(LayoutHash.Fnv1a64(cube.ToArray()), MirrorConventions.CubePartKey(mk, x, y, z, w));
                cube.InsertRange(0, BitConverter.GetBytes(2u));
                Assert.AreEqual(LayoutHash.Fnv1a64(cube.ToArray()), MirrorConventions.EdgePartKey(mk, x, y, z, w));
                Assert.AreNotEqual(MirrorConventions.CubePartKey(mk, x, y, z, w), MirrorConventions.EdgePartKey(mk, x, y, z, w));
            }
            AssertThrows<ArgumentException>(() => MirrorConventions.SubpartKeyText(1, ""));
            Assert.AreEqual(1UL, MirrorConventions.FloatingPartKey);
            Assert.AreEqual(0x89abcdefu, MirrorConventions.BonesVersion(0x0123456789abcdefUL));
        }

        // ------------------------------------------------------------------------------------------------ records

        [TestMethod]
        public void CharacterRecords()
        {
            ulong body = MirrorConventions.ModelKey(@"Models\Characters\Astronaut\SE_astronaut.mwm", 0, 1f, null, MirrorCharacterPart.Body);
            CharacterAdd ca = MirrorConventions.MakeCharacterAdd(42, body, true);
            Assert.AreEqual(42UL, ca.entityId);
            Assert.AreEqual(body, ca.modelKey);
            Assert.AreEqual(MirrorCharacterFlags.Version1 | MirrorCharacterFlags.Player | MirrorCharacterFlags.Fallback, ca.flags);
            Assert.AreEqual(78u, ca.boneCount);
            var hsv = new Vec3(0.25, -0.5, 0.75);
            PartAdd b = MirrorConventions.MakeCharacterPart(42, MirrorCharacterPart.Body, body, hsv, 9);
            PartAdd h = MirrorConventions.MakeCharacterPart(42, MirrorCharacterPart.Head1st, body + 1, hsv, 9);
            Assert.AreEqual(1UL, b.partKey);
            Assert.AreEqual(2UL, h.partKey);
            Assert.AreEqual(MirrorPartFlags.Version1 | MirrorPartClass.CharacterBody | MirrorPartFlags.MetalnessColorable, b.flags);
            Assert.AreEqual(MirrorPartFlags.Version1 | MirrorPartClass.CharacterBody | MirrorPartFlags.MetalnessColorable | MirrorPartFlags.HiddenInFirstPerson, h.flags);
            Assert.AreEqual(9u, b.skinId);
            SeMatrix l = ModelCodec.PartLocal(b);
            Assert.AreEqual(1.0, l.Right.X); Assert.AreEqual(1.0, l.Up.Y); Assert.AreEqual(1.0, l.Backward.Z); Assert.AreEqual(0.0, l.Translation.Length);
            Assert.AreEqual(0.25f, b.colorMaskHSV.x);
            AssertThrows<ArgumentOutOfRangeException>(() => MirrorConventions.MakeCharacterPart(42, MirrorCharacterPart.Tool, body, hsv, 0));
            PartAdd t = MirrorConventions.MakeToolPart(42, 7, hsv);
            Assert.AreEqual(MirrorCharacterPart.Tool, t.partKey);
            Assert.AreEqual(PartDrawKind.OwnChild, ChunkMath.DrawKind(t.flags));
            PartAdd f = MirrorConventions.MakeFloatingObjectPart(5, 7, hsv);
            Assert.AreEqual(1UL, f.partKey);
            Assert.AreEqual(PartDrawKind.Floating, ChunkMath.DrawKind(f.flags));
            Assert.AreEqual(PartDrawKind.Skinned, ChunkMath.DrawKind(b.flags));
        }

        private static int PopCount(uint v) { int n = 0; while (v != 0) { n += (int)(v & 1); v >>= 1; } return n; }

        internal static void AssertThrows<T>(Action a) where T : Exception
        {
            try { a(); }
            catch (T) { return; }
            Assert.Fail("expected " + typeof(T).Name);
        }
    }

    /// <summary>Repository paths and the Python oracle for the S5 tests (stdlib Python 3.10, <c>python -I</c>).</summary>
    internal static class S5Repo
    {
        private static string _root;

        /// <summary>The folder holding DeepEngineers.sln, found upwards from the test assembly.</summary>
        public static string Root
        {
            get
            {
                if (_root != null) return _root;
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "DeepEngineers.sln"))) dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
                if (dir == null) throw new InvalidOperationException("DeepEngineers.sln not found above " + AppDomain.CurrentDomain.BaseDirectory);
                _root = dir;
                return dir;
            }
        }

        public static string SkeletonTsv { get { return Path.Combine(Root, "src", "DeepEngineers.Wire", "Data", "SeSkeleton.canon1.tsv"); } }

        /// <summary>The 7-column rows of the skeleton data file (comments skipped, header checked).</summary>
        public static List<string[]> SkeletonRows()
        {
            var rows = new List<string[]>();
            bool header = false;
            foreach (string raw in File.ReadAllText(SkeletonTsv, Encoding.UTF8).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                if (!header)
                {
                    Assert.AreEqual(MirrorSkeleton.TsvHeader, line, "data file header");
                    header = true;
                    continue;
                }
                string[] cols = line.Split('\t');
                Assert.AreEqual(7, cols.Length, "7 columns: " + line);
                rows.Add(cols);
            }
            return rows;
        }

        /// <summary>DEEPENG_PYTHON, else %LOCALAPPDATA%\Programs\Python\Python310\python.exe, else "python".</summary>
        public static string Python
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("DEEPENG_PYTHON");
                if (!string.IsNullOrEmpty(env)) return env;
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (!string.IsNullOrEmpty(local))
                {
                    string p = Path.Combine(local, "Programs", "Python", "Python310", "python.exe");
                    if (File.Exists(p)) return p;
                }
                return "python";
            }
        }

        /// <summary>Runs <c>python -I args</c> in the repo root and returns stdout (UTF-8); throws on a non-zero exit.</summary>
        public static string RunPython(params string[] args)
        {
            var sb = new StringBuilder("-I");
            foreach (string a in args) sb.Append(' ').Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
            var psi = new System.Diagnostics.ProcessStartInfo(Python, sb.ToString())
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(300000)) { try { p.Kill(); } catch (InvalidOperationException) { } throw new TimeoutException("python " + sb); }
                if (p.ExitCode != 0) throw new InvalidOperationException("python " + sb + " exited " + p.ExitCode + "\n" + err.Result);
                return stdout;
            }
        }
    }
}
