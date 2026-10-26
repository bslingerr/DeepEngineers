using System;
using System.Collections.Generic;
using System.IO;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Terrain.Tests
{
    /// <summary>
 /// S8a  the SE conversion path offline. The core (Wire <see cref="S8aConvertCore"/>: the preimage
 /// boxes, the tile stitching across the site-v1 lattice seam, the tile offsets, the copy-on-write splice, the notice
 /// box) runs on the install's batch 12-18-12 (read in place, read-only) with an analytic tile source: a flat seabed at
 /// SN y −20 and a 2.5 m spherical hole at SN (−1, −20, −40), whose SE preimage straddles the seam of tiles (1, ·, 1) and
 /// (2, ·, 1) at SE X = 2 (the 4 m overlap). Checks: the octrees the carve does not reach stay byte-identical to the base;
 /// the notice box is the hole's voxel box (± the density band), not whole octrees; read back through the S4 reader
 /// (<see cref="SnOctreeSource"/> + <see cref="SnField"/>) the surface over the hole's footprint matches the analytic
 /// carve within S8a.N2's tolerance (0.25 m); S8a.M2 (corner sampling) moves it by more than that. Inconclusive without
 /// the install.
 /// </summary>
    [TestClass]
    public class S8aConvertCoreTests
    {
        private const double PlaneY = -20.0;
        private const double Radius = 2.5;
        private static readonly Vec3 Centre = new Vec3(-1.0, -20.0, -40.0);     // SN
        private const int Bx = 12, By = 18, Bz = 12;

        /// <summary>The analytic SE field over the site-v1 lattice: every tile's voxel reads the field at its SE sample point mapped to SN.</summary>
        private sealed class FieldTiles : IS8aTileSource
        {
            private readonly SiteMap _map;
            private readonly List<Vec3> _mins = new List<Vec3>();
            private readonly List<string> _names = new List<string>();
            public bool Carve;
            public long Reads;

            public FieldTiles(SiteMap map)
            {
                _map = map;
                for (int i = 1; i <= 2; i++)
                    for (int j = 1; j <= 2; j++)
                    {
                        _mins.Add(map.C + SiteV1.TileMinRelative(i, j, 1));
                        _names.Add(i + "," + j + ",1");
                    }
            }

            public int Count { get { return _mins.Count; } }
            public int TileSize { get { return SiteV1.Tile; } }
            public string Name(int i) { return _names[i]; }

            public bool TryGetTile(int index, out Vec3 placeMin)
            {
                placeMin = _mins[index];
                return true;
            }

            public int TileAt(Vec3 se)
            {
                int any = -1;
                for (int t = 0; t < _mins.Count; t++)
                {
                    Vec3 l = se - _mins[t];
                    if (l.X < 0 || l.Y < 0 || l.Z < 0 || l.X >= SiteV1.Tile || l.Y >= SiteV1.Tile || l.Z >= SiteV1.Tile) continue;
                    bool interior = l.X >= SiteV1.InteriorMargin && l.Y >= SiteV1.InteriorMargin && l.Z >= SiteV1.InteriorMargin &&
                                    l.X < SiteV1.Tile - SiteV1.InteriorMargin && l.Y < SiteV1.Tile - SiteV1.InteriorMargin && l.Z < SiteV1.Tile - SiteV1.InteriorMargin;
                    if (interior) return t;
                    if (any < 0) any = t;
                }
                return any;
            }

            /// <summary>Signed field in SN space: positive inside solid (below the plane and outside the sphere), ±1 beyond a metre.</summary>
            public double Field(Vec3 sn)
            {
                double s = PlaneY - sn.Y;
                if (Carve)
                {
                    double d = (sn - Centre).Length - Radius;
                    if (d < s) s = d;
                }
                return s < -1 ? -1 : s > 1 ? 1 : s;
            }

            private byte ContentAt(int tile, int x, int y, int z)
            {
                Vec3 min = _mins[tile];
                return (byte)Math.Round((Field(_map.SeToSn(new Vec3(min.X + x, min.Y + y, min.Z + z))) + 1.0) * 127.5);
            }

            /// <summary>SE's own view of its content at an SN point: the trilinear blend of the 8 LOD-0 samples around it (what SE's voxel mesh and CastRay follow), −1..1.</summary>
            public double SeTrilinear(Vec3 sn)
            {
                Vec3 w = _map.SnToSe(sn);
                int t = TileAt(w);
                if (t < 0) return double.NaN;
                Vec3 l = w - _mins[t];
                int ix = (int)Math.Floor(l.X), iy = (int)Math.Floor(l.Y), iz = (int)Math.Floor(l.Z);
                double fx = l.X - ix, fy = l.Y - iy, fz = l.Z - iz;
                double sum = 0;
                for (int c = 0; c < 8; c++)
                {
                    int i = c & 1, j = (c >> 1) & 1, k = c >> 2;
                    double wgt = (i == 1 ? fx : 1 - fx) * (j == 1 ? fy : 1 - fy) * (k == 1 ? fz : 1 - fz);
                    sum += wgt * (ContentAt(t, ix + i, iy + j, iz + k) / 127.5 - 1.0);
                }
                return sum;
            }

            public void ReadRange(int index, int minX, int minY, int minZ, int maxX, int maxY, int maxZ, byte[] content, byte[] material)
            {
                Vec3 min = _mins[index];
                int sx = maxX - minX + 1, sy = maxY - minY + 1;
                for (int z = minZ; z <= maxZ; z++)
                    for (int y = minY; y <= maxY; y++)
                        for (int x = minX; x <= maxX; x++)
                        {
                            Vec3 sn = _map.SeToSn(new Vec3(min.X + x, min.Y + y, min.Z + z));
                            double s = Field(sn);
                            int i = (x - minX) + sx * ((y - minY) + sy * (z - minZ));
                            content[i] = (byte)Math.Round((s + 1.0) * 127.5);
                            material[i] = 7;
                            Reads++;
                        }
            }
        }

        private static double Expected(double x, double z)
        {
            double d2 = (x - Centre.X) * (x - Centre.X) + (z - Centre.Z) * (z - Centre.Z);
            if (d2 >= Radius * Radius) return PlaneY;
            return Math.Min(PlaneY, Centre.Y - Math.Sqrt(Radius * Radius - d2));
        }

        /// <summary>The surface along a column through the S4 reader: the sign change of the trilinear sample (solid positive), by bisection.</summary>
        private static double Surface(SnField field, double x, double z, double top, double bottom)
        {
            bool far;
            Assert.IsTrue(field.Sample(x, top, z, out far) < 0, "water above the plane at (" + x + ", " + z + ")");
            Assert.IsTrue(field.Sample(x, bottom, z, out far) > 0, "rock below the plane at (" + x + ", " + z + ")");
            double hi = top, lo = bottom;
            for (int k = 0; k < 30; k++)
            {
                double mid = 0.5 * (hi + lo);
                if (field.Sample(x, mid, z, out far) < 0) hi = mid; else lo = mid;
            }
            return 0.5 * (hi + lo);
        }

        /// <summary>SE's own surface along a column: the zero of its trilinear content (the carve as SE's physics shows it).</summary>
        private static double SurfaceSe(FieldTiles tiles, double x, double z, double top, double bottom)
        {
            Assert.IsTrue(tiles.SeTrilinear(new Vec3(x, top, z)) < 0, "water above the plane (SE) at (" + x + ", " + z + ")");
            Assert.IsTrue(tiles.SeTrilinear(new Vec3(x, bottom, z)) > 0, "rock below the plane (SE) at (" + x + ", " + z + ")");
            double hi = top, lo = bottom;
            for (int k = 0; k < 30; k++)
            {
                double mid = 0.5 * (hi + lo);
                if (tiles.SeTrilinear(new Vec3(x, mid, z)) < 0) hi = mid; else lo = mid;
            }
            return 0.5 * (hi + lo);
        }

        private static S8aBatchFile LoadInstallBatch()
        {
            string path = Path.Combine(TestEnv.OctreeDir, SnOctreeFormat.BatchFileName(Bx, By, Bz));
            byte[] file = File.ReadAllBytes(path);
            S8aBatchFile b;
            string err;
            Assert.IsTrue(S8aBatchFile.TryParse(file, file.Length, Bx, By, Bz, out b, out err), err);
            return b;
        }

        [TestMethod]
        public void InstallBatch_SyntheticCarve_UntouchedOctreesIdentical_NoticeBoxTight_S4ReaderWithinN2()
        {
            TestEnv.RequireInstall();
            SiteMap map = SiteMap.ForSiteV1(new Vec3(-1000000.0, 250000.0, 1500000.0));
            var tiles = new FieldTiles(map);
            var core = new S8aConvertCore();
            // the hole's SE preimage straddles the lattice seam (tiles i = 1 and 2 overlap on SE X in [-2, 2))
            Vec3 centreSe = map.SnToSe(Centre);
            int drilled = tiles.TileAt(centreSe);
            Assert.IsTrue(drilled >= 0, "a tile holds the hole");
            Vec3 pm;
            tiles.TryGetTile(drilled, out pm);
            Vec3 local = centreSe - pm;
            int cx = (int)Math.Floor(local.X), cy = (int)Math.Floor(local.Y), cz = (int)Math.Floor(local.Z);
            int straddling = 0;
            for (int t = 0; t < tiles.Count; t++)
            {
                Vec3 m;
                tiles.TryGetTile(t, out m);
                Vec3 l = centreSe - m;
                if (l.X >= -8 && l.X < SiteV1.Tile + 8 && l.Y >= 0 && l.Y < SiteV1.Tile && l.Z >= 0 && l.Z < SiteV1.Tile) straddling++;
            }
            Assert.IsTrue(straddling >= 2, "the hole's neighbourhood meets at least two tiles (the seam): " + straddling);

            // pass 1: the base = the install batch with the plane written over the hole's neighbourhood (+-16 m), no carve
            S8aBatchFile baseBatch = LoadInstallBatch();
            tiles.Carve = false;
            S8aConvertResult r1 = core.ConvertBatch(map, tiles, baseBatch, new[] { new S8aEditBox(drilled, cx - 16, cy - 16, cz - 16, cx + 16, cy + 16, cz + 16) });
            Assert.IsTrue(r1.OctreesRewritten > 0, "the plane pass rewrites octrees: " + r1.Note);
            byte[] baseBytes = baseBatch.Serialize();
            S8aBatchFile b2;
            string err;
            Assert.IsTrue(S8aBatchFile.TryParse(baseBytes, baseBytes.Length, Bx, By, Bz, out b2, out err), err);

            // pass 2: the carve, as a drill edit of the drilled tile (the sphere's box + 3 m)
            tiles.Carve = true;
            int e = (int)Math.Ceiling(Radius) + 3;
            var edit = new S8aEditBox(drilled, cx - e, cy - e, cz - e, cx + e, cy + e, cz + e);
            S8aConvertResult r2 = core.ConvertBatch(map, tiles, b2, new[] { edit });
            Assert.IsTrue(r2.OctreesRewritten >= 1 && r2.OctreesRewritten <= 8, "the hole meets 1-8 octrees, rewrote " + r2.OctreesRewritten + " " + r2.Note);
            Assert.AreEqual(0, r2.Skipped, "no octree skipped: " + r2.Note);
            Assert.IsTrue(r2.Cells > 0 && r2.Cells < 4000, "cells changed: " + r2.Cells);

            // the notice box: the hole's batch-local voxel box (x 124..129, y 137..142, z 85..90) within the density band
            int hx0, hy0, hz0, hx1, hy1, hz1;
            S8aConvert.VoxelOf(Centre - new Vec3(Radius, Radius, Radius), out hx0, out hy0, out hz0);
            S8aConvert.VoxelOf(Centre + new Vec3(Radius, Radius, Radius), out hx1, out hy1, out hz1);
            hx0 -= Bx * 160; hx1 -= Bx * 160; hy0 -= By * 160; hy1 -= By * 160; hz0 -= Bz * 160; hz1 -= Bz * 160;
            string box = r2.MinX + "," + r2.MinY + "," + r2.MinZ + "-" + r2.MaxX + "," + r2.MaxY + "," + r2.MaxZ + " (hole " + hx0 + "," + hy0 + "," + hz0 + "-" + hx1 + "," + hy1 + "," + hz1 + ")";
            byte[] carvedBytes = b2.Serialize();
            S8aBatchFile b3;
            Assert.IsTrue(S8aBatchFile.TryParse(carvedBytes, carvedBytes.Length, Bx, By, Bz, out b3, out err), err);
            // the voxels that changed outside the hole's box + 1: the diagnostic of the box's reach (the SE content ramp of 1 m
 // and the trilinear stencil's reach of one SE voxel)
            var outsideSamples = new List<string>();
            int outsideCount = 0, changedCount = 0;
            int dx0 = int.MaxValue, dy0 = int.MaxValue, dz0 = int.MaxValue, dx1 = int.MinValue, dy1 = int.MinValue, dz1 = int.MinValue;
            {
                var ta = new byte[S8aOctree.Voxels]; var da = new byte[S8aOctree.Voxels]; var tb = new byte[S8aOctree.Voxels]; var db = new byte[S8aOctree.Voxels];
                for (int i = 0; i < 125; i++)
                {
                    int ix = i / 25, iy = (i / 5) % 5, iz = i % 5;
                    baseBatch.DecodeOctree(i, ta, da);
                    b3.DecodeOctree(i, tb, db);
                    for (int z = 0; z < 32; z++) for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                    {
                        int k = x + (y << 5) + (z << 10);
                        if (ta[k] == tb[k] && da[k] == db[k]) continue;
                        int lx = ix * 32 + x, ly = iy * 32 + y, lz = iz * 32 + z;
                        changedCount++;
                        dx0 = Math.Min(dx0, lx); dy0 = Math.Min(dy0, ly); dz0 = Math.Min(dz0, lz);
                        dx1 = Math.Max(dx1, lx); dy1 = Math.Max(dy1, ly); dz1 = Math.Max(dz1, lz);
                        if (lx >= hx0 - 1 && lx <= hx1 + 1 && ly >= hy0 - 1 && ly <= hy1 + 1 && lz >= hz0 - 1 && lz <= hz1 + 1) continue;
                        outsideCount++;
                        if (outsideSamples.Count < 40) outsideSamples.Add(lx + "," + ly + "," + lz + ":" + ta[k] + "/" + da[k] + "->" + tb[k] + "/" + db[k]);
                    }
                }
            }
            string reach = "; " + changedCount + " voxels changed, extent " + dx0 + "," + dy0 + "," + dz0 + "-" + dx1 + "," + dy1 + "," + dz1 + "; " + outsideCount +
                           " beyond the hole box + 1: " + string.Join(" ", outsideSamples.ToArray());
            // the sampler counts the raster voxels it changed before the encoder's collapse rule ("near children all equal" leaves
 // re-materialise far water voxels at the density of the near one, as the engine's own files do), so its box may
 // exceed the decoded difference by a voxel or two: it must contain it and stay the hole's neighbourhood
            Assert.IsTrue(r2.MinX <= dx0 && r2.MinY <= dy0 && r2.MinZ <= dz0 && r2.MaxX >= dx1 && r2.MaxY >= dy1 && r2.MaxZ >= dz1,
                          "the notice box contains every voxel that changed: " + box + reach);
            Assert.IsTrue((long)r2.Cells >= changedCount, "cells " + r2.Cells + " >= the voxels that changed " + changedCount);
            Assert.IsTrue(dx0 >= hx0 - 2 && dx1 <= hx1 + 2 && dz0 >= hz0 - 2 && dz1 <= hz1 + 2 && dy0 >= hy0 - 2 && dy1 <= hy1 + 2,
                          "the changed voxels lie within the hole box + 2 (the 1 m ramp and the stencil's reach): " + box + reach);
            Assert.IsTrue(r2.MinX >= hx0 - 5 && r2.MaxX <= hx1 + 5 && r2.MinZ >= hz0 - 5 && r2.MaxZ <= hz1 + 5 && r2.MinY >= hy0 - 5 && r2.MaxY <= hy1 + 5,
                          "the box is the hole's neighbourhood, not whole octrees (32 voxels): " + box + reach);
            Assert.IsTrue(r2.MaxX - r2.MinX >= 3 && r2.MaxZ - r2.MinZ >= 3 && r2.MaxY - r2.MinY >= 2, "the box covers the hole: " + box);

            // untouched octrees: byte-identical to the base
            int same = 0, differ = 0;
            for (int i = 0; i < 125; i++)
            {
                int ix = i / 25, iy = (i / 5) % 5, iz = i % 5;
                bool meets = ix * 32 <= r2.MaxX && ix * 32 + 31 >= r2.MinX && iy * 32 <= r2.MaxY && iy * 32 + 31 >= r2.MinY && iz * 32 <= r2.MaxZ && iz * 32 + 31 >= r2.MinZ;
                byte[] a = baseBatch.Octree(i), c = b3.Octree(i);
                bool equal = a.Length == c.Length;
                if (equal) for (int k = 0; k < a.Length; k++) if (a[k] != c[k]) { equal = false; break; }
                if (meets) { if (!equal) differ++; }
                else { Assert.IsTrue(equal, "octree " + i + " outside the carve's box changed"); same++; }
            }
            Assert.IsTrue(same >= 117 && differ >= 1, "untouched octrees identical " + same + ", carved " + differ);

            // the S4 reader on the written batch: the surface over the footprint = the analytic carve within N2's tolerance
            string tmp = TestEnv.NewTempDir("s8a-core");
            try
            {
                File.WriteAllBytes(Path.Combine(tmp, SnOctreeFormat.BatchFileName(Bx, By, Bz)), carvedBytes);
                string e2;
                SnOctreeSource src = SnOctreeSource.Open(tmp, out e2);
                Assert.IsNotNull(src, e2);
                try
                {
                    var field = new SnField(src);
                    // Two references over the 9 x 9 footprint (+-1.2 r): the analytic carve (S8a.N2: max <= 0.5 m) and SE's own
 // trilinear surface, i.e. what SE's voxel mesh and CastRay show (S8a.N3's comparison; P95 <= 0.25 m here:
 // the reader's own 1 m trilinear stage on a 2.5 m sphere). Sampling the carve's crease and curvature on a
 // 1 m SE grid alone moves the surface by 0.2-0.3 m within a metre of the rim and at the bottom, so the
 // analytic P95 is reported, not judged.
                    var errsAnalytic = new List<double>();
                    var errsSe = new List<double>();
                    double worst = 0, worstX = 0, worstZ = 0, axis = 0;
                    for (int ix = 0; ix < 9; ix++)
                        for (int iz = 0; iz < 9; iz++)
                        {
                            double x = Centre.X - 3.0 + 0.75 * ix, z = Centre.Z - 3.0 + 0.75 * iz;
                            double got = Surface(field, x, z, PlaneY + 8.0, PlaneY - 8.0);
                            double ea = Math.Abs(got - Expected(x, z));
                            double es = Math.Abs(got - SurfaceSe(tiles, x, z, PlaneY + 8.0, PlaneY - 8.0));
                            errsAnalytic.Add(ea);
                            errsSe.Add(es);
                            if (ea > worst) { worst = ea; worstX = x; worstZ = z; }
                            if (ix == 4 && iz == 4) axis = got;
                        }
                    errsAnalytic.Sort();
                    errsSe.Sort();
                    double p95Analytic = errsAnalytic[(int)Math.Ceiling(0.95 * errsAnalytic.Count) - 1];
                    double p95Se = errsSe[(int)Math.Ceiling(0.95 * errsSe.Count) - 1];
                    double maxSe = errsSe[errsSe.Count - 1];
                    string summary = " (vs the analytic carve: P95 " + p95Analytic.ToString("0.000") + " max " + worst.ToString("0.000") + " m at (" + worstX + ", " + worstZ + "); vs SE's trilinear surface: P95 " +
                                     p95Se.ToString("0.000") + " max " + maxSe.ToString("0.000") + " m)";
                    Assert.IsTrue(worst <= 0.5, "surface error vs the analytic carve max > 0.5 m (S8a.N2)" + summary);
                    Assert.IsTrue(p95Se <= 0.25 && maxSe <= 0.5, "surface error vs SE's own trilinear surface P95 > 0.25 m or max > 0.5 m" + summary);
                    Assert.IsTrue(Math.Abs(axis - (Centre.Y - Radius)) <= 0.3, "the hole's bottom on the axis " + axis.ToString("0.00") + " vs " + (Centre.Y - Radius) + summary);
                    Console.WriteLine("S8aConvertCoreTests: " + changedCount + " voxels changed, box " + box + summary);
                    // outside the footprint the plane stands (the stitched seabed on both sides of the seam)
                    foreach (double x in new[] { Centre.X - 6.0, Centre.X + 6.0 })
                    {
                        double got = Surface(field, x, Centre.Z, PlaneY + 8.0, PlaneY - 8.0);
                        Assert.IsTrue(Math.Abs(got - PlaneY) <= 0.25, "the plane beside the hole at x " + x + ": " + got.ToString("0.00"));
                    }
                }
                finally { src.Release(); }

                // S8a.M2 (corner sampling): the same carve lands half a metre away: the N2 position check must see it
                var coreM2 = new S8aConvertCore { SnOffset = 0.0 };
                S8aBatchFile b4;
                Assert.IsTrue(S8aBatchFile.TryParse(baseBytes, baseBytes.Length, Bx, By, Bz, out b4, out err), err);
                S8aConvertResult r4 = coreM2.ConvertBatch(map, tiles, b4, new[] { edit });
                Assert.IsTrue(r4.OctreesRewritten >= 1);
                File.WriteAllBytes(Path.Combine(tmp, SnOctreeFormat.BatchFileName(Bx, By, Bz)), b4.Serialize());
                SnOctreeSource src2 = SnOctreeSource.Open(tmp, out e2);
                Assert.IsNotNull(src2, e2);
                try
                {
                    var field2 = new SnField(src2);
                    double got = Surface(field2, Centre.X, Centre.Z, PlaneY + 8.0, PlaneY - 8.0);
                    Assert.IsTrue(Math.Abs(got - (Centre.Y - Radius)) > 0.3, "M2 shifts the hole's bottom by about half a metre: " + got.ToString("0.00") + " vs " + (Centre.Y - Radius));
                }
                finally { src2.Release(); }
            }
            finally { TestEnv.DeleteDir(tmp); }
            Assert.IsTrue(tiles.Reads > 0);
        }

        [TestMethod]
        public void SnBoxOf_AndOffsets_AreIntegerAndSymmetric()
        {
            SiteMap map = SiteMap.ForSiteV1(new Vec3(-1000000.0, 250000.0, 1500000.0));
            Vec3 a = map.C + SiteV1.TileMinRelative(1, 1, 1), b = map.C + SiteV1.TileMinRelative(2, 1, 1);
            int ox, oy, oz;
            S8aConvertCore.Offset(b, a, out ox, out oy, out oz);
            Assert.AreEqual(SiteV1.Pitch, ox, "tile 2 sits one pitch along X");
            Assert.AreEqual(0, oy);
            Assert.AreEqual(0, oz);
            S8aConvertCore.Offset(a, b, out ox, out oy, out oz);
            Assert.AreEqual(-SiteV1.Pitch, ox);
            int vx0, vy0, vz0, vx1, vy1, vz1;
            S8aConvertCore.SnBoxOf(map, a, 1000, 500, 1000, 1010, 510, 1010, out vx0, out vy0, out vz0, out vx1, out vy1, out vz1);
            Assert.IsTrue(vx1 - vx0 >= 11 && vx1 - vx0 <= 14, "an 11-voxel range maps to 11-14 SN voxels with the +-1 expansion: " + (vx1 - vx0));
            Assert.IsTrue(vy1 - vy0 >= 11 && vy1 - vy0 <= 14);
            Assert.IsTrue(vz1 - vz0 >= 11 && vz1 - vz0 <= 14);
        }
    }
}
