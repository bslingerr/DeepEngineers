using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S11 "Creatures and hazards", Subnautica side (ROADMAP §14 "### S11"; CONTRACT §5.4 "S11 conventions"). Created by the
 /// bridge after S6 and S7p when <c>DEEPENGINEERS_S11</c> is not off; owns:
 /// <list type="bullet">
 /// <item>A: the live weapon table (<see cref="S11WeaponTable"/>) S7p's creature damage goes through (a handheld weapon never
 /// kills a leviathan; the creature's tier from its own LiveMixin.maxHealth), the explosion yield and the picture's chain
 /// filter for stacked warheads;</item>
 /// <item>B: the proxies inside the creature avoidance scans (Harmony, <see cref="S11Patches"/>);</item>
 /// <item>C: the grabs (<see cref="S11Grabs"/>) and the bite pushes;</item>
 /// <item>D: every Subnautica damage source on the SE character (the hazard types S6 does not forward, the temperature and
 /// lava samplers at the character, radiation, acid, geysers, gas pods) and the crabsquid EMP on grids;</item>
 /// <item>E: oxygen from bubbles, air pockets and bases, and first-aid heals.</item>
 /// </list>
 /// Every record goes through S6's <see cref="DamageSink"/> (one dedupSeq numbering, one queue, one ring: exactly once on
 /// SE). Health stays SE's: nothing here changes a Subnautica health of anything that represents SE (sections and the
 /// ghost are shielded). Main thread only. Rows <c>s11.*</c>; dev command <c>de.s11</c>.
 /// </summary>
    internal sealed class S11Host : IDisposable
    {
        public const string Cmd = "de.s11";
        public const int MaxRowsPerSecond = 40;

        private readonly MonoBehaviour _bridge;
        private readonly S6Host _s6;
        private readonly MirrorConsumer _m;
        public readonly S11WeaponTable Weapons = new S11WeaponTable();
        /// s11 scale</c>.</summary>
        public readonly S11ScaleTable Scales = new S11ScaleTable();
        private double _nextSweepS;
        public long Scaled, AggroRows, AvoidScans;
        public readonly S11Grabs Grabs;
        public readonly S11Scene Scene;
        /// <summary> (format rage.hold): the live rage table, retuned with <c>de.s11 {action: rage,...}</c>.</summary>
        public readonly S11RageTable Rage = new S11RageTable();
        /// <summary> (format missile.hit): SE's mirrored missiles against the creatures.</summary>
        public readonly S11Missiles Missiles;
        private readonly S11Accumulator _area = new S11Accumulator();
        private readonly S11OnceSet _empOnce = new S11OnceSet();
        private readonly S11Fresh _radiation = new S11Fresh();
        private readonly List<S11Blast> _esc = new List<S11Blast>();
        private readonly HashSet<string> _faults = new HashSet<string>(StringComparer.Ordinal);
        private bool _started, _disposed;
        private double _nextHeatS, _nextLavaS, _nextBaseS, _nextBaseScanS;
        private ulong _nextRowMs;
        private int _rowsThisSecond;
        private long _hazardEvents;
        private BaseRoot[] _bases = new BaseRoot[0];

        // counters (s11.status)
        public long DangerHits, DangerFloored, DangerKills, ChainPlayed, ChainAbsorbed, ChainEscalated, Hazards, HazardRecords, HazardSkipped,
                    Heals, OxygenRecords, OxygenSeconds, EmpRecords, Pushes, Records, NotEmitted, Faults, AvoidHits, AvoidTerrainHits;
        public string LastHazard = "", LastDanger = "";

        // pending danger row (Danger -> AfterDanger)
        private LiveMixin _dLm;
        private float _dBase, _dApplied, _dHealth, _dMax;
        private int _dCls, _dTier, _dFloor;
        private string _dWhy;

        public S11Host(MonoBehaviour bridge, S6Host s6, MirrorConsumer mirror)
        {
            _bridge = bridge;
            _s6 = s6;
            _m = mirror;
            Grabs = new S11Grabs(this, s6, mirror);
            Scene = new S11Scene(this, s6, mirror);
            Missiles = new S11Missiles(this, mirror);
        }

        public S6Host S6 { get { return _s6; } }
        public bool HasS6 { get { return _s6 != null; } }
        private static double NowS { get { return Time.realtimeSinceStartup; } }

        public void Start()
        {
            if (_started) return;
            _started = true;
            S11Patches.Install();
            S11Hooks.Host = this;
            DeLog.Info("DE.S11 start: conventions '" + S11Conventions.Header + "' hash 0x" + S11Conventions.Hash().ToString("x16", CultureInfo.InvariantCulture) +
                       (S11Conventions.Hash() == S11Conventions.PinnedHash ? " (pinned)" : " MISMATCH (pinned 0x" + S11Conventions.PinnedHash.ToString("x16", CultureInfo.InvariantCulture) + ")") +
                       " s6=" + (_s6 != null ? "on" : "OFF (no proxies: grabs, hazards, air and EMP need S6)") + " patches " + S11Patches.Describe() +
                       " weapons " + Weapons.Describe() + " mutants=" + S11Mutants.Describe() + " (" + S11Switches.Var + "=on)");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Grabs.ReleaseAll("dispose"); } catch (Exception) { }
            try { Scene.Teardown(); } catch (Exception) { }
            S11DamageFx.Teardown(); // pass 5: Subnautica's damage FX cleared
            if (ReferenceEquals(S11Hooks.Host, this)) S11Hooks.Host = null;
        }

        public void OnNewSePeer()
        {
            try { Grabs.ReleaseAll("new SE peer"); } catch (Exception e) { Fault("peer", e); }
        }

        internal void Fault(string where, Exception e)
        {
            Faults++;
            if (_faults.Add(where)) DeLog.Warn("DE.S11 " + where + " error " + e.GetType().Name + ": " + e.Message + " (logged once; the feature keeps running)");
        }

        // ============================================================================================ per frame

        public void Tick(ulong nowMs)
        {
            if (_disposed || !_started) return;
            double now = NowS;
            if (nowMs >= _nextRowMs)
            {
                _nextRowMs = nowMs + 1000;
                _rowsThisSecond = 0;
                StatusRow();
            }
            if (Player.main == null) return;
            try { Grabs.Tick(now); } catch (Exception e) { Fault("grabs", e); }
            try { Missiles.Tick(now); } catch (Exception e) { Fault("missiles", e); }
            if (now >= _nextSurfaceS) { _nextSurfaceS = now + 0.1; try { SurfaceSample(now); } catch (Exception e) { Fault("surface", e); } }
            if (now >= _nextSweepS) { _nextSweepS = now + 2.0; try { Sweep(); } catch (Exception e) { Fault("sweep", e); } }
            try { Scene.Tick(now); } catch (Exception e) { Fault("scene", e); }
            S11DamageFx.Frame((float)now); // pass 5: Subnautica's damage FX found, cleared when its picture goes (guarded)
            if (now >= _nextDebrisS) { _nextDebrisS = now + 1.0; try { RefreshDebris(); } catch (Exception e) { Fault("debris", e); } }
            if (_s6 == null) return;
            try { Samplers(now); } catch (Exception e) { Fault("samplers", e); }
            try
            {
                double s = _area.Take(now, S11Conventions.Oxygen.MaxRecordS);
                if (s > 0) EmitOxygen(s, S11Conventions.OxygenSource.Area, CharacterPos(), "area");
            }
            catch (Exception e) { Fault("oxygen.area", e); }
        }

        private bool RowAllowed() { return ++_rowsThisSecond <= MaxRowsPerSecond; }
        public bool RowAllowedPublic() { return RowAllowed(); }

        private void StatusRow()
        {
            JsonOut j = S5Rows.Begin("s11.status");
            j.Num("dangerHits", DangerHits).Num("dangerFloored", DangerFloored).Num("dangerKills", DangerKills).Num("chainPlayed", ChainPlayed)
             .Num("chainAbsorbed", ChainAbsorbed).Num("chainEscalated", ChainEscalated).Num("hazards", Hazards).Num("hazardRecords", HazardRecords)
             .Num("hazardSkipped", HazardSkipped).Num("heals", Heals).Num("oxygenRecords", OxygenRecords).Num("empRecords", EmpRecords).Num("pushes", Pushes)
             .Num("grabs", Grabs.Started).Num("grabsReleased", Grabs.Released).Num("grabsHeld", (long)Grabs.Held).Num("grabRefusedSize", Grabs.RefusedSize)
             .Num("records", Records).Num("notEmitted", NotEmitted).Num("avoidHits", AvoidHits).Num("avoidTerrainHits", AvoidTerrainHits).Num("avoidScans", AvoidScans).Num("faults", Faults)
             .Str("weapons", Weapons.Overrides > 0 ? Weapons.Describe() : "default").Str("mutants", S11Mutants.Describe()).Str("patches", S11Patches.Describe())
             .Str("dmgFx", S11DamageFx.Describe()).Num("dmgFxFlags", (long)S11DamageFx.Flags()).Num("debrisRoots", (long)_debris.Count).Num("debrisEcoOff", DebrisEcoOff)
             .Num("debrisBitesRefused", S11Patches.DebrisBitesRefused).Num("grabRefusedDebris", Grabs.RefusedDebris)
             .Num("surfaceReleases", Grabs.SurfaceReleases).Num("rages", Grabs.Rages).Num("rageReleases", Grabs.RageReleases).Num("flees", Grabs.Flees)
             .Num("followClamps", FollowClamps).Num("guardStops", GuardStops).Num("missilesSeen", Missiles.Seen).Num("missileHits", Missiles.Hits).Num("missileRecords", Missiles.Records)
             .Num("missileAttributed", Missiles.Attributed).Num("missileBlocked", Missiles.Blocked).Num("ignoreRefreshes", Grabs.IgnoreRefreshes);
            S5Rows.End(j);
        }

        // ============================================================================================ scale and aggression

        private readonly HashSet<int> _scaledSeen = new HashSet<int>();

        /// <summary>

 /// absolute, so a creature saved scaled and loaded again never compounds; Creature.Start resets a non-growing
 /// creature to one, so the sweep re-asserts. A growing or aquarium creature (GrowMixin,
 /// WaterParkCreature) is never touched. Unity scales its colliders (body, mouth trigger), its child transforms
 /// (seamothAttachPoint, exosuitAttachPoint) and the animated skeleton with the root; LiveMixin health is data and
 /// stays. Then one s11.aggro row: the creatures within 80 m of the camera and what their LastTarget is (the
 /// character capsule, the ghost Player, a grid section, other), for the "nothing attacked me outside a ship" check.
 /// </summary>
        private void Sweep()
        {
            Camera cam = MainCamera.camera;
            Vector3 c = cam != null ? cam.transform.position : (Player.main != null ? Player.main.transform.position : Vector3.zero);
            GameObject ghost = Player.main != null ? Player.main.gameObject : null;
            ProxyMarker capsule = _s6 != null ? _s6.Character.Marker : null;
            int near = 0, onChar = 0, onGhost = 0, onGrid = 0, other = 0, aggressive = 0;
            foreach (Creature cr in UnityEngine.Object.FindObjectsOfType<Creature>())
            {
                if (cr == null) continue;
                GameObject go = cr.gameObject;
                int tech;
                try { tech = (int)CraftData.GetTechType(go); } catch (Exception) { continue; }
                if (Scales.Has(tech) && go.GetComponent<GrowMixin>() == null && go.GetComponent<WaterParkCreature>() == null)
                {
                    float f = (float)Scales.Factor(tech);
                    Vector3 want = new Vector3(f, f, f);
                    if ((go.transform.localScale - want).sqrMagnitude > 1e-6f)
                    {
                        go.transform.localScale = want;
                        if (_scaledSeen.Add(go.GetInstanceID()))
                        {
                            Scaled++;
                            JsonOut j = S5Rows.Begin("s11.scale");
                            // pass 5: the live bite beside the pinned bite multiplier
                            MeleeAttack ma = go.GetComponentInChildren<MeleeAttack>();
                            j.Str("creature", ((TechType)tech).ToString()).Num("instance", (long)go.GetInstanceID()).Num("factor", (double)f).Num("health", cr.liveMixin != null ? (double)cr.liveMixin.health : double.NaN)
                             .Num("biteSn", ma != null ? (double)ma.biteDamage : double.NaN).Num("biteMult", Scales.BiteMult(tech));
                            S5Rows.End(j);
                        }
                    }
                }
                if ((cr.transform.position - c).sqrMagnitude > 80f * 80f) continue;
                near++;
                if (cr.Aggression != null && cr.Aggression.Value >= 0.5f) aggressive++;
                LastTarget lt = go.GetComponent<LastTarget>();
                GameObject t = lt != null ? lt.target : null;
                if (t == null) continue;
                ProxyMarker pm = S11Hooks.MarkerOf(t);
                if (pm != null && ReferenceEquals(pm, capsule)) onChar++;
                else if (ghost != null && ReferenceEquals(t, ghost)) onGhost++;
                else if (pm != null) onGrid++;
                else other++;
            }
            if (_scaledSeen.Count > 4096) _scaledSeen.Clear();
            if (near == 0) return;
            AggroRows++;
            JsonOut a = S5Rows.Begin("s11.aggro");
            a.Num("near", (long)near).Num("aggressive", (long)aggressive).Num("targetCharacter", (long)onChar).Num("targetGhost", (long)onGhost)
             .Num("targetGrid", (long)onGrid).Num("targetOther", (long)other).Bool("capsule", capsule != null && _s6.Character.Active)
             .Bool("ghostAttackable", Player.main != null && Player.main.CanBeAttacked());
            S5Rows.End(a);
        }

        // ============================================================================================ A: danger model

        public float Danger(LiveMixin lm, float hp, int cls, string why)
        {
            float maxH = lm.maxHealth, health = lm.health;
            int tier = S11Danger.TierOf(maxH);
            int mult = Weapons.Mult(cls, tier);
            int floor = S11Mutants.IsOn(S11Mutants.NoFloor) ? 0 : Weapons.Floor(cls, tier);
            double applied = S11Danger.Applied(hp, health, maxH, mult, floor);
            DangerHits++;
            if (applied < hp * (mult / 1000.0) - 1e-6) DangerFloored++;
            KeyValuePair<ulong, double> att;
            if (_attributed.TryGetValue(lm.GetInstanceID(), out att))
            {
                _attributed.Remove(lm.GetInstanceID());
                if (NowS - att.Value < 0.05) why = (why ?? "") + " missile=" + att.Key.ToString(CultureInfo.InvariantCulture); // format missile.hit: this blast's struck creature
            }
            _dLm = lm; _dBase = hp; _dApplied = (float)applied; _dHealth = health; _dMax = maxH; _dCls = cls; _dTier = tier; _dFloor = floor; _dWhy = why;
            if (!(applied > 0)) { DangerRow(lm, false, true); _dLm = null; }
            return (float)applied;
        }

        public void AfterDanger(LiveMixin lm, bool killed)
        {
            if (!ReferenceEquals(lm, _dLm)) return;
            if (killed) DangerKills++;
            DangerRow(lm, killed, false);
            _dLm = null;
            if (!killed) Grabs.OnHolderHit(lm, _dApplied, _dWhy); // format rage.hold: the holder's book (SE weapons only: this is S7p's path)
        }

        // ============================================================================================: blast distance, shots from inside

        private readonly Dictionary<int, KeyValuePair<ulong, double>> _attributed = new Dictionary<int, KeyValuePair<ulong, double>>();
        private readonly Collider[] _insideCols = new Collider[16];

        /// <summary>
 /// format blast.damage: d = the distance to the nearest point of the creature's own colliders (Collider.ClosestPoint,
 /// UnityEngine.PhysicsModule), 0 for the creature a missile struck (S11Missiles.Attribute); the transform's distance
 /// without a collider or under S11.M15.
 /// </summary>
        public float BlastDistance(Vector3 centre, LiveMixin lm, float transformDistance)
        {
            if (lm == null || S11Mutants.IsOn(S11Mutants.OldHitTest)) return transformDistance;
            ulong mid = Missiles.Attribute(lm, centre, NowS);
            if (mid != 0) { _attributed[lm.GetInstanceID()] = new KeyValuePair<ulong, double>(mid, NowS); return 0f; }
            float best = float.PositiveInfinity;
            foreach (Collider c in lm.GetComponentsInChildren<Collider>(false))
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                float d = (ClosestPoint(c, centre) - centre).magnitude;
                if (d < best) best = d;
            }
            return float.IsInfinity(best) ? transformDistance : best;
        }

        /// <summary>
 /// Review 11: Collider.ClosestPoint is defined for box, sphere, capsule and convex mesh colliders only (a non-convex
 /// MeshCollider returns the query point: distance 0); those are measured by their bounds (Bounds.ClosestPoint).
 /// </summary>
        public static Vector3 ClosestPoint(Collider c, Vector3 p)
        {
            var mc = c as MeshCollider;
            if (mc != null && !mc.convex) return c.bounds.ClosestPoint(p);
            return c.ClosestPoint(p);
        }

        /// <summary>The world bounds of a creature's own solid colliders (empty: its position, zero size).</summary>
        public static Bounds BodyBounds(Collider[] cols, Transform t)
        {
            bool any = false;
            Bounds b = default(Bounds);
            if (cols != null)
                foreach (Collider c in cols)
                {
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
            return any ? b : new Bounds(t != null ? t.position : Vector3.zero, Vector3.zero);
        }

        /// <summary>
 /// A live creature the shot's origin lies in or within 0.75 m of (a muzzle in a body cannot miss it; non-convex mesh
 /// colliders give no inside test), or the creature holding the shooter's grid (its jaw within the grid's reach): it takes
 /// the shot (S11.M15: none).
 /// </summary>
        public LiveMixin CreatureAround(Vector3 origin) { return CreatureAround(origin, Vector3.zero); }

        public LiveMixin CreatureAround(Vector3 origin, Vector3 dir)
        {
            if (S11Mutants.IsOn(S11Mutants.OldHitTest)) return null;
            LiveMixin holder = Grabs.HolderAround(origin, dir);
            if (holder != null) return holder;
            int n = Physics.OverlapSphereNonAlloc(origin, 0.75f, _insideCols, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider c = _insideCols[i];
                if (c == null) continue;
                LiveMixin lm = Utils.FindAncestorWithComponent<LiveMixin>(c.gameObject);
                if (lm == null || !lm.IsAlive() || lm.gameObject.GetComponent<Creature>() == null) continue;
                if (Player.main != null && ReferenceEquals(lm, Player.main.liveMixin)) continue;
                if ((ClosestPoint(c, origin) - origin).sqrMagnitude <= 0.75f * 0.75f) return lm;
            }
            return null;
        }

        // ============================================================================================: the surface rule

        private sealed class SwimOwner { public Creature C; public LastTarget T; public Collider[] Cols; public double HalfH = double.NaN, NextBoundsS; public Rigidbody Rb; }
        private readonly Dictionary<int, SwimOwner> _owners = new Dictionary<int, SwimOwner>();
        public long GuardStops;

        private SwimOwner OwnerOf(Creature c)
        {
            SwimOwner o;
            int id = c.GetInstanceID();
            if (!_owners.TryGetValue(id, out o) || o.C == null)
            {
                if (_owners.Count > 4096) _owners.Clear();
                o = new SwimOwner { C = c, T = c.GetComponent<LastTarget>(), Cols = c.GetComponentsInChildren<Collider>(false), Rb = c.GetComponent<Rigidbody>() };
                _owners[id] = o;
            }
            return o;
        }

        /// <summary>The head of a holding-class creature (its attach point, the jaw), else its root.</summary>
        public static Transform HeadOf(Creature c)
        {
            var rl = c as ReaperLeviathan;
            var sd = c as SeaDragon;
            Transform j = rl != null ? rl.seamothAttachPoint : (sd != null ? sd.exosuitAttachPoint : null);
            return j != null ? j : c.transform;
        }

        /// <summary>
 /// A creature's girth (half its thinnest collider-bounds extent, refreshed every second: about 5 m for a x3 reaper) and
 /// its top: the higher of its root and head plus that girth. In-game 144924: the bounds' top of a 165 m reaper pitched
 /// a few degrees stood 13 m out of the water while its body was under it; a tail tip is not the creature leaving the water.
 /// </summary>
        public double HalfHeight(Creature c, out double top)
        {
            SwimOwner o = OwnerOf(c);
            double now = NowS;
            if (double.IsNaN(o.HalfH) || now >= o.NextBoundsS)
            {
                Bounds b = BodyBounds(o.Cols, c.transform);
                o.HalfH = Math.Min(b.extents.x, Math.Min(b.extents.y, b.extents.z));
                o.NextBoundsS = now + 1.0;
            }
            top = Math.Max(c.transform.position.y, HeadOf(c).position.y) + o.HalfH;
            return o.HalfH;
        }

        private readonly Dictionary<int, Creature> _locoOwners = new Dictionary<int, Creature>();

        /// <summary>
 /// format surface.release, at every physics step after the creature's own push (Locomotion.ManagedFixedUpdate postfix): a
 /// creature that holds, flees or followed a grid within 2 s, whose top is within MarginCm of the surface, loses its upward
 /// speed. S11.M14: none.
 /// </summary>
        public void FixedGuard(Locomotion l)
        {
            if (S11Mutants.IsOn(S11Mutants.NoSurface)) return;
            Rigidbody rb = l.useRigidbody;
            if (rb == null || rb.isKinematic || !(rb.velocity.y > 0f)) return;
            Creature c;
            int id = l.GetInstanceID();
            if (!_locoOwners.TryGetValue(id, out c))
            {
                if (_locoOwners.Count > 4096) _locoOwners.Clear();
                c = l.GetComponent<Creature>() ?? l.GetComponentInParent<Creature>();
                _locoOwners[id] = c;
            }
            if (c == null) return;
            KeyValuePair<Creature, double> f;
            bool rule = Grabs.IsHolding(c) || Grabs.IsFleeing(c) || (_following.TryGetValue(c.GetInstanceID(), out f) && NowS - f.Value <= 2.0);
            if (!rule) return;
            double top;
            HalfHeight(c, out top);
            if (!S11SurfaceRules.TopReleases(-top)) return;
            Vector3 v = rb.velocity;
            rb.velocity = new Vector3(v.x, 0f, v.z);
            GuardStops++;
        }
        private readonly Dictionary<int, SwimOwner> _swimOwners = new Dictionary<int, SwimOwner>();
        private readonly Dictionary<int, KeyValuePair<Creature, double>> _following = new Dictionary<int, KeyValuePair<Creature, double>>();
        private double _nextSurfaceS, _nextSurfaceRowS;
        private long _sSamples, _sHolding, _sFollowing, _sAbove, _sAboveRows;
        private double _sMaxHolding = double.NegativeInfinity, _sMaxFollowing = double.NegativeInfinity, _sMaxMouth = double.NegativeInfinity;
        public long FollowClamps, FollowCalls;
        private readonly List<int> _followGone = new List<int>();

        /// <summary>
 /// format surface.release: a creature that holds a grid, flees
 /// one, or hunts an SE grid section swims to targets no higher than −FollowDepthCm.
 /// Its own surfacing is never driven. S11.M14: no clamp.
 /// </summary>
        public void FollowClamp(Component sf, ref Vector3 target)
        {
            int sid = sf.GetInstanceID();
            SwimOwner so;
            if (!_swimOwners.TryGetValue(sid, out so) || so.C == null)
            {
                if (_swimOwners.Count > 4096) _swimOwners.Clear();
                Creature cr = sf.GetComponent<Creature>() ?? sf.GetComponentInParent<Creature>();
                so = new SwimOwner { C = cr };
                _swimOwners[sid] = so;
            }
            Creature c = so.C;
            if (c == null) return;
            SwimOwner o = OwnerOf(c);
            bool rule = Grabs.IsHolding(c) || Grabs.IsFleeing(c);
            if (!rule && o.T != null)
            {
                GameObject t = o.T.target;
                ProxyMarker pm = t != null ? S11Hooks.MarkerOf(t) : null;
                rule = pm != null && !pm.SelfTest && pm.Kind == DamageTargetKind.GridSection;
            }
            if (!rule) return;
            FollowCalls++;
            _following[c.GetInstanceID()] = new KeyValuePair<Creature, double>(c, NowS);
            if (S11Mutants.IsOn(S11Mutants.NoSurface)) return;
            double top;
            double cap = S11SurfaceRules.FollowY(target.y, HalfHeight(c, out top)); //  its own half height + the margin
            if (target.y > cap) { target.y = (float)cap; FollowClamps++; }
        }

        /// <summary>
 /// S11.C7 (every 0.1 s): the height of every creature that holds or followed a grid in the last 2 s (SN y = the altitude
 /// above the sea sphere); an <c>s11.surface</c> row each second, and one per sample above the surface.
 /// </summary>
        private void SurfaceSample(double now)
        {
            foreach (S11Grabs.HolderInfo h in Grabs.Holders())
            {
                if (h.Creature == null) continue;
                Sample(h.Creature, true, h.Attach, h.GridTop);
            }
            _followGone.Clear();
            foreach (KeyValuePair<int, KeyValuePair<Creature, double>> kv in _following)
            {
                Creature c = kv.Value.Key;
                if (c == null || !c.gameObject.activeInHierarchy || now - kv.Value.Value > 2.0) { _followGone.Add(kv.Key); continue; }
                if (Grabs.IsHolding(c)) continue;
                Sample(c, false, null, double.NaN);
            }
            foreach (int k in _followGone) _following.Remove(k);
            if (now >= _nextSurfaceRowS && _sSamples > 0)
            {
                _nextSurfaceRowS = now + 1.0;
                JsonOut j = S5Rows.Begin("s11.surface");
                j.Str("op", "second").Num("samples", _sSamples).Num("holding", _sHolding).Num("following", _sFollowing).Num("above", _sAbove)
                 .Num("maxYHolding", _sMaxHolding).Num("maxYFollowing", _sMaxFollowing).Num("maxMouthY", _sMaxMouth).Num("clamps", FollowClamps)
                 .Num("surfaceReleases", Grabs.SurfaceReleases).Str("mutants", S11Mutants.Describe());
                S5Rows.End(j);
                _sSamples = _sHolding = _sFollowing = _sAbove = 0;
                _sMaxHolding = _sMaxFollowing = _sMaxMouth = double.NegativeInfinity;
            }
        }

        private void Sample(Creature c, bool holding, Transform mouth, double gridTop)
        {
            //  the top of the creature's bounds, and of the held grid's (its proxies), against the surface
            double top;
            HalfHeight(c, out top);
            double y = double.IsNaN(gridTop) ? top : Math.Max(top, gridTop);
            _sSamples++;
            if (holding) { _sHolding++; if (y > _sMaxHolding) _sMaxHolding = y; if (mouth != null && mouth.position.y > _sMaxMouth) _sMaxMouth = mouth.position.y; }
            else { _sFollowing++; if (y > _sMaxFollowing) _sMaxFollowing = y; }
            if (!(y > 0)) return;
            _sAbove++;
            if (_sAboveRows++ > 2000) return;
            string tech;
            try { tech = CraftData.GetTechType(c.gameObject).ToString(); } catch (Exception) { tech = c.gameObject.name; }
            JsonOut j = S5Rows.Begin("s11.surface");
            j.Str("op", "above").Str("creature", tech).Num("instance", (long)c.GetInstanceID()).Num("y", y).Num("creatureTop", top).Num("gridTop", gridTop).Bool("holding", holding)
             .Num("mouthY", mouth != null ? (double)mouth.position.y : double.NaN).Str("mutants", S11Mutants.Describe());
            S5Rows.End(j);
        }

        private void DangerRow(LiveMixin lm, bool killed, bool floored)
        {
            string name;
            try { name = CraftData.GetTechType(lm.gameObject).ToString(); } catch (Exception) { name = lm.gameObject.name; }
            LastDanger = S11Conventions.WeaponClass.Name(_dCls) + " " + name + " " + _dApplied.ToString("0.#", CultureInfo.InvariantCulture) + "/" + _dBase.ToString("0.#", CultureInfo.InvariantCulture) +
                         " health " + _dHealth.ToString("0.#", CultureInfo.InvariantCulture) + " -> " + lm.health.ToString("0.#", CultureInfo.InvariantCulture) + (killed ? " KILLED" : "");
            if (killed && _dTier == S11Conventions.Tier.Leviathan) DeLog.Info("DE.S11 leviathan KILLED by " + LastDanger);
            if (!RowAllowed()) return;
            JsonOut j = S5Rows.Begin("s11.danger");
            j.Str("creature", name).Num("instance", (long)lm.gameObject.GetInstanceID()).Str("cls", S11Conventions.WeaponClass.Name(_dCls)).Str("tier", S11Conventions.Tier.Name(_dTier))
             .Num("base", (double)_dBase).Num("applied", (double)_dApplied).Num("healthBefore", (double)_dHealth).Num("healthAfter", (double)lm.health)
             .Num("maxHealth", (double)_dMax).Num("floorPermille", (long)_dFloor).Bool("floored", floored || _dApplied < _dBase - 1e-4f).Bool("killed", killed).Str("why", _dWhy ?? "");
            S5Rows.End(j);
        }

        // ============================================================================================ A: explosion chains

        public bool ChainAdmit(S11BlastChain chain, ref S7EventRow r, Vector3 wpos)
        {
            double y = S11BlastRules.YieldOf(r.extra, r.intensity);
            var b = new S11Blast(wpos.x, wpos.y, wpos.z, Math.Max(0.1, r.size), y, 0);
            long now = (long)(NowS * 1000.0);
            bool play = chain.Admit(b, now); // a chain whose window ended cannot absorb it (Admit skips it; the player's ChainClose plays it)
            if (play) ChainPlayed++; else ChainAbsorbed++;
            if (RowAllowed())
            {
                JsonOut j = S5Rows.Begin("s11.chain");
                j.Str("op", play ? "play" : "absorbed").Num("yield", y).Num("radius", (double)r.size).Num("extra", (long)r.extra).Vec("at", wpos.x, wpos.y, wpos.z).Num("open", (long)chain.OpenCount);
                S5Rows.End(j);
            }
            return play;
        }

        public int ChainClose(S11BlastChain chain, List<S7EventRow> into)
        {
            long now = (long)(NowS * 1000.0);
            chain.Close(now, _esc);
            int n = _esc.Count;
            foreach (S11Blast b in _esc)
            {
                into.Add(S11Hooks.EscalatedRow(b));
                ChainEscalated++;
                DeLog.Info("DE.S11 blast chain: " + b.Count + " explosion(s), yield " + b.Yield.ToString("0", CultureInfo.InvariantCulture) + " -> one effect of radius " +
                           b.R.ToString("0.#", CultureInfo.InvariantCulture) + " m at " + b.X.ToString("0.#", CultureInfo.InvariantCulture) + "," + b.Y.ToString("0.#", CultureInfo.InvariantCulture) + "," + b.Z.ToString("0.#", CultureInfo.InvariantCulture));
                JsonOut j = S5Rows.Begin("s11.chain");
                j.Str("op", "escalated").Num("yield", b.Yield).Num("count", (long)b.Count).Num("radius", b.R).Num("sumR3", b.SumR3).Vec("at", (float)b.X, (float)b.Y, (float)b.Z);
                S5Rows.End(j);
            }
            _esc.Clear();
            return n;
        }

        // ============================================================================================ D: hazards

 /// <summary>The S6 route (TypeNotForwarded on the ghost Player or the character section): a hazard of the S11 table → one record.</summary>
        public bool Unforwarded(bool isPlayer, ProxyMarker m, float amount, Vector3 position, int type, GameObject dealer, bool gatePass, bool atChar)
        {
            if (!S11HazardRules.Forwarded(type)) return false;
            if (!isPlayer && (m == null || m.Kind != DamageTargetKind.Character)) return false; // grid sections: crush and heat on hulls are S16's
            Hazards++;
            if (!gatePass) { HazardSkipped++; return true; }
            if (isPlayer && !atChar) { HazardSkipped++; return true; }
            ushort flags = isPlayer ? CombatConventions.C8FlagsExt.GhostPlayer : S11Conventions.Flags.ViaSection;
            if (_samplerDepth > 0) flags |= S11Conventions.Flags.ViaSampler;
            EmitHazard(type, amount, flags, position == default(Vector3) ? CharacterPos() : position, isPlayer ? "player" : "section");
            return true;
        }

        private int _samplerDepth;

        private void EmitHazard(int type, float amount, ushort flags, Vector3 at, string via)
        {
            long ev = ++_hazardEvents;
            if (S11Mutants.IsOn(S11Mutants.NoHazard)) { HazardRow(ev, type, amount, via, null, "mutant"); return; }
            string id;
            S11HazardRow row;
            float k = S11Conventions.TryHazardRow(type, out row) ? row.KPermille / 1000f : 1f;
            bool ok = EmitCharacter(S11Conventions.Unit.HazardHp, amount, type, flags, at, k, out id);
            if (ok) { HazardRecords++; S11DamageFx.Play(type, amount, at); } // pass 5: Subnautica's own damage FX under its picture
            HazardRow(ev, type, amount, via, ok ? id : null, ok ? null : "notEmitted");
            if (ok && S11Mutants.IsOn(S11Mutants.DoubleHazard) && EmitCharacter(S11Conventions.Unit.HazardHp, amount, type, flags, at, k, out id))
                HazardRow(ev, type, amount, via, id, null);
            LastHazard = (type >= 0 && type <= 16 ? ((DamageType)type).ToString() : type.ToString(CultureInfo.InvariantCulture)) + " " + amount.ToString("0.##", CultureInfo.InvariantCulture) + " via " + via;
        }

        private void HazardRow(long ev, int type, float amount, string via, string id, string why)
        {
            if (!RowAllowed()) return;
            JsonOut j = S5Rows.Begin("s11.hazard");
            j.Num("event", ev).Num("type", (long)type).Str("typeName", type >= 0 && type <= 16 ? ((DamageType)type).ToString() : "?").Num("amount", (double)amount).Str("via", via)
             .Str("id", id ?? "").Str("why", why ?? "");
            S5Rows.End(j);
        }

        /// <summary>A sampler's damage on the character section (Subnautica's own TakeDamage, so S6's route makes it a record).</summary>
        private void SectionDamage(float amount, int type, Vector3 at)
        {
            if (_s6 == null) return;
            ProxyMarker cm = _s6.Character.Marker;
            if (cm == null || cm.Live == null || !_s6.Character.Active) return;
            _samplerDepth++;
            try { cm.Live.TakeDamage(amount, at, (DamageType)type, null); }
            finally { _samplerDepth--; }
        }

        private void Samplers(double now)
        {
            CharacterProxy ch = _s6.Character;
            if (!ch.Active || !ch.AtCharacter) return;
            Vector3 p = CharacterPos();
            bool inWater = p.y < 0f;
            // heat: TemperatureDamage.UpdateDamage's rule at the SE character, once a second (the ghost's own is suppressed)
            if (now >= _nextHeatS)
            {
                _nextHeatS = now + 1.0;
                TemperatureDamage td = Player.main != null ? Player.main.GetComponent<TemperatureDamage>() : null;
                WaterTemperatureSimulation sim = WaterTemperatureSimulation.main;
                if (inWater && td != null && sim != null)
                {
                    double hp = S11HazardRules.HeatPerSecond(sim.GetTemperature(p), td.minDamageTemperature, td.baseDamagePerSecond);
                    if (hp > 0) SectionDamage((float)hp, (int)DamageType.Heat, p);
                }
            }
            // lava: TemperatureDamage.ApplyLavaDamage (2.5 every 0.1 s) while the capsule touches lava (MaterialDatabase.IsLava)
            if (now >= _nextLavaS)
            {
                _nextLavaS = now + S11Conventions.Lava.ProbeIntervalMs / 1000.0;
                if (inWater && TouchesLava(p)) SectionDamage(S11Conventions.Lava.HeatMilliHpPerProbe / 1000f, (int)DamageType.Heat, p);
            }
            // air in a powered Subnautica base (Player.CanBreathe: a powered sub, not under water there)
            if (now >= _nextBaseS)
            {
                _nextBaseS = now + 0.25;
                if (now >= _nextBaseScanS) { _nextBaseScanS = now + 30.0; _bases = UnityEngine.Object.FindObjectsOfType<BaseRoot>(); } //  every 30 s
                if (InsideBreathableBase(p)) EmitOxygen(S11Conventions.Oxygen.BaseRefillPerS * 0.25, S11Conventions.OxygenSource.Base, p, "base");
            }
        }

        private readonly RaycastHit[] _lavaHits = new RaycastHit[4];
        private static readonly Vector3[] LavaDirs = { Vector3.down, Vector3.up, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };

        private bool TouchesLava(Vector3 p)
        {
            float reach = S11Conventions.Lava.ReachCm / 100f + 0.9f; // from the capsule's centre: half its height plus the reach
            int mask = Voxeland.GetTerrainLayerMask();
            foreach (Vector3 d in LavaDirs)
            {
                float len = d.y != 0 ? reach : S11Conventions.Lava.ReachCm / 100f + 0.25f;
                int n = Physics.RaycastNonAlloc(p, d, _lavaHits, len, mask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                    if (MaterialDatabase.IsLava(_lavaHits[i].point, _lavaHits[i].normal)) return true;
            }
            return false;
        }

        private bool InsideBreathableBase(Vector3 p)
        {
            foreach (BaseRoot br in _bases)
            {
                if (br == null || !br.isBase) continue;
                Base b = br.GetComponent<Base>();
                if (b == null) continue;
                Int3 cell = b.WorldToGrid(p);
                if (!b.IsCellValid(cell) || b.IsCellEmpty(cell)) continue;
                if (br.powerRelay == null || br.powerRelay.GetPowerStatus() == PowerSystem.Status.Offline) continue;
                if (br.IsUnderwater(p)) continue;
                return true;
            }
            return false;
        }

        public void RadiationSeen(float amount) { _radiation.Seen(amount, NowS); }

        /// <summary>DamagePlayerInRadius.DoDamage prefix: the radiation level Subnautica computed this moment (0 = none).</summary>
        public float RadiationLevel() { return (float)_radiation.Get(NowS); }

        // ============================================================================================ D: patch callbacks

        public LiveMixin AcidTarget(GameObject go)
        {
            ProxyMarker pm = S11Hooks.MarkerOf(go);
            if (pm == null || pm.Kind != DamageTargetKind.Character || pm.Live == null) return null;
            return pm.Live;
        }

        public void OnGeyser(Geyser g, Collider other, float damage)
        {
            ProxyMarker pm = S11Hooks.MarkerOf(other);
            if (pm == null || pm.Kind != DamageTargetKind.Character || pm.Live == null) return;
            pm.Live.TakeDamage(damage * Time.deltaTime, g.transform.position, DamageType.Fire, null); // Geyser.OnTriggerStay's own call, on the section
        }

        public void OnGasPodTick(GasPod pod, HashSet<GameObject> touching, float amount)
        {
            if (touching == null || _s6 == null) return;
            ProxyMarker cm = _s6.Character.Marker;
            if (cm == null || cm.Live == null) return;
            foreach (GameObject go in touching)
            {
                if (go == null) continue;
                ProxyMarker pm = S11Hooks.MarkerOf(go);
                if (!ReferenceEquals(pm, cm)) continue;
                cm.Live.TakeDamage(amount, go.transform.position, DamageType.Poison, null); // GasPod.Update's own call, on the section
                return;
            }
        }

        public void OnEmp(EMPBlast blast, Collider c)
        {
            if (c == null || c.attachedRigidbody == null) return;
            ProxyMarker pm = c.attachedRigidbody.GetComponent<ProxyMarker>();
            if (pm == null || pm.Kind != DamageTargetKind.GridSection || pm.SelfTest) return;
            ulong root = pm.RootEntityId != 0 ? pm.RootEntityId : pm.EntityId;
            if (!_empOnce.Add(blast.GetInstanceID(), root)) return;
            double s = S11EmpRules.Seconds(blast.disableElectronicsTime);
            EmpOn(pm, s, c.ClosestPoint(blast.transform.position), (uint)blast.GetInstanceID(), null);
        }

        /// <summary>One EMP record on a grid section: the crabsquid blast's path and (pass 4) the scene's, so S11.M9 holds for both.</summary>
        public bool EmpOn(ProxyMarker pm, double s, Vector3 at, uint key, string via)
        {
            if (S11Mutants.IsOn(S11Mutants.NoEmp)) { EmpRow(pm, s, null, "mutant"); return false; }
            string id;
            bool ok = EmitGrid(S11Conventions.Unit.EmpS, pm, (float)s, 0, 0, at, Vector3.zero, Vector3.zero, (int)TechType.CrabSquid, key, out id);
            if (ok) EmpRecords++;
            EmpRow(pm, s, ok ? id : null, ok ? via : "notEmitted");
            return ok;
        }

        private void EmpRow(ProxyMarker pm, double s, string id, string why)
        {
            DeLog.Info("DE.S11 EMP on grid " + S6Rows.Hex(pm.RootEntityId) + ": " + s.ToString("0.#", CultureInfo.InvariantCulture) + " s of power loss" + (id != null ? " (record " + id + ")" : " NOT sent (" + why + ")"));
            JsonOut j = S5Rows.Begin("s11.emp");
            j.Str("root", S6Rows.Hex(pm.RootEntityId)).Str("entity", S6Rows.Hex(pm.EntityId)).Num("seconds", s).Str("id", id ?? "").Str("why", why ?? "");
            S5Rows.End(j);
        }

        // ============================================================================================ E: oxygen and heal

 /// <summary>Bubble.OnCollisionEnter on the SE character: the bubble's oxygen goes to SE, the bubble pops (true = handled).</summary>
        public bool OnBubble(Bubble b, Collision c)
        {
            if (_s6 == null || c == null) return false;
            ProxyMarker pm = c.gameObject != null ? c.gameObject.GetComponent<ProxyMarker>() : null;
            if (pm == null && c.collider != null) pm = S11Hooks.MarkerOf(c.collider);
            if (pm == null || pm.Kind != DamageTargetKind.Character) return false;
            EmitOxygen(b.oxygenSeconds, S11Conventions.OxygenSource.Bubble, b.transform.position, "bubble");
            return true;
        }

        public void OnOxygenArea(OxygenArea a, Collider other)
        {
            if (_s6 == null) return;
            ProxyMarker pm = S11Hooks.MarkerOf(other);
            if (pm == null || pm.Kind != DamageTargetKind.Character) return;
            _area.Add(a.oxygenPerSecond * Time.deltaTime);
        }

        /// <summary>The <c>air</c> scene: one oxygen record of a source through the same path as the game's sources.</summary>
        public void SceneOxygen(double seconds, int source, Vector3 at) { EmitOxygen(seconds, source, at, "scene"); }

        private void EmitOxygen(double seconds, int source, Vector3 at, string via)
        {
            if (!(seconds > 0)) return;
            if (S11Mutants.IsOn(S11Mutants.NoOxygen)) { OxygenRow(seconds, source, null, "mutant"); return; }
            string id;
            bool ok = EmitCharacter(S11Conventions.Unit.OxygenS, (float)Math.Min(seconds, S11Conventions.Oxygen.MaxRecordS), source, 0, at, 1f / S11Conventions.Oxygen.TankS, out id);
            if (ok) { OxygenRecords++; OxygenSeconds += (long)Math.Round(seconds); }
            OxygenRow(seconds, source, ok ? id : null, ok ? null : "notEmitted");
        }

        private void OxygenRow(double seconds, int source, string id, string why)
        {
            if (!RowAllowed()) return;
            JsonOut j = S5Rows.Begin("s11.oxygen");
            j.Str("source", S11Conventions.OxygenSource.Name(source)).Num("seconds", seconds).Str("id", id ?? "").Str("why", why ?? "");
            S5Rows.End(j);
        }

        /// <summary>LiveMixin.AddHealth on the ghost Player (Subnautica's first-aid kit): the heal goes to SE's health.</summary>
        public void OnHeal(float amount)
        {
            if (_s6 == null || !(amount > 0)) return;
            if (!_s6.PlayerAtCharacter) return;
            Heals++;
            string id = null;
            bool ok = false;
            if (!S11Mutants.IsOn(S11Mutants.NoHeal))
                ok = EmitCharacter(S11Conventions.Unit.HealHp, Math.Min(amount, S11Conventions.Heal.MaxRecordHp), 0, CombatConventions.C8FlagsExt.GhostPlayer, CharacterPos(), S11Conventions.Heal.KPermille / 1000f, out id);
            JsonOut j = S5Rows.Begin("s11.heal");
            j.Num("amount", (double)amount).Str("id", id ?? "").Str("why", ok ? "" : S11Mutants.IsOn(S11Mutants.NoHeal) ? "mutant" : "notEmitted");
            S5Rows.End(j);
        }

        // ============================================================================================ C: pushes

        public void AfterS6Hit(DamageRecord r, GameObject dealer, ProxyMarker m)
        {
            if (dealer == null || r.targetKind != DamageTargetKind.GridSection || r.snDamageType != CombatConventions.SnDamageForward.Normal) return;
            Creature c = dealer.GetComponentInParent<Creature>();
            if (c == null) return;
            string tech;
            try { tech = CraftData.GetTechType(c.gameObject).ToString(); } catch (Exception) { return; }
            S11PushRow row;
            if (!S11Conventions.TryPushRow(tech, out row)) return;
            ulong root = m.RootEntityId != 0 ? m.RootEntityId : m.EntityId;
            if (Grabs.Holds(c, root)) return; // its own hold damage
            if (IsDebris(root)) return; // pass 6: a remnant is never pushed
            Rigidbody rb = c.GetComponent<Rigidbody>();
            if (rb == null) return;
            var dir = new Vector3(r.attackDirSn.x, r.attackDirSn.y, r.attackDirSn.z);
            if (dir.sqrMagnitude < 0.25f) dir = (new Vector3(r.snWorldHit.x, r.snWorldHit.y, r.snWorldHit.z) - c.transform.position);
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();
            double closing = Vector3.Dot(rb.velocity, dir);
            double mass = S11PushRules.EffectiveMass(rb.mass, Scales.Factor(r.dealerTechType)); // a scaled body pushes with mass x scale^3
            double j = S11PushRules.Impulse(row, mass, Math.Max(closing, 1.0)); // a bite at rest still shoves: at least 1 m/s
            if (!(j > 0)) return;
            string id;
            bool ok = EmitGrid(S11Conventions.Unit.Push, m, (float)j, 0, 0, new Vector3(r.snWorldHit.x, r.snWorldHit.y, r.snWorldHit.z), dir, Vector3.zero, r.dealerTechType, (uint)c.GetInstanceID(), out id);
            if (ok) Pushes++;
            JsonOut jo = S5Rows.Begin("s11.push");
            jo.Str("creature", tech).Str("root", S6Rows.Hex(root)).Num("impulse", j).Num("mass", (double)rb.mass).Num("effectiveMass", mass).Num("closing", closing).Str("id", id ?? "").Str("bite", DamageRules.HitId(r.snGeneration, r.dedupSeq));
            S5Rows.End(jo);
        }

        // ============================================================================================ emission

        public Vector3 CharacterPos()
        {
            if (_s6 != null && _s6.Character.Marker != null) return _s6.Character.Marker.transform.position;
            return Player.main != null ? Player.main.transform.position : Vector3.zero;
        }

        /// <summary>One S11 record for the SE character (kind 2). False when there is no character, the Freedom gate is shut, or capture mode.</summary>
        internal bool EmitCharacter(uint unit, float amount, int type, ushort flags, Vector3 at, float kExpected, out string id)
        {
            id = null;
            if (_s6 == null) { NotEmitted++; return false; }
            CharacterProxy ch = _s6.Character;
            if (ch.CharacterId == 0 || !ch.Present || ch.Dead) { NotEmitted++; return false; }
            if (!_s6.GatePass) { NotEmitted++; return false; }
            var r = default(DamageRecord);
            r.unit = unit;
            r.amount = amount;
            r.seScaleExpected = kExpected;
            r.snDamageType = (ushort)type;
            r.flags = flags;
            r.targetKind = DamageTargetKind.Character;
            r.sectionId = 0;
            r.targetEntityId = r.rootEntityId = ch.CharacterId;
            r.snapshotTick = ch.Tick;
            Vec3 l = ch.HasTarget ? HitFrames.CharacterLocal(ch.O, ch.Q, ch.S, ProxySections.D(at)) : Vec3.Zero;
            r.localHit = new F32x3((float)l.X, (float)l.Y, (float)l.Z);
            r.snWorldHit = new F32x3(at.x, at.y, at.z);
            r.snTimeSec = Time.realtimeSinceStartup;
            return Write(ref r, out id);
        }

        /// <summary>: one MissileHit record (kind 1, target = root = an SE entity that is not a mirrored grid: the missile; c8.s11missile).</summary>
        internal bool EmitEntity(uint unit, ulong entity, float amount, int type, Vector3 at, Vector3 dir, int dealerTech, uint dealerInst, out string id)
        {
            id = null;
            if (_s6 == null || entity == 0) { NotEmitted++; return false; }
            if (!_s6.GatePass) { NotEmitted++; return false; }
            var r = default(DamageRecord);
            r.unit = unit;
            r.amount = amount;
            r.seScaleExpected = 1f;
            r.snDamageType = (ushort)type;
            r.targetKind = DamageTargetKind.GridSection;
            r.sectionId = 0;
            r.targetEntityId = r.rootEntityId = entity;
            r.snWorldHit = new F32x3(at.x, at.y, at.z);
            r.attackDirSn = new F32x3(dir.x, dir.y, dir.z);
            r.dealerTechType = dealerTech;
            r.dealerInstanceId = dealerInst;
            r.snTimeSec = Time.realtimeSinceStartup;
            return Write(ref r, out id);
        }

        /// <summary>One S11 record for a grid (kind 1): the section's entity, root and id; <paramref name="dir"/>, <paramref name="up"/> per unit.</summary>
        internal bool EmitGrid(uint unit, ProxyMarker m, float amount, int type, ushort flags, Vector3 at, Vector3 dir, Vector3 up, int dealerTech, uint dealerInst, out string id)
        {
            id = null;
            if (_s6 == null || m == null) { NotEmitted++; return false; }
            if (!_s6.GatePass) { NotEmitted++; return false; }
            var r = default(DamageRecord);
            r.unit = unit;
            r.amount = amount;
            r.seScaleExpected = 1f;
            r.snDamageType = (ushort)type;
            r.flags = flags;
            r.targetKind = DamageTargetKind.GridSection;
            r.sectionId = m.SectionId;
            ulong root = m.RootEntityId != 0 ? m.RootEntityId : m.EntityId;
            bool rootTarget = unit == S11Conventions.Unit.Grab || unit == S11Conventions.Unit.Push || unit == S11Conventions.Unit.EmpS;
            r.targetEntityId = rootTarget ? root : m.EntityId;
            r.rootEntityId = root;
            r.snapshotTick = m.StepTick;
            r.snWorldHit = new F32x3(at.x, at.y, at.z);
            r.attackDirSn = new F32x3(dir.x, dir.y, dir.z);
            r.localHit = new F32x3(up.x, up.y, up.z);
            r.dealerTechType = dealerTech;
            r.dealerInstanceId = dealerInst;
            r.snTimeSec = Time.realtimeSinceStartup;
            return Write(ref r, out id);
        }

        private bool Write(ref DamageRecord r, out string id)
        {
            id = null;
            bool queued;
            if (!_s6.Sink.EmitPrepared(ref r, _s6.HelloEpoch, out queued)) { NotEmitted++; return false; }
            Records++;
            id = DamageRules.HitId(r.snGeneration, r.dedupSeq);
            return true;
        }

        // ============================================================================================ pass 6: debris

        private double _nextDebrisS;
        private HashSet<ulong> _debris = new HashSet<ulong>(), _debrisNext = new HashSet<ulong>();
        private readonly Dictionary<ulong, bool> _debrisSeen = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, List<ProxySection>> _byRoot = new Dictionary<ulong, List<ProxySection>>();
        private readonly List<SkinSectionData> _datas = new List<SkinSectionData>();
        public long DebrisRows, DebrisEcoOff;

        /// <summary>

 /// estimates fewer than Debris.MinBlocks blocks (occupied box volume / one block: 0.5 m small, 2.5 m large from the
 /// mirror's GridAdd class) or whose box is under Debris.MinVolumeM3 is debris (<c>format debris.grid</c>): its
 /// sections' EcoTarget (S6's Shark target, ProxySections.AddEco) is switched off, so EcoRegion scans never offer it
 /// bites on it are refused (CanDealDamageTo postfix), no grab
 /// starts on it, no push. Re-judged every second (a ship broken down to a remnant becomes debris); S11.M11 keeps
 /// remnants as targets. Rows <c>s11.debris</c> on every change.
 /// </summary>
        private void RefreshDebris()
        {
            if (_s6 == null) return;
            foreach (List<ProxySection> l in _byRoot.Values) l.Clear();
            foreach (ProxySection sec in _s6.Proxies.AllSections())
            {
                if (sec == null || sec.Marker == null || sec.Marker.SelfTest || sec.Marker.Kind != DamageTargetKind.GridSection || sec.Data == null) continue;
                ulong r = sec.Marker.RootEntityId != 0 ? sec.Marker.RootEntityId : sec.Marker.EntityId;
                List<ProxySection> list;
                if (!_byRoot.TryGetValue(r, out list)) { list = new List<ProxySection>(); _byRoot[r] = list; }
                list.Add(sec);
            }
            bool filter = !S11Mutants.IsOn(S11Mutants.NoDebrisFilter);
            _debrisNext.Clear();
            foreach (KeyValuePair<ulong, List<ProxySection>> kv in _byRoot)
            {
                if (kv.Value.Count == 0) continue;
                _datas.Clear();
                double occupied = 0;
                foreach (ProxySection sec in kv.Value)
                {
                    _datas.Add(sec.Data);
                    foreach (SkinBoxData b in sec.Data.Boxes) occupied += 8.0 * Math.Abs(b.Half.X * b.Half.Y * b.Half.Z);
                }
                Vec3 min, max;
                double volume = S11GrabRules.SkinExtents(_datas, out min, out max) ? (max.X - min.X) * (max.Y - min.Y) * (max.Z - min.Z) : double.NaN;
                double edge = double.NaN;
                GridRecord gr;
                if (_m != null && _m.State.Grids.TryGetValue(kv.Key, out gr)) edge = gr.TargetClass == GridClass.SmallGrid ? 0.5 : gr.TargetClass == GridClass.LargeGrid ? 2.5 : double.NaN;
                double blocks = S11DebrisRules.EstimateBlocks(occupied, edge);
                bool debris = S11DebrisRules.IsDebris(blocks, volume);
                bool off = debris && filter;
                if (debris) _debrisNext.Add(kv.Key);
                foreach (ProxySection sec in kv.Value)
                {
                    EcoTarget eco = sec.Root != null ? sec.Root.GetComponent<EcoTarget>() : null;
                    if (eco != null && eco.enabled == off) { eco.enabled = !off; if (off) DebrisEcoOff++; }
                }
                bool prev;
                if (!_debrisSeen.TryGetValue(kv.Key, out prev) || prev != debris)
                {
                    _debrisSeen[kv.Key] = debris;
                    DebrisRows++;
                    JsonOut j = S5Rows.Begin("s11.debris");
                    j.Str("root", S6Rows.Hex(kv.Key)).Num("blocksEst", blocks).Num("volume", volume).Num("edge", edge).Num("sections", (long)kv.Value.Count)
                     .Bool("debris", debris).Bool("ecoOff", off).Str("mutants", S11Mutants.Describe());
                    S5Rows.End(j);
                    if (debris) DeLog.Info("DE.S11 debris " + S6Rows.Hex(kv.Key) + ": ~" + (double.IsNaN(blocks) ? "?" : blocks.ToString("0", CultureInfo.InvariantCulture)) + " block(s), box " +
                                           volume.ToString("0.#", CultureInfo.InvariantCulture) + " m3: " + (off ? "no creature hunts, bites, grabs or pushes it" : "S11.M11: still a target"));
                }
            }
            HashSet<ulong> t = _debris;
            _debris = _debrisNext;
            _debrisNext = t;
        }

        /// <summary>A grid remnant (<c>format debris.grid</c>) while the filter is on.</summary>
        public bool IsDebris(ulong root) { return root != 0 && _debris.Contains(root) && !S11Mutants.IsOn(S11Mutants.NoDebrisFilter); }

        /// <summary>A section of a grid remnant (the character is never debris).</summary>
        public bool IsDebrisMarker(ProxyMarker pm)
        {
            if (pm == null || pm.SelfTest || pm.Kind != DamageTargetKind.GridSection) return false;
            return IsDebris(pm.RootEntityId != 0 ? pm.RootEntityId : pm.EntityId);
        }

        // ============================================================================================ B: avoidance

        public const int ProxyLayerMask = 1 << 0;
        public bool AvoidOn { get { return _s6 != null && !S11Mutants.IsOn(S11Mutants.NoAvoid); } }

        /// <summary>A proxy section the creature is not attacking (its current target's entity) is an obstacle.</summary>
        public bool IsProxyObstacle(Collider c, GameObject creatureTarget)
        {
            ProxyMarker pm = S11Hooks.MarkerOf(c);
            if (pm == null || pm.SelfTest) return false;
            if (creatureTarget != null)
            {
                ProxyMarker tm = S11Hooks.MarkerOf(creatureTarget);
                if (tm != null && (tm.RootEntityId == pm.RootEntityId || tm.EntityId == pm.EntityId)) return false; // attacking that ship: no avoidance
            }
            AvoidHits++;
            return true;
        }

        private readonly RaycastHit[] _avoidHits = new RaycastHit[16];

        /// <summary>Review 3: a proxy-only cast beside AvoidObstacles' own scan (SphereCast with the action's radius, else a ray).</summary>
        public bool ProxyAhead(Vector3 origin, Vector3 dir, float radius, float dist, GameObject creatureTarget)
        {
            if (!(dist > 0f) || dir.sqrMagnitude < 1e-8f) return false;
            dir.Normalize();
            int n = radius > 0f
                ? Physics.SphereCastNonAlloc(origin, radius, dir, _avoidHits, dist, ProxyLayerMask, QueryTriggerInteraction.Ignore)
                : Physics.RaycastNonAlloc(origin, dir, _avoidHits, dist, ProxyLayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (IsProxyObstacle(_avoidHits[i].collider, creatureTarget)) return true;
            return false;
        }

        /// <summary>AvoidTerrain's octree test extended with the proxies (the leviathans' 30 m scan).</summary>
        public bool ProxyOnSegment(Vector3 a, Vector3 b, GameObject creatureTarget)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-3f) return false;
            int n = Physics.RaycastNonAlloc(a, d / len, _avoidHits, len, ProxyLayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (IsProxyObstacle(_avoidHits[i].collider, creatureTarget)) { AvoidTerrainHits++; return true; }
            return false;
        }

        // ============================================================================================ de.s11

        public bool Handle(ControlRequest req, JsonObjectWriter reply, ulong now)
        {
            if (req == null || req.Cmd != Cmd) return false;
            Dictionary<string, object> a = S3Args.ToDict(req.Args);
            object act;
            a.TryGetValue("action", out act);
            string action = (act as string) ?? (a.ContainsKey("weapon") ? "weapon" : "status");
            reply.Str("cmd", Cmd);
            switch (action)
            {
                case "weapon":
                {
                    object v;
                    if (a.TryGetValue("reset", out v) || (a.TryGetValue("weapon", out v) && (v as string) == "reset"))
                    {
                        Weapons.Reset();
                        DeLog.Info("DE.S11 weapon table reset to the pinned defaults: " + Weapons.Describe());
                        reply.Bool("ok", true).Str("weapons", Weapons.Describe());
                        return true;
                    }
                    int c, t;
                    int? mult, floor;
                    string error;
                    if (!S11Args.TryWeapon(Str(a, "class"), Str(a, "tier"), Str(a, "mult"), Str(a, "floor"), out c, out t, out mult, out floor, out error) ||
                        !Weapons.Set(c, t, mult, floor, out error))
                    {
                        reply.Bool("ok", false).Str("error", error);
                        return true;
                    }
                    DeLog.Info("DE.S11 weapon " + S11Conventions.WeaponClass.Name(c) + " vs " + S11Conventions.Tier.Name(t) + ": x" + (Weapons.Mult(c, t) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) +
                               " floor " + (Weapons.Floor(c, t) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " (de.s11; the pinned defaults are unchanged)");
                    reply.Bool("ok", true).Str("weapons", Weapons.Describe());
                    return true;
                }
                case "scene":
                    Scene.Handle(a, reply);
                    return true;
                case "scale":
                {
                    // de.s11 {action: scale, creature: ReaperLeviathan | 2540, factor} (chat: /de sn s11 scale creature=ReaperLeviathan factor=3)
                    object cv, fv;
                    int tech = 0;
                    string cname = a.TryGetValue("creature", out cv) ? Convert.ToString(cv, CultureInfo.InvariantCulture) : null;
                    S11ScaleRow srow;
                    if (cname != null && S11Conventions.TryScaleRow(cname, out srow)) tech = srow.TechType;
                    else if (cname != null) { int t; if (int.TryParse(cname, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) tech = t; }
                    if (a.ContainsKey("reset")) { Scales.Reset(); reply.Bool("ok", true).Str("scales", Scales.Describe()); DeLog.Info("DE.S11 scale table reset: " + Scales.Describe()); return true; }
                    double? f = null;
                    if (a.TryGetValue("factor", out fv)) { double d; if (S1Args.TryDouble(fv, out d)) f = d; }
                    string error;
                    if (!f.HasValue || !Scales.Set(tech, f, null, out error)) { reply.Bool("ok", false).Str("error", f.HasValue ? "scale: creature ReaperLeviathan|GhostLeviathan|GhostLeviathanJuvenile|SeaDragon" : "scale: factor 0.25..10"); return true; }
                    DeLog.Info("DE.S11 scale " + cname + " x" + Scales.Factor(tech).ToString("0.###", CultureInfo.InvariantCulture) + " (de.s11; the pinned default is unchanged; applied at the next sweep, 2 s)");
                    _nextSweepS = 0;
                    reply.Bool("ok", true).Str("scales", Scales.Describe());
                    return true;
                }
                case "rage":
                {
                    //: de.s11 {action: rage, creature: ReaperLeviathan | 2540, release: 0.15, anger: 1.5, extend: 4, flee: 10} or {.., reset}
                    object cv;
                    string cname = a.TryGetValue("creature", out cv) ? Convert.ToString(cv, CultureInfo.InvariantCulture) : "ReaperLeviathan";
                    if (a.ContainsKey("reset")) { Rage.Reset(); reply.Bool("ok", true).Str("rage", Rage.Describe()); DeLog.Info("DE.S11 rage table reset: " + Rage.Describe()); return true; }
                    S11RageRow rrow;
                    int tech = 0;
                    if (S11Conventions.TryRageRow(cname, out rrow)) tech = rrow.TechType;
                    else int.TryParse(cname, NumberStyles.Integer, CultureInfo.InvariantCulture, out tech);
                    string error;
                    if (!Rage.Set(tech, Opt(a, "release"), Opt(a, "anger"), Opt(a, "extend"), Opt(a, "flee"), out error)) { reply.Bool("ok", false).Str("error", error); return true; }
                    DeLog.Info("DE.S11 rage " + cname + ": " + Rage.Describe() + " (de.s11; the pinned defaults are unchanged)");
                    reply.Bool("ok", true).Str("rage", Rage.Describe());
                    return true;
                }
                case "teardown":
                    Grabs.ReleaseAll("teardown");
                    Scene.Teardown();
                    reply.Bool("ok", true);
                    return true;
                default:
                    reply.Bool("ok", true).Str("weapons", Weapons.Describe()).Str("patches", S11Patches.Describe()).Str("mutants", S11Mutants.Describe())
                         .Bool("s6", _s6 != null).Num("dangerHits", DangerHits).Num("dangerFloored", DangerFloored).Num("dangerKills", DangerKills)
                         .Num("chainEscalated", ChainEscalated).Num("hazardRecords", HazardRecords).Num("oxygenRecords", OxygenRecords).Num("heals", Heals)
                         .Num("empRecords", EmpRecords).Num("pushes", Pushes).Num("grabs", Grabs.Started).Num("grabsHeld", (long)Grabs.Held)
                         .Num("avoidHits", AvoidHits).Num("avoidTerrainHits", AvoidTerrainHits).Str("lastDanger", LastDanger).Str("lastHazard", LastHazard).Str("dmgFx", S11DamageFx.Describe())
                         .Num("radiation", (double)RadiationLevel()).Str("scales", Scales.Describe()).Num("scaled", Scaled)
                         .Str("rage", Rage.Describe()).Num("surfaceReleases", Grabs.SurfaceReleases).Num("rages", Grabs.Rages).Num("rageReleases", Grabs.RageReleases)
                         .Num("followClamps", FollowClamps).Num("missileHits", Missiles.Hits).Num("missileRecords", Missiles.Records).Num("missileAttributed", Missiles.Attributed);
                    return true;
            }
        }

        private static double? Opt(Dictionary<string, object> a, string k)
        {
            object v;
            double d;
            if (!a.TryGetValue(k, out v) || v == null || !S1Args.TryDouble(v, out d)) return null;
            return d;
        }

        private static string Str(Dictionary<string, object> a, string k)
        {
            object v;
            if (!a.TryGetValue(k, out v) || v == null) return null;
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
