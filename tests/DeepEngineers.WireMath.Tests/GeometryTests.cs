using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>Unit checks of the conventions behind WireMath (independent of the vector files).</summary>
    [TestClass]
    public class GeometryTests
    {
        private static Vec3 RandomUnit(Random rnd)
        {
            while (true)
            {
                var v = new Vec3(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1);
                double n = v.Length;
                if (n > 0.1 && n <= 1) return v / n;
            }
        }

        /// <summary>A random proper SE rotation as rows (Right = Forward x Up, Backward = -Forward) at T.</summary>
        private static SeMatrix RandomSeMatrix(Random rnd, Vec3 t)
        {
            Vec3 up = RandomUnit(rnd);
            Vec3 f = RandomUnit(rnd);
            f = (f - up * Vec3.Dot(f, up)).Normalized();
            return new SeMatrix(Vec3.Cross(f, up), up, -f, t);
        }

        [TestMethod]
        public void LookRotation_IdentityAndAxes()
        {
            Quat q = Quat.LookRotation(Vec3.UnitZ, Vec3.UnitY);
            Assert.AreEqual(0.0, Quat.RotationDistance(q, Quat.Identity), 1e-15);
            var rnd = new Random(1);
            for (int i = 0; i < 1000; i++)
            {
                Vec3 f = RandomUnit(rnd), u0 = RandomUnit(rnd);
                Vec3 u = (u0 - f * Vec3.Dot(u0, f)).Normalized();
                Quat r = Quat.LookRotation(f, u);
                Assert.IsTrue(r.W >= 0);
                Assert.AreEqual(1.0, r.Length, 1e-14);
                Assert.IsTrue(Vec3.Distance(r.Rotate(Vec3.UnitZ), f) < 1e-13);
                Assert.IsTrue(Vec3.Distance(r.Rotate(Vec3.UnitY), u) < 1e-13);
                Assert.IsTrue(Vec3.Distance(r.Rotate(Vec3.UnitX), Vec3.Cross(u, f)) < 1e-13, "Unity: local +X = Cross(up, forward)");
            }
        }

        [TestMethod]
        public void Quat_ProductAndConjugate()
        {
            var rnd = new Random(2);
            for (int i = 0; i < 1000; i++)
            {
                Quat a = Quat.LookRotation(RandomUnit(rnd), RandomUnit(rnd));
                Quat b = Quat.LookRotation(RandomUnit(rnd), RandomUnit(rnd));
                Vec3 v = RandomUnit(rnd) * 7.0;
                Assert.IsTrue(Vec3.Distance((a * b).Rotate(v), a.Rotate(b.Rotate(v))) < 1e-12, "a*b applies b first");
                Assert.IsTrue(Vec3.Distance(a.Conjugate().Rotate(a.Rotate(v)), v) < 1e-12);
            }
        }

        [TestMethod]
        public void PolarRotation_OfKTimesRigidFrameIsTheRigidFrame()
        {
            var rnd = new Random(3);
            for (int i = 0; i < 500; i++)
            {
                Quat q = Quat.LookRotation(RandomUnit(rnd), RandomUnit(rnd));
                double s = 0.99 + rnd.NextDouble() * 0.07;
                SeMatrix e = RandomSeMatrix(rnd, Vec3.Zero);
                Vec3 a0 = Wire.WireMath.Mirror(e.Right), a1 = Wire.WireMath.Mirror(e.Up), a2 = Wire.WireMath.Mirror(e.Backward);
                var m = new Mat3(Wire.WireMath.K(q, s, a0), Wire.WireMath.K(q, s, a1), -Wire.WireMath.K(q, s, a2));
                Mat3 r = m.PolarRotation();
                Assert.AreEqual(1.0, r.Determinant, 1e-12);
                Assert.IsTrue(Vec3.Distance(r.C0, a0) < 1e-12 && Vec3.Distance(r.C1, a1) < 1e-12 && Vec3.Distance(r.C2, -a2) < 1e-12);
                // a generic near-orthogonal matrix gives an orthonormal factor
                var g = new Mat3(r.C0 + RandomUnit(rnd) * 0.05, r.C1 + RandomUnit(rnd) * 0.05, r.C2 + RandomUnit(rnd) * 0.05);
                Mat3 rg = g.PolarRotation();
                Assert.AreEqual(1.0, rg.Determinant, 1e-12);
                Assert.AreEqual(0.0, Vec3.Dot(rg.C0, rg.C1), 1e-12);
                Assert.AreEqual(0.0, Vec3.Dot(rg.C1, rg.C2), 1e-12);
                Assert.AreEqual(1.0, rg.C2.Length, 1e-12);
            }
        }

        /// <summary>K = q^-1 S q equals s*v - (s - 1)*u*(u.v) with u = q^-1*y (CONTRACT 2.3).</summary>
        [TestMethod]
        public void K_ClosedForm()
        {
            var rnd = new Random(4);
            for (int i = 0; i < 1000; i++)
            {
                Quat q = Quat.LookRotation(RandomUnit(rnd), RandomUnit(rnd));
                double s = 0.99 + rnd.NextDouble() * 0.07;
                Vec3 v = RandomUnit(rnd) * 3.0;
                Vec3 u = q.Conjugate().Rotate(Vec3.UnitY);
                Assert.IsTrue(Vec3.Distance(Wire.WireMath.K(q, s, v), v * s - u * ((s - 1) * Vec3.Dot(u, v))) < 1e-13);
            }
        }

        /// <summary>A part matrix as a Unity child (CONTRACT 2.5) maps Mirror(l) to Mirror(M l).</summary>
        [TestMethod]
        public void PartLocalTransform_IsTheConjugateByMirror()
        {
            var rnd = new Random(5);
            for (int i = 0; i < 1000; i++)
            {
                SeMatrix part = RandomSeMatrix(rnd, RandomUnit(rnd) * 20.0);
                Vec3 l = RandomUnit(rnd) * 5.0;
                Vec3 unity = Wire.WireMath.PartLocalPosition(part) + Wire.WireMath.PartLocalRotation(part).Rotate(Wire.WireMath.Mirror(l));
                Assert.IsTrue(Vec3.Distance(unity, Wire.WireMath.Mirror(part.Transform(l))) < 1e-12);
            }
        }

        /// <summary>Group pivot = the nearest of the members' single-box pivots; the camera itself when inside a box.</summary>
        [TestMethod]
        public void PivotNearest_GroupPicksTheNearestMemberBox()
        {
            var rnd = new Random(8);
            for (int i = 0; i < 500; i++)
            {
                int n = 1 + rnd.Next(4);
                var ms = new SeMatrix[n];
                var lo = new Vec3[n];
                var hi = new Vec3[n];
                for (int k = 0; k < n; k++)
                {
                    ms[k] = RandomSeMatrix(rnd, RandomUnit(rnd) * 100.0);
                    Vec3 h = new Vec3(1 + rnd.NextDouble() * 20, 1 + rnd.NextDouble() * 20, 1 + rnd.NextDouble() * 20);
                    lo[k] = -h;
                    hi[k] = h;
                }
                Vec3 cam = RandomUnit(rnd) * 150.0;
                Vec3 g = Wire.WireMath.PivotNearest(ms, lo, hi, n, cam);
                double best = double.MaxValue;
                for (int k = 0; k < n; k++) best = Math.Min(best, Vec3.Distance(Wire.WireMath.PivotNearest(ms[k], lo[k], hi[k], cam), cam));
                Assert.AreEqual(best, Vec3.Distance(g, cam), 1e-12);
            }
            var m = new SeMatrix(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, new Vec3(10, 20, 30));
            Vec3 inside = new Vec3(11, 19, 31.5);
            Assert.IsTrue(Vec3.Distance(Wire.WireMath.PivotNearest(m, new Vec3(-2, -2, -2), new Vec3(2, 2, 2), inside), inside) < 1e-12);
            Assert.AreEqual(70.0, Wire.WireMath.VerticalFovDegrees(70.0 * Math.PI / 180.0), 1e-12);
        }

        [TestMethod]
        public void MirrorTris_SwapsTheLastTwo()
        {
            int[] t = { 0, 1, 2, 5, 6, 7 };
            Wire.WireMath.MirrorTris(t, t);
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 5, 7, 6 }, t);
            int a, b, c;
            Wire.WireMath.MirrorTri(3, 4, 5, out a, out b, out c);
            Assert.AreEqual(3, a); Assert.AreEqual(5, b); Assert.AreEqual(4, c);
            Assert.AreEqual(new Vec3(1, 2, -3), Wire.WireMath.Mirror(new Vec3(1, 2, 3)));
        }

        [TestMethod]
        public void SeMatrix_WireRowsRoundTrip()
        {
            var rows = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 10.5f, -3, 2 };
            SeMatrix m = SeMatrix.FromRows(rows);
            Assert.AreEqual(new Vec3(0, 0, -1), m.Forward);
            Assert.AreEqual(new Vec3(11.5, -1, 5), m.Transform(new Vec3(1, 2, 3)));
            Assert.AreEqual(new Vec3(1, 2, 3), m.InverseTransformPoint(new Vec3(11.5, -1, 5)));
            var back = new float[12];
            m.CopyRowsTo(back);
            CollectionAssert.AreEqual(rows, back);
            Assert.AreEqual(1.0, m.Determinant);
        }

        /// <summary>staticAxes inverts the pose rotation (SN -> SE -> SN) and has determinant +1; the camera pose is unscaled F.</summary>
        [TestMethod]
        public void StaticAxes_InvertThePoseRotation()
        {
            SiteMap site = SiteMap.SiteV1Test;
            var rnd = new Random(6);
            for (int i = 0; i < 1000; i++)
            {
                var c = new Vec3(rnd.NextDouble() * 15600 - 7800, rnd.NextDouble() * 3390 - 3040, rnd.NextDouble() * 15600 - 7800);
                Quat q = Quat.LookRotation(RandomUnit(rnd), RandomUnit(rnd));
                StaticBoxSe b = Wire.WireMath.StaticToSe(site, c, q, new Vec3(1, 2, 3));
                Assert.AreEqual(1.0, b.Determinant, 1e-12);
                Assert.IsTrue(Vec3.Distance(site.SeToSn(b.Centre), c) < 1e-6);
                WirePose p = Wire.WireMath.Pose(site, b.Matrix, b.Centre);
                Assert.IsTrue(Quat.RotationDistance(p.Q, q) < 1e-12);
                CameraPose cam = Wire.WireMath.Camera(site, b.Matrix);
                Assert.IsTrue(Vec3.Distance(cam.Position, site.SeToSn(b.Centre)) < 1e-9);
                Assert.IsTrue(Quat.RotationDistance(cam.Rotation, q) < 1e-12);
            }
        }

        /// <summary>The map's direction transform is improper (determinant -1) and orthonormal; the frame is right-handed (E x N = U).</summary>
        [TestMethod]
        public void SiteMap_FrameAndMapdir()
        {
            SiteMap site = SiteMap.SiteV1Test;
            Assert.AreEqual(1.0, Vec3.Dot(Vec3.Cross(site.E0, site.N0), site.U0), 1e-15);
            Assert.AreEqual(SiteV1.AnchorE0, site.E0);
            var rnd = new Random(7);
            for (int i = 0; i < 1000; i++)
            {
                var q = new Vec3(rnd.NextDouble() * 16384 - 8192, rnd.NextDouble() * 3340 - 3040, rnd.NextDouble() * 16384 - 8192);
                Vec3 p = site.SnToSe(q);
                Assert.IsTrue(Vec3.Distance(site.SeToSn(p), q) < 1e-6);
                Vec3 e, u, n;
                site.FrameAt(p, out e, out u, out n);
                Assert.AreEqual(1.0, Vec3.Dot(Vec3.Cross(e, n), u), 1e-12);
                Vec3 dx = site.DirSeToSn(p, Vec3.UnitX), dy = site.DirSeToSn(p, Vec3.UnitY), dz = site.DirSeToSn(p, Vec3.UnitZ);
                Assert.AreEqual(-1.0, Vec3.Dot(dx, Vec3.Cross(dy, dz)), 1e-12);
                Vec3 w = RandomUnit(rnd);
                Assert.IsTrue(Vec3.Distance(site.DirSnToSe(p, site.DirSeToSn(p, w)), w) < 1e-12);
            }
        }
    }
}
