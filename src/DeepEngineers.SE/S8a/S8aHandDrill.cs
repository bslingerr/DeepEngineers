using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Definitions;

namespace DeepEngineers.SE
{
    /// <summary>
 /// in our project we can't
 /// crouch so mining is a pain, specially on a flat surface"; "they still give stone when mining them, only with the character's
 /// drill, with the ship it's all good").
 /// on the seabed), so SE's crouch never happens there (<c>MyCharacter.Crouch</c> refuses while the jetpack runs and the Flying
 /// movement state has no crouch). SE's hand drill cuts a sphere of only 0.35 m x the drill definition's DistanceMultiplier at the
 /// ray hit
 /// so standing up it scratches the floor, and at a vein's edge the 0.35 m sphere stays in the cover's boundary
 /// voxels while Subnautica's 1 m voxel already reads as ore. A bigger sphere digs down under the feet without crouching and reaches
 /// the vein. Applied once per world to every <c>MyHandDrillDefinition</c> (<c>MyEngineerToolBaseDefinition.DistanceMultiplier</c>,
 /// a public field read when a drill is equipped): DEEPENGINEERS_HANDDRILL_SCALE, default 2, 0.5..4 (1 = SE's own values).
 /// The originals are kept per subtype so a second world in the same process never compounds.
 /// </summary>
    internal static class S8aHandDrill
    {
        public const string Env = "DEEPENGINEERS_HANDDRILL_SCALE";
        public const float Default = 2f;
        private static readonly Dictionary<string, float> _original = new Dictionary<string, float>();
        public static float Scale { get; private set; }
        public static int Applied { get; private set; }

        /// <summary>World up (after the planet rules): every hand drill definition's cut multiplier at its SE value times the scale.</summary>
        public static void Apply()
        {
            float s = Default;
            try
            {
                string v = Environment.GetEnvironmentVariable(Env);
                double d;
                if (!string.IsNullOrEmpty(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.5 && d <= 4.0) s = (float)d;
            }
            catch (Exception) { }
            Scale = s;
            Applied = 0;
            var sb = new System.Text.StringBuilder(200);
            try
            {
                MyDefinitionManager dm = MyDefinitionManager.Static;
                if (dm == null) { DeLog.Info("DE.S8a hand drill cut not applied: no definition manager"); return; }
                foreach (MyHandItemDefinition def in dm.GetHandItemDefinitions())   // the hand drills are hand items, not physical items (MyHandItemDefinition: MyDefinitionBase)
                {
                    var hd = def as MyHandDrillDefinition;
                    if (hd == null) continue;
                    string key = hd.Id.SubtypeName ?? "";
                    float orig;
                    if (!_original.TryGetValue(key, out orig)) { orig = hd.DistanceMultiplier; _original[key] = orig; }
                    hd.DistanceMultiplier = orig * s;
                    Applied++;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(key).Append(' ').Append(orig.ToString("0.##", CultureInfo.InvariantCulture)).Append("->")
                      .Append(hd.DistanceMultiplier.ToString("0.##", CultureInfo.InvariantCulture));
                }
                DeLog.Info("DE.S8a hand drill cut x" + s.ToString("0.##", CultureInfo.InvariantCulture) + " (" + Env + "): " + Applied +
                           " definition(s): " + sb + " (the cut sphere is 0.35 m x multiplier at the ray hit; takes effect when a drill is equipped)");
            }
            catch (Exception e) { DeLog.Info("DE.S8a hand drill cut not applied: " + e.GetType().Name + ": " + e.Message); }
        }
    }
    /// <summary>

 /// logical weapon pose (MyHandDrill.WorldPositionChanged: WeaponPosition.LogicalPositionWorld / LogicalOrientationWorld), which
 /// cannot point at the feet; SE players crouch for that, and the swim model keeps the proxy out of the crouch under water.
 /// On 4546B, when the local character looks more than LookDownDeg below the horizon with a hand drill, the drill's ray starts
 /// at the eyes and follows the view (GetHeadMatrix), and the ray is RayLengthM long instead of SE's 2.7 m (MyDrillSensorRayCast
 /// starts 0.5 m behind its origin), so the ground under the feet is in reach. DEEPENGINEERS_HANDDRILL_AIM=off: SE's own aim.
 /// </summary>
    internal static class S8aDrillAim
    {
        public static bool On = !string.Equals(Environment.GetEnvironmentVariable("DEEPENGINEERS_HANDDRILL_AIM"), "off", StringComparison.OrdinalIgnoreCase);
        public const double LookDownDeg = 30.0;
        public const float RayLengthM = 4.0f;
        private static System.Reflection.FieldInfo _drillBase, _rayLength;
        private static readonly HashSet<object> _lengthened = new HashSet<object>();
        public static long Aimed;
        private static bool _logged;

