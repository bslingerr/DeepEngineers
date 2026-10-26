using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepEngineers.SN
{
    /// <summary>The look-match build pass haze targets (S7t T5): true with the sky-at-the-horizon and far-water colours.</summary>
    internal delegate bool HazeTargetProvider(out HazeRgb horizon, out HazeRgb low);

    /// <summary>
 /// S7t-sn-altitude T3: the impostor switches and counters shared by the mirror's hooks (MirrorConsumer, S5Mirror,
 /// LightMirror, EffectsPlayer, S7pScene: each a one-line "S7t-sn-altitude" hook). Main thread only.
 /// </summary>
    internal static class S7tImpostors
    {
        /// <summary>The live settings (the altitude host's; defaults until it starts).</summary>
        public static AltitudeSettings Settings = new AltitudeSettings();
        /// <summary>Empty parts: an impostor reconciles its children against this (they go, none come).</summary>
        public static readonly Dictionary<ulong, PartRecord> NoParts = new Dictionary<ulong, PartRecord>();
        public static long CellsPosted, CellsDeferred, PartsKept, PartsSkipped, ChildrenRemoved, LightsSkipped, EffectsSkipped,
                           SwapsToCoarse, SwapsToFull, SlotsCreated, Culled;
        /// <summary>Grids built coarse right now (MirrorConsumer's set).</summary>
        public static int Count;

        /// <summary>Is a grid with these GridAdd flags built as an impostor now (the switch on and the bit set)?</summary>
        public static bool Active(uint gridFlags) { return Settings.Impostors && ImpostorRules.IsImpostor(gridFlags); }

        public static void ResetCounters()
        {
            CellsPosted = CellsDeferred = PartsKept = PartsSkipped = ChildrenRemoved = LightsSkipped = EffectsSkipped = 0;
            SwapsToCoarse = SwapsToFull = SlotsCreated = Culled = 0;
        }
    }

    /// <summary>
 /// S7t-sn-altitude T2 (ROADMAP "### S7t Seamless transition"): Subnautica's picture right up to ~1.5 km and beyond.
 /// <list type="bullet">
 /// <item><b>The sea to the horizon</b> ("the grid extended", the component allows it): the water's square is
 /// +-<c>camera.farClipPlane</c> (VERIFIED <c>WaterSurface.BeginRenderWaterSurface</c>, <c>HeightFieldMesh.BeginRender</c>),
 /// so above <c>minAlt</c> the far plane follows the geometric horizon of SE's planet (<see cref="HorizonRules.Frame"/>;
 /// 6.8 km at 380 m, 11 km at 1 km) through <c>SNCameraRoot.SetFarPlaneDistance(float)</c> (the game's own setter: main,
 /// GUI and IMGUI cameras), and Subnautica's own value (1,700 m, read once per camera root) comes back below it, when
 /// switched off and at dispose.</item>
 /// <item><b>The horizon skirt</b>: one ring mesh built once (rows <see cref="HorizonRules.SkirtRows"/>, 128 columns),
 /// moved to the camera and scaled (R, altitude, R) at the main camera's pre-cull; each row sits exactly at the
 /// depression of the sea at distance R / v, so it hazes the far sea from the fade start (alpha 0) to the horizon
 /// (alpha 1, the horizon colour) with a feather above it. Unity's own <c>Sprites/Default</c> (fallback
 /// <c>Hidden/Internal-Colored</c>), vertex colours in linear light, no depth write, drawn right after the water
 /// (its material's queue + 1, read once from <c>WaterSurface.surfaceMaterial</c>), enabled only for the main camera
 /// (pre-cull on, post-render off). Nearer things (ships, islands, the water within R) occlude it.</item>
 /// <item><b>The haze</b>: the table by altitude (<see cref="HorizonRules.DefaultTable"/>) interpolated per frame; the
 /// colours from the look-match build pass targets (<see cref="Targets"/>) or the named constants measured on the
 /// the
 /// optional density multiplier on uSky's <c>_SkyFogDensity</c> (default 1: untouched) written after uSky's Update.</item>
 /// </list>
 /// uSky's colour parameters and the sun light are never written. Nothing is allocated per frame; every step is guarded: an exception turns the altitude picture off
 /// on this side only (the far plane restored, logged once). Toggles: <c>de.s7t horizon on|off</c>,
 /// <c>de.s7t haze key=value</c>, <c>de.s7t impostor on|off key=value</c>, <c>de.s7t alt</c>; env
 /// <see cref="HorizonRules.EnvHorizon"/>, <see cref="HorizonRules.EnvHaze"/>, <see cref="ImpostorRules.EnvImpostor"/>.
 /// Rows <c>s7t.alt</c> and <c>s7t.imp</c> at 1 Hz (CONTRACT §5.4 "S7t rows (Subnautica, sn-altitude)").
 /// </summary>
    internal sealed class AltitudeHost : IDisposable
    {
        public const string Cmd = HorizonRules.Cmd;
        private const ulong RowEveryMs = 1000;
        private const ulong QueueRetryMs = 2000;
        private const float FarEpsilonM = 0.5f;

        /// <summary>
 /// The haze targets of a look-match table (S7t T5) when one carries them; null = the named constants measured on the
 /// DefaultHorizonRgb"/>, <see cref="HorizonRules.DefaultLowRgb"/>). The SN look
 /// match (LookMatchRules.cs, SnLookRules) keeps Subnautica's sky, fog and water as the reference and carries no colour, so
 /// nothing sets it today; the hook stays for the integrator.
 /// </summary>
        public static HazeTargetProvider Targets { get; set; }

        private readonly HazeRow[] _table = HorizonRules.DefaultTable;
        public AltitudeSettings Settings { get; private set; }
        private readonly string _envNote;

        // far plane (per camera root: a new world brings a new SNCameraRoot with the prefab's value)
        private SNCameraRoot _root;
        private int _rootId;
        private float _farBase = float.NaN, _farApplied = float.NaN;

        // the skirt
        private GameObject _skirtGo;
        private Mesh _skirtMesh;
        private Material _skirtMat;
        private MeshRenderer _skirtRenderer;
        private Color32[] _colours;
        private readonly Color32[] _rowColour = new Color32[HorizonRules.SkirtRows.Length + 1];
        private float _builtFeather = float.NaN;
        private string _shaderName = "none";
        private int _queue, _waterQueue = -1;

        // object at the player's x/z) hidden while the state is Above; restored with the rest
        private VFXClouds _clouds;
        private readonly List<Renderer> _cloudRenderers = new List<Renderer>();
        private bool _cloudsHidden;
        private ulong _nextCloudLookupMs;
        public const string EnvClouds = "DEEPENGINEERS_S7T_CLOUDS";
        public long CloudHides, CloudShows;
        private ulong _nextQueueTryMs;
        private bool _skirtFailed;
        private static FieldInfo _surfaceMaterialField;
        private static bool _surfaceFieldLooked;

        // this frame
        private AltitudeFrame _f;
        private int _lastState = -1;
        private double _todR = 1, _todG = 1, _todB = 1;
        private HazeRgb _horizonRgb, _lowRgb;
        private string _colourSource = "constants";
        private readonly int _fogId = Shader.PropertyToID("_SkyFogDensity");
        private bool _fogWritten;

        private Camera.CameraCallback _onPreCull, _onPostRender;
        private bool _subscribed, _disabled, _disposed;
        private string _disabledWhy;
        private ulong _nextRowMs;

        public long Frames, FarSets, FarRestores, SkirtBuilds, ColourWrites, FogWrites, PreCulls, Errors;
        public double MsMax;

        public AltitudeHost()
        {
            string err;
            Settings = HorizonRules.FromEnv(Env(HorizonRules.EnvHorizon), Env(HorizonRules.EnvHaze), Env(ImpostorRules.EnvImpostor), out err);
            bool cloudsOn;
            if (HorizonRules.TryOnOff(Env(EnvClouds) ?? "on", out cloudsOn)) Settings.Clouds = cloudsOn;
            S7tImpostors.Settings = Settings;
            _envNote = err;
            _horizonRgb = Settings.HorizonRgb;
            _lowRgb = Settings.LowRgb;
        }

        private static string Env(string name)
        {
            try { return Environment.GetEnvironmentVariable(name); } catch (Exception) { return null; }
        }

        public void Start()
        {
            _onPreCull = OnPreCull;
            _onPostRender = OnPostRender;
            Camera.onPreCull += _onPreCull;
            Camera.onPostRender += _onPostRender;
            _subscribed = true;
            if (_envNote != null) DeLog.Warn("DE.S7t altitude env: " + _envNote);
            DeLog.Info("DE.S7t altitude " + Settings.Describe() + " (" + HorizonRules.EnvHorizon + "=" + (Env(HorizonRules.EnvHorizon) ?? "unset") +
                       " " + HorizonRules.EnvHaze + "=" + (Env(HorizonRules.EnvHaze) ?? "unset") + " " + ImpostorRules.EnvImpostor + "=" +
                       (Env(ImpostorRules.EnvImpostor) ?? "unset") + "; de.s7t horizon|haze|impostor|alt; S7t T2/T3)");
        }

        // ============================================================================================ per frame

 /// <summary>Per frame from the bridge (main thread, Update): the frame's values, the far plane, the skirt's colours. Never throws.</summary>
        public void Tick(ulong nowMs)
        {
            if (_disabled || _disposed) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                Frames++;
                SNCameraRoot root = SNCameraRoot.main;
                Camera cam = root != null ? root.mainCam : null;
                if (root == null || cam == null)
                {
                    _root = null;
                    _farApplied = _farBase = float.NaN;
                    _f = default(AltitudeFrame);
                    Row(nowMs, false);
                    return;
                }
                int id = root.GetInstanceID();
                if (_root == null || id != _rootId)
                {
                    _root = root;
                    _rootId = id;
                    _farBase = cam.farClipPlane; // the prefab's own (1,700 m in Subnautica 1.22.83031)
                    _farApplied = _farBase;
                    DeLog.Info("DE.S7t altitude camera root " + id + ": Subnautica's far plane " + F(_farBase) + " m (restored below " + F(Settings.MinAltM) + " m)");
                }
                Vector3 p = cam.transform.position;
                double alt = p.y - Ocean.GetOceanLevel();
                bool impostors = S7tImpostors.Count > 0;
                _f = HorizonRules.Frame(Settings, _table, alt, _farBase, impostors);
                if (!S2Host.PictureSite && _f.State == AltitudeFrame.Above)
                {
                    // under the Waterline policy Subnautica's picture never shows above the sea: its own look stays
                    _f.State = AltitudeFrame.Off;
                    _f.FarM = _farBase;
                    _f.SkirtOn = false;
                    _f.FogMul = 1.0;
                }
                if (S8aOffSite.Active) _f.SkirtOn = false;   // terrain 4 "chart.offsite": no horizon skirt on a treadmill chart
                ApplyFar(root, (float)_f.FarM);
                ApplyClouds(nowMs, Settings.Clouds && _f.State == AltitudeFrame.Above);
                if (_f.SkirtOn)
                {
                    EnsureSkirt(nowMs);
                    if (_skirtRenderer != null) UpdateColours();
                }
                if (_f.State != _lastState)
                {
                    DeLog.Info("DE.S7t horizon " + AltitudeFrame.StateName(_f.State) + " at " + F(alt) + " m: far " + F(_f.FarM) + " m (horizon " + F(_f.HorizonM) +
                               " m), skirt " + (_f.SkirtOn ? "R " + F(_f.SkirtRadiusM) + " m" : "off") + ", fogMul " + F(_f.FogMul));
                    _lastState = _f.State;
                    Row(nowMs, true);
                }
                else Row(nowMs, false);
            }
            catch (Exception e)
            {
                Fail("tick", e);
            }
            finally
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (ms > MsMax) MsMax = ms;
            }
        }

        private void ApplyFar(SNCameraRoot root, float far)
        {
            if (float.IsNaN(far) || far <= 0) return;
            if (!float.IsNaN(_farApplied) && Math.Abs(far - _farApplied) < FarEpsilonM) return;
            root.SetFarPlaneDistance(far); // SNCameraRoot.SetFarPlaneDistance(float): main, GUI and IMGUI cameras
            if (!float.IsNaN(_farBase) && Math.Abs(far - _farBase) < FarEpsilonM) FarRestores++;
            else FarSets++;
            _farApplied = far;
        }

        /// <summary>The main camera's pre-cull (after Update and uSky's own globals): the skirt at the camera, the haze density.</summary>
        private void OnPreCull(Camera cam)
        {
            if (_disabled || _disposed || cam == null || _root == null || cam != _root.mainCam) return;
            try
            {
                PreCulls++;
                bool show = _f.SkirtOn && _skirtRenderer != null && _skirtGo != null;
                if (show)
                {
                    Vector3 p = cam.transform.position;
                    float alt = (float)(p.y - Ocean.GetOceanLevel());
                    if (alt < 0.01f) show = false;
                    else
                    {
                        float r = (float)_f.SkirtRadiusM;
                        Transform t = _skirtGo.transform;
                        t.position = p;
                        t.localScale = new Vector3(r, alt, r);
                    }
                }
                if (_skirtRenderer != null && _skirtRenderer.enabled != show) _skirtRenderer.enabled = show; // on for the main camera only
                ApplyFog();
            }
            catch (Exception e)
            {
                Fail("preCull", e);
            }
        }

        private void OnPostRender(Camera cam)
        {
            if (_skirtRenderer == null || _root == null || cam != _root.mainCam) return;
            try { if (_skirtRenderer.enabled) _skirtRenderer.enabled = false; } // never drawn by another camera (probes, captures)
            catch (Exception) { }
        }

        /// <summary>uSky writes <c>_SkyFogDensity</c> in its Update (skyFogDensity x (1 - eclipse)); a multiplier other than 1 re-writes it here.</summary>
        private void ApplyFog()
        {
            uSkyManager sky = uSkyManager.main;
            bool want = _f.State == AltitudeFrame.Above && Math.Abs(_f.FogMul - 1.0) > 1e-3;
            if (sky == null) { _fogWritten = false; return; }
            if (want)
            {
                Shader.SetGlobalFloat(_fogId, (float)(sky.skyFogDensity * (1f - sky.Eclipse()) * _f.FogMul));
                FogWrites++;
                _fogWritten = true;
            }
            else if (_fogWritten)
            {
                Shader.SetGlobalFloat(_fogId, sky.skyFogDensity * (1f - sky.Eclipse())); // uSky's own value back at once
                _fogWritten = false;
            }
        }

        // ============================================================================================ the skirt

        private void EnsureSkirt(ulong nowMs)
        {
            if (_skirtFailed) return;
            if (_skirtGo == null) // never built, or the scene took it (quit to menu): built again in the next world
            {
                _skirtRenderer = null;
                if (_skirtMesh == null) _skirtMesh = BuildMesh((float)Settings.Feather);
                if (_skirtMat == null) _skirtMat = MakeMaterial();
                if (_skirtMat == null) { _skirtFailed = true; return; }
                _skirtGo = new GameObject("DE.S7t.Horizon");
                _skirtGo.layer = 0;
                _skirtGo.AddComponent<MeshFilter>().sharedMesh = _skirtMesh;
                MeshRenderer r = _skirtGo.AddComponent<MeshRenderer>();
                r.sharedMaterial = _skirtMat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                r.allowOcclusionWhenDynamic = false;
                r.enabled = false;
                _skirtRenderer = r;
                for (int i = 0; i < _rowColour.Length; i++) _rowColour[i] = new Color32(0, 0, 0, 0);
                SkirtBuilds++;
                DeLog.Info("DE.S7t horizon skirt built (" + _skirtMesh.vertexCount + " vertices, shader " + _shaderName + ", queue " + _queue +
                           (_waterQueue >= 0 ? ", water queue " + _waterQueue : ", water queue not read yet") + ")");
            }
            if (Math.Abs(_builtFeather - (float)Settings.Feather) > 1e-4f) SetFeather((float)Settings.Feather);
            if (_waterQueue < 0 && nowMs >= _nextQueueTryMs)
            {
                _nextQueueTryMs = nowMs + QueueRetryMs;
                int wq = WaterQueue();
                if (wq >= 0)
                {
                    _waterQueue = wq;
                    _queue = Math.Min(3999, Math.Max(3001, wq + 1));
                    _skirtMat.renderQueue = _queue;
                    DeLog.Info("DE.S7t horizon skirt queue " + _queue + " (the water surface's material queue " + wq + " + 1)");
                }
            }
        }

        /// <summary>The ring: rows (feather, then <see cref="HorizonRules.SkirtRows"/>) x (columns + 1), unit radius, y = -v.</summary>
        private Mesh BuildMesh(float feather)
        {
            int rows = HorizonRules.SkirtRows.Length + 1, cols = HorizonRules.SkirtColumns + 1;
            var v = new Vector3[rows * cols];
            _colours = new Color32[rows * cols];
            var tris = new int[(rows - 1) * HorizonRules.SkirtColumns * 6];
            for (int r = 0; r < rows; r++)
            {
                float y = r == 0 ? feather : -HorizonRules.SkirtRows[r - 1];
                for (int c = 0; c < cols; c++)
                {
                    double a = 2.0 * Math.PI * c / HorizonRules.SkirtColumns;
                    v[r * cols + c] = new Vector3((float)Math.Cos(a), y, (float)Math.Sin(a));
                }
            }
            int k = 0;
            for (int r = 0; r + 1 < rows; r++)
                for (int c = 0; c < HorizonRules.SkirtColumns; c++)
                {
                    int i0 = r * cols + c, i1 = i0 + 1, i2 = i0 + cols, i3 = i2 + 1;
                    tris[k++] = i0; tris[k++] = i2; tris[k++] = i1;
                    tris[k++] = i1; tris[k++] = i2; tris[k++] = i3;
                }
            var m = new Mesh { name = "DE.S7t.HorizonSkirt" };
            m.MarkDynamic();
            m.vertices = v;
            m.colors32 = _colours;
            m.triangles = tris;
            m.bounds = new Bounds(Vector3.zero, new Vector3(2.2f, 2.2f + 2f * Math.Max(feather, 0.01f), 2.2f));
            _builtFeather = feather;
            return m;
        }

        /// <summary>The feather row (above the horizon) follows <c>feather</c>;</summary>
        private void SetFeather(float feather)
        {
            if (_skirtMesh == null) return;
            Vector3[] v = _skirtMesh.vertices;
            int cols = HorizonRules.SkirtColumns + 1;
            for (int c = 0; c < cols; c++) v[c].y = feather;
            _skirtMesh.vertices = v;
            _skirtMesh.bounds = new Bounds(Vector3.zero, new Vector3(2.2f, 2.2f + 2f * Math.Max(feather, 0.01f), 2.2f));
            _builtFeather = feather;
        }

        /// <summary>Unity's own shaders by name (nothing loaded from either game's assets): Sprites/Default, else Hidden/Internal-Colored.</summary>
        private Material MakeMaterial()
        {
            Shader s = null;
            bool colored = false;
            try { s = Shader.Find("Sprites/Default"); } catch (Exception) { }
            if (s != null && !s.isSupported) s = null;
            if (s == null)
            {
                try { s = Shader.Find("Hidden/Internal-Colored"); } catch (Exception) { }
                if (s != null && !s.isSupported) s = null;
                colored = s != null;
            }
            if (s == null)
            {
                DeLog.Warn("DE.S7t horizon skirt OFF: neither Sprites/Default nor Hidden/Internal-Colored is available (the far plane still follows the altitude)");
                return null;
            }
            var m = new Material(s) { name = "DE.S7t horizon (" + s.name + ")" };
            if (colored)
            {
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.SetInt("_ZTest", (int)CompareFunction.LessEqual);
                m.SetInt("_Cull", (int)CullMode.Off);
            }
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            _queue = 3001;
            m.renderQueue = _queue;
            _shaderName = s.name;
            return m;
        }

        /// <summary>The water surface material's queue; -1 until it exists.</summary>
        private static int WaterQueue()
        {
            try
            {
                if (!_surfaceFieldLooked)
                {
                    _surfaceFieldLooked = true;
                    _surfaceMaterialField = typeof(WaterSurface).GetField("surfaceMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_surfaceMaterialField == null) DeLog.Warn("DE.S7t horizon: WaterSurface.surfaceMaterial missing (the skirt stays at queue 3001)");
                }
                WaterSurface w = WaterSurface.Get();
                if (w == null || _surfaceMaterialField == null) return -1;
                var mat = _surfaceMaterialField.GetValue(w) as Material;
                return mat != null ? mat.renderQueue : -1;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>Per frame: the rows' colours from the frame, the targets and the time of day; the mesh written only when a row changed.</summary>
        private void UpdateColours()
        {

            HazeRgb h = Settings.HorizonRgb, l = Settings.LowRgb;
            _colourSource = "constants";
            HazeTargetProvider src = Targets;
            if (src != null && !(Settings.HorizonRgbSet && Settings.LowRgbSet))
            {
                HazeRgb th, tl;
                bool ok = false;
                try { ok = src(out th, out tl); } catch (Exception) { ok = false; th = tl = default(HazeRgb); }
                if (ok)
                {
                    if (!Settings.HorizonRgbSet) h = th;
                    if (!Settings.LowRgbSet) l = tl;
                    _colourSource = "lookmatch";
                }
            }
            if (Settings.HorizonRgbSet || Settings.LowRgbSet) _colourSource = _colourSource == "lookmatch" ? "lookmatch+owner" : "owner";
            _horizonRgb = h;
            _lowRgb = l;
            _todR = _todG = _todB = 1.0;
            uSkyManager sky = uSkyManager.main;
            if (Settings.Tod && sky != null && sky.meanSkyColor != null)
            {
                Color now = sky.meanSkyColor.Evaluate(Mathf.Repeat(sky.Timeline01, 1f));
                Color noon = sky.meanSkyColor.Evaluate(0.5f);
                HorizonRules.TodFactor(now.r, now.g, now.b, noon.r, noon.g, noon.b, Settings.NightFloor, out _todR, out _todG, out _todB);
            }
            bool changed = false;
            for (int i = 0; i < _rowColour.Length; i++)
            {
                double v = i == 0 ? -1.0 : HorizonRules.SkirtRows[i - 1];
                double r, g, b;
                HorizonRules.RowColour(l, h, v, _todR, _todG, _todB, Settings.Gain, out r, out g, out b);
                double a = HorizonRules.RowAlpha(v, _f.FadeRatio, _f.Alpha);
                var c = new Color32(B8(r), B8(g), B8(b), B8(a));
                Color32 o = _rowColour[i];
                if (c.r != o.r || c.g != o.g || c.b != o.b || c.a != o.a) { _rowColour[i] = c; changed = true; } // field compare: no boxing per frame
            }
            if (!changed || _colours == null) return;
            int cols = HorizonRules.SkirtColumns + 1;
            for (int i = 0; i < _rowColour.Length; i++)
            {
                Color32 c = _rowColour[i];
                int o = i * cols;
                for (int j = 0; j < cols; j++) _colours[o + j] = c;
            }
            _skirtMesh.colors32 = _colours;
            ColourWrites++;
        }

        private static byte B8(double x)
        {
            if (double.IsNaN(x) || x <= 0) return 0;
            if (x >= 1) return 255;
            return (byte)Math.Round(x * 255.0);
        }

        // ============================================================================================ C11

 /// <summary>
 /// <c>de.s7t</c>: this side answers
 /// <c>horizon on|off</c>, <c>haze [key=value...|reset]</c>, <c>impostor [on|off] [key=value...]</c> and <c>alt</c>, as
 /// chat words (<c>/de sn s7t haze alpha=0.8</c> -> <c>{argv: [...]}</c>) or as keys (<c>{horizon: "on"}</c>,
 /// <c>{haze: "alpha=0.8 fadeStart=0.5"}</c>, <c>{impostor: "off"}</c>, <c>{alt: true}</c>). False for another feature's words.
 /// </summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong nowMs)
        {
            if (req == null || req.Cmd != Cmd) return false;
            var raw = req.Args as Dictionary<string, object>;
            Dictionary<string, object> a = raw;
            object v;
            System.Collections.IList list = null;
            if (raw != null && raw.TryGetValue("argv", out v)) list = v as System.Collections.IList;
            else if (raw == null) list = req.Args as System.Collections.IList; // a bare word list
            List<string> words = null;
            if (list != null)
            {
                words = new List<string>();
                foreach (object o in list) words.Add(o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "");
            }
            string verb = null, arg = null;
            var kvs = new List<KeyValuePair<string, string>>();
            string error = null;
            if (words != null)
            {
                if (words.Count == 0 || !IsMine(words[0])) return false;
                verb = Norm(words[0]);
                for (int i = 1; i < words.Count; i++)
                {
                    string w = words[i];
                    int eq = w.IndexOf('=');
                    if (eq > 0) kvs.Add(new KeyValuePair<string, string>(w.Substring(0, eq), w.Substring(eq + 1)));
                    else if (w.Equals("status", StringComparison.OrdinalIgnoreCase)) continue; // the reply always carries the status
                    else if (arg == null) arg = w;
                    else { error = "unexpected word '" + w + "'"; break; }
                }
            }
            else
            {
                if (a == null) return false;
                foreach (string k in new[] { "horizon", "haze", "impostor", "impostors", "alt", "altitude" })
                    if (a.TryGetValue(k, out v))
                    {
                        verb = Norm(k);
                        if (v is bool) arg = verb == "haze" || verb == "alt" ? null : ((bool)v ? "on" : "off"); // {haze: true, alpha: 0.8}
                        else arg = v == null ? null : (v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture));
                        if (arg != null && (arg.Length == 0 || arg.Equals("status", StringComparison.OrdinalIgnoreCase))) arg = null;
                        break;
                    }
                if (verb == null && a.TryGetValue("action", out v) && IsMine(v as string)) verb = Norm((string)v);
                if (verb == null) return false;
                foreach (KeyValuePair<string, object> kv in a)
                {
                    if (kv.Key == "action" || kv.Key == "argv" || Norm(kv.Key) == verb) continue;
                    if (Array.Exists(AltitudeSettings.Keys, x => x.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                        kvs.Add(new KeyValuePair<string, string>(kv.Key, kv.Value is bool ? ((bool)kv.Value ? "on" : "off") : Convert.ToString(kv.Value, CultureInfo.InvariantCulture)));
                }
                if (verb == "haze" && arg != null && arg.IndexOf('=') > 0)
                {
                    string pe;
                    kvs.InsertRange(0, HorizonRules.ParseKv(arg, out pe));
                    if (pe != null) error = pe;
                    arg = null;
                }
            }
            reply.Str("cmd", req.Cmd).Str("side", "sn").Str("feature", "altitude");
            if (error == null) error = Apply(verb, arg, kvs);
            if (_disabled && error == null) error = "the altitude picture is disabled after an exception: " + _disabledWhy;
            reply.Bool("ok", error == null);
            if (error != null) reply.Str("error", error + " (usage: " + Usage + ")");
            Status(reply);
            Row(nowMs, true);
            return true;
        }

        public const string Usage = "de.s7t horizon on|off | haze [reset | key=value ...] | impostor [on|off] [minPart= perFrame= reach= impostorFar=] | alt " +
                                    "(haze keys: alpha fadeStart fadeEnd farK farMax fogMul feather horizonRgb=r,g,b lowRgb=r,g,b tod night gain minAlt; 'table' resets a table key)";

        private static bool IsMine(string w)
        {
            if (w == null) return false;
            switch (w.Trim().ToLowerInvariant())
            {
                case "horizon": case "haze": case "impostor": case "impostors": case "alt": case "altitude": return true;
                default: return false;
            }
        }

        private static string Norm(string w)
        {
            string s = (w ?? "").Trim().ToLowerInvariant();
            return s == "impostors" ? "impostor" : (s == "altitude" ? "alt" : s);
        }

        private string Apply(string verb, string arg, List<KeyValuePair<string, string>> kvs)
        {
            AltitudeSettings trial = Settings.Clone();
            bool b;
            switch (verb)
            {
                case "horizon":
                    if (arg != null) { if (!HorizonRules.TryOnOff(arg, out b)) return "horizon: on|off"; trial.Horizon = b; }
                    break;
                case "impostor":
                    if (arg != null) { if (!HorizonRules.TryOnOff(arg, out b)) return "impostor: on|off"; trial.Impostors = b; }
                    break;
                case "clouds":
                    if (arg != null) { if (!HorizonRules.TryOnOff(arg, out b)) return "clouds: on|off"; trial.Clouds = b; }
                    break;
                case "haze":
                    if (arg != null)
                    {
                        if (!arg.Equals("reset", StringComparison.OrdinalIgnoreCase)) return "haze: reset or key=value";
                        string e;
                        AltitudeSettings env = HorizonRules.FromEnv(null, Env(HorizonRules.EnvHaze), null, out e);
                        env.Horizon = trial.Horizon;
                        env.Impostors = trial.Impostors;
                        env.Clouds = trial.Clouds;
                        env.MinPartFrac = trial.MinPartFrac; env.CellsPerFrame = trial.CellsPerFrame; env.ReachM = trial.ReachM; env.ImpostorFarM = trial.ImpostorFarM;
                        trial = env;
                    }
                    break;
                case "alt":
                    break;
                default:
                    return "unknown action '" + verb + "'";
            }
            string err = trial.Apply(kvs);
            if (err != null) return err;
            bool impostorsChanged = trial.Impostors != Settings.Impostors;
            Settings = trial;
            S7tImpostors.Settings = trial; // the mirror's hooks see the change at their next frame (a mode change rebuilds those grids)
            if (kvs.Count > 0 || arg != null)
                DeLog.Info("DE.S7t altitude " + Settings.Describe() + " (de.s7t " + verb + ")" + (impostorsChanged ? " impostors " + (Settings.Impostors ? "on" : "off") : ""));
            return null;
        }

        private void Status(JsonObjectWriter w)
        {
            w.Str("state", AltitudeFrame.StateName(_f.State)).Num("altM", _f.AltM).Num("horizonM", _f.HorizonM).Num("farBaseM", (double)_farBase)
             .Num("farM", _f.FarM).Bool("skirtOn", _f.SkirtOn).Num("skirtRadiusM", _f.SkirtRadiusM).Num("fadeRatio", _f.FadeRatio)
             .Num("alpha", _f.Alpha).Num("fogMul", _f.FogMul).Str("horizonRgb", _horizonRgb.ToString()).Str("lowRgb", _lowRgb.ToString())
             .Str("colourSource", _colourSource).Str("shader", _shaderName).Num("queue", (long)_queue).Num("waterQueue", (long)_waterQueue)
             .Num("impostors", (long)S7tImpostors.Count).Num("impostorCellsPosted", S7tImpostors.CellsPosted).Num("impostorCellsDeferred", S7tImpostors.CellsDeferred)
             .Bool("disabled", _disabled).Str("settings", Settings.Describe())
             .Str("line", "DE.S7t " + AltitudeFrame.StateName(_f.State) + " alt " + F(_f.AltM) + " m far " + F(_f.FarM) + " m (base " + F(_farBase) + ") skirt " +
                          (_f.SkirtOn ? "R " + F(_f.SkirtRadiusM) + " m" : "off") + " fogMul " + F(_f.FogMul) + " colours " + _colourSource +
                          " impostors " + S7tImpostors.Count + (_disabled ? " DISABLED" : ""));
        }

        // ============================================================================================ rows

 /// <summary>s7t.alt and s7t.imp at 1 Hz (and at once on a state change or a command).</summary>
        private void Row(ulong nowMs, bool now)
        {
            if (!now && nowMs < _nextRowMs) return;
            _nextRowMs = nowMs + RowEveryMs;
            JsonOut j = S5Rows.Begin("s7t.alt");
            j.Str("state", AltitudeFrame.StateName(_f.State)).Num("altM", _f.AltM).Num("horizonM", _f.HorizonM).Num("farBaseM", (double)_farBase)
             .Num("farM", _f.FarM).Num("farApplied", (double)_farApplied).Bool("impostorFar", _f.ImpostorFar).Bool("pictureSite", S2Host.PictureSite)
             .Bool("skirtOn", _f.SkirtOn).Bool("skirtBuilt", _skirtGo != null).Num("skirtRadiusM", _f.SkirtRadiusM).Num("fadeRatio", _f.FadeRatio)
             .Num("alpha", _f.Alpha).Num("feather", _f.Feather).Num("fogMul", _f.FogMul).Bool("fogWritten", _fogWritten)
             .Str("horizonRgb", _horizonRgb.ToString()).Str("lowRgb", _lowRgb.ToString()).Str("colourSource", _colourSource)
             .Num("todR", _todR).Num("todG", _todG).Num("todB", _todB).Str("shader", _shaderName).Num("queue", (long)_queue).Num("waterQueue", (long)_waterQueue)
             .Num("frames", Frames).Num("preCulls", PreCulls).Num("farSets", FarSets).Num("farRestores", FarRestores).Num("skirtBuilds", SkirtBuilds)
             .Num("colourWrites", ColourWrites).Num("fogWrites", FogWrites).Num("errors", Errors).Num("msMax", MsMax).Bool("disabled", _disabled);
            S5Rows.End(j);
            MsMax = 0;
            if (S7tImpostors.Count > 0 || S7tImpostors.CellsPosted > 0 || S7tImpostors.SwapsToFull > 0 || now)
            {
                JsonOut k = S5Rows.Begin("s7t.imp");
                k.Bool("on", Settings.Impostors).Num("impostors", (long)S7tImpostors.Count).Num("cellsPosted", S7tImpostors.CellsPosted)
                 .Num("cellsDeferred", S7tImpostors.CellsDeferred).Num("partsKept", S7tImpostors.PartsKept).Num("partsSkipped", S7tImpostors.PartsSkipped)
                 .Num("childrenRemoved", S7tImpostors.ChildrenRemoved).Num("lightsSkipped", S7tImpostors.LightsSkipped).Num("effectsSkipped", S7tImpostors.EffectsSkipped)
                 .Num("swapsToCoarse", S7tImpostors.SwapsToCoarse).Num("swapsToFull", S7tImpostors.SwapsToFull).Num("slotsCreated", S7tImpostors.SlotsCreated)
                 .Num("culled", S7tImpostors.Culled).Num("minPart", Settings.MinPartFrac).Num("perFrame", (long)Settings.CellsPerFrame).Num("reachM", Settings.ReachM);
                S5Rows.End(k);
            }
        }

        // ============================================================================================ teardown

        private void Fail(string where, Exception e)
        {
            Errors++;
            _disabled = true;
            _disabledWhy = where + ": " + e.GetType().Name + ": " + e.Message;
            DeLog.Error("DE.S7t altitude DISABLED after an exception in " + where + " (the far plane restored; everything else keeps running): " + e);
            try { Restore("disabled"); } catch (Exception) { }
        }

        /// <summary>Subnautica's own far plane back, the skirt hidden, uSky's density back.</summary>
 /// <summary>Hides or shows Subnautica's cloud layer (VFXClouds and its renderers); the object is looked up every 5 s while missing.</summary>
        private void ApplyClouds(ulong nowMs, bool hide)
        {
            if (hide == _cloudsHidden && (_clouds != null || !hide)) return;
            if (_clouds == null)
            {
                if (!hide) { _cloudsHidden = false; return; }
                if (nowMs < _nextCloudLookupMs) return;
                _nextCloudLookupMs = nowMs + 5000;
                try { _clouds = UnityEngine.Object.FindObjectOfType<VFXClouds>(); } catch (Exception) { _clouds = null; }
                if (_clouds == null) return;
                _cloudRenderers.Clear();
                _cloudRenderers.AddRange(_clouds.GetComponentsInChildren<Renderer>(true));
            }
            for (int i = 0; i < _cloudRenderers.Count; i++)
            {
                Renderer r = _cloudRenderers[i];
                if (r != null) r.enabled = !hide;
            }
            _cloudsHidden = hide;
            if (hide) CloudHides++; else CloudShows++;
            DeLog.Info("DE.S7t clouds " + (hide ? "hidden" : "shown") + " (" + _cloudRenderers.Count + " renderers of VFXClouds; de.s7t clouds on|off)");
        }

        private void Restore(string why)
        {
            if (_cloudsHidden) ApplyClouds(0, false);
            if (_root != null && !float.IsNaN(_farBase) && !float.IsNaN(_farApplied) && Math.Abs(_farApplied - _farBase) >= FarEpsilonM)
            {
                _root.SetFarPlaneDistance(_farBase);
                _farApplied = _farBase;
                FarRestores++;
                DeLog.Info("DE.S7t altitude far plane back to " + F(_farBase) + " m (" + why + ")");
            }
            if (_skirtRenderer != null) _skirtRenderer.enabled = false;
            if (_fogWritten)
            {
                uSkyManager sky = uSkyManager.main;
                if (sky != null) Shader.SetGlobalFloat(_fogId, sky.skyFogDensity * (1f - sky.Eclipse()));
                _fogWritten = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Restore("dispose"); } catch (Exception) { }
            if (_subscribed)
            {
                try { Camera.onPreCull -= _onPreCull; } catch (Exception) { }
                try { Camera.onPostRender -= _onPostRender; } catch (Exception) { }
                _subscribed = false;
            }
            try { if (_skirtGo != null) UnityEngine.Object.Destroy(_skirtGo); } catch (Exception) { }
            try { if (_skirtMesh != null) UnityEngine.Object.Destroy(_skirtMesh); } catch (Exception) { }
            try { if (_skirtMat != null) UnityEngine.Object.Destroy(_skirtMat); } catch (Exception) { }
            _skirtGo = null;
            _skirtMesh = null;
            _skirtMat = null;
            _skirtRenderer = null;
            S7tImpostors.Settings = new AltitudeSettings { Impostors = Settings.Impostors };
        }

        private static string F(double v) { return double.IsNaN(v) ? "NaN" : v.ToString("0.#", CultureInfo.InvariantCulture); }
    }
}
