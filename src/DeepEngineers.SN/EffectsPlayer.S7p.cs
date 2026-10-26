using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;
using Knob = DeepEngineers.Wire.S7pFxSettings.Knob;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S7p-effects-under (ROADMAP "### S7p Polish pass" P2, P3, P4, P5, P6a; CONTRACT §5.4 "S7p effects-under"): the
 /// players of the polished looks, one per event type, on top of the S7 player (<see cref="S7Effects"/>):
 /// <list type="bullet">
 /// <item>P2 thruster: a flame cone per flame in the thruster's own colour (SE's colour of the row, else the kind's
 /// vanilla colour), SE's own length from strength, radius and FlameLengthScale, its light under water, a heat shimmer
 /// when the player's game has a distortion material; S7's bubbles and engine loop stay.</item>
 /// <item>P3 plume: per frame, every thruster flame whose heat reaches within 4 m of the sea surface (above or below)
 /// raises a white steam plume at that point (density from strength and distance; 3 per grid, 12 in all, the strongest
 /// kept, the rest counted), with Subnautica's own surface splash as spray when dense and boiling bubbles under it when
 /// the flame is below.</item>
 /// <item>P4 explosion: a flash, Subnautica's own explosion (the Seamoth destruction effect from 4 m, else the crashfish
 /// detonation), a fireball, smoke above water or a bubble cloud under it, a shock ring at twice SE's radius, debris
 /// sparks; S7's sound stays.</item>
 /// <item>P5 gun: a 50 ms muzzle flash (glow sprites + light), forward sparks, a fine bubble puff under water; a tracer
 /// flying muzzle → hit at the bullet's speed; a trail of fine bubbles along the shot under water; at the hit the
 /// game's own impact puff, sparks and fine bubbles. The diver's breath bubbles are never used.</item>
 /// <item>P6a drill: no stacked additive effect: one held sediment/dust plume (alpha-blended) per drill while the bit
 /// touches, coloured by what it touches, sparks on metal (a few on rock), a contact light capped at 1.0 / 4 m.</item>
 /// </list>
 /// Every look is scaled live by <c>de.s7p fx &lt;type&gt; &lt;scale&gt;</c> (env <c>DEEPENGINEERS_S7P_FX</c>); 0 = that
 /// S7p look off (the S7 look stays). Above water the S7p looks stay off unless <c>above</c> is on (P1 is scene-above's).
 /// Every per-frame path is capped with a drop counter (the <c>s7p.fx</c> row). Main thread.
 /// </summary>
    internal sealed partial class S7Effects
    {
        public const string S7pCmd = "de.s7p";

        /// <summary>The live scales (Subnautica's side; SE reads the same environment variable for its switches).</summary>
        internal static readonly S7pFxSettings S7p = NewS7pSettings();
        private static string _s7pEnvError;

        private static S7pFxSettings NewS7pSettings()
        {
            var s = new S7pFxSettings();
            try { _s7pEnvError = s.ApplyText(Environment.GetEnvironmentVariable(S7pFxSettings.EnvVar)); }
            catch (Exception e) { _s7pEnvError = e.GetType().Name + ": " + e.Message; }
            return s;
        }

        /// <summary>For scene-above (P1): the S7p looks are drawn above water too while this is on (<c>de.s7p fx above 1</c>).</summary>
        internal static bool AboveWater { get { return S7p.On(Knob.Above); } }

        // caps
        public const int MaxDrillLights = 4;
        /// <summary>Fine bubbles emitted per frame by every S7p look together (the rest counted <c>bubblesCapped</c>).</summary>
        public const int BubblesPerFrame = 600;
        /// <summary>Sparks emitted per frame by every S7p look together.</summary>
        public const int SparksPerFrame = 300;
        public const int MaxPlumeCandidates = 256;

        private S7FxPool _flame, _shimmer, _plume, _muzzle, _tracer, _sparks, _puff, _fireball, _smoke, _ring, _dust, _destruction;
        private readonly List<FxLight> _drillLights = new List<FxLight>();
        private int _bubblesThisFrame, _sparksThisFrame;
        private static readonly Quaternion Up = Quaternion.Euler(-90f, 0f, 0f); // local +Z (a cone's axis) → world up

 /// <summary>A few world-space emitters shared by every one-shot of a look (no pooled clone per shot).</summary>
        private sealed class Shared
        {
            public S7FxPool Pool;
            public int Max;
            public readonly List<S7FxPool.Inst> Items = new List<S7FxPool.Inst>();
        }

        private readonly Shared _sharedPuff = new Shared { Max = 4 }, _sharedSparks = new Shared { Max = 3 }, _sharedFireball = new Shared { Max = 2 },
                                _sharedSmoke = new Shared { Max = 2 }, _sharedRing = new Shared { Max = 2 }, _sharedTracer = new Shared { Max = 4 };

        // plumes (keyed by the thruster sustain)
        private sealed class PlumeState
        {
            public SKey Key;
            public S7FxPool.Inst Fx;
            public float Density, X, Z, NextSpray, NextBoil, NextSplash;
            public bool Live, FromBelow;
        }

        private readonly Dictionary<SKey, PlumeState> _plumes = new Dictionary<SKey, PlumeState>();
        private readonly List<SKey> _plumeGone = new List<SKey>();
        private readonly float[] _pcDensity = new float[MaxPlumeCandidates];
        private readonly ulong[] _pcEntity = new ulong[MaxPlumeCandidates];
        private readonly bool[] _pcTake = new bool[MaxPlumeCandidates];
        private readonly SKey[] _pcKey = new SKey[MaxPlumeCandidates];
        private readonly S7pPlume[] _pcHit = new S7pPlume[MaxPlumeCandidates];
        private readonly float[] _pcSize = new float[MaxPlumeCandidates];

        // counters (cumulative; the s7p.fx row carries them)
        private long _pFlames, _pFlameNone, _pShimmers, _pFlameLights, _pPlumes, _pPlumeDropped, _pPlumeCandCapped, _pPlumeNone, _pSprays, _pBoils,
                     _pMuzzles, _pMuzzleNone, _pTracers, _pTracerNone, _pStreakBubbles, _pBubbles, _pBubblesCapped, _pSparks, _pSparksCapped,
                     _pImpactSparks, _pExplosions, _pExplosionLayers, _pExplosionPrefabs, _pExplosionNone, _pDust, _pDustNone, _pDrillSparks,
                     _pDrillLights, _pDrillLightNone, _pDrillGame, _pSharedNone;

        // ============================================================================================ pools

        private void BuildS7pPools(Transform home)
        {
            _flame = Pool("s7p.flame", _lib.Composite(FxComposite.Flame), home, 96);
            _shimmer = Pool("s7p.shimmer", _lib.Composite(FxComposite.Shimmer), home, 24);
            _plume = Pool("s7p.plume", _lib.Composite(FxComposite.Plume), home, 16);
            _muzzle = Pool("s7p.muzzle", _lib.Composite(FxComposite.Muzzle), home, 16);
            _tracer = Pool("s7p.tracer", _lib.Composite(FxComposite.Tracer), home, _sharedTracer.Max);
            _sparks = Pool("s7p.sparks", _lib.Composite(FxComposite.Sparks), home, _sharedSparks.Max + 16);
            _puff = Pool("s7p.puff", _lib.Composite(FxComposite.Puff), home, _sharedPuff.Max);
            _fireball = Pool("s7p.fireball", _lib.Composite(FxComposite.Fireball), home, _sharedFireball.Max);
            _smoke = Pool("s7p.smoke", _lib.Composite(FxComposite.Smoke), home, _sharedSmoke.Max);
            _ring = Pool("s7p.ring", _lib.Composite(FxComposite.Ring), home, _sharedRing.Max);
            _dust = Pool("s7p.dust", _lib.Composite(FxComposite.Dust), home, 12);
            _destruction = Pool("s7p.destruction", _lib.First(FxSource.SeamothDestruction, FxSource.ExosuitDestruction), home, 4);
            _sharedPuff.Pool = _puff; _sharedSparks.Pool = _sparks; _sharedFireball.Pool = _fireball;
            _sharedSmoke.Pool = _smoke; _sharedRing.Pool = _ring; _sharedTracer.Pool = _tracer;
            for (int i = 0; i < MaxDrillLights; i++) _drillLights.Add(MakeLight("DE.S7p.Fx.DrillLight" + i, home));
            DeLog.Info("DE.S7p fx start: " + S7pFxConventions.Header + " " + DeLog.Hex(S7pFxConventions.Hash) +
                       (S7pFxConventions.Hash == S7pFxConventions.PinnedHash ? " pinned" : " MISMATCH") + ", " + S7p.Describe() +
                       (_s7pEnvError != null ? " (" + S7pFxSettings.EnvVar + ": " + _s7pEnvError + ")" : "") + "; " + _lib.Recipes.Describe());
        }

        private void WarmS7p()
        {
            if (_budget <= 0 || _flame == null) return;
            _flame.Warm(8, ref _budget);
            _puff.Warm(1, ref _budget);
            _sparks.Warm(1, ref _budget);
        }

        private void TeardownS7p()
        {
            foreach (FxLight l in _drillLights) if (l.Go != null) Object.Destroy(l.Go);
            _drillLights.Clear();
            _plumes.Clear();
            _sharedPuff.Items.Clear(); _sharedSparks.Items.Clear(); _sharedFireball.Items.Clear();
            _sharedSmoke.Items.Clear(); _sharedRing.Items.Clear(); _sharedTracer.Items.Clear();
            _flame = _shimmer = _plume = _muzzle = _tracer = _sparks = _puff = _fireball = _smoke = _ring = _dust = _destruction = null;
        }

        /// <summary>A shared world emitter of <paramref name="sh"/> (the one with the fewest particles; a new one within its cap).</summary>
        private S7FxPool.Inst SharedEmitter(Shared sh)
        {
            if (sh.Pool == null || !sh.Pool.Valid) return null;
            S7FxPool.Inst best = null;
            for (int i = sh.Items.Count - 1; i >= 0; i--)
            {
                S7FxPool.Inst it = sh.Items[i];
                if (it == null || it.Go == null || !it.Busy) { sh.Items.RemoveAt(i); continue; }
                if (best == null || (it.Root != null && best.Root != null && it.Root.particleCount < best.Root.particleCount)) best = it;
            }
            if (sh.Items.Count < sh.Max && (best == null || (best.Root != null && best.Root.particleCount > 0)))
            {
                S7FxPool.Inst it = sh.Pool.Take(Now, 0f, ref _budget); // held: never recycled by the pool
                if (it != null)
                {
                    it.Owner = sh;
                    it.T.SetParent(_lib.Home, false);
                    it.T.localPosition = Vector3.zero;
                    it.T.localRotation = Quaternion.identity;
                    it.T.localScale = Vector3.one;
                    it.Go.SetActive(true);
                    if (it.Root != null) { SetRate(it, 0f); it.Root.Play(true); }
                    sh.Items.Add(it);
                    best = it;
                }
            }
            if (best == null) _pSharedNone++;
            return best;
        }

        /// <summary>Stops a held instance emitting; the pool takes it back after <paramref name="linger"/> s (its particles fade).</summary>
        private static void ReleaseHeld(S7FxPool.Inst it, float now, float linger)
        {
            if (it == null || it.Go == null) return;
            try
            {
                if (it.Root != null)
                {
                    SetRate(it, 0f);
                    it.Root.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }
            catch (Exception) { }
            it.Owner = null;
            it.Until = now + Math.Max(0.05f, linger);
        }

        // ============================================================================================ per frame

        private void UpdateS7p(float now)
        {
            _bubblesThisFrame = 0;
            _sparksThisFrame = 0;
            if (_splashClones.Count > 0) ReapSplashClones(now);
            if (_plume == null) return;
            try { UpdatePlumes(now); }
            catch (Exception e) { DeLog.WarnOnce("s7p.plumes", "DE.S7p fx plumes error " + e.GetType().Name + ": " + e.Message + " (logged once)"); }
        }

        // ---- P3 the vapor plume

        private void UpdatePlumes(float now)
        {
            float tune = S7p[Knob.Plume];
            int n = 0;
            if (tune > 0f && _plume.Valid && _sustains.Count > 0)
            {
                foreach (Sustain s in _sustains.Values)
                {
                    if (s.Type != EventType.Thruster || !(s.Intensity > 0f)) continue;
                    if (s.Attached && s.Anchor == null) continue;
                    Vector3 p = WorldOf(s);
                    Vector3 d = s.Attached ? s.Anchor.TransformDirection(s.Dir) : s.Dir;
                    if (d.sqrMagnitude < 1e-8f) continue;
                    d.Normalize();
                    float len = S7pEffectRules.FlameLength(s.Intensity, s.Size, S7pFxCodec.LengthScaleOf(s.Extra), 1f);
                    S7pPlume h = S7pEffectRules.Plume(p.x, p.y, p.z, d.x, d.y, d.z, len, s.Intensity, s.Size, S7pEffectRules.PlumeBandM);
                    if (!h.Hit) continue;
                    if (n >= MaxPlumeCandidates) { _pPlumeCandCapped++; break; }
                    _pcDensity[n] = h.Density;
                    _pcEntity[n] = s.Key.Entity;
                    _pcKey[n] = s.Key;
                    _pcHit[n] = h;
                    _pcSize[n] = s.Size;
                    n++;
                }
            }
            int kept = n > 0 ? S7pEffectRules.SelectPlumes(_pcDensity, _pcEntity, n, S7pEffectRules.PlumePerEntity, S7pEffectRules.PlumeTotal, _pcTake) : 0;
            _pPlumeDropped += n - kept;
            foreach (PlumeState st in _plumes.Values) st.Live = false;
            for (int i = 0; i < n; i++)
            {
                if (!_pcTake[i]) continue;
                PlumeState st;
                if (!_plumes.TryGetValue(_pcKey[i], out st)) { st = new PlumeState { Key = _pcKey[i] }; _plumes[_pcKey[i]] = st; }
                st.Live = true;
                st.Density = _pcHit[i].Density * Math.Min(4f, tune);
                st.X = _pcHit[i].X;
                st.Z = _pcHit[i].Z;
                st.FromBelow = _pcHit[i].FromBelow;
                var at = new Vector3(st.X, 0.05f, st.Z);
                float size = _pcSize[i];
                // right now it's
 // showing some white pixels"): with Subnautica's VFXSplash at hand (the hull-crossing splash, S7pSplash: at most 4 live)
 // the plume is that splash pulsed at the thruster's spot, scaled by the flame, instead of the synthetic steam
                bool splashy = _lib.SplashTemplate != null && !SplashPlumeOff;
                if (splashy)
                {
                    if (now >= st.NextSplash)
                    {
                        if (S7pSplash(new Vector3(st.X, 0f, st.Z), Mathf.Clamp(size * 2f, 0.5f, 12f), Mathf.Clamp01(_pcHit[i].Density), now)) _pSprays++;
                        st.NextSplash = now + S7pEffectRules.PlumeSprayEveryS;
                    }
                    if (st.Fx != null) { ReleaseHeld(st.Fx, now, 0f); st.Fx = null; }
                }
                else
                {
                    if (st.Fx == null || st.Fx.Go == null)
                    {
                        st.Fx = _plume.Take(now, 0f, ref _budget);
                        if (st.Fx == null) { _pPlumeNone++; continue; }
                        st.Fx.Owner = st;
                        st.Fx.T.SetParent(_lib.Home, false);
                        st.Fx.T.position = at;
                        st.Fx.T.rotation = Up;
                        st.Fx.T.localScale = Vector3.one;
                        ActivatePlay(st.Fx);
                        _pPlumes++;
                    }
                    else st.Fx.T.position = at;
                    if (st.Fx.Root != null)
                    {
                        ParticleSystem.MainModule main = st.Fx.Root.main;
                        main.startSize = S7pEffectRules.PlumeSize(size, _pcHit[i].Density);
                        ParticleSystem.ShapeModule shape = st.Fx.Root.shape;
                        shape.radius = Mathf.Clamp(size * 2f, 0.3f, 6f);
                    }
                    SetRate(st.Fx, S7pEffectRules.PlumeRate(_pcHit[i].Density, size, tune));
                    if (_pcHit[i].Density >= S7pEffectRules.PlumeSprayDensity && now >= st.NextSpray && _splash != null && _splash.Valid)
                    {
                        // the surface spray: Subnautica's own surface splash (VFXConstructing.surfaceSplashFX), pulsed
                        if (OneShot(_splash, null, at, Vector3.up, at, Mathf.Clamp(size, 0.5f, 3f), now, 2.5f) != null) _pSprays++;
                        st.NextSpray = now + S7pEffectRules.PlumeSprayEveryS;
                    }
                }
                if (st.FromBelow && now >= st.NextBoil)
                {
                    st.NextBoil = now + 0.15f;
                    // boiling bubbles under the steam (the flame is below the surface)
                    int c = Mathf.Clamp((int)(6f * _pcHit[i].Density * Math.Max(0.5f, size)), 1, 12);
                    if (Bubbles(new Vector3(st.X, -0.6f, st.Z), Mathf.Clamp(size, 0.3f, 3f), c, 0.04f, 0.12f, 0.4f, 0.9f, 1.2f)) _pBoils++;
                }
            }
            if (_plumes.Count == 0) return;
            _plumeGone.Clear();
            foreach (KeyValuePair<SKey, PlumeState> kv in _plumes) if (!kv.Value.Live) _plumeGone.Add(kv.Key);
            for (int i = 0; i < _plumeGone.Count; i++)
            {
                PlumeState st = _plumes[_plumeGone[i]];
                ReleaseHeld(st.Fx, now, 4.5f);
                _plumes.Remove(_plumeGone[i]);
            }
        }

        // ---- P2 the flame

 /// <summary>The thruster's colour: SE's own (the row's <c>extra</c>), else the kind's vanilla colour.</summary>
        private static Color ThrusterColour(Sustain s)
        {
            float r, g, b, ls;
            if (!S7pFxCodec.TryDecodeFlame(s.Extra, out r, out g, out b, out ls)) S7pFxCodec.KindColour(s.Subtype, s.Intensity, out r, out g, out b);
            return new Color(r, g, b, 1f);
        }

        /// <summary>The flame, its light and its shimmer for one thruster sustain (on its rows; <paramref name="under"/> = the row's side).</summary>
        private void S7pFlame(Sustain s, float now, bool row, bool under)
        {
            bool want = (under || S7p.On(Knob.Above)) && S7p.On(Knob.Flame) && _flame != null && _flame.Valid && s.Intensity > 0f;
            if (!want)
            {
                S7pFlameOff(s, now);
                if (under) ReleaseGlow(s); // no S7p flame under water: no flame light either (S7 drew none there)
                return;
            }
            bool fresh = false;
            if (s.Flame == null || s.Flame.Go == null)
            {
                s.Flame = _flame.Take(now, 0f, ref _budget);
                if (s.Flame == null) { _pFlameNone++; return; }
                s.Flame.Owner = s;
                fresh = true;
                _pFlames++;
            }
            if (!fresh && !row) return;
            Place(s.Flame, s.Attached ? s.Anchor : null, s.Pos, s.Dir, WorldOf(s), 1f);
            if (fresh) ActivatePlay(s.Flame);
            float len = S7pEffectRules.FlameLength(s.Intensity, s.Size, S7pFxCodec.LengthScaleOf(s.Extra), S7p[Knob.Flame]);
            float rate = S7pEffectRules.FlameRate(s.Intensity, s.Size, S7p[Knob.Flame]);
            s.FlameLen = len;
            Color c = ThrusterColour(s);
            float gr, gg, gb, ga;
            S7pEffectRules.Glow(c.r, c.g, c.b, S7p[Knob.Glow], out gr, out gg, out gb, out ga);
            if (s.Flame.Root != null)
            {
                ParticleSystem.MainModule main = s.Flame.Root.main;
                main.startSpeed = S7pEffectRules.FlameSpeed(len);
                main.startSize = S7pEffectRules.FlameDiameter(s.Size, 1f);
                main.startColor = new Color(gr, gg, gb, ga);
                ParticleSystem.ShapeModule shape = s.Flame.Root.shape;
                shape.radius = Math.Max(0.02f, s.Size * 0.5f);
            }
            SetRate(s.Flame, rate);
            // the flame's light under water (above water S7's glow light is the flame's light)
            if (under)
            {
                float li, lr;
                S7pEffectRules.FlameLight(s.Intensity, len, S7p[Knob.Light], out li, out lr);
                if (li > 0f && GlowAt(s, s.Pos + s.Dir * (len * 0.3f), c, li, lr)) _pFlameLights++;
                else ReleaseGlow(s);
            }
            // the heat shimmer (only when the player's game lends a distortion material)
            float sr = S7pEffectRules.ShimmerRate(rate, S7p[Knob.Shimmer]);
            if (sr > 0f && _shimmer != null && _shimmer.Valid)
            {
                bool sf = false;
                if (s.Shimmer == null || s.Shimmer.Go == null)
                {
                    s.Shimmer = _shimmer.Take(now, 0f, ref _budget);
                    if (s.Shimmer != null) { s.Shimmer.Owner = s; sf = true; _pShimmers++; }
                }
                if (s.Shimmer != null)
                {
                    Place(s.Shimmer, s.Attached ? s.Anchor : null, s.Pos + s.Dir * (len * 0.25f), s.Dir, WorldOf(s), 1f);
                    if (sf) ActivatePlay(s.Shimmer);
                    if (s.Shimmer.Root != null)
                    {
                        ParticleSystem.MainModule sm = s.Shimmer.Root.main;
                        sm.startSpeed = S7pEffectRules.FlameSpeed(len) * 0.4f;
                        sm.startSize = S7pEffectRules.FlameDiameter(s.Size, 1f) * 1.5f;
                    }
                    SetRate(s.Shimmer, sr);
                }
            }
            else if (s.Shimmer != null) { ReleaseHeld(s.Shimmer, now, 0.4f); s.Shimmer = null; }
        }

        private static void S7pFlameOff(Sustain s, float now)
        {
            if (s.Flame != null) { ReleaseHeld(s.Flame, now, 0.3f); s.Flame = null; }
            if (s.Shimmer != null) { ReleaseHeld(s.Shimmer, now, 0.4f); s.Shimmer = null; }
            s.FlameLen = 0f;
        }

        // ---- P5 the gun

        private string S7pMuzzle(ref S7EventRow r, Transform anchor, Vector3 pos, Vector3 dir, Vector3 wpos, Vector3 wdir, bool under, float now)
        {
            S7pGunRecipe g = S7pEffectRules.Gun(r.size, 0f, 0, under, S7p[Knob.Muzzle], 0f, 0f, S7p[Knob.Light]);
            string look = null;
            if (g.LightIntensity > 0f && Flash(wpos, g.LightIntensity, g.LightRange, new Color(1f, 0.85f, 0.6f), g.LightSeconds)) look = "flash";
            if (g.FlashCount > 0 && _muzzle != null && _muzzle.Valid)
            {
                S7FxPool.Inst it = _muzzle.Take(now, now + 0.15f, ref _budget);
                if (it != null && it.Root != null)
                {
                    Place(it, anchor, anchor != null ? pos : wpos, dir, wpos, 1f);
                    it.Go.SetActive(true);
                    it.Root.Clear(true);
                    it.Root.Play(true);
                    var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
                    for (int k = 0; k < g.FlashCount; k++)
                    {
                        ep.position = new Vector3(0f, 0f, k * g.FlashSize * 0.35f); // local: along the barrel
                        ep.velocity = new Vector3(0f, 0f, 1.5f);
                        ep.startSize = g.FlashSize * (1f - 0.2f * k);
                        ep.startLifetime = g.FlashLife * (1f + 0.2f * k);
                        it.Root.Emit(ep, 1);
                    }
                    look = (look != null ? look + "+" : "") + "muzzle";
                    _pMuzzles++;
                }
                else _pMuzzleNone++;
            }
            if (g.MuzzleSparks > 0 && EmitSparks(wpos, wdir, g.MuzzleSparks, 6f, 12f, 18f, under)) look = (look != null ? look + "+" : "") + "sparks";
            if (g.MuzzleBubbles > 0 && Bubbles(wpos + (wdir.sqrMagnitude > 1e-8f ? wdir.normalized * 0.3f : Vector3.zero), 0.15f, g.MuzzleBubbles, 0.02f, 0.06f, 0.8f, 1.6f, 0.4f))
                look = (look != null ? look + "+" : "") + "bubbles";
            return look;
        }

        private string S7pStreak(ref S7EventRow r, Vector3 wpos, Vector3 wdir, bool under, float now)
        {
            if (wdir.sqrMagnitude < 1e-8f) return null;
            Vector3 d = wdir.normalized;
            // muzzle -> hit: SE's hit distance rides extra (cm; ext s7p fx 1); the bullet's own speed from the S7 size/lifetime
            float length;
            ushort travelMs;
            if (!S7pFxCodec.StreakTravel(r.size, r.lifetimeMs, r.extra, out length, out travelMs)) return null;
            S7pGunRecipe g = S7pEffectRules.Gun(1f, length, travelMs, under, 0f, S7p[Knob.Tracer], S7p[Knob.Streak], 0f);
            string look = null;
            if (g.TracerSpeed > 0f)
            {
                S7FxPool.Inst it = SharedEmitter(_sharedTracer);
                if (it != null && it.Root != null)
                {
                    if (it.R != null) it.R.velocityScale = g.TracerStretch / g.TracerSpeed;
                    var ep = new ParticleSystem.EmitParams
                    {
                        applyShapeToPosition = false,
                        position = wpos,
                        velocity = d * g.TracerSpeed,
                        startLifetime = g.TracerLife,
                        startSize = g.TracerWidth,
                        startColor = new Color32(255, 235, 190, 255),
                    };
                    it.Root.Emit(ep, 1);
                    look = "tracer";
                    _pTracers++;
                }
                else _pTracerNone++;
            }
            if (g.StreakBubbles > 0 && BubblePath(wpos, d, length, g.StreakBubbles, g.StreakBubbleSize, g.StreakBubbleLife, g.StreakRise))
            {
                look = (look != null ? look + "+" : "") + "bubbleStreak";
                _pStreakBubbles += g.StreakBubbles;
            }
            return look;
        }

        private string S7pImpact(ref S7EventRow r, Transform anchor, Vector3 pos, Vector3 dir, Vector3 wpos, Vector3 wdir, bool under, float now)
        {
            ushort surface = S7pFxCodec.Low(r.subtype);
            S7FxPool p = surface == 1 ? _impactMetal : surface == 2 ? _impactRock : _impactFallback;
            string look = OneShot(p, anchor, pos, dir, wpos, 1f, now, 2f) != null ? p.Name : null; // the game's own impact puff
            int sparks, bubbles;
            S7pEffectRules.Impact(surface, under, S7p[Knob.Impact], S7p[Knob.Sparks], out sparks, out bubbles);
            if (sparks > 0 && EmitSparks(wpos, wdir, sparks, 3f, 8f, 60f, under)) { look = (look != null ? look + "+" : "") + "sparks"; _pImpactSparks += sparks; }
            if (bubbles > 0 && Bubbles(wpos + (wdir.sqrMagnitude > 1e-8f ? wdir.normalized * 0.1f : Vector3.zero), 0.2f, bubbles, 0.03f, 0.08f, 1f, 2f, 0.45f))
                look = (look != null ? look + "+" : "") + "bubbles";
            return look;
        }

        // ---- P4 the explosion

        private string S7pExplosion(ref S7EventRow r, Vector3 wpos, bool under, float now, ref bool sound)
        {
            S7pExplosionRecipe e = S7pEffectRules.Explosion(r.size, r.intensity, under, S7p[Knob.Explosion]);
            string look = null;
            float lt = S7p[Knob.Light];
            if (lt > 0f && Flash(wpos, e.FlashIntensity * Math.Min(4f, lt), e.FlashRange, new Color(1f, 0.75f, 0.45f), e.FlashSeconds)) look = "flash";
            S7FxPool.Inst prefab = null;
            // R9: the destruction prefab (with its own FMOD emitters) only under water - above water the
 // particle layers make the blast and SE keeps its own sound (S7P-STATUS 2.16)
            if (under && e.Destruction && _destruction != null && _destruction.Valid)
            {
                float sc = S7Mutants.M13 ? 1f : e.PrefabScale;
                prefab = OneShot(_destruction, null, wpos, Vector3.up, wpos, sc, now, 8f);
                if (prefab != null) { look = (look != null ? look + "+" : "") + _destruction.Name; _traceScale = sc; }
            }
            if (prefab == null && under)
            {
                float scale = S7EventRules.ExplosionScale(Math.Max(0.1f, r.size), S7Mutants.M13);
                prefab = OneShot(_explosion, null, wpos, Vector3.up, wpos, scale, now, 4f);
                if (prefab != null) { look = (look != null ? look + "+" : "") + _explosion.Name; _traceScale = scale; }
            }
            if (prefab != null)
            {
                // the prefab's own emitter plays on enable (S7's HYPOTHESIS, unchanged): reported to the audio rule as started
                sound = true;
                _sounds[EventType.Explosion]++;
                SoundStarted(EventType.Explosion, r.flags);
                _pExplosionPrefabs++;
            }
            int layers = 0;
            float radius = Math.Max(0.5f, r.size);
            if (e.FireballCount > 0 && EmitBall(_sharedFireball, wpos, radius * 0.2f, e.FireballCount, e.FireballSize, e.FireballSpeed, e.FireballLife, 0.3f, new Color32(255, 170, 80, 255))) layers++;
            if (under)
            {
                // the smoke under water is a rising bubble cloud; then the burst of bubbles of S7
                if (e.SmokeCount > 0 && Bubbles(wpos, radius * 0.5f, e.SmokeCount, 0.08f, 0.35f, e.SmokeLife * 0.6f, e.SmokeLife, Math.Min(2f, 0.3f * e.SmokeSpeed + 0.4f))) layers++;
                if (e.BubbleCount > 0 && Bubbles(wpos, radius * 0.25f, e.BubbleCount, 0.03f, 0.12f, 1.5f, 3f, 0.8f)) layers++;
            }
            else if (e.SmokeCount > 0 && EmitBall(_sharedSmoke, wpos, radius * 0.4f, e.SmokeCount, e.SmokeSize, e.SmokeSpeed, e.SmokeLife, 1f, new Color32(64, 59, 56, 217))) layers++;
            if (e.RingCount > 0 && EmitRing(wpos, e, under)) layers++;
            if (e.SparkCount > 0 && EmitSparks(wpos, Vector3.up, e.SparkCount, e.SparkSpeed * 0.5f, e.SparkSpeed, 85f, under)) layers++;
            _pExplosions++;
            _pExplosionLayers += layers;
            if (layers > 0) look = (look != null ? look + "+" : "") + "layers" + layers.ToString(CultureInfo.InvariantCulture);
            if (look == null) _pExplosionNone++;
            return look;
        }

        // ---- P6a the drill

 /// <summary>The drill's dust, sparks and light (Drill and PrawnDrill sustains); <c>drillgame</c> = Subnautica's own effect, held once.</summary>
        private void S7pDrill(Sustain s, float now, bool row, bool under, bool hit)
        {
            if (S7p.On(Knob.DrillGame))
            {
                // Subnautica's own exoDrill (hand drill) / Prawn drill effect, ONE held instance while the bit touches (S7
 // replayed a 1.5 s one-shot every 0.1 s: ~15 stacked additive copies, the "blinding" white-out)
                S7pDrillComposedOff(s, now);
                S7FxPool pool = s.Type == EventType.PrawnDrill && _prawn.Valid ? _prawn : _drill;
                if (hit && pool.Valid)
                {
                    if (s.Fx == null || s.Fx.Go == null) { Attach(s, pool, now); if (s.Fx != null) _pDrillGame++; }
                }
                else if (s.Fx != null) DetachFx(s, now, 1.6f);
                return;
            }
            if (s.Fx != null) DetachFx(s, now, 1.6f); // a drillgame instance from a moment ago
            byte contact = S7pFxCodec.ContactOf(s.Subtype);
            float dust = S7pEffectRules.DustRate(hit, contact, s.Size, S7p[Knob.Drill]);
            float spark = S7pEffectRules.SparkRate(hit, contact, S7p[Knob.Sparks]);
            Vector3 back = -s.Dir; // the plume blows back out of the hole, against the bit's forward
            if (dust > 0f && _dust != null && _dust.Valid)
            {
                bool fresh = false;
                if (s.Dust == null || s.Dust.Go == null)
                {
                    s.Dust = _dust.Take(now, 0f, ref _budget);
                    if (s.Dust == null) _pDustNone++;
                    else { s.Dust.Owner = s; fresh = true; _pDust++; }
                }
                if (s.Dust != null)
                {
                    if (fresh || row) Place(s.Dust, s.Attached ? s.Anchor : null, s.Pos, back, WorldOf(s), 1f);
                    if (fresh) ActivatePlay(s.Dust);
                    if (s.Dust.Root != null)
                    {
                        float cr, cg, cb, ca;
                        S7pEffectRules.DustColour(contact, out cr, out cg, out cb, out ca);
                        ParticleSystem.MainModule main = s.Dust.Root.main;
                        main.startColor = new Color(cr, cg, cb, ca);
                        main.startSize = Mathf.Clamp(0.4f * Math.Max(0.5f, s.Size), 0.2f, 2.5f);
                    }
                    SetRate(s.Dust, dust);
                }
            }
            else if (s.Dust != null) SetRate(s.Dust, 0f);
            if (spark > 0f && _sparks != null && _sparks.Valid)
            {
                bool fresh = false;
                if (s.Sparks == null || s.Sparks.Go == null)
                {
                    s.Sparks = _sparks.Take(now, 0f, ref _budget);
                    if (s.Sparks != null) { s.Sparks.Owner = s; fresh = true; _pDrillSparks++; }
                }
                if (s.Sparks != null)
                {
                    if (fresh || row) Place(s.Sparks, s.Attached ? s.Anchor : null, s.Pos, back, WorldOf(s), 1f);
                    if (fresh) ActivatePlay(s.Sparks);
                    if (s.Sparks.Root != null)
                    {
                        ParticleSystem.MainModule main = s.Sparks.Root.main;
                        main.gravityModifier = under ? 0f : 0.6f;
                    }
                    SetRate(s.Sparks, spark);
                }
            }
            else if (s.Sparks != null) SetRate(s.Sparks, 0f);
            float li, lr;
            S7pEffectRules.DrillLight(hit, contact, S7p[Knob.Light], out li, out lr);
            if (li > 0f)
            {
                if (s.DrillLight == null)
                {
                    for (int i = 0; i < _drillLights.Count; i++)
                        if (!_drillLights[i].Busy && _drillLights[i].Go != null) { s.DrillLight = _drillLights[i]; break; }
                    if (s.DrillLight == null) _pDrillLightNone++;
                    else { s.DrillLight.Busy = true; s.DrillLight.Owner = s; _pDrillLights++; }
                }
                if (s.DrillLight != null)
                {
                    Transform t = s.DrillLight.Go.transform;
                    t.SetParent(s.Attached && s.Anchor != null ? s.Anchor : _lib.Home, false);
                    t.localPosition = s.Attached ? s.Pos + back * 0.3f : WorldOf(s);
                    Light l = s.DrillLight.L;
                    l.color = contact == S7pFxConventions.DrillContact.Metal ? new Color(1f, 0.75f, 0.4f) : new Color(1f, 0.85f, 0.65f);
                    l.intensity = S7pEffectRules.CapIntensity(li, S7pEffectRules.DrillLightMaxIntensity);
                    l.range = S7pEffectRules.CapRange(lr, S7pEffectRules.DrillLightMaxRange);
                    l.enabled = true;
                }
            }
            else ReleaseDrillLight(s);
            if (!hit && under && now >= s.NextPulse)
            {
                // spinning in open water: a fine trickle of bubbles at the bit (never the diver's breath bubbles)
                Bubbles(WorldOf(s), 0.1f, 2, 0.02f, 0.05f, 0.8f, 1.5f, 0.35f);
                s.NextPulse = now + 0.3f;
            }
        }

        private void S7pDrillComposedOff(Sustain s, float now)
        {
            if (s.Dust != null) { ReleaseHeld(s.Dust, now, 3f); s.Dust = null; }
            if (s.Sparks != null) { ReleaseHeld(s.Sparks, now, 0.6f); s.Sparks = null; }
            ReleaseDrillLight(s);
        }

        private static void ReleaseDrillLight(Sustain s)
        {
            if (s.DrillLight == null) return;
            s.DrillLight.Busy = false;
            s.DrillLight.Owner = null;
            if (s.DrillLight.L != null) s.DrillLight.L.enabled = false;
            s.DrillLight = null;
        }

        /// <summary>A sustain ends: every S7p hold goes back (its particles fade first).</summary>
        private void S7pEnd(Sustain s, float now)
        {
            S7pFlameOff(s, now);
            S7pDrillComposedOff(s, now);
        }

        // ============================================================================================ emitters

 /// <summary>Fine bubbles in a sphere (the Seamoth's own bubble look), capped per frame; false when nothing was emitted.</summary>
        private bool Bubbles(Vector3 centre, float radius, int count, float sizeMin, float sizeMax, float lifeMin, float lifeMax, float rise)
        {
            int n = BubbleBudget(count);
            if (n <= 0) return false;
            S7FxPool.Inst it = SharedEmitter(_sharedPuff);
            if (it == null || it.Root == null) return false;
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < n; i++)
            {
                ep.position = centre + Random.insideUnitSphere * radius;
                ep.velocity = new Vector3(Random.Range(-0.15f, 0.15f), rise + Random.Range(0f, 0.25f), Random.Range(-0.15f, 0.15f));
                ep.startSize = Random.Range(sizeMin, sizeMax);
                ep.startLifetime = Random.Range(lifeMin, lifeMax);
                it.Root.Emit(ep, 1);
            }
            _pBubbles += n;
            return true;
        }

        /// <summary>The bubble streak: fine bubbles along the shot's path, rising slowly.</summary>
        private bool BubblePath(Vector3 origin, Vector3 d, float length, int count, float size, float life, float rise)
        {
            int n = BubbleBudget(count);
            if (n <= 0 || !(length > 0f)) return false;
            S7FxPool.Inst it = SharedEmitter(_sharedPuff);
            if (it == null || it.Root == null) return false;
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            for (int i = 0; i < n; i++)
            {
                float t = (i + Random.value) / n * length;
                ep.position = origin + d * t + Random.insideUnitSphere * 0.05f;
                ep.velocity = new Vector3(Random.Range(-0.05f, 0.05f), rise * Random.Range(0.6f, 1.3f), Random.Range(-0.05f, 0.05f));
                ep.startSize = size * Random.Range(0.5f, 1.4f);
                ep.startLifetime = life * Random.Range(0.6f, 1.2f);
                it.Root.Emit(ep, 1);
            }
            _pBubbles += n;
            return true;
        }

        private int BubbleBudget(int count)
        {
            if (count <= 0) return 0;
            int room = BubblesPerFrame - _bubblesThisFrame;
            int n = Math.Min(count, Math.Max(0, room));
            if (n < count) _pBubblesCapped += count - n;
            _bubblesThisFrame += n;
            return n;
        }

        /// <summary>Sparks in a cone around <paramref name="dir"/> (any direction when zero), capped per frame.</summary>
        private bool EmitSparks(Vector3 at, Vector3 dir, int count, float speedMin, float speedMax, float coneDeg, bool under)
        {
            int room = SparksPerFrame - _sparksThisFrame;
            int n = Math.Min(count, Math.Max(0, room));
            if (n < count) _pSparksCapped += count - n;
            if (n <= 0) return false;
            S7FxPool.Inst it = SharedEmitter(_sharedSparks);
            if (it == null || it.Root == null) return false;
            ParticleSystem.MainModule main = it.Root.main;
            main.gravityModifier = under ? 0f : 0.6f;
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            float slow = under ? 0.6f : 1f;
            for (int i = 0; i < n; i++)
            {
                ep.position = at;
                ep.velocity = RandomCone(dir, coneDeg) * Random.Range(speedMin, speedMax) * slow;
                ep.startLifetime = under ? Random.Range(0.15f, 0.35f) : Random.Range(0.3f, 0.6f);
                ep.startSize = Random.Range(0.02f, 0.05f);
                it.Root.Emit(ep, 1);
            }
            _sparksThisFrame += n;
            _pSparks += n;
            return true;
        }

        /// <summary>A ball of particles (fireball, smoke): random points in a sphere, outward/upward speeds, sizes and lives varied.</summary>
 /// <summary>

 /// model scaled by curves + particles, at most 4 live) cloned at the hull's centre, scaled by the hull's width and the
 /// crossing speed; without a template the construction-splash one-shots across the hull's width. A bubble burst just
 /// below either way. True when anything played.
 /// </summary>
        private readonly List<KeyValuePair<GameObject, float>> _splashClones = new List<KeyValuePair<GameObject, float>>();
        public long SplashNative, SplashFallback;
        /// DEEPENGINEERS_S7P_SPLASHPLUME=off restores the synthetic steam.</summary>
        private static readonly bool SplashPlumeOff = ReadSplashPlumeOff();
        private static bool ReadSplashPlumeOff()
        {
            try { string v = Environment.GetEnvironmentVariable("DEEPENGINEERS_S7P_SPLASHPLUME"); return v != null && v.Trim().ToLowerInvariant() == "off"; }
            catch (Exception) { return false; }
        }

        private bool S7pSplash(Vector3 wpos, float width, float intensity, float now)
        {
            float w = Mathf.Clamp(width, 0.5f, 40f);
            float i = Mathf.Clamp01(intensity);
            bool any = false;
            VFXSplash tpl = _lib.SplashTemplate;
            if (tpl != null && _splashClones.Count < 4)
            {
                try
                {
                    bool wasActive = tpl.gameObject.activeSelf;
                    if (wasActive) tpl.gameObject.SetActive(false);          // the clone must not Awake before its scale is set
                    GameObject go = UnityEngine.Object.Instantiate(tpl.gameObject, new Vector3(wpos.x, 0f, wpos.z), Quaternion.identity);
                    if (wasActive) tpl.gameObject.SetActive(true);
                    go.name = "DE.S7.Splash";
                    var v = go.GetComponent<VFXSplash>();
                    v.PlayOnAwake = true;
                    float scale = Mathf.Clamp(0.6f + w * 0.12f + 0.8f * i, 0.6f, 4f);
                    go.transform.localScale = Vector3.one * scale;
                    go.SetActive(true);                                         // Awake: Init + Play (detached at sea level)
                    _splashClones.Add(new KeyValuePair<GameObject, float>(go, now + v.duration + 1f));
                    SplashNative++;
                    any = true;
                }
                catch (Exception e) { DeLog.WarnOnce("s7.splash.native", "DE.S7 fx VFXSplash clone failed " + e.GetType().Name + ": " + e.Message + " (logged once; the construction splash from now on)"); }
            }
            if (!any && _splash != null && _splash.Valid)
            {
                int n = Mathf.Clamp(1 + (int)(w / 4f), 1, 8);
                float scale = Mathf.Clamp(1f + 3f * i + w * 0.05f, 1f, 4f);
                for (int k = 0; k < n; k++)
                {
                    float a = (float)(k * 2.399963); // golden-angle spread over the hull's disc
                    float rr = n == 1 ? 0f : (w * 0.5f) * Mathf.Sqrt((k + 0.5f) / n);
                    var at = new Vector3(wpos.x + Mathf.Cos(a) * rr, 0f, wpos.z + Mathf.Sin(a) * rr);
                    if (OneShot(_splash, null, at, Vector3.up, at, scale, now, 3f) != null) { any = true; SplashFallback++; }
                }
            }
            int bub = Mathf.Clamp((int)(10f + 3f * w * (0.3f + 0.7f * i)), 6, 120);
            if (Bubbles(new Vector3(wpos.x, -0.8f, wpos.z), w * 0.45f, bub, 0.05f, 0.25f, 0.8f, 2.5f, 1.5f)) any = true;
            return any;
        }

        /// <summary>The native splash clones past their duration are destroyed (VFXSplash.OnDestroy clears its detached models).</summary>
        private void ReapSplashClones(float now)
        {
            for (int k = _splashClones.Count - 1; k >= 0; k--)
            {
                if (now < _splashClones[k].Value && _splashClones[k].Key != null) continue;
                try { if (_splashClones[k].Key != null) UnityEngine.Object.Destroy(_splashClones[k].Key); } catch (Exception) { }
                _splashClones.RemoveAt(k);
            }
        }

        private bool EmitBall(Shared sh, Vector3 centre, float radius, int count, float size, float speed, float life, float upBias, Color32 colour)
        {
            S7FxPool.Inst it = SharedEmitter(sh);
            if (it == null || it.Root == null || count <= 0) return false;
            var ep = new ParticleSystem.EmitParams { applyShapeToPosition = false, startColor = colour };
            for (int i = 0; i < count; i++)
            {
                Vector3 dirv = Random.onUnitSphere;
                dirv.y = Mathf.Abs(dirv.y) * upBias + dirv.y * (1f - upBias);
                ep.position = centre + Random.insideUnitSphere * radius;
                ep.velocity = dirv.normalized * speed * Random.Range(0.3f, 1f);
                ep.startSize = size * Random.Range(0.6f, 1.2f);
                ep.startLifetime = life * Random.Range(0.7f, 1f);
                it.Root.Emit(ep, 1);
            }
            return true;
        }

        /// <summary>The shock ring: particles on a horizontal circle flying outward (pale under water, dusty above).</summary>
        private bool EmitRing(Vector3 centre, S7pExplosionRecipe e, bool under)
        {
            S7FxPool.Inst it = SharedEmitter(_sharedRing);
            if (it == null || it.Root == null) return false;
            var ep = new ParticleSystem.EmitParams
            {
                applyShapeToPosition = false,
                startColor = under ? new Color32(205, 225, 235, 166) : new Color32(153, 148, 140, 166),
                startLifetime = e.RingLife,
                startSize = e.RingSize,
            };
            float r0 = Math.Max(0.2f, e.RingRadius * 0.05f);
            for (int i = 0; i < e.RingCount; i++)
            {
                float a = (i + Random.value * 0.5f) / e.RingCount * Mathf.PI * 2f;
                var radial = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                ep.position = centre + radial * r0;
                ep.velocity = radial * e.RingSpeed;
                it.Root.Emit(ep, 1);
            }
            return true;
        }

        private static Vector3 RandomCone(Vector3 dir, float coneDeg)
        {
            if (dir.sqrMagnitude < 1e-8f) return Random.onUnitSphere;
            Quaternion q = Quaternion.LookRotation(dir.normalized);
            Vector2 c = Random.insideUnitCircle * Mathf.Tan(Mathf.Clamp(coneDeg, 0f, 89f) * Mathf.Deg2Rad);
            return (q * new Vector3(c.x, c.y, 1f)).normalized;
        }

        /// <summary>A glow light of the S7 pool at <paramref name="local"/> (attached) with the S7p numbers; false when the pool is empty.</summary>
        private bool GlowAt(Sustain s, Vector3 local, Color colour, float intensity, float range)
        {
            if (s.Glow == null)
            {
                for (int i = 0; i < _glows.Count; i++)
                    if (!_glows[i].Busy && _glows[i].Go != null) { s.Glow = _glows[i]; break; }
                if (s.Glow == null) return false;
                s.Glow.Busy = true;
                s.Glow.Owner = s;
            }
            Transform t = s.Glow.Go.transform;
            t.SetParent(s.Attached && s.Anchor != null ? s.Anchor : _lib.Home, false);
            t.localPosition = s.Attached ? local : WorldOf(s);
            Light l = s.Glow.L;
            l.color = colour;
            l.intensity = intensity;
            l.range = range;
            l.enabled = intensity > 0f;
            return true;
        }

        // ============================================================================================ de.s7p fx

 /// <summary>
 /// <c>de.s7p fx [status | reset | &lt;type&gt; &lt;scale&gt; | &lt;type&gt;=&lt;scale&gt; …]</c> (C11; chat <c>/de sn s7p fx
 /// flame 1.5</c>); JSON forms <c>{fx: "flame 1.5"}</c>, <c>{fx: {flame: 1.5}}</c>, <c>{action: "fx", type, scale}</c>.
 /// Another first word is not answered here (false: the bridge asks the next).
 /// </summary>
        private bool HandleS7p(ControlRequest req, JsonObjectWriter reply)
        {
            bool mine;
            List<string> tokens = S7pTokens(req.Args, out mine);
            if (!mine) return false;
            string error;
            string action = S7p.Command(tokens, out error);
            if (action == "set" || action == "reset")
            {
                DeLog.Info("DE.S7p fx settings " + action + ": " + S7p.Describe());
                RefreshThrusters();
            }
            reply.Bool("ok", error == null).Str("cmd", req.Cmd).Str("verb", "fx").Str("action", action ?? "error");
            if (error != null) reply.Str("error", error);
            reply.Str("line", "DE.S7p fx " + S7p.Describe() + (error != null ? " — " + error : ""));
            S7pStatus(reply);
            return true;
        }

        private static List<string> S7pTokens(object args, out bool mine)
        {
            // the shared verb: only the requests with fx are the effects' (S7pFxSettings.Tokens, Wire-tested)
            return S7pFxSettings.Tokens(args, out mine); // {argv: […]}, a list, or the dictionary forms
        }

        /// <summary>A changed scale reaches the live flames at once (the next row would otherwise carry it).</summary>
        private void RefreshThrusters()
        {
            if (!_poolsBuilt || _disposed) return;
            float now = Now;
            foreach (Sustain s in _sustains.Values)
            {
                try { Visualize(s, now, true); }
                catch (Exception) { }
            }
        }

        // ============================================================================================ rows

 /// <summary>
 /// s7p.fx (1 Hz, beside s7.fx): the scales that differ from their defaults, the composites and donors, and the
 /// cumulative S7p counters (looks drawn, drops by cap: plumeDropped, bubblesCapped, sparksCapped, *None = a pool
 /// without a free clone this frame, sharedNone).
 /// </summary>
        private void S7pRow()
        {
            JsonOut j = S5Rows.Begin("s7p.fx");
            j.Num("snFrame", (long)DeLog.Frame).Num("seTick", Reader.LastHeaderTick).Str("ext", S7pFxConventions.Header)
             .Bool("pinned", S7pFxConventions.Hash == S7pFxConventions.PinnedHash).Str("changed", S7p.Changed()).Bool("above", S7p.On(Knob.Above));
            _lib.Recipes.Rows(j);
            j.Num("flames", _pFlames).Num("flameNone", _pFlameNone).Num("flameLights", _pFlameLights).Num("shimmers", _pShimmers)
             .Num("plumes", _pPlumes).Num("plumesLive", (long)_plumes.Count).Num("plumeDropped", _pPlumeDropped).Num("plumeCandCapped", _pPlumeCandCapped)
             .Num("plumeNone", _pPlumeNone).Num("sprays", _pSprays).Num("boils", _pBoils)
             .Num("muzzles", _pMuzzles).Num("muzzleNone", _pMuzzleNone).Num("tracers", _pTracers).Num("tracerNone", _pTracerNone)
             .Num("streakBubbles", _pStreakBubbles).Num("bubbles", _pBubbles).Num("bubblesCapped", _pBubblesCapped)
             .Num("sparks", _pSparks).Num("sparksCapped", _pSparksCapped).Num("impactSparks", _pImpactSparks)
             .Num("explosions", _pExplosions).Num("explosionPrefabs", _pExplosionPrefabs).Num("explosionLayers", _pExplosionLayers).Num("explosionNone", _pExplosionNone)
             .Num("dust", _pDust).Num("dustNone", _pDustNone).Num("drillSparks", _pDrillSparks).Num("drillLights", _pDrillLights).Num("drillLightNone", _pDrillLightNone)
             .Num("drillGame", _pDrillGame).Num("sharedNone", _pSharedNone).Num("lightsCapped", (long)_lib.LightsCapped).Num("stripped", (long)_lib.Stripped);
            S5Rows.End(j);
        }

        private void S7pStatus(JsonObjectWriter w)
        {
            w.Str("s7p", S7p.Describe()).Str("s7pExt", S7pFxConventions.Header + " " + DeLog.Hex(S7pFxConventions.Hash) +
                                                    (S7pFxConventions.Hash == S7pFxConventions.PinnedHash ? " pinned" : " MISMATCH"));
            if (_s7pEnvError != null) w.Str("s7pEnvError", _s7pEnvError);
            w.Num("s7pFlames", _pFlames).Num("s7pPlumesLive", (long)_plumes.Count).Num("s7pPlumeDropped", _pPlumeDropped)
             .Num("s7pTracers", _pTracers).Num("s7pExplosions", _pExplosions).Num("s7pDust", _pDust)
             .Num("s7pBubblesCapped", _pBubblesCapped).Num("s7pSparksCapped", _pSparksCapped).Num("s7pLightsCapped", (long)_lib.LightsCapped)
             .Str("s7pComposites", _lib.Recipes.Describe());
        }
    }
}
