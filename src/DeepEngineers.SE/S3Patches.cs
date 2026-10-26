using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Harmony patch of slice S3 (same id <c>deepengineers.se</c>): a second postfix on
 /// <c>Sandbox.MySandboxGame.PrepareForDraw</c> (<c>protected override void</c>, instance, no parameters, declared on
 /// <c>MySandboxGame</c>; the target S1Patches verifies, looked up again here with explicit parameter types and its
 /// shape checked before patching). It runs after the S1 C1 writer's postfix (registered later, same priority), where
 /// SE's camera of this frame is final, and writes the S3.N2 <c>s3.gps</c> rows (<see cref="GridMirrorProducer.OnPrepareForDraw"/>)
 /// from the current camera even while S3.M4 (with S1.M2's path) publishes C1 one tick late. A missing or changed
 /// target logs <c>DE.Harmony MISSING</c> and disables only the s3.gps rows.
 /// </summary>
    internal static class S3Patches
    {
        public const string PrepareForDrawName = "MySandboxGame.PrepareForDraw()";

        public static bool Applied { get; private set; }

        /// <summary>Called from the plugin's Harmony bootstrap inside its own try/catch.</summary>
        public static void Apply()
        {
            var harmony = new Harmony(HarmonyPatches.HarmonyId);
            MethodInfo target = AccessTools.Method(typeof(MySandboxGame), "PrepareForDraw", Type.EmptyTypes);
            if (target == null || target.IsStatic || target.ReturnType != typeof(void) || target.GetParameters().Length != 0 ||
                target.DeclaringType != typeof(MySandboxGame))
            {
                DeLog.Info("DE.Harmony MISSING " + PrepareForDrawName + " for S3 (" + (target == null ? "not found" : "shape changed") +
                           "): the s3.gps rows of S3.N2 are off; the S3 streams are unaffected");
                return;
            }
            try
            {
                MethodInfo post = typeof(S3Patches).GetMethod(nameof(PrepareForDrawPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(target, prefix: null, postfix: new HarmonyMethod(post), transpiler: null, finalizer: null);
                Applied = true;
                Patches info = Harmony.GetPatchInfo(target);
                int ours = info == null ? 0 : info.Postfixes.Count(p => p.owner == HarmonyPatches.HarmonyId);
                DeLog.Info("DE.Harmony S3 OK " + PrepareForDrawName + " postfix (s3.gps rows from this frame's camera; " + ours + " deepengineers.se postfixes on the target)");
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Harmony MISSING " + PrepareForDrawName + " S3 patch failed: " + e.GetType().Name + ": " + e.Message + " (s3.gps rows off)");
            }
        }

        // Never throws: an exception here would break SE's frame.
        private static void PrepareForDrawPostfix()
        {
            try { GridMirrorProducer.OnPrepareForDraw(); }
            catch (Exception) { }
        }
    }
}
