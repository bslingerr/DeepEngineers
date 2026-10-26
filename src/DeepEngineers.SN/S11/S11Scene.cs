using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S11 oracle scenes of the Subnautica side (<c>de.s11 {action: "scene", part: …}</c>, dev; driven by
 /// src/DeepEngineers.Launcher/launcher_s11.py, judged by tests/s11/analyze_s11.py):
 /// <list type="bullet">
 /// <item><c>danger</c> (S11.A1, A2, A4): the nearest leviathan within 300 m of the camera (the launcher spawns two) takes
 /// handheld rifle shots (30 SN_HP, the S7p number) through the weapon-class rule until three in a row deal nothing; a
 /// second one takes two hand-held rockets (Handheld class, yield 20,000); then ten large-turret shots (33) and a warhead
 /// blast (yield 15,000 at 5 m): <c>s11.danger</c> rows;</item>
 /// <item><c>path</c> (S11.B1): every creature within <c>radius</c> of the camera is sampled at 10 Hz for <c>seconds</c>;
 /// a sample whose centre lies inside a proxy collider (ClosestPoint = the point) counts as inside: <c>s11.path</c>.
 /// Pass 4: the scripted creature path. Every 2 s, up to 12
 /// creatures with an <c>AvoidObstacles</c> within <c>radius</c> of the wall (<c>wall: [x, y, z]</c>, SN; else the grid
 /// nearest the camera) are put in front of its face (found by a ray on the proxy layer), facing it at
 /// <c>scanRadius + scanDistance / 2</c>, and their own <c>Evaluate</c> runs once (S11Patches.ProbeAvoid): <c>s11.probe</c>
 /// {saw, result, hookHits}. Then each probed creature is lured every second to a point behind the wall
 /// skipped while its avoidance runs (counted as a
 /// steer), so it keeps meeting the hull;</item>
 /// <item><c>hazard</c> (S11.D1): 1 SN_HP of each hazard type on the ghost Player and on the character section through the
 /// game's own TakeDamage (the S6 route): <c>s11.hazard</c>;</item>
 /// <item><c>emp</c> (S11.D2; pass 4: its own part, run 3 had no grid in the hazard part): an EMP record of 2 s on the
 /// nearest grid section (the launcher places the room with its battery first): <c>s11.emp</c> (why "noGrid" without one);</item>
 /// <item><c>grab</c> (S11.C2/C3 in S11b; pass 4): the forced grab of S11se's grabse driven through Subnautica's own record
 /// path (its seqs: an SE inject with a test seq would push the dedup window past Subnautica's next real records): a
 /// reaper-keyed start (planned <c>seconds</c>) on the grid section nearest <c>at</c>, the attach point 2 m above its root,
 /// then hold records every 50 ms moving it up 1 m/s for <c>seconds</c>, then silence (SE's 1,000 ms timeout lets go):
 /// <c>s11.forced</c> rows;</item>
 /// <item><c>air</c> (S11.E1/E2): one bubble, one air-pocket and one base oxygen record, and a first-aid kit's
 /// <c>AddHealth(50)</c> on the ghost Player (the patched path): <c>s11.oxygen</c>, <c>s11.heal</c>.</item>
 /// </list>
 /// <item>: <c>volley</c> (S11.R1, R2): the nearest leviathan pinned in place (its rigidbody held still, health
 /// back to max) for <c>seconds</c>, its collider centre in the reply for SE's <c>de.s11 volley</c>; <c>burst</c> (S11.R3):
 /// gatling shots (S7 BulletStreak rows through S7p's own Shot) at the pinned creature, half from <c>from</c> m outside, half
 /// from inside its jaw (a held ship's gun): <c>s11.burst</c>; <c>holdhit</c> (S11.R4, R5): the reaper made to hold the
 /// nearest grid section twice, one rocket's worth (500 SN_HP, below the release share) in the first hold, two rockets
 /// (1,000, above it) in the second, through S7p's danger path: <c>s11.holdhit</c>, Subnautica's <c>s11.rage</c>;
 /// <c>surface</c> (S11.C7): <c>mode: follow</c> the reaper's LastTarget set to the section nearest <c>plate</c> (above the
 /// water) with Aggression 1 every second; <c>mode: rise</c> the reaper made to hold the section nearest <c>wall</c> and
 /// pushed up (an acceleration on its rigidbody, the scene's only drive) until it lets go.</item>
 /// Every scene writes one <c>s11.scene</c> row when it starts and one when it ends. Main thread.
 /// </summary>
    internal sealed class S11Scene
    {
        private readonly S11Host _host;
        private readonly S6Host _s6;
        private readonly MirrorConsumer _m;

        private string _part;
        private double _endS, _nextS;
        private float _radius = 60f;
        private int _danger, _quiet, _samples, _inside, _creaturesSeen, _near;
        private long _scans0;
        private bool _charAtStart;
        private LiveMixin _target;
        private readonly HashSet<int> _seen = new HashSet<int>();
        private readonly Collider[] _cols = new Collider[64];

        public S11Scene(S11Host host, S6Host s6, MirrorConsumer m)
        {
            _host = host;
            _s6 = s6;
            _m = m;
        }

        public string Active { get { return _part; } }

        public void Handle(Dictionary<string, object> a, JsonObjectWriter reply)
        {
            object v;
            string part = a.TryGetValue("part", out v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
            double seconds = a.TryGetValue("seconds", out v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 30.0;
            if (a.TryGetValue("radius", out v)) _radius = (float)Convert.ToDouble(v, CultureInfo.InvariantCulture);
            _hasWallHint = false;
            if (a.TryGetValue("wall", out v) && v is System.Collections.IList && ((System.Collections.IList)v).Count == 3)
            {
                var l = (System.Collections.IList)v;
                _wallHint = new Vector3(Convert.ToSingle(l[0], CultureInfo.InvariantCulture), Convert.ToSingle(l[1], CultureInfo.InvariantCulture), Convert.ToSingle(l[2], CultureInfo.InvariantCulture));
                _hasWallHint = true;
            }
            double now = Time.realtimeSinceStartup;
            switch (part)
            {
                case "danger":
                case "path":
                    Begin(part, now, seconds);
                    reply.Bool("ok", true).Str("part", part).Num("seconds", seconds);
                    return;
                case "hazard":
                    Begin(part, now, 0);
                    Hazards();
                    End("done");
                    reply.Bool("ok", true).Str("part", part);
                    return;
                case "grab":
                    _forcedFastS = a.TryGetValue("fastS", out v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0.0;
                    _forcedFastSpeed = a.TryGetValue("fastSpeed", out v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0.0;
                    Begin(part, now, seconds + _forcedFastS + 2.5);
                    _forcedKey = a.TryGetValue("key", out v) ? (uint)Convert.ToDouble(v, CultureInfo.InvariantCulture) : 77u;
                    _forcedHoldS = seconds;
                    bool started = ForcedStart(now);
                    if (!started) End("noGrid");
                    reply.Bool("ok", true).Str("part", part).Bool("started", started);
                    return;
                case "emp":
                    Begin(part, now, 0);
                    bool emitted = Emp();
                    End(emitted ? "done" : "noGrid");
                    reply.Bool("ok", true).Str("part", part).Bool("emitted", emitted);
                    return;
                case "air":
                    Begin(part, now, 0);
                    Air();
                    End("done");
                    reply.Bool("ok", true).Str("part", part);
                    return;
                case "volley":
                case "burst":
                case "holdhit":
                case "surface":
                    HandleNew(part, a, reply, now, seconds);
                    return;
                default:
                    reply.Bool("ok", false).Str("error", "scene part: danger | path | grab | hazard | emp | air | volley | burst | holdhit | surface");
                    return;
            }
        }

        private void Begin(string part, double now, double seconds)
        {
            if (_part != null) End("replaced");
            _part = part;
            _endS = now + Math.Max(1.0, seconds);
            _nextS = now;
            _danger = _quiet = _samples = _inside = _creaturesSeen = _near = 0;
            _probes = _probeSaw = _steers = 0;
            _insideRun.Clear();
            _maxRun = 0;
            _maxDepth = 0f;
            _probeHits = 0;
            _probeNextS = now + 1.0;
            _lureNextS = now + 1.0;
            _hasWall = false;
            _probed.Clear();
            _lured.Clear();
            _scans0 = _host.AvoidScans;
            _charAtStart = CharacterPresent();
            _phase = _rockets = _turret = 0;
            _respawned = false;
            _dangerT0 = now;
            _target = _target2 = null;
            _seen.Clear();
            JsonOut j = S5Rows.Begin("s11.scene");
            j.Str("part", part).Str("phase", "start").Num("seconds", seconds).Num("radius", (double)_radius).Bool("characterPresent", _charAtStart).Str("mutants", S11Mutants.Describe());
            S5Rows.End(j);
            DeLog.Info("DE.S11 scene " + part + " start (" + seconds.ToString("0", CultureInfo.InvariantCulture) + " s)");
        }

        private void End(string why)
        {
            if (_part == null) return;
            JsonOut j = S5Rows.Begin("s11.scene");
            j.Str("part", _part).Str("phase", "end").Str("why", why).Num("dangerShots", (long)_danger).Num("samples", (long)_samples).Num("inside", (long)_inside)
             .Num("creatures", (long)_creaturesSeen).Num("near", (long)_near).Num("avoidHits", _host.AvoidHits).Num("avoidTerrainHits", _host.AvoidTerrainHits)
             .Num("avoidScans", _host.AvoidScans - _scans0).Bool("characterPresent", _charAtStart && CharacterPresent())
             .Num("insideMaxRun", (long)_maxRun).Num("insideMaxDepth", (double)_maxDepth)
             .Num("probes", (long)_probes).Num("probeSaw", (long)_probeSaw).Num("probeHookHits", _probeHits).Num("lured", (long)_lured.Count).Num("steers", (long)_steers)
             .Bool("wall", _hasWall).Str("mutants", S11Mutants.Describe());
            S5Rows.End(j);
            DeLog.Info("DE.S11 scene " + _part + " end (" + why + "): shots " + _danger + ", path samples " + _samples + " inside " + _inside +
                       (_part == "path" ? ", probes " + _probes + " saw " + _probeSaw + " (hook hits " + _probeHits + "), lured " + _lured.Count + ", steers " + _steers : ""));
            _part = null;
            _target = null;
        }

        public void Teardown() { End("teardown"); }

        /// <summary>Run 1: the SE character is there to receive a record (present, alive, not seated): a scene without it is MISSING, not FAIL.</summary>
        private bool CharacterPresent()
        {
            return _s6 != null && _s6.Character.CharacterId != 0 && _s6.Character.Present && !_s6.Character.Dead;
        }

        public void Tick(double now)
        {
            if (_part == null) return;
            if (now >= _endS) { End("time"); return; }
            if (now < _nextS) return;
            if (_part == "danger") { _nextS = now + 0.25; DangerStep(); }
            else if (_part == "path") { _nextS = now + 0.1; PathStep(); }
            else if (_part == "volley" || _part == "burst" || _part == "holdhit" || _part == "surface") { TickNew(now); }
            else if (_part == "grab")
            {
                // 20261024-030926: the forced grab at the grab clock's cadence, catching up (every frame at 60 fps)
                double every = S11Conventions.Hold.RecordIntervalMs / 1000.0;
                _nextS += every;
                if (_nextS <= now) _nextS = now + every;
                ForcedHold(now);
            }
        }

        // ------------------------------------------------------------------------------------------ grab: the forced one (pass 4)

        private uint _forcedKey = 77u;
        private double _forcedHoldS, _forcedT0, _forcedFastS, _forcedFastSpeed;
        private int _forcedHolds;
        private ProxyMarker _forcedSection;
        private Vector3 _forcedW0;

        private bool ForcedStart(double now)
        {
            _forcedHolds = 0;
            _forcedSection = _hasWallHint ? NearestGridSectionTo(_wallHint) : NearestGridSection();
            if (_forcedSection == null) { ForcedRow("start", null, false, "noGrid"); return false; }
            // the attach point 2 m above the placement point (grabse's), else above the section
 //: 4 m above (the wall's centre of bounds moves onto it over the blend; S11.M13's bite point leaves it >= 4 m off)
            _forcedW0 = (_hasWallHint ? _wallHint : _forcedSection.transform.position) + Vector3.up * 4f;
            _forcedT0 = now;
            string id;
            bool ok = _host.EmitGrid(S11Conventions.Unit.Grab, _forcedSection, (float)(_forcedHoldS + _forcedFastS), S11Conventions.GrabPhase.Start, 0, _forcedW0, Vector3.forward, Vector3.up,
                                     (int)TechType.ReaperLeviathan, _forcedKey, out id);
            ForcedRow("start", id, ok, ok ? "" : "notEmitted");
            return ok;
        }

        private void ForcedHold(double now)
        {
            if (_forcedSection == null) return;
            double t = now - _forcedT0;
            if (t > _forcedHoldS + _forcedFastS) return; // silence: SE's timeout releases the grid
 // S11b 20261024-000956: the x3 reaper's mouth moved at 40 m/s; after the slow rise a fast segment along +x at
 // fastSpeed (the kinematic hold follows it within 2 m, S11.C5; the old force did not, S11.M12)
            Vector3 w = _forcedW0 + Vector3.up * (float)Math.Min(t, _forcedHoldS);
            if (t > _forcedHoldS) w += Vector3.right * (float)FastOffset(t - _forcedHoldS, _forcedFastSpeed);
            string id;
            bool ok = _host.EmitGrid(S11Conventions.Unit.Grab, _forcedSection, 1f, S11Conventions.GrabPhase.Hold, 0, w, Vector3.forward, Vector3.up,
                                     (int)TechType.ReaperLeviathan, _forcedKey, out id);
            if (ok) _forcedHolds++;
            if (!ok || _forcedHolds % 20 == 1) ForcedRow("hold", id, ok, ok ? "" : "notEmitted");
        }

        /// <summary>
 /// The fast segment's offset (S11b 20261024-122326: C5/C6 2.18 m = the 1 -> 40 m/s step over one 54 ms record gap):
 /// the mouth reaches its speed in RampS (160 m/s2 at 40 m/s, a lunge), then holds it.
 /// </summary>
        public const double RampS = 0.25;
        public static double FastOffset(double tau, double v)
        {
            if (!(tau > 0) || !(v > 0)) return 0.0;
            return tau < RampS ? 0.5 * (v / RampS) * tau * tau : 0.5 * v * RampS + v * (tau - RampS);
        }

        private void ForcedRow(string op, string id, bool ok, string why)
        {
            JsonOut j = S5Rows.Begin("s11.forced");
            j.Str("op", op).Str("id", id ?? "").Bool("ok", ok).Num("key", (long)_forcedKey).Num("holds", (long)_forcedHolds)
             .Str("root", _forcedSection != null ? S6Rows.Hex(_forcedSection.RootEntityId) : "").Str("why", why ?? "");
            S5Rows.End(j);
        }

        // ------------------------------------------------------------------------------------------ danger

        private int _phase, _rockets, _turret;
        private bool _respawned;
        private double _dangerT0;
        private LiveMixin _target2;

        /// <summary>
 /// Phases: 0 the rifle on the first leviathan until three shots in a row deal nothing (or it dies: S11.M1); 1 two
 /// hand-held rockets on a second leviathan (the launcher spawns two), else on the first;
 /// 2 ten large-turret shots, 3 a warhead blast on whichever is alive.
 /// </summary>
        private void DangerStep()
        {
            if (_target == null)
            {
                _target = NearestLeviathan(null);
                if (_target == null)
                {
                    // S11b 20261024-000956: the two reapers spawned 40 m out were nowhere within 300 m 10 s later (they swim off;
 // the scene shot nothing for 60 s). The search has no range now; with none at all after 5 s the scene
 // spawns two itself
                    if (!_respawned && Time.realtimeSinceStartup - _dangerT0 > 5.0)
                    {
                        _respawned = true;
                        bool sent = DevConsole.SendConsoleCommand("spawn reaperleviathan 2 40");
                        JsonOut j = S5Rows.Begin("s11.scene");
                        j.Str("part", "danger").Str("phase", "respawn").Bool("sent", sent);
                        S5Rows.End(j);
                        DeLog.Info("DE.S11 scene danger: no leviathan loaded 5 s in; spawned two (" + (sent ? "sent" : "NOT sent") + ")");
                    }
                    return; // waits for a spawn (the scene ends on time)
                }
                DeLog.Info("DE.S11 scene danger target " + _target.gameObject.name + " health " + _target.health.ToString("0", CultureInfo.InvariantCulture) + "/" + _target.maxHealth.ToString("0", CultureInfo.InvariantCulture));
            }
            if (_phase == 0)
            {
                if (!_target.IsAlive() || _quiet >= 3) { _phase = 1; return; }
                Shot(_target, S11Conventions.WeaponClass.Handheld, S7pDamageRules.HandProjectileHp, DamageType.Normal, "scene rifle");
                return;
            }
            if (_phase == 1)
            {
                if (_target2 == null) _target2 = NearestLeviathan(_target);
                LiveMixin r = _target2 != null && _target2.IsAlive() ? _target2 : (_target.IsAlive() ? _target : null);
                if (r == null || _rockets >= 2) { _phase = 2; return; }
                _rockets++;
                Shot(r, S11Conventions.WeaponClass.Handheld, (float)S11BlastRules.DamageHp(20000, 30, 1, 1000), DamageType.Explosive, "scene rocket");
                return;
            }
            LiveMixin t = _target.IsAlive() ? _target : (_target2 != null && _target2.IsAlive() ? _target2 : null);
            if (t == null) { End("killed"); return; }
            if (_phase == 2)
            {
                if (_turret++ >= 10) { _phase = 3; return; }
                Shot(t, S11Conventions.WeaponClass.LargeGrid, S7pDamageRules.ShipProjectileHp, DamageType.Normal, "scene large turret");
                return;
            }
            Shot(t, S11Conventions.WeaponClass.Explosion, (float)S11BlastRules.DamageHp(15000, 22.4415, 5, 1000), DamageType.Explosive, "scene warhead");
            End(t.IsAlive() ? "done" : "killed");
        }

        private void Shot(LiveMixin target, int cls, float hp, DamageType type, string why)
        {
            _danger++;
            Vector3 at = target.transform.position;
            float applied = S11Hooks.Danger(target, hp, cls, why);
            if (applied > 0f)
            {
                bool killed = target.TakeDamage(applied, at, type, null);
                S11Hooks.AfterDanger(target, killed);
                if (cls == S11Conventions.WeaponClass.Handheld && _phase == 0) _quiet = 0;
            }
            else if (_phase == 0) _quiet++;
        }

        private LiveMixin NearestLeviathan(LiveMixin except)
        {
            Camera cam = MainCamera.camera;
            Vector3 c = cam != null ? cam.transform.position : (Player.main != null ? Player.main.transform.position : Vector3.zero);
            LiveMixin best = null;
            float bestD = float.PositiveInfinity; // S11b 20261024-000956: any distance (the hits are direct calls; no range is needed)
            foreach (Creature cr in UnityEngine.Object.FindObjectsOfType<Creature>())
            {
                if (cr == null || cr.liveMixin == null || !cr.liveMixin.IsAlive() || ReferenceEquals(cr.liveMixin, except)) continue;
                if (S11Danger.TierOf(cr.liveMixin.maxHealth) != S11Conventions.Tier.Leviathan) continue;
                float d = (cr.transform.position - c).sqrMagnitude;
                if (d < bestD) { bestD = d; best = cr.liveMixin; }
            }
            return best;
        }

        // ------------------------------------------------------------------------------------------ path

        private void PathStep()
        {
            if (_s6 == null) { End("no S6"); return; }
            Camera cam = MainCamera.camera;
            if (cam == null) return;
            Physics.SyncTransforms();
            Vector3 c = cam.transform.position;
            double nowS = Time.realtimeSinceStartup;
            if (nowS >= _probeNextS) { _probeNextS = nowS + 2.0; ProbeStep(c); }
            if (nowS >= _lureNextS) { _lureNextS = nowS + 1.0; LureStep(); }
            int rows = 0;
            foreach (Creature cr in UnityEngine.Object.FindObjectsOfType<Creature>())
            {
                if (cr == null || !cr.gameObject.activeInHierarchy) continue;
                Vector3 p = cr.transform.position;
                if ((p - c).sqrMagnitude > _radius * _radius) continue;
                if (_seen.Add(cr.GetInstanceID())) _creaturesSeen++;
                _samples++;
                bool inside = false;
                string where = null;
                float depth = float.NaN;
                int n = Physics.OverlapSphereNonAlloc(p, 0.05f, _cols, S11Host.ProxyLayerMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n && !inside; i++)
                {
                    ProxyMarker pm = S11Hooks.MarkerOf(_cols[i]);
                    if (pm == null || pm.SelfTest) continue;
                    if ((_cols[i].ClosestPoint(p) - p).sqrMagnitude < 1e-6f) { inside = true; where = S6Rows.Hex(pm.EntityId) + "/" + S6Rows.Sec(pm.SectionId); depth = Depth(_cols[i], p); }
                }
                // night a test run-204313 (B1: 2 inside of 31,715): each inside sample's depth past the nearest face and
 // the creature's consecutive inside samples, so a contact transient is told from a creature sitting in a hull
                int id = cr.GetInstanceID(), run = 0;
                if (inside) { _insideRun.TryGetValue(id, out run); run++; _insideRun[id] = run; if (run > _maxRun) _maxRun = run; if (depth > _maxDepth) _maxDepth = depth; }
                else if (_insideRun.Count > 0) _insideRun.Remove(id);
                if (inside) _inside++;
                else if (NearProxy(p)) _near++; // run 1: a creature within 3 m of a hull, where its avoidance must act
                if (inside || rows < 4)
                {
                    rows++;
                    string tech;
                    try { tech = CraftData.GetTechType(cr.gameObject).ToString(); } catch (Exception) { tech = cr.gameObject.name; }
                    JsonOut j = S5Rows.Begin("s11.path");
                    j.Str("creature", tech).Num("instance", (long)cr.GetInstanceID()).Vec("p", p.x, p.y, p.z).Bool("inside", inside).Str("section", where ?? "");
                    if (inside) j.Num("depth", (double)depth).Num("run", (long)run);
                    S5Rows.End(j);
                }
            }
        }

        // ------------------------------------------------------------------------------------------ path: the scripted probe (pass 4)

        private const int MaxProbes = 12;
        private int _probes, _probeSaw, _steers;
        private long _probeHits;
        private double _probeNextS, _lureNextS;
        private bool _hasWall, _hasWallHint;
        private Vector3 _wallHint, _wallC, _wallN, _wallExt;
        private readonly HashSet<int> _probed = new HashSet<int>();
        private readonly List<Creature> _lured = new List<Creature>();
        private static readonly float[] Spots = { -0.6f, -0.2f, 0.2f, 0.6f }; // 16 spots on the face (12 probes never share one)

 /// <summary>The wall: the proxy sections of the grid nearest the hint (or the camera), their world bounds; its normal the thinnest axis, toward the camera.</summary>
        private bool FindWall(Vector3 cam)
        {
            Vector3 hint = _hasWallHint ? _wallHint : cam;
            ProxySection near = null;
            float best = float.PositiveInfinity;
            foreach (ProxySection s in _s6.Proxies.AllSections())
            {
                if (s == null || s.Marker == null || s.Marker.SelfTest || s.Root == null || s.Marker.Kind != DamageTargetKind.GridSection) continue;
                float d = (s.Root.transform.position - hint).sqrMagnitude;
                if (d < best) { best = d; near = s; }
            }
            if (near == null) return false;
            bool any = false;
            Bounds b = default(Bounds);
            foreach (ProxySection s in _s6.Proxies.AllSections())
            {
                if (s == null || s.Marker == null || s.Root == null || s.Marker.RootEntityId != near.Marker.RootEntityId) continue;
                foreach (Collider col in s.Root.GetComponentsInChildren<Collider>(false))
                {
                    if (col == null || !col.enabled || col.isTrigger) continue;
                    if (!any) { b = col.bounds; any = true; } else b.Encapsulate(col.bounds);
                }
            }
            if (!any) return false;
            _wallC = b.center;
            _wallExt = b.extents;
            int axis = _wallExt.x <= _wallExt.y && _wallExt.x <= _wallExt.z ? 0 : (_wallExt.y <= _wallExt.z ? 1 : 2);
            Vector3 n = axis == 0 ? Vector3.right : (axis == 1 ? Vector3.up : Vector3.forward);
            if (Vector3.Dot(cam - _wallC, n) < 0f) n = -n;
            _wallN = n;
            return true;
        }

        private void ProbeStep(Vector3 cam)
        {
            if (_probes >= MaxProbes) return;
            if (!_hasWall) _hasWall = FindWall(cam);
            if (!_hasWall) return;
            Vector3 u = Mathf.Abs(_wallN.y) > 0.5f ? Vector3.right : Vector3.up;
            Vector3 w = Vector3.Cross(_wallN, u).normalized;
            float extN = Mathf.Abs(Vector3.Dot(_wallExt, new Vector3(Mathf.Abs(_wallN.x), Mathf.Abs(_wallN.y), Mathf.Abs(_wallN.z))));
            float extU = Mathf.Abs(Vector3.Dot(_wallExt, new Vector3(Mathf.Abs(u.x), Mathf.Abs(u.y), Mathf.Abs(u.z))));
            float extW = Mathf.Abs(Vector3.Dot(_wallExt, new Vector3(Mathf.Abs(w.x), Mathf.Abs(w.y), Mathf.Abs(w.z))));
            foreach (Creature cr in UnityEngine.Object.FindObjectsOfType<Creature>())
            {
                if (_probes >= MaxProbes) break;
                if (cr == null || !cr.gameObject.activeInHierarchy || cr.liveMixin == null || !cr.liveMixin.IsAlive()) continue;
                if (_probed.Contains(cr.GetInstanceID())) continue;
                if ((cr.transform.position - _wallC).sqrMagnitude > _radius * _radius) continue;
                AvoidObstacles aa = cr.GetComponent<AvoidObstacles>();
                if (aa == null || !aa.enabled) continue;
                _probed.Add(cr.GetInstanceID());
                int k = _probes;
                Vector3 o = _wallC + _wallN * (extN + 10f) + u * (Spots[k % 4] * extU) + w * (Spots[(k / 4) % 4] * extW);
                RaycastHit hit;
                string tech;
                try { tech = CraftData.GetTechType(cr.gameObject).ToString(); } catch (Exception) { tech = cr.gameObject.name; }
                if (!FaceHit(o, -_wallN, extN * 2f + 20f, out hit))
                {
                    ProbeRow(tech, cr, aa, 0f, 0f, 0, false, "noFace");
                    continue;
                }
                float dFace = Mathf.Max(0.3f, aa.scanRadius + 0.5f * aa.scanDistance);
                Vector3 at = hit.point + hit.normal * dFace;
                Vector3 up = Mathf.Abs(hit.normal.y) > 0.9f ? Vector3.forward : Vector3.up;
                Quaternion q = Quaternion.LookRotation(-hit.normal, up);
                Rigidbody rb = cr.GetComponent<Rigidbody>();
                if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = at; rb.rotation = q; }
                cr.transform.SetPositionAndRotation(at, q);
                Physics.SyncTransforms();
                float result;
                long hits;
                bool ok = S11Patches.ProbeAvoid(aa, cr, out result, out hits);
                bool saw = ok && (hits > 0 || result > 0f);
                _probes++;
                if (saw) _probeSaw++;
                _probeHits += hits;
                ProbeRow(tech, cr, aa, dFace, result, hits, saw, ok ? "" : "refsMissing");
                _lured.Add(cr);
            }
        }

        private readonly RaycastHit[] _faceHits = new RaycastHit[16];

        /// <summary>The nearest proxy face along the ray (layer 0 also holds creatures and other bodies: those are skipped).</summary>
        private bool FaceHit(Vector3 o, Vector3 dir, float dist, out RaycastHit best)
        {
            best = default(RaycastHit);
            float bd = float.PositiveInfinity;
            int n = Physics.RaycastNonAlloc(o, dir, _faceHits, dist, S11Host.ProxyLayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                ProxyMarker pm = S11Hooks.MarkerOf(_faceHits[i].collider);
                if (pm == null || pm.SelfTest || _faceHits[i].distance >= bd) continue;
                bd = _faceHits[i].distance;
                best = _faceHits[i];
            }
            return bd < float.PositiveInfinity;
        }

        private void ProbeRow(string tech, Creature cr, AvoidObstacles aa, float dFace, float result, long hits, bool saw, string why)
        {
            JsonOut j = S5Rows.Begin("s11.probe");
            j.Str("creature", tech).Num("instance", (long)cr.GetInstanceID()).Bool("terrainOnly", aa.avoidTerrainOnly).Num("scanDistance", (double)aa.scanDistance)
             .Num("scanRadius", (double)aa.scanRadius).Num("dFace", (double)dFace).Num("result", (double)result).Num("hookHits", hits).Bool("saw", saw).Str("why", why)
             .Str("mutants", S11Mutants.Describe());
            S5Rows.End(j);
        }

        /// <summary>The probed creatures swim for a point behind the wall (their own avoidance, when it runs, is left alone: a steer).</summary>
        private void LureStep()
        {
            if (!_hasWall) return;
            float extN = Mathf.Abs(Vector3.Dot(_wallExt, new Vector3(Mathf.Abs(_wallN.x), Mathf.Abs(_wallN.y), Mathf.Abs(_wallN.z))));
            Vector3 behind = _wallC - _wallN * (extN + 6f);
            foreach (Creature cr in _lured)
            {
                if (cr == null || !cr.gameObject.activeInHierarchy) continue;
                AvoidObstacles aa = cr.GetComponent<AvoidObstacles>();
                if (S11Patches.Avoiding(aa)) { _steers++; continue; }
                SwimBehaviour sb = cr.GetComponent<SwimBehaviour>();
                if (sb != null) sb.SwimTo(behind, aa != null ? Mathf.Max(1f, aa.swimVelocity) : 3f);
            }
        }

        private readonly Dictionary<int, int> _insideRun = new Dictionary<int, int>();
        private int _maxRun;
        private float _maxDepth;

        /// <summary>How far a point inside a box collider is from its nearest face, metres (NaN for another shape).</summary>
        private static float Depth(Collider c, Vector3 p)
        {
            var b = c as BoxCollider;
            if (b == null) return float.NaN;
            Vector3 l = b.transform.InverseTransformPoint(p) - b.center, h = b.size * 0.5f, s = b.transform.lossyScale;
            float dx = (h.x - Mathf.Abs(l.x)) * Mathf.Abs(s.x), dy = (h.y - Mathf.Abs(l.y)) * Mathf.Abs(s.y), dz = (h.z - Mathf.Abs(l.z)) * Mathf.Abs(s.z);
            return Mathf.Max(0f, Mathf.Min(dx, Mathf.Min(dy, dz)));
        }

        private bool NearProxy(Vector3 p)
        {
            int n = Physics.OverlapSphereNonAlloc(p, 3f, _cols, S11Host.ProxyLayerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                ProxyMarker pm = S11Hooks.MarkerOf(_cols[i]);
                if (pm != null && !pm.SelfTest && pm.Kind == DamageTargetKind.GridSection) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------ hazard and air

        private static readonly int[] HazardTypes = { 1, 5, 6, 10, 14, 16 };

        private void Hazards()
        {
            Player p = Player.main;
            if (p != null && p.liveMixin != null)
                foreach (int t in HazardTypes) p.liveMixin.TakeDamage(1f, p.transform.position, (DamageType)t, null);
            ProxyMarker cm = _s6 != null ? _s6.Character.Marker : null;
            if (cm != null && cm.Live != null && _s6.Character.Active) cm.Live.TakeDamage(1f, cm.transform.position, DamageType.Acid, null);
        }

        /// <summary>Pass 4: S11.D2's own part (run 3: the hazard part ran before any grid was placed, so no EMP record left).</summary>
        private bool Emp()
        {
            ProxyMarker grid = NearestGridSection();
            if (grid == null)
            {
                JsonOut j = S5Rows.Begin("s11.emp");
                j.Str("root", "").Str("entity", "").Num("seconds", 2.0).Str("id", "").Str("why", "noGrid");
                S5Rows.End(j);
                return false;
            }
            _host.EmpOn(grid, 2.0, grid.transform.position, 1u, "scene"); // the blast's own path (S11.M9 included)
            return true;
        }

        private ProxyMarker NearestGridSection() { return NearestGridSectionTo(_host.CharacterPos()); }

        // ------------------------------------------------------------------------------------------: volley, burst, holdhit, surface

        private Creature _pin;
        private Vector3 _pinAt;
        private Quaternion _pinRot;
        private double _pinEndS;
        private int _burstLeft, _burstI, _burstHits, _burstN;
        private float _burstFrom;
        private int _hhPhase;
        private double _hhNextS;
        private ProxyMarker _sec;
        private string _surfMode;
        private double _surfT0, _burstHoldS;
        private bool _burstHolder;
        private Vector3 _hint;
        private bool _hasHint;

        private static Vector3? V3(Dictionary<string, object> a, string k)
        {
            object v;
            var l = a.TryGetValue(k, out v) ? v as System.Collections.IList : null;
            if (l == null || l.Count != 3) return null;
            return new Vector3(Convert.ToSingle(l[0], CultureInfo.InvariantCulture), Convert.ToSingle(l[1], CultureInfo.InvariantCulture), Convert.ToSingle(l[2], CultureInfo.InvariantCulture));
        }

        private Creature NearestLeviathanCreature()
        {
            LiveMixin lm = NearestLeviathan(null);
            return lm != null ? lm.GetComponent<Creature>() : null;
        }

        /// <summary>The centre of a creature's own non-trigger colliders (world bounds), its position without one.</summary>
        private static Vector3 BodyCentre(Creature c)
        {
            bool any = false;
            Bounds b = default(Bounds);
            foreach (Collider col in c.GetComponentsInChildren<Collider>(false))
            {
                if (col == null || !col.enabled || col.isTrigger) continue;
                if (!any) { b = col.bounds; any = true; } else b.Encapsulate(col.bounds);
            }
            return any ? b.center : c.transform.position;
        }

        private void Pin(Creature c, Vector3 at)
        {
            _pin = c;
            _pinAt = at;
            _pinRot = c.transform.rotation;
            Hold(c);
            if (c.liveMixin != null) c.liveMixin.health = c.liveMixin.maxHealth; // dev scene only: each sub-test starts from full health
            Physics.SyncTransforms();
        }

        private void Hold(Creature c)
        {
            if (c == null) return;
            Rigidbody rb = c.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = _pinAt; rb.rotation = _pinRot; }
            c.transform.SetPositionAndRotation(_pinAt, _pinRot);
        }

        private void HandleNew(string part, Dictionary<string, object> a, JsonObjectWriter reply, double now, double seconds)
        {
            object v;
            Vector3? hint = V3(a, "plate") ?? V3(a, "wall");
            _hasHint = hint.HasValue;
            _hint = hint ?? Vector3.zero;
            switch (part)
            {
                case "volley":
                {
                    Creature c = NearestLeviathanCreature();
                    if (c == null) { reply.Bool("ok", false).Str("error", "no leviathan loaded"); return; }
                    Begin(part, now, seconds);
                    Vector3? at = V3(a, "pin");
                    Pin(c, at ?? c.transform.position);
                    _pinEndS = now + seconds;
                    Vector3 centre = BodyCentre(c);
                    PinRow("start", c, centre);
                    reply.Bool("ok", true).Str("part", part).Num("instance", (long)c.gameObject.GetInstanceID()).Raw("centre", "[" + centre.x.ToString("R", CultureInfo.InvariantCulture) + "," + centre.y.ToString("R", CultureInfo.InvariantCulture) + "," + centre.z.ToString("R", CultureInfo.InvariantCulture) + "]")
                         .Str("creature", CraftData.GetTechType(c.gameObject).ToString()).Num("health", c.liveMixin != null ? (double)c.liveMixin.health : double.NaN);
                    return;
                }
                case "burst":
                {
                    Creature c = _pin != null && _pin.gameObject.activeInHierarchy ? _pin : NearestLeviathanCreature();
                    if (c == null || S11Hooks.DamageHost == null) { reply.Bool("ok", false).Str("error", c == null ? "no leviathan loaded" : "no S7p creature damage"); return; }
                    bool pinned = _pin == c;
                    Begin(part, now, Math.Max(seconds, 10.0));
                    if (!pinned) Pin(c, c.transform.position);
                    _burstN = Math.Max(2, Math.Min(64, a.TryGetValue("count", out v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 24));
                    _burstFrom = a.TryGetValue("from", out v) ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : 40f;
                    _burstLeft = _burstN;
                    _burstI = _burstHits = 0;
                    //  a third of the shots from a grid the creature holds, pointed at its body (the gun of a held ship)
                    ProxyMarker hs = NearestGridSectionTo(BodyCentre(c));
                    _burstHolder = false;
                    _burstHoldS = now;
                    if (hs != null && (hs.transform.position - BodyCentre(c)).magnitude < 120f)
                    {
                        _host.Grabs.ForcedHoldS = 12.0;
                        _burstHolder = _host.Grabs.ForceStart(c, hs);
                        _host.Grabs.ForcedHoldS = 0;
                        if (_burstHolder) { _hhRoot = hs.RootEntityId != 0 ? hs.RootEntityId : hs.EntityId; _burstHoldS = now + 1.5; } // after the blend onto the jaw
                    }
                    if (c.liveMixin != null) c.liveMixin.health = c.liveMixin.maxHealth; // dev scene only: full health for the burst
                    _pinEndS = now + Math.Max(seconds, 10.0);
                    reply.Bool("ok", true).Str("part", part).Num("shots", (long)_burstN);
                    return;
                }
                case "holdhit":
                {
                    //  the launcher fires SE's own rockets at the holder (de.s11 volley); this scene places the reaper deep by
 // the wall, makes it hold it (step "hold", once per hold), pins it while it holds, reports each hold's end and the flee
                    Creature c = _pin != null && _pin.gameObject.activeInHierarchy && _part == "holdhit" ? _pin : NearestLeviathanCreature();
                    if (c == null || !(c is ReaperLeviathan)) { reply.Bool("ok", false).Str("error", "no reaper loaded"); return; }
                    if (_part != "holdhit") { Begin(part, now, Math.Max(seconds, 60.0)); if (c.liveMixin != null) c.liveMixin.health = c.liveMixin.maxHealth; } // dev scene only
                    _pin = c;
                    ProxyMarker sec = _hasHint ? NearestGridSectionTo(_hint) : NearestGridSectionTo(c.transform.position);
                    if (sec == null) { reply.Bool("ok", false).Str("error", "no grid section yet"); return; }
                    _hhRoot = sec.RootEntityId != 0 ? sec.RootEntityId : sec.EntityId;
                    PlaceJawBy(c, sec);
                    _host.Grabs.ForcedHoldS = a.TryGetValue("holdS", out v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 6.0;
                    bool ok = _host.Grabs.ForceStart(c, sec);
                    _host.Grabs.ForcedHoldS = 0;
                    _hhN++;
                    _hhHolding = ok;
                    HoldHitRow("hold" + _hhN, ok, ok ? "" : _host.Grabs.LastRefusal, 0);
                    Vector3 centre = BodyCentre(c);
                    reply.Bool("ok", ok).Str("part", part).Num("instance", (long)c.gameObject.GetInstanceID()).Str("why", ok ? "" : _host.Grabs.LastRefusal)
                         .Raw("centre", "[" + centre.x.ToString("R", CultureInfo.InvariantCulture) + "," + centre.y.ToString("R", CultureInfo.InvariantCulture) + "," + centre.z.ToString("R", CultureInfo.InvariantCulture) + "]");
                    return;
                }
                default: // surface
                {
                    Creature c = NearestLeviathanCreature();
                    if (c == null) { reply.Bool("ok", false).Str("error", "no leviathan loaded"); return; }
                    _surfMode = a.TryGetValue("mode", out v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "follow";
                    Begin(part, now, seconds);
                    _pin = c;
                    _sec = _hasHint ? NearestGridSectionTo(_hint) : NearestGridSectionTo(c.transform.position);
                    _hhNextS = now;
                    _hhPhase = 0;
                    _surfT0 = now;
                    if (_sec == null && _surfMode != "rise") { End("noGrid"); reply.Bool("ok", false).Str("error", "no grid section"); return; }
                    reply.Bool("ok", true).Str("part", part).Str("mode", _surfMode).Num("instance", (long)c.gameObject.GetInstanceID());
                    return;
                }
            }
        }

        private void PinRow(string phase, Creature c, Vector3 centre)
        {
            JsonOut j = S5Rows.Begin("s11.pin");
            j.Str("phase", phase).Str("part", _part ?? "").Str("creature", c != null ? CraftData.GetTechType(c.gameObject).ToString() : "").Num("instance", c != null ? (long)c.gameObject.GetInstanceID() : 0L)
             .Vec("centre", centre.x, centre.y, centre.z).Num("health", c != null && c.liveMixin != null ? (double)c.liveMixin.health : double.NaN)
             .Num("scale", c != null ? (double)c.transform.localScale.x : double.NaN).Str("mutants", S11Mutants.Describe());
            S5Rows.End(j);
        }

        private void TickNew(double now)
        {
            if (_part == "volley")
            {
                if (_pin == null || !_pin.gameObject.activeInHierarchy) { End("creature gone"); return; }
                Hold(_pin);
                if (now >= _pinEndS) { PinRow("end", _pin, BodyCentre(_pin)); End("time"); }
                return;
            }
            if (_part == "burst") { BurstStep(now); return; }
            if (_part == "holdhit") { HoldHitStep(now); return; }
            SurfaceStep(now);
        }

        /// <summary>
 /// S11.R3: gatling rounds (S7 BulletStreak rows, one projectile each, through S7p's own Shot: the ray, the hit, the
 /// weapon class), four a frame (S7p's per-frame hit cap): even shots from <c>from</c> m outside toward the body centre,
 /// odd shots from inside the jaw (the attach point, a held ship's gun) along the creature's forward.
 /// </summary>
        private void BurstStep(double now)
        {
            Creature c = _pin;
            if (c == null || !c.gameObject.activeInHierarchy) { End("creature gone"); return; }
            Hold(c);
            if (now < _burstHoldS) return;
            for (int k = 0; k < 4 && _burstLeft > 0; k++, _burstLeft--, _burstI++)
            {
                int cat = _burstHolder ? _burstI % 3 : _burstI % 2;
                bool inside = cat == 1;
                Bounds hb;
                if (cat == 2 && _host.Grabs.IsHolding(c) && GridCentre(_hhRoot, out hb))
                {
                    Vector3 o2 = hb.center + new Vector3(Mathf.Sin(_burstI), Mathf.Cos(_burstI), 0.5f) * 0.5f;
                    Vector3 d2 = (BodyCentre(c) - o2).normalized;
                    FireShot(c, o2, d2, "holder");
                    continue;
                }
                Vector3 centre = BodyCentre(c);
                Vector3 origin, dir;
                if (inside)
                {
                    // a gun inside the body (a held ship in the jaw): half a metre under the surface the outside ray meets, firing on
                    Camera cam0 = MainCamera.camera;
                    Vector3 far = centre + (cam0 != null ? (cam0.transform.position - centre).normalized : Vector3.up) * 120f;
                    Vector3 d0 = (centre - far).normalized;
                    RaycastHit sh;
                    origin = Physics.Raycast(far, d0, out sh, 240f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && sh.collider != null
                             && Utils.FindAncestorWithComponent<LiveMixin>(sh.collider.gameObject) == c.liveMixin ? sh.point + d0 * 0.5f : centre;
                    dir = d0;
                }
                else
                {
                    Camera cam = MainCamera.camera;
                    Vector3 away = cam != null ? cam.transform.position - centre : Vector3.up;
                    if (away.sqrMagnitude < 1e-4f) away = Vector3.up;
                    float ang = _burstI * 2.399963f;
                    Vector3 side = Vector3.Cross(away.normalized, Vector3.up);
                    if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
                    origin = centre + away.normalized * _burstFrom + side.normalized * (Mathf.Cos(ang) * 1.5f) + Vector3.up * (Mathf.Sin(ang) * 1.5f);
                    dir = (centre - origin).normalized;
                }
                FireShot(c, origin, dir, inside ? "inside" : "outside");
            }
            if (_burstLeft <= 0)
            {
                DeLog.Info("DE.S11 scene burst: " + _burstHits + "/" + _burstN + " shots hit the pinned creature");
                if (_burstHolder) _host.Grabs.ReleaseAll("burst done");
                End("done");
            }
        }

        private void FireShot(Creature c, Vector3 origin, Vector3 dir, string from)
        {
            {
                long before = _host.DangerHits;
                var row = new S7EventRow
                {
                    type = S7Conventions.EventType.BulletStreak,
                    flags = origin.y < 0f ? S7Conventions.EventFlags.Underwater : (byte)0,
                    intensity = 1f / 8f,
                    size = S7Conventions.EventLimits.StreakMaxM,
                    pos = new F32x3(origin.x, origin.y, origin.z),
                    dir = new F32x3(dir.x, dir.y, dir.z),
                    lifetimeMs = 100,
                };
                S11Hooks.DamageHost.Apply(ref row, Time.realtimeSinceStartup);
                bool hit = _host.DangerHits > before;
                if (hit) _burstHits++;
                JsonOut j = S5Rows.Begin("s11.burst");
                j.Num("i", (long)_burstI).Str("from", from).Bool("hit", hit).Num("instance", (long)c.gameObject.GetInstanceID())
                 .Vec("origin", origin.x, origin.y, origin.z).Str("mutants", S11Mutants.Describe());
                S5Rows.End(j);
            }
        }

        private int _hhN;
        private bool _hhHolding;
        private ulong _hhRoot;
        private double _fleeEndS, _fleeNextS;
        private Vector3 _fleeFrom;

        private static Transform JawOf(Creature c) { Transform h = S11Host.HeadOf(c); return h != c.transform ? h : null; }

        /// <summary>
 /// The creature put deep beside the section (in-game 122326: ForceStart refused "surface", the spawned reaper's top was
 /// within the margin): its jaw 8 m from the section's centre on its own side, at the section's depth, then pinned.
 /// </summary>
 /// <summary>
 /// The creature facing the grid, its jaw at the grid's reach from the grid's centre (its body trailing away: in-game
 /// 150515 the body put 8 m from the wall overlapped it and broke it to debris), level with the centre and lowered until
 /// its top is 3 m under the surface; then pinned.
 /// </summary>
        private void PlaceJawBy(Creature c, ProxyMarker sec)
        {
            Bounds gb;
            ulong root = sec.RootEntityId != 0 ? sec.RootEntityId : sec.EntityId;
            Vector3 centre = GridCentre(root, out gb) ? gb.center : sec.transform.position;
            float reach = (gb.extents.magnitude > 0.1f ? gb.extents.magnitude : 6f) + 6f;
            Vector3 side = c.transform.position - centre;
            side.y = 0f;
            if (side.sqrMagnitude < 1e-4f) side = Vector3.right;
            side.Normalize();
            Quaternion face = Quaternion.LookRotation(-side, Vector3.up);
            c.transform.rotation = face;
            Transform jaw = JawOf(c);
            Vector3 delta = jaw != null ? jaw.position - c.transform.position : Vector3.zero;
            Vector3 at = centre + side * reach - delta;
            c.transform.position = at;
            double top;
            _host.HalfHeight(c, out top);
            if (top > -3.0) at.y -= (float)(top + 3.0);
            _pinRot = face;
            Pin(c, at);
            c.transform.rotation = face;
            _pinEndS = double.PositiveInfinity;
        }

        /// <summary>
 /// S11.R4/R5: while the reaper holds, it stays pinned (SE's rockets aim at it); when a hold ends, one
 /// s11.holdhit row (fleeing or not) and the pin lets go; after a release by damage, 10 s of s11.flee rows (its body
 /// centre's distance from the grid, every 0.5 s).
 /// </summary>
        private void HoldHitStep(double now)
        {
            Creature c = _pin;
            if (c == null || !c.gameObject.activeInHierarchy || c.liveMixin == null || !c.liveMixin.IsAlive()) { End("creature gone"); return; }
            if (_hhHolding)
            {
                if (_host.Grabs.IsHolding(c)) { Hold(c); return; }
                _hhHolding = false;
                bool fleeing = _host.Grabs.IsFleeing(c);
                HoldHitRow("holdEnd" + _hhN, true, fleeing ? "fleeing" : "", 0);
                Bounds gb;
                _fleeFrom = GridCentre(_hhRoot, out gb) ? gb.center : c.transform.position;
                if (fleeing) { _fleeEndS = now + 10.0; _fleeNextS = now; }
                return;
            }
            if (now <= _fleeEndS && now >= _fleeNextS)
            {
                _fleeNextS = now + 0.5;
                JsonOut j = S5Rows.Begin("s11.flee");
                j.Num("instance", (long)c.gameObject.GetInstanceID()).Num("distM", (double)(BodyCentre(c) - _fleeFrom).magnitude).Num("leftS", _fleeEndS - now)
                 .Bool("fleeing", _host.Grabs.IsFleeing(c)).Bool("holding", _host.Grabs.IsHolding(c)).Str("mutants", S11Mutants.Describe());
                S5Rows.End(j);
            }
        }

        private bool GridCentre(ulong root, out Bounds b)
        {
            bool any = false;
            b = default(Bounds);
            if (_s6 == null) return false;
            foreach (ProxySection s in _s6.Proxies.AllSections())
            {
                if (s == null || s.Owner == null || s.Marker == null || (s.Owner.EntityId != root && s.Owner.RootId != root)) continue;
                foreach (Collider col in s.Marker.Colliders)
                {
                    if (col == null || !col.enabled) continue;
                    if (!any) { b = col.bounds; any = true; } else b.Encapsulate(col.bounds);
                }
            }
            return any;
        }

        private void HoldHitRow(string step, bool ok, string why, double applied)
        {
            JsonOut j = S5Rows.Begin("s11.holdhit");
            j.Str("step", step).Bool("ok", ok).Str("why", why ?? "").Num("applied", applied).Num("instance", _pin != null ? (long)_pin.gameObject.GetInstanceID() : 0L)
             .Num("health", _pin != null && _pin.liveMixin != null ? (double)_pin.liveMixin.health : double.NaN).Str("rage", _host.Rage.Describe()).Str("mutants", S11Mutants.Describe());
            S5Rows.End(j);
        }

        /// <summary>
 /// S11.C7: <c>follow</c> sets the creature's LastTarget to the section above the
 /// water and its Aggression to 1 every second: its own attack
 /// swims toward it (clamped by the rule). <c>rise</c> forces a hold of the section and pushes the creature up at
 /// 30 m/s² (Rigidbody.AddForce, Acceleration) while it holds: the rule lets go at the surface.
 /// </summary>
        private void SurfaceStep(double now)
        {
            Creature c = _pin;
            if (c == null || !c.gameObject.activeInHierarchy) { End("creature gone"); return; }
            if (_surfMode == "rise")
            {
                if (_hhPhase == 0)
                {
                    // in-game 122326: "noGrid" (the wall's proxies not built yet): wait up to 5 s for them; the creature placed deep by it
                    if (_sec == null) _sec = _hasHint ? NearestGridSectionTo(_hint) : null;
                    if (_sec == null) { if (now - _surfT0 > 5.0) End("noGrid"); return; }
                    PlaceJawBy(c, _sec);
                    _pin = c;
                    _host.Grabs.ForcedHoldS = 20.0;
                    bool ok = _host.Grabs.ForceStart(c, _sec);
                    _host.Grabs.ForcedHoldS = 0;
                    JsonOut j = S5Rows.Begin("s11.surface");
                    j.Str("op", "rise").Bool("held", ok).Str("why", ok ? "" : _host.Grabs.LastRefusal).Num("y", (double)c.transform.position.y).Str("mutants", S11Mutants.Describe());
                    S5Rows.End(j);
                    if (!ok) { End("noHold"); return; }
                    _hhPhase = 1;
                    return;
                }
                if (!_host.Grabs.IsHolding(c))
                {
                    JsonOut j = S5Rows.Begin("s11.surface");
                    j.Str("op", "riseEnd").Num("y", (double)c.transform.position.y).Num("surfaceReleases", _host.Grabs.SurfaceReleases).Str("mutants", S11Mutants.Describe());
                    S5Rows.End(j);
                    End("released");
                    return;
                }
                Rigidbody rb = c.GetComponent<Rigidbody>();
                if (rb != null) rb.AddForce(Vector3.up * 30f, ForceMode.Acceleration);
                return;
            }
            if (now < _hhNextS) return;
            _hhNextS = now + 1.0;
            LastTarget lt = c.GetComponent<LastTarget>();
            if (lt != null && _sec != null) lt.SetTarget(_sec.gameObject);
            c.Aggression.Value = 1f;
        }

        private ProxyMarker NearestGridSectionTo(Vector3 c)
        {
            if (_s6 == null) return null;
            ProxyMarker best = null;
            float bestD = float.PositiveInfinity;
            foreach (ProxySection s in _s6.Proxies.AllSections())
            {
                if (s == null || s.Marker == null || s.Marker.SelfTest || s.Root == null || s.Marker.Kind != DamageTargetKind.GridSection) continue;
                float d = (s.Root.transform.position - c).sqrMagnitude;
                if (d < bestD) { bestD = d; best = s.Marker; }
            }
            return best;
        }

        private void Air()
        {
            Vector3 at = _host.CharacterPos();
            _host.SceneOxygen(10.0, S11Conventions.OxygenSource.Bubble, at);
            _host.SceneOxygen(2.5, S11Conventions.OxygenSource.Area, at);
            _host.SceneOxygen(7.5, S11Conventions.OxygenSource.Base, at);
            Player p = Player.main;
            if (p != null && p.liveMixin != null) p.liveMixin.AddHealth(50f); // Survival.Use(FirstAidKit)'s call (S11 prefix)
        }
    }
}
