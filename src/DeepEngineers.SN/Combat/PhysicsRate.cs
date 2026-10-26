using System;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// The physics rate of CONTRACT §2.6 (S6.N7): <c>MainGameController.Instance.RegisterHighFixedTimestepBehavior(MonoBehaviour)</c>
 /// while a moving proxy is within 200 m of the camera, <c>DeregisterHighFixedTimestepBehavior(MonoBehaviour)</c> after
 /// 2 s with none and always on world unload, teardown and OnDestroy
 /// The decision is
 /// <see cref="PhysicsRateRule"/>.
 /// </summary>
    internal sealed class PhysicsRate
    {
        public readonly PhysicsRateRule Rule = new PhysicsRateRule();
        public long Registrations, Deregistrations, Failures;
        public bool Registered { get { return Rule.Registered; } }

        public void Update(MonoBehaviour stepper, bool anyMovingNear, double nowSec)
        {
            int d = Rule.Update(anyMovingNear, nowSec);
            if (d > 0) Register(stepper, "a moving proxy within 200 m of the camera");
            else if (d < 0) Deregister(stepper, "no moving proxy near the camera for 2 s");
        }

        private void Register(MonoBehaviour stepper, string why)
        {
            try
            {
                MainGameController mgc = MainGameController.Instance;
                if (mgc == null || stepper == null) { Rule.ForceOff(); return; }
                mgc.RegisterHighFixedTimestepBehavior(stepper);
                Registrations++;
                DeLog.Info("DE.S6 physics rate: stepper registered (" + why + ") fixedDeltaTime=" + Time.fixedDeltaTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                Failures++;
                Rule.ForceOff();
                DeLog.WarnOnce("s6.rate", "DE.S6 physics rate: RegisterHighFixedTimestepBehavior failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        public void Deregister(MonoBehaviour stepper, string why)
        {
            try
            {
                MainGameController mgc = MainGameController.Instance;
                if (mgc != null && stepper != null) mgc.DeregisterHighFixedTimestepBehavior(stepper);
                Deregistrations++;
                DeLog.Info("DE.S6 physics rate: stepper deregistered (" + why + ") fixedDeltaTime=" + Time.fixedDeltaTime.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception e)
            {
                Failures++;
                DeLog.WarnOnce("s6.rate.off", "DE.S6 physics rate: DeregisterHighFixedTimestepBehavior failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>World unload, teardown, OnDestroy: deregistered whatever the rule says.</summary>
        public void ForceOff(MonoBehaviour stepper, string why)
        {
            if (Rule.ForceOff()) Deregister(stepper, why);
        }
    }
}
