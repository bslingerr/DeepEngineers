using System;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Wire;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.World;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// and the double airlock
 /// under Subnautica's rule). SE's own environment oxygen
 /// takes the room of <c>GetSafeOxygenBlock(WorldAABB.Center)</c>, which looks into the
 /// neighbouring cell when the centre's cell holds no room: a character hugging a wall from outside,
 /// or standing on a glass roof, reads the room behind the block and gets its oxygen. Under the sea this plugin already answers,
 /// per tick, whether the camera is in air (S12's room rule: a free cell of an airtight room, or the last airtight cells of a room
 /// open through a door only; <see cref="S12Air.LastIn"/>), so this postfix makes SE's environment oxygen follow that answer while
 /// the character is under the sea: 0 outside, SE's room level inside, full in a door-open chamber (SE vented it). Above the sea
 /// and in a cockpit SE's own values stay. DEEPENGINEERS_S12_O2=off restores SE's lookup.
 /// </summary>
    internal static class S12Oxygen
    {
        public const string Env = "DEEPENGINEERS_S12_O2";
        private static bool _installed, _on = true, _failed;
        private static FieldInfo _syncField;
        private static PropertyInfo _valueProp;
        public static long Overrides, Kept, Skipped;
        public static float LastSet = float.NaN;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                string v = Environment.GetEnvironmentVariable(Env);
                if (v != null && (v.Trim().ToLowerInvariant() == "off" || v.Trim() == "0")) { _on = false; DeLog.Info("DE.S12 oxygen rule off (" + Env + ")"); return; }
            }
            catch (Exception) { }
            try
            {
                MethodInfo m = AccessTools.Method(typeof(MyCharacterOxygenComponent), "UpdateOxygen");
                _syncField = AccessTools.Field(typeof(MyCharacter), "EnvironmentOxygenLevelSync");
                if (m == null || _syncField == null)
                {
                    DeLog.Info("DE.Harmony MISSING MyCharacterOxygenComponent.UpdateOxygen or MyCharacter.EnvironmentOxygenLevelSync (S12 oxygen rule): feature off");
                    _on = false;
                    return;
                }
                var harmony = new Harmony(HarmonyPatches.HarmonyId);
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(S12Oxygen).GetMethod(nameof(UpdateOxygenPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                DeLog.Info("DE.Harmony patched MyCharacterOxygenComponent.UpdateOxygen() postfix (S12 oxygen rule: under the sea the environment oxygen follows the room answer, a free cell of a room, never a wall's; " + Env + "=off restores SE's)");
            }
            catch (Exception e)
            {
                _on = false;
                DeLog.Info("DE.Harmony S12 oxygen FAIL " + e.GetType().Name + ": " + e.Message + " (SE's own oxygen lookup stays)");
            }
        }

        private static void UpdateOxygenPostfix(MyCharacterOxygenComponent __instance)
        {
            if (!_on || _failed) return;
            try
            {
                MyCharacter c = __instance != null ? __instance.Character : null;
                if (c == null || c.Closed) return;
                if (MySession.Static == null || !ReferenceEquals(MySession.Static.LocalCharacter, c)) return;
                if (c.Parent is MyCockpit) { Skipped++; return; }                                             // SE's own cockpit rule
                SiteMap map = SiteBinding.Map;
                if (map == null || !S12Air.HasCamera) { Skipped++; return; }
                Vector3D p = c.PositionComp.WorldAABB.Center;
                Vec3 sn = map.SeToSn(new Vec3(p.X, p.Y, p.Z));
                if (sn.Y >= 0) { Skipped++; return; }                                                         // above the sea: SE's atmosphere
                object sync = _syncField.GetValue(c);
                if (sync == null) { Skipped++; return; }
                if (_valueProp == null) _valueProp = sync.GetType().GetProperty("Value");
                if (_valueProp == null) { _failed = true; DeLog.Info("DE.S12 oxygen rule: Sync.Value not found; feature off"); return; }
                float cur = Convert.ToSingle(_valueProp.GetValue(sync, null), CultureInfo.InvariantCulture);
                bool air = S12Air.LastIn;
                float want = !air ? 0f : (S12Air.LastFrozen ? Math.Max(cur, 1f) : cur);   // a door-open chamber: SE vented it, Subnautica keeps its air
                if (Math.Abs(want - cur) < 1e-4f) { Kept++; return; }
                _valueProp.SetValue(sync, want, null);
                LastSet = want;
                Overrides++;
            }
            catch (Exception e)
            {
                _failed = true;
                DeLog.Info("DE.S12 oxygen rule FAIL " + e.GetType().Name + ": " + e.Message + " (SE's own oxygen lookup stays)");
            }
        }
    }
}
