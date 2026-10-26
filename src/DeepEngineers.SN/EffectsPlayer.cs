using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using DeepEngineers.Wire;
using DeepEngineers.Wire.Native;
using UnityEngine;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;
using Object = UnityEngine.Object;

namespace DeepEngineers.SN
{
    /// <summary>
 /// CONTRACT §5.4 "C2 effect
 /// events" "Reader", "Sources", <c>format audio.bus</c>, audio A6): the event window is copied with every C2 read
 /// (<see cref="Reader"/>, registered on the C2 table reader's <c>Extensions</c>), every seq is applied once, and each
 /// event is drawn and played with Subnautica's own prefabs and FMOD events (<see cref="S7EffectLibrary"/>, pooled,
 /// ≤ 2 instantiations per frame, ≤ 0.5 ms per frame). Attached events are children of the mirrored entity's Entity
 /// transform at <c>Mirror(pos)</c> (a grid, subgrid or the C1 character; a missing entity drops the event, counted);
 /// world events at <c>pos</c>. <c>Underwater</c> rows get the under-water look and sound; rows without it the light
 /// only (muzzle and explosion flashes, a thruster glow per kind) and no sound; splashes (<c>Surface</c>) their surface
 /// look. Sustains (thrusters, ship and hand tools) are kept by (entity, key), updated in place and faded when a Stop row
 /// comes or their 500 ms lifetime lapses; the thruster bubbles emit ∝ intensity × size (<see cref="S7EventRules"/>),
 /// one Seamoth engine loop per entity takes <c>rpm</c> = Σ i·s² / Σ s². Seeded failures read here: S7.M11 (constant
 /// thruster emission), S7.M13 (explosion scale fixed). Keyed by entity id throughout (v3). Main thread.
 /// </summary>
    internal sealed partial class S7Effects : IDisposable
    {
        public const string Cmd = "de.fx";
        /// <summary>Per-frame budget of the event player (CONTRACT: effects ≤ 0.5 ms per frame).</summary>
        public const double BudgetMs = 0.45;
        public const int MaxEventsPerFrame = 256;
        public const int InstantiatePerFrame = 2;
        public const int MaxFlashLights = 8, MaxGlowLights = 8, MaxLoops = 16;
        /// <summary>A world must be up this long before the sources are searched (the prefab database settles).</summary>
        public const float WorldSettleS = 5f;

        /// <summary>The event window reader (pinned role of <c>S7Effects.OnC2</c>: the MirrorWire C2 hook copies through it).</summary>
        public readonly S7EventReader Reader = new S7EventReader();

        private readonly MonoBehaviour _host;
        private readonly MirrorConsumer _mirror;
        private readonly S7EffectLibrary _lib = new S7EffectLibrary();

        // pools
        private S7FxPool _thruster, _burst, _streak, _impactMetal, _impactRock, _impactFallback, _grinder, _drill, _welder, _prawn, _explosion, _splash, _leak;
        private readonly List<S7FxPool> _pools = new List<S7FxPool>();
        private bool _poolsBuilt;
        private int _budget;

        // lights and loops (created at the pool build; no Instantiate)
        private sealed class FxLight
        {
            public GameObject Go;
            public Light L;
            public bool Busy;
            public float Start, Until, Peak;
            public object Owner;
        }

        private sealed class FxLoop
        {
            public GameObject Go;
            public FMOD_CustomLoopingEmitter E;
            public FMODAsset Asset;
            public bool Busy, Playing;
            public object Owner;
        }

        private readonly List<FxLight> _flashes = new List<FxLight>();
        private readonly List<FxLight> _glows = new List<FxLight>();
        private readonly List<FxLoop> _loops = new List<FxLoop>();

        // sustains
        private struct SKey : IEquatable<SKey>
        {
            public readonly ulong Entity, Key;
            public SKey(ulong e, ulong k) { Entity = e; Key = k; }
            public bool Equals(SKey o) { return Entity == o.Entity && Key == o.Key; }
            public override bool Equals(object obj) { return obj is SKey && Equals((SKey)obj); }
            public override int GetHashCode() { return (Entity.GetHashCode() * 397) ^ Key.GetHashCode(); }
        }

        private sealed class Sustain
        {
            public SKey Key;
            public byte Type, Flags;
            public ushort Subtype;
            public float Intensity, PrevIntensity, Size, Rate;
            public bool Attached;
            public Transform Anchor;
            public Vector3 Pos, Dir; // Unity-local of the anchor (attached) or world
            public float Expires, NextPulse, LastRowAt;
            public S7FxPool.Inst Fx;
            public S7FxPool FxPool;
            public FxLoop Sound;
            public FxLight Glow;
            public bool Shown;
            // S7p-effects-under (EffectsPlayer.S7p.cs): the row's extra (the thruster colour), the held looks and the drill light
            public ushort Extra;
            public float FlameLen;
            public S7FxPool.Inst Flame, Shimmer, Dust, Sparks;
            public FxLight DrillLight;
            public S7FxPool.Inst Jet; // S14 hook (point 5): the Prawn jet above the water
        }

        private readonly Dictionary<SKey, Sustain> _sustains = new Dictionary<SKey, Sustain>();
        private readonly Stack<Sustain> _freeSustains = new Stack<Sustain>();
        private readonly List<SKey> _scratch = new List<SKey>();

        // one engine loop per entity (S14: per entity and thruster type, keyed SKey(entity, kind))
        private sealed class Engine
        {
            public ulong Entity;
            public ushort Kind; // S14 hook: the thruster type (S7 ThrusterKind) whose loop this is
            public Transform Anchor;
            public FxLoop Sound;
            public double SumIS2, SumS2;
            public int Live, Under;
            public Vector3 SumPos;
            public float Rpm;
            // S14 hook (point 4b): the envelope keeps a released loop playing at its last place until its level reaches 0
            public readonly S14EngineEnv Env = new S14EngineEnv();
            public Transform LastAnchor;
            public Vector3 LastCentre;
        }

        private readonly Dictionary<SKey, Engine> _engines = new Dictionary<SKey, Engine>(); // S14 hook: keyed by (entity, thruster type)
        private readonly List<SKey> _engineScratch = new List<SKey>();

        // entity resolution (the C1 character by its frame name)
        private readonly Dictionary<ulong, Transform> _charCache = new Dictionary<ulong, Transform>();
        private readonly Dictionary<ulong, float> _charMiss = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> _lastFire = new Dictionary<ulong, float>();

        // counters
        private readonly long[] _applied = new long[S7EventRules.TypeSlots];
        private readonly long[] _spawned = new long[S7EventRules.TypeSlots];
        private readonly long[] _failed = new long[S7EventRules.TypeSlots];
        private readonly long[] _sounds = new long[S7EventRules.TypeSlots];
        private long _missingEntity, _lapsed, _stops, _frames, _overBudget, _eventsTotal;
        // S11 hook: stacked explosions as one escalated effect (Wire S11BlastChain)
        private S11BlastChain _s11Chain;
        private readonly List<S7EventRow> _s11Esc = new List<S7EventRow>();
        private float _traceScale = 1f;
        private double _msLast, _msMax;
        private float _worldSince = -1f;
        private ulong _nextRowMs, _traceUntilMs, _nextTraceRowMs;
        private bool _started, _disposed;
        private readonly Stopwatch _sw = new Stopwatch();

        public S7Effects(MonoBehaviour host, MirrorConsumer mirror)
        {
            if (mirror == null) throw new ArgumentNullException("mirror");
            _host = host;
            _mirror = mirror;
        }

        private static float Now { get { return Time.realtimeSinceStartup; } }

        // ============================================================================================ life cycle

 /// <summary>Registers the reader on the C2 table reader (the copy runs inside its seqlock window from now on).</summary>
        public void Start()
        {
            if (_started) return;
            if (!_mirror.C2.Extensions.Contains(Reader)) _mirror.C2.Extensions.Add(Reader);
            _started = true;
            DeLog.Info("DE.S7 fx start: event window reader on C2 (ext s7 look 1 " + DeLog.Hex(S7Conventions.Hash) +
                       (S7Conventions.Hash == S7Conventions.PinnedHash ? " pinned" : " MISMATCH") + "), sources searched once a world is up");
        }

