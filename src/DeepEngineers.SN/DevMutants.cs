using System;
using System.Collections.Generic;

namespace DeepEngineers.SN
{
    /// <summary>
 /// Seeded failures of the Subnautica side: the in-game WM-1 box check (CONTRACT §2.9 "mutants that must fail") and,
 /// from S1, the ROADMAP S1 mutants by id (S1.M4 save guards off, S1.M6 PDACameraFOVControl left on, S1.M7 hide phase
 /// None, S1.M8 run in background not enforced, S1.M10 Player never moved) and, from S2, S2.M2 (input silencer off +
 /// IgnoreFocus), S2.M3 (above-water mute skipped), S2.M5 (GetBiome patch off), S2.M6 (Idle recipe off), S2.M12
 /// (freeze pair per cause) and S2.M14 (vSyncCount 1 in Idle); the S2 ones take effect at the next frame (S2.M12 from
 /// the next freeze change); from S3, S3.M1 (Z mirror dropped), S3.M2 (triangle swap dropped), S3.M3 (no SkyApplier, a
 /// fresh property block set), S3.M4 (previous C2 snapshot), S3.M5 (capture file reused), S3.M6 (mirror roots
 /// preserved), S3.M7 (rigid shear test) and S3.M8 (_ng through an sRGB target), applied by the S3 host at the next
 /// frame (meshes rebuilt, renderers or textures re-made as needed). Dev builds only, off by default. Switched
 /// on at load by DEEPENGINEERS_MUTANT (comma-separated names; DEEPENGINEERS_MUTANTS, the SE side's variable, is read
 /// too, for the names this side knows) or later by the C11 command `de.mutant &lt;name&gt; on|off` (the WM-1 ones take
 /// effect at the next `de.selftest`, S1.M6/M10 at the ghost set-up, the others at once). Every mutant that is on is
 /// logged at Warning level; the WM-1 ones are also named in the DE.WireSelfTest line.
 /// </summary>
    internal static class DevMutants
    {
        /// <summary>Physics.SyncTransforms skipped: the box check after the 30° re-pose reads the stale pose.</summary>
        public const string SkipSyncTransforms = "sn.skipSyncTransforms";

        /// <summary>Colliders parented under the scaled Frame (unscaled boxes, sheared parent): the box check fails.</summary>
        public const string CollidersUnderFrame = "sn.collidersUnderFrame";

        // S1 seeded failures (ROADMAP S1 "Seeded failures"; ids as in the ROADMAP).
 /// <summary>S1.M4: both save guards let the original run (throwaway slot copy only, ROADMAP §3 rule 10).</summary>
        public const string S1SaveGuardsOff = "S1.M4";
        /// <summary>S1.M6: PDACameraFOVControl left enabled (the FOV drifts back to the options value).</summary>
        public const string S1FovControlLeftOn = "S1.M6";
        /// <summary>S1.M7: hide phase left at None (Subnautica's HUD stays visible).</summary>
        public const string S1HidePhaseNone = "S1.M7";
        /// <summary>S1.M8: "Run in background" not enforced.</summary>
        public const string S1NoRunInBackground = "S1.M8";
        /// <summary>S1.M10: the Player root is left where EnterLockedMode leaves it (never moved, lifepod state kept).</summary>
        public const string S1PlayerNotMoved = "S1.M10";

        // S2 seeded failures of the Subnautica side (ROADMAP S2 "Seeded failures"; ids as in the ROADMAP).
 /// <summary>S2.M2: the GameInput.ClearInput silencer off and InputSystem backgroundBehavior = IgnoreFocus.</summary>
        public const string S2InputSilencerOff = "S2.M2";
        /// <summary>S2.M3: the above-water mute skipped (C5 snBusTarget 0 taken as 1).</summary>
        public const string S2AboveWaterMuteSkipped = "S2.M3";
        /// <summary>S2.M5: the LargeWorld.GetBiome patch off (outside the tile: the wrap or null again).</summary>
        public const string S2BiomePatchOff = "S2.M5";
        /// <summary>S2.M6: the Idle recipe off (no freeze for Idle, cameras on, the user's frame rate, Normal priority).</summary>
        public const string S2IdleRecipeOff = "S2.M6";
        /// <summary>S2.</summary>
        public const string S2FreezePerCause = "S2.M12";
        /// <summary>S2.M14: vSyncCount left at 1 in Idle (targetFrameRate 10 then ignored by Unity).</summary>
        public const string S2VSyncLeftInIdle = "S2.M14";

