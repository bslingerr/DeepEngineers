using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using UnityEngine;
using DonorRole = DeepEngineers.Wire.S7pEffectRules.DonorRole;
using Object = UnityEngine.Object;

namespace DeepEngineers.SN
{
    /// <summary>The composed S7p looks (S7p-effects-under), each one template built from a borrowed Subnautica particle system.</summary>
    internal enum FxComposite
    {
        /// <summary>P2: the thruster's flame cone (glow donor, local space, tinted with the thruster's colour).</summary>
        Flame = 0,
        /// <summary>P2: the heat shimmer along the flame (a distortion donor; only when the player's game has one).</summary>
        Shimmer,
        /// <summary>P3: the vapor/steam plume where a flame meets the sea surface (smoke donor, world space).</summary>
        Plume,
        /// <summary>P5: the muzzle flash sprites (glow donor, local space, emitted).</summary>
        Muzzle,
        /// <summary>P5: the tracer (glow donor, one stretched particle flying muzzle → hit).</summary>
        Tracer,
        /// <summary>P4/P5/P6a: sparks (spark donor, stretched, world space; emitted or held with a rate).</summary>
        Sparks,
        /// <summary>P4/P5/P6a: fine bubbles (the Seamoth's own bubbles, world space, emitted: never the diver's breath bubbles).</summary>
        Puff,
        /// <summary>P4: the fireball (glow donor, world space, emitted).</summary>
        Fireball,
        /// <summary>P4: the smoke above water (smoke donor, world space, emitted).</summary>
        Smoke,
        /// <summary>P4: the shock ring (smoke donor, world space, emitted on a circle).</summary>
        Ring,
        /// <summary>P6a: the drill's sediment/dust plume (dust donor, never additive, world space, held with a rate).</summary>
        Dust,
        Count,
    }

    /// <summary>A Subnautica particle system lending its material (and its texture sheet, vertex streams, custom data) to a role.</summary>
    internal sealed class S7pDonor
    {
        public FxSource Source;
        public ParticleSystem Ps;
        public string Material, Shader, Texture;
        public bool Additive, Stretched, Relaxed;
        public int Score;

        public string Describe()
        {
            return Source + "/" + (Ps != null ? Ps.name : "?") + " mat=" + (Material ?? "?") + " shader=" + (Shader ?? "?") + " tex=" + (Texture ?? "?") +
                   " additive=" + (Additive ? "1" : "0") + " score=" + Score.ToString(CultureInfo.InvariantCulture) + (Relaxed ? " (by name)" : "");
        }
    }

    /// <summary>
 /// S7p-effects-under (ROADMAP S7p P2–P6a): the looks composed at run time from Subnautica's own particle systems. For
 /// each role (glow, smoke, spark, dust, shimmer) the best particle system of the found sources is chosen by
 /// <see cref="S7pEffectRules.DonorScore"/> (names, blending, the source's rank in the role's list); each composite is a
 /// clone of its donor's GameObject stripped to its ParticleSystem (children, scripts, sub-emitters, collision, lights,
 /// trails and forces off), so the donor's material, texture sheet, vertex streams and custom data stay as the game made
 /// them, and then the recipe's shape, lifetime, colour and emission are set. Templates live inactive under the library's
 /// template root (the game's prefabs are never touched, nothing is shipped or written). A role without a donor turns
 /// its composites off (logged once; the S7 look stays). Main thread.
 /// </summary>
    internal sealed class S7pRecipes
    {
        private readonly GameObject[] _tpl = new GameObject[(int)FxComposite.Count];
        private readonly string[] _why = new string[(int)FxComposite.Count];
        private readonly S7pDonor[] _donor = new S7pDonor[(int)DonorRole.Count];
        public bool Built { get; private set; }

        public GameObject Template(FxComposite c) { return _tpl[(int)c]; }
        public S7pDonor Donor(DonorRole r) { return _donor[(int)r]; }

