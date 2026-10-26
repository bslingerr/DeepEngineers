using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S7 lights (CONTRACT §5.4 "C2 lights (S7)"): the row flags and falloff, the SE order (nearest first within 60 m, at
 /// most 64), the Subnautica pool's shadow cap (S7.N3.shadows; S7.M18 = no cap), the intensity/range/cone mapping and
 /// the S7.N3 calibration arithmetic.
 /// </summary>
    [TestClass]
    public unsafe class S7LightsTests
    {
        [TestMethod]
        public void Row_Flags_Falloff_Validity()
        {
            uint f = S7LightRow.Flags(true, true, true, true);
            Assert.AreEqual(SeLightFlags.Spot | SeLightFlags.Shadows | SeLightFlags.On | S7Conventions.C2LightFlagsExt.Character | S7Conventions.C2LightFlagsExt.Version1, f);
            Assert.IsTrue(S7LightRow.IsVersion1(f) && S7LightRow.IsSpot(f) && S7LightRow.HasShadows(f) && S7LightRow.IsOn(f) && S7LightRow.IsCharacter(f));
            uint p = S7LightRow.Flags(false, false, true, false);
            Assert.IsFalse(S7LightRow.IsSpot(p) || S7LightRow.HasShadows(p) || S7LightRow.IsCharacter(p));
            Assert.IsFalse(S7LightRow.IsVersion1(SeLightFlags.On), "version 0: a pre-S7 row");
            Assert.IsFalse(S7LightRow.IsVersion1(0x20000000u | SeLightFlags.On), "version 2: ignored");

            var row = new SeLight { ownerId = 5, range = 10f, intensity = 2f, coneDeg = 0f, flags = p };
            row.colour = new F32x3(1f, 0.9f, 0.8f);
            S7LightRow.SetFalloff(ref row, 1.5f);
            Assert.AreEqual(1.5f, S7LightRow.GetFalloff(ref row));
            Assert.AreEqual(p, row.flags, "the falloff stays in pad_3c");
            Assert.AreEqual(10f, row.range);
            Assert.IsTrue(S7LightRow.Valid(ref row));
            var bad = row; bad.range = 0f; Assert.IsFalse(S7LightRow.Valid(ref bad), "range 0");
            bad = row; bad.pos = new F32x3(float.NaN, 0, 0); Assert.IsFalse(S7LightRow.Valid(ref bad), "NaN");
            bad = row; bad.intensity = -1f; Assert.IsFalse(S7LightRow.Valid(ref bad), "negative intensity");
            bad = row; bad.coneDeg = 200f; Assert.IsFalse(S7LightRow.Valid(ref bad), "cone > 180");
            bad = row; bad.flags = SeLightFlags.On; Assert.IsFalse(S7LightRow.Valid(ref bad), "version 0");
        }

        [TestMethod]
        public void Select_NearestFirstWithinTheRadius_Capped()
        {
            double[] d2 = { 100, 9, 3700, 3500, 9, 0, double.NaN, 1 };
            int[] order = new int[64];
            int n = LightSelect.Select(d2, d2.Length, 60, 64, order);
            // 3700 > 3600 (60 m) out; NaN out
            CollectionAssert.AreEqual(new[] { 5, 7, 1, 4, 0, 3 }, Copy(order, n), "nearest first, ties by index");
            n = LightSelect.Select(d2, d2.Length, 60, 3, order);
            CollectionAssert.AreEqual(new[] { 5, 7, 1 }, Copy(order, n), "capped: the three nearest");
            Assert.AreEqual(0, LightSelect.Select(d2, d2.Length, 60, 0, order));

            // property: equals a full sort for random inputs
            var rnd = new Random(3);
            for (int trial = 0; trial < 200; trial++)
            {
                int count = rnd.Next(0, 150);
                var d = new double[count];
                for (int i = 0; i < count; i++) d[i] = Math.Round(rnd.NextDouble() * 5000.0);
                int got = LightSelect.Select(d, count, 60, 64, order);
                var idx = new System.Collections.Generic.List<int>();
                for (int i = 0; i < count; i++) if (d[i] <= 3600) idx.Add(i);
                idx.Sort((a, b) => d[a] != d[b] ? d[a].CompareTo(d[b]) : a.CompareTo(b));
                if (idx.Count > 64) idx.RemoveRange(64, idx.Count - 64);
                CollectionAssert.AreEqual(idx.ToArray(), Copy(order, got), "trial " + trial);
            }
        }

        private static int[] Copy(int[] a, int n)
        {
            var r = new int[n];
            Array.Copy(a, r, n);
            return r;
        }

        [TestMethod]
        public void ShadowPlan_AtMostTwoNearestShadowedSpots_M18Uncapped()
        {
            uint spotSh = S7LightRow.Flags(true, true, true, false), spot = S7LightRow.Flags(true, false, true, false), point = S7LightRow.Flags(false, false, true, false);
            uint pointSh = S7LightRow.Flags(false, true, true, false); // a point row never casts shadows (format light.row)
            uint[] flags = { point, spotSh, spot, pointSh, spotSh, spotSh, spotSh };
            var sh = new bool[flags.Length];
            int n = LightPoolRules.ShadowPlan(flags, flags.Length, S7Conventions.LightLimits.MaxShadowedSpots, sh);
            Assert.AreEqual(2, n);
            CollectionAssert.AreEqual(new[] { false, true, false, false, true, false, false }, sh, "the two nearest shadowed spot rows");
            n = LightPoolRules.ShadowPlan(flags, flags.Length, -1, sh);
            Assert.AreEqual(4, n, "S7.M18: every shadowed spot row");
            n = LightPoolRules.ShadowPlan(flags, 0, 2, sh);
            Assert.AreEqual(0, n);
        }

        [TestMethod]
        public void Mapping_IntensityRangeCone()
        {
            Assert.AreEqual(3f, LightPoolRules.Intensity(1.5f, 2f));
            Assert.AreEqual(0f, LightPoolRules.Intensity(float.NaN, 1f));
            Assert.AreEqual(0f, LightPoolRules.Intensity(-1f, 1f));
            Assert.AreEqual(5f, LightPoolRules.Range(5f, 1f));
            Assert.AreEqual(0.01f, LightPoolRules.Range(0f, 1f));
            Assert.AreEqual(60f, LightPoolRules.SpotAngle(60f));
            Assert.AreEqual(179f, LightPoolRules.SpotAngle(200f));
            Assert.AreEqual(1f, LightPoolRules.SpotAngle(0f));
            // SE's own conversion (MyLight.ConeDegreesToConeMaxAngleCos / ConeMaxAngleCosToDegrees): 1 - cos(rad/2) and back
            foreach (double deg in new[] { 10.0, 45.0, 90.0, 136.2, 170.0 })
            {
                float c = (float)(1.0 - Math.Cos(deg * Math.PI / 180.0 / 2.0));
                Assert.AreEqual(deg, LightPoolRules.ConeDegreesFromMaxAngleCos(c), 1e-3, "cone " + deg);
            }
            // the character's reflector: ReflectorConeMaxAngleCos = 0.373 (MyRenderComponentCharacter) = a 102.0 deg full cone
            Assert.AreEqual(2.0 * Math.Acos(1.0 - 0.373) * 180.0 / Math.PI, LightPoolRules.ConeDegreesFromMaxAngleCos(0.373f), 1e-3);
        }

        [TestMethod]
        public void Reader_CopiesTheRowsInTheWindow_DropsPreS7Rows_TakesLightsFoundForTheTick()
        {
            var region = new byte[0x40000];
            var reader = new S7LightReader();
            var outRows = new SeLight[64];
            ulong tick;
            uint found;
            int serial;
            fixed (byte* r = region)
            {
                *(uint*)(r + 0x18) = 3; // lightCount
                var rows = (SeLight*)(r + SeEntitiesLimits.LightsOffset);
                rows[0] = new SeLight { ownerId = 7, range = 5f, intensity = 1f, flags = S7LightRow.Flags(false, false, true, false) };
                rows[1] = new SeLight { ownerId = 7, range = 5f, intensity = 1f, flags = SeLightFlags.On }; // version 0: dropped
                rows[2] = new SeLight { ownerId = 0, range = 20f, intensity = 2f, coneDeg = 60f, flags = S7LightRow.Flags(true, true, true, true) };
                C2Ext.Write(r, C2Ext.For(1234UL, S7Conventions.C2ExtFlags.Lights, 0, 1, 1, 0, 9));
                reader.CopyInWindow(r);
            }
            reader.Commit(1234UL);
            int n = reader.Snapshot(outRows, out tick, out found, out serial);
            Assert.AreEqual(2, n);
            Assert.AreEqual(1234UL, tick);
            Assert.AreEqual(9u, found, "lightsFound of a valid extension");
            Assert.AreEqual(1, reader.RowsNotV1);
            Assert.AreEqual(7UL, outRows[0].ownerId);
            Assert.IsTrue(S7LightRow.IsSpot(outRows[1].flags));
            // a stale extension (another tick): found falls back to the row count
            fixed (byte* r = region) reader.CopyInWindow(r);
            reader.Commit(1235UL);
            n = reader.Snapshot(outRows, out tick, out found, out serial);
            Assert.AreEqual(2u, found);
            // a huge count is clamped to the table
            fixed (byte* r = region) { *(uint*)(r + 0x18) = 1000; reader.CopyInWindow(r); }
            reader.Commit(1236UL);
            Assert.AreEqual(1, reader.RowsTruncated);
            reader.Reset();
            Assert.AreEqual(0, reader.Snapshot(outRows, out tick, out found, out serial));
        }

        [TestMethod]
        public void Reader_ReviewF20_StaleAfterOneSecond_ResetForgets()
        {
            var reader = new S7LightReader();
            Assert.IsTrue(double.IsPositiveInfinity(reader.SecondsSinceCommit), "nothing committed yet");
            reader.Commit(5);
            Assert.IsTrue(reader.SecondsSinceCommit < S7LightReader.StaleSeconds, "a fresh commit");
            reader.Reset();
            Assert.IsTrue(double.IsPositiveInfinity(reader.SecondsSinceCommit), "a new SE peer: nothing to draw until the next commit");
            Assert.AreEqual(1.0, S7LightReader.StaleSeconds);
        }

        [TestMethod]
        public void Calibration_RatioPassScale()
        {
            Assert.AreEqual(0.0, LightCalibration.LuminanceSrgb(0, 0, 0), 1e-12);
            Assert.AreEqual(1.0, LightCalibration.LuminanceSrgb(1, 1, 1), 1e-9);
            Assert.AreEqual(0.214, LightCalibration.LuminanceSrgb(0.5, 0.5, 0.5), 1e-3, "sRGB 0.5 is 21.4 % linear");
            Assert.IsTrue(LightCalibration.Pass(1.0));
            Assert.IsTrue(LightCalibration.Pass(0.75));
            Assert.IsTrue(LightCalibration.Pass(1.25));
            Assert.IsFalse(LightCalibration.Pass(1.26));
            Assert.IsFalse(LightCalibration.Pass(0.74));
            Assert.IsFalse(LightCalibration.Pass(double.NaN));
            Assert.IsTrue(double.IsNaN(LightCalibration.Ratio(0.3, 0)));
            Assert.AreEqual(2.0, LightCalibration.Ratio(0.4, 0.2), 1e-12);
            Assert.AreEqual(0.5, LightCalibration.SuggestScale(1.0, 0.4, 0.2), 1e-12, "twice too bright: half the scale");
            Assert.AreEqual(3.0, LightCalibration.SuggestScale(1.5, 0.1, 0.2), 1e-12);
            Assert.AreEqual(LightCalibration.MaxScale, LightCalibration.SuggestScale(1.0, 1e-6, 1.0));
            Assert.AreEqual(1.0, LightCalibration.SuggestScale(1.0, 0, 0.2), "a black probe changes nothing");
            // after applying the suggestion the (linear) ratio is 1
            double se = 0.12, sm = 0.3, k = 1.0;
            double k2 = LightCalibration.SuggestScale(k, se, sm);
            Assert.IsTrue(LightCalibration.Pass(LightCalibration.Ratio(se * k2 / k, sm)));
        }
    }
}
