using System;
using System.Globalization;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7t: the ONE source of the

 // and heading; mean sRGB of the same screen regions). Every side reads its targets from here:
 // - SE look match (LookMatchRules "fogColor" default) = Subnautica's sky at the horizon;
 // - Subnautica's horizon skirt (AltitudeRules HorizonRules.DefaultHorizonRgb / DefaultLowRgb) = Subnautica's sky at
 // the horizon and its far water;
 // - tests/s7t/band_compare.py TARGETS (the oracle's --targets table) is the Python twin, pinned against this class by
 // Wire.Tests S7tBandTargetsTests (the test reads the.py file).
 // Pure data, no game or Unity type.
 // =================================================================================================================

 /// <summary>One measured sRGB colour (0..255 per channel).</summary>
    public struct BandRgb : IEquatable<BandRgb>
    {
        public readonly byte R, G, B;

        public BandRgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        /// <summary>The channel as a 0..1 sRGB fraction (R = 0, G = 1, B = 2).</summary>
        public double Unit(int channel)
        {
            return (channel == 0 ? R : channel == 1 ? G : B) / 255.0;
        }

        public bool Equals(BandRgb o) { return R == o.R && G == o.G && B == o.B; }
        public override bool Equals(object o) { return o is BandRgb && Equals((BandRgb)o); }
        public override int GetHashCode() { return (R << 16) | (G << 8) | B; }
        public override string ToString() { return R.ToString(CultureInfo.InvariantCulture) + "," + G.ToString(CultureInfo.InvariantCulture) + "," + B.ToString(CultureInfo.InvariantCulture); }
    }

    /// <summary>

 /// screen bands of each game. Subnautica's palette is the reference; SE's numbers are where SE started (before EarthLike's
 /// atmosphere, deployed 23:17, and before T4/T5).
 /// </summary>
    public static class S7tBandTargets
    {
        /// <summary>Sky at the top of the frame: SE (135,221,235) L204, Subnautica (123,176,234) L169.</summary>
        public static readonly BandRgb SeSkyTop = new BandRgb(135, 221, 235), SnSkyTop = new BandRgb(123, 176, 234);
        /// <summary>Sky at the horizon: SE (153,234,246) L218, Subnautica (197,241,255) L233 (Subnautica's white haze band).</summary>
        public static readonly BandRgb SeSkyHorizon = new BandRgb(153, 234, 246), SnSkyHorizon = new BandRgb(197, 241, 255);
        /// <summary>Far water: SE (38,72,96) L67, Subnautica (129,155,187) L152 (Subnautica's aerial perspective).</summary>
        public static readonly BandRgb SeFarWater = new BandRgb(38, 72, 96), SnFarWater = new BandRgb(129, 155, 187);
        /// <summary>Near water: SE (27,68,104) L62, Subnautica (65,97,122) L92.</summary>
        public static readonly BandRgb SeNearWater = new BandRgb(27, 68, 104), SnNearWater = new BandRgb(65, 97, 122);

        /// <summary>The haze colour both sides aim at the horizon (SE's fog colour, Subnautica's skirt at the horizon).</summary>
        public static BandRgb HazeHorizon { get { return SnSkyHorizon; } }
        /// <summary>The colour Subnautica's far sea fades from (the skirt's low colour).</summary>
        public static BandRgb HazeLow { get { return SnFarWater; } }

        /// <summary>The band names of tests/s7t/band_compare.py, in its order.</summary>
        public static readonly string[] BandNames = { "sky_top", "sky_horizon", "far_water", "near_water" };

        /// <summary>The measured colour of a band (<see cref="BandNames"/>) on one side ("se" or "sn"); false for an unknown name.</summary>
        public static bool TryGet(string side, string band, out BandRgb c)
        {
            c = default(BandRgb);
            bool sn;
            if (string.Equals(side, "sn", StringComparison.OrdinalIgnoreCase)) sn = true;
            else if (string.Equals(side, "se", StringComparison.OrdinalIgnoreCase)) sn = false;
            else return false;
            switch (band)
            {
                case "sky_top": c = sn ? SnSkyTop : SeSkyTop; return true;
                case "sky_horizon": c = sn ? SnSkyHorizon : SeSkyHorizon; return true;
                case "far_water": c = sn ? SnFarWater : SeFarWater; return true;
                case "near_water": c = sn ? SnNearWater : SeNearWater; return true;
                default: return false;
            }
        }
    }
}
