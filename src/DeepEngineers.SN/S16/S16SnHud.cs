using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DeepEngineers.SN
{
    /// <summary>
 /// ")
 /// Subnautica's own depth meter and vehicle readouts, shown while the picture is Subnautica's, driven from SE's values
 /// (the C5 extension; <c>format hud.sn</c>). VERIFIED
 /// <list type="bullet">
 /// <item>Why they are hidden: the bridge's <c>GUIController.SetHidePhase(MaskHUD)</c> (UiHider) runs
 /// <c>HideForScreenshots.Hide</c>, which broadcasts <c>HideForScreenshots</c> to the uGUI
 /// root; <c>uGUI.HideForScreenshots</c> → <c>SetVisible(false)</c> disables every Canvas under it.
 /// The two elements are therefore re-parented (world position kept, a size taken from their rect) under S16's own
 /// overlay canvas "DE.S16.Hud" (no HideForScreenshots on it, so MaskHUD keeps hiding everything else) with the uGUI
 /// root canvas's CanvasScaler settings, and put back where they were when the world goes.</item>
 /// <item><c>uGUI_DepthCompass</c> (found once per world with <c>FindObjectOfType</c>): its own LateUpdate
 /// is switched off while S16 drives it, because it would read the ghost Player's mode;
 /// S16 writes its player readout (<c>depthText</c>/<c>suffixText</c>/<c>shadow</c>/<c>halfMoon</c>) in both
 /// modes (<c>format hud.sn</c>, depth 6): the diver's depth; seated, the Cyclops helm's form "depth m / crush m"
 /// (<c>CyclopsHelmHUDManager</c> writes <c>"{depth}m / {crush}m"</c>, white, red at the crush depth; the helm
 /// itself is a world-space screen on the Cyclops prefab, not a uGUI element to borrow), toned by the HUD state (normal,
 /// caution 0xFFC040 while creaking, <c>textColorDanger</c> + <c>halfMoonDanger</c> past the limit). The compass strip
 /// <c>uGUI_Compass.SetVisible(false)</c>.</item>

 /// (<c>submersibleDepth</c>/<c>submersibleCrushDepth</c>/<c>submersibleBackground</c>) is no longer used: only
 /// one of its four fields was measurable (rect "compass grid … 1 readout part(s)" in his log) and the readout it placed was
 /// not visible; and <c>uGUI_SeamothHUD</c> is no longer taken (it stays under uGUI, hidden by the bridge's MaskHUD).</item>
 /// </list>
 /// Main thread. A missing element logs <c>DE.S16 MISSING …</c> once; SE keeps its own HUD line then (the C7 flags say
 /// what is shown, <see cref="Flags"/>).
 /// <para>Placement (<c>format hud.place</c>
 /// pixels; put it above the HUD bar"): the canvas uses Subnautica's own HUD scale
 /// and each element is moved by the bounds of its enabled graphics, not by its root
 /// rect, and only by its readout (the shown mode's own fields
 /// kept the seated readout below it): the compass top-centre 6 % below the top edge, refitted on every mode change and
 /// every second while shown. The rect is logged once per element
 /// and mode (<c>DE.S16 Subnautica HUD rect …</c>) and carried by every <c>s16.snhud</c> row.</para>
 /// </summary>
    internal static class S16SnHud
    {
        private static GameObject _canvasGo;
        private static Canvas _canvas;
        private static uGUI_DepthCompass _compass;
        private static Saved _compassSaved;
        private static float _nextFindS;
        private static bool _missingCompassLogged;
        private static int _mode = -1, _lastDepth = int.MinValue, _lastCrush = int.MinValue, _lastTone = -1, _lastTextMode = -1;
        private static string _text = "";
        // the seated readout's live TMP check (format hud.sn, depth 7) and depthText's own settings, put back for the diver
        private static int _lines = -1;
        private static double _textW = -1, _compassW = -1, _centreDx = double.NaN;
        private static bool _fits, _seatedSet, _fitLogged;
        private static bool _origSaved, _origAuto, _origWrap, _origIgnore, _addedLayout;
        private static float _origSize, _origMin, _origMax, _origWidth;
        private static TextOverflowModes _origOverflow;
        private static TextAlignmentOptions _origAlign;
        private static LayoutElement _layout;
        private static float _nextRowS, _nextFitS;
        private static int _fitMode = -1, _screenW, _screenH;
        private static CanvasScaler _scaler;
        private static double _scale = 1.0;
        private static readonly HashSet<string> RectLogged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Vector3[] Corners = new Vector3[4];
        private static readonly List<Graphic> GraphicsScratch = new List<Graphic>();
        private static readonly List<double> RectScratch = new List<double>();
        private static readonly HashSet<Graphic> PartSet = new HashSet<Graphic>();
        private static Rect _compassPx;
        private static bool _haveCompassPx;
        public static long Shows, Rows, Fits;

        private struct Saved
        {
            public bool Have;
            public Transform Parent;
            public int Sibling;
            public Vector2 AnchorMin, AnchorMax, Pivot, AnchoredPosition, SizeDelta;
            public Vector3 Scale;
            public bool ScriptEnabled;
        }

        /// <summary>The C7 S16 flags of this frame (<c>format c7.s16</c>).</summary>
        public static int Flags { get; private set; }
        public static int Mode { get { return _mode < 0 ? S16Hud.SnHudNone : _mode; } }

        /// <summary>One frame: <paramref name="mode"/> from <see cref="S16Hud.SnHudMode"/>; never throws (the caller guards).</summary>
        public static void Frame(int mode, ref S16WireState w, float nowS)
        {
            if (_compass == null && nowS >= _nextFindS) Find(nowS);
            int flags = _compass != null ? S16Conventions.C7Flags.CompassOk : 0;
            bool show = mode != S16Hud.SnHudNone && _compass != null && _canvas != null;
            if (_canvas != null && _canvas.enabled != show) _canvas.enabled = show;
            if (show)
            {
                Scale();
                Readout(mode, ref w);
                flags |= mode == S16Hud.SnHudDiver ? S16Conventions.C7Flags.DiverShown : S16Conventions.C7Flags.GridShown;
                Shows++;
            }
            if (show && (mode != _fitMode || nowS >= _nextFitS))
            {
                // a frame after the mode's graphics were switched (their rects are current), then every second
                bool first = mode != _fitMode;
                _fitMode = mode;
                _nextFitS = nowS + (first ? 0.1f : 1f);
                Fit(mode);
            }
            else if (!show) _fitMode = -1;
            if (!show && _compass != null) OffField(ref w);
            Flags = flags;
            int shown = show ? mode : S16Hud.SnHudNone;
            if (shown != _mode || (show && nowS >= _nextRowS))
            {
                if (show && mode == S16Hud.SnHudGrid) Measure();
                if (shown != _mode)
                    DeLog.Info("DE.S16 Subnautica HUD " + Name(shown) + (shown == S16Hud.SnHudNone ? "" : " (depth " + w.DepthM.ToString("0.0", CultureInfo.InvariantCulture) + " m" +
                               (shown == S16Hud.SnHudGrid ? ", crush depth " + w.CrushM.ToString("0", CultureInfo.InvariantCulture) + " m" : "") + ")") +
                               "; compass " + (_compass != null ? "ok" : "MISSING") + (shown == S16Hud.SnHudNone ? "" : ", readout \"" + _text + "\""));
                _mode = shown;
                _nextRowS = nowS + 1f;
                Row(shown, ref w);
            }
        }

        private static string Name(int mode) { return mode == S16Hud.SnHudDiver ? "diver" : mode == S16Hud.SnHudGrid ? "grid" : "off"; }

        private static string _offText;
        public static long OffWrites;

        /// <summary>

 /// readout off (SE's state Off: above the sea or at the surface) Subnautica's compass kept writing its own number, the
 /// parked player's depth (a constant like "461 m"). Off, the number is ours: "+h" with the suffix "m" while the character is
 /// above the sea (the SE depth is negative there: the height over the surface), nothing at all otherwise.
 /// </summary>
        private static void OffField(ref S16WireState w)
        {
            try
            {
                uGUI_DepthCompass c = _compass;
                TextMeshProUGUI t = c != null ? c.depthText : null;
                if (t == null) return;
                bool above = w.DepthM < -0.5f && w.DepthM > -2000f;
                string text = above ? "+" + Mathf.RoundToInt(-w.DepthM).ToString(CultureInfo.InvariantCulture) : "";
                if (t.text != text) { t.text = text; OffWrites++; }
                if (c.suffixText != null && c.suffixText.enabled != above) SetEnabled(c.suffixText, above);
                _offText = text;
            }
            catch (Exception e) { DeLog.WarnOnce("s16.hud.off", "DE.S16 off field failed " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Row(int mode, ref S16WireState w)
        {
            Rows++;
            JsonOut j = S5Rows.Begin("s16.snhud");
            j.Str("mode", Name(mode)).Num("depth", (double)w.DepthM).Num("crush", (double)w.CrushM).Num("state", (long)w.HudState)
             .Str("text", mode == S16Hud.SnHudNone ? "" : _text).Str("tone", S16Conventions.Tone.Name(S16Hud.Tone(w.HudState)))
             .Num("lines", (long)(mode == S16Hud.SnHudGrid ? _lines : -1)).Num("textW", mode == S16Hud.SnHudGrid ? _textW : -1.0)
             .Num("compassW", mode == S16Hud.SnHudGrid ? _compassW : -1.0).Bool("oneLine", mode == S16Hud.SnHudGrid && _fits)
             .Num("centreDx", mode == S16Hud.SnHudGrid && !double.IsNaN(_centreDx) ? _centreDx : 0.0)
             .Bool("centred", mode == S16Hud.SnHudGrid && !double.IsNaN(_centreDx) && S16Hud.SeatedCentred(_centreDx, _compassW))
             .Bool("mutantWrap", DevMutants.IsOn(S16SnMutants.WrapReadout))
             .Bool("compass", _compass != null).Bool("seamoth", false).Bool("panel", false).Num("flags", (long)Flags)
             .Num("screenW", (long)_screenW).Num("screenH", (long)_screenH).Num("scale", _scale).Num("fits", Fits);
            // the placed content in screen pixels, top-left origin (format hud.place; the S16.SNHUD row)
            if (_haveCompassPx) j.Num("compassX0", (double)_compassPx.xMin).Num("compassY0", (double)_compassPx.yMin).Num("compassX1", (double)_compassPx.xMax).Num("compassY1", (double)_compassPx.yMax);
            S5Rows.End(j);
        }

        /// <summary>The canvas at Subnautica's own HUD scale for the current screen and its UI scale setting (format hud.place).</summary>
        private static void Scale()
        {
            if (_scaler == null) return;
            int sw = Screen.width, sh = Screen.height;
            double s = S16Hud.CanvasScale(sw, sh, uGUI_CanvasScaler.uiScale);
            if (sw == _screenW && sh == _screenH && Math.Abs(s - _scale) < 1e-6) return;
            _screenW = sw;
            _screenH = sh;
            _scale = s;
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _scaler.scaleFactor = (float)s;
            _fitMode = -1;   // refit at the new size
            RectLogged.Clear();
            DeLog.Info("DE.S16 HUD canvas scale " + s.ToString("0.###", CultureInfo.InvariantCulture) + " for " + sw + "x" + sh +
                       " (Subnautica's: min(W/" + S16Conventions.Hud.RefWidth + ", H/" + S16Conventions.Hud.RefHeight + ") x uiScale " +
                       uGUI_CanvasScaler.uiScale.ToString("0.##", CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>Moves the shown elements so their visible content sits at its anchor (format hud.place).</summary>
        private static void Fit(int mode)
        {
            if (_canvasGo == null) return;
            Fits++;
            // the readout only (format hud.place
 // readout's own fields in both modes (depth 6: seated shows the same readout with "m / crush m")
            if (_compass != null)
            {
                uGUI_DepthCompass c = _compass;
                _haveCompassPx = FitOne(c.gameObject, new Graphic[] { c.shadow, c.halfMoon, c.depthText, c.suffixText }, S16Hud.PlaceTopCentre, "compass " + Name(mode), out _compassPx);
            }
        }

        private static bool FitOne(GameObject go, Graphic[] parts, int anchor, string what, out Rect px)
        {
            px = default(Rect);
            var canvasRt = _canvasGo.transform as RectTransform;
            var rt = go.transform as RectTransform;
            if (canvasRt == null || rt == null) return false;
            float w = canvasRt.rect.width, h = canvasRt.rect.height;
            int used, backdrops;
            Vector2 min, max;
            if (!ReadoutBounds(go.transform, parts, canvasRt, w, h, out min, out max, out used, out backdrops)) return false;
            double dx, dy;
            S16Hud.Place(anchor, min.x, min.y, max.x, max.y, w, h, out dx, out dy);
            if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5)
            {
                rt.anchoredPosition += new Vector2((float)dx, (float)dy);
                min += new Vector2((float)dx, (float)dy);
                max += new Vector2((float)dx, (float)dy);
            }
            double qx0, qy0, qx1, qy1;
            S16Hud.ToScreenPx(min.x, min.y, max.x, max.y, w, h, _scale, out qx0, out qy0, out qx1, out qy1);
            px = Rect.MinMaxRect((float)qx0, (float)qy0, (float)qx1, (float)qy1);
            if (RectLogged.Add(what))
                DeLog.Info("DE.S16 Subnautica HUD rect " + what + ": (" + F(px.xMin) + ", " + F(px.yMin) + ")-(" + F(px.xMax) + ", " + F(px.yMax) + ") px of " +
                           _screenW + "x" + _screenH + " (top-left origin; canvas " + F(w) + "x" + F(h) + " units at scale " + _scale.ToString("0.###", CultureInfo.InvariantCulture) +
                           "; moved " + F((float)dx) + ", " + F((float)dy) + "; " + used + " readout part(s), " + backdrops + " backdrop(s) left out)" +
                           (px.xMin >= 0 && px.yMin >= 0 && px.xMax <= _screenW && px.yMax <= _screenH ? "" : " OUTSIDE the screen"));
            return true;
        }

        /// <summary>
 /// The readout's bounds (canvas units, origin the canvas centre; format hud.place): the mode's own fields and the
 /// graphics on their parents up to the element (a panel frame around the texts), enabled and active; the rule
 /// (<see cref="S16Hud.ReadoutBounds"/>) leaves out parts larger than half the canvas (backdrops).
 /// </summary>
        private static bool ReadoutBounds(Transform root, Graphic[] fields, RectTransform canvasRt, float w, float h, out Vector2 min, out Vector2 max, out int used, out int backdrops)
        {
            min = max = Vector2.zero;
            used = backdrops = 0;
            PartSet.Clear();
            RectScratch.Clear();
            foreach (Graphic f in fields)
            {
                if (f == null) continue;
                for (Transform t = f.transform; t != null && t != root; t = t.parent)
                {
                    GraphicsScratch.Clear();
                    t.GetComponents(GraphicsScratch);
                    foreach (Graphic g in GraphicsScratch) PartSet.Add(g);
                }
            }
            GraphicsScratch.Clear();
            foreach (Graphic g in PartSet)
            {
                if (g == null || !g.enabled || !g.gameObject.activeInHierarchy) continue;
                RectTransform r = g.rectTransform;
                if (r.rect.width < 1f || r.rect.height < 1f) continue;
                r.GetWorldCorners(Corners);
                Vector2 a = new Vector2(float.MaxValue, float.MaxValue), b = new Vector2(float.MinValue, float.MinValue);
                for (int i = 0; i < 4; i++)
                {
                    Vector3 l = canvasRt.InverseTransformPoint(Corners[i]);
                    a = Vector2.Min(a, l);
                    b = Vector2.Max(b, l);
                }
                RectScratch.Add(a.x);
                RectScratch.Add(a.y);
                RectScratch.Add(b.x);
                RectScratch.Add(b.y);
            }
            PartSet.Clear();
            double x0, y0, x1, y1;
            bool ok = S16Hud.ReadoutBounds(RectScratch, w, h, out x0, out y0, out x1, out y1, out used, out backdrops);
            RectScratch.Clear();
            if (!ok) return false;
            min = new Vector2((float)x0, (float)y0);
            max = new Vector2((float)x1, (float)y1);
            return true;
        }

        private static string F(float v) { return v.ToString("0", CultureInfo.InvariantCulture); }

        private static void Find(float nowS)
        {
            _nextFindS = nowS + 5f;
            if (_compass == null)
            {
                uGUI_DepthCompass c = UnityEngine.Object.FindObjectOfType<uGUI_DepthCompass>();
                if (c != null && EnsureCanvas(c.gameObject)) { _compass = c; _compassSaved = Take(c.gameObject, c, new Vector2(0.5f, 1f), new Vector2(0f, -24f)); }
                else if (!_missingCompassLogged) { _missingCompassLogged = true; DeLog.Warn("DE.S16 MISSING uGUI_DepthCompass (not found yet): SE keeps its depth line"); }
            }
            if (_compass != null)
                DeLog.Info("DE.S16 Subnautica HUD under S16's control: uGUI_DepthCompass re-parented under DE.S16.Hud (its script paused; no Seamoth panel, depth 6)");
        }

        /// <summary>The overlay canvas, with the CanvasScaler settings of the element's own root canvas.</summary>
        private static bool EnsureCanvas(GameObject element)
        {
            if (_canvas != null) return true;
            _canvasGo = new GameObject("DE.S16.Hud");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
            _canvas = _canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30;
            _scaler = _canvasGo.AddComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _screenW = _screenH = 0;
            Scale();
            _canvas.enabled = false;
            DeLog.Info("DE.S16 HUD canvas DE.S16.Hud created (Subnautica's HUD scale " + _scale.ToString("0.###", CultureInfo.InvariantCulture) + " for " + _screenW + "x" + _screenH + ")");
            return true;
        }

        /// <summary>Re-parents the element under the S16 canvas at a fixed anchor, its script paused; what was there is kept for <see cref="Teardown"/>.</summary>
        private static Saved Take(GameObject go, Behaviour script, Vector2 anchor, Vector2 offset)
        {
            var s = new Saved { Have = true, Parent = go.transform.parent, Sibling = go.transform.GetSiblingIndex(), Scale = go.transform.localScale, ScriptEnabled = script.enabled };
            var rt = go.transform as RectTransform;
            Vector2 size = Vector2.zero;
            if (rt != null)
            {
                s.AnchorMin = rt.anchorMin;
                s.AnchorMax = rt.anchorMax;
                s.Pivot = rt.pivot;
                s.AnchoredPosition = rt.anchoredPosition;
                s.SizeDelta = rt.sizeDelta;
                size = rt.rect.size;
            }
            script.enabled = false;
            go.transform.SetParent(_canvasGo.transform, false);
            if (rt != null)
            {
                rt.anchorMin = anchor;
                rt.anchorMax = anchor;
                rt.pivot = anchor;
                rt.anchoredPosition = offset;
                if (size.x > 1f && size.y > 1f) rt.sizeDelta = size;
            }
            go.transform.localScale = Vector3.one;
            return s;
        }

        private static void Restore(GameObject go, Behaviour script, Saved s)
        {
            if (go == null || !s.Have) return;
            go.transform.SetParent(s.Parent, false);
            go.transform.SetSiblingIndex(s.Sibling);
            var rt = go.transform as RectTransform;
            if (rt != null)
            {
                rt.anchorMin = s.AnchorMin;
                rt.anchorMax = s.AnchorMax;
                rt.pivot = s.Pivot;
                rt.anchoredPosition = s.AnchoredPosition;
                rt.sizeDelta = s.SizeDelta;
            }
            go.transform.localScale = s.Scale;
            if (script != null) script.enabled = s.ScriptEnabled;
        }

        /// <summary>The world went: both elements back under uGUI with their scripts, the canvas destroyed.</summary>
        public static void Teardown(string why)
        {
            try
            {
                if (_compass != null) DiverField(_compass);
                if (_addedLayout && _layout != null) UnityEngine.Object.Destroy(_layout);
                if (_compass != null) Restore(_compass.gameObject, _compass, _compassSaved);
                if (_canvasGo != null) UnityEngine.Object.Destroy(_canvasGo);
                if (_compass != null) DeLog.Info("DE.S16 Subnautica HUD returned to uGUI (" + why + ")");
            }
            catch (Exception e) { DeLog.Warn("DE.S16 HUD teardown " + e.GetType().Name + ": " + e.Message); }
            _compass = null;
            _canvas = null;
            _canvasGo = null;
            _scaler = null;
            _fitMode = -1;
            _screenW = _screenH = 0;
            _haveCompassPx = false;
            RectLogged.Clear();
            _compassSaved = default(Saved);
            _mode = -1;
            _lastDepth = _lastCrush = int.MinValue;
            _lastTone = _lastTextMode = -1;
            _text = "";
            _lines = -1;
            _textW = _compassW = -1;
            _centreDx = double.NaN;
            _fits = _seatedSet = _fitLogged = _origSaved = _addedLayout = false;
            _layout = null;
            Flags = 0;
        }

        /// <summary>
 /// The player readout in both modes (<c>format hud.sn</c>): the diver's depth; seated "depth" + "m / crush m" (the Cyclops
 /// helm's form), toned by the HUD state; the vehicle mode's fields stay off.
 /// </summary>
        private static void Readout(int mode, ref S16WireState w)
        {
            uGUI_DepthCompass c = _compass;
            SetEnabled(c.submersibleBackground, false);
            SetEnabled(c.submersibleDepth, false);
            SetEnabled(c.submersibleDepthSuffix, false);
            SetEnabled(c.submersibleCrushDepth, false);
            SetEnabled(c.shadow, true);
            SetEnabled(c.halfMoon, true);
            bool seated = mode == S16Hud.SnHudGrid;
            SetEnabled(c.depthText, true);
            SetEnabled(c.suffixText, !seated);   // depth 7: seated, one field holds the whole readout
            if (c.compass != null) c.compass.SetVisible(false);
            if (seated) SeatedField(c);
            else DiverField(c);
            int d = Mathf.FloorToInt(Math.Max(0f, w.DepthM)), crush = Mathf.RoundToInt(w.CrushM);
            if (d != _lastDepth || crush != _lastCrush || mode != _lastTextMode)
            {
                _lastDepth = d;
                _lastCrush = crush;
                _lastTextMode = mode;
                if (seated)
                {
                    _text = S16Hud.SeatedText(d, crush, Suffix());           // "10 m / 120 m", "1603 / 1700 m" (format hud.sn)
                    if (c.depthText != null) c.depthText.text = _text;
                    SizeSeated(c);
                }
                else
                {
                    string depth = d.ToString(CultureInfo.InvariantCulture), suffix = Suffix();
                    if (c.depthText != null) c.depthText.text = depth;
                    if (c.suffixText != null) c.suffixText.text = suffix;
                    _text = depth + " " + suffix;
                }
            }
            int tone = seated ? S16Hud.Tone(w.HudState) : S16Conventions.Tone.Normal;   // the diver's compass stays as it is
            if (tone != _lastTone)
            {
                _lastTone = tone;
                Color32 col = tone == S16Conventions.Tone.Danger ? c.textColorDanger
                            : tone == S16Conventions.Tone.Caution ? new Color32((byte)(S16Conventions.Hud.CautionRgb >> 16), (byte)((S16Conventions.Hud.CautionRgb >> 8) & 0xFF), (byte)(S16Conventions.Hud.CautionRgb & 0xFF), 255)
                            : c.textColorNormal;
                if (c.depthText != null) c.depthText.color = col;
                if (c.suffixText != null) c.suffixText.color = col;
                Sprite moon = tone == S16Conventions.Tone.Danger ? c.halfMoonDanger : c.halfMoonNormal;
                if (c.halfMoon != null && moon != null && c.halfMoon.sprite != moon) c.halfMoon.sprite = moon;
            }
        }

        /// <summary>
 /// depthText as the seated field (format hud.sn, depth 7/8
 /// no word wrapping, overflow, centred, as wide as the compass (the half-moon), its font size measured by
 /// <see cref="SizeSeated"/> (depth 8: TMP's auto-size stopped at its 40 % floor with "1603 m / 1700 m" 176 of 156 units),
 /// and out of any layout group (a LayoutElement with ignoreLayout). S16.M11 forces a wrap.
 /// </summary>
        private static void SeatedField(uGUI_DepthCompass c)
        {
            TextMeshProUGUI t = c.depthText;
            if (t == null) return;
            bool wrap = DevMutants.IsOn(S16SnMutants.WrapReadout);
            if (_seatedSet && t.enableWordWrapping == wrap) return;
            if (!_origSaved)
            {
                _origSaved = true;
                _origSize = t.fontSize;
                _origAuto = t.enableAutoSizing;
                _origMin = t.fontSizeMin;
                _origMax = t.fontSizeMax;
                _origWrap = t.enableWordWrapping;
                _origOverflow = t.overflowMode;
                _origAlign = t.alignment;
                _origWidth = t.rectTransform.rect.width;
                _layout = t.GetComponent<LayoutElement>();
                if (_layout == null) { _layout = t.gameObject.AddComponent<LayoutElement>(); _addedLayout = true; }
                _origIgnore = _layout.ignoreLayout;
            }
            _seatedSet = true;
            if (_layout != null) _layout.ignoreLayout = true;
            float compass = CompassWidthLocal(c, t);
            t.enableWordWrapping = wrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.alignment = TextAlignmentOptions.Center;
            t.enableAutoSizing = false;
            t.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, wrap ? compass * 0.35f : compass);
            CentreOnCompass(c, t);
            SizeSeated(c);
        }

        private static bool _origPosSaved;
        private static Vector2 _origAnchored;

        /// <summary>

 /// depthText left of suffixText, so the widened field is moved by the difference between the half-moon's centre and
 /// the field's centre, in the field's parent space; idempotent (measured from the current rect each time).
 /// </summary>
        private static void CentreOnCompass(uGUI_DepthCompass c, TextMeshProUGUI t)
        {
            Graphic g = c.halfMoon != null ? (Graphic)c.halfMoon : c.shadow;
            var parent = t.rectTransform.parent as RectTransform;
            if (g == null || parent == null) return;
            if (!_origPosSaved) { _origAnchored = t.rectTransform.anchoredPosition; _origPosSaved = true; }
            g.rectTransform.GetWorldCorners(Corners);
            Vector3 gc = parent.InverseTransformPoint((Corners[0] + Corners[2]) * 0.5f);
            Vector3 tc = parent.InverseTransformPoint(t.rectTransform.TransformPoint(t.rectTransform.rect.center));
            float dx = gc.x - tc.x;
            if (Math.Abs(dx) > 0.25f) t.rectTransform.anchoredPosition += new Vector2(dx, 0f);
        }

        /// <summary>
 /// The seated field's font size, measured (format hud.sn, depth 8): the preferred width of the text at the field's own
 /// size (TMP GetPreferredValues, one line), scaled so it fits FitPermille of the compass width, never below MinFontPermille
 /// of its own; under S16.M11 its own size (and a narrow field: it wraps).
 /// </summary>
        private static void SizeSeated(uGUI_DepthCompass c)
        {
            TextMeshProUGUI t = c.depthText;
            if (t == null || !_origSaved || !_seatedSet) return;
            t.enableAutoSizing = false;
            t.fontSize = _origSize;
            if (DevMutants.IsOn(S16SnMutants.WrapReadout)) return;
            Vector2 preferred = t.GetPreferredValues(_text);
            double target = S16Hud.SeatedTarget(CompassWidthLocal(c, t));
            t.fontSize = (float)S16Hud.SeatedFontSize(_origSize, preferred.x, target);
        }

        /// <summary>depthText back to the diver's own settings (the diver's compass stays as it is).</summary>
        private static void DiverField(uGUI_DepthCompass c)
        {
            TextMeshProUGUI t = c.depthText;
            if (t == null || !_seatedSet || !_origSaved) return;
            _seatedSet = false;
            t.enableWordWrapping = _origWrap;
            t.overflowMode = _origOverflow;
            t.alignment = _origAlign;
            t.enableAutoSizing = _origAuto;
            t.fontSizeMin = _origMin;
            t.fontSizeMax = _origMax;
            t.fontSize = _origSize;
            t.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, _origWidth);
            if (_origPosSaved) { t.rectTransform.anchoredPosition = _origAnchored; _origPosSaved = false; }
            if (_layout != null) _layout.ignoreLayout = _origIgnore;
        }

        /// <summary>The compass's width (the half-moon, else the shadow) in depthText's local units.</summary>
        private static float CompassWidthLocal(uGUI_DepthCompass c, TextMeshProUGUI t)
        {
            Graphic g = c.halfMoon != null ? (Graphic)c.halfMoon : c.shadow;
            if (g == null) return Math.Max(_origWidth, 1f);
            g.rectTransform.GetWorldCorners(Corners);
            Vector3 a = t.rectTransform.InverseTransformPoint(Corners[0]), b = t.rectTransform.InverseTransformPoint(Corners[2]);
            return Math.Max(Math.Abs(b.x - a.x), 1f);
        }

        /// <summary>
 /// The live TMP check of the seated field (format hud.sn): textInfo.lineCount after a mesh update, and the rendered
 /// width (textBounds) against the compass width, both in canvas units; logged once per world.
 /// </summary>
        private static void Measure()
        {
            uGUI_DepthCompass c = _compass;
            TextMeshProUGUI t = c != null ? c.depthText : null;
            var canvasRt = _canvasGo != null ? _canvasGo.transform as RectTransform : null;
            if (t == null || canvasRt == null) return;
            t.ForceMeshUpdate();
            _lines = t.textInfo != null ? t.textInfo.lineCount : -1;
            Bounds b = t.textBounds;
            Vector3 l = canvasRt.InverseTransformPoint(t.rectTransform.TransformPoint(new Vector3(b.min.x, b.center.y, 0f)));
            Vector3 r = canvasRt.InverseTransformPoint(t.rectTransform.TransformPoint(new Vector3(b.max.x, b.center.y, 0f)));
            _textW = Math.Abs(r.x - l.x);
            Graphic g = c.halfMoon != null ? (Graphic)c.halfMoon : c.shadow;
            _compassW = -1;
            _centreDx = double.NaN;
            if (g != null)
            {
                g.rectTransform.GetWorldCorners(Corners);
                float g0 = canvasRt.InverseTransformPoint(Corners[0]).x, g2 = canvasRt.InverseTransformPoint(Corners[2]).x;
                _compassW = Math.Abs(g2 - g0);
                // format hud.sn, depth 9: the rendered text's centre against the half-moon's
                _centreDx = (l.x + r.x) / 2.0 - (g0 + g2) / 2.0;
            }
            _fits = S16Hud.SeatedTextFits(_lines, _textW, _compassW);   // after ForceMeshUpdate, at the measured size: one line within 90 % of the compass
            if (!_fitLogged || !_fits)
            {
                if (!_fitLogged || _fits != _lastFitsLogged)
                    DeLog.Info("DE.S16 seated readout \"" + _text + "\": " + _lines + " line(s), text " + _textW.ToString("0.#", CultureInfo.InvariantCulture) + " of " +
                               _compassW.ToString("0.#", CultureInfo.InvariantCulture) + " units (target " + S16Hud.SeatedTarget(_compassW).ToString("0.#", CultureInfo.InvariantCulture) +
                               "), font " + t.fontSize.ToString("0.#", CultureInfo.InvariantCulture) + " of " + _origSize.ToString("0.#", CultureInfo.InvariantCulture) +
                               " (measured)" + (_fits ? "" : " DOES NOT FIT (wrapped or wider than 90 % of the compass)") +
                               (DevMutants.IsOn(S16SnMutants.WrapReadout) ? " [S16.M11]" : ""));
                _fitLogged = true;
                _lastFitsLogged = _fits;
            }
        }

        private static bool _lastFitsLogged;

        private static string Suffix()
        {
            Language l = Language.main;
            string s = l != null ? l.Get("MeterSuffix") : null;
            return string.IsNullOrEmpty(s) ? "m" : s;
        }

        private static void SetEnabled(Behaviour b, bool on)
        {
            if (b != null && b.enabled != on) b.enabled = on;
        }
    }
}
