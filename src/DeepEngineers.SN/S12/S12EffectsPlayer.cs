using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S12's entry points into S7's effect player (format flood.plane's splash, format vent.event's bubbles): the same emitters,
 /// the same per-frame bubble budget (<c>BubblesPerFrame</c>, S7p) and the same audio rule (<c>OneShotSound</c> through
 /// <c>AudioPolish.OnEventSound</c>: an under-water row while Subnautica's bus is live), so every S12 effect respects S7p's caps
 /// and the S2 bus rule. Main thread.
 /// </summary>
    internal sealed partial class S7Effects
    {
        public long S12BubblesAsked, S12BubblesEmitted, S12SplashSounds, S12SplashSilent;

        /// <summary>Fine bubbles at <paramref name="at"/> through S7's budget; the number emitted (0 when the frame's budget is spent).</summary>
        internal int S12Bubbles(Vector3 at, float radius, int count, float rise)
        {
            if (count <= 0) return 0;
            long before = _pBubbles;
            S12BubblesAsked += count;
            Bubbles(at, Mathf.Max(0.05f, radius), count, 0.02f, 0.08f, 1.0f, 2.6f, rise);
            int n = (int)(_pBubbles - before);
            S12BubblesEmitted += n;
            return n;
        }

        /// <summary>The flood's splash sound (Subnautica's Vehicle.splashSound, S7's FxSound.Splash) as an under-water hull-break row.</summary>
        internal bool S12SplashSound(Vector3 at, float intensity)
        {
            bool ok = OneShotSound(FxSound.Splash, at, intensity, S7Conventions.EventType.HullBreak, S7Conventions.EventFlags.Underwater);
            if (ok) S12SplashSounds++;
            else S12SplashSilent++;
            return ok;
        }
    }
}
