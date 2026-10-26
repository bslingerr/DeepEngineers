using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The calibration pattern (ROADMAP S2 SN item "calibration pattern command (full-screen solid colour UI, the only canvas
 /// left visible)"; oracle S2.N0; ARCHITECTURE §5.1 "Mode selection at start" reads one composed pixel of it): C11
 /// <c>de.calib {"colour": "#rrggbb"|"r,g,b"|[r,g,b]|name|"off"}</c>.
 /// <list type="bullet">
 /// <item>A screen-space-overlay <c>Canvas</c> at sorting order 32767 with one full-stretch <c>Image</c> of the colour
 /// (overlay canvases are drawn after every camera, so it covers the whole client area).</item>
 /// <item>Every other enabled root canvas is disabled while the pattern is on (recorded; re-enabled by "off" or at
 /// disposal) and checked again every second, so the pattern is the only canvas left visible.</item>
 /// <item>A clear-only camera of the same colour (culling mask 0, depth 100): in Idle and Track the main and GUI cameras
 /// are off, and whether Unity 2019.4 still presents overlay canvases with no enabled camera is not established
 /// (HYPOTHESIS), so the pattern brings its own camera.</item>
 /// </list>
 /// The objects live under the bridge object (DontDestroyOnLoad, SceneCleanerPreserve). Colours are given in sRGB; the
 /// UI and the camera clear write them unchanged to the back buffer (no post-processing on our camera).
 /// </summary>
    internal sealed class CalibrationPattern
    {
        public const short SortingOrder = 32767;

        private GameObject _root;
        private Canvas _canvas;
        private Image _image;
        private Camera _camera;
        private readonly List<Canvas> _disabled = new List<Canvas>();

        public bool On { get; private set; }
        public byte R, G, B;
        public long Shows, CanvasesDisabled;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Show(Transform parent, byte r, byte g, byte b)
        {
            R = r; G = g; B = b;
            Color c = new Color32(r, g, b, 255);
            if (_root == null)
            {
                _root = new GameObject("DeepEngineers.Calibration");
                if (parent != null) _root.transform.SetParent(parent, false);

                var camGo = new GameObject("DeepEngineers.CalibrationCamera");
                camGo.transform.SetParent(_root.transform, false);
                _camera = camGo.AddComponent<Camera>();
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.cullingMask = 0;
                _camera.depth = 100f;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                _camera.useOcclusionCulling = false;

                var canvasGo = new GameObject("DeepEngineers.CalibrationCanvas");
                canvasGo.transform.SetParent(_root.transform, false);
                _canvas = canvasGo.AddComponent<Canvas>();
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.sortingOrder = SortingOrder;

                var fill = new GameObject("DeepEngineers.CalibrationFill");
                fill.transform.SetParent(canvasGo.transform, false);
                _image = fill.AddComponent<Image>();
                _image.raycastTarget = false;
                RectTransform rt = _image.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
            _camera.backgroundColor = c;
            _image.color = c;
            _root.SetActive(true);
            On = true;
            Shows++;
            int n = DisableOtherCanvases();
            DeLog.Info("DE.Calib ON colour=" + CalibRules.Hex(r, g, b) + " (" + r + "," + g + "," + b + ") overlay canvas order " + SortingOrder +
                       " + clear camera depth 100; other root canvases disabled now=" + n + " total=" + _disabled.Count + " screen=" + Screen.width + "x" + Screen.height);
        }

        /// <summary>Per second while on: canvases enabled since are disabled too.</summary>
        public void Second()
        {
            if (!On) return;
            int n = DisableOtherCanvases();
            if (n > 0) DeLog.Info("DE.Calib " + n + " more root canvas(es) disabled (the pattern stays the only visible canvas)");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int DisableOtherCanvases()
        {
            int n = 0;
            foreach (Canvas cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (cv == null || cv == _canvas || !cv.enabled || !cv.isRootCanvas) continue;
                cv.enabled = false;
                _disabled.Add(cv);
                n++;
                CanvasesDisabled++;
            }
            return n;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Hide(string why)
        {
            if (!On && _disabled.Count == 0) return;
            On = false;
            if (_root != null) _root.SetActive(false);
            int restored = 0;
            foreach (Canvas cv in _disabled)
            {
                if (cv == null || cv.enabled) continue;
                cv.enabled = true;
                restored++;
            }
            _disabled.Clear();
            DeLog.Info("DE.Calib OFF (" + why + "): " + restored + " root canvas(es) re-enabled");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose()
        {
            try { Hide("bridge disposal"); } catch (Exception) { }
            try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch (Exception) { }
            _root = null;
        }

        public string Describe()
        {
            return On ? "calib{on " + CalibRules.Hex(R, G, B) + " canvasesDisabled=" + _disabled.Count + "}" : "calib{off}";
        }
    }
}
