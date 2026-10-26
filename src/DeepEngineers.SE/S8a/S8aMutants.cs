using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Seeded failures of slice S8a on the SE side (ROADMAP "### S8a" "Seeded failures"; build pass contract: "SE
 /// (every SE flag of the table)"). Dev builds only; switched through <see cref="Mutants.Set"/>
 /// (environment variable <c>DEEPENGINEERS_MUTANTS</c> at Init, C11 <c>de.mutant</c>, or <c>/de mutant</c>).
 /// Game-independent (plain flags), so tests/DeepEngineers.SE.Tests can turn them on. Each flag is read by the code
 /// that owns the feature it breaks:
 /// <list type="bullet">
 /// <item>S8a.M1 drill notices suppressed, files still written (<c>S8aDrillConverter</c>): S8a.N1.latency, N1.visible, N3.rays red;</item>
 /// <item>S8a.M2 the converter samples at the voxel corner, no + 0.5 (<c>S8aFieldSampler.SnOffset</c> 0): S8a.N2.position, N3.rays red;</item>
 /// <item>S8a.M3 the overlay is not added to the save snapshot (<c>S8aOverlay</c> save postfix): S8a.N4.save, N4.file red;</item>
 /// <item>S8a.M5 the drill store is written under the SE world folder (<c>S8aDrillConverter</c> root): S8a.N6.writes red;</item>
 /// <item>S8a.M6 the window is generated from <c>MyPlanet.Provider</c> instead of the storage (S8a-2 window worker): S8a.N7.edit red;</item>
 /// <item>S8a.M8 chart step T = the exact overshoot, not whole batches (S8a-2 chart manager): S8a.N8.batchstep red;</item>
 /// <item>S8a.M9 <c>NearPending</c> never set (S8a-2 window): S8a.N9.gate red;</item>
 /// <item>S8a.M10 the material table bypassed, every type 1 (S8a-2 window): S8a.N10.material red;</item>
 /// <item>S8a.M12 the S4 guards kept on the tiles while <c>DrillOn</c> (<c>S8aOverlay.AllowsEdit</c>): S8a.N1.edit red;</item>
 /// <item>S8a.M13 interior nodes = child 0's value (Wire <see cref="S8aWireMutants"/>; forwarded from here): S8a.C1 red (offline);</item>
 /// <item>S8a.M14 edits not mirrored into overlapping tiles (<c>S8aOverlay</c>): S8a.N2.overlap red;</item>
 /// <item>S8a.M23 (planet v2, ores you can see) the outcrop anchors written 8 m off along SN x (<c>S8aOutcropsSe</c>): S8a.NO.agree red;</item>
 /// <item>S8a.M27 (planet v2 R8, the ore density) the crater's cube rule on whatever the planet rules say (<c>S8aHost.ApplyPlanetRules</c>):
 /// S8a.N10.oredensity red under rules that switch it off (design 5); under design 3's defaults nothing changes;</item>
 /// <item>S8a.M15 (Stage F2, the fallback exercise, not a red mutant) the ring field fails on the reads that hold the <c>de.s8a ring
 /// {failAt}</c> column (<c>S8aRing</c>): SE's own heightmap serves them; S8a.NF.fallback judges that column (no hole, no seam).</item>
 /// </list>
 /// The Subnautica-side mutants (M4, M7, M11) are DeepEngineers.SN's <c>S8aMutants</c>.
 /// </summary>
    internal static class S8aMutants
    {
        public static volatile bool M1, M2, M3, M5, M6, M8, M9, M10, M12, M14, M15, M23, M27;

        /// <summary>The ids this side owns, in order (M13 is the Wire core's, switched through here).</summary>
        public static readonly string[] All = { "S8a.M1", "S8a.M2", "S8a.M3", "S8a.M5", "S8a.M6", "S8a.M8", "S8a.M9", "S8a.M10", "S8a.M12", "S8a.M13", "S8a.M14", "S8a.M15", "S8a.M23", "S8a.M27", "S8a.M30", "S8a.M31" };

        public static string Active
        {
            get
            {
                var parts = new List<string>(4);
                if (M1) parts.Add("S8a.M1");
                if (M2) parts.Add("S8a.M2");
                if (M3) parts.Add("S8a.M3");
                if (M5) parts.Add("S8a.M5");
                if (M6) parts.Add("S8a.M6");
                if (M8) parts.Add("S8a.M8");
                if (M9) parts.Add("S8a.M9");
                if (M10) parts.Add("S8a.M10");
                if (M12) parts.Add("S8a.M12");
                if (S8aWireMutants.M13Child0Interior) parts.Add("S8a.M13");
                if (M14) parts.Add("S8a.M14");
                if (M15) parts.Add("S8a.M15");
                if (M23) parts.Add("S8a.M23");
                if (M27) parts.Add("S8a.M27");
                if (S8aWireMutants.M30OresAsRock) parts.Add("S8a.M30");
                if (S8aWireMutants.M31OverlayOff) parts.Add("S8a.M31");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            M1 = M2 = M3 = M5 = M6 = M8 = M9 = M10 = M12 = M14 = M15 = M23 = M27 = false;
            S8aWireMutants.ClearAll();
        }

        /// <summary>Switches an SE-side S8a mutant; returns the log text (starting with <c>S8a.</c>), or null when not one of this side's ids.</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S8A.M1": M1 = on; return "S8a.M1 " + state + " (drill notices suppressed; the store files are still written)";
                case "S8A.M2": M2 = on; return "S8a.M2 " + state + " (the converter samples at the voxel corner: no + 0.5)";
                case "S8A.M3": M3 = on; return "S8a.M3 " + state + " (the overlay is not added to the save snapshot)";
                case "S8A.M5": M5 = on; return "S8a.M5 " + state + " (the drill store is written under the SE world folder; takes effect at the next world load)";
                case "S8A.M6": M6 = on; return "S8a.M6 " + state + " (the window is generated from MyPlanet.Provider, not the storage; S8a-2)";
                case "S8A.M8": M8 = on; return "S8a.M8 " + state + " (chart step T = the exact overshoot, not whole batches; S8a-2)";
                case "S8A.M9": M9 = on; return "S8a.M9 " + state + " (NearPending never set; S8a-2)";
                case "S8A.M10": M10 = on; return "S8a.M10 " + state + " (material table bypassed: every type 1; S8a-2)";
                case "S8A.M12": M12 = on; return "S8a.M12 " + state + " (the S4 guards keep blocking the tiles while DrillOn)";
                case "S8A.M13": S8aWireMutants.Set("S8a.M13", on); return "S8a.M13 " + state + " (interior octree nodes take child 0's value: no CacheDownsampledType)";
                case "S8A.M30": S8aWireMutants.Set("S8a.M30", on); return "S8a.M30 " + state + " (every ore voxel written as plain rock: NO.voxel and NO.look red)";
                case "S8A.M31": S8aWireMutants.Set("S8a.M31", on); return "S8a.M31 " + state + " (the crater's ore overlay writes nothing: NV.detector and NV.mined red; a batch painted before keeps its paint until passed again)";
                case "S8A.M14": M14 = on; return "S8a.M14 " + state + " (edits not mirrored into overlapping tiles)";
                case "S8A.M15": M15 = on; return "S8a.M15 " + state + " (Stage F2: the ring field fails on reads holding the de.s8a ring failAt column; SE's heightmap serves them)";
                case "S8A.M23": M23 = on; return "S8a.M23 " + state + " (planet v2: the outcrop anchors written 8 m off along SN x)";
                case "S8A.M27": M27 = on; return "S8a.M27 " + state + " (planet v2 R8: the crater's cube rule on whatever the rules say; the crater ores poll applies it within 5 s)";
                default: return null;
            }
        }
    }
}
