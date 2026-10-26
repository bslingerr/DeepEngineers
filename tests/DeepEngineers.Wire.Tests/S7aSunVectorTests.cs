using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// ROADMAP S7a (CONTRACT §5.4 "S7a conventions"): Wire <see cref="SunMath"/> against the independent Python reading
 /// <c>tools/worldmap/worldmap_ref.py s7asun</c>, through its committed vectors <c>tests/s7a/vectors/s7a_sun.tsv</c>
 /// (sky: SE's sun at random cameras of random sites, axes and times, 20 % with a declination, and site v1; cyc: the
 /// day-scalar remap and its inverse; step: the forward-only drive; light: the SunLight law; pack: centidegrees). A
 /// staleness test regenerates the file through Python and compares it byte for byte.
 /// </summary>
    [TestClass]
    public class S7aSunVectorTests
    {
        private static List<Dictionary<string, string>> _rows;
        private static readonly object Gate = new object();

        private static string TsvPath { get { return Path.Combine(S5Repo.Root, "tests", "s7a", "vectors", "s7a_sun.tsv"); } }

        private static List<Dictionary<string, string>> Rows()
        {
            lock (Gate)
            {
                if (_rows != null) return _rows;
                var list = new List<Dictionary<string, string>>();
                foreach (string raw in File.ReadAllText(TsvPath, Encoding.ASCII).Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] tok = line.Split('\t');
                    var d = new Dictionary<string, string>(StringComparer.Ordinal) { { "kind", tok[0] } };
                    for (int i = 1; i < tok.Length; i++)
                    {
                        int eq = tok[i].IndexOf('=');
                        d[tok[i].Substring(0, eq)] = tok[i].Substring(eq + 1);
                    }
                    list.Add(d);
                }
                _rows = list;
                return list;
            }
        }

        private static IEnumerable<Dictionary<string, string>> Kind(string k) { return Rows().Where(r => r["kind"] == k); }
        private static double D(Dictionary<string, string> r, string k) { return double.Parse(r[k], NumberStyles.Float, CultureInfo.InvariantCulture); }
        private static int I(Dictionary<string, string> r, string k) { return int.Parse(r[k], CultureInfo.InvariantCulture); }

        private static Vec3 V(Dictionary<string, string> r, string k)
        {
            double[] d = r[k].Split(',').Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            return new Vec3(d[0], d[1], d[2]);
        }

        private static SiteMap Site(Dictionary<string, string> r)
        {
            return new SiteMap(V(r, "C"), D(r, "Rs"), V(r, "U0"), V(r, "N0"), D(r, "psi"), V(r, "O"));
        }

        private static double Wrap180(double deg)
        {
            deg = Math.IEEERemainder(deg, 360.0);
            return deg;
        }

        [TestMethod]
        public void Vectors_FileHasEveryKind()
        {
            foreach (string k in new[] { "sky", "cyc", "step", "light", "pack" })
                Assert.IsTrue(Kind(k).Any(), "kind " + k);
            Assert.IsTrue(Kind("sky").Count() >= 300, "sky records");
            Assert.IsTrue(Kind("sky").Any(r => r["tag"] == "v1-anchor-noon"), "the site-v1 anchor record");
            Assert.IsTrue(Kind("step").Select(r => r["result"]).Distinct().Count() >= 3, "step results");
        }

        [TestMethod]
        public void Vectors_SeSky_MatchesThePythonReading()
        {
            int n = 0;
            foreach (var r in Kind("sky"))
            {
                SunSky k;
                SunMath.SeSky(Site(r), V(r, "P"), V(r, "s"), V(r, "a"), out k);
                string what = r["tag"] + " P=" + r["P"];
                Assert.AreEqual(I(r, "valid") == 1, k.PoleValid, what + " valid");
                Assert.AreEqual(0.0, SunMath.WrapRad(k.HourAngleRad - D(r, "H")), 1e-9, what + " H");
                Assert.AreEqual(D(r, "elev"), k.ElevationRad, 1e-9, what + " elev");
                Assert.AreEqual(D(r, "decl"), k.DeclinationRad, 1e-9, what + " decl");
                Assert.AreEqual(D(r, "lat"), k.LatitudeRad, 1e-9, what + " lat");
                Assert.AreEqual(D(r, "sunrise"), k.SunriseHourAngleRad, 1e-7, what + " sunrise");
                Assert.IsTrue(Vec3.MaxAbsDiff(V(r, "sunSn"), k.SunDirSn) < 1e-9, what + " sunDirSn");
                Assert.IsTrue(Vec3.MaxAbsDiff(V(r, "q"), k.PoleSn) < 1e-9, what + " pole");
                Assert.AreEqual(I(r, "azc"), (int)k.PoleAzCdeg, what + " az cdeg");
                Assert.AreEqual(I(r, "elc"), (int)k.PoleElCdeg, what + " el cdeg");
                Assert.AreEqual(D(r, "c"), SunMath.CycleTime(k.HourAngleRad), 1e-9, what + " c");
                n++;
            }
            Console.WriteLine("S7a sky vectors: " + n);
        }

        [TestMethod]
        public void Vectors_SkyAnglesAndDisk_MatchAndLieAlongSesSun()
        {
            double worst = 0, worstQ = 0;
            foreach (var r in Kind("sky"))
            {
                string what = r["tag"] + " P=" + r["P"];
                double sd, npo, sdq, npoq;
                SunMath.SkyAngles(V(r, "q"), out sd, out npo);
                Assert.AreEqual(0.0, Wrap180(sd - D(r, "sd")), 1e-7, what + " SunDirection");
                Assert.AreEqual(D(r, "npo"), npo, 1e-7, what + " NorthPoleOffset");
                SunMath.SkyAngles(SunMath.PoleFromCdeg((short)I(r, "azc"), (short)I(r, "elc")), out sdq, out npoq);
                Assert.AreEqual(0.0, Wrap180(sdq - D(r, "sdq")), 1e-7, what + " SunDirection (C6 pole)");
                Assert.AreEqual(D(r, "npoq"), npoq, 1e-7, what + " NorthPoleOffset (C6 pole)");
                Vec3 d = SunMath.SnSunDir(D(r, "c"), sd, npo);
                Vec3 dq = SunMath.SnSunDir(D(r, "c"), sdq, npoq);
                Assert.IsTrue(Vec3.MaxAbsDiff(V(r, "snDir"), d) < 1e-9, what + " disk");
                Assert.IsTrue(Vec3.MaxAbsDiff(V(r, "snDirQ"), dq) < 1e-9, what + " disk (C6 pole)");
                if (I(r, "valid") == 1 && Math.Abs(D(r, "decl")) < 1e-12)
                {
                    double e = SunMath.AngleDeg(d, V(r, "sunSn")), eq = SunMath.AngleDeg(dq, V(r, "sunSn"));
                    worst = Math.Max(worst, e);
                    worstQ = Math.Max(worstQ, eq);
                }
            }
            Console.WriteLine("S7a disk vs SE sun: worst " + worst.ToString("e3", CultureInfo.InvariantCulture) + " deg exact, " +
                              worstQ.ToString("0.0000", CultureInfo.InvariantCulture) + " deg through the C6 pole");
            Assert.IsTrue(worst < 1e-6, "exact mapping (CONTRACT: worst 2.4e-6 deg in doubles)");
            Assert.IsTrue(worstQ <= 0.02, "float32 / 0.01 deg pole step bound (CONTRACT ≤ 0.02 deg)");
        }

        [TestMethod]
        public void Vectors_SiteV1Anchor_SunDirectionMinus90_NpoZero_NoonAtCycleHalf()
        {
            var r = Kind("sky").Single(x => x["tag"] == "v1-anchor-noon");
            SunSky k;
            SunMath.SeSky(Site(r), V(r, "P"), V(r, "s"), V(r, "a"), out k);
            double sd, npo;
            SunMath.SkyAngles(k.PoleSn, out sd, out npo);
            Assert.AreEqual(-90.0, sd, 1e-9);
            Assert.AreEqual(0.0, npo, 1e-9);
            Assert.AreEqual(0.5, SunMath.CycleTime(k.HourAngleRad), 1e-12);
            // the sun rises at SN +x (CONTRACT: "on this site the sun rises at SN +x and sets at SN −x")
            Assert.IsTrue(SunMath.AngleDeg(SunMath.SnSunDir(0.25, sd, npo), Vec3.UnitX) < 1e-9);
            var rise = Kind("sky").Single(x => x["tag"] == "v1-anchor-sunrise");
            SunSky k2;
            SunMath.SeSky(Site(rise), V(rise, "P"), V(rise, "s"), V(rise, "a"), out k2);
            Assert.AreEqual(0.25, SunMath.CycleTime(k2.HourAngleRad), 1e-12, "SE's sun at (−1, 0, 0) is sunrise at the anchor");
        }

        [TestMethod]
        public void Vectors_DayScalarRemapAndInverse()
        {
            foreach (var r in Kind("cyc"))
            {
                double s = SunMath.DayScalarFromCycle(D(r, "c"), D(r, "rise"), D(r, "set"));
                Assert.AreEqual(D(r, "s"), s, 1e-14, "s of c=" + r["c"]);
                Assert.AreEqual(D(r, "cBack"), SunMath.CycleFromDayScalar(D(r, "s"), D(r, "rise"), D(r, "set")), 1e-14, "c of s=" + r["s"]);
            }
        }

        [TestMethod]
        public void Vectors_Step()
        {
            foreach (var r in Kind("step"))
            {
                double adv;
                int k = SunMath.Step(D(r, "sNow"), D(r, "cNow"), D(r, "sT"), D(r, "cT"), I(r, "force") == 1, out adv);
                Assert.AreEqual(r["result"], SunStep.Name(k), "step " + string.Join(" ", r.Select(kv => kv.Key + "=" + kv.Value)));
                Assert.AreEqual(D(r, "adv"), adv, 1e-15, "advance");
            }
        }

        [TestMethod]
        public void Vectors_SunLightAndPack()
        {
            foreach (var r in Kind("light"))
                Assert.AreEqual(D(r, "elev"), SunMath.SunLightElevationDeg(D(r, "c"), D(r, "max")), 1e-12, "c=" + r["c"]);
            foreach (var r in Kind("pack"))
                Assert.AreEqual(I(r, "cdeg"), (int)SunMath.PackCdeg(D(r, "deg")), "deg=" + r["deg"]);
        }

        [TestMethod]
        public void Vectors_FileIsCurrent_RegeneratedByThePythonReference()
        {
            string fresh = S5Repo.RunPython(Path.Combine("tools", "worldmap", "worldmap_ref.py"), "s7asun", "--vectors", "-");
            string committed = File.ReadAllText(TsvPath, Encoding.ASCII);
            Assert.AreEqual(committed.Replace("\r\n", "\n"), fresh.Replace("\r\n", "\n"),
                "tests/s7a/vectors/s7a_sun.tsv is stale: python -I tools/worldmap/worldmap_ref.py s7asun --vectors tests/s7a/vectors/s7a_sun.tsv");
        }
    }
}
