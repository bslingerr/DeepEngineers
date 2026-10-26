using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DeepEngineers.Wire
{
    /// <summary>One embedded SE subset record: an SE world matrix, its entity box and the camera, with the expected P* and pose.</summary>
    public struct Wm1PoseRecord
    {
        public SeMatrix Matrix;
        public Vec3 BoxMin, BoxMax, Camera;
        public Vec3 ExpectedPivot;
        public WirePose Expected;
    }

    /// <summary>One embedded Subnautica 'local' record: a wire pose, an SE-local point and its expected SN world point.</summary>
    public struct Wm1LocalRecord
    {
        public WirePose Pose;
        public Vec3 LocalSe;
        public Vec3 ExpectedSn;
    }

    /// <summary>
 /// One embedded Subnautica 'box' record: a wire pose, a section centre and one SE-local box (centre, half extents,
 /// axes e0..e2), with the expected section root position (SN world) and the expected pre-scaled collider
 /// (section-local Unity centre, half extents, child rotation).
 /// </summary>
    public struct Wm1BoxRecord
    {
        public WirePose Pose;
        public Vec3 SectionCentreSe, ExpectedSectionRootSn;
        public Vec3 BoxCentreSe, HalfExtentsSe;
        public Vec3 AxisSe0, AxisSe1, AxisSe2;
        public Vec3 ExpectedCentreUnity, ExpectedHalfExtentsUnity;
        public Quat ExpectedRotationUnity;
        public bool Rotated;
    }

    /// <summary>
 /// The two in-game WM-1 subsets (docs/ROADMAP.md 5.9): 50 'pose' records for SE; 50 'local' + 20 'box' records for
 /// Subnautica (10 of the boxes rotated). Data in the generated Wm1Subset.cs (tests/wm1/make_subset.py), all from
 /// the contract site SITE_V1.
 /// </summary>
    public static partial class Wm1Subset
    {
        /// <summary>The site the records were generated with (site v1 around the test centre).</summary>
        public static SiteMap Site
        {
            get { return SiteMap.ForSiteV1(new Vec3(SiteCx, SiteCy, SiteCz), SiteRs); }
        }

        private static Vec3 V(double[] a, int i) { return new Vec3(a[i], a[i + 1], a[i + 2]); }
        private static Quat Q(double[] a, int i) { return new Quat(a[i], a[i + 1], a[i + 2], a[i + 3]); }

        public static Wm1PoseRecord SePose(int index)
        {
            if (index < 0 || index >= SePoseCount) throw new ArgumentOutOfRangeException("index");
            double[] a = SePoseData;
            int b = index * SePoseStride;
            var r = new Wm1PoseRecord();
            r.Matrix = new SeMatrix(V(a, b), V(a, b + 3), V(a, b + 6), V(a, b + 9));
            r.BoxMin = V(a, b + 12);
            r.BoxMax = V(a, b + 15);
            r.Camera = V(a, b + 18);
            r.ExpectedPivot = V(a, b + 21);
            r.Expected = new WirePose(V(a, b + 24), Q(a, b + 27), a[b + 31]);
            return r;
        }

        public static Wm1LocalRecord SnLocal(int index)
        {
            if (index < 0 || index >= SnLocalCount) throw new ArgumentOutOfRangeException("index");
            double[] a = SnLocalData;
            int b = index * SnLocalStride;
            var r = new Wm1LocalRecord();
            r.Pose = new WirePose(V(a, b), Q(a, b + 3), a[b + 7]);
            r.LocalSe = V(a, b + 8);
            r.ExpectedSn = V(a, b + 11);
            return r;
        }

        public static Wm1BoxRecord SnBox(int index)
        {
            if (index < 0 || index >= SnBoxCount) throw new ArgumentOutOfRangeException("index");
            double[] a = SnBoxData;
            int b = index * SnBoxStride;
            var r = new Wm1BoxRecord();
            r.Pose = new WirePose(V(a, b), Q(a, b + 3), a[b + 7]);
            r.SectionCentreSe = V(a, b + 8);
            r.ExpectedSectionRootSn = V(a, b + 11);
            r.BoxCentreSe = V(a, b + 14);
            r.HalfExtentsSe = V(a, b + 17);
            r.AxisSe0 = V(a, b + 20);
            r.AxisSe1 = V(a, b + 23);
            r.AxisSe2 = V(a, b + 26);
            r.ExpectedCentreUnity = V(a, b + 29);
            r.ExpectedHalfExtentsUnity = V(a, b + 32);
            r.ExpectedRotationUnity = Q(a, b + 35);
            r.Rotated = a[b + 39] != 0.0;
            return r;
        }

        /// <summary>SHA-256 (lower-case hex) of the little-endian IEEE-754 bytes of the given arrays, in order.</summary>
        public static string DataSha256(params double[][] arrays)
        {
            if (!BitConverter.IsLittleEndian) throw new NotSupportedException("the wire is little-endian only");
            int n = 0;
            foreach (double[] a in arrays) n += a.Length;
            var bytes = new byte[n * 8];
            int off = 0;
            foreach (double[] a in arrays)
            {
                Buffer.BlockCopy(a, 0, bytes, off, a.Length * 8);
                off += a.Length * 8;
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(bytes);
                var sb = new StringBuilder(64);
                foreach (byte x in h) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>SHA-256 of the embedded SE data, recomputed now (equals <see cref="SeDataSha256"/> when intact).</summary>
        public static string ComputeSeDataSha256() { return DataSha256(SePoseData); }

        /// <summary>SHA-256 of the embedded Subnautica data, recomputed now (equals <see cref="SnDataSha256"/> when intact).</summary>
        public static string ComputeSnDataSha256() { return DataSha256(SnLocalData, SnBoxData); }
    }

    /// <summary>Which in-game subset to run.</summary>
    public enum SelfTestKind
    {
        /// <summary>SE plugin at Init: 50 'pose' records (P*, o, q, s).</summary>
        Se = 1,
        /// <summary>Subnautica plugin at load, math part: 50 'local' (Apply, ToLocal) + 20 'box' (K pre-scaling) records.
 /// The plugin adds the Unity part (real GameObjects, BoxColliders) itself.</summary>
        Sn = 2,
    }

    /// <summary>Outcome of <see cref="SelfTest.Run"/>; <see cref="LogLine"/> is what the plugins log.</summary>
    public sealed class SelfTestResult
    {
        public SelfTestKind Kind;
        public bool Pass;
        /// <summary>Records checked.</summary>
        public int Count;
        /// <summary>Records outside a tolerance.</summary>
        public int Failures;
        /// <summary>Worst position error (m): P*, o (SE); Apply, section root, collider centre and half extents (Subnautica).</summary>
        public double WorstPosition;
        /// <summary>Worst rotation error, min(|a - b|, |a + b|) over quaternion components.</summary>
        public double WorstRotation;
        /// <summary>Worst |s - s_expected| (SE only).</summary>
        public double WorstScale;
        /// <summary>Worst SE-local round trip |ToLocal(Apply(l)) - l| (m) (Subnautica only).</summary>
        public double WorstRoundTrip;
        /// <summary>Recomputed SHA-256 of the embedded data (first 16 hex digits are logged).</summary>
        public string DataSha256 = "";
        /// <summary>First failure or the exception text; empty on PASS.</summary>
        public string Message = "";

        /// <summary>
 /// <c>DE.WireSelfTest PASS|FAIL n=&lt;count&gt; worst=... side=se|sn site=SITE_V1 sha256=&lt;16 hex&gt;</c>
 /// (docs/CONTRACT.md 2.9, ROADMAP S0.G1-E4a).
 /// </summary>
        public string LogLine
        {
            get
            {
                var ci = CultureInfo.InvariantCulture;
                var sb = new StringBuilder(200);
                sb.Append("DE.WireSelfTest ").Append(Pass ? "PASS" : "FAIL");
                sb.Append(" n=").Append(Count.ToString(ci));
                sb.Append(" worst=pos:").Append(WorstPosition.ToString("0.0e+0", ci)).Append("m");
                sb.Append(",rot:").Append(WorstRotation.ToString("0.0e+0", ci));
                if (Kind == SelfTestKind.Se) sb.Append(",scale:").Append(WorstScale.ToString("0.0e+0", ci));
                else sb.Append(",roundtrip:").Append(WorstRoundTrip.ToString("0.0e+0", ci)).Append("m");
                sb.Append(" side=").Append(Kind == SelfTestKind.Se ? "se" : "sn");
                sb.Append(" site=").Append(Wm1Subset.SiteName);
                sb.Append(" sha256=").Append(DataSha256.Length >= 16 ? DataSha256.Substring(0, 16) : DataSha256);
                if (!Pass) sb.Append(" failures=").Append(Failures.ToString(ci)).Append(" reason=").Append(Message);
                return sb.ToString();
            }
        }

        public override string ToString() { return LogLine; }
    }

    /// <summary>
 /// The in-game WM-1 self-check (docs/CONTRACT.md 2.9 and 6 "Validation" step 6): recomputes the embedded subset with
 /// this build of <see cref="WireMath"/> in the real runtime (.NET Framework 4.8 under Pulsar, Unity Mono under
 /// BepInEx) and compares with the Python oracle's numbers within the WM-1 tolerances. Never throws; a FAIL
 /// disables the display bridge. Allocation: a few KiB once at load.
 /// </summary>
    public static class SelfTest
    {
        public static SelfTestResult Run(SelfTestKind kind)
        {
            var r = new SelfTestResult();
            r.Kind = kind;
            try
            {
                if (kind == SelfTestKind.Se) RunSe(r);
                else if (kind == SelfTestKind.Sn) RunSn(r);
                else throw new ArgumentOutOfRangeException("kind");
                r.Pass = r.Failures == 0 && r.Message.Length == 0 && r.Count > 0;
            }
            catch (Exception ex)
            {
                r.Pass = false;
                r.Message = ex.GetType().Name + ": " + ex.Message;
            }
            return r;
        }

        private static void Note(SelfTestResult r, string what, int index, double err, double tol)
        {
            // NaN never passes: !(err <= tol)
            if (!(err <= tol) && r.Message.Length == 0)
                r.Message = what + "[" + index.ToString(CultureInfo.InvariantCulture) + "] error "
                    + err.ToString("R", CultureInfo.InvariantCulture) + " > " + tol.ToString("R", CultureInfo.InvariantCulture);
        }

        private static double Worse(double worst, double err)
        {
            return double.IsNaN(err) ? double.PositiveInfinity : Math.Max(worst, err);
        }

        private static void RunSe(SelfTestResult r)
        {
            r.DataSha256 = Wm1Subset.ComputeSeDataSha256();
            if (r.DataSha256 != Wm1Subset.SeDataSha256)
                r.Message = "embedded SE subset corrupted (sha256 " + r.DataSha256 + " expected " + Wm1Subset.SeDataSha256 + ")";
            SiteMap site = Wm1Subset.Site;
            for (int i = 0; i < Wm1Subset.SePoseCount; i++)
            {
                Wm1PoseRecord rec = Wm1Subset.SePose(i);
                Vec3 pivot = WireMath.PivotNearest(rec.Matrix, rec.BoxMin, rec.BoxMax, rec.Camera);
                WirePose p = WireMath.Pose(site, rec.Matrix, pivot);
                double ePivot = Vec3.Distance(pivot, rec.ExpectedPivot);
                double eO = Vec3.Distance(p.O, rec.Expected.O);
                double eQ = Quat.RotationDistance(p.Q, rec.Expected.Q);
                double eS = Math.Abs(p.S - rec.Expected.S);
                bool wOk = p.Q.W >= 0;
                r.WorstPosition = Worse(Worse(r.WorstPosition, ePivot), eO);
                r.WorstRotation = Worse(r.WorstRotation, eQ);
                r.WorstScale = Worse(r.WorstScale, eS);
                bool ok = ePivot <= Wm1Subset.TolPosition && eO <= Wm1Subset.TolPosition && eQ <= Wm1Subset.TolRotation
                    && eS <= Wm1Subset.TolScale && wOk;
                if (!ok)
                {
                    r.Failures++;
                    Note(r, "pose.pivot", i, ePivot, Wm1Subset.TolPosition);
                    Note(r, "pose.o", i, eO, Wm1Subset.TolPosition);
                    Note(r, "pose.q", i, eQ, Wm1Subset.TolRotation);
                    Note(r, "pose.s", i, eS, Wm1Subset.TolScale);
                    if (!wOk && r.Message.Length == 0) r.Message = "pose.q[" + i.ToString(CultureInfo.InvariantCulture) + "] w < 0";
                }
                r.Count++;
            }
        }

        private static void RunSn(SelfTestResult r)
        {
            r.DataSha256 = Wm1Subset.ComputeSnDataSha256();
            if (r.DataSha256 != Wm1Subset.SnDataSha256)
                r.Message = "embedded SN subset corrupted (sha256 " + r.DataSha256 + " expected " + Wm1Subset.SnDataSha256 + ")";
            for (int i = 0; i < Wm1Subset.SnLocalCount; i++)
            {
                Wm1LocalRecord rec = Wm1Subset.SnLocal(i);
                Vec3 w = WireMath.Apply(rec.Pose, rec.LocalSe);
                double eW = Vec3.Distance(w, rec.ExpectedSn);
                double eRt = Vec3.Distance(WireMath.ToLocal(rec.Pose, rec.ExpectedSn), rec.LocalSe);
                r.WorstPosition = Worse(r.WorstPosition, eW);
                r.WorstRoundTrip = Worse(r.WorstRoundTrip, eRt);
                if (!(eW <= Wm1Subset.TolPosition && eRt <= Wm1Subset.TolLocalRoundTrip))
                {
                    r.Failures++;
                    Note(r, "local.apply", i, eW, Wm1Subset.TolPosition);
                    Note(r, "local.roundtrip", i, eRt, Wm1Subset.TolLocalRoundTrip);
                }
                r.Count++;
            }
            for (int i = 0; i < Wm1Subset.SnBoxCount; i++)
            {
                Wm1BoxRecord rec = Wm1Subset.SnBox(i);
                Vec3 root = WireMath.Apply(rec.Pose, rec.SectionCentreSe);
                ProxyBox box = WireMath.PrescaleBox(rec.Pose.Q, rec.Pose.S, rec.SectionCentreSe, rec.BoxCentreSe, rec.HalfExtentsSe,
                    rec.AxisSe0, rec.AxisSe1, rec.AxisSe2);
                double eRoot = Vec3.Distance(root, rec.ExpectedSectionRootSn);
                double eC = Vec3.Distance(box.Centre, rec.ExpectedCentreUnity);
                double eH = Vec3.Distance(box.HalfExtents, rec.ExpectedHalfExtentsUnity);
                double eR = Quat.RotationDistance(box.Rotation, rec.ExpectedRotationUnity);
                r.WorstPosition = Worse(Worse(Worse(r.WorstPosition, eRoot), eC), eH);
                r.WorstRotation = Worse(r.WorstRotation, eR);
                if (!(eRoot <= Wm1Subset.TolPosition && eC <= Wm1Subset.TolPosition && eH <= Wm1Subset.TolPosition
                      && eR <= Wm1Subset.TolRotation))
                {
                    r.Failures++;
                    Note(r, "box.root", i, eRoot, Wm1Subset.TolPosition);
                    Note(r, "box.centre", i, eC, Wm1Subset.TolPosition);
                    Note(r, "box.half", i, eH, Wm1Subset.TolPosition);
                    Note(r, "box.rotation", i, eR, Wm1Subset.TolRotation);
                }
                r.Count++;
            }
        }
    }
}
