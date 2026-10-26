using System;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7a conventions pin (CONTRACT §5.4 "S7a conventions"): the canonical text byte for byte, its hash
 /// 0x63d59c04766f4e03, line and byte counts, every value equal to the code's constants, every new bit outside the
 /// layout's constant groups and S5's <see cref="MirrorGridFlags"/>, the loco bytes inside the C1 pad after the S5a swim
 /// extension, the sky bytes on <c>SeTime.pad_3c</c>, layout 1.0 and the earlier extension pins unchanged, and the two
 /// shared rules (<see cref="S7aConventions.MirrorRadius"/>, <see cref="S7aConventions.AreaBoxCentreY"/>).
 /// </summary>
    [TestClass]
    public class S7aConventionsTests
    {
        /// <summary>CONTRACT §5.4 "S7a conventions", the canonical text block, copied line for line.</summary>
        private static readonly string[] ContractText =
        {
            "ext s7a picture 3",
            "const C1Loco Version 1",
            "const C1Loco Size 16",
            "const C1Loco Offset 160",
            "const C1LocoFlags Grounded 1",
            "const C1LocoFlags Crouching 2",
            "const C1LocoFlags Jetpack 4",
            "const C1LocoFlags InWater 8",
            "const C1LocoFlags Seated 16",
            "const C1LocoFlags Dead 32",
            "const C1LocoFlags Ladder 64",
            "const C1LocoFlags ToolHeld 128",
            "const C1LocoState None 0",
            "const C1LocoState Idle 1",
            "const C1LocoState Walk 2",
            "const C1LocoState Run 3",
            "const C1LocoState Sprint 4",
            "const C1LocoState CrouchIdle 5",
            "const C1LocoState CrouchWalk 6",
            "const C1LocoState Jump 7",
            "const C1LocoState Fall 8",
            "const C1LocoState Fly 9",
            "const C1LocoState Ladder 10",
            "const C1LocoState Seated 11",
            "const C1LocoState Dead 12",
            "const C1LocoState Swim 13",
            "const C2FlagsExt GhostShown 4",
            "const C2FlagsExt GhostAllowed 8",
            "const C5FlagsExt PictureSite 512",
            "const C6FlagsExt SkyPoleValid 4",
            "const C6Sky Size 4",
            "const C6Sky Offset 60",
            "const Ghost PartKey 1",
            "const Ghost MaxParts 32",
            "const Ghost EntityId 91274063699247105",
            "const GridFlagsExt Ghost 8",
            "const Limits SunHoldMaxCdeg 200",
            "const Limits PictureDwellMs 250",
            "const Limits FallAnimMs 450",
            "const Limits GroundAnimScalePermille 1000",
            "const Limits MirrorEnterAboveWaterM 1500",
            "const Limits MirrorLeaveAboveWaterM 1600",
            "const ViewPicture Site 0",
            "const ViewPicture Waterline 1",
            "field loco version 0xa0 1 u8",
            "field loco state 0xa1 1 u8",
            "field loco flags 0xa2 1 u8",
            "field loco pad 0xa3 1 u8",
            "field loco vForwardCm 0xa4 2 i16",
            "field loco vStrafeCm 0xa6 2 i16",
            "field loco vUpCm 0xa8 2 i16",
            "field loco yawRateCdeg 0xaa 2 i16",
            "field loco seMovement 0xac 2 u16",
            "field loco pad2 0xae 2 u16",
            "field sky poleAzCdeg 0x3c 2 i16",
            "field sky poleElCdeg 0x3e 2 i16",
            "format ghost C2 kind 1 pose = gizmo SpaceDefault.m_worldMatrixAdd; PartAdd key 1 class 3 NoShadows local = translation(ModelOffset) model = MyCubeBlockDefinition.Model|lod0",
            "format pole q = M_sn x W_sn, M = normalize(U-axis*(axis.U)), W = normalize(axis x U); az = atan2(q.x,q.z), el = asin(q.y), centi-degrees rounded half away from zero",
            "format sky c = frac(H/2pi+0.5); n = normalize(up-q*(up.q)); r = -(q x n); SunDirection = atan2(-r.x,-r.z); NorthPoleOffset = atan2(-n.x*cos(SunDirection)+n.z*sin(SunDirection),n.y)",
            "format time DayNightCycle paused; timePassedAsDouble += frac(sTarget-sNow)*1200 unless behind by <= SunHoldMaxCdeg of cycle angle (hold)",
        };

        [TestMethod]
        public void CanonicalText_IsTheContractTextByteForByte()
        {
            string expected = string.Join("\n", ContractText) + "\n";
            string text = S7aConventions.CanonicalText();
            Assert.AreEqual(expected, text, "S7aConventions.CanonicalText drifted from CONTRACT §5.4");
            Assert.AreEqual(60, S7aConventions.CanonicalLines);
            Assert.AreEqual(2250, S7aConventions.CanonicalBytes);
            Assert.AreEqual(S7aConventions.CanonicalLines, text.Split('\n').Length - 1, "lines");
            Assert.AreEqual(S7aConventions.CanonicalBytes, Encoding.ASCII.GetByteCount(text), "bytes");
            foreach (char c in text) Assert.IsTrue(c == '\n' || (c >= 0x20 && c < 0x7F), "ASCII only");
            Assert.AreEqual(S7aConventions.Header + "\n", text.Substring(0, S7aConventions.Header.Length + 1));
            Assert.AreEqual("ext s7a picture " + S7aConventions.ExtensionVersion, S7aConventions.Header);
        }

        [TestMethod]
        public void Hash_IsPinned()
        {
            Assert.AreEqual(0x4257c11c17ff772aUL, S7aConventions.PinnedHash);
            Assert.AreEqual(S7aConventions.PinnedHash, S7aConventions.Hash,
                "S7a conventions hash changed: bump 'ext s7a picture <n>', update both pins and MODLOG");
            Assert.AreEqual(S7aConventions.PinnedHash, LayoutHash.Fnv1a64Ascii(string.Join("\n", ContractText) + "\n"));
        }

        [TestMethod]
        public void Values_AreTheContractValues()
        {
            Assert.AreEqual(1, S7aConventions.C1Loco.Version);
            Assert.AreEqual(0xA0, S7aConventions.C1Loco.Offset);
            Assert.AreEqual(16, S7aConventions.C1Loco.Size);
            Assert.AreEqual(4u, S7aConventions.C2FlagsExt.GhostShown);
            Assert.AreEqual(8u, S7aConventions.C2FlagsExt.GhostAllowed);
            Assert.AreEqual(512u, S7aConventions.C5FlagsExt.PictureSite);
            Assert.AreEqual(4u, S7aConventions.C6FlagsExt.SkyPoleValid);
            Assert.AreEqual(0x3C, S7aConventions.C6Sky.Offset);
            Assert.AreEqual(4, S7aConventions.C6Sky.Size);
            Assert.AreEqual((1UL << 56) | 0x44454700000001UL, S7aConventions.Ghost.EntityId, "ConstructId(ENTITY = 1, 0x44454700000001), 'DEG'");
            Assert.AreEqual(1UL, S7aConventions.Ghost.PartKey);
            Assert.AreEqual(32, S7aConventions.Ghost.MaxParts);
            Assert.AreEqual(8u, S7aConventions.GridFlagsExt.Ghost);
            Assert.AreEqual(250, S7aConventions.Limits.PictureDwellMs);
            Assert.AreEqual(ViewThresholds.PictureDwellUs, S7aConventions.Limits.PictureDwellMs * 1000L, "the machine's dwell is the pinned one");
            Assert.AreEqual(1500, S7aConventions.Limits.MirrorEnterAboveWaterM);
            Assert.AreEqual(1600, S7aConventions.Limits.MirrorLeaveAboveWaterM);
            Assert.AreEqual(0u, ViewPicture.Site);
            Assert.AreEqual(1u, ViewPicture.Waterline);
            Assert.AreEqual("site", ViewPicture.Name(ViewPicture.Site));
            Assert.AreEqual("waterline", ViewPicture.Name(ViewPicture.Waterline));
            byte[] states = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
            string[] names = { "None", "Idle", "Walk", "Run", "Sprint", "CrouchIdle", "CrouchWalk", "Jump", "Fall", "Fly", "Ladder", "Seated", "Dead", "Swim" };
            for (int k = 0; k < states.Length; k++) Assert.AreEqual(names[k], S7aConventions.C1LocoState.Name(states[k]));
            byte[] foot = { S7aConventions.C1LocoState.Idle, S7aConventions.C1LocoState.Walk, S7aConventions.C1LocoState.Run,
                            S7aConventions.C1LocoState.Sprint, S7aConventions.C1LocoState.Jump, S7aConventions.C1LocoState.Fall };
            foreach (byte s in states) Assert.AreEqual(foot.Contains(s), S7aConventions.C1LocoState.IsFoot(s), "IsFoot " + s);
            Assert.IsFalse(S7aConventions.C1LocoState.IsFoot(200));
            byte flags = 0;
            foreach (byte f in new[] { S7aConventions.C1LocoFlags.Grounded, S7aConventions.C1LocoFlags.Crouching, S7aConventions.C1LocoFlags.Jetpack,
                                       S7aConventions.C1LocoFlags.InWater, S7aConventions.C1LocoFlags.Seated, S7aConventions.C1LocoFlags.Dead,
                                       S7aConventions.C1LocoFlags.Ladder, S7aConventions.C1LocoFlags.ToolHeld })
            {
                Assert.AreEqual(0, f & (f - 1), "single bit");
                Assert.AreEqual(0, flags & f, "distinct");
                flags |= f;
            }
        }

        [TestMethod]
        public void NewBits_LieOutsideTheLayoutAndS5Groups()
        {
            uint entity = SeEntityFlags.Static | SeEntityFlags.InCameraGroup;
            Assert.AreEqual(0u, (S7aConventions.C2FlagsExt.GhostShown | S7aConventions.C2FlagsExt.GhostAllowed) & entity, "C2 bits 2-3 free");
            uint state = 0;
            foreach (System.Reflection.FieldInfo f in typeof(SeStateFlags).GetFields())
                if (f.IsLiteral) state |= Convert.ToUInt32(f.GetRawConstantValue());
            Assert.AreEqual(0x1FFu, state, "SeStateFlags is bits 0-8");
            Assert.AreEqual(0u, S7aConventions.C5FlagsExt.PictureSite & state, "C5 bit 9 free");
            Assert.AreEqual(0u, S7aConventions.C6FlagsExt.SkyPoleValid & (SeTimeFlags.TimeSyncValid | SeTimeFlags.SkyOnly), "C6 bit 2 free");
            uint grid = MirrorGridFlags.Static | MirrorGridFlags.Subgrid | MirrorGridFlags.FloatingObject;
            Assert.AreEqual(0u, S7aConventions.GridFlagsExt.Ghost & grid, "GridAdd bit 3 free");
            Assert.AreEqual(0u, S7aConventions.GridFlagsExt.Ghost & MirrorPartFlags.VersionMask, "outside the version nibble");
            foreach (Type t in Layout.ConstantGroups)
                Assert.IsFalse(t.Name.StartsWith("S7a", StringComparison.Ordinal) || t == typeof(ViewPicture), "no S7a group joins the layout hash: " + t.Name);
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled, "layout 1.0 unchanged (no re-pin)");
        }

        [TestMethod]
        public void LocoBytes_InsideThePad94AfterTheSwimExtension_SkyBytesOnPad3c()
        {
            RecordDef frame = Layout.Records.First(r => r.Name == "SeFrame");
            FieldDef pad = frame.Fields.First(f => f.Offset == SwimWire.PadOffset);
            Assert.IsTrue(pad.IsPad, "0x94 of SeFrame is a pad in layout 1.0");
            Assert.AreEqual(SwimWire.Offset + SwimWire.Size, S7aConventions.C1Loco.Offset, "the loco bytes start right after the swim extension (0xA0)");
            Assert.IsTrue(S7aConventions.C1Loco.Offset + S7aConventions.C1Loco.Size <= pad.Offset + pad.Size, "inside pad_94");
            RecordDef time = Layout.Records.First(r => r.Name == "SeTime");
            FieldDef p3c = time.Fields.First(f => f.Offset == S7aConventions.C6Sky.Offset);
            Assert.IsTrue(p3c.IsPad, "SeTime 0x3C is the pad");
            Assert.AreEqual(S7aConventions.C6Sky.Size, p3c.Size);
            string[] fields = S7aConventions.FieldLines();
            Assert.AreEqual(12, fields.Length);
            Assert.AreEqual("loco version 0xa0 1 u8", fields[0]);
            Assert.AreEqual("loco pad2 0xae 2 u16", fields[9]);
            Assert.AreEqual("sky poleElCdeg 0x3e 2 i16", fields[11]);
        }

        [TestMethod]
        public void EarlierExtensionPins_Unchanged()
        {
            Assert.AreEqual(0x832ba93ca25bc307UL, SwimWire.Hash, "S5a swim extension");
            Assert.AreEqual(0x31dfd6f9befdef5dUL, MirrorConventions.Hash, "S5 mirror conventions");
            Assert.AreEqual(0xb6ee880712f8253bUL, CombatConventions.Hash(), "S6 combat conventions");
        }

        [TestMethod]
        public void MirrorRadius_AndAreaBox_UnderTheTwoPolicies()
        {
            double e, l;
            S7aConventions.MirrorRadius(true, 300.0, out e, out l);
            Assert.AreEqual(1500.0, e); Assert.AreEqual(1600.0, l);
            S7aConventions.MirrorRadius(true, 0.0, out e, out l);
            Assert.AreEqual(MirrorBudgets.EnterRadiusMetres, e, "at the waterline: S5's enter radius"); Assert.AreEqual(1600.0, l, "F10: the leave radius is kept under Site");
            S7aConventions.MirrorRadius(true, -5.0, out e, out l);
            Assert.AreEqual(250.0, e); Assert.AreEqual(1600.0, l, "F10: no pop-in after a dive");
            S7aConventions.MirrorRadius(false, 300.0, out e, out l);
            Assert.AreEqual(250.0, e, "Waterline: S5's radius above water too"); Assert.AreEqual(300.0, l);
            S7aConventions.MirrorRadius(true, double.NaN, out e, out l);
            Assert.AreEqual(250.0, e, "NaN: not above the sea");
            Assert.AreEqual(140.0, S7aConventions.AreaBoxCentreY(true, 300.0));
            Assert.AreEqual(60.0, S7aConventions.AreaBoxCentreY(true, 60.0));
            Assert.AreEqual(-20.0, S7aConventions.AreaBoxCentreY(true, -20.0));
            Assert.AreEqual(300.0, S7aConventions.AreaBoxCentreY(false, 300.0), "Waterline: the camera's own box");
        }
    }
}