        // S3 seeded failures of the Subnautica side (ROADMAP S3 "Seeded failures"; ids as in the ROADMAP).
 /// <summary>S3.M1: entity-local data not mirrored (no z negation on positions, normals, tangents, part matrices).</summary>
        public const string S3ZMirrorDropped = "S3.M1";
        /// <summary>S3.M2: triangles kept as (I0, I1, I2) (the (I0, I2, I1) rewrite dropped).</summary>
        public const string S3TriSwapDropped = "S3.M2";
        /// <summary>S3.M3: no SkyApplier on the mirror renderers, and a fresh MaterialPropertyBlock set on them.</summary>
        public const string S3NoSkyApplier = "S3.M3";
        /// <summary>S3.M4: entity poses taken from the C2 snapshot before the camera's newer bracket tick (one-tick lag).</summary>
        public const string S3PreviousSnapshot = "S3.M4";
        /// <summary>S3.M5: a repeated lab capture copies the previous PNG instead of capturing a new frame.</summary>
        public const string S3CaptureReused = "S3.M5";
        /// <summary>S3.M6: mirror roots given SceneCleanerPreserve (and DontDestroyOnLoad): they survive quit-to-menu.</summary>
        public const string S3RootPreserved = "S3.M6";
        /// <summary>S3.M7: the shear test (G1-E2) draws with a rigid Frame (scale 1) while the emulation keeps s.</summary>
        public const string S3RigidShear = "S3.M7";
        /// <summary>S3.M8: the _ng conversion blits into an sRGB render target (normals re-encoded).</summary>
        public const string S3NormalSrgbTarget = "S3.M8";

        // S5 seeded failures of the Subnautica side (ROADMAP S5 "Seeded failures" and "Builder contract"; ids as in the ROADMAP).
 /// <summary>S5.M1: roots and C2 subpart rows sampled at the snapshot before the bracket's newer tick (grids one tick behind the camera).</summary>
        public const string S5PreviousSnapshot = "S5.M1";
        /// <summary>S5.M2: the mesh build pass per-model flip skipped (the winding measure is still logged).</summary>
        public const string S5FlipOff = "S5.M2";
        /// <summary>S5.M4: the perf recipes use Full instead of Idle in Space.</summary>
        public const string S5FullInSpace = "S5.M4";
        /// <summary>S5.M5: the perf recipes keep the cameras on in Track.</summary>
        public const string S5CamerasOnInTrack = "S5.M5";

        // S7a seeded failures of the Subnautica side (ROADMAP S7a "Seeded failures"): M5 and M6 are the astronaut/sun
 // build pass (S7aAnimRules, DayNightApply); M2 (camera parked by view as S2) is the policy build pass S2Rules rule,
 // registered here by its id so `de.mutant S7a.
 /// <summary>S7a.M5: the out-of-water <c>move_speed*</c> forced to 0 (the walk shows the idle clip).</summary>
        public const string S7aMoveSpeedZero = "S7a.M5";
        /// <summary>S7a.M6: the time drive writes <c>timePassedAsDouble</c> straight from the target (backward allowed).</summary>
        public const string S7aTimeDirect = "S7a.M6";

        public const string EnvVar = "DEEPENGINEERS_MUTANT";
        /// <summary>The SE plugin's spelling (DEEPENGINEERS_MUTANTS); accepted too, so one variable can switch both sides.</summary>
        public const string EnvVarSe = "DEEPENGINEERS_MUTANTS";

