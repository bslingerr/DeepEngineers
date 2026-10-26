using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.Utils;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// S15, the kept non-voice picks on the SE side (docs/S15-DESIGN.md item 3; CONTRACT §5.4 "S15 conventions", formats
 /// sonar.ping, c6.s15events, water.far). Game thread, from <see cref="Scan"/> every ScanTicks.
 /// voice lines on (creature attack, hull breach and restored, engine power down on thrust, abandon ship, the
 /// oxygen warnings): their code is gone; <see cref="OnCreatureHit"/> stays a no-op so the S6/S11 hooks still compile.
 /// <list type="bullet">
 /// <item>Sonar: a functional block of the aboard grid whose <c>CustomName</c> holds "sonar" and is
 /// <c>Enabled</c> pings every SonarPeriodMs while seated.</item>
 /// <item>The far sea: the planet's water texture swapped above SwapUpM over the sea and back below SwapDownM (WaterLook).</item>
 /// </list>
 /// </summary>
    internal static unsafe partial class S15Se
    {
        private static readonly S15EventsState Ev = new S15EventsState();
        private static bool _sonarOn;
        private static long _nextPingMs;
        private static double _camDepth = double.NaN;
        private static string _sonarBlock;
        private static bool _far;
        public static long FarSwaps, SonarPings;

        private static void PicksReset()
        {
            _sonarOn = false;
            _nextPingMs = 0;
            _camDepth = double.NaN;
            _sonarBlock = null;
            _far = false;
        }

        // ================================================================================================ hooks

 /// <summary>
 /// The S6 / S11 hooks (game thread): a creature hit or grabbed this grid. The Cyclops's "hostile organism" line was removed
 /// nothing happens (the hook lines in and may go).
 /// </summary>
        public static void OnCreatureHit(long gridId, string via) { }

        // ================================================================================================ the scan

        private static void PicksScan(ulong tick, long ms)
        {
            SiteMap map = SiteBinding.Map;
            MyCamera cam = MySector.MainCamera;
            _camDepth = double.NaN;
            if (map != null && cam != null)
            {
                Vector3D p = cam.WorldMatrix.Translation;
                _camDepth = -map.SeToSn(new Vec3(p.X, p.Y, p.Z)).Y;
            }
            bool seated = _aboard != 0 && _aboardHow == "seat";
            Sonar(ms, seated);
            FarSea(ms);
        }

        /// <summary>The planet's texture swapped to the far sea above SwapUpM over the sea, back below SwapDownM (WaterLook).</summary>
        private static void FarSea(long ms)
        {
            if (double.IsNaN(_camDepth) || !_waterDone || !S15Switch.WaterOn || S15SeMutants.WaterOff) return;
            double alt = -_camDepth;
            bool want = _far ? alt > S15Conventions.WaterFar.SwapDownM : alt >= S15Conventions.WaterFar.SwapUpM;
            if (want == _far) return;
            string tex = want ? S15Conventions.WaterFar.Material : S15Conventions.Water.Material;
            Dictionary<string, object> r = WaterLook.Command(new Dictionary<string, object>(StringComparer.Ordinal) { { "texture", tex } }, "s15 far sea");
            object ok;
            bool done = r != null && r.TryGetValue("ok", out ok) && ok is bool && (bool)ok;
            _far = want;   // once per crossing, whatever the reply (a refused swap is a row, not a retry storm)
            FarSwaps++;
            StringBuilder sb = Head("waterswap");
            S5Rows.Bool(sb, "far", want);
            S5Rows.Str(sb, "texture", tex);
            S5Rows.Num(sb, "altitude", alt);
            S5Rows.Bool(sb, "ok", done);
            S5Rows.Str(sb, "line", r != null && r.ContainsKey("line") ? Convert.ToString(r["line"], CultureInfo.InvariantCulture) : null);
            S5Rows.Int(sb, "ms", ms);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
            DeLog.Info("DE.S15 far sea " + (want ? "on" : "off") + " at " + alt.ToString("0", CultureInfo.InvariantCulture) + " m: texture " + tex + (done ? "" : " (refused)"));
        }

        /// <summary>
 /// de.s15 {clock: c}: SE's own clock moved so the site's cycle time is c (0 midnight, 0.5 noon). SE turns its sun from
 /// MySession.ElapsedGameTime
 /// so the shift is the angle about the axis from the sun now to the sun of that cycle time
 /// (S7's SunSyncCore.SunForHourAngle), its sign tested with SE's own rotation. S7's de.s7 time only turned the sun
 /// field, which SE's renderer does not follow (run 20261024-041144: SE's pictures at night, Subnautica's at noon).
 /// </summary>
        public static string SetClock(double c, out double shiftS)
        {
            shiftS = 0;
            MySession s = MySession.Static;
            SiteMap map = SiteBinding.Map;
            MyCamera cam = MySector.MainCamera;
            if (s == null || map == null || cam == null) return "no session, site or camera";
            if (!s.Settings.EnableSunRotation || !(s.Settings.SunRotationIntervalMinutes > 0)) return "SE's sun does not rotate in this world";
            if (SunSync.DevPhase.HasValue) return "de.s7 time off first (S7's dev phase overwrites SE's sun field)";
            Vector3 sv = MySector.SunProperties.SunDirectionNormalized;
            Vector3 av = MySector.SunProperties.SunRotationAxis;
            Vector3D p = cam.WorldMatrix.Translation;
            Vec3 target = SunSyncCore.SunForHourAngle(map, new Vec3(p.X, p.Y, p.Z), new Vec3(sv.X, sv.Y, sv.Z), new Vec3(av.X, av.Y, av.Z), SunSyncCore.HourAngleOfCycle(c));
            var tgt = new Vector3((float)target.X, (float)target.Y, (float)target.Z);
            Vector3 axis = Vector3.Normalize(av);
            Vector3 a0 = sv - axis * Vector3.Dot(sv, axis), a1 = tgt - axis * Vector3.Dot(tgt, axis);
            if (a0.LengthSquared() < 1e-8f || a1.LengthSquared() < 1e-8f) return "the sun lies on its rotation axis";
            a0.Normalize();
            a1.Normalize();
            double phi = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3.Dot(a0, a1))));
            double period = 60.0 * s.Settings.SunRotationIntervalMinutes;
            double best = 0, bestDot = -2;
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                double dt = sign * phi * period / (2.0 * Math.PI);
                Vector3 pred = Vector3.Transform(sv, Matrix.CreateFromAxisAngle(av, (float)(2.0 * Math.PI * dt / period)));   // SE's own rotation
                double dot = Vector3.Dot(Vector3.Normalize(pred), tgt);
                if (dot > bestDot) { bestDot = dot; best = dt; }
            }
            if (best < 0) best += period;   // the clock only runs forward
            if (best < 120.0 || best > period - 120.0) best = 0;   // already there (within two minutes): no full turn
            shiftS = best;
            s.ElapsedGameTime = s.ElapsedGameTime + TimeSpan.FromSeconds(best);
            DeLog.Info("DE.S15 clock " + c.ToString("0.###", CultureInfo.InvariantCulture) + ": SE's ElapsedGameTime + " + best.ToString("0.0", CultureInfo.InvariantCulture) +
                       " s (predicted sun on target to " + (Math.Acos(Math.Min(1.0, bestDot)) * 180.0 / Math.PI).ToString("0.00", CultureInfo.InvariantCulture) + " deg)");
            return null;
        }

        // ================================================================================================ sonar

        private static void Sonar(long ms, bool seated)
        {
            bool on = false;
            string name = null;
            if (seated && _aboardGrid != null && !_aboardGrid.Closed)
            {
                int seen = 0;
                foreach (MyFunctionalBlock b in _aboardGrid.GetFatBlocks<MyFunctionalBlock>())
                {
                    if (++seen > 4096) break;
                    if (b == null || b.Closed || !b.Enabled) continue;
                    string n = b.CustomName != null ? b.CustomName.ToString() : null;
                    if (!S15PickRules.IsSonarName(n)) continue;
                    on = true;
                    name = n;
                    break;
                }
            }
            if (on != _sonarOn)
            {
                _sonarOn = on;
                _sonarBlock = name;
                _nextPingMs = ms;   // the first ping at once (CyclopsSonarButton: InvokeRepeating at 0)
                DeLog.Info("DE.S15 sonar " + (on ? "on (" + name + ")" : "off"));
            }
            if (!on || ms < _nextPingMs) return;
            _nextPingMs = ms + S15Conventions.Picks.SonarPeriodMs;
            uint seq = ++S15SeWire.SonarSeq;
            SonarPings++;
            StringBuilder sb = Head("sonar");
            S5Rows.Int(sb, "seq", seq);
            S5Rows.Id(sb, "grid", _aboard);
            S5Rows.Str(sb, "block", _sonarBlock);
            S5Rows.Int(sb, "ms", ms);
            Jsonl.WriteLineBuffered(S5Rows.End(sb));
        }

        // ================================================================================================ the C6 block

        private static void WritePickEvents(byte* c6Region)
        {
            Ev.Flags = (byte)(_sonarOn ? S15Conventions.EventFlags.SonarOn : 0);
            Ev.WorldEpoch = Phases.WorldEpoch;
            Ev.SonarSeq = S15SeWire.SonarSeq;
            S15EventsWire.Write(c6Region, Ev);
        }

        public static string PicksStatus()
        {
            return "sonar " + (_sonarOn ? "on" : "off") + " pings " + SonarPings + ", far sea " + (_far ? "on" : "off") + " (" + FarSwaps + " swaps)";
        }
    }
}
