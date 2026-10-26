using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The marker on every proxy section root (grid sections, the character section, the self-check's test section;
 /// CONTRACT §4.2): who the section stands for, and the pose that placed it at the last physics step (the hit point of
 /// §2.7 is computed with exactly that pose, never with the interpolated render transform).
 /// </summary>
    internal sealed class ProxyMarker : MonoBehaviour
    {
        public ulong EntityId, RootEntityId;
        public uint SectionId, Kind, TargetClass;
        public bool IsSubgrid, Large, SelfTest, Local;
        public double GridSize;
        /// <summary>(o, q, s), rel and the SE tick of the pose used at the last physics step (§2.7).</summary>
        public Vec3 StepO;
        public Quat StepQ = Quat.Identity;
        public double StepS = 1.0;
        public SeMatrix StepRel = InstanceMath.Identity;
        public ulong StepTick;
        public bool StepValid;
        /// <summary>The section's enabled colliders (closest-point hit points).</summary>
        public readonly List<Collider> Colliders = new List<Collider>();
        public LiveMixin Live;
    }

    /// <summary>
 /// The zero-damage modifier of every section (CONTRACT §4.2: Subnautica health never changes on anything that
 /// represents SE; belt and braces next to <c>LiveMixin.shielded</c>). VERIFIED <c>DamageModifier.ModifyDamage(float,
 /// DamageType)</c> is public virtual.
 /// </summary>
    internal sealed class ZeroDamageModifier : DamageModifier
    {
        public override float ModifyDamage(float damage, DamageType type) { return 0f; }
    }

    /// <summary>
 /// S6.M12's forwarder: an <c>IOnTakeDamage</c> receiver added by AddComponent. On a section it is added before the
 /// LiveMixin's Start (cached, it fires); on the Player it is added late, after Start, and is never called
 /// (VERIFIED <c>LiveMixin.Start</c> caches <c>GetComponents&lt;IOnTakeDamage&gt;</c> once) — the reason the contract
 /// captures damage with a TakeDamage postfix.
 /// </summary>
    internal sealed class LateDamageForwarder : MonoBehaviour, IOnTakeDamage
    {
        public LiveMixin Live;

        public void OnTakeDamage(DamageInfo damageInfo)
        {
            try
            {
                if (damageInfo == null) return;
                DamagePatches.Forwarded(Live, damageInfo.originalDamage, damageInfo.position, damageInfo.type, damageInfo.dealer);
            }
            catch (Exception)
            {
                // never throw into the game
            }
        }
    }

    /// <summary>
 /// The proxy stepper (CONTRACT §2.6): its FixedUpdate runs before the game's scripts and moves every section with
 /// MovePosition/MoveRotation; it is the behaviour registered with
 /// <c>MainGameController.RegisterHighFixedTimestepBehavior</c>. Physics does not step while Subnautica is frozen.
 /// </summary>
    [DefaultExecutionOrder(-1000)]
    internal sealed class ProxyStepper : MonoBehaviour
    {
        public S6Host Host;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        /// <summary>Clock ticks at the start of this physics step, and the proxy update time of the step (ms).</summary>
        public long StepStartTicks;
        public double ProxyMs;
        public long Steps;

        private void FixedUpdate()
        {
            StepStartTicks = Clock.ElapsedTicks;
            Steps++;
            S6Host h = Host;
            if (h != null) h.PhysicsStep();
            ProxyMs = (Clock.ElapsedTicks - StepStartTicks) * 1000.0 / Stopwatch.Frequency;
        }

        private void OnDestroy()
        {
            S6Host h = Host;
            Host = null;
            if (h != null) h.OnStepperDestroyed(this);
        }
    }

    /// <summary>
 /// The step timer (S6.B-T1 (3)): its FixedUpdate runs after every script's FixedUpdate, a WaitForFixedUpdate
 /// coroutine resumes after the physics simulation of the same step; PhysX ≈ resume − late FixedUpdate, the whole step
 /// = resume − the stepper's start. Feeds s6.perf; after the step it runs the penetration sampler (B-T1 (1)).
 /// </summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class ProxyStepTimer : MonoBehaviour
    {
        public ProxyStepper Stepper;
        public S6Host Host;
        private long _lateTicks;
        private Coroutine _co;

        private void OnEnable() { _co = StartCoroutine(AfterPhysics()); }

        private void OnDisable()
        {
            if (_co != null) { try { StopCoroutine(_co); } catch (Exception) { } }
            _co = null;
        }

        private void FixedUpdate()
        {
            if (Stepper != null) _lateTicks = Stepper.Clock.ElapsedTicks;
        }

        private IEnumerator AfterPhysics()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                ProxyStepper st = Stepper;
                S6Host h = Host;
                if (st == null || h == null) continue;
                long now = st.Clock.ElapsedTicks;
                double f = 1000.0 / Stopwatch.Frequency;
                double physx = _lateTicks > st.StepStartTicks ? (now - _lateTicks) * f : double.NaN;
                double step = (now - st.StepStartTicks) * f;
                try { h.AfterPhysicsStep(st.ProxyMs, physx, step); }
                catch (Exception e) { DeLog.WarnOnce("s6.afterstep", "DE.S6 after-step sampler failed: " + e.GetType().Name + ": " + e.Message); }
            }
        }
    }
}