        private static readonly string[] KnownNames =
        {
            SkipSyncTransforms, CollidersUnderFrame,
            S1SaveGuardsOff, S1FovControlLeftOn, S1HidePhaseNone, S1NoRunInBackground, S1PlayerNotMoved,
            S2InputSilencerOff, S2AboveWaterMuteSkipped, S2BiomePatchOff, S2IdleRecipeOff, S2FreezePerCause, S2VSyncLeftInIdle,
            S3ZMirrorDropped, S3TriSwapDropped, S3NoSkyApplier, S3PreviousSnapshot, S3CaptureReused, S3RootPreserved, S3RigidShear,
            S3NormalSrgbTarget,
            S5PreviousSnapshot, S5FlipOff, S5FullInSpace, S5CamerasOnInTrack,
            "S7a.M2", S7aMoveSpeedZero, S7aTimeDirect,
        };
        private static readonly HashSet<string> On = new HashSet<string>(StringComparer.Ordinal);

        public static bool IsOn(string name) { return BuildFlavor.Dev && On.Contains(name); }

        public static bool Any { get { return BuildFlavor.Dev && On.Count > 0; } }

        public static bool IsKnown(string name)
        {
            return Array.IndexOf(KnownNames, name) >= 0 || Array.IndexOf(S6Mutants.All, name) >= 0 // S6:
                || Array.IndexOf(S7Mutants.All, name) >= 0 // S7: S7Mutants.cs
                || Array.IndexOf(S8aMutants.All, name) >= 0 // S8a: S8aMutants.cs (M4, M7, M11)
                || Array.IndexOf(S11Mutants.All, name) >= 0 // S11 hook: (M1-M10)
                || Array.IndexOf(S16SnMutants.All, name) >= 0 // S16 hook: (M7)
                || Array.IndexOf(S13SnMutants.All, name) >= 0 // S13 hook: (M1, M2, M4, M6, M7, M8)
                || Array.IndexOf(S14SnMutants.All, name) >= 0 // S14 hook: (M4, M5, M6)
                || Array.IndexOf(S15SnMutants.All, name) >= 0 // S15 hook: (M5, M6, M7)
                || Array.IndexOf(S12SnMutants.All, name) >= 0; // S12 hook: (M6, M7)
        }

        public static bool Set(string name, bool on, out string error)
        {
            error = null;
            if (!BuildFlavor.Dev) { error = "mutants are disabled in release builds"; return false; }
            if (!IsKnown(name)) { error = "unknown mutant '" + name + "' (known: " + string.Join(", ", KnownNames) + ", " + string.Join(", ", S6Mutants.All) + ", " + string.Join(", ", S7Mutants.All) + ", " + string.Join(", ", S8aMutants.All) + ", " + string.Join(", ", S11Mutants.All) + ")"; return false; }
            if (on) On.Add(name); else On.Remove(name);
            DeLog.Warn("DE.Mutant " + name + (on ? " ON" : " OFF"));
            S7Mutants.OnSet(name, on); // S7.M2 = S7a.M6 (S7Mutants.cs)
            return true;
        }

        public static void LoadFromEnvironment()
        {
            if (!BuildFlavor.Dev) return;
            LoadFrom(EnvVar);
            LoadFrom(EnvVarSe);
        }

        private static void LoadFrom(string variable)
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable(variable); } catch (Exception) { }
            if (string.IsNullOrEmpty(v)) return;
            foreach (string part in v.Split(','))
            {
                string name = part.Trim();
                if (name.Length == 0) continue;
                // the SE side's names (S0.M5a...) may share the variable: only names this side knows are applied
                if (variable == EnvVarSe && !IsKnown(name)) continue;
                string error;
                if (!Set(name, true, out error)) DeLog.Warn("DE.Mutant ignored from " + variable + ": " + error);
            }
        }

        /// <summary>Names of the mutants that are on, comma-separated ("none" when none).</summary>
        public static string Describe()
        {
            if (!Any) return "none";
            var list = new List<string>(On);
            list.Sort(StringComparer.Ordinal);
            return string.Join(",", list.ToArray());
        }
    }
}