        /// <summary>The sources each role tries, best first (the rank feeds the score).</summary>
        private static readonly FxSource[][] Preference =
        {
            /* Glow */ new[] { FxSource.ExosuitJets, FxSource.FlareFx, FxSource.WelderFx, FxSource.CrashDetonate, FxSource.TorpedoExplosion, FxSource.SeamothDestruction, FxSource.HeatBladeFx, FxSource.ExosuitDestruction },
            /* Smoke */ new[] { FxSource.SeamothSmoke, FxSource.SeamothDestruction, FxSource.ExosuitDestruction, FxSource.FlareFx, FxSource.TorpedoExplosion, FxSource.CrashDetonate, FxSource.ExosuitLand, FxSource.ImpactRock },
            /* Spark */ new[] { FxSource.SeamothSparks, FxSource.WelderFx, FxSource.GrinderMetal, FxSource.ImpactMetal, FxSource.FlareFx, FxSource.CrashDetonate },
            /* Dust */ new[] { FxSource.ExosuitLand, FxSource.DrillRock, FxSource.DrillSand, FxSource.ImpactRock, FxSource.SeamothSmoke, FxSource.SeamothDestruction },
            /* Shimmer */ new[] { FxSource.HeatBladeFx, FxSource.ExosuitJets, FxSource.FlareFx, FxSource.SeamothDestruction, FxSource.CrashDetonate, FxSource.TorpedoExplosion, FxSource.WelderFx },
        };

        /// <summary>Picks the donors and builds every composite under <paramref name="home"/> (inactive). Logs the choices.</summary>
        public void Build(S7EffectLibrary lib, Transform home)
        {
            for (int r = 0; r < (int)DonorRole.Count; r++)
            {
                _donor[r] = Pick(lib, (DonorRole)r, false) ?? Pick(lib, (DonorRole)r, true);
                DeLog.Info("DE.S7p fx donor " + (DonorRole)r + " = " + (_donor[r] != null ? _donor[r].Describe() : "none (that role's composites off; the S7 look stays)"));
            }
            S7pDonor glow = _donor[(int)DonorRole.Glow], smoke = _donor[(int)DonorRole.Smoke], spark = _donor[(int)DonorRole.Spark],
                     dust = _donor[(int)DonorRole.Dust] ?? _donor[(int)DonorRole.Smoke], shimmer = _donor[(int)DonorRole.Shimmer];
            S7pDonor bubbles = BubbleDonor(lib);
            if (smoke == null)
            {

                // (donor Smoke none, Plume/Smoke/Dust off): a synthetic donor with the above-water kit's soft alpha dot
                smoke = SyntheticSmokeDonor(home);
                if (dust == null) dust = smoke;
                DeLog.Info("DE.S7p fx donor Smoke = " + (smoke != null ? "synthetic (" + smoke.Shader + ")" : "none, no synthetic either") + " (owner 2026-10-18: the plume needs it)");
            }
            Make(FxComposite.Flame, glow, home, ConfigureFlame);
            Make(FxComposite.Shimmer, shimmer, home, ConfigureShimmer);
            Make(FxComposite.Plume, smoke, home, ConfigurePlume);
            Make(FxComposite.Muzzle, glow, home, ConfigureMuzzle);
            Make(FxComposite.Tracer, glow, home, ConfigureTracer);
            Make(FxComposite.Sparks, spark ?? glow, home, ConfigureSparks);
            Make(FxComposite.Puff, bubbles, home, ConfigurePuff);
            Make(FxComposite.Fireball, glow, home, ConfigureFireball);
            Make(FxComposite.Smoke, smoke, home, ConfigureSmoke);
            Make(FxComposite.Ring, smoke ?? dust, home, ConfigureRing);
            Make(FxComposite.Dust, dust, home, ConfigureDust);
            Built = true;
            DeLog.Info("DE.S7p fx composites " + Describe());
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _tpl.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append((FxComposite)i).Append('=').Append(_tpl[i] != null ? "built" : "off(" + (_why[i] ?? "?") + ")");
            }
            return sb.ToString();
        }

        public void Rows(JsonOut j)
        {
            j.Obj("composites");
            for (int i = 0; i < _tpl.Length; i++) j.Str(((FxComposite)i).ToString(), _tpl[i] != null ? "built" : "off");
            j.EndObj();
            j.Obj("donors");
            for (int r = 0; r < _donor.Length; r++) j.Str(((DonorRole)r).ToString(), _donor[r] != null ? _donor[r].Source + "/" + (_donor[r].Ps != null ? _donor[r].Ps.name : "?") : "none");
            j.EndObj();
        }

        public void Detail(JsonObjectWriter w)
        {
            for (int r = 0; r < _donor.Length; r++) w.Str("donor." + (DonorRole)r, _donor[r] != null ? _donor[r].Describe() : "none");
            w.Str("composites", Describe());
        }

