using System;
using System.Globalization;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The runtime side of <see cref="S7pSelfShadowRules"/>: the character layer, resolved once the main camera exists (22:5x,

 /// unchecked and Subnautica's camera mask leaves the unnamed layers out), the main camera kept rendering it every frame
 /// (<see cref="KeepCamera"/>: the bit is set again whenever Subnautica's mask drops it), and the two uses: character
 /// renderers on the layer (<see cref="Apply"/>, re-applied by the character mirror when <see cref="Generation"/> moves),
 /// character-kind lights excluding it (<see cref="LightMirror"/>).
 /// </summary>
    internal static class S7pSelfShadow
    {
        /// <summary><c>DEEPENGINEERS_S7P_SELFSHADOW=off</c>: layer 0 and no exclusion, the look before.</summary>
        public static readonly bool Off = ReadOff();
        private static bool _resolved;
        private static int _layer = -1, _cameraMask;
        /// <summary>Moves when the layer changes (the character mirror re-applies its renderers' layers).</summary>
        public static int Generation;
        public static long CameraBitSets, LightBitSets, LightsSwept;
        private static float _nextSweep;
        public static string Note = "waiting for the main camera (layer 0 meanwhile)";

        public static int Layer { get { if (!_resolved) Resolve(); return _layer; } }
        public static int LampMask { get { return S7pSelfShadowRules.LampMask(Layer); } }
        public static int CameraMask { get { return _cameraMask; } }

        /// <summary>Puts a character renderer's GameObject on the character layer (Default while there is none).</summary>
        public static void Apply(GameObject go)
        {
            if (go == null) return;
            int want = S7pSelfShadowRules.RendererLayer(Layer);
            if (go.layer != want) go.layer = want;
        }

        /// <summary>Every posed frame: the main camera renders the character layer (its bit set again when Subnautica's mask lacks it).</summary>
        public static void KeepCamera()
        {
            int l = Layer;
            if (l < 0) return;
            Camera cam = MainCamera.camera;
            if (cam == null) return;
            int bit = 1 << l;
            int mask = cam.cullingMask;
            if ((mask & bit) != 0) return;
            cam.cullingMask = mask | bit;
            CameraBitSets++;
            if (CameraBitSets <= 3) DeLog.Info("DE.S7p self-shadow: the main camera's mask 0x" + mask.ToString("x", CultureInfo.InvariantCulture) + " lacked layer " + l + ": bit set (" + CameraBitSets + ")");
        }

        /// <summary>

 /// unnamed layers out (as the main camera's did) neither lights the character nor draws its shadow, the sun included.
 /// Every 3 s, every light in the scene that is not ours (our pool is named "DE.") gets the layer's bit. Called with
 /// <see cref="KeepCamera"/> every posed frame; cheap (a few hundred lights at most).
 /// </summary>
        public static void KeepLights()
        {
            int l = Layer;
            if (l < 0 || Time.unscaledTime < _nextSweep) return;
            _nextSweep = Time.unscaledTime + 3f;
            int bit = 1 << l;
            try
            {
                Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>();
                LightsSwept = lights.Length;
                for (int i = 0; i < lights.Length; i++)
                {
                    Light li = lights[i];
                    if (li == null) continue;
                    int mask = li.cullingMask;
                    if ((mask & bit) != 0) continue;
                    string n = li.name;
                    if (n != null && n.StartsWith("DE.", StringComparison.Ordinal)) continue; // our mirrored lights set their own masks every frame
                    li.cullingMask = mask | bit;
                    LightBitSets++;
                    if (LightBitSets <= 5) DeLog.Info("DE.S7p self-shadow: light '" + n + "' (" + li.type + ") mask 0x" + mask.ToString("x", CultureInfo.InvariantCulture) + " lacked layer " + l + ": bit set (" + LightBitSets + ")");
                }
            }
            catch (Exception e) { _nextSweep = Time.unscaledTime + 30f; DeLog.Info("DE.S7p self-shadow light sweep failed: " + e.GetType().Name + " " + e.Message); }
        }

        private static void Resolve()
        {
            if (Off) { _resolved = true; _layer = -1; Note = "off (DEEPENGINEERS_S7P_SELFSHADOW=off): layer 0, the lamp shadows the body"; DeLog.Info("DE.S7p self-shadow " + Note); return; }
            try
            {
                Camera cam = MainCamera.camera;
                if (cam == null) return; // not resolved: layer 0 until the camera exists (the character mirror asks again every frame)
                _resolved = true;
                _cameraMask = cam.cullingMask;
                _layer = S7pSelfShadowRules.PickLayer(LayerMask.LayerToName);
                Generation++;
                Note = (_layer < 0 ? "no unnamed user layer: layer 0, the lamp shadows the body" : "layer " + _layer.ToString(CultureInfo.InvariantCulture) + " (unnamed), character lamps exclude it, the main camera renders it")
                     + " (camera mask 0x" + _cameraMask.ToString("x", CultureInfo.InvariantCulture) + ")";
                DeLog.Info("DE.S7p self-shadow: " + Note + " (SE's UpdateShadowIgnoredObjects rule: the character never casts from its own lamp)");
                KeepCamera();
            }
            catch (Exception e) { _resolved = true; _layer = -1; Note = "error: " + e.GetType().Name + " " + e.Message; DeLog.Info("DE.S7p self-shadow " + Note); }
        }

        private static bool ReadOff()
        {
            try { string v = Environment.GetEnvironmentVariable("DEEPENGINEERS_S7P_SELFSHADOW"); return v != null && v.Trim().ToLowerInvariant() == "off"; }
            catch (Exception) { return false; }
        }
    }
}
