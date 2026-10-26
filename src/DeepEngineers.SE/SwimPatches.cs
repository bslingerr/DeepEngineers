using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using VRage.Game;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Harmony patches of slice S2b "Swimming" (same id <c>deepengineers.se</c>, Lib.Harmony 2.4.2 from Pulsar). Each
 /// target was read, is looked
 /// up again here with explicit parameter types and its shape checked before patching. A missing or changed target
 /// logs <c>DE.Harmony MISSING</c> and <c>DE.Swim FEATURE DISABLED</c> once and stays inert: the stock SE movement then
 /// applies. Independent of the S0/S1/S2 patches (a failure here never refuses the display bridge). Every patch body
 /// catches everything.
 /// <list type="bullet">
 /// <item><c>Sandbox.Engine.Physics.MyPhysics.SimulateInternal</c>
 /// prefix = the controller tick (the swim velocity is the last write before the step); postfix = the
 /// post-step read for the swim rows.</item>
 /// <item><c>Sandbox.Game.Entities.Character.MyCharacter.Simulate</c>
 /// prefix = a running
 /// script drives SE's own walk and jetpack out of the water through <c>MyCharacter.MoveAndRotate(Vector3, Vector2,
 /// float)</c>, as the gameplay screen does after its input handling
 /// </item>
 /// <item><c>Sandbox.Game.Entities.Character.Components.MyCharacterJetpackComponent.GetObjectBuilder(MyObjectBuilder_Character)</c>
 /// postfix = while the swim policy holds the player's own jetpack and dampener
 /// states, the saved character gets those.</item>
 /// </list>
 /// </summary>
    internal static class SwimPatches
    {
        public const string SimulateInternalName = "MyPhysics.SimulateInternal()";
        public const string CharacterSimulateName = "MyCharacter.Simulate()";
        public const string JetpackBuilderName = "MyCharacterJetpackComponent.GetObjectBuilder(MyObjectBuilder_Character)";

        public static bool VelocityOk { get; private set; }
        public static bool InjectOk { get; private set; }
        public static bool SaveOk { get; private set; }

        /// <summary>Called from the plugin's Harmony bootstrap inside a try/catch.</summary>
        public static void Apply()
        {
            SwimController.LoadConfig();
            if (!SwimController.Enabled)
            {
                DeLog.Info("DE.Harmony S2b skipped: " + SwimController.EnvVar + " is off (no swim patches)");
                return;
            }
            var harmony = new Harmony(HarmonyPatches.HarmonyId);

            MethodInfo sim = AccessTools.Method(typeof(MyPhysics), "SimulateInternal", Type.EmptyTypes);
            VelocityOk = Check(SimulateInternalName, sim, typeof(MyPhysics)) &&
                         Patch(harmony, SimulateInternalName, sim, Hm(nameof(SimulateInternalPrefix)), Hm(nameof(SimulateInternalPostfix)));

            MethodInfo chr = AccessTools.Method(typeof(MyCharacter), "Simulate", Type.EmptyTypes);
            InjectOk = Check(CharacterSimulateName, chr, typeof(MyCharacter)) &&
                       Patch(harmony, CharacterSimulateName, chr, Hm(nameof(CharacterSimulatePrefix)), null);

            MethodInfo job = AccessTools.Method(typeof(MyCharacterJetpackComponent), "GetObjectBuilder", new[] { typeof(MyObjectBuilder_Character) });
            SaveOk = Check(JetpackBuilderName, job, typeof(MyCharacterJetpackComponent), typeof(MyObjectBuilder_Character)) &&
                     Patch(harmony, JetpackBuilderName, job, null, Hm(nameof(JetpackGetObjectBuilderPostfix)));

            SwimController.SetHooks(VelocityOk, InjectOk, SaveOk);
            DeLog.Info("DE.Harmony S2b " + (VelocityOk && InjectOk && SaveOk ? "OK 3 targets patched: " : "PARTIAL: ") +
                       SimulateInternalName + " prefix+postfix " + (VelocityOk ? "ok" : "MISSING") + ", " +
                       CharacterSimulateName + " prefix " + (InjectOk ? "ok" : "MISSING") + ", " +
                       JetpackBuilderName + " postfix " + (SaveOk ? "ok" : "MISSING") + " (swim controller; S0/S1/S2 unaffected)");
        }

        private static HarmonyMethod Hm(string name)
        {
            return new HarmonyMethod(typeof(SwimPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>The target exists with this exact shape: instance, void, the given parameter types, declared on <paramref name="declaring"/>.</summary>
        private static bool Check(string name, MethodInfo m, Type declaring, params Type[] parameters)
        {
            if (m == null)
            {
                DeLog.Info("DE.Harmony MISSING " + name + " not found in this game build (S2b patch off)");
                return false;
            }
            ParameterInfo[] ps = m.GetParameters();
            bool sameParams = ps.Length == parameters.Length;
            for (int k = 0; sameParams && k < ps.Length; k++) sameParams = ps[k].ParameterType == parameters[k];
            if (m.IsStatic || m.ReturnType != typeof(void) || !sameParams || m.DeclaringType != declaring)
            {
                DeLog.Info("DE.Harmony MISSING " + name + " has a different shape: static=" + m.IsStatic + " returns " + m.ReturnType.Name +
                           " parameters=" + ps.Length + " declared on " + m.DeclaringType + " (S2b patch off)");
                return false;
            }
            string access = m.IsPublic ? "public" : m.IsPrivate ? "private" : m.IsFamily ? "protected" : "internal";
            DeLog.Info("DE.Harmony target " + m.DeclaringType.FullName + "." + m.Name + "(" +
                       string.Join(", ", ps.Select(p => p.ParameterType.Name).ToArray()) + ") " + access + (m.IsVirtual ? " virtual" : "") +
                       " instance returns Void (S2b)");
            return true;
        }

        private static bool Patch(Harmony harmony, string name, MethodInfo target, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try
            {
                harmony.Patch(target, prefix: prefix, postfix: postfix, transpiler: null, finalizer: null);
                Patches info = Harmony.GetPatchInfo(target);
                if (info != null)
                {
                    string others = string.Join(", ", info.Prefixes.Concat(info.Postfixes).Where(p => p.owner != HarmonyPatches.HarmonyId)
                                                                      .Select(p => p.owner).Distinct().ToArray());
                    if (others.Length > 0) DeLog.Info("DE.Harmony S2b note: " + name + " is also patched by " + others);
                }
                return true;
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Harmony MISSING " + name + " patch failed: " + e.GetType().Name + ": " + e.Message + " (S2b patch off)");
                return false;
            }
        }

        // ============================================================================================ patch bodies
 // Never throw: an exception here would break SE's physics step or the character's simulation.

        private static void SimulateInternalPrefix()
        {
            try { SwimController.BeforePhysicsStep(); } catch (Exception) { }
            try { RespawnWatch.Tick(); } catch (Exception) { }   // the lifepod respawn rule
        }

        private static void SimulateInternalPostfix()
        {
            try { SwimController.AfterPhysicsStep(); } catch (Exception) { }
        }

        private static void CharacterSimulatePrefix(MyCharacter __instance)
        {
            try { SwimController.BeforeCharacterSimulate(__instance); } catch (Exception) { }
        }

        private static void JetpackGetObjectBuilderPostfix(MyCharacterJetpackComponent __instance, MyObjectBuilder_Character __0)
        {
            try { SwimController.AfterJetpackObjectBuilder(__instance, __0); } catch (Exception) { }
        }
    }
}
