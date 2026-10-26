using System.Collections.Generic;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Seeded failures of slice S3 "First block" that belong to the SE producer (ROADMAP S3 "Seeded failures", §3 rule
 /// 1). Dev builds only; switched through <see cref="Mutants.Set"/> (environment variable <c>DEEPENGINEERS_MUTANTS</c>
 /// at Init, C11 <c>de.mutant</c>, or <c>/de mutant</c>). Game-independent, so tests/DeepEngineers.SE.Tests can turn
 /// it on.
 /// <list type="bullet">
 /// <item>S3.M4 block drawn from the previous snapshot (one-tick lag): the whole snapshot is one tick late, as the S3a
 /// stand-in does it (tests/standins/fake_se_producer.py --s3-mutant M4): C1 through S1.M2's path (the camera of the
 /// previous tick, published from the plugin Update) and every C2 entity record written at tick t carrying the pose of
 /// tick t − 1 (the tick stamp stays t), while the s3.gps marker rows keep the current camera (S3Patches). S3.N2m must
 /// go red. (A lagged C2 pose alone could not show: the S3 grid is static and N2 only turns the camera.) Switching S3.M4
 /// off also switches S1.M2 off.</item>
 /// </list>
 /// S3.M1 (Z mirror dropped), S3.M2 (triangle swap dropped), S3.M3 (SkyApplier), S3.M5 (capture reuse), S3.M6
 /// (SceneCleanerPreserve), S3.M7 (rigid shear child) and S3.M8 (sRGB normal target) are Subnautica-side mutants: the SE
 /// producer sends SE-space data unchanged (CONTRACT §2.5: the SN plugin mirrors).
 /// </summary>
    internal static class S3Mutants
    {
        /// <summary>S3.M4: the C2 pose lags one tick behind its tick stamp.</summary>
        public static volatile bool PoseOneTickLag;

        public static string Active
        {
            get
            {
                var parts = new List<string>(1);
                if (PoseOneTickLag) parts.Add("S3.M4");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            PoseOneTickLag = false;
        }

        /// <summary>Switches an S3 mutant; returns the log text (starting with <c>S3.</c>), or null when not an SE-side S3 id.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S3.M4":
                    PoseOneTickLag = on;
                    S1Mutants.CameraFromPluginUpdate = on;
                    return "S3.M4 " + state + " (the snapshot one tick late: C1 camera through S1.M2's path and the C2 grid pose of the previous tick, " +
                           "stamped with the current tick; the s3.gps marker rows keep the current camera)";
                case "S3.M1":
                case "S3.M2":
                case "S3.M3":
                case "S3.M5":
                case "S3.M6":
                case "S3.M7":
                case "S3.M8":
                    return "REFUSE " + id.Trim() + ": a Subnautica-side S3 mutant (send de.mutant to Subnautica: /de sn mutant ...)";
                default:
                    return null;
            }
        }
    }
}