        /// <summary>Templates are destroyed with the library's root; only the references go.</summary>
        public void Clear()
        {
            for (int i = 0; i < _tpl.Length; i++) { _tpl[i] = null; _why[i] = null; }
            for (int r = 0; r < _donor.Length; r++) _donor[r] = null;
            Built = false;
        }

        // ============================================================================================ donors

        private static S7pDonor Pick(S7EffectLibrary lib, DonorRole role, bool relaxed)
        {
            FxSource[] list = Preference[(int)role];
            S7pDonor best = null;
            for (int rank = 0; rank < list.Length; rank++)
            {
                GameObject t = lib.Template(list[rank]);
                if (t == null) continue;
                ParticleSystem[] all = t.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    ParticleSystem ps = all[i];
                    if (ps == null) continue;
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    Material m = r != null ? r.sharedMaterial : null;
                    if (m == null || r.renderMode == ParticleSystemRenderMode.Mesh) continue; // mesh particles (debris) lend no sprite
                    string shader = m.shader != null ? m.shader.name : null;
                    Texture tex = null;
                    try { tex = m.mainTexture; } catch (Exception) { }
                    bool additive = IsAdditive(m);
                    bool stretched = r.renderMode == ParticleSystemRenderMode.Stretch;
                    string texName = tex != null ? tex.name : null;
                    int score;
                    if (!relaxed) score = S7pEffectRules.DonorScore(role, rank, ps.name, m.name, shader, texName, additive, stretched);
                    else
                    {
                        // second pass: the blending could not be read (a custom shader): trust the names alone
                        if (!S7pEffectRules.DonorKeyword(role, ps.name, m.name, texName)) continue;
                        bool want = role == DonorRole.Glow || role == DonorRole.Spark;
                        if (role == DonorRole.Dust || role == DonorRole.Smoke) { if (additive) continue; }
                        score = S7pEffectRules.DonorScore(role, rank, ps.name, m.name, shader, texName, want, stretched);
                    }
                    if (score <= 0) continue;
                    if (best == null || score > best.Score)
                        best = new S7pDonor { Source = list[rank], Ps = ps, Material = m.name, Shader = shader, Texture = texName, Additive = additive, Stretched = stretched, Score = score, Relaxed = relaxed };
                }
            }
            return best;
        }

        /// <summary>An inactive, stopped ParticleSystem with the kit's synthetic alpha material: the donor of last resort for smoke/plume/dust.</summary>
        private static S7pDonor SyntheticSmokeDonor(Transform home)
        {
            try
            {
                string shader;
                Material m = S7pFxKit.SyntheticMaterial(false, out shader);
                if (m == null) return null;
                var go = new GameObject("DE.S7p.SyntheticSmokeDonor");
                go.transform.SetParent(home, false);
                go.SetActive(false);
                var ps = go.AddComponent<ParticleSystem>();
                ParticleSystem.MainModule main = ps.main;
                main.playOnAwake = false;
                main.loop = true;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                ParticleSystem.EmissionModule em = ps.emission;
                em.rateOverTime = 0f;
                var r = go.GetComponent<ParticleSystemRenderer>() ?? go.AddComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.sharedMaterial = m;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                return new S7pDonor { Source = FxSource.Bubbles, Ps = ps, Material = m.name, Shader = shader, Texture = "DE.S7p dot alpha", Additive = false, Score = 1 };
            }
            catch (Exception e)
            {
                DeLog.Warn("DE.S7p fx synthetic smoke donor failed: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>The Seamoth's own bubbles (S7's thruster source) lend the fine bubbles of every S7p look.</summary>
        private static S7pDonor BubbleDonor(S7EffectLibrary lib)
        {
            GameObject t = lib.Template(FxSource.Bubbles);
            ParticleSystem ps = t != null ? t.GetComponent<ParticleSystem>() ?? t.GetComponentInChildren<ParticleSystem>(true) : null;
            if (ps == null) return null;
            var r = ps.GetComponent<ParticleSystemRenderer>();
            Material m = r != null ? r.sharedMaterial : null;
            return new S7pDonor { Source = FxSource.Bubbles, Ps = ps, Material = m != null ? m.name : null, Shader = m != null && m.shader != null ? m.shader.name : null, Score = 1 };
        }

        /// <summary>Additive blending: the shader's name says so, or its <c>_DstBlend</c> is One (Unity's BlendMode.One = 1).</summary>
        public static bool IsAdditive(Material m)
        {
            if (m == null) return false;
            string sh = m.shader != null ? (m.shader.name ?? "").ToLowerInvariant() : "";
            if (sh.Contains("additive")) return true;
            try
            {
                if (m.HasProperty("_DstBlend") && Mathf.Approximately(m.GetFloat("_DstBlend"), 1f)) return true;
            }
            catch (Exception) { }
            return false;
        }

        // ============================================================================================ composites

        private void Make(FxComposite c, S7pDonor d, Transform home, Action<ParticleSystem, ParticleSystemRenderer> configure)
        {
            if (d == null || d.Ps == null) { _why[(int)c] = "no donor"; return; }
            GameObject go = null;
            try
            {
                go = Object.Instantiate(d.Ps.gameObject, home, false);
                go.name = "DE.S7p.Fx." + c;
                go.SetActive(false);
                for (int i = go.transform.childCount - 1; i >= 0; i--) Object.DestroyImmediate(go.transform.GetChild(i).gameObject);
                Component[] cs = go.GetComponents<Component>();
                for (int i = 0; i < cs.Length; i++)
                {
                    Component x = cs[i];
                    if (x == null || x is Transform || x is ParticleSystem || x is ParticleSystemRenderer) continue;
                    Object.DestroyImmediate(x);
                }
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                ParticleSystem ps = go.GetComponent<ParticleSystem>();
                ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
                if (ps == null || r == null) throw new InvalidOperationException("the clone has no ParticleSystem/renderer");
                Neutralise(ps);
                configure(ps, r);
                _tpl[(int)c] = go;
            }
            catch (Exception e)
            {
                if (go != null) Object.Destroy(go);
                _why[(int)c] = e.GetType().Name;
                DeLog.WarnOnce("s7p.composite." + c, "DE.S7p fx composite " + c + " FAILED " + e.GetType().Name + ": " + e.Message + " (that look off; the S7 look stays)");
            }
        }

        /// <summary>Everything a recipe does not ask for goes off; the material-facing modules (sheet, streams, custom data) stay.</summary>
        private static void Neutralise(ParticleSystem ps)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.prewarm = false;
            main.startSize3D = false;
            main.startDelay = 0f;
            main.gravityModifier = 0f;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.stopAction = ParticleSystemStopAction.None;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            ParticleSystem.EmissionModule em = ps.emission;
            em.enabled = false;
            em.rateOverTimeMultiplier = 0f;
            em.rateOverDistanceMultiplier = 0f;
            em.SetBursts(new ParticleSystem.Burst[0]);
            ParticleSystem.SubEmittersModule sub = ps.subEmitters; sub.enabled = false;
            ParticleSystem.CollisionModule col = ps.collision; col.enabled = false;
            ParticleSystem.TriggerModule trig = ps.trigger; trig.enabled = false;
            ParticleSystem.LightsModule lights = ps.lights; lights.enabled = false;
            ParticleSystem.ExternalForcesModule ext = ps.externalForces; ext.enabled = false;
            ParticleSystem.InheritVelocityModule inh = ps.inheritVelocity; inh.enabled = false;
            ParticleSystem.TrailModule trails = ps.trails; trails.enabled = false;
            ParticleSystem.ForceOverLifetimeModule force = ps.forceOverLifetime; force.enabled = false;
            ParticleSystem.VelocityOverLifetimeModule vel = ps.velocityOverLifetime; vel.enabled = false;
            ParticleSystem.LimitVelocityOverLifetimeModule lim = ps.limitVelocityOverLifetime; lim.enabled = false;
            ParticleSystem.ColorBySpeedModule cbs = ps.colorBySpeed; cbs.enabled = false;
            ParticleSystem.SizeBySpeedModule sbs = ps.sizeBySpeed; sbs.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;
            shape.scale = Vector3.one;
        }

        private static void Fade(ParticleSystem ps, float peakAt, float peakAlpha)
        {
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(peakAt <= 0f ? peakAlpha : 0f, 0f), new GradientAlphaKey(peakAlpha, Math.Max(0.01f, peakAt)), new GradientAlphaKey(0f, 1f) });
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        private static void Grow(ParticleSystem ps, float from, float to)
        {
            ParticleSystem.SizeOverLifetimeModule sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.separateAxes = false;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, from, 1f, to));
        }

        private static void ConfigureFlame(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = S7pEffectRules.FlameLifetimeS;
            main.startSpeed = 10f;
            main.startSize = 1f;
            main.startColor = Color.white;
            main.maxParticles = 320;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 5f;
            shape.radius = 0.5f;
            shape.radiusThickness = 1f;
            shape.arc = 360f;
            Grow(ps, 1f, 0.35f);
            Fade(ps, 0.08f, 1f);
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ConfigureShimmer(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = 0.4f;
            main.startSpeed = 4f;
            main.startSize = 1.5f;
            main.startColor = new Color(1f, 1f, 1f, 0.6f);
            main.maxParticles = 120;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 10f;
            shape.radius = 0.6f;
            Grow(ps, 0.8f, 1.6f);
            Fade(ps, 0.2f, 0.7f);
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ConfigurePlume(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 2.8f);
            main.startSize = 2f;
            main.startColor = new Color(1f, 1f, 1f, 0.75f);
            main.gravityModifier = -0.04f; // steam rises a little faster as it goes
            main.maxParticles = 500;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 22f;
            shape.radius = 1f;
            shape.radiusThickness = 1f;
            Grow(ps, 0.6f, 2.6f);
            Fade(ps, 0.15f, 0.8f);
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ConfigureMuzzle(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = 0.05f;
            main.startSpeed = 0f;
            main.startSize = 1f;
            main.startColor = new Color(1f, 0.9f, 0.7f, 1f);
            main.maxParticles = 24;
            ParticleSystem.ShapeModule shape = ps.shape; shape.enabled = false;
            Grow(ps, 0.8f, 1.6f);
            Fade(ps, 0f, 1f);
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ConfigureTracer(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 0.1f;
            main.startSpeed = 0f;
            main.startSize = 0.08f;
            main.startColor = new Color(1f, 0.9f, 0.65f, 1f);
            main.maxParticles = 8;
            ParticleSystem.ShapeModule shape = ps.shape; shape.enabled = false;
            ParticleSystem.SizeOverLifetimeModule sol = ps.sizeOverLifetime; sol.enabled = false;
            ParticleSystem.NoiseModule noise = ps.noise; noise.enabled = false; // a tracer flies straight
            ParticleSystem.RotationOverLifetimeModule rot = ps.rotationOverLifetime; rot.enabled = false;
            Fade(ps, 0f, 1f);
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.lengthScale = 1f;
            r.velocityScale = 0.01f;
            r.cameraVelocityScale = 0f;
        }

        private static void ConfigureSparks(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.06f);
            main.startColor = new Color(1f, 0.8f, 0.45f, 1f);
            main.maxParticles = 240;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 35f;
            shape.radius = 0.05f;
            ParticleSystem.LimitVelocityOverLifetimeModule lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.limitMultiplier = 1000f;
            lim.drag = 0.5f;
            Grow(ps, 1f, 0.4f);
            Fade(ps, 0f, 1f);
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.lengthScale = 1f;
            r.velocityScale = 0.04f;
            r.cameraVelocityScale = 0f;
        }

        private static void ConfigurePuff(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 1.8f;
            main.startSpeed = 0f;
            main.startSize = 0.06f;
            main.gravityModifier = -0.02f; // buoyant
            main.maxParticles = 800;
            ParticleSystem.ShapeModule shape = ps.shape; shape.enabled = false;
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ConfigureFireball(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 0.6f;
            main.startSpeed = 0f;
            main.startSize = 1f;
            main.startColor = new Color(1f, 0.65f, 0.3f, 1f);
            main.maxParticles = 400;
            ParticleSystem.ShapeModule shape = ps.shape; shape.enabled = false;
            ParticleSystem.LimitVelocityOverLifetimeModule lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.limitMultiplier = 1000f;
            lim.drag = 2f;
            Grow(ps, 0.5f, 1.7f);
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(1f, 0.6f, 0.35f), 0.4f), new GradientColorKey(new Color(0.3f, 0.25f, 0.25f), 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.9f, 0.3f), new GradientAlphaKey(0f, 1f) });
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(g);
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ConfigureSmoke(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 4f;
            main.startSpeed = 0f;
            main.startSize = 2f;
            main.startColor = new Color(0.25f, 0.23f, 0.22f, 0.85f);
            main.gravityModifier = -0.03f;
            main.maxParticles = 400;
            ParticleSystem.ShapeModule shape = ps.shape; shape.enabled = false;
            ParticleSystem.LimitVelocityOverLifetimeModule lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.limitMultiplier = 1000f;
            lim.drag = 1f;
            Grow(ps, 0.6f, 2.2f);
            Fade(ps, 0.12f, 0.85f);
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ConfigureRing(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 0.6f;
            main.startSpeed = 0f;
            main.startSize = 1f;
            main.startColor = new Color(0.8f, 0.88f, 0.92f, 0.65f);
            main.maxParticles = 200;
            ParticleSystem.ShapeModule shape = ps.shape; shape.enabled = false;
            ParticleSystem.LimitVelocityOverLifetimeModule lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.limitMultiplier = 1000f;
            lim.drag = 1.5f;
            Grow(ps, 0.6f, 1.8f);
            Fade(ps, 0.05f, 0.7f);
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ConfigureDust(ParticleSystem ps, ParticleSystemRenderer r)
        {
            ParticleSystem.MainModule main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 3f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.6f);
            main.startSize = 0.5f;
            main.startColor = new Color(0.42f, 0.38f, 0.33f, 0.55f);
            main.gravityModifier = -0.005f; // sediment hangs in the water
            main.maxParticles = 360;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 40f;
            shape.radius = 0.15f;
            ParticleSystem.LimitVelocityOverLifetimeModule lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.limitMultiplier = 1000f;
            lim.drag = 1.2f;
            Grow(ps, 0.5f, 2.5f);
            Fade(ps, 0.12f, 0.8f);
            r.renderMode = ParticleSystemRenderMode.Billboard;
        }

        // ============================================================================================ helpers

 /// <summary>
 /// The light cap (P6a "light capped"; S7p): every <c>Light</c> under <paramref name="go"/> gets at most
 /// <paramref name="maxIntensity"/> and <paramref name="maxRange"/>; returns how many were lowered.
 /// </summary>
        public static int CapLights(GameObject go, float maxIntensity, float maxRange)
        {
            if (go == null) return 0;
            int n = 0;
            Light[] ls = go.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < ls.Length; i++)
            {
                Light l = ls[i];
                if (l == null) continue;
                bool changed = false;
                if (l.intensity > maxIntensity) { l.intensity = maxIntensity; changed = true; }
                if (l.range > maxRange) { l.range = maxRange; changed = true; }
                if (changed) n++;
            }
            return n;
        }

        /// <summary>Lights under <paramref name="go"/> ("intensity/range" list, for the source log).</summary>
        public static string DescribeLights(GameObject go)
        {
            if (go == null) return "";
            Light[] ls = go.GetComponentsInChildren<Light>(true);
            if (ls.Length == 0) return "no lights";
            var sb = new StringBuilder();
            for (int i = 0; i < ls.Length && i < 6; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ls[i].intensity.ToString("0.##", CultureInfo.InvariantCulture)).Append('/').Append(ls[i].range.ToString("0.#", CultureInfo.InvariantCulture)).Append('m');
            }
            return ls.Length + " lights " + sb;
        }

        /// <summary>The particle systems of a source (name, shader, additive) for the source log (≤ 10).</summary>
        public static string DescribeSystems(GameObject go)
        {
            if (go == null) return "";
            ParticleSystem[] all = go.GetComponentsInChildren<ParticleSystem>(true);
            var sb = new StringBuilder();
            for (int i = 0; i < all.Length && i < 10; i++)
            {
                var r = all[i].GetComponent<ParticleSystemRenderer>();
                Material m = r != null ? r.sharedMaterial : null;
                if (i > 0) sb.Append("; ");
                sb.Append(all[i].name).Append('(').Append(m != null ? m.name : "-").Append(',').Append(m != null && m.shader != null ? m.shader.name : "-")
                  .Append(IsAdditive(m) ? ",add" : "").Append(r != null && r.renderMode == ParticleSystemRenderMode.Stretch ? ",stretch" : "").Append(')');
            }
            if (all.Length > 10) sb.Append("; +").Append(all.Length - 10);
            return sb.ToString();
        }
    }
}
