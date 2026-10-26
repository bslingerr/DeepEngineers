using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S14 "Audio v2", Subnautica side: the Unity-free part (compiled into tests/DeepEngineers.SN.Tests). The seeded
 // failures of this side: S14.M4 (the listener stays where the scene put it),
 // S14.M5 (no envelope on Subnautica's loop: S7's instant start/stop at full level), S14.M6 (no jet above the water). The
 // envelope itself is Wire's S14EngineEnv (SE's own thruster emitter uses it too), the engine mode is S14Sn's.
 // =================================================================================================================

    internal static class S14SnMutants
    {
        /// <summary>S14.M4: the FMOD listener is left where the scene's StudioListener put it (S14.N4L red).</summary>
        public const string ListenerLeft = "S14.M4";
        /// <summary>S14.M5: the engine loop starts and stops at once at full level (S7's rule; S14.N4E red).</summary>
        public const string NoEnvelope = "S14.M5";
        /// <summary>S14.M6: no Prawn jet on the above-water thruster flames, scene-above's kit puffs back (S14.N5, S14.N5O red).</summary>
        public const string NoJets = "S14.M6";
        public static readonly string[] All = { ListenerLeft, NoEnvelope, NoJets };
    }

    /// <summary>Point 4a: the listener's offset from the camera (m, degrees between the forward vectors).</summary>
    internal static class S14ListenerMath
    {
        public static double AngleDeg(double ax, double ay, double az, double bx, double by, double bz)
        {
            double la = Math.Sqrt(ax * ax + ay * ay + az * az), lb = Math.Sqrt(bx * bx + by * by + bz * bz);
            if (!(la > 1e-9) || !(lb > 1e-9)) return 180.0;
            double c = (ax * bx + ay * by + az * bz) / (la * lb);
            if (c > 1) c = 1;
            if (c < -1) c = -1;
            return Math.Acos(c) * 180.0 / Math.PI;
        }

        public static double Distance(double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = ax - bx, dy = ay - by, dz = az - bz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