        /// <summary>A new SE generation: the stream is learnt again, sustains and loops end.</summary>
        public void OnNewSePeer()
        {
            Reader.Reset();
            EndAllSustains("new SE peer");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            PerfApplier.TestFpsCap = 0; // review F10: never outlives the effects player
            try { _mirror.C2.Extensions.Remove(Reader); } catch (Exception) { }
            Teardown("disposed");
        }

        private void Teardown(string why)
        {
            EndAllSustains(why);
            foreach (S7FxPool p in _pools) { try { p.DestroyAll(); } catch (Exception) { } }
            _pools.Clear();
            foreach (FxLight l in _flashes) if (l.Go != null) Object.Destroy(l.Go);
            foreach (FxLight l in _glows) if (l.Go != null) Object.Destroy(l.Go);
            foreach (FxLoop l in _loops) if (l.Go != null) Object.Destroy(l.Go);
            _flashes.Clear(); _glows.Clear(); _loops.Clear();
            TeardownS7p(); // S7p-effects-under
            _engines.Clear();
            _charCache.Clear();
            _poolsBuilt = false;
            _lib.Teardown();
            Reader.Reset();
            DeLog.Info("DE.S7 fx teardown (" + why + ")");
        }

        // ============================================================================================ per frame

 /// <summary>Per frame (Bridge.Update, TickS7fx): world check, sources, events, sustains, engines, rows.</summary>
        public void Tick(ulong nowMs)
        {
            if (!_started || _disposed) return;
            _sw.Reset();
            _sw.Start();
            _frames++;
            float now = Now;
            bool world = Player.main != null;
            if (!world)
            {
                if (_worldSince >= 0) { _worldSince = -1f; if (_lib.Loaded || _poolsBuilt) Teardown("world gone"); }
                DrainWithoutWorld();
            }
            else
            {
                if (_worldSince < 0) _worldSince = now;
                if (!_lib.Loaded && !_lib.Loading && now - _worldSince >= WorldSettleS) _lib.Begin(_host);
                if (_lib.Loaded && !_poolsBuilt) BuildPools();
                _budget = InstantiatePerFrame;
                if (_poolsBuilt)
                {
                    WarmPools();
                    ApplyEvents(now);
                    UpdateSustains(now);
                    UpdateEngines(now);
                    S14Frame(now); // S14 hook: the listener and jets rows
                    UpdateLights(now);
                    UpdateS7p(now); // S7p-effects-under: plumes (P3), per-frame caps
                    for (int i = 0; i < _pools.Count; i++) _pools[i].Tick(now);
                }
                else DrainWithoutWorld();
            }
            _sw.Stop();
            _msLast = _sw.Elapsed.TotalMilliseconds;
            if (_msLast > _msMax) _msMax = _msLast;
            if (_msLast > 0.5) _overBudget++;
            if (nowMs >= _nextRowMs)
            {
                _nextRowMs = nowMs + 1000;
                Row();
            }
            if (nowMs >= _nextTraceRowMs)
            {
                _nextTraceRowMs = nowMs + 100;
                ThrusterRows(); // 10 Hz while thruster sustains live (S7.N4 thruster.rate / rpm)
            }
        }

        /// <summary>No world or no sources yet: the queue is emptied (counted failed) so it never grows stale.</summary>
        private void DrainWithoutWorld()
        {
            S7EventRow r;
            int n = 0;
            while (n < MaxEventsPerFrame * 4 && Reader.TryDequeue(out r))
            {
                n++;
                if (EventType.IsValid(r.type)) _failed[r.type]++;
            }
        }

        // ============================================================================================ pools

        private void BuildPools()
        {
            Transform home = _lib.Home;
            GameObject bubbles = _lib.Template(FxSource.Bubbles);
            GameObject breath = _lib.First(FxSource.BreathBubbles, FxSource.Bubbles);
            _thruster = Pool("thruster", bubbles, home, 128); // review F13: 64 -> 128 (a big ship: 30 thrusters x 4 flames)
            _burst = Pool("burst", breath, home, 24);
            _streak = Pool("streak", breath, home, 16);
            _impactMetal = Pool("impactMetal", _lib.First(FxSource.ImpactMetal, FxSource.ImpactFallback), home, 16);
            _impactRock = Pool("impactRock", _lib.First(FxSource.ImpactRock, FxSource.ImpactFallback), home, 16);
            _impactFallback = Pool("impactFallback", _lib.Template(FxSource.ImpactFallback), home, 8);
            _grinder = Pool("grinder", _lib.First(FxSource.GrinderMetal, FxSource.ImpactMetal), home, 12);
            _drill = Pool("drill", _lib.First(FxSource.DrillRock, FxSource.DrillSand), home, 12);
            _welder = Pool("welder", _lib.Template(FxSource.WelderFx), home, 8);
            _prawn = Pool("prawnDrill", _lib.Template(FxSource.PrawnDrillFx), home, 8);
            _explosion = Pool("explosion", _lib.First(FxSource.CrashDetonate, FxSource.TorpedoExplosion), home, 8);
            _splash = Pool("splash", _lib.Template(FxSource.Splash), home, 8);
            _leak = Pool("leak", _lib.Template(FxSource.Leak), home, 4);
            for (int i = 0; i < MaxFlashLights; i++) _flashes.Add(MakeLight("DE.S7.Fx.Flash" + i, home));
            for (int i = 0; i < MaxGlowLights; i++) _glows.Add(MakeLight("DE.S7.Fx.Glow" + i, home));
            BuildS7pPools(home); // S7p-effects-under
            BuildS14Pools(home); // S14 hook (point 5): the Prawn jets above the water
            _poolsBuilt = true;
            DeLog.Info("DE.S7 fx pools built: " + string.Join(" ", _pools.ConvertAll(p => p.Name + (p.Valid ? "" : "(none)")).ToArray()));
        }

        private S7FxPool Pool(string name, GameObject template, Transform home, int max)
        {
            var p = new S7FxPool(name, template, home, max);
            _pools.Add(p);
            return p;
        }

        private void WarmPools()
        {
            if (_budget <= 0) return;
            _thruster.Warm(8, ref _budget);
            _burst.Warm(4, ref _budget);
            _impactMetal.Warm(2, ref _budget);
            _streak.Warm(2, ref _budget);
            _explosion.Warm(1, ref _budget);
            WarmS7p(); // S7p-effects-under
        }

        private static FxLight MakeLight(string name, Transform home)
        {
            var go = new GameObject(name);
            go.transform.SetParent(home, false);
            Light l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.shadows = LightShadows.None;
            l.enabled = false;
            return new FxLight { Go = go, L = l };
        }

        private FxLoop TakeLoop(FMODAsset asset, Transform parent, Vector3 local, object owner)
        {
            if (asset == null) return null;
            FxLoop pick = null;
            for (int i = _loops.Count - 1; i >= 0; i--)
            {
                if (_loops[i].Go == null) { _loops.RemoveAt(i); continue; }
                if (!_loops[i].Busy) { pick = _loops[i]; break; }
            }
            if (pick == null)
            {
                if (_loops.Count >= MaxLoops) return null;
                var go = new GameObject("DE.S7.Fx.Loop" + _loops.Count.ToString(CultureInfo.InvariantCulture));
                go.transform.SetParent(_lib.Home, false);
                pick = new FxLoop { Go = go, E = go.AddComponent<FMOD_CustomLoopingEmitter>() };
                pick.E.followParent = true;
                _loops.Add(pick);
            }
            pick.Busy = true;
            pick.Owner = owner;
            pick.Go.transform.SetParent(parent != null ? parent : _lib.Home, false);
            pick.Go.transform.localPosition = local;
            if (!ReferenceEquals(pick.Asset, asset))
            {
                pick.E.SetAsset(asset);
                pick.Asset = asset;
            }
            return pick;
        }

