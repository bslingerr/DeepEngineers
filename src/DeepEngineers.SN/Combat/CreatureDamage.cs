using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;

namespace DeepEngineers.SN
{
    /// <summary>

 /// Subnautica's creatures. Its own <see cref="S7EventReader"/> on the C2 table reader's <c>Extensions</c> (the same
 /// window the effect player reads, every seq applied once here too: no edit of the effects player) feeds:
 /// <list type="bullet">
 /// <item>BulletStreak (one per <c>MyGunBase.Shoot</c>; projectiles only, missiles explode): a ray from the muzzle along
 /// the shot, <see cref="S7pDamageRules.RayRangeM"/> long (the creature is not in SE, so SE's own hit may be short of
 /// it), sorted hits; the shooter's own S6 proxy and the ghost Player are passed through, a creature takes
 /// <see cref="S7pDamageRules.ProjectileHp"/> × projectile count and stops the shot, anything else solid stops it.</item>
 /// <item>Explosion: every creature within the radius, Subnautica's own RadiusDamage falloff
 /// (<see cref="S7pDamageRules.ExplosionHp"/>), measured to the LiveMixin's transform, once per creature.</item>
 /// <item>Tools (Welder, Grinder, Drill, PrawnDrill; Repair = 0): sustains kept by (entity, key) until their Stop row or
 /// lifetime; every <see cref="S7pDamageRules.ToolStepS"/> a contact sphere at the work point damages each creature
 /// in it by dps × step.</item>
 /// </list>
 /// Damage goes through <c>LiveMixin.TakeDamage(float originalDamage, Vector3 position, DamageType type, GameObject
 /// dealer)</c>: only on a LiveMixin whose GameObject carries a <c>Creature</c>
 /// (never an S6 proxy section, never the ghost Player: the S6 forwarder's postfix ignores creatures, so nothing goes
 /// back to SE). Dealer = the ghost Player for the local player's rows (creatures react to the player), else none.
 /// Caps per frame: <see cref="S7pDamageRules.MaxEventsPerFrame"/> events, <see cref="S7pDamageRules.MaxHitsPerFrame"/>
 /// damage calls, <see cref="S7pDamageRules.MaxSustains"/> tools; drops counted in <c>s7p.damage</c>. Live scale
 /// <c>de.s7p {damage: scale|off|on}</c>, env <c>DEEPENGINEERS_S7P_DAMAGE</c> (default 1). Keyed by entity id (v3:
 /// one ocean authority applies damage; CONTRACT §4.2 "S7p"). Main thread.
 /// </summary>
    internal sealed class S7pCreatureDamage : IDisposable
    {
        public readonly S7EventReader Reader = new S7EventReader();
        private readonly MirrorConsumer _m;
        private bool _registered, _disposed;

        public double Scale = S7pDamageRules.DefaultScale;
        public string ScaleSource = "default";

        private sealed class Tool
        {
            public ulong Entity, Key;
            public byte Type, Flags;
            public ushort Subtype;
            public F32x3 Pos;
            public float Size;
            public float LastRowAt, NextStepAt;
        }

        private readonly Dictionary<long, Tool> _tools = new Dictionary<long, Tool>();
        private readonly List<long> _toolScratch = new List<long>();
        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private readonly Collider[] _cols = new Collider[64];
        private readonly HashSet<LiveMixin> _once = new HashSet<LiveMixin>();
        private readonly Dictionary<ulong, Transform> _charCache = new Dictionary<ulong, Transform>();
        private readonly Dictionary<ulong, float> _charMiss = new Dictionary<ulong, float>();
        private int _hitsThisFrame;

        // counters (s7p.damage: totals and since the previous row)
        public long Events, Shots, Blasts, ToolSteps, Rays, Hits, Kills, Blocked, PassedShooter, MissedEntity, Lapsed, ToolsDropped, EventsDeferred, HitsDropped, DrainedOff;
        public double HpTotal;
        private long _rowHits, _rowKills, _rowShots, _rowBlasts, _rowSteps, _rowHitRows;
        private double _rowHp;
        private ulong _nextRowMs;
        public string LastHit = "";

        public S7pCreatureDamage(MirrorConsumer mirror)
        {
            if (mirror == null) throw new ArgumentNullException("mirror");
            _m = mirror;
        }

