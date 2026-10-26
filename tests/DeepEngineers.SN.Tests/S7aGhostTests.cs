using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7a placement ghost, Subnautica side, Unity-free parts (ROADMAP S7a "Placement ghost"; CONTRACT §5.4 "S7a
 /// conventions", "Placement ghost"): the ghost's records through the C4 consumer state (an ordinary chunked grid that
 /// keeps its Ghost marker), the marker as the view, S5 rows and S6 read it, the two colours, the material chain and
 /// the explicit override, visibility, and the presented-tick bookkeeping of the s7a.ghost rows.
 /// </summary>
    [TestClass]
    public class S7aGhostTests
    {
        private static readonly float[] Rows = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };

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

        // ------------------------------------------------------------------------------------- C4 consumer state

        [TestMethod]
        public void Records_GhostIsAnOrdinaryChunkedGrid_ThatKeepsItsMarker()
        {
            var s = new MirrorState();
            Assert.AreEqual(C4Outcome.GridAdded, Feed(s, C4Type.GridAdd, Bytes(S7aGhost.MakeGridAdd(GridClass.LargeGrid, 0))));
            Assert.AreEqual(C4Outcome.PartChanged, Feed(s, C4Type.PartAdd, Bytes(S7aGhost.MakeModelPart(0x55UL, new Vec3(0, 0.5, 0)))));
            GridRecord g = s.Grids[S7aGhost.EntityId];
            Assert.IsTrue(GhostRules.IsGhost(g.Id, g.Flags), "id and flag");
            Assert.IsTrue(S7aGhost.IsGhostGrid(0x99UL, g.Flags), "the flag alone marks it (S6 reads the same marker)");
            Assert.AreEqual(0UL, g.ParentId);
            Assert.IsFalse(g.IsSubgrid);
            PartRecord p = g.Parts[1UL];
            Assert.IsTrue(p.Chunked, "class FatBlock without Dynamic: chunked like any S5 fat block (§2.8 posing)");
            Assert.AreEqual(S5Conv.ClassFatBlock, p.Class);
            Assert.AreNotEqual(0u, p.ChunkBits & S5Conv.NoShadows, "NoShadows enters the chunk key");
            Assert.AreEqual(0.5f, p.Local[10]);
            Assert.AreEqual(0L, s.ConventionsRejected);
            Assert.AreEqual(1, s.Grids.Count, "mirroredEntityCount includes the ghost");
        }

        [TestMethod]
        public void Records_CubePartsReplaceTheModel_ClassChangeReindexes_EntityRemoveDropsIt()
        {
            var s = new MirrorState();
            Feed(s, C4Type.GridAdd, Bytes(S7aGhost.MakeGridAdd(GridClass.LargeGrid, 0)));
            for (ulong k = 1; k <= 3; k++) Feed(s, C4Type.PartAdd, Bytes(S7aGhost.MakeCubePart(k, 0x100 + k, Rows)));
            GridRecord g = s.Grids[S7aGhost.EntityId];
            Assert.AreEqual(3, g.Parts.Count);
            foreach (PartRecord p in g.Parts.Values) Assert.IsTrue(p.Chunked);
            Feed(s, C4Type.PartRemove, Bytes(ModelCodec.MakePartRemove(S7aGhost.EntityId, 2)));
            Feed(s, C4Type.PartRemove, Bytes(ModelCodec.MakePartRemove(S7aGhost.EntityId, 3)));
            Feed(s, C4Type.PartAdd, Bytes(S7aGhost.MakeModelPart(0x77UL, new Vec3(0, 0, 0))));
            Assert.AreEqual(1, g.Parts.Count);
            Assert.AreEqual(0x77UL, g.Parts[1UL].ModelKey);
            Assert.AreEqual(20f, g.CellSize);
            Feed(s, C4Type.GridAdd, Bytes(S7aGhost.MakeGridAdd(GridClass.SmallGrid, 0)));
            Assert.AreEqual(4f, s.Grids[S7aGhost.EntityId].CellSize, "a small block: GridAdd again, cells re-indexed, parts kept");
            Assert.AreEqual(1, s.Grids[S7aGhost.EntityId].Parts.Count);
            Assert.AreEqual(C4Outcome.GridRemoved, Feed(s, C4Type.EntityRemove, Bytes(ModelCodec.MakeEntityRemove(S7aGhost.EntityId))));
            Assert.AreEqual(0, s.Grids.Count);
        }

        [TestMethod]
        public void Marker_OrdinaryGridsAndLegacyBitsAreNotTheGhost()
        {
            Assert.IsFalse(GhostRules.IsGhost(0x44455335000001UL, MirrorConventions.GridFlags(false, false, false)));
            Assert.IsFalse(GhostRules.IsGhost(0x44455335000001UL, S7aConventions.GridFlagsExt.Ghost), "legacy nibble 0");
            Assert.IsTrue(GhostRules.IsGhost(S7aGhost.EntityId, 0u));
        }

        // ------------------------------------------------------------------------------------- colour, material, visibility

        [TestMethod]
        public void Colour_GreenWhenAllowed_RedWhenBlocked_Translucent()
        {
            float r, g, b, a;
            GhostRules.Colour(true, out r, out g, out b, out a);
            Assert.AreEqual(0.25f, r); Assert.AreEqual(1.0f, g); Assert.AreEqual(0.35f, b); Assert.AreEqual(0.40f, a);
            GhostRules.Colour(false, out r, out g, out b, out a);
            Assert.AreEqual(1.0f, r); Assert.AreEqual(0.25f, g); Assert.AreEqual(0.20f, b); Assert.AreEqual(0.40f, a);
            Assert.AreEqual(3000, GhostRules.TransparentQueue);
            // the colour follows the newest C2 row's bit 3
            GhostRules.Colour(S7aGhost.Allowed(S7aGhost.C2Flags(true, true)), out r, out g, out b, out a);
            Assert.AreEqual(1.0f, g);
            GhostRules.Colour(S7aGhost.Allowed(S7aGhost.C2Flags(true, false)), out r, out g, out b, out a);
            Assert.AreEqual(1.0f, r);
        }

        [TestMethod]
        public void MaterialChain_GlassThenStandardThenSprite_ExplicitWhenAvailable()
        {
            Assert.AreEqual(GhostMaterialSource.Standard, GhostRules.Choose(GhostMaterialSource.Auto, true, true, true), "owner 2026-10-17: Standard Fade shows the tint, glass did not");
            Assert.AreEqual(GhostMaterialSource.Glass, GhostRules.Choose(GhostMaterialSource.Auto, true, false, true), "no Standard shader: glass");
            Assert.AreEqual(GhostMaterialSource.Standard, GhostRules.Choose(GhostMaterialSource.Auto, false, true, true));
            Assert.AreEqual(GhostMaterialSource.Sprite, GhostRules.Choose(GhostMaterialSource.Auto, false, false, true));
            Assert.AreEqual(GhostMaterialSource.None, GhostRules.Choose(GhostMaterialSource.Auto, false, false, false));
            Assert.AreEqual(GhostMaterialSource.Sprite, GhostRules.Choose(GhostMaterialSource.Sprite, true, true, true), "the owner's override");
            Assert.AreEqual(GhostMaterialSource.Standard, GhostRules.Choose(GhostMaterialSource.Standard, true, true, true));
            Assert.AreEqual(GhostMaterialSource.Glass, GhostRules.Choose(GhostMaterialSource.Standard, true, false, true), "unavailable override: the chain");
            // S7 integrator: the Standard-first order (16:51) left this line on the old Glass-first chain; Auto now
 // leaves glass for Standard, and still upgrades from Sprite once a better source is available
            Assert.IsTrue(GhostRules.ShouldRebuild(GhostMaterialSource.Auto, GhostMaterialSource.Glass, true, true, true), "auto moves to Standard when it is available");
            Assert.IsTrue(GhostRules.ShouldRebuild(GhostMaterialSource.Auto, GhostMaterialSource.Sprite, true, false, true), "auto upgrades to glass once it arrives (no Standard)");
            Assert.IsFalse(GhostRules.ShouldRebuild(GhostMaterialSource.Standard, GhostMaterialSource.Standard, true, true, true));
            GhostMaterialSource src;
            foreach (string n in new[] { "auto", "glass", "standard", "sprite" })
            {
                Assert.IsTrue(GhostRules.TryParseSource(n.ToUpperInvariant(), out src), n);
                Assert.AreEqual(n, GhostRules.Name(src));
            }
            Assert.IsFalse(GhostRules.TryParseSource("marmoset", out src));
            Assert.AreEqual("none", GhostRules.Name(GhostMaterialSource.None));
        }

        [TestMethod]
        public void Visibility_OnlyWhileShownWithAMeshAndNotCulled()
        {
            Assert.IsTrue(GhostRules.Visible(true, true, false));
            Assert.IsFalse(GhostRules.Visible(false, true, false), "SE hides the gizmo cube");
            Assert.IsFalse(GhostRules.Visible(true, false, false), "no mesh yet");
            Assert.IsFalse(GhostRules.Visible(true, true, true), "beyond the S5 cull distance");
            Assert.IsTrue(GhostRules.Visible(S7aGhost.Shown(S7aGhost.C2Flags(true, false)), true, false));
        }

        // ------------------------------------------------------------------------------------- presented ticks

        [TestMethod]
        public void Presented_FirstSampleAtOrPastTheChangeTick_NewerChangeSupersedes()
        {
            var t = new GhostPresentTracker();
            Assert.IsTrue(t.NoteRow(100, 1, 2, 3, 0, 0, 0, 1), "the first row is a change");
            Assert.IsFalse(t.NoteRow(100, 9, 9, 9, 0, 0, 0, 1), "the same snapshot read again");
            Assert.IsFalse(t.OnPresented(99, 101), "an older bracket");
            Assert.IsTrue(t.OnPresented(100, 102));
            Assert.AreEqual(100UL, t.FirstPresentedTick);
            Assert.AreEqual(102UL, t.NewestAtPresent);
            Assert.IsFalse(t.OnPresented(101, 103), "nothing pending");
            Assert.IsFalse(t.NoteRow(101, 1, 2, 3, 0, 0, 0, 1), "the same pose next tick: not a change");
            Assert.IsTrue(t.NoteRow(102, 1, 2, 3.5, 0, 0, 0, 1));
            Assert.IsTrue(t.NoteRow(103, 1, 2, 4, 0, 0, 0, 1), "a newer change before the older was drawn");
            Assert.AreEqual(1L, t.Superseded);
            Assert.AreEqual(103UL, t.PendingTick);
            Assert.IsTrue(t.OnPresented(104, 105));
            Assert.AreEqual(104UL, t.FirstPresentedTick);
            Assert.AreEqual(3L, t.Changes);
            Assert.AreEqual(2L, t.Presented);
            t.Reset();
            Assert.IsTrue(t.NoteRow(200, 1, 2, 4, 0, 0, 0, 1), "after a removal the next row is a change again");
        }

        [TestMethod]
        public void PoseCompare_Float32Exact_AndErrMm()
        {
            Assert.IsFalse(GhostRules.PoseDiffers(1, 2, 3, 0, 0, 0, 1, 1, 2, 3, 0, 0, 0, 1));
            Assert.IsFalse(GhostRules.PoseDiffers(1 + 1e-12, 2, 3, 0, 0, 0, 1, 1, 2, 3, 0, 0, 0, 1), "below float32 resolution");
            Assert.IsTrue(GhostRules.PoseDiffers(1.001, 2, 3, 0, 0, 0, 1, 1, 2, 3, 0, 0, 0, 1));
            Assert.IsTrue(GhostRules.PoseDiffers(1, 2, 3, 0, 0.7071, 0, 0.7071, 1, 2, 3, 0, 0, 0, 1), "a rotation is a change");
            Assert.AreEqual(1.0, GhostRules.ErrMm(0, 0, 0.001, 0, 0, 0), 1e-9);
            Assert.AreEqual(5000.0, GhostRules.ErrMm(3, 4, 0, 0, 0, 0), 1e-9);
        }

        [TestMethod]
        public void RowState_ChangeTest()
        {
            var a = new GhostViewState { Present = true, Shown = true, Allowed = true, Material = "glass", Renderers = 2 };
            var b = a;
            Assert.IsTrue(a.SameAs(b));
            b.Allowed = false;
            Assert.IsFalse(a.SameAs(b), "a colour change writes a row at once");
            b = a;
            b.Material = "standard";
            Assert.IsFalse(a.SameAs(b));
            b = a;
            b.Renderers = 3;
            Assert.IsFalse(a.SameAs(b));
        }
    }
}
