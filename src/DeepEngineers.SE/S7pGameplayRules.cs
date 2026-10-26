using System;
using System.Globalization;

namespace DeepEngineers.SE
{
    // =================================================================================================================
 // S7p-gameplay: the game-independent rules of the SE side,
 // replay-exact and tested (tests/DeepEngineers.SE.Tests/S7pGameplayTests.cs). No game type.
 // - DampenerHoldRules: a ship with inertial dampeners on holds its depth under water (P12).
 // - YawLockRules: the camera that only turns on one axis (P13).
 // - CameraAxisRules: the per-axis input and motion of the camera over a second (P13 trace).
 // - ExportReadDebt: SE's S5 export reads amortised to an average per tick (P14).
 // =================================================================================================================

 /// <summary>
 /// the dampeners are on, shouldn't it
 /// compensate?"). SE's dampener commands, per axis of the
 /// grid, thrust = 2·M·(target − v − g/2) = −M·g − 2·M·v (+ 2·M·target): gravity is cancelled exactly, a velocity is
 /// damped with a 0.5 s time constant, and a constant outside force F (the Water Mod's buoyancy, an APPLY_WORLD_FORCE
 /// it adds every tick) leaves a steady drift v = F / (2·M): the slow rise. The fix offsets the dampening target by
 /// −F / (2·M) (world frame; SE adds it in the grid's frame): the dampeners then command −F as well and the ship holds
 /// its depth, with the thrust they really have (a weak ship still rises, slower). Dampeners off, the pilot's input on
 /// that axis (SE scales the dampening by 1 − |input|) or no thrusters at all (no thrust component): the buoyancy stays.
 /// The character's own swim buoyancy is a different path (S2b) and never touched.
 /// </summary>
    internal static class DampenerHoldRules
    {
        public const string EnvVar = "DEEPENGINEERS_S7P_HOLD";
        public const double DefaultFactor = 1.0;
        public const double MaxFactor = 2.0;
        /// <summary>Sanity cap on |F| / M (m/s²): a larger value (a bad read) is ignored.</summary>
        public const double MaxAccel = 50.0;

        /// <summary>
 /// The Water Mod applies the buoyancy only while the grid is in the water (Water Mod 3.23
 /// <c>WaterPhysicsComponentGrid.UpdateAfter1</c>: <c>FluidDepth &lt; WorldVolume.Radius</c>, <c>SimulatePhysics</c>,
 /// blocks under water); its <c>BuoyancyForce</c> field keeps its last value after the grid leaves, so it is read only then.
 /// </summary>
        public static bool BuoyancyApplied(double fluidDepth, double worldRadius, float percentUnderwater, bool simulatePhysics)
        {
            if (!simulatePhysics) return false;
            if (double.IsNaN(fluidDepth) || double.IsNaN(worldRadius) || float.IsNaN(percentUnderwater)) return false;
            return percentUnderwater > 0f && fluidDepth < worldRadius;
        }

        /// <summary>
 /// The dampening-target offset (world frame, m/s) for a buoyancy force F (N) on a mass M (kg): −F / (2·M) × factor.
 /// False (offset 0) for a non-finite input, a non-positive mass, a zero factor or |F| / M above <see cref="MaxAccel"/>.
 /// </summary>
 /// <summary>
 /// The torque the buoyancy exerts about the centre of mass, negated: −((cb − com) × F) (N·m, world), the counter-torque the

 /// tilting toward the sky"). False when anything is not finite or the lever is over <see cref="MaxLeverM"/> (a corrupt read).
 /// </summary>
        public const double MaxLeverM = 500.0;
        /// <summary>The force hold: −F × factor (N, world), applied at the centre of buoyancy; false when not finite or factor ≤ 0.</summary>
        public static bool HoldForce(double fx, double fy, double fz, double factor, out double hx, out double hy, out double hz)
        {
            hx = hy = hz = 0;
            double[] v = { fx, fy, fz, factor };
            for (int i = 0; i < v.Length; i++) if (double.IsNaN(v[i]) || double.IsInfinity(v[i])) return false;
            if (!(factor > 0)) return false;
            hx = -fx * factor; hy = -fy * factor; hz = -fz * factor;
            return hx != 0 || hy != 0 || hz != 0;
        }