        public static void Patch(HarmonyLib.Harmony harmony)
        {
            try
            {
                var t = typeof(Sandbox.Game.Weapons.MyHandDrill);
                var m = t.GetMethod("WorldPositionChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public, null, new[] { typeof(object) }, null);
                _drillBase = t.GetField("m_drillBase", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                _rayLength = typeof(Sandbox.Game.Weapons.Guns.MyDrillSensorRayCast).GetField("m_rayLength", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (harmony == null || m == null || _drillBase == null || _rayLength == null)
                {
                    DeLog.Info("DE.Harmony MISSING S8a MyHandDrill.WorldPositionChanged / m_drillBase / m_rayLength: the hand drill keeps SE's aim");
                    return;
                }
                harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(S8aDrillAim).GetMethod(nameof(Postfix), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)));
                DeLog.Info("DE.Harmony S8a OK MyHandDrill.WorldPositionChanged postfix (looking down on 4546B, the drill ray follows the view from the eyes, " + RayLengthM + " m)");
            }
            catch (Exception e) { DeLog.Info("DE.Harmony S8a drill aim patch failed: " + e.GetType().Name + ": " + e.Message); }
        }

        public static void Postfix(Sandbox.Game.Weapons.MyHandDrill __instance)
        {
            if (!On) return;
            try
            {
                Sandbox.Game.Entities.Character.MyCharacter c = __instance.Owner;
                if (c == null || Sandbox.Game.World.MySession.Static == null || !ReferenceEquals(c, Sandbox.Game.World.MySession.Static.LocalCharacter)) return;
                if (!SiteBinding.IsOk) return;
                Sandbox.Game.Entities.MyPlanet planet = SiteBinding.Planet;
                if (planet == null) return;
                VRageMath.MatrixD head = c.GetHeadMatrix(false, true);
                VRageMath.Vector3D eye = head.Translation, centre = planet.PositionComp.GetPosition();
                if (VRageMath.Vector3D.Distance(eye, centre) > planet.AverageRadius + LifepodPod.NearM) return;   // 4546B only
                VRageMath.Vector3D up = VRageMath.Vector3D.Normalize(eye - centre);
                VRageMath.Vector3D fwd = VRageMath.Vector3D.Normalize(head.Forward);
                if (VRageMath.Vector3D.Dot(fwd, up) > -Math.Sin(LookDownDeg * Math.PI / 180.0)) return;           // not looking down: SE's aim
                var drillBase = _drillBase.GetValue(__instance) as Sandbox.Game.Weapons.MyDrillBase;
                if (drillBase == null) return;
                var sensor = drillBase.Sensor as Sandbox.Game.Weapons.Guns.MyDrillSensorRayCast;
                if (sensor != null && _lengthened.Add(sensor)) _rayLength.SetValue(sensor, RayLengthM);
                VRageMath.MatrixD m = VRageMath.MatrixD.Identity;
                VRageMath.Vector3D right = c.WorldMatrix.Right;
                m.Forward = fwd;
                m.Up = VRageMath.Vector3D.Normalize(right.Cross(fwd));
                m.Right = VRageMath.Vector3D.Normalize(fwd.Cross(m.Up));
                m.Translation = eye;
                drillBase.UpdatePosition(m);
                Aimed++;
                if (!_logged) { _logged = true; DeLog.Info("DE.S8a hand drill aimed from the eyes (looking " + Math.Round(Math.Asin(-VRageMath.Vector3D.Dot(fwd, up)) * 180.0 / Math.PI) + " deg down, ray " + RayLengthM + " m)"); }
            }
            catch (Exception e) { On = false; DeLog.Info("DE.S8a hand drill aim off after " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