        private static float Now { get { return Time.realtimeSinceStartup; } }

        public void Start()
        {
            if (_registered) return;
            if (!_m.C2.Extensions.Contains(Reader)) _m.C2.Extensions.Add(Reader);
            _registered = true;
            S11Hooks.DamageHost = this; // S11 hook: the burst scene's shots
            DeLog.Info("DE.S7p damage start: SE gun/explosion/tool events damage Subnautica's creatures (scale " + Scale.ToString("0.###", CultureInfo.InvariantCulture) +
                       " from " + ScaleSource + "; hand " + S7pDamageRules.HandProjectileHp + " / ship " + S7pDamageRules.ShipProjectileHp + " SN_HP per projectile)");
        }

        public void OnNewSePeer()
        {
            Reader.Reset();
            _tools.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _m.C2.Extensions.Remove(Reader); } catch (Exception) { }
            _tools.Clear();
            _charCache.Clear();
        }

        // ============================================================================================ per frame

        public void Tick(ulong nowMs)
        {
            if (_disposed || !_registered) return;
            _hitsThisFrame = 0;
            float now = Now;
            bool world = Player.main != null;
            if (!world || Scale <= 0)
            {
                // no world or switched off: the queue is drained (never stale), the tools forgotten
                S7EventRow r;
                int n = 0;
                while (n < S7pDamageRules.MaxEventsPerFrame * 4 && Reader.TryDequeue(out r)) { n++; DrainedOff++; }
                _tools.Clear();
            }
            else
            {
                S7EventRow r;
                int n = 0;
                while (Reader.TryDequeue(out r))
                {
                    Events++;
                    try { Apply(ref r, now); }
                    catch (Exception e) { DeLog.WarnOnce("s7p.damage.apply", "DE.S7p damage apply " + EventType.Name(r.type) + " error " + e.GetType().Name + ": " + e.Message + " (logged once)"); }
                    if (++n >= S7pDamageRules.MaxEventsPerFrame) { if (Reader.PendingCount > 0) EventsDeferred++; break; } // the rest next frame
                }
                StepTools(now);
            }
            if (nowMs >= _nextRowMs)
            {
                _nextRowMs = nowMs + 1000;
                Row();
            }
        }

        internal void Apply(ref S7EventRow r, float now)
        {
            byte type = r.type;
            if (type == EventType.BulletStreak) { Shot(ref r); return; }
            if (type == EventType.Explosion) { Blast(ref r); return; }
            if (type == EventType.Welder || type == EventType.Grinder || type == EventType.Drill || type == EventType.PrawnDrill || type == EventType.Repair)
                ToolRow(ref r, now);
        }

        // ============================================================================================ shots

