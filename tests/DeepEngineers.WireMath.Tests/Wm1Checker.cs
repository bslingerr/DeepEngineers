using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DeepEngineers.Wire;

namespace DeepEngineers.WireMath.Tests
{
    /// <summary>A parsed WM-1 vector file: header, site, tolerances and the records in file order.</summary>
    internal sealed class Wm1File
    {
        public string Path;
        public Dictionary<string, object> Header;
        public SiteMap Site;
        public string SiteName;
        public List<Dictionary<string, object>> Records = new List<Dictionary<string, object>>();
        public Dictionary<string, int> KindCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        public double TolPos, TolPos32, TolDir, TolQuat, TolScale, TolRoundTrip;

        private static readonly Dictionary<string, Wm1File> Cache = new Dictionary<string, Wm1File>(StringComparer.Ordinal);

        /// <summary>Loads (once per test run) the file for "v1" or "example", generating it with the Python oracle.</summary>
        public static Wm1File ForSite(string site)
        {
            lock (Cache)
            {
                Wm1File f;
                if (!Cache.TryGetValue(site, out f))
                {
                    f = Load(RepoEnv.Wm1File(site));
                    Cache[site] = f;
                }
                return f;
            }
        }

        public static Wm1File Load(string path)
        {
            var f = new Wm1File { Path = path };
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length == 0) continue;
                var rec = (Dictionary<string, object>)MiniJson.Parse(line);
                string kind = J.S(rec, "kind");
                if (kind == "header")
                {
                    if (f.Header != null) throw new FormatException("two headers in " + path);
                    f.Header = rec;
                    continue;
                }
                int n;
                f.KindCounts.TryGetValue(kind, out n);
                f.KindCounts[kind] = n + 1;
                f.Records.Add(rec);
            }
            if (f.Header == null) throw new FormatException("no header in " + path);
            var site = J.Obj(f.Header, "site");
            f.SiteName = J.S(site, "name");
            f.Site = new SiteMap(J.V3(site, "C"), J.D(site, "Rs"), J.V3(site, "U0"), J.V3(site, "N0"), J.D(site, "psi_deg"), J.V3(site, "O"));
            var tol = J.Obj(f.Header, "tolerances");
            f.TolPos = J.D(tol, "pos_double_m");
            f.TolPos32 = J.D(tol, "pos_float32_m");
            f.TolDir = J.D(tol, "dir");
            f.TolQuat = J.D(tol, "quat");
            f.TolScale = J.D(tol, "scale");
            f.TolRoundTrip = J.D(tol, "local_roundtrip_m");
            return f;
        }
    }

    /// <summary>Per-kind outcome: records checked, records failing, the worst error of every measure and its tolerance.</summary>
    internal sealed class KindReport
    {
        public string Kind;
        public int Records;
        public int Failed;
        public readonly SortedDictionary<string, double> Worst = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, double> Tolerance = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public string FirstFailure;
    }

    internal sealed class Wm1Report
    {
        public string Impl;
        public string Site;
        public readonly SortedDictionary<string, KindReport> Kinds = new SortedDictionary<string, KindReport>(StringComparer.Ordinal);

        public int TotalRecords { get { return Kinds.Values.Sum(k => k.Records); } }
        public int TotalFailed { get { return Kinds.Values.Sum(k => k.Failed); } }

        public int Failed(string kind)
        {
            KindReport k;
            return Kinds.TryGetValue(kind, out k) ? k.Failed : 0;
        }

        public string Summary()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("WM-1 " + Site + " / " + Impl + ": " + TotalRecords + " records, " + TotalFailed + " failed");
            foreach (KindReport k in Kinds.Values)
            {
                sb.Append("  ").Append(k.Kind.PadRight(7)).Append(' ').Append(k.Records.ToString(ci).PadLeft(6))
                  .Append(" records, ").Append(k.Failed.ToString(ci).PadLeft(5)).Append(" failed |");
                foreach (KeyValuePair<string, double> w in k.Worst)
                {
                    double t = k.Tolerance[w.Key];
                    sb.Append(' ').Append(w.Key).Append(' ').Append(w.Value.ToString("0.00e+00", ci)).Append(" (tol ")
                      .Append(t.ToString("0e+00", ci)).Append(')');
                }
                sb.AppendLine();
                if (k.FirstFailure != null) sb.Append("    first failure: ").AppendLine(k.FirstFailure);
            }
            return sb.ToString();
        }
    }

    /// <summary>
 /// Checks every WM-1 record with a <see cref="WireImpl"/> within the header tolerances:
 /// positions in doubles 1e-6 m, after float32 1 mm; directions and quaternions (as rotations) 1e-9; scale 1e-12;
 /// local round trip 1e-9 m. Float32 copies of q and s (no header tolerance) are compared at 2.5e-7, two float32
 /// ulps at 1. Velocities and angular velocities use the direction tolerance as an absolute bound.
 /// </summary>
    internal static class Wm1Checker
    {
        public const double Float32UnitTol = 2.5e-7;

        private sealed class Ctx
        {
            public KindReport K;
            public bool RecordOk;
            public int Index;

            public void Check(string measure, double err, double tol)
            {
                double w;
                K.Worst.TryGetValue(measure, out w);
                K.Worst[measure] = double.IsNaN(err) ? double.PositiveInfinity : Math.Max(w, err);
                K.Tolerance[measure] = tol;
                if (!(err <= tol))
                {
                    if (RecordOk && K.FirstFailure == null)
                        K.FirstFailure = "record " + Index + ": " + measure + " error " + err.ToString("R", CultureInfo.InvariantCulture)
                            + " > " + tol.ToString("R", CultureInfo.InvariantCulture);
                    RecordOk = false;
                }
            }

            public void Require(string measure, bool ok)
            {
                Check(measure, ok ? 0.0 : 1.0, 0.0);
            }
        }

        public static Wm1Report Run(Wm1File f, WireImpl impl)
        {
            var rep = new Wm1Report { Impl = impl.Name, Site = f.SiteName };
            SiteMap site = f.Site;
            var poses = new List<WirePose>();
            var poseMatrices = new List<SeMatrix>();
            var ctx = new Ctx();
            for (int i = 0; i < f.Records.Count; i++)
            {
                Dictionary<string, object> r = f.Records[i];
                string kind = J.S(r, "kind");
                KindReport k;
                if (!rep.Kinds.TryGetValue(kind, out k)) { k = new KindReport { Kind = kind }; rep.Kinds[kind] = k; }
                ctx.K = k;
                ctx.RecordOk = true;
                ctx.Index = i + 1;   // line number (the header is line 0)
                switch (kind)
                {
                    case "point": Point(f, impl, site, r, ctx); break;
                    case "dir": Dir(f, impl, site, r, ctx); break;
                    case "pose":
                        {
                            SeMatrix m = J.Matrix(r, "se_matrix");
                            poses.Add(new WirePose(J.V3(r, "o"), J.Q4(r, "q"), J.D(r, "s")));
                            poseMatrices.Add(m);
                            Pose(f, impl, site, r, m, ctx);
                            break;
                        }
                    case "camera": Camera(f, impl, site, r, ctx); break;
                    case "local": Local(f, impl, site, r, poses, poseMatrices, ctx); break;
                    case "box": Box(f, impl, r, poses, ctx); break;
                    case "mesh": Mesh(f, impl, r, ctx); break;
                    case "motion": Motion(f, impl, site, r, ctx); break;
                    case "static": Static(f, impl, site, r, ctx); break;
                    default: throw new FormatException("unknown record kind '" + kind + "' at line " + ctx.Index);
                }
                k.Records++;
                if (!ctx.RecordOk) k.Failed++;
            }
            return rep;
        }

        private static void Point(Wm1File f, WireImpl impl, SiteMap site, Dictionary<string, object> r, Ctx c)
        {
            Vec3 se = J.V3(r, "se"), sn = J.V3(r, "sn");
            Vec3 got = impl.SeToSn(site, se);
            c.Check("F", Vec3.Distance(got, sn), f.TolPos);
            c.Check("F^-1", Vec3.Distance(impl.SnToSe(site, sn), se), f.TolPos);
            c.Check("F.f32", Vec3.Distance(got.RoundToFloat32(), J.V3(r, "sn_f32")), f.TolPos32);
        }

        private static void Dir(Wm1File f, WireImpl impl, SiteMap site, Dictionary<string, object> r, Ctx c)
        {
            Vec3 at = J.V3(r, "at_se"), se = J.V3(r, "se"), sn = J.V3(r, "sn");
            c.Check("mapdir", Vec3.Distance(impl.DirSeToSn(site, at, se), sn), f.TolDir);
            c.Check("mapdir^-1", Vec3.Distance(impl.DirSnToSe(site, at, sn), se), f.TolDir);
        }

        private static void Pose(Wm1File f, WireImpl impl, SiteMap site, Dictionary<string, object> r, SeMatrix m, Ctx c)
        {
            Vec3 pivot = impl.PivotNearest(m, J.V3(r, "box_min"), J.V3(r, "box_max"), J.V3(r, "camera_se"));
            c.Check("pivot", Vec3.Distance(pivot, J.V3(r, "pivot_se")), f.TolPos);
            WirePose p = impl.Pose(site, m, pivot);
            c.Check("o", Vec3.Distance(p.O, J.V3(r, "o")), f.TolPos);
            c.Check("q", Quat.RotationDistance(p.Q, J.Q4(r, "q")), f.TolQuat);
            c.Require("q.w>=0", p.Q.W >= 0);
            c.Check("s", Math.Abs(p.S - J.D(r, "s")), f.TolScale);
            c.Check("o.f32", Vec3.Distance(p.O.RoundToFloat32(), J.V3(r, "o_f32")), f.TolPos32);
            c.Check("q.f32", Quat.RotationDistance(p.Q.RoundToFloat32(), J.Q4(r, "q_f32")), Float32UnitTol);
            c.Check("s.f32", Math.Abs((float)p.S - J.D(r, "s_f32")), Float32UnitTol);
        }

        private static void Camera(Wm1File f, WireImpl impl, SiteMap site, Dictionary<string, object> r, Ctx c)
        {
            CameraPose cp = impl.Camera(site, J.Matrix(r, "se_matrix"));
            c.Check("pos", Vec3.Distance(cp.Position, J.V3(r, "pos")), f.TolPos);
            c.Check("q", Quat.RotationDistance(cp.Rotation, J.Q4(r, "q")), f.TolQuat);
            c.Require("q.w>=0", cp.Rotation.W >= 0);
        }

        private static void Local(Wm1File f, WireImpl impl, SiteMap site, Dictionary<string, object> r,
            List<WirePose> poses, List<SeMatrix> matrices, Ctx c)
        {
            int pi = J.I(r, "pose");
            WirePose p = poses[pi];
            Vec3 l = J.V3(r, "local_se"), sn = J.V3(r, "sn");
            c.Check("Apply", Vec3.Distance(impl.Apply(p, l), sn), f.TolPos);
            c.Check("ToLocal", Vec3.Distance(impl.ToLocal(p, sn), l), f.TolRoundTrip);
            // the exact map of the same point (information in the file; checks F and SE matrix composition)
            c.Check("F(M l)", Vec3.Distance(impl.SeToSn(site, matrices[pi].Transform(l)), J.V3(r, "exact_sn")), f.TolPos);
        }

        private static void Box(Wm1File f, WireImpl impl, Dictionary<string, object> r, List<WirePose> poses, Ctx c)
        {
            WirePose p = poses[J.I(r, "pose")];
            Vec3 cSec = J.V3(r, "section_centre_se");
            c.Check("root", Vec3.Distance(impl.Apply(p, cSec), J.V3(r, "section_root_sn")), f.TolPos);
            List<object> axes = J.Arr(r, "axes_se");
            ProxyBox b = impl.PrescaleBox(p.Q, p.S, cSec, J.V3(r, "box_centre_se"), J.V3(r, "half_extents_se"),
                J.V3(axes[0]), J.V3(axes[1]), J.V3(axes[2]));
            c.Check("centre", Vec3.Distance(b.Centre, J.V3(r, "centre_section_local_unity")), f.TolPos);
            c.Check("half", Vec3.Distance(b.HalfExtents, J.V3(r, "half_extents_unity")), f.TolPos);
            List<object> au = J.Arr(r, "axes_unity");
            c.Check("axes", Math.Max(Vec3.Distance(b.AxisU0, J.V3(au[0])),
                Math.Max(Vec3.Distance(b.AxisU1, J.V3(au[1])), Vec3.Distance(b.AxisU2, J.V3(au[2])))), f.TolDir);
            Quat expected = J.Q4(r, "rotation_section_local_unity");
            c.Check("rotation", Quat.RotationDistance(b.Rotation, expected), f.TolQuat);
            if (!J.B(r, "rotated")) c.Check("rotation=I", Quat.RotationDistance(b.Rotation, Quat.Identity), f.TolQuat);
        }

        private static void Mesh(Wm1File f, WireImpl impl, Dictionary<string, object> r, Ctx c)
        {
            List<object> vse = J.Arr(r, "verts_se"), vu = J.Arr(r, "verts_unity");
            List<object> tse = J.Arr(r, "tris_se"), tu = J.Arr(r, "tris_unity"), nse = J.Arr(r, "normals_se");
            var verts = new Vec3[vse.Count];
            double worstV = 0;
            for (int i = 0; i < vse.Count; i++)
            {
                verts[i] = impl.Mirror(J.V3(vse[i]));
                worstV = Math.Max(worstV, Vec3.Distance(verts[i], J.V3(vu[i])));
            }
            c.Check("verts", worstV, f.TolPos);
            int triMismatch = 0, inward = 0;
            for (int t = 0; t < tse.Count; t++)
            {
                var a = (List<object>)tse[t];
                var e = (List<object>)tu[t];
                int u0, u1, u2;
                impl.MirrorTri((int)(double)a[0], (int)(double)a[1], (int)(double)a[2], out u0, out u1, out u2);
                if (u0 != (int)(double)e[0] || u1 != (int)(double)e[1] || u2 != (int)(double)e[2]) triMismatch++;
                // winding: the Unity triangle's geometric normal must point along the mirrored outward normal
                Vec3 n = impl.Mirror(J.V3(nse[t]));
                Vec3 g = Vec3.Cross(verts[u1] - verts[u0], verts[u2] - verts[u0]);
                if (!(Vec3.Dot(g, n) > 0)) inward++;
            }
            c.Check("tris", triMismatch, 0);
            c.Check("winding", inward, 0);
        }

        private static void Motion(Wm1File f, WireImpl impl, SiteMap site, Dictionary<string, object> r, Ctx c)
        {
            Vec3 at = J.V3(r, "at_se");
            c.Check("omega", Vec3.Distance(impl.AngVel(site, at, J.V3(r, "omega_se")), J.V3(r, "omega_sn")), f.TolDir);
            c.Check("v", Vec3.Distance(impl.Vel(site, at, J.V3(r, "v_se")), J.V3(r, "v_sn")), f.TolDir);
        }

        private static void Static(Wm1File f, WireImpl impl, SiteMap site, Dictionary<string, object> r, Ctx c)
        {
            StaticBoxSe sb = impl.StaticToSe(site, J.V3(r, "centre_sn"), J.Q4(r, "q_sn"), J.V3(r, "half_extents_sn"));
            c.Check("centre", Vec3.Distance(sb.Centre, J.V3(r, "centre_se")), f.TolPos);
            c.Check("s", Math.Abs(sb.S - J.D(r, "s")), f.TolScale);
            var ax = J.Obj(r, "axes_se");
            c.Check("axes", Math.Max(Vec3.Distance(sb.Matrix.Right, J.V3(ax, "right")),
                Math.Max(Vec3.Distance(sb.Matrix.Up, J.V3(ax, "up")), Vec3.Distance(sb.Matrix.Backward, J.V3(ax, "backward")))), f.TolDir);
            c.Check("det=+1", Math.Abs(sb.Determinant - 1.0), f.TolDir);
            c.Check("half", Vec3.Distance(sb.HalfExtents, J.V3(r, "half_extents_se")), f.TolPos);
        }
    }
}