        private void ReleaseLoop(FxLoop l)
        {
            if (l == null) return;
            l.Busy = false;
            l.Owner = null;
            if (l.Go == null) return;
            try
            {
                if (l.Playing) l.E.Stop();
                l.Playing = false;
                l.Go.transform.SetParent(_lib.Home, false);
            }
            catch (Exception) { }
        }

        private static void LoopPlay(FxLoop l, bool on)
        {
            if (l == null || l.Go == null) return;
            if (on && !l.Playing) { l.E.Play(); l.Playing = true; }
            else if (!on && l.Playing) { l.E.Stop(); l.Playing = false; }
        }

        // ============================================================================================ events

        private void ApplyEvents(float now)
        {
            if (S11Hooks.ChainClose(_s11Chain, _s11Esc) > 0) // S11 hook: a closed chain plays once with the summed yield
            {
                S11Hooks.Replaying = true;
                try { for (int i = 0; i < _s11Esc.Count; i++) { S7EventRow e = _s11Esc[i]; Apply(ref e, now); } }
                catch (Exception e) { DeLog.WarnOnce("fx.s11", "DE.S7 fx S11 escalated explosion error " + e.GetType().Name + ": " + e.Message + " (logged once)"); }
                finally { S11Hooks.Replaying = false; _s11Esc.Clear(); }
            }
            S7EventRow r;
            int n = 0;
            while (n < MaxEventsPerFrame && Reader.TryDequeue(out r))
            {
                n++;
                _eventsTotal++;
                try { Apply(ref r, now); }
                catch (Exception e)
                {
                    if (EventType.IsValid(r.type)) _failed[r.type]++;
                    DeLog.WarnOnce("fx.apply." + r.type, "DE.S7 fx apply " + EventType.Name(r.type) + " error " + e.GetType().Name + ": " + e.Message + " (logged once)");
                }
                if (_sw.Elapsed.TotalMilliseconds > BudgetMs) break; // the rest next frame (the queue is bounded)
            }
        }

        private void Apply(ref S7EventRow r, float now)
        {
            byte type = r.type;
            if (!EventType.IsValid(type)) return;
            _traceScale = 1f;
            Transform anchor = null;
            Vector3 pos = M(r.pos), dir = M(r.dir);
            Vector3 wpos, wdir;
            if (r.Attached)
            {
                if (!TryEntity(r.entityId, now, out anchor))
                {
                    _missingEntity++;
                    _failed[type]++;
                    Trace(ref r, "dropped", Vector3.zero, null, false);
                    return;
                }
                wpos = anchor.TransformPoint(pos);
                wdir = dir.sqrMagnitude > 1e-8f ? anchor.TransformDirection(dir) : Vector3.zero;
            }
            else
            {
                pos = new Vector3(r.pos.x, r.pos.y, r.pos.z); // SN-world: no mirror
                dir = new Vector3(r.dir.x, r.dir.y, r.dir.z);
                wpos = pos;
                wdir = dir;
            }
            _applied[type]++;
            if (r.IsSustain || type == EventType.Thruster)
            {
                ApplySustain(ref r, anchor, pos, dir, wpos, now);
                return;
            }
            bool under = r.Underwater;
            string look = null;
            bool sound = false;
            switch (type)
            {
                case EventType.WeaponMuzzle:
                    if (S7p.On(S7pFxSettings.Knob.Muzzle) && (under || S7p.On(S7pFxSettings.Knob.Above)))
                    {
                        // S7p-effects-under (P5): a bright 50 ms flash, sparks, fine bubbles (never the diver's breath bubbles)
                        look = S7pMuzzle(ref r, anchor, pos, dir, wpos, wdir, under, now);
                        if (under) sound = FireSound(r.entityId, FxSound.TorpedoFire, wpos, r.intensity, now, type, r.flags);
                        break;
                    }
                    look = Flash(wpos, 4f * (under ? S7EventRules.MuzzleUnderwaterDim : 1f), 8f, new Color(1f, 0.85f, 0.6f), Math.Max(0.06f, r.lifetimeMs / 1000f)) ? "flash" : null;
                    if (under)
                    {
                        if (Burst(_burst, anchor, pos, dir, wpos, 6, now, 1.5f) != null) look = (look ?? "") + "+bubbles";
                        sound = FireSound(r.entityId, FxSound.TorpedoFire, wpos, r.intensity, now, type, r.flags);
                    }
                    break;
                case EventType.BulletStreak:
                    if ((S7p.On(S7pFxSettings.Knob.Tracer) || S7p.On(S7pFxSettings.Knob.Streak)) && (under || S7p.On(S7pFxSettings.Knob.Above)))
                    {
                        look = S7pStreak(ref r, wpos, wdir, under, now); // S7p-effects-under (P5): tracer muzzle -> hit, bubble streak
                        break;
                    }
                    if (under) look = Streak(wpos, wdir, r.size, r.intensity, r.lifetimeMs / 1000f, now) ? "streak" : null;
                    break;
                case EventType.Impact:
                    if (S7p.On(S7pFxSettings.Knob.Impact) && (under || S7p.On(S7pFxSettings.Knob.Above)))
                    {
                        look = S7pImpact(ref r, anchor, pos, dir, wpos, wdir, under, now); // S7p-effects-under (P5)
                        break;
                    }
                    if (under)
                    {
                        S7FxPool p = r.subtype == 1 ? _impactMetal : r.subtype == 2 ? _impactRock : _impactFallback;
                        look = OneShot(p, anchor, pos, dir, wpos, 1f, now, 2f) != null ? p.Name : null;
                        if (Burst(_burst, anchor, pos, dir, wpos, 8, now, 2f) != null) look = (look ?? "") + "+bubbles";
                    }
                    break;
                case EventType.Explosion:
                {
                    if (!S11Hooks.ChainAdmit(ref _s11Chain, ref r, wpos)) { look = "s11-absorbed"; break; } // S11 hook: absorbed into an open chain
                    if (S7p.On(S7pFxSettings.Knob.Explosion))
                    {
                        // S7p-effects-under (P4): the layered explosion (above water the flash only unless the 'above' switch)
                        if (under || S7p.On(S7pFxSettings.Knob.Above)) look = S7pExplosion(ref r, wpos, under, now, ref sound);
                        else
                        {
                            S7pExplosionRecipe fe = S7pEffectRules.Explosion(r.size, r.intensity, false, 0f);
                            look = S7p.On(S7pFxSettings.Knob.Light) && Flash(wpos, fe.FlashIntensity * Math.Min(4f, S7p[S7pFxSettings.Knob.Light]), fe.FlashRange, new Color(1f, 0.75f, 0.45f), fe.FlashSeconds) ? "flash" : null;
                        }
                        break;
                    }
                    float radius = Math.Max(0.1f, r.size);
                    look = Flash(wpos, 8f, Mathf.Clamp(radius * 3f, 5f, 80f), new Color(1f, 0.75f, 0.45f), 0.25f) ? "flash" : null;
                    if (under)
                    {
                        float scale = S7EventRules.ExplosionScale(radius, S7Mutants.M13);
                        S7FxPool.Inst it = OneShot(_explosion, null, wpos, Vector3.up, wpos, scale, now, 4f);
                        _traceScale = scale;
                        if (it != null)
                        {
                            look = (look ?? "") + "+" + _explosion.Name;
                            // the prefab's own emitter plays on enable (HYPOTHESIS): reported to the audio rule as started
                            sound = true;
                            _sounds[type]++;
                            SoundStarted(type, r.flags);
                        }
                        else if (Burst(_burst, null, wpos, Vector3.up, wpos, (int)Mathf.Clamp(20 * scale, 10, 200), now, 3f) != null) look = (look ?? "") + "+bubbles";
                    }
                    break;
                }
                case EventType.GridImpact:
                    if (under)
                    {
                        look = OneShot(_impactMetal, anchor, pos, dir, wpos, 1f, now, 2f) != null ? _impactMetal.Name : null;
                        if (Burst(_burst, anchor, pos, dir, wpos, (int)(6 + 20 * r.intensity), now, 2f) != null) look = (look ?? "") + "+bubbles";
                        sound = OneShotSound(r.intensity >= 0.5f ? FxSound.HitFast : FxSound.HitMedium, wpos, r.intensity, type, r.flags);
                    }
                    break;
                case EventType.HullBreak:
                    if (under)
                    {
                        float life = Math.Max(1f, r.lifetimeMs / 1000f);
                        if (OneShot(_leak, anchor, pos, dir, wpos, 1f, now, life) != null) look = _leak.Name;
                        else
                        {
                            S7FxPool.Inst col = OneShot(_thruster, anchor, pos, dir, wpos, 1f, now, life + 3f);
                            if (col != null && col.Root != null)
                            {
                                SetRate(col, _lib.BubbleBaseRate * 4f);
                                look = "bubbleColumn";
                            }
                        }
                    }
                    break;
                case EventType.Splash:
                    if (r.Has(EventFlags.Surface))
                    {

                        // construction-splash one-shot was invisible against a ship's hull - now several across the hull's width,
 // scaled by the crossing speed, plus a white spray above and a bubble burst below (S7pSplash)
                        look = S7pSplash(wpos, r.size, r.intensity, now) ? _splash.Name + "+spray" : null;
                        // the orchestrator's documented exception to format audio.bus: Vehicle.splashSound
 // plays for a surface splash although its row is not Underwater (while Subnautica's bus is up)
                        sound = OneShotSound(FxSound.Splash, wpos, r.intensity, type, r.flags);
                    }
                    break;
            }
            if (look != null) _spawned[type]++;
            else if (under || type == EventType.Splash) _failed[type]++;
            Trace(ref r, r.Attached ? "attached" : "world", wpos, look, sound);
        }

