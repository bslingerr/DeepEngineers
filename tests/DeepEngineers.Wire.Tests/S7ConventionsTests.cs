using System;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7 conventions pin (CONTRACT §5.4 "S7 conventions"): the canonical text byte for byte, its hash
 /// 0x1f9b9b4a876a97f9, 143 lines and 6,583 bytes, every value equal to the code's constants, every new bit outside the
 /// layout's, S5's, S6's and S7a's constant groups, the interior bytes inside <c>pad_94</c> after the loco extension, the
 /// C2 extension inside <c>pad_1c</c>, the event rows inside the C2 region past <c>SeEntitiesLimits.End</c>, the light
 /// falloff on <c>SeLight.pad_3c</c>, and layout 1.0 and the earlier extension pins unchanged.
 /// </summary>
    [TestClass]
    public class S7ConventionsTests
    {
        /// <summary>CONTRACT §5.4 "S7 conventions", the canonical text block, copied line for line.</summary>
        private static readonly string[] ContractText =
        {
            "ext s7 look 1",
            "const BoneLimits AstronautBones 81",
            "const BoneLimits MaxBones 160",
            "const C12Ext Version 1",
            "const C1Interior Version 1",
            "const C1Interior Size 16",
            "const C1Interior Offset 176",
            "const C1InteriorFlags InRoom 1",
            "const C1InteriorFlags Pressurization 2",
            "const C1InteriorFlags Processing 4",
            "const C1InteriorFlags Breached 8",
            "const C1InteriorFlags CharacterInRoom 16",
            "const C2Events WindowTicks 8",
            "const C2Events RowSize 64",
            "const C2Events MaxPerTick 128",
            "const C2Events MaxRows 1024",
            "const C2Events Offset 262144",
            "const C2Ext Version 1",
            "const C2Ext Offset 28",
            "const C2Ext Size 36",
            "const C2ExtFlags Lights 1",
            "const C2ExtFlags Events 2",
            "const C2LightFlagsExt Character 8",
            "const C2LightFlagsExt Version1 268435456",
            "const C2LightFlagsExt VersionMask 4026531840",
            "const CharacterFlagsExt ExtraBones 4",
            "const EventCaps Drill 8",
            "const EventCaps Explosion 8",
            "const EventCaps Grinder 8",
            "const EventCaps HullBreak 8",
            "const EventCaps PrawnDrill 8",
            "const EventCaps Repair 8",
            "const EventCaps Welder 8",
            "const EventCaps GridImpact 16",
            "const EventCaps Splash 16",
            "const EventCaps WeaponMuzzle 16",
            "const EventCaps BulletStreak 32",
            "const EventCaps Impact 32",
            "const EventCaps Thruster 64",
            "const EventFlags Attached 1",
            "const EventFlags Sustain 2",
            "const EventFlags Stop 4",
            "const EventFlags Underwater 8",
            "const EventFlags Surface 16",
            "const EventFlags Hit 32",
            "const EventFlags Local 64",
            "const EventLimits SustainRefreshTicks 15",
            "const EventLimits SurfaceBandCm 50",
            "const EventLimits SustainStepPermille 50",
            "const EventLimits StreakMaxM 60",
            "const EventLimits SplashMinSpeedCmS 200",
            "const EventLimits EffectRadiusM 250",
            "const EventLimits SustainLifetimeMs 500",
            "const EventLimits ExplosionRadiusM 1000",
            "const EventType Thruster 1",
            "const EventType WeaponMuzzle 2",
            "const EventType BulletStreak 3",
            "const EventType Impact 4",
            "const EventType Explosion 5",
            "const EventType Welder 6",
            "const EventType Grinder 7",
            "const EventType Drill 8",
            "const EventType Repair 9",
            "const EventType PrawnDrill 10",
            "const EventType GridImpact 11",
            "const EventType HullBreak 12",
            "const EventType Splash 13",
            "const InteriorClass Exterior 0",
            "const InteriorClass Boundary 1",
            "const InteriorClass Inside 2",
            "const LightLimits MaxShadowedSpots 2",
            "const LightLimits RadiusM 60",
            "const LightLimits MaxLights 64",
            "const RoomLimits MaxBoxes 4096",
            "const RoomSetFlags Truncated 1",
            "const RoomSetFlags Version1 268435456",
            "const StaticFlags Census 1",
            "const StaticFlags AllowListed 2",
            "const StaticFlags SkewApprox 4",
            "const StaticsLimits BatchM 160",
            "const StaticsLimits CreateRadiusM 300",
            "const StaticsLimits ScanRadiusM 320",
            "const StaticsLimits CloseRadiusM 350",
            "const StaticsLimits MaxPerHost 1024",
            "const StaticsLimits MinRescanMs 2000",
            "const StaticsLimits MaxPerBatch 4096",
            "const ThrusterKind Other 0",
            "const ThrusterKind Ion 1",
            "const ThrusterKind Atmospheric 2",
            "const ThrusterKind Hydrogen 3",
            "const ToolSource Hand 1",
            "const ToolSource Ship 2",
            "const WeaponKind Projectile 1",
            "const WeaponKind Missile 2",
            "field interior version 0xb0 1 u8",
            "field interior flags 0xb1 1 u8",
            "field interior roomO2 0xb2 1 u8",
            "field interior pad 0xb3 1 u8",
            "field interior roomIndex 0xb4 4 u32",
            "field interior roomGridId 0xb8 8 u64",
            "field c2ext version 0x1c 1 u8",
            "field c2ext flags 0x1d 1 u8",
            "field c2ext eventCount 0x1e 2 u16",
            "field c2ext eventFirstSeq 0x20 4 u32",
            "field c2ext eventStream 0x24 4 u32",
            "field c2ext eventsDropped 0x28 4 u32",
            "field c2ext lightsFound 0x2c 4 u32",
            "field c2ext tickLow 0x30 4 u32",
            "field c2ext pad 0x34 12 u8[12]",
            "field light falloff 0x3c 4 f32",
            "field event seq 0x0 4 u32",
            "field event type 0x4 1 u8",
            "field event flags 0x5 1 u8",
            "field event lifetimeMs 0x6 2 u16",
            "field event entityId 0x8 8 u64",
            "field event key 0x10 8 u64",
            "field event pos 0x18 12 f32[3]",
            "field event dir 0x24 12 f32[3]",
            "field event intensity 0x30 4 f32",
            "field event size 0x34 4 f32",
            "field event tickLow 0x38 4 u32",
            "field event subtype 0x3c 2 u16",
            "field event pad 0x3e 2 u16",
            "field statics version 0x6 2 u16",
            "field prim flags 0x1 1 u8",
            "field roomset flags 0xc 4 u32",
            "format audio.bus Subnautica event sounds only on Underwater rows while snBusTarget > 0; rows without Underwater draw lights only",
            "format audio.muffle postfix MyEntity3DSoundEmitter.SelectEffect: LowPassNoHelmetNoOxy while seMixActive and C1 interior InRoom clear, sounds ModifiableByHelmetFilters only",
            "format bones.order rows 0..77 canon1; rows 78..boneCount-1 the model's non-canonical bones in Model.Bones order; boneCount = Model.Bones.Length <= 160",
            "format bones.unity Mirror * M^T * Mirror; extra rows posed flat under the Entity from their absolute matrices",
            "format event.frame Attached: pos, dir in the SE-local frame of entityId; else SN-world (F, mapdir)",
            "format event.key fnv1a64(utf8 thr|<fatKey x16>|<flame index>) | fnv1a64(utf8 tool|<fatKey x16>) | fnv1a64(utf8 tool|e<entity id>); one-shots 0",
            "format event.priority over MaxPerTick: Stop rows, Explosion, HullBreak, GridImpact, Splash, Impact, WeaponMuzzle, Repair, Welder, Grinder, Drill, PrawnDrill, Thruster, BulletStreak; newest first within a type",
            "format event.sustain a row on start, on |d intensity| >= SustainStepPermille/1000, on an attached move > 0.05 m, else every SustainRefreshTicks; lifetimeMs = SustainLifetimeMs; Stop row at the end; Subnautica fades a sustain whose lifetime ran out",
            "format event.window rows = accepted events of ticks [tick-7, tick], seq consecutive from eventFirstSeq; eventStream changes when seq restarts; tickLow = low 32 bits of tick",
            "format interior.class Inside: cell round(T/g) inside a room box; Boundary: Chebyshev distance 1 from a room box; else Exterior",
            "format interior.flag GetOxygenRoomForCubeGridPosition(cell of the camera) else GetSafeOxygenBlock(camera).Room; IsAirtight; last value held while IsProcessingData",
            "format interior.sky Inside: SetCustomSky(MarmoSkies.main.GetSky(BaseInterior)); Boundary: the same while C1 roomGridId = its entity, else SetSky(Auto) with dynamic; Exterior: SetSky(Auto)",
            "format light.row ownerId frame as event.frame; a point row per LightOn, a Spot row per ReflectorOn; nearest first within RadiusM; cap MaxLights; Shadows on spot rows only",
            "format roomset.box cells [min, max] of airtight room air cells, MyVoxelSegmentation Simple; Truncated: one box per room (its cell AABB)",
            "format statics.axes staticAxes(q) = (mapdir^-1(q*x), mapdir^-1(q*y), mapdir^-1(q*(-z))), det +1; size_i *= sqrt((a_i.x^2+a_i.z^2)/s^2+a_i.y^2)",
            "format statics.capsule size = (radius, halfSegment, axis 0|1|2); halfSegment = max(0, height/2 - radius); HkCapsuleShape(F^-1(c - a*halfSegment) - o, F^-1(c + a*halfSegment) - o, radius/s(c)); sphere radius/s(c)",
            "format statics.host DE.Statics.<bx>.<by>.<bz>.e<epoch>[.p<k>]: MyEntity, no model, Save false, layer 13, HkStaticCompoundShape baked, origin F^-1(mean centre), identity rotation",
        };

        [TestMethod]
        public void CanonicalText_IsTheContractTextByteForByte()
        {
            string expected = string.Join("\n", ContractText) + "\n";
            string text = S7Conventions.CanonicalText();
            Assert.AreEqual(expected, text, "S7Conventions.CanonicalText drifted from CONTRACT §5.4");
            Assert.AreEqual(143, S7Conventions.CanonicalLines);
            Assert.AreEqual(6583, S7Conventions.CanonicalBytes);
            Assert.AreEqual(S7Conventions.CanonicalLines, text.Split('\n').Length - 1, "lines");
            Assert.AreEqual(S7Conventions.CanonicalBytes, Encoding.ASCII.GetByteCount(text), "bytes");
            foreach (char c in text) Assert.IsTrue(c == '\n' || (c >= 0x20 && c < 0x7F), "ASCII only");
            Assert.AreEqual("ext s7 look " + S7Conventions.ExtensionVersion, S7Conventions.Header);
        }

        [TestMethod]
        public void CanonicalText_MatchesTheContractDocumentWhenPresent()
        {
            // the fenced block in docs/CONTRACT.md (found by its first line), when the repository is above the test output
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string contract = null;
            for (int up = 0; up < 8 && !string.IsNullOrEmpty(dir); up++)
            {
                string candidate = System.IO.Path.Combine(dir, "docs", "CONTRACT.md");
                if (System.IO.File.Exists(candidate)) { contract = candidate; break; }
                dir = System.IO.Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            if (contract == null) Assert.Inconclusive("docs/CONTRACT.md not found above " + AppDomain.CurrentDomain.BaseDirectory);
            string doc = System.IO.File.ReadAllText(contract, Encoding.UTF8).Replace("\r\n", "\n");
            int i = doc.IndexOf("```\next s7 look 1\n", StringComparison.Ordinal);
            Assert.IsTrue(i >= 0, "the S7 canonical block is in CONTRACT.md");
            i += 4;
            int j = doc.IndexOf("```", i, StringComparison.Ordinal);
            Assert.AreEqual(S7Conventions.CanonicalText(), doc.Substring(i, j - i), "CONTRACT.md block = the code's text");
        }

        [TestMethod]
        public void Hash_IsPinned()
        {
            Assert.AreEqual(0x1f9b9b4a876a97f9UL, S7Conventions.PinnedHash);
            Assert.AreEqual(S7Conventions.PinnedHash, S7Conventions.Hash,
                "S7 conventions hash changed: bump 'ext s7 look <n>', update both pins and MODLOG");
            Assert.AreEqual(S7Conventions.PinnedHash, LayoutHash.Fnv1a64Ascii(string.Join("\n", ContractText) + "\n"));
        }

        [TestMethod]
        public void Values_AreTheContractValues()
        {
            Assert.AreEqual(81, S7Conventions.BoneLimits.AstronautBones);
            Assert.AreEqual(SeFrameLimits.MaxBones, S7Conventions.BoneLimits.MaxBones, "C1 bones");
            Assert.AreEqual(0xB0, S7Conventions.C1Interior.Offset);
            Assert.AreEqual(16, S7Conventions.C1Interior.Size);
            Assert.AreEqual(0x40000, S7Conventions.C2Events.Offset);
            Assert.AreEqual(64, S7Conventions.C2Events.RowSize);
            Assert.AreEqual(1024, S7Conventions.C2Events.MaxRows);
            Assert.AreEqual(0x1C, S7Conventions.C2Ext.Offset);
            Assert.AreEqual(36, S7Conventions.C2Ext.Size);
            Assert.AreEqual(0x10000000u, S7Conventions.C2LightFlagsExt.Version1);
            Assert.AreEqual(0xF0000000u, S7Conventions.C2LightFlagsExt.VersionMask);
            Assert.AreEqual(0x10000000u, S7Conventions.RoomSetFlags.Version1);
            Assert.AreEqual(SeEntitiesLimits.MaxLights, S7Conventions.LightLimits.MaxLights, "the light table holds every row");
            int f = 0;
            foreach (byte b in new[] { S7Conventions.C1InteriorFlags.InRoom, S7Conventions.C1InteriorFlags.Pressurization, S7Conventions.C1InteriorFlags.Processing,
                                       S7Conventions.C1InteriorFlags.Breached, S7Conventions.C1InteriorFlags.CharacterInRoom })
            {
                Assert.AreEqual(0, b & (b - 1), "single bit");
                Assert.AreEqual(0, f & b, "distinct");
                f |= b;
            }
            int ef = 0;
            foreach (byte b in new[] { S7Conventions.EventFlags.Attached, S7Conventions.EventFlags.Sustain, S7Conventions.EventFlags.Stop, S7Conventions.EventFlags.Underwater,
                                       S7Conventions.EventFlags.Surface, S7Conventions.EventFlags.Hit, S7Conventions.EventFlags.Local })
            {
                Assert.AreEqual(0, b & (b - 1), "single bit");
                Assert.AreEqual(0, ef & b, "distinct");
                ef |= b;
            }
            for (int t = 1; t <= 13; t++)
            {
                Assert.IsTrue(S7Conventions.EventType.IsValid(t));
                Assert.IsTrue(S7Conventions.EventCaps.Of(t) > 0, "a cap for " + S7Conventions.EventType.Name(t));
                Assert.IsFalse(S7Conventions.EventType.Name(t).StartsWith("Unknown", StringComparison.Ordinal));
            }
            Assert.AreEqual(0, S7Conventions.EventCaps.Of(0));
            Assert.AreEqual(0, S7Conventions.EventCaps.Of(14));
            Assert.AreEqual(64, S7Conventions.EventCaps.Of(S7Conventions.EventType.Thruster));
            Assert.AreEqual("inside", S7Conventions.InteriorClass.Name(S7Conventions.InteriorClass.Inside));
        }

        [TestMethod]
        public void NewBits_LieOutsideTheLayoutAndEarlierGroups()
        {
            uint layoutLight = SeLightFlags.Spot | SeLightFlags.Shadows | SeLightFlags.On;
            Assert.AreEqual(0u, S7Conventions.C2LightFlagsExt.Character & layoutLight, "C2 light bit 3 free");
            Assert.AreEqual(0u, S7Conventions.C2LightFlagsExt.Character & S7Conventions.C2LightFlagsExt.VersionMask);
            Assert.AreEqual(0u, layoutLight & S7Conventions.C2LightFlagsExt.VersionMask, "the nibble is free of the layout bits");
            uint ch = MirrorCharacterFlags.Player | MirrorCharacterFlags.Fallback;
            Assert.AreEqual(0u, S7Conventions.CharacterFlagsExt.ExtraBones & ch, "CharacterAdd bit 2 free");
            Assert.AreEqual(0u, S7Conventions.CharacterFlagsExt.ExtraBones & 0xF0000000u, "outside the version nibble");
            Assert.AreEqual(0u, S7Conventions.RoomSetFlags.Truncated & 0xF0000000u);
            foreach (Type t in Layout.ConstantGroups)
                Assert.IsFalse(t.FullName.Contains("S7Conventions"), "no S7 group joins the layout hash: " + t.Name);
            Assert.AreEqual(LayoutHash.PinnedLayout10, LayoutHash.Compiled, "layout 1.0 unchanged (no re-pin)");
        }

        [TestMethod]
        public void InteriorBytes_InsidePad94AfterTheLocoExtension()
        {
            RecordDef frame = Layout.Records.First(r => r.Name == "SeFrame");
            FieldDef pad = frame.Fields.First(fd => fd.Offset == SwimWire.PadOffset);
            Assert.IsTrue(pad.IsPad, "0x94 of SeFrame is a pad in layout 1.0");
            Assert.AreEqual(S7aConventions.C1Loco.Offset + S7aConventions.C1Loco.Size, S7Conventions.C1Interior.Offset, "right after the loco extension");
            Assert.IsTrue(S7Conventions.C1Interior.Offset + S7Conventions.C1Interior.Size <= pad.Offset + pad.Size, "inside pad_94");
            string[] fields = S7Conventions.FieldLines();
            Assert.AreEqual(32, fields.Length);
            Assert.AreEqual("interior version 0xb0 1 u8", fields[0]);
            Assert.AreEqual("interior roomGridId 0xb8 8 u64", fields[5]);
        }

        [TestMethod]
        public void C2Extension_InsidePad1c_EventsPastTheTables_FalloffOnPad3c()
        {
            RecordDef hdr = Layout.Records.First(r => r.Name == "SeEntitiesHeader");
            FieldDef p1c = hdr.Fields.First(fd => fd.Offset == S7Conventions.C2Ext.Offset);
            Assert.IsTrue(p1c.IsPad, "SeEntitiesHeader 0x1C is the pad");
            Assert.AreEqual(S7Conventions.C2Ext.Size, p1c.Size, "the extension fills pad_1c");
            Assert.IsTrue(S7Conventions.C2Events.Offset >= SeEntitiesLimits.End, "event rows after the light table");
            Assert.IsTrue(S7Conventions.C2Events.Offset + S7Conventions.C2Events.MaxRows * S7Conventions.C2Events.RowSize <= 0x80000, "inside the 512 KiB C2 region");
            RecordDef light = Layout.Records.First(r => r.Name == "SeLight");
            FieldDef p3c = light.Fields.First(fd => fd.Offset == S7LightRow.OffFalloff);
            Assert.IsTrue(p3c.IsPad && p3c.Size == 4, "SeLight 0x3C is the 4-byte pad");
        }

        [TestMethod]
        public void EarlierExtensionPins_Unchanged()
        {
            Assert.AreEqual(0x832ba93ca25bc307UL, SwimWire.Hash, "S5a swim extension");
            Assert.AreEqual(0x31dfd6f9befdef5dUL, MirrorConventions.Hash, "S5 mirror conventions");
            Assert.AreEqual(0xb6ee880712f8253bUL, CombatConventions.Hash(), "S6 combat conventions");
            Assert.AreEqual(S7aConventions.PinnedHash, S7aConventions.Hash, "S7a conventions (pinned by S7aConventionsTests; the orchestrator moved it to ext s7a picture 3 during S7)");
        }
    }
}
