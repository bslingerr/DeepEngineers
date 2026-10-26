using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>

 /// hydrogen thrust is constant, its air density is clamped to full below the sea's radius, its gravity is 1 between the
 /// planet's minimum and maximum radius; the Water Mod's drag and buoyancy carry no depth term), so the measurement decides:
 /// every 5 s in world, the controlled grid's thrusters by definition subtype: count, working, the sum of the definition's
 /// force, of SE's own <c>MaxEffectiveThrust</c> (its effectiveness after the air density) and of <c>CurrentThrust</c>; with
 /// the grid's depth, mass, speed, vertical speed, the natural gravity and the air density at the grid. Row <c>s16.thrust</c>;
 /// a <c>DE.S16 thrust</c> line every 30 s. Read-only, never throws.
 /// </summary>
    internal static class S16ThrustProbe
    {
        private static ulong _nextMs, _nextLogMs;
        private static readonly Dictionary<string, double[]> _byType = new Dictionary<string, double[]>(StringComparer.Ordinal);
        private static readonly List<string> _order = new List<string>();
        public static long Rows, Errors;

        public static void Tick(ulong nowMs, bool inWorld)
        {
            if (!inWorld || nowMs < _nextMs) return;
            _nextMs = nowMs + 5000;
            try
            {
                MySession s = MySession.Static;
                var cockpit = s != null ? s.ControlledEntity as MyCockpit : null;
                MyCubeGrid g = cockpit != null ? cockpit.CubeGrid : null;
                SiteMap map = SiteBinding.SiteMapV1;
                MyPlanet planet = SiteBinding.Planet;
                if (g == null || g.Closed || g.MarkedForClose || map == null || planet == null) return;
                Vector3D p = g.PositionComp.GetPosition();
                Vec3 sn = map.SeToSn(new Vec3(p.X, p.Y, p.Z));
                Vector3 grav = Sandbox.Game.GameSystems.MyGravityProviderSystem.CalculateNaturalGravityInPoint(p);
                Vector3D up = Vector3D.Normalize(p - planet.PositionComp.GetPosition());
                Vector3 v = g.Physics != null ? g.Physics.LinearVelocity : Vector3.Zero;
                double vUp = Vector3D.Dot(v, up);
                float air = planet.GetAirDensity(p);
                float mass = g.Physics != null ? g.Physics.Mass : 0f;
                _byType.Clear();
                _order.Clear();
                foreach (MyThrust t in g.GetFatBlocks<MyThrust>())
                {
                    if (t == null || t.BlockDefinition == null) continue;
                    string key = t.BlockDefinition.Id.SubtypeName ?? "";
                    double[] a;
                    if (!_byType.TryGetValue(key, out a)) { a = new double[6]; _byType[key] = a; _order.Add(key); }
                    a[0] += 1;
                    if (t.IsWorking) a[1] += 1;
                    if (t.IsPowered) a[2] += 1;
                    var it = t as Sandbox.ModAPI.Ingame.IMyThrust;   // MaxEffectiveThrust / CurrentThrust live on the mod API interface (MyThrust implements it)
                    a[3] += t.BlockDefinition.ForceMagnitude;
                    a[4] += it != null ? it.MaxEffectiveThrust : 0f;
                    a[5] += it != null ? it.CurrentThrust : 0f;
                }
                var sb = new StringBuilder(512);
                sb.Append("{\"t\":"); JsonText.AppendString(sb, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.Append(",\"ev\":\"s16.thrust\"");
                S5Rows.Int(sb, "grid", g.EntityId);
                S5Rows.Str(sb, "name", g.DisplayName ?? "");
                S5Rows.Num(sb, "depthM", -sn.Y);
                S5Rows.Num(sb, "massKg", mass);
                S5Rows.Num(sb, "speedMps", v.Length());
                S5Rows.Num(sb, "vUpMps", vUp);
                S5Rows.Num(sb, "gravityG", grav.Length() / 9.81);
                S5Rows.Num(sb, "airDensity", air);
                sb.Append(",\"types\":[");
                var line = new StringBuilder(256);
                for (int i = 0; i < _order.Count; i++)
                {
                    double[] a = _byType[_order[i]];
                    if (i > 0) { sb.Append(','); line.Append("; "); }
                    sb.Append("{\"type\":"); JsonText.AppendString(sb, _order[i]);
                    sb.Append(",\"n\":").Append(((long)a[0]).ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"working\":").Append(((long)a[1]).ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"powered\":").Append(((long)a[2]).ToString(CultureInfo.InvariantCulture));
                    sb.Append(",\"forceN\":").Append(S3Rows.G(a[3]));
                    sb.Append(",\"maxEffectiveN\":").Append(S3Rows.G(a[4]));
                    sb.Append(",\"currentN\":").Append(S3Rows.G(a[5]));
                    sb.Append('}');
                    line.Append(_order[i]).Append(" n=").Append((long)a[0]).Append(" working=").Append((long)a[1])
                        .Append(" maxEffective=").Append(a[3] > 0 ? (100.0 * a[4] / a[3]).ToString("0", CultureInfo.InvariantCulture) : "?").Append("% of force")
                        .Append(" current=").Append((a[5] / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" kN");
                }
                sb.Append(']');
                Jsonl.WriteLineBuffered(S5Rows.End(sb));
                Rows++;
                if (nowMs >= _nextLogMs)
                {
                    _nextLogMs = nowMs + 30000;
                    DeLog.Info("DE.S16 thrust grid " + g.EntityId + " '" + (g.DisplayName ?? "") + "' at " + (-sn.Y).ToString("0", CultureInfo.InvariantCulture) + " m: " +
                               (line.Length > 0 ? line.ToString() : "no thrusters") + "; mass " + (mass / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " t, vertical " +
                               vUp.ToString("0.0", CultureInfo.InvariantCulture) + " m/s, gravity " + (grav.Length() / 9.81).ToString("0.00", CultureInfo.InvariantCulture) + " g, air " +
                               air.ToString("0.00", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception e)
            {
                Errors++;
                if (Errors <= 3) DeLog.Info("DE.S16 thrust probe failed: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
