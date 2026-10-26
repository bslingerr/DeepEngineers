using System;
using System.Reflection;
using DeepEngineers.Wire;
using UnityEngine;

namespace DeepEngineers.SN
{
    /// <summary>
 /// S9-version-gate: Subnautica's build pin (CONTRACT §0: 1.22.83031, Steam build 20241558), checked on
 /// the plugin's first player-loop frame before the bridge start. Sources:
 /// <c>SNUtils.GetPlasticChangeSetOfBuild(int fallbackValue)</c> (Assembly-CSharp-firstpass; reads
 /// StreamingAssets\SNUnmanagedData\plastic_status.ignore, "83031" on this PC) and <c>Application.version</c>
 /// ("1.22.83031"). Only a session launched for the bridge is gated: a plain Subnautica
 /// launch stays dormant anyway and only gets the log line.
 /// <para>On a mismatch with the token: one <c>DE.Version REFUSE</c> line in plain words
 /// (<see cref="VersionPinRules.SnLogWords"/>), the plugin stays dormant (no bridge, no section, no window, save or
 /// setting change: CONTRACT §1.2 "dormant"), and one on-screen notice through the game's own
 /// <c>ErrorMessage.AddMessage(string)</c> (public static; it drops the text while its private static <c>main</c> is
 /// null, so the notice waits until <c>ErrorMessage.main</c> exists, at most <see cref="NoticeGiveUpS"/> s).</para>
 /// </summary>
    internal static class SnVersionGate
    {
        public const float NoticeGiveUpS = 600f;

        public static bool Refused { get; private set; }
        public static string FoundText { get; private set; }

        private static bool _noticeShown;
        private static float _noticeGiveUpAt = -1f;
        private static FieldInfo _mainField;

        /// <summary>
 /// The check (main thread, first player-loop frame). Returns false when the bridge must not start. Never throws.
 /// </summary>
        public static bool CheckAtStart()
        {
            int changeset = 0;
            string app = null;
            try { changeset = ReadChangeset(); } catch (Exception) { changeset = 0; }
            try { app = Application.version; } catch (Exception) { app = null; }
            int fake;
            if (TryTestChangeset(out fake))
            {
                DeLog.Info("DE.Version TEST-HOOK sn changeset " + changeset + " -> " + fake + " (" + TestEnvVar + ", dev build)");
                changeset = fake;
            }
            string found;
            bool ok = VersionPinRules.SnMatches(changeset, app, out found);
            FoundText = found;
            string token;
            bool bridge = false;
            try { bridge = BridgeToken.IsPresent(out token); } catch (Exception) { }
            string fields = "changeset=" + changeset + " appVersion=" + (app ?? "null") + " expected=" + VersionPinRules.SnChangeset +
                            " (" + VersionPinRules.SnGameVersion + ")";
            if (ok)
            {
                DeLog.Info("DE.Version OK sn " + fields);
                return true;
            }
            if (!bridge)
            {
                DeLog.Info("DE.Version MISMATCH sn " + fields + " (no bridge token: plain Subnautica, nothing to refuse)");
                return true; // BridgeStartup stays dormant without the token by itself
            }
            Refused = true;
            DeLog.Warn("DE.Version REFUSE sn " + fields + " - " + VersionPinRules.SnLogWords(found));
            DeLog.Info("DE.Bridge DORMANT (game version " + found + ", pinned " + VersionPinRules.SnGameVersion + ")");
            _noticeGiveUpAt = SafeTime() + NoticeGiveUpS;
            return false;
        }

        /// <summary>Every frame while refused: shows the notice once, as soon as the game's message area exists.</summary>
        public static void TickNotice()
        {
            if (!Refused || _noticeShown) return;
            float now = SafeTime();
            if (_noticeGiveUpAt >= 0f && now > _noticeGiveUpAt)
            {
                _noticeShown = true;
                DeLog.Warn("DE.Version notice not shown: the game's message area did not appear within " + NoticeGiveUpS + " s (the log line stands)");
                return;
            }
            try
            {
                if (_mainField == null) _mainField = typeof(ErrorMessage).GetField("main", BindingFlags.NonPublic | BindingFlags.Static);
                if (_mainField == null || _mainField.GetValue(null) == null) return;
                ErrorMessage.AddMessage(VersionPinRules.SnHud(FoundText));
                _noticeShown = true;
                DeLog.Info("DE.Version notice shown: " + VersionPinRules.SnHud(FoundText));
            }
            catch (Exception e)
            {
                _noticeShown = true;
                DeLog.Warn("DE.Version notice failed (" + e.GetType().Name + ": " + e.Message + "); the log line stands");
            }
        }

        /// <summary>Dev builds: <c>DEEPENGINEERS_TEST_SN_CHANGESET=&lt;n&gt;</c> pretends another build (settles the notice HYPOTHESIS).</summary>
        public const string TestEnvVar = "DEEPENGINEERS_TEST_SN_CHANGESET";

        private static bool TryTestChangeset(out int value)
        {
            value = 0;
            if (!BuildFlavor.Dev) return false; // S9-release-flavor: test hooks are dev only
            string v = null;
            try { v = Environment.GetEnvironmentVariable(TestEnvVar); } catch (Exception) { }
            return !string.IsNullOrEmpty(v) && int.TryParse(v.Trim(), out value) && value > 0;
        }

        /// <summary>Isolated so that a missing member fails this call only (it is then read as "unknown").</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int ReadChangeset() { return SNUtils.GetPlasticChangeSetOfBuild(0); }

        private static float SafeTime()
        {
            try { return Time.realtimeSinceStartup; } catch (Exception) { return 0f; }
        }
    }
}
