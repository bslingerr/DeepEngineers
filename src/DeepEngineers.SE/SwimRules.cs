using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>Why the controller left the swim state (1–3 come from the model, 10+ from the SE controller).</summary>
    internal static class SwimExit
    {
        public const uint None = 0;
        public const uint Surface = 1;      // model: eye above the exit line, free in the water
        public const uint Grounded = 2;     // model: eye above the grounded exit line while standing on something
        public const uint Jetpack = 3;      // model: the latch turned off with the jetpack running (flown out, mode Fly)
        public const uint ModelOff = 10;    // de.swim model=off
        public const uint Seated = 11;      // the character sat down (cockpit, seat)
        public const uint Dead = 12;
        public const uint LeftWater = 13;   // left the dive planet's water (site unbound, other planet, space)
        public const uint CharacterChanged = 14; // respawn, another character, world unload
        public const uint Room = 15;        // S7: the character's eye cell is in an airtight room (the swim gate, InteriorProbe.cs)

        public static string Name(uint r)
        {
            switch (r)
            {
                case None: return "none";
                case Surface: return "surface";
                case Grounded: return "grounded";
                case Jetpack: return "jetpack";
                case ModelOff: return "model-off";
                case Seated: return "seated";
                case Dead: return "dead";
                case LeftWater: return "left-water";
                case CharacterChanged: return "character-changed";
                case Room: return "room";
                default: return "reason" + r;
            }
        }
    }

    /// <summary>What to do with SE's jetpack and dampeners on a swim exit.</summary>
    internal struct SwimRestore
    {
        public bool SetJetpack, JetpackTo;
        public bool SetDampeners, DampenersTo;
        public bool RestoreO2Multiplier;
        /// <summary>Seated: the jetpack's remembered state waits for the engineer to leave the seat (<see cref="SwimSuitPolicy.Parked"/>).</summary>
        public bool JetpackParked;

        public override string ToString()
        {
            return "jetpack->" + (SetJetpack ? (JetpackTo ? "on" : "off") : JetpackParked ? "parked" : "kept") +
                   " dampeners->" + (SetDampeners ? (DampenersTo ? "on" : "off") : "kept") +
                   " o2mult->" + (RestoreO2Multiplier ? "restored" : "kept");
        }
    }

    /// <summary>
 /// The jetpack, dampener and oxygen-multiplier policy of the swim state (game-independent; ROADMAP S2b: jetpack and
 /// dampeners off in the water, the jetpack back when the head is out, the player's previous state restored on exit).
 /// <list type="bullet">
 /// <item>Entry: remember the jetpack and dampener states (and the suit's oxygen multiplier, once read).</item>
 /// <item>While swimming: the jetpack is switched off whenever it is on with the head under (<c>jetpackAllowed</c>
 /// false); with the head out it is the player's (switching it on hands the engineer to SE's jetpack, mode Fly, and
 /// the latch exit above +0.8 m then has the reason jetpack). The dampeners are kept off while the latch is on.
 /// S2b.M2: the jetpack is never touched.</item>
 /// <item>Exit: surface, grounded, model off, left the water → jetpack and dampeners back to the remembered states;
 /// jetpack exit → dampeners back, the jetpack stays on (the player's choice); seated → dampeners back now and the
 /// jetpack <b>parked</b>
 /// leaving
 /// the seat in the water the swim goes on with the remembered states (<see cref="Unpark"/>), out of the water the
 /// remembered jetpack and dampeners are set after the cockpit's own restore (<see cref="LeaveSeatOutOfWater"/>);
 /// dead or another character → nothing. The oxygen multiplier is restored on every exit except dead/another
 /// character (that character is gone). S2b.M2: no jetpack restore.</item>
 /// <item>While <see cref="Active"/> (parked included) a save stores the remembered jetpack and dampener states, not
 /// the swim's.</item>
 /// </list>
 /// </summary>
    internal sealed class SwimSuitPolicy
    {
        public bool Active { get; private set; }
        /// <summary>The engineer sat down while swimming: <see cref="Active"/> stays on, the jetpack waits for him to leave the seat.</summary>
        public bool Parked { get; private set; }
        public bool JetpackWas { get; private set; }
        public bool DampenersWas { get; private set; }
        public bool HaveO2Multiplier { get; private set; }
        public float O2MultiplierWas { get; private set; }

        public void OnEnter(bool jetpackOn, bool dampenersOn)
        {
            Active = true;
            Parked = false;
            JetpackWas = jetpackOn;
            DampenersWas = dampenersOn;
            HaveO2Multiplier = false;
            O2MultiplierWas = 1f;
        }

        /// <summary>The suit's multiplier before the rule first changed it (kept only once per swim).</summary>
        public void RememberO2Multiplier(float m)
        {
            if (!Active || HaveO2Multiplier) return;
            HaveO2Multiplier = true;
            O2MultiplierWas = m;
        }

        /// <summary>

 /// its flying state (the jetpack's state, without the jetpack), so SE's ground locomotion never engages on the
 /// seabed; with the head out or off the latch the proxy follows the jetpack again. S2b.M2: never touched.
 /// </summary>
        public bool ProxyFlyingWanted(bool jetpackAllowed, bool mutantM2)
        {
            return Active && !mutantM2 && !jetpackAllowed;
        }

        /// <summary>True when the controller must switch the jetpack off this tick.</summary>
        public bool MustSwitchJetpackOff(bool jetpackOn, bool jetpackAllowed, bool mutantM2)
        {
            return Active && !mutantM2 && jetpackOn && !jetpackAllowed;
        }

        /// <summary>True when the controller must switch the dampeners off this tick.</summary>
        public bool MustSwitchDampenersOff(bool dampenersOn)
        {
            return Active && dampenersOn;
        }

        /// <summary>Leaves the swim state; returns what to restore.</summary>
        public SwimRestore OnExit(uint reason, bool mutantM2)
        {
            var r = new SwimRestore();
            if (!Active) return r;
            if (Parked && reason != SwimExit.Dead && reason != SwimExit.CharacterChanged) return r; // already left once
            Active = false;
            Parked = false;
            switch (reason)
            {
                case SwimExit.Dead:
                case SwimExit.CharacterChanged:
                    return r;
                case SwimExit.Seated:
                    // the jetpack waits for the seat to be left (the cockpit restores its own copy first)
                    Active = true;
                    Parked = true;
                    r.JetpackParked = true;
                    r.SetDampeners = true;
                    r.DampenersTo = DampenersWas;
                    r.RestoreO2Multiplier = HaveO2Multiplier;
                    HaveO2Multiplier = false; // a swim after the seat remembers the multiplier again
                    return r;
                case SwimExit.Jetpack:
                    r.SetDampeners = true;
                    r.DampenersTo = DampenersWas;
                    break;
                default:
                    r.SetDampeners = true;
                    r.DampenersTo = DampenersWas;
                    if (!mutantM2)
                    {
                        r.SetJetpack = true;
                        r.JetpackTo = JetpackWas;
                    }
                    break;
            }
            r.RestoreO2Multiplier = HaveO2Multiplier;
            return r;
        }

        /// <summary>Left the seat into the water (the latch is on again): the swim goes on with the remembered states.</summary>
        public void Unpark()
        {
            Parked = false;
        }

        /// <summary>Left the seat out of the water: the remembered jetpack and dampeners (after the cockpit's own restore).</summary>
        public SwimRestore LeaveSeatOutOfWater(bool mutantM2)
        {
            var r = new SwimRestore();
            if (!Active || !Parked) return r;
            Active = false;
            Parked = false;
            r.SetDampeners = true;
            r.DampenersTo = DampenersWas;
            if (!mutantM2)
            {
                r.SetJetpack = true;
                r.JetpackTo = JetpackWas;
            }
            return r;
        }
    }

    /// <summary>
 /// Subnautica's oxygen rule on SE's suit (game-independent; ROADMAP S2b.N4). Subnautica counts oxygen in seconds of
 /// breathing ("units"): the tank holds <c>tankSeconds</c>, the diver uses 1 / 1.5 / 2 units per second at the safe /
 /// unsafe / crush depth classes with the head under, and refills 30 units per second near the surface (the model's
 /// numbers, <see cref="SwimModelAdapter.Numbers"/>). SE's suit holds <c>OxygenCapacity</c> litres and breathes
 /// <c>OxygenConsumptionRate × 60/100</c> litres per second while the helmet is closed
 /// where
 /// <c>OxygenConsumptionRate = Definition.OxygenConsumption × Definition.OxygenConsumptionMultiplier ×
 /// OxygenConsumptionRateMutliplier</c>
 /// The same fraction of the tank per second on both sides gives the multiplier; the refill is added to
 /// <c>SuitOxygenAmount</c> directly.
 /// </summary>
    internal static class SwimO2Rule
    {
        /// <summary>SE's sink requires <c>rate × 60 / 100</c> per second.</summary>
        public const double SeSinkPerSecondFactor = 60.0 / 100.0;

        /// <summary>
 /// The <c>OxygenConsumptionRateMutliplier</c> that makes SE's suit lose the fraction <c>unitsPerSec /
 /// tankSeconds</c> of its capacity per second. <paramref name="baseRate"/> is the definition's rate
 /// (<c>OxygenConsumption × OxygenConsumptionMultiplier</c>). 0 when no oxygen is used. NaN when it cannot be
 /// computed (no capacity or base rate).
 /// </summary>
        public static double Multiplier(double unitsPerSec, double tankSeconds, double capacityLitres, double baseRate)
        {
            if (unitsPerSec <= 0) return 0.0;
            if (!(tankSeconds > 0) || !(capacityLitres > 0) || !(baseRate > 0)) return double.NaN;
            double targetLitresPerSec = unitsPerSec / tankSeconds * capacityLitres;
            return targetLitresPerSec / (baseRate * SeSinkPerSecondFactor);
        }

        /// <summary>Litres to add to the suit over <paramref name="dt"/> seconds for a refill of <paramref name="unitsPerSec"/>.</summary>
        public static double RefillLitres(double unitsPerSec, double tankSeconds, double capacityLitres, double dt)
        {
            if (!(unitsPerSec > 0) || !(tankSeconds > 0) || !(capacityLitres > 0) || !(dt > 0)) return 0.0;
            return unitsPerSec / tankSeconds * capacityLitres * dt;
        }

        /// <summary>

 /// off); without one Subnautica's tank rule applies. <paramref name="seOwn"/> is the multiplier SE had before the
 /// rule first touched it (1 unless a mod changed it).
 /// </summary>
        public static double WantedMultiplier(bool bottleWithGas, double seOwn, double subnauticaWanted)
        {
            return bottleWithGas ? seOwn : subnauticaWanted;
        }

        /// <summary>Fraction of the tank per second (the oracle's comparison unit).</summary>
        public static double FractionPerSecond(double unitsPerSec, double tankSeconds)
        {
            return tankSeconds > 0 ? unitsPerSec / tankSeconds : double.NaN;
        }

        /// <summary>True when the multiplier must be written again (first time, or changed by more than 0.1 %).</summary>
        public static bool NeedsWrite(double current, double wanted)
        {
            if (double.IsNaN(wanted)) return false;
            if (double.IsNaN(current)) return true;
            return Math.Abs(current - wanted) > 1e-3 * Math.Max(1.0, Math.Abs(wanted));
        }
    }
}