        private void Shot(ref S7EventRow r)
        {
            Shots++;
            _rowShots++;
            Vector3 origin, dir;
            bool character;
            Transform anchor;
            if (!Resolve(ref r, out origin, out dir, out character, out anchor)) { MissedEntity++; return; }
            if (dir.sqrMagnitude < 1e-8f) return;
            dir.Normalize();
            int count = S7pDamageRules.ProjectileCount(r.intensity);
            float range = S7pDamageRules.RayRangeM(r.size);
            float hp = (float)(S7pDamageRules.ProjectileHp(character) * count * Scale);
            Rays++;
            int n = Physics.RaycastNonAlloc(new Ray(origin, dir), _hits, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            SortHits(n);
            ulong root = RootOf(r.entityId);
            LiveMixin inside = S11Hooks.CreatureAround(origin, dir); // S11 hook: a shot fired from inside a creature (a held ship's gun in the jaw) hits it
            if (inside != null) { Damage(inside, hp, origin, S7pDamageRules.SnDamageType(r.type), r.flags, "shot", S11Hooks.ClassOf(_m, r.entityId, character, false)); return; }
            for (int i = 0; i < n; i++)
            {
                Collider c = _hits[i].collider;
                if (c == null) continue;
                if (IsShooter(c, r.entityId, root, anchor)) { PassedShooter++; continue; }
                LiveMixin lm = Utils.FindAncestorWithComponent<LiveMixin>(c.gameObject);
                if (lm != null && Player.main != null && ReferenceEquals(lm, Player.main.liveMixin)) { PassedShooter++; continue; } // the ghost never stops a shot
                if (lm != null && IsCreature(lm))
                {
                    if (!lm.IsAlive()) continue; // a dead body lets the shot through
                    Damage(lm, hp, _hits[i].point, S7pDamageRules.SnDamageType(r.type), r.flags, "shot", S11Hooks.ClassOf(_m, r.entityId, character, false)); // S11 hook: the weapon class
                    return;
                }
                Blocked++; // terrain, a wreck, another grid's proxy: the shot ends there
                return;
            }
        }

        /// <summary>Insertion sort of the first n hits by distance (n ≤ 32; RaycastNonAlloc returns them unordered).</summary>
        private void SortHits(int n)
        {
            for (int i = 1; i < n; i++)
            {
                RaycastHit h = _hits[i];
                int j = i - 1;
                while (j >= 0 && _hits[j].distance > h.distance) { _hits[j + 1] = _hits[j]; j--; }
                _hits[j + 1] = h;
            }
        }

        // ============================================================================================ explosions

        private void Blast(ref S7EventRow r)
        {
            Blasts++;
            _rowBlasts++;
            Vector3 centre, dir;
            bool character;
            Transform anchor;
            if (!Resolve(ref r, out centre, out dir, out character, out anchor)) { MissedEntity++; return; }
            float radius = r.size;
            if (!(radius > 0f) || float.IsInfinity(radius)) return;
            radius = Math.Min(radius, S7pDamageRules.MaxBlastRadiusM); // the overlap query stays small (SE's largest blasts are tens of metres)
            int n = Physics.OverlapSphereNonAlloc(centre, radius, _cols, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            _once.Clear();
            for (int i = 0; i < n; i++)
            {
                Collider c = _cols[i];
                if (c == null) continue;
                LiveMixin lm = Utils.FindAncestorWithComponent<LiveMixin>(c.gameObject);
                if (lm == null || !IsCreature(lm) || !lm.IsAlive() || !_once.Add(lm)) continue;
                float d = S11Hooks.BlastDistance(centre, lm, (centre - lm.transform.position).magnitude); // S11 hook: the creature's nearest collider point (RadiusDamage's transform without S11)
                float hp = S11Hooks.BlastBaseHp(ref r, radius, d, Scale); // S11 hook: SE's own yield (stacked warheads sum), else S7p's capped intensity
                if (hp <= 0f) continue;
                Damage(lm, hp, centre, S7pDamageRules.SnExplosive, r.flags, "blast", S11Hooks.BlastClass(ref r)); // S11 hook: the weapon class of the explosion's origin
            }
            _once.Clear();
        }

        // ============================================================================================ tools

        private void ToolRow(ref S7EventRow r, float now)
        {
            long k = unchecked((long)(r.entityId * 1099511628211UL) ^ (long)r.key);
            Tool t;
            if (r.IsStop) { _tools.Remove(k); return; }
            if (!_tools.TryGetValue(k, out t))
            {
                if (_tools.Count >= S7pDamageRules.MaxSustains) { ToolsDropped++; return; }
                t = new Tool { NextStepAt = now };
                _tools[k] = t;
            }
            t.Entity = r.entityId;
            t.Key = r.key;
            t.Type = r.type;
            t.Flags = r.flags;
            t.Subtype = r.subtype;
            t.Pos = r.pos;
            t.Size = r.size;
            t.LastRowAt = now;
        }

        private void StepTools(float now)
        {
            if (_tools.Count == 0) return;
            float life = S7Conventions.EventLimits.SustainLifetimeMs / 1000f;
            _toolScratch.Clear();
            foreach (KeyValuePair<long, Tool> kv in _tools)
            {
                Tool t = kv.Value;
                if (now - t.LastRowAt > life) { _toolScratch.Add(kv.Key); Lapsed++; continue; }
                if (now < t.NextStepAt) continue;
                t.NextStepAt = now + (float)S7pDamageRules.ToolStepS;
                float hp = S7pDamageRules.ToolStepHp(t.Type, t.Subtype, S7pDamageRules.ToolStepS, Scale);
                if (hp <= 0f) continue;
                ToolSteps++;
                _rowSteps++;
                var row = new S7EventRow { type = t.Type, flags = t.Flags, entityId = t.Entity, pos = t.Pos, subtype = t.Subtype };
                Vector3 p, dir;
                bool character;
                Transform anchor;
                if (!Resolve(ref row, out p, out dir, out character, out anchor)) { MissedEntity++; continue; }
                int n = Physics.OverlapSphereNonAlloc(p, S7pDamageRules.ContactRadius(t.Size), _cols, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                _once.Clear();
                for (int i = 0; i < n; i++)
                {
                    Collider c = _cols[i];
                    if (c == null) continue;
                    LiveMixin lm = Utils.FindAncestorWithComponent<LiveMixin>(c.gameObject);
                    if (lm == null || !IsCreature(lm) || !lm.IsAlive() || !_once.Add(lm)) continue;
                    Damage(lm, hp, p, S7pDamageRules.SnDamageType(t.Type), t.Flags, EventType.Name(t.Type), S11Hooks.ClassOf(_m, t.Entity, character, false)); // S11 hook: the weapon class
                }
                _once.Clear();
            }
            foreach (long k in _toolScratch) _tools.Remove(k);
        }

        // ============================================================================================ damage

        private void Damage(LiveMixin lm, float hp, Vector3 at, int snType, byte flags, string why, int cls)
        {
            if (_hitsThisFrame >= S7pDamageRules.MaxHitsPerFrame && !S11Hooks.BypassHitCap(cls)) { HitsDropped++; return; } // S11 hook: blasts are never dropped
            _hitsThisFrame++;
            hp = S11Hooks.Danger(lm, hp, cls, why); // S11 hook: the weapon-class rule against the creature's own maxHealth (A)
            if (!(hp > 0f)) return;
            float before = lm.health;
            GameObject dealer = (flags & EventFlags.Local) != 0 && Player.main != null ? Player.main.gameObject : null;
            bool killed = lm.TakeDamage(hp, at, (DamageType)snType, dealer);
            S11Hooks.AfterDanger(lm, killed); // S11 hook: the s11.danger row
            Hits++;
            _rowHits++;
            HpTotal += hp;
            _rowHp += hp;
            if (killed) { Kills++; _rowKills++; }
            string name;
            try { name = CraftData.GetTechType(lm.gameObject).ToString(); } catch (Exception) { name = lm.gameObject.name; }
            LastHit = why + " " + name + " " + hp.ToString("0.#", CultureInfo.InvariantCulture) + " SN_HP health " +
                      before.ToString("0.#", CultureInfo.InvariantCulture) + " -> " + lm.health.ToString("0.#", CultureInfo.InvariantCulture) + (killed ? " KILLED" : "");
            if (killed) DeLog.Info("DE.S7p damage KILL " + LastHit);
            if (_rowHitRows < S7pDamageRules.MaxHitRowsPerSecond)
            {
                _rowHitRows++;
                JsonOut j = S5Rows.Begin("s7p.hit");
                j.Str("why", why).Str("creature", name).Num("hp", (double)hp).Num("healthBefore", (double)before).Num("healthAfter", (double)lm.health)
                 .Num("maxHealth", (double)lm.maxHealth).Bool("killed", killed).Num("snType", (long)snType).Bool("local", (flags & EventFlags.Local) != 0)
                 .Vec("at", at.x, at.y, at.z).Num("scale", Scale);
                S5Rows.End(j);
            }
        }

        private static bool IsCreature(LiveMixin lm)
        {
            return lm != null && lm.gameObject.GetComponent<Creature>() != null;
        }

        /// <summary>A collider of the shooter: its own S6 proxy (same entity or root), or under its mirrored Entity.</summary>
        private static bool IsShooter(Collider c, ulong entity, ulong root, Transform anchor)
        {
            if (anchor != null && c.transform.IsChildOf(anchor)) return true;
            ProxyMarker pm = c.GetComponentInParent<ProxyMarker>();
            if (pm == null || entity == 0) return false;
            return pm.EntityId == entity || pm.RootEntityId == entity || (root != 0 && (pm.EntityId == root || pm.RootEntityId == root));
        }

        private ulong RootOf(ulong id)
        {
            GridRecord g;
            if (id != 0 && _m.State.Grids.TryGetValue(id, out g) && g.IsSubgrid) return g.ParentId;
            return id;
        }

        // ============================================================================================ positions

 /// <summary>
 /// The event's world point and direction (CONTRACT <c>format event.frame</c>): attached rows are SE-local metres of
 /// the entity, mirrored (z negated) into its Entity transform; world rows are SN-world already.
 /// </summary>
        private bool Resolve(ref S7EventRow r, out Vector3 pos, out Vector3 dir, out bool character, out Transform anchor)
        {
            character = false;
            anchor = null;
            if (!r.Attached)
            {
                pos = new Vector3(r.pos.x, r.pos.y, r.pos.z);
                dir = new Vector3(r.dir.x, r.dir.y, r.dir.z);
                return true;
            }
            if (!TryEntity(r.entityId, out anchor, out character))
            {
                pos = dir = Vector3.zero;
                return false;
            }
            pos = anchor.TransformPoint(new Vector3(r.pos.x, r.pos.y, -r.pos.z));
            var d = new Vector3(r.dir.x, r.dir.y, -r.dir.z);
            dir = d.sqrMagnitude > 1e-8f ? anchor.TransformDirection(d) : Vector3.zero;
            return true;
        }

        private bool TryEntity(ulong id, out Transform t, out bool character)
        {
            t = null;
            character = false;
            if (id == 0) return false;
            MirrorGridView v;
            if (_m.ViewMap.TryGetValue(id, out v) && v != null && v.Alive && v.HasPose && v.EntityT != null) { t = v.EntityT; return true; }
            CharacterMirror c = _m.Character;
            if (c != null && c.CharacterId == id && c.EntityTransform != null) { t = c.EntityTransform; character = true; return true; }
            if (_charCache.TryGetValue(id, out t) && t != null) { character = true; return true; }
            float now = Now, miss;
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
            character = true;
            return true;
        }

        // ============================================================================================ rows and status

 /// <summary>s7p.damage (1 Hz while anything happened in the last second, every 10 s otherwise).</summary>
        private void Row()
        {
            bool any = _rowShots + _rowBlasts + _rowSteps + _rowHits > 0;
            if (!any && (_quiet++ % 10) != 0) { _rowHitRows = 0; return; }
            JsonOut j = S5Rows.Begin("s7p.damage");
            j.Num("scale", Scale).Num("shots", _rowShots).Num("blasts", _rowBlasts).Num("toolSteps", _rowSteps).Num("hits", _rowHits).Num("kills", _rowKills)
             .Num("hp", _rowHp).Num("tools", (long)_tools.Count)
             .Num("eventsTotal", Events).Num("raysTotal", Rays).Num("hitsTotal", Hits).Num("killsTotal", Kills).Num("hpTotal", HpTotal)
             .Num("blocked", Blocked).Num("passedShooter", PassedShooter).Num("missedEntity", MissedEntity).Num("lapsed", Lapsed)
             .Num("eventsDeferred", EventsDeferred).Num("hitsDropped", HitsDropped).Num("toolsDropped", ToolsDropped).Num("drainedOff", DrainedOff)
             .Num("readerMissed", Reader.Missed).Num("readerOverflow", Reader.Overflow).Num("readerQueued", Reader.Queued).Str("lastHit", LastHit);
            S5Rows.End(j);
            _rowShots = _rowBlasts = _rowSteps = _rowHits = _rowKills = 0;
            _rowHp = 0;
            _rowHitRows = 0;
        }

        private long _quiet;

        public void Status(JsonObjectWriter w)
        {
            w.Num("damageScale", Scale).Str("damageScaleSource", ScaleSource).Bool("damageOn", Scale > 0).Num("damageEvents", Events).Num("damageShots", Shots)
             .Num("damageBlasts", Blasts).Num("damageToolSteps", ToolSteps).Num("damageHits", Hits).Num("damageKills", Kills).Num("damageHp", HpTotal)
             .Num("damageBlocked", Blocked).Num("damageMissedEntity", MissedEntity).Num("damageHitsDropped", HitsDropped).Num("damageEventsDeferred", EventsDeferred)
             .Num("damageReaderMissed", Reader.Missed).Str("damageLastHit", LastHit);
        }
    }
}
