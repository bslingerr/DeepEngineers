using System;
using System.Collections.Generic;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7t-sn-altitude (ROADMAP "### S7t Seamless transition" T2 and T3's Subnautica half): the far plane by altitude, the
 /// haze table, the horizon skirt's rows and colours, the live settings and the impostor rules (including the coarse
 /// cell build in MeshBuilder). Pure rules: no game type, no Unity type; replay-exact.
 /// </summary>
    [TestClass]
    public class S7tAltitudeTests
    {
        private const double Base = 1700.0; // Subnautica's own far plane (DE.S1 camera line, sn-20261017_224212-5380)

        private static AltitudeFrame At(double alt, bool impostors = false, AltitudeSettings s = null)
        {
            return HorizonRules.Frame(s ?? new AltitudeSettings(), HorizonRules.DefaultTable, alt, Base, impostors);
        }

        // ------------------------------------------------------------------------------------------ the horizon distance

        [TestMethod]
        public void S7tT2_HorizonDistance_OnSesSea_Rs60km()
        {
            Assert.AreEqual(0.0, HorizonRules.HorizonDistanceM(0));
            Assert.AreEqual(0.0, HorizonRules.HorizonDistanceM(-10));
            Assert.AreEqual(0.0, HorizonRules.HorizonDistanceM(double.NaN));
            Assert.AreEqual(Math.Sqrt(2 * SiteV1.RsExpected * 380 + 380.0 * 380), HorizonRules.HorizonDistanceM(380), 1e-9);
            Assert.AreEqual(6763.1, HorizonRules.HorizonDistanceM(380), 0.5, "the owner's 380 m screenshot: 6.8 km");
            Assert.AreEqual(11000.0, HorizonRules.HorizonDistanceM(1000), 1.0, "the band (+1,000 m)");
            Assert.AreEqual(13500.0, HorizonRules.HorizonDistanceM(1500), 1.0);
            Assert.AreEqual(100.0, HorizonRules.HorizonDistanceM(1, 5000), 0.01, "sqrt(2 x 5000 x 1 + 1)");
        }

        // ------------------------------------------------------------------------------------------ the table

        [TestMethod]
        public void S7tT2_Table_SortedAndInRange()
        {
            HazeRow[] t = HorizonRules.DefaultTable;
            for (int i = 0; i < t.Length; i++)
            {
                if (i > 0) Assert.IsTrue(t[i].AltM > t[i - 1].AltM, "sorted by altitude");
                Assert.IsTrue(t[i].FadeStart >= 0.05 && t[i].FadeStart < t[i].FadeEnd && t[i].FadeEnd <= 1.0, "row " + i);
                Assert.AreEqual(1.0, t[i].FogMul, "uSky's own density everywhere by default (the measured aerial perspective kept)");
                Assert.IsTrue(t[i].Alpha >= 0 && t[i].Alpha <= 1);
                Assert.IsTrue(t[i].FarK > 0);
            }
            Assert.AreEqual(0.0, t[0].Alpha, "at the sea surface no skirt: Subnautica's own surface look");
        }

        [TestMethod]
        public void S7tT2_Sample_ExactInterpolatedClamped()
        {
            HazeRow[] t = HorizonRules.DefaultTable;
            HazeRow r = HorizonRules.Sample(t, 400);
            Assert.AreEqual(0.45, r.FadeStart, 1e-12);
            r = HorizonRules.Sample(t, 275); // halfway 150 -> 400
            Assert.AreEqual((0.55 + 0.45) / 2, r.FadeStart, 1e-12);
            Assert.AreEqual((0.97 + 0.96) / 2, r.FadeEnd, 1e-12);
            Assert.AreEqual(275, r.AltM);
            r = HorizonRules.Sample(t, 25); // halfway 0 -> 50: alpha 0 -> 1
            Assert.AreEqual(0.5, r.Alpha, 1e-12);
            Assert.AreEqual(t[0].FadeStart, HorizonRules.Sample(t, -50).FadeStart);
            Assert.AreEqual(t[t.Length - 1].FadeStart, HorizonRules.Sample(t, 9000).FadeStart);
            Assert.AreEqual(t[0].FadeStart, HorizonRules.Sample(t, double.NaN).FadeStart);
            Assert.AreEqual(0.0, HorizonRules.Sample(new HazeRow[0], 100).Alpha, "an empty table draws nothing");
        }

        // ------------------------------------------------------------------------------------------ the frame

        [TestMethod]
        public void S7tT2_Frame_BelowAndOff_KeepSubnauticasOwnLook()
        {
            AltitudeFrame f = At(1.0);
            Assert.AreEqual(AltitudeFrame.Below, f.State);
            Assert.AreEqual(Base, f.FarM);
            Assert.IsFalse(f.SkirtOn);
            Assert.AreEqual(1.0, f.FogMul);
            f = At(-500);
            Assert.AreEqual(AltitudeFrame.Below, f.State, "under water: nothing of S7t");
            Assert.AreEqual(Base, f.FarM);
            // R2: at the surface with impostors the far plane reaches them; under water never
            f = At(1.0, true);
            Assert.AreEqual(AltitudeFrame.Below, f.State);
            Assert.IsTrue(f.ImpostorFar && f.FarM > Base, "swimming at the surface: a ship parked 2-5 km up stays in view");
            Assert.IsFalse(f.SkirtOn);
            Assert.AreEqual(1.0, f.FogMul);
            f = At(-1.0, true);
            Assert.AreEqual(Base, f.FarM, "under the surface: Subnautica's own far plane even with impostors");
            var off = new AltitudeSettings { Horizon = false };
            f = At(1000, true, off);
            Assert.AreEqual(AltitudeFrame.Off, f.State);
            Assert.AreEqual(Base, f.FarM, "switched off: the far plane stays Subnautica's even with impostors");
            Assert.IsFalse(f.SkirtOn);
            Assert.AreEqual(AltitudeFrame.Off, HorizonRules.Frame(null, HorizonRules.DefaultTable, 500, Base, false).State);
        }

        [TestMethod]
        public void S7tT2_Frame_TheBand_SeaToTheHorizon_OldPictureKeptInside1700m()
        {
            AltitudeFrame f = At(380);
            Assert.AreEqual(AltitudeFrame.Above, f.State);
            Assert.AreEqual(HorizonRules.HorizonDistanceM(380), f.FarM, 1e-6, "farK 1: the water's square reaches the horizon of SE's planet");
            Assert.IsTrue(f.SkirtOn);
            Assert.IsTrue(f.SkirtRadiusM > Base, "the fade starts beyond the old 1.7 km water: the measured aerial perspective is kept");
            f = At(1000);
            Assert.AreEqual(11000.0, f.FarM, 1.0);
            Assert.AreEqual(0.40 * f.FarM, f.SkirtRadiusM, 1e-6);
            Assert.AreEqual(0.95 / 0.40, f.FadeRatio, 1e-12);
            for (double alt = 50; alt <= 3000; alt += 10)
            {
                f = At(alt);
                Assert.IsTrue(f.SkirtRadiusM >= Base * 0.99, "alt " + alt + ": skirt R " + f.SkirtRadiusM);
                Assert.IsTrue(f.FarM >= Base && f.FarM <= HorizonRules.DefaultFarMaxM, "alt " + alt);
                // the skirt's rows (radius R, down to the fade start's depression) lie inside the far plane in every direction
                Assert.IsTrue(Math.Sqrt(f.SkirtRadiusM * f.SkirtRadiusM + alt * alt) < f.FarM, "alt " + alt + ": the skirt is not clipped");
                // the water's far edge (forward: the far plane) is fully hazed: v = R / far >= 1 / ratio
                Assert.AreEqual(f.Alpha, HorizonRules.RowAlpha(f.SkirtRadiusM / f.FarM, f.FadeRatio, f.Alpha), 1e-9, "alt " + alt + ": no visible edge");
            }
        }

        [TestMethod]
        public void S7tT2_Frame_Continuous_ThroughTheDive()
        {
            AltitudeFrame prev = At(2.0);
            for (double alt = 2.0; alt <= 2000; alt += 0.5)
            {
                AltitudeFrame f = At(alt);
                Assert.IsTrue(Math.Abs(f.FarM - prev.FarM) < 40, "alt " + alt + ": the far plane moves smoothly (" + prev.FarM + " -> " + f.FarM + ")");
                Assert.IsTrue(Math.Abs(f.Alpha - prev.Alpha) < 0.02, "alt " + alt);
                Assert.IsTrue(Math.Abs(f.SkirtRadiusM - prev.SkirtRadiusM) < 40, "alt " + alt);
                prev = f;
            }
        }

        [TestMethod]
        public void S7tT3_Frame_ImpostorsPushTheFarPlane_CapAndBase()
        {
            AltitudeFrame f = At(50, true);
            Assert.AreEqual(ImpostorRules.DefaultFarM, f.FarM, "a ship 5 km up is not clipped at 1.7 km");
            Assert.IsTrue(f.ImpostorFar);
            Assert.AreEqual(HorizonRules.HorizonDistanceM(50), At(50, false).FarM, 1e-6);
            Assert.AreEqual(HorizonRules.HorizonDistanceM(1000), At(1000, true).FarM, 1e-6, "the horizon is farther: impostors change nothing");
            Assert.AreEqual(HorizonRules.DefaultFarMaxM, At(5000).FarM, "capped at farMax");
            var s = new AltitudeSettings { Impostors = false };
            Assert.AreEqual(HorizonRules.HorizonDistanceM(50), At(50, true, s).FarM, 1e-6, "impostors off: no extra far plane");
            s = new AltitudeSettings { FarK = 0.1 };
            Assert.AreEqual(Base, At(380, false, s).FarM, "never below Subnautica's own far plane");
        }

        // ------------------------------------------------------------------------------------------ the skirt rows

        [TestMethod]
        public void S7tT2_Rows_AlphaRamp_FeatherAndHorizon()
        {
            float[] rows = HorizonRules.SkirtRows;
            Assert.AreEqual(0f, rows[0], "row 0 is the horizon");
            Assert.AreEqual(1f, rows[rows.Length - 1], "the last row is the fade start");
            for (int i = 1; i < rows.Length; i++) Assert.IsTrue(rows[i] > rows[i - 1]);
            double ratio = 0.95 / 0.40;
            Assert.AreEqual(0.0, HorizonRules.RowAlpha(-0.06, ratio, 1), "the feather row (above the horizon) is transparent");
            Assert.AreEqual(0.8, HorizonRules.RowAlpha(0, ratio, 0.8), 1e-12, "the horizon carries the frame's alpha");
            Assert.AreEqual(0.0, HorizonRules.RowAlpha(1, ratio, 1), 1e-12, "the fade start is transparent: no seam with the water");
            Assert.AreEqual(1.0, HorizonRules.RowAlpha(1 / ratio, ratio, 1), 1e-9, "opaque from the fade end out");
            double last = 2;
            for (double v = 0.0; v <= 1.0; v += 0.01)
            {
                double a = HorizonRules.RowAlpha(v, ratio, 1);
                Assert.IsTrue(a <= last + 1e-12, "v " + v + ": the haze grows with distance");
                last = a;
            }
            // the row v shows the sea at distance R / v: alpha follows the sea distance only
            double r = 4400, d = 7000;
            Assert.AreEqual(HorizonRules.SmoothStep(r, r * ratio, d), HorizonRules.RowAlpha(r / d, ratio, 1), 1e-9);
            Assert.AreEqual(1.0, HorizonRules.RowAlpha(0.5, 1.0, 1), 1e-12, "a degenerate ratio is a hard step, not NaN");
        }

        [TestMethod]
        public void S7tT2_Rows_Colours_LinearLight_TodAndGain()
        {
            HazeRgb low = HorizonRules.DefaultLowRgb, top = HorizonRules.DefaultHorizonRgb;
            Assert.AreEqual(new HazeRgb(197, 241, 255), top, "the measured SN sky at the horizon (23:05 pair)");
            Assert.AreEqual(new HazeRgb(129, 155, 187), low, "the measured SN far water at 380 m");
            Assert.AreEqual(new HazeRgb(DeepEngineers.Wire.S7tBandTargets.HazeHorizon.R, DeepEngineers.Wire.S7tBandTargets.HazeHorizon.G, DeepEngineers.Wire.S7tBandTargets.HazeHorizon.B), top, "S7t integrator: the one measured source (Wire S7tBandTargets)");
            Assert.AreEqual(new HazeRgb(DeepEngineers.Wire.S7tBandTargets.HazeLow.R, DeepEngineers.Wire.S7tBandTargets.HazeLow.G, DeepEngineers.Wire.S7tBandTargets.HazeLow.B), low, "S7t integrator: the one measured source (Wire S7tBandTargets)");
            double r, g, b;
            HorizonRules.RowColour(low, top, 0, 1, 1, 1, 1, out r, out g, out b);
            Assert.AreEqual(HorizonRules.SrgbToLinear(197 / 255.0), r, 1e-12);
            Assert.AreEqual(HorizonRules.SrgbToLinear(255 / 255.0), b, 1e-12);
            HorizonRules.RowColour(low, top, -0.1, 1, 1, 1, 1, out r, out g, out b);
            Assert.AreEqual(HorizonRules.SrgbToLinear(241 / 255.0), g, 1e-12, "the feather row keeps the horizon colour");
            HorizonRules.RowColour(low, top, 1, 1, 1, 1, 1, out r, out g, out b);
            Assert.AreEqual(HorizonRules.SrgbToLinear(129 / 255.0), r, 1e-12);
            HorizonRules.RowColour(low, top, 0.5, 0.5, 1, 2, 1.5, out r, out g, out b);
            double mid = (HorizonRules.SrgbToLinear(129 / 255.0) + HorizonRules.SrgbToLinear(197 / 255.0)) / 2;
            Assert.AreEqual(mid * 0.5 * 1.5, r, 1e-12);
            Assert.AreEqual(0.0, HorizonRules.SrgbToLinear(0));
            Assert.AreEqual(1.0, HorizonRules.SrgbToLinear(1));
            Assert.AreEqual(0.2140, HorizonRules.SrgbToLinear(0.5), 1e-4);
        }

        [TestMethod]
        public void S7tT2_Tod_RatioOfUskysOwnMeanSky_ClampedAndSafe()
        {
            double r, g, b;
            HorizonRules.TodFactor(0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.02, out r, out g, out b);
            Assert.AreEqual(1.0, r); Assert.AreEqual(1.0, g); Assert.AreEqual(1.0, b);
            HorizonRules.TodFactor(0.25, 0.0, 2.0, 0.5, 0.5, 0.5, 0.02, out r, out g, out b);
            Assert.AreEqual(0.5, r, 1e-12, "dusk: Subnautica's own darkening");
            Assert.AreEqual(0.02, g, 1e-12, "night: the floor");
            Assert.AreEqual(1.5, b, 1e-12, "capped");
            HorizonRules.TodFactor(0.3, 0.3, 0.3, 0.01, double.NaN, 0.5, 0.02, out r, out g, out b);
            Assert.AreEqual(1.0, r, "a dark reference: no ratio");
            Assert.AreEqual(1.0, g, "NaN: no ratio");
            Assert.AreEqual(0.6, b, 1e-12);
        }

        // ------------------------------------------------------------------------------------------ settings and env

        [TestMethod]
        public void S7tT2_Settings_Keys_RangesAndTable()
        {
            var s = new AltitudeSettings();
            string e;
            Assert.IsNull(s.Set("alpha", "0.5"));
            Assert.AreEqual(0.5, s.AlphaMul);
            Assert.IsNull(s.Set("FADESTART", "0.3"));
            Assert.AreEqual(0.3, s.FadeStart);
            Assert.AreEqual(0.3, HorizonRules.Frame(s, HorizonRules.DefaultTable, 1000, Base, false).SkirtRadiusM / HorizonRules.HorizonDistanceM(1000), 1e-9);
            Assert.IsNull(s.Set("fadeStart", "table"));
            Assert.IsTrue(double.IsNaN(s.FadeStart));
            Assert.IsNull(s.Set("horizonRgb", "200,240,250"));
            Assert.AreEqual(new HazeRgb(200, 240, 250), s.HorizonRgb);
            Assert.IsTrue(s.HorizonRgbSet);
            Assert.IsNull(s.Set("lowRgb", "#8090a0"));
            Assert.AreEqual(new HazeRgb(0x80, 0x90, 0xa0), s.LowRgb);
            Assert.IsNull(s.Set("horizon", "off"));
            Assert.IsFalse(s.Horizon);
            Assert.IsNull(s.Set("perFrame", "9"));
            Assert.AreEqual(9, s.CellsPerFrame);
            Assert.IsNotNull(s.Set("alpha", "2"), "out of range");
            Assert.IsNotNull(s.Set("fadeEnd", "x"), "not a number");
            Assert.IsNotNull(s.Set("lowRgb", "1,2"), "two channels");
            Assert.IsNotNull(s.Set("lowRgb", "1,2,300"), "out of 0..255");
            e = s.Set("colour", "1");
            Assert.IsTrue(e != null && e.Contains("unknown key"), e);
            var kv = HorizonRules.ParseKv("alpha=0.7;gain=1.2  tod=off", out e);
            Assert.IsNull(e);
            Assert.AreEqual(3, kv.Count);
            Assert.IsNull(s.Apply(kv));
            Assert.AreEqual(1.2, s.Gain);
            Assert.IsFalse(s.Tod);
            HorizonRules.ParseKv("alpha", out e);
            Assert.IsNotNull(e, "a word without '=' is refused");
            StringAssert.Contains(s.Describe(), "fadeStart=table");
        }

        [TestMethod]
        public void S7tT2_Env_Defaults_BadValuesKeepTheDefaults()
        {
            string e;
            AltitudeSettings s = HorizonRules.FromEnv(null, null, null, out e);
            Assert.IsNull(e);
            Assert.IsTrue(s.Horizon);
            Assert.IsTrue(s.Impostors, "the same default as de.s7t: on");
            s = HorizonRules.FromEnv("off", "fogMul=1.5 feather=0.1", "0", out e);
            Assert.IsNull(e);
            Assert.IsFalse(s.Horizon);
            Assert.IsFalse(s.Impostors);
            Assert.AreEqual(1.5, s.FogMul);
            Assert.AreEqual(0.1, s.Feather);
            s = HorizonRules.FromEnv("maybe", "fogMul=1.5 alpha=7", null, out e);
            Assert.IsNotNull(e);
            Assert.IsTrue(s.Horizon, "a bad toggle keeps on");
            Assert.IsTrue(double.IsNaN(s.FogMul), "a bad haze list keeps the whole table (nothing half applied)");
        }

        // ------------------------------------------------------------------------------------------ T3 impostors

        [TestMethod]
        public void S7tT3_ImpostorBit_Version1Only_NoClashWithS5S7aS7pBits()
        {
            const uint v1 = MirrorGridFlags.Version1;
            Assert.AreEqual(64u, ImpostorRules.ImpostorGridFlag);
            Assert.IsTrue(ImpostorRules.IsImpostor(v1 | ImpostorRules.ImpostorGridFlag));
            Assert.IsTrue(ImpostorRules.IsImpostor(v1 | ImpostorRules.ImpostorGridFlag | MirrorGridFlags.Subgrid));
            Assert.IsFalse(ImpostorRules.IsImpostor(ImpostorRules.ImpostorGridFlag), "an S3 (version 0) GridAdd never carries it");
            Assert.IsFalse(ImpostorRules.IsImpostor(v1));
            Assert.IsFalse(ImpostorRules.IsImpostor(v1 | S7pScene.GridFlagProjection | S7pScene.GridFlagMissile | 8u));
            uint taken = MirrorGridFlags.Static | MirrorGridFlags.Subgrid | MirrorGridFlags.FloatingObject | 8u /* S7a Ghost */ |
                         S7pScene.GridFlagProjection | S7pScene.GridFlagMissile | 0xF0000000u /* version nibble */;
            Assert.AreEqual(0u, taken & ImpostorRules.ImpostorGridFlag, "bit 6 is free in layout 1.0");
            var s = new AltitudeSettings();
            S7tImpostorsShim.Check(s);
        }

        [TestMethod]
        public void S7tT3_LargeBlocksOnly_ByModelExtentAgainstTheBlockSize()
        {
            Assert.AreEqual(2.5, ImpostorRules.BlockSizeM(GridClass.LargeGrid));
            Assert.AreEqual(0.5, ImpostorRules.BlockSizeM(GridClass.SmallGrid));
            Assert.IsTrue(ImpostorRules.KeepPart(2.5, 2.5, 2.5, GridClass.LargeGrid, 0.75), "an armor block");
            Assert.IsTrue(ImpostorRules.KeepPart(0.2, 7.5, 0.3, GridClass.LargeGrid, 0.75), "a long thin block counts by its largest extent");
            Assert.IsFalse(ImpostorRules.KeepPart(1.0, 0.5, 0.8, GridClass.LargeGrid, 0.75), "a greeble");
            Assert.IsTrue(ImpostorRules.KeepPart(0.5, 0.5, 0.5, GridClass.SmallGrid, 0.75));
            Assert.IsFalse(ImpostorRules.KeepPart(0.2, 0.1, 0.3, GridClass.SmallGrid, 0.75));
            Assert.IsTrue(ImpostorRules.KeepPart(0.01, 0, 0, GridClass.LargeGrid, 0), "minPart 0: everything");
            Assert.IsFalse(ImpostorRules.KeepPart(double.NaN, double.NaN, double.NaN, GridClass.LargeGrid, 0));
        }

        [TestMethod]
        public void S7tT3_Cull_AboveTheSeaToTheVerticalReach_UnderwaterUnchanged()
        {
            double above = ImpostorRules.CullM(true, 10, 5000, 200);
            Assert.AreEqual(Math.Sqrt(1600.0 * 1600 + 5000.0 * 5000) + 100, above, 1e-9);
            Assert.IsTrue(above <= ImpostorRules.DefaultFarM, "the far plane with impostors covers their cull distance");
            // the SE producer's reach (Wire S7tImpostorRules, S7t-se-composite): an impostor at its leave edge is drawn and not clipped
            Assert.AreEqual(S7tImpostorRules.UpLeaveM, ImpostorRules.DefaultReachM);
            Assert.AreEqual(S7tImpostorRules.GridFlagImpostor, ImpostorRules.ImpostorGridFlag);
            double edge = Math.Sqrt(S7tImpostorRules.HorizontalLeaveM * S7tImpostorRules.HorizontalLeaveM + S7tImpostorRules.UpLeaveM * S7tImpostorRules.UpLeaveM);
            double cullDefault = ImpostorRules.CullM(true, 10, ImpostorRules.DefaultReachM, 200);
            Assert.IsTrue(edge < cullDefault && cullDefault <= ImpostorRules.DefaultFarM, "edge " + edge + " cull " + cullDefault + " far " + ImpostorRules.DefaultFarM);
            Assert.AreEqual(200, ImpostorRules.CullM(true, -5, 5000, 200), "under water: S5's own distance");
            Assert.AreEqual(200, ImpostorRules.CullM(false, 500, 5000, 200), "Waterline policy: S5's own distance");
            Assert.IsTrue(ImpostorRules.MayPost(0, 4));
            Assert.IsTrue(ImpostorRules.MayPost(3, 4));
            Assert.IsFalse(ImpostorRules.MayPost(4, 4), "capped per frame");
            Assert.IsFalse(ImpostorRules.MayPost(0, 0), "perFrame 0 posts nothing (deferred)");
        }

        [TestMethod]
        public void S7tT3_CoarseCell_OneFlatChunkPerColour_NoShadows_EveryTriangleOnce()
        {
            const ulong matA = 0xA000000000000001UL, matB = 0xB000000000000002UL;
            ModelData cube = ProceduralCube.Model(0xC0BE, 1.25f, matA);
            cube.Submeshes = new[] { new SubmeshInfo { MaterialKey = matA, TriStart = 0, TriCount = 6 }, new SubmeshInfo { MaterialKey = matB, TriStart = 6, TriCount = 6 } };
            cube.SubmeshCount = 2;
            PreparedModel pm = MeshBuilder.Prepare(cube, true, true);
            Assert.IsTrue(ImpostorRules.KeepPart(pm.MaxX - pm.MinX, pm.MaxY - pm.MinY, pm.MaxZ - pm.MinZ, GridClass.LargeGrid, 0.75), "a 2.5 m cube is a large block");

            CellBuildResult full = MeshBuilder.BuildCell(Input(pm, false));
            CellBuildResult coarse = MeshBuilder.BuildCell(Input(pm, true));
            Assert.AreEqual(2, full.Chunks.Count, "full: one chunk per SE material");
            Assert.AreEqual(1, coarse.Chunks.Count, "impostor: one chunk per colour");
            Assert.AreEqual(ImpostorRules.MaterialKey, coarse.Chunks[0].Key.MaterialKey);
            Assert.IsTrue(coarse.Chunks[0].Key.NoShadows, "no shadows");
            Assert.AreEqual(full.Triangles, coarse.Triangles, "every triangle drawn once");
            Assert.AreEqual(12, coarse.Chunks[0].TriangleCount);
            Assert.AreNotEqual(ImpostorRules.MaterialKey, full.Chunks[0].Key.MaterialKey);
        }

        private static CellBuildInput Input(PreparedModel pm, bool impostor)
        {
            var input = new CellBuildInput { GridId = 7, Options = new BuildOptions(), Impostor = impostor };
            var p = new PartRecord { EntityId = 7, PartKey = 1, ModelKey = 0xC0BE, H = 0.1f, S = 0.2f, V = 0.1f };
            float[] id = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
            Array.Copy(id, p.Local, 12);
            input.Parts.Add(new PartBuild { Part = p, Model = pm });
            return input;
        }

        /// <summary>The settings' impostor keys round-trip (the S7tImpostors switches read them on the Unity side).</summary>
        private static class S7tImpostorsShim
        {
            public static void Check(AltitudeSettings s)
            {
                Assert.IsNull(s.Set("impostor", "off"));
                Assert.IsFalse(s.Impostors);
                Assert.IsNull(s.Set("minPart", "0.5"));
                Assert.AreEqual(0.5, s.MinPartFrac);
                Assert.IsNull(s.Set("reach", "3000"));
                Assert.AreEqual(3000, s.ReachM);
                Assert.IsNotNull(s.Set("perFrame", "-1"));
            }
        }
    }
}