namespace DeepEngineers.SE
{
    /// <summary>
 /// Compensation of what SE's physics step does to the velocity the controller sets (game-independent). The velocity
 /// is written right before the Havok step and read back right after it; in free water the only difference is the
 /// step's own effect on the character body
 /// The estimator keeps an exponential mean of the in-step ratio <c>measured·applied / |applied|²</c> over
 /// free-water samples only (not grounded, at least 0.5 m/s, ratio in [0.9, 1], residual across the motion under 2 %:
 /// contacts are excluded); mode <c>auto</c> then applies <c>1 / ratio</c>, <c>off</c> applies 1, a number applies
 /// itself. The swim rows log the gain and both velocities, so the oracle sees the effect either way.
 /// <para>A pure scale is all the gain can compensate. An additive in-step term (per-body gravity in Havok, a force)
 /// leaves a residual along a fixed axis, which can still pass the ratio filter on samples moving along that axis; so
 /// the mean residual <c>measured − applied</c> per axis over the free-water samples is kept as a diagnostic
 ///: a vertical bias near −g·dt (−0.16 m/s) is a gravity leak (H1).</para>
 /// </summary>
    internal sealed class SwimGain
    {
        public const int MinSamples = 30;
        public const double Alpha = 0.05;
        public const double MinRatio = 0.9;
        public const double MaxRatio = 1.0001;
        public const double MaxPerpFraction = 0.02;
        public const double MinSpeed = 0.5;

