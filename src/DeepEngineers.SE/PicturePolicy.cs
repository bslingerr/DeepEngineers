using System;
using System.Collections.Generic;
using DeepEngineers.Wire;

namespace DeepEngineers.SE
{
    /// <summary>
 /// The S7a picture policy the SE plugin feeds the view machine (<see cref="ViewInputs.Picture"/>; ARCHITECTURE §4.2
 /// "Picture policy (S7a)", CONTRACT §5.4 "C5/C7 semantics for S7a"): <see cref="ViewPicture.Site"/> (the default since
 /// S7a: Subnautica's picture inside the site below the Approach band) or <see cref="ViewPicture.Waterline"/> (S2's
 /// policy, the fallback). Read once from <c>DEEPENGINEERS_PICTURE=site|waterline</c> at start, switched at run time by
 /// <c>de.s7 {picture: "site"|"waterline"}</c> (<see cref="S7aCommands"/> delegates here). The machine takes a change on
 /// its next step; the switch is meant for Space (a switch while Subnautica's picture is shown in Approach swaps at once).
 /// Game-independent.
 /// </summary>
    internal static class PicturePolicy
    {
        public const string EnvVar = "DEEPENGINEERS_PICTURE";

        private static uint _current = FromEnvironment(out _source);
        private static string _source;

        /// <summary>The selected policy (the machine's effective policy also honours S7a.M1: <see cref="ViewStateMachine.EffectivePicture"/>).</summary>
        public static uint Current { get { return _current; } }
        /// <summary>Where <see cref="Current"/> came from: "default", "env" or the last command's via.</summary>
        public static string Source { get { return _source; } }
        public static long Switches { get; private set; }
        /// <summary>Review F17: the live view state (set by S2Host; null = unknown, the switch is allowed).</summary>
        public static Func<uint> CurrentViewState;

        /// <summary>"site" → Site, "waterline" → Waterline (case and blanks ignored); false for anything else.</summary>
        public static bool TryParse(string v, out uint picture)
        {
            picture = ViewPicture.Site;
            if (v == null) return false;
            switch (v.Trim().ToLowerInvariant())
            {
                case "site": picture = ViewPicture.Site; return true;
                case "waterline": picture = ViewPicture.Waterline; return true;
                default: return false;
            }
        }

        private static uint FromEnvironment(out string source)
        {
            source = "default";
            try
            {
                string v = Environment.GetEnvironmentVariable(EnvVar);
                uint p;
                if (!string.IsNullOrEmpty(v) && TryParse(v, out p)) { source = "env"; return p; }
            }
            catch (Exception) { }
            return ViewPicture.Site;
        }

        /// <summary>Sets the policy (tests, commands); returns true when it changed.</summary>
        public static bool Set(uint picture, string via)
        {
            uint p = picture == ViewPicture.Waterline ? ViewPicture.Waterline : ViewPicture.Site;
            _source = via ?? "set";
            if (p == _current) return false;
            _current = p;
            Switches++;
            return true;
        }

