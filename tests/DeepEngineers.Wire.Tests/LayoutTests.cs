using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S0.N1 (C# side): every region and record against numbers typed by hand from docs/CONTRACT.md §5.2/§5.4 (never
 /// derived from the structs or from Layout's tables), the layout-hash grammar and its pinned value, and the
 /// canonical text fixture shared with the Python oracle.
 /// </summary>
    [TestClass]
    public class LayoutTests
    {
        /// <summary>Layout 1.0 hash, pinned in docs/CONTRACT.md §6.</summary>
        private const ulong PinnedHash = 0x4f3a35f6fc1eaf31UL;

        public TestContext TestContext { get; set; }

        // CONTRACT §5.2 region map. Columns: id kind offset size recordSize version name.
 // Offsets and sizes are CONTRACT's; ids, kinds, record sizes and versions are layout 1.0's (Layout.cs header).
        private const string RegionTable = @"
0x00 1 0x0000000 0x1000    256 1 C0 Header
0x01 2 0x0001000 0x1000    128 1 Ring control
0x10 3 0x0010000 0x2000    256 1 C1 SeFrame
0x20 4 0x0020000 0x80000   96  1 C2 SeEntities
0x30 7 0x1000000 0x4000000 8   1 C3 SeModels
0x40 7 0x0600000 0x800000  8   1 C4 SeParts
0x50 3 0x0002000 0x1000    256 1 C5 SeState
0x60 3 0x0003000 0x1000    64  1 C6 SeTime
0x70 3 0x0004000 0x1000    256 1 C7 SnStatus
0x80 6 0x00B0000 0x20000   128 1 C8 SnDamage
0x90 4 0x00A0000 0x4000    48  1 C9 SnCreatures
0xA0 5 0x0005000 0x1000    256 1 C10 Capture
0xB0 7 0x0100000 0x40000   8   1 C11a CtlToSe
0xB1 7 0x0140000 0x40000   8   1 C11b CtlFromSe
0xB2 7 0x0180000 0x40000   8   1 C11c CtlToSn
0xB3 7 0x01C0000 0x40000   8   1 C11d CtlFromSn
0xC0 7 0x0200000 0x400000  8   1 C12 SnStatics
";

        // CONTRACT §5.2 "reserved" rows (offset size).
        private const string ReservedTable = @"
0x0006000 0xA000
0x0012000 0xE000
0x00A4000 0xC000
0x00D0000 0x30000
0x0E00000 0x200000
";

        // CONTRACT §5.4, typed field by field: record size, then "name offset size". `pad` rows are CONTRACT's
 // explicit pads; ω is `omega`; u16-prefixed strings in fixed records are `<name>Length` + `<name>`.
        private const string FieldTable = @"
SectionHeader 256
 magic 0x000 4
 layoutMajor 0x004 2
 layoutMinor 0x006 2
 byteOrderMark 0x008 4
 headerSize 0x00C 4
 sectionSize 0x010 8
 layoutHash 0x018 8
 wireVersion 0x020 4
 siteVersion 0x024 4
 damageScaleVersion 0x028 4
 regionCount 0x02C 4
 qpcFrequency 0x030 8
 devControl 0x038 4
 pad 0x03C 4
 snPid 0x040 4
 snGeneration 0x044 4
 snHeartbeatMs 0x048 8
 snPhase 0x050 4
 snFlags 0x054 4
 snHwnd 0x058 8
 pad 0x060 32
 sePid 0x080 4
 seGeneration 0x084 4
 seHeartbeatMs 0x088 8
 sePhase 0x090 4
 seFlags 0x094 4
 seHwnd 0x098 8
 seLoadStep 0x0A0 4
 pad 0x0A4 92
RegionEntry 32
 id 0x00 4
 kind 0x04 4
 offset 0x08 8
 size 0x10 8
 recordSize 0x18 4
 version 0x1C 4
RingControlEntry 128
 head 0x00 8
 pad 0x08 56
 tail 0x40 8
 pad 0x48 56
SeFrame 256
 seq 0x00 4
 flags 0x04 4
 tick 0x08 8
 qpcCapture 0x10 8
 simTimeSec 0x18 8
 simSpeed 0x20 4
 worldEpoch 0x24 4
 camPos 0x28 12
 camRot 0x34 16
 vFovDeg 0x44 4
 near 0x48 4
 aspect 0x4C 4
 cameraMode 0x50 4
 pad 0x54 4
 characterId 0x58 8
 charO 0x60 12
 charQ 0x6C 16
 charS 0x7C 4
 boneCount 0x80 4
 bonesVersion 0x84 4
 pelvisSn 0x88 12
 pad 0x94 108
SeBone 48
 rows 0x00 48
SeEntitiesHeader 64
 seq 0x00 4
 entityCount 0x04 4
 tick 0x08 8
 worldEpoch 0x10 4
 subpartCount 0x14 4
 lightCount 0x18 4
 pad 0x1C 36
SeEntity 96
 entityId 0x00 8
 kind 0x08 4
 flags 0x0C 4
 groupId 0x10 8
 o 0x18 12
 q 0x24 16
 s 0x34 4
 k 0x38 4
 v 0x3C 12
 omega 0x48 12
 partsVersion 0x54 4
 skinVersion 0x58 4
 pad 0x5C 4
SeSubpart 64
 ownerId 0x00 8
 partKey 0x08 8
 local 0x10 48
SeLight 64
 ownerId 0x00 8
 pos 0x08 12
 dir 0x14 12
 colour 0x20 12
 range 0x2C 4
 intensity 0x30 4
 coneDeg 0x34 4
 flags 0x38 4
 pad 0x3C 4
SeState 256
 seq 0x00 4
 flags 0x04 4
 tick 0x08 8
 viewState 0x10 4
 approachMode 0x14 4
 displayMode 0x18 4
 overlays 0x1C 4
 zone 0x20 4
 worldEpoch 0x24 4
 perfMode 0x28 4
 targetFps 0x2C 4
 snBusTarget 0x30 4
 crossfadeMs 0x34 4
 helloEpoch 0x38 4
 linkEpoch 0x3C 4
 Rs 0x40 8
 siteHash 0x48 8
 siteVersion 0x50 4
 pad 0x54 4
 seRect 0x58 16
 camSnY 0x68 4
 camVelSn 0x6C 12
 crossingTick 0x78 8
 pad 0x80 128
SeTime 64
 seq 0x00 4
 jumpSeq 0x04 4
 tick 0x08 8
 hourAngleRad 0x10 8
 sunElevationRad 0x18 4
 sunDirSn 0x1C 12
 dayLengthSec 0x28 4
 simSpeed 0x2C 4
 paused 0x30 4
 flags 0x34 4
 sunriseHourAngleRad 0x38 4
 pad 0x3C 4
SnStatus 256
 seq 0x00 4
 flags 0x04 4
 snPhase 0x08 4
 perfModeApplied 0x0C 4
 snFrame 0x10 8
 presentedSeTick 0x18 8
 areaBuiltSeTick 0x20 8
 areaBuiltPos 0x28 12
 helloAckEpoch 0x34 4
 linkAckEpoch 0x38 4
 mirroredEntityCount 0x3C 4
 pendingParts 0x40 4
 damageDropped 0x44 4
 c3BytesDrained 0x48 8
 damageOverflowDepth 0x50 4
 frameMsP50 0x54 4
 frameMsP99 0x58 4
 hostKind 0x5C 4
 snHwnd 0x60 8
 snRect 0x68 16
 capabilities 0x78 4
 vSyncCountApplied 0x7C 4
 targetFrameRateApplied 0x80 4
 fixedDeltaTime 0x84 4
 resamplesLastFrame 0x88 4
 pad 0x8C 116
DamageRecord 128
 dedupSeq 0x00 4
 snGeneration 0x04 4
 unit 0x08 4
 amount 0x0C 4
 seScaleExpected 0x10 4
 snDamageType 0x14 2
 flags 0x16 2
 targetKind 0x18 4
 sectionId 0x1C 4
 targetEntityId 0x20 8
 rootEntityId 0x28 8
 snapshotTick 0x30 8
 localHit 0x38 12
 snWorldHit 0x44 12
 attackDirSn 0x50 12
 dealerTechType 0x5C 4
 dealerInstanceId 0x60 4
 worldEpoch 0x64 4
 snTimeSec 0x68 8
 pad 0x70 16
SnCreaturesHeader 64
 seq 0x00 4
 count 0x04 4
 snFrame 0x08 8
 pad 0x10 48
SnCreature 48
 instanceId 0x00 4
 techType 0x04 4
 posSn 0x08 12
 velSn 0x14 12
 aggression 0x20 4
 flags 0x24 4
 targetEntityId 0x28 8
CaptureRequest 256
 seq 0x00 4
 reqId 0x04 4
 kind 0x08 4
 pad 0x0C 4
 atSeTick 0x10 8
 pathLength 0x18 2
 path 0x1A 230
CaptureResult 256
 seq 0x00 4
 reqId 0x04 4
 status 0x08 4
 alpha 0x0C 4
 snFrame 0x10 8
 seTickUsed 0x18 8
 pathLength 0x20 2
 path 0x22 222
MessageHeader 8
 type 0x00 4
 payloadBytes 0x04 4
ModelBegin 64
 modelKey 0x00 8
 lod 0x08 4
 vertexCount 0x0C 4
 indexCount 0x10 4
 submeshCount 0x14 4
 flags 0x18 4
 bounds 0x1C 24
 patternScale 0x34 4
 chunkCount 0x38 4
 totalBytes 0x3C 4
ModelChunkHeader 16
 modelKey 0x00 8
 chunkIndex 0x08 4
 bytes 0x0C 4
ModelEnd 16
 modelKey 0x00 8
 payloadHash 0x08 8
MaterialDefHeader 16
 materialKey 0x00 8
 drawTechnique 0x08 4
 flags 0x0C 4
SkinnedBindHeader 16
 modelKey 0x00 8
 boneCount 0x08 4
 pad 0x0C 4
ModelVertex 56
 pos 0x00 12
 normal 0x0C 12
 tangent 0x18 12
 binormal 0x24 12
 uv 0x30 8
ModelVertexSkinned 76
 pos 0x00 12
 normal 0x0C 12
 tangent 0x18 12
 binormal 0x24 12
 uv 0x30 8
 boneIndex 0x38 4
 weight 0x3C 16
ModelSubmesh 24
 materialKey 0x00 8
 triStart 0x08 4
 triCount 0x0C 4
 drawTechnique 0x10 4
 pad 0x14 4
GridAdd 32
 entityId 0x00 8
 targetClass 0x08 4
 flags 0x0C 4
 parentEntityId 0x10 8
 worldEpoch 0x18 4
 pad 0x1C 4
EntityRemove 8
 entityId 0x00 8
PartAdd 100
 entityId 0x00 8
 partKey 0x08 8
 modelKey 0x10 8
 local 0x18 48
 colorMaskHSV 0x48 12
 uvOffset 0x54 8
 skinId 0x5C 4
 flags 0x60 4
PartRemove 16
 entityId 0x00 8
 partKey 0x08 8
PartRecolor 32
 entityId 0x00 8
 partKey 0x08 8
 hsv 0x10 12
 pad 0x1C 4
SkinSetHeader 16
 entityId 0x00 8
 skinVersion 0x08 4
 sectionCount 0x0C 4
SkinSection 24
 sectionId 0x00 4
 centre 0x04 12
 boxCount 0x10 4
 kind 0x14 4
SkinBox 64
 centre 0x00 12
 half 0x0C 12
 axes 0x18 36
 flags 0x3C 4
RoomSetHeader 16
 entityId 0x00 8
 count 0x08 4
 pad 0x0C 4
RoomBox 24
 min 0x00 12
 max 0x0C 12
CharacterAdd 24
 entityId 0x00 8
 modelKey 0x08 8
 flags 0x10 4
 boneCount 0x14 4
ClearAll 8
 worldEpoch 0x00 4
 linkEpoch 0x04 4
BatchStaticsHeader 16
 bx 0x00 2
 by 0x02 2
 bz 0x04 2
 pad 0x06 2
 epoch 0x08 4
 count 0x0C 4
StaticPrimitive 48
 kind 0x00 1
 flags 0x01 1
 pad 0x02 2
 centreSn 0x04 12
 qSn 0x10 16
 size 0x20 12
 classIdHash 0x2C 4
BatchClear 16
 bx 0x00 2
 by 0x02 2
 bz 0x04 2
 pad 0x06 2
 epoch 0x08 4
 pad 0x0C 4
";

        private sealed class HandField { public string Name; public int Offset; public int Size; }
        private sealed class HandRecord { public string Name; public int Size; public List<HandField> Fields = new List<HandField>(); }

        private static int ParseInt(string s)
        {
            return s.StartsWith("0x", StringComparison.Ordinal)
                ? int.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : int.Parse(s, CultureInfo.InvariantCulture);
        }

        private static long ParseLong(string s)
        {
            return s.StartsWith("0x", StringComparison.Ordinal)
                ? long.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : long.Parse(s, CultureInfo.InvariantCulture);
        }

        private static List<HandRecord> HandRecords()
        {
            var list = new List<HandRecord>();
            HandRecord cur = null;
            foreach (string raw in FieldTable.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0) continue;
                string[] p = line.Trim().Split(' ');
                if (!line.StartsWith(" ", StringComparison.Ordinal))
                {
                    cur = new HandRecord { Name = p[0], Size = ParseInt(p[1]) };
                    list.Add(cur);
                }
                else
                {
                    cur.Fields.Add(new HandField { Name = p[0], Offset = ParseInt(p[1]), Size = ParseInt(p[2]) });
                }
            }
            return list;
        }

        private static Type RecordType(string name)
        {
            Type t = typeof(SectionHeader).Assembly.GetType("DeepEngineers.Wire." + name);
            Assert.IsNotNull(t, "no struct DeepEngineers.Wire." + name);
            return t;
        }

        [TestMethod]
        public void S0N1_RegionMap_MatchesContract()
        {
            var expected = new List<string[]>();
            foreach (string raw in RegionTable.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                expected.Add(Regex.Split(line, " +"));
            }
            Assert.AreEqual(expected.Count, Layout.Regions.Count, "region count");
            Assert.AreEqual(17, Layout.RegionCount);
            for (int i = 0; i < expected.Count; i++)
            {
                string[] e = expected[i];
                RegionDef g = Layout.Regions[i];
                string label = string.Join(" ", e, 6, e.Length - 6);
                Assert.AreEqual((uint)ParseLong(e[0]), (uint)g.Id, label + " id");
                Assert.AreEqual((uint)ParseLong(e[1]), (uint)g.Kind, label + " kind");
                Assert.AreEqual(ParseLong(e[2]), g.Offset, label + " offset");
                Assert.AreEqual(ParseLong(e[3]), g.Size, label + " size");
                Assert.AreEqual(ParseInt(e[4]), g.RecordSize, label + " recordSize");
                Assert.AreEqual((uint)ParseInt(e[5]), g.Version, label + " version");
                Assert.AreEqual(label, g.Name, "name");
            }
        }

        [TestMethod]
        public void S0N1_RegionsAndReservedTileTheSection()
        {
            var spans = new List<KeyValuePair<long, long>>();
            foreach (RegionDef g in Layout.Regions) spans.Add(new KeyValuePair<long, long>(g.Offset, g.Size));
            foreach (string raw in ReservedTable.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] p = Regex.Split(line, " +");
                spans.Add(new KeyValuePair<long, long>(ParseLong(p[0]), ParseLong(p[1])));
            }
            spans.Sort((a, b) => a.Key.CompareTo(b.Key));
            long next = 0;
            foreach (var s in spans)
            {
                Assert.AreEqual(next, s.Key, "gap or overlap at 0x" + next.ToString("x"));
                next = s.Key + s.Value;
            }
            Assert.AreEqual(0x5000000L, next, "end");
            Assert.AreEqual(0x5000000L, Layout.SectionSize);
            Assert.AreEqual(0x5000000L, WireInfo.SectionSize);
        }

        [TestMethod]
        public void S0N1_RingControl_OrderAndOffsets()
        {
            // CONTRACT §5.2: ring r = 0…7 in the order C3, C4, C8, C12, C11a, C11b, C11c, C11d; head at 0x1000 + r·0x80, tail +0x40.
            RegionId[] order =
            {
                RegionId.C3SeModels, RegionId.C4SeParts, RegionId.C8SnDamage, RegionId.C12SnStatics,
                RegionId.C11aCtlToSe, RegionId.C11bCtlFromSe, RegionId.C11cCtlToSn, RegionId.C11dCtlFromSn,
            };
            long[] dataOffsets = { 0x1000000, 0x0600000, 0x00B0000, 0x0200000, 0x0100000, 0x0140000, 0x0180000, 0x01C0000 };
            long[] capacities = { 0x4000000, 0x800000, 1024, 0x400000, 0x40000, 0x40000, 0x40000, 0x40000 };
            for (int r = 0; r < 8; r++)
            {
                Assert.AreEqual(order[r], Layout.RingRegion((RingIndex)r), "ring " + r);
                Assert.AreEqual(0x1000L + r * 0x80, Layout.HeadOffset((RingIndex)r), "head " + r);
                Assert.AreEqual(0x1000L + r * 0x80 + 0x40, Layout.TailOffset((RingIndex)r), "tail " + r);
                Assert.AreEqual(dataOffsets[r], Layout.Region(order[r]).Offset, "data " + r);
                Assert.AreEqual(capacities[r], RingControl.Capacity((RingIndex)r), "capacity " + r);
                // head and tail on separate 64-byte lines
                Assert.AreEqual(0L, Layout.HeadOffset((RingIndex)r) % 64);
                Assert.AreEqual(0L, Layout.TailOffset((RingIndex)r) % 64);
            }
            Assert.AreEqual(0, (int)RingIndex.C3SeModels);
            Assert.AreEqual(2, (int)RingIndex.C8SnDamage);
            Assert.AreEqual(7, (int)RingIndex.C11dCtlFromSn);
        }

        [TestMethod]
        public void S0N1_RecordSizes_MatchContract()
        {
            foreach (HandRecord h in HandRecords())
            {
                Type t = RecordType(h.Name);
                Assert.AreEqual(h.Size, Marshal.SizeOf(t), h.Name + " Marshal.SizeOf");
                Assert.AreEqual(h.Size, Layout.ManagedSize(t), h.Name + " sizeof");
                int sum = 0;
                foreach (HandField f in h.Fields) sum += f.Size;
                Assert.AreEqual(h.Size, sum, h.Name + " hand table fields do not add up (typing error in the test)");
            }
            // CONTRACT §5.4 sizes stated in prose
            Assert.AreEqual(4096, Layout.HeaderSize);
            Assert.AreEqual(100, Marshal.SizeOf(typeof(PartAdd)));
            Assert.AreEqual(108L + 4, ByteRing.FramedSize(100)); // "100 B payload, 108 B framed, 112 B padded"
            Assert.AreEqual(1024L, Layout.Region(RegionId.C8SnDamage).Size / Marshal.SizeOf(typeof(DamageRecord)));
        }

        [TestMethod]
        public void S0N1_FieldOffsets_MatchContract()
        {
            int checkedFields = 0;
            foreach (HandRecord h in HandRecords())
            {
                Type t = RecordType(h.Name);
                RecordDef def = Layout.Record(h.Name);
                Assert.AreEqual(h.Fields.Count, def.Fields.Count, h.Name + " field count in Layout's table");
                for (int i = 0; i < h.Fields.Count; i++)
                {
                    HandField f = h.Fields[i];
                    FieldDef d = def.Fields[i];
                    string label = h.Name + "." + f.Name + "@0x" + f.Offset.ToString("x");
                    Assert.AreEqual(f.Name, d.Name, label + " name in Layout's table");
                    Assert.AreEqual(f.Offset, d.Offset, label + " offset in Layout's table");
                    Assert.AreEqual(f.Size, d.Size, label + " size in Layout's table");
                    string member = f.Name == "pad" ? "pad_" + f.Offset.ToString("x") : f.Name;
                    FieldInfo fi = t.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Assert.IsNotNull(fi, label + " member " + member);
                    Assert.AreEqual((long)f.Offset, Marshal.OffsetOf(t, member).ToInt64(), label + " Marshal.OffsetOf");
                    Assert.AreEqual(f.Size, Marshal.SizeOf(fi.FieldType), label + " field size");
                    checkedFields++;
                }
            }
            Assert.AreEqual(Layout.Records.Count, HandRecords().Count, "record count");
            TestContext.WriteLine("checked " + checkedFields + " fields in " + Layout.Records.Count + " records");
        }

        [TestMethod]
        public unsafe void S0N1_CSharpSizeof_MatchesContract()
        {
            // sizeof(T) as the plugins' generic code sees it (the Unsafe.SizeOf of CONTRACT §6 validation step 3)
            var sizes = new Dictionary<string, int>
            {
                { "SectionHeader", sizeof(SectionHeader) }, { "RegionEntry", sizeof(RegionEntry) }, { "RingControlEntry", sizeof(RingControlEntry) },
                { "SeFrame", sizeof(SeFrame) }, { "SeBone", sizeof(SeBone) }, { "SeEntitiesHeader", sizeof(SeEntitiesHeader) },
                { "SeEntity", sizeof(SeEntity) }, { "SeSubpart", sizeof(SeSubpart) }, { "SeLight", sizeof(SeLight) },
                { "SeState", sizeof(SeState) }, { "SeTime", sizeof(SeTime) }, { "SnStatus", sizeof(SnStatus) },
                { "DamageRecord", sizeof(DamageRecord) }, { "SnCreaturesHeader", sizeof(SnCreaturesHeader) }, { "SnCreature", sizeof(SnCreature) },
                { "CaptureRequest", sizeof(CaptureRequest) }, { "CaptureResult", sizeof(CaptureResult) }, { "MessageHeader", sizeof(MessageHeader) },
                { "ModelBegin", sizeof(ModelBegin) }, { "ModelChunkHeader", sizeof(ModelChunkHeader) }, { "ModelEnd", sizeof(ModelEnd) },
                { "MaterialDefHeader", sizeof(MaterialDefHeader) }, { "SkinnedBindHeader", sizeof(SkinnedBindHeader) },
                { "ModelVertex", sizeof(ModelVertex) }, { "ModelVertexSkinned", sizeof(ModelVertexSkinned) }, { "ModelSubmesh", sizeof(ModelSubmesh) },
                { "GridAdd", sizeof(GridAdd) }, { "EntityRemove", sizeof(EntityRemove) }, { "PartAdd", sizeof(PartAdd) },
                { "PartRemove", sizeof(PartRemove) }, { "PartRecolor", sizeof(PartRecolor) }, { "SkinSetHeader", sizeof(SkinSetHeader) },
                { "SkinSection", sizeof(SkinSection) }, { "SkinBox", sizeof(SkinBox) }, { "RoomSetHeader", sizeof(RoomSetHeader) },
                { "RoomBox", sizeof(RoomBox) }, { "CharacterAdd", sizeof(CharacterAdd) }, { "ClearAll", sizeof(ClearAll) },
                { "BatchStaticsHeader", sizeof(BatchStaticsHeader) }, { "StaticPrimitive", sizeof(StaticPrimitive) }, { "BatchClear", sizeof(BatchClear) },
            };
            List<HandRecord> hand = HandRecords();
            Assert.AreEqual(hand.Count, sizes.Count);
            foreach (HandRecord h in hand)
            {
                Assert.AreEqual(h.Size, sizes[h.Name], h.Name + " sizeof");
                Assert.AreEqual(h.Size, Layout.ManagedSize(RecordType(h.Name)), h.Name + " Layout.ManagedSize");
            }
        }

        [TestMethod]
        public void S0N1_LayoutValidate_Passes()
        {
            Layout.Validate();
            string err;
            Assert.IsTrue(Layout.TryValidate(out err), err);
        }

        [TestMethod]
        public void S0N1_SeqlockRecords_HaveSeqFirst()
        {
            foreach (Type t in new[] { typeof(SeFrame), typeof(SeEntitiesHeader), typeof(SeState), typeof(SeTime),
                                       typeof(SnStatus), typeof(SnCreaturesHeader), typeof(CaptureRequest), typeof(CaptureResult) })
                Assert.AreEqual(0L, Marshal.OffsetOf(t, "seq").ToInt64(), t.Name);
        }

        [TestMethod]
        public void S0N1_Fnv1a64_KnownVectors()
        {
            Assert.AreEqual(0xcbf29ce484222325UL, LayoutHash.Fnv1a64Ascii(""));
            Assert.AreEqual(0xaf63dc4c8601ec8cUL, LayoutHash.Fnv1a64Ascii("a"));
            Assert.AreEqual(0x85944171f73967e8UL, LayoutHash.Fnv1a64Ascii("foobar"));
        }

        [TestMethod]
        public void S0N1_LayoutHash_PinnedValue()
        {
            ulong h = LayoutHash.Compiled;
            TestContext.WriteLine("layout 1.0 hash = " + LayoutHash.Format(h));
            Assert.AreEqual(PinnedHash, h, "layout hash changed: " + LayoutHash.Format(h) + " (update CONTRACT §6, LayoutHash.PinnedLayout10 and this test only with a layout version bump)");
            Assert.AreEqual(LayoutHash.PinnedLayout10, h, "LayoutHash.PinnedLayout10");
            Assert.AreEqual(LayoutHash.Fnv1a64Ascii(LayoutHash.CanonicalText()), h);
        }

        [TestMethod]
        public void S0N1_CanonicalText_MatchesFixtureByteForByte()
        {
            byte[] actual = Encoding.ASCII.GetBytes(LayoutHash.CanonicalText());
            string path = TestUtil.FixturePath("layout-1.0.txt");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path + ".actual", actual);
                Assert.Fail("fixture missing: " + path + " (generated text written to .actual)");
            }
            byte[] expected = File.ReadAllBytes(path);
            if (!BytesEqual(expected, actual))
            {
                File.WriteAllBytes(path + ".actual", actual);
                Assert.Fail("canonical text differs from the fixture (written to " + path + ".actual)");
            }
            Assert.AreEqual(LayoutHash.Compiled, LayoutHash.Fnv1a64(expected), "fixture hash");
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        [TestMethod]
        public void S0N1_CanonicalText_Grammar()
        {
            string text = LayoutHash.CanonicalText();
            Assert.IsTrue(text.EndsWith("\n", StringComparison.Ordinal), "ends with LF");
            Assert.IsFalse(text.Contains("\r"), "no CR");
            Assert.IsFalse(text.Contains("  "), "single spaces");
            Assert.IsFalse(text.Contains(" \n"), "no trailing spaces");
            foreach (char c in text) Assert.IsTrue(c == '\n' || (c >= 0x20 && c <= 0x7E), "ASCII");

            var region = new Regex(@"^region (\d+) (\d+) 0x([0-9a-f]+) 0x([0-9a-f]+) (\d+) (\d+)$");
            var ring = new Regex(@"^ring (\d+) (\d+) 0x([0-9a-f]+) 0x([0-9a-f]+)$");
            var table = new Regex(@"^table (\d+) ([A-Za-z0-9]+) 0x([0-9a-f]+) (\d+) (\d+) ([A-Za-z0-9]+)$");
            var message = new Regex(@"^message (\d+) (\d+) ([A-Za-z0-9]+)$");
            var constant = new Regex(@"^const ([A-Za-z0-9]+) ([A-Za-z0-9]+) (\d+)$");
            var field = new Regex(@"^field ([A-Za-z0-9]+) ([A-Za-z0-9]+) 0x([0-9a-f]+) (\d+) ((u8|u16|u32|u64|i16|i32|i64|f32|f64)(\[\d+\])?)$");
            string[] lines = text.Substring(0, text.Length - 1).Split('\n');
            int regions = 0, fields = 0, rings = 0, tables = 0, messages = 0, consts = 0;
            long lastId = -1;
            string lastRecord = null;
            long lastOffset = -1;
            int block = 0; // 0 region, 1 ring, 2 table, 3 message, 4 const, 5 field: blocks never go back
            string lastKey = null;
            foreach (string line in lines)
            {
                Match m = region.Match(line);
                if (m.Success)
                {
                    Assert.AreEqual(0, block, "region lines come first: " + line);
                    long id = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    Assert.IsTrue(id > lastId, "regions in id order: " + line);
                    Assert.IsFalse(m.Groups[3].Value.Length > 1 && m.Groups[3].Value[0] == '0', "no leading zeros: " + line);
                    lastId = id; regions++;
                    continue;
                }
                m = ring.Match(line);
                if (m.Success)
                {
                    Assert.IsTrue(block <= 1, "ring lines after the regions: " + line); block = 1;
                    Assert.AreEqual(rings, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), "ring index order: " + line);
                    rings++;
                    continue;
                }
                m = table.Match(line);
                if (m.Success)
                {
                    Assert.IsTrue(block <= 2, "table block: " + line); block = 2;
                    string key = m.Groups[1].Value.PadLeft(10, '0') + "|" + m.Groups[3].Value.PadLeft(10, '0');
                    Assert.IsTrue(tables == 0 || string.CompareOrdinal(lastKey, key) < 0, "tables by region then offset: " + line);
                    lastKey = key; tables++;
                    continue;
                }
                m = message.Match(line);
                if (m.Success)
                {
                    Assert.IsTrue(block <= 3, "message block: " + line);
                    if (block < 3) lastKey = null;
                    block = 3;
                    string key = m.Groups[1].Value.PadLeft(10, '0') + "|" + m.Groups[2].Value.PadLeft(10, '0');
                    Assert.IsTrue(lastKey == null || string.CompareOrdinal(lastKey, key) < 0, "messages by region then type: " + line);
                    lastKey = key; messages++;
                    continue;
                }
                m = constant.Match(line);
                if (m.Success)
                {
                    Assert.IsTrue(block <= 4, "const block: " + line);
                    if (block < 4) lastKey = null;
                    block = 4;
                    string key = m.Groups[1].Value + "|" + m.Groups[3].Value.PadLeft(20, '0') + "|" + m.Groups[2].Value;
                    Assert.IsTrue(lastKey == null || string.CompareOrdinal(lastKey, key) < 0, "consts by group, value, name: " + line);
                    lastKey = key; consts++;
                    continue;
                }
                m = field.Match(line);
                Assert.IsTrue(m.Success, "grammar: " + line);
                block = 5;
                string rec = m.Groups[1].Value;
                long off = long.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (lastRecord != null)
                {
                    int c = string.CompareOrdinal(lastRecord, rec);
                    Assert.IsTrue(c < 0 || (c == 0 && off > lastOffset), "sorted by record then offset: " + line);
                }
                lastRecord = rec; lastOffset = off; fields++;
            }
            Assert.AreEqual(17, regions);
            Assert.AreEqual(8, rings);
            Assert.AreEqual(17, tables);
            Assert.AreEqual(16, messages);
            Assert.AreEqual(144, consts);
            int expectedFields = 0;
            foreach (HandRecord h in HandRecords()) expectedFields += h.Fields.Count;
            Assert.AreEqual(expectedFields, fields);
            Assert.AreEqual(506, lines.Length, "CONTRACT §6: 506 lines");
            Assert.IsTrue(text.StartsWith("region 0 1 0x0 0x1000 256 1\nregion 1 2 0x1000 0x1000 128 1\n", StringComparison.Ordinal), "first lines");
            Assert.IsTrue(text.Contains("\nfield SeEntity omega 0x48 12 f32[3]\n"), "omega line");
            Assert.IsTrue(text.Contains("\nfield SectionHeader magic 0x0 4 u32\n"), "magic line");
            //  the sub-table bases, ring order, message codes and enum values are in the text
            Assert.IsTrue(text.Contains("\nring 0 48 0x1000 0x1040\n") && text.Contains("\nring 7 179 0x1380 0x13c0\n"), "ring order");
            Assert.IsTrue(text.Contains("\ntable 16 bones 0x100 160 48 SeBone\n"), "C1 bones at 0x100");
            Assert.IsTrue(text.Contains("\ntable 32 entities 0x40 1024 96 SeEntity\n"), "C2 entities at 0x40");
            Assert.IsTrue(text.Contains("\ntable 32 subparts 0x18040 2048 64 SeSubpart\n"), "C2 subparts at 0x18040");
            Assert.IsTrue(text.Contains("\ntable 32 lights 0x38040 64 64 SeLight\n"), "C2 lights at 0x38040");
            Assert.IsTrue(text.Contains("\ntable 144 records 0x40 256 48 SnCreature\n"), "C9 records at 0x40");
            Assert.IsTrue(text.Contains("\ntable 160 result 0x800 1 256 CaptureResult\n"), "C10 result at +0x800");
            Assert.IsTrue(text.Contains("\nmessage 64 9 ClearAll\n"), "C4 ClearAll = 9");
            Assert.IsTrue(text.Contains("\nconst SnPhase Quitting 7\n") && text.Contains("\nconst Overlays PeerExited 8\n"), "enum and bit values");
        }

        [TestMethod]
        public void S0N1_LayoutHash_PinsPlacementRingsMessagesAndConstants()
        {
            // Review finding 5: two builds that differ in any of these must not pass validation step 3 against each other.
            ulong baseHash = LayoutHash.Compiled;
            var tables = new List<TableDef>(Layout.Tables);
            TableDef lights = tables.Find(t => t.Name == "lights");
            tables[tables.IndexOf(lights)] = new TableDef(lights.Region, lights.Name, lights.Offset + 0x40, lights.Count, lights.Stride, lights.Record);
            Assert.AreNotEqual(baseHash, Hash(Layout.RingOrder, tables, Layout.Messages, Layout.Constants), "C2 lights base");
            TableDef result = new List<TableDef>(Layout.Tables).Find(t => t.Name == "result");
            tables = new List<TableDef>(Layout.Tables);
            tables[tables.IndexOf(result)] = new TableDef(result.Region, result.Name, 0x400, 1, 256, result.Record);
            Assert.AreNotEqual(baseHash, Hash(Layout.RingOrder, tables, Layout.Messages, Layout.Constants), "C10 result offset");

            var rings = new List<RegionId>(Layout.RingOrder);
            RegionId tmp = rings[4]; rings[4] = rings[5]; rings[5] = tmp; // C11a <-> C11b
            Assert.AreNotEqual(baseHash, Hash(rings, Layout.Tables, Layout.Messages, Layout.Constants), "ring order");

            var messages = new List<MessageDef>(Layout.Messages);
            MessageDef clear = messages.Find(m => m.Record == "ClearAll");
            messages[messages.IndexOf(clear)] = new MessageDef(clear.Region, 10, clear.Record);
            Assert.AreNotEqual(baseHash, Hash(Layout.RingOrder, Layout.Tables, messages, Layout.Constants), "message code");

            var consts = new List<ConstDef>(Layout.Constants);
            ConstDef quitting = consts.Find(c => c.Group == "SnPhase" && c.Name == "Quitting");
            consts[consts.IndexOf(quitting)] = new ConstDef("SnPhase", "Quitting", 8);
            Assert.AreNotEqual(baseHash, Hash(Layout.RingOrder, Layout.Tables, Layout.Messages, consts), "enum value");
            consts = new List<ConstDef>(Layout.Constants);
            ConstDef exited = consts.Find(c => c.Group == "Overlays" && c.Name == "PeerExited");
            consts[consts.IndexOf(exited)] = new ConstDef("Overlays", "PeerExited", 16);
            Assert.AreNotEqual(baseHash, Hash(Layout.RingOrder, Layout.Tables, Layout.Messages, consts), "flag bit");

            Assert.AreEqual(baseHash, Hash(Layout.RingOrder, Layout.Tables, Layout.Messages, Layout.Constants), "unchanged inputs");
            Assert.AreEqual(144, Layout.Constants.Count, "every literal of the 36 constant groups");
            Assert.AreEqual(36, Layout.ConstantGroups.Length);
        }

        private static ulong Hash(IReadOnlyList<RegionId> rings, IReadOnlyList<TableDef> tables, IReadOnlyList<MessageDef> messages,
            IReadOnlyList<ConstDef> consts)
        {
            return LayoutHash.Fnv1a64Ascii(LayoutHash.BuildCanonicalText(Layout.Regions, rings, tables, messages, consts, Layout.Records));
        }

        [TestMethod]
        public void S0N1_LayoutHash_IsSensitiveToEveryNumber()
        {
            var regions = new List<RegionDef>(Layout.Regions);
            ulong baseHash = LayoutHash.Fnv1a64Ascii(LayoutHash.BuildCanonicalText(regions, Layout.Records));
            Assert.AreEqual(LayoutHash.Compiled, baseHash);
            RegionDef c5 = Layout.Region(RegionId.C5SeState);
            int i5 = regions.IndexOf(c5);
            regions[i5] = new RegionDef(c5.Id, c5.Kind, c5.Offset + 0x1000, c5.Size, c5.RecordSize, c5.Version, c5.Name);
            Assert.AreNotEqual(baseHash, LayoutHash.Fnv1a64Ascii(LayoutHash.BuildCanonicalText(regions, Layout.Records)), "offset");
            regions[i5] = new RegionDef(c5.Id, c5.Kind, c5.Offset, c5.Size, c5.RecordSize, 2, c5.Name);
            Assert.AreNotEqual(baseHash, LayoutHash.Fnv1a64Ascii(LayoutHash.BuildCanonicalText(regions, Layout.Records)), "version");

            var records = new List<RecordDef>(Layout.Records);
            RecordDef st = Layout.Record("SeState");
            var fields = new List<FieldDef>(st.Fields);
            FieldDef rs = fields.Find(f => f.Name == "Rs");
            fields[fields.IndexOf(rs)] = new FieldDef(rs.Name, rs.Offset, rs.Size, "u64", false, rs.Member);
            records[records.IndexOf(st)] = new RecordDef(st.Name, st.ClrType, st.Size, fields.ToArray());
            Assert.AreNotEqual(baseHash, LayoutHash.Fnv1a64Ascii(LayoutHash.BuildCanonicalText(Layout.Regions, records)), "type");
        }

        [TestMethod]
        public void S0N1_ProtocolIdentity_Constants()
        {
            Assert.AreEqual(BitConverter.ToUInt32(Encoding.ASCII.GetBytes("DEEP"), 0), WireInfo.Magic, "magic bytes D E E P");
            Assert.AreEqual(0x50454544u, WireInfo.Magic);
            Assert.AreEqual(0x01020304u, WireInfo.ByteOrderMark);
            Assert.AreEqual(1u, WireInfo.WireVersion);
            Assert.AreEqual(1u, WireInfo.SiteVersion);
            Assert.AreEqual(1u, WireInfo.DamageScaleVersion);
            Assert.AreEqual((ushort)1, WireInfo.LayoutMajor);
            Assert.AreEqual((ushort)0, WireInfo.LayoutMinor);
            Assert.AreEqual(@"Local\DeepEngineers_v1", WireInfo.SectionName);
            Assert.AreEqual(@"Local\DeepEngineers_v1_sn", WireInfo.MutexSn);
            Assert.AreEqual(@"Local\DeepEngineers_v1_se", WireInfo.MutexSe);
            Assert.AreEqual(@"Local\DeepEngineers_v1_launcher", WireInfo.LauncherObject);
            Assert.AreEqual("DEEPENGINEERS_BRIDGE", WireInfo.BridgeEnvVar);
            Assert.AreEqual("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;S-1-5-21-1-2-3-1001)S:(ML;;NW;;;ME)", WireInfo.SectionSddl("S-1-5-21-1-2-3-1001"));
            Assert.IsTrue(BitConverter.IsLittleEndian);
            StringAssert.Contains(WireInfo.Describe(), "wire v1 layout 1.0 hash 0x");
        }

        [TestMethod]
        public void S0N1_Enumerations_MatchContract()
        {
            Assert.AreEqual(0u, SnPhase.None); Assert.AreEqual(1u, SnPhase.Booting); Assert.AreEqual(2u, SnPhase.Menu);
            Assert.AreEqual(3u, SnPhase.LoadingSave); Assert.AreEqual(4u, SnPhase.ReadyIdle); Assert.AreEqual(5u, SnPhase.WarmingUp);
            Assert.AreEqual(6u, SnPhase.Active); Assert.AreEqual(7u, SnPhase.Quitting);
            for (uint p = 0; p <= 7; p++) Assert.AreEqual(p >= 1 && p <= 3, SnPhase.IsLoading(p), "snPhase loading " + p);
            Assert.AreEqual(0u, SePhase.None); Assert.AreEqual(1u, SePhase.Menu); Assert.AreEqual(2u, SePhase.LoadingWorld);
            Assert.AreEqual(3u, SePhase.InWorld); Assert.AreEqual(4u, SePhase.Saving); Assert.AreEqual(5u, SePhase.Exiting);
            for (uint p = 0; p <= 5; p++) Assert.AreEqual(p == 2 || p == 4, SePhase.IsLoading(p), "sePhase loading " + p);
            Assert.AreEqual(1u, SnFlags.BridgeMode); Assert.AreEqual(2u, SnFlags.GhostingDisabled); Assert.AreEqual(4u, SnFlags.StylesApplied);
            Assert.AreEqual(1u, SeFlags.GateOpen); Assert.AreEqual(8u, SeFlags.FailSafeActive);
            Assert.AreEqual(1u, DevControl.HarnessAttached); Assert.AreEqual(2u, DevControl.SeChatRelay);
            Assert.AreEqual(1u << 8, SeFrameFlags.CharacterSwimming);
            Assert.AreEqual(1u << 8, SeStateFlags.GuiScreenOpen);
            Assert.AreEqual(1u << 3, Overlays.PeerExited);
            Assert.AreEqual(1u << 10, SnStatusFlags.DamageSelfTestPass);
            Assert.AreEqual(1u, HostKind.Subnautica1);
            Assert.AreEqual(1u << 4, Capabilities.Capture);
            Assert.AreEqual(1u, DamageUnit.SnHp);
            Assert.AreEqual((ushort)8, DamageFlags.FrameSubgrid);
            Assert.AreEqual(9u, C4Type.ClearAll); Assert.AreEqual(3u, C4Type.PartAdd); Assert.AreEqual(1u, C4Type.GridAdd);
            Assert.AreEqual(6u, C4Type.SkinSet); Assert.AreEqual(7u, C4Type.RoomSet); Assert.AreEqual(8u, C4Type.CharacterAdd);
            Assert.AreEqual(1u, C3Type.ModelBegin); Assert.AreEqual(2u, C3Type.ModelChunk); Assert.AreEqual(3u, C3Type.ModelEnd);
            Assert.AreEqual(4u, C3Type.MaterialDef); Assert.AreEqual(5u, C3Type.SkinnedBind);
            Assert.AreEqual(1u, C11Type.Json);
            Assert.AreEqual(1u, C12Type.BatchStatics); Assert.AreEqual(2u, C12Type.BatchClear);
            Assert.AreEqual(0x800, CaptureLayout.ResultOffset);
        }

        [TestMethod]
        public void S0N1_CapturePathStrings_RoundTrip()
        {
            var req = new CaptureRequest();
            Assert.IsTrue(req.SetPath("s1/WM-2/frame_0001.png"));
            Assert.AreEqual("s1/WM-2/frame_0001.png", req.GetPath());
            Assert.AreEqual((ushort)22, req.pathLength);
            Assert.IsFalse(req.SetPath(new string('x', 231)), "231 bytes do not fit 230");
            Assert.AreEqual("s1/WM-2/frame_0001.png", req.GetPath(), "unchanged after a failed set");
            var res = new CaptureResult();
            Assert.IsTrue(res.SetPath(new string('y', 222)));
            Assert.IsFalse(res.SetPath(new string('y', 223)));
        }
    }
}