        public string Mode { get; private set; }
        public double Fixed { get; private set; }
        public double Ratio { get; private set; }
        public int Samples { get; private set; }
        public long Rejected { get; private set; }
        /// <summary>Mean residual (measured − applied, SN m/s) per axis over the free-water samples (exponential, <see cref="Alpha"/>).</summary>
        public Vec3 Bias { get; private set; }
        public int BiasSamples { get; private set; }

        public SwimGain()
        {
            Mode = "auto";
            Fixed = 1.0;
            Reset();
        }

        public void Reset()
        {
            Ratio = double.NaN;
            Samples = 0;
            Rejected = 0;
            Bias = Vec3.Zero;
            BiasSamples = 0;
        }

        /// <summary>The factor applied to the model's velocity before it is written to SE.</summary>
        public double Gain
        {
            get
            {
                if (Mode == "off") return 1.0;
                if (Mode == "fixed") return Fixed;
                if (Samples < MinSamples || double.IsNaN(Ratio) || Ratio < MinRatio || Ratio > 1.0) return 1.0;
                return 1.0 / Ratio;
            }
        }

        /// <summary>One post-step sample; true when it was used.</summary>
        public bool Observe(Vec3 applied, Vec3 measured, bool grounded)
        {
            double a2 = applied.LengthSquared;
            if (grounded || a2 < MinSpeed * MinSpeed) return false;
            Vec3 res = measured - applied;
            if (!double.IsNaN(res.X) && !double.IsNaN(res.Y) && !double.IsNaN(res.Z))
            {
                Bias = BiasSamples == 0 ? res : Bias + (res - Bias) * Alpha;
                BiasSamples++;
            }
            double r = Vec3.Dot(measured, applied) / a2;
            double perp = (measured - applied * r).Length / Math.Sqrt(a2);
            if (r < MinRatio || r > MaxRatio || perp > MaxPerpFraction || double.IsNaN(r))
            {
                Rejected++;
                return false;
            }
            Ratio = double.IsNaN(Ratio) ? r : Ratio + Alpha * (r - Ratio);
            Samples++;
            return true;
        }

        /// <summary><c>auto</c>, <c>off</c> or a fixed factor in [0.5, 2].</summary>
        public bool SetMode(object value, out string error)
        {
            error = null;
            string s = value as string;
            if (s != null && (s == "auto" || s == "off")) { Mode = s; return true; }
            double d;
            if (value is double) d = (double)value;
            else if (s == null || !double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
            {
                error = "gain must be auto, off or a number in [0.5, 2]";
                return false;
            }
            if (!(d >= 0.5 && d <= 2.0)) { error = "gain must be in [0.5, 2]"; return false; }
            Mode = "fixed";
            Fixed = d;
            return true;
        }

        public string Describe()
        {
            return Mode + (Mode == "fixed" ? " " + SwimText.N(Fixed) : "") + " gain=" + SwimText.N(Gain) +
                   " inStepRatio=" + (double.IsNaN(Ratio) ? "n/a" : Ratio.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)) +
                   " samples=" + Samples + " bias=" + (BiasSamples > 0 ? SwimText.V(Bias) : "n/a");
        }
    }
}
