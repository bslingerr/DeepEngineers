using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>

 /// CONTRACT §5.4 <c>format missile.hit</c>). Cause: SE's missiles explode only on SE contacts
 /// or at the end of their range, and the creatures exist only here (no SE
 /// body: the S6 proxies are SE grids mirrored INTO Subnautica, never creatures mirrored into SE), so a rocket flew through
 /// any creature and its explosion, wherever it happened, was measured to the creature's root transform (S7p's
 /// RadiusDamage rule): tens of metres from the hull of a x3 reaper. Rail slugs and artillery shells (MyMissile with a
 /// health pool, explosion damage 0) never touched a creature at all.
 /// Now, every frame, each mirrored missile (GridAdd Missile, S7p P1; no collider on mirrored entities) is swept from its
 /// last pose to this one against <c>Physics.DefaultRaycastLayers</c> (triggers ignored): the nearest collider of a live
 /// creature before any other solid is a hit (its first pose also: an overlap of HitRadiusCm, a missile born inside a
 /// creature, and a cast BackCastCm back along -forward). One MissileHit record per missile (exactly once: the record's
 /// dedupSeq, the missile id kept until the missile leaves); SE moves the missile there and explodes it, and the struck
 /// creature takes that explosion at d = 0 (<see cref="Attribute"/>, within AttributionMs). S11.M15 (the old hit test):
 /// no sweep, blasts measured to the transform. Main thread.
 /// </summary>
    internal sealed class S11Missiles
    {
        private sealed class Track
        {
            public ulong Id;
            public Vector3 Last, First;
            public bool HasLast, Hit, Seen;
            public int Frames;
            public double LastS;
        }

        private sealed class Pending
        {
            public ulong Missile;
            public LiveMixin Creature;
            public Vector3 Point;
            public double AtS;
        }

        private readonly S11Host _host;
        private readonly MirrorConsumer _m;
        private readonly Dictionary<ulong, Track> _tracks = new Dictionary<ulong, Track>();
        private readonly List<ulong> _gone = new List<ulong>();
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly RaycastHit[] _hits = new RaycastHit[32];
        private readonly Collider[] _cols = new Collider[32];
        private readonly int[] _kinds = new int[32];
        public long Seen, Swept, Hits, Blocked, Records, NotEmitted, Attributed, FirstPoseHits;

        public S11Missiles(S11Host host, MirrorConsumer m)
        {
            _host = host;
            _m = m;
        }

        public int Tracked { get { return _tracks.Count; } }

        public void Tick(double now)
        {
            if (_m == null) return;
            foreach (Track t in _tracks.Values) t.Seen = false;
            foreach (GridRecord g in _m.State.Grids.Values)
            {
                if (!S7pScene.IsMissile(g.Flags)) continue;
                MirrorGridView v;
                if (!_m.ViewMap.TryGetValue(g.Id, out v) || v == null || !v.Alive || !v.HasPose || v.EntityT == null) continue;
                Track t;
                if (!_tracks.TryGetValue(g.Id, out t)) { t = new Track { Id = g.Id }; _tracks[g.Id] = t; Seen++; }
                t.Seen = true;
                Vector3 p = v.EntityT.position;
                if (!t.Hit && !S11Mutants.IsOn(S11Mutants.OldHitTest)) Sweep(t, p, v.EntityT.forward, now);
                if (!t.HasLast) t.First = p;
                t.Last = p;
                t.LastS = now;
                t.HasLast = true;
                t.Frames++;
            }
            _gone.Clear();
            foreach (Track t in _tracks.Values) if (!t.Seen) _gone.Add(t.Id);
            for (int i = 0; i < _gone.Count; i++) _tracks.Remove(_gone[i]);
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (now - _pending[i].AtS > S11Conventions.Missile.AttributionMs / 1000.0 || _pending[i].Creature == null) _pending.RemoveAt(i);
        }

        private void Sweep(Track t, Vector3 p, Vector3 fwd, double now)
        {
            Swept++;
            LiveMixin lm = null;
            Vector3 at = p, dir = fwd;
            string how = null;
            if (!t.HasLast)
            {
                // the first pose: born inside a creature (a held ship's launcher in the jaw), or the flight before the mirror existed
                float back = S11Conventions.Missile.BackCastCm / 100f;
                Vector3 f = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
                lm = Inside(p, out at);
                if (lm != null) how = "inside";
                if (lm == null)
                {
                    // fired from a held ship: the holder's jaw within the grid's reach of its first flight ("shooting the holder")
                    lm = _host.Grabs.HolderNearSegment(p - f * back, p, out at);
                    if (lm != null) how = "holder";
                }
                if (lm == null)
                {
                    lm = Segment(p, p - f * back, out at); // from the pose back toward the launcher: the nearest creature before any hull
                    if (lm != null) how = "backcast";
                }
                if (lm != null) FirstPoseHits++;
            }
            else
            {
                Vector3 d = p - t.Last;
                if (d.sqrMagnitude > 1e-8f) dir = d.normalized;
                lm = Segment(t.Last, p, out at);
                if (lm != null) how = "segment";
                else
                {
                    lm = Inside(p, out at);
                    if (lm != null) how = "inside";
                }
                if (lm == null && t.Frames == 1 && now > t.LastS)
                {
                    //  at the second pose the speed is known; a fast slug (2,000 m/s) flew 30-100 m before its mirror existed
                    double back = S11MissileRules.BackCastM((p - t.Last).magnitude / (now - t.LastS));
                    if (back > S11Conventions.Missile.BackCastCm / 100.0 + 1e-6)
                    {
                        lm = Segment(t.First, t.First - dir * (float)back, out at);
                        if (lm != null) how = "backcastSpeed";
                    }
                }
            }
            if (lm == null) return;
            t.Hit = true;
            Hits++;
            string tech;
            try { tech = CraftData.GetTechType(lm.gameObject).ToString(); } catch (Exception) { tech = lm.gameObject.name; }
            int techId;
            try { techId = (int)CraftData.GetTechType(lm.gameObject); } catch (Exception) { techId = 0; }
            if (dir.sqrMagnitude < 0.25f) dir = Vector3.forward;
            string id;
            int kind = how == "holder" ? S11Conventions.Missile.HitHolder : S11Conventions.Missile.HitBody; //  SE spares the held grid
            bool ok = _host.EmitEntity(S11Conventions.Unit.MissileHit, t.Id, 1f, kind, at, dir.normalized, techId, (uint)lm.gameObject.GetInstanceID(), out id);
            if (ok) Records++; else NotEmitted++;
            _pending.Add(new Pending { Missile = t.Id, Creature = lm, Point = at, AtS = now });
            JsonOut j = S5Rows.Begin("s11.missile");
            j.Str("phase", "hit").Str("missile", t.Id.ToString(CultureInfo.InvariantCulture)).Str("creature", tech).Num("instance", (long)lm.gameObject.GetInstanceID())
             .Vec("at", at.x, at.y, at.z).Str("how", how).Num("frames", (long)t.Frames).Str("id", id ?? "").Bool("sent", ok);
            S5Rows.End(j);
        }

        /// <summary>A live creature whose collider contains the point (an overlap of HitRadiusCm; the nearest), or null.</summary>
        private LiveMixin Inside(Vector3 p, out Vector3 at)
        {
            at = p;
            int n = Physics.OverlapSphereNonAlloc(p, S11Conventions.Missile.HitRadiusCm / 100f, _cols, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            LiveMixin best = null;
            float bd = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                LiveMixin lm = CreatureOf(_cols[i]);
                if (lm == null) continue;
                Vector3 c = S11Host.ClosestPoint(_cols[i], p);
                float d = (c - p).sqrMagnitude;
                if (d < bd) { bd = d; best = lm; at = c; }
            }
            return best;
        }

        /// <summary>The first creature on the segment before any other solid (the ghost Player and proxies of no grid pass), or null.</summary>
        private LiveMixin Segment(Vector3 a, Vector3 b, out Vector3 at)
        {
            at = b;
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 1e-3f) return null;
            int n = Physics.RaycastNonAlloc(new Ray(a, d / len), _hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 1; i < n; i++)
            {
                RaycastHit h = _hits[i];
                int j = i - 1;
                while (j >= 0 && _hits[j].distance > h.distance) { _hits[j + 1] = _hits[j]; j--; }
                _hits[j + 1] = h;
            }
            for (int i = 0; i < n; i++) _kinds[i] = KindOf(_hits[i].collider);
            int k = S11MissileRules.FirstCreature(_kinds, n);
            if (k < 0) { if (n > 0) Blocked++; return null; }
            at = _hits[k].point;
            return CreatureOf(_hits[k].collider);
        }

        /// <summary>0 passes (the ghost Player, nothing), 1 a live creature, 2 another solid (terrain, a grid's proxy, a wreck).</summary>
        private static int KindOf(Collider c)
        {
            if (c == null) return 0;
            LiveMixin lm = Utils.FindAncestorWithComponent<LiveMixin>(c.gameObject);
            if (lm != null && Player.main != null && ReferenceEquals(lm, Player.main.liveMixin)) return 0;
            if (lm != null && lm.gameObject.GetComponent<Creature>() != null) return lm.IsAlive() ? 1 : 0;
            return 2;
        }

        private static LiveMixin CreatureOf(Collider c)
        {
            if (c == null) return null;
            LiveMixin lm = Utils.FindAncestorWithComponent<LiveMixin>(c.gameObject);
            if (lm == null || !lm.IsAlive() || lm.gameObject.GetComponent<Creature>() == null) return null;
            if (Player.main != null && ReferenceEquals(lm, Player.main.liveMixin)) return null;
            return lm;
        }

        /// <summary>
 /// The blast distance rule's d = 0 (format blast.damage, missile.hit): this creature was struck by a missile within
 /// AttributionMs, at a point within JoinPadM of the explosion centre; consumed once. The missile id, or 0.
 /// </summary>
        public ulong Attribute(LiveMixin lm, Vector3 centre, double now)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                Pending p = _pending[i];
                if (!ReferenceEquals(p.Creature, lm)) continue;
                if (!S11MissileRules.Attributed((now - p.AtS) * 1000.0, (p.Point - centre).magnitude)) continue;
                _pending.RemoveAt(i);
                Attributed++;
                return p.Missile;
            }
            return 0;
        }

        public void Clear()
        {
            _tracks.Clear();
            _pending.Clear();
        }
    }
}
