using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using UnityEngine;
using EventFlags = DeepEngineers.Wire.S7Conventions.EventFlags;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The S11 hook points other slices' files call (each call site is one line, commented "S11 hook"): S7p's creature
 /// damage (the weapon class, the explosion yield, the blast cap exemption), S7's and S7p's explosion players (the
 /// chain filter), S6's TakeDamage route (the hazard types S6 does not forward, the bite push), S1's UI hider (the
 /// radiation level it zeroes). With no S11 host every hook returns the slice's own behaviour unchanged.
 /// Main thread only (every caller is a Unity callback or a frame tick).
 /// </summary>
    internal static class S11Hooks
    {
        /// <summary>The running S11 host (null: S11 off, every hook a no-op).</summary>
        public static volatile S11Host Host;
        public static bool Active { get { return Host != null; } }

        /// <summary>True while an escalated chain row is being played (it must not be admitted again).</summary>
        [ThreadStatic] public static bool Replaying;

        // ------------------------------------------------------------------------------------------ A (S7p creature damage)

 /// <summary>The weapon class of an S7 row's source: attached to the C1 character → Handheld; to a grid → by its GridAdd class.</summary>
        public static int ClassOf(MirrorConsumer m, ulong entityId, bool character, bool explosion)
        {
            if (explosion) return S11Conventions.WeaponClass.Explosion;
            if (character) return S11Conventions.WeaponClass.Handheld;
            bool large = false;
            try
            {
                GridRecord g;
                if (m != null && entityId != 0 && m.State.Grids.TryGetValue(entityId, out g))
                {
                    if (g.IsSubgrid && g.ParentId != 0)
                    {
                        GridRecord p;
                        if (m.State.Grids.TryGetValue(g.ParentId, out p)) g = p;
                    }
                    large = g.TargetClass == GridClass.LargeGrid;
                }
            }
            catch (Exception) { }
            return large ? S11Conventions.WeaponClass.LargeGrid : S11Conventions.WeaponClass.SmallGrid;
        }

        /// <summary>The SN_HP S7p deals after the weapon-class rule (A); S7p's own amount when S11 is off.</summary>
        public static float Danger(LiveMixin lm, float hp, int cls, string why)
        {
            S11Host h = Host;
            if (h == null || lm == null) return hp;
            try { return h.Danger(lm, hp, cls, why); }
            catch (Exception e) { h.Fault("danger", e); return hp; }
        }

        /// <summary>After S7p's TakeDamage (the row of the hit: health after, killed).</summary>
        public static void AfterDanger(LiveMixin lm, bool killed)
        {
            S11Host h = Host;
            if (h == null || lm == null) return;
            try { h.AfterDanger(lm, killed); }
            catch (Exception e) { h.Fault("danger.after", e); }
        }

        /// <summary>: S7p's creature damage, registered at its start (the burst scene injects its shots through it).</summary>
        public static volatile S7pCreatureDamage DamageHost;

        /// <summary>
 /// (format blast.damage): an explosion's distance to a creature, the nearest point of its own colliders (a x3
 /// reaper's hull, not its root), 0 for the creature a missile struck; S7p's transform distance when S11 is off.
 /// </summary>
        public static float BlastDistance(Vector3 centre, LiveMixin lm, float transformDistance)
        {
            S11Host h = Host;
            if (h == null || lm == null) return transformDistance;
            try { return h.BlastDistance(centre, lm, transformDistance); }
            catch (Exception e) { h.Fault("blast.distance", e); return transformDistance; }
        }

        /// <summary>: the creature a shot is fired from inside (a held ship's gun in the jaw), or null.</summary>
        public static LiveMixin CreatureAround(Vector3 origin, Vector3 dir)
        {
            S11Host h = Host;
            if (h == null) return null;
            try { return h.CreatureAround(origin, dir); }
            catch (Exception e) { h.Fault("shot.inside", e); return null; }
        }

        /// <summary>Blast damage calls are never dropped by S7p's per-frame hit cap (A: N warheads act as N yields).</summary>
        public static bool BypassHitCap(int cls)
        {
            return Host != null && cls == S11Conventions.WeaponClass.Explosion && !S11Mutants.IsOn(S11Mutants.NoStack);
        }

        /// <summary>
 /// The base SN_HP of an explosion at <paramref name="distance"/>: S11's yield (the row's extra, SE's own damage) with
 /// Subnautica's falloff; S7p's capped intensity rule when S11 is off or under S11.M2.
 /// </summary>
        public static float BlastBaseHp(ref S7EventRow r, float radius, float distance, double scale)
        {
            if (Host == null || S11Mutants.IsOn(S11Mutants.NoStack))
                return (float)(S7pDamageRules.ExplosionHp(r.intensity, radius, distance) * scale);
            double y = S11BlastRules.YieldOf(r.extra, r.intensity);
            return (float)(S11BlastRules.DamageHp(y, radius, distance, 1000) * scale);
        }

        /// <summary>Review 5: an Explosion row's weapon class (subtype bits 8-11; 0 = an older SE or a warhead: Explosion).</summary>
        public static int BlastClass(ref S7EventRow r)
        {
            return S11BlastRules.ClassOfSubtype(r.subtype);
        }

        // ------------------------------------------------------------------------------------------ A (explosion players)

 /// <summary>
 /// An Explosion row reaching one of the explosion players: true = play it now (it opened a chain), false = absorbed
 /// into an open chain (played later as part of the escalated effect). Other rows, S11 off, S11.M2: true.
 /// </summary>
        public static bool ChainAdmit(ref S11BlastChain chain, ref S7EventRow r, Vector3 wpos)
        {
            S11Host h = Host;
            if (h == null || r.type != EventType.Explosion || Replaying || S11Mutants.IsOn(S11Mutants.NoStack)) return true;
            try
            {
                if (chain == null) chain = new S11BlastChain();
                return h.ChainAdmit(chain, ref r, wpos);
            }
            catch (Exception e) { h.Fault("chain.admit", e); return true; }
        }

        /// <summary>The escalated rows whose chain window closed (to be played with <see cref="Replaying"/> set).</summary>
        public static int ChainClose(S11BlastChain chain, List<S7EventRow> into)
        {
            S11Host h = Host;
            if (h == null || chain == null || into == null) return 0;
            try { return h.ChainClose(chain, into); }
            catch (Exception e) { h.Fault("chain.close", e); return 0; }
        }

        // ------------------------------------------------------------------------------------------ D (S6 TakeDamage route)

 /// <summary>
 /// A TakeDamage on the ghost Player or the character section whose type S6 does not forward (S6Skip
 /// TypeNotForwarded): a hazard of the S11 table becomes an S11 record. True = S11 took it (S6 does not count a skip).
 /// </summary>
        public static bool Unforwarded(bool isPlayer, ProxyMarker m, float amount, Vector3 position, int type, GameObject dealer, bool gatePass, bool atChar, bool capture)
        {
            S11Host h = Host;
            if (h == null || capture) return false;
            try { return h.Unforwarded(isPlayer, m, amount, position, type, dealer, gatePass, atChar); }
            catch (Exception e) { h.Fault("hazard", e); return false; }
        }

        /// <summary>After S6 wrote a record (a bite on a grid can push it: C).</summary>
 /// <summary>Pass 5, bridge hook (WriteStatus): the C7 S11 block at 0xC0 (<c>format c7.s11</c>); absent while S11 is off.</summary>
        public static void FillStatus(ref SnStatus s)
        {
            if (Host == null) return;
            try { S11Wire.WriteStatus(ref s, S11DamageFx.Flags()); }
            catch (Exception) { }
        }

        public static void AfterS6Hit(DamageRecord r, GameObject dealer, bool isPlayer, ProxyMarker m)
        {
            S11Host h = Host;
            if (h == null) return;
            // pass 5: a hit on the SE character (a bite, a shot) shows Subnautica's own damage FX under its picture
            if (r.targetKind == DamageTargetKind.Character) S11DamageFx.Play(r.snDamageType, r.amount, new Vector3(r.snWorldHit.x, r.snWorldHit.y, r.snWorldHit.z));
            if (isPlayer || m == null) return;
            try { h.AfterS6Hit(r, dealer, m); }
            catch (Exception e) { h.Fault("push", e); }
        }

        // ------------------------------------------------------------------------------------------ D (S1 UI hider)

 /// <summary>The radiation amount Subnautica set on the ghost Player, read right before the UI hider zeroes it.</summary>
        public static void RadiationSeen(float amount)
        {
            S11Host h = Host;
            if (h == null) return;
            h.RadiationSeen(amount);
        }

        // ------------------------------------------------------------------------------------------ helpers

 /// <summary>The proxy marker a collider belongs to (its own object or an ancestor), or null.</summary>
        public static ProxyMarker MarkerOf(Collider c)
        {
            if (c == null) return null;
            try { return c.GetComponentInParent<ProxyMarker>(); }
            catch (Exception) { return null; }
        }

        public static ProxyMarker MarkerOf(GameObject go)
        {
            if (go == null) return null;
            try { return go.GetComponentInParent<ProxyMarker>(); }
            catch (Exception) { return null; }
        }

        /// <summary>A synthetic Explosion row for an escalated chain (SN-world, not attached; Underwater below the waterline).</summary>
        public static S7EventRow EscalatedRow(S11Blast b)
        {
            var r = new S7EventRow
            {
                type = EventType.Explosion,
                flags = b.Y < 0 ? EventFlags.Underwater : (byte)0,
                lifetimeMs = S7EventRules.OneShotLifetimeMs(EventType.Explosion),
                pos = new F32x3((float)b.X, (float)b.Y, (float)b.Z),
                intensity = (float)Math.Min(1.0, b.Yield / 1000.0),
                size = (float)Math.Max(0.1, b.R),
                extra = S11BlastRules.YieldCode(b.Yield),
            };
            return r;
        }
    }
}
