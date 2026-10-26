using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>SiteV1 constants and siteHash against tools/worldmap/site_v1.py (CONTRACT section 3).</summary>
    [TestClass]
    public class SiteV1Tests
    {
        private static string WorldmapDir { get { return RepoEnv.PathIn("tools", "worldmap"); } }

        [TestMethod]
        public void SiteHash_IsTheContractValue()
        {
            string text = SiteV1.CanonicalRecordJson();
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            Console.WriteLine("canonical site record: " + bytes.Length + " bytes, siteHash " + SiteV1.HashHex(SiteV1.Fnv1a64(bytes)));
            Assert.AreEqual(1506, bytes.Length);
            Assert.AreEqual(SiteV1.SiteRecordBytes, bytes.Length);
            Assert.AreEqual(0x53ab8e77d68103abUL, SiteV1.SiteHash);
            Assert.AreEqual(SiteV1.SiteHash, SiteV1.ComputeSiteHash());
            Assert.AreEqual("0x53ab8e77d68103ab", SiteV1.HashHex(SiteV1.SiteHash));
        }

        [TestMethod]
        public void CanonicalRecordJson_IsBytewisePythonJsonDumps()
        {
            string py = RepoEnv.RunPython("-c",
                "import sys, json; sys.path.insert(0, sys.argv[1]); import site_v1; "
                + "sys.stdout.write(json.dumps(site_v1.record(), sort_keys=True, separators=(',', ':')))",
                WorldmapDir);
            Assert.AreEqual(py, SiteV1.CanonicalRecordJson());
        }

        [TestMethod]
        public void Fnv1a64_KnownVectors()
        {
            Assert.AreEqual(0xcbf29ce484222325UL, SiteV1.Fnv1a64(new byte[0]));
            Assert.AreEqual(0xaf63dc4c8601ec8cUL, SiteV1.Fnv1a64(Encoding.ASCII.GetBytes("a")));
            Assert.AreEqual(0x85944171f73967e8UL, SiteV1.Fnv1a64(Encoding.ASCII.GetBytes("foobar")));
        }

        [TestMethod]
        public void PyFloatRepr_MatchesPythonRepr()
        {
            double[] values =
            {
                0.0, -0.0, 1.0, -1.0, 0.1, 0.12, -0.05, 0.9, 2.0, 7.0, 80.0, 14400.0, 60000.0, 57000.0, 67200.0, -2000.0,
                -3040.0, 2048.0, 160.0, -1920.0, 60000.00390625, -3000.00390625, 1.0526316165924072, -1000000.0, 250000.0,
                1500000.0, 1e16, 1e15, 9999999999999998.0, 1e-4, 1e-5, 1.5e-7, 123456789.0, 1e22, 1e100, 5e-324,
                1.7976931348623157e308, 2.2250738585072014e-308, 0.3, 1.0 / 3.0, Math.PI, 1e-6, 1e-9, 1e-12, 0.001,
            };
            // exact bits as hex, so neither runtime's number formatting is involved in the input
            var arg = string.Join(",", values.Select(v => BitConverter.DoubleToInt64Bits(v).ToString("x16", CultureInfo.InvariantCulture)));
            string py = RepoEnv.RunPython("-c",
                "import sys, struct; sys.stdout.write(chr(10).join(repr(struct.unpack('>d', bytes.fromhex(x))[0]) for x in sys.argv[1].split(',')))",
                arg);
            string[] lines = py.Split('\n');
            Assert.AreEqual(values.Length, lines.Length);
            for (int i = 0; i < values.Length; i++)
            {
                string expected = lines[i].Trim();
                Assert.AreEqual(expected, SiteV1.PyFloatRepr(values[i]), "bits " + BitConverter.DoubleToInt64Bits(values[i]).ToString("x16"));
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(values[i]), BitConverter.DoubleToInt64Bits(PyFloat.Parse(expected)), "parse " + expected);
            }
        }

        /// <summary>PyFloat.Parse is correctly rounded where the.NET Framework parser is not (and agrees with Python on random 17-digit strings).</summary>
        [TestMethod]
        public void PyFloatParse_IsCorrectlyRounded()
        {
            Assert.AreEqual(0x3FDF5FABB1F4EE46L, BitConverter.DoubleToInt64Bits(PyFloat.Parse("0.490214275155925")));
            Assert.AreEqual(0.0, PyFloat.Parse("0e5"));
            Assert.AreEqual(-0.0, PyFloat.Parse("-0.0"));
            Assert.IsTrue(BitConverter.DoubleToInt64Bits(PyFloat.Parse("-0.0")) < 0);
            Assert.AreEqual(double.Epsilon, PyFloat.Parse("5e-324"));
            Assert.AreEqual(0.0, PyFloat.Parse("2e-324"));
            Assert.AreEqual(double.MaxValue, PyFloat.Parse("1.7976931348623157e308"));
            Assert.AreEqual(double.PositiveInfinity, PyFloat.Parse("1.8e308"));
            var rnd = new Random(1210014);
            var strings = new List<string>();
            for (int i = 0; i < 300; i++)
            {
                var sb = new StringBuilder();
                sb.Append(rnd.Next(1, 10));
                sb.Append('.');
                for (int k = 0; k < 16; k++) sb.Append(rnd.Next(0, 10));
                sb.Append('e').Append(rnd.Next(-320, 309));
                strings.Add(sb.ToString());
            }
            string py = RepoEnv.RunPython("-c",
                "import sys, struct; sys.stdout.write(chr(10).join(struct.pack('>d', float(x)).hex() for x in sys.argv[1].split(',')))",
                string.Join(",", strings));
            string[] lines = py.Split('\n');
            Assert.AreEqual(strings.Count, lines.Length);
            int frameworkOff = 0;
            for (int i = 0; i < strings.Count; i++)
            {
                long expected = Convert.ToInt64(lines[i].Trim(), 16);
                Assert.AreEqual(expected, BitConverter.DoubleToInt64Bits(PyFloat.Parse(strings[i])), strings[i]);
                if (BitConverter.DoubleToInt64Bits(double.Parse(strings[i], CultureInfo.InvariantCulture)) != expected) frameworkOff++;
            }
            Console.WriteLine("runtime double.Parse disagreed with Python on " + frameworkOff + " of " + strings.Count + " random 17-digit inputs; PyFloat.Parse on 0");
        }

        /// <summary>Each constant equals the corresponding field of <c>python -I tools/worldmap/site_v1.py record</c>.</summary>
        [TestMethod]
        public void Constants_EqualSiteV1PyRecord()
        {
            var rec = (Dictionary<string, object>)MiniJson.Parse(RepoEnv.RunPython(Path.Combine("tools", "worldmap", "site_v1.py"), "record"));
            Assert.AreEqual((double)SiteV1.SiteVersion, J.D(rec, "siteVersion"));

            var planet = J.Obj(rec, "planet");
            Assert.AreEqual(SiteV1.GeneratorSubtypeId, J.S(planet, "generatorSubtypeId"));
            Assert.AreEqual(SiteV1.StorageRadius, J.D(planet, "storageRadius_R"));
            Assert.AreEqual(SiteV1.HillMin, J.D(planet, "hillMin"));
            Assert.AreEqual(SiteV1.HillMax, J.D(planet, "hillMax"));
            Assert.AreEqual(SiteV1.SurfaceGravity, J.D(planet, "surfaceGravity"));
            Assert.AreEqual(SiteV1.GravityFalloffPower, J.D(planet, "gravityFalloffPower"));
            Assert.AreEqual(SiteV1.Heightmaps, J.S(planet, "heightmaps"));
            Assert.AreEqual(SiteV1.MinimumRadius, J.D(planet, "minimumRadius"));
            Assert.AreEqual(SiteV1.MaximumRadius, J.D(planet, "maximumRadius"));
            Assert.AreEqual(SiteV1.AverageRadius, J.D(planet, "averageRadius"));
            Assert.AreEqual(SiteV1.AtmosphereAltitude, J.D(planet, "atmosphereAltitude"));
            var atmo = J.Obj(planet, "atmosphere");
            Assert.AreEqual(SiteV1.AtmosphereBreathable, J.B(atmo, "breathable"));
            Assert.AreEqual(SiteV1.AtmosphereDensity, J.D(atmo, "density"));
            Assert.AreEqual(SiteV1.AtmosphereOxygenDensity, J.D(atmo, "oxygenDensity"));
            Assert.AreEqual(SiteV1.AtmosphereLimitAltitude, J.D(atmo, "limitAltitude"));
            Assert.AreEqual((double)SiteV1.AtmosphereMaxWindSpeed, J.D(atmo, "maxWindSpeed"));

            var water = J.Obj(rec, "water");
            Assert.AreEqual(SiteV1.WaterSettingsRadius, J.D(water, "settingsRadius"));
            Assert.AreEqual(SiteV1.WaveHeight, J.D(water, "waveHeight"));
            Assert.AreEqual(SiteV1.TideHeight, J.D(water, "tideHeight"));
            Assert.AreEqual(SiteV1.Volumetric, J.B(water, "volumetric"));
            Assert.AreEqual(SiteV1.CrushDamage, J.D(water, "crushDamage"));
            Assert.AreEqual(SiteV1.CurrentSpeed, J.D(water, "currentSpeed"));
            Assert.AreEqual(SiteV1.EnableFish, J.B(water, "enableFish"));
            Assert.AreEqual(SiteV1.RsExpected, J.D(water, "RsExpectedFloat32"));
            Assert.AreEqual(SiteV1.RsSource, J.S(water, "RsSource"));

            var anchor = J.Obj(rec, "anchor");
            Assert.AreEqual(SiteV1.AnchorU0, J.V3(anchor, "U0"));
            Assert.AreEqual(SiteV1.AnchorN0, J.V3(anchor, "N0"));
            Assert.AreEqual(SiteV1.AnchorE0, J.V3(anchor, "E0"));
            Assert.AreEqual(SiteV1.AnchorPsiDeg, J.D(anchor, "psi_deg"));
            Assert.AreEqual(SiteV1.AnchorO, J.V3(anchor, "O"));
            Assert.AreEqual(SiteV1.BaseSunDirection, J.V3(anchor, "baseSunDirection"));
            Assert.AreEqual((double)SiteV1.SunRotationIntervalMinutes, J.D(anchor, "sunRotationIntervalMinutes"));
            Assert.AreEqual("runtime: planet.PositionComp.GetPosition(); tests use (-1000000.0, 250000.0, 1500000.0)", J.S(rec, "C"));

            var depth = J.Obj(rec, "depth");
            Assert.AreEqual(SiteV1.AbyssLimitSnY, J.D(depth, "abyssLimitSnY"));
            Assert.AreEqual(SiteV1.PlanetFloorSnY, J.D(depth, "planetFloorSnY"));

            var terrain = J.Obj(rec, "terrain");
            Assert.AreEqual(SiteV1.TerrainRepresentation, J.S(terrain, "representation"));
            Assert.AreEqual((double)SiteV1.Tile, J.D(terrain, "tile"));
            Assert.AreEqual((double)SiteV1.Pitch, J.D(terrain, "pitch"));
            Assert.AreEqual((double)SiteV1.LateralMin, J.D(terrain, "lateralMin"));
            Assert.AreEqual((double)SiteV1.NLateral, J.D(terrain, "nLateral"));
            Assert.AreEqual((double)SiteV1.RadialMin, J.D(terrain, "radialMin"));
            Assert.AreEqual((double)SiteV1.NRadial, J.D(terrain, "nRadial"));
            Assert.AreEqual((double)SiteV1.InteriorMargin, J.D(terrain, "interiorMargin"));
            Assert.AreEqual(SiteV1.TerrainSaved, J.B(terrain, "saved"));
            Assert.AreEqual(SiteV1.UseVoxelOffset, J.B(terrain, "useVoxelOffset"));
            Assert.AreEqual(SiteV1.TerrainEdits, J.S(terrain, "edits"));

            var sn = J.Obj(rec, "snSource");
            Assert.AreEqual(SiteV1.SnDataYmin, J.D(sn, "dataYmin"));
            Assert.AreEqual(SiteV1.SnDataYmax, J.D(sn, "dataYmax"));
            Assert.AreEqual(SiteV1.SnWorldYmin, J.D(sn, "worldYmin"));
            Assert.AreEqual(SiteV1.SnTileHalf, J.D(sn, "tileHalf"));
            var man = J.Obj(sn, "manifest");
            Assert.AreEqual((double)SiteV1.SnManifestBuildId, J.D(man, "buildid"));
            Assert.AreEqual((double)SiteV1.SnManifestFiles, J.D(man, "files"));
            Assert.AreEqual((double)SiteV1.SnManifestBytes, J.D(man, "bytes"));
            List<object> rows = J.Arr(man, "batchRowsWithFiles");
            Assert.AreEqual((double)SiteV1.SnBatchRowFirst, (double)rows[0]);
            Assert.AreEqual((double)SiteV1.SnBatchRowLast, (double)rows[1]);
        }

        /// <summary>The float32 derivations of site_v1.py (C# float arithmetic as SE and the Water Mod do it).</summary>
        [TestMethod]
        public void Constants_FollowTheFloat32Derivations()
        {
            float r = (float)60000.0;
            float minHill = (float)((double)(float)(-0.05) * r);
            float maxHill = (float)((double)(float)0.12 * r);
            float inner = (float)((double)r + minHill);
            float outer = (float)((double)r + maxHill);
            float settings = (float)(60000.0 / 57000.0);
            float rs = (float)((double)inner * settings);
            Assert.AreEqual(SiteV1.StorageRadius, (double)r);
            Assert.AreEqual(SiteV1.MinimumRadius, (double)inner);
            Assert.AreEqual(SiteV1.MaximumRadius, (double)outer);
            Assert.AreEqual(SiteV1.AtmosphereAltitude, (double)maxHill * SiteV1.AtmosphereLimitAltitude);
            Assert.AreEqual(SiteV1.WaterSettingsRadius, (double)settings);
            Assert.AreEqual(SiteV1.WaterSettingsRadiusBits, SiteV1.FloatBits(settings));
            Assert.AreEqual(SiteV1.RsExpected, (double)rs);
            Assert.AreEqual(SiteV1.RsExpectedFloat, rs);
            Assert.AreEqual(SiteV1.RsExpectedBits, SiteV1.FloatBits(rs));
            Assert.AreEqual(SiteV1.PlanetFloorSnY, (double)inner - (double)rs);
            // the runtime check is bit for bit
            Assert.IsTrue(SiteV1.IsRsExact(60000.00390625f));
            Assert.IsFalse(SiteV1.IsRsExact(60000.0f));
            Assert.IsFalse(SiteV1.IsRsExact(BitConverter.ToSingle(BitConverter.GetBytes(SiteV1.RsExpectedBits + 1), 0)));
        }

        [TestMethod]
        public void Tiles_EntityIdsAndLattice()
        {
            Assert.AreEqual(91274119533887488L, SiteV1.TileEntityId(0, 0, 0));
            Assert.AreEqual(0x0144455400010211L, SiteV1.TileEntityId(1, 1, 2));
            Assert.AreEqual(0x0144455400010000L + 0x100 * 1 + 0x10 * 3 + 2, SiteV1.TileEntityId(2, 3, 1));
            int[][] tiles = SiteV1.TilesCreated();
            Assert.AreEqual(34, tiles.Length);
            Assert.AreEqual(34, tiles.Select(t => SiteV1.TileEntityId(t[0], t[1], t[2])).Distinct().Count());
            Assert.AreEqual(2, tiles.Count(t => t[2] == 2));
            Assert.AreEqual(new Vec3(-2042, -2042, 58064), SiteV1.TileMinRelative(0, 0, 0));
            Assert.AreEqual(new Vec3(-2042 + 1020, -2042 + 3 * 1020, 58064 + 2 * 1020), SiteV1.TileMinRelative(1, 3, 2));
        }

        /// <summary>CONTRACT section 3 "Conformance vectors" table (test centre, doubles, tolerance 1e-6 m; table printed to 6 decimals).</summary>
        [TestMethod]
        public void SiteMap_ContractConformanceTable()
        {
            SiteMap s = SiteMap.SiteV1Test;
            var rows = new[]
            {
                new { sn = new Vec3(0, 0, 0), se = new Vec3(-1000000.000000, 250000.000000, 1560000.003906), scale = 1.000000 },
                new { sn = new Vec3(-1029, -150, -1116), se = new Vec3(-998973.681996, 251113.091246, 1559830.850460), scale = 1.002506 },
                new { sn = new Vec3(198, -1657, -518), se = new Vec3(-1000192.529159, 250503.687396, 1558340.511965), scale = 1.028401 },
                new { sn = new Vec3(346, 155, 906), se = new Vec3(-1000346.878728, 249091.699052, 1560147.145897), scale = 0.997423 },
                new { sn = new Vec3(2048, -500, 2048), se = new Vec3(-1002030.144690, 247969.855310, 1559430.694846), scale = 1.008403 },
            };
            foreach (var row in rows)
            {
                Vec3 se = s.SnToSe(row.sn);
                Assert.IsTrue(Vec3.MaxAbsDiff(se, row.se) <= 1e-6, "F^-1" + row.sn + " = " + se + " vs table " + row.se);
                Assert.IsTrue(Vec3.Distance(s.SeToSn(se), row.sn) <= 1e-6, "F(F^-1" + row.sn + ")");
                Assert.AreEqual(row.scale, s.ScaleAt(se), 5e-7, "scale at " + row.sn);
                Assert.AreEqual(row.sn.Y, (se - s.C).Length - s.Rs, 1e-6, "altitude = SN y");
            }
        }
    }
}
