using System;
using System.Globalization;
using System.Text;
using ChartC = DeepEngineers.Wire.S8aConventions.Chart;
using SnWorld = DeepEngineers.Wire.S8aConventions.SnWorld;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S8a charts (the treadmill; CONTRACT §3.3 "Chart and treadmill", §5.4 formats chart.step, chart.anchor, store.key).
 // A chart is a SiteMap with its own anchor; chart 0 is the site map v1. Pure math, doubles, SE side only. Python twin:
 // tools/planet/s8a_ref.py (vectors tests/s8a/vectors/chart.tsv).
 // =================================================================================================================

 /// <summary>The decision of <see cref="S8aChart.Decide(uint, Vec3, Vec3, Vec3, bool)"/> for one tick.</summary>
    public struct S8aChartDecision
    {
        /// <summary>T in batches along SN x and z (meaningful when <see cref="Step"/>).</summary>
        public int Tx, Tz;
        /// <summary>Back to chart 0 (the site map).</summary>
        public bool ToSite;
        /// <summary>Build chart k + 1 with T = (Tx, Tz) (T = 0: a re-centre).</summary>
        public bool Step;
        /// <summary>"site-out", "outer-x", "outer-z", "outer-xz", "inner", "anchor", "return", "treadmill-off" or null.</summary>
        public string Reason;

        public bool Any { get { return Step || ToSite; } }

        public void Deconstruct(out int tx, out int tz, out bool toSite)
        {
            tx = Tx;
            tz = Tz;
            toSite = ToSite;
        }

        public override string ToString()
        {
            if (ToSite) return "toSite (" + Reason + ")";
            if (Step) return "step T=(" + Tx + "," + Tz + ") batches (" + Reason + ")";
            return "none";
        }
    }

    /// <summary>The chart rule ("chart.step") and construction ("chart.anchor"), and the chart key ("store.key").</summary>
    public static class S8aChart
    {
        /// <summary>Metres per batch (160).</summary>
        public const double BatchM = SnWorld.BatchVoxels;

        /// <summary>
 /// Builds chart k + 1 ("chart.anchor"): anchor U0' = the camera's radial up (its point on the sea sphere); N0' = the
 /// carried north of chart k at the camera (<see cref="SiteMap.FrameAt"/>), so mapdir is continuous there; ψ kept;
 /// O' = F_k(camera) − T horizontally with O'.y = 0, so F_k+1(camera) = F_k(camera) − T. T = (tx, tz) batches;
 /// (0, 0) re-centres and keeps the camera's SN position.
 /// </summary>
        public static SiteMap Step(SiteMap k, Vec3 cameraSe, int tx, int tz)
        {
            return StepMetres(k, cameraSe, tx * BatchM, tz * BatchM);
        }

        /// <summary><see cref="Step"/> with T in metres (whole batches by the rule; S8a.M8 passes the exact overshoot).</summary>
        public static SiteMap StepMetres(SiteMap k, Vec3 cameraSe, double txM, double tzM)
        {
            if (k == null) throw new ArgumentNullException("k");
            Vec3 e, u, n;
            k.FrameAt(cameraSe, out e, out u, out n);
            Vec3 fk = k.SeToSn(cameraSe);
            double psiDeg = k.Psi * (180.0 / Math.PI);
            var o = new Vec3(fk.X - txM, 0.0, fk.Z - tzM);
            return new SiteMap(k.C, k.Rs, u, n, psiDeg, o);
        }

        /// <summary>The camera's arc distance from chart k's anchor along the sea sphere (= |F_k(camera).xz − O.xz|, metres).</summary>
        public static double ArcFromAnchor(SiteMap k, Vec3 cameraSe)
        {
            Vec3 p = k.SeToSn(cameraSe);
            double dx = p.X - k.O.X, dz = p.Z - k.O.Z;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
 /// "chart.step" for one tick from the SE camera: <paramref name="chartK"/> = the current chart (chart 0 = the site map),
 /// <paramref name="site"/> = the site map v1.
 /// </summary>
        public static S8aChartDecision Decide(SiteMap chartK, SiteMap site, uint chartSeq, Vec3 cameraSe, bool treadmillOn)
        {
            if (site == null) throw new ArgumentNullException("site");
            Vec3 camSite = site.SeToSn(cameraSe);
            Vec3 camK = chartSeq == 0 || chartK == null ? camSite : chartK.SeToSn(cameraSe);
            Vec3 o = chartSeq == 0 || chartK == null ? site.O : chartK.O;
            return Decide(chartSeq, camK, o, camSite, treadmillOn);
        }

        /// <summary>
 /// "chart.step" from SN positions: <paramref name="camSnK"/> under the current chart (whose anchor's SN position is
 /// <paramref name="anchorSnK"/>), <paramref name="camSnSite"/> under the site map v1.
 /// <list type="bullet">
 /// <item>chart 0, TreadmillOn only: site ρ∞ &gt; Chart0OutM → chart 1 with T = Step0Batches · sign along the larger of
 /// |x|, |z| (x on a tie);</item>
 /// <item>chart k ≥ 1: site ρ∞ ≤ ReturnM (or TreadmillOn off) → back to chart 0; |x| &gt; BoxOuterM → T = StepBatches ·
 /// sign(x) along x, likewise z (both when both); |x| &lt; BoxInnerM and |z| &lt; BoxInnerM → T = −StepBatches · sign
 /// along the larger axis; arc distance from the anchor &gt; AnchorMaxM → T = 0 (re-centre); one step per tick.</item>
 /// </list>
 /// </summary>
        public static S8aChartDecision Decide(uint chartSeq, Vec3 camSnK, Vec3 anchorSnK, Vec3 camSnSite, bool treadmillOn)
        {
            var d = new S8aChartDecision();
            double siteRho = Math.Max(Math.Abs(camSnSite.X), Math.Abs(camSnSite.Z));
            if (chartSeq == ChartC.Site)
            {
                if (!treadmillOn || !(siteRho > ChartC.Chart0OutM)) return d;
                d.Step = true;
                d.Reason = "site-out";
                if (Math.Abs(camSnSite.X) >= Math.Abs(camSnSite.Z)) d.Tx = ChartC.Step0Batches * Sign(camSnSite.X);
                else d.Tz = ChartC.Step0Batches * Sign(camSnSite.Z);
                return d;
            }
            if (!treadmillOn)
            {
                d.ToSite = true;
                d.Reason = "treadmill-off";
                return d;
            }
            if (siteRho <= ChartC.ReturnM)
            {
                d.ToSite = true;
                d.Reason = "return";
                return d;
            }
            double x = camSnK.X, z = camSnK.Z, ax = Math.Abs(x), az = Math.Abs(z);
            if (ax > ChartC.BoxOuterM || az > ChartC.BoxOuterM)
            {
                d.Step = true;
                if (ax > ChartC.BoxOuterM) d.Tx = ChartC.StepBatches * Sign(x);
                if (az > ChartC.BoxOuterM) d.Tz = ChartC.StepBatches * Sign(z);
                d.Reason = d.Tx != 0 && d.Tz != 0 ? "outer-xz" : d.Tx != 0 ? "outer-x" : "outer-z";
                return d;
            }
            if (ax < ChartC.BoxInnerM && az < ChartC.BoxInnerM)
            {
                d.Step = true;
                d.Reason = "inner";
                if (ax >= az) d.Tx = -ChartC.StepBatches * Sign(x);
                else d.Tz = -ChartC.StepBatches * Sign(z);
                return d;
            }
            double dx = x - anchorSnK.X, dz = z - anchorSnK.Z;
            if (Math.Sqrt(dx * dx + dz * dz) > ChartC.AnchorMaxM)
            {
                d.Step = true;
                d.Reason = "anchor";
                return d;
            }
            return d;
        }

        private static int Sign(double v) { return v < 0 ? -1 : 1; }

        /// <summary>"store.key": the chart key of a map and a sculpt hash (<see cref="ChartKeyText(Vec3, Vec3, double, Vec3, ulong)"/>).</summary>
        public static ulong ChartKey(SiteMap m, ulong sculptHash)
        {
            if (m == null) throw new ArgumentNullException("m");
            return ChartKey(m.U0, m.N0, m.Psi * (180.0 / Math.PI), m.O, sculptHash);
        }

        public static ulong ChartKey(Vec3 u0, Vec3 n0, double psiDeg, Vec3 o, ulong sculptHash)
        {
            return S8aStore.Fnv1a64Utf8(ChartKeyText(u0, n0, psiDeg, o, sculptHash));
        }

        /// <summary>
 /// <c>"chart|" + U0.x|U0.y|U0.z|N0.x|N0.y|N0.z|psiDeg|O.x|O.y|O.z + "|" + sculptHash x16</c>, each double as
 /// <see cref="R(double)"/>.
 /// </summary>
        public static string ChartKeyText(Vec3 u0, Vec3 n0, double psiDeg, Vec3 o, ulong sculptHash)
        {
            var sb = new StringBuilder(256);
            sb.Append("chart|");
            sb.Append(R(u0.X)).Append('|').Append(R(u0.Y)).Append('|').Append(R(u0.Z)).Append('|');
            sb.Append(R(n0.X)).Append('|').Append(R(n0.Y)).Append('|').Append(R(n0.Z)).Append('|');
            sb.Append(R(psiDeg)).Append('|');
            sb.Append(R(o.X)).Append('|').Append(R(o.Y)).Append('|').Append(R(o.Z)).Append('|');
            sb.Append(sculptHash.ToString("x16", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>
 /// The round-trip text of a double, deterministic on every runtime: "G15" when it parses back to the same value,
 /// else "G17" (the documented "R" rule); zero (either sign) is "0"; NaN "NaN"; infinities "Infinity"/"-Infinity".
 /// </summary>
        public static string R(double v)
        {
            if (v == 0.0) return "0";
            if (double.IsNaN(v)) return "NaN";
            if (double.IsPositiveInfinity(v)) return "Infinity";
            if (double.IsNegativeInfinity(v)) return "-Infinity";
            string s = v.ToString("G15", CultureInfo.InvariantCulture);
            if (double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture) != v) s = v.ToString("G17", CultureInfo.InvariantCulture);
            return s;
        }
    }
}
