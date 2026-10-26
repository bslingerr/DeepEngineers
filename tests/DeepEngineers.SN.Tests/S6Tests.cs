using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S6 Unity-free parts of the Subnautica plugin (ROADMAP S6 SN plugin; CONTRACT §2.6, §2.7, §4.2, §5.4 "C4 type 6
 /// SkinSet (S6)", C8, "C7 for S6"): the SkinSet store behind MirrorState.Feed (versions, stale, bad, kind 2, epoch,
 /// EntityRemove, ClearAll), the TakeDamage emit rule and the C8 record build pass (sequence, flags, factors, capture
 /// mode), the hit-point frames (root grid, subgrid, S6.M11, character), the 256-shapes round robin over Wire's
 /// ShapeBudget, the physics-rate hysteresis and motion thresholds, the Freedom gate table, the self-check
 /// bookkeeping (S6.N9), spawn arguments, penetration streaks (B-T1 (1)), synthetic skins, the local loop and the
 /// mutant names.
 /// </summary>
    [TestClass]
    public class S6Tests
    {
        private const ulong Grid = 0x1000000000000001UL;
        private const ulong Sub = 0x1000000000000002UL;

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

        private static SkinSectionData Section(int ix, int boxes, uint kind = SkinSectionKind.Grid)
        {
            var s = new SkinSectionData { SectionId = CollisionSkinMath.SectionId(ix, 0, 0), Centre = new Vec3(ix * 15 + 6.25, 6.25, 6.25), Kind = kind };
            for (int i = 0; i < boxes; i++)
                s.Boxes.Add(SkinBoxData.Aligned(new Vec3(ix * 15 + i * 2.5, 0, 0), new Vec3(1.25, 1.25, 1.25), 0));
            s.ContentHash = CollisionSkinMath.SectionContentHash(s);
            return s;
        }

        private static Quat RandomQuat(Random r)
        {
            var q = new Quat(r.NextDouble() * 2 - 1, r.NextDouble() * 2 - 1, r.NextDouble() * 2 - 1, r.NextDouble() * 2 - 1);
            return q.Normalized().WithNonNegativeW();
        }

        private static SeMatrix RandomRigid(Random r)
        {
            Quat q = RandomQuat(r);
            // rows Right, Up, Backward of a proper rotation (SE rows)
            Vec3 right = q.Rotate(Vec3.UnitX), up = q.Rotate(Vec3.UnitY), back = q.Rotate(Vec3.UnitZ);
            return new SeMatrix(right, up, back, new Vec3(r.NextDouble() * 20 - 10, r.NextDouble() * 5, r.NextDouble() * 20 - 10));
        }

        private static DamageRecord Rec(uint kind, float amount, ushort type, ushort flags)
        {
            var r = default(DamageRecord);
            r.targetKind = kind; r.amount = amount; r.snDamageType = type; r.flags = flags; r.unit = DamageUnit.SnHp;
            return r;
        }

        // ------------------------------------------------------------------------------------- SkinSet store (MirrorState.Feed)

        [TestMethod]
        public void SkinSet_FeedStoresVersionsAndCallsBack()
        {
            var st = new MirrorState();
            var seen = new List<string>();
            st.Skins.Changed = e => seen.Add(e.EntityId.ToString("x") + " v" + e.Version + " s" + e.Sections.Count + " b" + e.Boxes);
            byte[] v1 = SkinCodec.Encode(Grid, 1, new[] { Section(0, 3), Section(1, 2) });
            Assert.AreEqual(C4Outcome.Ignored, Feed(st, C4Type.SkinSet, v1), "the drawn mirror has nothing to do for a skin");
            Assert.AreEqual(1, st.Skins.Skins.Count);
            Assert.AreEqual(1u, st.Skins.Skins[Grid].Version);
            Assert.AreEqual(5, st.Skins.Skins[Grid].Boxes);
            Feed(st, C4Type.SkinSet, v1);
            Assert.AreEqual(1L, st.Skins.Stale, "a version <= the applied one is ignored (skinStale)");
            Feed(st, C4Type.SkinSet, SkinCodec.Encode(Grid, 2, new[] { Section(0, 4) }));
            Assert.AreEqual(2u, st.Skins.Skins[Grid].Version);
            Assert.AreEqual(1, st.Skins.Skins[Grid].Sections.Count);
            CollectionAssert.AreEqual(new[] { "1000000000000001 v1 s2 b5", "1000000000000001 v2 s1 b4" }, seen);
            // the decoder's content hash equals Wire's SectionContentHash of the same section (diff by id + hash on the SN side)
            Assert.AreEqual(CollisionSkinMath.SectionContentHash(Section(0, 4)), st.Skins.Skins[Grid].Sections[0].ContentHash);
            // an empty skin (sectionCount 0) removes every section but keeps the entity's version
            Feed(st, C4Type.SkinSet, SkinCodec.Encode(Grid, 3, new SkinSectionData[0]));
            Assert.AreEqual(0, st.Skins.Skins[Grid].Sections.Count);
            Assert.AreEqual(3u, st.Skins.Skins[Grid].Version);
        }

        [TestMethod]
        public void SkinSet_EntityRemoveAndClearAllDropSkins_VersionRestarts()
        {
            var st = new MirrorState();
            var removed = new List<ulong>();
            int cleared = 0;
            st.Skins.Removed = id => removed.Add(id);
            st.Skins.Cleared = () => cleared++;
            Feed(st, C4Type.SkinSet, SkinCodec.Encode(Grid, 5, new[] { Section(0, 1) }));
            Feed(st, C4Type.SkinSet, SkinCodec.Encode(Sub, 1, new[] { Section(0, 1) }));
            Feed(st, C4Type.EntityRemove, Bytes(new EntityRemove { entityId = Grid }));
            CollectionAssert.AreEqual(new[] { Grid }, removed);
            Assert.IsFalse(st.Skins.Skins.ContainsKey(Grid));
            Feed(st, C4Type.SkinSet, SkinCodec.Encode(Grid, 1, new[] { Section(0, 1) }));
            Assert.AreEqual(1u, st.Skins.Skins[Grid].Version, "after EntityRemove the version restarts at 1");
            Feed(st, C4Type.ClearAll, Bytes(new ClearAll { worldEpoch = 2, linkEpoch = 1 }));
            Assert.AreEqual(0, st.Skins.Skins.Count);
            Assert.AreEqual(1, cleared);
            Feed(st, C4Type.SkinSet, SkinCodec.Encode(Grid, 1, new[] { Section(0, 1) }));
            Assert.AreEqual(1u, st.Skins.Skins[Grid].Version, "after ClearAll the version restarts at 1");
        }

        [TestMethod]
        public void SkinSet_BadKindTwoAndEpochDropped_AreCounted()
        {
            var st = new MirrorState();
            byte[] good = SkinCodec.Encode(Grid, 1, new[] { Section(0, 2) });
            byte[] cut = new byte[good.Length - 4];
            Array.Copy(good, cut, cut.Length);
            Feed(st, C4Type.SkinSet, cut);
            Assert.AreEqual(1L, st.Skins.Bad);
            Assert.AreEqual(0, st.Skins.Skins.Count, "a bad message is dropped whole");
            Assert.IsNotNull(st.Skins.LastError);
            // a kind-2 section is valid but ignored and counted
            Feed(st, C4Type.SkinSet, SkinCodec.Encode(Grid, 1, new[] { Section(0, 2), Section(1, 1, SkinSectionKind.Character) }));
            Assert.AreEqual(1L, st.Skins.KindIgnored);
            Assert.AreEqual(1, st.Skins.Skins[Grid].Sections.Count);
            // a grid whose GridAdd the epoch filter dropped: its skin is dropped too
            st.AcceptEpoch = 5;
            Feed(st, C4Type.GridAdd, Bytes(new GridAdd { entityId = Sub, targetClass = GridClass.LargeGrid, worldEpoch = 4 }));
            Feed(st, C4Type.SkinSet, SkinCodec.Encode(Sub, 1, new[] { Section(0, 1) }));
            Assert.AreEqual(1L, st.Skins.EpochDropped);
            Assert.IsFalse(st.Skins.Skins.ContainsKey(Sub));
        }

        // ------------------------------------------------------------------------------------- emit rule and records

        [TestMethod]
        public void EmitRule_Table()
        {
            Assert.AreEqual(S6Skip.None, S6Emit.Check(true, 30f, 0, true, false, false, false));
            Assert.AreEqual(S6Skip.Gate, S6Emit.Check(false, 30f, 0, true, false, false, false), "the game's own gate failed: nothing is emitted");
            Assert.AreEqual(S6Skip.ZeroAmount, S6Emit.Check(true, 0f, 7, true, false, false, false), "a blast's edge gives 0");
            Assert.AreEqual(S6Skip.ZeroAmount, S6Emit.Check(true, float.NaN, 0, true, false, false, false));
            Assert.AreEqual(S6Skip.ZeroAmount, S6Emit.Check(true, -1f, 0, true, false, false, false));
            Assert.AreEqual(S6Skip.TypeNotForwarded, S6Emit.Check(true, 5f, 4, true, false, false, false), "Collide is not forwarded");
            Assert.AreEqual(S6Skip.TypeNotForwarded, S6Emit.Check(true, 5f, 1, true, false, false, false), "Heat is not forwarded");
            Assert.AreEqual(S6Skip.None, S6Emit.Check(true, 15f, 8, true, false, false, false), "Electrical is forwarded");
            Assert.AreEqual(S6Skip.None, S6Emit.Check(true, 50f, 7, true, false, false, false), "Explosive is forwarded");
            Assert.AreEqual(S6Skip.Refused, S6Emit.Check(true, 30f, 0, false, false, false, false), "the Freedom gate refuses");
            Assert.AreEqual(S6Skip.PlayerNotAtCharacter, S6Emit.Check(true, 30f, 0, true, true, false, false));
            Assert.AreEqual(S6Skip.None, S6Emit.Check(true, 30f, 0, true, true, true, false));
            // capture mode (the self-check) checks the patch path only
            Assert.AreEqual(S6Skip.None, S6Emit.Check(true, 1f, 0, false, true, false, true));
            Assert.AreEqual(S6Skip.Gate, S6Emit.Check(false, 1f, 0, true, true, true, true));
            foreach (S6Skip s in Enum.GetValues(typeof(S6Skip))) Assert.IsFalse(string.IsNullOrEmpty(S6Emit.SkipName(s)));
        }

        [TestMethod]
        public void Flags_PerHitSource()
        {
            Assert.AreEqual((ushort)DamageFlags.MouthContact, S6Emit.Flags(HitVia.Mouth, false, false));
            Assert.AreEqual((ushort)(DamageFlags.Radius | DamageFlags.FallbackHitPoint), S6Emit.Flags(HitVia.Radius, false, false));
            Assert.AreEqual((ushort)DamageFlags.FallbackHitPoint, S6Emit.Flags(HitVia.Shocker, false, false), "the ampeel passes no dealer: fallback hit point (S6.N6)");
            Assert.AreEqual((ushort)(DamageFlags.FallbackHitPoint | DamageFlags.FrameSubgrid), S6Emit.Flags(HitVia.Dealer, true, false));
            Assert.AreEqual((ushort)(DamageFlags.FallbackHitPoint | CombatConventions.C8FlagsExt.GhostPlayer), S6Emit.Flags(HitVia.Player, false, true));
            Assert.AreEqual((ushort)(DamageFlags.MouthContact | DamageFlags.FrameSubgrid), S6Emit.Flags(HitVia.Mouth, true, false));
        }

        [TestMethod]
        public void RecordBuilder_FieldsSequenceAndCapture()
        {
            var b = new DamageRecordBuilder(7);
            var h = new HitInput
            {
                Amount = 80f, Type = 0, Flags = DamageFlags.MouthContact, TargetKind = DamageTargetKind.GridSection, SectionId = 0x20020020,
                TargetClass = GridClass.LargeGrid, TargetEntityId = Grid, RootEntityId = Grid, SnapshotTick = 1234, LocalHit = new Vec3(1, 2, 3),
                WorldHit = new Vec3(10, -20, 30), DealerPoint = new Vec3(10, -20, 26), HasDealerPoint = true, DealerTechType = 2540, DealerInstanceId = 99,
                SnTimeSec = 12.5,
            };
            DamageRecord r1 = b.Build(ref h, 3, false);
            Assert.AreEqual(1u, r1.dedupSeq, "dedupSeq starts at 1 (0 is never sent)");
            Assert.AreEqual(7u, r1.snGeneration);
            Assert.AreEqual(DamageUnit.SnHp, r1.unit);
            Assert.AreEqual(80f, r1.amount);
            Assert.AreEqual(25f, r1.seScaleExpected, "large-grid section: k_large 25");
            Assert.AreEqual((ushort)0, r1.snDamageType);
            Assert.AreEqual(DamageTargetKind.GridSection, r1.targetKind);
            Assert.AreEqual(0x20020020u, r1.sectionId);
            Assert.AreEqual(Grid, r1.targetEntityId);
            Assert.AreEqual(1234UL, r1.snapshotTick);
            Assert.AreEqual(3f, r1.localHit.z);
            Assert.AreEqual(1f, r1.attackDirSn.z, 1e-6, "unit vector from the dealer's point to w");
            Assert.AreEqual(2540, r1.dealerTechType);
            Assert.AreEqual(3u, r1.worldEpoch);
            Assert.AreEqual(12.5, r1.snTimeSec);
            DamageRecord cap = b.Build(ref h, 3, true);
            Assert.AreEqual(0u, cap.dedupSeq, "a captured self-check record takes no sequence number");
            DamageRecord r2 = b.Build(ref h, 3, false);
            Assert.AreEqual(2u, r2.dedupSeq, "no gap after a capture");
            h.TargetClass = GridClass.SmallGrid;
            Assert.AreEqual(1f, b.Build(ref h, 3, false).seScaleExpected, "small grid: k_small 1");
            h.TargetKind = DamageTargetKind.Character;
            DamageRecord c = b.Build(ref h, 3, false);
            Assert.AreEqual(1f, c.seScaleExpected, "character: k_char 1");
            Assert.AreEqual(0u, c.sectionId, "the character section is 0");
            h.HasDealerPoint = false;
            DamageRecord nd = b.Build(ref h, 3, false);
            Assert.AreEqual(0f, nd.attackDirSn.x);
            Assert.AreEqual(0f, nd.attackDirSn.y);
            Assert.AreEqual(0f, nd.attackDirSn.z, "zero when there is no dealer point");
            Assert.AreEqual(5u, b.LastSeq);
            // the SE side's checks pass on what the build pass writes (Wire DamageRules)
            Assert.AreEqual(DamageReject.None, DamageRules.CheckPayload(r1));
            Assert.AreEqual(DamageReject.None, DamageRules.CheckHeader(r1, 7, 3));
            Assert.IsTrue(DamageRules.ScaleMatches(r1.seScaleExpected, DamageRules.FactorForClass(DamageTargetKind.GridSection, GridClass.LargeGrid)));
            Assert.AreEqual(25f, S6Emit.ScaleExpected(DamageTargetKind.GridSection, GridClass.LargeGrid));
        }

        // ------------------------------------------------------------------------------------- hit-point frames (§2.7)

        [TestMethod]
        public void HitFrames_RootGridRoundTrip()
        {
            var rnd = new Random(1);
            for (int i = 0; i < 200; i++)
            {
                var o = new Vec3(rnd.NextDouble() * 2000 - 1000, -rnd.NextDouble() * 1500, rnd.NextDouble() * 2000 - 1000);
                Quat q = RandomQuat(rnd);
                double s = 0.97 + rnd.NextDouble() * 0.04;
                var l = new Vec3(rnd.NextDouble() * 40 - 20, rnd.NextDouble() * 10, rnd.NextDouble() * 40 - 20);
                Vec3 w = WireMath.Apply(o, q, s, l);
                Vec3 back = HitFrames.GridLocal(o, q, s, InstanceMath.Identity, false, w, false);
                Assert.IsTrue(Vec3.Distance(back, l) < 1e-9, "root grid: ToLocal with the step pose inverts Apply");
            }
        }

        [TestMethod]
        public void HitFrames_SubgridFrameAndMutantM11()
        {
            var rnd = new Random(2);
            for (int i = 0; i < 200; i++)
            {
                var o = new Vec3(rnd.NextDouble() * 200 - 100, -rnd.NextDouble() * 500, rnd.NextDouble() * 200 - 100);
                Quat q = RandomQuat(rnd);
                double s = 0.98 + rnd.NextDouble() * 0.02;
                SeMatrix rel = RandomRigid(rnd);
                var lSub = new Vec3(rnd.NextDouble() * 4 - 2, rnd.NextDouble() * 2, rnd.NextDouble() * 4 - 2);
                Vec3 w = CollisionSkinMath.LocalToWorld(o, q, s, rel, true, lSub);
                Vec3 back = HitFrames.GridLocal(o, q, s, rel, true, w, false);
                Assert.IsTrue(Vec3.Distance(back, lSub) < 1e-8, "subgrid: InvertRigid(rel) after ToLocal gives the subgrid frame");
                Vec3 m11 = HitFrames.GridLocal(o, q, s, rel, true, w, true);
                Assert.IsTrue(Vec3.Distance(m11, rel.Transform(lSub)) < 1e-8, "S6.M11 leaves the point in the root grid's frame");
                if (Vec3.Distance(rel.Transform(lSub), lSub) > 0.5) Assert.IsTrue(Vec3.Distance(m11, lSub) > 0.4, "S6.M11 lands on another block");
            }
        }

        [TestMethod]
        public void HitFrames_SectionPlacementMatchesLocalToWorld()
        {
            // the section root at Apply(o, q, s, rel·c_sec) with rotation q·R(rel) and boxes pre-scaled with K of that rotation
 // puts a box centre where the subgrid's affine puts it (CONTRACT §2.6 "Subgrids")
            var rnd = new Random(3);
            for (int i = 0; i < 100; i++)
            {
                var o = new Vec3(rnd.NextDouble() * 200 - 100, -rnd.NextDouble() * 500, rnd.NextDouble() * 200 - 100);
                Quat q = RandomQuat(rnd);
                double s = 0.98 + rnd.NextDouble() * 0.02;
                SeMatrix rel = RandomRigid(rnd);
                var c = new Vec3(rnd.NextDouble() * 10, rnd.NextDouble() * 10, rnd.NextDouble() * 10);
                var b = c + new Vec3(rnd.NextDouble() * 6 - 3, rnd.NextDouble() * 6 - 3, rnd.NextDouble() * 6 - 3);
                Vec3 root = CollisionSkinMath.SectionRootPosition(o, q, s, rel, c);
                Quat rot = CollisionSkinMath.SectionRootRotation(q, rel);
                ProxyBox pb = WireMath.PrescaleBox(rot, s, c, b, new Vec3(1, 1, 1), Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
                Vec3 placed = root + rot.Rotate(pb.Centre);
                Assert.IsTrue(Vec3.Distance(placed, CollisionSkinMath.LocalToWorld(o, q, s, rel, true, b)) < 1e-8);
            }
        }

        [TestMethod]
        public void HitFrames_Character()
        {
            var o = new Vec3(24, -8, 0);
            Quat q = Quat.LookRotation(new Vec3(1, 0, 1).Normalized(), Vec3.UnitY);
            const double s = 0.999;
            var l = new Vec3(0.1, 1.0, -0.2);
            Assert.IsTrue(Vec3.Distance(HitFrames.CharacterLocal(o, q, s, WireMath.Apply(o, q, s, l)), l) < 1e-12);
        }

        // ------------------------------------------------------------------------------------- shape budget (§2.6)

        [TestMethod]
        public void ShapeQueue_1000BoxesTake4Steps()
        {
            var q = new ShapeQueue<string>();
            var slices = new List<string>();
            int done = 0;
            q.Enqueue("A", 1000);
            int steps = 0;
            while (q.Pending > 0 && steps < 10)
            {
                int used = q.Step(CombatConventions.Limits.MaxShapesPerStep, (item, first, count) => slices.Add(item + ":" + first + "+" + count), item => done++);
                Assert.IsTrue(used <= 256);
                steps++;
            }
            Assert.AreEqual(4, steps, "a 1,000-box skin takes 4 steps");
            Assert.AreEqual(1, done);
            CollectionAssert.AreEqual(new[] { "A:0+256", "A:256+256", "A:512+256", "A:768+232" }, slices);
        }

        [TestMethod]
        public void ShapeQueue_ManySectionsShareTheBudget()
        {
            var q = new ShapeQueue<string>();
            var total = new Dictionary<string, int>();
            var done = new List<string>();
            int[] sizes = { 300, 40, 500, 7, 260 };
            for (int i = 0; i < sizes.Length; i++) { q.Enqueue("S" + i, sizes[i]); total["S" + i] = 0; }
            int steps = 0, sum = 0;
            while (q.Pending > 0 && steps < 50)
            {
                int used = q.Step(256, (item, first, count) =>
                {
                    Assert.AreEqual(total[item], first, "slices are contiguous");
                    total[item] += count;
                }, item => done.Add(item));
                Assert.IsTrue(used <= 256, "at most 256 shapes per physics step");
                sum += used;
                steps++;
            }
            Assert.AreEqual(1107, sum);
            Assert.AreEqual(5, done.Count);
            for (int i = 0; i < sizes.Length; i++) Assert.AreEqual(sizes[i], total["S" + i]);
            Assert.AreEqual(5, steps, "1,107 shapes at 256 per step: 5 steps, no idle grant");
        }

        [TestMethod]
        public void ShapeQueue_EnqueueAgainRestartsCancelDrops()
        {
            var q = new ShapeQueue<string>();
            var got = new List<string>();
            q.Enqueue("A", 300);
            q.Step(256, (i, f, c) => got.Add(i + f + "+" + c), null);
            q.Enqueue("A", 300); // a newer pose supersedes the pass
            Assert.AreEqual(1, q.Pending);
            q.Step(256, (i, f, c) => got.Add(i + f + "+" + c), null);
            CollectionAssert.AreEqual(new[] { "A0+256", "A0+256" }, got);
            q.Enqueue("B", 10);
            q.Cancel("A");
            Assert.IsFalse(q.Contains("A"));
            Assert.IsTrue(q.Contains("B"));
            q.Clear();
            Assert.AreEqual(0, q.Pending);
        }

        // ------------------------------------------------------------------------------------- physics rate and motion

        [TestMethod]
        public void PhysicsRate_RegistersAtOnceDeregistersAfter2s()
        {
            var r = new PhysicsRateRule();
            Assert.AreEqual(0, r.Update(false, 0.0));
            Assert.AreEqual(+1, r.Update(true, 1.0), "a moving proxy near the camera registers at once");
            Assert.AreEqual(0, r.Update(true, 1.5));
            Assert.AreEqual(0, r.Update(false, 2.0));
            Assert.AreEqual(0, r.Update(false, 3.4), "hold 2 s");
            Assert.AreEqual(-1, r.Update(false, 3.5), "deregistered 2 s after the last moving proxy");
            Assert.IsFalse(r.Registered);
            Assert.AreEqual(+1, r.Update(true, 4.0));
            Assert.IsTrue(r.ForceOff(), "world unload: always off");
            Assert.IsFalse(r.ForceOff());
            Assert.AreEqual(200f, PhysicsRateRule.NearRadiusM);
        }

        [TestMethod]
        public void MotionRule_Thresholds()
        {
            var p = new Vec3(1, 2, 3);
            Quat q = Quat.Identity;
            Assert.AreEqual(0.25, MotionRule.WindowSeconds, "S6-1: moving for one C4 cadence gap; the 2 s hold is the rate rule's (S6.N7: not stacked)");
            Assert.IsFalse(MotionRule.Moved(p, q, p + new Vec3(0.0009, 0, 0), q));
            Assert.IsTrue(MotionRule.Moved(p, q, p + new Vec3(0.0011, 0, 0), q), "> 1 mm");
            double a = 0.06 * Math.PI / 180 / 2;
            var q2 = new Quat(0, Math.Sin(a), 0, Math.Cos(a));
            Assert.IsTrue(MotionRule.Moved(p, q, p, q2), "> 0.05°");
            double b = 0.04 * Math.PI / 180 / 2;
            Assert.IsFalse(MotionRule.Moved(p, q, p, new Quat(0, Math.Sin(b), 0, Math.Cos(b))));
            Assert.IsTrue(MotionRule.Teleport(p, q, p + new Vec3(2.1, 0, 0), q), "beyond 2 m: teleport");
            Assert.IsFalse(MotionRule.Teleport(p, q, p + new Vec3(1.9, 0, 0), q));
            double c = 31.0 * Math.PI / 180 / 2;
            Assert.IsTrue(MotionRule.Teleport(p, q, p, new Quat(Math.Sin(c), 0, 0, Math.Cos(c))), "beyond 30°: teleport");
        }

        // ------------------------------------------------------------------------------------- Freedom gate (S6.N2)

        [TestMethod]
        public void FreedomGate_Table()
        {
            string line;
            Assert.IsTrue(FreedomRules.Evaluate(GameModeOptionBits.Freedom, 0, false, out line));
            Assert.AreEqual("DE.S6 freedom mode=Freedom invisible=false PASS", line);
            Assert.IsFalse(FreedomRules.Evaluate(GameModeOptionBits.Creative, 0, true, out line), "Creative (NoAggression) refuses");
            StringAssert.Contains(line, "REFUSED");
            Assert.IsFalse(FreedomRules.Evaluate(GameModeOptionBits.Survival, 0, false, out line), "Survival refuses (Freedom required)");
            StringAssert.Contains(line, "mode=Survival");
            Assert.IsFalse(FreedomRules.Evaluate(GameModeOptionBits.Freedom, 0, true, out line), "the invisible cheat refuses");
            Assert.IsFalse(FreedomRules.Evaluate(GameModeOptionBits.Freedom, GameModeOptionBits.NoAggression, false, out line), "NoAggression in the cheats refuses");
            Assert.IsFalse(FreedomRules.Evaluate(GameModeOptionBits.Hardcore, 0, false, out line));
        }

        // ------------------------------------------------------------------------------------- self-check (S6.N9)

        [TestMethod]
        public void SelfCheck_ExactlyOneRecordEach()
        {
            int sec, pl;
            string d;
            var good = new List<DamageRecord> { Rec(1, 1f, 0, 0), Rec(2, 1f, 0, CombatConventions.C8FlagsExt.GhostPlayer) };
            Assert.IsTrue(SelfCheckBook.Judge(good, out sec, out pl, out d), d);
            Assert.AreEqual(1, sec);
            Assert.AreEqual(1, pl);
            Assert.IsFalse(SelfCheckBook.Judge(new List<DamageRecord> { Rec(1, 1f, 0, 0) }, out sec, out pl, out d), "no Player record (S6.M12)");
            StringAssert.Contains(d, "no Player record");
            Assert.IsFalse(SelfCheckBook.Judge(new List<DamageRecord>(), out sec, out pl, out d));
            Assert.IsFalse(SelfCheckBook.Judge(new List<DamageRecord> { Rec(1, 1f, 0, 0), Rec(1, 1f, 0, 0), Rec(2, 1f, 0, 16) }, out sec, out pl, out d), "two section records");
            Assert.IsFalse(SelfCheckBook.Judge(new List<DamageRecord> { Rec(1, 2f, 0, 0), Rec(2, 1f, 0, 16) }, out sec, out pl, out d), "amount 1");
            Assert.IsFalse(SelfCheckBook.Judge(new List<DamageRecord> { Rec(1, 1f, 7, 0), Rec(2, 1f, 0, 16) }, out sec, out pl, out d), "type Normal");
            Assert.IsFalse(SelfCheckBook.Judge(new List<DamageRecord> { Rec(1, 1f, 0, 0), Rec(2, 1f, 0, 0) }, out sec, out pl, out d), "the Player record carries GhostPlayer");
        }

        // ------------------------------------------------------------------------------------- spawn arguments

        [TestMethod]
        public void SpawnArgs_ParseValidateAndCommand()
        {
            var names = new[] { "None", "Peeper", "BoneShark", "ReaperLeviathan", "Shocker", "Crash" };
            string t, e;
            int n, d;
            var a = new Dictionary<string, object> { { "techType", "reaperleviathan" }, { "count", 1.0 }, { "dist", 30.0 } };
            Assert.IsTrue(SpawnArgs.TryParse(a, names, out t, out n, out d, out e), e);
            Assert.AreEqual("ReaperLeviathan", t, "TechType parsed ignoring case");
            Assert.AreEqual("spawn ReaperLeviathan 1 30", SpawnArgs.Command(t, n, d));
            Assert.IsFalse(SpawnArgs.TryParse(new Dictionary<string, object> { { "techType", "Peeper" }, { "count", 21.0 } }, names, out t, out n, out d, out e));
            Assert.IsFalse(SpawnArgs.TryParse(new Dictionary<string, object> { { "techType", "Peeper" }, { "count", 0.0 } }, names, out t, out n, out d, out e));
            Assert.IsFalse(SpawnArgs.TryParse(new Dictionary<string, object> { { "techType", "Peeper" }, { "dist", 101.0 } }, names, out t, out n, out d, out e));
            Assert.IsFalse(SpawnArgs.TryParse(new Dictionary<string, object> { { "techType", "Unicorn" } }, names, out t, out n, out d, out e));
            Assert.IsFalse(SpawnArgs.TryParse(new Dictionary<string, object>(), names, out t, out n, out d, out e));
            Assert.IsTrue(SpawnArgs.TryParse(SpawnArgs.FromArgv(new List<object> { "peeper", "20", "15" }), names, out t, out n, out d, out e), e);
            Assert.AreEqual("spawn Peeper 20 15", SpawnArgs.Command(t, n, d));
            Assert.IsTrue(SpawnArgs.TryParse(SpawnArgs.FromArgv(new List<object> { "Shocker" }), names, out t, out n, out d, out e));
            Assert.AreEqual(1, n, "count default 1");
            Assert.AreEqual(12, d, "dist default 12 (the game's maxDist default)");
        }

        // ------------------------------------------------------------------------------------- B-T1 (1) penetration streaks

        [TestMethod]
        public void PenetrationStreaks_FailOnTheFourthDeepStep()
        {
            var p = new PenetrationStreaks();
            long k = PenetrationStreaks.Key(42, 0x200200u);
            for (int step = 0; step < 3; step++) { p.BeginStep(); p.Note(k, 0.15); p.EndStep(); }
            Assert.IsTrue(p.Pass, "3 consecutive deep steps are allowed");
            p.BeginStep(); p.EndStep(); // no overlap this step: the run ends
            for (int step = 0; step < 3; step++) { p.BeginStep(); p.Note(k, 0.2); p.EndStep(); }
            Assert.IsTrue(p.Pass);
            p.BeginStep(); Assert.AreEqual(4, p.Note(k, 0.11)); p.EndStep();
            Assert.IsFalse(p.Pass, "a 4th consecutive step deeper than 0.1 m fails B-T1 (1)");
            Assert.AreEqual(1L, p.Violations);
            p.BeginStep(); Assert.AreEqual(0, p.Note(k, 0.05)); p.EndStep();
            Assert.AreEqual(4, p.MaxStreakSeen);
            int pairs, streak;
            double depth;
            p.TakeWindow(out pairs, out depth, out streak);
            Assert.AreEqual(0.2, depth, 1e-12);
            p.Reset();
            Assert.IsTrue(p.Pass);
        }

        [TestMethod]
        public void MsWindow_Percentiles()
        {
            var w = new MsWindow(1000);
            for (int i = 1; i <= 100; i++) w.Add(i * 0.01);
            double p50, p95, max;
            int n;
            w.Take(out p50, out p95, out max, out n);
            Assert.AreEqual(100, n);
            Assert.AreEqual(0.50, p50, 1e-12);
            Assert.AreEqual(0.95, p95, 1e-12);
            Assert.AreEqual(1.00, max, 1e-12);
            w.Take(out p50, out p95, out max, out n);
            Assert.AreEqual(0, n, "the window restarts");
            Assert.IsTrue(double.IsNaN(p95));
        }

        // ------------------------------------------------------------------------------------- synthetic skins and the local loop

        [TestMethod]
        public void SyntheticSkins_Bt1And1000Boxes()
        {
            List<SkinSectionData> bt1 = SyntheticSkins.Bt1();
            Assert.AreEqual(2, bt1.Count, "S6.B-T1: 2 sections");
            Assert.AreEqual(50, SyntheticSkins.CountBoxes(bt1), "S6.B-T1: 50 boxes");
            Vec3 mn = new Vec3(1e9, 1e9, 1e9), mx = new Vec3(-1e9, -1e9, -1e9);
            foreach (SkinSectionData s in bt1)
                foreach (SkinBoxData b in s.Boxes)
                {
                    Vec3 lo = b.Centre - b.Half, hi = b.Centre + b.Half;
                    mn = new Vec3(Math.Min(mn.X, lo.X), Math.Min(mn.Y, lo.Y), Math.Min(mn.Z, lo.Z));
                    mx = new Vec3(Math.Max(mx.X, hi.X), Math.Max(mx.Y, hi.Y), Math.Max(mx.Z, hi.Z));
                    Assert.AreEqual(CombatConventions.SkinBoxSource.Synthetic, b.Source);
                    Assert.IsTrue(b.Half.X <= 4 && b.Half.Y <= 4 && b.Half.Z <= 4);
                }
            Assert.AreEqual(20.0, mx.X - mn.X, 1e-9);
            Assert.AreEqual(6.0, mx.Y - mn.Y, 1e-9);
            Assert.AreEqual(6.0, mx.Z - mn.Z, 1e-9);
            string err;
            Assert.IsTrue(SkinCodec.Validate(bt1, out err), err);
            Assert.AreEqual(1000, SyntheticSkins.CountBoxes(SyntheticSkins.Perf(1000)), "B-T1 (3): 1,000 boxes");
            Assert.IsTrue(SkinCodec.Validate(SyntheticSkins.Perf(1000), out err), err);
            // the store takes them like a wire skin
            var store = new SkinStore();
            ulong id;
            Assert.AreEqual(SkinFeed.Applied, store.Apply(9, 1, bt1, out id));
            Assert.AreEqual(50, store.Skins[9].Boxes);
        }

        [TestMethod]
        public void LocalLoop_FiveMetresPerSecondAlongTheHull()
        {
            var c = new Vec3(10, -10, 20);
            Vec3 o0, o1;
            Quat q0, q1;
            LocalLoop.Pose(c, 15, 5, 3.0, out o0, out q0);
            LocalLoop.Pose(c, 15, 5, 3.01, out o1, out q1);
            Assert.AreEqual(5.0, Vec3.Distance(o0, o1) / 0.01, 1e-3, "5 m/s");
            Assert.AreEqual(15.0, Vec3.Distance(o0, c), 1e-9);
            Vec3 tangent = (o1 - o0).Normalized();
            Assert.IsTrue(Math.Abs(Vec3.Dot(q0.Rotate(Vec3.UnitX), tangent)) > 0.999, "the hull's long axis (SE-local x) follows the path");
            Assert.AreEqual(1.0, q0.Rotate(Vec3.UnitY).Y, 1e-12, "level");
        }

        // ------------------------------------------------------------------------------------- switches and mutants

        [TestMethod]
        public void Switches_AndMutantNames()
        {
            Assert.IsFalse(S6Switches.IsOff(null));
            Assert.IsFalse(S6Switches.IsOff("on"));
            Assert.IsTrue(S6Switches.IsOff("off"));
            Assert.IsTrue(S6Switches.IsOff(" OFF "));
            Assert.AreEqual(8, S6Mutants.All.Length);
            foreach (string m in S6Mutants.All) Assert.IsTrue(DevMutants.IsKnown(m), m + " is switchable by de.mutant / DEEPENGINEERS_MUTANT");
            CollectionAssert.AreEquivalent(new[] { "S6.M1", "S6.M2", "S6.M5", "S6.M8", "S6.M9", "S6.M10", "S6.M11", "S6.M12" }, S6Mutants.All);
        }
    }
}
