using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DeepEngineers.SN
{
    /// <summary>The particle sources the effect player draws with (all found in the player's own Subnautica at run time).</summary>
    internal enum FxSource
    {
        /// <summary><c>SeaMoth.bubbles</c>: thruster trails, hull-break columns, the fallback of every under-water look.</summary>
        Bubbles = 0,
        /// <summary><c>PlayerBreathBubbles.bubblesPrefab</c>: muzzle, impact and grid-impact bursts, bullet streaks.</summary>
        BreathBubbles,
        /// <summary><c>VFXConstructing.surfaceSplashFX</c> of the Seamoth prefab.</summary>
        Splash,
        /// <summary><c>Crash.detonateParticlePrefab</c> (the crashfish detonation; its own radius 5 m).</summary>
        CrashDetonate,
        /// <summary><c>SeamothTorpedo.explosionPrefab</c> (the explosion fallback).</summary>
        TorpedoExplosion,
        /// <summary>The repair tool's <c>Welder.fxControl</c> emitter prefab (welder and repair sustains).</summary>
        WelderFx,
        /// <summary><c>ExosuitDrillArm.drillFXprefab</c> (the Prawn drill).</summary>
        PrawnDrillFx,
        /// <summary><c>VFXSubLeakPoint.leakEffectPrefabs[0]</c> when a loaded scene has one (hull break; optional).</summary>
        Leak,
        /// <summary><c>VFXSurfaceTypeManager</c> prefabs: impact on metal, on rock, the fallback surface.</summary>
        ImpactMetal,
        ImpactRock,
        ImpactFallback,
        /// <summary><c>VFXSurfaceTypeManager</c> diamondBlade on metal (the grinder).</summary>
        GrinderMetal,
        /// <summary><c>VFXSurfaceTypeManager</c> exoDrill on rock / sand (the sediment cloud of the hand drill).</summary>
        DrillRock,
        DrillSand,
        // ---- S7p-effects-under: more of the player's own effects, found the same way (donors of the
 // composed looks of EffectsRecipes.cs; the two destruction effects are played as the explosion prefab)
 /// <summary><c>Exosuit.fxcontrol.emitters[0].fx</c>: the Prawn's jump jets (a thruster under water).</summary>
        ExosuitJets,
        /// <summary><c>Exosuit.fxcontrol.emitters[2].fx</c>: the Prawn's landing puff (sediment).</summary>
        ExosuitLand,
        /// <summary><c>Flare.fxControl.emitters[*].fx</c>: the burning flare (an underwater flame).</summary>
        FlareFx,
        /// <summary><c>HeatBlade.fxControl.emitters[*].fx</c>: the thermoblade's heat.</summary>
        HeatBladeFx,
        /// <summary><c>Vehicle.destructionEffect</c> of the Seamoth (its gameplay parts stripped): the P4 explosion.</summary>
        SeamothDestruction,
        /// <summary><c>Vehicle.destructionEffect</c> of the Prawn (stripped; the explosion's fallback and a donor).</summary>
        ExosuitDestruction,
        /// <summary><c>VFXSeamothDamages.smokeParticles</c> (the damaged Seamoth's smoke): the plume's steam.</summary>
        SeamothSmoke,
        /// <summary><c>VFXSeamothDamages.sparksPrefabs[0]</c> (the damaged Seamoth's sparks).</summary>
        SeamothSparks,
        Count,
    }

    /// <summary>The FMOD events the effect player starts (assets of the player's own game, by <c>FMODAsset.path</c>).</summary>
    internal enum FxSound
    {
        /// <summary><c>SeaMoth.engineSound.engineRpmSFX.asset</c>, parameter <c>rpm</c>.</summary>
        EngineLoop = 0,
        /// <summary><c>Welder.weldSound.startLoopSound.asset</c> (repair tool loop).</summary>
        WeldLoop,
        /// <summary><c>ExosuitDrillArm.loop.asset</c> / <c>loopHit.asset</c>.</summary>
        DrillLoop,
        DrillLoopHit,
        /// <summary><c>SeamothTorpedo.fireSound</c> (muzzle, HYPOTHESIS feel).</summary>
        TorpedoFire,
        /// <summary><c>CollisionSound.hitSoundMedium</c> / <c>hitSoundFast</c> of the Seamoth prefab (grid impacts, HYPOTHESIS).</summary>
        HitMedium,
        HitFast,
        /// <summary><c>Vehicle.splashSound</c> of the Seamoth (played for surface splashes: the documented exception to <c>format audio.bus</c>).</summary>
        Splash,
        Count,
    }

    /// <summary>
 /// The effect sources of S7 (CONTRACT §5.4 "C2 effect events", "Sources (Subnautica, S7EffectLibrary)"), found once
 /// per world from the player's own game: prefabs through <c>CraftData.GetPrefabForTechTypeAsync(TechType, bool)</c>
 /// (Seamoth 2000, Exosuit 2001 → <c>armPrefabs</c> ExosuitDrillArmModule 2116, Welder 750, Crash 2509), their
 /// component fields, <c>PlayerBreathBubbles</c>, <c>VFXSurfaceTypeManager.main</c> and a loaded
 /// <c>VFXSubLeakPoint</c>; FMOD assets by their path (logged). Each source becomes one inactive <b>template</b> clone
 /// under <c>DE.S7.Fx/Templates</c> (the game's prefab is never touched) that passes the <b>safety filter</b>: nothing
 /// but Transform, ParticleSystem(Renderer), Light, renderers and mesh filters, animators, <c>VFX*</c>, <c>FMOD*</c> and
 /// <c>SkyApplier</c> components (no Collider, Rigidbody, LiveMixin, damage or identity component: refused, logged), with
 /// the self-destroy helpers (<c>*Destroy*</c>) stripped so the clones can be pooled. A missing or refused source falls
 /// back to the bubbles (or nothing) with <c>DE.S7 fx SOURCE MISSING &lt;kind&gt; &lt;what&gt;</c> once. Nothing shipped,
 /// nothing written to the game. Main thread.
 /// </summary>
    internal sealed class S7EffectLibrary
    {
        private GameObject _root;
        private Transform _templates;
        public Transform PoolRoot { get; private set; }
        private readonly GameObject[] _template = new GameObject[(int)FxSource.Count];
        private readonly string[] _state = new string[(int)FxSource.Count];
        private readonly string[] _why = new string[(int)FxSource.Count];
        private readonly FMODAsset[] _sound = new FMODAsset[(int)FxSound.Count];
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>S7p-effects-under: the composed looks (donors and templates), built once the sources are found.</summary>
        public readonly S7pRecipes Recipes = new S7pRecipes();
        /// <summary>S7p-effects-under: lights lowered by the S7p caps in the drill, impact and explosion sources.</summary>
        public int LightsCapped { get; private set; }
        /// <summary>S7p-effects-under: gameplay parts removed from the stripped sources (destruction effects).</summary>
        public int Stripped { get; private set; }

        public bool Loading { get; private set; }
        public bool Loaded { get; private set; }
        /// <summary>
 /// "): Subnautica's <c>VFXSplash</c> (a surface
 /// splash model scaled by curves + its particles; Awake = Init + Play, Play detaches them at sea level y = 0) found among
 /// the loaded objects (the lifepod's landing splash is the expected carrier); null = not loaded (the old one-shots stay).
 /// </summary>
        public VFXSplash SplashTemplate { get; private set; }
        public string SplashTemplateName = "none";

        private void FindSplashTemplate()
        {
            SplashTemplate = null;
            try
            {
                foreach (VFXSplash v in Resources.FindObjectsOfTypeAll<VFXSplash>())
                {
                    if (v == null || v.gameObject == null) continue;
                    if (v.name.StartsWith("DE.", StringComparison.Ordinal)) continue;
                    if (v.particlesPrefab == null || v.underPrefab == null) continue;
                    if (SplashTemplate == null || (v.surfacePrefab != null && SplashTemplate.surfacePrefab == null)) SplashTemplate = v;
                }
            }
            catch (Exception e) { DeLog.Warn("DE.S7 fx VFXSplash lookup failed: " + e.GetType().Name + ": " + e.Message); }
            SplashTemplateName = SplashTemplate != null
                ? SplashTemplate.name + " (surface=" + (SplashTemplate.surfacePrefab != null ? SplashTemplate.surfacePrefab.name : "none") + ", particles=" +
                  SplashTemplate.particlesPrefab.name + ", duration " + SplashTemplate.duration.ToString("0.#", CultureInfo.InvariantCulture) + " s)"
                : "none";
            DeLog.Info("DE.S7 fx VFXSplash template = " + SplashTemplateName + (SplashTemplate != null ? "" : " (grid crossings use the construction splash)"));
        }
        public string Error { get; private set; }
        /// <summary>The bubbles' own emission rate (particles/s), the base of the thruster rule; HYPOTHESIS 20 when the source has none.</summary>
        public float BubbleBaseRate { get; private set; }
        public int Refused { get; private set; }

        public GameObject Template(FxSource s) { return _template[(int)s]; }
        public FMODAsset Sound(FxSound s) { return _sound[(int)s]; }
        public bool Has(FxSource s) { return _template[(int)s] != null; }

        /// <summary>found | refused (safety filter) | missing, per source (the s7.fx row's <c>sources</c>).</summary>
        public string State(FxSource s) { return _state[(int)s] ?? "missing"; }

        /// <summary>The first available source of a list (the fallback chain), or null.</summary>
        public GameObject First(FxSource a, FxSource b)
        {
            return _template[(int)a] ?? _template[(int)b];
        }

        /// <summary>Starts the world's search (coroutine on <paramref name="host"/>); a no-op while one runs or after success.</summary>
        public void Begin(MonoBehaviour host)
        {
            if (Loading || Loaded || host == null) return;
            Loading = true;
            Error = null;
            try { host.StartCoroutine(Load()); }
            catch (Exception e)
            {
                Loading = false;
                Error = "StartCoroutine: " + e.GetType().Name + ": " + e.Message;
                DeLog.Error("DE.S7 fx library FAILED " + Error + " (no Subnautica effects)");
            }
        }

        private IEnumerator Load()
        {
            EnsureRoot();
            BubbleBaseRate = 20f;
            // Seamoth: bubbles, engine loop, torpedo explosion and fire sound, surface splash and its sound, collision sounds
            CoroutineTask<GameObject> seamoth = CraftData.GetPrefabForTechTypeAsync(TechType.Seamoth, false);
            yield return seamoth;
            Step("Seamoth", () => FromSeamoth(seamoth.GetResult()));
            CoroutineTask<GameObject> exosuit = CraftData.GetPrefabForTechTypeAsync(TechType.Exosuit, false);
            yield return exosuit;
            Step("Exosuit", () => FromExosuit(exosuit.GetResult()));
            CoroutineTask<GameObject> welder = CraftData.GetPrefabForTechTypeAsync(TechType.Welder, false);
            yield return welder;
            Step("Welder", () => FromWelder(welder.GetResult()));
            CoroutineTask<GameObject> crash = CraftData.GetPrefabForTechTypeAsync(TechType.Crash, false);
            yield return crash;
            Step("Crash", () => FromCrash(crash.GetResult()));
            // S7p-effects-under: the flare (an underwater flame) and the thermoblade (heat) lend their particle looks
            CoroutineTask<GameObject> flare = CraftData.GetPrefabForTechTypeAsync(TechType.Flare, false);
            yield return flare;
            Step("Flare", () => FromTool(flare.GetResult() != null ? flare.GetResult().GetComponent<Flare>() : null, FxSource.FlareFx, "Flare.fxControl"));
            CoroutineTask<GameObject> blade = CraftData.GetPrefabForTechTypeAsync(TechType.HeatBlade, false);
            yield return blade;
            Step("HeatBlade", () => FromTool(blade.GetResult() != null ? blade.GetResult().GetComponent<HeatBlade>() : null, FxSource.HeatBladeFx, "HeatBlade.fxControl"));
            Step("PlayerBreathBubbles", FromBreath);
            Step("VFXSurfaceTypeManager", FromSurfaces);
            Step("VFXSubLeakPoint", FromLeak);
            for (int i = 0; i < (int)FxSource.Count; i++)
                if (_template[i] == null && _state[i] == null) Missing((FxSource)i, _why[i] ?? "not found");
            for (int i = 0; i < (int)FxSound.Count; i++)
                if (_sound[i] == null) Warn("sound." + (FxSound)i, "DE.S7 fx SOURCE MISSING sound " + (FxSound)i + " (that sound off)");
            Step("S7p caps", CapSourceLights);
            Step("S7p recipes", () => Recipes.Build(this, _templates));
            Step("VFXSplash", FindSplashTemplate);
            Loading = false;
            Loaded = true;
            DeLog.Info("DE.S7 fx library " + Describe() + " bubbleRate=" + BubbleBaseRate.ToString("0.##", CultureInfo.InvariantCulture) + "/s refused=" + Refused +
                       " stripped=" + Stripped + " lightsCapped=" + LightsCapped);
        }

        /// <summary>
 /// S7p-effects-under (P6a "light capped"): the lights inside the drill sources (S7's look was "blinding") are capped
 /// at <see cref="S7pEffectRules.DrillLightMaxIntensity"/> / <see cref="S7pEffectRules.DrillLightMaxRange"/>, the impact
 /// and stripped explosion sources at the borrowed-light caps; every source's lights and systems are logged once.
 /// </summary>
        private void CapSourceLights()
        {
            for (int i = 0; i < (int)FxSource.Count; i++)
            {
                GameObject t = _template[i];
                if (t == null) continue;
                var s = (FxSource)i;
                int n = 0;
                if (s == FxSource.DrillRock || s == FxSource.DrillSand || s == FxSource.PrawnDrillFx)
                    n = S7pRecipes.CapLights(t, S7pEffectRules.DrillLightMaxIntensity, S7pEffectRules.DrillLightMaxRange);
                else if (s == FxSource.ImpactMetal || s == FxSource.ImpactRock || s == FxSource.ImpactFallback || s == FxSource.SeamothDestruction || s == FxSource.ExosuitDestruction)
                    n = S7pRecipes.CapLights(t, S7pEffectRules.BorrowedLightMaxIntensity, S7pEffectRules.BorrowedLightMaxRange);
                LightsCapped += n;
                DeLog.Info("DE.S7p fx source " + s + ": " + S7pRecipes.DescribeLights(t) + (n > 0 ? " (" + n + " capped)" : "") + "; systems " + S7pRecipes.DescribeSystems(t));
            }
        }

        /// <summary>S7p-effects-under: the first emitter prefab of a tool's <c>VFXController</c> that passes the safety filter.</summary>
        private void FromTool(Component tool, FxSource s, string what)
        {
            VFXController fx = null;
            if (tool is Flare) fx = ((Flare)tool).fxControl;
            else if (tool is HeatBlade) fx = ((HeatBlade)tool).fxControl;
            if (fx == null || fx.emitters == null) { _why[(int)s] = what + ": no prefab or no VFXController"; return; }
            for (int i = 0; i < fx.emitters.Length; i++)
            {
                VFXController.VFXEmitter e = fx.emitters[i];
                if (e != null && e.fx != null && Take(s, e.fx, what + ".emitters[" + i + "].fx")) return;
            }
            if (_state[(int)s] == null) _why[(int)s] = what + " has no usable emitter prefab";
        }

        private void Step(string what, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                Warn("step." + what, "DE.S7 fx SOURCE MISSING " + what + " " + e.GetType().Name + ": " + e.Message + " (its kinds fall back)");
            }
        }

        private void FromSeamoth(GameObject prefab)
        {
            if (prefab == null) { _why[(int)FxSource.Bubbles] = _why[(int)FxSource.Splash] = _why[(int)FxSource.TorpedoExplosion] = "no Seamoth prefab"; return; }
            var sm = prefab.GetComponent<SeaMoth>();
            if (sm == null) { _why[(int)FxSource.Bubbles] = "no SeaMoth component"; return; }
            if (sm.bubbles != null)
            {
                if (Take(FxSource.Bubbles, sm.bubbles.gameObject, "SeaMoth.bubbles"))
                {
                    var ps = _template[(int)FxSource.Bubbles].GetComponent<ParticleSystem>();
                    if (ps != null && ps.emission.rateOverTimeMultiplier > 0) BubbleBaseRate = ps.emission.rateOverTimeMultiplier;
                }
            }
            else _why[(int)FxSource.Bubbles] = "SeaMoth.bubbles null";
            if (sm.engineSound != null && sm.engineSound.engineRpmSFX != null) Asset(FxSound.EngineLoop, sm.engineSound.engineRpmSFX.asset, "SeaMoth.engineSound.engineRpmSFX");
            Asset(FxSound.Splash, sm.splashSound, "Vehicle.splashSound");
            if (sm.torpedoTypes != null)
            {
                for (int i = 0; i < sm.torpedoTypes.Length; i++)
                {
                    TorpedoType tt = sm.torpedoTypes[i];
                    GameObject tp = tt != null ? tt.prefab : null;
                    var torpedo = tp != null ? tp.GetComponent<SeamothTorpedo>() : null;
                    if (torpedo == null) continue;
                    if (torpedo.explosionPrefab != null && !Has(FxSource.TorpedoExplosion)) Take(FxSource.TorpedoExplosion, torpedo.explosionPrefab, "SeamothTorpedo.explosionPrefab");
                    if (_sound[(int)FxSound.TorpedoFire] == null) Asset(FxSound.TorpedoFire, torpedo.fireSound, "SeamothTorpedo.fireSound");
                }
            }
            var vc = prefab.GetComponentInChildren<VFXConstructing>(true);
            if (vc != null && vc.surfaceSplashFX != null) Take(FxSource.Splash, vc.surfaceSplashFX, "VFXConstructing.surfaceSplashFX");
            else _why[(int)FxSource.Splash] = "no VFXConstructing.surfaceSplashFX on the Seamoth";
            var cs = prefab.GetComponentInChildren<CollisionSound>(true);
            if (cs != null)
            {
                Asset(FxSound.HitMedium, cs.hitSoundMedium, "CollisionSound.hitSoundMedium");
                Asset(FxSound.HitFast, cs.hitSoundFast, "CollisionSound.hitSoundFast");
            }
            // S7p-effects-under: the Seamoth's destruction effect (P4) and its damage smoke and sparks (donors)
            if (sm.destructionEffect != null) TakeStripped(FxSource.SeamothDestruction, sm.destructionEffect, "SeaMoth Vehicle.destructionEffect");
            else _why[(int)FxSource.SeamothDestruction] = "SeaMoth Vehicle.destructionEffect null";
            var dmg = prefab.GetComponentInChildren<VFXSeamothDamages>(true);
            if (dmg != null)
            {
                if (dmg.smokeParticles != null) Take(FxSource.SeamothSmoke, dmg.smokeParticles.gameObject, "VFXSeamothDamages.smokeParticles");
                else _why[(int)FxSource.SeamothSmoke] = "VFXSeamothDamages.smokeParticles null";
                if (dmg.sparksPrefabs != null)
                    for (int i = 0; i < dmg.sparksPrefabs.Length && !Has(FxSource.SeamothSparks); i++)
                        if (dmg.sparksPrefabs[i] != null) Take(FxSource.SeamothSparks, dmg.sparksPrefabs[i], "VFXSeamothDamages.sparksPrefabs[" + i + "]");
            }
            else _why[(int)FxSource.SeamothSmoke] = _why[(int)FxSource.SeamothSparks] = "no VFXSeamothDamages on the Seamoth";
        }

        private void FromExosuit(GameObject prefab)
        {
            var ex = prefab != null ? prefab.GetComponent<Exosuit>() : null;
            if (ex != null)
            {
                // S7p-effects-under: the Prawn's jump jets and landing puff (donors), its destruction effect (P4 fallback)
                if (ex.fxcontrol != null && ex.fxcontrol.emitters != null)
                {
                    if (ex.fxcontrol.emitters.Length > 0 && ex.fxcontrol.emitters[0] != null && ex.fxcontrol.emitters[0].fx != null)
                        Take(FxSource.ExosuitJets, ex.fxcontrol.emitters[0].fx, "Exosuit.fxcontrol.emitters[0].fx");
                    if (ex.fxcontrol.emitters.Length > 2 && ex.fxcontrol.emitters[2] != null && ex.fxcontrol.emitters[2].fx != null)
                        Take(FxSource.ExosuitLand, ex.fxcontrol.emitters[2].fx, "Exosuit.fxcontrol.emitters[2].fx");
                }
                else _why[(int)FxSource.ExosuitJets] = _why[(int)FxSource.ExosuitLand] = "Exosuit.fxcontrol null";
                if (ex.destructionEffect != null) TakeStripped(FxSource.ExosuitDestruction, ex.destructionEffect, "Exosuit Vehicle.destructionEffect");
            }
            if (ex == null || ex.armPrefabs == null) { _why[(int)FxSource.PrawnDrillFx] = "no Exosuit.armPrefabs"; return; }
            for (int i = 0; i < ex.armPrefabs.Length; i++)
            {
                if (ex.armPrefabs[i].techType != TechType.ExosuitDrillArmModule || ex.armPrefabs[i].prefab == null) continue;
                var arm = ex.armPrefabs[i].prefab.GetComponentInChildren<ExosuitDrillArm>(true);
                if (arm == null) break;
                if (arm.drillFXprefab != null) Take(FxSource.PrawnDrillFx, arm.drillFXprefab, "ExosuitDrillArm.drillFXprefab");
                else _why[(int)FxSource.PrawnDrillFx] = "ExosuitDrillArm.drillFXprefab null";
                if (arm.loop != null) Asset(FxSound.DrillLoop, arm.loop.asset, "ExosuitDrillArm.loop");
                if (arm.loopHit != null) Asset(FxSound.DrillLoopHit, arm.loopHit.asset, "ExosuitDrillArm.loopHit");
                return;
            }
            _why[(int)FxSource.PrawnDrillFx] = "no ExosuitDrillArmModule in Exosuit.armPrefabs";
        }

        private void FromWelder(GameObject prefab)
        {
            var w = prefab != null ? prefab.GetComponent<Welder>() : null;
            if (w == null) { _why[(int)FxSource.WelderFx] = "no Welder prefab"; return; }
            if (w.fxControl != null && w.fxControl.emitters != null)
            {
                for (int i = 0; i < w.fxControl.emitters.Length; i++)
                {
                    VFXController.VFXEmitter e = w.fxControl.emitters[i];
                    if (e != null && e.fx != null && Take(FxSource.WelderFx, e.fx, "Welder.fxControl.emitters[" + i + "].fx")) break;
                }
            }
            if (!Has(FxSource.WelderFx) && _state[(int)FxSource.WelderFx] == null) _why[(int)FxSource.WelderFx] = "Welder.fxControl has no emitter prefab";
            if (w.weldSound != null && w.weldSound.startLoopSound != null) Asset(FxSound.WeldLoop, w.weldSound.startLoopSound.asset, "Welder.weldSound.startLoopSound");
        }

        private void FromCrash(GameObject prefab)
        {
            var c = prefab != null ? prefab.GetComponent<Crash>() : null;
            if (c == null || c.detonateParticlePrefab == null) { _why[(int)FxSource.CrashDetonate] = "no Crash.detonateParticlePrefab"; return; }
            Take(FxSource.CrashDetonate, c.detonateParticlePrefab, "Crash.detonateParticlePrefab");
        }

        private void FromBreath()
        {
            var b = Object.FindObjectOfType<PlayerBreathBubbles>();
            if (b == null || b.bubblesPrefab == null) { _why[(int)FxSource.BreathBubbles] = "no PlayerBreathBubbles in the scene"; return; }
            Take(FxSource.BreathBubbles, b.bubblesPrefab.gameObject, "PlayerBreathBubbles.bubblesPrefab");
        }

        private void FromSurfaces()
        {
            VFXSurfaceTypeManager m = VFXSurfaceTypeManager.main;
            if (m == null) { _why[(int)FxSource.ImpactMetal] = "no VFXSurfaceTypeManager.main"; return; }
            Surface(m, FxSource.ImpactMetal, VFXSurfaceTypes.metal, VFXEventTypes.impact);
            Surface(m, FxSource.ImpactRock, VFXSurfaceTypes.rock, VFXEventTypes.impact);
            Surface(m, FxSource.ImpactFallback, VFXSurfaceTypes.fallback, VFXEventTypes.impact);
            Surface(m, FxSource.GrinderMetal, VFXSurfaceTypes.metal, VFXEventTypes.diamondBlade);
            Surface(m, FxSource.DrillRock, VFXSurfaceTypes.rock, VFXEventTypes.exoDrill);
            Surface(m, FxSource.DrillSand, VFXSurfaceTypes.sand, VFXEventTypes.exoDrill);
        }

        private void Surface(VFXSurfaceTypeManager m, FxSource s, VFXSurfaceTypes surface, VFXEventTypes ev)
        {
            GameObject p = m.GetFXprefab(surface, ev);
            if (p == null) { _why[(int)s] = "VFXSurfaceTypeManager has no " + surface + "/" + ev; return; }
            Take(s, p, "VFXSurfaceTypeManager " + surface + "/" + ev);
        }

        private void FromLeak()
        {
            var leak = Object.FindObjectOfType<VFXSubLeakPoint>();
            if (leak == null || leak.leakEffectPrefabs == null || leak.leakEffectPrefabs.Length == 0 || leak.leakEffectPrefabs[0] == null)
            {
                _why[(int)FxSource.Leak] = "no loaded VFXSubLeakPoint (optional: bubbles instead)";
                _state[(int)FxSource.Leak] = "fallback";
                return;
            }
            Take(FxSource.Leak, leak.leakEffectPrefabs[0], "VFXSubLeakPoint.leakEffectPrefabs[0]");
        }

        private void Asset(FxSound s, FMODAsset a, string what)
        {
            if (a == null) { Warn("sound." + s, "DE.S7 fx SOURCE MISSING sound " + s + " " + what + " null (that sound off)"); return; }
            _sound[(int)s] = a;
            DeLog.Info("DE.S7 fx sound " + s + " = " + what + " path=" + (a.path ?? "?") + " id=" + (a.id ?? "?"));
        }

        /// <summary>Clones <paramref name="src"/> into an inactive template under DE.S7.Fx/Templates if it passes the safety filter.</summary>
        private bool Take(FxSource s, GameObject src, string what)
        {
            if (src == null) return false;
            GameObject t = Object.Instantiate(src, _templates, false);
            t.name = "DE.S7.Fx." + s;
            string bad = UnsafeComponent(t);
            if (bad != null)
            {
                Object.Destroy(t);
                Refused++;
                _state[(int)s] = "refused";
                _why[(int)s] = what + " has " + bad;
                Warn("refused." + s, "DE.S7 fx SOURCE REFUSED " + s + " " + what + ": component " + bad + " (effects never touch gameplay; falls back)");
                return false;
            }
            StripSelfDestroy(t);
            t.SetActive(false); // clones start inactive: no OnEnable (playOnAwake) before they are placed
            t.transform.localPosition = Vector3.zero;
            t.transform.localRotation = Quaternion.identity;
            _template[(int)s] = t;
            _state[(int)s] = "found";
            _why[(int)s] = what;
            DeLog.Info("DE.S7 fx source " + s + " = " + what + " (" + t.GetComponentsInChildren<ParticleSystem>(true).Length + " particle systems)");
            return true;
        }

        /// <summary>
 /// S7p-effects-under: a source whose prefab mixes a look with gameplay parts (the vehicles' destruction effects carry
 /// debris with colliders and rigidbodies): cloned inactive (no Awake runs), every unsafe component's GameObject
 /// removed (its own component when it sits on the root), then the safety filter must pass; at least one particle
 /// system must remain. Counted in <see cref="Stripped"/>; refused like <see cref="Take"/> otherwise.
 /// </summary>
        private bool TakeStripped(FxSource s, GameObject src, string what)
        {
            if (src == null) return false;
            GameObject t = Object.Instantiate(src, _templates, false);
            t.name = "DE.S7.Fx." + s;
            int removed = 0;
            for (int pass = 0; pass < 64; pass++)
            {
                Component bad = FirstUnsafe(t);
                if (bad == null) break;
                try
                {
                    if (bad.gameObject == t) Object.DestroyImmediate(bad);
                    else Object.DestroyImmediate(bad.gameObject);
                    removed++;
                }
                catch (Exception) { break; }
            }
            string still = UnsafeComponent(t);
            if (still != null || t.GetComponentsInChildren<ParticleSystem>(true).Length == 0)
            {
                Object.Destroy(t);
                Refused++;
                _state[(int)s] = "refused";
                _why[(int)s] = what + (still != null ? " keeps " + still : " has no particle system left");
                Warn("refused." + s, "DE.S7p fx SOURCE REFUSED " + s + " " + what + ": " + _why[(int)s] + " after stripping (effects never touch gameplay)");
                return false;
            }
            Stripped += removed;
            StripSelfDestroy(t);
            t.SetActive(false);
            t.transform.localPosition = Vector3.zero;
            t.transform.localRotation = Quaternion.identity;
            _template[(int)s] = t;
            _state[(int)s] = "found";
            _why[(int)s] = what + (removed > 0 ? " (" + removed + " gameplay parts stripped)" : "");
            DeLog.Info("DE.S7p fx source " + s + " = " + what + " (" + t.GetComponentsInChildren<ParticleSystem>(true).Length + " particle systems, " + removed + " gameplay parts stripped)");
            return true;
        }

        /// <summary>VFX components with effects beyond drawing.</summary>
        private static readonly HashSet<string> UnsafeVfx = new HashSet<string>(StringComparer.Ordinal)
        {
            "VFXPushBodiesFromCenter", "VFXExplosionPlayerFX", "VFXEarthquake", "VFXFallingChunk", "VFXFallingDebris", "VFXFallingRocks",
            "VFXWaterSpray", "VFXWeatherManager", "VFXConsoleCommands", "VFXSchoolFish", "VFXSchoolFishManager", "VFXSchoolFishRepulsor",
            "VFXPrecursorGunElevator", "VFXPrecursorTeleporter", "VFXPrecursorDisableGunTerminal", "VFXScan", "VFXScanning", "VFXOnAttack",
        };

        /// <summary>The safety filter: the first component that is not drawing-only (null = safe).</summary>
        public static string UnsafeComponent(GameObject go)
        {
            Component c = FirstUnsafe(go);
            return c != null ? c.GetType().FullName : null;
        }

        private static Component FirstUnsafe(GameObject go)
        {
            Component[] cs = go.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < cs.Length; i++)
            {
                Component c = cs[i];
                if (c == null) continue; // a missing script: nothing runs
                if (c is Transform || c is ParticleSystem || c is ParticleSystemRenderer || c is Light || c is Renderer || c is MeshFilter) continue;
                string n = c.GetType().Name;
                if (UnsafeVfx.Contains(n)) return c; // S7p-effects-under
                if (n.StartsWith("VFX", StringComparison.Ordinal) || n.StartsWith("FMOD", StringComparison.Ordinal)) continue;
                if (n == "Animator" || n == "Animation" || n == "LensFlare" || n == "SkyApplier" || n == "LightShadows") continue;
                return c;
            }
            return null;
        }

        /// <summary>Removes the self-destroy helpers from a template (the pool owns the clones' life).</summary>
        private static void StripSelfDestroy(GameObject t)
        {
            Component[] cs = t.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < cs.Length; i++)
            {
                Component c = cs[i];
                if (c == null || c is Transform) continue;
                if (c.GetType().Name.IndexOf("Destroy", StringComparison.Ordinal) >= 0) Object.DestroyImmediate(c);
            }
        }

        private void Missing(FxSource s, string why)
        {
            _state[(int)s] = s == FxSource.Leak ? "fallback" : "missing";
            Warn("missing." + s, "DE.S7 fx SOURCE MISSING " + s + " " + why + " (falls back to the bubbles or nothing)");
        }

        private void Warn(string key, string line)
        {
            if (_warned.Add(key)) DeLog.Warn(line);
        }

        private void EnsureRoot()
        {
            if (_root != null) return;
            _root = new GameObject("DE.S7.Fx");
            var templates = new GameObject("Templates");
            templates.SetActive(false);
            _templates = templates.transform;
            _templates.SetParent(_root.transform, false);
            var pools = new GameObject("Pools");
            PoolRoot = pools.transform;
            PoolRoot.SetParent(_root.transform, false);
        }

        /// <summary>The root for pooled objects (made on demand).</summary>
        public Transform Home { get { EnsureRoot(); return PoolRoot; } }

        /// <summary>S7p-effects-under: a composed look's template (null when its role found no donor).</summary>
        public GameObject Composite(FxComposite c) { return Recipes.Template(c); }

        public string Describe()
        {
            var parts = new List<string>();
            for (int i = 0; i < (int)FxSource.Count; i++) parts.Add((FxSource)i + "=" + State((FxSource)i));
            for (int i = 0; i < (int)FxSound.Count; i++) parts.Add("sound." + (FxSound)i + "=" + (_sound[i] != null ? "found" : "missing"));
            return string.Join(" ", parts.ToArray());
        }

        public void Sources(JsonOut j)
        {
            j.Obj("sources");
            for (int i = 0; i < (int)FxSource.Count; i++) j.Str(((FxSource)i).ToString(), State((FxSource)i));
            for (int i = 0; i < (int)FxSound.Count; i++) j.Str("sound." + (FxSound)i, _sound[i] != null ? "found" : "missing");
            j.EndObj();
        }

        public void SourceDetail(JsonObjectWriter w)
        {
            for (int i = 0; i < (int)FxSource.Count; i++) w.Str("src." + (FxSource)i, State((FxSource)i) + (_why[i] != null ? " (" + _why[i] + ")" : ""));
            for (int i = 0; i < (int)FxSound.Count; i++) w.Str("sound." + (FxSound)i, _sound[i] != null ? "found " + (_sound[i].path ?? "?") : "missing");
        }

        /// <summary>The world went away: templates and pools destroyed (the next world searches again).</summary>
        public void Teardown()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _templates = null;
            PoolRoot = null;
            for (int i = 0; i < _template.Length; i++) { _template[i] = null; _state[i] = null; _why[i] = null; }
            for (int i = 0; i < _sound.Length; i++) _sound[i] = null;
            Recipes.Clear(); // S7p-effects-under: the composite templates went with the root
            Loaded = false;
            Loading = false;
            Refused = 0;
            Stripped = 0;
            LightsCapped = 0;
        }
    }

    /// <summary>
 /// A pool of clones of one template (CONTRACT "Effects come from pools … at most 2 Instantiate per frame"): an idle
 /// clone, else a new one when the frame's instantiation budget allows, else the oldest busy one is recycled.
 /// Clones live under the pool root while idle; a clone destroyed with its parent (a mirror view) is forgotten.
 /// </summary>
    internal sealed class S7FxPool
    {
        internal sealed class Inst
        {
            public GameObject Go;
            public Transform T;
            public ParticleSystem Root;
            /// <summary>S7p-effects-under: the root's renderer (the tracer's stretch is set per shot).</summary>
            public ParticleSystemRenderer R;
            public ParticleSystem[] All;
            public bool Busy, LateTime;
            public float Until;
            public float StartedAt;
            /// </summary>
            public object Owner;
        }

        public readonly string Name;
        private readonly GameObject _template;
        private readonly Transform _home;
        private readonly int _max;
        private readonly List<Inst> _items = new List<Inst>();
        public long Created, Recycled, Exhausted;
        private bool _sustainFullLogged;

        public S7FxPool(string name, GameObject template, Transform home, int max)
        {
            Name = name;
            _template = template;
            _home = home;
            _max = Math.Max(1, max);
        }

        public bool Valid { get { return _template != null; } }
        public int Count { get { return _items.Count; } }
        public int BusyCount { get { int n = 0; for (int i = 0; i < _items.Count; i++) if (_items[i].Busy) n++; return n; } }

        /// <summary>
 /// An instance placed and activated (busy until <paramref name="until"/>; 0 = owned by a sustain until it releases
 /// it), or null (no template, pool exhausted this frame). Review F13: a full pool recycles only its
 /// oldest one-shot; a clone a sustain holds (<c>Until</c> 0) is never lent to another caller (two sustains moving
 /// one emitter, one's DetachFx stopping the other): the excess flames get no clone, counted and logged once.
 /// </summary>
        public Inst Take(float now, float until, ref int instantiateBudget)
        {
            if (_template == null) return null;
            Inst pick = null, oldest = null;
            int held = 0;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                Inst it = _items[i];
                if (it.Go == null) { _items.RemoveAt(i); continue; }
                if (!it.Busy) { pick = it; break; }
                if (it.Until == 0f) { held++; continue; } // sustain-owned: never recycled
                if (oldest == null || it.StartedAt < oldest.StartedAt) oldest = it;
            }
            if (pick == null && _items.Count < _max && instantiateBudget > 0)
            {
                instantiateBudget--;
                pick = Make();
            }
            if (pick == null && oldest != null)
            {
                Recycled++;
                Release(oldest);
                pick = oldest;
            }
            if (pick == null)
            {
                Exhausted++;
                if (held > 0 && !_sustainFullLogged)
                {
                    _sustainFullLogged = true;
                    DeLog.Warn("DE.S7 fx pool " + Name + " full: " + held + " of " + _items.Count + " clones held by sustains; further sustains get no clone (review F13; logged once)");
                }
                return null;
            }
            pick.Busy = true;
            pick.StartedAt = now;
            pick.Until = until;
            return pick;
        }

        /// <summary>Creates idle clones up to <paramref name="count"/> within the budget (warm-up after the library load).</summary>
        public void Warm(int count, ref int instantiateBudget)
        {
            while (_template != null && _items.Count < Math.Min(count, _max) && instantiateBudget > 0)
            {
                instantiateBudget--;
                Make();
            }
        }

        private Inst Make()
        {
            GameObject go = Object.Instantiate(_template, _home, false);
            go.name = _template.name + "." + _items.Count.ToString(CultureInfo.InvariantCulture);
            go.SetActive(false);
            var it = new Inst
            {
                Go = go,
                T = go.transform,
                Root = go.GetComponent<ParticleSystem>(),
                R = go.GetComponent<ParticleSystemRenderer>(),
                All = go.GetComponentsInChildren<ParticleSystem>(true),
                LateTime = go.GetComponentInChildren<VFXLateTimeParticles>(true) != null,
            };
            if (it.Root == null && it.All.Length > 0) it.Root = it.All[0];
            _items.Add(it);
            Created++;
            return it;
        }

        /// <summary>Back to idle: stopped, deactivated, under the pool root.</summary>
        public void Release(Inst it)
        {
            if (it == null) return;
            it.Busy = false;
            it.Owner = null;
            if (it.Go == null) return;
            try
            {
                if (it.Root != null) it.Root.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                it.Go.SetActive(false);
                if (_home != null) it.T.SetParent(_home, false);
            }
            catch (Exception) { }
        }

        /// <summary>Releases the instances whose time ran out.</summary>
        public void Tick(float now)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                Inst it = _items[i];
                if (it.Go == null) { _items.RemoveAt(i); continue; }
                if (it.Busy && it.Until > 0 && now >= it.Until) Release(it);
            }
        }

        public void ReleaseAll()
        {
            for (int i = 0; i < _items.Count; i++) Release(_items[i]);
        }

        public void DestroyAll()
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Go != null) Object.Destroy(_items[i].Go);
            _items.Clear();
        }
    }
}
