using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S3 poses (CONTRACT §2.3/§2.5, ARCHITECTURE §5.4/§5.8): Apply with the entity scale k, the Frame/Entity split, the
 /// S3.G1-E2 shear case numerically (Frame (1.0284, 1, 1.0284) and (1.0345, 1, 1.0345), Entity rotating 360° in 1°
 /// steps, Unity's float32 matrix arithmetic emulated), the S3.M7 rigid-child mutant, the K-baked fallback, the
 /// affine-vs-F bound, and the C2 float32 round trip.
 /// </summary>
    [TestClass]
    public class S3PoseTests
    {
        public TestContext TestContext { get; set; }

        private static readonly Vec3 LabO = new Vec3(-1029.0, -150.0, -1116.0);

        // ------------------------------------------------------------------ Unity float32 matrix arithmetic (emulated)
 // Matrix4x4.Rotate and MultiplyPoint3x4 follow the managed code of UnityEngine.CoreModule;
 // TRS = T*R*S and the product are the textbook float32 forms (Unity's are native). Row-major m[r, c], 3x4.

        private sealed class F34
        {
            public readonly float[,] M = new float[3, 4];

            public static F34 Trs(Vec3 pos, Quat q, Vec3 scale)
            {
                float x = (float)q.X, y = (float)q.Y, z = (float)q.Z, w = (float)q.W;
                float n1 = x * 2f, n2 = y * 2f, n3 = z * 2f;
                float n4 = x * n1, n5 = y * n2, n6 = z * n3, n7 = x * n2, n8 = x * n3, n9 = y * n3, n10 = w * n1, n11 = w * n2, n12 = w * n3;
                var r = new float[3, 3];
                r[0, 0] = 1f - (n5 + n6); r[1, 0] = n7 + n12; r[2, 0] = n8 - n11;
                r[0, 1] = n7 - n12; r[1, 1] = 1f - (n4 + n6); r[2, 1] = n9 + n10;
                r[0, 2] = n8 + n11; r[1, 2] = n9 - n10; r[2, 2] = 1f - (n4 + n5);
                float[] s = { (float)scale.X, (float)scale.Y, (float)scale.Z };
                var m = new F34();
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 3; j++) m.M[i, j] = r[i, j] * s[j];
                }
                m.M[0, 3] = (float)pos.X; m.M[1, 3] = (float)pos.Y; m.M[2, 3] = (float)pos.Z;
                return m;
            }

            public static F34 operator *(F34 a, F34 b)
            {
                var c = new F34();
                for (int i = 0; i < 3; i++)
                {
                    for (int j = 0; j < 4; j++)
                    {
                        float t = a.M[i, 0] * b.M[0, j] + a.M[i, 1] * b.M[1, j] + a.M[i, 2] * b.M[2, j];
                        if (j == 3) t += a.M[i, 3];
                        c.M[i, j] = t;
                    }
                }
                return c;
            }

            public Vec3 MultiplyPoint3x4(float px, float py, float pz)
            {
                float x = M[0, 0] * px + M[0, 1] * py + M[0, 2] * pz + M[0, 3];
                float y = M[1, 0] * px + M[1, 1] * py + M[1, 2] * pz + M[1, 3];
                float z = M[2, 0] * px + M[2, 1] * py + M[2, 2] * pz + M[2, 3];
                return new Vec3(x, y, z);
            }
        }

        private static Quat AngleAxis(double deg, Vec3 axis)
        {
            Vec3 a = axis.Normalized();
            double h = deg * Math.PI / 360.0;
            return new Quat(a.X * Math.Sin(h), a.Y * Math.Sin(h), a.Z * Math.Sin(h), Math.Cos(h)).WithNonNegativeW();
        }

        /// <summary>S3.G1-E2 numerically: what renderer.localToWorldMatrix gives (float32) vs the emulated Apply (doubles).</summary>
        private void ShearSweep(double s, double k, Vec3 o, Vec3 chunkOriginSe, out double worstRel, out double worstAbs, out double worstRigid, out double worstRigidMismatch)
        {
            worstRel = worstAbs = worstRigid = worstRigidMismatch = 0;
            var axis = new Vec3(0.3, 1.0, 0.45);
            Vec3[] corners = MeshConventions.AabbCorners(new Vec3(-1.25, -1.25, -1.25), new Vec3(1.25, 1.25, 1.25));
            for (int deg = 0; deg < 360; deg++)
            {
                Quat q = AngleAxis(deg, axis).RoundToFloat32();
                // the float32 values Unity holds, and the same values as doubles for the emulated Apply
                var pose = new WireEntityPose(o.RoundToFloat32(), q, (float)s, (float)k);
                FrameEntitySplit f = PoseMath.Split(pose);
                F34 frame = F34.Trs(f.FramePosition, Quat.Identity, f.FrameScale);
                F34 entity = F34.Trs(Vec3.Zero, f.EntityRotation, f.EntityScale);
                Vec3 cu = WireMath.Mirror(chunkOriginSe);
                F34 chunk = frame * entity * F34.Trs(cu, Quat.Identity, new Vec3(1, 1, 1));
                F34 rigid = F34.Trs(pose.O, q, new Vec3(k, k, k)) * F34.Trs(cu, Quat.Identity, new Vec3(1, 1, 1));   // S3.M7: no scaled Frame
                foreach (Vec3 l in corners)
                {
                    Vec3 v = WireMath.Mirror(l - chunkOriginSe);   // chunk-local Unity vertex
                    Vec3 drawn = chunk.MultiplyPoint3x4((float)v.X, (float)v.Y, (float)v.Z);
                    Vec3 want = PoseMath.Apply(pose, l);
                    worstAbs = Math.Max(worstAbs, (drawn - want).Length);
                    Vec3 lin = new Vec3(chunk.M[0, 0] * (float)v.X + chunk.M[0, 1] * (float)v.Y + chunk.M[0, 2] * (float)v.Z,
                                        chunk.M[1, 0] * (float)v.X + chunk.M[1, 1] * (float)v.Y + chunk.M[1, 2] * (float)v.Z,
                                        chunk.M[2, 0] * (float)v.X + chunk.M[2, 1] * (float)v.Y + chunk.M[2, 2] * (float)v.Z);
                    Vec3 wantChunkOrigin = PoseMath.Apply(pose, chunkOriginSe);
                    worstRel = Math.Max(worstRel, (lin - (want - wantChunkOrigin)).Length);   // the matrix's linear part (shear) alone
                    Vec3 rd = rigid.MultiplyPoint3x4((float)v.X, (float)v.Y, (float)v.Z);
                    double dev = (rd - want).Length;
                    worstRigid = Math.Max(worstRigid, dev);
                    double formula = PoseMath.RigidDrawError(q, pose.S, pose.K, l);
                    worstRigidMismatch = Math.Max(worstRigidMismatch, Math.Abs(dev - formula));
                }
            }
        }

        [TestMethod]
        public void G1E2_ShearedFrame_CornersMatchApply_EveryDegree()
        {
            foreach (double s in new[] { 1.0284, 1.0345 })
            {
                foreach (double k in new[] { 1.0, 1.5 })
                {
                    foreach (Vec3 origin in new[] { Vec3.Zero, new Vec3(2.5, -2.5, 5.0) })
                    {
                        double rel, abs, rigid, mism;
                        ShearSweep(s, k, LabO, origin, out rel, out abs, out rigid, out mism);
                        double relO, absO, rigidO, mismO;
                        ShearSweep(s, k, Vec3.Zero, origin, out relO, out absO, out rigidO, out mismO);
                        TestContext.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "Frame ({0}, 1, {0}) k={1} chunk {2}: linear part {3:E2} m; absolute at the lab o {4:E2} m, at o=0 {5:E2} m; rigid child (S3.M7) {6:F2} mm",
                            s, k, origin, rel, abs, absO, rigid * 1000));
                        Assert.IsTrue(rel < 1e-5, "S3.G1-E2: the sheared Frame/Entity draws Apply (linear part) " + rel);
                        Assert.IsTrue(absO < 1e-4, "S3.G1-E2 threshold 1e-4 m with the Frame at the origin " + absO);
                        Assert.IsTrue(abs < 2.5e-4, "absolute at |o| ~ 1.5 km: float32 spacing 1.2e-4 m " + abs);
                        Assert.IsTrue(rigid > 1e-2, "S3.M7: the rigid child is off by centimetres " + rigid);
                        Assert.IsTrue(mism < 2e-4, "S3.M7: and the deviation is the rigid-draw error " + mism);
                    }
                }
            }
        }

        [TestMethod]
        public void FrameEntity_DoubleComposition_EqualsApplyWithK()
        {
            var rnd = new Random(30030020);
            for (int i = 0; i < 2000; i++)
            {
                Quat q = new Quat(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5).Normalized().WithNonNegativeW();
                double s = new[] { 1.0284, 1.0345, 0.9942, 1.0 }[i % 4];
                double k = new[] { 1.0, 0.5, 2.0 }[i % 3];
                var p = new WireEntityPose(new Vec3(rnd.Next(-2000, 2000), rnd.Next(-2000, 0), rnd.Next(-2000, 2000)), q, s, k);
                var l = new Vec3(rnd.NextDouble() * 10 - 5, rnd.NextDouble() * 10 - 5, rnd.NextDouble() * 10 - 5);
                Vec3 a = PoseMath.Apply(p, l);
                Assert.IsTrue((PoseMath.EntityMatrix(p).TransformPoint(WireMath.Mirror(l)) - a).Length < 1e-9);
                Vec3 c = new Vec3(1, 2, 3);
                Assert.IsTrue((PoseMath.ChildMatrix(p, WireMath.Mirror(c), Quat.Identity).TransformPoint(WireMath.Mirror(l - c)) - a).Length < 1e-9);
                Assert.IsTrue((PoseMath.ToLocal(p, a) - l).Length < 1e-9, "ToLocal inverts Apply with k");
                Assert.IsTrue((a - WireMath.Apply(p.O, p.Q, p.S, l * k)).Length < 1e-12);
                // K-baked fallback: a unit-scale chunk root rotated by q holds the exact positions for this (q, s)
                Vec3 root = PoseMath.ChunkRoot(p, c);
                Assert.IsTrue((root + q.Rotate(PoseMath.KBakedVertex(p, c, l)) - a).Length < 1e-9);
                // the rigid draw differs by exactly the rigid-draw error
                Assert.IsTrue(Math.Abs((PoseMath.RigidMatrix(p).TransformPoint(WireMath.Mirror(l)) - a).Length - PoseMath.RigidDrawError(q, s, k, l)) < 1e-9);
            }
            FrameEntitySplit sp = PoseMath.Split(new WireEntityPose(LabO, Quat.Identity, 1.0284, 1.5));
            Assert.AreEqual(LabO, sp.FramePosition);
            Assert.AreEqual(new Vec3(1.0284, 1.0, 1.0284), sp.FrameScale);
            Assert.AreEqual(Quat.Identity, sp.FrameRotation);
            Assert.AreEqual(Vec3.Zero, sp.EntityPosition);
            Assert.AreEqual(new Vec3(1.5, 1.5, 1.5), sp.EntityScale);
        }

        [TestMethod]
        public void Shear_ZeroForYawOnly_PositiveWhenTilted()
        {
            Assert.IsTrue(PoseMath.Shear(AngleAxis(37, Vec3.UnitY), 1.0284) < 1e-15, "S commutes with yaw");
            Assert.IsTrue(PoseMath.Shear(AngleAxis(90, Vec3.UnitX), 1.0284) < 1e-15, "a quarter turn keeps the axes level/vertical");
            double sh = PoseMath.Shear(AngleAxis(45, Vec3.UnitX), 1.0284);
            Assert.IsTrue(Math.Abs(sh - (1.0284 * 1.0284 - 1) / (1.0284 * 1.0284 + 1)) < 1e-12, "45 deg tilt: cos = (s^2 - 1)/(s^2 + 1) " + sh);
            Assert.AreEqual(0.0, PoseMath.Shear(AngleAxis(45, Vec3.UnitX), 1.0), 1e-15);
        }

        [TestMethod]
        public void AffineVsF_BoundHolds_PivotAtTheCameraNearestPoint()
        {
            SiteMap site = SiteMap.SiteV1Test;
            var rnd = new Random(30030021);
            double worst = 0;
            for (int i = 0; i < 1500; i++)
            {
                Vec3 sn = new Vec3(rnd.NextDouble() * 4096 - 2048, -rnd.NextDouble() * 2000, rnd.NextDouble() * 4096 - 2048);
                Vec3 t = site.SnToSe(sn);
                Vec3 e, u, n;
                site.FrameAt(t, out e, out u, out n);
                SeMatrix m = S3TestModels.Rigid(u + e * (rnd.NextDouble() - 0.5), rnd.NextDouble() * 6, t);
                double L = new[] { 2.5, 20.0, 100.0 }[i % 3];
                var bmin = new Vec3(-L / 2, -L / 2, -L / 2);
                var bmax = new Vec3(L / 2, L / 2, L / 2);
                Vec3 cam = m.Transform(new Vec3((rnd.NextDouble() - 0.5) * 4 * L, (rnd.NextDouble() - 0.5) * 2 * L, (rnd.NextDouble() - 0.5) * 4 * L));
                Vec3 pivot = WireMath.PivotNearest(m, bmin, bmax, cam);
                WireEntityPose p = PoseMath.FromSe(site, m, pivot);
                foreach (Vec3 l in MeshConventions.AabbCorners(bmin, bmax))
                {
                    Vec3 w = m.Transform(l);
                    double err = (PoseMath.Apply(p, l) - site.SeToSn(w)).Length;
                    double bound = PoseMath.AffineVsFBound(site, pivot, w);
                    worst = Math.Max(worst, err / (bound + 1e-9));
                }
            }
            TestContext.WriteLine("affine vs F: worst |Apply - F| / bound = " + worst.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsTrue(worst <= 1.0, "ratio " + worst);
            Assert.IsTrue(PoseMath.AffineVsFBound(0, 60000, 1, 0.01) == 0);
        }

        [TestMethod]
        public void PoseWithK_FromScaledRows_AndTheFloat32Wire()
        {
            SiteMap site = SiteMap.SiteV1Test;
            Vec3 t = site.SnToSe(LabO);
            Vec3 e, u, n;
            site.FrameAt(t, out e, out u, out n);
            SeMatrix m = new SeMatrix(e, u, -n, t);   // level, facing north
            Vec3 pivot = m.Transform(new Vec3(0, 0, -1.25));
            WireEntityPose p1 = PoseMath.FromSe(site, m, pivot, 2.0);
            var scaled = new SeMatrix(m.Right * 2.0, m.Up * 2.0, m.Backward * 2.0, m.Translation);
            WireEntityPose p2 = PoseMath.FromSeScaled(site, scaled, pivot);
            Assert.AreEqual(2.0, p2.K, 1e-12);
            Assert.IsTrue((p1.O - p2.O).Length < 1e-9 && Quat.RotationDistance(p1.Q, p2.Q) < 1e-12 && Math.Abs(p1.S - p2.S) < 1e-15);
            Assert.IsTrue((PoseMath.Apply(p1, new Vec3(1, 1, 1)) - PoseMath.Apply(p2, new Vec3(1, 1, 1))).Length < 1e-9);
            try
            {
                PoseMath.FromSeScaled(site, new SeMatrix(m.Right * 2.0, m.Up, m.Backward, m.Translation), pivot);
                Assert.Fail("non-uniform scale has no wire field");
            }
            catch (ArgumentException) { }
            // the pose of a level block facing north at the lab spot: identity-like rotation, o at the lab point's offset
            Assert.IsTrue(Math.Abs(p1.S - site.Rs / (pivot - site.C).Length) < 1e-15);
            Assert.IsTrue((PoseMath.Apply(p1, Vec3.Zero) - LabO).Length < 1e-3, "the entity origin lands at the lab point");
            var rec = new SeEntity();
            PoseMath.WriteWire(ref rec, p1);
            WireEntityPose back = PoseMath.ReadWire(rec);
            Assert.AreEqual((float)p1.S, (float)back.S);
            Assert.AreEqual(2.0, back.K);
            Assert.IsTrue((back.O - p1.O).Length < 1e-3);
            Assert.IsTrue(Quat.RotationDistance(back.Q, p1.Q) < 1e-6);
            Assert.IsTrue(rec.q.w >= 0, "w >= 0 on the wire");
        }
    }
}
