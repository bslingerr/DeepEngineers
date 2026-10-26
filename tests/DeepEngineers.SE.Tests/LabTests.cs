using System;
using System.Collections.Generic;
using System.Linq;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// The lab commands' game-independent parts (src/DeepEngineers.SE/LabLogic.cs): the <c>de.spawnplanet</c> min-corner
 /// rule against tools/worldmod/check_planet.py (python -I, an independent implementation), the argument parsing of
 /// the C11 and chat forms, and the <c>de.g2e3</c> decision rows (PASS/FAIL thresholds) on fake facts.
 /// </summary>
    [TestClass]
    public class LabTests
    {
        public TestContext TestContext { get; set; }

        private const float HillMax = 0.12f; // DE_4546B HillParams Max (src/DeepEngineers.WorldMod/Data/PlanetGeneratorDefinitions.sbc)

 // ====================================================================================== min corner

 /// <summary>check_planet.py --json --centre=X,Y,Z: its "spawn" block (savedMinCorner = what AddPlanet takes).</summary>
        private Vec3 CheckPlanetMinCorner(Vec3 centre, out Vec3 printedCentre, out double spawnSize)
        {
            string arg = "--centre=" + ((long)centre.X) + "," + ((long)centre.Y) + "," + ((long)centre.Z);
            string json = PyOracle.Run("tools/worldmod/check_planet.py", "--json", arg);
            var root = (IDictionary<string, object>)MiniJson.Parse(json);
            var spawn = MiniJson.GetObject(root, "spawn");
            Assert.IsNotNull(spawn, "check_planet.py --json has no spawn block");
            printedCentre = V((List<object>)spawn["centre"]);
            spawnSize = MiniJson.GetNumber(spawn, "spawnSize").Value;
            TestContext.WriteLine("    check_planet.py " + arg + ": savedMinCorner " + LabSpawn.V(V((List<object>)spawn["savedMinCorner"])) +
                                  " spawnPlanetPos " + LabSpawn.V(V((List<object>)spawn["spawnPlanetPos"])) + " (pass=" + root["pass"] + ")");
            Assert.AreEqual(V((List<object>)spawn["savedMinCorner"]), V((List<object>)spawn["spawnPlanetPos"]));
            return V((List<object>)spawn["savedMinCorner"]);
        }

        private static Vec3 V(List<object> l)
        {
            return new Vec3((double)l[0], (double)l[1], (double)l[2]);
        }

        [TestMethod]
        public void Lab_MinCorner_MatchesCheckPlanet()
        {
            int storage = LabSpawn.StorageSize(LabSpawn.DefaultSize, HillMax);
            Assert.AreEqual(262144, storage, "FindBestOctreeSize(2 * (60000 + 7200)) = 2^18");
            Assert.AreEqual("DE_4546B-4546d120000", LabSpawn.StorageName(LabSpawn.DefaultDefinition, LabSpawn.DefaultSeed, LabSpawn.DefaultSize));

            // md §5
            Vec3 c = SiteV1.TestC;
            Vec3 mc = LabSpawn.MinCornerFromCentre(c, storage, true);
            Assert.AreEqual(new Vec3(-1131072.5, 118927.5, 1368927.5), mc);
            Assert.AreEqual(c, LabSpawn.CentreFromMinCorner(mc, storage, true), "the forward rule gives the centre back exactly");

            // independent oracle: check_planet.py (Python) for the test centre and two more integer centres
            foreach (Vec3 centre in new[] { SiteV1.TestC, new Vec3(0, 0, 0), new Vec3(123457, -7654321, 42) })
            {
                Vec3 printed;
                double size;
                Vec3 py = CheckPlanetMinCorner(centre, out printed, out size);
                Assert.AreEqual(centre, printed);
                Assert.AreEqual((double)LabSpawn.DefaultSize, size, "check_planet.py spawn size");
                Vec3 cs = LabSpawn.MinCornerFromCentre(centre, LabSpawn.StorageSize((float)size, HillMax), true);
                Assert.AreEqual(py, cs, "C# min corner vs check_planet.py for " + LabSpawn.V(centre));
            }

            // the stock EarthLike of "Star System" (DE_LAB_WORLD.md §5): saved at -131072 per axis, centre 0.5
            Assert.AreEqual(new Vec3(0.5, 0.5, 0.5), LabSpawn.CentreFromMinCorner(new Vec3(-131072, -131072, -131072), 262144, true));
            // without the half-voxel offset (a game that does not set it) the corner is C - storage/2
            Assert.AreEqual(new Vec3(-1131072, 118928, 1368928), LabSpawn.MinCornerFromCentre(c, storage, false));
        }

        [TestMethod]
        public void Lab_StorageSize_FindBestOctreeSizeRule()
        {
            Assert.AreEqual(32, LabSpawn.StorageSize(1f, HillMax), "radius clamped to 1: 2.24 -> 32");
            Assert.AreEqual(131072, LabSpawn.StorageSize(120000f, 0f), "no hills: exactly 120000 -> 131072");
            Assert.AreEqual(131072, LabSpawn.StorageSize(131072f, 0f), "target equal to a power of two stays (loop is n < target)");
            Assert.AreEqual(262144, LabSpawn.StorageSize(131074f, 0f), "just above -> next power");
            Assert.AreEqual(262144, LabSpawn.StorageSize(120000f, 0.12f));
            Assert.AreEqual(65536, LabSpawn.StorageSize(38000f, 0.12f), "a 38 km planet: 2 * (19000 + 2280) = 42560 -> 65536");
        }

        // ================================================================================ argument parsing

        private static SpawnRequest Parse(string json, bool ok = true, string errContains = null)
        {
            var args = json == null ? null : (IDictionary<string, object>)MiniJson.Parse(json);
            SpawnRequest r;
            string err;
            bool got = LabSpawn.TryParse(args, out r, out err);
            Assert.AreEqual(ok, got, json + " -> " + err);
            if (errContains != null) StringAssert.Contains(err, errContains);
            return r;
        }

        [TestMethod]
        public void Lab_SpawnArguments_C11()
        {
            // defaults (SiteV1 / check_planet.py)
            foreach (string empty in new[] { null, "{}" })
            {
                SpawnRequest d = Parse(empty);
                Assert.AreEqual("DE_4546B", d.Definition);
                Assert.AreEqual(120000f, d.Size);
                Assert.AreEqual(4546, d.Seed);
                Assert.AreEqual(SiteV1.TestC, d.Centre);
                Assert.IsFalse(d.DryRun);
            }
            SpawnRequest r = Parse("{\"definition\":\"DE_4546B\",\"size\":120000,\"seed\":4546,\"centre\":[-1000000,250000,1500000],\"dryRun\":true}");
            Assert.AreEqual(SiteV1.TestC, r.Centre);
            Assert.IsTrue(r.DryRun);
            r = Parse("{\"center\":{\"x\":1,\"y\":-2.5,\"z\":3}}");
            Assert.AreEqual(new Vec3(1, -2.5, 3), r.Centre);
            r = Parse("{\"seed\":-7,\"size\":60000.5}");
            Assert.AreEqual(-7, r.Seed);
            Assert.AreEqual(60000.5f, r.Size);

            // refusals: a typo never falls back to a default
            Parse("{\"centr\":[0,0,0]}", false, "unknown argument 'centr'");
            Parse("{\"size\":\"120000\"}", false, "size");
            Parse("{\"size\":0}", false, "size");
            Parse("{\"size\":9000000}", false, "size");
            Parse("{\"seed\":1.5}", false, "seed");
            Parse("{\"seed\":3000000000}", false, "seed");
            Parse("{\"centre\":[1,2]}", false, "centre");
            Parse("{\"centre\":[1,2,\"3\"]}", false, "centre");
            Parse("{\"centre\":{\"x\":1,\"y\":2}}", false, "centre");
            Parse("{\"centre\":[0,0,0],\"center\":[0,0,0]}", false, "either");
            Parse("{\"definition\":\"\"}", false, "definition");
            Parse("{\"definition\":7}", false, "definition");
            Parse("{\"dryRun\":\"yes\"}", false, "dryRun");
        }

        [TestMethod]
        public void Lab_SpawnArguments_Chat()
        {
            var args = new Dictionary<string, object>(StringComparer.Ordinal);
            string usage;
            string[] argv = "spawnplanet dry def=DE_4546B size=120000 seed=4546 centre=-1000000,250000,1500000".Split(' ');
            Assert.IsTrue(LabSpawn.ParseChat(argv, 1, args, out usage), usage);
            SpawnRequest r;
            string err;
            Assert.IsTrue(LabSpawn.TryParse(args, out r, out err), err);
            Assert.IsTrue(r.DryRun);
            Assert.AreEqual(SiteV1.TestC, r.Centre);
            Assert.AreEqual(4546, r.Seed);

            args.Clear();
            Assert.IsTrue(LabSpawn.ParseChat(new[] { "spawnplanet" }, 1, args, out usage));
            Assert.AreEqual(0, args.Count, "no tokens: every default");
            foreach (string bad in new[] { "size", "size=", "=5", "centre=1,2", "centre=1,2,x", "seed=abc", "colour=red" })
            {
                args.Clear();
                Assert.IsFalse(LabSpawn.ParseChat(new[] { "spawnplanet", bad }, 1, args, out usage), bad);
                Assert.AreEqual(LabSpawn.ChatUsage, usage);
            }
            // a chat value that parses but is out of range is refused by TryParse (same rule as C11)
            args.Clear();
            Assert.IsTrue(LabSpawn.ParseChat(new[] { "spawnplanet", "seed=1.5" }, 1, args, out usage));
            Assert.IsFalse(LabSpawn.TryParse(args, out r, out err));
        }

        [TestMethod]
        public void Lab_Chat_RoutesSpawnG2E3AndRebind()
        {
            using (var rig = new Rig(TestContext))
            {
                var calls = new List<KeyValuePair<string, IDictionary<string, object>>>();
                var dev = new DevChannel(new Link(() => true, Rig.Name(), Rig.MutexName()))
                {
                    ShowChat = (s, t) => rig.Chat.Add(t),
                    ExtraCommand = (cmd, a, via) =>
                    {
                        calls.Add(new KeyValuePair<string, IDictionary<string, object>>(cmd, a));
                        return new Dictionary<string, object> { { "ok", true }, { "line", "stub " + cmd } };
                    },
                };
                Assert.IsTrue(dev.HandleChat("/de spawnplanet", 1));
                Assert.IsTrue(dev.HandleChat("/de spawnplanet dry centre=1,2,3", 2));
                Assert.IsTrue(dev.HandleChat("/de spawnplanet bogus", 3), "usage, still ours");
                Assert.IsTrue(dev.HandleChat("/de g2e3", 4));
                Assert.IsTrue(dev.HandleChat("/de g2e3 now", 5), "usage, still ours");
                Assert.IsTrue(dev.HandleChat("/de site rebind", 6));
                Assert.IsTrue(dev.HandleChat("/de site whatever", 7), "usage, still ours");
                Assert.AreEqual(4, calls.Count, string.Join(" | ", rig.Chat));
                Assert.IsTrue(calls[0].Key == "de.spawnplanet" && calls[0].Value.Count == 0);
                Assert.IsTrue(calls[1].Key == "de.spawnplanet" && MiniJson.GetBool(calls[1].Value, "dryRun") == true);
                Assert.AreEqual(3, ((List<object>)calls[1].Value["centre"]).Count);
                Assert.IsTrue(calls[2].Key == "de.g2e3" && calls[2].Value.Count == 0);
                Assert.IsTrue(calls[3].Key == "de.site" && MiniJson.GetBool(calls[3].Value, "rebind") == true);
                Assert.IsTrue(rig.Chat.Exists(t => t.StartsWith("usage: /de spawnplanet", StringComparison.Ordinal)), string.Join(" | ", rig.Chat));
                Assert.IsTrue(rig.Chat.Exists(t => t.StartsWith("usage: /de g2e3", StringComparison.Ordinal)));
                Assert.IsTrue(rig.Chat.Exists(t => t.StartsWith("usage: /de site", StringComparison.Ordinal)));
            }
        }

        [TestMethod]
        public void Lab_OtherPlanetDistanceRule()
        {

            // Europa); illustrative centres on the Star System scale (0.1 to 6 million m from the origin), all far from C
            var star = new List<KeyValuePair<string, Vec3>>
            {
                new KeyValuePair<string, Vec3>("EarthLike", new Vec3(0.5, 0.5, 0.5)),
                new KeyValuePair<string, Vec3>("Moon", new Vec3(16384.5, 136384.5, -113615.5)),
                new KeyValuePair<string, Vec3>("Mars", new Vec3(1031072.5, 131072.5, 1631072.5)),
                new KeyValuePair<string, Vec3>("Europa", new Vec3(916384.5, 16384.5, 1616384.5)),
                new KeyValuePair<string, Vec3>("Alien", new Vec3(131072.5, 131072.5, 5731072.5)),
                new KeyValuePair<string, Vec3>("Titan", new Vec3(36384.5, 226384.5, 5796384.5)),
            };
            double d;
            Assert.IsNull(LabSpawn.TooClosePlanet(star, SiteV1.TestC, out d));
            Assert.IsTrue(d > 1.0e6, "nearest " + d);
            star.Add(new KeyValuePair<string, Vec3>("Near", SiteV1.TestC + new Vec3(0, 150000, 0)));
            Assert.AreEqual("Near", LabSpawn.TooClosePlanet(star, SiteV1.TestC, out d));
            Assert.AreEqual(150000.0, d);
            Assert.IsNull(LabSpawn.TooClosePlanet(null, SiteV1.TestC, out d));
        }

        // ======================================================================================= G2-E3 rows

 /// <summary>A healthy DE_4546B as SE computes it: every value from the game's own formulas (see LabCommands).</summary>
        private static G2E3Facts Good()
        {
            float rs = SiteV1.RsExpectedFloat;
            float air = (float)Math.Max(0.0, Math.Min(1.0, 1.0 - (rs - 60000.0) / 14400.0)) * 1f; // MyPlanet.GetAirDensity
            var f = new G2E3Facts
            {
                PlanetCount = 1,
                Centre = SiteV1.TestC,
                MinimumRadius = 57000f, AverageRadius = 60000f, MaximumRadius = 67200f,
                RsBits = SiteV1.RsExpectedBits, SettingsRadiusBits = SiteV1.WaterSettingsRadiusBits,
                WaveHeight = 0f, TideHeight = 0f, CrushDamage = 0f, CurrentSpeed = 0f, EnableFish = false, Volumetric = false,
                SeaLevelElevation = (double)rs - 60000.0,
                GravityWater = 9.81f, GravityMinus1000 = 9.81f, GravityMinus2000 = 9.81f,
                AirDensity = air,
                Oxygen = air * 0.9f,       // MyOxygenProviderSystem.GetOxygenInPoint: the planet is the only provider in range
                OxygenPlanet = air * 0.9f, // MyPlanet.GetOxygenForPosition
                AtmoThrust = G2E3.ThrustMultiplier(air, true, true, 0.3f, 1f, 1f / (1f - 0.3f), 0f, 1f),
                FloorMaterial = "Sand_02",
            };
            for (int i = 0; i < 3; i++) f.Floor[i] = 57000.0 - rs;
            return f;
        }

        private List<G2E3Row> Eval(G2E3Facts f, out int pass, out int fail, out int manual)
        {
            List<G2E3Row> rows = G2E3.Evaluate(f);
            string summary = G2E3.Summary(rows, out pass, out fail, out manual);
            foreach (G2E3Row r in rows)
            {
                // DE.G2E3 <check> <value> <expected> PASS|FAIL|MANUAL: tokens 1..4 hold no spaces
                string[] t = r.Line.Split(' ');
                Assert.AreEqual("DE.G2E3", t[0]);
                Assert.AreEqual(r.Check, t[1]);
                Assert.AreEqual(r.Value, t[2], r.Line);
                Assert.AreEqual(r.Expected, t[3], r.Line);
                Assert.AreEqual(r.StatusName, t[4], r.Line);
            }
            StringAssert.StartsWith(summary, "DE.G2E3 SUMMARY " + pass + " PASS " + fail + " FAIL " + manual + " MANUAL");
            return rows;
        }

        private G2E3Row RowOf(List<G2E3Row> rows, string check)
        {
            G2E3Row[] m = rows.Where(r => r.Check == check).ToArray();
            Assert.AreEqual(1, m.Length, check);
            return m[0];
        }

        /// <summary>Changes one fact of a good set; exactly the named rows go red, every other computed row stays PASS.</summary>
        private void Red(string what, Action<G2E3Facts> change, params string[] failing)
        {
            G2E3Facts f = Good();
            change(f);
            int pass, fail, manual;
            List<G2E3Row> rows = Eval(f, out pass, out fail, out manual);
            string[] red = rows.Where(r => r.Status == G2E3Status.Fail).Select(r => r.Check).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            string[] want = failing.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            TestContext.WriteLine("    " + what + ": " + string.Join(", ", red));
            CollectionAssert.AreEqual(want, red, what + ": " + string.Join(" | ", rows.Where(r => r.Status == G2E3Status.Fail).Select(r => r.Line)));
            Assert.AreEqual(2, manual);
        }

        [TestMethod]
        public void G2E3_GoodSite_AllComputedRowsPass()
        {
            int pass, fail, manual;
            List<G2E3Row> rows = Eval(Good(), out pass, out fail, out manual);
            foreach (G2E3Row r in rows) TestContext.WriteLine("    " + r.Line);
            Assert.AreEqual(0, fail);
            Assert.AreEqual(2, manual, "8.crush (rig) and 10.loadErrors (log)");
            Assert.AreEqual(rows.Count - 2, pass);
            Assert.AreEqual(26, rows.Count);
            Assert.AreEqual(G2E3Status.Manual, RowOf(rows, "8.crush").Status);
            StringAssert.Contains(RowOf(rows, "8.crush").Line, " MANUAL (rig) ");
            StringAssert.Contains(RowOf(rows, "10.loadErrors").Line, " MANUAL (log) ");
            Assert.AreEqual("0.00390625", RowOf(rows, "4.seaLevel").Value, "Rs - AverageRadius");
            Assert.AreEqual("-3000.00390625", RowOf(rows, "9.floor.anchor").Value);
            Assert.AreEqual("0x476a6001(60000.00390625)", RowOf(rows, "3.sea.Rs").Value);
            Assert.IsTrue(double.Parse(RowOf(rows, "6.atmoThrust").Value, System.Globalization.CultureInfo.InvariantCulture) >= 0.9999);
            Assert.AreEqual("(-1000000,250000,1500000)", RowOf(rows, "1.centre").Value);
            // check 7 is the air vent's reading (provider system); the planet's own share is in the note
            StringAssert.Contains(RowOf(rows, "7.oxygen").Note, "GetOxygenInPoint");
            StringAssert.Contains(RowOf(rows, "7.oxygen").Note, "planet share 0.89999");
        }

        [TestMethod]
        public void G2E3_Thresholds_EachRowTurnsRed()
        {
            Red("planet missing", f => f.PlanetCount = 0, "0.planetCount");
            Red("two planets", f => f.PlanetCount = 2, "0.planetCount");
            Red("centre off by 0.5 (the save edit not done)", f => f.Centre = SiteV1.TestC + new Vec3(0.5, 0, 0), "1.centre");
            Red("centre within 1 mm", f => f.Centre = SiteV1.TestC + new Vec3(0.0009, -0.0009, 0));
            Red("average radius 60000.002", f => f.AverageRadius = 60000.002, "2.radius.average");
            Red("minimum radius of another hill", f => f.MinimumRadius = 54000.0, "2.radius.minimum");
            Red("maximum radius", f => f.MaximumRadius = 66000.0, "2.radius.maximum");
            Red("Rs + 1 ulp (S1.M5)", f => f.RsBits = SiteV1.RsExpectedBits + 1, "3.sea.Rs");
            Red("settings radius 1.05", f => f.SettingsRadiusBits = SiteV1.FloatBits(1.05f), "3.sea.radius");
            Red("waves", f => f.WaveHeight = 1f, "3.sea.waveHeight");
            Red("tides", f => f.TideHeight = 0.5f, "3.sea.tideHeight");
            Red("crush 0.5 (/wcrush 0.5)", f => f.CrushDamage = 0.5f, "3.sea.crushDamage");
            Red("current", f => f.CurrentSpeed = 2f, "3.sea.currentSpeed");
            Red("fish", f => f.EnableFish = true, "3.sea.enableFish");
            Red("volumetric", f => f.Volumetric = true, "3.sea.volumetric");
            Red("settings unreadable", f =>
            {
                f.SettingsRadiusBits = null; f.WaveHeight = f.TideHeight = f.CrushDamage = f.CurrentSpeed = null; f.EnableFish = f.Volumetric = null;
                f.SettingsError = "Jakaria.WaterData.Version=3.24";
            }, "3.sea.radius", "3.sea.waveHeight", "3.sea.tideHeight", "3.sea.crushDamage", "3.sea.currentSpeed", "3.sea.enableFish", "3.sea.volumetric");
            Red("sea level 0.06", f => f.SeaLevelElevation = 0.06, "4.seaLevel");
            Red("sea level -0.05 (boundary)", f => f.SeaLevelElevation = -0.05);
            Red("gravity 9.80 at -2000", f => f.GravityMinus2000 = 9.80, "5.gravity.-2000");
            Red("gravity +0.0009 (another planet's pull)", f => f.GravityWater = 9.8109);
            Red("gravity +0.002", f => f.GravityMinus1000 = 9.812, "5.gravity.-1000");
            Red("gravity unreadable", f => f.GravityWater = null, "5.gravity.water");
            Red("air density 0.998", f => f.AirDensity = 0.998, "6.airDensity");
            Red("atmospheric thrust 0.9989", f => f.AtmoThrust = 0.9989, "6.atmoThrust");
            Red("thruster definition missing", f => { f.AtmoThrust = null; f.AtmoThrustDetail = "not found"; }, "6.atmoThrust");
            Red("oxygen 0.89", f => f.Oxygen = 0.89, "7.oxygen");
            Red("oxygen NaN", f => f.Oxygen = double.NaN, "7.oxygen");
            Red("another provider adds oxygen at the water (vent reads 1.0)", f => f.Oxygen = 1.0, "7.oxygen");
            Red("provider system unreadable", f => f.Oxygen = null, "7.oxygen");
            Red("floor -2999.9 at the tile corner", f => f.Floor[1] = -2999.9, "9.floor.tileCorner");
            Red("floor -3000.049 (inside the tolerance)", f => f.Floor[2] = -3000.049);
            Red("floor -3000.06", f => f.Floor[2] = -3000.06, "9.floor.faceX");
            Red("floor unreadable", f => f.Floor[0] = null, "9.floor.anchor");
            Red("stone floor (material maps not loaded)", f => f.FloorMaterial = "Stone", "9.floor.material");
            Red("no material", f => f.FloorMaterial = null, "9.floor.material");
        }

        [TestMethod]
        public void G2E3_AtmosphericThrustFormula()
        {
            // stock SmallBlockSmallAtmosphericThrust: influence 0.3..1 -> effectiveness 0..1, needs atmosphere
            float inv = 1f / (1f - 0.3f);
            Assert.AreEqual(1.0, G2E3.ThrustMultiplier(1f, true, true, 0.3f, 1f, inv, 0f, 1f), 1e-6);
            Assert.AreEqual(0.0, G2E3.ThrustMultiplier(0.3f, true, true, 0.3f, 1f, inv, 0f, 1f), 1e-6);
            Assert.AreEqual(0.0, G2E3.ThrustMultiplier(0.1f, true, true, 0.3f, 1f, inv, 0f, 1f), 1e-6, "clamped below");
            Assert.AreEqual(0.5, G2E3.ThrustMultiplier(0.65f, true, true, 0.3f, 1f, inv, 0f, 1f), 1e-6);
            Assert.AreEqual(0.0, G2E3.ThrustMultiplier(1f, false, true, 0.3f, 1f, inv, 0f, 1f), 1e-6, "no atmosphere: effectiveness at min");
            Assert.AreEqual(1.0, G2E3.ThrustMultiplier(0.2f, true, false, 0.5f, 0.5f, 0f, 0f, 1f), 1e-6, "min == max influence: 1");
            // the DE_4546B water point: air density 1 - 0.0039/14400 -> at least 0.999 (check 6)
            float air = (float)(1.0 - (SiteV1.RsExpected - 60000.0) / 14400.0);
            Assert.IsTrue(G2E3.ThrustMultiplier(air, true, true, 0.3f, 1f, inv, 0f, 1f) >= 0.999);
        }
    }
}