        /// <summary><c>de.s7 {picture: "site"|"waterline"}</c>: the reply (ok, picture, was, effective, line).</summary>
        public static Dictionary<string, object> Command(IDictionary<string, object> args)
        {
            object v = null;
            if (args != null) args.TryGetValue("picture", out v);
            uint p;
            if (!TryParse(v as string, out p))
            {
                string err = "de.s7: picture must be \"site\" or \"waterline\" (got " + (v == null ? "null" : "'" + v + "'") + ")";
                DeLog.Info("DE.S7a " + err);
                return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", err }, { "line", err } };
            }
            uint was = _current;
            // review F17: a switch with the latch on drops margins and skip in one request: Space only
            try
            {
                Func<uint> view = CurrentViewState;
                uint vs = view != null ? view() : ViewState.Space;
                if (p != _current && vs != ViewState.Space)
                {
                    string err = "de.s7: switch the picture policy in Space only (view " + vs + ": the latch may be on)";
                    DeLog.Info("DE.S7a " + err);
                    return new Dictionary<string, object>(StringComparer.Ordinal) { { "ok", false }, { "error", err }, { "line", err } };
                }
            }
            catch (Exception) { }
            bool changed = Set(p, "de.s7");
            uint eff = ViewStateMachine.EffectivePicture(p, S7aPolicyMutants.MachineWaterline ? ViewMutants.S7aM1Waterline : 0u);
            string line = "DE.S7a picture " + ViewPicture.Name(p) + (changed ? " (was " + ViewPicture.Name(was) + ")" : " (unchanged)") +
                          (eff != p ? "; effective " + ViewPicture.Name(eff) + " (MUTANT S7a.M1)" : "") + "; the view machine takes it on its next step";
            DeLog.Info(line);
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "ok", true }, { "picture", ViewPicture.Name(p) }, { "was", ViewPicture.Name(was) }, { "changed", changed },
                { "effective", ViewPicture.Name(eff) }, { "line", line },
            };
        }

        /// <summary>
 /// The S5 entity-set radii under S7a (game-independent rule of <c>PictureHooks.EntityRadii</c>): while the camera
 /// is above the sea (<paramref name="camSnY"/> &gt; 0) under the Site policy, at least 1,500 m to enter (above the sea) and 1,600 m
 /// to leave at any altitude (F10) (<see cref="S7aConventions.MirrorRadius"/>); otherwise the configured S5 radii unchanged. A larger
 /// configured radius (dev <c>de.s5</c>) is kept.
 /// </summary>
        public static void EntityRadii(bool pictureSite, double camSnY, ref double enterM, ref double leaveM)
        {
            if (!pictureSite) return;
            double e, l;
            S7aConventions.MirrorRadius(true, camSnY, out e, out l);   // F10: leave 1,600 m at any altitude, enter 1,500 m above the sea
            if (e > enterM) enterM = e;
            if (l > leaveM) leaveM = l;
        }

        public static string Describe()
        {
            return ViewPicture.Name(_current) + " (" + _source + ")" + (S7aPolicyMutants.MachineWaterline ? " MUTANT S7a.M1: the machine runs waterline" : "");
        }
    }

    /// <summary>
 /// The S7a seeded failures of the picture policy on the SE side (ROADMAP S7a "Seeded failures"); dev builds only,
 /// switched through <see cref="Mutants.Set"/> (<c>DEEPENGINEERS_MUTANTS</c>, <c>de.mutant</c>, <c>/de mutant</c>):
 /// <list type="bullet">
 /// <item>S7a.M1: the view machine runs the Waterline policy while Site is selected (<see cref="ViewMutants.S7aM1Waterline"/>
 /// through <see cref="ViewStateDriver"/>); S7a.N1 red.</item>
 /// <item>S7a.M3: no crossing fade on a picture edge (<see cref="CrossingFlash"/>); S7a.N2 fade red.</item>
 /// </list>
 /// S7a.M4 is the placement ghost's (<c>S7aGhostMutants</c>); M2, M5, M6 are Subnautica-side and answered NOT-SE.
 /// </summary>
    internal static class S7aPolicyMutants
    {
        /// <summary>S7a.M1: the machine runs Waterline while Site is selected.</summary>
        public static volatile bool MachineWaterline;
        /// <summary>S7a.M3: no fade on a picture edge.</summary>
        public static volatile bool NoFade;

        public static string Active
        {
            get
            {
                var parts = new List<string>(2);
                if (MachineWaterline) parts.Add("S7a.M1");
                if (NoFade) parts.Add("S7a.M3");
                return string.Join(",", parts.ToArray());
            }
        }

        public static void ClearAll()
        {
            MachineWaterline = false;
            NoFade = false;
        }

        /// <summary>Switches S7a.M1 / S7a.M3; NOT-SE text for the Subnautica-side ids; null for any other id (S7a.M4 included).</summary>
        public static string Set(string id, bool on)
        {
            string state = on ? "ON" : "OFF";
            switch ((id ?? "").Trim().ToUpperInvariant())
            {
                case "S7A.M1":
                    MachineWaterline = on;
                    return "S7a.M1 " + state + " (the view machine runs the waterline picture policy while site is selected; S7a.N1 red)";
                case "S7A.M3":
                    NoFade = on;
                    return "S7a.M3 " + state + " (no crossing fade on a picture edge; S7a.N2 fade red)";
                case "S7A.M2":
                case "S7A.M5":
                case "S7A.M6":
                    return "NOT-SE " + id.Trim() + " is a Subnautica-side seeded failure (SN plugin); nothing switched here";
                default:
                    return null;
            }
        }
    }
}
