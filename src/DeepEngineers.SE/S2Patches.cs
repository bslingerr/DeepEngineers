using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using VRage;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Harmony patches of slice S2 (same id <c>deepengineers.se</c>, Lib.Harmony 2.4.2 from Pulsar). Each target was read
 /// in the decompiled SE 1.210.014 sources (path and line next to it below), is looked up again here with explicit
 /// parameter types and its shape checked before patching. A missing or changed target logs <c>DE.Harmony MISSING</c>
 /// once and stays inert; the S2 features that need it are refused (never the S0/S1 link). Every patch body catches
 /// everything: nothing throws into SE's render, sound or save code.
 /// <list type="bullet">
 /// <item>Render (VRage.Render11.dll, internal static class <c>VRageRender.MyRender11</c>,
 /// ): <c>Present</c> internal static void
 /// (postfix: the window work after each present, render thread); <c>FullDraw(bool)</c> private static void
 /// (prefix: frame start; postfix: alpha fill and readback copy after <c>ConsumeMainSprites</c>);
 /// <c>RenderMainSprites(IRtvBindable, MyViewport, MyViewport, Vector2, MyViewport?)</c> internal static void
 /// (prefix: the scene skip, clearing <c>m_drawScene</c> after <c>ProcessMessageQueue</c> set it; the
 /// parameterless overload is three lines and could be inlined into FullDraw, the 5-parameter one starts a
 /// task and is not).</item>
 /// <item>Window (VRage.Platform.Windows.dll, internal sealed class <c>VRage.Platform.Windows.Forms.MyGameWindow</c>,
 /// ):
 /// <c>OnModeChanged(MyWindowModeEnum, int, int, Rectangle)</c> public void
 /// <c>get_DrawEnabled</c> public bool
 /// </item>
 /// <item>Audio (Sandbox.Game.dll): <c>MySandboxGame.UpdateSound</c>
 /// private void (postfix: the mix, after SE's own focus logic; run after the plugins' Update,
 /// usually as a parallel task); <c>MyGuiScreenOptionsAudio.Save</c>
 /// private void (prefix: the volumes it copies into <c>SpaceEngineers.cfg</c> at are reset to the dialog's
 /// own slider values first).</item>
 /// <item>Save (Water Mod fallback only): <c>MySession.Save(out MySessionSnapshot, string, Action&lt;SaveProgress&gt)</c>
 /// public bool (prefix + finalizer).</item>
 /// </list>
 /// The Water Mod's own sound component is patched later, by name, from its in-memory script assembly
 /// (<see cref="WaterModAudio"/>).
 /// </summary>
    internal static class S2Patches
    {
        public static readonly List<string> Applied = new List<string>();
        public static readonly List<string> Missing = new List<string>();

        public static bool PresentOk, FullDrawOk, SpritesOk, ModeChangedOk, DrawEnabledOk, UpdateSoundOk, OptionsSaveOk, SessionSaveOk;
        public static Harmony Harmony;

        /// <summary>Design F needs the present, FullDraw and sprite hooks and the m_drawScene field.</summary>
        public static bool DesignFHooksOk { get { return PresentOk && FullDrawOk && SpritesOk && RenderHooks.DrawSceneField != null; } }
        /// <summary>A′ needs the present hook (window calls) and the DrawEnabled getter.</summary>
        public static bool APrimeHooksOk { get { return PresentOk && DrawEnabledOk; } }

        /// <summary>Called from the plugin's Harmony bootstrap inside a try/catch.</summary>
        public static void Apply()
        {
            Harmony = new Harmony(HarmonyPatches.HarmonyId);

            // ---------------------------------------------------------------- render (VRage.Render11)
            Type r11 = null;
            try { r11 = Type.GetType("VRageRender.MyRender11, VRage.Render11", false); } catch (Exception) { }
            if (r11 == null) Miss("VRageRender.MyRender11 (VRage.Render11) not found: design F refused");
            else
            {
                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo drawScene = r11.GetField("m_drawScene", S);
                if (drawScene == null || drawScene.FieldType != typeof(bool)) Miss("MyRender11.m_drawScene (private static bool) not found");
                else RenderHooks.BindDrawScene(drawScene);
                RenderHooks.DeviceProperty = r11.GetProperty("DeviceInstance", S);
                RenderHooks.BackbufferProperty = r11.GetProperty("Backbuffer", S);
                if (RenderHooks.DeviceProperty == null || RenderHooks.BackbufferProperty == null)
                    DeLog.Info("DE.Harmony note: MyRender11.DeviceInstance/Backbuffer not found: alpha fill and readback unavailable");

                MethodInfo present = r11.GetMethod("Present", S, null, Type.EmptyTypes, null);
                PresentOk = Check("MyRender11.Present()", present, true, typeof(void)) &&
                            Patch("MyRender11.Present()", present, null, Hm(nameof(PresentPostfix)), null);

                MethodInfo fullDraw = r11.GetMethod("FullDraw", S, null, new[] { typeof(bool) }, null);
                FullDrawOk = Check("MyRender11.FullDraw(bool)", fullDraw, true, typeof(void)) &&
                             fullDraw.GetParameters()[0].Name == "draw" &&
                             Patch("MyRender11.FullDraw(bool)", fullDraw, Hm(nameof(FullDrawPrefix)), Hm(nameof(FullDrawPostfix)), null);
                if (fullDraw != null && fullDraw.GetParameters()[0].Name != "draw") Miss("MyRender11.FullDraw parameter is not named 'draw'");

                MethodInfo sprites = r11.GetMethods(S).FirstOrDefault(m => m.Name == "RenderMainSprites" && m.GetParameters().Length == 5 &&
                                                                           m.GetParameters()[0].ParameterType.Name == "IRtvBindable");
                SpritesOk = Check("MyRender11.RenderMainSprites(IRtvBindable, MyViewport, MyViewport, Vector2, MyViewport?)", sprites, true, typeof(void)) &&
                            Patch("MyRender11.RenderMainSprites(5 parameters)", sprites, Hm(nameof(SpritesPrefix)), null, null);
                RenderHooks.PatchS7t(Harmony, r11); // S7t-se-composite (T1): ConsumeMainSprites prefix, the crossfade pass before the HUD sprites
                S10Se.PatchHud(Harmony);        //: SE's HUD screen skipped under the curtain (no flicker)
                LifepodPod.PatchCreativeSpawn(Harmony);   //: a Creative suit spawns at 4546B where SE computes its position
                S8aDrillAim.Patch(Harmony);               //: looking down on 4546B, the hand drill aims from the eyes
            }

            // ---------------------------------------------------------------- window (VRage.Platform.Windows)
            Type gw = null;
            try { gw = Type.GetType("VRage.Platform.Windows.Forms.MyGameWindow, VRage.Platform.Windows", false); } catch (Exception) { }
            if (gw == null) Miss("VRage.Platform.Windows.Forms.MyGameWindow not found: TopMost undo and A' refused");
            else
            {
                MethodInfo mode = gw.GetMethod("OnModeChanged", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(MyWindowModeEnum), typeof(int), typeof(int), typeof(VRageMath.Rectangle) }, null);
                ModeChangedOk = Check("MyGameWindow.OnModeChanged(MyWindowModeEnum, int, int, Rectangle)", mode, false, typeof(void)) &&
                                Patch("MyGameWindow.OnModeChanged", mode, null, Hm(nameof(ModeChangedPostfix)), null);

                PropertyInfo drawEnabled = gw.GetProperty("DrawEnabled", BindingFlags.Instance | BindingFlags.Public);
                MethodInfo getter = drawEnabled != null ? drawEnabled.GetGetMethod() : null;
                DrawEnabledOk = Check("MyGameWindow.get_DrawEnabled()", getter, false, typeof(bool)) &&
                                Patch("MyGameWindow.get_DrawEnabled", getter, Hm(nameof(DrawEnabledPrefix)), null, null);
            }

            // ---------------------------------------------------------------- audio (Sandbox.Game)
            MethodInfo updateSound = AccessTools.Method(typeof(MySandboxGame), "UpdateSound", Type.EmptyTypes);
            UpdateSoundOk = Check("MySandboxGame.UpdateSound()", updateSound, false, typeof(void)) &&
                            Patch("MySandboxGame.UpdateSound()", updateSound, null, Hm(nameof(UpdateSoundPostfix)), null);

            MethodInfo optSave = AccessTools.Method(typeof(MyGuiScreenOptionsAudio), "Save", Type.EmptyTypes);
            OptionsSaveOk = Check("MyGuiScreenOptionsAudio.Save()", optSave, false, typeof(void)) &&
                            Patch("MyGuiScreenOptionsAudio.Save()", optSave, Hm(nameof(OptionsSavePrefix)), null, null);

            // ---------------------------------------------------------------- save (Water Mod fallback)
            MethodInfo save = AccessTools.Method(typeof(MySession), "Save",
                new[] { typeof(MySessionSnapshot).MakeByRefType(), typeof(string), typeof(Action<SaveProgress>) });
            SessionSaveOk = Check("MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>) [S2 Water Mod fallback]", save, false, typeof(bool)) &&
                            Patch("MySession.Save(out ...) [S2 Water Mod fallback]", save, Hm(nameof(SessionSavePrefix)), null, Hm(nameof(SessionSaveFinalizer)));

            DeLog.Info(Missing.Count == 0
                ? "DE.Harmony S2 OK " + Applied.Count + " targets patched: " + string.Join(", ", Applied.ToArray())
                : "DE.Harmony S2 " + Applied.Count + " patched, MISSING: " + string.Join("; ", Missing.ToArray()) +
                  " (designF=" + DesignFHooksOk + " aPrime=" + APrimeHooksOk + " audio=" + UpdateSoundOk + "; S0/S1 unaffected)");
        }

        private static HarmonyMethod Hm(string name)
        {
            return new HarmonyMethod(typeof(S2Patches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
        }

        private static void Miss(string what)
        {
            Missing.Add(what);
            DeLog.Info("DE.Harmony MISSING " + what);
        }

        private static bool Check(string name, MethodInfo m, bool expectStatic, Type expectReturn)
        {
            if (m == null) { Miss(name + " not found in this game build (patch off)"); return false; }
            if (m.IsStatic != expectStatic || m.ReturnType != expectReturn)
            {
                Miss(name + " has a different shape: static=" + m.IsStatic + " returns " + m.ReturnType.Name + " (patch off)");
                return false;
            }
            string access = m.IsPublic ? "public" : m.IsPrivate ? "private" : m.IsFamily ? "protected" : "internal";
            DeLog.Info("DE.Harmony target " + m.DeclaringType.FullName + "." + m.Name + "(" +
                       string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ") " + access +
                       (m.IsStatic ? " static" : " instance") + " returns " + m.ReturnType.Name + " (S2)");
            return true;
        }

        private static bool Patch(string name, MethodInfo target, HarmonyMethod prefix, HarmonyMethod postfix, HarmonyMethod finalizer)
        {
            try
            {
                Harmony.Patch(target, prefix: prefix, postfix: postfix, transpiler: null, finalizer: finalizer);
                Applied.Add(name);
                return true;
            }
            catch (Exception e)
            {
                Miss(name + " patch failed: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // ============================================================================================ patch bodies
 // Never throw: an exception here would break SE's render, sound, options or save code.

        private static void PresentPostfix()
        {
            try { RenderHooks.PresentPostfix(); } catch (Exception) { }
        }

        private static void FullDrawPrefix()
        {
            try { RenderHooks.FullDrawPrefix(); } catch (Exception) { }
        }

        private static void FullDrawPostfix(bool draw)
        {
            try { RenderHooks.FullDrawPostfix(draw); } catch (Exception) { }
        }

        private static void SpritesPrefix()
        {
            try { RenderHooks.RenderMainSpritesPrefix(); } catch (Exception) { }
        }

        private static void ModeChangedPostfix()
        {
            try { RenderHooks.ModeChangedPostfix(); } catch (Exception) { }
        }

        private static bool DrawEnabledPrefix(ref bool __result)
        {
            try
            {
                if (FallbackAPrime.DrawDisabled) { __result = false; return false; }
            }
            catch (Exception) { }
            return true;
        }

        private static void UpdateSoundPostfix()
        {
            try { S2Host.OnUpdateSound(); } catch (Exception) { }
        }

        private static void OptionsSavePrefix(object __instance)
        {
            try { S2Host.OnOptionsAudioSave(__instance); } catch (Exception) { }
        }

        private static void SessionSavePrefix()
        {
            try { WaterModAudio.BeforeSessionSave(); } catch (Exception) { }
        }

        private static void SessionSaveFinalizer()
        {
            try { WaterModAudio.AfterSessionSave(); } catch (Exception) { }
        }
    }
}