        // ---- sustains

        private void ApplySustain(ref S7EventRow r, Transform anchor, Vector3 pos, Vector3 dir, Vector3 wpos, float now)
        {
            var key = new SKey(r.entityId, r.key);
            Sustain s;
            bool exists = _sustains.TryGetValue(key, out s);
            if (r.IsStop)
            {
                _stops++;
                if (exists)
                {
                    EndSustain(s, now);
                    SustainRow(s, "stop");
                }
                Trace(ref r, "stop", wpos, null, false);
                return;
            }
            if (!exists)
            {
                s = _freeSustains.Count > 0 ? _freeSustains.Pop() : new Sustain();
                s.Key = key;
                s.Type = r.type;
                s.PrevIntensity = 0f;
                s.Fx = null; s.FxPool = null; s.Sound = null; s.Glow = null; s.NextPulse = 0f; s.Shown = false;
                s.Flame = null; s.Shimmer = null; s.Dust = null; s.Sparks = null; s.DrillLight = null; s.FlameLen = 0f; // S7p-effects-under
                _sustains[key] = s;
            }
            else s.PrevIntensity = s.Intensity;
            s.Flags = r.flags;
            s.Subtype = r.subtype;
            s.Extra = r.extra; // S7p-effects-under: the thruster's flame colour (ext s7p fx 1)
            s.Intensity = S7EventRules.Clamp01(r.intensity);
            s.Size = r.size;
            s.Attached = r.Attached;
            s.Anchor = anchor;
            s.Pos = pos;
            s.Dir = dir;
            s.LastRowAt = now;
            s.Expires = now + Math.Max(0.1f, r.lifetimeMs / 1000f);
            Visualize(s, now, true);
            SustainRow(s, exists ? "refresh" : "start");
            Trace(ref r, exists ? "update" : "start", wpos, s.FxPool != null ? s.FxPool.Name : s.Glow != null ? "glow" : null, s.Sound != null && s.Sound.Playing);
        }

        /// <summary>Creates or updates a sustain's look and sound from its current row (row = a new row just arrived).</summary>
        private void Visualize(Sustain s, float now, bool row)
        {
            bool under = (s.Flags & EventFlags.Underwater) != 0;
            bool hit = (s.Flags & EventFlags.Hit) != 0;
            if (row && s.Fx != null && s.Fx.Go != null) Place(s.Fx, s.Attached ? s.Anchor : null, s.Pos, s.Dir, WorldOf(s), 1f); // a moved sustain (subpart, hand tool)
            switch (s.Type)
            {
                case EventType.Thruster:
                {
                    ushort kind = S7pFxCodec.Low(s.Subtype); // S7p-effects-under: the S7 meaning (bits 8-15 are S7p's on drills only)
                    if (under)
                    {
                        if (!(S7p.On(S7pFxSettings.Knob.Flame) && S7p.On(S7pFxSettings.Knob.Light))) ReleaseGlow(s);
                        if (s.Fx == null || s.Fx.Go == null) Attach(s, _thruster, now);
                        if (s.Fx != null)
                        {
                            s.Rate = S7EventRules.ThrusterEmissionRate(_lib.BubbleBaseRate, s.Intensity, s.Size, S7Mutants.M11);
                            SetRate(s.Fx, s.Rate);
                            if (row && S7EventRules.CavitationEdge(s.PrevIntensity, s.Intensity) && s.Fx.Root != null)
                                s.Fx.Root.Emit(Math.Max(1, (int)(S7EventRules.CavitationBurstPerM * Math.Max(0.5f, s.Size))));
                        }
                    }
                    else
                    {
                        DetachFx(s, now, 0f);
                        s.Rate = 0f;
                        if (kind != S7Conventions.ThrusterKind.Atmospheric) Glow(s, now);
                    }
                    if (under) S14JetOff(s, now); else S14Jet(s, now, row); // S14 hook (point 5): the Prawn jet above the water
                    S7pFlame(s, now, row, under); // S7p-effects-under (P2): the flame cone in the thruster's colour
                    s.Shown = (under && s.Fx != null) || (!under && s.Glow != null) || (!under && kind == S7Conventions.ThrusterKind.Atmospheric);
                    break;
                }
                case EventType.Welder:
                case EventType.Repair:
                    if (under)
                    {
                        if ((s.Fx == null || s.Fx.Go == null) && _welder.Valid) Attach(s, _welder, now);
                        if (s.Fx != null && s.Fx.Root != null && !s.Fx.Root.IsAlive(true) && now >= s.NextPulse) { s.Fx.Root.Play(true); s.NextPulse = now + 0.5f; }
                        if (s.Fx == null && now >= s.NextPulse) { Burst(_burst, s.Attached ? s.Anchor : null, s.Pos, s.Dir, WorldOf(s), 3, now, 1f); s.NextPulse = now + 0.25f; }
                        SustainSound(s, FxSound.WeldLoop);
                    }
                    else { DetachFx(s, now, 0f); StopSound(s); }
                    s.Shown = under && (s.Fx != null || _burst.Valid);
                    break;
                case EventType.Drill when S7p.On(S7pFxSettings.Knob.Drill) || S7p.On(S7pFxSettings.Knob.DrillGame):
                case EventType.PrawnDrill when S7p.On(S7pFxSettings.Knob.Drill) || S7p.On(S7pFxSettings.Knob.DrillGame):
                    // S7p-effects-under (P6a): one held, alpha-blended sediment plume + sparks + a capped light (no stacked white-out)
                    if (under || S7p.On(S7pFxSettings.Knob.Above))
                    {
                        S7pDrill(s, now, row, under, hit);
                        if (under) SustainSound(s, s.Type == EventType.Drill ? FxSound.DrillLoop : hit ? FxSound.DrillLoopHit : FxSound.DrillLoop);
                        else StopSound(s);
                    }
                    else { S7pDrillComposedOff(s, now); DetachFx(s, now, 0f); StopSound(s); }
                    s.Shown = under;
                    break;
                case EventType.Grinder:
                case EventType.Drill:
                    if (under)
                    {
                        S7FxPool p = s.Type == EventType.Grinder ? _grinder : _drill;
                        if (hit && now >= s.NextPulse)
                        {
                            OneShot(p.Valid ? p : _burst, s.Attached ? s.Anchor : null, s.Pos, s.Dir, WorldOf(s), 1f, now, 1.5f);
                            s.NextPulse = now + S7EventRules.GrinderRepeatS;
                        }
                        else if (!hit && now >= s.NextPulse)
                        {
                            // running without touching anything (intensity 0.25): a small bubble trickle at the tool
                            Burst(_burst, s.Attached ? s.Anchor : null, s.Pos, s.Dir, WorldOf(s), 2, now, 1f);
                            s.NextPulse = now + 0.25f;
                        }
                        SustainSound(s, s.Type == EventType.Grinder ? FxSound.WeldLoop : FxSound.DrillLoop);
                    }
                    else StopSound(s);
                    s.Shown = under;
                    break;
                case EventType.PrawnDrill:
                    if (under)
                    {
                        if ((s.Fx == null || s.Fx.Go == null) && _prawn.Valid) Attach(s, _prawn, now);
                        if (s.Fx == null && hit && now >= s.NextPulse) { OneShot(_drill.Valid ? _drill : _burst, s.Attached ? s.Anchor : null, s.Pos, s.Dir, WorldOf(s), 1f, now, 1.5f); s.NextPulse = now + S7EventRules.GrinderRepeatS; }
                        SustainSound(s, hit ? FxSound.DrillLoopHit : FxSound.DrillLoop);
                    }
                    else { DetachFx(s, now, 0f); StopSound(s); }
                    s.Shown = under;
                    break;
                default:
                    s.Shown = false;
                    break;
            }
        }

