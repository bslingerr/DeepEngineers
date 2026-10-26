using System;
using System.Collections.Generic;
using System.Text;
using DeepEngineers.Wire;
using WireSwimInputs = DeepEngineers.Wire.SwimInputs;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The seam between the SE swim controller and the S2b movement model, <see cref="SwimModel"/> of
 /// <c>DeepEngineers.Wire.dll</c> (CONTRACT §2.10; ROADMAP "S2b Swimming"). Everything the controller knows about the
 /// model goes through this file: one <see cref="SwimModel.Step"/> per SE simulation tick with the tick's inputs in
 /// the SN frame (y up, sea level y = 0, metres and seconds), the controller's view of the result, the oxygen rates
 /// and the <c>swim</c> row (<see cref="SwimTrace"/> plus the SE keys of <see cref="SwimRowExtras"/>). Game-independent
 /// (tested in tests/DeepEngineers.SE.Tests, including the replay of its rows by tests/s2b/swim_replay.py).
 /// <para><b>Controller view</b>: the controller's swim state
 /// is the model's swim latch (<see cref="InWater"/>; modes Swim, Surface and Fly), so its <c>DE.Swim enter|exit</c>
 /// lines fall on the ticks of the rows' Entered/Exited events; the velocity is applied only while the model controls
 /// it (<see cref="Controls"/>: Swim and Surface; in Fly SE's jetpack moves the engineer). Exit reasons: the jetpack
 /// running at the latch exit → jetpack (the climb out); the grounded line → grounded; else surface. Any change of the
 /// state outside <see cref="SwimModel.Step"/> (reset, controller exit, teleport) makes the next row's step index skip
 /// one, so the replay sees a gap there instead of a state mismatch.</para>
 /// <para>The frame conversions of velocities are the CONTRACT §2.3 <c>vel(v) = S·mapdir_P(v)</c> with
 /// <c>S = diag(s, 1, s)</c>, <c>s = Rs/|P − C|</c>, and its inverse.</para>
 /// </summary>
    internal sealed class SwimModelAdapter
    {
        public const string ModelName = "Wire SwimModel v1 (Subnautica Main-scene values, 0.1 s motor step)";

        private SwimState _s;          // the model's state
        private SwimState _pre;        // the state before the last Step (the row's "s_*")
        private WireSwimInputs _in;    // the last Step's inputs
        private SwimOutputs _out;      // the last Step's outputs
        private bool _haveRow;

        /// <summary>The controller's swim state: the model's swim latch (Swim, Surface or Fly).</summary>
        public bool Swimming { get { return _s.Latched; } }

        /// <summary>Same as <see cref="Swimming"/>: the swim latch.</summary>
        public bool InWater { get { return _s.Latched; } }

        /// <summary>The model controlled the velocity on the last tick (Swim or Surface).</summary>
        public bool Controls { get { return _s.Controlling; } }

        /// <summary>The last <see cref="SwimMode"/>.</summary>
        public uint Mode { get { return _s.Mode; } }

        /// <summary>Number of model steps so far (the next row's <c>n</c>).</summary>
        public ulong Steps { get { return _s.Steps; } }

        /// <summary>The last tick's outputs (valid after a <see cref="Tick"/>).</summary>
        public SwimOutputs LastOutputs { get { return _out; } }

        /// <summary>Back to Walk with no held velocity (world load, character change). The step index restarts at 0.</summary>
        public void Reset()
        {
            _s = default(SwimState);
            _haveRow = false;
        }

        /// <summary>
 /// The controller's own exits (model off, seat, death, left the dive planet, velocity write failure): back to Walk
 /// without a model transition; the next tick in the water enters again. The step index skips one (a replay gap).
 /// </summary>
        public void ForceOut()
        {
            ulong n = _s.Steps;
            _s = default(SwimState);
            _s.Steps = n + 1;
        }

        /// <summary>After a teleport: the latch is kept, the held velocity dropped, the next controlled tick runs a fresh motor step (a replay gap).</summary>
        public void RestartStep()
        {
            _s.Phase = 0;
            _s.Held = Vec3.Zero;
            _s.Steps++;
        }

        /// <summary>One SE tick through the model.</summary>
        public SwimTickOut Tick(ref SwimTickIn t)
        {
            _in = new WireSwimInputs
            {
                Tick = t.Tick,
                MoveStrafe = t.Strafe,
                MoveVertical = t.Vertical,
                MoveForward = t.Forward,
                Sprint = t.Sprint,
                ToolHeld = t.ToolHeld,
                Grounded = t.Grounded,
                JetpackOn = t.JetpackOn,
                OnLadder = t.OnLadder,
                OnGrid = t.OnGrid,
                LookForward = t.LookForwardSn,
                LookRight = t.LookRightSn,
                Velocity = t.VelocitySn,
                EyePos = new Vec3(t.EyeSn.X, t.EyeY, t.EyeSn.Z),
                Oxygen = t.OxygenUnits,
                Mutants = ModelMutantBits(t.Mutants),
                // S5a.M1: the model runs without the fins, the row and C1 claim them (the replay and the fins oracle go red)
                FinsModifier = t.FinsNotApplied ? 0.0 : t.FinsModifier,
            };
            _pre = _s;
            SwimModel.Step(ref _s, ref _in, out _out);
            if (t.FinsNotApplied) _in.FinsModifier = t.FinsModifier;
            _haveRow = true;

            var o = new SwimTickOut
            {
                Swimming = _s.Latched,
                ControlsVelocity = _out.ControlsVelocity,
                Mode = _out.Mode,
                Events = _out.Events,
                Stepped = _out.SnStep,
                VelocitySn = _out.Velocity,
                JetpackAllowed = _out.JetpackAllowed,
                DepthClass = _out.DepthClass,
                OxygenRuleActive = _out.OxygenRuleActive,
                OxygenDelta = _out.OxygenDelta,
                MaxSpeed = _out.MaxSpeed,
                Yield = _out.Yield,
            };
            if ((_out.Events & SwimEvents.Entered) != 0) o.Transition = 1;
            else if ((_out.Events & SwimEvents.Exited) != 0)
            {
                o.Transition = -1;
                o.ExitReason = ExitReasonOf(t.EyeY, t.Grounded, t.JetpackOn, _in.Mutants);
            }
            Rates(_out, t.EyeY, out o.O2UseUnitsPerSec, out o.O2RefillUnitsPerSec);
            return o;
        }

        /// <summary>Why the latch turned off this tick (model reasons 1-3 of <see cref="SwimExit"/>).</summary>
        public static uint ExitReasonOf(double eyeY, bool grounded, bool jetpackOn, uint wireMutants)
        {
            if (jetpackOn) return SwimExit.Jetpack;
            if ((wireMutants & SwimMutants.M3NoHysteresis) == 0 && grounded &&
                eyeY > SwimThresholds.SwimExitGroundedEyeY && eyeY < SwimThresholds.SwimExitEyeY)
                return SwimExit.Grounded;
            return SwimExit.Surface;
        }

        /// <summary>
 /// The oxygen rule as rates (Subnautica units per second): breathing with the eye below 0 (1 / 1.5 / 2 per second
 /// at the safe / unsafe / crush depth classes, <see cref="SwimModel.BreathOf"/>), refill above −1 m (30 per
 /// second); both 0 while the rule is not in force (out of the water, S2b.M4).
 /// </summary>
        public static void Rates(SwimOutputs o, double eyeY, out double usePerSec, out double refillPerSec)
        {
            usePerSec = 0.0;
            refillPerSec = 0.0;
            if (!o.OxygenRuleActive) return;
            if (eyeY < SwimThresholds.UnderwaterEyeY)
            {
                int period;
                double amount;
                SwimModel.BreathOf(o.DepthClass, out period, out amount);
                if (period > 0) usePerSec = amount / (period * SwimThresholds.SeTickDt);
            }
            if (eyeY > SwimThresholds.OxygenRefillEyeY) refillPerSec = SwimThresholds.OxygenRefillPerSecond;
        }

        /// <summary>S2b mutant bits (<see cref="S2bMutants.ModelBits"/>) to the model's own (<see cref="SwimMutants"/>).</summary>
        public static uint ModelMutantBits(uint s2b)
        {
            uint m = 0;
            if ((s2b & S2bMutants.BitM1) != 0) m |= SwimMutants.M1Numbers;
            if ((s2b & S2bMutants.BitM2) != 0) m |= SwimMutants.M2JetpackInWater;
            if ((s2b & S2bMutants.BitM3) != 0) m |= SwimMutants.M3NoHysteresis;
            if ((s2b & S2bMutants.BitM4) != 0) m |= SwimMutants.M4OxygenOff;
            return m;
        }

        /// <summary>
 /// The last tick's <c>swim</c> row: the Wire <see cref="SwimTrace"/> row (exact bits, replayed by
 /// tests/s2b/swim_replay.py) with a UTC <c>t</c> first and the SE keys of <paramref name="x"/> appended (no key
 /// collides with SwimTrace's). Null before the first tick. Allocates the row string (tests and tools; the
 /// controller uses <see cref="AppendRow"/>, which allocates nothing).
 /// </summary>
        public string Row(char[] buf, string segment, ref SwimRowExtras x, StringBuilder sb)
        {
            return AppendRow(buf, segment, ref x, sb) ? sb.ToString() : null;
        }

        /// <summary>
 /// The row of <see cref="Row"/> written into <paramref name="sb"/> (cleared first) without allocating once the
 /// build pass has grown.
 /// False before the first tick or when the Wire row does not fit <paramref name="buf"/>.
 /// </summary>
        public bool AppendRow(char[] buf, string segment, ref SwimRowExtras x, StringBuilder sb)
        {
            if (!_haveRow) return false;
            int n = SwimTrace.Format(ref _pre, ref _in, ref _out, buf, segment);
            if (n < 2) return false;
            sb.Length = 0;
            sb.Append('{');
            if (x.Utc != null)
            {
                sb.Append("\"t\":");
                JsonText.AppendString(sb, x.Utc);
                sb.Append(',');
            }
            else if (x.UtcTicks > 0)
            {
                sb.Append("\"t\":\"");
                SwimText.AppendUtc(sb, x.UtcTicks);
                sb.Append("\",");
            }
            sb.Append(buf, 1, n - 2); // the SwimTrace keys without the outer braces
            x.AppendTo(sb);
            sb.Append('}');
            return true;
        }

        /// <summary>Subnautica's O2 tank in seconds of breathing (units), for the oxygen rule: the diver's own 45, no tank.</summary>
        public static double TankSeconds { get { return SwimThresholds.OxygenCapacity; } }

        /// <summary>The model's numbers for <c>de.swim dump</c> (the oracle's replay uses the same ones, tests/s2b/sn_reference.py).</summary>
        public static Dictionary<string, object> Numbers()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "model", ModelName },
                { "forwardMaxSpeed", SwimThresholds.DiveForwardMax },
                { "backwardMaxSpeed", SwimThresholds.DiveBackwardMax },
                { "strafeMaxSpeed", SwimThresholds.DiveStrafeMax },
                { "verticalMaxSpeed", SwimThresholds.VerticalMax },
                { "runMaxSpeeds", new object[] { SwimThresholds.RunForwardMax, SwimThresholds.RunBackwardMax, SwimThresholds.RunStrafeMax } },
                { "waterAcceleration", SwimThresholds.WaterAcceleration },
                { "swimDrag", SwimThresholds.SwimDrag },
                { "surfaceSpeedMultiplier", SwimThresholds.SurfaceSpeedMultiplier },
                { "heldToolPenalty", SwimThresholds.HeldToolPenalty },
                { "entryVerticalFactor", SwimThresholds.EntryVerticalFactor },
                { "groundAcceleration", SwimThresholds.GroundAcceleration },
                { "surfaceBand", new object[] { SwimThresholds.SurfaceBandBottomY, SwimThresholds.SurfaceTopY } },
                { "surfaceExponent", SwimThresholds.SurfaceExponent },
                { "lookDownY", SwimThresholds.LookDownY },
                { "sprintFactor", SwimThresholds.SprintFactor },
                { "fixedStepSec", SwimThresholds.SnStepDt },
                { "stepTicks", SwimThresholds.SnStepTicks },
                { "enterEyeY", SwimThresholds.SwimEnterEyeY },
                { "exitEyeY", SwimThresholds.SwimExitEyeY },
                { "exitEyeYGrounded", SwimThresholds.SwimExitGroundedEyeY },
                { "jetpackEyeY", new object[] { SwimThresholds.JetpackOffEyeY, SwimThresholds.JetpackOnEyeY } },
                { "o2PerSec", new object[] { SwimThresholds.BreathFactorSafe, SwimThresholds.BreathFactorUnsafe, SwimThresholds.BreathFactorCrush } },
                { "o2DepthClassesM", new object[] { SwimThresholds.SafeDepth, SwimThresholds.UnsafeDepth, SwimThresholds.CrushDepth } },
                { "o2RefillPerSec", SwimThresholds.OxygenRefillPerSecond },
                { "o2RefillAboveY", SwimThresholds.OxygenRefillEyeY },
                { "o2TankSeconds", SwimThresholds.OxygenCapacity },
                { "mutantM1Factor", SwimThresholds.MutantM1Scale },
                { "mutantM3EyeY", SwimThresholds.MutantM3EyeY },
                { "finsSpeedAdjust", new object[] { 0.0, SwimThresholds.FinsSpeedAdjust, SwimThresholds.UltraglideFinsSpeedAdjust } },
            };
        }

        public static string DepthClassName(uint c)
        {
            switch (c)
            {
                case SwimDepthClass.Surface: return "surface";
                case SwimDepthClass.Safe: return "safe";
                case SwimDepthClass.Unsafe: return "unsafe";
                case SwimDepthClass.Crush: return "crush";
                default: return "?" + c;
            }
        }

        // ================================================================================== SE <-> SN conversions

 /// <summary>SE-world velocity at SE point <paramref name="p"/> to SN m/s: S·mapdir_P(v).</summary>
        public static Vec3 SeToSnVelocity(SiteMap site, Vec3 p, Vec3 vSe)
        {
            Vec3 e, u, n;
            site.FrameAt(p, out e, out u, out n);
            Vec3 d = site.DirSeToSn(e, u, n, vSe);
            double s = site.ScaleAt(p);
            return new Vec3(d.X * s, d.Y, d.Z * s);
        }

        /// <summary>SN velocity (m/s) to SE-world velocity at SE point <paramref name="p"/>: mapdir_P⁻¹(S⁻¹·v).</summary>
        public static Vec3 SnToSeVelocity(SiteMap site, Vec3 p, Vec3 vSn)
        {
            Vec3 e, u, n;
            site.FrameAt(p, out e, out u, out n);
            double s = site.ScaleAt(p);
            return site.DirSnToSe(e, u, n, new Vec3(vSn.X / s, vSn.Y, vSn.Z / s));
        }

        /// <summary>SE-world direction at P to an SN unit direction (mapdir, renormalised).</summary>
        public static Vec3 SeToSnDirection(SiteMap site, Vec3 p, Vec3 wSe)
        {
            Vec3 d = site.DirSeToSn(p, wSe);
            double l = d.Length;
            return l > 1e-12 ? d / l : Vec3.Zero;
        }
    }

    /// <summary>One SE tick's inputs to the model, SN frame (see <see cref="SwimModelAdapter"/>).</summary>
    internal struct SwimTickIn
    {
        /// <summary>SE tick (<c>MySession.GameplayFrameCounter</c>): the breath phase of the oxygen rule.</summary>
        public ulong Tick;
        /// <summary>Forward (+) / backward (−), strafe right (+) / left (−), up (+) / down (−), each in [−1, 1].</summary>
        public double Forward, Strafe, Vertical;
        /// <summary>SE's sprint key (logged; Subnautica's swim motor has no sprint).</summary>
        public bool Sprint;
        /// <summary>A hand tool or weapon is held (Subnautica: max speed −1 m/s).</summary>
        public bool ToolHeld;
        /// <summary>The look: camera forward and right as SN unit vectors.</summary>
        public Vec3 LookForwardSn, LookRightSn;
        /// <summary>SN y of the eye (the first-person camera point) before this tick's physics step: the model's height.</summary>
        public double EyeY;
        /// <summary>The eye in SN metres (X and Z are logged; <see cref="EyeY"/> is the height used).</summary>
        public Vec3 EyeSn;
        /// <summary>SN y of the chest (eye − 0.4 m): logged only.</summary>
        public double ChestY;
        /// <summary>The character stands on something (SE character proxy <c>Supported</c>).</summary>
        public bool Grounded;
        /// <summary>SE's jetpack is running (on and powered): with the head out the model yields to it (Fly).</summary>
        public bool JetpackOn;
        /// <summary>The character is on a ladder (<c>MyCharacter.IsOnLadder</c>): the model yields the tick to SE's ladder.</summary>
        public bool OnLadder;
        /// <summary>The character's support is a cube grid (deck): with the jetpack off and no up input the model yields to SE's walking.</summary>
        public bool OnGrid;
        /// <summary>The character's velocity, SN m/s (the controller's velocity source: after the previous physics step by default).</summary>
        public Vec3 VelocitySn;
        /// <summary>The suit's oxygen in Subnautica units (SE suit fill level × 45; NaN when it cannot be read).</summary>
        public double OxygenUnits;
        /// <summary>S2b mutant bits (<see cref="S2bMutants.ModelBits"/>).</summary>
        public uint Mutants;
        /// <summary>S5a fins modifier, m/s (<see cref="SwimFins.Modifier"/>; the controller's setting, default ultra 3.2).</summary>
        public double FinsModifier;
        /// <summary>S5a.M1: step without the fins, log them anyway (<see cref="S5aMutants.FinsNotApplied"/>).</summary>
        public bool FinsNotApplied;
    }

    /// <summary>One SE tick's outputs of the model, as the controller uses them.</summary>
    internal struct SwimTickOut
    {
        /// <summary>The swim latch is on (modes Swim, Surface, Fly): the controller's swim state.</summary>
        public bool Swimming;
        /// <summary>The model sets the velocity this tick (Swim, Surface).</summary>
        public bool ControlsVelocity;
        /// <summary><see cref="SwimYield"/>: SE moves the character this tick (a ladder, a grid deck) while the latch holds.</summary>
        public uint Yield;
        /// <summary><see cref="SwimMode"/>.</summary>
        public uint Mode;
        /// <summary><see cref="SwimEvents"/>.</summary>
        public uint Events;
        /// <summary>+1 entered the water this tick (latch on), −1 left it (latch off), 0 no change.</summary>
        public int Transition;
        /// <summary>The exit reason (<see cref="SwimExit"/>; 1 surface, 2 grounded, 3 jetpack).</summary>
        public uint ExitReason;
        /// <summary>A Subnautica motor step ran this tick (the velocity is held between steps).</summary>
        public bool Stepped;
        /// <summary>The velocity to apply this tick, SN m/s (valid while <see cref="ControlsVelocity"/>).</summary>
        public Vec3 VelocitySn;
        /// <summary>SE's jetpack may run (always out of the water; in it only with the head out).</summary>
        public bool JetpackAllowed;
        /// <summary>Subnautica depth class of the eye (0 surface, 1 safe, 2 unsafe, 3 crush).</summary>
        public uint DepthClass;
        /// <summary>The oxygen rule is in force (in the water, not S2b.M4).</summary>
        public bool OxygenRuleActive;
        /// <summary>The model's oxygen change this tick (Subnautica units; logged and replayed, the controller applies the rates).</summary>
        public double OxygenDelta;
        /// <summary>O2 used and refilled, Subnautica units (seconds of breathing) per second.</summary>
        public double O2UseUnitsPerSec, O2RefillUnitsPerSec;
        /// <summary>The motor's max speed at this tick's step (fins, tool and surface included); 0 on a tick without a step (S5a speed reference).</summary>
        public double MaxSpeed;
    }

    /// <summary>
 /// The SE keys appended to the Wire <c>swim</c> row (<see cref="SwimModelAdapter.Row"/>; read by
 /// tests/s2b/analyze_swim.py, ignored by the replay): <c>siteVersion</c>, <c>dt</c>, <c>src</c> (script|keys|none),
 /// <c>step</c> (script step index, −1 none), <c>vsrc</c> (the velocity source), <c>exit</c> (reason name on an exit
 /// tick), <c>chestY</c>, <c>applied</c> (the velocity was written), <c>gain</c>, <c>vApp</c> (written = vel × gain),
 /// <c>vSe</c> (SE's velocity right after its physics step, SN m/s: the measured motion), <c>posAfter</c> (the eye after
 /// the step), <c>jetWant</c>, <c>damp</c>, <c>dampWant</c>, <c>o2Use</c>, <c>o2Refill</c> (units/s) and, when the
 /// suit can be read, <c>o2L</c>, <c>o2Cap</c>, <c>o2Mult</c>, <c>helmet</c>. Numbers are fixed-point decimals (9 for
 /// <c>dt</c>, 6 otherwise; <c>null</c> for NaN), written without allocation; the replayed values are the Wire row's bits.
 /// </summary>
    internal struct SwimRowExtras
    {
        /// <summary>The UTC stamp as text (tests and tools); null → <see cref="UtcTicks"/>.</summary>
        public string Utc { get; set; }
        /// <summary>The UTC stamp as <c>DateTime.Ticks</c> (the controller: no string); 0 = none.</summary>
        public long UtcTicks;
        public int SiteVersion;
        public double Dt;
        public string Src;
        public int Step;
        public string VelocitySource;
        public string Exit;
        public double ChestY;
        public bool Applied;
        public double Gain;
        public Vec3 VApplied, VSe, PosAfter;
        public bool JetWant, Damp, DampWant;
        public double O2Use, O2Refill;
        public bool HaveSuit;
        public double O2Litres, O2Capacity, O2Multiplier;
        public bool Helmet;

        public void AppendTo(StringBuilder sb)
        {
            sb.Append(",\"siteVersion\":"); SwimText.AppendInt(sb, SiteVersion);
            sb.Append(",\"dt\":"); SwimText.AppendFixed(sb, Dt, 9);
            sb.Append(",\"src\":\"").Append(Src ?? "none").Append('"');
            sb.Append(",\"step\":"); SwimText.AppendInt(sb, Step);
            sb.Append(",\"vsrc\":\"").Append(VelocitySource ?? "").Append('"');
            sb.Append(",\"exit\":\"").Append(Exit ?? "").Append('"');
            sb.Append(",\"chestY\":"); SwimText.AppendFixed(sb, ChestY, 6);
            sb.Append(",\"applied\":").Append(Applied ? '1' : '0');
            sb.Append(",\"gain\":"); SwimText.AppendFixed(sb, Gain, 6);
            sb.Append(",\"vApp\":"); SwimText.Arr(sb, VApplied);
            sb.Append(",\"vSe\":"); SwimText.Arr(sb, VSe);
            sb.Append(",\"posAfter\":"); SwimText.Arr(sb, PosAfter);
            sb.Append(",\"jetWant\":").Append(JetWant ? '1' : '0');
            sb.Append(",\"damp\":").Append(Damp ? '1' : '0');
            sb.Append(",\"dampWant\":").Append(DampWant ? '1' : '0');
            sb.Append(",\"o2Use\":"); SwimText.AppendFixed(sb, O2Use, 6);
            sb.Append(",\"o2Refill\":"); SwimText.AppendFixed(sb, O2Refill, 6);
            if (HaveSuit)
            {
                sb.Append(",\"o2L\":"); SwimText.AppendFixed(sb, O2Litres, 6);
                sb.Append(",\"o2Cap\":"); SwimText.AppendFixed(sb, O2Capacity, 6);
                sb.Append(",\"o2Mult\":"); SwimText.AppendFixed(sb, O2Multiplier, 6);
                sb.Append(",\"helmet\":").Append(Helmet ? '1' : '0');
            }
        }
    }
}
