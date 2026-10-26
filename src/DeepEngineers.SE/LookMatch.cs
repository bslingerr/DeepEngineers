using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using DeepEngineers.Wire;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7t T5 look match, SE's game side (ROADMAP "### S7t Seamless transition" T5; rules in <see cref="LookMatchRules"/>;
 /// CONTRACT §3.4, §1.1, §5.4 "S7t look match"). SE's look moves from its own in Space to the match set at the
 /// Approach band (shadows, tone, fog, sky), restored when the picture is off, on peer loss, under water and far from
 /// the site. Every member below was VERIFIED
 /// <list type="bullet">
 /// <item><c>Sandbox.Game.Gui.MyGuiScreenGamePlay.Draw</c>:
 /// it sends, every frame and in this order, <c>MyRenderProxy.UpdateFogSettings(ref MyRenderFogSettings)</c> (a local
 /// built from <c>MySector.FogProperties</c>), <c>UpdatePlanetSettings(ref MyRenderPlanetSettings)</c> (a local from
 /// <c>MySector.PlanetProperties</c>), <c>UpdateRenderEnvironment(ref MyEnvironmentData, bool)</c> (a local copy of
 /// <c>MySector.SunProperties.EnvironmentData</c>) and <c>SwitchPostprocessSettings(ref MyPostprocessSettings)</c> (a
 /// ref to the static <c>MyPostprocessSettingsWrapper.Settings</c>). A prefix on Draw computes the weight once per
 /// frame; prefixes on the four <c>VRageRender.MyRenderProxy</c> methods (public static,
 /// ) change the copy that goes to the renderer. The post-process prefix changes the static
 /// it is handed by ref and a finalizer puts the exact original back after the message took its copy, so
 /// <c>MyPostprocessSettingsWrapper.Settings</c> (saved by <c>MySector.SaveEnvironmentDefinition</c>) never keeps a
 /// change. Nothing of <c>MySector</c> is written, so <c>MySector.GetEnvironmentSettings</c> (the world's saved
 /// environment) is unaffected.</item>
 /// <item>The bound planet's atmosphere: <c>MyPlanet.AtmosphereSettings</c> (public get; read only) and the planet's
 /// render component <c>MyRenderComponentPlanet.UpdateAtmosphereSettings(MyAtmosphereSettings)</c>
 /// which only
 /// sends <c>MyRenderProxy.UpdateAtmosphereSettings(uint, MyAtmosphereSettings)</c>. The setter
 /// <c>MyPlanet.AtmosphereSettings</c> is never used: it writes <c>m_planetInitValues</c>, which
 /// <c>MyPlanet.GetObjectBuilder</c> saves into the world; the load reads the definition anyway
 /// Sent at most once per 10 frames, refreshed every 120 while it holds, SE's own sent back when
 /// the keys clear or the weight returns to 0.</item>
 /// </list>
 /// A hook that never runs in 300 active frames (the JIT inlined the small proxy method into Draw before the patch)
 /// is logged once (<c>DE.S7t look … never ran</c>); its keys then have no effect. Update thread only.
 /// </summary>
    internal static class LookMatch
    {
        public const string HarmonyId = "deepengineers.se.s7t.look";
        public const int InlineCheckFrames = 300;

        public static readonly LookTable Table = new LookTable();
        public static readonly List<string> Hooks = new List<string>();
        public static readonly List<string> Missing = new List<string>();
        public static string DisabledWhy;

        private static readonly LookRowCadence Cadence = new LookRowCadence();
        private static readonly int N = LookMatchRules.Keys.Length;
        private static readonly float[] OwnV = new float[N * 3], NowV = new float[N * 3];
        private static readonly bool[] Seen = new bool[N];

        // frame state
        private static double _w, _wTarget, _y = double.NaN;
        private static string _why = "init";
        private static long _lastTs;
        private static bool _active, _inlineLogged;
        private static ulong _tick;
        public static long Draws, ActiveDraws, EnvHits, FogHits, PlanetHits, PostHits, Rows, Errors;

        // post-process restore (prefix → finalizer, one call)
        private static MyPostprocessSettings _postSaved;
        private static bool _postDirty;

        // atmosphere
        private static object _atmoPlanet;
        private static uint _atmoEpoch;
        private static double[] _atmoLastSent;
        private static bool _atmoModified;
        private static int _atmoFramesSince = 1000;
        private static MethodInfo _atmoMethod;
        private static Type _atmoMethodType;
        public static long AtmoSent, AtmoDeferred, AtmoFailures, AtmoRestores;
        private static string _atmoError;

        private static readonly LookKey KContrast = K("contrast"), KSaturation = K("saturation"), KBrightness = K("brightness"), KTint = K("tint"),
            KVibrance = K("vibrance"), KBloom = K("bloom"), KExposure = K("exposure"), KEyeTau = K("eyeTau"), KWhitePoint = K("whitePoint"),
            KVignette = K("vignette"), KVignetteLength = K("vignetteLength"),
            KSun = K("sun"), KAmbient = K("ambient"), KAmbientSpec = K("ambientSpec"), KAoDir = K("aoDir"), KAoIndirect = K("aoIndirect"),
            KSkybox = K("skybox"), KEnvSkybox = K("envSkybox"), KEnvAtmo = K("envAtmo"), KShadowFade = K("shadowFade"), KSunColor = K("sunColor"),
            KFog = K("fog"), KFogDensity = K("fogDensity"), KFogColor = K("fogColor"), KFogAtmo = K("fogAtmo"), KFogSky = K("fogSky"),
            KAtmoInt = K("atmoInt"), KAtmoAmbient = K("atmoAmbient"), KAtmoDesat = K("atmoDesat"), KClouds = K("clouds"),
            KRayleigh = K("rayleigh"), KMieColor = K("mieColor"), KMieG = K("mieG"), KMieHeight = K("mieHeight"), KRayleighHeight = K("rayleighHeight"),
            KAtmoIntensity = K("atmoIntensity"), KAtmoFog = K("atmoFog");

        private static LookKey K(string name)
        {
            LookKey k = LookMatchRules.Find(name);
            if (k == null) throw new InvalidOperationException("S7t look key '" + name + "' missing from LookMatchRules.Keys");
            return k;
        }

        // ============================================================================================== patches

 /// <summary>Plugin.ApplyHarmony (S7t-look-match hook): env defaults, then the five patches (each missing target only turns its keys off).</summary>
        public static void Apply()
        {
            var applied = new List<string>();
            List<string> problems = Table.ApplyEnv(Environment.GetEnvironmentVariable(LookMatchRules.EnvVar), applied);
            DeLog.Info("DE.S7t look match: " + Table.Describe() + (applied.Count > 0 ? " (env " + LookMatchRules.EnvVar + ": " + string.Join(" ", applied.ToArray()) + ")" : "") +
                       (problems.Count > 0 ? " env problems: " + string.Join("; ", problems.ToArray()) : ""));
            var h = new Harmony(HarmonyId);
            Patch(h, "MyGuiScreenGamePlay.Draw()", AccessTools.Method(typeof(MyGuiScreenGamePlay), "Draw", Type.EmptyTypes), typeof(bool), false, "DrawPrefix", null);
            Patch(h, "MyRenderProxy.UpdateRenderEnvironment(ref MyEnvironmentData, bool)",
                AccessTools.Method(typeof(MyRenderProxy), "UpdateRenderEnvironment", new[] { typeof(MyEnvironmentData).MakeByRefType(), typeof(bool) }), typeof(void), true, "EnvPrefix", null);
            Patch(h, "MyRenderProxy.UpdateFogSettings(ref MyRenderFogSettings)",
                AccessTools.Method(typeof(MyRenderProxy), "UpdateFogSettings", new[] { typeof(MyRenderFogSettings).MakeByRefType() }), typeof(void), true, "FogPrefix", null);
            Patch(h, "MyRenderProxy.UpdatePlanetSettings(ref MyRenderPlanetSettings)",
                AccessTools.Method(typeof(MyRenderProxy), "UpdatePlanetSettings", new[] { typeof(MyRenderPlanetSettings).MakeByRefType() }), typeof(void), true, "PlanetPrefix", null);
            Patch(h, "MyRenderProxy.SwitchPostprocessSettings(ref MyPostprocessSettings)",
                AccessTools.Method(typeof(MyRenderProxy), "SwitchPostprocessSettings", new[] { typeof(MyPostprocessSettings).MakeByRefType() }), typeof(void), true, "PostPrefix", "PostFinalizer");
            DeLog.Info("DE.Harmony SE s7t look: " + Hooks.Count + " hooks (" + string.Join(", ", Hooks.ToArray()) + ")" +
                       (Missing.Count > 0 ? "; MISSING " + string.Join(", ", Missing.ToArray()) + " (those keys have no effect)" : ""));
        }

        private static void Patch(Harmony h, string name, MethodInfo target, Type ret, bool isStatic, string prefix, string finalizer)
        {
            try
            {
                if (target == null) { Missing.Add(name + " (not found)"); return; }
                if (target.ReturnType != ret || target.IsStatic != isStatic) { Missing.Add(name + " (shape changed)"); return; }
                h.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(typeof(LookMatch).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(typeof(LookMatch).GetMethod(finalizer, BindingFlags.Static | BindingFlags.NonPublic)) : null);
                Hooks.Add(name);
            }
            catch (Exception e) { Missing.Add(name + " (" + e.GetType().Name + ": " + e.Message + ")"); }
        }

        // ============================================================================================== per frame

        private static void DrawPrefix()
        {
            if (DisabledWhy != null) { RestoreAtmoWhenDisabled(); return; } // S7t integrator: a disabled match still sends the planet's own sky back once
            try { Frame(); }
            catch (Exception e)
            {
                Errors++;
                _active = false;
                DisabledWhy = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.S7t look DISABLED after " + DisabledWhy + " (SE's own look stays; the rest of the plugin runs)");
            }
        }

        private static void Frame()
        {
            Draws++;
            long ts = Stopwatch.GetTimestamp();
            double dt = _lastTs == 0 ? 0.0 : (ts - _lastTs) / (double)Stopwatch.Frequency;
            if (dt > 0.25) dt = 0.25; // a hitch or a pause never jumps the ease
            _lastTs = ts;

            LookGateInputs gi = GateInputs();
            string why;
            double target = LookMatchRules.Target(ref gi, out why);
            double prev = _w;
            _wTarget = target;
            _w = LookMatchRules.Ease(_w, target, dt, Table.EaseS);
            _y = gi.Y;
            _active = _w > 0.0;
            if (_active) ActiveDraws++;
            bool edge = why != _why || (prev > 0.0) != (_w > 0.0) || ((prev >= 1.0) != (_w >= 1.0));
            _why = why;

            Atmosphere();
            InlineCheck();
            ulong now = Wire.Native.Win32.TickCount64();
            if (Cadence.Due(now, _active, edge)) Row(edge ? "edge" : "periodic");
        }

        private static LookGateInputs GateInputs()
        {
            var gi = new LookGateInputs
            {
                Enabled = Table.Enabled,
                Force = Table.Force,
                RampM = Table.RampM,
                BandEnterY = ViewThresholds.ApproachEnterY,
                Y = double.NaN,
            };
            ViewStateDriver d = S2Host.Driver;
            if (d == null || !d.Active || !GameState.SessionExists || !GameState.GameplayLoadingDone) return gi;
            ulong tick = GameState.GameplayTick;
            _tick = tick;
            ulong age = tick >= d.Tick ? tick - d.Tick : d.Tick - tick;
            if (age > LookMatchRules.DriverStaleTicks) return gi;
            ViewInputs li = d.LastInputs;
            ViewOutputs lo = d.Last;
            gi.InWorld = true;
            gi.BridgeUp = li.BridgeUp;
            gi.DiveZone = li.OnSite && SiteZones.IsDive(li.Zone);
            gi.PeerAlive = li.Peer == PeerState.Alive;
            gi.SitePolicy = lo.PicturePolicy == ViewPicture.Site;
            gi.Y = d.Y;
            return gi;
        }

        private static void InlineCheck()
        {
            if (_inlineLogged || ActiveDraws < InlineCheckFrames) return;
            _inlineLogged = true;
            var never = new List<string>();
            if (Hooks.Count > 0)
            {
                if (EnvHits == 0 && (Table.AnySet(LookGroup.Env))) never.Add("UpdateRenderEnvironment (sun/ambient/skybox keys)");
                if (FogHits == 0 && Table.AnySet(LookGroup.Fog)) never.Add("UpdateFogSettings (fog keys)");
                if (PlanetHits == 0 && Table.AnySet(LookGroup.Planet)) never.Add("UpdatePlanetSettings (atmoInt/atmoAmbient/atmoDesat/clouds)");
                if (PostHits == 0 && Table.AnySet(LookGroup.Post)) never.Add("SwitchPostprocessSettings (post-process keys)");
            }
            DeLog.Info(never.Count == 0
                ? "DE.S7t look hooks all ran within " + InlineCheckFrames + " active frames (env " + EnvHits + ", fog " + FogHits + ", planet " + PlanetHits + ", post " + PostHits + ")"
                : "DE.S7t look: " + string.Join(", ", never.ToArray()) + " never ran in " + InlineCheckFrames + " active frames (inlined by the JIT before the patch?): those keys have no effect");
        }

        // ---------------------------------------------------------------------------------------------- render copies

        private static float B(LookKey k, int ch, float own)
        {
            double[] t = Table.Get(k);
            if (t == null) return own;
            float v = LookMatchRules.Blend(k, t, ch, own, _w);
            int i = k.Index * 3 + ch;
            OwnV[i] = own;
            NowV[i] = v;
            Seen[k.Index] = true;
            return v;
        }

        private static Vector3 B3(LookKey k, Vector3 own)
        {
            if (Table.Get(k) == null) return own;
            return new Vector3(B(k, 0, own.X), B(k, 1, own.Y), B(k, 2, own.Z));
        }

        private static void EnvPrefix(ref MyEnvironmentData data)
        {
            if (!_active) return;
            try
            {
                EnvHits++;
                data.EnvironmentLight.SunDiffuseFactor = B(KSun, 0, data.EnvironmentLight.SunDiffuseFactor);
                data.EnvironmentLight.AmbientDiffuseFactor = B(KAmbient, 0, data.EnvironmentLight.AmbientDiffuseFactor);
                data.EnvironmentLight.AmbientSpecularFactor = B(KAmbientSpec, 0, data.EnvironmentLight.AmbientSpecularFactor);
                data.EnvironmentLight.AODirLight = B(KAoDir, 0, data.EnvironmentLight.AODirLight);
                data.EnvironmentLight.AOIndirectLight = B(KAoIndirect, 0, data.EnvironmentLight.AOIndirectLight);
                data.EnvironmentLight.SkyboxBrightness = B(KSkybox, 0, data.EnvironmentLight.SkyboxBrightness);
                data.EnvironmentLight.EnvSkyboxBrightness = B(KEnvSkybox, 0, data.EnvironmentLight.EnvSkyboxBrightness);
                data.EnvironmentLight.EnvAtmosphereBrightness = B(KEnvAtmo, 0, data.EnvironmentLight.EnvAtmosphereBrightness);
                data.EnvironmentLight.ShadowFadeoutMultiplier = B(KShadowFade, 0, data.EnvironmentLight.ShadowFadeoutMultiplier);
                data.EnvironmentLight.SunColorRaw = B3(KSunColor, data.EnvironmentLight.SunColorRaw);
            }
            catch (Exception e) { HookError("env", e); }
        }

        private static void FogPrefix(ref MyRenderFogSettings settings)
        {
            if (!_active) return;
            try
            {
                FogHits++;
                settings.FogMultiplier = B(KFog, 0, settings.FogMultiplier);
                settings.FogDensity = B(KFogDensity, 0, settings.FogDensity);
                settings.FogAtmo = B(KFogAtmo, 0, settings.FogAtmo);
                settings.FogSkybox = B(KFogSky, 0, settings.FogSkybox);
                double[] fc = Table.Get(KFogColor);
                if (fc != null)
                {
                    Color c = settings.FogColor;
                    byte r = LookMatchRules.BlendByte(KFogColor, fc, 0, c.R, _w), g = LookMatchRules.BlendByte(KFogColor, fc, 1, c.G, _w), b = LookMatchRules.BlendByte(KFogColor, fc, 2, c.B, _w);
                    int i = KFogColor.Index * 3;
                    OwnV[i] = c.R; OwnV[i + 1] = c.G; OwnV[i + 2] = c.B;
                    NowV[i] = r; NowV[i + 1] = g; NowV[i + 2] = b;
                    Seen[KFogColor.Index] = true;
                    c.R = r; c.G = g; c.B = b;
                    settings.FogColor = c;
                }
            }
            catch (Exception e) { HookError("fog", e); }
        }

        private static void PlanetPrefix(ref MyRenderPlanetSettings settings)
        {
            if (!_active) return;
            try
            {
                PlanetHits++;
                settings.AtmosphereIntensityMultiplier = B(KAtmoInt, 0, settings.AtmosphereIntensityMultiplier);
                settings.AtmosphereIntensityAmbientMultiplier = B(KAtmoAmbient, 0, settings.AtmosphereIntensityAmbientMultiplier);
                settings.AtmosphereDesaturationFactorForward = B(KAtmoDesat, 0, settings.AtmosphereDesaturationFactorForward);
                settings.CloudsIntensityMultiplier = B(KClouds, 0, settings.CloudsIntensityMultiplier);
            }
            catch (Exception e) { HookError("planet", e); }
        }

        /// <summary>The settings arrive by ref to <c>MyPostprocessSettingsWrapper.Settings</c>: kept, changed for the message, put back by the finalizer.</summary>
        private static void PostPrefix(ref MyPostprocessSettings settings)
        {
            _postDirty = false;
            if (!_active) return;
            try
            {
                _postSaved = settings;
                _postDirty = true;
                PostHits++;
                settings.Data.Contrast = B(KContrast, 0, settings.Data.Contrast);
                settings.Data.Saturation = B(KSaturation, 0, settings.Data.Saturation);
                settings.Data.Brightness = B(KBrightness, 0, settings.Data.Brightness);
                settings.Data.BrightnessFactorR = B(KTint, 0, settings.Data.BrightnessFactorR);
                settings.Data.BrightnessFactorG = B(KTint, 1, settings.Data.BrightnessFactorG);
                settings.Data.BrightnessFactorB = B(KTint, 2, settings.Data.BrightnessFactorB);
                settings.Data.Vibrance = B(KVibrance, 0, settings.Data.Vibrance);
                settings.Data.BloomMult = B(KBloom, 0, settings.Data.BloomMult);
                settings.Data.LuminanceExposure = B(KExposure, 0, settings.Data.LuminanceExposure);
                settings.Data.EyeAdaptationTau = B(KEyeTau, 0, settings.Data.EyeAdaptationTau);
                settings.Data.WhitePoint = B(KWhitePoint, 0, settings.Data.WhitePoint);
                settings.Data.VignetteStart = B(KVignette, 0, settings.Data.VignetteStart);
                settings.Data.VignetteLength = B(KVignetteLength, 0, settings.Data.VignetteLength);
            }
            catch (Exception e)
            {
                if (_postDirty) settings = _postSaved;
                _postDirty = false;
                HookError("post", e);
            }
        }

        private static void PostFinalizer(ref MyPostprocessSettings settings)
        {
            if (!_postDirty) return;
            settings = _postSaved;
            _postDirty = false;
        }

        private static void HookError(string hook, Exception e)
        {
            Errors++;
            DisabledWhy = hook + " " + e.GetType().Name + ": " + e.Message;
            _active = false;
            DeLog.Info("DE.S7t look DISABLED in the " + hook + " hook: " + DisabledWhy + " (SE's own look from the next frame)");
        }

        // ---------------------------------------------------------------------------------------------- atmosphere

        private static void Atmosphere()
        {
            MyPlanet planet = SiteBinding.Planet;
            uint epoch = Phases.WorldEpoch;
            if (!ReferenceEquals(planet, _atmoPlanet) || epoch != _atmoEpoch)
            {
                _atmoPlanet = planet;
                _atmoEpoch = epoch;
                _atmoLastSent = null;
                _atmoModified = false;
                _atmoFramesSince = 1000;
            }
            if (planet == null || planet.Closed) return;
            if (_atmoFramesSince < int.MaxValue) _atmoFramesSince++;
            MyAtmosphereSettings own = planet.AtmosphereSettings;
            bool want = _active && Table.AnySet(LookGroup.Atmo);
            if (!want)
            {
                if (_atmoModified && _atmoFramesSince >= LookMatchRules.AtmoMinFrames)
                {
                    if (SendAtmo(planet, own)) { AtmoRestores++; DeLog.Info("DE.S7t look atmosphere: the planet's own settings sent back (" + _why + ")"); }
                    _atmoModified = false;
                    _atmoLastSent = null;
                    _atmoFramesSince = 0;
                }
                return;
            }
            MyAtmosphereSettings s = own;
            s.RayleighScattering = B3(KRayleigh, own.RayleighScattering);
            s.MieColorScattering = B3(KMieColor, own.MieColorScattering);
            s.MieG = B(KMieG, 0, own.MieG);
            s.MieHeight = B(KMieHeight, 0, own.MieHeight);
            s.RayleighHeight = B(KRayleighHeight, 0, own.RayleighHeight);
            s.Intensity = B(KAtmoIntensity, 0, own.Intensity);
            s.FogIntensity = B(KAtmoFog, 0, own.FogIntensity);
            double[] snap =
            {
                s.RayleighScattering.X, s.RayleighScattering.Y, s.RayleighScattering.Z, s.MieColorScattering.X, s.MieColorScattering.Y,
                s.MieColorScattering.Z, s.MieG, s.MieHeight, s.RayleighHeight, s.Intensity, s.FogIntensity,
            };
            bool changed = LookMatchRules.Differs(snap, _atmoLastSent);
            if (!changed && _atmoFramesSince < LookMatchRules.AtmoRefreshFrames) return;
            if (_atmoFramesSince < LookMatchRules.AtmoMinFrames) { if (changed) AtmoDeferred++; return; }
            if (SendAtmo(planet, s))
            {
                _atmoLastSent = snap;
                _atmoModified = true;
            }
            _atmoFramesSince = 0;
        }

        /// <summary>
 /// S7t integrator (restore path): after the look match disabled itself (an exception in a hook or the frame), an
 /// atmosphere it had sent (only with an atmosphere key set live) is sent back once as the planet's own settings, so it
 /// does not stay until the world reloads. Never throws.
 /// </summary>
        private static void RestoreAtmoWhenDisabled()
        {
            if (!_atmoModified) return;
            _atmoModified = false;
            _atmoLastSent = null;
            try
            {
                MyPlanet planet = SiteBinding.Planet;
                if (planet == null || planet.Closed || !ReferenceEquals(planet, _atmoPlanet)) return;
                if (SendAtmo(planet, planet.AtmosphereSettings)) { AtmoRestores++; DeLog.Info("DE.S7t look atmosphere: the planet's own settings sent back (disabled)"); }
            }
            catch (Exception) { }
        }

        private static bool SendAtmo(MyPlanet planet, MyAtmosphereSettings s)
        {
            try
            {
                object rc = planet.Render;
                if (rc == null) return false;
                Type t = rc.GetType();
                if (_atmoMethod == null || _atmoMethodType != t)
                {
                    _atmoMethodType = t;
                    _atmoMethod = t.GetMethod("UpdateAtmosphereSettings", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(MyAtmosphereSettings) }, null);
                }
                if (_atmoMethod == null)
                {
                    AtmoFailures++;
                    if (_atmoError == null) { _atmoError = t.FullName + ".UpdateAtmosphereSettings(MyAtmosphereSettings) not found"; DeLog.Info("DE.S7t look atmosphere OFF: " + _atmoError); }
                    return false;
                }
                _atmoMethod.Invoke(rc, new object[] { s });
                AtmoSent++;
                return true;
            }
            catch (Exception e)
            {
                AtmoFailures++;
                Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                string msg = inner.GetType().Name + ": " + inner.Message;
                if (_atmoError != msg) { _atmoError = msg; DeLog.Info("DE.S7t look atmosphere not sent: " + msg + " (the planet keeps its own sky)"); }
                return false;
            }
        }

        // ============================================================================================== command

 /// <summary>
 /// <c>de.s7t {look: …}</c> / chat <c>/de s7t look …</c> (the verb is shared by the S7t features: other keys return
 /// null and reach their own handler). Chained first in <c>Plugin.ExtraCommand</c> (S7t-look-match hook).
 /// </summary>
        public static Dictionary<string, object> Execute(string cmd, IDictionary<string, object> args, string via)
        {
            if (cmd != LookMatchRules.Cmd) return null;
            List<string> tokens;
            if (!LookMatchRules.IsMine(args, out tokens)) return null;
            var applied = new List<string>();
            List<string> problems = Table.Apply(tokens, applied);
            bool full = tokens.Count == 0 || tokens.Exists(t => t.Equals("status", StringComparison.OrdinalIgnoreCase));
            var sb = new StringBuilder("DE.S7t look ");
            if (applied.Count > 0) sb.Append("applied ").Append(string.Join(" ", applied.ToArray())).Append("; ");
            if (problems.Count > 0) sb.Append("problems: ").Append(string.Join("; ", problems.ToArray())).Append("; ");
            sb.Append("w=").Append(_w.ToString("0.00", CultureInfo.InvariantCulture)).Append(" (").Append(_why).Append(", y=")
              .Append(double.IsNaN(_y) ? "n/a" : _y.ToString("0", CultureInfo.InvariantCulture)).Append(" m, band +")
              .Append(ViewThresholds.ApproachEnterY.ToString("0", CultureInfo.InvariantCulture)).Append(')');
            if (DisabledWhy != null) sb.Append(" DISABLED (").Append(DisabledWhy).Append(')');
            if (full) sb.Append("; ").Append(Table.Describe());
            sb.Append(" (via ").Append(via).Append(')');
            string line = sb.ToString();
            DeLog.Info(line);
            Row("command");
            var d = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ok", problems.Count == 0 }, { "line", line }, { "w", _w }, { "wTarget", _wTarget }, { "gate", _why },
                { "table", Table.Describe() }, { "hooks", Hooks.Count }, { "missing", Missing.Count }, { "disabled", DisabledWhy },
                { "draws", Draws }, { "envHits", EnvHits }, { "fogHits", FogHits }, { "planetHits", PlanetHits }, { "postHits", PostHits },
                { "atmoSent", AtmoSent },
            };
            if (problems.Count > 0) d["problems"] = problems;
            return d;
        }

        // ============================================================================================== rows

 /// <summary>One <c>s7t.look</c> row (CONTRACT §5.4 "S7t look match").</summary>
        private static void Row(string why)
        {
            try
            {
                var sb = new StringBuilder(900);
                sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.Append(",\"ev\":\"s7t.look\",\"side\":\"se\",\"why\":"); JsonText.AppendString(sb, why);
                sb.Append(",\"tick\":").Append(_tick.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"gate\":"); JsonText.AppendString(sb, _why);
                sb.Append(",\"w\":"); JsonText.AppendNumber(sb, Math.Round(_w, 4));
                sb.Append(",\"wTarget\":"); JsonText.AppendNumber(sb, Math.Round(_wTarget, 4));
                sb.Append(",\"y\":"); JsonText.AppendNumber(sb, Math.Round(_y, 1)); // null when no world
                sb.Append(",\"band\":"); JsonText.AppendNumber(sb, ViewThresholds.ApproachEnterY);
                sb.Append(",\"ramp\":"); JsonText.AppendNumber(sb, Table.RampM);
                sb.Append(",\"ease\":"); JsonText.AppendNumber(sb, Table.EaseS);
                sb.Append(",\"force\":"); JsonText.AppendNumber(sb, Table.Force); // null = auto
                sb.Append(",\"enabled\":").Append(Table.Enabled ? "true" : "false");
                sb.Append(",\"draws\":").Append(Draws).Append(",\"activeDraws\":").Append(ActiveDraws);
                sb.Append(",\"envHits\":").Append(EnvHits).Append(",\"fogHits\":").Append(FogHits).Append(",\"planetHits\":").Append(PlanetHits).Append(",\"postHits\":").Append(PostHits);
                sb.Append(",\"atmoSent\":").Append(AtmoSent).Append(",\"atmoDeferred\":").Append(AtmoDeferred).Append(",\"atmoFailures\":").Append(AtmoFailures)
                  .Append(",\"atmoRestores\":").Append(AtmoRestores).Append(",\"atmoModified\":").Append(_atmoModified ? "true" : "false");
                sb.Append(",\"hooks\":").Append(Hooks.Count).Append(",\"missing\":").Append(Missing.Count).Append(",\"errors\":").Append(Errors);
                sb.Append(",\"rowsDropped\":").Append(Cadence.Dropped);
                if (DisabledWhy != null) { sb.Append(",\"disabled\":"); JsonText.AppendString(sb, DisabledWhy); }
                // the live values: [own, now] per set key (colours: own r,g,b then now r,g,b)
                sb.Append(",\"live\":{");
                bool first = true;
                foreach (LookKey k in LookMatchRules.Keys)
                {
                    if (!Seen[k.Index] || Table.Get(k) == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    JsonText.AppendString(sb, k.Name);
                    sb.Append(":[");
                    int n = k.Channels, b0 = k.Index * 3;
                    for (int c = 0; c < n; c++) { if (c > 0) sb.Append(','); JsonText.AppendNumber(sb, Math.Round(OwnV[b0 + c], 5)); }
                    for (int c = 0; c < n; c++) { sb.Append(','); JsonText.AppendNumber(sb, Math.Round(NowV[b0 + c], 5)); }
                    sb.Append(']');
                }
                sb.Append("}}");
                Jsonl.WriteLineBuffered(sb.ToString());
                Rows++;
            }
            catch (Exception) { }
        }

        /// <summary>One status line (the S7t integrator's status, the de.s7t status of another handler).</summary>
        public static string Status()
        {
            return "look w=" + _w.ToString("0.00", CultureInfo.InvariantCulture) + " (" + _why + ") hooks " + Hooks.Count + "/5" +
                   (Missing.Count > 0 ? " missing " + Missing.Count : "") + " hits env " + EnvHits + " fog " + FogHits + " planet " + PlanetHits +
                   " post " + PostHits + " atmo " + AtmoSent + (DisabledWhy != null ? " DISABLED" : "");
        }
    }
}
