using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S8a converter core, part 2 (CONTRACT §3.3 "Conversion", §5.4 formats convert.sample, convert.copyonwrite,
 /// convert.material, biome.rule): the scalar rules against the Python twin's vectors (tests/s8a/vectors/convert.tsv),
 /// the column form of F⁻¹ bit for bit against <see cref="SiteMap.SnToSe"/>, and the field sampler on a synthetic SE
 /// content buffer holding a sphere surface at SN y −20 (the analytic density per voxel, the stencil-outside rule, the
 /// copy-on-write stencil test and the kept base type).
 /// </summary>
    [TestClass]
    public class S8aConvertTests
    {
        [TestMethod]
        public void Vectors_SampleContentCentreMaterialBiome()
        {
            int samples = 0, contents = 0, centres = 0, materials = 0, biomes = 0;
            foreach (string[] f in S8aVectors.Lines("convert.tsv"))
            {
                switch (f[0])
                {
                    case "sample":
                    {
                        byte t, d;
                        S8aConvert.EncodeSample(S8aVectors.D(f[1]), byte.Parse(f[2]), out t, out d);
                        Assert.AreEqual(S8aVectors.I(f[3]), t, "type of " + string.Join(" ", f));
                        Assert.AreEqual(S8aVectors.I(f[4]), d, "density of " + string.Join(" ", f));
                        samples++;
                        break;
                    }
                    case "content":
                        Assert.AreEqual(f[2], S8aVectors.Bits(S8aConvert.ContentToD(byte.Parse(f[1]))), "content " + f[1]);
                        contents++;
                        break;
                    case "centre":
                    {
                        Vec3 p = S8aConvert.SampleCentreSn(S8aVectors.I(f[1]), S8aVectors.I(f[2]), S8aVectors.I(f[3]), S8aVectors.D(f[4]));
                        Assert.AreEqual(f[5], S8aVectors.Bits(p.X), string.Join(" ", f));
                        Assert.AreEqual(f[6], S8aVectors.Bits(p.Y), string.Join(" ", f));
                        Assert.AreEqual(f[7], S8aVectors.Bits(p.Z), string.Join(" ", f));
                        centres++;
                        break;
                    }
                    case "material":
                        Assert.AreEqual(S8aVectors.I(f[2]), S8aMaterialMap.SnType(f[1]), f[1]);
                        materials++;
                        break;
                    case "biome":
                        Assert.AreEqual(S8aVectors.I(f[3]), S8aMaterialMap.BiomeOf(f[1] == "-" ? null : f[1], S8aVectors.D(f[2])), string.Join(" ", f));
                        biomes++;
                        break;
                    default:
                        Assert.Fail("unknown row " + f[0]);
                        break;
                }
            }
            Assert.AreEqual(17 * 3, samples);
            Assert.AreEqual(256, contents);
            Assert.AreEqual(10, centres);
            Assert.AreEqual(19, materials);                   // Stage C: 7 surface + 9 ore rows + 3 others
            Assert.AreEqual(11, biomes);
        }

        [TestMethod]
        public void EncodeSample_Ends_AndNaN()
        {
            byte t, d;
            S8aConvert.EncodeSample(1.0, 5, out t, out d); Assert.AreEqual(5, t); Assert.AreEqual(0, d);
            S8aConvert.EncodeSample(-1.0, 5, out t, out d); Assert.AreEqual(0, t); Assert.AreEqual(0, d);
            S8aConvert.EncodeSample(0.0, 5, out t, out d); Assert.AreEqual(5, t); Assert.AreEqual(126, d);
            S8aConvert.EncodeSample(-1e-9, 5, out t, out d); Assert.AreEqual(0, t); Assert.AreEqual(125, d);
            S8aConvert.EncodeSample(0.999, 5, out t, out d); Assert.AreEqual(5, t); Assert.AreEqual(251, d);
            S8aConvert.EncodeSample(-0.995, 5, out t, out d); Assert.AreEqual(0, t); Assert.AreEqual(0, d, "below -125/126 a near voxel is far empty");
            S8aConvert.EncodeSample(double.NaN, 5, out t, out d); Assert.AreEqual(0, t); Assert.AreEqual(0, d, "NaN = far empty");
            Assert.AreEqual(-1.0, S8aConvert.ContentToD(0));
            Assert.AreEqual(1.0, S8aConvert.ContentToD(255));
            Assert.AreEqual(0.0, S8aConvert.ContentToD(127) + S8aConvert.ContentToD(128), 1e-15, "127 and 128 straddle the surface");
        }

        [TestMethod]
        public void VoxelOf_FloorDiv_BatchOfVoxel()
        {
            int x, y, z;
            S8aConvert.VoxelOf(new Vec3(-2048, -3040, -2048), out x, out y, out z);
            Assert.AreEqual(0, x); Assert.AreEqual(0, y); Assert.AreEqual(0, z);
            S8aConvert.VoxelOf(new Vec3(-2047.9, -3039.5, 0.0), out x, out y, out z);
            Assert.AreEqual(0, x); Assert.AreEqual(0, y); Assert.AreEqual(2048, z);
            S8aConvert.VoxelOf(new Vec3(-2048.1, 0, 0), out x, out y, out z);
            Assert.AreEqual(-1, x);
            Assert.AreEqual(-1, S8aConvert.FloorDiv(-1, 160));
            Assert.AreEqual(-1, S8aConvert.FloorDiv(-160, 160));
            Assert.AreEqual(-2, S8aConvert.FloorDiv(-161, 160));
            Assert.AreEqual(0, S8aConvert.FloorDiv(159, 160));
            Assert.AreEqual(1, S8aConvert.BatchOfVoxel(160));
            Assert.AreEqual(12, S8aConvert.BatchOfVoxel(2048));
            Assert.AreEqual(25, S8aConvert.BatchOfVoxel(4095));
            Assert.AreEqual(26, S8aConvert.BatchOfVoxel(4160));
        }

        [TestMethod]
        public void ColumnForm_EqualsSiteMapSnToSe_BitForBit()
        {
            SiteMap m = SiteMap.SiteV1Test;
            var rng = new Random(8);
            for (int i = 0; i < 2000; i++)
            {
                double x = (rng.NextDouble() - 0.5) * 9000.0, z = (rng.NextDouble() - 0.5) * 9000.0;
                double y = -3040.0 + rng.NextDouble() * 3200.0;
                Vec3 u = S8aConvert.ColumnDir(m, x, z);
                Vec3 a = S8aConvert.SnToSeOnColumn(m, u, y);
                Vec3 b = m.SnToSe(new Vec3(x, y, z));
                Assert.AreEqual(b.X, a.X, 0.0, "x at " + i);
                Assert.AreEqual(b.Y, a.Y, 0.0, "y at " + i);
                Assert.AreEqual(b.Z, a.Z, 0.0, "z at " + i);
            }
            // the sea point of the SN origin is C + U0 * Rs
            Vec3 o = S8aConvert.SnToSeOnColumn(m, S8aConvert.ColumnDir(m, 0, 0), 0.0);
            Vec3 e = m.C + m.U0 * m.Rs;
            Assert.AreEqual(0.0, (o - e).Length, 1e-9);
        }

        [TestMethod]
        public void Trilinear_CornersAndOrder()
        {
            Assert.AreEqual(1.0, S8aConvert.Trilinear(1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0));
            Assert.AreEqual(2.0, S8aConvert.Trilinear(1, 2, 3, 4, 5, 6, 7, 8, 1, 0, 0), "dx = 1");
            Assert.AreEqual(3.0, S8aConvert.Trilinear(1, 2, 3, 4, 5, 6, 7, 8, 0, 1, 0), "dy = 2");
            Assert.AreEqual(5.0, S8aConvert.Trilinear(1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 1), "dz = 4");
            Assert.AreEqual(8.0, S8aConvert.Trilinear(1, 2, 3, 4, 5, 6, 7, 8, 1, 1, 1));
            Assert.AreEqual(4.5, S8aConvert.Trilinear(1, 2, 3, 4, 5, 6, 7, 8, 0.5, 0.5, 0.5), 1e-12);
        }

        // ---------------------------------------------------------------------------------------------- the sampler

        private const double SurfaceSnY = -20.0;
        private const byte SeMaterialIndex = 3, SnTypeOfIt = 150;

        /// <summary>A sampler over a synthetic SE buffer: content from the signed distance to the sphere of SN altitude −20 (positive inside), material index 3.</summary>
        private static S8aFieldSampler MakeSampler(SiteMap m, int ox, int oy, int oz, out Vec3 corner, out int minX, out int minY, out int minZ, out int sx, out int sy, out int sz)
        {
            corner = m.SnToSe(new Vec3(SnWorld.OriginX + 32 * ox, SnWorld.OriginY + 32 * oy, SnWorld.OriginZ + 32 * oz)) - new Vec3(40, 40, 40);
            int maxX, maxY, maxZ;
            S8aFieldSampler.PreimageBox(m, 32 * ox, 32 * oy, 32 * oz, 32, corner, 1, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
            sx = maxX - minX + 1; sy = maxY - minY + 1; sz = maxZ - minZ + 1;
            Assert.IsTrue(sx >= 34 && sx <= 40 && sy >= 34 && sy <= 40 && sz >= 34 && sz <= 40, "preimage box " + sx + "x" + sy + "x" + sz);
            var content = new byte[sx * sy * sz];
            var material = new byte[sx * sy * sz];
            double rSurface = m.Rs + SurfaceSnY;
            for (int z = 0; z < sz; z++)
                for (int y = 0; y < sy; y++)
                    for (int x = 0; x < sx; x++)
                    {
                        var w = new Vec3(corner.X + minX + x, corner.Y + minY + y, corner.Z + minZ + z);
                        double dist = rSurface - (w - m.C).Length;                 // positive inside the sphere (below the seabed)
                        double c = Math.Round((Math.Max(-1.0, Math.Min(1.0, dist)) + 1.0) * 127.5);
                        int i = x + sx * (y + sy * z);
                        content[i] = (byte)Math.Max(0, Math.Min(255, c));
                        material[i] = SeMaterialIndex;
                    }
            var s = new S8aFieldSampler { Map = m };
            s.SetBuffer(content, material, sx, sy, sz, minX, minY, minZ, corner);
            var table = new byte[256];
            for (int i = 0; i < 256; i++) table[i] = S8aMaterialMap.Fallback;
            table[SeMaterialIndex] = SnTypeOfIt;
            s.MaterialTable = table;
            return s;
        }

        /// <summary>Ores as voxels (S8a.NO.voxel, S8a.M30): the census counts the ore surface voxels and their types; M30 writes them as rock.</summary>
        [TestMethod]
        public void Sampler_OreCensus_AndM30()
        {
            SiteMap m = SiteMap.SiteV1Test;
            const int ox = 64, oy = 94, oz = 64;
            Vec3 corner; int minX, minY, minZ, sx, sy, sz;
            S8aFieldSampler s = MakeSampler(m, ox, oy, oz, out corner, out minX, out minY, out minZ, out sx, out sy, out sz);
            byte[] saved = S8aMaterialMap.OreCanon;
            try
            {
                var oc = new byte[256];
                oc[SeMaterialIndex] = SnTypeOfIt;                       // the test's material stands for an ore here
                S8aMaterialMap.OreCanon = oc;
                long s0 = S8aFieldSampler.OreSurfaceVoxels, t0 = S8aFieldSampler.OreTypedVoxels;
                var t = new byte[S8aOctree.Voxels];
                var d = new byte[S8aOctree.Voxels];
                s.SampleOctree(ox, oy, oz, t, d, false);
                long surface = S8aFieldSampler.OreSurfaceVoxels - s0, typed = S8aFieldSampler.OreTypedVoxels - t0;
                Assert.IsTrue(surface > 100, "ore surface voxels " + surface);
                Assert.AreEqual(surface, typed, "every ore surface voxel carries the ore's type");
                S8aWireMutants.Set("S8a.M30", true);
                long s1 = S8aFieldSampler.OreSurfaceVoxels, t1 = S8aFieldSampler.OreTypedVoxels;
                var t2 = new byte[S8aOctree.Voxels];
                var d2 = new byte[S8aOctree.Voxels];
                s.SampleOctree(ox, oy, oz, t2, d2, false);
                Assert.AreEqual(surface, S8aFieldSampler.OreSurfaceVoxels - s1, "the same voxels under M30");
                Assert.AreEqual(0L, S8aFieldSampler.OreTypedVoxels - t1, "M30: none carries the ore's type");
                for (int i = 0; i < t2.Length; i++)
                    if (t2[i] != 0) Assert.AreEqual(S8aMaterialMap.Fallback, t2[i], "M30 writes rock");
            }
            finally
            {
                S8aWireMutants.ClearAll();
                S8aMaterialMap.OreCanon = saved;
            }
        }

        /// <summary>Planet v2 route B: inside a placed chunk solid voxels take the chunk's native type; an ore material keeps its ore type.</summary>
        [TestMethod]
        public void Sampler_NativeType_ForSolidVoxels_NotForOres()
        {
            SiteMap m = SiteMap.SiteV1Test;
            const int ox = 64, oy = 94, oz = 64;
            Vec3 corner; int minX, minY, minZ, sx, sy, sz;
            S8aFieldSampler s = MakeSampler(m, ox, oy, oz, out corner, out minX, out minY, out minZ, out sx, out sy, out sz);
            byte[] saved = S8aMaterialMap.OreCanon;
            try
            {
                S8aMaterialMap.OreCanon = new byte[256];
                s.NativeType = w => (byte)45;                           // RedSeaweedTall03 everywhere the chunk lies
                var t = new byte[S8aOctree.Voxels];
                var d = new byte[S8aOctree.Voxels];
                s.SampleOctree(ox, oy, oz, t, d, false);
                int solid = 0;
                for (int i = 0; i < t.Length; i++)
                    if (t[i] != 0) { solid++; Assert.AreEqual(45, t[i], "a solid voxel takes the native type"); }
                Assert.IsTrue(solid > 100);
                var oc = new byte[256];
                oc[SeMaterialIndex] = SnTypeOfIt;                       // now the material is an ore: its type wins
                S8aMaterialMap.OreCanon = oc;
                s.SampleOctree(ox, oy, oz, t, d, false);
                for (int i = 0; i < t.Length; i++)
                    if (t[i] != 0) Assert.AreEqual(SnTypeOfIt, t[i], "an ore keeps its ore type");
            }
            finally
            {
                S8aMaterialMap.OreCanon = saved;
                s.NativeType = null;
            }
        }

        [TestMethod]
        public void Sampler_SphereSurface_EveryVoxelClassifiedAndDensityWithinQuantisation()
        {
            SiteMap m = SiteMap.SiteV1Test;
            const int ox = 64, oy = 94, oz = 64;                   // SN x 0..32, y −32..−1, z 0..32
            Vec3 corner; int minX, minY, minZ, sx, sy, sz;
            S8aFieldSampler s = MakeSampler(m, ox, oy, oz, out corner, out minX, out minY, out minZ, out sx, out sy, out sz);
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            int n = s.SampleOctree(ox, oy, oz, t, d, false);
            Assert.AreEqual(S8aOctree.Voxels, n);
            Assert.AreEqual(0, s.LastOutside, "the preimage box covers every stencil");
            Assert.AreEqual(S8aOctree.Voxels, s.LastSampled);
            // the oracle, written from the contract text ("convert.sample"): P = Origin + v + 0.5; w = F⁻¹(P); D = trilinear
 // over the 8 LOD-0 samples content/127.5 − 1 around w − corner (SE's content is the clamped distance, so the
 // blend of clamped corners is the truth, not the clamp of the distance at P); s = clamp(D); the encoding rule
            double rSurface = m.Rs + SurfaceSnY;
            int near = 0, far = 0;
            for (int z = 0; z < 32; z++)
                for (int x = 0; x < 32; x++)
                    for (int y = 0; y < 32; y++)
                    {
                        Vec3 p = S8aConvert.SampleCentreSn(32 * ox + x, 32 * oy + y, 32 * oz + z);
                        Vec3 w = m.SnToSe(p) - corner;
                        double fx = w.X - Math.Floor(w.X), fy = w.Y - Math.Floor(w.Y), fz = w.Z - Math.Floor(w.Z);
                        var c = new double[8];
                        for (int k = 0; k < 8; k++)
                        {
                            var q = new Vec3(corner.X + Math.Floor(w.X) + (k & 1), corner.Y + Math.Floor(w.Y) + ((k >> 1) & 1), corner.Z + Math.Floor(w.Z) + ((k >> 2) & 1));
                            double dist = Math.Max(-1.0, Math.Min(1.0, rSurface - (q - m.C).Length));
                            byte content = (byte)Math.Max(0, Math.Min(255, Math.Round((dist + 1.0) * 127.5)));
                            c[k] = content / 127.5 - 1.0;
                        }
                        double dd = S8aConvert.Trilinear(c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7], fx, fy, fz);
                        byte wt, wd;
                        S8aConvert.EncodeSample(dd, SnTypeOfIt, out wt, out wd);
                        int i = S8aOctree.Index(x, y, z);
                        string at = "voxel " + x + "," + y + "," + z + " snY " + p.Y;
                        Assert.AreEqual(wt, t[i], at + " type");
                        Assert.AreEqual(wd, d[i], at + " density");
                        // the coarse truth: well past the band every voxel is far; at the surface every voxel is near
                        double sTrue = SurfaceSnY - p.Y;
                        if (sTrue >= 2.0) { Assert.AreEqual(SnTypeOfIt, t[i], at + " far solid"); Assert.AreEqual(0, d[i], at + " far solid"); far++; }
                        else if (sTrue <= -2.0) { Assert.AreEqual(0, t[i], at + " far empty"); Assert.AreEqual(0, d[i], at + " far empty"); far++; }
                        else if (Math.Abs(sTrue) <= 0.5)
                        {
                            near++;
                            Assert.IsTrue(d[i] != 0, at + " near");
                            int want = (int)Math.Floor(sTrue * 126.0) + 126;
                            Assert.IsTrue(Math.Abs(d[i] - want) <= 12, at + " density " + d[i] + " vs " + want + " (corner quantisation + the clamped corners: <= 0.0625 + 1/127.5 in s)");
                            Assert.AreEqual(sTrue >= 0 ? SnTypeOfIt : 0, t[i], at + " near type");
                        }
                    }
            Assert.IsTrue(near >= 32 * 32 && near <= 2 * 32 * 32, "one or two voxel layers at the surface: " + near);
            Assert.IsTrue(far >= 26 * 32 * 32, "most of the octree is far: " + far);
            // the octree encodes to a proper surface octree, census clean
            byte[] nodes = S8aOctree.EncodeOctree(t, d);
            Assert.IsTrue(nodes.Length / 4 > 100 && nodes.Length / 4 < 37449, "nodes " + nodes.Length / 4);
            var st = new S8aRuleStats();
            S8aOctree.CheckRules(nodes, 0, nodes.Length / 4, st);
            Assert.IsTrue(st.Clean, st.ToString());
        }

        [TestMethod]
        public void Sampler_MeshBox_FarRockTypesAreNotMeshChanges_ASurfaceTypeIs()
        {
            // a test run (S8a.ND.live): a second sample whose deep rock took another SE material changes voxels (the file) but none the
 // level-0 mesh shows; then a surface material change is a mesh change, boxed at the surface
            SiteMap m = SiteMap.SiteV1Test;
            const int ox = 64, oy = 94, oz = 64;                   // SN y -32..-1, the surface at -20
            Vec3 corner; int minX, minY, minZ, sx, sy, sz;
            S8aFieldSampler s = MakeSampler(m, ox, oy, oz, out corner, out minX, out minY, out minZ, out sx, out sy, out sz);
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            s.SampleOctree(ox, oy, oz, t, d, false);
            var content = new byte[sx * sy * sz];
            var material = new byte[sx * sy * sz];
            double rSurface = m.Rs + SurfaceSnY;
            for (int z = 0; z < sz; z++)
                for (int y = 0; y < sy; y++)
                    for (int x = 0; x < sx; x++)
                    {
                        var w = new Vec3(corner.X + minX + x, corner.Y + minY + y, corner.Z + minZ + z);
                        double dist = rSurface - (w - m.C).Length;
                        int i = x + sx * (y + sy * z);
                        content[i] = (byte)Math.Max(0, Math.Min(255, Math.Round((Math.Max(-1.0, Math.Min(1.0, dist)) + 1.0) * 127.5)));
                        material[i] = dist > 4.0 ? (byte)4 : SeMaterialIndex;          // the rock deeper than 4 m: SE material 4
                    }
            s.SetBuffer(content, material, sx, sy, sz, minX, minY, minZ, corner);
            var table = new byte[256];
            for (int i = 0; i < 256; i++) table[i] = S8aMaterialMap.Fallback;
            table[SeMaterialIndex] = SnTypeOfIt;
            table[4] = 160;
            s.MaterialTable = table;
            s.SampleOctree(ox, oy, oz, t, d, false);
            Assert.IsTrue(s.LastChanged > 1000, "deep rock voxels changed type: " + s.LastChanged);
            Assert.AreEqual(0, s.LastMeshChanged, "none the level-0 mesh shows");
            Assert.IsTrue(s.LastMeshMinX > s.LastMeshMaxX, "no mesh box");
            // the surface's SE material changes: the near voxels change type: mesh changes at the surface (SN y -22.. -18)
            for (int i = 0; i < material.Length; i++) if (material[i] == SeMaterialIndex) material[i] = 5;
            table[5] = 141;
            s.MaterialTable = table;
            s.SampleOctree(ox, oy, oz, t, d, false);
            Assert.IsTrue(s.LastMeshChanged >= 32 * 32, "the surface layer: " + s.LastMeshChanged);
            Assert.IsTrue(s.LastMeshMinY >= 32 - 22 - 2 + 0 && s.LastMeshMaxY <= 32 - 18 + 2, "mesh box y " + s.LastMeshMinY + ".." + s.LastMeshMaxY);
            Assert.IsTrue(S8aFieldSampler.FacesTheMesh(t, d, 5, 32 - 20 - 2, 5) || d[S8aOctree.Index(5, 32 - 20 - 1, 5)] != 0, "a voxel under the surface faces it");
        }

        [TestMethod]
        public void Sampler_BinaryCutWall_WrittenNear_KeepsItsTypeThroughTheCollapse()
        {
            // a test run (S8a.N10.ore): SE's binary cut (content 0 above SN y -10, 255 below), the wall's material (3) 4 m deep, another
 // (4) below: the first solid voxel under the cut is near at WallDensity with the wall's type, and after the octree's
 // collapse rule (encode, decode) every wall voxel still carries it
            SiteMap m = SiteMap.SiteV1Test;
            const int ox = 64, oy = 94, oz = 64;
            Vec3 corner; int minX, minY, minZ, sx, sy, sz;
            S8aFieldSampler s = MakeSampler(m, ox, oy, oz, out corner, out minX, out minY, out minZ, out sx, out sy, out sz);
            var content = new byte[sx * sy * sz];
            var material = new byte[sx * sy * sz];
            double rCut = m.Rs - 10.0;
            for (int z = 0; z < sz; z++)
                for (int y = 0; y < sy; y++)
                    for (int x = 0; x < sx; x++)
                    {
                        var w = new Vec3(corner.X + minX + x, corner.Y + minY + y, corner.Z + minZ + z);
                        double depth = rCut - (w - m.C).Length;
                        int i = x + sx * (y + sy * z);
                        content[i] = depth > 0 ? (byte)255 : (byte)0;
                        material[i] = depth > 4.0 ? (byte)4 : SeMaterialIndex;
                    }
            s.SetBuffer(content, material, sx, sy, sz, minX, minY, minZ, corner);
            var table = new byte[256];
            for (int i = 0; i < 256; i++) table[i] = S8aMaterialMap.Fallback;
            table[SeMaterialIndex] = SnTypeOfIt;
            table[4] = 160;
            s.MaterialTable = table;
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            bool was = S8aFieldSampler.WallNear;
            try
            {
                S8aFieldSampler.WallNear = true;
                // the SN samples shifted against SE's grid so the transition voxel falls on the empty side (s < 0) and the one under it
 // is far-solid: a wall (with the default 0.5 here the transition voxel is near-solid everywhere)
                int best = -1;
                foreach (double off in new[] { 0.0, 0.25, 0.75, 0.9 })
                {
                    s.SnOffset = off;
                    s.SampleOctree(ox, oy, oz, t, d, false);
                    if (s.LastWalls > 0) { best = (int)(off * 100); break; }
                }
                Assert.IsTrue(best >= 0, "an offset that puts walls in this octree");
                {
                }
                int farSolid = 0, empty = 0, near = 0;
                for (int i = 0; i < t.Length; i++) { if (t[i] == 0) empty++; else if (d[i] == 0) farSolid++; else near++; }
                Assert.IsTrue(s.LastWalls >= 32 * 32 / 2, "walls " + s.LastWalls + " far " + farSolid + " empty " + empty + " near " + near + " sampled " + s.LastSampled);
                int walls = 0, bad = 0;
                byte[] nodes = S8aOctree.EncodeOctree(t, d);
                var t2 = new byte[S8aOctree.Voxels];
                var d2 = new byte[S8aOctree.Voxels];
                S8aOctree.DecodeOctree(nodes, 0, nodes.Length / 4, t2, d2);
                for (int z = 1; z < 31; z++)
                    for (int x = 1; x < 31; x++)
                        for (int y = 1; y < 31; y++)
                        {
                            int i = S8aOctree.Index(x, y, z);
                            if (t[i] == 0 || t[i + 32] != 0) continue;          // the first solid voxel under an empty one
                            walls++;
                            Assert.AreEqual(S8aFieldSampler.WallDensity, d[i], "wall density");
                            if (t2[i] != SnTypeOfIt) bad++;
                        }
                Assert.IsTrue(walls >= 30 * 30 / 2, "walls found " + walls);
                Assert.AreEqual(0, bad, "wall voxels whose type the collapse replaced");
                var st = new S8aRuleStats();
                S8aOctree.CheckRules(nodes, 0, nodes.Length / 4, st);
                Assert.IsTrue(st.Clean, st.ToString());
            }
            finally { S8aFieldSampler.WallNear = was; }
        }

        [TestMethod]
        public void Sampler_StencilOutsideTheBuffer_KeepsTheRasterValue()
        {
            SiteMap m = SiteMap.SiteV1Test;
            const int ox = 64, oy = 94, oz = 64;
            Vec3 corner; int minX, minY, minZ, sx, sy, sz;
            S8aFieldSampler s = MakeSampler(m, ox, oy, oz, out corner, out minX, out minY, out minZ, out sx, out sy, out sz);
            // a buffer shifted by 20 voxels in x: a part of the octree falls outside
            var content = new byte[sx * sy * sz];
            s.SetBuffer(content, null, sx, sy, sz, minX + 20, minY, minZ, corner);
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            for (int i = 0; i < t.Length; i++) { t[i] = 7; d[i] = 7; }
            s.SampleOctree(ox, oy, oz, t, d, false);
            Assert.IsTrue(s.LastOutside > 1000 && s.LastOutside < S8aOctree.Voxels, "outside " + s.LastOutside);
            Assert.AreEqual(S8aOctree.Voxels, s.LastOutside + s.LastSampled);
            int kept = 0;
            for (int i = 0; i < t.Length; i++) if (t[i] == 7 && d[i] == 7) kept++;
            Assert.AreEqual(s.LastOutside, kept, "every outside voxel keeps its raster value");
        }

        [TestMethod]
        public void Sampler_CopyOnWrite_RecomputesOnlyStencilsMeetingTheEditBox_AndKeepsASolidBaseType()
        {
            SiteMap m = SiteMap.SiteV1Test;
            const int ox = 64, oy = 94, oz = 64;
            Vec3 corner; int minX, minY, minZ, sx, sy, sz;
            S8aFieldSampler s = MakeSampler(m, ox, oy, oz, out corner, out minX, out minY, out minZ, out sx, out sy, out sz);
            var t = new byte[S8aOctree.Voxels];
            var d = new byte[S8aOctree.Voxels];
            for (int i = 0; i < t.Length; i++) { t[i] = 9; d[i] = 200; }                     // base: near solid, type 9
 // the edit: the stencil base of voxel (5, 8, 7) (snY −23.5: far below the surface)
            Vec3 p = S8aConvert.SampleCentreSn(32 * ox + 5, 32 * oy + 8, 32 * oz + 7);
            int bx, by, bz;
            s.StencilBase(m.SnToSe(p), out bx, out by, out bz);
            var boxes = new[] { bx, by, bz, bx + 1, by + 1, bz + 1 };
            Assert.AreEqual(0, s.SampleOctreeCopyOnWrite(ox, oy, oz, t, d, null, 0), "no boxes: nothing recomputed");
            int n = s.SampleOctreeCopyOnWrite(ox, oy, oz, t, d, boxes, 1);
            Assert.IsTrue(n >= 1 && n <= 64, "stencils meeting a 2x2x2 box: " + n);
            Assert.AreEqual(n, s.LastSampled);
            int changed = 0, kept = 0;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == 9 && d[i] == 200) kept++;
                else changed++;
            }
            Assert.AreEqual(S8aOctree.Voxels - n, kept, "every other voxel keeps its base value");
            int at = S8aOctree.Index(5, 8, 7);
            Assert.AreEqual(9, t[at], "a solid base keeps its type (T = base type)");
            Assert.AreEqual(0, d[at], "far solid");
            Assert.IsTrue(changed >= 1);
            // the same with a base that is not solid: T comes from the table
            for (int i = 0; i < t.Length; i++) { t[i] = 0; d[i] = 0; }
            s.SampleOctreeCopyOnWrite(ox, oy, oz, t, d, boxes, 1);
            Assert.AreEqual(SnTypeOfIt, t[at], "an empty base takes the mapped SE material");
            Assert.AreEqual(0, d[at]);
        }

        [TestMethod]
        public void PreimageBox_CoversEverySamplePointsStencil()
        {
            SiteMap m = SiteMap.SiteV1Test;
            var rng = new Random(3);
            for (int k = 0; k < 20; k++)
            {
                int ox = rng.Next(40, 90), oy = rng.Next(60, 100), oz = rng.Next(40, 90);
                Vec3 corner = m.SnToSe(new Vec3(SnWorld.OriginX + 32 * ox, SnWorld.OriginY + 32 * oy, SnWorld.OriginZ + 32 * oz)) - new Vec3(7.3, 11.1, 2.9);
                int minX, minY, minZ, maxX, maxY, maxZ;
                S8aFieldSampler.PreimageBox(m, 32 * ox, 32 * oy, 32 * oz, 32, corner, 1, out minX, out minY, out minZ, out maxX, out maxY, out maxZ);
                for (int i = 0; i < 200; i++)
                {
                    Vec3 p = S8aConvert.SampleCentreSn(32 * ox + rng.Next(32), 32 * oy + rng.Next(32), 32 * oz + rng.Next(32));
                    Vec3 w = m.SnToSe(p) - corner;
                    int fx = (int)Math.Floor(w.X), fy = (int)Math.Floor(w.Y), fz = (int)Math.Floor(w.Z);
                    Assert.IsTrue(fx >= minX && fx + 1 <= maxX && fy >= minY && fy + 1 <= maxY && fz >= minZ && fz + 1 <= maxZ, "octree " + ox + "," + oy + "," + oz);
                }
            }
        }

        [TestMethod]
        public void MaterialMap_TableAndSculptValues()
        {
            byte[] table = S8aMaterialMap.BuildTable(i => i == 3 ? "Stone_02" : i == 4 ? "Grass" : i == 5 ? "Ice_01" : null);
            Assert.AreEqual(150, table[3]);
            Assert.AreEqual(219, table[4]);
            Assert.AreEqual(84, table[5], "another material falls back to 84");
            Assert.AreEqual(84, table[0]);
            byte[] u = S8aMaterialMap.UniformTable(1);
            foreach (byte b in u) Assert.AreEqual(1, b);
            Assert.AreEqual(1, S8aMaterialMap.SculptValue("Sand_02"));
            Assert.AreEqual(6, S8aMaterialMap.SculptValue("Stone_05"));
            Assert.AreEqual(7, S8aMaterialMap.SculptValue("Grass"), "Stage C: the island's Grass is CustomMaterialTable row 7");
            Assert.AreEqual(0, S8aMaterialMap.SculptValue("Platinum_01"), "ores are no surface material");
            Assert.AreEqual(13, S8aMaterialMap.BiomeNames.Length, "Stage C: biome.names 0..12");
            Assert.AreEqual("void", S8aMaterialMap.BiomeNames[S8aMaterialMap.BiomeVoid]);
        }
    }
}