        public static bool CounterTorque(double cbx, double cby, double cbz, double comx, double comy, double comz, double fx, double fy, double fz,
                                         double factor, out double tx, out double ty, out double tz)
        {
            tx = ty = tz = 0;
            double rx = cbx - comx, ry = cby - comy, rz = cbz - comz;
            double[] v = { rx, ry, rz, fx, fy, fz, factor };
            for (int i = 0; i < v.Length; i++) if (double.IsNaN(v[i]) || double.IsInfinity(v[i])) return false;
            if (!(factor > 0)) return false;
            if (rx * rx + ry * ry + rz * rz > MaxLeverM * MaxLeverM) return false;
            // r × F
            double cx = ry * fz - rz * fy, cy = rz * fx - rx * fz, cz = rx * fy - ry * fx;
            tx = -cx * factor; ty = -cy * factor; tz = -cz * factor;
            return tx != 0 || ty != 0 || tz != 0;
        }

        public static bool Offset(double fx, double fy, double fz, double mass, double factor, out double ox, out double oy, out double oz)
        {
            ox = oy = oz = 0;
            if (!(mass > 0) || double.IsInfinity(mass) || !(factor > 0)) return false;
            if (!Finite(fx) || !Finite(fy) || !Finite(fz)) return false;
            double a = Math.Sqrt(fx * fx + fy * fy + fz * fz) / mass;
            if (a <= 0 || a > MaxAccel) return false;
            double k = -factor / (2.0 * mass);
            ox = fx * k; oy = fy * k; oz = fz * k;
            return true;
        }

        /// <summary>
 /// SE's dampener thrust on one axis as the decomp computes it (DampenersEnabled, no input, no relative entity):
 /// 2·M·(target − (v + g/2)) — the reference the offset is checked against.
 /// </summary>
        public static double DampenerThrust(double mass, double target, double v, double g)
        {
            return (target - (v + g / 2.0)) / 0.5 * mass;
        }

