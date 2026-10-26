using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S5 "Whole grids + character", SE producer, game-independent parts (ROADMAP S5 "SE plugin"; CONTRACT §5.4 "C1–C4
 /// conventions for S5"): the conventions facade against Wire's MirrorConventions and the API-table vectors, the S5
 /// mutant, the entity-set hysteresis (enter 250 m, leave 300 m, 60 s, whole hierarchies, nearest first, cap, pins),
 /// the prioritised C4 lanes (order, tickets, purge, back-pressure, budget) and the lane chooser, the C2 row plan and
 /// its drops, the S5 part diff (replace semantics) and the per-cell debounce, the character split (Wire's split with
 /// the exporter's names and materials), skins (overrides, variants, merge order), skinned C3 models with SkinnedBind,
 /// the S5 C4 records against Wire's makers, the chunk count, the Dynamic threshold, the de.s5 syntax and settings, the
 ///.derec recorder (read back with Wire's DerecReader) and the link's C1 bones and C2 subpart rows in a real section;
 /// everything on the wire is checked byte for byte by s5_oracle.py (python -I) against tests/wire/layout_py.py and
 /// tools/worldmap/wire_ref.py, with seeded errors that must go red.
 /// </summary>
    [TestClass]
    public unsafe class S5Tests
    {
        public TestContext TestContext { get; set; }

        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); }

        // ================================================================================================== helpers

        private static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
        private static string HexKey(ulong v) { return "0x" + v.ToString("x16", CultureInfo.InvariantCulture); }
        private static string G(double v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        private sealed class FakeSink : IMessageSink
        {
            public long Capacity = long.MaxValue;
            public long Used;
            public readonly List<KeyValuePair<uint, byte[]>> Got = new List<KeyValuePair<uint, byte[]>>();
            public RingWriteStatus TryWrite(uint type, byte[] payload, int count)
            {
                if (count > 100000) return RingWriteStatus.TooLarge;
                if (Used + count > Capacity) return RingWriteStatus.Full;
                Used += count;
                var b = new byte[count];
                Buffer.BlockCopy(payload, 0, b, 0, count);
                Got.Add(new KeyValuePair<uint, byte[]>(type, b));
                return RingWriteStatus.Ok;
            }
        }

        private static OutMessage Msg(uint type, int n, long entity = 0, int size = 16)
        {
            var b = new byte[size];
            b[0] = (byte)n;
            return new OutMessage(type, b, 0, "m" + n) { Entity = entity };
        }

        /// <summary>A skinned two-material model: 3 quads (6 triangles), the last quad's material "Astronaut_head".</summary>
        internal static ExportedModel SkinnedModel()
        {
            var pos = new List<float>(); var nrm = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
            var bi = new List<byte>(); var bw = new List<float>();
            for (int q = 0; q < 3; q++)
            {
                int b = pos.Count / 3;
                for (int c = 0; c < 4; c++)
                {
                    pos.AddRange(new[] { q * 1.5f + (c & 1), (c >> 1) * 1.25f, 0.1f * q });
                    nrm.AddRange(new[] { 0f, 0f, 1f });
                    uv.AddRange(new[] { (float)(c & 1), (float)(c >> 1) });
                    bi.AddRange(new byte[] { (byte)(2 + q), (byte)(13 * (q / 2)), 0, 0 });
                    bw.AddRange(new[] { 0.75f, 0.25f, 0f, 0f });
                }
                idx.AddRange(new[] { b, b + 1, b + 3, b, b + 3, b + 2 });
            }
            int v = pos.Count / 3;
            var m = new ExportedModel
            {
                AssetName = @"Models\Characters\Astronaut\SE_astronaut.mwm",
                ModelKey = 0x1234UL,
                VertexCount = v,
                SeVertexCount = v,
                SeTriangleCount = idx.Count / 3,
                Positions = pos.ToArray(), Normals = nrm.ToArray(), Tangents = new float[3 * v], Binormals = new float[3 * v], Uvs = uv.ToArray(),
                Indices = idx.ToArray(),
                BoneIndices = bi.ToArray(), BoneWeights = bw.ToArray(),
                SkinnedBind = Enumerable.Range(0, 12 * MirrorSkeleton.BoneCount).Select(i => (float)((i % 12) == 0 || (i % 12) == 4 || (i % 12) == 8 ? 1 : 0)).ToArray(),
                Submeshes = new[]
                {
                    new ExportedSubmesh { MaterialKey = 0xA1UL, TriStart = 0, TriCount = 4, DrawTechnique = 0, MaterialName = "Astronaut" },
                    new ExportedSubmesh { MaterialKey = 0xB2UL, TriStart = 4, TriCount = 2, DrawTechnique = 0, MaterialName = "Astronaut_head" },
                },
                Materials = new[]
                {
                    new MaterialInfo { Key = 0xA1UL, Name = "Astronaut", ColorMetalRaw = @"Textures\Models\Characters\Astronaut_cm.dds" },
                    new MaterialInfo { Key = 0xB2UL, Name = "Astronaut_head", ColorMetalRaw = @"Textures\Models\Characters\Head_cm.dds" },
                },
            };
            Assert.IsNull(m.Validate(), m.Validate());
            return m;
        }

        // ================================================================================================== conventions

        [TestMethod]
        public void Conventions_FacadeEqualsWire_AndTheApiTableVectors()
        {
            Assert.AreEqual(MirrorConventions.Hash, S5Conv.Hash());
            Assert.AreEqual(0x31dfd6f9befdef5dUL, S5Conv.Hash(), "CONTRACT §5.4 pinned conventions hash");
            Assert.AreEqual(MirrorConventions.CanonicalText(), S5Conv.CanonicalText());
            Assert.AreEqual(1539, Encoding.ASCII.GetByteCount(S5Conv.CanonicalText()));
            Assert.AreEqual(0x995765282e7d4f1dUL, MirrorSkeleton.Hash, "canon1");
            // the API table's example vectors
            Assert.AreEqual(0x5148adb433ae18cfUL, S5Conv.FatKey(-3, 0, 12));
            Assert.AreEqual(0x09b78ce1c6718cc7UL, S5Conv.SubpartKey(0x5148adb433ae18cfUL, "DoorLeft"));
            Assert.AreEqual(0xf4a128d807ca725cUL, S5Conv.SubpartKey(0x5148adb433ae18cfUL, "TurretBase/TurretBarrel"));
            Assert.AreEqual(0xfded4532u, S5Conv.SkinId("Clean_Armor"), "lower-cased before hashing");
            Assert.AreEqual(0u, S5Conv.SkinId(null));
            // the handed-out modelKey/cube/edge vectors (0xf18ee13d1b3e0dec, 0xb59d1fc7857bd192, 0xd5f1574eb7e10570) hash a text
 // whose "\a" a shell turned into BEL (0x07); with the real backslash (recomputed with python -I from a file):
            ulong mk = S5Conv.ModelKey(@"Models\Cubes\Large\ArmorCube.mwm", 0, 1f, null, null);
            Assert.AreEqual(0xff3f6e15d5fff26aUL, mk);
            Assert.AreEqual(S3Keys.ModelKey(@"Models\Cubes\Large\ArmorCube.mwm", 0, 1f), mk, "no skin, no character: S3's key");
            Assert.AreEqual(0xeb6d37388d73d6d4UL, S5Conv.CubeKey(mk, 2.5f, 0f, -5f, 0f));
            Assert.AreEqual(PartDiff.PartKey(mk, 2.5f, 0f, -5f, 0f), S5Conv.CubeKey(mk, 2.5f, 0f, -5f, 0f), "cube keys are S3's");
            Assert.AreEqual(0x3dab9da5885325a6UL, S5Conv.EdgeKey(mk, 2.5f, 0f, -5f, 0f));
            Assert.AreEqual(0xb59d1fc7857bd192UL, S5Conv.CubeKey(0xf18ee13d1b3e0decUL, 2.5f, 0f, -5f, 0f), "the handed-out cube vector from its own (BEL) model key");
            Assert.AreEqual(0xd5f1574eb7e10570UL, S5Conv.EdgeKey(0xf18ee13d1b3e0decUL, 2.5f, 0f, -5f, 0f));
            Assert.AreEqual("models\\characters\\astronaut\\se_astronaut.mwm|lod0|skin=astronautskin_x|char=head1st",
                            S5Conv.ModelKeyText(@"Models\Characters\Astronaut\SE_astronaut.mwm", 0, 1f, "AstronautSkin_X", "head1st"));
            // flags: class in bits 0-3, the version nibble 1
            uint f = S5Conv.Part(S5Conv.PartClass.Subpart, S5Conv.PartFlags.Dynamic | S5Conv.PartFlags.MetalnessColorable);
            Assert.AreEqual(S5Conv.PartClass.Subpart, S5Conv.ClassOf(f));
            Assert.AreEqual(1u, S5Conv.VersionOf(f));
            Assert.AreEqual(MirrorConventions.PartFlags(MirrorPartClass.Subpart, MirrorPartFlags.Dynamic | MirrorPartFlags.MetalnessColorable), f);
            Assert.AreEqual(S5Conv.Version1 | S5Conv.GridFlags.Static | S5Conv.GridFlags.Subgrid, S5Conv.Grid(true, true, false));
            Assert.AreEqual(S5Conv.Version1 | S5Conv.CharFlags.Player | S5Conv.CharFlags.Fallback, S5Conv.Character(true));
        }

        [TestMethod]
        public void Mutants_S5M3_IsTheSeHalf_TheOthersAreRefusedAsSubnauticaSide()
        {
            StringAssert.StartsWith(Mutants.Set("S5.M3", true), "S5.M3 ON");
            Assert.IsTrue(S5Mutants.DropPartRemove);
            StringAssert.Contains(Mutants.Active, "S5.M3");
            foreach (string id in new[] { "S5.M1", "S5.M2", "S5.M4", "S5.M5" })
                StringAssert.StartsWith(Mutants.Set(id, true), "REFUSE " + id, "Subnautica-side");
            Mutants.ClearAll();
            Assert.IsFalse(S5Mutants.DropPartRemove);
            StringAssert.Contains(Mutants.Set("S9.M9", true), "S5.M3", "the unknown-id line lists S5.M3");
        }

        // ================================================================================================== entity set

        private static List<EntityCandidate> Cands(params EntityCandidate[] c) { return new List<EntityCandidate>(c); }

        [TestMethod]
        public void EntitySet_EntersAt250_StaysTo300_LeavesAfter60sOutside()
        {
            var t = new EntitySetTracker();
            var added = new List<long>();
            var removed = new List<KeyValuePair<long, string>>();
            t.Update(0, Cands(new EntityCandidate(1, 1, S5EntityKind.Grid, 260)), true, added, removed);
            Assert.AreEqual(0, added.Count, "260 m: not inside the enter radius");
            t.Update(16, Cands(new EntityCandidate(1, 1, S5EntityKind.Grid, 249.9)), false, added, removed);
            Assert.AreEqual(0, added.Count, "adds only while the zone allows them");
            t.Update(33, Cands(new EntityCandidate(1, 1, S5EntityKind.Grid, 249.9)), true, added, removed);
            CollectionAssert.AreEqual(new long[] { 1 }, added);
            t.Update(50, Cands(new EntityCandidate(1, 1, S5EntityKind.Grid, 299)), true, added, removed);
            Assert.IsTrue(added.Count == 0 && removed.Count == 0 && t.Contains(1), "hysteresis: stays up to 300 m");
            t.Update(100, Cands(), true, added, removed);
            t.Update(100 + 59999, Cands(new EntityCandidate(1, 1, S5EntityKind.Grid, 301)), true, added, removed);
            Assert.AreEqual(0, removed.Count, "outside for less than 60 s");
            t.Update(100 + 60000, Cands(), true, added, removed);
            Assert.AreEqual(1, removed.Count);
            Assert.AreEqual(1L, removed[0].Key);
            StringAssert.Contains(removed[0].Value, "60 s");
            Assert.IsFalse(t.Contains(1));
            // back inside 300 m resets the timer
            t.Update(70000, Cands(new EntityCandidate(2, 2, S5EntityKind.FloatingObject, 10)), true, added, removed);
            t.Update(80000, Cands(new EntityCandidate(2, 2, S5EntityKind.FloatingObject, 400)), true, added, removed);
            t.Update(100000, Cands(new EntityCandidate(2, 2, S5EntityKind.FloatingObject, 280)), true, added, removed);
            t.Update(150000, Cands(new EntityCandidate(2, 2, S5EntityKind.FloatingObject, 400)), true, added, removed);
            t.Update(200000, Cands(new EntityCandidate(2, 2, S5EntityKind.FloatingObject, 400)), true, added, removed);
            Assert.IsTrue(t.Contains(2), "outside again only since 150 s");
            t.Update(210001, Cands(), true, added, removed);
            Assert.IsFalse(t.Contains(2));
        }

        [TestMethod]
        public void EntitySet_WholeHierarchies_RootFirst_AttachJoinsAtOnce_LeaveTogether()
        {
            var t = new EntitySetTracker();
            var added = new List<long>();
            var removed = new List<KeyValuePair<long, string>>();
            // root 10 at 280 m (outside the enter radius), its subgrid 11 at 240 m: the hierarchy enters, root first
            t.Update(0, Cands(new EntityCandidate(11, 10, S5EntityKind.Grid, 240), new EntityCandidate(10, 10, S5EntityKind.Grid, 280)), true, added, removed);
            CollectionAssert.AreEqual(new long[] { 10, 11 }, added, "root first, then the members");
            // a rotor head attached (12, root 10) far away: joins at once
            t.Update(16, Cands(new EntityCandidate(10, 10, S5EntityKind.Grid, 280), new EntityCandidate(11, 10, S5EntityKind.Grid, 240), new EntityCandidate(12, 10, S5EntityKind.Grid, 900)), false, added, removed);
            CollectionAssert.AreEqual(new long[] { 12 }, added, "members of an in-set hierarchy join even when adds are off");
            // the hierarchy stays while any member is within 300 m
            t.Update(1000, Cands(new EntityCandidate(10, 10, S5EntityKind.Grid, 350), new EntityCandidate(11, 10, S5EntityKind.Grid, 299), new EntityCandidate(12, 10, S5EntityKind.Grid, 900)), true, added, removed);
            t.Update(70000, Cands(new EntityCandidate(10, 10, S5EntityKind.Grid, 350), new EntityCandidate(11, 10, S5EntityKind.Grid, 299), new EntityCandidate(12, 10, S5EntityKind.Grid, 900)), true, added, removed);
            Assert.AreEqual(3, t.Count);
            // all outside: they leave together after 60 s, the root last
            t.Update(80000, Cands(new EntityCandidate(10, 10, S5EntityKind.Grid, 350), new EntityCandidate(11, 10, S5EntityKind.Grid, 310), new EntityCandidate(12, 10, S5EntityKind.Grid, 900)), true, added, removed);
            t.Update(140000, Cands(new EntityCandidate(10, 10, S5EntityKind.Grid, 350), new EntityCandidate(11, 10, S5EntityKind.Grid, 310), new EntityCandidate(12, 10, S5EntityKind.Grid, 900)), true, added, removed);
            Assert.AreEqual(3, removed.Count);
            Assert.AreEqual(10L, removed[2].Key, "the root's EntityRemove last");
            Assert.AreEqual(0, t.Count);
        }

        [TestMethod]
        public void EntitySet_Cap_NearestFirst_RestWaits_PinsStay()
        {
            var t = new EntitySetTracker { MaxEntities = 3 };
            var added = new List<long>();
            var removed = new List<KeyValuePair<long, string>>();
            Assert.IsTrue(t.Pin(99, 99, S5EntityKind.Grid), "de.s3 registrations are pinned");
            t.Update(0, Cands(new EntityCandidate(1, 1, S5EntityKind.Grid, 200), new EntityCandidate(2, 2, S5EntityKind.Grid, 50),
                              new EntityCandidate(3, 3, S5EntityKind.Grid, 100)), true, added, removed);
            CollectionAssert.AreEqual(new long[] { 2, 3 }, added, "nearest first up to the cap (the pin counts)");
            Assert.AreEqual(1, t.Waiting);
            t.Update(1000, Cands(), true, added, removed);
            t.Update(62000, Cands(), true, added, removed);
            Assert.IsTrue(t.Contains(99), "a pinned entity never leaves by distance");
            Assert.IsFalse(t.Contains(2) || t.Contains(3));
            Assert.IsTrue(t.Remove(99));
            Assert.AreEqual(0.0, EntitySetTracker.BoxDistance(1, 1, 1, 0, 0, 0, 2, 2, 2));
            Assert.AreEqual(5.0, EntitySetTracker.BoxDistance(-3, 6, 1, 0, 0, 0, 2, 2, 2), 1e-12);
        }

        // ================================================================================================== C4 lanes

        [TestMethod]
        public void C4Lanes_LaneOrder_Fifo_Tickets_Purge()
        {
            var q = new C4Lanes("C4");
            long first = q.Enqueue(C4Lanes.FirstSend, Msg(C4Type.PartAdd, 1, 7));
            long edit = q.Enqueue(C4Lanes.Edit, Msg(C4Type.PartAdd, 2, 8));
            long near = q.Enqueue(C4Lanes.NearEdit, Msg(C4Type.PartRemove, 3, 8));
            long ctl = q.Enqueue(C4Lanes.Control, Msg(C4Type.GridAdd, 4, 7));
            q.Enqueue(C4Lanes.FirstSend, Msg(C4Type.PartAdd, 5, 7));
            Assert.AreEqual(5, q.Count);
            Assert.IsFalse(q.IsDone(first) || q.IsDone(ctl));
            Assert.AreEqual(C4Lanes.FirstSend, C4Lanes.LaneOf(first));
            Assert.AreEqual(0, q.Purge(0), "entity 0 is never purged");
            Assert.AreEqual(2, q.Purge(8), "purge removes entity 8's messages from lanes 2-4");
            Assert.IsTrue(q.LaneCount(2) == 0 && q.LaneCount(3) == 0 && q.LaneCount(1) == 1 && q.LaneCount(4) == 2);
            Assert.IsTrue(q.IsDone(edit) && q.IsDone(near), "purged messages count as left");
            Assert.AreEqual(2, q.Purge(7), "lane 1 keeps its messages: only entity 7's two lane-4 messages go");
            Assert.AreEqual(1, q.LaneCount(1));
            q.Enqueue(C4Lanes.FirstSend, Msg(C4Type.PartAdd, 1, 7));
            q.Enqueue(C4Lanes.FirstSend, Msg(C4Type.PartAdd, 5, 7));
            var sink = new FakeSink();
            PumpResult r = q.Pump(sink, new PumpBudget(1000, 100, 0), Stopwatch.GetTimestamp, -1);
            Assert.AreEqual(PumpStop.Empty, r.Stop);
            CollectionAssert.AreEqual(new byte[] { 4, 1, 5 }, sink.Got.Select(g => g.Value[0]).ToArray(), "lane 1 first, then lane 4 in FIFO order");
            Assert.IsTrue(q.IsDone(first) && q.IsDone(ctl));
            Assert.AreEqual(0L, q.PendingBytes);
            Assert.AreEqual(3L, q.TotalWritten);
            // S3 compatibility: Enqueue without a lane = lane 4
            Assert.AreEqual(C4Lanes.FirstSend, C4Lanes.LaneOf(q.Enqueue(Msg(1, 9))));
            Assert.IsFalse(q.IsDone(-1), "no ticket is never done");
        }

        [TestMethod]
        public void C4Lanes_BackPressure_Lanes12Only_AndTheBudget()
        {
            var q = new C4Lanes("C4");
            q.Enqueue(C4Lanes.FirstSend, Msg(3, 1));
            q.Enqueue(C4Lanes.Edit, Msg(3, 2));
            q.Enqueue(C4Lanes.NearEdit, Msg(3, 3));
            q.Enqueue(C4Lanes.Control, Msg(1, 4));
            var sink = new FakeSink();
            PumpResult r = q.Pump(sink, new PumpBudget(1000, 100, 0), Stopwatch.GetTimestamp, (1L << 20) - 1);
            CollectionAssert.AreEqual(new byte[] { 4, 3 }, sink.Got.Select(g => g.Value[0]).ToArray(), "C4 free < 1 MiB: lanes 1-2 only");
            Assert.AreEqual(PumpStop.Full, r.Stop);
            Assert.IsTrue(q.LastBackPressure);
            Assert.AreEqual(1, q.BackPressureTicks);
            r = q.Pump(sink, new PumpBudget(1000, 1, 0), Stopwatch.GetTimestamp, 8L << 20);
            Assert.IsTrue(r.Stop == PumpStop.Budget && r.Written == 1, "message budget: one per tick here");
            r = q.Pump(sink, new PumpBudget(1000, 100, 16), Stopwatch.GetTimestamp, 8L << 20);
            Assert.IsTrue(r.Written == 1 && q.Count == 0, "byte budget: the first message of a tick is always attempted, then the budget stops");
            // a full ring stops the tick, the message stays at the head
            q.Enqueue(C4Lanes.Control, Msg(1, 5));
            var full = new FakeSink { Capacity = 0 };
            r = q.Pump(full, new PumpBudget(1000, 100, 0), Stopwatch.GetTimestamp, -1);
            Assert.IsTrue(r.Stop == PumpStop.Full && q.Count == 1 && q.FullEvents == 1);
            Assert.AreEqual(PumpStop.Held, q.Pump(null, new PumpBudget(1, 1, 0), Stopwatch.GetTimestamp, -1).Stop, "no sink: held");
        }

        [TestMethod]
        public void LaneChooser_NeverOvertakesAnEntitysEarlierMessages()
        {
            var q = new C4Lanes("C4");
            var c = new LaneChooser();
            Assert.AreEqual(C4Lanes.NearEdit, c.Choose(q, true));
            Assert.AreEqual(C4Lanes.Edit, c.Choose(q, false));
            c.Note(q.Enqueue(C4Lanes.FirstSend, Msg(3, 1, 5)));
            Assert.AreEqual(C4Lanes.FirstSend, c.Choose(q, true), "first send still queued: edits follow it in lane 4");
            q.Pump(new FakeSink(), new PumpBudget(1000, 100, 0), Stopwatch.GetTimestamp, -1);
            c.Note(q.Enqueue(C4Lanes.Edit, Msg(3, 2, 5)));
            Assert.AreEqual(C4Lanes.Edit, c.Choose(q, true), "an edit in lane 3 is pending: a near edit cannot overtake it");
            q.Pump(new FakeSink(), new PumpBudget(1000, 100, 0), Stopwatch.GetTimestamp, -1);
            Assert.AreEqual(C4Lanes.NearEdit, c.Choose(q, true));
            Assert.IsTrue(c.AllDone(q));
        }

        // ================================================================================================== C2 rows

        [TestMethod]
        public void C2Rows_SubgridsThenDynamicThenTool_NearestFirst_DropsCounted()
        {
            var rows = new List<C2RowCandidate>
            {
                new C2RowCandidate { Kind = C2RowKind.Tool, Distance = 0, Row = new SeSubpart { ownerId = 9, partKey = 3 } },
                new C2RowCandidate { Kind = C2RowKind.DynamicSubpart, Distance = 50, Row = new SeSubpart { ownerId = 1, partKey = 100 } },
                new C2RowCandidate { Kind = C2RowKind.Subgrid, Distance = 80, Row = new SeSubpart { ownerId = 2, partKey = 21 } },
                new C2RowCandidate { Kind = C2RowKind.DynamicSubpart, Distance = 10, Row = new SeSubpart { ownerId = 1, partKey = 101 } },
                new C2RowCandidate { Kind = C2RowKind.Subgrid, Distance = 20, Row = new SeSubpart { ownerId = 3, partKey = 31 } },
            };
            var output = new SeSubpart[16];
            int dropped;
            int n = C2RowPlanner.Plan(rows, output, 16, out dropped);
            Assert.AreEqual(5, n);
            Assert.AreEqual(0, dropped);
            CollectionAssert.AreEqual(new ulong[] { 31, 21, 101, 100, 3 }, output.Take(n).Select(r => r.partKey).ToArray());
            n = C2RowPlanner.Plan(rows, output, 3, out dropped);
            Assert.AreEqual(3, n);
            Assert.AreEqual(2, dropped, "past the table: dropped and counted (the tool goes first)");
            // a subgrid row is Wire's relative matrix (subWorld x rootWorld^-1, the translation difference first)
            var root = new SeMatrix(new Vec3(0, 0, -1), new Vec3(0, 1, 0), new Vec3(1, 0, 0), new Vec3(1.0e6, 2.5e5, -3.0e5));
            var sub = new SeMatrix(new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(1.0e6 + 2.5, 2.5e5 + 1, -3.0e5 - 4));
            SeSubpart row = InstanceMath.SubgridRow(1, 2, sub, root);
            SeMatrix back = InstanceMath.Multiply(InstanceMath.RowLocal(row), root);
            Assert.AreEqual(0, (back.Translation - sub.Translation).Length, 1e-3, "row × root = sub (1 mm, float32 rows)");
        }

        // ================================================================================================== diff, debounce

        [TestMethod]
        public void PartDiff_S5_ReplaceUnderTheSameKey_RecolorRemove_FlagsInTheSignature()
        {
            PartSnapshot a = S3Tests.Part(0x11, 0, 0, 0), b = S3Tests.Part(0x11, 2.5f, 0, 0), c = S3Tests.Part(0x11, 5f, 0, 0);
            a.Flags = b.Flags = c.Flags = S5Conv.Part(S5Conv.PartClass.CubePart, 0);
            var held = new Dictionary<ulong, PartSnapshot> { { a.PartKey, a }, { b.PartKey, b }, { c.PartKey, c } };
            PartSnapshot a2 = a; a2.ModelKey = 0x22; // a fat block keeps its key across build stages: same key, new model
            PartSnapshot b2 = b; b2.H = 0.5f;
            PartSnapshot d = S3Tests.Part(0x11, 7.5f, 0, 0);
            var now = new Dictionary<ulong, PartSnapshot> { { a2.PartKey, a2 }, { b2.PartKey, b2 }, { d.PartKey, d } };
            List<PartSnapshot> adds = new List<PartSnapshot>(), replaces = new List<PartSnapshot>(), removes = new List<PartSnapshot>(), recolors = new List<PartSnapshot>();
            S5PartDiff.Compute(held, now, adds, replaces, removes, recolors);
            Assert.IsTrue(adds.Count == 1 && adds[0].PartKey == d.PartKey);
            Assert.IsTrue(replaces.Count == 1 && replaces[0].ModelKey == 0x22, "replace: PartAdd only, no PartRemove");
            Assert.IsTrue(removes.Count == 1 && removes[0].PartKey == c.PartKey);
            Assert.IsTrue(recolors.Count == 1 && recolors[0].H == 0.5f);
            PartSnapshot dyn = a; dyn.Flags |= S5Conv.PartFlags.Dynamic;
            Assert.AreNotEqual(S5PartDiff.SnapshotHash(a), S5PartDiff.SnapshotHash(dyn), "flags in the signature");
            Assert.IsFalse(dyn.SameExceptColour(a), "a flag change is a replace");
            Assert.AreNotEqual(PartDiff.Signature(new Dictionary<ulong, PartSnapshot> { { a.PartKey, a } }),
                               PartDiff.Signature(new Dictionary<ulong, PartSnapshot> { { a.PartKey, dyn } }), "S3's signature sees the flags too");
        }

        [TestMethod]
        public void SignatureDebounce_TwoIdenticalReads_ThenOnlyChanges()
        {
            var d = new SignatureDebounce();
            Assert.IsFalse(d.Offer(5, 2));
            Assert.IsTrue(d.Unsettled(2), "one read: wait for the next tick");
            Assert.IsTrue(d.Offer(5, 2), "stable and new");
            d.Commit(5);
            Assert.IsFalse(d.Offer(5, 2));
            Assert.IsTrue(d.Settled(2), "unchanged");
            Assert.IsFalse(d.Offer(6, 2));
            Assert.IsTrue(d.Unsettled(2), "a change needs two reads again (SE's parallel cell rebuild)");
            Assert.IsFalse(d.Offer(7, 2));
            Assert.IsTrue(d.Offer(7, 2));
            d.Forget();
            Assert.IsFalse(d.Offer(7, 2));
            Assert.IsTrue(d.Offer(7, 2), "forgotten (stream restart): resent");
        }

        // ================================================================================================== character

        [TestMethod]
        public void CharacterSplit_HeadMaterials_UsedVerticesOnly_SkinningCarried_EqualsWire()
        {
            ExportedModel full = SkinnedModel();
            ExportedModel body, head;
            CharacterSplit.Split(full, new[] { "Head", "ASTRONAUT_HEAD", "Spacesuit_hood" }, out body, out head);
            Assert.IsNotNull(body);
            Assert.IsNotNull(head);
            Assert.IsNull(body.Validate(), body.Validate());
            Assert.IsNull(head.Validate(), head.Validate());
            Assert.AreEqual(8, body.VertexCount, "two quads");
            Assert.AreEqual(4, head.VertexCount, "one quad, only its vertices");
            Assert.AreEqual("Astronaut_head", head.Submeshes.Single().MaterialName, "case-insensitive material match");
            Assert.AreEqual(0xB2UL, head.Materials.Single().Key, "the head's material only");
            Assert.AreEqual(0xA1UL, body.Materials.Single().Key);
            CollectionAssert.AreEqual(new byte[] { 4, 13, 0, 0 }, head.BoneIndices.Take(4).ToArray(), "skinning carried with the vertex");
            Assert.AreSame(full.SkinnedBind, head.SkinnedBind);
            // Wire's split on the same geometry gives the same arrays
            ModelGeometry wb, wh;
            ModelGeometry g = S3Codec.ToGeometry(full);
            SkinMath.SplitByMaterial(g, new[] { false, true }, out wb, out wh);
            CollectionAssert.AreEqual(wh.Indices.Select(i => (int)i).ToArray(), head.Indices);
            CollectionAssert.AreEqual(wb.Positions, body.Positions);
            // no head material: no head part
            CharacterSplit.Split(full, new string[0], out body, out head);
            Assert.IsNull(head);
            Assert.AreEqual(full.VertexCount, body.VertexCount);
        }

        [TestMethod]
        public void BoneTable_Canon1FromTheDataFile_MapsAnyOrderByName_WeightsToTheCanonicalAncestor()
        {
            // the data file is the table the SE plugin picks C1 bones by (MirrorSkeleton is generated from it)
            string tsv = File.ReadAllText(PyOracle.PathIn("src", "DeepEngineers.Wire", "Data", "SeSkeleton.canon1.tsv"));
            var rows = tsv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)).Skip(1).Select(l => l.Split('\t')).ToList();
            Assert.AreEqual(MirrorSkeleton.BoneCount, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.AreEqual(MirrorSkeleton.BoneName(i), rows[i][1], "row " + i);
                Assert.AreEqual(MirrorSkeleton.Parent(i), int.Parse(rows[i][2], CultureInfo.InvariantCulture), "row " + i);
            }
            Assert.AreEqual(MirrorSkeleton.PinnedHash, MirrorSkeleton.Hash);
            Assert.IsTrue(MirrorSkeleton.MapsAllRequiredHumanBones(), "the 15 required human bones are mapped");

            // a model with the female order (bones 14-23 permuted) and 3 extra mesh-node bones under the head and a palm
            var names = new List<string>();
            var parents = new List<int>();
            var order = Enumerable.Range(0, 78).ToList();
            order.Reverse(14, 10);
            foreach (int c in order) names.Add(MirrorSkeleton.BoneName(c));
            foreach (int c in order) parents.Add(MirrorSkeleton.Parent(c) < 0 ? -1 : names.IndexOf(MirrorSkeleton.ParentName(c)));
            names.Add("SE_RigHeadExtra"); parents.Add(names.IndexOf("SE_RigHead"));
            names.Add("SE_RigHeadExtra2"); parents.Add(names.Count - 2);
            names.Add("MeshNode_Palm"); parents.Add(names.IndexOf("SE_RigRPalm"));
            SkeletonMap map;
            string why;
            Assert.IsTrue(SkeletonMap.TryBuild(names, parents, out map, out why), why);
            for (int c = 0; c < 78; c++) Assert.AreEqual(MirrorSkeleton.BoneName(c), names[map.CanonicalToModel[c]], "canonical row " + c + " by name");
            Assert.AreEqual(3, map.NonCanonicalBones);
            var bi = new byte[] { (byte)names.IndexOf("SE_RigHeadExtra2"), (byte)names.IndexOf("SE_RigHead"), (byte)names.IndexOf("MeshNode_Palm"), 0 };
            var bw = new float[] { 0.25f, 0.25f, 0.5f, 0f };
            var ob = new byte[4];
            var ow = new float[4];
            Assert.IsTrue(SkinMath.RemapInfluences(map, bi, bw, 0, 4, ob, ow, 0));
            CollectionAssert.AreEqual(new byte[] { (byte)MirrorSkeleton.Head, (byte)MirrorSkeleton.RightPalm, 0, 0 }, ob, "merged on the ancestors, sorted by weight (ties: lower index)");
            CollectionAssert.AreEqual(new float[] { 0.5f, 0.5f, 0f, 0f }, ow);
            // a canonical bone under another parent: no export (the default Engineer, logged FALLBACK)
            parents[names.IndexOf("SE_RigHead")] = names.IndexOf("SE_RigSpine1");
            Assert.IsFalse(SkeletonMap.TryBuild(names, parents, out map, out why));
            StringAssert.Contains(why, "SE_RigHead");
        }

        [TestMethod]
        public void SkinnedModel_C3_ModelBeginSkinned_76BVertices_SkinnedBindLastInTheGroup()
        {
            ExportedModel m = SkinnedModel();
            long payload;
            List<OutMessage> msgs = S3Codec.ModelMessages(m, 256, 7, out payload);
            Assert.AreEqual(C3Type.ModelBegin, msgs[0].Type);
            ModelBegin b;
            Assert.IsTrue(ModelCodec.TryRead(msgs[0].Payload, 0, msgs[0].Payload.Length, out b));
            Assert.AreEqual(ModelFlags.Skinned, b.flags & ModelFlags.Skinned);
            Assert.AreEqual((long)m.VertexCount * ModelCodec.SkinnedVertexStride + m.Indices.Length * 4 + m.Submeshes.Length * 24, payload);
            OutMessage last = msgs[msgs.Count - 1];
            Assert.AreEqual(C3Type.SkinnedBind, last.Type);
            Assert.IsTrue(msgs.All(x => x.Group == 7), "one group: a model that can never fit drops its bind too");
            SkinnedBindDefinition sbd;
            string err;
            fixed (byte* p = last.Payload) Assert.IsTrue(ModelCodec.TryDecodeSkinnedBind(p, last.Payload.Length, out sbd, out err), err);
            Assert.AreEqual(MirrorSkeleton.BoneCount, sbd.BoneCount);
            Assert.AreEqual(m.ModelKey, sbd.ModelKey);
            // the payload decodes with the canonical indices and weights
            var assembler = new List<byte>();
            foreach (OutMessage x in msgs) if (x.Type == C3Type.ModelChunk) assembler.AddRange(x.Payload.Skip(16));
            ModelGeometry g;
            Assert.IsTrue(ModelCodec.TryDecodePayload(b, assembler.ToArray(), 0, assembler.Count, out g, out err), err);
            CollectionAssert.AreEqual(m.BoneIndices, g.BoneIndices);
            CollectionAssert.AreEqual(m.BoneWeights, g.BoneWeights);
        }

        [TestMethod]
        public void Skins_OverridesOnlyNamedMaterials_VariantSharesGeometry_MergeLaterWins()
        {
            ExportedModel m = SkinnedModel();
            var changes = new Dictionary<string, SkinTextureChange>(StringComparer.Ordinal) { { "Astronaut_head", new SkinTextureChange { ColorMetal = @"Textures\Skins\Gold_cm.dds" } } };
            Assert.IsTrue(SkinResolve.Overrides(m, changes));
            Assert.IsFalse(SkinResolve.Overrides(m, new Dictionary<string, SkinTextureChange> { { "Other", new SkinTextureChange() } }), "no material of the model: the base key");
            int applied = 0;
            ExportedModel v = SkinResolve.Variant(m, 0x99UL, changes, (orig, ch) =>
            {
                applied++;
                return new MaterialInfo { Key = orig.Key ^ 0xF00DUL, Name = orig.Name, ColorMetalRaw = ch.ColorMetal };
            });
            Assert.AreEqual(1, applied);
            Assert.AreEqual(0x99UL, v.ModelKey);
            Assert.AreSame(m.Positions, v.Positions, "same geometry");
            Assert.AreEqual(0xA1UL, v.Submeshes[0].MaterialKey, "unchanged material keeps its key");
            Assert.AreEqual(0xB2UL ^ 0xF00DUL, v.Submeshes[1].MaterialKey, "changed material: a new content key");
            Assert.AreEqual(2, v.Materials.Length);
            var merged = SkinResolve.Merge(new IDictionary<string, SkinTextureChange>[]
            {
                new Dictionary<string, SkinTextureChange> { { "A", new SkinTextureChange { ColorMetal = "cm1", NormalGloss = "ng1" } } },
                new Dictionary<string, SkinTextureChange> { { "A", new SkinTextureChange { ColorMetal = "cm2" } }, { "B", new SkinTextureChange { AlphaMask = "am" } } },
            });
            Assert.AreEqual("cm2", merged["A"].ColorMetal, "a later skin wins per texture");
            Assert.AreEqual("ng1", merged["A"].NormalGloss, "and keeps what it does not change");
            Assert.AreEqual("am", merged["B"].AlphaMask);
        }

        [TestMethod]
        public void Records_S5_GridAdd_PartAdd_CharacterAdd_EqualWiresMakers()
        {
            OutMessage ga = S3Codec.GridAddMsg(77, false, 42, 3, S5Conv.Grid(false, true, false));
            Assert.AreEqual(77L, ga.Entity, "C4 messages carry their entity (purge)");
            Assert.AreEqual(Hex(ModelCodec.ToBytes(ModelCodec.MakeGridAdd(77, GridClass.SmallGrid, MirrorGridFlags.Version1 | MirrorGridFlags.Subgrid, 42, 3))), Hex(ga.Payload));
            OutMessage ca = S3Codec.CharacterAddMsg(5, 0xABCDUL, S5Conv.Character(true), (uint)MirrorSkeleton.BoneCount);
            Assert.AreEqual(Hex(ModelCodec.ToBytes(MirrorConventions.MakeCharacterAdd(5, 0xABCDUL, true))), Hex(ca.Payload));
            var body = new PartSnapshot
            {
                PartKey = S5Conv.CharPart.Head1st, ModelKey = 0xABCEUL, Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
                H = 0.1f, S = -0.2f, V = 0.3f, SkinId = 7,
                Flags = S5Conv.Part(S5Conv.PartClass.CharacterBody, S5Conv.PartFlags.MetalnessColorable | S5Conv.PartFlags.HiddenInFirstPerson),
            };
            OutMessage pa = S3Codec.PartAddMsg(5, body);
            Assert.AreEqual(Hex(ModelCodec.ToBytes(MirrorConventions.MakeCharacterPart(5, MirrorCharacterPart.Head1st, 0xABCEUL, new Vec3(0.1f, -0.2f, 0.3f), 7))), Hex(pa.Payload));
            var fo = new PartSnapshot { PartKey = 1, ModelKey = 0x55, Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }, Flags = S5Conv.Part(S5Conv.PartClass.FloatingObject, 0) };
            Assert.AreEqual(Hex(ModelCodec.ToBytes(MirrorConventions.MakeFloatingObjectPart(9, 0x55, Vec3.Zero))), Hex(S3Codec.PartAddMsg(9, fo).Payload));
            PartAdd rd;
            Assert.IsTrue(ModelCodec.TryRead(pa.Payload, 0, pa.Payload.Length, out rd));
            Assert.AreEqual(MirrorRecordVersion.Version1, MirrorConventions.Classify(rd.flags));
            Assert.AreEqual(PartDrawKind.Skinned, ChunkMath.DrawKind(rd.flags));
        }

        // ================================================================================================== chunks, motion

        [TestMethod]
        public void Chunks_ExpectedCount_ByCellMaterialColourAndFlags_DynamicAndToolsOwn()
        {
            var parts = new List<PartSnapshot>();
            uint cube = S5Conv.Part(S5Conv.PartClass.CubePart, 0);
            parts.Add(Make(0x1, 0, 0, 0, 0f, cube));           // cell (0,0,0)
            parts.Add(Make(0x1, 19.9f, 0, 0, 0f, cube));       // same 20 m cell
            parts.Add(Make(0x1, 20f, 0, 0, 0f, cube));         // next cell
            parts.Add(Make(0x1, -0.1f, 0, 0, 0f, cube));       // cell -1 (floor)
            parts.Add(Make(0x1, 0, 0, 0, 0.5f, cube));         // another colour
            parts.Add(Make(0x1, 0, 0, 0, 0f, S5Conv.Part(S5Conv.PartClass.Edge, S5Conv.PartFlags.NoShadows))); // other flags
            parts.Add(Make(0x2, 0, 0, 0, 0f, S5Conv.Part(S5Conv.PartClass.Subpart, S5Conv.PartFlags.Dynamic)));  // own child
            parts.Add(Make(0x3, 0, 0, 0, 0f, S5Conv.Part(S5Conv.PartClass.Tool, S5Conv.PartFlags.Dynamic)));     // own child
            Func<ulong, IList<ulong>> mats = k => k == 0x1 ? new ulong[] { 0xA, 0xB } : new ulong[] { 0xC };
            int own;
            Assert.AreEqual(5 * 2, S5Chunks.Count(1, true, parts, mats, out own), "5 distinct (cell, colour, flags) × 2 materials");
            Assert.AreEqual(2, own);
            Assert.AreEqual(ChunkMath.CellOf(-0.1f, 0, 0, GridClass.LargeGrid).X, -1);
            Assert.AreEqual(4f, ChunkMath.CellSize(GridClass.SmallGrid), "small grids: 4 m cells");
        }

        private static PartSnapshot Make(ulong model, float x, float y, float z, float h, uint flags)
        {
            return new PartSnapshot { PartKey = PartDiff.PartKey(model, x, y, z, h), ModelKey = model, Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, x, y, z }, H = h, Flags = flags };
        }

        [TestMethod]
        public void DynamicSubparts_ThresholdIs1e4MetreOr1e4Radian_AndAnimatingTypes()
        {
            float[] sent = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 2, 3 };
            Assert.IsFalse(SubpartMotion.Moved(sent, new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(1 + 5e-5, 2, 3))));
            Assert.IsTrue(SubpartMotion.Moved(sent, new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(1 + 2e-4, 2, 3))));
            foreach (double a in new[] { 5e-5, 2e-4 })
            {
                double c = Math.Cos(a), s = Math.Sin(a);
                var rot = new SeMatrix(new Vec3(c, s, 0), new Vec3(-s, c, 0), Vec3.UnitZ, new Vec3(1, 2, 3));
                Assert.AreEqual(a > 1e-4, SubpartMotion.Moved(sent, rot), "angle " + a);
            }
            Assert.IsFalse(SubpartMotion.Moved(null, InstanceMath.Identity));
            Assert.IsTrue(SubpartMotion.AnimatesByType("MyAirtightHangarDoor") && SubpartMotion.AnimatesByType("MyLargeGatlingTurret") && SubpartMotion.AnimatesByType("MyPistonTop"));
            Assert.IsFalse(SubpartMotion.AnimatesByType("MyCockpit"));
            float[] fl = SubpartMotion.Floats(new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(4, 5, 6)));
            CollectionAssert.AreEqual(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 4, 5, 6 }, fl);
        }

        // ================================================================================================== de.s5 syntax

        [TestMethod]
        public void S5Args_ChatSyntax_AndSettings()
        {
            string usage;
            Assert.AreEqual("status", S5Args.ActionOf(S5Args.ParseChat(new[] { "s5" }, out usage)));
            Assert.AreEqual("stats", S5Args.ActionOf(S5Args.ParseChat(new[] { "s5", "stats" }, out usage)));
            Dictionary<string, object> a = S5Args.ParseChat(new[] { "s5", "set", "enterM=200", "character=off" }, out usage);
            Assert.AreEqual("set", S5Args.ActionOf(a));
            var set = (IDictionary<string, object>)a["set"];
            Assert.AreEqual(200.0, set["enterM"]);
            Assert.AreEqual(false, set["character"]);
            a = S5Args.ParseChat(new[] { "s5", "export", "123456789012345678", "sample=5" }, out usage);
            long id;
            Assert.IsTrue(S5Args.TryEntityId(a["entityId"], out id) && id == 123456789012345678L, "ids above 2^53 travel as text");
            Assert.AreEqual(5.0, a["sample"]);
            Assert.AreEqual("on", S5Args.ParseChat(new[] { "s5", "character", "ON" }, out usage)["character"]);
            a = S5Args.ParseChat(new[] { "s5", "record", "ship2000", "c1=true", "c2=on" }, out usage);
            var rec = (IDictionary<string, object>)a["record"];
            Assert.IsTrue((string)rec["name"] == "ship2000" && (bool)rec["c1"] && (bool)rec["c2"]);
            Assert.AreEqual("stop", S5Args.ParseChat(new[] { "s5", "record", "stop" }, out usage)["record"]);
            Assert.AreEqual(200.0, ((IDictionary<string, object>)S5Args.ParseChat(new[] { "s5", "models", "200" }, out usage)["models"])["count"]);
            a = S5Args.ParseChat(new[] { "s5", "coh", "77", "1", "2", "3", "seconds=5" }, out usage);
            Assert.AreEqual(3, ((List<object>)((IDictionary<string, object>)a["coh"])["point"]).Count);
            Assert.AreEqual(4.0, S5Args.ParseChat(new[] { "s5", "trace", "4" }, out usage)["trace"]);
            Assert.IsNull(S5Args.ParseChat(new[] { "s5", "export" }, out usage));
            Assert.AreEqual(S5Args.ChatUsage, usage);
            Assert.IsNull(S5Args.ParseChat(new[] { "s5", "character", "maybe" }, out usage));
            Assert.IsNull(S5Args.ParseChat(new[] { "s5", "bogus" }, out usage));
            Assert.IsFalse(S5Args.TryEntityId(9007199254740994.0, out id), "doubles above 2^53 are refused");

            var st = new S5Settings();
            string why;
            Assert.IsNotNull(st.Apply(new Dictionary<string, object> { { "enterM", 100.0 }, { "leaveM", 150.0 }, { "keepS", 30.0 }, { "maxEntities", 5000.0 } }, out why), why);
            Assert.IsTrue(st.EnterM == 100 && st.LeaveM == 150 && st.KeepS == 30 && st.MaxEntities == S5Defaults.MaxEntities, "capped at the C2 capacity");
            Assert.IsNull(st.Apply(new Dictionary<string, object> { { "leaveM", 50.0 } }, out why));
            StringAssert.Contains(why, "leaveM");
            Assert.AreEqual(150.0, st.LeaveM, "a refused set changes nothing");
            Assert.IsNull(st.Apply(new Dictionary<string, object> { { "speed", 1.0 } }, out why));
            Assert.IsNull(st.Apply(new Dictionary<string, object> { { "enabled", "perhaps" } }, out why));
        }

        [TestMethod]
        public void Chat_DeS5_GoesToTheExtraCommand()
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
                Assert.IsTrue(dev.HandleChat("/de s5 status", 1));
                Assert.IsTrue(dev.HandleChat("/de s5 record ship500 c2=true", 2));
                Assert.IsTrue(dev.HandleChat("/de s5 nonsense", 3), "usage, still ours");
                Assert.AreEqual(2, calls.Count);
                Assert.IsTrue(calls.All(c => c.Key == "de.s5"));
                Assert.IsTrue(rig.Chat.Contains("stub de.s5") && rig.Chat.Exists(t => t.StartsWith("usage: /de s5", StringComparison.Ordinal)), string.Join(" | ", rig.Chat));
            }
        }

        // ================================================================================================== recorder

        private static string TempDir(string tag)
        {
            string d = Path.Combine(Path.GetTempPath(), "de-s5-" + tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        [TestMethod]
        public void Recorder_WritesADerecWiresReaderReads_WithTheSidecar()
        {
            string dir = TempDir("rec");
            try
            {
                var rec = new StreamRecorder();
                string why;
                Assert.IsFalse(rec.Start(dir, "../evil", false, false, 1210014, 3, false, 1700000000000UL, out why), "names are checked");
                Assert.IsTrue(rec.Start(dir, "ship500", true, true, 1210014, 3, false, 1700000000000UL, out why), why);
                Assert.IsFalse(rec.Start(dir, "other", false, false, 1, 1, false, 1, out why), "one recording at a time");
                ExportedModel cube = S3Tests.Cube();
                long bytes;
                foreach (OutMessage m in S3Codec.ModelMessages(cube, 512, 1, out bytes)) rec.RecordMessage(Derec.KindC3, m.Type, m.Payload, m.Payload.Length, 10, 1000);
                OutMessage ga = S3Codec.GridAddMsg(55, true, 0, 3, S5Conv.Grid(true, false, false));
                rec.RecordMessage(Derec.KindC4, ga.Type, ga.Payload, ga.Payload.Length, 11, 1001);
                OutMessage pa = S3Codec.PartAddMsg(55, S3Tests.Part(cube.ModelKey, 0, 0, 0));
                rec.RecordMessage(Derec.KindC4, pa.Type, pa.Payload, pa.Payload.Length, 11, 1002);
                var c1 = new byte[Derec.C1RecordBytes(78)];
                c1[8] = 12;
                rec.RecordRaw(Derec.KindC1, c1, c1.Length, 12, 1003);
                var c2 = new byte[Derec.C2SnapshotBytes(1, 2, 0)];
                rec.RecordRaw(Derec.KindC2, c2, c2.Length, 12, 1004);
                rec.Marker("{\"ev\":\"edit\",\"kind\":\"grind\"}", 13, 1005);
                rec.Stop("test");
                Assert.IsTrue(rec.Join(10000), "the writer thread ends");
                Assert.IsNull(rec.LastError, rec.LastError);
                string path = Derec.RecordingPath(dir, "ship500");
                var kinds = new List<ushort>();
                using (var r = new DerecReader(File.OpenRead(path)))
                {
                    Assert.AreEqual(LayoutHash.Compiled, r.Header.layoutHash);
                    Assert.AreEqual(MirrorConventions.Hash, r.Header.conventionsHash);
                    Assert.AreEqual(1210014u, r.Header.seVersion);
                    Assert.AreEqual(Derec.FlagHasC1 | Derec.FlagHasC2, r.Header.flags);
                    Assert.AreEqual(3u, r.Header.worldEpoch);
                    DerecRecordHeader h;
                    byte[] body;
                    while (r.TryNext(out h, out body)) kinds.Add(h.kind);
                    Assert.IsTrue(r.SawEnd && !r.Truncated, "the end record closes the file");
                }
                int c3 = kinds.Count(k => k == Derec.KindC3);
                Assert.IsTrue(c3 >= 3, "ModelBegin, chunks, ModelEnd");
                CollectionAssert.AreEqual(new ushort[] { Derec.KindC4, Derec.KindC4, Derec.KindC1, Derec.KindC2, Derec.KindMarker }, kinds.Skip(c3).ToArray());
                var side = (IDictionary<string, object>)MiniJson.Parse(File.ReadAllText(Derec.SidecarPath(dir, "ship500")));
                Assert.AreEqual("ship500", side["name"]);
                Assert.AreEqual(1.0, side["models"]);
                Assert.AreEqual(1.0, side["grids"]);
                Assert.AreEqual(1.0, side["parts"]);
                Assert.AreEqual(0.0, side["dropped"]);
                Assert.AreEqual((double)kinds.Count, side["records"]);
                Assert.AreEqual(rec.LastSha256, side["sha256"]);
                Assert.AreEqual("0x" + MirrorConventions.Hash.ToString("x16", CultureInfo.InvariantCulture), side["conventionsHash"]);
                // not recording: nothing queued
                rec.RecordMessage(Derec.KindC4, 1, new byte[4], 4, 1, 1);
                Assert.AreEqual(kinds.Count, (int)rec.RecordsWritten);
                // the oracle reads the same file independently
                RunOracle(new StringBuilder("{\"recording\":{\"path\":").Append(JsonText.Serialize(path)).Append(",\"sidecar\":")
                    .Append(JsonText.Serialize(Derec.SidecarPath(dir, "ship500"))).Append(",\"flags\":3,\"kinds\":[").Append(string.Join(",", kinds)).Append("]}}").ToString(), "rec");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        // ================================================================================================== link

        [TestMethod]
        public void Link_C1WithCanonBones_C2WithSubpartRows_InARealSection()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    byte* b = sn.Base;
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(LinkState.Up, link.State);
                    var bones = new float[12 * SeFrameLimits.MaxBones];
                    for (int i = 0; i < 12 * 78; i++) bones[i] = i * 0.5f;
                    var rows = new[]
                    {
                        InstanceMath.MakeSubpartRow(55, 56, new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(1, 2, 3))),
                        InstanceMath.MakeSubpartRow(55, 0x1234, InstanceMath.Identity),
                    };
                    link.BeforeSeFrame = (tick, qpc) =>
                    {
                        var e = new SeEntity { entityId = 55, kind = SeEntityKind.RootGrid, groupId = 55, q = new F32x4(0, 0, 0, 1), s = 1, k = 1 };
                        Assert.IsTrue(link.WriteSeEntities(tick, 1, new[] { e }, 1, rows, 2));
                    };
                    var frame = new SeFrame { tick = 900, worldEpoch = 1, boneCount = 78, bonesVersion = 0xDEADBEEF, characterId = 7 };
                    Assert.IsTrue(link.WriteSeFrame(ref frame, bones, 78));
                    byte* c1 = Layout.RegionPtr(b, RegionId.C1SeFrame);
                    uint seq;
                    Assert.IsTrue(Seqlock.ReadBegin(c1, out seq));
                    var head = *(SeFrame*)c1;
                    float* fb = (float*)(c1 + SeFrameLimits.BonesOffset);
                    Assert.IsTrue(head.tick == 900 && head.boneCount == 78 && head.bonesVersion == 0xDEADBEEF && head.characterId == 7);
                    Assert.AreEqual(0.5f * (12 * 77 + 11), fb[12 * 77 + 11], "the last bone's last float");
                    Assert.AreEqual(0f, fb[12 * 78], "nothing past boneCount written");
                    Assert.IsTrue(Seqlock.ReadEnd(c1, seq), "head and bones in one seqlock window");
                    byte* c2 = Layout.RegionPtr(b, RegionId.C2SeEntities);
                    var hdr = *(SeEntitiesHeader*)c2;
                    var r1 = *(SeSubpart*)(c2 + SeEntitiesLimits.SubpartsOffset + SeEntitiesLimits.SubpartSize);
                    Assert.IsTrue(hdr.entityCount == 1 && hdr.subpartCount == 2 && hdr.tick == 900);
                    Assert.IsTrue(r1.ownerId == 55 && r1.partKey == 0x1234);
                    // S1's head-only write keeps working
                    var f2 = new SeFrame { tick = 901 };
                    Assert.IsTrue(link.WriteSeFrame(ref f2));
                    try { link.WriteSeFrame(ref f2, bones, 161); Assert.Fail("more than 160 bones refused"); }
                    catch (ArgumentOutOfRangeException) { }
                    try { link.WriteSeEntities(1, 1, new SeEntity[0], 0, rows, 3); Assert.Fail("rows beyond the array refused"); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
        }

        // ================================================================================================== oracle

        private void RunOracle(string json, string tag)
        {
            string dir = PyOracle.PathIn("tests", "out", "se-s5");
            Directory.CreateDirectory(dir);
            string inPath = Path.Combine(dir, "s5-oracle-" + tag + ".json");
            File.WriteAllText(inPath, json, new UTF8Encoding(false));
            string stdout = PyOracle.Run(Path.Combine("tests", "DeepEngineers.SE.Tests", "s5_oracle.py"), inPath);
            TestContext.WriteLine(stdout);
            Assert.IsTrue(stdout.Contains("S5 ORACLE OK"), stdout);
        }

        private static void Floats(StringBuilder sb, IEnumerable<float> v)
        {
            sb.Append('[');
            bool first = true;
            foreach (float f in v) { if (!first) sb.Append(','); first = false; sb.Append(G(f)); }
            sb.Append(']');
        }

        private static void SplitJson(StringBuilder sb, ExportedModel m)
        {
            if (m == null) { sb.Append("null"); return; }
            sb.Append("{\"positions\":"); Floats(sb, m.Positions);
            sb.Append(",\"indices\":[").Append(string.Join(",", m.Indices)).Append(']');
            sb.Append(",\"boneIndices\":[").Append(string.Join(",", m.BoneIndices)).Append(']');
            sb.Append(",\"boneWeights\":"); Floats(sb, m.BoneWeights);
            sb.Append(",\"submeshes\":[");
            for (int i = 0; i < m.Submeshes.Length; i++)
            {
                ExportedSubmesh s = m.Submeshes[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"materialKey\":\"").Append(HexKey(s.MaterialKey)).Append("\",\"triStart\":").Append(s.TriStart).Append(",\"triCount\":").Append(s.TriCount)
                  .Append(",\"name\":"); JsonText.AppendString(sb, s.MaterialName); sb.Append('}');
            }
            sb.Append("]}");
        }

        private static string PartAddRecord(byte[] payload)
        {
            PartAdd p;
            Assert.IsTrue(ModelCodec.TryRead(payload, 0, payload.Length, out p));
            var sb = new StringBuilder("{\"record\":\"PartAdd\",\"fields\":{");
            sb.Append("\"entityId\":\"").Append(HexKey(p.entityId)).Append("\",\"partKey\":\"").Append(HexKey(p.partKey)).Append("\",\"modelKey\":\"").Append(HexKey(p.modelKey)).Append('"');
            sb.Append(",\"local\":"); Floats(sb, Enumerable.Range(0, 12).Select(i => p.local[i]));
            sb.Append(",\"colorMaskHSV\":["); sb.Append(G(p.colorMaskHSV.x)).Append(',').Append(G(p.colorMaskHSV.y)).Append(',').Append(G(p.colorMaskHSV.z)).Append(']');
            sb.Append(",\"uvOffset\":[").Append(G(p.uvOffset.x)).Append(',').Append(G(p.uvOffset.y)).Append(']');
            sb.Append(",\"skinId\":").Append(p.skinId).Append(",\"flags\":").Append(p.flags);
            sb.Append("},\"hex\":\"").Append(Hex(payload)).Append("\"}");
            return sb.ToString();
        }

        [TestMethod]
        public void Oracle_KeysFlagsRecordsSplitAndRows_AgreeWithLayoutPyAndWireRef_SeededErrorsGoRed()
        {
            var sb = new StringBuilder("{\"keys\":[");
            ulong fat = S5Conv.FatKey(-3, 0, 12);
            sb.Append("{\"kind\":\"fat\",\"min\":[-3,0,12],\"key\":\"").Append(HexKey(fat)).Append("\"},");
            sb.Append("{\"kind\":\"fat\",\"min\":[100,-7,2147483647],\"key\":\"").Append(HexKey(S5Conv.FatKey(100, -7, int.MaxValue))).Append("\"},");
            sb.Append("{\"kind\":\"sub\",\"fatKey\":\"").Append(HexKey(fat)).Append("\",\"names\":[\"TurretBase\",\"TurretBarrel\"],\"key\":\"")
              .Append(HexKey(S5Conv.SubpartKey(fat, "TurretBase/TurretBarrel"))).Append("\"},");
            ulong mk = S5Conv.ModelKey(@"Models\Cubes\Large\ArmorSlope.mwm", 0, 1f, null, null);
            float[] t = { 2.5f, -1.25f, 7.5f, 3.0000002f };
            sb.Append("{\"kind\":\"cube\",\"modelKey\":\"").Append(HexKey(mk)).Append("\",\"t\":"); Floats(sb, t);
            sb.Append(",\"key\":\"").Append(HexKey(S5Conv.CubeKey(mk, t[0], t[1], t[2], t[3]))).Append("\"},");
            sb.Append("{\"kind\":\"edge\",\"modelKey\":\"").Append(HexKey(mk)).Append("\",\"t\":"); Floats(sb, t);
            sb.Append(",\"key\":\"").Append(HexKey(S5Conv.EdgeKey(mk, t[0], t[1], t[2], t[3]))).Append("\"},");
            foreach (var mspec in new[] { Tuple.Create(@"Models\Cubes\Large\ArmorSlope.mwm", 1f, (string)null, "", 0), Tuple.Create("Models/Cubes/Small/Thing.mwm", 0.4f, "Clean_Armor", "", 0),
                                          Tuple.Create(@"Models\Characters\Astronaut\SE_astronaut.mwm", 1f, "AstronautSkin+Helmet", "body", 1), Tuple.Create(@"Models\Characters\Astronaut\SE_astronaut.mwm", 1f, (string)null, "head1st", 2) })
            {
                sb.Append("{\"kind\":\"model\",\"path\":"); JsonText.AppendString(sb, mspec.Item1);
                sb.Append(",\"scale\":").Append(G(mspec.Item2)).Append(",\"skin\":"); if (mspec.Item3 == null) sb.Append("null"); else JsonText.AppendString(sb, mspec.Item3);
                sb.Append(",\"char\":").Append(mspec.Item5).Append(",\"key\":\"").Append(HexKey(S5Conv.ModelKey(mspec.Item1, 0, mspec.Item2, mspec.Item3, mspec.Item4.Length == 0 ? null : mspec.Item4))).Append("\"},");
            }
            sb.Append("{\"kind\":\"skin\",\"skin\":\"Clean_Armor\",\"id\":").Append(S5Conv.SkinId("Clean_Armor")).Append("}],\"flags\":[");
            var fl = new List<string>();
            foreach (uint cls in new[] { 1u, 2u, 3u, 4u, 5u, 6u, 7u })
                foreach (uint extra in new[] { 0u, S5Conv.PartFlags.Dynamic, S5Conv.PartFlags.NoShadows | S5Conv.PartFlags.MetalnessColorable, S5Conv.PartFlags.BuildStage | S5Conv.PartFlags.Deformed, S5Conv.PartFlags.HiddenInFirstPerson })
                    fl.Add("{\"cls\":" + cls + ",\"extra\":" + extra + ",\"flags\":" + S5Conv.Part(cls, extra) + "}");
            sb.Append(string.Join(",", fl)).Append("],\"records\":[");
            var subpart = new PartSnapshot
            {
                PartKey = S5Conv.SubpartKey(fat, "DoorLeft"), ModelKey = mk, Local = new float[] { 0, 0, -1, 0, 1, 0, 1, 0, 0, 1.25f, 0.5f, -2.5f },
                H = 0.25f, S = -0.5f, V = 0.125f, SkinId = S5Conv.SkinId("Clean_Armor"),
                Flags = S5Conv.Part(S5Conv.PartClass.Subpart, S5Conv.PartFlags.Dynamic | S5Conv.PartFlags.MetalnessColorable),
            };
            OutMessage spMsg = S3Codec.PartAddMsg(-9000000000000000123L, subpart);
            sb.Append(PartAddRecord(spMsg.Payload)).Append(',');
            var edgePart = new PartSnapshot { PartKey = S5Conv.EdgeKey(mk, 1, 2, 3, 4), ModelKey = mk, Local = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 2, 3 }, H = 0.5f, Flags = S5Conv.Part(S5Conv.PartClass.Edge, S5Conv.PartFlags.NoShadows) };
            sb.Append(PartAddRecord(S3Codec.PartAddMsg(77, edgePart).Payload)).Append(',');
            OutMessage ga = S3Codec.GridAddMsg(78, true, 77, 5, S5Conv.Grid(true, true, false));
            sb.Append("{\"record\":\"GridAdd\",\"fields\":{\"entityId\":\"").Append(HexKey(78)).Append("\",\"targetClass\":1,\"flags\":").Append(S5Conv.Grid(true, true, false))
              .Append(",\"parentEntityId\":\"").Append(HexKey(77)).Append("\",\"worldEpoch\":5},\"hex\":\"").Append(Hex(ga.Payload)).Append("\"},");
            OutMessage ca = S3Codec.CharacterAddMsg(7, 0xFEEDUL, S5Conv.Character(false), 78);
            sb.Append("{\"record\":\"CharacterAdd\",\"fields\":{\"entityId\":\"").Append(HexKey(7)).Append("\",\"modelKey\":\"").Append(HexKey(0xFEEDUL)).Append("\",\"flags\":")
              .Append(S5Conv.Character(false)).Append(",\"boneCount\":78},\"hex\":\"").Append(Hex(ca.Payload)).Append("\"}],\"split\":{\"model\":");
            ExportedModel full = SkinnedModel();
            ExportedModel body, head;
            CharacterSplit.Split(full, new[] { "Astronaut_head" }, out body, out head);
            SplitJson(sb, full);
            sb.Append(",\"head\":[\"Astronaut_head\"],\"body\":"); SplitJson(sb, body);
            sb.Append(",\"headPart\":"); SplitJson(sb, head);
            sb.Append("},\"relative\":[");
            var root = new SeMatrix(new Vec3(0, 0, -1), new Vec3(0, 1, 0), new Vec3(1, 0, 0), new Vec3(-1029000.25, 250000.5, 1500000.75));
            double c = Math.Cos(0.3), s = Math.Sin(0.3);
            var sub = new SeMatrix(new Vec3(c, 0, s), new Vec3(0, 1, 0), new Vec3(-s, 0, c), new Vec3(-1029000.25 + 3.5, 250000.5 - 2, 1500000.75 + 11));
            SeSubpart row = InstanceMath.SubgridRow(1, 2, sub, root);
            sb.Append("{\"child\":["); SeRows(sb, sub); sb.Append("],\"parent\":["); SeRows(sb, root); sb.Append("],\"row\":"); Floats(sb, Enumerable.Range(0, 12).Select(i => row.local[i]));
            sb.Append("}]}");
            string json = sb.ToString();
            RunOracle(json, "main");

            // seeded errors: a flipped flag byte in the subpart PartAdd and a moved vertex of the head part must go red
            string good = Hex(spMsg.Payload);
            string bad = json.Replace(good, good.Substring(0, good.Length - 2) + "21");
            string headPos = G(head.Positions[0]);
            int at = bad.LastIndexOf("\"headPart\":{\"positions\":[" + headPos, StringComparison.Ordinal);
            Assert.IsTrue(at > 0);
            bad = bad.Substring(0, at) + "\"headPart\":{\"positions\":[" + G(head.Positions[0] + 0.001f) + bad.Substring(at + ("\"headPart\":{\"positions\":[" + headPos).Length);
            try
            {
                RunOracle(bad, "seeded");
                Assert.Fail("seeded errors not caught");
            }
            catch (InvalidOperationException e)
            {
                TestContext.WriteLine(e.Message);
                Assert.IsTrue(e.Message.Contains("S5 ORACLE FAILED") && e.Message.Contains("PartAdd bytes differ") && e.Message.Contains("split headPart positions differ"), e.Message);
            }
        }

        private static void SeRows(StringBuilder sb, SeMatrix m)
        {
            var r = new[] { m.Right.X, m.Right.Y, m.Right.Z, m.Up.X, m.Up.Y, m.Up.Z, m.Backward.X, m.Backward.Y, m.Backward.Z, m.Translation.X, m.Translation.Y, m.Translation.Z };
            sb.Append(string.Join(",", r.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
        }
    }
}
