using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// The S3 review fixes of on the Subnautica side's Unity-free code: S3.C2 measured on drawn vertices (so a
 /// dropped mirror turns it red even on a symmetric box), Wire's triangle coverage rule (C1's drawn triangle total),
 /// the tangent-frame census (S3.N1t), the C7 pendingParts leaks (epoch-dropped grids, records lost to a re-sync), the
 /// C1/C2 pair rule, the conversion fidelity check of S3.N1d (S3.M8), the build-options cache and a worker that stops
 /// without blocking the main thread.
 /// </summary>
    [TestClass]
    public class S3ReviewFixTests
    {
        private const ulong MatA = 0xA000000000000001UL;
        private const ulong MatB = 0xB000000000000002UL;

        private static float[] YawRows(double deg, Vec3 t)
        {
            Quat q = MirrorPoseMath.AngleAxis(deg, Vec3.UnitY);
            Vec3 r = q.Rotate(Vec3.UnitX), u = q.Rotate(Vec3.UnitY), b = q.Rotate(Vec3.UnitZ);
            return new[] { (float)r.X, (float)r.Y, (float)r.Z, (float)u.X, (float)u.Y, (float)u.Z, (float)b.X, (float)b.Y, (float)b.Z, (float)t.X, (float)t.Y, (float)t.Z };
        }

        private static PartRecord Part(float[] rows)
        {
            var p = new PartRecord { EntityId = 1, PartKey = 1, ModelKey = 0xC0BE, H = 0, S = 0, V = 0 };
            Array.Copy(rows, p.Local, 12);
            return p;
        }

        private static List<PlacementProbe[]> Probes(CellBuildResult r)
        {
            var l = new List<PlacementProbe[]>();
            foreach (ChunkMeshData c in r.Chunks) l.Add(c.Probes);
            return l;
        }

        // ------------------------------------------------------------------------------------- finding 2: S3.C2

        [TestMethod]
        public void S3C2_DrawnVertexProbes_MatchApply_AndADroppedMirrorIsRedOnASymmetricBox()
        {
            ModelData cube = ProceduralCube.Model(0xC0BE, 1.25f, MatA);
            float[] rows = YawRows(90, new Vec3(2.5, 0, -2.5));
            Quat q = MirrorPoseMath.AngleAxis(30, Vec3.UnitY);
            var pose = new EntityPose { Id = 1, Ox = -1029, Oy = -151.25, Oz = -1116, Qx = q.X, Qy = q.Y, Qz = q.Z, Qw = q.W, S = 1.00257, K = 1 };
            double[] mtx = MirrorPoseMath.HierarchyMatrix(pose);
            Func<Vec3, Vec3> toWorld = u => MirrorPoseMath.Transform(mtx, u);
            SeMatrix pm = SeMatrix.FromRows(rows);

            // the plugin's build: mirror and swap on
            var ok = new CellBuildInput { GridId = 1, Options = new BuildOptions() };
            ok.Parts.Add(new PartBuild { Part = Part(rows), Model = MeshBuilder.Prepare(cube, true, true) });
            CellBuildResult good = MeshBuilder.BuildCell(ok);
            var seLocal = new Vec3[8];
            var world = new Vec3[8];
            Assert.IsTrue(MeshBuilder.ProbeCorners(Probes(good), new List<Func<Vec3, Vec3>> { toWorld }, seLocal, world));
            for (int i = 0; i < 8; i++)
            {
                Assert.IsTrue((world[i] - MirrorPoseMath.ApplySeLocal(pose, seLocal[i])).Length < 1e-4, "drawn corner " + i + " = Apply(o, q, s, l)");
                // the probe is the extreme vertex along corner direction i, by the independent SE-local position
                Vec3 sg = MeshBuilder.CornerSign(i);
                for (int j = 0; j < cube.VertexCount; j++)
                {
                    Vec3 l = MeshBuilder.SeLocalOf(rows, cube.Pos, j);
                    Assert.IsTrue(Vec3.Dot(l, sg) <= Vec3.Dot(seLocal[i], sg) + 1e-6, "no vertex beyond the probe along corner " + i);
                }
                // and its SE-local position is the part matrix applied to a model corner (SeMatrix, Wire's own transform)
                bool found = false;
                for (int k = 0; k < 8 && !found; k++)
                    found = (pm.Transform(MeshConventions.AabbCorner(new Vec3(-1.25, -1.25, -1.25), new Vec3(1.25, 1.25, 1.25), k)) - seLocal[i]).Length < 1e-5;
                Assert.IsTrue(found, "probe " + i + " is a corner of the part's box");
            }
            // for a box the 8 probes are its corners, rotated with the part
            var lo = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
            var hi = new Vec3(double.MinValue, double.MinValue, double.MinValue);
            foreach (Vec3 l in seLocal) { lo = new Vec3(Math.Min(lo.X, l.X), Math.Min(lo.Y, l.Y), Math.Min(lo.Z, l.Z)); hi = new Vec3(Math.Max(hi.X, l.X), Math.Max(hi.Y, l.Y), Math.Max(hi.Z, l.Z)); }
            Assert.IsTrue((lo - new Vec3(1.25, -1.25, -3.75)).Length < 1e-5 && (hi - new Vec3(3.75, 1.25, -1.25)).Length < 1e-5, "probe box = the part's box " + lo + " " + hi);

            // S3.M1: the Z mirror dropped (model and part composition): the drawn bounds are the same box (what the old
 // bounds-based line measured, so it saw nothing), but the drawn vertices are not where Apply puts them
            var m1 = new CellBuildInput { GridId = 1, Options = new BuildOptions { MirrorZ = false } };
            m1.Parts.Add(new PartBuild { Part = Part(YawRows(0, Vec3.Zero)), Model = MeshBuilder.Prepare(cube, false, true) });
            var m1ok = new CellBuildInput { GridId = 1, Options = new BuildOptions() };
            m1ok.Parts.Add(new PartBuild { Part = Part(YawRows(0, Vec3.Zero)), Model = MeshBuilder.Prepare(cube, true, true) });
            ChunkMeshData a = MeshBuilder.BuildCell(m1).Chunks[0], b = MeshBuilder.BuildCell(m1ok).Chunks[0];
            Assert.IsTrue(a.MinZ == b.MinZ && a.MaxZ == b.MaxZ && a.MinX == b.MinX && a.MaxY == b.MaxY, "a symmetric box has the same drawn bounds with and without the mirror");
            Assert.IsTrue(MeshBuilder.ProbeCorners(new List<PlacementProbe[]> { a.Probes }, new List<Func<Vec3, Vec3>> { toWorld }, seLocal, world));
            double worst = 0;
            for (int i = 0; i < 8; i++) worst = Math.Max(worst, (world[i] - MirrorPoseMath.ApplySeLocal(pose, seLocal[i])).Length);
            Assert.IsTrue(worst > 2.4, "S3.M1: the drawn corners are 2.5 m off (" + worst + " m): S3.C2 red");
        }

        // ------------------------------------------------------------------------------------- finding 7: coverage

        [TestMethod]
        public void S3C1_EachTriangleDrawnOnce_OverlapFirstWins_UncoveredToMaterialZero_AsWire()
        {
            ModelData cube = ProceduralCube.Model(0xC0BE, 1.25f, MatA);
            // submeshes: A over triangles 0..7, B over 6..9 (6 and 7 overlap A), 10 and 11 in no range
            cube.Submeshes = new[] { new SubmeshInfo { MaterialKey = MatA, TriStart = 0, TriCount = 8 }, new SubmeshInfo { MaterialKey = MatB, TriStart = 6, TriCount = 4 } };
            cube.SubmeshCount = 2;
            int unc, ovl;
            List<KeyValuePair<ulong, List<int>>> groups = MeshBuilder.TriangleGroups(cube.Submeshes, 12, out unc, out ovl);
            Assert.AreEqual(2, unc);
            Assert.AreEqual(2, ovl);
            var count = new Dictionary<ulong, int>();
            foreach (KeyValuePair<ulong, List<int>> g in groups) count[g.Key] = g.Value.Count;
            Assert.AreEqual(8, count[MatA]); Assert.AreEqual(2, count[MatB]); Assert.AreEqual(2, count[0UL]);

            var input = new CellBuildInput { GridId = 1, Options = new BuildOptions() };
            input.Parts.Add(new PartBuild { Part = Part(YawRows(0, Vec3.Zero)), Model = MeshBuilder.Prepare(cube, true, true) });
            CellBuildResult r = MeshBuilder.BuildCell(input);
            Assert.AreEqual(12, r.Triangles, "every model triangle drawn exactly once (C1: drawn = model triangles)");
            Assert.AreEqual(12, r.ModelTriangles);
            Assert.AreEqual(2, r.UncoveredTriangles);
            Assert.AreEqual(2, r.OverlapTriangles);

            // Wire's UnityMeshBuilder groups the same triangles the same way
            ModelGeometry g2 = ModelGeometry.Allocate(cube.VertexCount, cube.IndexCount, 2, ModelFlags.HasTangents);
            Array.Copy(cube.Pos, g2.Positions, cube.Pos.Length); Array.Copy(cube.Nrm, g2.Normals, cube.Nrm.Length);
            Array.Copy(cube.Tan, g2.Tangents, cube.Tan.Length); Array.Copy(cube.Bin, g2.Binormals, cube.Bin.Length); Array.Copy(cube.Uv, g2.Uvs, cube.Uv.Length);
            for (int i = 0; i < cube.IndexCount; i++) g2.Indices[i] = (uint)cube.Indices[i];
            g2.Submeshes[0] = new SubmeshRange(MatA, 0, 8, SeDrawTechnique.Mesh);
            g2.Submeshes[1] = new SubmeshRange(MatB, 6, 4, SeDrawTechnique.Mesh);
            g2.PatternScale = 1f;
            g2.ComputeBounds();
            var wb = new UnityMeshBuilder();
            wb.AddPart(g2, SeMatrix.FromRows(YawRows(0, Vec3.Zero)), 0, 0, false);
            UnityMeshData w = wb.Build();
            Assert.AreEqual(2, w.UncoveredTriangles);
            var wcount = new Dictionary<ulong, int>();
            foreach (UnitySubmesh s in w.Submeshes) wcount[s.MaterialKey] = s.IndexCount / 3;
            foreach (ChunkMeshData c in r.Chunks)
                Assert.AreEqual(wcount[c.Key.MaterialKey], c.TriangleCount, "material " + c.Key.MaterialKey.ToString("x") + ": SN chunk = Wire submesh");
            Assert.AreEqual(wcount.Count, r.Chunks.Count);
        }

        // ------------------------------------------------------------------------------------- finding 3: N1t census

        [TestMethod]
        public void S3N1t_TangentCensus_AlongUv_AndAFlippedBinormalIsSeen()
        {
            ModelData cube = ProceduralCube.Model(0xC0BE, 1.25f, MatA);
            TangentCensus c = TangentCensus.Measure(cube.Pos, cube.Tan, cube.Bin, cube.Uv, cube.Indices);
            Assert.AreEqual(12, c.Measured);
            Assert.AreEqual(1.0, c.TangentFraction, 1e-12, "tangent = +dP/du");
            Assert.AreEqual(1.0, c.BinormalFraction, 1e-12, "binormal = +dP/dv");
            PreparedModel pm = MeshBuilder.Prepare(cube, true, true);
            Assert.AreEqual(12, pm.Tangents.Measured, "Prepare logs it (DE.Mirror model ... tangentU= binormalV=)");
            StringAssert.Contains(pm.Describe(), "binormalV=100%");
            var flipped = (float[])cube.Bin.Clone();
            for (int i = 0; i < flipped.Length; i++) flipped[i] = -flipped[i];
            TangentCensus f = TangentCensus.Measure(cube.Pos, cube.Tan, flipped, cube.Uv, cube.Indices);
            Assert.AreEqual(0.0, f.BinormalFraction, 1e-12, "a binormal along -v is seen");
            TangentCensus none = TangentCensus.Measure(cube.Pos, new float[cube.Tan.Length], new float[cube.Bin.Length], cube.Uv, cube.Indices);
            Assert.AreEqual(0, none.Measured, "no stored frame (hasTangents clear): nothing measured");
        }

        // ------------------------------------------------------------------------------------- findings 5 and 19: C7

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

        private static PartAdd PartAddRec(ulong entity, ulong key)
        {
            var p = new PartAdd { entityId = entity, partKey = key, modelKey = 0xC0BE };
            float[] rows = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
            for (int i = 0; i < 12; i++) p.local[i] = rows[i];
            return p;
        }

        [TestMethod]
        public void S3C7_PartsOfAnEpochDroppedGrid_AreDropped_AndALossIsReportedUntilClearAll()
        {
            var s = new MirrorState { AcceptEpoch = 3 };
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.GridAdd, Bytes(new GridAdd { entityId = 200, targetClass = GridClass.LargeGrid, worldEpoch = 2 })));
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.PartAdd, Bytes(PartAddRec(200, 1))), "a part of a dropped grid is dropped, not orphaned");
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.PartRecolor, Bytes(new PartRecolor { entityId = 200, partKey = 1 })));
            Assert.AreEqual(C4Outcome.Ignored, Feed(s, C4Type.PartRemove, Bytes(new PartRemove { entityId = 200, partKey = 1 })));
            Assert.AreEqual(0, s.Orphans.Count, "nothing pending forever (review finding 19)");
            Assert.AreEqual(4, s.EpochDropped);
            // a grid of the right epoch with the same id later is accepted again
            Assert.AreEqual(C4Outcome.GridAdded, Feed(s, C4Type.GridAdd, Bytes(new GridAdd { entityId = 200, targetClass = GridClass.LargeGrid, worldEpoch = 3 })));
            Assert.AreEqual(C4Outcome.PartChanged, Feed(s, C4Type.PartAdd, Bytes(PartAddRec(200, 1))));
            Assert.AreEqual(1, s.Grids[200].Parts.Count);
            // a real orphan (part before its grid) is still kept
            Feed(s, C4Type.PartAdd, Bytes(PartAddRec(300, 1)));
            Assert.AreEqual(1, s.Orphans.Count);

            Assert.IsFalse(s.LostSinceClearAll);
            s.NoteLoss();
            Assert.IsTrue(s.LostSinceClearAll, "a corrupt-framing re-sync: the mirror may be incomplete (C7 pendingParts + 1)");
            Assert.AreEqual(C4Outcome.ClearAll, Feed(s, C4Type.ClearAll, Bytes(new ClearAll { worldEpoch = 3, linkEpoch = 1 })));
            Assert.IsFalse(s.LostSinceClearAll, "SE's ClearAll and resend replace what was lost");
            Assert.AreEqual(1, s.Losses);
        }

        // ------------------------------------------------------------------------------------- finding 10: C1/C2 pair

        [TestMethod]
        public unsafe void S3C2Pair_RereadsWhenBehindTheCameraTick_CountsIncoherent()
        {
            var buf = new byte[SeEntitiesLimits.EntitiesOffset + SeEntitiesLimits.EntitySize];
            fixed (byte* b = buf)
            {
                var r = new EntityTableReader(b);
                SeEntitiesHeader* hdr = (SeEntitiesHeader*)b;
                SeEntity* e = (SeEntity*)(b + SeEntitiesLimits.EntitiesOffset);
                hdr->entityCount = 1; hdr->worldEpoch = 1; hdr->tick = 76;
                e[0].entityId = 9; e[0].q = new F32x4(0, 0, 0, 1); e[0].s = 1; e[0].k = 1;
                hdr->seq = 2;
                Assert.IsTrue(r.PollPair(76), "C2 at the camera's tick: coherent");
                Assert.AreEqual(0, r.Incoherent);
                Assert.IsFalse(r.PollPair(77), "C1 is at 77, C2 still at 76 after the re-reads");
                Assert.AreEqual(1, r.Incoherent);
                Assert.AreEqual(EntityTableReader.PairRereads_Max, (int)r.PairRereads, "re-read 4 times (CONTRACT §5.1 C2)");
                hdr->tick = 77; hdr->seq = 4;
                Assert.IsTrue(r.PollPair(77), "SE wrote C2(77): coherent");
                Assert.AreEqual(77UL, r.History.Newest.Tick);
            }
        }

        // ------------------------------------------------------------------------------------- finding 3: N1d

        [TestMethod]
        public void S3N1d_Fidelity_LinearTargetWithinOneLevel_SrgbTargetFails()
        {
            var rnd = new Random(7);
            int n = 64 * 64 * 4;
            var f = new float[n];
            var lin = new byte[n];
            var srgb = new byte[n];
            for (int i = 0; i < n; i++)
            {
                f[i] = (float)rnd.NextDouble();
                lin[i] = TextureRepack.ToByte(f[i]);                                   // an 8-bit UNorm target stores the value
                srgb[i] = TextureRepack.ToByte(TextureRepack.LinearToSrgb(f[i]));      // S3.M8: an sRGB target encodes it
            }
            double max, mean;
            int texels;
            Assert.IsTrue(TextureRepack.FidelityError(lin, f, out max, out mean, out texels));
            Assert.AreEqual(64 * 64, texels);
            Assert.IsTrue(max <= TextureRepack.FidelityMaxError, "UNorm target: " + max);
            Assert.IsTrue(TextureRepack.FidelityError(srgb, f, out max, out mean, out texels));
            Assert.IsTrue(max > 0.2, "S3.M8 (sRGB target): " + max + " > 1/255: S3.N1d red whatever the sun does");
            Assert.IsFalse(TextureRepack.FidelityError(lin, new float[3], out max, out mean, out texels), "size mismatch refused");
            // the flat normal 0.5 lands at +0.47 tilt through an sRGB target: the monotonic encode N1b cannot see
            double flat = 2 * TextureRepack.LinearToSrgb(0.5) - 1;
            Assert.IsTrue(flat > 0.45 && flat < 0.49);
        }

        // ------------------------------------------------------------------------------------- finding 21, 16

        [TestMethod]
        public void S3Options_Build_IsCachedUntilAnOptionOrAMutantChanges()
        {
            var o = new S3Options();
            var none = new S3MutantSet();
            BuildOptions a = o.Build(none), b = o.Build(none);
            Assert.AreSame(a, b, "no allocation per frame");
            BuildOptions c = o.Build(new S3MutantSet { M1 = true });
            Assert.AreNotSame(a, c);
            Assert.IsFalse(c.MirrorZ);
            o.VFlip = true;
            Assert.IsTrue(o.Build(new S3MutantSet { M1 = true }).VFlip);
        }

        [TestMethod]
        public void S3Worker_Dispose_ReturnsWithoutWaitingForARunningJob()
        {
            var w = new MirrorWorker();
            var started = new System.Threading.ManualResetEventSlim(false);
            w.Post("slow", () => { started.Set(); System.Threading.Thread.Sleep(600); return "late"; });
            Assert.IsTrue(started.Wait(5000));
            var sw = Stopwatch.StartNew();
            w.Dispose();
            Assert.IsTrue(sw.ElapsedMilliseconds < 100, "CONTRACT §7: no blocking beyond the 100 ms quit poll (" + sw.ElapsedMilliseconds + " ms)");
            Assert.IsTrue(w.Stopped);
            w.Post("after", () => "x");
            System.Threading.Thread.Sleep(800);
            object o;
            Assert.IsFalse(w.TryTake(out o), "the late result and later jobs are discarded");
        }
    }
}
