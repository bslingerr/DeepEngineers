using System;
using System.IO;
using DeepEngineers.SE.Terrain;
using DeepEngineers.Terrain;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S4.N3 on the real data: the provider core of the SE plugin (ProviderCore over the Terrain
 /// core's TileSampler and SnField) reads the user's CompiledOctreesCache in place, read-only, through the tile that
 /// holds two measured spots (S3 real-run layer-30 hits: Safe Shallows SN (0, 0) at −17.212 m, the grandReef spot
 /// (−1029, −1116) at −235.138 m). ProviderSelfCheck (the same code as the in-game <c>de.terrain n3</c>) must find no
 /// violation, a radial line through the open water 10–20 m above the seabed must not block, and one through the
 /// seabed must. Inconclusive when the Subnautica install is absent (DEEPENG_SUBNAUTICA or the Steam default).
 /// </summary>
    [TestClass]
    public class S4RealOctreeTests
    {
        private static readonly SiteMap Map = SiteMap.SiteV1Test;
        private static readonly MaterialBands Bands = new MaterialBands(new byte[] { 10, 11, 12, 13, 14, 15, 16 });

        [TestCleanup]
        public void Cleanup()
        {
            Mutants.ClearAll();
        }

        private static SnOctreeSource OpenOrInconclusive()
        {
            string dir = SnSourceManifest.OctreeDirOf(SnSourceManifest.ResolveSubnauticaDir(null));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                Assert.Inconclusive("Subnautica's CompiledOctreesCache not found (" + (dir ?? "null") + "); set DEEPENG_SUBNAUTICA");
            string err;
            SnOctreeSource src = SnOctreeSource.Open(dir, out err);
            Assert.IsNotNull(src, err);
            return src;
        }

        /// <summary>The created tile whose interior holds SE world point <paramref name="w"/> (tile-local LOD-0 coordinates out).</summary>
        private static int[] TileOf(Vec3 w, out Vec3 local)
        {
            foreach (int[] t in SiteV1.TilesCreated())
            {
                Vec3 l = w - TerrainLattice.TileMin(Map.C, t[0], t[1], t[2]);
                if (l.X >= 32 && l.Y >= 32 && l.Z >= 32 && l.X < SiteV1.Tile - 32 && l.Y < SiteV1.Tile - 32 && l.Z < SiteV1.Tile - 32)
                {
                    local = l;
                    return t;
                }
            }
            foreach (int[] t in SiteV1.TilesCreated())
            {
                Vec3 l = w - TerrainLattice.TileMin(Map.C, t[0], t[1], t[2]);
                if (TerrainLattice.Inside(TerrainLattice.TileMin(Map.C, t[0], t[1], t[2]), w))
                {
                    local = l;
                    return t;
                }
            }
            local = default(Vec3);
            return null;
        }

        private static void CheckSpot(SnOctreeSource src, double x, double seabedY, double z, string what)
        {
            Vec3 local;
            int[] t = TileOf(Map.SnToSe(new Vec3(x, seabedY, z)), out local);
            Assert.IsNotNull(t, what + ": no created tile holds the spot");
            var core = new ProviderCore(new TileSampler(new SnField(src), Map, TerrainLattice.TileMin(Map.C, t[0], t[1], t[2]), Bands),
                                        t[0], t[1], t[2], SiteV1.TileEntityId(t[0], t[1], t[2]), null);
            SelfCheckResult r = ProviderSelfCheck.Run(core, 4546, 400, (int)local.X, (int)local.Y, (int)local.Z, 24);
            Console.WriteLine(what + ": tile " + t[0] + "," + t[1] + "," + t[2] + " focus (" + (int)local.X + "," + (int)local.Y + "," + (int)local.Z +
                              ") boxes " + r.Boxes + " disjoint/contains/intersects " + r.Disjoint + "/" + r.Contains + "/" + r.Intersects +
                              " lines " + r.LineChecks + " violations " + r.Violations + " " + (r.FirstViolation ?? ""));
            Assert.IsTrue(r.Pass, what + ": " + r.FirstViolation);

            // the radial line through the spot: open water 10..20 m above the seabed does not block (laser antennas, the
 // physics prefetch; nearer than that a slope within 1-2 m sideways is honestly Mixed); a line from 5 m above
 // to 20 m below the seabed does, and its interval holds the seabed
            Vec3 top = Map.SnToSe(new Vec3(x, seabedY + 20, z)) - TerrainLattice.TileMin(Map.C, t[0], t[1], t[2]);
            Vec3 low = Map.SnToSe(new Vec3(x, seabedY + 10, z)) - TerrainLattice.TileMin(Map.C, t[0], t[1], t[2]);
            double s, e;
            Assert.IsFalse(ProviderCore.IntersectLine(core, top.X, top.Y, top.Z, low.X, low.Y, low.Z, out s, out e), what + ": open water above the seabed blocks");
            Vec3 a = Map.SnToSe(new Vec3(x, seabedY + 5, z)) - TerrainLattice.TileMin(Map.C, t[0], t[1], t[2]);
            Vec3 b = Map.SnToSe(new Vec3(x, seabedY - 20, z)) - TerrainLattice.TileMin(Map.C, t[0], t[1], t[2]);
            Assert.IsTrue(ProviderCore.IntersectLine(core, a.X, a.Y, a.Z, b.X, b.Y, b.Z, out s, out e), what + ": the line through the seabed does not block");
            double tSeabed = 5.0 / 25.0;
            Assert.IsTrue(s <= tSeabed + 1e-9 && e >= tSeabed - 1e-9, what + ": the seabed lies outside [start, end] = [" + s + ", " + e + "]");
        }

        [TestMethod]
        public void N3_RealOctrees_SafeShallowsAndGrandReef_NoViolation()
        {
            SnOctreeSource src = OpenOrInconclusive();
            try
            {
                CheckSpot(src, 0, -17.212, 0, "Safe Shallows (0, 0)");
                CheckSpot(src, -1029, -235.138, -1116, "grandReef (-1029, -1116)");
            }
            finally { src.Release(); }
        }
    }
}
