using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DeepEngineers.SE.Combat;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S6 "Collision B + combat", SE plugin, game-independent parts (ROADMAP S6 "SE plugin"; CONTRACT §2.6, §4.1, §5.4
 /// "C4 type 6 SkinSet (S6)", "S6 combat conventions"): the S6 mutants in BuildFlavor's registry (S6.M3 dedup off, S6.M7
 /// factors swapped; the Subnautica and world mutants refused), the per-block skin rule, skin splitting and sectioning of
 /// synthetic large- and small-grid block sets (coverage, lattice, ≤ 3 / 16 cells per axis, ≤ 4 m half extents, OBB
 /// order, the SkinSet round trip), the version stream (resend only on change, restart at 1, previous sections kept),
 /// the gate decision tables, the damage check order of §4.1 on every reject reason, the S6.N1 arithmetic with its
 /// mutants red, the hit point → cell → block mapping (own cell, nearest neighbour, none), the de.s6 test-record
 /// build pass, the de.s6 / de.spawn syntax, the s6.* rows and the DE.Damage line, the reject-row limiter, the SE
 /// self-check, and the link's C8 consumer (EntryRing re-synced with the other consumer tails) in a real section.
 /// </summary>
    [TestClass]
    public unsafe class S6Tests
    {
        public TestContext TestContext { get; set; }

        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); }

        // ================================================================================================== helpers

        private const uint Gen = 3, Epoch = 7;
        private const long Small = 101, Large = 202, Sub = 404, Root = 202, Char = 303;

        private static SentSkin Skin(long id, bool large, bool subgrid, long root, params uint[] sections)
        {
            var s = new SentSkin { EntityId = id, RootId = root, Large = large, Subgrid = subgrid, GridSize = large ? 2.5 : 0.5, Version = 1 };
            foreach (uint x in sections) s.Sections.Add(x);
            return s;
        }

        private static readonly uint Sec0 = CollisionSkinMath.SectionId(0, 0, 0);
        private static readonly uint Sec1 = CollisionSkinMath.SectionId(1, 0, 0);
        private static readonly uint SecOld = CollisionSkinMath.SectionId(-1, 0, 0);

        private static Dictionary<long, SentSkin> Skins()
        {
            var d = new Dictionary<long, SentSkin>
            {
                { Small, Skin(Small, false, false, Small, Sec0) },
                { Large, Skin(Large, true, false, Large, Sec0, Sec1) },
                { Sub, Skin(Sub, false, true, Root, Sec0) },
            };
            d[Large].PreviousSections.Add(SecOld);
            return d;
        }

        private static DamageRecord Rec(uint seq, long target, uint kind = DamageTargetKind.GridSection, uint section = 0, float amount = 2f, long root = -1,
                                        ushort type = 0, ushort flags = DamageFlags.MouthContact, float k = float.NaN)
        {
            var r = new DamageRecord
            {
                dedupSeq = seq, snGeneration = Gen, unit = DamageUnit.SnHp, amount = amount, snDamageType = type, flags = flags,
                targetKind = kind, sectionId = kind == DamageTargetKind.GridSection && section == 0 ? Sec0 : section,
                targetEntityId = (ulong)target, rootEntityId = (ulong)(root >= 0 ? root : target), worldEpoch = Epoch,
                localHit = new F32x3(0.1f, 0.2f, 0.3f),
            };
            r.seScaleExpected = float.IsNaN(k) ? (float)(kind == DamageTargetKind.GridSection && target == Large ? 25.0 : 1.0) : k;
            return r;
        }

        private static DamageVerdict Check(DamageRecord r, DedupWindow w, Dictionary<long, SentSkin> skins = null, bool swap = false)
        {
            skins = skins ?? Skins();
            return DamagePipeline.Check(r, Gen, Epoch, w, id => { SentSkin s; return skins.TryGetValue(id, out s) ? s : null; }, Char, swap);
        }

        private static DedupWindow Window()
        {
            var w = new DedupWindow();
            w.Reset(Gen);
            return w;
        }

        // ================================================================================================== mutants

        [TestMethod]
        public void Mutants_Registry_S6()
        {
            Assert.IsTrue(Mutants.Set("S6.M3", true).StartsWith("S6.M3 ON", StringComparison.Ordinal));
            Assert.IsTrue(S6Mutants.DedupOff);
            Assert.IsTrue(Mutants.Set("s6.m7", true).StartsWith("S6.M7 ON", StringComparison.Ordinal));
            Assert.IsTrue(S6Mutants.SwapGridFactors);
            StringAssert.Contains(Mutants.Active, "S6.M3,S6.M7");
            foreach (string sn in new[] { "S6.M1", "S6.M2", "S6.M5", "S6.M8", "S6.M9", "S6.M10", "S6.M11", "S6.M12", "S6.M4", "S6.M6" })
                StringAssert.StartsWith(Mutants.Set(sn, true), "REFUSE", sn + " is not an SE code switch");
            Mutants.ClearAll();
            Assert.IsFalse(S6Mutants.DedupOff || S6Mutants.SwapGridFactors, "ClearAll switches S6 off");
            StringAssert.StartsWith(Mutants.Set("S6.M99", true), "UNKNOWN");
        }

        // ================================================================================================== skin

        [TestMethod]
        public void SkinRules_BlockClassification()
        {
            Assert.AreEqual(BlockSkinKind.None, SkinRules.Classify(false, true, false, true), "no physics: nothing");
            Assert.AreEqual(BlockSkinKind.Cells, SkinRules.Classify(true, true, true, false), "cube topology: cells whatever the PhysicsOption");
            Assert.AreEqual(BlockSkinKind.Cells, SkinRules.Classify(true, true, false, true), "cube topology with shapes: still cells");
            Assert.AreEqual(BlockSkinKind.None, SkinRules.Classify(true, false, true, true), "mesh topology, PhysicsOption None: nothing");
            Assert.AreEqual(BlockSkinKind.Shapes, SkinRules.Classify(true, false, false, true), "mesh topology with collision shapes: OBBs");
            Assert.AreEqual(BlockSkinKind.Cells, SkinRules.Classify(true, false, false, false), "mesh topology without shapes: cells (AddShapesBox)");
        }

        private static void AssertSkinValid(List<SkinSectionData> secs, double g, bool large, string what)
        {
            int cellsPerSection = large ? 6 : 32, maxCells = large ? 3 : 16;
            uint prev = 0;
            for (int i = 0; i < secs.Count; i++)
            {
                SkinSectionData s = secs[i];
                if (i > 0) Assert.IsTrue(s.SectionId > prev, what + ": ascending section ids");
                prev = s.SectionId;
                Assert.IsTrue(s.Boxes.Count >= 1, what + ": a section has boxes");
                int ix, iy, iz;
                CollisionSkinMath.SectionIndexOf(s.SectionId, out ix, out iy, out iz);
                Vec3 c = CollisionSkinMath.SectionCentre(ix, iy, iz, g, large);
                Assert.IsTrue(Vec3.MaxAbsDiff(c, s.Centre) < 1e-9, what + ": centre is the lattice centre");
                double halfSection = cellsPerSection * g / 2;
                foreach (SkinBoxData b in s.Boxes)
                {
                    Assert.IsTrue(b.Half.X > 0 && b.Half.X <= 4.0 + 1e-9 && b.Half.Y <= 4.0 + 1e-9 && b.Half.Z <= 4.0 + 1e-9, what + ": half extents ≤ 4 m " + b);
                    if (b.Source == CombatConventions.SkinBoxSource.Segment)
                    {
                        Assert.IsTrue(b.Half.X <= maxCells * g / 2 + 1e-9 && b.Half.Y <= maxCells * g / 2 + 1e-9 && b.Half.Z <= maxCells * g / 2 + 1e-9, what + ": ≤ " + maxCells + " cells per axis");
                        Assert.IsTrue(Math.Abs(b.Centre.X - s.Centre.X) + b.Half.X <= halfSection + 1e-9 &&
                                      Math.Abs(b.Centre.Y - s.Centre.Y) + b.Half.Y <= halfSection + 1e-9 &&
                                      Math.Abs(b.Centre.Z - s.Centre.Z) + b.Half.Z <= halfSection + 1e-9, what + ": a cell box lies inside its section cube");
                        Assert.IsTrue(b.HasIdentityAxes && !b.IsRotated, what + ": cell boxes are grid-aligned");
                    }
                }
                Assert.AreEqual(CollisionSkinMath.SectionContentHash(s), s.ContentHash, what + ": content hash set");
            }
        }

        private static int CellsCovered(List<SkinSectionData> secs, double g)
        {
            double v = 0;
            foreach (SkinSectionData s in secs)
                foreach (SkinBoxData b in s.Boxes)
                    if (b.Source == CombatConventions.SkinBoxSource.Segment) v += 8 * b.Half.X * b.Half.Y * b.Half.Z;
            return (int)Math.Round(v / (g * g * g));
        }

        [TestMethod]
        public void SkinBuild_LargeHull_SplitSectionedCovered()
        {
            // a large-grid hull 16 × 3 × 4 cells (40 × 7.5 × 10 m) with a 2 × 1 × 1 notch, plus a mesh block's two collision
 // pieces (one 9 m long rotated piece, split into 2 of half 2.25 m) at block (3, 3, 1)
            const double g = 2.5;
            var b = new SkinBuild(g, true);
            b.AddCells(0, 0, 0, 15, 2, 3);
            b.Cells.RemoveAll(c => c.x >= 5 && c.x <= 6 && c.y == 2 && c.z == 3);
            int cells = b.Cells.Count;
            Assert.AreEqual(16 * 3 * 4 - 2, cells);
            var rot = new SeMatrix(new Vec3(Math.Cos(0.3), 0, -Math.Sin(0.3)), Vec3.UnitY, new Vec3(Math.Sin(0.3), 0, Math.Cos(0.3)), Vec3.Zero);
            b.AddObb(3, 3, 1, 1, CollisionSkinMath.ObbFromModelAabb(new Vec3(-0.5, -0.5, -0.5), new Vec3(0.5, 0.5, 0.5), InstanceMath.Identity, new Vec3(7.5, 7.5, 2.5), 1.0));
            b.AddObb(3, 3, 1, 0, CollisionSkinMath.ObbFromModelAabb(new Vec3(-4.5, -0.25, -0.25), new Vec3(4.5, 0.25, 0.25), rot, new Vec3(7.5, 7.5, 2.5), 1.0));
            Assert.AreEqual(3, b.ObbCount, "the 9 m piece is split into ⌈4.5 / 4⌉ = 2, plus the small one");
            b.SortCells();
            List<CellRange> seg = b.GreedySegments();
            string err;
            Assert.IsTrue(CollisionSkinMath.CheckCoverage(b.Cells, seg, out err), err);
            List<SkinSectionData> secs = b.Finish(seg);
            AssertSkinValid(secs, g, true, "large hull");
            Assert.AreEqual(cells, CellsCovered(secs, g), "every cell covered exactly once (volume)");
            // x 0..15 at 6 cells per section → 3 sections along x; y 0..2 and z 0..3 within section 0 → 3 sections
            Assert.AreEqual(3, secs.Count(s => { int ix, iy, iz; CollisionSkinMath.SectionIndexOf(s.SectionId, out ix, out iy, out iz); return iy == 0 && iz == 0; }));
            List<SkinBoxData> obbs = b.OrderedObbs();
            Assert.AreEqual(3, obbs.Count);
            Assert.IsTrue(obbs[0].IsRotated && obbs[1].IsRotated && !obbs[2].IsRotated, "OBB order: block, shape 0 (its 2 pieces), shape 1");
            Assert.AreEqual(2.25, obbs[0].Half.X, 1e-9, "equal pieces");
            int obbBoxes = secs.Sum(s => s.Boxes.Count(x => x.Source == CombatConventions.SkinBoxSource.ModelShapeObb));
            Assert.AreEqual(3, obbBoxes, "every OBB piece is in a section");

            // the SkinSet round trip
            byte[] p = SkinCodec.Encode(77, 3, secs);
            Assert.AreEqual(SkinCodec.PayloadBytes(secs), p.Length);
            var back = new List<SkinSectionData>();
            SkinSetHeader h;
            Assert.IsTrue(SkinCodec.TryDecode(p, 0, p.Length, out h, back, out err), err);
            Assert.IsTrue(h.entityId == 77 && h.skinVersion == 3 && h.sectionCount == secs.Count);
            for (int i = 0; i < secs.Count; i++) Assert.AreEqual(secs[i].ContentHash, CollisionSkinMath.SectionContentHash(back[i]), "section " + i + " bytes");
        }

        [TestMethod]
        public void SkinBuild_SmallGrid_LongBeam_16CellsAnd32CellSections()
        {
            const double g = 0.5;
            var b = new SkinBuild(g, false);
            b.AddCells(-10, 0, 0, 69, 0, 0);  // 80 cells along x, crossing the section planes at x = 0, 32, 64
            b.SortCells();
            List<CellRange> seg = b.GreedySegments();
            Assert.AreEqual(1, seg.Count, "one run");
            List<SkinSectionData> secs = b.Finish(seg);
            AssertSkinValid(secs, g, false, "small beam");
            Assert.AreEqual(4, secs.Count, "sections ix -1, 0, 1, 2");
            Assert.AreEqual(80, CellsCovered(secs, g));
            int boxes = secs.Sum(s => s.Boxes.Count);
            // pieces per section: -10..-1 (10 cells: 1 box), 0..31 (2 × 16), 32..63 (2 × 16), 64..69 (6 cells: 1 box)
            Assert.AreEqual(6, boxes);
        }

        [TestMethod]
        public void SkinBuild_Deterministic_AndEmptyGrid()
        {
            var a = new SkinBuild(2.5, true);
            var c = new SkinBuild(2.5, true);
            a.AddCells(0, 0, 0, 4, 4, 4);
            // the same cells in another order (SE's GetBlocks HashSet order)
            for (int x = 4; x >= 0; x--) c.AddCells(x, 0, 0, x, 4, 4);
            a.SortCells(); c.SortCells();
            List<SkinSectionData> sa = a.Finish(a.GreedySegments()), sc = c.Finish(c.GreedySegments());
            CollectionAssert.AreEqual(sa.Select(s => s.ContentHash).ToList(), sc.Select(s => s.ContentHash).ToList(), "same skin whatever the block order");
            var e = new SkinBuild(0.5, false);
            Assert.AreEqual(0, e.Finish(e.GreedySegments()).Count, "an empty grid has no section (SkinSet with 0 sections)");
            Assert.AreEqual(16, SkinCodec.PayloadBytes(new List<SkinSectionData>()));
        }

        [TestMethod]
        public void SkinStream_ResendOnlyOnChange_RestartAtOne()
        {
            var b = new SkinBuild(2.5, true);
            b.AddCells(0, 0, 0, 11, 0, 0);
            b.SortCells();
            List<SkinSectionData> v1 = b.Finish(b.GreedySegments());
            var st = new SkinStream();
            Assert.IsTrue(st.Differs(v1), "first send");
            Assert.AreEqual(1u, st.NextVersion);
            st.Commit(v1);
            Assert.AreEqual(1u, st.Version);
            Assert.IsFalse(st.Differs(v1), "unchanged rebuild sends nothing");

            var b2 = new SkinBuild(2.5, true);
            b2.AddCells(0, 0, 0, 10, 0, 0);   // the last cell removed: only section ix 1 changes
            b2.SortCells();
            List<SkinSectionData> v2 = b2.Finish(b2.GreedySegments());
            Assert.IsTrue(st.Differs(v2));
            Assert.AreEqual(1, st.ChangedSections(v2), "one section changed (content hash)");
            HashSet<uint> before = st.Current;
            st.Commit(v2);
            Assert.AreEqual(2u, st.Version);
            Assert.AreSame(before, st.Previous, "the previous sent sections stay known");

            var b3 = new SkinBuild(2.5, true);
            b3.AddCells(0, 0, 0, 5, 0, 0);    // section ix 1 removed, section ix 0 unchanged
            b3.SortCells();
            List<SkinSectionData> v3 = b3.Finish(b3.GreedySegments());
            Assert.AreEqual(1, st.ChangedSections(v3), "a removed section counts");
            st.Commit(v3);
            Assert.IsTrue(st.Previous.Contains(CollisionSkinMath.SectionId(1, 0, 0)) && !st.Current.Contains(CollisionSkinMath.SectionId(1, 0, 0)));

            st.Restart();
            Assert.AreEqual(0u, st.Version);
            Assert.IsTrue(st.Differs(v3), "after a stream restart the same skin is sent again");
            Assert.AreEqual(1u, st.NextVersion, "from version 1");
            Assert.AreEqual(1L, st.Restarts);
        }

        // ================================================================================================== gates, factors

        [TestMethod]
        public void Gates_DecisionTables()
        {
            string why;
            Assert.IsFalse(CombatGate.Blocks(false, true, true, out why)); StringAssert.Contains(why, "DestructibleBlocks off");
            Assert.IsFalse(CombatGate.Blocks(true, false, false, out why)); StringAssert.Contains(why, "grid not destructible");
            Assert.IsTrue(CombatGate.Blocks(true, false, true, out why), "ForceBlockDestructible");
            Assert.IsTrue(CombatGate.Blocks(true, true, false, out why) && why == null);
            Assert.IsFalse(CombatGate.Character(true, false, false, out why)); StringAssert.Contains(why, "Creative");
            Assert.IsFalse(CombatGate.Character(false, false, true, out why)); StringAssert.Contains(why, "CharactersCanDie false");
            Assert.IsFalse(CombatGate.Character(false, true, false, out why)); StringAssert.Contains(why, "CharacterCanDie false");
            Assert.IsTrue(CombatGate.Character(false, true, true, out why) && why == null, "Survival");

            Assert.AreEqual(25.0, CombatGate.AppliedFactor(DamageTargetKind.GridSection, true, false));
            Assert.AreEqual(1.0, CombatGate.AppliedFactor(DamageTargetKind.GridSection, false, false));
            Assert.AreEqual(1.0, CombatGate.AppliedFactor(DamageTargetKind.Character, false, false));
            Assert.AreEqual(1.0, CombatGate.AppliedFactor(DamageTargetKind.GridSection, true, true), "S6.M7: large takes k_small");
            Assert.AreEqual(25.0, CombatGate.AppliedFactor(DamageTargetKind.GridSection, false, true), "S6.M7: small takes k_large");
            Assert.AreEqual(1.0, CombatGate.AppliedFactor(DamageTargetKind.Character, false, true), "S6.M7 leaves k_char");
            Assert.AreEqual("DeepBite", CombatGate.SeType(0));
            Assert.AreEqual("DeepBlast", CombatGate.SeType(7));
            Assert.AreEqual("DeepShock", CombatGate.SeType(8));
            Assert.IsNull(CombatGate.SeType(1));
        }

        // ================================================================================================== check order

        [TestMethod]
        public void Pipeline_EveryRejectInContractOrder()
        {
            var w = Window();
            uint s = 1;
            Func<DamageRecord, DamageReject> run = r => Check(r, w).Reject;
            DamageRecord r0;

            r0 = Rec(s++, Small); r0.snGeneration = 2; Assert.AreEqual(DamageReject.Generation, run(r0));
            r0 = Rec(s++, Small); r0.worldEpoch = 6; Assert.AreEqual(DamageReject.Epoch, run(r0));
            r0 = Rec(0, Small); Assert.AreEqual(DamageReject.Sequence, run(r0));
            r0 = Rec(s, Small); Assert.AreEqual(DamageReject.None, run(r0));
            Assert.AreEqual(DamageReject.Duplicate, run(r0), "the same id again");
            r0 = Rec(s++, Small); r0.amount = 0; Assert.AreEqual(DamageReject.Duplicate, run(r0), "dedup (step 3) before the payload (step 4)");
            r0 = Rec(s++, Small); r0.unit = 2; Assert.AreEqual(DamageReject.Unit, run(r0));
            r0 = Rec(s++, Small, amount: 0f); Assert.AreEqual(DamageReject.Amount, run(r0));
            r0 = Rec(s++, Small, amount: 500.5f); Assert.AreEqual(DamageReject.Amount, run(r0));
            r0 = Rec(s++, Small, amount: 500f); Assert.AreEqual(DamageReject.None, run(r0), "500 SN_HP is the limit, included");
            r0 = Rec(s++, Small, amount: float.NaN); Assert.AreEqual(DamageReject.Amount, run(r0));
            r0 = Rec(s++, Small, amount: -1f); Assert.AreEqual(DamageReject.Amount, run(r0));
            r0 = Rec(s++, Small, kind: 3); Assert.AreEqual(DamageReject.Kind, run(r0));
            r0 = Rec(s++, Small, type: 1); Assert.AreEqual(DamageReject.Type, run(r0));
            r0 = Rec(s++, Small); r0.localHit = new F32x3(float.PositiveInfinity, 0, 0); Assert.AreEqual(DamageReject.HitPoint, run(r0));
            r0 = Rec(s++, 999); Assert.AreEqual(DamageReject.Entity, run(r0), "not mirrored");
            r0 = Rec(s++, Small, root: 5); Assert.AreEqual(DamageReject.Entity, run(r0), "root mismatch");
            r0 = Rec(s++, Sub, root: Sub, flags: DamageFlags.FrameSubgrid); Assert.AreEqual(DamageReject.Entity, run(r0), "a subgrid's root is its GridAdd parent");
            r0 = Rec(s++, Large, section: CollisionSkinMath.SectionId(5, 5, 5)); Assert.AreEqual(DamageReject.Section, run(r0));
            r0 = Rec(s++, Large, section: SecOld); Assert.AreEqual(DamageReject.None, run(r0), "a section of the previous SkinSet is accepted");
            r0 = Rec(s++, Small, flags: DamageFlags.FrameSubgrid); Assert.AreEqual(DamageReject.Frame, run(r0), "frame = subgrid on a root grid");
            r0 = Rec(s++, Sub, root: Root, flags: DamageFlags.MouthContact); Assert.AreEqual(DamageReject.Frame, run(r0), "frame = subgrid missing on a subgrid");
            r0 = Rec(s++, Sub, root: Root, flags: DamageFlags.FrameSubgrid | DamageFlags.MouthContact); Assert.AreEqual(DamageReject.None, run(r0));
            r0 = Rec(s++, 1234, kind: DamageTargetKind.Character); Assert.AreEqual(DamageReject.Entity, run(r0), "not the exported character");
            r0 = Rec(s++, Char, kind: DamageTargetKind.Character, root: 1); Assert.AreEqual(DamageReject.Entity, run(r0), "character root");
            r0 = Rec(s++, Char, kind: DamageTargetKind.Character, section: 1); Assert.AreEqual(DamageReject.Section, run(r0));
            r0 = Rec(s++, Char, kind: DamageTargetKind.Character, flags: DamageFlags.FrameSubgrid); Assert.AreEqual(DamageReject.Frame, run(r0));
            r0 = Rec(s++, Char, kind: DamageTargetKind.Character, flags: (ushort)(CombatConventions.C8FlagsExt.GhostPlayer | DamageFlags.FallbackHitPoint));
            DamageVerdict v = Check(r0, w);
            Assert.IsTrue(v.Reject == DamageReject.None && v.Character && v.K == 1.0 && !v.ScaleMismatch);
            Assert.AreEqual("player", DamagePipeline.Via(r0.flags));

            // no character exported
            var w2 = Window();
            DamageVerdict nc = DamagePipeline.Check(Rec(1, Char, kind: DamageTargetKind.Character), Gen, Epoch, w2, id => null, 0, false);
            Assert.AreEqual(DamageReject.Entity, nc.Reject);

            // factors and the scale check
            v = Check(Rec(s++, Large), w);
            Assert.IsTrue(v.Reject == DamageReject.None && v.K == 25.0 && !v.ScaleMismatch);
            v = Check(Rec(s++, Large, k: 1f), w);
            Assert.IsTrue(v.Reject == DamageReject.None && v.K == 25.0 && v.ScaleMismatch, "SE uses its own factor and counts the mismatch");
            v = Check(Rec(s++, Small), w, swap: true);
            Assert.IsTrue(v.K == 25.0 && v.ScaleMismatch, "S6.M7");
        }

        [TestMethod]
        public void Pipeline_DryRunMarksNothing_DedupWindowEdges()
        {
            var w = Window();
            DamageRecord r = Rec(5, Small);
            Assert.AreEqual(DamageReject.None, Check(r, null).Reject, "dry run");
            Assert.AreEqual(DamageReject.None, Check(r, null).Reject, "dry run again: nothing was marked");
            Assert.AreEqual(DamageReject.Sequence, Check(Rec(0, Small), null).Reject, "seq 0 refused even in a dry run");
            Assert.AreEqual(DamageReject.None, Check(r, w).Reject);
            Assert.AreEqual(DamageReject.Duplicate, Check(r, w).Reject);
            Assert.AreEqual(DamageReject.None, Check(Rec(5 + 8192 + 10, Small), w).Reject, "window slides");
            Assert.AreEqual(DamageReject.TooOld, Check(Rec(3, Small), w).Reject);
            Assert.AreEqual(DamageReject.None, Check(Rec(5 + 8192, Small), w).Reject, "reordered within the window");
            Assert.AreEqual(1L, w.Reordered);
            Assert.IsTrue(w.Gaps >= 1);
        }

        /// <summary>
 /// The S6.N1 arithmetic on the consumer's checks: 30 unique bites per target (small grid, large grid, character), 10
 /// duplicates in total; applied SE amount = Σ unique × k (k_small 1, k_large 25, k_char 1). S6.M3 applies the
 /// duplicates; S6.M7 swaps the grid factors: both red.
 /// </summary>
        [TestMethod]
        public void N1_Arithmetic_GreenAndMutantsRed()
        {
            var rnd = new Random(1);
            var recs = new List<DamageRecord>();
            double sumSmall = 0, sumLarge = 0, sumChar = 0;
            uint seq = 1;
            foreach (long t in new[] { Small, Large, Char })
                for (int i = 0; i < 30; i++)
                {
                    bool ch = t == Char;
                    float a = (float)(ch ? 1 + rnd.NextDouble() : 1 + 2 * rnd.NextDouble());
                    recs.Add(Rec(seq++, t, kind: ch ? DamageTargetKind.Character : DamageTargetKind.GridSection, amount: a, flags: ch ? DamageFlags.MouthContact : DamageFlags.MouthContact));
                    if (t == Small) sumSmall += a; else if (t == Large) sumLarge += a; else sumChar += a;
                }
            for (int i = 0; i < 10; i++) recs.Insert(15 + i * 7, recs[i * 9]); // 10 replays, spread through the stream
            Assert.AreEqual(100, recs.Count);

            Func<bool, bool, double[]> run = (dedupOff, swap) =>
            {
                var w = Window();
                w.Bypass = dedupOff;
                var got = new double[3];
                foreach (DamageRecord r in recs)
                {
                    DamageVerdict v = Check(r, w, swap: swap);
                    if (v.Reject != DamageReject.None) { Assert.AreEqual(DamageReject.Duplicate, v.Reject); continue; }
                    long t = (long)r.targetEntityId;
                    got[t == Small ? 0 : t == Large ? 1 : 2] += r.amount * v.K;
                }
                return got;
            };
            double[] ok = run(false, false);
            Assert.AreEqual(sumSmall * 1.0, ok[0], 1e-3, "small grid: Σ × k_small");
            Assert.AreEqual(sumLarge * 25.0, ok[1], 1e-3, "large grid: Σ × k_large");
            Assert.AreEqual(sumChar * 1.0, ok[2], 1e-3, "character: Σ × k_char");

            double[] m3 = run(true, false);
            Assert.IsTrue(m3[0] + m3[1] / 25 + m3[2] > sumSmall + sumLarge + sumChar + 5, "S6.M3: the duplicates are applied (red)");
            double[] m7 = run(false, true);
            Assert.AreNotEqual(sumSmall, m7[0], 1e-3, "S6.M7 small red");
            Assert.AreNotEqual(sumLarge * 25, m7[1], 1e-3, "S6.M7 large red");
            Assert.AreEqual(sumChar, m7[2], 1e-3, "S6.M7 leaves the character");
        }

        // ================================================================================================== hit point → block

        [TestMethod]
        public void HitPoint_CellAndNearestBlock()
        {
            // a small grid (0.5 m) with blocks at cells x 0..3, y 0, z 0, and a 2×1×1 block covering cells 5..6 (multi-cell)
            var occ = new HashSet<string>();
            for (int x = 0; x <= 3; x++) occ.Add(x + ",0,0");
            occ.Add("5,0,0"); occ.Add("6,0,0");
            Func<I32x3, bool> occupied = c => occ.Contains(c.x + "," + c.y + "," + c.z);
            I32x3 cell;
            bool searched;
            Assert.IsTrue(DamageRules.NearestOccupied(new Vec3(1.1, 0.1, -0.2), 0.5, occupied, out cell, out searched));
            Assert.IsTrue(cell.x == 2 && cell.y == 0 && cell.z == 0 && !searched, "round(1.1 / 0.5) = 2 (the block under the point)");
            Assert.IsTrue(DamageRules.NearestOccupied(new Vec3(0.25, 0, 0), 0.5, occupied, out cell, out searched));
            Assert.AreEqual(1, cell.x, "0.5 rounds away from zero (VRage Vector3I.Round)");
            Assert.IsTrue(DamageRules.NearestOccupied(new Vec3(-0.25, 0, 0), 0.5, occupied, out cell, out searched));
            Assert.IsTrue(cell.x == 0 && searched, "-0.5 rounds to -1 (empty), its nearest occupied neighbour is 0");
            Assert.IsTrue(DamageRules.NearestOccupied(new Vec3(2.1, 0.3, 0), 0.5, occupied, out cell, out searched));
            Assert.IsTrue(cell.x == 5 && cell.y == 0 && searched, "a skin box over-approximation: (4, 1, 0) empty → nearest (5, 0, 0)");
            Assert.IsFalse(DamageRules.NearestOccupied(new Vec3(10, 10, 10), 0.5, occupied, out cell, out searched), "NoBlock");

            // a subgrid hit: the point in the subgrid's frame selects the subgrid's block; the same point left in the root
 // frame (S6.M11) lands elsewhere
            var rel = new SeMatrix(new Vec3(0, 0, -1), Vec3.UnitY, Vec3.UnitX, new Vec3(10, 0, 0)); // subgrid world × root world⁻¹
            Vec3 lSub = new Vec3(1.0, 0, 0);
            Vec3 lRoot = rel.Transform(lSub);
            Vec3 back = InstanceMath.InvertRigid(rel).Transform(lRoot);
            Assert.IsTrue(Vec3.MaxAbsDiff(back, lSub) < 1e-12, "InvertRigid(rel)·l returns the subgrid frame");
            I32x3 inSub = DamageRules.HitCell(lSub, 0.5), inRoot = DamageRules.HitCell(lRoot, 0.5);
            Assert.IsFalse(inSub.x == inRoot.x && inSub.y == inRoot.y && inSub.z == inRoot.z, "the root-frame point selects another cell (S6.M11 red)");
        }

        // ================================================================================================== de.s6 damage records

        [TestMethod]
        public void Inject_DefaultsAndOverrides()
        {
            Dictionary<long, SentSkin> skins = Skins();
            Func<long, SentSkin> skinOf = id => { SentSkin s; return skins.TryGetValue(id, out s) ? s : null; };
            uint next = 40;
            Func<uint> seq = () => next++;
            DamageRecord r;
            string err;

            var spec = (IDictionary<string, object>)MiniJson.Parse("{\"target\":\"202\",\"local\":[1.0,0.5,0.25],\"amount\":3}");
            Assert.IsTrue(S6Inject.TryBuild(spec, Gen, Epoch, seq, skinOf, Char, out r, out err), err);
            Assert.IsTrue(r.dedupSeq == 40 && r.snGeneration == Gen && r.worldEpoch == Epoch && r.unit == 1 && r.amount == 3f && r.snDamageType == 0);
            Assert.IsTrue(r.targetKind == DamageTargetKind.GridSection && (long)r.targetEntityId == Large && (long)r.rootEntityId == Large);
            Assert.AreEqual(Sec0, r.sectionId, "auto section: the lattice section of the point");
            Assert.AreEqual(25f, r.seScaleExpected, "SE's own factor by default");
            Assert.AreEqual(DamageFlags.FallbackHitPoint, r.flags);
            Assert.AreEqual(DamageReject.None, Check(r, Window()).Reject, "a default record passes the checks");

            spec = (IDictionary<string, object>)MiniJson.Parse("{\"target\":\"202\",\"local\":[20.0,0.0,0.0],\"amount\":3}");
            Assert.IsTrue(S6Inject.TryBuild(spec, Gen, Epoch, seq, skinOf, Char, out r, out err), err);
            Assert.AreEqual(Sec1, r.sectionId, "x 20 m = cell 8 → lattice section 1");
            spec = (IDictionary<string, object>)MiniJson.Parse("{\"target\":\"202\",\"local\":[100.0,0.0,0.0],\"amount\":3}");
            Assert.IsTrue(S6Inject.TryBuild(spec, Gen, Epoch, seq, skinOf, Char, out r, out err), err);
            Assert.AreEqual(Sec1, r.sectionId, "outside the skin: the nearest section");

            spec = (IDictionary<string, object>)MiniJson.Parse("{\"target\":\"404\",\"local\":[0,0,0],\"amount\":1,\"type\":\"shock\"}");
            Assert.IsTrue(S6Inject.TryBuild(spec, Gen, Epoch, seq, skinOf, Char, out r, out err), err);
            Assert.IsTrue((long)r.rootEntityId == Root && (r.flags & DamageFlags.FrameSubgrid) != 0 && r.snDamageType == 8, "subgrid: root and frame flag");
            Assert.AreEqual(DamageReject.None, Check(r, Window()).Reject);

            spec = (IDictionary<string, object>)MiniJson.Parse("{\"target\":\"character\",\"local\":[0,0.9,0],\"amount\":1.5}");
            Assert.IsTrue(S6Inject.TryBuild(spec, Gen, Epoch, seq, skinOf, Char, out r, out err), err);
            Assert.IsTrue(r.targetKind == DamageTargetKind.Character && (long)r.targetEntityId == Char && (long)r.rootEntityId == Char && r.sectionId == 0 && r.seScaleExpected == 1f);
            Assert.AreEqual(DamageReject.None, Check(r, Window()).Reject);

            // S6.N10-style overrides keep their bad values
            spec = (IDictionary<string, object>)MiniJson.Parse("{\"target\":\"101\",\"local\":[0,0,0],\"amount\":\"nan\",\"unit\":2,\"kind\":3,\"section\":\"0x00123456\",\"seq\":0,\"gen\":9,\"epoch\":1,\"flags\":8}");
            Assert.IsTrue(S6Inject.TryBuild(spec, Gen, Epoch, seq, skinOf, Char, out r, out err), err);
            Assert.IsTrue(float.IsNaN(r.amount) && r.unit == 2 && r.targetKind == 3 && r.sectionId == 0x123456 && r.dedupSeq == 0 && r.snGeneration == 9 && r.worldEpoch == 1 && r.flags == 8);
            Assert.AreEqual(DamageReject.Generation, Check(r, Window()).Reject);

            Assert.IsFalse(S6Inject.TryBuild((IDictionary<string, object>)MiniJson.Parse("{\"target\":\"101\",\"amount\":1}"), Gen, Epoch, seq, skinOf, Char, out r, out err));
            Assert.IsFalse(S6Inject.TryBuild((IDictionary<string, object>)MiniJson.Parse("{\"target\":\"101\",\"local\":[0,0,0],\"amount\":1,\"type\":\"heat\"}"), Gen, Epoch, seq, skinOf, Char, out r, out err));
            ushort ty;
            Assert.IsTrue(S6Inject.TryType("blast", out ty) && ty == 7);
            Assert.IsTrue(S6Inject.TryType(8.0, out ty) && ty == 8);
            Assert.IsTrue(S6Inject.TryType(null, out ty) && ty == 0);
        }

        // ================================================================================================== syntax

        [TestMethod]
        public void Args_ChatAndSpawn()
        {
            string usage;
            Assert.AreEqual(true, S6Args.ParseChat(new[] { "s6" }, out usage)["status"]);
            Dictionary<string, object> a = S6Args.ParseChat(new[] { "s6", "skin", "123456789012345678", "rows" }, out usage);
            Assert.IsTrue((string)a["skin"] == "123456789012345678" && (bool)a["rows"]);
            a = S6Args.ParseChat(new[] { "s6", "resend", "all" }, out usage);
            Assert.AreEqual("all", a["resend"]);
            a = S6Args.ParseChat(new[] { "s6", "damage", "202", "1", "2.5", "-3", "80", "blast", "apply" }, out usage);
            var d = (Dictionary<string, object>)a["damage"];
            Assert.IsTrue((string)d["target"] == "202" && (double)d["amount"] == 80 && (double)d["type"] == 7 && (bool)a["apply"]);
            CollectionAssert.AreEqual(new object[] { 1.0, 2.5, -3.0 }, (List<object>)d["local"]);
            a = S6Args.ParseChat(new[] { "s6", "damage", "character", "0", "0.9", "0", "30" }, out usage);
            Assert.IsTrue((string)((Dictionary<string, object>)a["damage"])["target"] == "character" && !(bool)a["apply"]);
            Assert.AreEqual("damage", S6Args.ActionOf(a));
            Assert.IsNull(S6Args.ParseChat(new[] { "s6", "damage", "202", "1", "2" }, out usage));
            Assert.AreEqual(S6Args.ChatUsage, usage);
            Assert.IsNull(S6Args.ParseChat(new[] { "s6", "skin", "abc" }, out usage));
            Assert.IsNull(S6Args.ParseChat(new[] { "s6", "bogus" }, out usage));
            Assert.AreEqual("selftest", S6Args.ActionOf(S6Args.ParseChat(new[] { "s6", "selftest" }, out usage)));

            a = S6Args.ParseSpawn(new[] { "spawn", "ReaperLeviathan", "1", "30" }, out usage);
            Assert.IsTrue((string)a["techType"] == "ReaperLeviathan" && (double)a["count"] == 1 && (double)a["dist"] == 30);
            a = S6Args.ParseSpawn(new[] { "spawn", "Peeper" }, out usage);
            Assert.IsTrue((double)a["count"] == 1 && (double)a["dist"] == 12, "defaults n 1, dist 12");
            Assert.IsNull(S6Args.ParseSpawn(new[] { "spawn", "Peeper", "21" }, out usage), "n ≤ 20");
            Assert.IsNull(S6Args.ParseSpawn(new[] { "spawn", "Peeper", "1", "101" }, out usage), "dist ≤ 100");
            Assert.IsNull(S6Args.ParseSpawn(new[] { "spawn", "rm -rf" }, out usage), "a console command cannot be injected");
            Assert.IsNull(S6Args.ParseSpawn(new[] { "spawn", "Peeper;kill" }, out usage));
            Assert.IsNull(S6Args.ParseSpawn(new[] { "spawn" }, out usage));
        }

        // ================================================================================================== rows

        [TestMethod]
        public void Rows_DamageRejectSkin_AndAppliedLine()
        {
            DamageRecord r = Rec(7, Large, amount: 30f, type: 7, flags: (ushort)(DamageFlags.Radius | DamageFlags.FallbackHitPoint));
            var a = new S6Rows.Applied { R = r, K = 25, KExpected = 25, Se = 750, AppliedAmount = 750, SeType = "DeepBlast", BlockDef = "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock",
                                         Block = new I32x3(1, -2, 3), Searched = true, EffBefore = 2500, EffAfter = 1750, Tick = 1234 };
            var row = (IDictionary<string, object>)MiniJson.Parse(S6Rows.Damage("2026-10-17T00:00:00Z", a));
            Assert.AreEqual("s6.damage", row["ev"]);
            Assert.AreEqual("3:7", row["id"]);
            Assert.AreEqual("grid", row["kind"]);
            Assert.AreEqual("202", row["targetStr"]);
            Assert.AreEqual("radius", row["via"]);
            Assert.AreEqual("DeepBlast", row["seType"]);
            Assert.AreEqual(750.0, row["applied"]);
            CollectionAssert.AreEqual(new object[] { 1.0, -2.0, 3.0 }, (List<object>)row["block"]);
            Assert.IsNull(row["hpBefore"], "NaN health fields are null on a grid row");
            Assert.AreEqual(Sec0, (uint)(double)row["section"]);

            string line = S6Rows.AppliedLine(a);
            StringAssert.StartsWith(line, "DE.Damage applied id=3:7 unit=SN_HP k=25 se=750 applied=750 target=202 kind=grid section=" + Sec0 + " block=");
            StringAssert.Contains(line, " block=1,-2,3 type=DeepBlast amount=30");

            var rej = (IDictionary<string, object>)MiniJson.Parse(S6Rows.Reject("t", r, DamageReject.NotDestructible, "world DestructibleBlocks off", true));
            Assert.IsTrue((string)rej["ev"] == "s6.reject" && (string)rej["reason"] == "NotDestructible" && (bool)rej["injected"]);

            var b = new SkinBuild(2.5, true);
            b.AddCells(0, 0, 0, 7, 0, 0);
            b.SortCells();
            List<SkinSectionData> secs = b.Finish(b.GreedySegments());
            var sk = new SentSkin { EntityId = 202, RootId = 202, Large = true, GridSize = 2.5, Version = 4 };
            var skin = (IDictionary<string, object>)MiniJson.Parse(S6Rows.Skin("t", "send", sk, "edit", secs, 8, 1234, 0.25, "ok", 3, true, null));
            Assert.IsTrue((string)skin["ev"] == "s6.skin" && (double)skin["sections"] == 2 && (double)skin["boxes"] == 3 && (double)skin["segmentBoxes"] == 3 && (double)skin["obbBoxes"] == 0);
            Assert.AreEqual(2, ((List<object>)skin["detail"]).Count);
        }

        [TestMethod]
        public void RejectRows_200PerReasonPerMinute()
        {
            var l = new RejectRowLimiter();
            int n = 0;
            for (int i = 0; i < 250; i++) if (l.Allow(DamageReject.Amount, 1000)) n++;
            Assert.AreEqual(200, n);
            Assert.IsTrue(l.Allow(DamageReject.Entity, 1000), "per reason");
            Assert.IsFalse(l.Allow(DamageReject.Amount, 60999));
            Assert.IsTrue(l.Allow(DamageReject.Amount, 61000), "a new minute");
            Assert.AreEqual(51L, l.Suppressed);
        }

        [TestMethod]
        public void SelfTest_Passes()
        {
            string detail;
            Assert.IsTrue(S6SelfTest.Run(out detail), detail);
            StringAssert.Contains(detail, "0xb6ee880712f8253b");
        }

        // ================================================================================================== link C8

 /// <summary>
 /// The link's C8 consumer (S6 hook in Link.cs): records written by a Subnautica-side producer are read in order;
 /// a recovery re-syncs the tail through the EntryRing (which caches its tail), so records written after the
 /// re-sync are read and nothing older is replayed; the head is never written by SE.
 /// </summary>
        [TestMethod]
        public void Link_C8_ReadInOrder_ResyncThroughConsumerObject()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var input = Rig.Input();
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    byte* b = sn.Base;
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    DamageRecord got;
                    Assert.IsFalse(link.TryReadDamage(out got), "not linked yet");
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(LinkState.Up, link.State);
                    var prod = new EntryRing(b, RingIndex.C8SnDamage);
                    for (uint i = 1; i <= 5; i++) Assert.AreEqual(RingWriteStatus.Ok, prod.TryWrite(new DamageRecord { dedupSeq = i, amount = i }));
                    for (uint i = 1; i <= 3; i++)
                    {
                        Assert.IsTrue(link.TryReadDamage(out got));
                        Assert.AreEqual(i, got.dedupSeq, "in order");
                    }
                    // stall and recover: consumer tails re-synced (C8 through its consumer object)
                    rig.Run(link, ref input, 100, hv);
                    rig.Run(link, ref input, 3200, hv, snBeat: false);
                    Assert.AreEqual(PeerStatus.Stalled, link.Peer);
                    rig.Run(link, ref input, 100, hv);
                    Assert.AreEqual(PeerStatus.Alive, link.Peer);
                    Assert.AreEqual(RingControl.ReadHead(b, RingIndex.C8SnDamage), RingControl.ReadTail(b, RingIndex.C8SnDamage), "C8 tail re-synced");
                    Assert.IsFalse(link.TryReadDamage(out got), "records 4, 5 dropped by the re-sync, not replayed");
                    Assert.AreEqual(RingWriteStatus.Ok, prod.TryWrite(new DamageRecord { dedupSeq = 6 }));
                    Assert.IsTrue(link.TryReadDamage(out got) && got.dedupSeq == 6, "a record after the re-sync is read");
                    Assert.IsFalse(link.TryReadDamage(out got));
                    Assert.AreEqual(6UL, RingControl.ReadHead(b, RingIndex.C8SnDamage), "SE never writes the C8 head");
                    Assert.AreEqual(0L, link.DamageRingCorrupt);

                    // impossible counters (a broken producer moved its head behind the tail): counted, tail re-synced to the head
                    RingControl.PublishHead(b, RingIndex.C8SnDamage, 2);
                    Assert.IsFalse(link.TryReadDamage(out got));
                    Assert.AreEqual(1L, link.DamageRingCorrupt);
                    Assert.AreEqual(2UL, RingControl.ReadTail(b, RingIndex.C8SnDamage), "tail re-synced to the head");
                    Assert.IsTrue(rig.Has("DE.Link C8 counters impossible"));
                    RingControl.PublishHead(b, RingIndex.C8SnDamage, 3);
                    Assert.IsTrue(link.TryReadDamage(out got), "reading resumes from the re-synced tail");
                }
            }
        }
    }
}
