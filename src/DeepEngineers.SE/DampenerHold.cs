using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S7p-gameplay P12: a ship with inertial dampeners on holds its depth under water (<see cref="DampenerHoldRules"/>).
 /// <para><b>How the rise happened</b>: the buoyancy is the
 /// Water Mod's, not ours — <c>WaterPhysicsComponentGrid.UpdateAfter1</c> (a game-logic component on every grid, after
 /// simulation) calls <c>IGrid.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, BuoyancyForce, centre of
 /// buoyancy, null)</c> every tick while the grid is in the water; Havok applies it in the next step. SE's dampeners
 /// (<c>MyEntityThrustComponent.UpdateBeforeSimulation(bool, MyEntity)</c> → <c>UpdateThrusts</c> →
 /// <c>ComputeBaseThrust</c>) cancel gravity exactly and damp velocity proportionally, so a constant upward force leaves
 /// a steady rise of F / (2·M).</para>
 /// <para><b>The fix</b>: a Harmony postfix on the private <c>MyEntityThrustComponent.CalculateRelativeVelocity(MyEntity)</c>
 /// (returns the dampening target in the grid's frame: <c>Vector3.Zero</c> without a relative-dampening entity) adds
 /// −F / (2·M) in the grid's frame (<c>Vector3.TransformNormal(Vector3, MatrixD)</c> with
 /// <c>PositionComp.WorldMatrixNormalizedInv</c>, as SE computes its own target): ComputeBaseThrust then commands −F on
 /// top of the gravity compensation, through the real thrusters (power, clamping to their maximum, the pilot's input
 /// scaling the dampening on an axis: all SE's). Only for a <c>MyCubeGrid</c> entity (never the character's jetpack),
 /// dampeners on, a dynamic physics body, the Water Mod applying buoyancy now; autopilot thrust (<c>ComputeAiThrust</c>)
 /// does not read the target (HYPOTHESIS: an autopilot ship keeps the drift). The thrust update runs in SE's parallel
 /// grid updates (<c>Schedule(…, parallel: true)</c>): the postfix reads only the grid's own state and writes only the
 /// grid's own statistics entry.</para>
 /// <para>Live: <c>de.s7p {hold: on | off | factor}</c>, env <c>DEEPENGINEERS_S7P_HOLD</c> (default on). Rows
 /// <c>s7p.hold</c> once a second for the grids it held (≤ 8).</para>
 /// </summary>
    internal static class DampenerHold
    {
        private const string Id = "deepengineers.se.s7p.hold";
        public static bool Patched { get; private set; }
        public static string PatchError { get; private set; }
        /// <summary>The fraction of the buoyancy cancelled (0 = off).</summary>
        public static volatile string FactorText = "1";
        private static double _factor = DampenerHoldRules.DefaultFactor;
        public static double Factor { get { return _factor; } set { _factor = value; FactorText = value.ToString("0.###", CultureInfo.InvariantCulture); } }

        internal sealed class Stat
        {
            public long Grid;
            public long Applications, ReportedApplications, Torques, Forces;
            public float ForceN, MassKg, OffsetMs, TorqueNm;
            public bool Applied;
        }

        public static long TorqueApplications, TorqueNoCentre, ForceApplications, ForceNoCentre;
        /// <summary>
 /// The thrust-target path
 /// cancelled the rise through the thrusters, and thrusters that do not sit on the centre of mass turned that constant
 /// thrust into a pitch the gyros only damp (the counter-torque of 00:40 did not remove it: s7p.hold rows show it
 /// applied while the ship still pitched). In force mode the hold applies −F × factor at the Water Mod's centre of
 /// buoyancy each tick (<c>AddForce(APPLY_WORLD_FORCE, force, position, null)</c>): the mod's force and ours act at the
 /// same point, so the net force and the net torque both vanish; the thrusters get no target and burn nothing. Without
 /// the centre field the force goes to the centre of mass (the rise still cancelled, counted in ForceNoCentre).
 /// <c>de.s7p hold=force|thrust</c>, env DEEPENGINEERS_S7P_HOLD_MODE.
 /// </summary>
        public static volatile bool ForceMode = ReadForceMode();

        private static bool ReadForceMode()
        {
            try { string v = Environment.GetEnvironmentVariable("DEEPENGINEERS_S7P_HOLD_MODE"); return !(v != null && v.Trim().Equals("thrust", StringComparison.OrdinalIgnoreCase)); }
            catch (Exception) { return true; }
        }

        public static string ModeText { get { return ForceMode ? "force" : "thrust"; } }

        /// <summary>
 /// The Water Mod applies its
 /// buoyancy at the centre of buoyancy (<c>WaterPhysicsComponentGrid.UpdateAfter1</c>: <c>AddForce(APPLY_WORLD_FORCE,
 /// BuoyancyForce, centre, null)</c>), so a ship whose centre of buoyancy is off its centre of mass gets a steady torque; the
 /// gyros damp angular velocity proportionally, so it drifts. Each tick (main thread, before the simulation; the Havok step
 /// sums both) the grids the hold acted on get the counter-torque −((cb − com) × F) × factor through
 /// <c>MyPhysicsComponentBase.AddForce(MyPhysicsForceType, Vector3?, Vector3D?, Vector3?, float?, bool, bool)</c>
 /// with the torque only. Dampeners off, a static body or a missing centre field: nothing (counted).
 /// </summary>
        public static void Tick()
        {
            if (!Patched || Stats.Count == 0) return;
            double factor = Factor;
            if (!(factor > 0)) return;
            WaterModClient api = SiteBinding.Api;
            if (api == null) return;
            bool centreOk = api.GridBuoyancyCentreReady;
            if (!centreOk && !ForceMode) { TorqueNoCentre++; return; }
            foreach (KeyValuePair<long, Stat> kv in Stats)
            {
                Stat s = kv.Value;
                if (!s.Applied) continue;
                MyEntity e;
                if (!MyEntities.TryGetEntityById(kv.Key, out e)) continue;
                var grid = e as MyCubeGrid;
                if (grid == null || grid.Closed || grid.MarkedForClose || grid.Physics == null || grid.Physics.IsStatic || !grid.Physics.Enabled) continue;
                MyEntityThrustComponent thrust;
                if (!grid.Components.TryGet(out thrust) || thrust == null || !thrust.DampenersEnabled) continue;
                Vector3 force = Vector3.Zero;
                bool applied = false;
                Vector3D com = grid.Physics.CenterOfMassWorld;
                Vector3D cb = com;
                if (ForceMode)
                {
                    bool haveCentre = centreOk && api.TryReadGridBuoyancy(grid, out force, out applied, out cb);
                    if (!haveCentre)
                    {
                        if (!api.TryReadGridBuoyancy(grid, out force, out applied)) continue;
                        cb = com;
                        ForceNoCentre++;
                    }
                    if (!applied) continue;
                    double hx, hy, hz;
                    if (!DampenerHoldRules.HoldForce(force.X, force.Y, force.Z, factor, out hx, out hy, out hz)) continue;
                    try
                    {
                        grid.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, new Vector3((float)hx, (float)hy, (float)hz), cb, null);
                        s.Forces++;
                        ForceApplications++;
                    }
                    catch (Exception) { Rejected++; }
                    continue;
                }
                if (!api.TryReadGridBuoyancy(grid, out force, out applied, out cb) || !applied) continue;
                double tx, ty, tz;
                if (!DampenerHoldRules.CounterTorque(cb.X, cb.Y, cb.Z, com.X, com.Y, com.Z, force.X, force.Y, force.Z, factor, out tx, out ty, out tz)) continue;
                try
                {
                    grid.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, null, null, new Vector3((float)tx, (float)ty, (float)tz));
                    s.Torques++;
                    s.TorqueNm = (float)Math.Sqrt(tx * tx + ty * ty + tz * tz);
                    TorqueApplications++;
                }
                catch (Exception) { Rejected++; }
            }
        }

        private static readonly ConcurrentDictionary<long, Stat> Stats = new ConcurrentDictionary<long, Stat>();
        public static long Calls, Applications, NotGrid, NoBuoyancy, NotApplied, Rejected;

        public static void Apply()
        {
            string env = Environment.GetEnvironmentVariable(DampenerHoldRules.EnvVar);
            double f;
            string error;
            if (!DampenerHoldRules.TryParse(env, out f, out error)) { f = DampenerHoldRules.DefaultFactor; DeLog.Info("DE.S7p hold: " + DampenerHoldRules.EnvVar + "=" + env + " " + error + " (default on)"); }
            Factor = f;
            try
            {
                MethodInfo m = AccessTools.Method(typeof(MyEntityThrustComponent), "CalculateRelativeVelocity", new[] { typeof(MyEntity) });
                if (m == null || m.ReturnType != typeof(Vector3))
                {
                    PatchError = "MyEntityThrustComponent.CalculateRelativeVelocity(MyEntity) returning Vector3 not found";
                    DeLog.Info("DE.Harmony MISSING s7p hold: " + PatchError + " (the depth hold is off)");
                    return;
                }
                var harmony = new Harmony(Id);
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(DampenerHold).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Patched = true;
                DeLog.Info("DE.Harmony SE s7p hold: MyEntityThrustComponent.CalculateRelativeVelocity postfix (dampeners hold the depth against the Water Mod's buoyancy; factor " +
                           FactorText + (string.IsNullOrEmpty(env) ? "" : " from " + DampenerHoldRules.EnvVar) + ")");
            }
            catch (Exception e)
            {
                PatchError = e.GetType().Name + ": " + e.Message;
                DeLog.Info("DE.Harmony s7p hold FAIL " + PatchError + " (the depth hold is off; everything else unaffected)");
            }
        }

        /// <summary>SE's parallel thrust update: the dampening target gets −F / (2·M) in the grid's frame.</summary>
        private static void Postfix(MyEntityThrustComponent __instance, ref Vector3 __result)
        {
            try
            {
                double factor = _factor;
                if (!(factor > 0)) return;
                System.Threading.Interlocked.Increment(ref Calls);
                var grid = __instance.Entity as MyCubeGrid;
                if (grid == null) { System.Threading.Interlocked.Increment(ref NotGrid); return; } // the character's jetpack: untouched
                if (!__instance.DampenersEnabled) return;
                var ph = grid.Physics;
                if (ph == null || ph.IsStatic || !ph.Enabled) return;
                WaterModClient api = SiteBinding.Api;
                Vector3 force;
                bool applied;
                if (api == null || !api.TryReadGridBuoyancy(grid, out force, out applied)) { System.Threading.Interlocked.Increment(ref NoBuoyancy); return; }
                if (!applied) { System.Threading.Interlocked.Increment(ref NotApplied); return; }
                if (ForceMode)
                {
                    // force mode: no thrust target; the grid is registered for Tick, which applies the force at the centre of buoyancy
                    Stat fs = Stats.GetOrAdd(grid.EntityId, id => new Stat { Grid = id });
                    fs.Applications++;
                    fs.ForceN = force.Length();
                    fs.MassKg = (float)ph.Mass;
                    fs.OffsetMs = 0f;
                    fs.Applied = true;
                    return;
                }
                double mass = ph.Mass;
                double ox, oy, oz;
                if (!DampenerHoldRules.Offset(force.X, force.Y, force.Z, mass, factor, out ox, out oy, out oz)) { System.Threading.Interlocked.Increment(ref Rejected); return; }
                MatrixD inv = grid.PositionComp.WorldMatrixNormalizedInv;
                __result += Vector3.TransformNormal(new Vector3((float)ox, (float)oy, (float)oz), inv);
                System.Threading.Interlocked.Increment(ref Applications);
                Stat s = Stats.GetOrAdd(grid.EntityId, id => new Stat { Grid = id });
                s.Applications++;
                s.ForceN = force.Length();
                s.MassKg = (float)mass;
                s.OffsetMs = (float)Math.Sqrt(ox * ox + oy * oy + oz * oz);
                s.Applied = true;
            }
            catch (Exception)
            {
                // never into SE's thrust update; counted
                System.Threading.Interlocked.Increment(ref Rejected);
            }
        }

        /// <summary>
 /// s7p.hold (P12; plugin Update, once a second): the grids the hold acted on in the last second (≤ 8): the
 /// buoyancy, the mass, the target offset, the grid's vertical speed along the local up (≈ 0 when it holds) and the
 /// applications; the counters of the postfix.
 /// </summary>
        public static void Second(ulong tick)
        {
            if (!Patched) return;
            int n = 0;
            foreach (KeyValuePair<long, Stat> kv in Stats)
            {
                Stat s = kv.Value;
                long apps = s.Applications - s.ReportedApplications;
                if (apps <= 0) continue;
                s.ReportedApplications = s.Applications;
                if (n++ >= 8) continue;
                double vUp = double.NaN;
                MyEntity e;
                if (MyEntities.TryGetEntityById(kv.Key, out e) && e != null && e.Physics != null)
                {
                    Vector3D p = e.PositionComp.GetPosition();
                    Vector3 g = Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(p);
                    if (g.LengthSquared() > 1e-6f)
                    {
                        Vector3 up = -Vector3.Normalize(g);
                        vUp = Vector3.Dot(e.Physics.LinearVelocity, up);
                    }
                }
                var row = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }, { "ev", "s7p.hold" }, { "tick", (long)tick },
                    { "entity", kv.Key.ToString(CultureInfo.InvariantCulture) }, { "buoyancyN", (double)s.ForceN }, { "massKg", (double)s.MassKg },
                    { "offsetMs", (double)s.OffsetMs }, { "vUpMs", double.IsNaN(vUp) ? null : (object)Math.Round(vUp, 4) }, { "applications", apps },
                    { "torqueNm", (double)s.TorqueNm }, { "torques", s.Torques }, { "torqueTotal", TorqueApplications }, { "torqueNoCentre", TorqueNoCentre },
                    { "mode", ModeText }, { "forces", s.Forces }, { "forceTotal", ForceApplications }, { "forceNoCentre", ForceNoCentre },
                    { "factor", Factor }, { "callsTotal", Calls }, { "applicationsTotal", Applications }, { "noBuoyancy", NoBuoyancy }, { "notApplied", NotApplied },
                    { "rejected", Rejected },
                };
                Jsonl.WriteLineBuffered(JsonText.Serialize(row));
            }
            if (Stats.Count > 1024) Stats.Clear();
        }

        public static void Forget() { Stats.Clear(); }

        public static Dictionary<string, object> Status()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            d["holdPatched"] = Patched;
            d["holdError"] = PatchError;
            d["holdFactor"] = Factor;
            d["holdMode"] = ModeText;
            d["holdForceTotal"] = ForceApplications;
            d["holdCalls"] = Calls;
            d["holdApplications"] = Applications;
            d["holdNoBuoyancy"] = NoBuoyancy;
            d["holdNotApplied"] = NotApplied;
            d["holdRejected"] = Rejected;
            d["holdGrids"] = Stats.Count;
            WaterModClient api = SiteBinding.Api;
            d["holdBuoyancyReader"] = api != null && api.GridBuoyancyReady ? "ready" : (api != null ? (api.GridBuoyancyError ?? "API not received") : "no API");
            return d;
        }
    }
}
