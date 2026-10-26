using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// The S1 site validation decision table (src/DeepEngineers.SE/SiteValidation.cs; CONTRACT §3 "Runtime values and
 /// validation"): fake Water Mod API results → <c>DE.Site WAIT|OK|REFUSE</c>, the 600-tick limit, the check order, the
 /// 10 s re-check, and the seeded failures S1.M5 (Rs + 1 ulp) and S1.M9 (API never received) turned red.
 /// </summary>
    [TestClass]
    public class SiteValidationTests
    {
        public TestContext TestContext { get; set; }

        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); }

        /// <summary>A healthy site v1 as the game glue would report it (GetDepth differs from y by the Water Mod's 1.76 mm).</summary>
        private static SiteFacts Good(int ticks = 1)
        {
            return new SiteFacts
            {
                Ticks = ticks,
                ApiReceived = true,
                PlanetCount = 1,
                HasWater = true,
                RsBits = SiteV1.RsExpectedBits,
                DepthMaxError = 0.00176,
                DepthDetail = "6 points",
                CrushDamage = 0f,
                WaveHeight = 0f,
                TideHeight = 0f,
                CurrentSpeed = 0f,
            };
        }

        private SiteDecision Check(SiteFacts f, SiteStatus status, string field, string lineStart = null)
        {
            SiteDecision d = SiteValidation.Decide(f);
            TestContext.WriteLine("    " + d.Line);
            Assert.AreEqual(status, d.Status, d.Line);
            Assert.AreEqual(field, d.Field, d.Line);
            if (lineStart != null) StringAssert.StartsWith(d.Line, lineStart);
            return d;
        }

        [TestMethod]
        public void S1Site_DecisionTable_EveryRow()
        {
            // OK
            Check(Good(), SiteStatus.Ok, "", "DE.Site OK site v1 bound");
            Check(Good(600), SiteStatus.Ok, "");

            // 1. API received: WAIT until tick 600, then REFUSE (the S1.M9 symptom)
            var f = Good(1); f.ApiReceived = false;
            Check(f, SiteStatus.Wait, "api", "DE.Site WAIT API not received");
            f.Ticks = 599;
            Check(f, SiteStatus.Wait, "api");
            f.Ticks = 600;
            Check(f, SiteStatus.Refuse, "api", "DE.Site REFUSE API not received");

            // 2. API usable: refused at once
            f = Good(1); f.ApiError = "VerifyVersion(22, \"DeepEngineers\") returned false";
            Check(f, SiteStatus.Refuse, "api", "DE.Site REFUSE API VerifyVersion");
            f = Good(1); f.ApiError = "delegate check failed: GetDepth type Func`3 (expected Func`3)";
            Check(f, SiteStatus.Refuse, "api");

            // 3. exactly one DE_4546B
            f = Good(1); f.PlanetCount = 2;
            Check(f, SiteStatus.Refuse, "planet", "DE.Site REFUSE 2 DE_4546B planets");
            f = Good(1); f.PlanetCount = 0;
            Check(f, SiteStatus.Wait, "planet");
            f.Ticks = 600;
            Check(f, SiteStatus.Refuse, "planet", "DE.Site REFUSE no DE_4546B planet");

            // 4. HasWater
            f = Good(5); f.HasWater = false;
            Check(f, SiteStatus.Wait, "water");
            f.Ticks = 600;
            Check(f, SiteStatus.Refuse, "water");
            f = Good(5); f.HasWater = null;
            Check(f, SiteStatus.Wait, "water");

            // 5. Rs bit for bit
            f = Good(5); f.RsBits = null;
            Check(f, SiteStatus.Wait, "Rs");
            f.Ticks = 600;
            Check(f, SiteStatus.Refuse, "Rs");
            f = Good(1); f.RsBits = SiteV1.RsExpectedBits + 1;
            Check(f, SiteStatus.Refuse, "Rs", "DE.Site REFUSE Rs found=60000.0078125");
            f = Good(1); f.RsBits = SiteV1.FloatBits(63500f); // the superseded recon sea
            Check(f, SiteStatus.Refuse, "Rs");

            // 6. GetDepth = y within 0.01 m
            f = Good(5); f.DepthMaxError = null; f.DepthDetail = "GetDepth at SN (0, 0, 0) returned null";
            Check(f, SiteStatus.Wait, "depth");
            f.Ticks = 600;
            Check(f, SiteStatus.Refuse, "depth");
            f = Good(1); f.DepthMaxError = 0.0100001;
            Check(f, SiteStatus.Refuse, "depth", "DE.Site REFUSE GetDepth differs from y");
            f = Good(1); f.DepthMaxError = 0.01;
            Check(f, SiteStatus.Ok, "");
            f = Good(1); f.DepthMaxError = double.NaN;
            Check(f, SiteStatus.Refuse, "depth");

            // 7. crush/wave/tide/current 0
            f = Good(1); f.CrushDamage = 50f;
            Check(f, SiteStatus.Refuse, "crushDamage", "DE.Site REFUSE crushDamage=50");
            f = Good(1); f.WaveHeight = 0.5f;
            Check(f, SiteStatus.Refuse, "waveHeight");
            f = Good(1); f.TideHeight = 1f;
            Check(f, SiteStatus.Refuse, "tideHeight");
            f = Good(1); f.CurrentSpeed = 0.1f;
            Check(f, SiteStatus.Refuse, "currentSpeed");
            f = Good(1); f.CrushDamage = -0f;
            Check(f, SiteStatus.Ok, "");
            f = Good(1); f.CrushDamage = null; f.CurrentSpeed = null; f.SettingsUnavailable = "Jakaria.WaterData.Version=3.24, reflection pinned to 3.23";
            Check(f, SiteStatus.Wait, "crushDamage");
            f.Ticks = 600;
            SiteDecision d = Check(f, SiteStatus.Refuse, "crushDamage", "DE.Site REFUSE crushDamage unverifiable");
            StringAssert.Contains(d.Line, "3.24");
            f = Good(1); f.WaveHeight = null;
            Check(f, SiteStatus.Wait, "waveHeight");

            // order: the first failing check decides
            f = Good(1); f.ApiReceived = false; f.PlanetCount = 2; f.RsBits = 0;
            Check(f, SiteStatus.Wait, "api");
            f = Good(1); f.PlanetCount = 2; f.RsBits = 0; f.CrushDamage = 9f;
            Check(f, SiteStatus.Refuse, "planet");
            f = Good(1); f.RsBits = 0; f.CrushDamage = 9f;
            Check(f, SiteStatus.Refuse, "Rs");
        }

        [TestMethod]
        public void S1Site_Recheck_RsAndCrush()
        {
            Assert.AreEqual(SiteStatus.Ok, SiteValidation.Recheck(SiteV1.RsExpectedBits, 0f, null).Status);
            SiteDecision d = SiteValidation.Recheck(SiteV1.RsExpectedBits + 1, 0f, null);
            Assert.IsTrue(d.Status == SiteStatus.Refuse && d.Field == "Rs" && d.Line.StartsWith("DE.Site REFUSE Rs found=", StringComparison.Ordinal), d.Line);
            d = SiteValidation.Recheck(SiteV1.RsExpectedBits, 50f, null);
            Assert.IsTrue(d.Status == SiteStatus.Refuse && d.Field == "crushDamage", d.Line);
            d = SiteValidation.Recheck(null, 0f, null);
            Assert.IsTrue(d.Status == SiteStatus.Refuse && d.Field == "Rs", d.Line);
            d = SiteValidation.Recheck(SiteV1.RsExpectedBits, null, "no WaterComponent on the planet");
            Assert.IsTrue(d.Status == SiteStatus.Refuse && d.Field == "crushDamage" && d.Line.Contains("no WaterComponent"), d.Line);
            Assert.AreEqual(10000UL, SiteValidation.RecheckIntervalMs, "CONTRACT §3: every 10 s");
            Assert.AreEqual(600, SiteValidation.MaxTicks, "CONTRACT §3: ≤ 600 ticks");
        }

        /// <summary>S1.M5 (Rs read plus one ulp): the check that passes on the real value goes red with the mutant.</summary>
        [TestMethod]
        public void S1M5_RsPlusOneUlp_TurnsTheSiteCheckRed()
        {
            uint read = SiteV1.FloatBits(57000f * 1.0526316f); // what GetPhysicalData returns on a correct sea
            Assert.AreEqual(SiteV1.RsExpectedBits, read, "Rs = f32(57000 * 1.0526316f)");

            var f = Good(1);
            f.RsBits = SiteValidation.ApplyRsMutant(read);
            Assert.AreEqual(SiteStatus.Ok, SiteValidation.Decide(f).Status, "green without the mutant");

            Assert.AreEqual("S1.M5 ON (Water Mod sea radius read plus one float32 ulp)", Mutants.Set("S1.M5", true));
            f.RsBits = SiteValidation.ApplyRsMutant(read);
            SiteDecision d = SiteValidation.Decide(f);
            TestContext.WriteLine("    mutant: " + d.Line);
            Assert.IsTrue(d.Status == SiteStatus.Refuse && d.Line.StartsWith("DE.Site REFUSE Rs", StringComparison.Ordinal), "RED with S1.M5: " + d.Line);
            SiteDecision rc = SiteValidation.Recheck(f.RsBits, 0f, null);
            TestContext.WriteLine("    mutant re-check: " + rc.Line);
            Assert.IsTrue(rc.Status == SiteStatus.Refuse && rc.Line.StartsWith("DE.Site REFUSE Rs", StringComparison.Ordinal), "re-check red too (switching S1.M5 on in game shows it within 10 s)");
        }

        /// <summary>
 /// S1.M9 (handler registered late, at the first UpdateBeforeSimulation): the dictionary is missed, so the API is never received; the table
 /// must end in <c>DE.Site REFUSE API not received</c> at tick 600 (S1.N13 red), whatever else is fine.
 /// </summary>
        [TestMethod]
        public void S1M9_ApiNeverReceived_RefusesAt600Ticks()
        {
            var f = Good(1);
            f.ApiReceived = false;
            int refusedAt = -1;
            for (int t = 1; t <= 700 && refusedAt < 0; t++)
            {
                f.Ticks = t;
                SiteDecision d = SiteValidation.Decide(f);
                if (d.Status == SiteStatus.Refuse) { refusedAt = t; TestContext.WriteLine("    " + d.Line); StringAssert.StartsWith(d.Line, "DE.Site REFUSE API not received"); }
                else Assert.AreEqual(SiteStatus.Wait, d.Status);
            }
            Assert.AreEqual(600, refusedAt, "REFUSE exactly at the 600th tick");
            f.ApiReceived = true;
            f.Ticks = 599;
            Assert.AreEqual(SiteStatus.Ok, SiteValidation.Decide(f).Status, "received in time: OK (S1.N13 green)");
        }

        [TestMethod]
        public void S1Mutants_SwitchesAndNames()
        {
            Mutants.ClearAll();
            Assert.AreEqual("", Mutants.Active);
            StringAssert.StartsWith(Mutants.Set("s1.m1", true), "S1.M1 ON");
            StringAssert.StartsWith(Mutants.Set("S1.M2", true), "S1.M2 ON");
            StringAssert.StartsWith(Mutants.Set("S1.M9", true), "S1.M9 ON");
            Assert.IsTrue(S1Mutants.NaiveTangentMap && S1Mutants.CameraFromPluginUpdate && S1Mutants.HandlerLate);
            Assert.AreEqual("S1.M1,S1.M2,S1.M9", Mutants.Active);
            Mutants.Set("S0.M8", true);
            Assert.AreEqual("S0.M8,S1.M1,S1.M2,S1.M9", Mutants.Active);
            StringAssert.StartsWith(Mutants.Set("S1.M7", true), "UNKNOWN S1.M7");
            Mutants.ClearAll();
            Assert.IsFalse(S1Mutants.NaiveTangentMap || S1Mutants.CameraFromPluginUpdate || S1Mutants.RsPlusOneUlp || S1Mutants.HandlerLate);
            Assert.AreEqual("", Mutants.Active);
        }
    }
}
