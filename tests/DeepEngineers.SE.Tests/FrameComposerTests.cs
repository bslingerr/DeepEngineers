using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// The C1 SeFrame composition of the S1 frame writer (src/DeepEngineers.SE/FrameComposer.cs) fed with fake camera and
 /// character matrices, checked field by field against tools/worldmap/wire_ref.py (through c1_oracle.py, never the
 /// C# code): camPos = F(Pcam), camRot = LookRotation(mapdir(F), mapdir(U)), vFovDeg, the character pose (o, q, s)
 /// at P*, pelvisSn = F(pelvis). Also: flags, teleport/jump detection, camera velocity, the no-character defaults, the
 /// test-command geometry (teleport, look, path), the de.pixel projection, zones and perf mode, the C1/C5 writes into a
 /// real section, and the S1.M1 seeded failure turned red.
 /// </summary>
    [TestClass]
    public unsafe class FrameComposerTests
    {
        public TestContext TestContext { get; set; }

        private const double PosTolD = 1e-6;    // CONTRACT §2.9: positions in doubles
        private const double PosTolF = 1e-3;    // after float32
        private const double RotTolD = 1e-9;
        private const double RotTolF = 1e-6;

        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); }

        // =============================================================================================== oracle cases

        private sealed class Case
        {
            public string Id;
            public SeMatrix Cam;
            public double Fov;
            public bool HasChar;
            public SeMatrix Char;
            public List<SeMatrix> MemberM = new List<SeMatrix>();
            public List<Vec3> MemberMin = new List<Vec3>();
            public List<Vec3> MemberMax = new List<Vec3>();
            public Vec3 Pelvis;
        }

        private static SeMatrix RandomRotation(Random rnd, Vec3 t)
        {
            // a random unit quaternion's rotation matrix, as SE rows (Right, Up, Backward)
            double a = rnd.NextDouble() * 2 - 1, b = rnd.NextDouble() * 2 - 1, c = rnd.NextDouble() * 2 - 1, d = rnd.NextDouble() * 2 - 1;
            double n = Math.Sqrt(a * a + b * b + c * c + d * d);
            var q = new Quat(a / n, b / n, c / n, d / n);
            return new SeMatrix(q.Rotate(Vec3.UnitX), q.Rotate(Vec3.UnitY), q.Rotate(Vec3.UnitZ), t);
        }

        private static List<Case> MakeCases(SiteMap site)
        {
            var cases = new List<Case>();
            // S1.WM-2: spectator at F^-1(-1029, -150, -1116) facing SN north
            cases.Add(new Case { Id = "wm2", Cam = S1Math.SpectatorAt(site, new Vec3(-1029, -150, -1116), S1Math.HeadingSn(0), Vec3.UnitY), Fov = 70 * Math.PI / 180 });
            // S1.WM-4: deepest floor, looking E, N, down; a tile corner looking yaw +30 from north
            Vec3 floor = new Vec3(198, -1657, -518);
            foreach (string dir in new[] { "E", "N", "down" })
            {
                Vec3 f, u; string label;
                Assert.IsTrue(S1Math.LookDirections(dir, Vec3.UnitZ, Vec3.UnitY, out f, out u, out label));
                cases.Add(new Case { Id = "wm4-" + label, Cam = S1Math.SpectatorAt(site, floor, f, u), Fov = 1.2 });
            }
            {
                Vec3 f, u; string label;
                Assert.IsTrue(S1Math.LookDirections("yaw+30", Vec3.UnitZ, Vec3.UnitY, out f, out u, out label));
                cases.Add(new Case { Id = "corner-" + label, Cam = S1Math.SpectatorAt(site, new Vec3(2048, -500, 2048), f, u), Fov = 0.9 });
            }
            // S1.WM-3 last sample
            cases.Add(new Case { Id = "wm3-last", Cam = S1Math.SpectatorAt(site, S1Math.PathSampleSn(99, 100, 2000, -100, 90), S1Math.HeadingSn(90), Vec3.UnitY), Fov = 1.0 });

            // random cameras with a character and a pivot group (character box + a grid box)
            var rnd = new Random(20261012);
            for (int i = 0; i < 40; i++)
            {
                var sn = new Vec3(rnd.NextDouble() * 5000 - 2500, rnd.NextDouble() * 3390 - 3040, rnd.NextDouble() * 5000 - 2500);
                Vec3 camSe = site.SnToSe(sn);
                var c = new Case { Id = "rnd" + i, Cam = RandomRotation(rnd, camSe), Fov = 0.6 + rnd.NextDouble(), HasChar = i % 4 != 3 };
                if (c.HasChar)
                {
                    Vec3 off = new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * (i % 5 == 0 ? 0.2 : 400.0);
                    c.Char = RandomRotation(rnd, camSe + off);
                    c.Pelvis = c.Char.Transform(new Vec3(0, 0.95, 0));
                    c.MemberM.Add(c.Char); c.MemberMin.Add(new Vec3(-0.5, 0, -0.5)); c.MemberMax.Add(new Vec3(0.5, 1.8, 0.5));
                    if (i % 2 == 0)
                    {
                        SeMatrix grid = RandomRotation(rnd, c.Char.Translation + new Vec3(3, -2, 5));
                        c.MemberM.Add(grid); c.MemberMin.Add(new Vec3(-12.5, -7.5, -40)); c.MemberMax.Add(new Vec3(12.5, 7.5, 40));
                    }
                }
                cases.Add(c);
            }
            // camera inside the character's box (first person): P* = the camera
            {
                SeMatrix ch = S1Math.SpectatorAt(site, new Vec3(10, -30, 20), S1Math.HeadingSn(45), Vec3.UnitY);
                var c = new Case { Id = "first-person", HasChar = true, Char = ch, Fov = 1.2, Pelvis = ch.Transform(new Vec3(0, 0.95, 0)) };
                c.Cam = new SeMatrix(ch.Right, ch.Up, ch.Backward, ch.Transform(new Vec3(0, 1.6, -0.1)));
                c.MemberM.Add(ch); c.MemberMin.Add(new Vec3(-0.5, 0, -0.5)); c.MemberMax.Add(new Vec3(0.5, 1.8, 0.5));
                cases.Add(c);
            }
            return cases;
        }

        private static string G(double v) { return v.ToString("G17", CultureInfo.InvariantCulture); }
        private static string V(Vec3 v) { return "[" + G(v.X) + "," + G(v.Y) + "," + G(v.Z) + "]"; }
        private static string Rows(SeMatrix m) { return "[" + V(m.Right) + "," + V(m.Up) + "," + V(m.Backward) + "," + V(m.Translation) + "]"; }

        private static Dictionary<string, IDictionary<string, object>> RunOracle(SiteMap site, string siteSpec, List<Case> cases, string tag)
        {
            var sb = new StringBuilder();
            sb.Append("{\"site\":").Append(siteSpec).Append(",\"cases\":[");
            for (int i = 0; i < cases.Count; i++)
            {
                Case c = cases[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(c.Id).Append("\",\"cam\":").Append(Rows(c.Cam)).Append(",\"fovRad\":").Append(G(c.Fov));
                if (c.HasChar)
                {
                    sb.Append(",\"char\":{\"m\":").Append(Rows(c.Char)).Append(",\"pelvis\":").Append(V(c.Pelvis)).Append(",\"members\":[");
                    for (int k = 0; k < c.MemberM.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append("{\"m\":").Append(Rows(c.MemberM[k])).Append(",\"min\":").Append(V(c.MemberMin[k])).Append(",\"max\":").Append(V(c.MemberMax[k])).Append('}');
                    }
                    sb.Append("]}");
                }
                sb.Append('}');
            }
            sb.Append("]}");
            string dir = PyOracle.PathIn("tests", "out", "se-s1");
            Directory.CreateDirectory(dir);
            string inPath = Path.Combine(dir, "c1-oracle-" + tag + "-in.json"), outPath = Path.Combine(dir, "c1-oracle-" + tag + "-out.json");
            File.WriteAllText(inPath, sb.ToString(), new UTF8Encoding(false));
            string stdout = PyOracle.Run(Path.Combine("tests", "DeepEngineers.SE.Tests", "c1_oracle.py"), inPath, outPath);
            Assert.IsTrue(stdout.Contains("C1 ORACLE OK"), stdout);
            var root = (IDictionary<string, object>)MiniJson.Parse(File.ReadAllText(outPath, Encoding.UTF8));
            var byId = new Dictionary<string, IDictionary<string, object>>(StringComparer.Ordinal);
            foreach (object o in (List<object>)root["cases"])
            {
                var d = (IDictionary<string, object>)o;
                byId[(string)d["id"]] = d;
            }
            return byId;
        }

        private static Vec3 Vx(IDictionary<string, object> d, string key)
        {
            var l = (List<object>)d[key];
            return new Vec3((double)l[0], (double)l[1], (double)l[2]);
        }

        private static Quat Qx(IDictionary<string, object> d, string key)
        {
            var l = (List<object>)d[key];
            return new Quat((double)l[0], (double)l[1], (double)l[2], (double)l[3]);
        }

        private static Vec3 Fv(F32x3 v) { return new Vec3(v.x, v.y, v.z); }
        private static Quat Fq(F32x4 q) { return new Quat(q.x, q.y, q.z, q.w); }

        private void ComposeAndCompare(SiteMap site, string siteSpec, string tag)
        {
            List<Case> cases = MakeCases(site);
            var oracle = RunOracle(site, siteSpec, cases, tag);
            double worstPosD = 0, worstPosF = 0, worstRotD = 0, worstRotF = 0, worstFov = 0, worstS = 0, worstPivot = 0;
            foreach (Case c in cases)
            {
                IDictionary<string, object> o = oracle[c.Id];
                var composer = new FrameComposer();
                var group = new PivotGroup(8);
                for (int k = 0; k < c.MemberM.Count; k++) group.Add(c.MemberM[k], c.MemberMin[k], c.MemberMax[k]);
                var clock = new FrameClock { Tick = 1000, Qpc = 123456789, SimTimeSec = 16.65, SimSpeed = 1f, WorldEpoch = 3, InWorld = true };
                var cam = new CameraSample { World = c.Cam, FovRad = c.Fov, Near = 0.05f, Aspect = 16f / 9f, Mode = CameraMode.Spectator };
                var ch = new CharacterSample();
                if (c.HasChar) ch = new CharacterSample { Present = true, EntityId = 0x1234567890UL, World = c.Char, PelvisWorld = c.Pelvis, PelvisFromBone = true };
                SeFrame f = default(SeFrame);
                FrameDiagnostics d;
                composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);

                Vec3 camPos = Vx(o, "camPos");
                Quat camRot = Qx(o, "camRot");
                worstPosD = Math.Max(worstPosD, Vec3.MaxAbsDiff(d.CamSn, camPos));
                worstPosF = Math.Max(worstPosF, Vec3.MaxAbsDiff(Fv(f.camPos), camPos));
                worstRotD = Math.Max(worstRotD, Quat.RotationDistance(d.CamRot, camRot));
                worstRotF = Math.Max(worstRotF, Quat.RotationDistance(Fq(f.camRot), camRot));
                worstFov = Math.Max(worstFov, Math.Abs(f.vFovDeg - (double)o["vFovDeg"]));
                Assert.IsTrue(f.camRot.w >= 0f, c.Id + ": camRot w >= 0");
                Assert.AreEqual(1000UL, f.tick, c.Id);
                Assert.AreEqual(123456789L, f.qpcCapture, c.Id);
                Assert.AreEqual(3u, f.worldEpoch, c.Id);
                Assert.AreEqual(0.05f, f.near, c.Id);
                Assert.AreEqual(CameraMode.Spectator, f.cameraMode, c.Id);
                Assert.AreEqual(0u, f.boneCount, c.Id + ": S1 has no bones");
                Assert.AreEqual((float)(camPos.Y), (float)d.CamSn.Y, 1e-3f, c.Id);
                if (c.HasChar)
                {
                    worstPivot = Math.Max(worstPivot, Vec3.MaxAbsDiff(d.PivotSe, Vx(o, "pivot")));
                    worstPosF = Math.Max(worstPosF, Vec3.MaxAbsDiff(Fv(f.charO), Vx(o, "charO")));
                    worstRotF = Math.Max(worstRotF, Quat.RotationDistance(Fq(f.charQ), Qx(o, "charQ")));
                    worstS = Math.Max(worstS, Math.Abs(f.charS - (double)o["charS"]));
                    worstS = Math.Max(worstS, Math.Abs(d.CharS - (double)o["charS"]));
                    worstPosD = Math.Max(worstPosD, Vec3.MaxAbsDiff(d.PelvisSn, Vx(o, "pelvisSn")));
                    worstPosF = Math.Max(worstPosF, Vec3.MaxAbsDiff(Fv(f.pelvisSn), Vx(o, "pelvisSn")));
                    Assert.AreEqual(0x1234567890UL, f.characterId, c.Id);
                    Assert.IsTrue((f.flags & SeFrameFlags.CharacterPresent) != 0, c.Id);
                    Assert.AreEqual(Vx(o, "pelvisSn").Y < 0, (f.flags & SeFrameFlags.CharacterSwimming) != 0, c.Id + ": swimming = pelvis below the sea");
                }
                else
                {
                    Assert.IsTrue(f.characterId == 0 && (f.flags & SeFrameFlags.CharacterPresent) == 0, c.Id);
                    Assert.IsTrue(f.charS == 1f && f.charQ.w == 1f && f.charQ.x == 0f, c.Id + ": no character: identity, s 1");
                    Assert.IsTrue(f.charO.x == f.camPos.x && f.pelvisSn.z == f.camPos.z, c.Id + ": no character: charO = pelvisSn = camPos");
                }
            }
            string summary = tag + ": " + cases.Count + " cases; worst camPos/pelvis doubles " + worstPosD.ToString("e2") + " m, float32 " +
                             worstPosF.ToString("e2") + " m; rotation doubles " + worstRotD.ToString("e2") + ", float32 " + worstRotF.ToString("e2") +
                             "; vFovDeg " + worstFov.ToString("e2") + "; s " + worstS.ToString("e2") + "; pivot " + worstPivot.ToString("e2") + " m";
            TestContext.WriteLine("    " + summary);
            Assert.IsTrue(worstPosD <= PosTolD, "doubles position " + summary);
            Assert.IsTrue(worstPosF <= PosTolF, "float32 position " + summary);
            Assert.IsTrue(worstRotD <= RotTolD, "doubles rotation " + summary);
            Assert.IsTrue(worstRotF <= RotTolF, "float32 rotation " + summary);
            Assert.IsTrue(worstFov <= 1e-4, "vFovDeg " + summary);
            Assert.IsTrue(worstS <= 1e-6, "s " + summary);
            Assert.IsTrue(worstPivot <= PosTolD, "pivot " + summary);
        }

        [TestMethod]
        public void S1C1_FieldsMatchWireRef_SiteV1()
        {
            ComposeAndCompare(SiteMap.SiteV1Test, "\"v1\"", "v1");
        }

        [TestMethod]
        public void S1C1_FieldsMatchWireRef_RuntimeCentre()
        {
            // a planet spawned elsewhere (the lab world's integer position): the map must use the runtime C
            var c = new Vec3(123456.0, -654321.0, 250000.0);
            ComposeAndCompare(SiteMap.ForSiteV1(c, SiteV1.RsExpected), "{\"C\":" + V(c) + ",\"Rs\":" + G(SiteV1.RsExpected) + "}", "runtimeC");
        }

        // ============================================================================================ test commands

        [TestMethod]
        public void S1Commands_TeleportLookPathGeometry()
        {
            SiteMap site = SiteMap.SiteV1Test;
            // de.teleport WM-2: F(P) = the SN point, facing north (camRot identity), against CONTRACT §3's vector too
            SeMatrix m = S1Math.SpectatorAt(site, new Vec3(-1029, -150, -1116), S1Math.HeadingSn(0), Vec3.UnitY);
            Vec3 contract = new Vec3(-998973.681996, 251113.091246, 1559830.850460);
            Assert.IsTrue(Vec3.MaxAbsDiff(m.Translation, contract) < 1e-6, "F^-1(WM-2 point) = CONTRACT §3 vector: " + m.Translation);
            CameraPose cp = WireMath.Camera(site, m);
            Assert.IsTrue(Vec3.MaxAbsDiff(cp.Position, new Vec3(-1029, -150, -1116)) < 1e-9, "F(P) = commanded point");
            Assert.IsTrue(Quat.RotationDistance(cp.Rotation, Quat.Identity) < 1e-12, "facing SN north: identity " + cp.Rotation);
            Assert.IsTrue(Math.Abs(m.Determinant - 1.0) < 1e-12, "rigid, right-handed SE matrix");

            // de.look at the deepest floor: E, N, down, yaw+30
            Vec3 floor = new Vec3(198, -1657, -518);
            var expect = new Dictionary<string, Vec3>
            {
                { "E", Vec3.UnitX }, { "N", Vec3.UnitZ }, { "down", new Vec3(0, -1, 0) },
                { "yaw+30", new Vec3(Math.Sin(Math.PI / 6), 0, Math.Cos(Math.PI / 6)) },
                { "yaw-90", new Vec3(-1, 0, 0) },
                { "pitch+45", new Vec3(0, Math.Sqrt(0.5), Math.Sqrt(0.5)) },
                { "pitch-90", new Vec3(0, -1, 0) },
            };
            foreach (var kv in expect)
            {
                Vec3 f, u; string label;
                Assert.IsTrue(S1Math.LookDirections(kv.Key, Vec3.UnitZ, Vec3.UnitY, out f, out u, out label), kv.Key);
                CameraPose p = WireMath.Camera(site, S1Math.SpectatorAt(site, floor, f, u));
                Vec3 fwd = p.Rotation.Rotate(Vec3.UnitZ);
                double angleDeg = Math.Acos(Math.Min(1.0, Vec3.Dot(fwd, kv.Value))) * 180 / Math.PI;
                TestContext.WriteLine("    look " + label + ": forward " + fwd + " (" + angleDeg.ToString("e2") + " deg)");
                Assert.IsTrue(angleDeg < 1e-6, "S1.WM-4 look " + kv.Key + " forward within 0.05 deg (here " + angleDeg + ")");
                Assert.IsTrue(Vec3.MaxAbsDiff(p.Position, floor) < 1e-6, "position kept");
            }
            Vec3 ff, uu; string lb;
            Assert.IsFalse(S1Math.LookDirections("sideways", Vec3.UnitZ, Vec3.UnitY, out ff, out uu, out lb));
            // mapped SE gravity direction at the floor = (0, -1, 0) in SN (WM-4)
            Vec3 pFloor = site.SnToSe(floor);
            Vec3 g = (site.C - pFloor).Normalized();
            Assert.IsTrue(Vec3.MaxAbsDiff(site.DirSeToSn(pFloor, g), new Vec3(0, -1, 0)) < 1e-12, "gravity maps to SN down");

            // de.path (S1.WM-3): 100 samples, 2 km east, altitude -100; every sample's F(P).y = -100
            Assert.IsTrue(Vec3.MaxAbsDiff(S1Math.PathSampleSn(0, 100, 2000, -100, 90), new Vec3(0, -100, 0)) < 1e-12);
            Assert.IsTrue(Vec3.MaxAbsDiff(S1Math.PathSampleSn(99, 100, 2000, -100, 90), new Vec3(2000, -100, 0)) < 1e-9);
            double worst = 0;
            for (int k = 0; k < 100; k++)
            {
                Vec3 q = S1Math.PathSampleSn(k, 100, 2000, -100, 90);
                SeMatrix pm = S1Math.SpectatorAt(site, q, S1Math.HeadingSn(90), Vec3.UnitY);
                worst = Math.Max(worst, Vec3.MaxAbsDiff(site.SeToSn(pm.Translation), q));
            }
            Assert.IsTrue(worst < 1e-8, "path samples round-trip " + worst);
        }

        [TestMethod]
        public void S1Pixel_ProjectionThroughTheViewProjectionMatrix()
        {
            SiteMap site = SiteMap.SiteV1Test;
            SeMatrix cam = S1Math.SpectatorAt(site, new Vec3(-1029, -150, -1116), S1Math.HeadingSn(0), Vec3.UnitY);
            double fov = 70 * Math.PI / 180, aspect = 2560.0 / 1440.0;
            double[] vp = S1Math.Multiply(S1Math.ViewOf(cam), S1Math.PerspectiveFieldOfView(fov, aspect, 0.05, 30000));
            double px, py, z;
            Assert.IsTrue(S1Math.Project(vp, 0, 0, 2560, 1440, cam.Translation + cam.Forward * 10, out px, out py, out z), "in front");
            Assert.AreEqual(1280.0, px, 1e-6); Assert.AreEqual(720.0, py, 1e-6);
            double a = 10 * Math.PI / 180;
            Assert.IsTrue(S1Math.Project(vp, 0, 0, 2560, 1440, cam.Translation + cam.Forward * 10 + cam.Up * (10 * Math.Tan(a)), out px, out py, out z));
            Assert.AreEqual(720.0 - Math.Tan(a) / Math.Tan(fov / 2) * 720.0, py, 1e-6, "10 deg above the axis");
            Assert.AreEqual(1280.0, px, 1e-6);
            Assert.IsTrue(S1Math.Project(vp, 0, 0, 2560, 1440, cam.Translation + cam.Forward * 10 + cam.Right * 5, out px, out py, out z));
            Assert.IsTrue(px > 1280.0, "Right is to the right on screen");
            Assert.IsFalse(S1Math.Project(vp, 0, 0, 2560, 1440, cam.Translation - cam.Forward * 10, out px, out py, out z), "behind the camera");
            Assert.IsTrue(S1Math.Project(vp, 100, 50, 2560, 1440, cam.Translation + cam.Forward * 10, out px, out py, out z));
            Assert.AreEqual(1380.0, px, 1e-6, "viewport offset"); Assert.AreEqual(770.0, py, 1e-6);
        }

        // ================================================================================== flags, jumps, velocity

 /// <summary>
 /// Review finding 11: a command's C1 teleported flag is consumed only by frames that were written, and is carried
 /// on <see cref="TeleportLatch.Frames"/> (2) written frames so a Subnautica frame missing one snapshot still snaps.
 /// FrameWriter's protocol: TeleportRequested = Pending; OnWritten after a written frame that carried it.
 /// </summary>
        [TestMethod]
        public void S1C1_TeleportFlag_ConsumedOnlyByWrittenFrames_CarriedOnTwo()
        {
            var t = new TeleportLatch();
            Assert.AreEqual(2, TeleportLatch.Frames);
            Assert.IsFalse(t.Pending);
            t.Arm();
            for (int i = 0; i < 3; i++) Assert.IsTrue(t.Pending, "not consumed by an unwritten frame (no hello, link down)");
            int flagged = 0;
            for (int i = 0; i < 4; i++)
            {
                if (t.Pending) { flagged++; t.OnWritten(); }
            }
            Assert.AreEqual(2, flagged, "carried on two written frames");
            Assert.IsFalse(t.Pending);
            t.OnWritten();
            Assert.IsFalse(t.Pending, "never negative");
            t.Arm();
            t.OnWritten();
            t.Arm();
            Assert.IsTrue(t.Pending, "a new command re-arms both frames");
        }

        [TestMethod]
        public void S1C1_FlagsTeleportJumpVelocity()
        {
            SiteMap site = SiteMap.SiteV1Test;
            var composer = new FrameComposer();
            var group = new PivotGroup(4);
            var ch = new CharacterSample();
            SeFrame f = default(SeFrame);
            FrameDiagnostics d;
            Vec3 q0 = new Vec3(0, -50, 0);
            SeMatrix m = S1Math.SpectatorAt(site, q0, S1Math.HeadingSn(0), Vec3.UnitY);
            var cam = new CameraSample { World = m, FovRad = 1.2, Near = 0.05f, Aspect = 1.5f, Mode = CameraMode.FirstPerson, FirstPerson = true };
            var clock = new FrameClock { Tick = 10, InWorld = true, Paused = true };
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(SeFrameFlags.InWorld | SeFrameFlags.Paused | SeFrameFlags.FirstPerson, f.flags, "first frame: no teleport, flags");
            // integration: the test records (S1Commands.LogFrame fwdSe/upSe) carry the SE camera's own Forward and Up
            Assert.IsTrue(Vec3.MaxAbsDiff(d.CamFwdSe, m.Forward) == 0 && Vec3.MaxAbsDiff(d.CamUpSe, m.Up) == 0, "diagnostics fwdSe/upSe");
            Assert.IsTrue(Vec3.MaxAbsDiff(d.CamSe, m.Translation) == 0, "diagnostics camSe");

            // 1 m east in one tick: 60 m/s, no jump
            clock = new FrameClock { Tick = 11, InWorld = true };
            cam.World = S1Math.SpectatorAt(site, new Vec3(1, -50, 0), S1Math.HeadingSn(0), Vec3.UnitY);
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(0u, f.flags & SeFrameFlags.Teleported);
            Assert.IsTrue(Vec3.MaxAbsDiff(d.CamVelSn, new Vec3(60, 0, 0)) < 1e-6, "camVelSn " + d.CamVelSn);

            // 25 m in one tick: jump -> teleported, velocity 0
            clock = new FrameClock { Tick = 12, InWorld = true };
            cam.World = S1Math.SpectatorAt(site, new Vec3(26, -50, 0), S1Math.HeadingSn(0), Vec3.UnitY);
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.IsTrue((f.flags & SeFrameFlags.Teleported) != 0 && d.Jump, "> 20 m jump");
            Assert.IsTrue(d.CamVelSn.Length == 0, "velocity 0 after a teleport");

            // 19 m: no jump
            clock = new FrameClock { Tick = 13, InWorld = true };
            cam.World = S1Math.SpectatorAt(site, new Vec3(45, -50, 0), S1Math.HeadingSn(0), Vec3.UnitY);
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(0u, f.flags & SeFrameFlags.Teleported, "19 m is not a jump");

            // 50 deg turn: jump; 30 deg: not
            clock = new FrameClock { Tick = 14, InWorld = true };
            cam.World = S1Math.SpectatorAt(site, new Vec3(45, -50, 0), S1Math.HeadingSn(50), Vec3.UnitY);
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.IsTrue((f.flags & SeFrameFlags.Teleported) != 0, "> 45 deg turn");
            clock = new FrameClock { Tick = 15, InWorld = true };
            cam.World = S1Math.SpectatorAt(site, new Vec3(45, -50, 0), S1Math.HeadingSn(80), Vec3.UnitY);
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(0u, f.flags & SeFrameFlags.Teleported, "30 deg (S1.N2 yaw step) is not a jump");

            // a command: teleported even without movement
            clock = new FrameClock { Tick = 16, InWorld = true, TeleportRequested = true };
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.IsTrue((f.flags & SeFrameFlags.Teleported) != 0 && !d.Jump, "commanded teleport");
            Assert.AreEqual(2L, composer.Jumps);
            Assert.AreEqual(3L, composer.Teleports);

            // character: seated (no swimming), dead (flag, no swimming), swimming
            SeMatrix cm = S1Math.SpectatorAt(site, new Vec3(45, -51, 0), S1Math.HeadingSn(0), Vec3.UnitY);
            ch = new CharacterSample { Present = true, EntityId = 77, World = cm, PelvisWorld = cm.Translation, Seated = true };
            clock = new FrameClock { Tick = 17, InWorld = true };
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(SeFrameFlags.InWorld | SeFrameFlags.CharacterPresent | SeFrameFlags.CharacterSeated, f.flags & ~SeFrameFlags.FirstPerson);
            ch.Seated = false; ch.Dead = true;
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(SeFrameFlags.InWorld | SeFrameFlags.CharacterPresent | SeFrameFlags.CharacterDead, f.flags & ~SeFrameFlags.FirstPerson);
            ch.Dead = false;
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(SeFrameFlags.InWorld | SeFrameFlags.CharacterPresent | SeFrameFlags.CharacterSwimming, f.flags & ~SeFrameFlags.FirstPerson);
            Assert.IsTrue(Math.Abs(f.pelvisSn.y - (-51f)) < 1e-3, "pelvisSn = F(pelvis)");
            Assert.IsTrue(Vec3.MaxAbsDiff(d.PivotSe, cm.Translation) < 1e-9, "no group boxes: P* = the character position");
            ch.PelvisWorld = site.SnToSe(new Vec3(45, 2, 0));
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(0u, f.flags & SeFrameFlags.CharacterSwimming, "pelvis above the sea: not swimming");

            composer.Reset();
            clock = new FrameClock { Tick = 18, InWorld = true };
            cam.World = S1Math.SpectatorAt(site, new Vec3(1500, -50, 0), S1Math.HeadingSn(0), Vec3.UnitY);
            composer.Compose(site, ref clock, ref cam, ref ch, group, ref f, out d);
            Assert.AreEqual(0u, f.flags & SeFrameFlags.Teleported, "after Reset the first frame is never a jump");
        }

        // ========================================================================================= S1.M1 mutant red

        [TestMethod]
        public void S1M1_NaiveTangentMap_TurnsWm2AndWm3Red()
        {
            SiteMap site = SiteMap.SiteV1Test;
            var wm2 = new Vec3(-1029, -150, -1116);
            Vec3 wm3 = S1Math.PathSampleSn(99, 100, 2000, -100, 90);
            Func<Vec3, Vec3> camPos = q =>
            {
                var composer = new FrameComposer();
                var cam = new CameraSample { World = S1Math.SpectatorAt(site, q, S1Math.HeadingSn(0), Vec3.UnitY), FovRad = 1.2 };
                var ch = new CharacterSample();
                var clock = new FrameClock { Tick = 1, InWorld = true };
                SeFrame f = default(SeFrame);
                FrameDiagnostics d;
                composer.Compose(site, ref clock, ref cam, ref ch, new PivotGroup(1), ref f, out d);
                return d.CamSn;
            };
            double greenWm2 = (camPos(wm2) - wm2).Length, greenWm3 = Math.Abs(camPos(wm3).Y - (-100));
            Assert.IsTrue(greenWm2 < 1e-6 && greenWm3 < 1e-6, "green: WM-2 " + greenWm2 + " m, WM-3 dy " + greenWm3 + " m");

            Mutants.Set("S1.M1", true);
            Vec3 m2 = camPos(wm2), m3 = camPos(wm3);
            double missWm2 = (m2 - wm2).Length, missWm2Y = Math.Abs(m2.Y - wm2.Y);
            TestContext.WriteLine("    S1.M1: WM-2 misses by " + missWm2.ToString("0.00") + " m (y " + missWm2Y.ToString("0.00") + " m); WM-3 at 2 km y = " + m3.Y.ToString("0.00"));
            Assert.IsTrue(missWm2Y > 15 && missWm2Y < 25, "RED: WM-2 misses by about 19.2 m vertically (ROADMAP S1.M1), here " + missWm2Y);
            Assert.IsTrue(Math.Abs(m3.Y - (-133.3)) < 1.0, "RED: WM-3 drifts to about -133.3 m at 2 km, here " + m3.Y);
            Mutants.ClearAll();
            Assert.IsTrue((camPos(wm2) - wm2).Length < 1e-6, "green again with the mutant off");
        }

        // ======================================================================================= zones, perf mode

        [TestMethod]
        public void S1Zones_HysteresisAndPerfMode()
        {
            var z = new ZoneTracker();
            Assert.AreEqual(Zone.OffSite, z.Update(false, 0, 0));
            Assert.AreEqual(Zone.Core, z.Update(true, 0, 0));
            Assert.AreEqual(Zone.Core, z.Update(true, -1950, 1950));
            Assert.AreEqual(Zone.TileVoid, z.Update(true, 2000, -100));
            Assert.AreEqual(Zone.TileVoid, z.Update(true, 100, 2048));
            Assert.AreEqual(Zone.OuterVoid, z.Update(true, 3000, 0));
            Assert.AreEqual(Zone.OuterVoid, z.Update(true, 7850, 0), "inside: kept to 7,900 m");
            Assert.AreEqual(Zone.Far, z.Update(true, 7950, 0));
            Assert.AreEqual(Zone.Far, z.Update(true, 0, -7850), "outside: re-entered only at 7,800 m");
            Assert.AreEqual(Zone.OuterVoid, z.Update(true, 0, -7790));
            Assert.AreEqual(Zone.OffSite, z.Update(false, 0, 0));
            Assert.AreEqual(Zone.Far, z.Update(true, 7850, 0), "leaving the site resets the hysteresis");

            uint fps;
            Assert.AreEqual(PerfMode.Full, S1Perf.For(Zone.Core, true, out fps)); Assert.AreEqual(0u, fps);
            Assert.AreEqual(PerfMode.Full, S1Perf.For(Zone.OuterVoid, true, out fps));
            Assert.AreEqual(PerfMode.Idle, S1Perf.For(Zone.Core, false, out fps)); Assert.AreEqual(10u, fps);
            Assert.AreEqual(PerfMode.Idle, S1Perf.For(Zone.Far, true, out fps));
            Assert.AreEqual(PerfMode.Idle, S1Perf.For(Zone.OffSite, true, out fps));
        }

        // ======================================================================== C1 and C5 in a real section

        [TestMethod]
        public void S1Link_WritesC1AndTheC5SiteFields()
        {
            using (var rig = new Rig(TestContext))
            {
                string name = Rig.Name();
                var link = new Link(() => true, name, Rig.MutexName());
                var frame = new SeFrame { tick = 5, camPos = new F32x3(1, 2, 3) };
                Assert.IsFalse(link.WriteSeFrame(ref frame), "not linked: nothing written");
                var input = Rig.Input(SePhase.InWorld);
                CreatorResult cr = rig.CreateSn(name, Rig.SelfPid);
                using (SharedSection sn = cr.Section)
                {
                    byte* b = sn.Base;
                    HeaderView hv = sn.Header;
                    hv.SnPhase = SnPhase.ReadyIdle;
                    rig.Run(link, ref input, 1100, hv);
                    Assert.AreEqual(LinkState.Up, link.State);
                    SeState st = Rig.ReadC5(b);
                    Assert.IsTrue(st.zone == Zone.OffSite && st.perfMode == PerfMode.Idle && st.targetFps == 10, "S0 values while not bound");

                    input.SiteActive = true;
                    input.HelloEpoch = 1; input.WorldEpoch = 1; input.Rs = SiteV1.RsExpected;
                    input.Zone = Zone.Core; input.PerfMode = PerfMode.Full; input.TargetFps = 0;
                    input.CamSnY = -150f; input.CamVelSn = new F32x3(1.5f, 0f, -2f);
                    rig.Run(link, ref input, 32, hv);
                    st = Rig.ReadC5(b);
                    Assert.IsTrue(st.zone == Zone.Core && st.perfMode == PerfMode.Full && st.targetFps == 0, "C5 zone/perfMode/targetFps");
                    Assert.IsTrue(st.camSnY == -150f && st.camVelSn.x == 1.5f && st.camVelSn.z == -2f, "C5 camSnY/camVelSn");
                    Assert.IsTrue(st.viewState == ViewState.Space, "view state stays Space in S1");
                    Assert.IsTrue(st.helloEpoch == 1 && st.Rs == SiteV1.RsExpected, "hello with the validated Rs");

                    // C1 composed from a WM-2 camera, written twice: coherent copy, seq advances by 2
                    SiteMap site = SiteMap.SiteV1Test;
                    var composer = new FrameComposer();
                    var cam = new CameraSample { World = S1Math.SpectatorAt(site, new Vec3(-1029, -150, -1116), S1Math.HeadingSn(0), Vec3.UnitY), FovRad = 70 * Math.PI / 180, Near = 0.05f, Aspect = 16f / 9f, Mode = CameraMode.Spectator };
                    var ch = new CharacterSample();
                    var clock = new FrameClock { Tick = 4242, Qpc = 99, WorldEpoch = 1, InWorld = true };
                    FrameDiagnostics d;
                    composer.Compose(site, ref clock, ref cam, ref ch, new PivotGroup(1), ref frame, out d);
                    byte* c1 = Layout.RegionPtr(b, RegionId.C1SeFrame);
                    uint before = Seqlock.Current(c1);
                    Assert.IsTrue(link.WriteSeFrame(ref frame), "written while linked");
                    uint s1 = Seqlock.Current(c1);
                    Assert.IsTrue(link.WriteSeFrame(ref frame));
                    uint s2 = Seqlock.Current(c1);
                    Assert.IsTrue((s1 & 1) == 0 && s1 != 0 && s2 == s1 + 2 && s1 == Seqlock.NextPublished(before), "seq even, +2 per write, continued from " + before);
                    SeFrame read = default(SeFrame);
                    uint seq;
                    Assert.AreEqual(SeqlockReadStatus.Fresh, Seqlock.TryRead(c1, ref read, out seq));
                    Assert.IsTrue(read.tick == 4242 && read.qpcCapture == 99 && read.worldEpoch == 1 && read.boneCount == 0, "C1 header fields");
                    Assert.IsTrue(Math.Abs(read.camPos.x - (-1029f)) < 1e-3 && Math.Abs(read.camPos.y - (-150f)) < 1e-3 && Math.Abs(read.camPos.z - (-1116f)) < 1e-3, "C1 camPos");
                    Assert.IsTrue(Math.Abs(read.camRot.w - 1f) < 1e-6 && Math.Abs(read.vFovDeg - 70f) < 1e-4, "C1 camRot identity (north), vFovDeg 70");
                    Assert.AreEqual(CameraMode.Spectator, read.cameraMode);
                    Assert.AreEqual(2L, link.SeFramesWritten);
                }
            }
        }

        // =========================================================================== /de chat verbs → C11 args

        [TestMethod]
        public void S1Chat_VerbsBuildTheC11Arguments()
        {
            using (var rig = new Rig(TestContext))
            {
                var calls = new List<KeyValuePair<string, IDictionary<string, object>>>();
                var dev = new DevChannel(new Link(() => true, Rig.Name(), Rig.MutexName()))
                {
                    ShowChat = (s, t) => rig.Chat.Add(t),
                    ExtraCommand = (cmd, args, via) =>
                    {
                        calls.Add(new KeyValuePair<string, IDictionary<string, object>>(cmd, args));
                        Assert.AreEqual("chat", via);
                        return new Dictionary<string, object> { { "ok", true }, { "line", "stub " + cmd } };
                    },
                };
                Assert.IsTrue(dev.HandleChat("/de teleport -1029 -150 -1116", 1));
                Assert.IsTrue(dev.HandleChat("/de teleport 1 2 3 45", 2));
                Assert.IsTrue(dev.HandleChat("/de path 1000 50", 3));
                Assert.IsTrue(dev.HandleChat("/de look yaw+30", 4));
                Assert.IsTrue(dev.HandleChat("/de pixel ahead 25", 5));
                Assert.IsTrue(dev.HandleChat("/de pixel 1 2 3", 6));
                Assert.IsTrue(dev.HandleChat("/de site repair", 7));
                Assert.IsTrue(dev.HandleChat("/de site", 8));
                Assert.IsTrue(dev.HandleChat("/de teleport 1 2", 9), "usage, still ours");
                Assert.IsTrue(dev.HandleChat("/de look yaw+30 50 30", 10));
                Assert.AreEqual(9, calls.Count, "the bad teleport is not executed");
                Assert.IsTrue(calls[8].Key == "de.look" && MiniJson.GetNumber(calls[8].Value, "repeat") == 50 && MiniJson.GetNumber(calls[8].Value, "everyTicks") == 30, "S1.N2 scripted steps");
                Assert.AreEqual("de.teleport", calls[0].Key);
                Assert.AreEqual(-1029.0, MiniJson.GetNumber(calls[0].Value, "x"));
                Assert.IsNull(MiniJson.GetNumber(calls[0].Value, "yawDeg"));
                Assert.AreEqual(45.0, MiniJson.GetNumber(calls[1].Value, "yawDeg"));
                Assert.IsTrue(calls[2].Key == "de.path" && MiniJson.GetNumber(calls[2].Value, "lengthM") == 1000 && MiniJson.GetNumber(calls[2].Value, "samples") == 50);
                Assert.IsTrue(calls[3].Key == "de.look" && MiniJson.GetString(calls[3].Value, "dir") == "yaw+30");
                Assert.IsTrue(calls[4].Key == "de.pixel" && MiniJson.GetNumber(calls[4].Value, "ahead") == 25);
                Assert.IsTrue(calls[5].Key == "de.pixel" && MiniJson.GetNumber(calls[5].Value, "z") == 3);
                Assert.IsTrue(calls[6].Key == "de.site" && MiniJson.GetBool(calls[6].Value, "repair") == true);
                Assert.IsTrue(calls[7].Key == "de.site" && MiniJson.GetBool(calls[7].Value, "repair") == null);
                Assert.IsTrue(rig.Chat.Contains("stub de.teleport") && rig.Chat.Exists(t => t.StartsWith("usage: /de teleport", StringComparison.Ordinal)), string.Join(" | ", rig.Chat));
            }
        }
    }
}
