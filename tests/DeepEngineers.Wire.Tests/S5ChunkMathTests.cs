using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>S5 chunking (CONTRACT §5.4 "Chunk key and consumer rules") and the S5 budgets / back-pressure decisions.</summary>
    [TestClass]
    public unsafe class S5ChunkMathTests
    {
        /// <summary>The S3 consumer's CellKey.Of, copied (DeepEngineers.SN MirrorWire.cs): float quotient, Math.Floor.</summary>
        private static int S3Cell(float t, float cellSize) { return (int)Math.Floor(t / cellSize); }

        /// <summary>The S3 consumer's ColourRules.Key, copied (DeepEngineers.SN MirrorWire.cs).</summary>
        private static long S3Colour(float h, float s, float v)
        {
            Func<float, float> clamp = x => float.IsNaN(x) ? 0f : (x < -2f ? -2f : (x > 2f ? 2f : x));
            long a = (long)Math.Round(clamp(h) * 10000.0) + 20000;
            long b = (long)Math.Round(clamp(s) * 10000.0) + 20000;
            long c = (long)Math.Round(clamp(v) * 10000.0) + 20000;
            return (a << 32) | (b << 16) | c;
        }

        [TestMethod]
        public void Cell_SizesAndBoundaries()
        {
            Assert.AreEqual(20f, ChunkMath.CellSize(GridClass.LargeGrid));
            Assert.AreEqual(4f, ChunkMath.CellSize(GridClass.SmallGrid));
            Assert.AreEqual(ChunkMath.BlocksPerChunk * ChunkMath.LargeBlockSize, ChunkMath.LargeCellSize);
            Assert.AreEqual(ChunkMath.BlocksPerChunk * ChunkMath.SmallBlockSize, ChunkMath.SmallCellSize);
            Assert.AreEqual(new ChunkCell(0, 0, 0), ChunkMath.CellOf(0f, -0f, 19.99f, GridClass.LargeGrid));
            Assert.AreEqual(new ChunkCell(1, -1, -1), ChunkMath.CellOf(20f, -20f, -0.0001f, GridClass.LargeGrid));
            Assert.AreEqual(new ChunkCell(-1, 0, 1), ChunkMath.CellOf(-4f, 3.9999998f, 4f, GridClass.SmallGrid));
            Assert.AreEqual(new ChunkCell(-5000, 5000, 0), ChunkMath.CellOf(-1e5f, 1e5f, 1.25f, GridClass.LargeGrid));
            Assert.AreEqual(new Vec3(-20, 0, 40), ChunkMath.CellOrigin(new ChunkCell(-1, 0, 2), GridClass.LargeGrid));
            Assert.AreEqual(130f, ChunkMath.EdgeViewDistance(GridClass.LargeGrid));
            Assert.AreEqual(35f, ChunkMath.EdgeViewDistance(GridClass.SmallGrid));
        }

        [TestMethod]
        public void Cell_EqualsTheS3ConsumerRule()
        {
            var rnd = new Random(8);
            for (int i = 0; i < 20000; i++)
            {
                uint cls = (i & 1) == 0 ? GridClass.LargeGrid : GridClass.SmallGrid;
                float size = cls == GridClass.LargeGrid ? 20f : 4f;
                float x = (float)((rnd.NextDouble() - 0.5) * 2000), y = (float)Math.Round((rnd.NextDouble() - 0.5) * 200) * 2.5f, z = (float)((rnd.NextDouble() - 0.5) * 20);
                ChunkCell c = ChunkMath.CellOf(x, y, z, cls);
                Assert.AreEqual(S3Cell(x, size), c.X);
                Assert.AreEqual(S3Cell(y, size), c.Y);
                Assert.AreEqual(S3Cell(z, size), c.Z);
            }
        }

        [TestMethod]
        public void ColourKey_EqualsTheS3ConsumerRule()
        {
            var rnd = new Random(9);
            float[] special = { 0f, -0f, 1f, -1f, 2f, -2f, 3f, -3f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.00005f, 0.00015f, -0.00025f };
            foreach (float a in special)
                foreach (float b in special)
                    Assert.AreEqual(S3Colour(a, b, a), ChunkMath.ColourKey(a, b, a));
            for (int i = 0; i < 20000; i++)
            {
                float h = (float)(rnd.NextDouble() * 5 - 2.5), s = (float)(rnd.NextDouble() * 5 - 2.5), v = (float)(rnd.NextDouble() * 5 - 2.5);
                Assert.AreEqual(S3Colour(h, s, v), ChunkMath.ColourKey(h, s, v));
            }
            Assert.AreEqual(ChunkMath.ColourKey(0.1f, 0.2f, 0.3f), ChunkMath.ColourKey(0.10003f, 0.19998f, 0.30004f), "quantised to 1e-4");
            Assert.AreNotEqual(ChunkMath.ColourKey(0.1f, 0.2f, 0.3f), ChunkMath.ColourKey(0.1001f, 0.2f, 0.3f));
            var p = ModelCodec.MakePartAdd(1, 2, 3, InstanceMath.Identity, new Vec3(0.5, -0.25, 1), 0, 0);
            Assert.AreEqual(ChunkMath.ColourKey(0.5f, -0.25f, 1f), ChunkMath.ColourKey(p));
        }

        [TestMethod]
        public void DrawKind_PerClassAndVersion()
        {
            Assert.AreEqual(PartDrawKind.Chunk, ChunkMath.DrawKind(0), "S3 producer");
            Assert.AreEqual(PartDrawKind.Chunk, ChunkMath.DrawKind(MirrorPartFlags.Dynamic), "legacy ignores S5 bits");
            Assert.AreEqual(PartDrawKind.Chunk, ChunkMath.DrawKind(MirrorPartFlags.Version1), "v1 class 0 = cube part");
            uint[] chunked = { MirrorPartClass.CubePart, MirrorPartClass.Edge, MirrorPartClass.FatBlock, MirrorPartClass.Subpart };
            foreach (uint c in chunked)
            {
                Assert.AreEqual(PartDrawKind.Chunk, ChunkMath.DrawKind(MirrorConventions.PartFlags(c)));
                Assert.AreEqual(PartDrawKind.OwnChild, ChunkMath.DrawKind(MirrorConventions.PartFlags(c, MirrorPartFlags.Dynamic)));
                Assert.IsTrue(ChunkMath.IsChunked(MirrorConventions.PartFlags(c, MirrorPartFlags.NoShadows | MirrorPartFlags.BuildStage)));
            }
            Assert.AreEqual(PartDrawKind.OwnChild, ChunkMath.DrawKind(MirrorConventions.PartFlags(MirrorPartClass.Tool)));
            Assert.AreEqual(PartDrawKind.Floating, ChunkMath.DrawKind(MirrorConventions.PartFlags(MirrorPartClass.FloatingObject)));
            Assert.AreEqual(PartDrawKind.Skinned, ChunkMath.DrawKind(MirrorConventions.PartFlags(MirrorPartClass.CharacterBody)));
            Assert.AreEqual(PartDrawKind.None, ChunkMath.DrawKind(MirrorPartFlags.Version1 | 8));
            Assert.AreEqual(PartDrawKind.None, ChunkMath.DrawKind(2u << 28));
            Assert.AreEqual(0u, ChunkMath.ChunkFlags(MirrorPartFlags.NoShadows), "legacy");
            Assert.AreEqual(MirrorPartFlags.NoShadows | MirrorPartFlags.MetalnessColorable,
                ChunkMath.ChunkFlags(MirrorConventions.PartFlags(MirrorPartClass.Edge, MirrorPartFlags.NoShadows | MirrorPartFlags.MetalnessColorable | MirrorPartFlags.BuildStage)));
        }

        [TestMethod]
        public void ChunkKey_SplitsByFlagsAndColour()
        {
            var m = new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(25, -1, 41));
            PartAdd a = ModelCodec.MakePartAdd(7, 1, 100, m, new Vec3(0.1, 0.2, 0.3), 0, 0, 0, MirrorConventions.PartFlags(MirrorPartClass.CubePart));
            PartAdd b = ModelCodec.MakePartAdd(7, 2, 100, m, new Vec3(0.1, 0.2, 0.3), 0, 0, 0, MirrorConventions.PartFlags(MirrorPartClass.Edge, MirrorPartFlags.NoShadows));
            ChunkKey ka = ChunkMath.KeyOf(7, a, GridClass.LargeGrid, 55);
            ChunkKey kb = ChunkMath.KeyOf(7, b, GridClass.LargeGrid, 55);
            Assert.AreEqual(new ChunkCell(1, -1, 2), ka.Cell);
            Assert.AreNotEqual(ka, kb, "NoShadows splits the chunk");
            Assert.AreEqual(ka, ChunkMath.KeyOf(7, ModelCodec.MakePartAdd(7, 9, 101, m, new Vec3(0.1, 0.2, 0.3), 0, 0, 0, MirrorConventions.PartFlags(MirrorPartClass.FatBlock)), GridClass.LargeGrid, 55));
            Assert.AreNotEqual(ka, ChunkMath.KeyOf(8, a, GridClass.LargeGrid, 55));
            Assert.AreNotEqual(ka, ChunkMath.KeyOf(7, a, GridClass.LargeGrid, 56));
            Assert.AreEqual(new ChunkCell(6, -1, 10), ChunkMath.KeyOf(7, a, GridClass.SmallGrid, 55).Cell);
        }

        [TestMethod]
        public void IndexFormatAndRebuildGate()
        {
            Assert.IsFalse(ChunkMath.NeedsUInt32Indices(0));
            Assert.IsFalse(ChunkMath.NeedsUInt32Indices(65535));
            Assert.IsTrue(ChunkMath.NeedsUInt32Indices(65536));
            Assert.IsTrue(ChunkMath.ShouldRebuild(true, 0));
            Assert.IsFalse(ChunkMath.ShouldRebuild(false, 9));
            Assert.IsTrue(ChunkMath.ShouldRebuild(false, 10));
            Assert.AreEqual(256, ChunkMath.MaxQueuedBuilds);
        }

        [TestMethod]
        public void BackPressure_Classes()
        {
            Assert.AreEqual(C4Priority.Control, MirrorBackPressure.Classify(C4Type.ClearAll, false, 1e9));
            Assert.AreEqual(C4Priority.Control, MirrorBackPressure.Classify(C4Type.EntityRemove, true, 0));
            Assert.AreEqual(C4Priority.Control, MirrorBackPressure.Classify(C4Type.CharacterAdd, true, 0));
            Assert.AreEqual(C4Priority.Control, MirrorBackPressure.Classify(C4Type.GridAdd, false, 500), "re-parent");
            Assert.AreEqual(C4Priority.FirstSend, MirrorBackPressure.Classify(C4Type.GridAdd, true, 5));
            Assert.AreEqual(C4Priority.NearEdit, MirrorBackPressure.Classify(C4Type.PartRemove, false, 50));
            Assert.AreEqual(C4Priority.Edit, MirrorBackPressure.Classify(C4Type.PartRecolor, false, 50.01));
            Assert.AreEqual(C4Priority.FirstSend, MirrorBackPressure.Classify(C4Type.PartAdd, true, 1));
            Assert.AreEqual(C4Priority.Edit, MirrorBackPressure.Classify(C4Type.RoomSet, false, 1));
            Assert.IsTrue(MirrorBackPressure.AdmitC4(C4Priority.Control, 0));
            Assert.IsTrue(MirrorBackPressure.AdmitC4(C4Priority.NearEdit, 1));
            Assert.IsFalse(MirrorBackPressure.AdmitC4(C4Priority.Edit, (1 << 20) - 1));
            Assert.IsTrue(MirrorBackPressure.AdmitC4(C4Priority.FirstSend, 1 << 20));
            Assert.IsFalse(MirrorBackPressure.MayStartModel((8 << 20) - 1));
            Assert.IsTrue(MirrorBackPressure.MayStartModel(8 << 20));
            Assert.IsTrue(MirrorBackPressure.CompareRows(C2RowKind.Subgrid, 900, C2RowKind.DynamicSubpart, 1) < 0);
            Assert.IsTrue(MirrorBackPressure.CompareRows(C2RowKind.Tool, 0, C2RowKind.DynamicSubpart, 900) > 0);
            Assert.IsTrue(MirrorBackPressure.CompareRows(C2RowKind.DynamicSubpart, 3, C2RowKind.DynamicSubpart, 4) < 0);
            Assert.AreEqual(2048, MirrorBackPressure.RowsKept(5000));
            Assert.AreEqual(7, MirrorBackPressure.RowsKept(7));
        }
    }
}
