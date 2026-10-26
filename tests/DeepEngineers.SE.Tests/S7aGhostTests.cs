using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7a placement ghost, SE producer, game-independent parts (ROADMAP S7a "Placement ghost"; CONTRACT §5.4 "S7a
 /// conventions", "Placement ghost"): Wire's ghost records (GridAdd, model part, cube parts, C2 flags, the marker),
 /// the record plan (GridAdd once per activation and on a cube-size change, part replaces and removes, EntityRemove,
 /// stream restart), the latency bookkeeping against C7 presentedSeTick, the seeded failure S7a.M4, the switch, the
 /// de.s7 ghost syntax and the s7a.ghost row.
 /// </summary>
    [TestClass]
    public class S7aGhostTests
    {
        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); }

        private static readonly float[] Rot90 = { 0, 0, -1, 0, 1, 0, 1, 0, 0, 0.5f, 0, -1.25f };

        private static GhostPartSpec Model(ulong key, float offY = 0f)
        {
            return GhostPartSpec.ModelPart(key, "Models\\Cubes\\Large\\Thing.mwm", 1f, 0f, offY, 0f);
        }

        private static List<GhostPartSpec> Parts(params GhostPartSpec[] p) { return new List<GhostPartSpec>(p); }

        private static string Kinds(List<GhostOp> ops)
        {
            var s = new List<string>();
            foreach (GhostOp o in ops) s.Add(o.ToString());
            return string.Join("; ", s.ToArray());
        }

        // ================================================================================================== Wire records

        [TestMethod]
        public void Records_GridAdd_FixedIdGhostFlagParentZeroEpoch()
        {
            Assert.AreEqual(0x0144454700000001UL, S7aGhost.EntityId, "\"DEG\" + 1 (CONTRACT S7a conventions)");
            Assert.AreEqual(S7aConventions.Ghost.EntityId, S7aGhost.EntityId);
            GridAdd a = S7aGhost.MakeGridAdd(GridClass.LargeGrid, 7);
            Assert.AreEqual(S7aGhost.EntityId, a.entityId);
            Assert.AreEqual(GridClass.LargeGrid, a.targetClass);
            Assert.AreEqual(MirrorGridFlags.Version1 | 8u, a.flags, "Version1 | Ghost (bit 3)");
            Assert.AreEqual(0UL, a.parentEntityId);
            Assert.AreEqual(7u, a.worldEpoch);
            Assert.AreEqual(32, ModelCodec.ToBytes(a).Length);
            Assert.AreEqual(GridClass.SmallGrid, S7aGhost.MakeGridAdd(GridClass.SmallGrid, 1).targetClass);
            Assert.AreEqual(GridClass.LargeGrid, S7aGhost.TargetClassOf(true));
            Assert.AreEqual(GridClass.SmallGrid, S7aGhost.TargetClassOf(false));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => S7aGhost.MakeGridAdd(3, 1));
            // the ghost bit is outside S5's grid flags (Static 1, Subgrid 2, FloatingObject 4) and the version nibble
            Assert.AreEqual(0u, S7aConventions.GridFlagsExt.Ghost & (MirrorGridFlags.Static | MirrorGridFlags.Subgrid | MirrorGridFlags.FloatingObject | MirrorPartFlags.VersionMask));
        }

        [TestMethod]
        public void Records_ModelPart_KeyOneFatBlockNoShadowsTranslationOfTheModelOffset()
        {
            PartAdd p = S7aGhost.MakeModelPart(0x1122334455667788UL, new Vec3(0.25, -0.5, 1.0));
            Assert.AreEqual(S7aGhost.EntityId, p.entityId);
            Assert.AreEqual(1UL, p.partKey);
            Assert.AreEqual(0x1122334455667788UL, p.modelKey);
            Assert.AreEqual(MirrorPartFlags.Version1 | MirrorPartClass.FatBlock | MirrorPartFlags.NoShadows, p.flags);
            Assert.AreEqual(MirrorPartClass.FatBlock, MirrorConventions.EffectivePartClass(p.flags));
            float[] want = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.25f, -0.5f, 1.0f };
            for (int i = 0; i < 12; i++) Assert.AreEqual(want[i], p.local[i], "local[" + i + "]");
            Assert.AreEqual(0f, p.colorMaskHSV.x); Assert.AreEqual(0f, p.colorMaskHSV.y); Assert.AreEqual(0f, p.colorMaskHSV.z);
            Assert.AreEqual(0u, p.skinId);
            Assert.AreEqual(0f, p.uvOffset.x); Assert.AreEqual(0f, p.uvOffset.y);
            Assert.AreEqual(100, ModelCodec.ToBytes(p).Length);
        }

        [TestMethod]
        public void Records_CubePart_KeysOneToThirtyTwo_ClassCubePart_RowsAsGiven()
        {
            PartAdd p = S7aGhost.MakeCubePart(5, 0xABUL, Rot90);
            Assert.AreEqual(5UL, p.partKey);
            Assert.AreEqual(0xABUL, p.modelKey);
            Assert.AreEqual(MirrorPartClass.CubePart, MirrorConventions.EffectivePartClass(p.flags));
            Assert.IsTrue(MirrorConventions.HasPartFlag(p.flags, MirrorPartFlags.NoShadows), "the ghost never casts shadows");
            Assert.IsFalse(MirrorConventions.HasPartFlag(p.flags, MirrorPartFlags.Dynamic), "chunked like any cube part");
            for (int i = 0; i < 12; i++) Assert.AreEqual(Rot90[i], p.local[i]);
            Assert.AreEqual(32, S7aGhost.MaxParts);
            S7aGhost.MakeCubePart(32, 1, Rot90);
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => S7aGhost.MakeCubePart(0, 1, Rot90));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => S7aGhost.MakeCubePart(33, 1, Rot90));
            Assert.ThrowsException<ArgumentException>(() => S7aGhost.MakeCubePart(1, 1, new float[11]));
            var bad = (float[])Rot90.Clone();
            bad[4] = float.NaN;
            Assert.ThrowsException<ArgumentException>(() => S7aGhost.MakeCubePart(1, 1, bad));
        }

        [TestMethod]
        public void Records_C2Flags_ShownBit2AllowedBit3_OutsideTheLayoutsEntityFlags()
        {
            Assert.AreEqual(0u, S7aGhost.C2Flags(false, false));
            Assert.AreEqual(4u, S7aGhost.C2Flags(true, false));
            Assert.AreEqual(8u, S7aGhost.C2Flags(false, true));
            Assert.AreEqual(12u, S7aGhost.C2Flags(true, true));
            Assert.IsTrue(S7aGhost.Shown(4u)); Assert.IsFalse(S7aGhost.Allowed(4u));
            Assert.IsTrue(S7aGhost.Allowed(8u)); Assert.IsFalse(S7aGhost.Shown(8u));
            Assert.AreEqual(0u, S7aGhost.C2Flags(true, true) & (SeEntityFlags.Static | SeEntityFlags.InCameraGroup),
                "bits 2-3 never collide with SeEntityFlags (Static, InCameraGroup)");
        }

        [TestMethod]
        public void Marker_IdOrVersion1GhostBit_LegacyBitMeansNothing()
        {
            Assert.IsTrue(S7aGhost.IsGhostGrid(S7aGhost.EntityId, 0u), "the id alone");
            Assert.IsTrue(S7aGhost.IsGhostGrid(S7aGhost.EntityId, S7aGhost.GridFlags));
            Assert.IsTrue(S7aGhost.IsGhostGrid(42UL, MirrorGridFlags.Version1 | S7aConventions.GridFlagsExt.Ghost), "the flag on a version-1 GridAdd");
            Assert.IsFalse(S7aGhost.IsGhostGrid(42UL, S7aConventions.GridFlagsExt.Ghost), "bit 3 of a legacy (nibble 0) GridAdd");
            Assert.IsFalse(S7aGhost.IsGhostGrid(42UL, (2u << 28) | S7aConventions.GridFlagsExt.Ghost), "a rejected nibble");
            Assert.IsFalse(S7aGhost.IsGhostGrid(42UL, MirrorConventions.GridFlags(true, true, false)), "an ordinary S5 grid");
            Assert.IsFalse(S7aGhost.IsGhostGrid(42UL, MirrorConventions.GridFlags(false, false, true)), "a floating object");
        }

        // ================================================================================================== record plan

        [TestMethod]
        public void Plan_FirstActivation_GridAddThenThePart_SteadyTicksSendNothing()
        {
            var plan = new GhostPlan();
            var ops = new List<GhostOp>();
            plan.Active(GridClass.LargeGrid, Parts(Model(0x10)), ops);
            Assert.AreEqual("GridAdd class 1; PartAdd key 1 model 0x0000000000000010", Kinds(ops));
            Assert.IsTrue(plan.Alive);
            Assert.AreEqual(1u, plan.PartsVersion);
            Assert.AreEqual(1, plan.SentParts);
            for (int t = 0; t < 5; t++)
            {
                ops.Clear();
                plan.Active(GridClass.LargeGrid, Parts(Model(0x10)), ops); // the pose moves every tick: C2 only
                Assert.AreEqual(0, ops.Count, "nothing on C4 while the definition stays");
            }
            Assert.AreEqual(1u, plan.PartsVersion);
            Assert.AreEqual(1L, plan.GridAdds);
            Assert.AreEqual(1L, plan.PartAdds);
        }

        [TestMethod]
        public void Plan_DefinitionChange_ReplacesKeyOne_CubeSizeChange_SendsGridAddAgain()
        {
            var plan = new GhostPlan();
            var ops = new List<GhostOp>();
            plan.Active(GridClass.LargeGrid, Parts(Model(0x10)), ops);
            ops.Clear();
            plan.Active(GridClass.LargeGrid, Parts(Model(0x20, 0.5f)), ops);
            Assert.AreEqual("PartAdd key 1 model 0x0000000000000020", Kinds(ops), "same key: a replace, no remove");
            Assert.AreEqual(2u, plan.PartsVersion);
            ops.Clear();
            plan.Active(GridClass.SmallGrid, Parts(Model(0x30)), ops);
            Assert.AreEqual("GridAdd class 2; PartAdd key 1 model 0x0000000000000030", Kinds(ops), "the cube size changed: GridAdd again first");
            Assert.AreEqual(GridClass.SmallGrid, plan.SentClass);
            Assert.AreEqual(0L, plan.EntityRemoves, "a class change is not a removal");
        }

        [TestMethod]
        public void Plan_ArmorCubeParts_ToABlockModel_RemovesFirstThenReplaces()
        {
            var plan = new GhostPlan();
            var ops = new List<GhostOp>();
            var armor = new List<GhostPartSpec>();
            for (ulong k = 1; k <= 3; k++) armor.Add(GhostPartSpec.CubePart(k, 0x100 + k, "Models\\Cubes\\Large\\Armor" + k + ".mwm", 1f, Rot90));
            plan.Active(GridClass.LargeGrid, armor, ops);
            Assert.AreEqual(4, ops.Count, "GridAdd + 3 cube parts");
            Assert.AreEqual(3, plan.SentParts);
            ops.Clear();
            plan.Active(GridClass.LargeGrid, Parts(Model(0x10)), ops);
            Assert.AreEqual("PartRemove key 2; PartRemove key 3; PartAdd key 1 model 0x0000000000000010", Kinds(ops));
            Assert.AreEqual(1, plan.SentParts);
        }

        [TestMethod]
        public void Plan_AtMostThirtyTwoParts()
        {
            var plan = new GhostPlan();
            var ops = new List<GhostOp>();
            var many = new List<GhostPartSpec>();
            for (ulong k = 1; k <= 40; k++) many.Add(GhostPartSpec.CubePart(k, 0x200 + k, "a.mwm", 1f, Rot90));
            plan.Active(GridClass.LargeGrid, many, ops);
            Assert.AreEqual(1 + S7aGhost.MaxParts, ops.Count);
            Assert.AreEqual(S7aGhost.MaxParts, plan.SentParts);
        }

        [TestMethod]
        public void Plan_Inactive_EntityRemoveOnce_ReactivationStartsAtGridAdd()
        {
            var plan = new GhostPlan();
            var ops = new List<GhostOp>();
            plan.Active(GridClass.LargeGrid, Parts(Model(0x10)), ops);
            ops.Clear();
            plan.Inactive(ops);
            Assert.AreEqual("EntityRemove", Kinds(ops), "removed within the tick the builder deactivates");
            Assert.IsFalse(plan.Alive);
            ops.Clear();
            plan.Inactive(ops);
            Assert.AreEqual(0, ops.Count, "only once");
            plan.Active(GridClass.LargeGrid, Parts(Model(0x10)), ops);
            Assert.AreEqual("GridAdd class 1; PartAdd key 1 model 0x0000000000000010", Kinds(ops), "a new activation resends everything");
            Assert.AreEqual(2L, plan.Activations);
        }

        [TestMethod]
        public void Plan_StreamRestart_ResendsFromGridAdd_WithoutAnEntityRemove()
        {
            var plan = new GhostPlan();
            var ops = new List<GhostOp>();
            plan.Active(GridClass.LargeGrid, Parts(Model(0x10)), ops);
            plan.OnStreamRestart(); // ClearAll took it
            ops.Clear();
            plan.Active(GridClass.LargeGrid, Parts(Model(0x10)), ops);
            Assert.AreEqual("GridAdd class 1; PartAdd key 1 model 0x0000000000000010", Kinds(ops));
            Assert.AreEqual(0L, plan.EntityRemoves);
            ops.Clear();
            plan.OnStreamRestart();
            plan.Inactive(ops);
            Assert.AreEqual(0, ops.Count, "nothing to remove after a ClearAll");
        }

        [TestMethod]
        public void Plan_PartSpec_ToRecord_IsWiresBuilder()
        {
            GhostPartSpec m = Model(0x77, 0.75f);
            PartAdd a = m.ToRecord(), b = S7aGhost.MakeModelPart(0x77, new Vec3(0, 0.75, 0));
            CollectionAssert.AreEqual(ModelCodec.ToBytes(b), ModelCodec.ToBytes(a));
            GhostPartSpec c = GhostPartSpec.CubePart(3, 0x99, "x.mwm", 1f, Rot90);
            CollectionAssert.AreEqual(ModelCodec.ToBytes(S7aGhost.MakeCubePart(3, 0x99, Rot90)), ModelCodec.ToBytes(c.ToRecord()));
            Assert.IsTrue(m.SameAs(Model(0x77, 0.75f)));
            Assert.IsFalse(m.SameAs(Model(0x77, 0.5f)), "a moved model offset is a replace");
            Assert.IsFalse(m.SameAs(Model(0x78, 0.75f)));
        }

        // ================================================================================================== latency

        [TestMethod]
        public void Latency_ResolvesAtTheFirstPresentedTickAtOrPastTheChange()
        {
            var l = new GhostLatency();
            var got = new List<long>();
            l.NoteChange(100);
            Assert.AreEqual(0, l.Resolve(101, 99, got), "Subnautica still presents an older tick");
            Assert.AreEqual(0, l.Resolve(101, 0, got), "no C7 yet");
            Assert.AreEqual(1, l.Resolve(102, 100, got));
            Assert.AreEqual(2L, got[0], "SE tick 102 saw it presented: 2 ticks");
            Assert.AreEqual(2L, l.Last);
            l.NoteChange(110);
            l.NoteChange(111);
            l.NoteChange(111); // the same tick again: one pending
            Assert.AreEqual(2, l.Pending);
            got.Clear();
            Assert.AreEqual(2, l.Resolve(113, 111, got), "a presented newer pose covers the older change");
            CollectionAssert.AreEqual(new long[] { 3, 2 }, got);
            Assert.AreEqual(3L, l.Max);
            Assert.AreEqual(3L, l.Samples);
            Assert.AreEqual(3L, l.P95);
            l.NoteChange(120);
            l.Reset();
            Assert.AreEqual(0, l.Pending);
            Assert.AreEqual(0, l.Resolve(200, 200, got));
        }

        [TestMethod]
        public void Latency_PendingIsBounded_P95OverTheWindow()
        {
            var l = new GhostLatency();
            for (ulong t = 1; t <= 100; t++) l.NoteChange(t);
            Assert.AreEqual(GhostLatency.MaxPending, l.Pending);
            Assert.AreEqual(100 - GhostLatency.MaxPending, l.Dropped);
            var x = new GhostLatency();
            for (ulong t = 1; t <= 100; t++)
            {
                x.NoteChange(t * 10);
                x.Resolve(t * 10 + (t <= 95 ? 1UL : 9UL), t * 10, null);
            }
            Assert.AreEqual(1L, x.P95, "95 of 100 samples are 1 tick");
            Assert.AreEqual(9L, x.Max);
            Assert.AreEqual(-1L, new GhostLatency().P95);
        }

        // ================================================================================================== mutant, switch, syntax, row

        [TestMethod]
        public void Mutant_S7aM4_AllowedAlways_OtherS7aIdsBelongToTheirOwners()
        {
            StringAssert.StartsWith(Mutants.Set("S7a.M4", true), "S7a.M4 ON");
            Assert.IsTrue(S7aGhostMutants.AllowedAlways);
            StringAssert.Contains(Mutants.Active, "S7a.M4");
            Mutants.ClearAll();
            Assert.IsFalse(S7aGhostMutants.AllowedAlways);
            StringAssert.StartsWith(S7aGhostMutants.Set("s7a.m4", true), "S7a.M4 ON", "case-insensitive");
            StringAssert.StartsWith(S7aGhostMutants.Set("S7a.M4", false), "S7a.M4 OFF");
            foreach (string id in new[] { "S7a.M1", "S7a.M2", "S7a.M3", "S7a.M5", "S7a.M6" })
                Assert.IsNull(S7aGhostMutants.Set(id, true), id + " is not the ghost's");
            StringAssert.Contains(Mutants.Set("S9.M9", true), "S7a.M4", "the unknown-id line lists S7a.M4");
        }

        [TestMethod]
        public void Switch_AndGhostSyntax()
        {
            Assert.IsTrue(GhostSwitch.Parse(null, true));
            Assert.IsFalse(GhostSwitch.Parse("off", true));
            Assert.IsFalse(GhostSwitch.Parse(" OFF ", true));
            Assert.IsTrue(GhostSwitch.Parse("on", false));
            Assert.IsTrue(GhostSwitch.Parse("maybe", true), "unknown text keeps the default");
            string block, error;
            Assert.AreEqual(GhostArgs.Verb.On, GhostArgs.Parse(Args("ghost", "on"), out block, out error));
            Assert.AreEqual(GhostArgs.Verb.Off, GhostArgs.Parse(Args("ghost", "off"), out block, out error));
            Assert.AreEqual(GhostArgs.Verb.Off, GhostArgs.Parse(Args("ghost", false), out block, out error));
            Assert.AreEqual(GhostArgs.Verb.Stop, GhostArgs.Parse(Args("ghost", "stop"), out block, out error));
            Assert.AreEqual(GhostArgs.Verb.Status, GhostArgs.Parse(Args("ghost", "status"), out block, out error));
            Assert.AreEqual(GhostArgs.Verb.None, GhostArgs.Parse(Args("time", 0.5), out block, out error));
            var probe = Args("ghost", "probe");
            probe["block"] = "CubeBlock/LargeBlockArmorBlock";
            Assert.AreEqual(GhostArgs.Verb.Probe, GhostArgs.Parse(probe, out block, out error));
            Assert.AreEqual("CubeBlock/LargeBlockArmorBlock", block);
            Assert.AreEqual(GhostArgs.Verb.Bad, GhostArgs.Parse(Args("ghost", "probe"), out block, out error));
            StringAssert.Contains(error, "<TypeId>/<SubtypeId>");
            probe["block"] = "CubeBlock/";
            Assert.AreEqual(GhostArgs.Verb.Bad, GhostArgs.Parse(probe, out block, out error));
            Assert.AreEqual(GhostArgs.Verb.Bad, GhostArgs.Parse(Args("ghost", "fly"), out block, out error));
            Assert.AreEqual("MyObjectBuilder_CubeBlock", GhostArgs.FullTypeId("CubeBlock"));
            Assert.AreEqual("MyObjectBuilder_TerminalBlock", GhostArgs.FullTypeId("MyObjectBuilder_TerminalBlock"));
        }

        private static Dictionary<string, object> Args(string k, object v)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal) { { k, v } };
        }

        [TestMethod]
        public void Row_ParsesWithThePoseTheModelAndTheCounters_ChangeDetection()
        {
            var d = new GhostRowData
            {
                Tick = 1234, Enabled = true, Active = true, Shown = true, Allowed = false, Def = "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock",
                Model = "Models\\Cubes\\Large\\Thing.mwm", ModelKey = 0xABCDEF0123456789UL, CubeSize = "Large", Mode = "grid", Parts = 1, PartsVersion = 3,
                SeWorld = new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(10, 20, 30)), HasPose = true,
                SnO = new Vec3(1, 2, 3), SnQ = new Quat(0, 0, 0, 1), SnS = 1, GridAdds = 1, PartAdds = 2, LatencyTicks = 2, LatencyP95 = 3, LatencyMax = 4,
                LatencySamples = 9, PresentedSeTick = 1232,
            };
            string row = GhostRows.Row("2026-10-17T12:00:00.0000000Z", d);
            var o = (Dictionary<string, object>)MiniJson.Parse(row);
            Assert.AreEqual("s7a.ghost", o["ev"]);
            Assert.AreEqual("0x0144454700000001", o["entityId"]);
            Assert.AreEqual("0xabcdef0123456789", o["modelKey"]);
            Assert.AreEqual(true, o["active"]);
            Assert.AreEqual(false, o["allowed"]);
            Assert.AreEqual("grid", o["mode"]);
            var se = (Dictionary<string, object>)o["seWorld"];
            var rows = (List<object>)se["rows"];
            Assert.AreEqual(4, rows.Count, "Right, Up, Backward, Translation");
            Assert.AreEqual(3, ((List<object>)rows[3]).Count);
            Assert.AreEqual(30.0, Convert.ToDouble(((List<object>)rows[3])[2], CultureInfo.InvariantCulture));
            var sn = (Dictionary<string, object>)o["snPose"];
            Assert.AreEqual(4, ((List<object>)sn["q"]).Count);
            var c4 = (Dictionary<string, object>)o["c4"];
            Assert.AreEqual(2.0, Convert.ToDouble(c4["partAdds"], CultureInfo.InvariantCulture));
            var lat = (Dictionary<string, object>)o["latency"];
            Assert.AreEqual(3.0, Convert.ToDouble(lat["p95Ticks"], CultureInfo.InvariantCulture));
            Assert.IsFalse(o.ContainsKey("reason"));

            GhostRowData e = d;
            e.Tick = 1240;
            e.LatencyTicks = 7;
            Assert.IsFalse(GhostRows.Changed(d, e), "tick and latency alone are not a change (10 Hz rows carry them)");
            e.Allowed = true;
            Assert.IsTrue(GhostRows.Changed(d, e));
            var off = new GhostRowData { Tick = 1300, Enabled = true, Reason = "builder off" };
            var p = (Dictionary<string, object>)MiniJson.Parse(GhostRows.Row("2026-10-17T12:00:01.0000000Z", off));
            Assert.AreEqual("builder off", p["reason"]);
            Assert.IsFalse(p.ContainsKey("seWorld"));
            Assert.IsTrue(GhostRows.Changed(d, off));
        }

        [TestMethod]
        public void Row_CarriesTheTrueBuildAllowed_BesideTheC2Flag_SoS7aM4TurnsColourRed()
        {
            // integrator: S7a.M4 forces only the C2 flag (allowed); S7a.N3.colour compares Subnautica's drawn
 // colour with SE's own m_buildAllowed, which the row carries as buildAllowed.
            var d = new GhostRowData { Tick = 50, Enabled = true, Active = true, Shown = true, Allowed = true, BuildAllowed = false, MutantAllowed = true };
            var o = (Dictionary<string, object>)MiniJson.Parse(GhostRows.Row("2026-10-17T12:00:02.0000000Z", d));
            Assert.AreEqual(true, o["allowed"], "the C2 flag as sent (forced by S7a.M4)");
            Assert.AreEqual(false, o["buildAllowed"], "SE's m_buildAllowed");
            Assert.AreEqual(true, o["mutantS7aM4"]);
            GhostRowData e = d;
            e.BuildAllowed = true;
            Assert.IsTrue(GhostRows.Changed(d, e), "a change of m_buildAllowed alone writes a row");
        }
    }
}