        private void UpdateSustains(float now)
        {
            if (_sustains.Count == 0) return;
            _scratch.Clear();
            foreach (KeyValuePair<SKey, Sustain> kv in _sustains)
            {
                Sustain s = kv.Value;
                if (now >= s.Expires) { _scratch.Add(kv.Key); continue; }
                if (s.Attached && s.Anchor == null) { _scratch.Add(kv.Key); continue; } // its mirror view went away
                if (s.Type != EventType.Thruster) Visualize(s, now, false); // pulses (grinder, drill, welder re-play)
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                Sustain s;
                if (!_sustains.TryGetValue(_scratch[i], out s)) continue;
                _lapsed++;
                EndSustain(s, now);
                SustainRow(s, "fade"); // its lifetime ran out without a row (a lost Stop, a dropped refresh, S7.M14)
            }
        }

        private void EndSustain(Sustain s, float now)
        {
            DetachFx(s, now, S7EventRules.SustainFadeS);
            StopSound(s);
            ReleaseGlow(s);
            S7pEnd(s, now); // S7p-effects-under
            S14JetOff(s, now); // S14 hook (point 5)
            s.Shown = false;
            _sustains.Remove(s.Key);
            s.Anchor = null;
            _freeSustains.Push(s);
        }

        private void EndAllSustains(string why)
        {
            float now = Now;
            _scratch.Clear();
            foreach (SKey k in _sustains.Keys) _scratch.Add(k);
            for (int i = 0; i < _scratch.Count; i++)
            {
                Sustain s;
                if (_sustains.TryGetValue(_scratch[i], out s)) EndSustain(s, now);
            }
            foreach (Engine e in _engines.Values) { ReleaseLoop(e.Sound); e.Sound = null; }
            _engines.Clear();
        }

        private void Attach(Sustain s, S7FxPool pool, float now)
        {
            S7FxPool.Inst it = pool.Take(now, 0f, ref _budget); // busy until released by the sustain
            if (it == null) return;
            Place(it, s.Attached ? s.Anchor : null, s.Pos, s.Dir, WorldOf(s), 1f);
            it.Owner = s;
            s.Fx = it;
            s.FxPool = pool;
            ActivatePlay(it);
            _spawned[s.Type]++;
        }

