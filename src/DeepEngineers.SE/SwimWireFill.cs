using System;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// What the swim controller knows at the end of a tick, as the C1 swim extension needs it (game-independent; filled
 /// by <see cref="SwimController"/>, composed by <see cref="SwimWireFill"/>, tested in tests/DeepEngineers.SE.Tests).
 /// </summary>
    internal struct SwimWireSource
    {
        /// <summary>False while the controller is idle (no character, dead, seated, off the planet, model off, out of the water high above the sea): flags 0.</summary>
        public bool Active;
        /// <summary>The swim latch (modes Swim, Surface, Fly).</summary>
        public bool Latched;
        /// <summary><see cref="SwimMode"/>.</summary>
        public uint Mode;
        /// <summary>The model set the velocity this tick.</summary>
        public bool Controls;
        public bool Grounded;
        /// <summary>The model allows SE's jetpack this tick.</summary>
        public bool JetpackAllowed;
        /// <summary>SN y of the eye.</summary>
        public double EyeY;
        /// <summary>Body-relative move input in [−1, 1].</summary>
        public double Forward, Strafe, Vertical;
        /// <summary>The velocity set (or SE's own while the model does not control), SN m/s.</summary>
        public Vec3 Velocity;
        /// <summary><see cref="SwimOutputs.MaxSpeed"/> of the last motor step (0 before the first).</summary>
        public double LastMaxSpeed;
        /// <summary><see cref="SwimFins"/> setting.</summary>
        public uint Fins;
        /// <summary>The suit's oxygen fraction 0..1 (NaN: unreadable).</summary>
        public double O2Fraction;
        /// <summary>The dev override (NaN: none).</summary>
        public double O2Override;
        /// <summary><see cref="SwimDepthClass"/>.</summary>
        public uint DepthClass;
    }

    /// <summary>
 /// Composes the C1 swim extension from the controller's tick (CONTRACT §5.4 "C1 swim extension"): the flags of
 /// <see cref="SwimWireFlags"/>, the move axes, the speed fractions over <see cref="SwimWire.SpeedReference"/> of the
 /// last motor step, the O2 fraction (the override when set, else the suit; NaN → 255). Idle: flags 0, mode Walk, axes
 /// and speeds 0, fins and O2 still written. The S5a mutants are applied here (<see cref="S5aMutants"/>): M2 freezes the
 /// O2 byte, M3 freezes the flags, mode, axes and speeds (o2 and fins stay live) after the first non-idle write.
 /// </summary>
    internal static class SwimWireFill
    {
        private static bool _haveO2Frozen, _haveFrozen;
        private static byte _o2Frozen;
        private static SwimWireState _frozen;

        /// <summary>Forgets the mutants' frozen values (tests; a new world).</summary>
        public static void Reset()
        {
            _haveO2Frozen = false;
            _haveFrozen = false;
        }

        public static SwimWireState Compose(ref SwimWireSource s)
        {
            var w = new SwimWireState { Version = (byte)SwimWire.Version, Fins = (byte)s.Fins };
            bool overridden = !double.IsNaN(s.O2Override);
            double o2 = overridden ? s.O2Override : s.O2Fraction;
            w.O2 = s.Active || overridden ? SwimWire.Fraction(o2) : (byte)255;
            if (overridden) w.Flags |= SwimWireFlags.O2Override;
            if (s.Active)
            {
                if (s.Latched) w.Flags |= SwimWireFlags.InWater;
                if (s.EyeY < SwimThresholds.UnderwaterEyeY) w.Flags |= SwimWireFlags.Underwater;
                if (s.Mode == SwimMode.Surface) w.Flags |= SwimWireFlags.AtSurface;
                if (s.Grounded) w.Flags |= SwimWireFlags.Grounded;
                if (s.Forward != 0.0 || s.Strafe != 0.0 || s.Vertical != 0.0) w.Flags |= SwimWireFlags.Moving;
                if (s.Latched && !s.JetpackAllowed) w.Flags |= SwimWireFlags.JetpackSuppressed;
                if (s.Controls) w.Flags |= SwimWireFlags.Controls;
                w.Mode = (byte)s.Mode;
                w.MoveForward = SwimWire.Axis(s.Forward);
                w.MoveStrafe = SwimWire.Axis(s.Strafe);
                w.MoveVertical = SwimWire.Axis(s.Vertical);
                w.DepthClass = (byte)s.DepthClass;
                if (s.Controls)
                {
                    double reference = SwimWire.SpeedReference(s.LastMaxSpeed);
                    double h = Math.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Z * s.Velocity.Z);
                    w.SpeedH = SwimWire.SpeedFraction(h, reference);
                    w.SpeedV = SwimWire.SpeedFraction(s.Velocity.Y, reference);
                    w.VyFrac = SwimWire.SignedFraction(s.Velocity.Y, reference);
                }
            }
            // seeded failures (dev builds)
            if (S5aMutants.O2Stuck)
            {
                if (!_haveO2Frozen) { _o2Frozen = w.O2; _haveO2Frozen = true; }
                w.O2 = _o2Frozen;
                w.Flags = (byte)(w.Flags & ~SwimWireFlags.O2Override);
            }
            if (S5aMutants.WireFrozen && s.Active)
            {
                // the flags, mode, axes and speeds of the first active tick for ever; o2 and fins stay live
                if (!_haveFrozen) { _frozen = w; _haveFrozen = true; }
                SwimWireState live = w;
                w = _frozen;
                w.O2 = live.O2;
                w.Fins = live.Fins;
                w.Version = live.Version;
                w.Flags = (byte)((w.Flags & ~SwimWireFlags.O2Override) | (live.Flags & SwimWireFlags.O2Override));
            }
            return w;
        }
    }
}

