using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DeepEngineers.Wire;
using FMOD;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S14 "Audio v2", the Subnautica side (ROADMAP §14 "### S14"; docs/S14-BRIEF.md; CONTRACT §5.4 "S14 conventions"), main
 /// thread only. Every game member VERIFIED
 /// <list type="bullet">
 /// <item><b>Point 4a, the listener.</b> FMOD's listener comes from the scene's <c>FMODUnity.StudioListener</c>
 /// Our bridge unparents and drives the camera rig (CameraDriver: the rig
 /// <c>camRoot</c> leaves <c>camPivot</c>, MainCameraControl disabled), so a listener anywhere else than the camera hears
 /// A postfix
 /// on SetListenerLocation writes the main camera's pose (position, forward, up) and attenuation position for the same
 /// listener
 /// while MainCameraControl is disabled (the bridge drives the camera). <c>s14.listener</c> rows measure it: the
 /// scene listener's own place, and FMOD's readback (<c>getListenerAttributes</c>) against the camera.</item>
 /// <item><b>Point 4b, the engine loop.</b> The S7 engine loop (<c>SeaMoth.engineSound.engineRpmSFX</c>, an
 /// FMOD_CustomLoopingEmitter with the <c>rpm</c> parameter) gets S14's envelope (<see cref="S14EngineEnv"/>): the event
 /// volume through the emitter's private <c>evt</c>
 /// Subnautica's own Seamoth ramps rpm at 1/s up and 0.2/s down
 /// (EngineRpmSFXManager.cs);</item>
 /// <item><b>Point 5, the jets above water</b>: S14EffectsJets.cs (part of the S7 effects player).</item>
 /// </list>
 /// Off with <c>DEEPENGINEERS_S14=off</c>; dev command <c>de.s14</c>. Nothing in Subnautica's sounds is filtered (SE's
 /// underwater treatment acts on SE's own voices only).
 /// </summary>
    internal static class S14Sn
    {
        public const string Verb = "de.s14";
        public const string EnvVar = "DEEPENGINEERS_S14";

        private static bool _configured;
        public static bool Enabled { get; private set; } = true;
        public static bool ListenerOn = true, EnvelopeOn = true, JetsOn = true;
        /// <summary>

 /// <c>subnautica</c> = the Cyclops loop for hydrogen, the Seamoth loop for ion, SE's own for atmospheric
 /// (<c>/de sn s14 engine se|subnautica</c>). SE learns it from the C7 S14 block.
 /// </summary>
        public static byte EngineMode = S14Conventions.Engine.ModeSe;
        /// <summary>The Seamoth loop (S7's library, SeaMoth.engineSound.engineRpmSFX), set by the effects player every frame.</summary>
        public static FMODAsset SeamothLoop;
        private static FMODAsset _cyclopsLoop;
        private static float _nextCyclopsTryS;
        private static bool _cyclopsLogged;
        public static float JetKnob = 2f;   // /de sn s14 jetScale 0.1..4

 // ---------------------------------------------------------------------------------------------- listener
        private static bool _applyTried, _listenerPatched;
        private static long _listenerCalls, _overrides;
        private static bool _sampleDue;
        private static float _nextListenerRowS;
        // the last measurement (taken inside the postfix, so the camera and FMOD's readback are of the same instant)
        private static bool _measured;
        private static string _sceneListener = "none";
        private static double _sceneOffsetM = double.NaN, _sceneAngleDeg = double.NaN, _readOffsetM = double.NaN, _readAngleDeg = double.NaN;
        private static double _attenOffsetM = double.NaN;
        private static bool _overrodeLast, _driven;
        private static int _listenerCount;

        // ---------------------------------------------------------------------------------------------- engine
        private static AccessTools.FieldRef<FMOD_CustomEmitter, EventInstance> _evt;
        private static bool _evtTried;
        private static float _nextEngineRowS;
        private static int _engineRowsThisSecond;
        private static float _engineRowWindowS;
        public static long EngineStarts, EngineResumes, EngineStops, VolumeWrites;

        // ---------------------------------------------------------------------------------------------- config

        public static void LoadConfig()
        {
            if (_configured) return;
            _configured = true;
            string v = null;
            try { v = Environment.GetEnvironmentVariable(EnvVar); } catch (Exception) { }
            Enabled = !(v != null && (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)));
            DeLog.Info("DE.S14 config (Subnautica): " + (Enabled ? "on" : "OFF (" + EnvVar + "=" + v + ")") + "; " + S14Conventions.Header + " " + DeLog.Hex(S14Conventions.Hash()) +
                       (S14Conventions.Hash() == S14Conventions.PinnedHash ? " pinned" : " MISMATCH"));
        }

        /// <summary>Installs the listener postfix once (idempotent; never throws).</summary>
        public static void EnsureApplied()
        {
            if (_applyTried) return;
            _applyTried = true;
            LoadConfig();
            if (!Enabled) return;
            try
            {
                MethodInfo m = AccessTools.Method(typeof(StudioListener), "SetListenerLocation", Type.EmptyTypes);
                if (m == null || m.IsStatic || m.ReturnType != typeof(void))
                {
                    DeLog.Warn("DE.S14 MISSING FMODUnity.StudioListener.SetListenerLocation() (the listener fix off)");
                    return;
                }
                new Harmony("deepengineers.sn.s14").Patch(m, postfix: new HarmonyMethod(typeof(S14Sn).GetMethod(nameof(ListenerPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                _listenerPatched = true;
                DeLog.Info("DE.Harmony SN deepengineers.sn.s14 StudioListener.SetListenerLocation postfix (the FMOD listener on the camera while the bridge drives it)");
            }
            catch (Exception e) { DeLog.Warn("DE.S14 MISSING StudioListener.SetListenerLocation patch " + e.GetType().Name + ": " + e.Message + " (the listener fix off)"); }
        }

        // ============================================================================================== point 4a

 /// <summary>After the scene listener set its pose: the camera's pose for the same listener (never throws).</summary>
        private static void ListenerPostfix(StudioListener __instance)
        {
            try
            {
                _listenerCalls++;
                if (!Enabled || __instance == null) return;
                int n = __instance.ListenerNumber;
                if (n < 0 || n >= 8) return;
                Camera cam = MainCamera.camera;
                MainCameraControl mcc = MainCameraControl.main;
                bool driven = cam != null && mcc != null && !mcc.enabled; // CameraDriver disables MainCameraControl while it drives
                _driven = driven;
                bool wanted = driven && ListenerOn && !DevMutants.IsOn(S14SnMutants.ListenerLeft);
                Transform ct = cam != null ? cam.transform : null;
                if (wanted)
                {
                    RuntimeManager.StudioSystem.setListenerAttributes(n, ct.To3DAttributes(), ct.position.ToFMODVector());
                    _overrides++;
                }
                _overrodeLast = wanted;
                if (_sampleDue && n == 0 && ct != null)
                {
                    _sampleDue = false;
                    Measure(__instance, ct);
                }
            }
            catch (Exception) { }
        }

        private static void Measure(StudioListener l, Transform ct)
        {
            Vector3 cp = ct.position, cf = ct.forward;
            Transform lt = l.transform;
            _sceneListener = Path(lt);
            _sceneOffsetM = S14ListenerMath.Distance(lt.position.x, lt.position.y, lt.position.z, cp.x, cp.y, cp.z);
            _sceneAngleDeg = S14ListenerMath.AngleDeg(lt.forward.x, lt.forward.y, lt.forward.z, cf.x, cf.y, cf.z);
            ATTRIBUTES_3D a;
            VECTOR att;
            if (RuntimeManager.StudioSystem.getListenerAttributes(0, out a, out att) == RESULT.OK)
            {
                _readOffsetM = S14ListenerMath.Distance(a.position.x, a.position.y, a.position.z, cp.x, cp.y, cp.z);
                _readAngleDeg = S14ListenerMath.AngleDeg(a.forward.x, a.forward.y, a.forward.z, cf.x, cf.y, cf.z);
                _attenOffsetM = S14ListenerMath.Distance(att.x, att.y, att.z, cp.x, cp.y, cp.z);
            }
            else _readOffsetM = _readAngleDeg = _attenOffsetM = double.NaN;
            int count = 0;
            foreach (StudioListener s in RuntimeManager.Listeners) if (s != null) count++;
            _listenerCount = count;
            _measured = true;
        }

        private static string Path(Transform t)
        {
            if (t == null) return "none";
            string p = t.name;
            int depth = 0;
            for (Transform u = t.parent; u != null && depth < 6; u = u.parent, depth++) p = u.name + "/" + p;
            return p;
        }

        // ============================================================================================== point 4b

 /// <summary>
 /// Subnautica's loop for a thruster type in the current mode, or null (SE's own sound plays it): hydrogen = the Cyclops
 /// engine
 /// ion = the Seamoth engine, atmospheric and other = none.
 /// </summary>
        public static FMODAsset EngineAsset(ushort kind)
        {
            if (!Enabled || EngineMode != S14Conventions.Engine.ModeSubnautica) return null;
            if (kind == S7Conventions.ThrusterKind.Hydrogen) return _cyclopsLoop;
            if (kind == S7Conventions.ThrusterKind.Ion) return SeamothLoop;
            return null;
        }

        /// <summary>
 /// The Cyclops engine loop from the scene prefab Subnautica keeps
 /// tried every 10 s while the mode wants it.
 /// </summary>
        private static void ResolveCyclops(float now)
        {
            if (_cyclopsLoop != null || EngineMode != S14Conventions.Engine.ModeSubnautica || now < _nextCyclopsTryS) return;
            _nextCyclopsTryS = now + 10f;
            try
            {
                GameObject cyclops = LightmappedPrefabs.main != null ? LightmappedPrefabs.main.GetScenePrefab("cyclops") : null;
                SubControl sc = cyclops != null ? cyclops.GetComponentInChildren<SubControl>(true) : null;
                EngineRpmSFXManager m = sc != null ? sc.engineRPMManager : null;
                _cyclopsLoop = m != null && m.engineRpmSFX != null ? m.engineRpmSFX.asset : null;
                if (_cyclopsLoop != null) DeLog.Info("DE.S14 engine hydrogen = SubControl.engineRPMManager.engineRpmSFX.asset (Cyclops) path=" + _cyclopsLoop.path);
                else if (!_cyclopsLogged) { _cyclopsLogged = true; DeLog.Warn("DE.S14 MISSING the Cyclops engine loop (the cyclops scene prefab is not loaded): hydrogen thrusters keep SE's own sound; retried every 10 s"); }
            }
            catch (Exception e) { DeLog.WarnOnce("s14.cyclops", "DE.S14 Cyclops engine loop lookup " + e.GetType().Name + ": " + e.Message + " (logged once)"); }
        }

        /// <summary>Bridge (S14 hook, WriteStatus): the C7 S14 block, every frame (format c7.s14).</summary>
        public static void FillStatus(ref SnStatus s)
        {
            try
            {
                if (!Enabled) return;
                S14Wire.WriteStatus(ref s, S14Rules.C7FlagsOf(EngineMode, _cyclopsLoop != null, SeamothLoop != null));
            }
            catch (Exception) { }
        }

        /// <summary>The engine loop's volume and rpm for this frame (the S7 player's UpdateEngines, S14 hook).</summary>
        public static void EngineApply(FMOD_CustomLoopingEmitter e, S14EngineEnv env, double rawRpm)
        {
            if (e == null || env == null) return;
            bool noEnv = !Enabled || !EnvelopeOn || DevMutants.IsOn(S14SnMutants.NoEnvelope);
            e.SetParameterValue("rpm", (float)env.RpmParam(noEnv, rawRpm));
            if (!_evtTried)
            {
                _evtTried = true;
                try { _evt = AccessTools.FieldRefAccess<FMOD_CustomEmitter, EventInstance>("evt"); }
                catch (Exception ex) { DeLog.Warn("DE.S14 MISSING FMOD_CustomEmitter.evt (" + ex.GetType().Name + "): the engine loop keeps its level (envelope rpm only)"); }
            }
            if (_evt == null) return;
            EventInstance ev = _evt(e);
            if (!ev.hasHandle()) return;
            ev.setVolume((float)env.Volume(noEnv));
            VolumeWrites++;
        }

        /// <summary>Whether this frame steps the envelope (S14 on and not S14.M5).</summary>
        public static bool NoEnvelope { get { return !Enabled || !EnvelopeOn || DevMutants.IsOn(S14SnMutants.NoEnvelope); } }

        /// <summary>An envelope event of one engine: a row (capped at 20 per second) for the S14.N4E analysis.</summary>
        public static void EngineEvent(ulong entity, ushort kind, S14EngineEnv env, S14EnvEvent ev, FMOD_CustomLoopingEmitter e, double rawRpm, bool attackMeasured)
        {
            if (ev == S14EnvEvent.Start) EngineStarts++;
            else if (ev == S14EnvEvent.Resume) EngineResumes++;
            else if (ev == S14EnvEvent.Stopped) EngineStops++;
            bool row = ev != S14EnvEvent.None || attackMeasured;
            if (!row) return;
            float now = Time.unscaledTime;
            if (now >= _engineRowWindowS + 1f) { _engineRowWindowS = now; _engineRowsThisSecond = 0; }
            if (++_engineRowsThisSecond > 20) return;
            EngineRow(attackMeasured && ev != S14EnvEvent.Start ? "attack" : ev.ToString().ToLowerInvariant(), entity, kind, env, e, rawRpm);
        }

        /// <summary>Once per second per engine while one plays (the S7 player calls it for each playing engine).</summary>
        public static bool EngineRowDue(float now)
        {
            return now >= _nextEngineRowS;
        }

        public static void EngineRowsWritten(float now) { _nextEngineRowS = now + S14Conventions.Rows.PeriodMs / 1000f; }

        public static void EngineRow(string why, ulong entity, ushort kind, S14EngineEnv env, FMOD_CustomLoopingEmitter e, double rawRpm)
        {
            bool noEnv = NoEnvelope;
            double readback = double.NaN;
            try
            {
                if (_evt != null && e != null)
                {
                    EventInstance ev = _evt(e);
                    float v;
                    if (ev.hasHandle() && ev.getVolume(out v) == RESULT.OK) readback = v;
                }
            }
            catch (Exception) { }
            JsonOut j = S5Rows.Begin("s14.engine");
            j.Str("why", why).Str("source", "subnautica").Str("type", KindName(kind)).Str("loop", kind == S7Conventions.ThrusterKind.Hydrogen ? "cyclops" : "seamoth")
             .Num("entity", (long)entity).Num("level", Math.Round(env.Level, 4)).Num("rpm", Math.Round(rawRpm, 4)).Num("rpmS", Math.Round(env.RpmS, 4))
             .Num("volume", Math.Round(env.Volume(noEnv), 4)).Num("volumeRead", double.IsNaN(readback) ? -1.0 : Math.Round(readback, 4))
             .Num("gain", Math.Round(S14Rules.EngineGain(env.RpmS), 4)).Bool("playing", env.Playing).Bool("want", env.Want)
             .Num("attackMs", double.IsNaN(env.LastAttackMs) ? -1.0 : Math.Round(env.LastAttackMs, 1))
             .Num("releaseMs", double.IsNaN(env.LastReleaseMs) ? -1.0 : Math.Round(env.LastReleaseMs, 1))
             .Num("starts", env.Starts).Num("resumes", env.Resumes).Num("stops", env.Stops).Bool("envelope", !noEnv)
             .Bool("mutantS14M5", DevMutants.IsOn(S14SnMutants.NoEnvelope));
            S5Rows.End(j);
        }

        // ============================================================================================== per frame

 /// <summary>The S7 player's Tick (S14 hook): the patch on first use, the listener sample and its row at 1 Hz.</summary>
        public static void Frame(float now)
        {
            try
            {
                if (!_applyTried) EnsureApplied();
                if (!Enabled) return;
                ResolveCyclops(now);
                if (!_listenerPatched) return;
                if (now < _nextListenerRowS) return;
                _nextListenerRowS = now + S14Conventions.Rows.PeriodMs / 1000f;
                if (_measured && _driven)
                {
                    JsonOut j = S5Rows.Begin("s14.listener");
                    j.Str("scene", _sceneListener).Num("listeners", (long)_listenerCount)
                     .Num("sceneOffsetM", R(_sceneOffsetM)).Num("sceneAngleDeg", R(_sceneAngleDeg))
                     .Num("readOffsetM", R(_readOffsetM)).Num("readAngleDeg", R(_readAngleDeg)).Num("attenOffsetM", R(_attenOffsetM))
                     .Bool("overridden", _overrodeLast).Num("overrides", _overrides).Num("calls", _listenerCalls).Bool("on", ListenerOn)
                     .Bool("ok", S14Rules.ListenerOk(_readOffsetM, _readAngleDeg)).Bool("mutantS14M4", DevMutants.IsOn(S14SnMutants.ListenerLeft));
                    S5Rows.End(j);
                }
                _measured = false;
                _sampleDue = true; // the next SetListenerLocation measures
            }
            catch (Exception e) { DeLog.WarnOnce("s14.frame", "DE.S14 frame error " + e.GetType().Name + ": " + e.Message + " (logged once)"); }
        }

        private static double R(double v) { return double.IsNaN(v) ? -1.0 : Math.Round(v, 4); }

        public static string KindName(ushort kind)
        {
            return kind == S7Conventions.ThrusterKind.Hydrogen ? "hydrogen" : kind == S7Conventions.ThrusterKind.Ion ? "ion" :
                   kind == S7Conventions.ThrusterKind.Atmospheric ? "atmospheric" : "other";
        }

        // ============================================================================================== de.s14

 /// <summary>
 /// <c>de.s14 {status} | {engine: se|subnautica} | {listener|envelope|jets: on|off} | {jetScale: 0.1..4}</c> (chat
 /// <c>/de sn s14 engine subnautica</c>); dev builds.
 /// </summary>
        public static bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Verb) return false;
            try
            {
                if (!BuildFlavor.Dev) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", UserCommandRules.RefusalText); return true; }
                Dictionary<string, object> a = S3Args.ToDict(req.Args);
                object v;
                if (a.TryGetValue("argv", out v) && v is System.Collections.IList)
                {
                    var l = (System.Collections.IList)v;
                    if (l.Count >= 2) a[Convert.ToString(l[0], CultureInfo.InvariantCulture)] = Convert.ToString(l[1], CultureInfo.InvariantCulture);
                    else if (l.Count == 1) a["action"] = Convert.ToString(l[0], CultureInfo.InvariantCulture);
                }
                string error = null;
                error = Switch(a, "listener", ref ListenerOn) ?? error;
                error = Switch(a, "envelope", ref EnvelopeOn) ?? error;
                error = Switch(a, "jets", ref JetsOn) ?? error;
                if (a.TryGetValue("engine", out v))
                {
                    string m = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    if (m == "se") EngineMode = S14Conventions.Engine.ModeSe;
                    else if (m == "subnautica" || m == "sn") { EngineMode = S14Conventions.Engine.ModeSubnautica; _nextCyclopsTryS = 0f; }
                    else error = "engine: se|subnautica";
                    if (error == null) DeLog.Info("DE.S14 engine mode " + S14Conventions.Engine.ModeName(EngineMode) + " (hydrogen " +
                                                  (EngineMode == S14Conventions.Engine.ModeSubnautica ? "Cyclops loop" : "SE's own") + ", ion " +
                                                  (EngineMode == S14Conventions.Engine.ModeSubnautica ? "Seamoth loop" : "SE's own") + ", atmospheric SE's own)");
                }
                if (a.TryGetValue("jetScale", out v))
                {
                    double d;
                    if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.1 && d <= 4) JetKnob = (float)d;
                    else error = "jetScale: 0.1..4";
                }
                if (error != null) { reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", error); return true; }
                string line = "DE.S14 engine=" + S14Conventions.Engine.ModeName(EngineMode) + " (cyclops " + (_cyclopsLoop != null ? "loaded" : "not loaded") +
                              ", seamoth " + (SeamothLoop != null ? "loaded" : "not loaded") + ") listener=" + (ListenerOn ? "on" : "off") + " envelope=" + (EnvelopeOn ? "on" : "off") + " jets=" + (JetsOn ? "on" : "off") +
                              " jetScale=" + JetKnob.ToString("0.##", CultureInfo.InvariantCulture) + " | listener " + _sceneListener + " sceneOffset=" + R(_sceneOffsetM) +
                              " m overrides=" + _overrides + " | engines starts=" + EngineStarts + " resumes=" + EngineResumes + " stops=" + EngineStops + " | jets " + S7Effects.S14JetStatus();
                reply.Bool("ok", true).Str("cmd", req.Cmd).Str("engine", S14Conventions.Engine.ModeName(EngineMode)).Bool("listener", ListenerOn).Bool("envelope", EnvelopeOn).Bool("jets", JetsOn).Num("jetScale", (double)JetKnob)
                     .Str("scene", _sceneListener).Num("overrides", _overrides).Bool("patched", _listenerPatched).Str("line", line);
                return true;
            }
            catch (Exception e)
            {
                reply.Bool("ok", false).Str("cmd", req.Cmd).Str("error", e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        private static string Switch(IDictionary<string, object> a, string key, ref bool field)
        {
            object v;
            if (!a.TryGetValue(key, out v)) return null;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            if (s != "on" && s != "off") return key + ": on|off";
            field = s == "on";
            return null;
        }
    }
}