        /// <summary>Stops emitting; the pool takes the clone back after the bubbles had time to rise (<paramref name="fade"/> + 3 s).</summary>
        private static void DetachFx(Sustain s, float now, float fade)
        {
            S7FxPool.Inst it = s.Fx;
            s.Fx = null;
            S7FxPool pool = s.FxPool;
            s.FxPool = null;
            if (it == null || it.Go == null) return;
            if (it.Root != null)
            {
                SetRate(it, 0f);
                it.Root.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            it.Owner = null;
            it.Until = now + fade + 3f;
            if (pool == null) it.Busy = false;
        }

        private void SustainSound(Sustain s, FxSound kind)
        {
            FMODAsset a = _lib.Sound(kind);
            if (a == null) { StopSound(s); return; }
            if (s.Sound != null && s.Sound.Go != null && ReferenceEquals(s.Sound.Asset, a)) { LoopPlay(s.Sound, true); return; }
            StopSound(s);
            if (!SoundAllowed(s.Type, s.Flags)) return; // format audio.bus (counted refused by the audio rule)
            s.Sound = TakeLoop(a, s.Attached ? s.Anchor : null, s.Attached ? s.Pos : WorldOf(s), s);
            if (s.Sound != null)
            {
                LoopPlay(s.Sound, true);
                _sounds[s.Type]++;
            }
        }

        private void StopSound(Sustain s)
        {
            if (s.Sound == null) return;
            ReleaseLoop(s.Sound);
            s.Sound = null;
        }

        private void Glow(Sustain s, float now)
        {
            if (s.Glow == null)
            {
                for (int i = 0; i < _glows.Count; i++)
                    if (!_glows[i].Busy && _glows[i].Go != null) { s.Glow = _glows[i]; break; }
                if (s.Glow == null) return;
                s.Glow.Busy = true;
                s.Glow.Owner = s;
            }
            Transform t = s.Glow.Go.transform;
            t.SetParent(s.Attached && s.Anchor != null ? s.Anchor : _lib.Home, false);
            t.localPosition = s.Attached ? s.Pos + s.Dir * Math.Max(0.5f, s.Size) : WorldOf(s);
            Light l = s.Glow.L;
            l.color = ThrusterColour(s); // S7p-effects-under (P2): SE's own flame colour (else the kind's vanilla one)
            l.intensity = 1.5f * s.Intensity;
            l.range = Math.Max(2f, s.Size * 6f);
            l.enabled = s.Intensity > 0f;
        }

        private static void ReleaseGlow(Sustain s)
        {
            if (s.Glow == null) return;
            s.Glow.Busy = false;
            s.Glow.Owner = null;
            if (s.Glow.L != null) s.Glow.L.enabled = false;
            s.Glow = null;
        }

        private static Vector3 WorldOf(Sustain s)
        {
            if (s.Attached && s.Anchor != null) return s.Anchor.TransformPoint(s.Pos);
            return s.Pos;
        }

        // ---- engines (one Seamoth engine loop per entity, rpm = Σ i·s² / Σ s² of its live thruster sustains)

        private void UpdateEngines(float now)
        {
            foreach (Engine e in _engines.Values) { e.SumIS2 = 0; e.SumS2 = 0; e.Live = 0; e.Under = 0; e.SumPos = Vector3.zero; e.Anchor = null; }
            foreach (Sustain s in _sustains.Values)
            {
                if (s.Type != EventType.Thruster || !s.Attached || s.Anchor == null) continue;
                Engine e;
                ushort kind = S7pFxCodec.Low(s.Subtype);
                var ek = new SKey(s.Key.Entity, kind);
                if (!_engines.TryGetValue(ek, out e)) { e = new Engine { Entity = s.Key.Entity, Kind = kind }; _engines[ek] = e; }
                S7EventRules.RpmAdd(ref e.SumIS2, ref e.SumS2, s.Intensity, s.Size);
                e.Live++;
                if ((s.Flags & EventFlags.Underwater) != 0) e.Under++;
                e.SumPos += s.Pos;
                e.Anchor = s.Anchor;
            }
            _engineScratch.Clear();
            S14Sn.SeamothLoop = _lib.Sound(FxSound.EngineLoop);
            // S14 hook (point 4b, CONTRACT §5.4 "S14 conventions" formats engine.mode, thruster.sn): by default no Subnautica loop
 // (SE's own sound per thruster type plays under water); with /de sn s14 engine subnautica hydrogen plays the Cyclops
 // loop, ion the Seamoth loop, atmospheric stays SE's. The envelope (Wire S14EngineEnv) ramps the loop's level and rpm
 // (200 ms up, 300 ms down), keeps a released loop playing at its last place until its level is 0 (corrections a
 // moment apart never restart it) and sets the event volume level x gain(rpm)
            double dtMs = S14EngineDtMs(now);
            bool noEnv = S14Sn.NoEnvelope;
            bool rowDue = S14Sn.EngineRowDue(Time.unscaledTime);
            foreach (Engine e in _engines.Values)
            {
                e.Rpm = S7EventRules.EngineRpm(e.SumIS2, e.SumS2);
                FMODAsset asset = S14Sn.EngineAsset(e.Kind);
                bool want = e.Live > 0 && e.Under > 0 && e.Anchor != null && asset != null;
                if (want) { e.LastAnchor = e.Anchor; e.LastCentre = e.SumPos / e.Live; }
                long attacks = e.Env.Attacks;
                S14EnvEvent ev = e.Env.Step(want, e.Rpm, dtMs, Time.unscaledTime, noEnv);
                bool attackMeasured = e.Env.Attacks != attacks;
                if (!e.Env.Playing || e.LastAnchor == null || asset == null)
                {
                    if (e.Sound != null) { ReleaseLoop(e.Sound); e.Sound = null; }
                    S14Sn.EngineEvent(e.Entity, e.Kind, e.Env, ev, null, e.Rpm, attackMeasured);
                    if (e.Live == 0) _engineScratch.Add(new SKey(e.Entity, e.Kind));
                    continue;
                }
                if (e.Sound == null || e.Sound.Go == null)
                {
                    if (!want) { S14Sn.EngineEvent(e.Entity, e.Kind, e.Env, ev, null, e.Rpm, attackMeasured); continue; } // released before it ever sounded: nothing to fade
                    if (!SoundAllowed(EventType.Thruster, (byte)(EventFlags.Underwater | EventFlags.Sustain | EventFlags.Attached))) continue;
                    e.Sound = TakeLoop(asset, e.LastAnchor, e.LastCentre, e);
                    if (e.Sound == null) continue;
                    _sounds[EventType.Thruster]++;
                }
                else e.Sound.Go.transform.localPosition = e.LastCentre;
                LoopPlay(e.Sound, true);
                S14Sn.EngineApply(e.Sound.E, e.Env, e.Rpm); // the rpm parameter (smoothed) and the event volume
                S14Sn.EngineEvent(e.Entity, e.Kind, e.Env, ev, e.Sound.E, e.Rpm, attackMeasured);
                if (rowDue) S14Sn.EngineRow("periodic", e.Entity, e.Kind, e.Env, e.Sound.E, e.Rpm);
            }
            if (rowDue) S14Sn.EngineRowsWritten(Time.unscaledTime);
            for (int i = 0; i < _engineScratch.Count; i++) _engines.Remove(_engineScratch[i]);
        }

        // ---- one-shots

        private S7FxPool.Inst OneShot(S7FxPool pool, Transform anchor, Vector3 pos, Vector3 dir, Vector3 wpos, float scale, float now, float seconds)
        {
            if (pool == null || !pool.Valid) return null;
            S7FxPool.Inst it = pool.Take(now, now + seconds, ref _budget);
            if (it == null) return null;
            Place(it, anchor, anchor != null ? pos : wpos, dir, wpos, scale);
            ActivatePlay(it);
            return it;
        }

        private S7FxPool.Inst Burst(S7FxPool pool, Transform anchor, Vector3 pos, Vector3 dir, Vector3 wpos, int count, float now, float seconds)
        {
            if (pool == null || !pool.Valid) return null;
            S7FxPool.Inst it = pool.Take(now, now + seconds, ref _budget);
            if (it == null) return null;
            Place(it, anchor, anchor != null ? pos : wpos, dir, wpos, 1f);
            it.Go.SetActive(true);
            if (it.Root != null)
            {
                SetRate(it, 0f);
                it.Root.Play(true);
                it.Root.Emit(Math.Max(1, count));
            }
            return it;
        }

        /// <summary>Bubbles along the bullet's path: EmitParams at world points every 0.75 m (≤ 80), rising slowly.</summary>
        private bool Streak(Vector3 origin, Vector3 dir, float length, float intensity, float lifetime, float now)
        {
            if (!_streak.Valid || dir.sqrMagnitude < 1e-8f) return false;
            S7FxPool.Inst it = _streak.Take(now, now + lifetime + 3f, ref _budget);
            if (it == null || it.Root == null) return false;
            it.T.SetParent(_lib.Home, false);
            it.T.position = origin;
            it.T.rotation = Quaternion.identity;
            it.Go.SetActive(true);
            ParticleSystem.MainModule main = it.Root.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            SetRate(it, 0f);
            it.Root.Play(true);
            Vector3 d = dir.normalized;
            int n = Mathf.Clamp((int)(length / 0.75f), 1, 80);
            float step = length / n;
            var ep = new ParticleSystem.EmitParams();
            ep.applyShapeToPosition = false;
            for (int i = 0; i < n; i++)
            {
                ep.position = origin + d * (step * i);
                ep.velocity = new Vector3(0f, 0.3f + 0.2f * intensity, 0f);
                ep.startLifetime = 1f + lifetime + 0.02f * i;
                it.Root.Emit(ep, 1);
            }
            return true;
        }

        private static void Place(S7FxPool.Inst it, Transform anchor, Vector3 pos, Vector3 dir, Vector3 wpos, float scale)
        {
            if (anchor != null)
            {
                it.T.SetParent(anchor, false);
                it.T.localPosition = pos;
                it.T.localRotation = dir.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(dir) : Quaternion.identity;
            }
            else
            {
                it.T.position = wpos;
                it.T.rotation = dir.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(dir) : Quaternion.identity;
            }
            it.T.localScale = new Vector3(scale, scale, scale);
            if (scale != 1f && it.All != null)
            {
                // a scaled one-shot (explosion ∝ radius, splash): every particle system follows the transform's scale
                for (int i = 0; i < it.All.Length; i++)
                {
                    if (it.All[i] == null) continue;
                    ParticleSystem.MainModule main = it.All[i].main;
                    main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                }
            }
        }

        private static void ActivatePlay(S7FxPool.Inst it)
        {
            it.Go.SetActive(true);
            if (it.Root != null)
            {
                it.Root.Clear(true);
                it.Root.Play(true);
            }
            if (it.LateTime) it.Go.BroadcastMessage("Play", SendMessageOptions.DontRequireReceiver);
        }

        private static void SetRate(S7FxPool.Inst it, float rate)
        {
            if (it == null || it.Root == null) return;
            ParticleSystem.EmissionModule em = it.Root.emission;
            em.enabled = rate > 0f;
            em.rateOverTimeMultiplier = rate;
        }

        private bool Flash(Vector3 wpos, float intensity, float range, Color colour, float seconds)
        {
            float now = Now;
            FxLight pick = null;
            for (int i = 0; i < _flashes.Count; i++)
            {
                FxLight l = _flashes[i];
                if (l.Go == null) continue;
                if (!l.Busy) { pick = l; break; }
                if (pick == null || l.Start < pick.Start) pick = l; // recycle the oldest
            }
            if (pick == null) return false;
            pick.Busy = true;
            pick.Start = now;
            pick.Until = now + seconds;
            pick.Peak = intensity;
            pick.Go.transform.SetParent(_lib.Home, false);
            pick.Go.transform.position = wpos;
            pick.L.color = colour;
            pick.L.range = range;
            pick.L.intensity = intensity;
            pick.L.enabled = true;
            return true;
        }

        private void UpdateLights(float now)
        {
            for (int i = 0; i < _flashes.Count; i++)
            {
                FxLight l = _flashes[i];
                if (!l.Busy || l.L == null) continue;
                if (now >= l.Until) { l.Busy = false; l.L.enabled = false; continue; }
                float k = 1f - (now - l.Start) / Math.Max(0.01f, l.Until - l.Start);
                l.L.intensity = l.Peak * Mathf.Clamp01(k);
            }
        }

        private bool FireSound(ulong entity, FxSound kind, Vector3 wpos, float intensity, float now, byte type, byte flags)
        {
            float last;
            if (_lastFire.TryGetValue(entity, out last) && now - last < 0.06f) return false; // ≤ ~16 shots/s per shooter
            _lastFire[entity] = now;
            if (_lastFire.Count > 256) _lastFire.Clear();
            return OneShotSound(kind, wpos, intensity, type, flags);
        }

        /// <summary>A 3D one-shot at the event, volume ∝ intensity (A6), only when the audio rule allows it (<c>format audio.bus</c>).</summary>
        private bool OneShotSound(FxSound kind, Vector3 wpos, float intensity, byte type, byte flags)
        {
            FMODAsset a = _lib.Sound(kind);
            if (a == null || !SoundAllowed(type, flags)) return false;
            FMODUWE.PlayOneShot(a, wpos, Mathf.Clamp(intensity, 0.2f, 1f));
            _sounds[type]++;
            return true;
        }

        /// <summary>
 /// <c>format audio.bus</c> through the audio-polish rule (<see cref="AudioPolish.OnEventSound"/>: Underwater rows only
 /// while C5 <c>snBusTarget</c> &gt; 0; counted there for the <c>s7.audio</c> row); own rule if that call fails.
 /// </summary>
        private static bool SoundAllowed(byte type, byte flags)
        {
            try { return AudioPolish.OnEventSound(type, flags); }
            catch (Exception) { return (flags & EventFlags.Underwater) != 0; }
        }

        private static void SoundStarted(byte type, byte flags)
        {
            try { AudioPolish.OnEventSoundStarted(type, flags); }
            catch (Exception) { }
        }

        // ============================================================================================ entities

 /// <summary>The Entity transform of a mirrored grid or subgrid (S3/S5 views) or of the C1 character (its mirror frame).</summary>
        private bool TryEntity(ulong id, float now, out Transform t)
        {
            t = null;
            if (id == 0) return false;
            if (_mirror.S7tIsImpostor(id)) { S7tImpostors.EffectsSkipped++; return false; } // S7t-sn-altitude (T3): an impostor has no effects
            MirrorGridView v;
            if (_mirror.ViewMap.TryGetValue(id, out v) && v != null && v.Alive && v.HasPose && v.EntityT != null)
            {
                t = v.EntityT;
                return true;
            }
            CharacterMirror c = _mirror.Character; // the C1 character
            if (c != null && c.CharacterId == id && c.EntityTransform != null) { t = c.EntityTransform; return true; }
            if (_charCache.TryGetValue(id, out t) && t != null) return true;
            float miss;
            if (_charMiss.TryGetValue(id, out miss) && now - miss < 1f) { t = null; return false; }
            GameObject go = GameObject.Find("DE.Mirror.Character." + id.ToString("x", CultureInfo.InvariantCulture));
            Transform e = go != null ? go.transform.Find("Entity") : null;
            if (e == null)
            {
                _charMiss[id] = now;
                if (_charMiss.Count > 256) _charMiss.Clear();
                t = null;
                return false;
            }
            _charCache[id] = e;
            t = e;
            return true;
        }

        private static Vector3 M(F32x3 v) { return new Vector3(v.x, v.y, -v.z); }

        // ============================================================================================ rows

 /// <summary>
 /// The s7.fx row (1 Hz): per-type applied/spawned/failed and the missed events since the previous row (the
 /// analyzer sums rows over a window), soundsStarted per type since the previous row, their totals, the reader's
 /// counters, the sources and the pools.
 /// </summary>
        private void Row()
        {
            JsonOut j = S5Rows.Begin("s7.fx");
            j.Num("snFrame", (long)DeLog.Frame).Num("seTick", Reader.LastHeaderTick);
            Delta(j, "applied", _applied, _prevApplied);
            Delta(j, "spawned", _spawned, _prevSpawned);
            Delta(j, "failed", _failed, _prevFailed);
            Delta(j, "soundsStarted", _sounds, _prevSounds);
            PerType(j, "appliedTotal", _applied);
            PerType(j, "failedTotal", _failed);
            long missed = Reader.Missed;
            j.Num("missed", missed - _prevMissed).Num("missedTotal", missed);
            _prevMissed = missed;
            j.Num("queued", Reader.Queued).Num("pending", (long)Reader.PendingCount).Num("overflow", Reader.Overflow)
             .Num("streamResets", Reader.StreamResets).Num("staleExt", Reader.StaleExtension).Num("noExt", Reader.NoExtension).Num("badRows", Reader.BadRows)
             .Num("stream", (long)Reader.CurrentStream).Num("lastSeq", (long)Reader.LastSeq)
             .Num("sustains", (long)_sustains.Count).Num("engines", (long)_engines.Count)
             .Num("missingEntity", _missingEntity).Num("lapsed", _lapsed).Num("stops", _stops).Num("events", _eventsTotal)
             .Num("ms", _msLast).Num("msMax", _msMax).Num("overBudgetFrames", _overBudget)
             .Bool("library", _lib.Loaded).Bool("pools", _poolsBuilt).Str("mutants", (S7Mutants.M11 ? "S7.M11 " : "") + (S7Mutants.M13 ? "S7.M13" : ""));
            _lib.Sources(j);
            j.Obj("pools");
            foreach (S7FxPool p in _pools) j.Str(p.Name, p.Valid ? p.BusyCount + "/" + p.Count + " created " + p.Created + " recycled " + p.Recycled + " exhausted " + p.Exhausted : "none");
            j.EndObj();
            S5Rows.End(j);
            _msMax = 0;
            if (_poolsBuilt) S7pRow(); // S7p-effects-under
        }

        private readonly long[] _prevApplied = new long[S7EventRules.TypeSlots], _prevSpawned = new long[S7EventRules.TypeSlots],
                                _prevFailed = new long[S7EventRules.TypeSlots], _prevSounds = new long[S7EventRules.TypeSlots];
        private long _prevMissed;
        private ulong _eventRowSecondMs;
        private int _eventRowsThisSecond;
        /// <summary>s7.fx.event rows per second at most (log volume; the stress burst is 128 in one tick).</summary>
        public const int EventRowsPerSecond = 256;

        private static void Delta(JsonOut j, string name, long[] now, long[] prev)
        {
            j.Obj(name);
            for (int t = 1; t < now.Length; t++)
            {
                long d = now[t] - prev[t];
                prev[t] = now[t];
                if (d != 0) j.Num(EventType.Name(t), d);
            }
            j.EndObj();
        }

        private static void PerType(JsonOut j, string name, long[] c)
        {
            j.Obj(name);
            for (int t = 1; t < c.Length; t++) if (c[t] != 0) j.Num(EventType.Name(t), c[t]);
            j.EndObj();
        }

        /// <summary>
 /// s7.fx.event (every applied one-shot, at most 256 per second; sustain rows too while tracing): snFrame, seTick
 /// (the C2 tick that carried it), seq, stream, type, flags, entity, key, how (attached | world | dropped | start |
 /// update | stop), posSn (the Unity world point drawn = SN-world), latencyTicks / latencyFrames (SE ticks from the
 /// raise to this apply; one SE tick ≈ one frame at 60 Hz), scale, lifetimeMs, source (the look), sound.
 /// </summary>
        private void Trace(ref S7EventRow r, string how, Vector3 w, string look, bool sound)
        {
            bool tracing = Win32.TickCount64() < _traceUntilMs;
            bool sustain = r.IsSustain || r.type == EventType.Thruster;
            if (sustain && !tracing) return;
            ulong nowMs = Win32.TickCount64();
            if (nowMs >= _eventRowSecondMs + 1000) { _eventRowSecondMs = nowMs; _eventRowsThisSecond = 0; }
            if (_eventRowsThisSecond >= EventRowsPerSecond) return;
            _eventRowsThisSecond++;
            ulong c2 = Reader.LastHeaderTick;
            long lat = (long)(uint)((uint)c2 - r.tickLow);
            if (lat > 1000000) lat = -1;
            JsonOut j = S5Rows.Begin("s7.fx.event");
            j.Num("snFrame", (long)DeLog.Frame).Num("seTick", c2).Num("seq", (long)r.seq).Num("stream", (long)Reader.CurrentStream)
             .Str("type", EventType.Name(r.type)).Num("flags", (long)r.flags)
             .Str("entity", r.entityId.ToString(CultureInfo.InvariantCulture)).Str("key", "0x" + r.key.ToString("x16", CultureInfo.InvariantCulture))
             .Str("how", how).Vec("posSn", w.x, w.y, w.z).Num("intensity", (double)r.intensity).Num("size", (double)r.size).Num("subtype", (long)r.subtype)
             .Num("lifetimeMs", (long)r.lifetimeMs).Num("tickLow", (long)r.tickLow).Num("latencyTicks", lat).Num("latencyFrames", lat)
             .Num("scale", (double)_traceScale).Str("source", look).Bool("sound", sound).Bool("underwater", r.Underwater);
            S5Rows.End(j);
        }

        /// <summary>s7.fx.sustain: a sustain's state change (start | refresh | stop | fade), tools always, thrusters while tracing.</summary>
        private void SustainRow(Sustain s, string state)
        {
            if (s.Type == EventType.Thruster && Win32.TickCount64() >= _traceUntilMs) return;
            JsonOut j = S5Rows.Begin("s7.fx.sustain");
            j.Num("snFrame", (long)DeLog.Frame).Num("seTick", Reader.LastHeaderTick).Str("type", EventType.Name(s.Type))
             .Str("key", "0x" + s.Key.Key.ToString("x16", CultureInfo.InvariantCulture)).Str("entity", s.Key.Entity.ToString(CultureInfo.InvariantCulture))
             .Str("state", state).Bool("shown", s.Shown).Num("intensity", (double)s.Intensity).Bool("hit", (s.Flags & EventFlags.Hit) != 0)
             .Bool("underwater", (s.Flags & EventFlags.Underwater) != 0).Bool("sound", s.Sound != null && s.Sound.Playing);
            S5Rows.End(j);
        }

        /// <summary>
 /// s7.fx.thruster (10 Hz while thruster sustains live): per flame its intensity and size, the bubble emission set
 /// (particles/s; 0 above water), the engine loop's rpm as set on its FMOD emitter (0 while it does not play) and the
 /// rule's rpm for that entity (S7.N4 thruster.rate and thruster.rpm).
 /// </summary>
        private void ThrusterRows()
        {
            foreach (Sustain s in _sustains.Values)
            {
                if (s.Type != EventType.Thruster) continue;
                Engine e;
                _engines.TryGetValue(new SKey(s.Key.Entity, S7pFxCodec.Low(s.Subtype)), out e); // S14 hook: the loop of this flame's type
                bool playing = e != null && e.Sound != null && e.Sound.Playing;
                if (!S10Sn.TakeRow(S10Sn.RowThruster)) continue; // S10 hook (round 2 D): the row under a per-second budget (the collector)
                JsonOut j = S5Rows.Begin("s7.fx.thruster");
                j.Num("snFrame", (long)DeLog.Frame).Num("seTick", Reader.LastHeaderTick).Str("entity", s.Key.Entity.ToString(CultureInfo.InvariantCulture))
                 .Str("key", "0x" + s.Key.Key.ToString("x16", CultureInfo.InvariantCulture)).Num("intensity", (double)s.Intensity).Num("size", (double)s.Size)
                 .Num("emission", (double)(s.Fx != null ? s.Rate : 0f)).Num("rpm", playing ? (double)e.Rpm : 0.0).Num("rpmRule", e != null ? (double)e.Rpm : 0.0)
                 .Bool("enginePlaying", playing).Bool("constant", S7Mutants.M11).Bool("underwater", (s.Flags & EventFlags.Underwater) != 0)
                 .Num("kind", (long)S7pFxCodec.Low(s.Subtype)).Bool("shown", s.Shown)
                 // S7p-effects-under (P2): the flame drawn (length, SE's colour on the wire or 0, the length scale)
                 .Bool("flame", s.Flame != null && s.Flame.Go != null).Num("flameLen", (double)s.FlameLen)
                 .Str("colour", "0x" + s.Extra.ToString("x4", CultureInfo.InvariantCulture)).Num("lengthScale", (double)S7pFxCodec.LengthScaleOf(s.Extra));
                S5Rows.End(j);
            }
        }

        // ============================================================================================ de.fx

 /// <summary><c>de.fx {action: status | sources | trace, seconds}</c> (Subnautica side; CONTRACT §5.4 "S7 control commands").</summary>
        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req != null && req.Cmd == S7pCmd) return HandleS7p(req, reply); // S7p-effects-under: de.s7p fx … (other verbs: false)
            if (S14Sn.Handle(req, reply, now)) return true; // S14 hook: de.s14 {status | listener | envelope | jets | jetScale}
            if (req == null || req.Cmd != Cmd) return false;
            Dictionary<string, object> args = S3Args.ToDict(req.Args);
            string action = S3Args.Str(args, "action", "status");
            string err = null;
            switch (action)
            {
                case "status":
                    break;
                case "sources":
                    _lib.SourceDetail(reply);
                    _lib.Recipes.Detail(reply); // S7p-effects-under: donors and composites
                    break;
                case "trace":
                {
                    object v;
                    double s = 10;
                    if (args.TryGetValue("seconds", out v) && v is double) s = (double)v;
                    s = Math.Max(0, Math.Min(600, s));
                    _traceUntilMs = now + (ulong)(s * 1000);
                    reply.Num("trace", s);
                    break;
                }
                case "fps":
                {
                    // review F10: the 30 fps cap of the S7.N4.loss burst (the S2 recipe's pair returns when cleared: cap 0)
                    object v;
                    double cap = 0;
                    if (args.TryGetValue("cap", out v) && v is double) cap = (double)v;
                    if (cap < 0 || cap > 1000) { err = "de.fx fps: cap 1..1000 frames per second, 0 = off"; break; }
                    PerfApplier.TestFpsCap = (int)cap;
                    DeLog.Info("DE.S7 fx test frame cap " + (cap > 0 ? cap.ToString("0", CultureInfo.InvariantCulture) + " fps (vSyncCount 0)" : "off (the perf recipe again)"));
                    reply.Num("fpsCap", cap);
                    break;
                }
                default:
                    err = "unknown action '" + action + "' (status, sources, trace, fps)";
                    break;
            }
            reply.Bool("ok", err == null).Str("cmd", req.Cmd).Str("action", action);
            if (err != null) reply.Str("error", err);
            Status(reply);
            return true;
        }

        public void Status(JsonObjectWriter w)
        {
            w.Bool("fxStarted", _started).Bool("fxLibrary", _lib.Loaded).Bool("fxLoading", _lib.Loading).Bool("fxPools", _poolsBuilt)
             .Num("fxEvents", _eventsTotal).Num("fxMissed", Reader.Missed).Num("fxPending", (long)Reader.PendingCount).Num("fxSustains", (long)_sustains.Count)
             .Num("fxEngines", (long)_engines.Count).Num("fxMissingEntity", _missingEntity).Num("fxMsLast", _msLast)
             .Str("fxStream", "0x" + Reader.CurrentStream.ToString("x8", CultureInfo.InvariantCulture)).Str("fxSources", _lib.Describe())
             .Bool("fxTracing", Win32.TickCount64() < _traceUntilMs).Num("fxFpsCap", (long)PerfApplier.TestFpsCap);
            S7pStatus(w); // S7p-effects-under
        }
    }
}
