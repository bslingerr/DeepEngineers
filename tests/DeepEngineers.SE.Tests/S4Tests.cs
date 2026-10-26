using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S4 "Planet + terrain", SE plugin, game-independent parts (ROADMAP S4 "SE plugin", S4.N3 provider unit tests):
 /// the VRage-free provider core (<see cref="ProviderCore"/>: request flags, detectOnly never touching the target,
 /// EmptyData/FullContent only for proven-uniform ranges, writes only into existing arrays at the right strides,
 /// Intersect never Disjoint/Contains on a non-uniform range, the line test over open water, the inert and closed
 /// provider, idempotent Close, no allocation over 10,000 reads), the deterministic ids and the lattice, the terrain
 /// gate's decision table (CONTRACT §1.2), the fail-safe footprint, the guard counters and their once-per-second log
 /// limit under threads, the S4 mutants (M2, M3, M8, M8b, M9 hit exactly their targets; N3 turns red under M8b), the
 /// <c>/de terrain</c> chat syntax and the A-T1 ray plan. The fields are test fields with octree-like semantics (far
 /// samples saturated, conservative classification) over the real Terrain core's TileSampler.
 /// </summary>
    [TestClass]
    public class S4Tests
    {
        [TestCleanup]
        public void Cleanup()
        {
            Mutants.ClearAll();
            TerrainFailSafe.ResetSession();
            TerrainGuards.ResetCounts();
        }

        // ================================================================================================= fixtures

 /// <summary>Planet centre of the site-v1 conformance vectors (CONTRACT §3).</summary>
        private static readonly SiteMap Map = SiteMap.SiteV1Test;
        /// <summary>Tile (2, 2, 1): lateral min −2 m, radial min 59,084 m; it holds the anchor's water column (SN y ≈ −916 … +108).</summary>
        private const int TI = 2, TJ = 2, TK = 1;
        private static Vec3 TileMin { get { return TerrainLattice.TileMin(Map.C, TI, TJ, TK); } }
        private static readonly MaterialBands Bands = new MaterialBands(new byte[] { 10, 11, 12, 13, 14, 15, 16 });

        /// <summary>
 /// A plane at SN y = <see cref="Plane"/> with octree-like semantics: D = Plane − y within 2 m of the plane ("near"),
 /// saturated ±1 with allFarSameSign beyond; ClassifySn proves Empty/Full only for boxes whose 1 m neighbourhood is
 /// entirely far.
 /// </summary>
        private sealed class TruncatedPlane : ISnField
        {
            public readonly double Plane;
            public TruncatedPlane(double plane) { Plane = plane; }

            public float Sample(double x, double y, double z, out bool allFarSameSign)
            {
                double d = Plane - y;
                if (d > 2) { allFarSameSign = true; return 1f; }
                if (d < -2) { allFarSameSign = true; return -1f; }
                allFarSameSign = false;
                return (float)d;
            }

            public ContentClass ClassifySn(double loX, double loY, double loZ, double hiX, double hiY, double hiZ)
            {
                if (loY - 1 > Plane + 2) return ContentClass.Empty;
                if (hiY + 1 < Plane - 2) return ContentClass.Full;
                return ContentClass.Mixed;
            }
        }

        private sealed class Uniform : ISnField
        {
            private readonly bool _solid;
            public Uniform(bool solid) { _solid = solid; }
            public float Sample(double x, double y, double z, out bool allFarSameSign) { allFarSameSign = true; return _solid ? 1f : -1f; }
            public ContentClass ClassifySn(double loX, double loY, double loZ, double hiX, double hiY, double hiZ) { return _solid ? ContentClass.Full : ContentClass.Empty; }
        }

        private const double Plane = -17.2;

        private static ProviderCore Core(ISnField f, Action release = null)
        {
            return new ProviderCore(new TileSampler(f, Map, TileMin, Bands), TI, TJ, TK, SiteV1.TileEntityId(TI, TJ, TK), release);
        }

        /// <summary>Tile-local z (LOD-0) of SN height <paramref name="snY"/> on the anchor's column (local x = y = 2).</summary>
        private static double LocalZ(double snY)
        {
            return Map.SnToSe(new Vec3(0, snY, 0)).Z - TileMin.Z;
        }

        private static int SurfaceZ { get { return (int)Math.Round(LocalZ(Plane)); } }

        // ======================================================================================= ids and lattice

        [TestMethod]
        public void Ids_AreDeterministic_AndMatchTheContract()
        {
            Assert.AreEqual(91274119533887488L, SiteV1.TileEntityId(0, 0, 0));
            Assert.AreEqual(0x0144455400010211L, SiteV1.TileEntityId(1, 1, 2));
            int[][] tiles = SiteV1.TilesCreated();
            Assert.AreEqual(TerrainLattice.TileCount, tiles.Length);
            var ids = new HashSet<long>(tiles.Select(t => SiteV1.TileEntityId(t[0], t[1], t[2])));
            Assert.AreEqual(34, ids.Count, "34 distinct ids");
            foreach (long id in ids) Assert.AreEqual(1L, id >> 56, "ConstructId(ENTITY = 1, …)");
            Assert.AreEqual("DE.Terrain.1.2.0.s1", TerrainLattice.StorageName(1, 2, 0));
            Assert.AreEqual("0x0144455400010211", TerrainLattice.IdHex(0x0144455400010211L));
            Assert.AreEqual(TerrainGate.SnProviderId, SnProviderIdConst);
        }

        /// <summary>The provider id of CONTRACT §3.1 (pinned here so a change shows).</summary>
        private const int SnProviderIdConst = 45460;

        [TestMethod]
        public void Lattice_MinCorners_WorldMatrix_AndTheS4M2Shift()
        {
            Vec3 c = SiteV1.TestC;
            Vec3 m = TerrainLattice.TileMin(c, 3, 0, 2);
            Assert.AreEqual(c.X + 1018, m.X, 0);
            Assert.AreEqual(c.Y - 2042, m.Y, 0);
            Assert.AreEqual(c.Z + 60104, m.Z, 0);
            Vec3 t = TerrainLattice.WorldTranslation(TerrainLattice.PlaceMin(m, false));
            Assert.AreEqual(m.X + 512, t.X, 0);
            Assert.AreEqual(m.Y + 512, t.Y, 0);
            Assert.AreEqual(m.Z + 512, t.Z, 0);
            Vec3 shifted = TerrainLattice.PlaceMin(m, true);
            Assert.AreEqual(m.X, shifted.X, 0);
            Assert.AreEqual(m.Y, shifted.Y, 0);
            Assert.AreEqual(m.Z + 2, shifted.Z, 0, "S4.M2: +2 m along U0 = world +Z");
            Assert.IsTrue(TerrainLattice.Inside(m, m));
            Assert.IsFalse(TerrainLattice.Inside(m, m + new Vec3(1024, 0, 0)));
        }

        [TestMethod]
        public void MutantCell_HoldsTheSafeShallowsPoint()
        {
            int x0, y0, z0;
            Assert.IsTrue(TerrainLattice.MutantCell(Map, TileMin, out x0, out y0, out z0));
            Assert.AreEqual(0, x0 % 8);
            Assert.AreEqual(0, y0 % 8);
            Assert.AreEqual(0, z0 % 8);
            Vec3 l = Map.SnToSe(TerrainLattice.MutantCellSn) - TileMin;
            Assert.IsTrue(l.X >= x0 && l.X < x0 + 8 && l.Y >= y0 && l.Y < y0 + 8 && l.Z >= z0 && l.Z < z0 + 8, "the point lies in its cell");
            Assert.IsFalse(TerrainLattice.MutantCell(Map, TerrainLattice.TileMin(Map.C, 0, 0, 0), out x0, out y0, out z0), "tile (0,0,0) does not hold it");
        }

        [TestMethod]
        public void ThrowawayRefusal_OnlyWithAutosaveOff()
        {
            Assert.IsNull(TerrainLattice.ThrowawayRefusal("S4.M4", 0));
            StringAssert.StartsWith(TerrainLattice.ThrowawayRefusal("S4.M10", 5), "REFUSE S4.M10");
        }

        // ============================================================================================ detectOnly

        [TestMethod]
        public void DetectOnly_NeverTouchesTheTarget_AndKeepsTheInputBits()
        {
            ProviderCore core = Core(new TruncatedPlane(Plane));
            int z = SurfaceZ;
            var c = Enumerable.Repeat((byte)0xA5, 4096).ToArray();
            var m = Enumerable.Repeat((byte)0x5A, 4096).ToArray();
            int input = ProviderCore.RfSurfaceMaterial | ProviderCore.RfForPhysics | ProviderCore.RfEmptyData | ProviderCore.RfFullContent |
                        ProviderCore.RfContentChecked | ProviderCore.RfOneMaterial | ProviderCore.RfUseNativeProvider;
            foreach (int lod in new[] { 0, 1, 2, 10 })
            {
                int lo = lod == 10 ? 0 : (z >> lod) - 2, hi = lod == 10 ? 0 : (z >> lod) + 2;
                int flags = ProviderCore.Read(core, lod, lo, lo, lo, hi, hi, hi, ProviderCore.DtContent | ProviderCore.DtMaterial, input, c, m, 0, 16, 256, true);
                Assert.IsTrue(c.All(b => b == 0xA5) && m.All(b => b == 0x5A), "detectOnly wrote into the target at lod " + lod);
                Assert.AreEqual(input & ~ProviderCore.RfOutputMask, flags & ~ProviderCore.RfOutputMask, "input bits other than the five kept");
            }
            // the whole leaf (LOD 10, MinInLod = MaxInLod = 0) holds the surface: nothing proven
            int leaf = ProviderCore.Read(core, 10, 0, 0, 0, 0, 0, 0, ProviderCore.DtContent, 0, null, null, 0, 0, 0, true);
            Assert.AreEqual(0, leaf & ProviderCore.RfOutputMask);
        }

        [TestMethod]
        public void DetectOnly_FlagsOnlyProvenUniformRanges()
        {
            ProviderCore empty = Core(new Uniform(false)), solid = Core(new Uniform(true)), plane = Core(new TruncatedPlane(Plane));
            Assert.AreEqual(ProviderCore.RfEmptyData | ProviderCore.RfContentChecked,
                            ProviderCore.Read(empty, 10, 0, 0, 0, 0, 0, 0, ProviderCore.DtContent, 0, null, null, 0, 0, 0, true));
            Assert.AreEqual(ProviderCore.RfEmptyData | ProviderCore.RfContentChecked,
                            ProviderCore.Read(empty, 10, 0, 0, 0, 0, 0, 0, ProviderCore.DtMaterial, 0, null, null, 0, 0, 0, true), "material: no solid in the box");
            Assert.AreEqual(ProviderCore.RfFullContent | ProviderCore.RfContentChecked,
                            ProviderCore.Read(solid, 3, 1, 1, 1, 2, 2, 2, ProviderCore.DtContent, 0, null, null, 0, 0, 0, true));
            Assert.AreEqual(0, ProviderCore.Read(solid, 3, 1, 1, 1, 2, 2, 2, ProviderCore.DtMaterial, 0, null, null, 0, 0, 0, true) & ProviderCore.RfOutputMask,
                            "a material request is never claimed uniform from content alone");
            Assert.AreEqual(ProviderCore.RfFullContent | ProviderCore.RfContentChecked,
                            ProviderCore.Read(solid, 10, 0, 0, 0, 0, 0, 0, ProviderCore.DtContent, 0, null, null, 0, 0, 0, true),
                            "the whole leaf (LOD 10 = exactly [0, 1023]³) of a provably solid tile");
            int z = SurfaceZ;
            Assert.AreEqual(0, ProviderCore.Read(plane, 0, 0, 0, z - 4, 7, 7, z + 4, ProviderCore.DtContent, 0, null, null, 0, 0, 0, true) & ProviderCore.RfOutputMask);
            Assert.AreEqual(ProviderCore.RfEmptyData | ProviderCore.RfContentChecked,
                            ProviderCore.Read(plane, 0, 0, 0, z + 20, 7, 7, z + 30, ProviderCore.DtContent, 0, null, null, 0, 0, 0, true));
            Assert.AreEqual(ProviderCore.RfFullContent | ProviderCore.RfContentChecked,
                            ProviderCore.Read(plane, 0, 0, 0, z - 30, 7, 7, z - 20, ProviderCore.DtContent, 0, null, null, 0, 0, 0, true));
        }

        // ============================================================================================= request path

        [TestMethod]
        public void Request_FlagsExactlyAsTheWrittenBytes()
        {
            ProviderCore core = Core(new TruncatedPlane(Plane));
            int z = SurfaceZ;
            var c = new byte[16 * 16 * 16];
            var m = new byte[16 * 16 * 16];
            // above the plane: all empty → EmptyData; material 255 where content 0
            int f = ProviderCore.Read(core, 0, 0, 0, z + 10, 7, 7, z + 17, ProviderCore.DtContent | ProviderCore.DtMaterial, ProviderCore.RfSurfaceMaterial, c, m, 0, 8, 64, false);
            Assert.AreEqual(ProviderCore.RfSurfaceMaterial | ProviderCore.RfEmptyData, f);
            Assert.IsTrue(c.Take(512).All(b => b == 0) && m.Take(512).All(b => b == 255));
            // below: all full → FullContent, materials of the band (Sand_02 for −40 ≤ y < 2: index 11)
            f = ProviderCore.Read(core, 0, 0, 0, z - 17, 7, 7, z - 10, ProviderCore.DtContent | ProviderCore.DtMaterial, ProviderCore.RfEmptyData | ProviderCore.RfContentChecked, c, m, 0, 8, 64, false);
            Assert.AreEqual(ProviderCore.RfFullContent, f, "stale input bits cleared, FullContent proven");
            Assert.IsTrue(c.Take(512).All(b => b == 255) && m.Take(512).All(b => b == 11));
            // across: neither
            f = ProviderCore.Read(core, 0, 0, 0, z - 3, 7, 7, z + 4, ProviderCore.DtContent | ProviderCore.DtMaterial, 0, c, m, 0, 8, 64, false);
            Assert.AreEqual(0, f & ProviderCore.RfOutputMask);
            // content falls through the surface upwards (monotone along +z = U0 near the anchor)
            for (int k = 1; k < 8; k++) Assert.IsTrue(c[k * 64] <= c[(k - 1) * 64], "content decreases upwards");
            // material-only request over empty water: EmptyData (every written material 255)
            f = ProviderCore.Read(core, 0, 0, 0, z + 10, 3, 3, z + 13, ProviderCore.DtMaterial, 0, null, m, 0, 4, 16, false);
            Assert.AreEqual(ProviderCore.RfEmptyData, f);
        }

        [TestMethod]
        public void Request_WritesOnlyExistingArrays_AtTheTargetStrides()
        {
            ProviderCore core = Core(new TruncatedPlane(Plane));
            int z = SurfaceZ;
            // a 6³ target with the 4³ range written at offset (1, 1, 1): the border must stay untouched
            var c = Enumerable.Repeat((byte)0xEE, 216).ToArray();
            int baseIndex = 1 + 1 * 6 + 1 * 36;
            int f = ProviderCore.Read(core, 0, 0, 0, z - 2, 3, 3, z + 1, ProviderCore.DtContent | ProviderCore.DtMaterial, 0, c, null, baseIndex, 6, 36, false);
            for (int x = 0; x < 6; x++)
                for (int y = 0; y < 6; y++)
                    for (int k = 0; k < 6; k++)
                    {
                        bool inside = x >= 1 && x <= 4 && y >= 1 && y <= 4 && k >= 1 && k <= 4;
                        byte b = c[x + y * 6 + k * 36];
                        if (!inside) Assert.AreEqual((byte)0xEE, b, "border at " + x + "," + y + "," + k);
                    }
            Assert.AreEqual(0, f & ProviderCore.RfOutputMask, "a range across the surface");
            // a target without arrays for the requested types: nothing written, nothing claimed
            Assert.AreEqual(0, ProviderCore.Read(core, 0, 0, 0, z + 10, 1, 1, z + 11, ProviderCore.DtContent, 0, null, null, 0, 2, 4, false) & ProviderCore.RfOutputMask);
            // a range that does not fit the array: not written, counted
            long bad = ProviderCore.BadTargets;
            var small = new byte[4];
            ProviderCore.Read(core, 0, 0, 0, 0, 7, 7, 7, ProviderCore.DtContent, 0, small, null, 0, 8, 64, false);
            Assert.AreEqual(bad + 1, ProviderCore.BadTargets);
        }

        // =============================================================================================== intersect

        [TestMethod]
        public void IntersectBox_LodZeroBoxes_NeverGuesses()
        {
            ProviderCore core = Core(new TruncatedPlane(Plane));
            int z = SurfaceZ;
            Assert.AreEqual(ProviderCore.CtDisjoint, ProviderCore.IntersectBox(core, 0, 0, z + 16, 15, 15, z + 31));
            Assert.AreEqual(ProviderCore.CtContains, ProviderCore.IntersectBox(core, 0, 0, z - 31, 15, 15, z - 16));
            Assert.AreEqual(ProviderCore.CtIntersects, ProviderCore.IntersectBox(core, 0, 0, z - 4, 7, 7, z + 4));
            ProviderCore solid = Core(new Uniform(true));
            Assert.AreEqual(ProviderCore.CtContains, ProviderCore.IntersectBox(solid, 8, 8, 8, 15, 15, 15));
            Assert.AreEqual(ProviderCore.CtIntersects, ProviderCore.IntersectBox(solid, 1016, 0, 0, 1024, 8, 8), "a box reaching outside [0, 1023]³ is never Full");
            // the box SE passes is "already intersected with the leaf box [0, 1024]": the 1024 face is allowed
            Assert.AreEqual(ProviderCore.CtDisjoint, ProviderCore.IntersectBox(Core(new Uniform(false)), 0, 0, 0, 1024, 1024, 1024));
        }

        [TestMethod]
        public void SelfCheck_RandomBoxes_PassOnThePlane_AtLod012()
        {
            ProviderCore core = Core(new TruncatedPlane(Plane));
            SelfCheckResult r = ProviderSelfCheck.Run(core, 7, 400, 2, 2, SurfaceZ, 40);
            Console.WriteLine("boxes " + r.Boxes + " disjoint/contains/intersects " + r.Disjoint + "/" + r.Contains + "/" + r.Intersects + " lines " + r.LineChecks + " violations " + r.Violations + " " + r.FirstViolation);
            Assert.IsTrue(r.Pass, r.FirstViolation);
            Assert.IsTrue(r.Disjoint > 0 && r.Contains > 0 && r.Intersects > 0, "the boxes cover all three answers");
        }

        [TestMethod]
        public void SelfCheck_CurvedTileFarFromTheAnchor_Passes()
        {
            // SN (-1200, -300, -1200) lies in tile (3, 3, 1) (E0 = -X, N0 = -Y), ~1.7 km from the anchor: the plane is curved
 // and tilted in tile coordinates
            Vec3 min = TerrainLattice.TileMin(Map.C, 3, 3, 1);
            var core = new ProviderCore(new TileSampler(new TruncatedPlane(-300), Map, min, Bands), 3, 3, 1, SiteV1.TileEntityId(3, 3, 1), null);
            Vec3 l = Map.SnToSe(new Vec3(-1200, -300, -1200)) - min;
            Assert.IsTrue(l.X > 32 && l.Y > 32 && l.Z > 32 && l.X < 992 && l.Y < 992 && l.Z < 992, "focus inside the tile: " + l.X + "," + l.Y + "," + l.Z);
            SelfCheckResult r = ProviderSelfCheck.Run(core, 11, 300, (int)l.X, (int)l.Y, (int)l.Z, 48);
            Assert.IsTrue(r.Pass, r.FirstViolation);
        }

        [TestMethod]
        public void IntersectLine_OpenWaterFalse_SurfaceTrue()
        {
            int z = SurfaceZ;
            double s, e;
            Assert.IsFalse(ProviderCore.IntersectLine(Core(new Uniform(false)), 10, 10, 1000, 10, 10, 10, out s, out e), "open water does not block a laser antenna");
            ProviderCore core = Core(new TruncatedPlane(Plane));
            Assert.IsFalse(ProviderCore.IntersectLine(core, 10, 10, z + 100, 500, 10, z + 100, out s, out e), "a horizontal line 100 m over the plane");
            Assert.IsTrue(ProviderCore.IntersectLine(core, 2.5, 2.5, z + 200, 2.5, 2.5, z - 200, out s, out e));
            double tPlane = (z + 200 - LocalZ(Plane)) / 400.0;
            Assert.IsTrue(s <= tPlane && e >= tPlane && s >= 0 && e <= 1, "the crossing lies in [start, end]: " + s + " .. " + e);
            Assert.IsTrue(s > tPlane - 0.1, "the first non-empty step is the one at the surface (steps of ≤ 16 m): " + s);
            Assert.AreEqual(1.0, e, 1e-9, "rock below the plane is not empty: the interval runs to the line's end");
        }

        // ========================================================================================= inert and close

        [TestMethod]
        public void InertAndClosedProvider_ReadEmpty_IntersectDisjoint_CloseOnce()
        {
            var c = Enumerable.Repeat((byte)7, 64).ToArray();
            var m = Enumerable.Repeat((byte)7, 64).ToArray();
            int f = ProviderCore.Read(null, 0, 0, 0, 0, 3, 3, 3, ProviderCore.DtContent | ProviderCore.DtMaterial, 0, c, m, 0, 4, 16, false);
            Assert.AreEqual(ProviderCore.RfEmptyData, f);
            Assert.IsTrue(c.All(b => b == 0) && m.All(b => b == 255));
            Assert.AreEqual(ProviderCore.CtDisjoint, ProviderCore.IntersectBox(null, 0, 0, 0, 7, 7, 7));
            double s, e;
            Assert.IsFalse(ProviderCore.IntersectLine(null, 0, 0, 0, 10, 10, 10, out s, out e));

            int released = 0;
            ProviderCore core = Core(new Uniform(true), () => Interlocked.Increment(ref released));
            Assert.AreEqual(ProviderCore.CtContains, ProviderCore.IntersectBox(core, 8, 8, 8, 15, 15, 15));
            Parallel.For(0, 8, _ => core.Close());
            core.Close();
            Assert.AreEqual(1, released, "Close is idempotent: one release");
            Assert.IsTrue(core.IsClosed);
            Assert.AreEqual(ProviderCore.CtDisjoint, ProviderCore.IntersectBox(core, 8, 8, 8, 15, 15, 15), "after Close: Disjoint without touching the source");
            f = ProviderCore.Read(core, 0, 0, 0, 0, 3, 3, 3, ProviderCore.DtContent, 0, c, null, 0, 4, 16, false);
            Assert.AreEqual(ProviderCore.RfEmptyData, f);
        }

        // ============================================================================================ allocation

        [TestMethod]
        public void TenThousandReads_AllocateNothing()
        {
            ProviderCore core = Core(new TruncatedPlane(Plane));
            int z = SurfaceZ;
            var c = new byte[6 * 6 * 6];
            var m = new byte[6 * 6 * 6];
            for (int k = 0; k < 200; k++) ProviderCore.Read(core, k % 3, 0, 0, (z >> (k % 3)) - 3, 5, 5, (z >> (k % 3)) + 2, 3, 0, c, m, 0, 6, 36, k % 7 == 0);
            try { AppDomain.MonitoringIsEnabled = true; }
            catch (Exception ex) { Assert.Inconclusive("AppDomain monitoring unavailable: " + ex.Message); }
            GC.Collect();
            long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int k = 0; k < 10000; k++)
            {
                int lod = k % 3;
                ProviderCore.Read(core, lod, 0, 0, (z >> lod) - 3, 5, 5, (z >> lod) + 2, ProviderCore.DtContent | ProviderCore.DtMaterial, ProviderCore.RfForPhysics,
                                  c, m, 0, 6, 36, k % 7 == 0);
                if (k % 10 == 0) ProviderCore.IntersectBox(core, 0, 0, z - 8, 7, 7, z + 8);
            }
            long after = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            Console.WriteLine("10,000 reads allocated " + (after - before) + " bytes (whole AppDomain)");
            Assert.IsTrue(after - before < 64 * 1024, "allocated " + (after - before) + " bytes over 10,000 reads");
        }

        [TestMethod]
        public void ReadStats_Percentiles()
        {
            var s = new ReadStats();
            Assert.AreEqual(0, s.PercentileUs(0.99));
            long tick = System.Diagnostics.Stopwatch.Frequency / 1000000; // 1 µs
            for (int k = 0; k < 98; k++) s.Add(tick * 15);   // 15 µs
            s.Add(tick * 1500);                               // 1.5 ms
            s.Add(tick * 25000);                              // 25 ms
            Assert.AreEqual(100, s.Calls);
            Assert.AreEqual(20, s.PercentileUs(0.5), 1e-9, "the 10–20 µs bin");
            Assert.AreEqual(1600, s.PercentileUs(0.99), 1e-9, "the 1.5–1.6 ms bin");
            Assert.AreEqual(25000, s.MaxUs, 2);
            Assert.AreEqual(0, ReadStats.Bin(0.5));
            Assert.AreEqual(100, ReadStats.Bin(1000));
            Assert.AreEqual(190, ReadStats.Bin(10000));
        }

        // ================================================================================================ mutants

        [TestMethod]
        public void S4M3_IntersectHole_OnlyTheCell()
        {
            ProviderCore core = Core(new Uniform(true));
            int x0, y0, z0;
            Assert.IsTrue(TerrainLattice.MutantCell(Map, TileMin, out x0, out y0, out z0));
            core.SetMutantCell(x0, y0, z0);
            Assert.AreEqual(ProviderCore.CtContains, ProviderCore.IntersectBox(core, x0, y0, z0, x0 + 7, y0 + 7, z0 + 7));
            S4Mutants.IntersectHole = true;
            Assert.AreEqual(ProviderCore.CtDisjoint, ProviderCore.IntersectBox(core, x0, y0, z0, x0 + 7, y0 + 7, z0 + 7), "M3: the cell is a hole");
            Assert.AreEqual(ProviderCore.CtDisjoint, ProviderCore.IntersectBox(core, x0 - 16, y0 - 16, z0 - 16, x0, y0, z0), "every box touching it");
            Assert.AreEqual(ProviderCore.CtContains, ProviderCore.IntersectBox(core, x0 + 8, y0, z0, x0 + 15, y0 + 7, z0 + 7), "the neighbour cell is untouched");
        }

        [TestMethod]
        public void S4M8_EmptyDataOnTheCellsRequest_SelfCheckRed()
        {
            ProviderCore core = Core(new TruncatedPlane(Plane));
            int x0, y0, z0;
            Assert.IsTrue(TerrainLattice.MutantCell(Map, TileMin, out x0, out y0, out z0));
            core.SetMutantCell(x0, y0, z0);
            var c = new byte[12 * 12 * 12];
            int f = ProviderCore.Read(core, 0, x0 - 1, y0 - 1, z0 - 1, x0 + 10, y0 + 10, z0 + 10, ProviderCore.DtContent, ProviderCore.RfForPhysics, c, null, 0, 12, 144, false);
            Assert.AreEqual(0, f & ProviderCore.RfEmptyData, "the surface cell is not empty");
            Assert.IsTrue(ProviderSelfCheck.Run(core, 3, 200, x0 + 4, y0 + 4, z0 + 4, 6).Pass);
            S4Mutants.EmptyDataOnSurface = true;
            f = ProviderCore.Read(core, 0, x0 - 1, y0 - 1, z0 - 1, x0 + 10, y0 + 10, z0 + 10, ProviderCore.DtContent, ProviderCore.RfForPhysics, c, null, 0, 12, 144, false);
            Assert.AreNotEqual(0, f & ProviderCore.RfEmptyData, "M8: the physics cell's request claims EmptyData");
            Assert.IsTrue(c.Any(b => b != 0), "with the bytes unchanged");
            f = ProviderCore.Read(core, 0, x0 + 8 - 1, y0 - 1, z0 - 1, x0 + 18, y0 + 10, z0 + 10, ProviderCore.DtContent, ProviderCore.RfForPhysics, c, null, 0, 12, 144, false);
            Assert.AreEqual(0, f & ProviderCore.RfEmptyData, "the neighbour cell's request is honest");
        }

        [TestMethod]
        public void S4M8b_DetectOnlyLie_TurnsN3Red()
        {
            ProviderCore core = Core(new TruncatedPlane(Plane));
            int x0, y0, z0;
            Assert.IsTrue(TerrainLattice.MutantCell(Map, TileMin, out x0, out y0, out z0));
            core.SetMutantCell(x0, y0, z0);
            SelfCheckResult green = ProviderSelfCheck.Run(core, 5, 300, x0 + 4, y0 + 4, z0 + 4, 8);
            Assert.IsTrue(green.Pass, green.FirstViolation);
            S4Mutants.EmptyDataDetectOnly = true;
            Assert.AreEqual(ProviderCore.RfEmptyData | ProviderCore.RfContentChecked,
                            ProviderCore.Read(core, 0, x0, y0, z0, x0 + 7, y0 + 7, z0 + 7, ProviderCore.DtContent, 0, null, null, 0, 0, 0, true));
            SelfCheckResult red = ProviderSelfCheck.Run(core, 5, 300, x0 + 4, y0 + 4, z0 + 4, 8);
            Assert.IsFalse(red.Pass, "S4.N3 must go red under S4.M8b");
            StringAssert.Contains(red.FirstViolation, "detectOnly EmptyData");
            // the request path stays honest under M8b (only the detect path lies)
            var c = new byte[12 * 12 * 12];
            Assert.AreEqual(0, ProviderCore.Read(core, 0, x0 - 1, y0 - 1, z0 - 1, x0 + 10, y0 + 10, z0 + 10, ProviderCore.DtContent, 0, c, null, 0, 12, 144, false) & ProviderCore.RfEmptyData);
        }

        [TestMethod]
        public void Mutants_Registry_S4Ids()
        {
            foreach (string id in new[] { "S4.M2", "S4.M3", "S4.M4", "S4.M5", "S4.M6", "S4.M7", "S4.M8", "S4.M8b", "S4.M10" })
            {
                string r = Mutants.Set(id, true);
                StringAssert.StartsWith(r, id + " ON", id);
            }
            Assert.IsTrue(S4Mutants.TileShift2m && S4Mutants.IntersectHole && S4Mutants.SaveTrue && S4Mutants.WrongManifest && S4Mutants.NoPrefetch &&
                          S4Mutants.EmptyDataOnSurface && S4Mutants.EmptyDataDetectOnly && S4Mutants.RandomIds && TerrainMutants.HalfVoxelShift);
            StringAssert.Contains(Mutants.Active, "S4.M2,S4.M3,S4.M4,S4.M5,S4.M6,S4.M7,S4.M8,S4.M8b,S4.M10");
            StringAssert.StartsWith(Mutants.Set("s4.m9.cutout", true), "S4.M9.cutout ON");
            Assert.IsTrue(S4Mutants.GuardOff(TerrainPaths.Cutout));
            Assert.IsFalse(S4Mutants.GuardOff(TerrainPaths.WriteRange));
            StringAssert.Contains(S4Mutants.Active, "S4.M9.cutout");
            StringAssert.StartsWith(Mutants.Set("S4.M9.nope", true), "REFUSE");
            StringAssert.StartsWith(Mutants.Set("S4.M9", true), "REFUSE");
            StringAssert.StartsWith(Mutants.Set("S4.M1a", true), "REFUSE", "offline A-T0 mutants are refused in SE");
            Assert.IsFalse(TerrainMutants.SignFlip);
            Mutants.ClearAll();
            Assert.AreEqual("", S4Mutants.Active);
            Assert.IsFalse(TerrainMutants.HalfVoxelShift);
        }

        // ================================================================================================== gate

        private static TerrainGate.Facts GoodFacts()
        {
            return new TerrainGate.Facts { GlobalOk = true, GlobalWhy = "offline single player", PlanetCount = 1, ManifestOk = true, ProviderRegistered = true };
        }

        [TestMethod]
        public void Gate_DecisionTable_ContractOrder()
        {
            TerrainGate.Decision d = TerrainGate.Decide(GoodFacts());
            Assert.AreEqual(TerrainGate.Result.Ok, d.Result);
            Assert.IsFalse(d.FailSafe);

            TerrainGate.Facts f = GoodFacts(); f.GlobalOk = false; f.GlobalWhy = "onlineMode=PUBLIC"; f.ManifestOk = false;
            d = TerrainGate.Decide(f);
            Assert.AreEqual(TerrainGate.Result.Skip, d.Result);
            Assert.IsFalse(d.FailSafe, "multiplayer: no fail-safe");
            StringAssert.StartsWith(d.Line, "DE.Terrain SKIP multiplayer");

            f = GoodFacts(); f.PlanetCount = 0;
            d = TerrainGate.Decide(f);
            Assert.AreEqual("DE.Terrain SKIP no DE_4546B", d.Line);
            Assert.IsFalse(d.FailSafe);
            f = GoodFacts(); f.PlanetCount = 2;
            Assert.AreEqual("DE.Terrain SKIP planets=2", TerrainGate.Decide(f).Line);

            f = GoodFacts(); f.ManifestOk = false; f.ManifestField = "files"; f.ManifestFound = "5416"; f.ManifestExpected = "5417"; f.ProviderRegistered = false;
            d = TerrainGate.Decide(f);
            Assert.AreEqual(TerrainGate.Result.Refuse, d.Result);
            Assert.IsTrue(d.FailSafe);
            Assert.AreEqual("DE.Terrain REFUSE source files found=5416 expected=5417", d.Line, "condition 3 before 4");

            f = GoodFacts(); f.ProviderRegistered = false; f.ProviderError = "id 45460 belongs to X"; f.MissingMembers = "M";
            d = TerrainGate.Decide(f);
            Assert.AreEqual("provider", d.Condition);
            Assert.IsTrue(d.FailSafe);

            f = GoodFacts(); f.MissingMembers = "MyWorldGenerator.AddVoxelMap(...)"; f.IdTaken = "tile 0,0,0";
            Assert.AreEqual("members", TerrainGate.Decide(f).Condition);
            f = GoodFacts(); f.IdTaken = "tile 1,1,0 id=0x… taken by MyCubeGrid";
            d = TerrainGate.Decide(f);
            Assert.AreEqual("ids", d.Condition);
            Assert.IsTrue(d.FailSafe);
        }

        [TestMethod]
        public void FailSafe_Footprint_AndOnce()
        {
            Assert.IsTrue(TerrainFailSafe.InFootprint(new Vec3(0, -1, 0)));
            Assert.IsTrue(TerrainFailSafe.InFootprint(new Vec3(2100, -1500, -2100)));
            Assert.IsFalse(TerrainFailSafe.InFootprint(new Vec3(2100.01, -10, 0)));
            Assert.IsFalse(TerrainFailSafe.InFootprint(new Vec3(0, 0, 0)), "y < 0 only");
            Assert.IsFalse(TerrainFailSafe.InFootprint(new Vec3(0, 5, 0)));
            Assert.IsFalse(TerrainFailSafe.Active);
            Assert.IsTrue(TerrainFailSafe.SetActive("REFUSE source files"));
            Assert.IsFalse(TerrainFailSafe.SetActive("second"), "once per session");
            Assert.AreEqual("REFUSE source files", TerrainFailSafe.Reason);
            Assert.AreEqual(1, TerrainFailSafe.CountSaveBlocked());
            Assert.AreEqual(2, TerrainFailSafe.CountSaveBlocked());
            TerrainFailSafe.ResetSession();
            Assert.IsFalse(TerrainFailSafe.Active);
            Assert.AreEqual(0, TerrainFailSafe.SaveBlocked);
            Assert.AreEqual("DeepEngineers terrain missing: saving disabled", TerrainFailSafe.HudText);
        }

        // ================================================================================================ guards

        [TestMethod]
        public void Guards_CountAll_LogAtMostOncePerSecondPerPath()
        {
            long n;
            Assert.IsTrue(TerrainGuards.RecordBlocked(TerrainPaths.Meteor, 1000, out n));
            Assert.AreEqual(1, n);
            Assert.IsFalse(TerrainGuards.RecordBlocked(TerrainPaths.Meteor, 1500, out n));
            Assert.IsTrue(TerrainGuards.RecordBlocked(TerrainPaths.Cutout, 1500, out n), "per path");
            Assert.IsTrue(TerrainGuards.RecordBlocked(TerrainPaths.Meteor, 2000, out n));
            Assert.AreEqual(3, TerrainGuards.Count("meteor"));
            TerrainGuards.ResetCounts();
            int logs = 0;
            Parallel.For(0, 8, t =>
            {
                long c;
                for (int k = 0; k < 10000; k++)
                    if (TerrainGuards.RecordBlocked(TerrainPaths.WriteRange, 5000, out c)) Interlocked.Increment(ref logs);
            });
            Assert.AreEqual(80000, TerrainGuards.Count(TerrainPaths.WriteRange), "Interlocked counts from worker threads");
            Assert.AreEqual(1, logs, "one log line for the burst within one second");
            Assert.AreEqual(-1, TerrainPaths.IndexOf("drill"));
            Assert.AreEqual(TerrainPaths.VoxelHand, TerrainPaths.IndexOf("VoxelHand"));
            CollectionAssert.AreEqual(new[] { "cutout", "shipdrill", "explosion", "meteor", "voxelhand", "writerange" }, TerrainPaths.Names);
        }

        // ================================================================================================== chat

        [TestMethod]
        public void Chat_Syntax()
        {
            string usage;
            var a = S4Args.ParseChat(new[] { "terrain" }, out usage);
            Assert.AreEqual("status", a["action"]);
            a = S4Args.ParseChat(new[] { "terrain", "hash", "0", "-17.2", "0", "16" }, out usage);
            CollectionAssert.AreEqual(new object[] { 0.0, -17.2, 0.0 }, (List<object>)a["sn"]);
            Assert.AreEqual(16.0, a["half"]);
            a = S4Args.ParseChat(new[] { "terrain", "edit", "cutout", "1", "-20", "3", "4" }, out usage);
            Assert.AreEqual("cutout", a["path"]);
            Assert.AreEqual(4.0, a["radius"]);
            a = S4Args.ParseChat(new[] { "terrain", "rays", "10", "20", "size=8", "prefetch=off", "random=0" }, out usage);
            Assert.AreEqual("at1", a["action"]);
            CollectionAssert.AreEqual(new object[] { 10.0, 20.0 }, (List<object>)a["origin"]);
            Assert.AreEqual(8.0, a["size"]);
            Assert.AreEqual(false, a["prefetch"]);
            Assert.IsFalse(S4Args.Flag(a, "prefetch", true));
            a = S4Args.ParseChat(new[] { "terrain", "drop", "1,2", "-3.5,4" }, out usage);
            Assert.AreEqual(2, ((List<object>)a["points"]).Count);
            CollectionAssert.AreEqual(new object[] { -3.5, 4.0 }, (List<object>)((List<object>)a["points"])[1]);
            a = S4Args.ParseChat(new[] { "terrain", "perf", "from=-100,-40,0", "to=100,-40,0", "speed=100" }, out usage);
            CollectionAssert.AreEqual(new[] { -100.0, -40.0, 0.0 }, S4Args.Numbers(a, "from", 3));
            a = S4Args.ParseChat(new[] { "terrain", "bake", "remove" }, out usage);
            Assert.AreEqual(true, a["remove"]);
            a = S4Args.ParseChat(new[] { "terrain", "at0b", "lods=0,1,2", "heights=-17.5" }, out usage);
            Assert.AreEqual(-17.5, a["heights"], "a single number stays a number");
            CollectionAssert.AreEqual(new[] { -17.5 }, S4Args.Numbers(a, "heights", 0), "and reads as a one-element list");
            Assert.IsNull(S4Args.ParseChat(new[] { "terrain", "edit", "nope", "1", "2", "3" }, out usage));
            StringAssert.Contains(usage, "edit");
            Assert.IsNull(S4Args.ParseChat(new[] { "terrain", "frobnicate" }, out usage));
            Assert.IsNull(S4Args.ParseChat(new[] { "terrain", "hash", "1", "2" }, out usage));
            Assert.AreEqual("at1", S4Args.ActionOf(new Dictionary<string, object> { { "action", "rays" } }));
            Assert.AreEqual("status", S4Args.ActionOf(null));
            Assert.IsNull(S4Args.NumbersOf(-17.5, 3), "a scalar is not a point");
            Assert.IsNull(S4Args.NumbersOf("x", 0));
        }

        // ============================================================================================== ray plan

        [TestMethod]
        public void RayPlan_GridRandomCells()
        {
            List<RayColumn> g = RayPlan.Grid(0, 0, 64, 2.5, 20, -400);
            Assert.AreEqual(4096, g.Count);
            Assert.AreEqual(-78.75, g[0].X, 1e-12);
            Assert.AreEqual(78.75, g[4095].Z, 1e-12);
            Assert.AreEqual(0.0, g.Average(c => c.X), 1e-9);
            List<RayColumn> r1 = RayPlan.Random(1000, 4546, 2000, 160, -1950), r2 = RayPlan.Random(1000, 4546, 2000, 160, -1950);
            Assert.IsTrue(r1.Zip(r2, (a, b) => a.X == b.X && a.Z == b.Z).All(x => x), "seeded");
            Assert.IsTrue(r1.All(c => Math.Abs(c.X) <= 2000 && Math.Abs(c.Z) <= 2000 && c.Set == "random"));
            var cells = new List<int>();
            RayPlan.CellsOnSegment(new Vec3(4, 4, 500), new Vec3(4, 4, 400), cells);
            Assert.AreEqual(13 * 3, cells.Count, "z 500 → 400 crosses cells 62 … 50");
            Assert.AreEqual(62, cells[2]);
            Assert.AreEqual(50, cells[cells.Count - 1]);
            double t0, t1;
            Assert.IsTrue(RayPlan.ClipToBox(new Vec3(-10, 5, 5), new Vec3(30, 5, 5), Vec3.Zero, 20, out t0, out t1));
            Assert.AreEqual(0.25, t0, 1e-12);
            Assert.AreEqual(0.75, t1, 1e-12);
            Assert.IsFalse(RayPlan.ClipToBox(new Vec3(-10, 25, 5), new Vec3(30, 25, 5), Vec3.Zero, 20, out t0, out t1));
        }

        [TestMethod]
        public void Hash_IsFnv1a64()
        {
            var b = new byte[] { 1, 2, 3, 250, 0, 7 };
            Assert.AreEqual(SiteV1.Fnv1a64(b), S4Hash.Add(S4Hash.Offset, b, b.Length));
            Assert.AreEqual("0xcbf29ce484222325", S4Hash.Hex(S4Hash.Offset));
        }

        [TestMethod]
        public void Rows_AreOneJsonObject()
        {
            string row = S4Rows.Row("s4.ray", "set", "grid", "i", 3, "castSnY", -17.25, "castSe", null, "cells", new object[] { 1, 2 });
            var o = (IDictionary<string, object>)MiniJson.Parse(row);
            Assert.AreEqual("s4.ray", o["ev"]);
            Assert.AreEqual(-17.25, (double)o["castSnY"], 0);
            Assert.IsNull(o["castSe"]);
            Assert.IsFalse(row.Contains("\n"));
        }
    }
}
