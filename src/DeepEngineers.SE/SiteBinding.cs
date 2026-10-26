using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DeepEngineers.Wire;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Planet;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace DeepEngineers.SE
{
    /// <summary>
 /// Site binding (CONTRACT §3 "Runtime values and validation", ARCHITECTURE §5.5; ROADMAP S1 "SE plugin", S1.N13,
 /// S1.G2-E3/E4, S1.M5, S1.M9). Driven by <see cref="DeepEngineersSession"/>:
 /// <list type="bullet">
 /// <item><c>LoadData</c>: a handler left by an earlier session is unregistered, then a fresh
 /// <see cref="WaterModClient"/> registers handler 50271 (S1.M9: at the first UpdateBeforeSimulation instead);</item>
 /// <item><c>BeforeStart</c> (after <c>MyEntities.Load</c>): the <c>DE_4546B</c> planets are counted and
 /// C = <c>planet.PositionComp.GetPosition</c> is read;</item>
 /// <item>each <c>UpdateBeforeSimulation</c> until decided (≤ 600 ticks): facts gathered from the API and the
 /// version-gated reflection, judged by <see cref="SiteValidation.Decide"/>, logged <c>DE.Site WAIT|OK|REFUSE</c>;
 /// OK binds the map (<see cref="Map"/>, used by the C1 writer);</item>
 /// <item>every 10 s (plugin Update, wall clock): Rs and crush damage re-checked;</item>
 /// 0526316f)</c> through the API
 /// then a new validation; the other values have no API setter and are reported with the Water Mod command that
 /// fixes them;</item>
 /// <item><see cref="Rebind"/> (<c>de.site {rebind}</c>, <c>/de site rebind</c>, and <c>de.spawnplanet</c> after its
 /// spawn): the planet is looked up again and the validation restarts from WAIT (600 ticks). A session refused for
 /// "no DE_4546B planet" also polls every 10 s and rebinds by itself when one appears (a spawn by any route).</item>
 /// </list>
 /// The full site record (constants, C, Rs, the 34 tile ids, sun facts) is logged with <c>siteVersion</c> when the
 /// site is decided. Update thread only. Never throws.
 /// </summary>
    internal static class SiteBinding
    {
        /// <summary>CONTRACT §3 sun checks (logged only in S1; they gate time sync, S7).</summary>
        public const double SunTolerance = 1e-3;
        /// <summary>The camera depth cross-check is skipped beyond this |y| (GetDepth is a float: 0.01 m needs |y| ≲ 60 km; kept well inside).</summary>
        public const double CameraDepthCheckMaxY = 10000.0;
        /// <summary>Ticks to wait after a repair before validating again (the Water Mod recomputes its radius in its next simulation step).</summary>
        public const int RepairHoldTicks = 30;
        /// <summary>The closest-water test (onSite) is refreshed every this many C1 frames.</summary>
        public const int ClosestWaterPollFrames = 30;

        /// <summary>SN test points of the depth cross-check: the anchor and the CONTRACT §3 conformance points.</summary>
        public static readonly Vec3[] DepthTestPointsSn =
        {
            new Vec3(0, 0, 0), new Vec3(-1029, -150, -1116), new Vec3(198, -1657, -518), new Vec3(346, 155, 906), new Vec3(2048, -500, 2048),
        };

        private static bool _active;
        private static uint _epoch;
        private static int _ticks;
        private static int _holdTicks;
        private static string _lastWaitField;
        private static int _lastWaitLoggedTick;
        private static bool _hasC;
        private static ulong _nextRecheckMs;
        private static int _closestPollCountdown;
        private static bool _closestIsSite;
        private static long _firstSimUpdateTimestamp;
        private static ulong _nextPlanetPollMs;

        public static WaterModClient Api { get; private set; }
        public static SiteStatus Status { get; private set; }
        public static SiteDecision Last { get; private set; }
        /// <summary>The bound map (non-null only while <see cref="Status"/> is OK).</summary>
        public static SiteMap Map { get; private set; }
        /// <summary>S8a (B3 hook): the site map v1 as decided (chart 0); <see cref="Map"/> is the current chart, which the treadmill may replace.</summary>
        public static SiteMap SiteMapV1 { get; private set; }
        /// <summary>S8a (B3 hook): 0 = the site chart; + 1 per treadmill step (CONTRACT "chart.apply").</summary>
        public static uint ChartSeq { get; private set; }
        public static MyPlanet Planet { get; private set; }

        /// <summary>
 /// S8a (B3 hook, CONTRACT "chart.apply"): the chart manager swaps the map at the tick it queues the ChartStep notice,
 /// so everything mapped through <see cref="Map"/> this tick (C1, C2, the S8a header) is under the new chart; a
 /// consumer that caches the map object (S7Statics, SunSync) re-binds when the reference changes.
 /// </summary>
        public static void SetChart(SiteMap map, uint chartSeq, string why)
        {
            if (map == null) return;
            Map = map;
            ChartSeq = chartSeq;
            DeLog.Info("DE.Site chart " + chartSeq + " (" + why + "): " + (chartSeq == 0 ? "site map v1" : "treadmill chart") + " U0=(" + map.U0.X.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + ", " +
                       map.U0.Y.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + ", " + map.U0.Z.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
        public static int PlanetCount { get; private set; }
        public static Vec3 C { get; private set; }
        /// <summary>The validated sea radius (double of the float32 read), 0 until bound.</summary>
        public static double Rs { get; private set; }
        public static long Rechecks { get; private set; }
        public static long RecheckFailures { get; private set; }
        public static int TicksToDecision { get; private set; }

        public static bool IsOk { get { return Status == SiteStatus.Ok && Map != null; } }
        private static bool _waterLookApplied;
        /// </summary>
        private static bool _wavesPinned;
        public static bool IsRefused { get { return Status == SiteStatus.Refuse; } }
        /// <summary>Stopwatch timestamp of the first UpdateBeforeSimulation of this session (0 before).</summary>
        public static long FirstSimUpdateTimestamp { get { return _firstSimUpdateTimestamp; } }

        // ======================================================================================= session callbacks

        public static void OnLoadData()
        {
            //  a session whose UnloadData never ran (an exception in the load) must not leave its
 // handler registered next to the new one
            if (Api != null)
            {
                try { Api.Unregister(); } catch (Exception) { }
                DeLog.Info("DE.Site LoadData: handler " + WaterModClient.HandlerId + " of an earlier session unregistered (its UnloadData did not run)");
            }
            ResetSession();
            _epoch = Phases.WorldEpoch;
            if (IsMultiplayer())
            {
                DeLog.Info("DE.Site inert: multiplayer session (CONTRACT §1.2: the plugin is inert; no Water Mod handler registered)");
                return;
            }
            _active = true;
            Api = new WaterModClient();
            if (S1Mutants.HandlerLate)
            {
                DeLog.Info("DE.Site LoadData worldEpoch=" + _epoch + ": MUTANT S1.M9, handler " + WaterModClient.HandlerId + " NOT registered here (at the first UpdateBeforeSimulation instead)");
                return;
            }
            Api.Register("LoadData");
            DeLog.Info("DE.Site LoadData worldEpoch=" + _epoch + ": Water Mod handler " + WaterModClient.HandlerId + " registered (before any BeforeStart)");
        }

        public static void OnBeforeStart()
        {
            if (!_active) return;
            FindPlanet("BeforeStart (after MyEntities.Load)");
        }

        public static void OnUpdateBeforeSimulation()
        {
            // Defaults) once per world, as soon as the site is bound
            if (IsOk && !_waterLookApplied) { _waterLookApplied = true; try { WaterLook.ApplyDefaults("site bound"); } catch (Exception e) { DeLog.Info("DE.Water defaults: " + e.GetType().Name + ": " + e.Message); } }
            if (!_active || Status != SiteStatus.Wait) return;
            if (_firstSimUpdateTimestamp == 0)
            {
                if (S1Mutants.HandlerLate && Api != null && !Api.Registered)
                {
                    // S1.M9: after every BeforeStart, so the Water Mod's dictionary (sent from its BeforeStart) is missed
                    Api.Register("first UpdateBeforeSimulation (MUTANT S1.M9)");
                    DeLog.Info("DE.Site first UpdateBeforeSimulation: MUTANT S1.M9 registered handler " + WaterModClient.HandlerId +
                               " now, after every BeforeStart (the Water Mod's dictionary was sent from its BeforeStart: missed)");
                }
                _firstSimUpdateTimestamp = Stopwatch.GetTimestamp();
                long start = Phases.LoadStartTimestamp;
                DeLog.Info("DE.Site first UpdateBeforeSimulation worldEpoch=" + _epoch +
                           (start != 0 ? " +" + Ms(_firstSimUpdateTimestamp - start) + " ms after the RunLoad prefix" : "") +
                           "; API " + (Api != null && Api.Received ? "already received" : "not received yet"));
            }
            if (_holdTicks > 0) { _holdTicks--; return; }
            _ticks++;
            Evaluate();
        }

        public static void OnUnloadData()
        {
            _waterLookApplied = false;
            _wavesPinned = false;
            if (Api != null) Api.Unregister();
            Jsonl.Flush();
            if (_active) DeLog.Info("DE.Site UnloadData worldEpoch=" + _epoch + " status=" + SiteDecision.StatusName(Status) + " (handler " + WaterModClient.HandlerId + " unregistered)");
            ResetSession();
        }

        private static void ResetSession()
        {
            _active = false;
            Status = SiteStatus.Wait;
            Last = default(SiteDecision);
            Map = null;
            SiteMapV1 = null;   //: a world change must not leave the previous world's chart-0 map (the Aurora beacon of a new Creative world landed at the lab world's planet)
            ChartSeq = 0;
            Planet = null;
            PlanetCount = 0;
            _hasC = false;
            Rs = 0;
            _ticks = 0;
            _holdTicks = 0;
            _lastWaitField = null;
            _lastWaitLoggedTick = 0;
            _nextRecheckMs = 0;
            _closestPollCountdown = 0;
            _closestIsSite = false;
            _firstSimUpdateTimestamp = 0;
            _nextPlanetPollMs = 0;
            TicksToDecision = 0;
            Api = null;
        }

        private static bool IsMultiplayer()
        {
            try
            {
                MySession s = MySession.Static;
                return Sandbox.Engine.Platform.Game.IsDedicated || Sync.MultiplayerActive ||
                       (s != null && s.OnlineMode != MyOnlineModeEnum.OFFLINE);
            }
            catch (Exception) { return false; }
        }

        // =============================================================================================== evaluation

 /// <summary>Counts the DE_4546B planets and keeps the first; <paramref name="logLabel"/> non-null logs the planet list under it.</summary>
        private static void FindPlanet(string logLabel)
        {
            bool log = logLabel != null;
            int count = 0;
            MyPlanet found = null;
            var names = new StringBuilder();
            List<MyPlanet> planets = MyPlanets.GetPlanets();
            if (planets != null)
            {
                foreach (MyPlanet p in planets)
                {
                    if (p == null || p.Closed) continue;
                    string subtype = p.Generator != null ? p.Generator.Id.SubtypeName : "?";
                    if (log) names.Append(names.Length > 0 ? ", " : "").Append(subtype).Append('#').Append(p.EntityId);
                    if (subtype != SiteV1.GeneratorSubtypeId) continue;
                    count++;
                    if (found == null) found = p;
                }
            }
            PlanetCount = count;
            Planet = count == 1 ? found : null;
            if (Planet != null && !_hasC)
            {
                Vector3D c = Planet.PositionComp.GetPosition();
                C = new Vec3(c.X, c.Y, c.Z);
                _hasC = true;
            }
            if (log)
                DeLog.Info("DE.Site " + logLabel + ": planets [" + names + "]; " + SiteV1.GeneratorSubtypeId + " count=" + count +
                           (_hasC ? " C=" + V(C) + " (planet.PositionComp.GetPosition(), read once per session)" : ""));
        }

        private static void Evaluate()
        {
            if (Planet == null || Planet.Closed) { _hasC = false; FindPlanet(null); }
            SiteFacts f = Gather();
            SiteDecision d = SiteValidation.Decide(f);
            if (d.Status == SiteStatus.Wait)
            {
                Last = d;
                if (d.Field != _lastWaitField || _ticks - _lastWaitLoggedTick >= 120)
                {
                    _lastWaitField = d.Field;
                    _lastWaitLoggedTick = _ticks;
                    DeLog.Info(d.Line);
                }
                return;
            }
            Decided(d, f);
        }

        private static SiteFacts Gather()
        {
            var f = new SiteFacts { Ticks = _ticks, PlanetCount = PlanetCount };
            WaterModClient api = Api;
            if (api == null) return f;
            f.ApiReceived = api.Received;
            f.ApiError = api.Error;
            if (!api.Usable || Planet == null) return f;

            f.HasWater = api.HasWater(Planet);
            if (f.HasWater != true) return f;
            f.RsBits = api.RsBits(Planet);

            WaterSettingsSnapshot ws;
            string why;
            bool haveSettings = api.TryReadSettings(Planet, out ws, out why);
            if (haveSettings)
            {
                f.CrushDamage = ws.CrushDamage;
                f.CurrentSpeed = ws.CurrentSpeed;
            }
            else f.SettingsUnavailable = why;
            float? wave = api.WaveHeight(Planet), tide = api.TideHeight(Planet);
            f.WaveHeight = wave ?? (haveSettings ? (float?)ws.WaveHeight : null);
            f.TideHeight = tide ?? (haveSettings ? (float?)ws.TideHeight : null);

            // 5 m waves into
 // the world, and at the next load the bind-time depth check (GetDepth includes the wave: 0.268 m) refused the
 // site, so the view machine never left Space. Our own look's waves go back to 0 once before the check (the mod
 // recomputes in its next step: hold like a repair); ApplyDefaults restores the look as soon as the site is bound.
            if (!_wavesPinned && f.WaveHeight.HasValue && f.WaveHeight.Value != 0f && WaterLook.Defaults.ContainsKey("waveHeight"))
            {
                _wavesPinned = true;
                try
                {
                    var pin = new Dictionary<string, object> { { "waveHeight", "0" } };
                    WaterLook.Command(pin, "site bind: waves set to 0 for the depth check");
                    DeLog.Info("DE.Site waves " + F(f.WaveHeight.Value) + " m from the saved look: set to 0 for the depth check (the look comes back once the site is bound); holding " + RepairHoldTicks + " ticks");
                    _holdTicks = RepairHoldTicks;
                    f.DepthDetail = "waves set to 0, holding " + RepairHoldTicks + " ticks";
                    return f;
                }
                catch (Exception e) { DeLog.Info("DE.Site waves pin failed: " + e.GetType().Name + ": " + e.Message); }
            }
            if (f.RsBits.HasValue && _hasC)
            {
                string detail;
                f.DepthMaxError = DepthCheck(api, SiteValidation.FloatFromBits(f.RsBits.Value), out detail);
                f.DepthDetail = detail;
            }
            else f.DepthDetail = _hasC ? "Rs unreadable" : "planet centre not read";
            return f;
        }

        /// <summary>Max |GetDepth(P) − y(P)| over the test points (and the camera when |y| ≤ 10 km); null when a depth is unreadable.</summary>
        private static double? DepthCheck(WaterModClient api, float rsRead, out string detail)
        {
            var map = SiteMap.ForSiteV1(C, rsRead);
            double worst = -1;
            string worstAt = "";
            int n = 0;
            for (int i = 0; i <= DepthTestPointsSn.Length; i++)
            {
                Vec3 p;
                string label;
                if (i < DepthTestPointsSn.Length)
                {
                    p = map.SnToSe(DepthTestPointsSn[i]);
                    label = "SN " + V(DepthTestPointsSn[i]);
                }
                else
                {
                    Vec3 cam;
                    if (!CameraPosition(out cam)) break;
                    double yc = map.SeToSn(cam).Y;
                    if (Math.Abs(yc) > CameraDepthCheckMaxY) break;
                    p = cam;
                    label = "camera";
                }
                double y = map.SeToSn(p).Y;
                float? depth = api.Depth(new Vector3D(p.X, p.Y, p.Z), Planet);
                if (!depth.HasValue || float.IsNaN(depth.Value) || float.IsInfinity(depth.Value))
                {
                    detail = "GetDepth at " + label + " returned " + (depth.HasValue ? depth.Value.ToString("R", CultureInfo.InvariantCulture) : "null");
                    return null;
                }
                double err = Math.Abs(depth.Value - y);
                n++;
                if (err > worst)
                {
                    worst = err;
                    worstAt = label + " GetDepth=" + depth.Value.ToString("R", CultureInfo.InvariantCulture) + " y=" + y.ToString("0.######", CultureInfo.InvariantCulture);
                }
            }
            detail = n + " points, worst at " + worstAt;
            return worst;
        }

        private static bool CameraPosition(out Vec3 p)
        {
            p = Vec3.Zero;
            try
            {
                var cam = MySector.MainCamera;
                if (cam == null) return false;
                Vector3D c = cam.Position;
                p = new Vec3(c.X, c.Y, c.Z);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void Decided(SiteDecision d, SiteFacts f)
        {
            Status = d.Status;
            Last = d;
            TicksToDecision = _ticks;
            if (d.Status == SiteStatus.Ok)
            {
                Rs = SiteValidation.FloatFromBits(f.RsBits.Value);
                Map = SiteMap.ForSiteV1(C, Rs);
                SiteMapV1 = Map;      // S8a (B3 hook): chart 0 again at every decision
                ChartSeq = 0;
                _nextRecheckMs = 0;
                _closestPollCountdown = 0;
                try { S8aChartManager.OnSiteRebound(); } catch (Exception) { }
            }
            else
            {
                Map = null;
                SiteMapV1 = null;
                ChartSeq = 0;
            }
            DeLog.Info(d.Line + " (siteVersion=" + SiteV1.SiteVersion + " worldEpoch=" + _epoch + (d.Status == SiteStatus.Refuse ? "; display bridge refused, SE plays with the Water Mod view" : "") + ")");
            LogRecord(f);
        }

        // ========================================================================================= plugin update

 /// <summary>
 /// Plugin Update (update thread, wall clock): the 10 s re-check of Rs and crush damage while bound, and the 10 s
 /// planet poll of a "no DE_4546B planet" refusal (<see cref="PollForPlanet"/>).
 /// </summary>
        public static void Tick(ulong nowMs, bool inWorld)
        {
            if (!_active || !inWorld || Api == null) return;
            if (Status == SiteStatus.Refuse) { PollForPlanet(nowMs); return; }
            if (Status != SiteStatus.Ok) return;
            if (_nextRecheckMs == 0) { _nextRecheckMs = nowMs + SiteValidation.RecheckIntervalMs; return; }
            if (nowMs < _nextRecheckMs) return;
            _nextRecheckMs = nowMs + SiteValidation.RecheckIntervalMs;
            Rechecks++;
            WaterSettingsSnapshot ws;
            string why;
            float? crush = Api.TryReadSettings(Planet, out ws, out why) ? (float?)ws.CrushDamage : null;
            SiteDecision d = SiteValidation.Recheck(Api.RsBits(Planet), crush, why);
            if (d.Status == SiteStatus.Ok) return;
            RecheckFailures++;
            Status = SiteStatus.Refuse;
            Last = d;
            Map = null;
            DeLog.Info(d.Line + " (siteVersion=" + SiteV1.SiteVersion + " worldEpoch=" + _epoch + "; display bridge refused from now on)");
        }

        /// <summary>
 /// A session refused for "no DE_4546B planet" (field planet, count 0) looks for one every 10 s and rebinds when it
 /// appears, so a planet spawned after the 600-tick refusal (de.spawnplanet, the spawn menu, a mod) is validated
 /// without reloading the world. Other refusals stay final for the session (repair or rebind by hand).
 /// </summary>
        private static void PollForPlanet(ulong nowMs)
        {
            if (Last.Field != "planet" || PlanetCount != 0) return;
            if (nowMs < _nextPlanetPollMs) return;
            _nextPlanetPollMs = nowMs + SiteValidation.RecheckIntervalMs;
            int n = 0;
            List<MyPlanet> planets = MyPlanets.GetPlanets();
            if (planets != null)
                foreach (MyPlanet p in planets)
                    if (p != null && !p.Closed && p.Generator != null && p.Generator.Id.SubtypeName == SiteV1.GeneratorSubtypeId) n++;
            if (n > 0) Rebind(n + " " + SiteV1.GeneratorSubtypeId + " planet(s) appeared after the REFUSE (10 s poll)");
        }

        /// <summary>
 /// ARCHITECTURE §4.1 <c>onSite</c>: the bound site and the camera's closest Water Mod planet is <c>DE_4546B</c>
 /// (polled every <see cref="ClosestWaterPollFrames"/> frames; cheap in between).
 /// </summary>
        public static bool OnSite(Vec3 cameraSe)
        {
            if (!IsOk) return false;
            if (_closestPollCountdown-- <= 0)
            {
                _closestPollCountdown = ClosestWaterPollFrames;
                MyPlanet closest = Api.ClosestWater(new Vector3D(cameraSe.X, cameraSe.Y, cameraSe.Z));
                _closestIsSite = closest != null && ReferenceEquals(closest, Planet);
            }
            return _closestIsSite;
        }

        // ================================================================================================ commands

 /// </summary>
        public static Dictionary<string, object> Repair()
        {
            var r = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!_active || Api == null) return Fail(r, "no DE world session (or multiplayer: inert)");
            bool server;
            try { server = Sync.IsServer; } catch (Exception) { server = false; }
            if (!server) return Fail(r, "server only (the Water Mod's SetWaterRadius refuses on a client)");
            if (!Api.Usable) return Fail(r, "Water Mod API not usable: " + Api.Describe());
            FindPlanet(null);
            if (Planet == null) return Fail(r, PlanetCount + " " + SiteV1.GeneratorSubtypeId + " planets (exactly one is required; recreate the planet)");
            if (Api.HasWater(Planet) != true) return Fail(r, SiteV1.GeneratorSubtypeId + " has no water: add it with the Water Mod (/wcreate near the planet) or recreate the planet");

            var actions = new List<string>();
            uint? bits = Api.RsBits(Planet);
            if (bits != SiteV1.RsExpectedBits)
            {
                float multiplier = SiteValidation.FloatFromBits(SiteV1.WaterSettingsRadiusBits);
                string why;
                bool ok = Api.SetWaterRadius(Planet, multiplier, out why);
                string a = "SetWaterRadius(" + SiteV1.GeneratorSubtypeId + ", " + ((double)multiplier).ToString("R", CultureInfo.InvariantCulture) +
                           " (bits 0x" + SiteV1.WaterSettingsRadiusBits.ToString("x8", CultureInfo.InvariantCulture) + ")) -> " + ok + (why != null ? " " + why : "") +
                           " (Rs was " + (bits.HasValue ? SiteValidation.RsText(bits.Value) : "unreadable") + ")";
                actions.Add(a);
                DeLog.Info("DE.Site REPAIR " + a);
            }
            else actions.Add("Rs already " + SiteValidation.RsText(bits.Value));

            WaterSettingsSnapshot ws;
            string swhy;
            if (Api.TryReadSettings(Planet, out ws, out swhy))
            {

                if (ws.CrushDamage != 0f) actions.Add("crushDamage=" + F(ws.CrushDamage) + ": type /wcrush 0 near " + SiteV1.GeneratorSubtypeId);
                if (ws.WaveHeight != 0f) actions.Add("waveHeight=" + F(ws.WaveHeight) + ": type /wwaveheight 0 near " + SiteV1.GeneratorSubtypeId);
                if (ws.TideHeight != 0f) actions.Add("tideHeight=" + F(ws.TideHeight) + ": type /wtideheight 0 near " + SiteV1.GeneratorSubtypeId);
                if (ws.CurrentSpeed != 0f) actions.Add("currentSpeed=" + F(ws.CurrentSpeed) + ": type /wcurrentspeed 0 near " + SiteV1.GeneratorSubtypeId);
            }
            else actions.Add("settings unreadable (" + swhy + "): check crush/wave/tide/current by hand");

            // validate again from scratch (after the Water Mod's next simulation step recomputed its radius)
            Status = SiteStatus.Wait;
            Map = null;
            _ticks = 0;
            _holdTicks = RepairHoldTicks;
            _lastWaitField = null;
            string line = "DE.Site REPAIR done: " + string.Join("; ", actions.ToArray()) + "; validating again (≤ " + SiteValidation.MaxTicks + " ticks)";
            DeLog.Info(line);
            r["ok"] = true;
            r["line"] = line;
            r["actions"] = actions.ToArray();
            return r;
        }

        /// <summary>
 /// Validate again from scratch (update thread): the planet is looked up again (C re-read), the decision goes back
 /// to WAIT and <see cref="OnUpdateBeforeSimulation"/> judges it within <see cref="SiteValidation.MaxTicks"/> ticks.
 /// Called by <c>de.spawnplanet</c> after its spawn, by <c>de.site {"rebind": true}</c> / <c>/de site rebind</c>, and
 /// by the 10 s poll of a "no planet" refusal.
 /// </summary>
        public static Dictionary<string, object> Rebind(string why)
        {
            var r = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!_active || Api == null)
            {
                r["ok"] = false;
                r["error"] = "no DE world session (or multiplayer: inert)";
                r["line"] = "DE.Site REBIND refused: " + r["error"];
                DeLog.Info((string)r["line"]);
                return r;
            }
            string before = SiteDecision.StatusName(Status) + (Last.Reason != null ? " " + Last.Reason : "");
            Status = SiteStatus.Wait;
            Last = default(SiteDecision);
            Map = null;
            Planet = null;
            PlanetCount = 0;
            _hasC = false;
            Rs = 0;
            _ticks = 0;
            _wavesPinned = false;
            _holdTicks = 0;
            _lastWaitField = null;
            _lastWaitLoggedTick = 0;
            _nextRecheckMs = 0;
            _nextPlanetPollMs = 0;
            _closestPollCountdown = 0;
            _closestIsSite = false;
            TicksToDecision = 0;
            FindPlanet("REBIND lookup");
            string line = "DE.Site REBIND (" + why + "): was " + before + "; " + SiteV1.GeneratorSubtypeId + " count=" + PlanetCount +
                          (_hasC ? " C=" + V(C) : "") + "; validating again (<= " + SiteValidation.MaxTicks + " ticks)";
            DeLog.Info(line);
            r["ok"] = true;
            r["line"] = line;
            r["planetCount"] = PlanetCount;
            r["C"] = _hasC ? new object[] { C.X, C.Y, C.Z } : null;
            return r;
        }

        /// <summary>de.site: the record and the current status.</summary>
        public static Dictionary<string, object> Info()
        {
            var r = new Dictionary<string, object>(StringComparer.Ordinal);
            r["ok"] = true;
            r["status"] = SiteDecision.StatusName(Status);
            r["reason"] = Last.Reason;
            r["field"] = Last.Field;
            r["siteVersion"] = SiteV1.SiteVersion;
            r["siteHash"] = LayoutHash.Format(SiteV1.SiteHash);
            r["planetCount"] = PlanetCount;
            r["planetId"] = Planet != null ? (object)Planet.EntityId : null;
            r["C"] = _hasC ? new object[] { C.X, C.Y, C.Z } : null;
            r["Rs"] = Rs;
            r["ticksToDecision"] = TicksToDecision;
            r["rechecks"] = Rechecks;
            r["recheckFailures"] = RecheckFailures;
            r["api"] = Api != null ? Api.Describe() : "none";
            r["line"] = "DE.Site " + SiteDecision.StatusName(Status) + " " + (Last.Reason ?? "(no decision yet)") + " | C=" + (_hasC ? V(C) : "?") +
                        " Rs=" + Rs.ToString("R", CultureInfo.InvariantCulture) + " siteVersion=" + SiteV1.SiteVersion + " api: " + (Api != null ? Api.Describe() : "none");
            LogRecord(null);
            return r;
        }

        private static Dictionary<string, object> Fail(Dictionary<string, object> r, string error)
        {
            r["ok"] = false;
            r["error"] = error;
            r["line"] = "DE.Site REPAIR refused: " + error;
            DeLog.Info((string)r["line"]);
            return r;
        }

        // =================================================================================================== record

 /// <summary>The full site record (CONTRACT §3: constants, C, Rs, tile list with entity ids, sun axis) with siteVersion.</summary>
        private static void LogRecord(SiteFacts? facts)
        {
            try
            {
                var sb = new StringBuilder(2048);
                sb.Append("DE.Site RECORD siteVersion=").Append(SiteV1.SiteVersion)
                  .Append(" siteHash=").Append(LayoutHash.Format(SiteV1.SiteHash))
                  .Append(" status=").Append(SiteDecision.StatusName(Status))
                  .Append(" generator=").Append(SiteV1.GeneratorSubtypeId).Append(" count=").Append(PlanetCount)
                  .Append(" planetId=").Append(Planet != null ? Planet.EntityId.ToString(CultureInfo.InvariantCulture) : "none")
                  .Append(" R=").Append(SiteV1.StorageRadius.ToString("R", CultureInfo.InvariantCulture))
                  .Append(" hill=").Append(SiteV1.HillMin.ToString("R", CultureInfo.InvariantCulture)).Append('/').Append(SiteV1.HillMax.ToString("R", CultureInfo.InvariantCulture))
                  .Append(" waterRadius=").Append(SiteV1.WaterSettingsRadius.ToString("R", CultureInfo.InvariantCulture))
                  .Append(" (bits 0x").Append(SiteV1.WaterSettingsRadiusBits.ToString("x8", CultureInfo.InvariantCulture)).Append(')')
                  .Append(" RsExpected=").Append(SiteValidation.RsText(SiteV1.RsExpectedBits))
                  .Append(" C=").Append(_hasC ? V(C) : "?")
                  .Append(" Rs=").Append(Rs.ToString("R", CultureInfo.InvariantCulture))
                  .Append(" anchor U0=").Append(V(SiteV1.AnchorU0)).Append(" N0=").Append(V(SiteV1.AnchorN0)).Append(" E0=").Append(V(SiteV1.AnchorE0))
                  .Append(" psi=").Append(SiteV1.AnchorPsiDeg.ToString("R", CultureInfo.InvariantCulture)).Append(" O=").Append(V(SiteV1.AnchorO));
                if (Planet != null)
                {
                    sb.Append(" planet.MinimumRadius=").Append(((double)Planet.MinimumRadius).ToString("R", CultureInfo.InvariantCulture))
                      .Append(" MaximumRadius=").Append(((double)Planet.MaximumRadius).ToString("R", CultureInfo.InvariantCulture));
                }
                if (Api != null && Planet != null)
                {
                    WaterSettingsSnapshot ws;
                    string why;
                    if (Api.TryReadSettings(Planet, out ws, out why))
                        sb.Append(" water{radius=").Append(((double)ws.SettingsRadius).ToString("R", CultureInfo.InvariantCulture))
                          .Append(" (bits 0x").Append(SiteV1.FloatBits(ws.SettingsRadius).ToString("x8", CultureInfo.InvariantCulture)).Append(')')
                          .Append(" componentRadius=").Append(ws.ComponentRadius.ToString("R", CultureInfo.InvariantCulture))
                          .Append(" crush=").Append(F(ws.CrushDamage)).Append(" wave=").Append(F(ws.WaveHeight))
                          .Append(" tide=").Append(F(ws.TideHeight)).Append(" current=").Append(F(ws.CurrentSpeed))
                          .Append(" volumetric=").Append(ws.Volumetric).Append('}');
                    else sb.Append(" water{unreadable: ").Append(why).Append('}');
                    sb.Append(" waterMod=").Append(Api.WaterModVersion ?? "unknown");
                }
                if (facts.HasValue && facts.Value.DepthDetail != null) sb.Append(" depthCheck=").Append(facts.Value.DepthDetail);
                AppendSun(sb);
                AppendWorldSettings(sb);
                AppendTiles(sb);
                DeLog.Info(sb.ToString());
            }
            catch (Exception e)
            {
                DeLog.Info("DE.Site RECORD error " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void AppendSun(StringBuilder sb)
        {
            try
            {
                Vector3 axis = MySector.SunProperties.SunRotationAxis;
                Vector3 b = MySector.SunProperties.BaseSunDirectionNormalized;
                var s = MySession.Static != null ? MySession.Static.Settings : null;
                bool enabled = s != null && s.EnableSunRotation;
                float interval = s != null ? s.SunRotationIntervalMinutes : float.NaN;
                double axisU = Math.Abs(axis.X * SiteV1.AnchorU0.X + axis.Y * SiteV1.AnchorU0.Y + axis.Z * SiteV1.AnchorU0.Z);
                double baseAxis = Math.Abs(axis.X * b.X + axis.Y * b.Y + axis.Z * b.Z);
                bool ok = axisU < SunTolerance && baseAxis < SunTolerance && enabled && interval == SiteV1.SunRotationIntervalMinutes;
                sb.Append(" sun{axis=(").Append(F(axis.X)).Append(", ").Append(F(axis.Y)).Append(", ").Append(F(axis.Z))
                  .Append(") base=(").Append(F(b.X)).Append(", ").Append(F(b.Y)).Append(", ").Append(F(b.Z))
                  .Append(") |axis.U0|=").Append(axisU.ToString("0.######", CultureInfo.InvariantCulture))
                  .Append(" |base.axis|=").Append(baseAxis.ToString("0.######", CultureInfo.InvariantCulture))
                  .Append(" rotation=").Append(enabled).Append(" intervalMin=").Append(F(interval))
                  .Append(ok ? " OK" : " MISMATCH (time sync would be refused, sky only: S7)").Append('}');
            }
            catch (Exception e) { sb.Append(" sun{error ").Append(e.GetType().Name).Append('}'); }
        }

        private static void AppendWorldSettings(StringBuilder sb)
        {
            try
            {
                var s = MySession.Static != null ? MySession.Static.Settings : null;
                if (s == null) return;
                sb.Append(" world{gameMode=").Append(s.GameMode).Append(" destructibleBlocks=").Append(s.DestructibleBlocks)
                  .Append(" oxygen=").Append(s.EnableOxygen).Append(" pressurization=").Append(s.EnableOxygenPressurization).Append('}');
            }
            catch (Exception e) { sb.Append(" world{error ").Append(e.GetType().Name).Append('}'); }
        }

        private static void AppendTiles(StringBuilder sb)
        {
            int[][] tiles = SiteV1.TilesCreated();
            sb.Append(" tiles=").Append(tiles.Length).Append(" [");
            for (int t = 0; t < tiles.Length; t++)
            {
                int i = tiles[t][0], j = tiles[t][1], k = tiles[t][2];
                if (t > 0) sb.Append(' ');
                sb.Append(i).Append(j).Append(k).Append(":0x").Append(SiteV1.TileEntityId(i, j, k).ToString("x16", CultureInfo.InvariantCulture));
                if (_hasC)
                {
                    Vec3 m = C + SiteV1.TileMinRelative(i, j, k);
                    sb.Append('@').Append(m.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                      .Append(m.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(m.Z.ToString("R", CultureInfo.InvariantCulture));
                }
            }
            sb.Append(']');
        }

        // ================================================================================================= helpers

        public static string V(Vec3 v)
        {
            return "(" + v.X.ToString("R", CultureInfo.InvariantCulture) + ", " + v.Y.ToString("R", CultureInfo.InvariantCulture) + ", " +
                   v.Z.ToString("R", CultureInfo.InvariantCulture) + ")";
        }

        private static string F(float v)
        {
            return ((double)v).ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Ms(long ticks)
        {
            return (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture);
        }
    }
}
