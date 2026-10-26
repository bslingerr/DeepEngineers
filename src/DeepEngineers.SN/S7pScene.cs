using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using DeepEngineers.Wire;

using UnityEngine;
using UnityEngine.Rendering;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;
using Object = UnityEngine.Object;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7p-scene-above, Subnautica side (ROADMAP "### S7p Polish pass" P1, P7; CONTRACT §5.4 "S7p scene-above"; rules in
 /// Wire <see cref="S7pScene"/>). Owned by the <see cref="MirrorConsumer"/> (created with it, ticked after its material
 /// and S5 steps while the world is up, torn down with its Unity objects):
 /// <list type="bullet">
 /// <item>P1 route B (<see cref="S7pAboveFx"/>): its own reader on the C2 event window plays every row without
 /// <c>Underwater</c> (the picture's waterline, SN y = 0) with an above-water variant: muzzle flash, tracer, sparks,
 /// thruster flame, smoke, blast. The S7 effect player keeps the under-water looks, the surface splash and its flash
 /// lights; this player adds particles only (no sound: SE plays its own above water).</item>
 /// <item>P1 missiles (<see cref="S7pMissileTrails"/>): SE mirrors each missile in flight as a floating entity with the
 /// Missile GridAdd bit (its own model, posed by C2 every tick); this side adds the trail (flame and smoke above water,
 /// a bubble stream below).</item>
 /// <item>P7 (<see cref="S7pProjectionView"/>): every renderer of an entity with the Projection GridAdd bit takes one
 /// hologram material (Unity's Standard in Fade mode, SE's hologram blue, emissive), no shadows.</item>
 /// </list>
 /// One world-space particle system per look (<see cref="S7pFxKit"/>), emitted through explicit parameters: no prefab,
 /// no collider, nothing that touches Subnautica's gameplay; the materials are Unity's own shaders found by name (the
 /// legacy particle shaders, else any loaded additive/alpha particle shader, else Sprites/Default or
 /// Hidden/Internal-Colored) with a soft dot made in code. Toggle <c>de.s7p {above: events|se|off}</c> and
 /// <c>DEEPENGINEERS_S7P_ABOVE</c> (default events); row <c>s7p.above</c> at 1 Hz. Every step is guarded: an exception
 /// turns S7p off on this side only (logged once). Main thread.
 /// </summary>
    internal sealed class S7pSceneSn
    {
        public const string Cmd = S7pScene.Cmd;
        private const ulong RowEveryMs = 1000;

        private readonly MirrorConsumer _m;
        private readonly S7pFxKit _kit = new S7pFxKit();
        public readonly S7pAboveFx Fx;
        public readonly S7pMissileTrails Trails;
        public readonly S7pProjectionView Holo;
        private S7pAboveMode _mode;
        private bool _envLogged, _disabled;
        private string _disabledWhy;
        private ulong _nextRowMs;
        private int _scanFrame = -1000;
        private readonly Stopwatch _sw = new Stopwatch();
        public long Frames, EmitsCapped, BudgetCuts;
        private double _msMax, _msLast;

        public S7pSceneSn(MirrorConsumer m)
        {
            _m = m;
            bool fromEnv;
            string raw = null;
            try { raw = Environment.GetEnvironmentVariable(S7pScene.AboveEnvVar); } catch (Exception) { }
            _mode = S7pScene.AboveFromEnv(raw, out fromEnv);
            Fx = new S7pAboveFx(m, _kit);
            Trails = new S7pMissileTrails(m, _kit);
            Holo = new S7pProjectionView(m);
        }

        public S7pAboveMode Mode { get { return _mode; } }

        /// <summary>Per frame after the mirror's material, S5 and ghost steps (world up). Never throws.</summary>
        public void Tick(ulong nowMs)
        {
            if (_disabled) return;
            try
            {
                _sw.Reset();
                _sw.Start();
                if (!_envLogged)
                {
                    _envLogged = true;
                    if (!Fx.Registered) Fx.Register();
                    DeLog.Info("DE.S7p scene-above SN: above=" + S7pScene.Name(_mode) + " (" + S7pScene.AboveEnvVar + " " +
                               (Environment.GetEnvironmentVariable(S7pScene.AboveEnvVar) ?? "unset: default events") + "; de.s7p {above}) projections=hologram missiles=trail");
                }
                Frames++;
                float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
                float now = Time.unscaledTime;
                int f = Time.frameCount;
                if (f - _scanFrame >= 3)
                {
                    _scanFrame = f;
                    Trails.Rescan();
                    Holo.Rescan();
                }
                int left = S7pScene.MaxEmitsPerFrame;
                long capped = 0;
                Fx.Frame(now, dt, _mode, ref left, ref capped, _sw);
                Trails.Frame(dt, _mode, ref left, ref capped);
                Holo.Frame();
                if (capped > 0) { EmitsCapped += capped; BudgetCuts++; }
                _sw.Stop();
                _msLast = _sw.Elapsed.TotalMilliseconds;
                if (_msLast > _msMax) _msMax = _msLast;
                if (nowMs >= _nextRowMs)
                {
                    _nextRowMs = nowMs + RowEveryMs;
                    Row("tick");
                }
            }
            catch (Exception e) { Disable("tick", e); }
        }

        /// <summary>A cell of an entity was uploaded: a projection's renderers take the hologram material at once.</summary>
        public void OnEntityBuilt(MirrorGridView v)
        {
            if (_disabled || v == null) return;
            try { Holo.ApplyNow(v); }
            catch (Exception e) { Disable("build", e); }
        }

        /// <summary>The world went away or the bridge stops: particles, materials and holds are dropped (made again lazily).</summary>
        public void TearDown(string why)
        {
            try { Fx.Clear(why); } catch (Exception) { }
            try { Trails.Clear(); } catch (Exception) { }
            try { Holo.Clear(); } catch (Exception) { }
            try { _kit.Destroy(); } catch (Exception) { }
        }

        public void Dispose()
        {
            TearDown("disposed");
            try { Fx.Unregister(); } catch (Exception) { }
        }

        private void Disable(string where, Exception e)
        {
            _disabled = true;
            _disabledWhy = where + ": " + e.GetType().Name + ": " + e.Message;
            DeLog.Error("DE.S7p scene-above SN DISABLED after an exception in " + _disabledWhy + " (S0-S7 keep running)\n" + e.StackTrace);
            try { TearDown("disabled"); } catch (Exception) { }
        }

        // ------------------------------------------------------------------------------------------ C11

 /// <summary>
 /// <c>de.s7p {above: events|se|off}</c> (also <c>{argv: [...]}</c> from the chat relay; <c>{action: scene}</c> or no key:
 /// status). The verb is shared (<see cref="S7pScene.IsMine"/>): false for another feature's keys.
 /// </summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Cmd) return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            string error = null;
            object v;
            string above = null;
            // the verb is shared by the S7p features (CONTRACT §5.4): another feature's keys go on to its own handler
            List<string> words = null;
            // S7p-integrator: S3Args.ToDict expands a chat relay's argv ("/de sn s7p above se" -> {action: "above"}), so the
 // words are read from the raw request; without this the two-word form never reached this handler
            var raw = req.Args as Dictionary<string, object>;
            if (raw != null && raw.TryGetValue("argv", out v) && v is System.Collections.IList) a = raw;
            if (a.TryGetValue("argv", out v) && v is System.Collections.IList)
            {
                words = new List<string>();
                foreach (object o in (System.Collections.IList)v) words.Add(o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "");
            }
            var keys = new List<string>();
            foreach (string k in a.Keys) if (k != "argv" && k != "action") keys.Add(k);
            if (!S7pScene.IsMine(keys, a.TryGetValue("action", out v) ? v as string : null, words)) return false;
            reply.Str("cmd", req.Cmd).Str("side", "sn");
            if (words != null)
            {
                string bad;
                Dictionary<string, string> kv = S7pScene.ParseArgv(words, out bad);
                if (bad != null) error = bad + " (" + S7pScene.Usage + ")";
                string s;
                if (kv.TryGetValue("above", out s)) above = s;
            }
            if (a.TryGetValue("above", out v)) above = v as string;
            if (error == null && above != null)
            {
                S7pAboveMode m;
                if (!S7pScene.TryParseAbove(above, out m)) error = "above: events | se | off";
                else SetMode(m, "de.s7p");
            }
            if (_disabled && error == null) error = "S7p scene-above is disabled after an exception: " + _disabledWhy;
            reply.Bool("ok", error == null);
            if (error != null) reply.Str("error", error);
            Status(reply);
            return true;
        }

        public void SetMode(S7pAboveMode m, string why)
        {
            if (m == _mode) return;
            _mode = m;
            if (!S7pScene.PlaysAboveVariants(m)) Fx.Clear("above " + S7pScene.Name(m));
            DeLog.Info("DE.S7p above=" + S7pScene.Name(m) + " (" + why + ")");
            Row("mode");
        }

        private void Status(JsonObjectWriter w)
        {
            w.Str("above", S7pScene.Name(_mode)).Bool("disabled", _disabled).Num("rowsSeen", Fx.Seen).Num("rowsPlayed", Fx.Played)
             .Num("sustains", (long)Fx.SustainCount).Num("missileViews", (long)Trails.Count).Num("projectionViews", (long)Holo.Count)
             .Num("emits", _kit.Emitted).Num("emitsCapped", EmitsCapped).Str("additive", _kit.AdditiveName).Str("alpha", _kit.AlphaName).Str("hologram", Holo.MaterialName)
             .Str("line", "DE.S7p above=" + S7pScene.Name(_mode) + " rows " + Fx.Played + "/" + Fx.Seen + " sustains " + Fx.SustainCount + " missiles " + Trails.Count +
                          " projections " + Holo.Count + " emits " + _kit.Emitted + " (capped " + EmitsCapped + ") shaders " + _kit.AdditiveName + "/" + _kit.AlphaName +
                          (_disabled ? " DISABLED" : ""));
        }

        // ------------------------------------------------------------------------------------------ rows

 /// <summary>s7p.above (1 Hz and on a mode change): the route, the rows played by type, sustains, emits and caps, missiles, projections.</summary>
        private void Row(string why)
        {
            JsonOut j = S5Rows.Begin("s7p.above");
            j.Str("why", why).Str("above", S7pScene.Name(_mode)).Bool("disabled", _disabled)
             .Num("rowsSeen", Fx.Seen).Num("rowsPlayed", Fx.Played).Num("rowsUnderwater", Fx.SkippedUnder).Num("rowsMode", Fx.SkippedMode)
             .Num("rowsNoEntity", Fx.NoEntity).Num("rowsDeferredToFx", Fx.DeferredToFx).Bool("fxAbove", S7Effects.AboveWater).Num("readerMissed", Fx.Reader.Missed).Num("readerOverflow", Fx.Reader.Overflow)
             .Num("sustains", (long)Fx.SustainCount).Num("sustainCapDrops", Fx.SustainCapDrops).Num("rowsBudgetLeft", Fx.BudgetLeftRows)
             .Num("emits", _kit.Emitted).Num("emitsCapped", EmitsCapped).Num("budgetCuts", BudgetCuts)
             .Num("missileViews", (long)Trails.Count).Num("missileTrailEmits", Trails.Emits).Num("missileCapDrops", Trails.CapDrops)
             .Num("projectionViews", (long)Holo.Count).Num("projectionRenderers", Holo.Renderers).Num("projectionSwaps", Holo.Swaps)
             .Str("additive", _kit.AdditiveName).Str("alpha", _kit.AlphaName).Str("hologram", Holo.MaterialName)
             .Num("msLast", _msLast).Num("msMax", _msMax);
            j.Obj("playedByType");
            for (int t = 1; t < Fx.PlayedByType.Length; t++) if (Fx.PlayedByType[t] > 0) j.Num(EventType.Name(t), Fx.PlayedByType[t]);
            j.EndObj();
            S5Rows.End(j);
            _msMax = 0;
        }
    }

    // ===================================================================================================== particle kit

 /// <summary>
 /// The seven world-space particle systems of S7p (<see cref="S7pLook"/>), created on first use under a <c>DE.S7p</c>
 /// root, emitting only through <see cref="ParticleSystem.Emit(ParticleSystem.EmitParams, int)"/> (no emission module,
 /// no shape), unscaled time (Subnautica's pause and freeze never hold SE's effects). Materials: one additive (flash,
 /// fire, sparks, tracers) and one alpha-blended (smoke, dust, vapour) from Unity shaders found by name.
 /// </summary>
    internal sealed class S7pFxKit
    {
        private GameObject _root;
        private readonly ParticleSystem[] _ps = new ParticleSystem[7];
        private Material _add, _alpha;
        private Texture2D _dotAdd, _dotAlpha;
        private bool _failed;
        public long Emitted;
        public string AdditiveName = "none", AlphaName = "none";

        private static readonly string[] AdditiveShaders = { "Legacy Shaders/Particles/Additive", "Particles/Additive", "Mobile/Particles/Additive" };
        private static readonly string[] AlphaShaders = { "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended", "Mobile/Particles/Alpha Blended" };

        public bool Ready { get { return _root != null && !_failed; } }

        private bool Ensure()
        {
            if (_failed) return false;
            if (_root != null) return true;
            try
            {
                _root = new GameObject("DE.S7p");
                _dotAdd = Dot(true);
                _dotAlpha = Dot(false);
                _add = MakeMaterial(true, out AdditiveName);
                _alpha = MakeMaterial(false, out AlphaName);

                // chain ended on a textureless shader (squares); the soft additive dot is used for those looks instead
                if (_alpha != null && !_alpha.HasProperty("_MainTex") && _add != null && _add.HasProperty("_MainTex"))
                {
                    DeLog.Warn("DE.S7p fx kit: alpha material '" + AlphaName + "' has no texture (square particles): smoke/dust/vapor use the additive dot instead");
                    AlphaName = AlphaName + " -> additive dot";
                    _alpha = _add;
                }
                Make(S7pLook.Flash, 256, 0f, ParticleSystemRenderMode.Billboard, 0f, true, 1.5f, false);
                Make(S7pLook.Fire, 4000, 0f, ParticleSystemRenderMode.Billboard, 0f, true, 1.8f, false);
                Make(S7pLook.Smoke, 2500, -0.02f, ParticleSystemRenderMode.Billboard, 0f, false, 3.2f, true);
                Make(S7pLook.Spark, 4000, 1f, ParticleSystemRenderMode.Stretch, 0.035f, true, 1f, false);
                Make(S7pLook.Tracer, 512, 0f, ParticleSystemRenderMode.Stretch, 0.016f, true, 1f, false);
                Make(S7pLook.Dust, 2000, -0.01f, ParticleSystemRenderMode.Billboard, 0f, false, 2.6f, true);
                Make(S7pLook.Vapor, 2000, -0.03f, ParticleSystemRenderMode.Billboard, 0f, false, 3f, true);
                DeLog.Info("DE.S7p fx kit ready: additive=" + AdditiveName + " alpha=" + AlphaName + " (7 world-space particle systems, unscaled time)");
                return true;
            }
            catch (Exception e)
            {
                _failed = true;
                DeLog.Error("DE.S7p fx kit FAILED (" + e.GetType().Name + ": " + e.Message + "): no above-water particles");
                Destroy();
                _failed = true;
                return false;
            }
        }

        private void Make(S7pLook look, int max, float gravity, ParticleSystemRenderMode mode, float velocityScale, bool additive, float grow, bool fadeIn)
        {
            var go = new GameObject("DE.S7p." + look);
            go.transform.SetParent(_root.transform, false);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.startLifetime = 1f;
            main.startSpeed = 0f;
            main.startSize = 1f;
            main.gravityModifier = gravity;
            main.useUnscaledTime = true;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false;
            ParticleSystem.ShapeModule sh = ps.shape;
            sh.enabled = false;
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      fadeIn ? new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0f, 1f) }
                             : new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
            ParticleSystem.SizeOverLifetimeModule sz = ps.sizeOverLifetime;
            sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, grow));
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = mode;
            if (mode == ParticleSystemRenderMode.Stretch) { r.velocityScale = velocityScale; r.lengthScale = 1f; }
            r.sharedMaterial = additive ? _add : _alpha;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.maxParticleSize = 0.9f;
            ps.Play(true);
            _ps[(int)look] = ps;
        }

        private Material MakeMaterial(bool additive, out string name) { return MakeMaterial(additive, _dotAdd, _dotAlpha, out name); }

        private static Texture2D _synthAdd, _synthAlpha;

        /// <summary>

 /// effects-under's plume, smoke and dust composites were off; they get a synthetic donor drawn with this): the same shader
 /// chain and soft dot; the dots are cached for the process.
 /// </summary>
        internal static Material SyntheticMaterial(bool additive, out string shaderName)
        {
            if (_synthAdd == null) _synthAdd = Dot(true);
            if (_synthAlpha == null) _synthAlpha = Dot(false);
            return MakeMaterial(additive, _synthAdd, _synthAlpha, out shaderName);
        }

        private static Material MakeMaterial(bool additive, Texture2D dotAdd, Texture2D dotAlpha, out string name)
        {
            Shader s = null;
            string[] chain = additive ? AdditiveShaders : AlphaShaders;
            for (int i = 0; i < chain.Length && s == null; i++)
            {
                Shader c = null;
                try { c = Shader.Find(chain[i]); } catch (Exception) { }
                if (c != null && c.isSupported) s = c;
            }
            if (s == null)
            {
                // any loaded particle shader of the right kind (Subnautica's own; a run-time lookup, nothing copied)
                try
                {

                    // "Legacy Shaders/Particles/Alpha Blended Premultiply" and the scan took it; with a straight-alpha dot a
 // premultiplied blend draws the quad's white where the dot is transparent. Plain alpha first, premultiply last.
                    Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
                    for (int pass = 0; pass < 2 && s == null; pass++)
                        foreach (Shader c in all)
                        {
                            if (c == null || !c.isSupported) continue;
                            string n = c.name.ToLowerInvariant();
                            bool premul = n.Contains("premultipl");
                            if (pass == 0 && premul) continue;
                            if (n.Contains("particle") && (additive ? n.Contains("add") : (n.Contains("alpha") && n.Contains("blend")))) { s = c; break; }
                        }
                }
                catch (Exception) { }
            }
            bool colored = false;
            // Sprites/Default (always in a Unity build) keeps the soft dot: alpha-blended, a round glow rather than additive squares
            if (s == null) { try { s = Shader.Find("Sprites/Default"); } catch (Exception) { } if (s != null && !s.isSupported) s = null; }
            if (s == null) { try { s = Shader.Find("Hidden/Internal-Colored"); } catch (Exception) { } colored = s != null; }
            if (s == null) throw new InvalidOperationException("no particle shader (legacy particles, Sprites/Default, Hidden/Internal-Colored)");
            var m = new Material(s) { name = "DE.S7p " + (additive ? "additive" : "alpha") + " (" + s.name + ")" };
            if (colored)
            {
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                m.SetInt("_ZWrite", 0);
                m.SetInt("_Cull", (int)CullMode.Off);
            }
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            // a premultiplied-alpha shader needs the dot with RGB = alpha (the additive dot is exactly that)
            bool premultiplied = s.name.IndexOf("premultipl", StringComparison.OrdinalIgnoreCase) >= 0;
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", additive || premultiplied ? dotAdd : dotAlpha);
            m.renderQueue = 3000;
            name = s.name;
            return m;
        }

        /// <summary>A 64 × 64 soft dot made in code (additive: colour and alpha fall off together; alpha: white with the alpha falloff).</summary>
        private static Texture2D Dot(bool additive)
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "DE.S7p dot " + (additive ? "add" : "alpha"), wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float f = Mathf.Clamp01(1f - r);
                    f = f * f * (3f - 2f * f);
                    byte a = (byte)Mathf.RoundToInt(f * 255f);
                    byte c = additive ? a : (byte)255;
                    px[y * n + x] = new Color32(c, c, c, a);
                }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>
 /// Emits <paramref name="count"/> particles of a puff at a world point: velocity = a direction in the cone around
 /// <paramref name="dir"/> (spread 0 along it, 1 any) × speed × [0.6, 1], plus the puff's upward drift; a ring puff
 /// flies in the plane perpendicular to <paramref name="dir"/>. Returns the particles emitted.
 /// </summary>
        public int Emit(ref S7pPuff p, Vector3 pos, Vector3 dir, int count)
        {
            if (count <= 0 || !Ensure()) return 0;
            ParticleSystem ps = _ps[(int)p.Look];
            if (ps == null) return 0;
            Vector3 d = dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.zero;
            var c = new Color(p.R, p.G, p.B, p.A);
            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < count; i++)
            {
                Vector3 v;
                if (p.Ring)
                {
                    Vector3 axis = d == Vector3.zero ? Vector3.up : d;
                    Vector3 r = Vector3.ProjectOnPlane(UnityEngine.Random.onUnitSphere, axis);
                    if (r.sqrMagnitude < 1e-6f) r = Vector3.Cross(axis, Vector3.right);
                    v = r.normalized;
                }
                else if (d == Vector3.zero || p.Spread >= 1f) v = UnityEngine.Random.onUnitSphere;
                else
                {
                    v = d + UnityEngine.Random.insideUnitSphere * (p.Spread * 2f);
                    if (v.sqrMagnitude < 1e-6f) v = d;
                    v.Normalize();
                }
                bool exact = p.Look == S7pLook.Tracer; // a tracer flies the streak's length in its lifetime exactly
                float speed = exact ? p.Speed : p.Speed * UnityEngine.Random.Range(0.6f, 1f);
                ep.position = pos + (p.Jitter > 0f ? UnityEngine.Random.insideUnitSphere * p.Jitter : Vector3.zero);
                ep.velocity = v * speed + Vector3.up * p.Up;
                ep.startSize = Mathf.Max(0.005f, exact ? p.Size : p.Size * UnityEngine.Random.Range(0.8f, 1.2f));
                ep.startLifetime = Mathf.Max(0.02f, exact ? p.Life : p.Life * UnityEngine.Random.Range(0.8f, 1.15f));
                ep.startColor = c;
                ps.Emit(ep, 1);
            }
            Emitted += count;
            return count;
        }

        public void Destroy()
        {
            for (int i = 0; i < _ps.Length; i++) _ps[i] = null;
            if (_root != null) Object.Destroy(_root);
            if (_add != null) Object.Destroy(_add);
            if (_alpha != null) Object.Destroy(_alpha);
            if (_dotAdd != null) Object.Destroy(_dotAdd);
            if (_dotAlpha != null) Object.Destroy(_dotAlpha);
            _root = null; _add = _alpha = null; _dotAdd = _dotAlpha = null;
        }
    }

    // ===================================================================================================== P1 above-water events

 /// <summary>
 /// The above-water player (P1 route B): its own <see cref="S7EventReader"/> on the consumer's C2 extension list
 /// (independent of the S7 effect player's; each sees every seq once), one-shots emitted at once, sustains (thrusters,
 /// tools, drills; hull-break jets) kept by (entity, key) like the S7 player and emitted per frame from their rates;
 /// at most <see cref="S7pScene.MaxAboveSustains"/> sustains (over: counted) and the shared per-frame emission budget.
 /// Attached rows follow the mirrored grid or character (SE-local pos/dir mirrored once: (x, y, −z)); world rows are
 /// SN-world. Rows with <c>Underwater</c> are left to the S7 player; a sustain whose newest row is under water holds
 /// without emitting.
 /// </summary>
    internal sealed class S7pAboveFx
    {
        public const int MaxRowsPerFrame = 512;
        public const double RowBudgetMs = 0.25;

        public readonly S7EventReader Reader = new S7EventReader();
        private readonly MirrorConsumer _m;
        private readonly S7pFxKit _kit;
        private readonly S7pPuff[] _puffs = new S7pPuff[S7pAboveRules.MaxPuffs];
        private readonly S7pRate[] _rates = new S7pRate[S7pAboveRules.MaxRates];

        private struct SKey : IEquatable<SKey>
        {
            public readonly ulong Entity, Key;
            public SKey(ulong e, ulong k) { Entity = e; Key = k; }
            public bool Equals(SKey o) { return Entity == o.Entity && Key == o.Key; }
            public override bool Equals(object obj) { return obj is SKey && Equals((SKey)obj); }
            public override int GetHashCode() { return (Entity.GetHashCode() * 397) ^ Key.GetHashCode(); }
        }

        private sealed class Sus
        {
            public SKey Key;
            public byte Type, Flags;
            public ushort Subtype, Extra;
            public float Intensity, Size, Expires;
            public bool Attached;
            public ulong Entity;
            public Vector3 Pos, Dir;
            public readonly float[] Carry = new float[S7pAboveRules.MaxRates];
        }

        private readonly Dictionary<SKey, Sus> _sus = new Dictionary<SKey, Sus>();
        private readonly Stack<Sus> _free = new Stack<Sus>();
        private readonly List<SKey> _scratch = new List<SKey>();

        public bool Registered { get; private set; }
        public long Seen, Played, SkippedUnder, SkippedMode, NoEntity, SustainCapDrops, BudgetLeftRows, DeferredToFx;
        public readonly long[] PlayedByType = new long[16];
        public int SustainCount { get { return _sus.Count; } }

        public S7pAboveFx(MirrorConsumer m, S7pFxKit kit) { _m = m; _kit = kit; }

        // S11 hook: stacked explosions above water as one escalated effect (Wire S11BlastChain)
        private S11BlastChain _s11Chain;
        private readonly List<S7EventRow> _s11Esc = new List<S7EventRow>();

        public void Register()
        {
            if (Registered || _m == null || _m.C2 == null) return;
            if (!_m.C2.Extensions.Contains(Reader)) _m.C2.Extensions.Add(Reader);
            Registered = true;
        }

        public void Unregister()
        {
            if (!Registered) return;
            try { _m.C2.Extensions.Remove(Reader); } catch (Exception) { }
            Registered = false;
        }

        public void Frame(float now, float dt, S7pAboveMode mode, ref int left, ref long capped, Stopwatch sw)
        {
            if (!Registered) Register();
            if (S11Hooks.ChainClose(_s11Chain, _s11Esc) > 0) // S11 hook: a closed chain plays once with the summed yield
            {
                S11Hooks.Replaying = true;
                try { for (int i = 0; i < _s11Esc.Count; i++) { S7EventRow e = _s11Esc[i]; Apply(ref e, now, mode, ref left, ref capped); } }
                finally { S11Hooks.Replaying = false; _s11Esc.Clear(); }
            }
            S7EventRow r;
            int n = 0;
            while (n < MaxRowsPerFrame && Reader.TryDequeue(out r))
            {
                n++;
                Seen++;
                Apply(ref r, now, mode, ref left, ref capped);
                if (sw.Elapsed.TotalMilliseconds > RowBudgetMs) { BudgetLeftRows += Reader.PendingCount; break; } // the rest next frame
            }
            if (_sus.Count > 0) Sustains(now, dt, ref left, ref capped);
        }

        private void Apply(ref S7EventRow r, float now, S7pAboveMode mode, ref int left, ref long capped)
        {
            // effects-under's polished looks drawn above water too (de.s7p fx above 1): those types are theirs then (no double look)
            if (S7Effects.AboveWater && FxCovers(r.type) && (r.flags & EventFlags.Stop) == 0) { DeferredToFx++; return; }
            if (!S7pAboveRules.Plays(mode, r.type, r.flags))
            {
                if (!S7pScene.PlaysAboveVariants(mode)) SkippedMode++;
                else SkippedUnder++;
                // a sustain that went under water keeps its key (its rows flag it); nothing to emit
                Sus held;
                if ((r.IsSustain || r.type == EventType.Thruster) && _sus.TryGetValue(new SKey(r.entityId, r.key), out held))
                {
                    held.Flags = r.flags;
                    held.Expires = now + Math.Max(0.1f, r.lifetimeMs / 1000f);
                }
                return;
            }
            Transform anchor = null;
            Vector3 pos, dir;
            if (r.Attached)
            {
                if (!TryEntity(r.entityId, out anchor)) { NoEntity++; return; }
                pos = M(r.pos);
                dir = M(r.dir);
            }
            else
            {
                pos = new Vector3(r.pos.x, r.pos.y, r.pos.z);
                dir = new Vector3(r.dir.x, r.dir.y, r.dir.z);
            }
            if (r.IsSustain || S7pAboveRules.IsSustainType(r.type))
            {
                Sustain(ref r, pos, dir, now);
                Count(r.type);
                return;
            }
            Vector3 wpos = anchor != null ? anchor.TransformPoint(pos) : pos;
            Vector3 wdir = anchor != null && dir.sqrMagnitude > 1e-8f ? anchor.TransformDirection(dir) : dir;
            if (r.type == EventType.Explosion && !S11Hooks.ChainAdmit(ref _s11Chain, ref r, wpos)) return; // S11 hook: absorbed into an open chain
            if (r.type == EventType.HullBreak)
            {
                // a timed vapour jet: held as a sustain of its own key (entity, seq) for the row's lifetime
                var k = new SKey(r.entityId, 0xB4EA000000000000UL | r.seq);
                Sus s = Take(k);
                if (s == null) return;
                s.Type = r.type; s.Flags = r.flags; s.Subtype = r.subtype; s.Intensity = 1f; s.Size = r.size;
                s.Attached = r.Attached; s.Entity = r.entityId; s.Pos = pos; s.Dir = dir;
                s.Expires = now + Mathf.Clamp(r.lifetimeMs / 1000f, 0.5f, 4f);
                Count(r.type);
                return;
            }
            int np = S7pAboveRules.OneShot(r.type, r.subtype, r.intensity, r.size, r.lifetimeMs, r.extra, r.flags, _puffs);   // R4: the tracer stops at the hit
            for (int i = 0; i < np; i++)
            {
                int want = _puffs[i].Count;
                int give = S7pAboveRules.Budget(want, ref left, ref capped);
                if (give > 0) _kit.Emit(ref _puffs[i], wpos, wdir, give);
            }
            if (np > 0) Count(r.type);
        }

        /// <summary>The types effects-under's S7p player also draws above water when its <c>above</c> knob is on (EffectsPlayer.S7p.cs).</summary>
        private static bool FxCovers(byte type)
        {
            switch (type)
            {
                case EventType.Thruster: case EventType.WeaponMuzzle: case EventType.BulletStreak: case EventType.Impact:
                case EventType.Explosion: case EventType.Drill: case EventType.PrawnDrill:
                    return true;
                default: return false;
            }
        }

        private void Count(byte type)
        {
            Played++;
            if (type < PlayedByType.Length) PlayedByType[type]++;
        }

        private Sus Take(SKey k)
        {
            Sus s;
            if (_sus.TryGetValue(k, out s)) return s;
            if (_sus.Count >= S7pScene.MaxAboveSustains) { SustainCapDrops++; return null; }
            s = _free.Count > 0 ? _free.Pop() : new Sus();
            s.Key = k;
            for (int i = 0; i < s.Carry.Length; i++) s.Carry[i] = 0f;
            _sus[k] = s;
            return s;
        }

        private void Sustain(ref S7EventRow r, Vector3 pos, Vector3 dir, float now)
        {
            var k = new SKey(r.entityId, r.key);
            if (r.IsStop)
            {
                Sus old;
                if (_sus.TryGetValue(k, out old)) End(old);
                return;
            }
            Sus s = Take(k);
            if (s == null) return;
            s.Type = r.type;
            s.Flags = r.flags;
            s.Subtype = r.subtype;
            s.Extra = r.extra; // S7p-effects-under's flame colour/length (0 = the kind's)
            s.Intensity = r.intensity;
            s.Size = r.size;
            s.Attached = r.Attached;
            s.Entity = r.entityId;
            s.Pos = pos;
            s.Dir = dir;
            s.Expires = now + Math.Max(0.1f, r.lifetimeMs / 1000f);
        }

        private void Sustains(float now, float dt, ref int left, ref long capped)
        {
            _scratch.Clear();
            foreach (KeyValuePair<SKey, Sus> kv in _sus)
            {
                Sus s = kv.Value;
                if (now >= s.Expires) { _scratch.Add(kv.Key); continue; }
                if ((s.Flags & EventFlags.Underwater) != 0) continue; // the S7 player's under water
                if (s.Type == EventType.Thruster && S7Effects.S14KitThrusterOff()) continue; // S14 hook (jets.old): the Prawn jet replaces the kit's thruster puffs above water
                Transform anchor = null;
                if (s.Attached && !TryEntity(s.Entity, out anchor)) { _scratch.Add(kv.Key); continue; }
                Vector3 wpos = anchor != null ? anchor.TransformPoint(s.Pos) : s.Pos;
                Vector3 wdir = anchor != null && s.Dir.sqrMagnitude > 1e-8f ? anchor.TransformDirection(s.Dir) : s.Dir;
                int nr;
                if (s.Type == EventType.HullBreak)
                {
                    _rates[0] = new S7pRate { PerSecond = 30f, Puff = HullJet(s.Size) };
                    nr = 1;
                }
                else nr = S7pAboveRules.Sustain(s.Type, s.Subtype, s.Intensity, s.Size, (s.Flags & EventFlags.Hit) != 0, _rates, s.Extra);
                for (int i = 0; i < nr; i++)
                {
                    int want = S7pAboveRules.Accumulate(_rates[i].PerSecond, dt, ref s.Carry[i], 64);
                    int give = S7pAboveRules.Budget(want, ref left, ref capped);
                    if (give > 0) _kit.Emit(ref _rates[i].Puff, wpos, wdir, give);
                }
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                Sus s;
                if (_sus.TryGetValue(_scratch[i], out s)) End(s);
            }
        }

        private static S7pPuff HullJet(float size)
        {
            return new S7pPuff { Look = S7pLook.Vapor, Size = Mathf.Clamp(size, 0.5f, 2.5f) * 0.4f, Speed = 6f, Spread = 0.2f, Life = 1f, R = 0.95f, G = 0.97f, B = 1f, A = 0.4f };
        }

        private void End(Sus s)
        {
            _sus.Remove(s.Key);
            _free.Push(s);
        }

        /// <summary>Drops every hold (mode change, teardown); the reader keeps its stream (no seq is replayed).</summary>
        public void Clear(string why)
        {
            foreach (Sus s in _sus.Values) _free.Push(s);
            _sus.Clear();
        }

        /// <summary>The mirrored entity's Entity transform (a grid, subgrid or the C1 character); false while it has no view or pose.</summary>
        private bool TryEntity(ulong id, out Transform t)
        {
            t = null;
            if (id == 0) return false;
            if (_m.S7tIsImpostor(id)) { S7tImpostors.EffectsSkipped++; return false; } // S7t-sn-altitude (T3): an impostor has no effects
            MirrorGridView v;
            if (_m.ViewMap.TryGetValue(id, out v) && v != null && v.Alive && v.HasPose && v.EntityT != null) { t = v.EntityT; return true; }
            CharacterMirror c = _m.Character;
            if (c != null && c.CharacterId == id && c.EntityTransform != null) { t = c.EntityTransform; return true; }
            return false;
        }

        private static Vector3 M(F32x3 v) { return new Vector3(v.x, v.y, -v.z); }
    }

    // ===================================================================================================== P1 missiles

 /// <summary>
 /// The missile trails (P1): the mirrored entities whose GridAdd carries the Missile bit (rescanned every 3 frames, at
 /// most <see cref="S7pScene.MaxMissileTrails"/>), each emitting from its tail along its flight (the position change
 /// between frames, else the entity's forward): flame and smoke above the waterline, a bubble stream below
 /// (<see cref="S7pScene.MissileTrail"/> decides per route and depth).
 /// </summary>
    internal sealed class S7pMissileTrails
    {
        private readonly MirrorConsumer _m;
        private readonly S7pFxKit _kit;
        private readonly S7pRate[] _rates = new S7pRate[S7pAboveRules.MaxRates];

        private sealed class Trail
        {
            public ulong Id;
            public Vector3 Last;
            public bool HasLast, Seen;
            public readonly float[] Carry = new float[S7pAboveRules.MaxRates];
        }

        private readonly Dictionary<ulong, Trail> _trails = new Dictionary<ulong, Trail>();
        private readonly List<ulong> _gone = new List<ulong>();
        public long Emits, CapDrops;
        public int Count { get { return _trails.Count; } }

        public S7pMissileTrails(MirrorConsumer m, S7pFxKit kit) { _m = m; _kit = kit; }

        public void Rescan()
        {
            foreach (Trail t in _trails.Values) t.Seen = false;
            foreach (GridRecord g in _m.State.Grids.Values)
            {
                if (!S7pScene.IsMissile(g.Flags)) continue;
                Trail t;
                if (!_trails.TryGetValue(g.Id, out t))
                {
                    if (_trails.Count >= S7pScene.MaxMissileTrails) { CapDrops++; continue; }
                    t = new Trail { Id = g.Id };
                    _trails[g.Id] = t;
                }
                t.Seen = true;
            }
            _gone.Clear();
            foreach (Trail t in _trails.Values) if (!t.Seen) _gone.Add(t.Id);
            for (int i = 0; i < _gone.Count; i++) _trails.Remove(_gone[i]);
        }

        public void Frame(float dt, S7pAboveMode mode, ref int left, ref long capped)
        {
            if (_trails.Count == 0 || !(dt > 0f)) return;
            foreach (Trail t in _trails.Values)
            {
                MirrorGridView v;
                if (!_m.ViewMap.TryGetValue(t.Id, out v) || v == null || !v.Alive || !v.HasPose || v.EntityT == null) { t.HasLast = false; continue; }
                Vector3 p = v.EntityT.position;
                Vector3 fwd = t.HasLast ? p - t.Last : Vector3.zero;
                if (fwd.sqrMagnitude < 1e-6f) fwd = v.EntityT.forward;
                fwd.Normalize();
                t.Last = p;
                t.HasLast = true;
                bool under = p.y < 0f;
                if (!S7pScene.MissileTrail(mode, under)) continue;
                int nr = S7pAboveRules.MissileTrail(under, _rates);
                Vector3 tail = p - fwd * 0.9f;
                for (int i = 0; i < nr; i++)
                {
                    int want = S7pAboveRules.Accumulate(_rates[i].PerSecond, dt, ref t.Carry[i], 32);
                    int give = S7pAboveRules.Budget(want, ref left, ref capped);
                    if (give > 0) Emits += _kit.Emit(ref _rates[i].Puff, tail, -fwd, give);
                }
            }
        }

        public void Clear() { _trails.Clear(); }
    }

    // ===================================================================================================== P7 projections

 /// <summary>
 /// The projector holograms (P7): every chunk and child renderer of an entity whose GridAdd carries the Projection bit
 /// takes one shared material, Unity's Standard shader in Fade mode tinted SE's hologram blue with an emission so it
 /// reads at night; Sprites/Default as the fallback. Shadows off. Re-applied every frame (a renderer whose Marmoset slot
 /// resolves later is caught at once) and when a cell is uploaded. No collider exists on mirrored entities; SE sends no
 /// collision skin, room set or light for a projection.
 /// </summary>
    internal sealed class S7pProjectionView
    {
        private readonly MirrorConsumer _m;
        private readonly List<ulong> _ids = new List<ulong>();
        private Material _holo;
        private bool _looked, _none;
        public long Swaps, Renderers;
        public string MaterialName = "none";
        public int Count { get { return _ids.Count; } }

        public S7pProjectionView(MirrorConsumer m) { _m = m; }

        public void Rescan()
        {
            _ids.Clear();
            foreach (GridRecord g in _m.State.Grids.Values)
                if (S7pScene.IsProjection(g.Flags)) _ids.Add(g.Id);
        }

        public void Frame()
        {
            if (_ids.Count == 0) return;
            long n = 0;
            for (int i = 0; i < _ids.Count; i++)
            {
                MirrorGridView v;
                if (_m.ViewMap.TryGetValue(_ids[i], out v) && v != null && v.Alive) n += Apply(v);
            }
            Renderers = n;
        }

        public void ApplyNow(MirrorGridView v)
        {
            GridRecord g;
            if (v == null || !_m.State.Grids.TryGetValue(v.Id, out g) || !S7pScene.IsProjection(g.Flags)) return;
            if (!_ids.Contains(v.Id)) _ids.Add(v.Id);
            Apply(v);
        }

        private int Apply(MirrorGridView v)
        {
            Material m = Holo();
            if (m == null) return 0;
            int n = 0;
            foreach (ChunkView c in v.Chunks.Values) if (c.Renderer != null) { Set(c.Renderer, m); n++; }
            foreach (ChildPartView ch in v.Children.Values)
                foreach (ChildRenderer r in ch.Renderers)
                    if (r.Renderer != null) { Set(r.Renderer, m); n++; }
            return n;
        }

        private void Set(Renderer r, Material m)
        {
            if (r.sharedMaterial != m) { r.sharedMaterial = m; Swaps++; }
            if (r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.Off;
            if (r.receiveShadows) r.receiveShadows = false;
        }

        private Material Holo()
        {
            if (_holo != null) return _holo;
            if (_none) return null;
            if (!_looked)
            {
                _looked = true;
                Shader std = null, sprite = null;
                try { std = Shader.Find("Standard"); } catch (Exception) { }
                try { sprite = Shader.Find("Sprites/Default"); } catch (Exception) { }
                var col = new Color(S7pProjectionRules.R, S7pProjectionRules.G, S7pProjectionRules.B, S7pProjectionRules.A);
                if (std != null && std.isSupported)
                {
                    var m = new Material(std) { name = "DE.S7p hologram (Standard Fade)" };
                    m.SetFloat("_Mode", 2f);
                    m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    m.SetInt("_ZWrite", 0);
                    m.DisableKeyword("_ALPHATEST_ON");
                    m.EnableKeyword("_ALPHABLEND_ON");
                    m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    if (m.HasProperty("_Color")) m.SetColor("_Color", col);
                    if (m.HasProperty("_EmissionColor"))
                    {
                        m.EnableKeyword("_EMISSION");
                        m.SetColor("_EmissionColor", new Color(S7pProjectionRules.EmissionR, S7pProjectionRules.EmissionG, S7pProjectionRules.EmissionB, 1f));
                    }
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.2f);
                    m.renderQueue = 3000;
                    _holo = m;
                    MaterialName = "standard";
                }
                else if (sprite != null && sprite.isSupported)
                {
                    var m = new Material(sprite) { name = "DE.S7p hologram (Sprites/Default)" };
                    if (m.HasProperty("_Color")) m.SetColor("_Color", col);
                    m.renderQueue = 3000;
                    _holo = m;
                    MaterialName = "sprite";
                }
                else
                {
                    _none = true;
                    DeLog.WarnOnce("s7p.holonone", "DE.S7p hologram material=none (no Standard or Sprites/Default shader): projections keep SE's block materials");
                    return null;
                }
                DeLog.Info("DE.S7p hologram material=" + MaterialName + " colour (" + col.r + ", " + col.g + ", " + col.b + ", " + col.a + ")");
            }
            return _holo;
        }

        public void Clear()
        {
            _ids.Clear();
            if (_holo != null) Object.Destroy(_holo);
            _holo = null;
            _looked = false;
            _none = false;
        }
    }
}
