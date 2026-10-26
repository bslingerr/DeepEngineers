using System;
using System.Globalization;

namespace DeepEngineers.Wire
{
    /// <summary>
 /// S9: the game builds DeepEngineers is pinned to (CONTRACT §0) and the plain-words messages both plugins
 /// show when the running game is another build. One source for the numbers and the wording: SE's
 /// <c>VersionGate</c> (HUD notification + <c>DE.Version REFUSE</c> line) and Subnautica's <c>SnVersionGate</c>
 /// (<c>ErrorMessage</c> notice + <c>DE.Version REFUSE</c> line) read it; docs/README.md quotes it. Pure.
 /// </summary>
    public static class VersionPinRules
    {
        /// <summary>SE <c>MyPerGameSettings.BasicGameInfo.GameVersion</c> of the pinned build (1.210.014).</summary>
        public const int SeGameVersion = 1210014;
        /// <summary>Steam build id of the pinned SE (app 244850).</summary>
        public const int SeSteamBuild = 24675677;

        /// <summary>Subnautica changeset of the pinned build (<c>SNUtils.GetPlasticChangeSetOfBuild</c>).</summary>
        public const int SnChangeset = 83031;
        /// <summary>Subnautica's <c>Application.version</c> of the pinned build.</summary>
        public const string SnGameVersion = "1.22.83031";
        /// <summary>Steam build id of the pinned Subnautica (app 264710).</summary>
        public const int SnSteamBuild = 20241558;

        /// <summary>The advice both messages end with (ROADMAP S9 README item, CONTRACT §7 "Updates").</summary>
        public const string UpdateAdvice =
            "To keep a working pair, set both games in Steam to \"Only update this game when I launch it\" " +
            "(Properties > Updates) and always start them with Play.cmd, which does not start them through Steam.";

        /// <summary>SE's integer game version as players read it: 1210014 → "1.210.014"; null → "unknown".</summary>
        public static string FormatSeVersion(int? v)
        {
            if (!v.HasValue || v.Value < 0) return "unknown";
            int x = v.Value;
            return (x / 1000000).ToString(CultureInfo.InvariantCulture) + "." +
                   ((x / 1000) % 1000).ToString("000", CultureInfo.InvariantCulture) + "." +
                   (x % 1000).ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>SE: true when the running game is the pinned build.</summary>
        public static bool SeMatches(int? found) { return found.HasValue && found.Value == SeGameVersion; }

        /// <summary>
 /// Subnautica: the changeset decides when it can be read (&gt; 0); otherwise <c>Application.version</c> must be the
 /// pinned text. <paramref name="foundText"/> is what the messages call "this game".
 /// </summary>
        public static bool SnMatches(int changeset, string appVersion, out string foundText)
        {
            string app = string.IsNullOrEmpty(appVersion) ? null : appVersion.Trim();
            if (changeset > 0)
            {
                foundText = app != null ? app + " (changeset " + changeset.ToString(CultureInfo.InvariantCulture) + ")"
                                        : "changeset " + changeset.ToString(CultureInfo.InvariantCulture);
                return changeset == SnChangeset;
            }
            foundText = app ?? "unknown";
            return app != null && app == SnGameVersion;
        }

        /// <summary>SE HUD notification (one, in the first world): what is needed, that DeepEngineers stays off, the advice.</summary>
        public static string SeHud(int? found)
        {
            return "DeepEngineers is off: it needs Space Engineers " + FormatSeVersion(SeGameVersion) + ", this game is " +
                   FormatSeVersion(found) + ". No Subnautica view this session. Set Steam updates to \"only when I launch it\" (README).";
        }

        /// <summary>The plain words of SE's <c>DE.Version REFUSE</c> line (after its fields).</summary>
        public static string SeLogWords(int? found)
        {
            return "DeepEngineers needs Space Engineers " + FormatSeVersion(SeGameVersion) + " (Steam build " +
                   SeSteamBuild.ToString(CultureInfo.InvariantCulture) + ") and this game is " + FormatSeVersion(found) +
                   ". The plugin stays inactive for the underwater view this session: Subnautica draws nothing and the " +
                   "window, picture and sound are left alone, so Space Engineers plays as usual with the Water Mod (the " +
                   "crater terrain and the save guard still load, so a DeepEngineers world stays safe). Install the " +
                   "DeepEngineers release made for this game build when there is one. " + UpdateAdvice;
        }

        /// <summary>Subnautica's notice (one, on its own screen: the plugin stays dormant, so the window is not hidden).</summary>
        public static string SnHud(string foundText)
        {
            return "DeepEngineers is off: it needs Subnautica " + SnGameVersion + ", this game is " + (foundText ?? "unknown") +
                   ". Set Steam updates to \"only when I launch it\" (README).";
        }

        /// <summary>The plain words of Subnautica's <c>DE.Version REFUSE</c> line (after its fields).</summary>
        public static string SnLogWords(string foundText)
        {
            return "DeepEngineers needs Subnautica " + SnGameVersion + " (Steam build " +
                   SnSteamBuild.ToString(CultureInfo.InvariantCulture) + ") and this game is " + (foundText ?? "unknown") +
                   ". The plugin stays inactive: Subnautica runs as the plain game (no bridge, no hidden window, no save " +
                   "or setting change), so Space Engineers gets no underwater view. Close Subnautica normally. Install " +
                   "the DeepEngineers release made for this game build when there is one. " + UpdateAdvice;
        }
    }
}
