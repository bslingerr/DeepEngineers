using System;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S5 instance and pose math (CONTRACT §2.5, §2.8, §5.4): SE row algebra, the subgrid relative row at planet-scale
 /// translations, the Unity mirror conjugation (point rule, homomorphism, TRS decomposition, the child-under-Entity
 /// identity behind S5.N3), interpolation endpoints and the Dynamic threshold.
 /// </summary>
    [TestClass]
    public class S5InstanceMathTests
    {
        internal static Vec3 RandomUnit(Random rnd)
        {
            while (true)
            {
                var v = new Vec3(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1);
                double n = v.Length;
                if (n > 0.1 && n <= 1) return v / n;
            }
        }

        /// <summary>A random rigid SE matrix (orthonormal, right-handed rows) at t.</summary>
        internal static SeMatrix RandomRigid(Random rnd, Vec3 t)
        {
            Vec3 up = RandomUnit(rnd);
            Vec3 f = RandomUnit(rnd);
            f = (f - up * Vec3.Dot(f, up)).Normalized();
            Vec3 right = Vec3.Cross(f, up);
            return new SeMatrix(right, up, -f, t);
        }

        internal static SeMatrix RandomRigid(Random rnd, double tscale)
        {
            return RandomRigid(rnd, new Vec3((rnd.NextDouble() * 2 - 1) * tscale, (rnd.NextDouble() * 2 - 1) * tscale, (rnd.NextDouble() * 2 - 1) * tscale));
        }

        internal static SeMatrix Scaled(SeMatrix m, double sx, double sy, double sz)
        {
            return new SeMatrix(m.Right * sx, m.Up * sy, m.Backward * sz, m.Translation);
        }

        internal static Vec3 RandomPoint(Random rnd, double s)
        {
            return new Vec3((rnd.NextDouble() * 2 - 1) * s, (rnd.NextDouble() * 2 - 1) * s, (rnd.NextDouble() * 2 - 1) * s);
        }

        internal static double MaxDiff(SeMatrix a, SeMatrix b)
        {
            return Math.Max(Math.Max(Vec3.MaxAbsDiff(a.Right, b.Right), Vec3.MaxAbsDiff(a.Up, b.Up)),
                            Math.Max(Vec3.MaxAbsDiff(a.Backward, b.Backward), Vec3.MaxAbsDiff(a.Translation, b.Translation)));
        }

        private static bool BitEqual(SeMatrix a, SeMatrix b)
        {
            return a.Right.Equals(b.Right) && a.Up.Equals(b.Up) && a.Backward.Equals(b.Backward) && a.Translation.Equals(b.Translation);
        }

        [TestMethod]
        public void Multiply_ComposesTransforms()
        {
            var rnd = new Random(1);
            for (int i = 0; i < 200; i++)
            {
                SeMatrix a = Scaled(RandomRigid(rnd, 20), 0.5 + rnd.NextDouble(), 1, 2), b = RandomRigid(rnd, 100);
                Vec3 l = RandomPoint(rnd, 3);
                Assert.IsTrue((InstanceMath.Multiply(a, b).Transform(l) - b.Transform(a.Transform(l))).Length < 1e-12);
                Assert.IsTrue(MaxDiff(InstanceMath.Multiply(InstanceMath.Identity, a), a) == 0);
            }
        }

        [TestMethod]
        public void Invert_GeneralAndRigid()
        {
            var rnd = new Random(2);
            for (int i = 0; i < 200; i++)
            {
                SeMatrix r = RandomRigid(rnd, 1000);
                SeMatrix s = Scaled(r, 0.3 + rnd.NextDouble(), 0.3 + rnd.NextDouble(), 0.3 + rnd.NextDouble());
                Assert.IsTrue(MaxDiff(InstanceMath.Multiply(InstanceMath.Invert(s), s), InstanceMath.Identity) < 1e-12);
                Assert.IsTrue(MaxDiff(InstanceMath.Multiply(s, InstanceMath.Invert(s)), InstanceMath.Identity) < 1e-9);
                Assert.IsTrue(MaxDiff(InstanceMath.InvertRigid(r), InstanceMath.Invert(r)) < 1e-9);
            }
            MirrorConventionsTests.AssertThrows<ArgumentException>(() => InstanceMath.Invert(new SeMatrix(Vec3.UnitX, Vec3.UnitX, Vec3.UnitZ, Vec3.Zero)));
        }

        [TestMethod]
        public void Relative_SubgridRowAtPlanetScale()
        {
            var rnd = new Random(3);
            double worst = 0;
            for (int i = 0; i < 500; i++)
            {
                SeMatrix root = RandomRigid(rnd, 2e6);
                SeMatrix local = RandomRigid(rnd, 40);
                SeMatrix sub = InstanceMath.Multiply(local, root);
                SeMatrix rel = InstanceMath.Relative(sub, root);
                worst = Math.Max(worst, MaxDiff(rel, local));
                Vec3 l = RandomPoint(rnd, 10);
                Assert.IsTrue((InstanceMath.Multiply(rel, root).Transform(l) - sub.Transform(l)).Length < 1e-6);
                SeSubpart row = InstanceMath.SubgridRow(11, 22, sub, root);
                Assert.AreEqual(11UL, row.ownerId);
                Assert.AreEqual(22UL, row.partKey);
                Assert.IsTrue(MaxDiff(InstanceMath.RowLocal(row), rel) < 1e-5, "float32 row");
            }
            Assert.IsTrue(worst < 1e-8, "relative pose error " + worst + " m at 2,000 km");
            Assert.IsTrue(MaxDiff(InstanceMath.RelativeGeneral(InstanceMath.Multiply(InstanceMath.Identity, InstanceMath.Identity), InstanceMath.Identity), InstanceMath.Identity) == 0);
        }

        [TestMethod]
        public void Unity_PointRuleAndHomomorphism()
        {
            var rnd = new Random(4);
            for (int i = 0; i < 300; i++)
            {
                SeMatrix a = Scaled(RandomRigid(rnd, 30), 0.4 + rnd.NextDouble(), 0.4 + rnd.NextDouble(), 0.4 + rnd.NextDouble());
                SeMatrix b = RandomRigid(rnd, 30);
                Vec3 l = RandomPoint(rnd, 5);
                Affine3 ua = InstanceMath.ToUnityAffine(a);
                Assert.IsTrue((ua.TransformPoint(WireMath.Mirror(l)) - WireMath.Mirror(a.Transform(l))).Length < 1e-12, "point rule");
                Affine3 composed = InstanceMath.ToUnityAffine(b) * ua;
                Affine3 direct = InstanceMath.ToUnityAffine(InstanceMath.Multiply(a, b));
                Assert.IsTrue((composed.TransformPoint(WireMath.Mirror(l)) - direct.TransformPoint(WireMath.Mirror(l))).Length < 1e-11, "homomorphism");
                Assert.IsTrue(Math.Abs(Det(ua.Linear) - a.Determinant) < 1e-9, "det preserved (proper rotation stays proper)");
            }
        }

        private static double Det(Mat3 m) { return Vec3.Dot(m.C0, Vec3.Cross(m.C1, m.C2)); }

        [TestMethod]
        public void Unity_TrsDecompositionAndRoundTrip()
        {
            var rnd = new Random(5);
            for (int i = 0; i < 300; i++)
            {
                double k = i < 150 ? 1.0 : 0.25 + rnd.NextDouble() * 3;
                SeMatrix m = Scaled(RandomRigid(rnd, 50), k, k, k);
                UnityTrs t = InstanceMath.ToUnity(m);
                Assert.IsTrue(t.Rotation.W >= 0, "w >= 0");
                Assert.IsTrue(Math.Abs(t.Rotation.Length - 1) < 1e-12);
                Vec3 l = RandomPoint(rnd, 5);
                Vec3 viaTrs = t.Matrix.TransformPoint(WireMath.Mirror(l));
                Assert.IsTrue((viaTrs - InstanceMath.ToUnityAffine(m).TransformPoint(WireMath.Mirror(l))).Length < 1e-11);
                Assert.IsTrue(MaxDiff(InstanceMath.FromUnity(t), m) < 1e-12);
            }
            UnityTrs id = InstanceMath.ToUnity(InstanceMath.Identity);
            Assert.AreEqual(Quat.Identity, id.Rotation);
            Assert.AreEqual(new Vec3(1, 1, 1), id.Scale);
        }

        [TestMethod]
        public void Unity_ChildUnderEntityEqualsApply_N3AtAlpha1()
        {
            // A Dynamic subpart or subgrid child (unit scale) under the Entity, posed with ToUnity(local): its drawn point is
 // Apply(o, q, s, k) of the composed SE-local point, the identity S5.N3 checks within 1 mm at alpha 1.
            var rnd = new Random(6);
            for (int i = 0; i < 300; i++)
            {
                var pose = new WireEntityPose(RandomPoint(rnd, 3000), RandomQuat(rnd), 0.94 + rnd.NextDouble() * 0.1, 1.0);
                SeMatrix local = RandomRigid(rnd, 25);
                UnityTrs t = InstanceMath.ToUnity(local);
                Vec3 l = RandomPoint(rnd, 4);
                Vec3 drawn = PoseMath.ChildMatrix(pose, t.Position, t.Rotation).TransformPoint(WireMath.Mirror(l));
                Vec3 expected = PoseMath.Apply(pose, local.Transform(l));
                Assert.IsTrue((drawn - expected).Length < 1e-9, "child " + (drawn - expected).Length);
            }
        }

        internal static Quat RandomQuat(Random rnd)
        {
            double u1 = rnd.NextDouble(), u2 = rnd.NextDouble(), u3 = rnd.NextDouble();
            double a = Math.Sqrt(1 - u1), b = Math.Sqrt(u1);
            return new Quat(a * Math.Sin(2 * Math.PI * u2), a * Math.Cos(2 * Math.PI * u2), b * Math.Sin(2 * Math.PI * u3), b * Math.Cos(2 * Math.PI * u3)).WithNonNegativeW();
        }

        [TestMethod]
        public void ColumnMajor_Layout()
        {
            var m = new SeMatrix(new Vec3(1, 2, 3), new Vec3(4, 5, 6), new Vec3(7, 8, 9), new Vec3(10, 11, 12));
            var d = new float[18];
            InstanceMath.ToUnityColumnMajor(m, d, 2);
            CollectionAssert.AreEqual(new float[] { 0, 0, 1, 2, -3, 0, 4, 5, -6, 0, -7, -8, 9, 0, 10, 11, -12, 1 }, d);
            MirrorConventionsTests.AssertThrows<ArgumentException>(() => InstanceMath.ToUnityColumnMajor(m, new float[15], 0));
        }

        [TestMethod]
        public void Interpolate_EndpointsExactAndMidpoint()
        {
            var rnd = new Random(7);
            for (int i = 0; i < 100; i++)
            {
                SeMatrix a = RandomRigid(rnd, 10), b = RandomRigid(rnd, 10);
                Assert.IsTrue(BitEqual(InstanceMath.Interpolate(a, b, 1.0), b), "alpha 1 = the newer pose exactly");
                Assert.IsTrue(BitEqual(InstanceMath.Interpolate(a, b, 1.5), b));
                Assert.IsTrue(BitEqual(InstanceMath.Interpolate(a, b, 0.0), a));
                Assert.IsTrue(BitEqual(InstanceMath.Interpolate(a, b, -1.0), a));
                Assert.IsTrue(BitEqual(InstanceMath.Interpolate(a, b, double.NaN), a));
                SeMatrix mid = InstanceMath.Interpolate(a, b, 0.5);
                Assert.IsTrue((mid.Translation - (a.Translation + b.Translation) * 0.5).Length < 1e-12);
                Assert.IsTrue(Math.Abs(InstanceMath.RotationAngle(a, mid) - InstanceMath.RotationAngle(mid, b)) < 1e-9, "slerp halves the angle");
                Assert.IsTrue(Math.Abs(mid.Determinant - 1) < 1e-12);
            }
            // Row scale is interpolated too.
            SeMatrix s1 = Scaled(InstanceMath.Identity, 1, 1, 1), s3 = Scaled(InstanceMath.Identity, 3, 3, 3);
            Assert.IsTrue(Math.Abs(InstanceMath.Interpolate(s1, s3, 0.25).Right.Length - 1.5) < 1e-12);
            // Shortest arc: q and -q are the same rotation.
            Quat q = RandomQuat(rnd);
            Quat r = InstanceMath.Slerp(q, new Quat(-q.X, -q.Y, -q.Z, -q.W), 0.5);
            Assert.IsTrue(Quat.RotationDistance(q, r) < 1e-12);
        }

        [TestMethod]
        public void HasMoved_Threshold()
        {
            var rnd = new Random(8);
            SeMatrix a = RandomRigid(rnd, 5);
            Assert.IsFalse(InstanceMath.HasMoved(a, a));
            Assert.IsFalse(InstanceMath.HasMoved(a, new SeMatrix(a.Right, a.Up, a.Backward, a.Translation + new Vec3(0.5e-4, 0, 0))));
            Assert.IsTrue(InstanceMath.HasMoved(a, new SeMatrix(a.Right, a.Up, a.Backward, a.Translation + new Vec3(0, 2e-4, 0))));
            Assert.IsFalse(InstanceMath.HasMoved(a, Turn(a, 0.5e-4)));
            Assert.IsTrue(InstanceMath.HasMoved(a, Turn(a, 2e-4)));
            Assert.IsTrue(Math.Abs(InstanceMath.RotationAngle(a, Turn(a, 1e-3)) - 1e-3) < 1e-9);
            Assert.IsTrue(Math.Abs(InstanceMath.RotationAngle(a, Turn(a, 2.5)) - 2.5) < 1e-9);
        }

        /// <summary>a turned by angle about its own Up axis (rows rotated in world space).</summary>
        private static SeMatrix Turn(SeMatrix a, double angle)
        {
            Vec3 axis = a.Up;
            Func<Vec3, Vec3> rot = v => v * Math.Cos(angle) + Vec3.Cross(axis, v) * Math.Sin(angle) + axis * (Vec3.Dot(axis, v) * (1 - Math.Cos(angle)));
            return new SeMatrix(rot(a.Right), rot(a.Up), rot(a.Backward), a.Translation);
        }
    }
}