namespace DeepEngineers.SE
{
    /// <summary>
 /// What the SE character's tick says about its movement out of the water, as the S7a C1 locomotion extension needs it
 /// (CONTRACT §5.4 "C1 locomotion extension"; game-independent: filled by <see cref="LocoWireFill"/>, composed by
 /// <see cref="LocoCompose"/>, tested in tests/DeepEngineers.SE.Tests). Directions in SE world axes (doubles).
 /// </summary>
    internal struct LocoSource
    {
        /// <summary>A controlled character exists (C1's <c>MySession.Static.LocalCharacter</c>, not closed).</summary>
        public bool Present;
        /// <summary>The raw <c>MyCharacter.GetCurrentMovementState</c>.</summary>
        public ushort Movement;
        /// <summary>The swim latch of this tick (C1 swim extension flag bit 0).</summary>
        public bool SwimLatch;
        /// <summary><c>Physics.CharacterProxy.Supported</c> (the swim controller's grounded source).</summary>
        public bool Grounded;
        public bool Crouching, Jetpack, Dead, Seated;
        /// </summary>
        public bool ToolHeld;
        /// <summary><c>Physics.LinearVelocity</c>, m/s.</summary>
        public Vec3 Velocity;
        /// <summary><c>WorldMatrix.Forward</c>, <c>.Right</c>, <c>.Up</c>.</summary>
        public Vec3 Forward, Right, Up;
    }

    /// <summary>
 /// Composes the C1 locomotion extension (CONTRACT §5.4 "C1 locomotion extension"): the state from
 /// <see cref="S7aLoco.StateFromSe"/>, the flags, the velocity along the horizontal forward / right (the
 /// <c>WorldMatrix.Up</c> component removed, normalised) and along up in cm/s, and the yaw rate about up from
 /// consecutive ticks in centidegrees per simulation second (+ = turning right, clockwise seen from above). One
 /// instance per writer (it keeps the previous tick's horizontal forward); allocation-free.
 /// </summary>
    internal sealed class LocoCompose
    {
        private Vec3 _prevFwdH;
        private bool _hasPrev;