        /// <summary>"on"/true = 1, "off"/false = 0, a number in [0, 2] (the fraction of the buoyancy cancelled).</summary>
        public static bool TryParse(object v, out double factor, out string error)
        {
            factor = DefaultFactor;
            error = null;
            if (v == null) return true;
            if (v is bool) { factor = (bool)v ? DefaultFactor : 0.0; return true; }
            string s = v as string;
            double d;
            if (s != null)
            {
                s = s.Trim().ToLowerInvariant();
                if (s.Length == 0 || s == "on" || s == "true") return true;
                if (s == "off" || s == "false") { factor = 0.0; return true; }
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { error = "hold: on | off | a factor in [0, 2]"; return false; }
            }
            else
            {
                try { d = Convert.ToDouble(v, CultureInfo.InvariantCulture); }
                catch (Exception) { error = "hold: on | off | a factor in [0, 2]"; return false; }
            }
            if (double.IsNaN(d) || d < 0 || d > MaxFactor) { error = "hold: a factor in [0, 2]"; return false; }
            factor = d;
            return true;
        }

        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }
    }

    /// <summary>
 /// In SE's walking mode (jetpack off) the
 /// body turns (yaw) only when the character proxy is not in the air, or the character is falling or jumping
 /// the head (pitch) turns always. <c>m_isFalling</c> is set only by
 /// <c>StartFalling</c>, which SE calls on a change of the proxy state into IN_AIR (<c>OnCharacterStateChanged</c>) or
 /// when the jetpack is switched off in the air. <c>Sit</c> clears it and <c>Stand</c> does not set it again, so a
 /// character that leaves a seat while it is already IN_AIR (no state change: under water the swim model keeps it off
 /// the seabed, so it never lands) can look up and down but never turn: the stuck axis. Here: the predicate of that
 /// state, held for <see cref="PersistTicks"/> ticks, is cleared by calling SE's own <c>StartFalling</c>.
 /// </summary>
    internal static class YawLockRules
    {
        public const string EnvVar = "DEEPENGINEERS_S7P_YAWFIX";
        /// <summary>Ticks the state must hold (SE itself leaves it within a tick when it is transient: a jump's end).</summary>
        public const int PersistTicks = 6;
        // HkCharacterStateType: ON_GROUND 0, JUMPING 1, IN_AIR 2, CLIMBING 3; MyCharacter.HK_CHARACTER_FLYING 5
        public const int StateInAir = 2, StateFlying = 5;

        /// <summary>True when SE would ignore the yaw input (the walking-mode body rotation) this tick.</summary>
        public static bool YawBlocked(bool jetpackRunning, bool dead, bool sitting, bool onLadder, bool falling, bool jumping, int groundState)
        {
            if (jetpackRunning || dead || sitting || onLadder) return false;
            if (falling || jumping) return false;
            return groundState == StateInAir || groundState == StateFlying;
        }

        /// <summary>A fix that did not take is tried again this often (ticks) while the state holds.</summary>
        public const int RetryTicks = 120;

        /// <summary>
 /// The persistence counter: true on the tick the fix must run — when the state has held
 /// <see cref="PersistTicks"/> ticks, then every <see cref="RetryTicks"/> while it still holds.
 /// </summary>
        public static bool Step(bool blocked, ref int ticks)
        {
            if (!blocked) { ticks = 0; return false; }
            if (ticks < int.MaxValue) ticks++;
            return ticks == PersistTicks || (ticks > PersistTicks && (ticks - PersistTicks) % RetryTicks == 0);
        }

        public static bool TryParse(object v, out bool on, out string error)
        {
            on = true;
            error = null;
            if (v == null) return true;
            if (v is bool) { on = (bool)v; return true; }
            string s = (v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture)).Trim().ToLowerInvariant();
            if (s == "on" || s == "true" || s == "1" || s.Length == 0) { on = true; return true; }
            if (s == "off" || s == "false" || s == "0") { on = false; return true; }
            error = "yawfix: on | off";
            return false;
        }
    }

    /// <summary>
 /// P13 trace: the camera's input and motion per axis over a window. Yaw about the local up (gravity up on a planet),
 /// pitch = elevation of the forward vector above the local horizon. An axis with input on at least
 /// <see cref="MinInputTicks"/> ticks and less than <see cref="StuckDeg"/> of motion is stuck.
 /// </summary>
    internal sealed class CameraAxisRules
    {
        public const int MinInputTicks = 20;
        public const double StuckDeg = 0.05;

        private bool _have;
        private double _yaw, _pitch;
        public double YawDeg, PitchDeg;
        public int YawInputTicks, PitchInputTicks, BlockedTicks, Samples;

        /// <summary>
 /// One tick: the camera's forward (f) and the local up (u, unit), the rotation input (pitch = x, yaw = y as SE's
 /// RotationIndicator), and whether the yaw-block predicate held.
 /// </summary>
        public void Add(double fx, double fy, double fz, double ux, double uy, double uz, double inPitch, double inYaw, bool yawBlocked)
        {
            if (inYaw != 0) YawInputTicks++;
            if (inPitch != 0) PitchInputTicks++;
            if (yawBlocked) BlockedTicks++;
            double yaw, pitch;
            if (!Angles(fx, fy, fz, ux, uy, uz, out yaw, out pitch)) return;
            if (_have)
            {
                YawDeg += Math.Abs(Wrap(yaw - _yaw));
                PitchDeg += Math.Abs(pitch - _pitch);
            }
            _yaw = yaw; _pitch = pitch; _have = true;
            Samples++;
        }

        public bool YawStuck { get { return YawInputTicks >= MinInputTicks && YawDeg < StuckDeg; } }
        public bool PitchStuck { get { return PitchInputTicks >= MinInputTicks && PitchDeg < StuckDeg; } }

        /// <summary>Restarts the window (the last orientation stays the reference).</summary>
        public void Restart()
        {
            YawDeg = PitchDeg = 0;
            YawInputTicks = PitchInputTicks = BlockedTicks = Samples = 0;
        }

        /// <summary>
 /// Yaw and pitch (degrees) of a forward vector against an up vector: pitch = asin(f·u); yaw = the angle of the
 /// horizontal part of f in a reference frame of the plane ⊥ u (east-like axis = a fixed world axis projected).
 /// False for a degenerate input.
 /// </summary>
        public static bool Angles(double fx, double fy, double fz, double ux, double uy, double uz, out double yaw, out double pitch)
        {
            yaw = pitch = 0;
            double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz), ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (!(fl > 1e-9) || !(ul > 1e-9) || double.IsNaN(fl + ul) || double.IsInfinity(fl + ul)) return false;
            fx /= fl; fy /= fl; fz /= fl; ux /= ul; uy /= ul; uz /= ul;
            double s = fx * ux + fy * uy + fz * uz;
            pitch = Math.Asin(Math.Max(-1, Math.Min(1, s))) * 180.0 / Math.PI;
            // reference axes in the horizontal plane: a = the world axis least aligned with u, made ⊥ u; b = u × a
            double ax = 0, ay = 0, az = 0;
            if (Math.Abs(ux) <= Math.Abs(uy) && Math.Abs(ux) <= Math.Abs(uz)) ax = 1; else if (Math.Abs(uy) <= Math.Abs(uz)) ay = 1; else az = 1;
            double d = ax * ux + ay * uy + az * uz;
            ax -= d * ux; ay -= d * uy; az -= d * uz;
            double al = Math.Sqrt(ax * ax + ay * ay + az * az);
            ax /= al; ay /= al; az /= al;
            double bx = uy * az - uz * ay, by = uz * ax - ux * az, bz = ux * ay - uy * ax;
            double hx = fx - s * ux, hy = fy - s * uy, hz = fz - s * uz;
            if (hx * hx + hy * hy + hz * hz < 1e-12) { yaw = 0; return true; } // straight up/down: yaw undefined (held)
            yaw = Math.Atan2(hx * bx + hy * by + hz * bz, hx * ax + hy * ay + hz * az) * 180.0 / Math.PI;
            return true;
        }

        public static double Wrap(double d)
        {
            d %= 360.0;
            if (d > 180.0) d -= 360.0;
            else if (d <= -180.0) d += 360.0;
            return d;
        }
    }

    /// <summary>
 /// P14: SE's S5 export reads (render cells and fat blocks of the mirrored grids, <c>GridMirrorProducer.S5UpdateGrids</c>)
 /// have a 1.5 ms deadline checked between cells; one render cell of a workshop ship holds thousands of parts and its
 /// budget read.msMax 82.8). The debt keeps the average
 /// a tick that read longer than <see cref="AvgMs"/> owes the excess, and the next ticks skip reading (the stream is
 /// late, far grids last: the producer orders grids nearest first) until it is paid back at <see cref="AvgMs"/> per
 /// tick; the debt is capped at <see cref="CapMs"/>. AvgMs ≤ 0 = off.
 /// </summary>
    internal sealed class ExportReadDebt
    {
        public const string EnvVar = "DEEPENGINEERS_S7P_READAVG";
        public const double DefaultAvgMs = 4.0;
        public const double MaxAvgMs = 50.0;
        public const double DefaultCapMs = 250.0;

        public double AvgMs = DefaultAvgMs;
        public double CapMs = DefaultCapMs;
        private double _debt;
        public long Ticks, Skipped, Spikes;
        public double DebtMs { get { return _debt; } }

        /// <summary>Before the read step: true = read this tick; false = skipped (pays back AvgMs of debt).</summary>
        public bool Allow()
        {
            Ticks++;
            if (AvgMs <= 0 || _debt <= 0) return true;
            _debt -= AvgMs;
            if (_debt < 0) _debt = 0;
            Skipped++;
            return false;
        }

        /// <summary>After a read step that ran: its cost beyond the average becomes debt.</summary>
        public void Spent(double ms)
        {
            if (AvgMs <= 0 || double.IsNaN(ms) || ms <= AvgMs) return;
            Spikes++;
            _debt = Math.Min(CapMs, _debt + (ms - AvgMs));
        }

        public void Reset() { _debt = 0; }

        /// <summary>"off"/0 = off, "on" = the default, a number of ms in (0, 50].</summary>
        public static bool TryParse(object v, out double avgMs, out string error)
        {
            avgMs = DefaultAvgMs;
            error = null;
            if (v == null) return true;
            if (v is bool) { avgMs = (bool)v ? DefaultAvgMs : 0; return true; }
            string s = v as string;
            double d;
            if (s != null)
            {
                s = s.Trim().ToLowerInvariant();
                if (s.Length == 0 || s == "on" || s == "true") return true;
                if (s == "off" || s == "false") { avgMs = 0; return true; }
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { error = "readAvgMs: off | on | ms in (0, 50]"; return false; }
            }
            else
            {
                try { d = Convert.ToDouble(v, CultureInfo.InvariantCulture); }
                catch (Exception) { error = "readAvgMs: off | on | ms in (0, 50]"; return false; }
            }
            if (double.IsNaN(d) || d < 0 || d > MaxAvgMs) { error = "readAvgMs: ms in (0, 50] or off"; return false; }
            avgMs = d;
            return true;
        }
    }
}
