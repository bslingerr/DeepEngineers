using System;
using System.Globalization;
using DeepEngineers.Wire;
using Sandbox.Game;
using SpaceEngineers.Game;

namespace DeepEngineers.SE
{
    internal enum VersionStatus
    {
        Unknown = 0,
        Ok = 1,
        /// <summary>The running game is not the pinned build: display bridge off, plugin otherwise healthy.</summary>
        Refused = 2,
    }

    /// <summary>
 /// Game version pin (CONTRACT §0, ROADMAP S0.N12/S0.M8). The value is read at run time from
 /// `MyPerGameSettings.BasicGameInfo.GameVersion`
 /// never
 /// from the `SpaceEngineersGame.SE_VERSION` const, which the compiler inlines into this assembly.
 /// A refusal turns the display bridge off (C0 `seFlags.displayRefused`); the link itself stays up.
 /// </summary>
    internal static class VersionGate
    {
        /// <summary>CONTRACT §0: SE 1.210.014 (Steam build 24675677); equal to Wire's VersionPinRules.SeGameVersion (S9 test).</summary>
        public const int ExpectedGameVersionConst = 1210014;

        /// <summary>The expected value, set at build time from the const; a test hook may override it.</summary>
        public static int Expected = ExpectedGameVersionConst;

        /// <summary>Dev builds: `DEEPENGINEERS_TEST_GAMEVERSION=&lt;n&gt;` runs the S0.N12 hook once at Init.</summary>
        public const string TestEnvVar = "DEEPENGINEERS_TEST_GAMEVERSION";

        public static VersionStatus Status { get; private set; }
        public static int? Found { get; private set; }
        public static string Source { get; private set; }
        public static string LastLine { get; private set; }

        public static bool Refused { get { return Status == VersionStatus.Refused; } }

        /// <summary>Reads the game version now and logs `DE.Version OK|REFUSE`.</summary>
        public static string Check(string context)
        {
            int? found;
            string source;
            try
            {
                if (Mutants.VersionFromCompiledConst)
                {
                    // S0.M8 mutant: the inlined compile-time const ignores what the running game says.
                    found = SpaceEngineersGame.SE_VERSION;
                    source = "SpaceEngineersGame.SE_VERSION (compile-time const; MUTANT S0.M8)";
                }
                else
                {
                    found = MyPerGameSettings.BasicGameInfo.GameVersion;
                    source = "MyPerGameSettings.BasicGameInfo.GameVersion";
                }
            }
            catch (Exception e)
            {
                found = null;
                source = "error " + e.GetType().Name + ": " + e.Message;
            }
            Found = found;
            Source = source;
            bool ok = found.HasValue && found.Value == Expected;
            Status = ok ? VersionStatus.Ok : VersionStatus.Refused;
            string f = found.HasValue ? found.Value.ToString(CultureInfo.InvariantCulture) : "null";
            // S9-version-gate: the fields first (tools parse them), then the plain words of Wire.VersionPinRules
            LastLine = ok
                ? "DE.Version OK game=" + f + " expected=" + Expected + " source=" + source + " (" + context + ")"
                : "DE.Version REFUSE game=" + f + " expected=" + Expected + " source=" + source + " (" + context +
                  "; display bridge off, plugin otherwise healthy) - " + VersionPinRules.SeLogWords(found);
            DeLog.Info(LastLine);
            _hudPending = !ok; // S9: one HUD notice, shown from the first in-world simulation update (TickHud)
            return LastLine;
        }

        // ------------------------------------------------------------------------------------- S9 HUD notice

        private static volatile bool _hudPending;
        private static bool _hudShown;

        /// <summary>
 /// S9-version-gate: once per process, at the first in-world simulation update after a refusal (the HUD exists
 /// only in a world; the check itself runs at Init, at the main menu), one red HUD notification with
 /// <see cref="VersionPinRules.SeHud"/> through <c>IMyUtilities.ShowNotification(string, int, string)</c>
 /// Retried while the
 /// HUD is not up; a later re-check that passes clears it. Game thread (the session component).
 /// </summary>
        public static void TickHud()
        {
            if (!_hudPending || _hudShown) return;
            try
            {
                var u = Sandbox.ModAPI.MyAPIGateway.Utilities;
                if (u == null) return;
                u.ShowNotification(VersionPinRules.SeHud(Found), 20000, "Red");
                _hudShown = true;
                _hudPending = false;
                DeLog.Info("DE.Version HUD notice shown: " + VersionPinRules.SeHud(Found));
            }
            catch (Exception) { /* the HUD is not up yet: retried at the next update */ }
        }

        /// <summary>
 /// S0.N12 dev-build test hook: sets the game's `GameVersion` to <paramref name="fakeVersion"/>, runs the check,
 /// and restores the original value right after (update thread; no other game code runs in between). The check
 /// result (REFUSE for any value other than 1210014) stays in force until the next check.
 /// </summary>
        public static string RunTestHook(int fakeVersion, string via)
        {
            if (!BuildFlavor.IsDev)
            {
                string r = "DE.Version TEST-HOOK refused: release build";
                DeLog.Info(r);
                return r;
            }
            int? saved;
            try { saved = MyPerGameSettings.BasicGameInfo.GameVersion; }
            catch (Exception e)
            {
                string r = "DE.Version TEST-HOOK failed to read the game version: " + e.GetType().Name;
                DeLog.Info(r);
                return r;
            }
            string savedText = saved.HasValue ? saved.Value.ToString(CultureInfo.InvariantCulture) : "null";
            DeLog.Info("DE.Version TEST-HOOK set MyPerGameSettings.BasicGameInfo.GameVersion " + savedText + " -> " + fakeVersion + " (" + via + ")");
            string line;
            try
            {
                MyPerGameSettings.BasicGameInfo.GameVersion = fakeVersion;
                line = Check("test hook via " + via);
            }
            finally
            {
                try { MyPerGameSettings.BasicGameInfo.GameVersion = saved; } catch (Exception) { }
            }
            int? back = null;
            try { back = MyPerGameSettings.BasicGameInfo.GameVersion; } catch (Exception) { }
            DeLog.Info("DE.Version TEST-HOOK restored GameVersion=" + (back.HasValue ? back.Value.ToString(CultureInfo.InvariantCulture) : "null") +
                       (back == saved ? "" : " (RESTORE MISMATCH, expected " + savedText + ")"));
            return line;
        }

        /// <summary>Init: the plain check, or the S0.N12 hook when the dev environment variable asks for it.</summary>
        public static void CheckAtInit()
        {
            string env = null;
            if (BuildFlavor.IsDev)
            {
                try { env = Environment.GetEnvironmentVariable(TestEnvVar); } catch (Exception) { }
            }
            int fake;
            if (!string.IsNullOrEmpty(env) && int.TryParse(env.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out fake))
                RunTestHook(fake, "env " + TestEnvVar);
            else
                Check("Init");
        }
    }
}
