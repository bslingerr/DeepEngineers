using System;
using System.Globalization;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>Outcome of the site validation (CONTRACT §3 "Runtime values and validation").</summary>
    internal enum SiteStatus
    {
        /// <summary>Not decided yet (retried each simulation tick, up to <see cref="SiteValidation.MaxTicks"/>).</summary>
        Wait = 0,
        /// <summary>Every check passed: the site map is bound and C1 may be written.</summary>
        Ok = 1,
        /// <summary>A check failed: the display bridge is refused for this session (the tiles, from S4, stay).</summary>
        Refuse = 2,
    }

    /// <summary>
 /// What the SE plugin found about the dive site at one validation attempt. Filled by the game glue
 /// (<c>SiteBinding</c>, from the Water Mod API and version-gated reflection); judged by
 /// <see cref="SiteValidation.Decide"/>, which is game-independent (tests/DeepEngineers.SE.Tests).
 /// A null value means "could not be read".
 /// </summary>
    internal struct SiteFacts
    {
        /// <summary>Simulation ticks since the first <c>UpdateBeforeSimulation</c> of the session (1 at the first).</summary>
        public int Ticks;
        /// <summary>The Water Mod's delegate dictionary arrived on handler 50271.</summary>
        public bool ApiReceived;
        /// <summary>VerifyVersion false, a missing delegate key or a delegate of another type (null when fine).</summary>
        public string ApiError;
        /// <summary>Number of planets whose generator subtype is <c>DE_4546B</c>.</summary>
        public int PlanetCount;
        public bool? HasWater;
        /// <summary>IEEE-754 bits of <c>GetPhysicalData(planet).Item2</c> (the sea radius, float32).</summary>
        public uint? RsBits;
        /// <summary>Largest |GetDepth(P) − y(P)| over the test points, metres (y from the site map with the read Rs).</summary>
        public double? DepthMaxError;
        /// <summary>Where the worst depth difference was found, or why depths could not be compared.</summary>
        public string DepthDetail;
        public float? CrushDamage;
        public float? WaveHeight;
        public float? TideHeight;
        public float? CurrentSpeed;
        /// <summary>Why the per-planet Water Mod settings could not be read (version pin, reflection), or null.</summary>
        public string SettingsUnavailable;
    }

    /// <summary>One validation verdict: status, the field it is about and the reason text.</summary>
    internal struct SiteDecision
    {
        public SiteStatus Status;
        /// <summary>The check that decided (api, planet, water, Rs, depth, crushDamage, waveHeight, tideHeight, currentSpeed), "" for OK.</summary>
        public string Field;
        public string Reason;

        /// <summary>The log line: <c>DE.Site WAIT|OK|REFUSE &lt;reason&gt;</c>.</summary>
        public string Line
        {
            get { return "DE.Site " + StatusName(Status) + " " + Reason; }
        }

        public static string StatusName(SiteStatus s)
        {
            switch (s)
            {
                case SiteStatus.Ok: return "OK";
                case SiteStatus.Refuse: return "REFUSE";
                default: return "WAIT";
            }
        }
    }

    /// <summary>
 /// The site validation decision table (CONTRACT §3, ARCHITECTURE §5.5; ROADMAP S1 "SE plugin", S1.N13, S1.M5,
 /// S1.M9). Checks run in this order and the first failing one decides:
 /// <list type="number">
 /// <item>API received (else WAIT, and after 600 ticks <c>REFUSE API not received</c>);</item>
 /// <item>API usable: <c>VerifyVersion(22, "DeepEngineers")</c> true, every delegate key we use present with its type
 /// (else REFUSE at once);</item>
 /// <item>exactly one <c>DE_4546B</c> planet (two or more: REFUSE at once; none: WAIT, then REFUSE);</item>
 /// <item><c>HasWater</c> (WAIT, then REFUSE);</item>
 /// <item><c>Rs</c> equal to 60000.00390625f bit for bit (unreadable: WAIT, then REFUSE; different: REFUSE at once);</item>
 /// <item><c>GetDepth</c> equal to <c>y</c> within 0.01 m at every test point (as Rs);</item>
 /// <item><c>CrushDamage</c>, <c>WaveHeight</c>, <c>TideHeight</c>, <c>CurrentSpeed</c> all 0 (as Rs).</item>
 /// </list>
 /// A value that cannot change by waiting (a wrong Rs, a non-zero crush) refuses at once; a value that may still
 /// arrive (the dictionary, the planet's water) waits up to <see cref="MaxTicks"/>. The sun checks (time sync only,
 /// S7) are logged by the game glue and never decide here.
 /// </summary>
    internal static class SiteValidation
    {
        /// <summary>CONTRACT §3: validation retried for up to 600 ticks from the first UpdateBeforeSimulation.</summary>
        public const int MaxTicks = 600;
        /// <summary>CONTRACT §3: GetDepth at the test points equal to y within 0.01 m.</summary>
        public const double DepthToleranceM = 0.01;
        /// <summary>CONTRACT §3: Rs and crush damage re-checked every 10 s.</summary>
        public const ulong RecheckIntervalMs = 10000;

        public static SiteDecision Decide(SiteFacts f)
        {
            if (!f.ApiReceived)
                return WaitOrRefuse(f.Ticks, "api", "API not received (Water Mod handler 50271: no delegate dictionary)");
            if (f.ApiError != null)
                return Refuse("api", "API " + f.ApiError);

            if (f.PlanetCount > 1)
                return Refuse("planet", f.PlanetCount.ToString(CultureInfo.InvariantCulture) + " " + SiteV1.GeneratorSubtypeId +
                                        " planets (exactly one is required)");
            if (f.PlanetCount < 1)
                return WaitOrRefuse(f.Ticks, "planet", "no " + SiteV1.GeneratorSubtypeId + " planet in this world");

            if (f.HasWater != true)
                return WaitOrRefuse(f.Ticks, "water", f.HasWater.HasValue
                    ? SiteV1.GeneratorSubtypeId + " has no Water Mod water (HasWater false)"
                    : "HasWater unreadable");

            if (!f.RsBits.HasValue)
                return WaitOrRefuse(f.Ticks, "Rs", "Rs unreadable (GetPhysicalData)");
            if (f.RsBits.Value != SiteV1.RsExpectedBits)
                return Refuse("Rs", "Rs found=" + RsText(f.RsBits.Value) + " expected=" + RsText(SiteV1.RsExpectedBits) +
                                    " (Water Mod radius of " + SiteV1.GeneratorSubtypeId + " changed; /de site repair sets it back)");

            if (!f.DepthMaxError.HasValue)
                return WaitOrRefuse(f.Ticks, "depth", "GetDepth unavailable" + Detail(f.DepthDetail));
            if (!(f.DepthMaxError.Value <= DepthToleranceM)) // NaN refuses too
                return Refuse("depth", "GetDepth differs from y by " + Num(f.DepthMaxError.Value) + " m (tolerance " +
                                       Num(DepthToleranceM) + " m)" + Detail(f.DepthDetail));

            SiteDecision d;
            if (!Zero(f.CrushDamage, "crushDamage", f.Ticks, f.SettingsUnavailable, out d)) return d;
            if (!Zero(f.WaveHeight, "waveHeight", f.Ticks, f.SettingsUnavailable, out d)) return d;
            if (!Zero(f.TideHeight, "tideHeight", f.Ticks, f.SettingsUnavailable, out d)) return d;
            if (!Zero(f.CurrentSpeed, "currentSpeed", f.Ticks, f.SettingsUnavailable, out d)) return d;

            return new SiteDecision
            {
                Status = SiteStatus.Ok,
                Field = "",
                Reason = "site v" + SiteV1.SiteVersion + " bound: one " + SiteV1.GeneratorSubtypeId + ", Rs=" + RsText(f.RsBits.Value) +
                         ", GetDepth = y within " + Num(f.DepthMaxError.Value) + " m, crush/wave/tide/current 0 after " +
                         f.Ticks.ToString(CultureInfo.InvariantCulture) + " ticks",
            };
        }

        /// <summary>
 /// The 10 s re-check of a bound site (CONTRACT §3: "Rs and crush damage are re-checked every 10 s"). OK when
 /// both still hold, REFUSE naming the field otherwise (never WAIT: the site was bound).
 /// </summary>
        public static SiteDecision Recheck(uint? rsBits, float? crushDamage, string settingsUnavailable)
        {
            if (!rsBits.HasValue)
                return Refuse("Rs", "Rs unreadable at the 10 s re-check (GetPhysicalData)");
            if (rsBits.Value != SiteV1.RsExpectedBits)
                return Refuse("Rs", "Rs found=" + RsText(rsBits.Value) + " expected=" + RsText(SiteV1.RsExpectedBits) + " at the 10 s re-check");
            if (!crushDamage.HasValue)
                return Refuse("crushDamage", "crushDamage unreadable at the 10 s re-check" + Detail(settingsUnavailable));
            if (crushDamage.Value != 0f)
                return Refuse("crushDamage", "crushDamage=" + Num(crushDamage.Value) + " expected 0 at the 10 s re-check");
            return new SiteDecision { Status = SiteStatus.Ok, Field = "", Reason = "Rs and crushDamage unchanged at the 10 s re-check" };
        }

        /// <summary>The Rs bits the site check sees: the read bits, plus one ulp under the S1.M5 seeded failure.</summary>
        public static uint ApplyRsMutant(uint bits)
        {
            return S1Mutants.RsPlusOneUlp ? bits + 1u : bits;
        }

        public static float FloatFromBits(uint bits)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        /// <summary>"60000.00390625 (bits 0x476a6001)".</summary>
        public static string RsText(uint bits)
        {
            float v = FloatFromBits(bits);
            return ((double)v).ToString("R", CultureInfo.InvariantCulture) + " (bits 0x" + bits.ToString("x8", CultureInfo.InvariantCulture) + ")";
        }

        // ------------------------------------------------------------------------------------------------ helpers

        private static bool Zero(float? value, string field, int ticks, string unavailable, out SiteDecision d)
        {
            if (!value.HasValue)
            {
                d = WaitOrRefuse(ticks, field, field + " unverifiable" + Detail(unavailable));
                return false;
            }
            if (value.Value != 0f || float.IsNaN(value.Value))
            {
                d = Refuse(field, field + "=" + Num(value.Value) + " expected 0 on " + SiteV1.GeneratorSubtypeId +
                                  " (Water Mod setting saved in this world)");
                return false;
            }
            d = default(SiteDecision);
            return true;
        }

        private static SiteDecision WaitOrRefuse(int ticks, string field, string reason)
        {
            if (ticks >= MaxTicks)
                return Refuse(field, reason + " after " + ticks.ToString(CultureInfo.InvariantCulture) + " ticks");
            return new SiteDecision { Status = SiteStatus.Wait, Field = field, Reason = reason + " (tick " + ticks.ToString(CultureInfo.InvariantCulture) + "/" + MaxTicks + ")" };
        }

        private static SiteDecision Refuse(string field, string reason)
        {
            return new SiteDecision { Status = SiteStatus.Refuse, Field = field, Reason = reason };
        }

        private static string Detail(string d)
        {
            return string.IsNullOrEmpty(d) ? "" : " (" + d + ")";
        }

        private static string Num(double v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
 /// Camera zone with hysteresis (CONTRACT §5.4 C5 <c>zone</c>; ARCHITECTURE §4.2 zones): CORE inside the terrain
 /// footprint (max(|x|, |z|) ≤ 1,950 m), TILE VOID the rest of the 4,096 m tile (≤ 2,048 m), OUTER VOID up to
 /// ρ∞ = 7,800 m (left only beyond 7,900 m), FAR beyond, off-site when the camera's closest Water Mod planet is not
 /// the bound <c>DE_4546B</c> or the site is not bound. ρ∞ = max(|x|, |z|) of F(P).
 /// </summary>
    internal sealed class ZoneTracker
    {
        public const double CoreHalfM = 1950.0;
        public const double TileHalfM = 2048.0;
        public const double ZoneInM = 7800.0;
        public const double ZoneOutM = 7900.0;

        private bool _inZone;

        public uint Current { get; private set; }

        public ZoneTracker()
        {
            Current = Zone.OffSite;
        }

        public void Reset()
        {
            _inZone = false;
            Current = Zone.OffSite;
        }

        public uint Update(bool onSite, double snX, double snZ)
        {
            if (!onSite || double.IsNaN(snX) || double.IsNaN(snZ))
            {
                _inZone = false;
                Current = Zone.OffSite;
                return Current;
            }
            double rho = Math.Max(Math.Abs(snX), Math.Abs(snZ));
            _inZone = _inZone ? rho <= ZoneOutM : rho <= ZoneInM;
            if (!_inZone) Current = Zone.Far;
            else if (rho <= CoreHalfM) Current = Zone.Core;
            else if (rho <= TileHalfM) Current = Zone.TileVoid;
            else Current = Zone.OuterVoid;
            return Current;
        }

        public static string Name(uint zone)
        {
            switch (zone)
            {
                case Zone.Core: return "CORE";
                case Zone.TileVoid: return "TILE VOID";
                case Zone.OuterVoid: return "OUTER VOID";
                case Zone.Far: return "FAR";
                default: return "off-site";
            }
        }
    }

    /// <summary>
 /// C5 <c>perfMode</c> in S1 (no view state machine yet, S2): Subnautica must render and stream at the driven
 /// camera for the S1 checks, so it runs <c>Full</c> while the hello is published and the camera is in a dive zone
 /// (CORE, TILE VOID, OUTER VOID), and <c>Idle</c> otherwise (FAR, off-site, no hello). <c>targetFps</c>: 10 in Idle
 /// (ARCHITECTURE §4.2), 0 in Full = "no cap" (CONTRACT does not define the Full value; reported).
 /// </summary>
    internal static class S1Perf
    {
        public const uint IdleTargetFps = 10;
        public const uint FullTargetFps = 0;

        public static uint For(uint zone, bool helloPublished, out uint targetFps)
        {
            bool dive = zone == Zone.Core || zone == Zone.TileVoid || zone == Zone.OuterVoid;
            if (helloPublished && dive)
            {
                targetFps = FullTargetFps;
                return PerfMode.Full;
            }
            targetFps = IdleTargetFps;
            return PerfMode.Idle;
        }
    }
}
