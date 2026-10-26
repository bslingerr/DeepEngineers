using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Harmony patch of slice S1 (ROADMAP §5.4: same id <c>deepengineers.se</c>, Lib.Harmony 2.4.2 from Pulsar). Target
 /// read and looked up again here with explicit parameter types, shape
 /// verified before patching:
 /// <list type="bullet">
 /// <item><c>Sandbox.MySandboxGame.PrepareForDraw</c>: <c>protected override void</c>, instance, no parameters,
 /// declared on <c>MySandboxGame</c> (the abstract slot is
 /// <c>Sandbox.Engine.Platform.Game.PrepareForDraw</c>, called by <c>Game.UpdateInternal</c> right after
 /// <c>Update</c> on non-dedicated builds; <c>SpaceEngineersGame</c> does not override it). Postfix →
 /// <see cref="FrameWriter.Capture"/> (the C1 writer; the camera is final there).</item>
 /// </list>
 /// A missing or changed target logs <c>DE.Harmony MISSING</c>, leaves the frame writer off and fails the start-up
 /// self-check (display bridge refused); nothing throws into the game.
 /// </summary>
    internal static class S1Patches
    {
        public const string PrepareForDrawName = "MySandboxGame.PrepareForDraw()";

        public static bool Applied { get; private set; }
        public static string Missing { get; private set; }

        /// <summary>Called from the plugin's Harmony bootstrap inside a try/catch.</summary>
        public static void Apply()
        {
            var harmony = new Harmony(HarmonyPatches.HarmonyId);
            MethodInfo target = AccessTools.Method(typeof(MySandboxGame), "PrepareForDraw", Type.EmptyTypes);
            if (target == null)
            {
                Missing = PrepareForDrawName + " not found";
                DeLog.Info("DE.Harmony MISSING " + PrepareForDrawName + " (not found in this game build; C1 writer off, display bridge refused)");
                return;
            }
            if (target.IsStatic || target.ReturnType != typeof(void) || target.GetParameters().Length != 0 || target.DeclaringType != typeof(MySandboxGame))
            {
                Missing = PrepareForDrawName + " changed (static=" + target.IsStatic + " returns " + target.ReturnType.Name + " declared on " + target.DeclaringType + ")";
                DeLog.Info("DE.Harmony MISSING " + Missing + " (patch off; C1 writer off, display bridge refused)");
                return;
            }
            string access = target.IsPublic ? "public" : target.IsPrivate ? "private" : target.IsFamily ? "protected" : "internal";
            DeLog.Info("DE.Harmony target " + target.DeclaringType.FullName + "." + target.Name + "() " + access + " instance returns Void (S1 C1 writer)");
            try
            {
                MethodInfo post = typeof(S1Patches).GetMethod(nameof(PrepareForDrawPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(target, prefix: null, postfix: new HarmonyMethod(post), transpiler: null, finalizer: null);
                Applied = true;
                Patches info = Harmony.GetPatchInfo(target);
                string others = info == null ? "" : string.Join(", ", info.Postfixes.Where(p => p.owner != HarmonyPatches.HarmonyId).Select(p => p.owner).ToArray());
                DeLog.Info("DE.Harmony S1 OK 1 target patched: " + PrepareForDrawName + " postfix (C1 SeFrame writer)" +
                           (others.Length > 0 ? "; other postfixes: " + others : ""));
            }
            catch (Exception e)
            {
                Missing = PrepareForDrawName + " patch failed: " + e.GetType().Name;
                DeLog.Info("DE.Harmony MISSING " + PrepareForDrawName + " patch failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Never throws: an exception here would break SE's frame.
        private static void PrepareForDrawPostfix()
        {
            try { FrameWriter.Capture(false); }
            catch (Exception) { }
        }
    }
}
