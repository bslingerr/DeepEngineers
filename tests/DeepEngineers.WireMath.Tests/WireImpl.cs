using DeepEngineers.Wire;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>
 /// The math under test, behind virtual calls so the WM-1 checker can run the production code
 /// (<see cref="WireImpl"/> delegates straight to DeepEngineers.Wire) or a seeded failure (<see cref="MutantWireImpl"/>).
 /// </summary>
    internal class WireImpl
    {
        public virtual string Name { get { return "production"; } }

        // F, F^-1, mapdir (SE side)
        public virtual Vec3 SeToSn(SiteMap site, Vec3 p) { return site.SeToSn(p); }
        public virtual Vec3 SnToSe(SiteMap site, Vec3 q) { return site.SnToSe(q); }
        public virtual Vec3 DirSeToSn(SiteMap site, Vec3 p, Vec3 w) { return site.DirSeToSn(p, w); }
        public virtual Vec3 DirSnToSe(SiteMap site, Vec3 p, Vec3 d) { return site.DirSnToSe(p, d); }

        // SE-side builders
        public virtual Vec3 PivotNearest(SeMatrix m, Vec3 bmin, Vec3 bmax, Vec3 cam) { return Wire.WireMath.PivotNearest(m, bmin, bmax, cam); }
        public virtual WirePose Pose(SiteMap site, SeMatrix m, Vec3 pivot) { return Wire.WireMath.Pose(site, m, pivot); }
        public virtual CameraPose Camera(SiteMap site, SeMatrix m) { return Wire.WireMath.Camera(site, m); }
        public virtual Vec3 Vel(SiteMap site, Vec3 pivot, Vec3 v) { return Wire.WireMath.Vel(site, pivot, v); }
        public virtual Vec3 AngVel(SiteMap site, Vec3 pivot, Vec3 w) { return Wire.WireMath.AngVel(site, pivot, w); }
        public virtual StaticBoxSe StaticToSe(SiteMap site, Vec3 c, Quat q, Vec3 h) { return Wire.WireMath.StaticToSe(site, c, q, h); }

        // SN side
        public virtual Vec3 Mirror(Vec3 v) { return Wire.WireMath.Mirror(v); }

        public virtual void MirrorTri(int i0, int i1, int i2, out int u0, out int u1, out int u2)
        {
            Wire.WireMath.MirrorTri(i0, i1, i2, out u0, out u1, out u2);
        }

        public virtual Vec3 Apply(WirePose p, Vec3 l) { return Wire.WireMath.Apply(p, l); }
        public virtual Vec3 ToLocal(WirePose p, Vec3 w) { return Wire.WireMath.ToLocal(p, w); }

        public virtual ProxyBox PrescaleBox(Quat q, double s, Vec3 cSec, Vec3 b, Vec3 h, Vec3 e0, Vec3 e1, Vec3 e2)
        {
            return Wire.WireMath.PrescaleBox(q, s, cSec, b, h, e0, e1, e2);
        }
    }

    /// <summary>The seeded failures of ROADMAP S0.M4 (gap-1 section 5, CONTRACT 2.9), plus the static-axes mutant.</summary>
    internal enum Mutant
    {
        /// <summary>No mutation: the re-implementation in <see cref="MutantWireImpl"/> must pass WM-1 like production.</summary>
        None,
        /// <summary>Mirror(x, y, z) = (x, y, z) on the Subnautica side.</summary>
        MirrorDropped,
        /// <summary>Unity triangles keep (I0, I1, I2).</summary>
        TriangleSwapDropped,
        /// <summary>mapdir (the carried frame) taken at the entity origin m.T instead of the pivot P*.</summary>
        MapdirAtEntityOrigin,
        /// <summary>S = identity: the horizontal Jacobian scale is never applied.</summary>
        ScaleDropped,
        /// <summary>S = diag(s, s, s): s applied to y as well (e.g. localScale (s, s, s)).</summary>
        ScaleAppliedToY,
        /// <summary>omega_SN = +mapdir(omega): the pseudovector sign flip forgotten.</summary>
        AngVelNoSignFlip,
        /// <summary>Subnautica rebuilds q as LookRotation(Mirror(q*z), Mirror(q*y)) before using it (double mirror).</summary>
        DoubleMirror,
        /// <summary>staticAxes without the mirror: third row mapdir^-1(q*z) instead of mapdir^-1(q*(-z)) (determinant -1).</summary>
        StaticAxesWithoutMirror,
    }

    /// <summary>
 /// Test-only re-implementation of the composite wire steps from primitives, with one switchable mutation. Never
 /// part of production code (ROADMAP S0.M4: "switchable code paths in a test-only helper").
 /// </summary>
    internal sealed class MutantWireImpl : WireImpl
    {
        private readonly Mutant _m;

        public MutantWireImpl(Mutant m) { _m = m; }

        public override string Name { get { return "mutant " + _m; } }

        private Vec3 Mir(Vec3 v)
        {
            return _m == Mutant.MirrorDropped ? v : new Vec3(v.X, v.Y, -v.Z);
        }

        private Vec3 S(Vec3 v, double s)
        {
            switch (_m)
            {
                case Mutant.ScaleDropped: return v;
                case Mutant.ScaleAppliedToY: return v * s;
                default: return new Vec3(v.X * s, v.Y, v.Z * s);
            }
        }

        private Vec3 SInv(Vec3 v, double s)
        {
            switch (_m)
            {
                case Mutant.ScaleDropped: return v;
                case Mutant.ScaleAppliedToY: return v / s;
                default: return new Vec3(v.X / s, v.Y, v.Z / s);
            }
        }

        /// <summary>The rotation the Subnautica side actually uses for a received wire q.</summary>
        private Quat SnQ(Quat q)
        {
            if (_m != Mutant.DoubleMirror) return q;
            return Quat.LookRotation(Mir(q.Rotate(Vec3.UnitZ)), Mir(q.Rotate(Vec3.UnitY)));
        }

        public override Vec3 Mirror(Vec3 v) { return Mir(v); }

        public override void MirrorTri(int i0, int i1, int i2, out int u0, out int u1, out int u2)
        {
            u0 = i0;
            if (_m == Mutant.TriangleSwapDropped) { u1 = i1; u2 = i2; }
            else { u1 = i2; u2 = i1; }
        }

        public override WirePose Pose(SiteMap site, SeMatrix m, Vec3 pivot)
        {
            double s = site.Rs / (pivot - site.C).Length;
            Vec3 at = _m == Mutant.MapdirAtEntityOrigin ? m.Translation : pivot;
            Vec3 e, u, n;
            site.FrameAt(at, out e, out u, out n);
            Vec3 o = site.SeToSn(pivot) + S(site.DirSeToSn(e, u, n, m.Translation - pivot), s);
            Quat q = Quat.LookRotation(site.DirSeToSn(e, u, n, m.Forward), site.DirSeToSn(e, u, n, m.Up));
            return new WirePose(o, q, s);
        }

        public override Vec3 Vel(SiteMap site, Vec3 pivot, Vec3 v)
        {
            double s = site.Rs / (pivot - site.C).Length;
            return S(site.DirSeToSn(pivot, v), s);
        }

        public override Vec3 AngVel(SiteMap site, Vec3 pivot, Vec3 w)
        {
            Vec3 d = site.DirSeToSn(pivot, w);
            return _m == Mutant.AngVelNoSignFlip ? d : -d;
        }

        public override StaticBoxSe StaticToSe(SiteMap site, Vec3 c, Quat q, Vec3 h)
        {
            Quat qn = q.Normalized();
            Vec3 p = site.SnToSe(c);
            double s = site.Rs / (p - site.C).Length;
            Vec3 e, u, n;
            site.FrameAt(p, out e, out u, out n);
            Vec3 right = site.DirSnToSe(e, u, n, qn.Rotate(Vec3.UnitX));
            Vec3 up = site.DirSnToSe(e, u, n, qn.Rotate(Vec3.UnitY));
            Vec3 third = _m == Mutant.StaticAxesWithoutMirror ? Vec3.UnitZ : new Vec3(0.0, 0.0, -1.0);
            Vec3 backward = site.DirSnToSe(e, u, n, qn.Rotate(third));
            var axes = new SeMatrix(right, up, backward, p);
            var half = new Vec3(
                h.X * SInv(qn.Rotate(Vec3.UnitX), s).Length,
                h.Y * SInv(qn.Rotate(Vec3.UnitY), s).Length,
                h.Z * SInv(qn.Rotate(Vec3.UnitZ), s).Length);
            return new StaticBoxSe(axes, s, axes.Determinant, half);
        }

        public override Vec3 Apply(WirePose p, Vec3 l)
        {
            return p.O + S(SnQ(p.Q).Rotate(Mir(l)), p.S);
        }

        public override Vec3 ToLocal(WirePose p, Vec3 w)
        {
            return Mir(SnQ(p.Q).Conjugate().Rotate(SInv(w - p.O, p.S)));
        }

        private Vec3 K(Quat q, double s, Vec3 v)
        {
            return q.Conjugate().Rotate(S(q.Rotate(v), s));
        }

        public override ProxyBox PrescaleBox(Quat q, double s, Vec3 cSec, Vec3 b, Vec3 h, Vec3 e0, Vec3 e1, Vec3 e2)
        {
            Quat qs = SnQ(q);
            Vec3 centre = K(qs, s, Mir(b - cSec));
            Vec3 a0 = Mir(e0), a1 = Mir(e1), a2 = Mir(e2);
            Vec3 k0 = K(qs, s, a0), k1 = K(qs, s, a1), k2 = K(qs, s, a2);
            var half = new Vec3(h.X * k0.Length, h.Y * k1.Length, h.Z * k2.Length);
            Mat3 r = new Mat3(k0, k1, -k2).PolarRotation();
            return new ProxyBox(centre, half, r.ToQuat(), a0, a1, a2);
        }
    }
}
