using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S6 "Collision B + combat" on the Subnautica side (ROADMAP S6 SN plugin; CONTRACT §2.6, §2.7, §4.2, §5.4), created
 /// by the bridge after S5 when <c>DEEPENGINEERS_S6</c> is not off. It owns:
 /// <list type="bullet">
 /// <item>the grid proxies (<see cref="ProxySections"/>, fed by MirrorState's SkinSet store and posed from the mirror's
 /// applied pose at each camera sample) and the character section (<see cref="CharacterProxy"/>, from C1);</item>
 /// <item>the proxy stepper / step timer (physics-step movement, the 256-shapes budget, s6.perf) and the physics rate
 /// (<see cref="PhysicsRate"/>);</item>
 /// <item>the damage path: <see cref="DamagePatches"/> → <see cref="OnTakeDamage"/> → <see cref="DamageSink"/> (C8),
 /// the bite acceptance (<see cref="AcceptBite"/>), the Freedom gate, the start-up self-check (S6.N9);</item>
 /// <item>the dev commands <c>de.s6</c> and <c>de.spawn</c> (<see cref="S6Scene"/> runs B-T1, the G1-E3 probe, paths and
 /// penetration sampling);</item>
 /// <item>C7: flags bit 10 damageSelfTestPass, capabilities bit 1, damageDropped, damageOverflowDepth, fixedDeltaTime.</item>
 /// </list>
 /// Every step runs in its own guard (<c>DE.S6 FEATURE … DISABLED</c> once); S0–S5 never depend on S6.
 /// </summary>
    internal sealed class S6Host : IDisposable
    {
        public const string RowHit = "s6.hit", RowSkin = "s6.skin", RowPerf = "s6.perf", RowGate = "s6.gate", RowSelfTest = "s6.selftest", RowSpawn = "s6.spawn";
        public const int MaxHitRowsPerSecond = 100;

        private readonly MonoBehaviour _bridge;
        private readonly S1Host _s1;
        private readonly S3Host _s3;
        private readonly DamageSink _sink;
        public readonly ProxySections Proxies = new ProxySections();
        public readonly CharacterProxy Character = new CharacterProxy();
        private readonly PhysicsRate _rate = new PhysicsRate();
        public readonly S6Scene Scene;
        private readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.Ordinal);

        private GameObject _go;
        private ProxyStepper _stepper;
        private ProxyStepTimer _timer;
        private bool _started, _worldUp;
        private uint _helloEpoch;
        private int _lastSampleFrame = -1;
        private bool _m1;
        private double _now;
        private Action _aSample, _aRate, _aSelf, _aScene, _aSceneSecond, _aFlush, _aStepper, _aPerf, _aRefreshRow, _aGate;

        // Freedom gate
        private bool _gatePass, _gateKnown;
        private string _gateLine = "not evaluated";
        private int _gateMode, _gateCheats;
        private bool _gateInvisible;
        private double _nextGateSec;

        // self-check (S6.N9)
        private bool _selfTestPass;
        private int _selfStage;
        private int _selfFrame;
        private GameObject _testGo;
        private LiveMixin _testLive;
        private string _selfDetail = "not run";
        private int _selfRuns, _selfRunsWorld;
        private bool _selfRequested;

        // perf (s6.perf) and hits
        private readonly MsWindow _proxyMs = new MsWindow(1024), _physxMs = new MsWindow(1024), _stepMs = new MsWindow(1024), _proxyPhysxMs = new MsWindow(1024);
        private long _steps, _stepsWindow;
        private double _nextSecond;
        private int _hitRows;
        public long Hits, HitsSection, HitsCharacter, HitsPlayer, HitsMouth, HitsFallback, HitsRadius, SkipRows;
        private readonly RaycastHit[] _losHits = new RaycastHit[64];
        public long LosBlocked, BitesAccepted;
        private double _spawnUntil;

        public S6Host(MonoBehaviour bridge, S1Host s1, S3Host s3, EntryRing c8, uint generation)
        {
            if (s3 == null) throw new ArgumentNullException("s3");
            _bridge = bridge;
            _s1 = s1;
            _s3 = s3;
            _sink = new DamageSink(c8, generation);
            Scene = new S6Scene(this);
        }

        public MirrorConsumer Mirror { get { return _s3.Mirror; } }
        public DamageSink Sink { get { return _sink; } }
        public bool GatePass { get { return _gatePass; } }
        // S11 hook: the hello epoch and the ghost rule for S11's records through the same sink
        public uint HelloEpoch { get { return _helloEpoch; } }
        public bool PlayerAtCharacter { get { return Character.AtCharacter && (_s1 == null || _s1.Ghost.AtPelvis); } }
        public string GateLine { get { return _gateLine; } }
        public bool WorldUp { get { return _worldUp; } }
        public bool SelfTestPass { get { return _selfTestPass; } }
        public ProxyStepper Stepper { get { return _stepper; } }
        public PhysicsRate Rate { get { return _rate; } }
        public double Now { get { return Time.realtimeSinceStartup; } }

        public void Start()
        {
            SkinStore skins = Mirror.State.Skins;
            skins.Changed = OnSkin;
            skins.Removed = OnSkinRemoved;
            skins.Cleared = OnSkinsCleared;
            Mirror.S6CameraSample = OnCameraSample;
            EnsureStepper();
            DamagePatches.Host = this;
            DamagePatches.Install();
            ulong conv = LayoutHash.Fnv1a64Ascii(CombatConventions.CanonicalText());
            DeLog.Info("DE.S6 start: conventions '" + CombatConventions.Header + "' hash " + DeLog.Hex(conv) +
                       (conv == CombatConventions.PinnedHash ? " (pinned)" : " MISMATCH (pinned " + DeLog.Hex(CombatConventions.PinnedHash) + ")") +
                       " c8=" + (_sink.HasRing ? "ring" : "NONE") + " gen=" + _sink.Builder.Generation + " " + DamagePatches.Describe() +
                       " mutants=" + S6Mutants.Describe() + " (" + S6Switches.Var + "=on)");
            _started = true;
        }

        /// <summary>
 /// The "DE.S6" object with the stepper and the step timer (DontDestroyOnLoad + SceneCleanerPreserve: it holds no world
 /// state); made again if something destroyed it.
 /// </summary>
        private void EnsureStepper()
        {
            if (_go != null && _stepper != null && _timer != null) return;
            if (_go != null) Object.Destroy(_go);
            _go = new GameObject("DE.S6");
            Object.DontDestroyOnLoad(_go);
            _go.AddComponent<SceneCleanerPreserve>();
            _stepper = _go.AddComponent<ProxyStepper>();
            _stepper.Host = this;
            _timer = _go.AddComponent<ProxyStepTimer>();
            _timer.Stepper = _stepper;
            _timer.Host = this;
        }

        // ------------------------------------------------------------------------------------------ per frame

        public void Tick(ulong nowMs, uint helloEpoch)
        {
            if (!_started) return;
            _helloEpoch = helloEpoch;
            double now = Now;
            _now = now;
            if (_aSample == null)
            {
                // cached delegates: no allocation per frame
                _aSample = () => Sample(_now);
                _aRate = () => _rate.Update(_stepper, AnyMovingNear(_now), _now);
                _aSelf = () => SelfCheckStep(_now);
                _aScene = () => Scene.Tick(_now);
                _aSceneSecond = () => Scene.PerSecond(_now);
                _aFlush = _sink.Flush;
                _aStepper = EnsureStepper;
                _aPerf = PerfRow;
                _aRefreshRow = Proxies.RefreshRow;
                _aGate = EvaluateGate;
            }
            Guard("stepper", _aStepper);
            bool ready = Mirror.WorldReady && Player.main != null;
            if (ready && !_worldUp) Guard("worldUp", OnWorldUp);
            else if (!ready && _worldUp) Guard("worldDown", () => OnWorldDown("the host world went away"));
            Guard("flush", _aFlush);
            if (!_worldUp) return;
            if (now >= _nextGateSec) { _nextGateSec = now + 1.0; Guard("gate", _aGate); }
            bool m1 = S6Mutants.IsOn(S6Mutants.Triggers);
            if (m1 != _m1) { _m1 = m1; Proxies.RefreshAll(); DeLog.Warn("DE.S6 S6.M1 " + (m1 ? "ON: proxy colliders become triggers" : "off: colliders non-trigger again")); }
            if (_lastSampleFrame < Time.frameCount - 1) Guard("sample", _aSample);
            Guard("rate", _aRate);
            Guard("selftest", _aSelf);
            Guard("scene", _aScene);
            if (now >= _nextSecond)
            {
                _nextSecond = now + 1.0;
                _hitRows = 0;
                Guard("perf", _aPerf);
                Guard("refreshRow", _aRefreshRow);
                Guard("sceneSecond", _aSceneSecond);
            }
        }

        private void OnWorldUp()
        {
            _worldUp = true;
            _selfTestPass = false;
            _lastSampleFrame = -1;
            _selfStage = 0;
            _selfDetail = "pending (world loaded)";
            _selfRunsWorld = 0;
            _gateKnown = false;
            _nextGateSec = 0;
            // skins received while the world was down (or before S6 started) become proxies now
            foreach (SkinEntry e in Mirror.State.Skins.Skins.Values) OnSkin(e);
            DeLog.Info("DE.S6 world up: proxies " + Proxies.EntityCount + " entities; self-check pending the Freedom gate");
        }

        private void OnWorldDown(string why)
        {
            _worldUp = false;
            _selfTestPass = false;
            Scene.Stop(why);
            Proxies.Clear(why);
            Character.Destroy(Proxies);
            DestroyTestSection();
            _rate.ForceOff(_stepper, why);
        }

        private void EvaluateGate()
        {
            string line;
            int mode, cheats;
            bool inv;
            bool pass = FreedomGate.Evaluate(out line, out mode, out cheats, out inv);
            if (_gateKnown && pass == _gatePass && mode == _gateMode && cheats == _gateCheats && inv == _gateInvisible) return;
            _gateKnown = true;
            _gatePass = pass;
            _gateMode = mode; _gateCheats = cheats; _gateInvisible = inv;
            _gateLine = line;
            if (pass) DeLog.Info(line); else DeLog.Warn(line);
            JsonOut j = S6Rows.Begin(RowGate);
            j.Str("mode", GameModeOptionBits.Name(mode)).Num("modeValue", (long)mode).Num("cheats", (long)cheats).Bool("invisible", inv).Bool("pass", pass).Str("line", line);
            S6Rows.End(j);
        }

        /// <summary>The mirror's camera sample (MirrorConsumer.OnCameraSample hook, after the character): proxy targets follow it.</summary>
        public void OnCameraSample(CamBracket br, bool preCull)
        {
            if (!_started || !_worldUp || _aSample == null) return;
            _lastSampleFrame = Time.frameCount;
            _now = Now;
            Guard("sample", _aSample);
        }

        private void Sample(double now)
        {
            Proxies.Sample(Mirror, now);
            Character.Sample(Mirror.Character, now);
        }

        private bool AnyMovingNear(double now)
        {
            Camera cam = MainCamera.camera;
            if (cam == null) return false;
            Vector3 c = cam.transform.position;
            if (Proxies.AnyMovingNear(c, PhysicsRateRule.NearRadiusM, now)) return true;
            if (!Character.HasTarget || now - Character.LastMovedSec > MotionRule.WindowSeconds) return false;
            Vec3 p = Character.TargetPos;
            double dx = p.X - c.x, dy = p.Y - c.y, dz = p.Z - c.z;
            return dx * dx + dy * dy + dz * dz <= (double)PhysicsRateRule.NearRadiusM * PhysicsRateRule.NearRadiusM;
        }

        // ------------------------------------------------------------------------------------------ physics step

 /// <summary>The stepper's FixedUpdate (before the game's scripts).</summary>
        public void PhysicsStep()
        {
            if (!_started || !_worldUp || _disabled.Contains("step")) return;
            try
            {
                _steps++;
                _stepsWindow++;
                Proxies.Step(CombatConventions.Limits.MaxShapesPerStep);
                Character.Step(Proxies);
                Scene.BeforeStep();
            }
            catch (Exception e)
            {
                _disabled.Add("step");
                DeLog.Error("DE.S6 FEATURE step DISABLED after " + e.GetType().Name + ": " + e.Message + " (proxies stop moving)\n" + e);
            }
        }

        /// <summary>After the physics simulation of the step (WaitForFixedUpdate): step times and the penetration sampler.</summary>
        public void AfterPhysicsStep(double proxyMs, double physxMs, double stepMs)
        {
            if (!_started || !_worldUp) return;
            _proxyMs.Add(proxyMs);
            _stepMs.Add(stepMs);
            if (!double.IsNaN(physxMs)) { _physxMs.Add(physxMs); _proxyPhysxMs.Add(proxyMs + physxMs); }
            Scene.AfterStep();
        }

        public void OnStepperDestroyed(ProxyStepper s)
        {
            try { _rate.ForceOff(s, "proxy stepper destroyed"); } catch (Exception) { }
            if (ReferenceEquals(s, _stepper)) _stepper = null;
        }

        private void PerfRow()
        {
            double p50, p95, max, s50, s95, smax, x50, x95, xmax, a50, a95, amax;
            int n, ns, nx, na;
            _proxyMs.Take(out p50, out p95, out max, out n);
            _stepMs.Take(out s50, out s95, out smax, out ns);
            _physxMs.Take(out x50, out x95, out xmax, out nx);
            _proxyPhysxMs.Take(out a50, out a95, out amax, out na);
            if (_stepsWindow == 0 && Proxies.SectionCount == 0 && !Scene.Running) return;
            JsonOut j = S6Rows.Begin(RowPerf);
            j.Num("steps", _stepsWindow).Num("fixedDt", (double)Time.fixedDeltaTime).Bool("registered", _rate.Registered).Num("sections", (long)Proxies.SectionCount)
             .Num("shapes", (long)Proxies.ShapeCount).Num("refreshes", Proxies.Refreshes).Num("created", Proxies.Created).Num("proxyMsP50", p50).Num("proxyMsP95", p95)
             .Num("stepMsP50", s50).Num("stepMsP95", s95).Num("physxMsP50", x50).Num("physxMsP95", x95).Num("proxyPhysxMsP50", a50).Num("proxyPhysxMsP95", a95)
             .Num("proxyPhysxMsMax", amax).Bool("proxiesOn", Proxies.SectionCount > 0).Bool("character", Character.Active).Str("phase", Scene.Phase)
             .Num("pendingPasses", (long)Proxies.Budget.Pending);
            S6Rows.End(j);
            _stepsWindow = 0;
        }

        // ------------------------------------------------------------------------------------------ skins (SkinStore callbacks)

        private void OnSkin(SkinEntry e)
        {
            if (!_started || !_worldUp) return; // kept in the store; OnWorldUp builds every stored skin
            Guard("skin", () =>
            {
                GridRecord g;
                Mirror.State.Grids.TryGetValue(e.EntityId, out g);
                int before = Proxies.SectionCount;
                Proxies.ApplySkin(e.EntityId, e.Version, e.Sections, g, false);
                SkinStore st = Mirror.State.Skins;
                JsonOut j = S6Rows.Begin(RowSkin);
                j.Str("entity", S6Rows.Hex(e.EntityId)).Num("version", (long)e.Version).Num("sections", (long)e.Sections.Count).Num("boxes", (long)e.Boxes)
                 .Num("sectionsBefore", (long)before).Num("sectionsNow", (long)Proxies.SectionCount).Num("created", Proxies.Created).Num("updated", Proxies.Updated)
                 .Num("removed", Proxies.Removed).Num("stale", st.Stale).Num("bad", st.Bad).Num("kindIgnored", st.KindIgnored).Bool("known", g != null)
                 .Num("targetClass", g != null ? (long)g.TargetClass : 0L).Bool("subgrid", g != null && g.ParentId != 0);
                if (st.LastError != null) j.Str("lastError", st.LastError);
                S6Rows.End(j);
            });
        }

        private void OnSkinRemoved(ulong id) { Guard("skinRemove", () => Proxies.Remove(id, "EntityRemove")); }

        private void OnSkinsCleared() { Guard("skinClear", () => Proxies.ClearWire("C4 ClearAll / new SE generation / new world epoch")); }

        // ------------------------------------------------------------------------------------------ damage (DamagePatches)

 /// <summary>The TakeDamage prefix's question: one of our sections, the character section, the test section or the Player.</summary>
        public bool IsTarget(LiveMixin lm)
        {
            ProxyMarker m;
            if (Proxies.TryGet(lm, out m)) return true;
            Player p = Player.main;
            return p != null && ReferenceEquals(lm, p.liveMixin);
        }

        /// <summary>
 /// The TakeDamage postfix (CONTRACT §4.2 "Emit rule", §2.7 hit point): one C8 record per call on our targets.
 /// </summary>
        public void OnTakeDamage(LiveMixin lm, float amount, Vector3 position, int type, GameObject dealer, bool gamePassed, DamageContext ctx)
        {
            if (!_started) return;
            ProxyMarker m;
            bool isPlayer = false;
            if (!Proxies.TryGet(lm, out m))
            {
                Player p = Player.main;
                if (p == null || !ReferenceEquals(lm, p.liveMixin)) return;
                isPlayer = true;
            }
            bool capture = _sink.CaptureMode;
            bool atChar = Character.AtCharacter && (_s1 == null || _s1.Ghost.AtPelvis);
            S6Skip skip = S6Emit.Check(gamePassed, amount, type, _gatePass, isPlayer, atChar, capture);
            if (skip == S6Skip.None && ctx.Kind == DamageContext.Blast && S6Mutants.IsOn(S6Mutants.DropRadius)) skip = S6Skip.RadiusMutant;
            // review F10: RadiusDamage / a shock can reach the ghost Player's LiveMixin and the character section in one event
            if (skip == S6Skip.None && isPlayer && Character.Active && (ctx.Kind == DamageContext.Blast || ctx.Kind == DamageContext.Shocker)) skip = S6Skip.PlayerShadowed;
            if (skip != S6Skip.None)
            {
                if (skip == S6Skip.TypeNotForwarded && S11Hooks.Unforwarded(isPlayer, m, amount, position, type, dealer, _gatePass, atChar, capture)) return; // S11 hook: the hazard table
                _sink.Count(skip, type);
                SkipRow(skip, type, amount, isPlayer ? "player" : m.SelfTest ? "selftest" : S6Rows.Hex(m.EntityId));
                return;
            }
            if (!S6Mutants.IsOn(S6Mutants.NoSync)) Physics.SyncTransforms();

            // the hit point w (CONTRACT §2.7: mouth stash, dealer context, dealer, position, root)
            HitVia via;
            Vector3 w;
            Vector3 dp = Vector3.zero;
            bool hasDp = false;
            GameObject dealerGo = dealer;
            if (isPlayer)
            {
                via = HitVia.Player;
                if (dealer != null) { dp = dealer.transform.position; hasDp = true; }
                else if (ctx.Kind == DamageContext.Shocker || ctx.Kind == DamageContext.Blast) { dp = ctx.Point; hasDp = true; dealerGo = ctx.Kind == DamageContext.Shocker ? ctx.Creature : ctx.Source; }
                else if (position != default(Vector3)) { dp = position; hasDp = true; }
                w = lm.transform.position;
            }
            else if (ctx.Kind == DamageContext.Mouth && ctx.Col != null && ReferenceEquals(ctx.Target, lm.gameObject) && ReferenceEquals(dealer, ctx.Creature))
            {
                via = HitVia.Mouth;
                dp = ctx.Point; hasDp = true;
                w = ctx.Col.ClosestPoint(ctx.Point);
            }
            else if (ctx.Kind == DamageContext.Shocker && (ctx.Target == null || ReferenceEquals(ctx.Target, lm.gameObject)))
            {
                via = HitVia.Shocker;
                dp = ctx.Point; hasDp = true;
                dealerGo = ctx.Creature;
                w = Closest(m, dp, lm);
            }
            else if (ctx.Kind == DamageContext.Blast)
            {
                via = HitVia.Radius;
                dp = ctx.Point; hasDp = true;
                dealerGo = ctx.Source;
                w = Closest(m, dp, lm);
            }
            else if (dealer != null)
            {
                via = HitVia.Dealer;
                dp = dealer.transform.position; hasDp = true;
                w = Closest(m, dp, lm);
            }
            else if (position != default(Vector3))
            {
                via = HitVia.Position;
                dp = position; hasDp = true;
                w = Closest(m, dp, lm);
            }
            else
            {
                via = HitVia.Root;
                w = lm.transform.position;
                _sink.NoDealerPoint++;
            }

            var h = new HitInput
            {
                Amount = amount, Type = type, WorldHit = ProxySections.D(w), DealerPoint = ProxySections.D(dp), HasDealerPoint = hasDp,
                SnTimeSec = Time.realtimeSinceStartup,
            };
            if (dealerGo != null)
            {
                h.DealerTechType = DealerTech(dealerGo);
                h.DealerInstanceId = (uint)dealerGo.GetInstanceID();
            }
            bool subgrid = false;
            if (isPlayer)
            {
                h.TargetKind = DamageTargetKind.Character;
                h.TargetEntityId = h.RootEntityId = Character.CharacterId;
                h.SnapshotTick = Character.Tick;
                h.LocalHit = Character.HasTarget ? HitFrames.CharacterLocal(Character.O, Character.Q, Character.S, h.WorldHit) : Vec3.Zero;
                h.Flags = S6Emit.Flags(via, false, true);
            }
            else if (m.Kind == DamageTargetKind.Character)
            {
                h.TargetKind = DamageTargetKind.Character;
                h.TargetEntityId = h.RootEntityId = m.EntityId;
                h.SnapshotTick = m.StepTick;
                h.LocalHit = m.StepValid ? HitFrames.CharacterLocal(m.StepO, m.StepQ, m.StepS, h.WorldHit) : Vec3.Zero;
                h.Flags = S6Emit.Flags(via, false, false);
            }
            else
            {
                subgrid = m.IsSubgrid && !m.SelfTest;
                h.TargetKind = DamageTargetKind.GridSection;
                h.SectionId = m.SectionId;
                h.TargetClass = m.TargetClass;
                h.TargetEntityId = m.EntityId;
                h.RootEntityId = m.RootEntityId;
                h.SnapshotTick = m.StepTick;
                h.LocalHit = m.StepValid ? HitFrames.GridLocal(m.StepO, m.StepQ, m.StepS, m.StepRel, subgrid, h.WorldHit, S6Mutants.IsOn(S6Mutants.RootFrameHit)) : Vec3.Zero;
                h.Flags = S6Emit.Flags(via, subgrid, false);
            }
            bool queued;
            DamageRecord r = _sink.Emit(ref h, _helloEpoch, out queued);
            Hits++;
            if (isPlayer) HitsPlayer++; else if (m.Kind == DamageTargetKind.Character) HitsCharacter++; else HitsSection++;
            if (via == HitVia.Mouth) HitsMouth++; else HitsFallback++;
            if (via == HitVia.Radius) HitsRadius++;
            Scene.NoteHit(r, via, m, isPlayer);
            HitRow(r, via, dealerGo, queued, capture, m);
            if (!capture) S11Hooks.AfterS6Hit(r, dealerGo, isPlayer, m); // S11 hook: a leviathan bite pushes a grid it does not hold
        }

        /// <summary>CraftData.GetTechType of the dealer, else of its Creature root (a MeleeAttack may sit on a child); 0 = None.</summary>
        private static int DealerTech(GameObject go)
        {
            try
            {
                TechType t = CraftData.GetTechType(go);
                if (t == TechType.None)
                {
                    Creature c = go.GetComponentInParent<Creature>();
                    if (c != null) t = CraftData.GetTechType(c.gameObject);
                }
                return (int)t;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>The nearest point of the section's enabled colliders to p (its root when it has none).</summary>
        private static Vector3 Closest(ProxyMarker m, Vector3 p, LiveMixin lm)
        {
            Vector3 best = lm.transform.position;
            float bestD = float.PositiveInfinity;
            if (m == null) return best;
            foreach (Collider c in m.Colliders)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                Vector3 q = c.ClosestPoint(p);
                float d = (q - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = q; }
            }
            return best;
        }

        private void HitRow(DamageRecord r, HitVia via, GameObject dealer, bool queued, bool capture, ProxyMarker m)
        {
            if (++_hitRows > MaxHitRowsPerSecond) return;
            JsonOut j = S6Rows.Begin(RowHit);
            j.Num("gen", (long)r.snGeneration).Num("seq", (long)r.dedupSeq).Str("id", DamageRules.HitId(r.snGeneration, r.dedupSeq)).Num("kind", (long)r.targetKind)
             .Str("target", S6Rows.Hex(r.targetEntityId)).Str("root", S6Rows.Hex(r.rootEntityId)).Str("section", S6Rows.Sec(r.sectionId)).Num("type", (long)r.snDamageType)
             .Str("seType", CombatConventions.SeDamageTypeName(r.snDamageType) ?? "none").Num("amount", (double)r.amount).Num("scaleExpected", (double)r.seScaleExpected)
             .Num("flags", (long)r.flags).Str("via", S6Emit.ViaName(via)).Str("dealer", dealer != null ? TechName(r.dealerTechType) : "none").Num("dealerId", (long)r.dealerInstanceId)
             .Vec("w", r.snWorldHit.x, r.snWorldHit.y, r.snWorldHit.z).Vec("local", r.localHit.x, r.localHit.y, r.localHit.z).Num("tick", r.snapshotTick)
             .Num("worldEpoch", (long)r.worldEpoch).Bool("queued", queued).Bool("capture", capture).Bool("selfTest", m != null && m.SelfTest)
             .Num("health", m != null && m.Live != null ? (double)m.Live.health : double.NaN);
            S6Rows.End(j);
        }

        private void SkipRow(S6Skip skip, int type, float amount, string target)
        {
            SkipRows++;
            if (++_hitRows > MaxHitRowsPerSecond) return;
            JsonOut j = S6Rows.Begin(RowHit);
            j.Str("skip", S6Emit.SkipName(skip)).Num("type", (long)type).Num("amount", (double)amount).Str("target", target);
            S6Rows.End(j);
        }

        public static string TechName(int techType)
        {
            try { return ((TechType)techType).ToString(); } catch (Exception) { return techType.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>
 /// The CanDealDamageTo postfix's question (CONTRACT §2.7 "Bite acceptance"): an enabled proxy marker, the combat
 /// gate open, and the game's own line-of-sight rule with every section of the same root counted as the target
 /// (raycast from the creature to the section root, mask −5, triggers ignored, hits resolved through
 /// attachedRigidbody; the creature itself and other Creatures do not block). No SyncTransforms: the game's own query
 /// runs the same way in the same call.
 /// </summary>
        public bool AcceptBite(MeleeAttack attacker, GameObject target)
        {
            ProxyMarker m;
            if (!_worldUp || !Proxies.TryGetRoot(target, out m)) return false;
            if (m == null || !m.isActiveAndEnabled || m.SelfTest || !_gatePass) { LosBlocked++; return false; }
            Vector3 from = attacker.transform.position;
            Vector3 dir = target.transform.position - from;
            float dist = dir.magnitude;
            if (dist > 1e-4f)
            {
                int n = Physics.RaycastNonAlloc(from, dir / dist, _losHits, dist, -5, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    Collider c = _losHits[i].collider;
                    if (c == null) continue;
                    GameObject go = c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : c.gameObject;
                    if (ReferenceEquals(go, target) || ReferenceEquals(go, attacker.gameObject)) continue;
                    ProxyMarker om;
                    if (Proxies.TryGetRoot(go, out om) && om.Kind == m.Kind && om.RootEntityId == m.RootEntityId) continue;
                    if (go.GetComponent<Creature>() != null) continue;
                    LosBlocked++;
                    return false;
                }
            }
            BitesAccepted++;
            return true;
        }

        // ------------------------------------------------------------------------------------------ spawns

        public void OpenSpawnWindow(double seconds) { _spawnUntil = Math.Max(_spawnUntil, Now + seconds); }

        /// <summary>CrafterLogic.NotifyCraftEnd postfix: one s6.spawn row per object while a de.spawn window is open.</summary>
        public void OnSpawned(GameObject go, TechType techType)
        {
            if (Now > _spawnUntil) return;
            Scene.TrackSpawn(go, techType);
            JsonOut j = S6Rows.Begin(RowSpawn);
            j.Str("techType", techType.ToString()).Num("id", (long)go.GetInstanceID()).Vec("pos", ProxySections.D(go.transform.position));
            AvoidObstacles ao = go.GetComponentInChildren<AvoidObstacles>(true);
            if (ao != null) j.Bool("avoidTerrainOnly", ao.avoidTerrainOnly); else j.Str("avoidTerrainOnly", "none");
            MeleeAttack ma = go.GetComponentInChildren<MeleeAttack>(true);
            if (ma != null) j.Num("biteDamage", (double)ma.biteDamage).Num("biteInterval", (double)ma.biteInterval).Str("melee", ma.GetType().Name);
            var tags = new List<string>();
            foreach (OnTouch ot in go.GetComponentsInChildren<OnTouch>(true)) if (ot != null) tags.Add(string.IsNullOrEmpty(ot.tagFilter) ? "(empty)" : ot.tagFilter);
            j.Str("tagFilter", tags.Count == 0 ? "none" : string.Join(",", tags.ToArray()));
            S6Rows.End(j);
        }

        // ------------------------------------------------------------------------------------------ self-check (S6.N9)

        public void RequestSelfCheck() { _selfRequested = true; _selfStage = 0; }

        private void SelfCheckStep(double now)
        {
            if (_selfStage == 0)
            {
                if (_selfTestPass && !_selfRequested) return;
                if (!_gatePass) { _selfDetail = "waiting for the Freedom gate"; return; }
                if (!DamagePatches.InstalledTakeDamage && !S6Mutants.IsOn(S6Mutants.LateForwarder)) { _selfDetail = "TakeDamage patch MISSING"; return; }
                Player p = Player.main;
                if (p == null || p.liveMixin == null) return;
                if (_selfRunsWorld > 0 && !_selfRequested) return; // once per world unless asked again
                CreateTestSection();
                _selfStage = 1;
                _selfFrame = Time.frameCount;
                return;
            }
            if (_selfStage == 1 && Time.frameCount > _selfFrame) // the test LiveMixin's Start has run (its receivers cached)
            {
                RunSelfCheck();
                _selfStage = 2;
                _selfRequested = false;
            }
        }

        private void CreateTestSection()
        {
            DestroyTestSection();
            _testGo = new GameObject("DE.Proxy selftest");
            _testGo.layer = 0;
            Object.DontDestroyOnLoad(_testGo);
            _testGo.transform.position = new Vector3(0f, 4000f, 0f); // no collider, no renderer, no EcoTarget: never touched
            _testLive = ProxySections.AddLive(_testGo);
            ProxyMarker m = _testGo.AddComponent<ProxyMarker>();
            m.SelfTest = true;
            m.Kind = DamageTargetKind.GridSection;
            m.TargetClass = GridClass.SmallGrid;
            m.Live = _testLive;
            Proxies.Register(m, _testLive, _testGo);
        }

        private void DestroyTestSection()
        {
            if (_testGo == null) return;
            Proxies.Unregister(_testLive, _testGo);
            Object.Destroy(_testGo);
            _testGo = null;
            _testLive = null;
        }

        private void RunSelfCheck()
        {
            _selfRuns++;
            _selfRunsWorld++;
            Player p = Player.main;
            if (S6Mutants.IsOn(S6Mutants.LateForwarder) && p != null && p.GetComponent<LateDamageForwarder>() == null)
            {
                LateDamageForwarder f = p.gameObject.AddComponent<LateDamageForwarder>(); // late: never in the Player's cached receivers
                f.Live = p.liveMixin;
            }
            float playerBefore = p != null && p.liveMixin != null ? p.liveMixin.health : float.NaN;
            _sink.Captured.Clear();
            _sink.CaptureMode = true;
            string err = null;
            try
            {
                if (_testLive != null) _testLive.TakeDamage(1f, default(Vector3), DamageType.Normal, null);
                if (p != null && p.liveMixin != null) p.liveMixin.TakeDamage(1f, default(Vector3), DamageType.Normal, null);
            }
            catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            finally { _sink.CaptureMode = false; }
            float playerAfter = p != null && p.liveMixin != null ? p.liveMixin.health : float.NaN;
            if (p != null && p.liveMixin != null && playerAfter < playerBefore) p.liveMixin.health = playerBefore; // Subnautica health never changes (§4.2)
            int sec, pl;
            string detail;
            bool pass = SelfCheckBook.Judge(_sink.Captured, out sec, out pl, out detail) && err == null && _gatePass;
            if (err != null) detail += " error=" + err;
            _selfTestPass = pass;
            _selfDetail = detail;
            float testHealth = _testLive != null ? _testLive.health : float.NaN;
            DestroyTestSection();
            string line = "DE.S6 selftest " + (pass ? "PASS" : "FAIL") + " " + detail + " playerHealth=" + playerBefore.ToString("0.###", CultureInfo.InvariantCulture) + "->" +
                          playerAfter.ToString("0.###", CultureInfo.InvariantCulture) + " testHealth=" + testHealth.ToString("0.###", CultureInfo.InvariantCulture) +
                          " mutants=" + S6Mutants.Describe() + " run=" + _selfRuns;
            if (pass) DeLog.Info(line); else DeLog.Warn(line);
            JsonOut j = S6Rows.Begin(RowSelfTest);
            j.Num("sectionRecords", (long)sec).Num("playerRecords", (long)pl).Bool("pass", pass).Str("detail", detail).Num("playerHealthBefore", (double)playerBefore)
             .Num("playerHealthAfter", (double)playerAfter).Num("testHealth", (double)testHealth).Str("mutants", S6Mutants.Describe()).Num("run", (long)_selfRuns)
             .Bool("gate", _gatePass).Str("patches", DamagePatches.Describe());
            S6Rows.End(j);
        }

        // ------------------------------------------------------------------------------------------ C7

        public void FillStatus(ref SnStatus s)
        {
            if (!_started) return;
            bool m2 = S6Mutants.IsOn(S6Mutants.NoCanDeal), m12 = S6Mutants.IsOn(S6Mutants.LateForwarder);
            if (DamagePatches.InstalledTakeDamage && DamagePatches.InstalledCanDeal && !m2 && !m12 && _gatePass) s.capabilities |= Capabilities.CreaturesAndDamage;
            if (_selfTestPass && _gatePass && (DamagePatches.InstalledTakeDamage || m12)) s.flags |= SnStatusFlags.DamageSelfTestPass;
            s.damageDropped = (uint)(_sink.Dropped & 0xFFFFFFFFL);
            s.damageOverflowDepth = (uint)_sink.Depth;
            s.fixedDeltaTime = Time.fixedDeltaTime; // every frame (S6.N7)
        }

        // ------------------------------------------------------------------------------------------ commands

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req.Cmd == "de.spawn")
            {
                var list = S1Args.Unwrap(req.Args) as List<object>;
                Dictionary<string, object> a = list != null ? SpawnArgs.FromArgv(list) : S3Args.ToDict(req.Args);
                string error;
                JsonOut spawned = Spawn(a, out error);
                reply.Bool("ok", error == null).Str("cmd", req.Cmd);
                if (error != null) reply.Str("error", error); else reply.Raw("spawn", spawned.ToString());
                return true;
            }
            if (req.Cmd != "de.s6") return false;
            Dictionary<string, object> args = S3Args.ToDict(req.Args);
            string action = S3Args.Str(args, "action", "status");
            string err = null;
            JsonOut detail = null;
            switch (action)
            {
                case "status":
                    break;
                case "selftest":
                    if (!_gatePass) err = "freedom gate REFUSED: " + _gateLine;
                    else if (!_worldUp) err = "no world";
                    else { RequestSelfCheck(); reply.Str("note", "the self-check runs over the next two frames (s6.selftest row, DE.S6 selftest line)"); }
                    break;
                case "spawn":
                    detail = Spawn(args, out err);
                    break;
                default:
                    if (!Scene.Handle(action, args, out err, out detail)) err = "unknown action '" + action + "' (status, selftest, spawn, bt1, local, probe, paths, penetration, teardown)";
                    break;
            }
            reply.Bool("ok", err == null).Str("cmd", req.Cmd).Str("action", action);
            if (err != null) reply.Str("error", err);
            if (detail != null) reply.Raw("detail", detail.ToString());
            Status(reply);
            return true;
        }

        /// <summary>de.spawn / de.s6 spawn: refused unless the Freedom gate passed (S6.N2).</summary>
        public JsonOut Spawn(Dictionary<string, object> a, out string error)
        {
            if (!_worldUp) { error = "no world"; return null; }
            if (!_gatePass) { error = "freedom gate REFUSED: " + _gateLine; return null; }
            string t, cmd;
            int count, dist;
            if (!SpawnArgs.TryParse(a, SpawnRelay.TechTypeNames(), out t, out count, out dist, out error)) return null;
            OpenSpawnWindow(30);
            bool ok = SpawnRelay.Send(t, count, dist, out cmd, out error);
            JsonOut j = S6Rows.Begin(RowSpawn);
            j.Str("techType", t).Num("count", (long)count).Num("dist", (long)dist).Str("cmd", cmd).Bool("ok", ok);
            if (error != null) j.Str("error", error);
            S6Rows.End(j);
            DeLog.Info("DE.S6 spawn '" + cmd + "' " + (ok ? "sent" : "FAILED: " + error));
            var r = new JsonOut();
            r.Str("techType", t).Num("count", (long)count).Num("dist", (long)dist).Str("cmd", cmd).Bool("sent", ok);
            return ok ? r : null;
        }

        public void Status(JsonObjectWriter w)
        {
            w.Bool("started", _started).Bool("worldUp", _worldUp).Bool("gatePass", _gatePass).Str("gate", _gateLine).Bool("selfTestPass", _selfTestPass).Str("selfTest", _selfDetail)
             .Str("patches", DamagePatches.Describe()).Str("mutants", S6Mutants.Describe()).Num("entities", (long)Proxies.EntityCount).Num("sections", (long)Proxies.SectionCount)
             .Num("shapes", (long)Proxies.ShapeCount).Num("pendingPasses", (long)Proxies.Budget.Pending).Num("refreshes", Proxies.Refreshes).Num("teleports", Proxies.Teleports)
             .Bool("character", Character.Active).Str("characterSource", Character.Source).Str("characterId", S6Rows.Hex(Character.CharacterId))
             .Bool("rateRegistered", _rate.Registered).Num("fixedDeltaTime", (double)Time.fixedDeltaTime).Num("steps", _steps)
             .Num("hits", Hits).Num("hitsSection", HitsSection).Num("hitsCharacter", HitsCharacter).Num("hitsPlayer", HitsPlayer).Num("hitsMouth", HitsMouth)
             .Num("hitsFallback", HitsFallback).Num("hitsRadius", HitsRadius).Num("bitesAccepted", BitesAccepted).Num("losBlocked", LosBlocked)
             .Num("recordsBuilt", _sink.Built).Num("recordsWritten", _sink.Written).Num("recordsQueued", _sink.Queued).Num("damageDropped", _sink.Dropped)
             .Num("overflowDepth", (long)_sink.Depth).Num("lastSeq", (long)_sink.Builder.LastSeq).Num("worldEpoch", (long)_helloEpoch)
             .Num("skipGate", _sink.Gate).Num("skipZeroAmount", _sink.ZeroAmount).Num("skipTypeNotForwarded", _sink.TypeNotForwarded).Num("skipRefused", _sink.Refused)
             .Num("skipPlayerNotAtCharacter", _sink.PlayerNotAtCharacter).Num("skipRadiusMutant", _sink.RadiusDropped).Num("noDealerPoint", _sink.NoDealerPoint)
             .Num("skinsApplied", Mirror.State.Skins.Applied).Num("skinStale", Mirror.State.Skins.Stale).Num("skinBad", Mirror.State.Skins.Bad)
             .Str("scene", Scene.Phase).Str("disabled", _disabled.Count == 0 ? "none" : string.Join(",", new List<string>(_disabled).ToArray()));
        }

        // ------------------------------------------------------------------------------------------ teardown

        public void Dispose()
        {
            if (DamagePatches.Host == this) DamagePatches.Host = null;
            try { Scene.Stop("S6 host disposed"); } catch (Exception) { }
            try { _rate.ForceOff(_stepper, "bridge teardown"); } catch (Exception) { }
            try { Proxies.Clear("S6 host disposed"); } catch (Exception) { }
            try { Character.Destroy(Proxies); } catch (Exception) { }
            try { DestroyTestSection(); } catch (Exception) { }
            try
            {
                SkinStore skins = Mirror.State.Skins;
                skins.Changed = null; skins.Removed = null; skins.Cleared = null;
                Mirror.S6CameraSample = null;
            }
            catch (Exception) { }
            if (_stepper != null) _stepper.Host = null;
            if (_timer != null) _timer.Host = null;
            if (_go != null) Object.Destroy(_go);
            _go = null;
            _started = false;
        }

        private void Guard(string feature, Action a)
        {
            if (_disabled.Contains(feature)) return;
            try { a(); }
            catch (Exception e)
            {
                _disabled.Add(feature);
                DeLog.Error("DE.S6 FEATURE " + feature + " DISABLED after " + e.GetType().Name + ": " + e.Message + " (the rest of the bridge keeps running)\n" + e);
            }
        }
    }
}