        public void Reset() { _hasPrev = false; }

        public LocoWireState Compose(ref LocoSource src, double simDt)
        {
            var w = new LocoWireState();
            if (!src.Present)
            {
                _hasPrev = false;
                return w; // version 1, state None, the rest 0
            }
            w.SeMovement = src.Movement;
            w.State = S7aLoco.StateFromSe(src.Movement, true, src.SwimLatch);
            byte flags = 0;
            if (src.Grounded) flags |= S7aConventions.C1LocoFlags.Grounded;
            if (src.Crouching) flags |= S7aConventions.C1LocoFlags.Crouching;
            if (src.Jetpack) flags |= S7aConventions.C1LocoFlags.Jetpack;
            if (src.ToolHeld) flags |= S7aConventions.C1LocoFlags.ToolHeld;
            if (src.SwimLatch) flags |= S7aConventions.C1LocoFlags.InWater;
            if (src.Seated || w.State == S7aConventions.C1LocoState.Seated) flags |= S7aConventions.C1LocoFlags.Seated;
            if (src.Dead || w.State == S7aConventions.C1LocoState.Dead) flags |= S7aConventions.C1LocoFlags.Dead;
            if (w.State == S7aConventions.C1LocoState.Ladder) flags |= S7aConventions.C1LocoFlags.Ladder;
            w.Flags = flags;

            Vec3 up = Unit(src.Up);
            Vec3 fwdH = Horizontal(src.Forward, up);
            Vec3 rightH = Horizontal(src.Right, up);
            w.VForwardCm = S7aLoco.Cm(Vec3.Dot(src.Velocity, fwdH));
            w.VStrafeCm = S7aLoco.Cm(Vec3.Dot(src.Velocity, rightH));
            w.VUpCm = S7aLoco.Cm(Vec3.Dot(src.Velocity, up));

            double rate = 0.0;
            if (_hasPrev && simDt > 0.0 && fwdH.LengthSquared > 0.0)
            {
                // atan2((prev x now)·up, prev·now) is + for a left turn (counter-clockwise seen from above): negate
                double ang = Math.Atan2(Vec3.Dot(Vec3.Cross(_prevFwdH, fwdH), up), Vec3.Dot(_prevFwdH, fwdH));
                rate = -ang * (180.0 / Math.PI) / simDt;
            }
            w.YawRateCdeg = S7aLoco.Cdeg(rate);
            if (fwdH.LengthSquared > 0.0)
            {
                _prevFwdH = fwdH;
                _hasPrev = true;
            }
            return w;
        }

        private static Vec3 Unit(Vec3 v)
        {
            double n = v.Length;
            return n > 1e-12 ? v * (1.0 / n) : Vec3.UnitY;
        }

        /// <summary>v with its <paramref name="up"/> component removed, normalised (zero when v is along up).</summary>
        public static Vec3 Horizontal(Vec3 v, Vec3 up)
        {
            Vec3 h = v - up * Vec3.Dot(v, up);
            double n = h.Length;
            return n > 1e-9 ? h * (1.0 / n) : Vec3.Zero;
        }
    }

    /// <summary>The s7a.loco row cadence (CONTRACT ROADMAP S7a: every change of state or flags, else 2 Hz).</summary>
    internal sealed class LocoRowCadence
    {
        public const ulong PeriodMs = 500;
        private bool _have;
        private byte _state, _flags;
        private ulong _lastMs;

        public void Reset() { _have = false; }

        public bool Due(ref LocoWireState s, ulong nowMs)
        {
            bool changed = !_have || s.State != _state || s.Flags != _flags;
            if (!changed && nowMs - _lastMs < PeriodMs) return false;
            _have = true;
            _state = s.State;
            _flags = s.Flags;
            _lastMs = nowMs;
            return true;
        }
    }
}
