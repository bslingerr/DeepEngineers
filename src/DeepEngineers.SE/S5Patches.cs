using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Harmony patches of slice S5 (same id <c>deepengineers.se</c>), each looked up with explicit parameter types and its
 /// shape checked before patching; a missing or changed target logs <c>DE.Harmony MISSING</c> and leaves only its marks
 /// off (the exporter's round-robin sweep still finds every edit, later):
 /// <list type="bullet">
 /// <item>postfix on <c>Sandbox.Game.Entities.Cube.MyCubeGridRenderCell.RebuildInstanceParts(RenderFlags)</c>
 /// the cell is queued (a lock-free queue, any thread) and diffed on the update thread;</item>
 /// <item>postfix on <c>Sandbox.Game.Entities.MyCubeGrid.ChangeColorAndSkin(MySlimBlock, Vector3?, MyStringHash?)</c>
 /// the block's fat part and subparts are
 /// read again (armor cells follow through the cell rebuild it triggers).</item>
 /// </list>
 /// </summary>
    internal static class S5Patches
    {
        public const string CellTarget = "MyCubeGridRenderCell.RebuildInstanceParts(RenderFlags)";
        public const string PaintTarget = "MyCubeGrid.ChangeColorAndSkin(MySlimBlock, Vector3?, MyStringHash?)";

        public static bool CellApplied { get; private set; }
        public static bool PaintApplied { get; private set; }

        /// <summary>Called from the plugin's Harmony bootstrap inside its own try/catch.</summary>
        public static void Apply()
        {
            var harmony = new Harmony(HarmonyPatches.HarmonyId);
            MethodInfo cell = AccessTools.Method(typeof(MyCubeGridRenderCell), "RebuildInstanceParts", new[] { typeof(RenderFlags) });
            if (cell == null || cell.IsStatic || cell.ReturnType != typeof(void) || cell.DeclaringType != typeof(MyCubeGridRenderCell))
                DeLog.Info("DE.Harmony MISSING " + CellTarget + " for S5 (" + (cell == null ? "not found" : "shape changed") + "): armor cells are found by the sweep only");
            else
            {
                try
                {
                    harmony.Patch(cell, postfix: new HarmonyMethod(typeof(S5Patches).GetMethod(nameof(CellPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    CellApplied = true;
                    DeLog.Info("DE.Harmony S5 OK " + CellTarget + " postfix (dirty render cells → the S5 exporter)");
                }
                catch (Exception e) { DeLog.Info("DE.Harmony MISSING " + CellTarget + " S5 patch failed: " + e.GetType().Name + ": " + e.Message); }
            }
            MethodInfo paint = AccessTools.Method(typeof(MyCubeGrid), "ChangeColorAndSkin", new[] { typeof(MySlimBlock), typeof(Vector3?), typeof(MyStringHash?) });
            ParameterInfo[] ps = paint != null ? paint.GetParameters() : null;
            if (paint == null || paint.IsStatic || paint.ReturnType != typeof(bool) || ps == null || ps.Length != 3 || ps[0].Name != "block")
                DeLog.Info("DE.Harmony MISSING " + PaintTarget + " for S5 (" + (paint == null ? "not found" : "shape changed") + "): fat-block paint is found by the sweep only");
            else
            {
                try
                {
                    harmony.Patch(paint, postfix: new HarmonyMethod(typeof(S5Patches).GetMethod(nameof(PaintPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    PaintApplied = true;
                    Patches info = Harmony.GetPatchInfo(paint);
                    int ours = info == null ? 0 : info.Postfixes.Count(p => p.owner == HarmonyPatches.HarmonyId);
                    DeLog.Info("DE.Harmony S5 OK " + PaintTarget + " postfix (" + ours + " deepengineers.se postfix(es))");
                }
                catch (Exception e) { DeLog.Info("DE.Harmony MISSING " + PaintTarget + " S5 patch failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }

        // Never throw: an exception here would break SE's update.
        private static void CellPostfix(MyCubeGridRenderCell __instance)
        {
            try { GridMirrorProducer.MarkCellFromHook(__instance); }
            catch (Exception) { }
        }

        private static void PaintPostfix(MySlimBlock block)
        {
            try { GridMirrorProducer.MarkBlockFromHook(block); }
            catch (Exception) { }
        }
    }
}
