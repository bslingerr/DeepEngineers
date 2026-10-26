using System;
using System.Collections.Generic;
using System.Globalization;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S11 C, the grabs (ROADMAP §14 S11 point 2; CONTRACT §5.4 "S11 conventions", unit Grab). Subnautica's reaper grabs only a
 /// concrete SeaMoth or Exosuit
 /// the sea dragon only an Exosuit
 /// S11 gives SE grids the same treatment when the grid's own box fits the
 /// creature's size class (<see cref="S11GrabRules.SizeFits"/>; bigger grids are bitten and pushed, never held):
 /// <list type="bullet">
 /// <item>start under the creature's own conditions (alive, bite interval elapsed, the reaper's Aggression ≥ 0.5, no
 /// death cinematic, the 10 s cooldown after a release, not holding), Aggression set to 0 as GrabVehicle does, the
 /// creature's colliders ignore the grid's sections while it holds (Physics.IgnoreCollision), the reaper's
 /// "seamoth_attack" animator bool held on (its Update writes IsHoldingSeamoth every frame: a postfix re-asserts it);</item>
 /// <item>a hold record every 50 ms with the attach point (seamothAttachPoint / exosuitAttachPoint) for SE's spring-damper
 /// hold (SE owns the physics; never a teleport);</item>
 /// <item>the hold damage as the creature's own: TakeDamage(dps × interval, Normal, the creature) on the held section every
 /// interval, which S6 forwards as an ordinary bite;</item>
 /// <item>release on the creature's own release rules: the planned time (8 + Random·5 s for the reaper, 3 s for the sea
 /// dragon's throw), an Electrical or Poison hit (ReaperLeviathan.OnTakeDamage), death or OnDisable, the grid gone.</item>
 /// </list>
 /// Main thread.
 /// </summary>
    internal sealed class S11Grabs
    {
        private sealed class Grab
        {
            public Creature Creature;
            public LiveMixin CreatureLive;
            public Transform Attach;
            public S11GrabRow Row;
            public ProxyMarker Section;
            public ulong Root;
            public uint Key;
            public S11GrabClock Clock;
            public float Dps;
            public readonly List<KeyValuePair<Collider, Collider>> Ignored = new List<KeyValuePair<Collider, Collider>>();
            public bool Reaper;
            public long Holds;
            public readonly S11FollowWatch Follow = new S11FollowWatch();
            public string Why;
            public double LastGapM = double.NaN;
            //: the rage book of this hold (format rage.hold), the jaw rows, the collision ignores of new proxy colliders
            public readonly S11HoldRage Rage = new S11HoldRage();
            public S11RageRow RageRow;
            public bool HasRage, Forced;
            public double NextJawS, NextIgnoreS, MaxMouthY = double.NegativeInfinity;
            public readonly HashSet<int> IgnoredProxies = new HashSet<int>();
            public Collider[] Mine;
            /// <summary>The held grid's half diagonal + 4 m: a shot or a missile fired from within it hits the holder.</summary>
            public double ReachM = 6.0;
            /// <summary>Review 4: the held grid's half height (its proxies' world bounds, refreshed every 0.25 s).</summary>
            public double GridHalfH;
            /// <summary>In-game 144924 (C6: a x3 reaper's held thrash moved the jaw at 145 m/s): the animator's speed before the hold.</summary>
            public Animator Anim;
            public float AnimSpeed = 1f;
        }

        /// <summary>format rage.hold: a creature that let go by damage flees (no grab, no attack) until EndS.</summary>
        private sealed class Flee
        {
            public Creature C;
            public double EndS, NextSwimS;
            public Vector3 From;
        }

        private readonly S11Host _host;
        private readonly S6Host _s6;
        private readonly MirrorConsumer _m;
        private readonly Dictionary<int, Grab> _active = new Dictionary<int, Grab>();
        private readonly Dictionary<int, double> _releasedAt = new Dictionary<int, double>();
        private readonly List<int> _scratch = new List<int>();
        private static readonly System.Random Rng = new System.Random();

        public long Started, Released, RefusedSize, RefusedOther, RefusedDebris, HoldRecords, HoldDamage, SurfaceReleases, Rages, RageReleases, Flees, IgnoreRefreshes;
        private readonly Dictionary<int, Flee> _fleeing = new Dictionary<int, Flee>();
        public string LastRefusal = "";
        /// <summary>The planned hold of a scene's forced grab (0 = the creature's own 8 + Random·5 s).</summary>
        public double ForcedHoldS;

        public S11Grabs(S11Host host, S6Host s6, MirrorConsumer m)
        {
            _host = host;
            _s6 = s6;
            _m = m;
        }

        public int Held { get { return _active.Count; } }

        public bool IsHolding(Creature c) { return c != null && _active.ContainsKey(c.GetInstanceID()); }

        /// <summary>format rage.hold: the creature let go by damage and flees.</summary>
        public bool IsFleeing(Creature c) { return c != null && _fleeing.ContainsKey(c.GetInstanceID()); }

        /// <summary>
 /// ("shooting the holder"): a shot fired from inside the held grid's reach of a holding creature's jaw (the
 /// ship sits in it) hits the holder, whatever its colliders look like inside the mouth. The holder, or null.
 /// </summary>
        public LiveMixin HolderAround(Vector3 p, Vector3 dir)
        {
            foreach (Grab g in _active.Values)
            {
                if (g.Attach == null || g.CreatureLive == null || !g.CreatureLive.IsAlive()) continue;
                if ((g.Attach.position - p).magnitude > g.ReachM) continue;
                //  only a shot that points at the holder's body (its bounds centre); a gun aimed away leaves the jaw
                if (dir.sqrMagnitude > 1e-8f && g.Creature != null)
                {
                    Bounds b = S11Host.BodyBounds(g.Mine, g.Creature.transform);
                    if (Vector3.Dot(dir, b.center - p) <= 0f) continue;
                }
                return g.CreatureLive;
            }
            return null;
        }

        /// <summary>The same for a missile's first segment (its flight before its mirror existed): the holder whose jaw lies within reach of it.</summary>
        public LiveMixin HolderNearSegment(Vector3 a, Vector3 b, out Vector3 at)
        {
            at = b;
            foreach (Grab g in _active.Values)
            {
                if (g.Attach == null || g.CreatureLive == null || !g.CreatureLive.IsAlive()) continue;
                Vector3 j = g.Attach.position, d = b - a;
                float t = d.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(j - a, d) / d.sqrMagnitude) : 0f;
                Vector3 c = a + d * t;
                if ((c - j).magnitude <= g.ReachM) { at = c; return g.CreatureLive; }
            }
            return null;
        }

        public struct HolderInfo { public Creature Creature; public Transform Attach; public double GridTop; }

        /// <summary>The holders now (the surface sampler and guard, S11.C7): the creature, its jaw, the held grid's top.</summary>
        public IEnumerable<HolderInfo> Holders()
        {
            foreach (Grab g in _active.Values)
                if (g.Creature != null) yield return new HolderInfo { Creature = g.Creature, Attach = g.Attach, GridTop = g.Attach != null ? g.Attach.position.y + g.GridHalfH : double.NaN };
        }

        /// <summary>The held grid's proxy bounds (world) and whether it has any.</summary>
        private bool GridBounds(ulong root, out Bounds b)
        {
            bool any = false;
            b = default(Bounds);
            foreach (ProxySection s in _s6.Proxies.AllSections())
            {
                if (s == null || s.Owner == null || s.Marker == null) continue;
                if (s.Owner.EntityId != root && s.Owner.RootId != root) continue;
                foreach (Collider c in s.Marker.Colliders)
                {
                    if (c == null || !c.enabled) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
            }
            return any;
        }

        /// <summary>
 /// S11.
 /// skin box centre (SE-local metres, z negated into the Entity, as S7p resolves attached rows), not the proxies (they lag).
 /// </summary>
        private bool DrawnCentre(ulong root, out Vector3 p)
        {
            p = Vector3.zero;
            MirrorGridView v;
            SkinEntry e;
            if (_m == null || !_m.ViewMap.TryGetValue(root, out v) || v == null || !v.Alive || !v.HasPose || v.EntityT == null) return false;
            if (!_m.State.Skins.Skins.TryGetValue(root, out e)) return false;
            Vec3 min, max;
            if (!S11GrabRules.SkinExtents(e.Sections, out min, out max)) return false;
            Vec3 c = (min + max) * 0.5;
            p = v.EntityT.TransformPoint(new Vector3((float)c.X, (float)c.Y, (float)-c.Z));
            return true;
        }

        /// <summary>Review 7: Subnautica's mirror knows a subgrid by its parent (GridRecord.ParentId): such a grid is never held (SE refuses too).</summary>
        private bool HasSubgrids(ulong root)
        {
            if (_m == null) return false;
            foreach (GridRecord r in _m.State.Grids.Values) if (r.IsSubgrid && r.ParentId == root) return true;
            return false;
        }

        /// <summary>Review 4: the highest of the creature's bounds top and the held grid's (its half height above the jaw).</summary>
        private double TopOf(Creature c, Transform attach, double gridHalfH)
        {
            double top;
            _host.HalfHeight(c, out top);
            return attach != null ? Math.Max(top, attach.position.y + gridHalfH) : top;
        }

        public bool Holds(Creature c, ulong root)
        {
            Grab g;
            return c != null && _active.TryGetValue(c.GetInstanceID(), out g) && g.Root == root;
        }

        private bool RootHeld(ulong root)
        {
            foreach (Grab g in _active.Values) if (g.Root == root) return true;
            return false;
        }

        // ------------------------------------------------------------------------------------------ start

 /// <summary>
 /// ReaperMeleeAttack.OnTouch prefix: false = skip the original (this reaper holds a grid: no bite while holding,
 /// as the game's IsHoldingVehicle return). A grab starts here when the touched target is a grabbable grid section.
 /// </summary>
        public bool OnReaperTouch(ReaperMeleeAttack atk, Collider collider, float timeLastBite)
        {
            ReaperLeviathan reaper = atk.reaper;
            if (reaper == null) return true;
            if (IsHolding(reaper)) return false;
            if (_s6 == null || collider == null) return true;
            // the game's own conditions
            if (atk.liveMixin == null || !atk.liveMixin.IsAlive() || !(Time.time > timeLastBite + atk.biteInterval) || !(reaper.Aggression.Value >= 0.5f)) return true;
            if (reaper.IsHoldingVehicle() || (atk.playerDeathCinematic != null && atk.playerDeathCinematic.IsCinematicModeActive())) return true;
            GameObject target = atk.GetTarget(collider);
            TryStart(reaper, target, reaper.seamothAttachPoint, reaper.seamothDamagePerSecond, true, false);
            // the original goes on; after a grab start Aggression is 0 (as GrabVehicle), so its own Aggression >= 0.5 test returns at
 // once: the grab start makes no bite (the hold damage follows every second); without a grab it bites the section
            return true;
        }

        /// <summary>SeaDragonMeleeAttack.OnTouchFront prefix: a sea dragon grabs a grabbable grid section (SeaDragon.GrabExosuit's attach rule).</summary>
        public void OnDragonTouch(SeaDragonMeleeAttack atk, Collider collider)
        {
            SeaDragon dragon = atk.seaDragon;
            if (dragon == null || _s6 == null || collider == null || IsHolding(dragon)) return;
            if (atk.liveMixin == null || !atk.liveMixin.IsAlive() || dragon.IsHoldingExosuit()) return;
            if (atk.playerDeathCinematic != null && atk.playerDeathCinematic.IsCinematicModeActive()) return;
            GameObject target = atk.GetTarget(collider);
            if (target == null || dragon.exosuitAttachPoint == null) return;
            if (dragon.exosuitAttachPoint.InverseTransformPoint(target.transform.position).y < -3f) return; // SeaDragon.GrabExosuit:44
            TryStart(dragon, target, dragon.exosuitAttachPoint, dragon.exosuitDamagePerSecond, false, false);
        }

        /// <summary>
 /// the scenes (holdhit, surface): a real creature made to hold a grid section now, under every grid rule
 /// (size class, debris, static) but not the touch conditions, the cooldown or a flee. False with <see cref="LastRefusal"/>.
 /// </summary>
        public bool ForceStart(Creature c, ProxyMarker pm)
        {
            LastRefusal = "";
            if (c == null || pm == null) { LastRefusal = "no creature or no section"; return false; }
            if (IsHolding(c)) { LastRefusal = "already holding"; return false; }
            var rl = c as ReaperLeviathan;
            var sd = c as SeaDragon;
            if (rl != null) return TryStart(rl, pm.gameObject, rl.seamothAttachPoint, rl.seamothDamagePerSecond, true, true);
            if (sd != null) return TryStart(sd, pm.gameObject, sd.exosuitAttachPoint, sd.exosuitDamagePerSecond, false, true);
            LastRefusal = "not a reaper or a sea dragon";
            return false;
        }

        private bool TryStart(Creature c, GameObject target, Transform attach, float dps, bool reaper, bool forced)
        {
            ProxyMarker pm = S11Hooks.MarkerOf(target);
            if (pm == null || pm.Kind != DamageTargetKind.GridSection || pm.SelfTest || attach == null) { LastRefusal = "not a grid section"; return false; }
            string tech;
            try { tech = CraftData.GetTechType(c.gameObject).ToString(); } catch (Exception) { LastRefusal = "no TechType"; return false; }
            S11GrabRow row;
            if (!S11Conventions.TryGrabRow(tech, out row)) { LastRefusal = "no grab row"; return false; }
            double now = Time.realtimeSinceStartup;
            double rel;
            if (!forced && _releasedAt.TryGetValue(c.GetInstanceID(), out rel) && !S11GrabClock.CooldownOver(rel, now, row)) { RefusedOther++; LastRefusal = "cooldown"; return false; }
            if (!forced && IsFleeing(c)) { RefusedOther++; LastRefusal = "fleeing"; return false; } // format rage.hold: no grab while it flees
            ulong root = pm.RootEntityId != 0 ? pm.RootEntityId : pm.EntityId;
            Bounds gb;
            double gridHalf = GridBounds(root, out gb) ? gb.extents.y : 0.0;
            // format surface.release: no grab where the creature's or the grid's top is within the margin of the surface
            if (!S11Mutants.IsOn(S11Mutants.NoSurface) && S11SurfaceRules.TopReleases(-TopOf(c, attach, gridHalf))) { RefusedOther++; LastRefusal = "surface"; return false; }
            if (HasSubgrids(root)) { RefusedOther++; LastRefusal = "subgrids"; return false; }
            if (RootHeld(root)) { RefusedOther++; LastRefusal = "grid held"; return false; }
            if (_host.IsDebris(root))
            {
                // pass 6: a remnant is never a grab candidate (format debris.grid); SE refuses it too (S11Reject.Debris)
                RefusedDebris++;
                Vec3 dmin, dmax;
                SizeOf(root, out dmin, out dmax);
                GrabRow("refused", tech, root, dmin, dmax, false, 0, null, "debris");
                LastRefusal = "debris";
                return false;
            }
            GridRecord gr;
            if (_m != null && _m.State.Grids.TryGetValue(root, out gr) && (gr.Flags & MirrorGridFlags.Static) != 0)
            {
                RefusedOther++; //  SE refuses a static grid (S11Reject.Static); the mirror knows it (GridAdd flags)
                LastRefusal = "static";
                return false;
            }
            Vec3 min, max;
            bool fits = SizeOf(root, out min, out max) && S11GrabRules.SizeFits(row, max.X - min.X, max.Y - min.Y, max.Z - min.Z);
            if (!fits && !S11Mutants.IsOn(S11Mutants.NoSizeClass))
            {
                RefusedSize++;
                GrabRow("refused", tech, root, min, max, false, 0, null, "size class: bitten and pushed");
                LastRefusal = "size class";
                return false;
            }
            var g = new Grab
            {
                Creature = c, CreatureLive = c.liveMixin, Attach = attach, Row = row, Section = pm, Root = root, Reaper = reaper,
                Key = (uint)c.GetInstanceID(), Dps = dps, Forced = forced,
            };
            int techInt = (int)CraftData.GetTechType(c.gameObject);
            S11HoldRageRow(g, techInt);
            g.ReachM = 0.5 * Math.Sqrt((max.X - min.X) * (max.X - min.X) + (max.Y - min.Y) * (max.Y - min.Y) + (max.Z - min.Z) * (max.Z - min.Z)) + 4.0;
            g.GridHalfH = gridHalf;
            double hold = S11GrabRules.HoldSeconds(row, Rng.NextDouble());
            if (forced && ForcedHoldS > 0) hold = ForcedHoldS; // the scenes' short holds
            g.Clock = new S11GrabClock(now, hold, row);
            string id;
            if (!_host.EmitGrid(S11Conventions.Unit.Grab, pm, (float)hold, S11Conventions.GrabPhase.Start, 0, attach.position, attach.forward, attach.up, (int)CraftData.GetTechType(c.gameObject), g.Key, out id))
            {
                RefusedOther++;
                GrabRow("refused", tech, root, min, max, fits, hold, null, "record not written");
                LastRefusal = "record not written";
                return false;
            }
            c.Aggression.Value = 0f; // ReaperLeviathan.GrabVehicle / SeaDragon.GrabExosuit
 // GrabVehicle
 // seamothGrabSound.Play, SeaDragon.GrabExosuit Utils.PlayFMODAsset(exosuitGrabSound, transform)
            try
            {
                var rl = c as ReaperLeviathan;
                var sd = c as SeaDragon;
                if (rl != null && rl.seamothGrabSound != null) rl.seamothGrabSound.Play();
                else if (sd != null && sd.exosuitGrabSound != null) Utils.PlayFMODAsset(sd.exosuitGrabSound, sd.transform);
            }
            catch (Exception) { }
            IgnoreCollisions(g, true);
            // in-game 144924: the scaled jaw swings scale x faster than the creature Subnautica animates (its held-Seamoth thrash at 145
 // m/s for a x3 reaper); while holding, the animation plays at 1 / scale
 // so the jaw moves at the unscaled creature's speed and the held grid keeps up
            try
            {
                g.Anim = c.GetAnimator();
                float sc = c.transform.localScale.x;
                if (g.Anim != null && sc > 1.01f) { g.AnimSpeed = g.Anim.speed; g.Anim.speed = g.AnimSpeed / sc; }
                else g.Anim = null;
            }
            catch (Exception) { g.Anim = null; }
            _active[c.GetInstanceID()] = g;
            Started++;
            DeLog.Info("DE.S11 grab START " + tech + " holds grid " + S6Rows.Hex(root) + " for " + hold.ToString("0.0", CultureInfo.InvariantCulture) + " s (box " +
                       (max.X - min.X).ToString("0.0", CultureInfo.InvariantCulture) + "x" + (max.Y - min.Y).ToString("0.0", CultureInfo.InvariantCulture) + "x" + (max.Z - min.Z).ToString("0.0", CultureInfo.InvariantCulture) +
                       " m, size class " + row.MaxVolumeM3 + " m3 / " + row.MaxEdgeM.ToString("0.#", CultureInfo.InvariantCulture) + " m" + (fits ? "" : ", S11.M4 IGNORED IT") + ", record " + id + ")");
            GrabRow("start", tech, root, min, max, fits, hold, id, forced ? "forced (scene)" : null);
            return true;
        }

        private void S11HoldRageRow(Grab g, int techInt)
        {
            S11RageRow rr;
            g.HasRage = _host.Rage.TryGet(techInt, out rr);
            g.RageRow = rr;
        }

        /// <summary>The root grid's box from its skin (the S6 SkinSet Subnautica holds; the grid's own frame).</summary>
        private bool SizeOf(ulong root, out Vec3 min, out Vec3 max)
        {
            SkinEntry e;
            if (_m != null && _m.State.Skins.Skins.TryGetValue(root, out e)) return S11GrabRules.SkinExtents(e.Sections, out min, out max);
            min = max = Vec3.Zero;
            return false;
        }

        private void IgnoreCollisions(Grab g, bool ignore)
        {
            if (ignore)
            {
                Collider[] mine = g.Mine ?? (g.Mine = g.Creature.GetComponentsInChildren<Collider>(true));
                foreach (ProxySection s in _s6.Proxies.AllSections())
                {
                    if (s == null || s.Owner == null || s.Marker == null) continue;
                    if (s.Owner.EntityId != g.Root && s.Owner.RootId != g.Root) continue;
                    foreach (Collider pc in s.Marker.Colliders)
                    {
                        //: a hold's damage changes the grid's skin, and its proxy colliders are rebuilt: each new one is
 // ignored too (refreshed every 0.25 s), or the kinematic proxy shoves the creature it sits in
                        if (pc == null || !g.IgnoredProxies.Add(pc.GetInstanceID())) continue;
                        foreach (Collider cc in mine)
                        {
                            if (cc == null || cc.isTrigger) continue;
                            Physics.IgnoreCollision(cc, pc, true);
                            g.Ignored.Add(new KeyValuePair<Collider, Collider>(cc, pc));
                        }
                    }
                }
                return;
            }
            foreach (KeyValuePair<Collider, Collider> kv in g.Ignored)
                if (kv.Key != null && kv.Value != null) Physics.IgnoreCollision(kv.Key, kv.Value, false);
            g.Ignored.Clear();
            g.IgnoredProxies.Clear();
        }

        // ------------------------------------------------------------------------------------------ per frame

        public void Tick(double now)
        {
            if (_fleeing.Count > 0) FleeTick(now);
            if (_active.Count == 0) return;
            _scratch.Clear();
            foreach (KeyValuePair<int, Grab> kv in _active)
            {
                Grab g = kv.Value;
                string why = null;
                if (g.Creature == null || !g.Creature.gameObject.activeInHierarchy) why = "creature gone";
                else if (g.CreatureLive != null && !g.CreatureLive.IsAlive()) why = "creature dead";
                else if (g.Section == null || g.Section.Live == null) why = "grid gone";
                else if (g.Clock.Expired(now)) why = "hold ended";
                else if (g.Attach != null && g.Follow.Lost(now, g.Clock.StartS, g.LastGapM = GapM(g))) why = "grid not following (SE refused or let go)";
                else if (g.Attach != null && !S11Mutants.IsOn(S11Mutants.NoSurface) && S11SurfaceRules.TopReleases(-TopOf(g.Creature, g.Attach, g.GridHalfH)))
                {
                    // format surface.release: the creature's or the held grid's top within MarginCm of the surface (SN y =
 // the altitude above the sea sphere)
                    why = "surface (top " + (-TopOf(g.Creature, g.Attach, g.GridHalfH)).ToString("0.0", CultureInfo.InvariantCulture) + " m deep)";
                    SurfaceReleases++;
                }
                if (g.Attach != null && g.Attach.position.y > g.MaxMouthY) g.MaxMouthY = g.Attach.position.y;
                if (why == null && now >= g.NextIgnoreS) { g.NextIgnoreS = now + 0.25; try { IgnoreCollisions(g, true); IgnoreRefreshes++; } catch (Exception) { } }
                if (now >= g.NextJawS) { g.NextJawS = now + 0.25; JawRow(g, now, why); }
                if (why != null) { g.Why = why; _scratch.Add(kv.Key); continue; }
                if (g.Clock.HoldDue(now) && g.Attach != null)
                {
                    string id;
                    if (_host.EmitGrid(S11Conventions.Unit.Grab, g.Section, 1f, S11Conventions.GrabPhase.Hold, 0, g.Attach.position, g.Attach.forward, g.Attach.up, (int)CraftData.GetTechType(g.Creature.gameObject), g.Key, out id))
                    {
                        HoldRecords++;
                        g.Holds++;
                    }
                }
                if (g.Clock.DamageDue(now))
                {
                    double hp = S11GrabRules.HoldDamage(g.Row, g.Dps) * (g.HasRage && !S11Mutants.IsOn(S11Mutants.NoRage) ? g.Rage.DamageMult(g.RageRow) : 1.0); // format rage.hold: angrier
                    if (hp > 0 && g.Section.Live != null)
                    {
                        g.Section.Live.TakeDamage((float)hp, g.Attach != null ? g.Attach.position : g.Section.transform.position, DamageType.Normal, g.Creature.gameObject); // the creature's DamageVehicle, on the held section
                        HoldDamage++;
                    }
                }
            }
            foreach (int k in _scratch)
            {
                Grab g;
                if (_active.TryGetValue(k, out g)) Release(k, g, g.Why ?? ReasonOf(g, now));
            }
        }

        /// <summary>The gap between the mouth and the held grid: the nearest point of its proxy colliders to the attach point (m).</summary>
        private double GapM(Grab g)
        {
            double best = double.NaN;
            Vector3 a = g.Attach.position;
            foreach (ProxySection s in _s6.Proxies.AllSections())
            {
                if (s == null || s.Owner == null || s.Marker == null) continue;
                if (s.Owner.EntityId != g.Root && s.Owner.RootId != g.Root) continue;
                foreach (Collider c in s.Marker.Colliders)
                {
                    if (c == null || !c.enabled) continue;
                    double d = (c.ClosestPoint(a) - a).magnitude;
                    if (double.IsNaN(best) || d < best) best = d;
                }
            }
            return best;
        }

        private static string ReasonOf(Grab g, double now)
        {
            if (g.Creature == null || !g.Creature.gameObject.activeInHierarchy) return "creature gone";
            if (g.CreatureLive != null && !g.CreatureLive.IsAlive()) return "creature dead";
            if (g.Section == null || g.Section.Live == null) return "grid gone";
            return "hold ended";
        }

        /// <summary>
 /// s11.jaw: the attach point (the jaw centre,
 /// where Subnautica holds a Seamoth), the centre of the held grid's proxy colliders (their world bounds), the gap between
 /// them, the mouth's and the creature's height (SN y = altitude above the sea sphere).
 /// </summary>
        private void JawRow(Grab g, double now, string why)
        {
            if (g.Attach == null || g.Creature == null) return;
            Bounds b;
            bool any = GridBounds(g.Root, out b);
            if (any) g.GridHalfH = b.extents.y;
            Vector3 a = g.Attach.position;
            Vector3 drawn;
            bool hasDrawn = DrawnCentre(g.Root, out drawn);
            JsonOut j = S5Rows.Begin("s11.jaw");
            j.Num("key", (long)g.Key).Str("creature", g.Row.Creature).Num("ageS", now - g.Clock.StartS).Num("centreGapM", any ? (double)(b.center - a).magnitude : double.NaN)
             .Num("drawnGapM", hasDrawn ? (double)(drawn - a).magnitude : double.NaN).Num("blendS", g.Row.BlendMs / 1000.0)
             .Num("mouthY", (double)a.y).Num("creatureY", (double)g.Creature.transform.position.y).Num("gapM", g.LastGapM)
             .Num("rageMult", g.HasRage ? g.Rage.DamageMult(g.RageRow) : 1.0).Bool("forced", g.Forced).Str("why", why ?? "");
            S5Rows.End(j);
        }

        /// <summary>
 /// format rage.
 /// from S11Host.AfterDanger, the SN_HP an SE weapon just dealt to a creature (danger.applied). A holder's book decides,
 /// each outcome once per hold: angrier
 /// or it lets go and flees (<see cref="StartFlee"/>). S11.M16: nothing.
 /// </summary>
        public void OnHolderHit(LiveMixin lm, double applied, string why)
        {
            if (lm == null || !(applied > 0)) return;
            Creature c = lm.GetComponent<Creature>();
            if (c == null) return;
            int k = c.GetInstanceID();
            Grab g;
            if (!_active.TryGetValue(k, out g)) return;
            if (!g.HasRage || S11Mutants.IsOn(S11Mutants.NoRage)) { RageRow(g, lm, "ignored", applied, why); return; }
            S11RageOutcome o = g.Rage.Hit(applied, lm.maxHealth, g.RageRow);
            double now = Time.realtimeSinceStartup;
            if (o == S11RageOutcome.Rage)
            {
                Rages++;
                g.Clock.EndS += g.RageRow.ExtendMs / 1000.0;
                c.Aggression.Add(1f);
                RageRow(g, lm, "rage", applied, why);
                DeLog.Info("DE.S11 rage: " + g.Row.Creature + " hit for " + applied.ToString("0", CultureInfo.InvariantCulture) + " SN_HP while holding (" + g.Rage.Taken.ToString("0", CultureInfo.InvariantCulture) +
                           " of " + S11HoldRage.Threshold(lm.maxHealth, g.RageRow).ToString("0", CultureInfo.InvariantCulture) + "): angrier, hold damage x" + g.Rage.DamageMult(g.RageRow).ToString("0.##", CultureInfo.InvariantCulture) +
                           ", hold +" + (g.RageRow.ExtendMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s");
            }
            else if (o == S11RageOutcome.Release)
            {
                RageReleases++;
                RageRow(g, lm, "release", applied, why);
                Release(k, g, "rage release: " + g.Rage.Taken.ToString("0", CultureInfo.InvariantCulture) + " SN_HP taken (threshold " + S11HoldRage.Threshold(lm.maxHealth, g.RageRow).ToString("0", CultureInfo.InvariantCulture) + ")");
                StartFlee(c, g, now);
            }
            else RageRow(g, lm, "taken", applied, why);
        }

        private void RageRow(Grab g, LiveMixin lm, string outcome, double applied, string why)
        {
            JsonOut j = S5Rows.Begin("s11.rage");
            j.Num("key", (long)g.Key).Str("creature", g.Row.Creature).Str("outcome", outcome).Num("applied", applied).Num("taken", g.Rage.Taken)
             .Num("threshold", S11HoldRage.Threshold(lm.maxHealth, g.RageRow)).Num("maxHealth", (double)lm.maxHealth).Num("hits", (long)g.Rage.Hits)
             .Num("angerMult", g.Rage.DamageMult(g.RageRow)).Num("extendS", g.Rage.Raged ? g.RageRow.ExtendMs / 1000.0 : 0.0).Bool("raged", g.Rage.Raged)
             .Num("holdAgeS", Time.realtimeSinceStartup - g.Clock.StartS).Str("why", why ?? "").Str("mutants", S11Mutants.Describe());
            S5Rows.End(j);
        }

        /// <summary>
 /// format rage.hold: the creature flees for FleeMs: <c>Creature.Scared.Add(1)</c>
 /// <c>Aggression.Value</c> held at 0,
 /// <c>LastTarget.SetTarget(null)</c> (as AttackLastTarget.StopPerform), and every second
 /// <c>SwimBehaviour.SwimTo(Vector3, float)</c> away from the grid, no higher than the follow depth
 /// </summary>
        private void StartFlee(Creature c, Grab g, double now)
        {
            if (c == null || !g.HasRage) return;
            Vector3 from = g.Section != null ? g.Section.transform.position : c.transform.position;
            _fleeing[c.GetInstanceID()] = new Flee { C = c, EndS = now + g.RageRow.FleeMs / 1000.0, NextSwimS = now, From = from };
            Flees++;
            try
            {
                c.Scared.Add(1f);
                c.Aggression.Value = 0f;
                LastTarget lt = c.GetComponent<LastTarget>();
                if (lt != null) lt.SetTarget(null);
            }
            catch (Exception) { }
        }

        private readonly List<int> _fleeScratch = new List<int>();

        private void FleeTick(double now)
        {
            _fleeScratch.Clear();
            foreach (KeyValuePair<int, Flee> kv in _fleeing)
            {
                Flee f = kv.Value;
                if (f.C == null || !f.C.gameObject.activeInHierarchy || now >= f.EndS) { _fleeScratch.Add(kv.Key); continue; }
                f.C.Aggression.Value = 0f;
                if (now < f.NextSwimS) continue;
                f.NextSwimS = now + 1.0;
                Vector3 p = f.C.transform.position, away = p - f.From;
                if (away.sqrMagnitude < 1e-4f) away = f.C.transform.forward;
                Vector3 to = p + away.normalized * 60f;
                double topNow;
                to.y = (float)S11SurfaceRules.FollowY(to.y, _host.HalfHeight(f.C, out topNow));
                SwimBehaviour sb = f.C.GetComponent<SwimBehaviour>();
                if (sb != null) sb.SwimTo(to, 15f);
            }
            foreach (int k in _fleeScratch) _fleeing.Remove(k);
        }

        /// <summary>ReaperLeviathan.OnTakeDamage postfix: an Electrical or Poison hit makes the creature let go (its own rule).</summary>
        public void OnCreatureDamaged(Creature c, int snType)
        {
            if (c == null) return;
            Grab g;
            int k = c.GetInstanceID();
            if (!_active.TryGetValue(k, out g)) return;
            if (S11GrabRules.ReleasesOn(g.Row, snType)) Release(k, g, "hit by " + ((DamageType)snType));
        }

        /// <summary>ReaperLeviathan.OnDisable / SeaDragon.OnDisable postfix.</summary>
        public void OnCreatureDisabled(Creature c)
        {
            if (c == null) return;
            Grab g;
            int k = c.GetInstanceID();
            if (_active.TryGetValue(k, out g)) Release(k, g, "creature disabled");
        }

        /// <summary>ReaperLeviathan.Update postfix: the hold animation (its Update wrote IsHoldingSeamoth, false for a grid).</summary>
        public void OnReaperUpdate(ReaperLeviathan r)
        {
            if (r == null || !_active.ContainsKey(r.GetInstanceID())) return;
            SafeAnimator.SetBool(r.GetAnimator(), "seamoth_attack", true);
        }

        public void ReleaseAll(string why)
        {
            _scratch.Clear();
            _scratch.AddRange(_active.Keys);
            foreach (int k in _scratch)
            {
                Grab g;
                if (_active.TryGetValue(k, out g)) Release(k, g, why);
            }
        }

        private void Release(int key, Grab g, string why)
        {
            _active.Remove(key);
            _releasedAt[key] = Time.realtimeSinceStartup;
            Released++;
            try { IgnoreCollisions(g, false); } catch (Exception) { }
            try { if (g.Anim != null) g.Anim.speed = g.AnimSpeed; } catch (Exception) { }
            if (g.Creature != null)
            {
                // (format surface.release, S11.C7): the hold's highest mouth
                JsonOut sj = S5Rows.Begin("s11.jaw");
                sj.Num("key", (long)g.Key).Str("creature", g.Row.Creature).Num("ageS", Time.realtimeSinceStartup - g.Clock.StartS).Num("maxMouthY", g.MaxMouthY)
                  .Num("creatureY", (double)g.Creature.transform.position.y).Bool("released", true).Str("why", why ?? "");
                S5Rows.End(sj);
            }
            if (g.Reaper && g.Creature is ReaperLeviathan)
            {
                try { SafeAnimator.SetBool(g.Creature.GetAnimator(), "seamoth_attack", false); } catch (Exception) { }
                try { var rl = (ReaperLeviathan)g.Creature; if (rl.seamothGrabSound != null) rl.seamothGrabSound.Stop(); } catch (Exception) { } // ReleaseVehicle's Stop
            }
            string id = null;
            if (!S11Mutants.IsOn(S11Mutants.NoRelease) && g.Section != null)
            {
                Vector3 at = g.Attach != null ? g.Attach.position : g.Section.transform.position;
                _host.EmitGrid(S11Conventions.Unit.Grab, g.Section, 1f, S11Conventions.GrabPhase.Release, 0, at, Vector3.zero, Vector3.zero,
                               g.Creature != null ? (int)CraftData.GetTechType(g.Creature.gameObject) : 0, g.Key, out id);
            }
            string tech = g.Row.Creature;
            DeLog.Info("DE.S11 grab RELEASE " + tech + " grid " + S6Rows.Hex(g.Root) + " (" + why + ", " + g.Holds + " hold records" + (id != null ? ", record " + id : S11Mutants.IsOn(S11Mutants.NoRelease) ? ", S11.M5: NO release record" : "") + ")");
            GrabRow("release", tech, g.Root, Vec3.Zero, Vec3.Zero, true, Time.realtimeSinceStartup - g.Clock.StartS, id, why);
        }

        private void GrabRow(string phase, string creature, ulong root, Vec3 min, Vec3 max, bool fits, double holdS, string id, string why)
        {
            JsonOut j = S5Rows.Begin("s11.grab");
            j.Str("phase", phase).Str("creature", creature).Str("root", S6Rows.Hex(root)).Num("sx", max.X - min.X).Num("sy", max.Y - min.Y).Num("sz", max.Z - min.Z)
             .Bool("fits", fits).Num("holdS", holdS).Str("id", id ?? "").Str("why", why ?? "");
            S5Rows.End(j);
        }
    }
}
