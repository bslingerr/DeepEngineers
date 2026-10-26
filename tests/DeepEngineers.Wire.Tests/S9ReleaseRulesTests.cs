using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S9: <see cref="UserCommandRules"/> (what a release build still runs: the user tuning
 /// commands of the conventions, nothing of the dev/lab/test set) and <see cref="VersionPinRules"/> (the pins of
 /// CONTRACT §0 and the plain-words messages of both version gates).
 /// </summary>
    [TestClass]
    public class S9ReleaseRulesTests
    {
        private static string[] W(string line) { return line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); }

        private static Dictionary<string, object> Args(params object[] kv)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        private static Dictionary<string, object> Argv(params string[] words)
        {
            var list = new List<object>();
            foreach (string w in words) list.Add(w);
            return Args("argv", list);
        }

        // ------------------------------------------------------------------------------------------------ chat

        [TestMethod]
        public void Chat_Dev_RunsEverything()
        {
            foreach (string line in new[] { "mutant S0.M8 on", "teleport 0 0 0", "spawnplanet", "sn quit", "s3 lab 0 0", "vtest 1", "selftest" })
                Assert.IsTrue(UserCommandRules.ChatAllowed(true, W(line)), line);
        }

        [TestMethod]
        public void Chat_Release_KeepsTheUserTuningCommands()
        {
            string[] keep =
            {
                "", "help", "version", "HELP",
                "water", "water waveHeight=0.3 fog=0.1,0.2,0.3", "water reset",
                "s7 band 1000 1100", "s7 band default", "s7 band status",
                "s7p fx", "s7p fx light 0.3", "s7p fx glow=0.25", "s7p fx reset",
                "s7t look", "s7t look shadow=0.6", "s7t look off",
                "sn s7t look shadow=0.6 ambient=1.5", "sn s7t horizon off", "sn s7t clouds on",
                "sn s7 lightScale suit 0.04", "sn s7 lightscale status", "sn s7 landmarks off",
                "sn s7p fx light 0.3", "sn de.s7t horizon on", "display mode aprime", "display state",
            };
            foreach (string line in keep) Assert.IsTrue(UserCommandRules.ChatAllowed(false, W(line)), "kept: /de " + line);
        }

        [TestMethod]
        public void Chat_Release_RefusesDevLabAndTestCommands()
        {
            string[] refuse =
            {
                "mutant S0.M8 on", "vtest 1210015", "selftest", "ping", "state", "teleport 0 0 0", "path", "look E", "pixel 0 0 0",
                "site repair", "spawnplanet", "g2e3", "display probe=true", "display toggle", "display readback on", "view underwater", "audio", "zorder", "swim",
                "s3 lab 0 0", "s5 record", "s6 damage", "spawn Peeper 1 5", "terrain", "s7", "s7 status", "s7 ghost on",
                "s7 time 0.5", "s7 picture site", "s7p above off", "s7p hold=1", "s7t fade off", "s7t flash on",
                "s7t impostors off", "sn", "sn quit", "sn mutant x on", "sn s7", "sn s7 status", "sn s7t", "sn s7t mask on",
                "sn s7t haze farMax=8000", "sn s7p damage=2", "sn s2 record=on", "sn capture", "sn lights scale=0.1",
            };
            foreach (string line in refuse) Assert.IsFalse(UserCommandRules.ChatAllowed(false, W(line)), "refused: /de " + line);
        }

        // --------------------------------------------------------------------------------------------- requests

        [TestMethod]
        public void Request_Release_ChatRelayForms()
        {
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7", Argv("lightScale", "suit", "0.04")));
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7", Argv("landmarks", "off")));
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7t", Argv("look", "shadow=0.6")));
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7t", Argv("horizon", "off")));
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7t", Argv("clouds", "on")));
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7p", Argv("fx", "light", "0.3")));
            // SE's own relay of /de s7p fx … (EffectsProducer) and SE's s7t chat form (argv + {firstWord: rest})
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7p", Args("fx", "light 0.3")));
            var s7t = Argv("look", "shadow=0.6");
            s7t["look"] = "shadow=0.6";
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7t", s7t));
            // the action modifier never decides: {fx: "flame", action: "1.5"} is the fx command
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.s7p", Args("fx", "flame", "action", "1.5")));
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "de.water", null));
            Assert.IsTrue(UserCommandRules.RequestAllowed(false, "DE.S7", Argv("LIGHTSCALE", "status")), "case-insensitive");
        }

        [TestMethod]
        public void Request_Release_RefusesEverythingElse()
        {
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.quit", null));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.mutant", Args("name", "S1.M3", "state", "on")));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.ping", null));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.capture", Args("name", "x")));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s3", Args("h", "raycast")));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.spawn", Args("techType", "Reaper")));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7", null), "no selector");
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7", Args("action", "status")), "action alone is no selector");
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7", Argv("ghost", "on")));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7", Args("lightScale", "suit 1", "time", 0.5)), "every key must be allowed");
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7t", Argv("haze", "farMax=8000")));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7t", Args("look", "x", "fade", "off")));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7p", Args("above", "off")));
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7p", Args("argv", new List<object>())), "empty argv");
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s7p", "fx"), "args must be an object");
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, null, null));
            Assert.IsTrue(UserCommandRules.RequestAllowed(true, "de.mutant", Args("name", "S1.M3")), "dev runs everything");
        }

        [TestMethod]
        public void ControlRead_ReleaseOnlyFromSeChatRelay()
        {
            uint h = DevControl.HarnessAttached, r = DevControl.SeChatRelay;
            Assert.IsFalse(UserCommandRules.ControlReadAllowed(true, 0));
            Assert.IsTrue(UserCommandRules.ControlReadAllowed(true, h));
            Assert.IsTrue(UserCommandRules.ControlReadAllowed(true, r));
            Assert.IsFalse(UserCommandRules.ControlReadAllowed(false, 0));
            Assert.IsFalse(UserCommandRules.ControlReadAllowed(false, h), "a release build never serves a harness");
            Assert.IsTrue(UserCommandRules.ControlReadAllowed(false, r));
            Assert.IsFalse(UserCommandRules.ControlReadAllowed(false, h | r));
            Assert.IsTrue(UserCommandRules.HarnessReadAllowed(true, h));
            Assert.IsFalse(UserCommandRules.HarnessReadAllowed(true, r));
            Assert.IsFalse(UserCommandRules.HarnessReadAllowed(false, h));
        }

        [TestMethod]
        public void ReleaseHelp_NamesOnlyKeptCommands()
        {
            Assert.IsTrue(UserCommandRules.ReleaseHelp.Length >= 5);
            foreach (string line in UserCommandRules.ReleaseHelp)
            {
                Match m = Regex.Match(line, @"^/de ((?:sn )?[a-z0-9.]+)(?: ([A-Za-z]+))?");
                Assert.IsTrue(m.Success, line);
                string[] argv = W(m.Value.Substring(4));
                Assert.IsTrue(UserCommandRules.ChatAllowed(false, argv), "help line runs in release: " + line);
            }
        }

        // -------------------------------------------------------------------------------------------- versions

        [TestMethod]
        public void Pins_AreContractSection0()
        {
            Assert.AreEqual(1210014, VersionPinRules.SeGameVersion);
            Assert.AreEqual(24675677, VersionPinRules.SeSteamBuild);
            Assert.AreEqual(83031, VersionPinRules.SnChangeset);
            Assert.AreEqual("1.22.83031", VersionPinRules.SnGameVersion);
            Assert.AreEqual(20241558, VersionPinRules.SnSteamBuild);
            Assert.AreEqual("1.210.014", VersionPinRules.FormatSeVersion(1210014));
            Assert.AreEqual("1.211.003", VersionPinRules.FormatSeVersion(1211003));
            Assert.AreEqual("1.205.026", VersionPinRules.FormatSeVersion(1205026));
            Assert.AreEqual("unknown", VersionPinRules.FormatSeVersion(null));
        }

        [TestMethod]
        public void Pins_EqualThePluginsAndTheInstallerSources()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("repository root not found above " + AppDomain.CurrentDomain.BaseDirectory);
            string gate = File.ReadAllText(Path.Combine(root, "src", "DeepEngineers.SE", "VersionGate.cs"), Encoding.UTF8);
            Match m = Regex.Match(gate, @"ExpectedGameVersionConst\s*=\s*(\d+)\s*;");
            Assert.IsTrue(m.Success, "VersionGate.ExpectedGameVersionConst");
            Assert.AreEqual(VersionPinRules.SeGameVersion.ToString(), m.Groups[1].Value, "SE VersionGate = VersionPinRules");
            string pins = Path.Combine(root, "src", "DeepEngineers.Launcher", "de_pins.py");
            if (!File.Exists(pins)) return; // the installer's pin file (S9) is optional for this test
            string py = File.ReadAllText(pins, Encoding.UTF8);
            StringAssert.Contains(py, "SE_BUILD_ID = \"" + VersionPinRules.SeSteamBuild + "\"");
            StringAssert.Contains(py, "SN_BUILD_ID = \"" + VersionPinRules.SnSteamBuild + "\"");
            StringAssert.Contains(py, "SN_CHANGESET = \"" + VersionPinRules.SnChangeset + "\"");
            StringAssert.Contains(py, "SE_GAME_VERSION = " + VersionPinRules.SeGameVersion);
        }

        [TestMethod]
        public void SnMatches_ChangesetFirst_ThenAppVersion()
        {
            string found;
            Assert.IsTrue(VersionPinRules.SnMatches(83031, "1.22.83031", out found));
            Assert.AreEqual("1.22.83031 (changeset 83031)", found);
            Assert.IsFalse(VersionPinRules.SnMatches(84000, "1.22.83031", out found), "the changeset decides when readable");
            Assert.IsTrue(VersionPinRules.SnMatches(0, "1.22.83031", out found), "unreadable changeset: the app version");
            Assert.IsFalse(VersionPinRules.SnMatches(0, "1.23.90000", out found));
            Assert.AreEqual("1.23.90000", found);
            Assert.IsFalse(VersionPinRules.SnMatches(0, null, out found));
            Assert.AreEqual("unknown", found);
        }

        [TestMethod]
        public void Messages_SayTheBuild_Inactive_AndTheUpdateAdvice()
        {
            string[] all =
            {
                VersionPinRules.SeHud(1211003), VersionPinRules.SeLogWords(1211003),
                VersionPinRules.SnHud("1.23.90000"), VersionPinRules.SnLogWords("1.23.90000"),
            };
            foreach (string s in all)
            {
                StringAssert.Contains(s, "when I launch it", s);
                Assert.IsTrue(s.Contains("is off") || s.Contains("stays inactive"), "says DeepEngineers stays off: " + s);
                Assert.IsFalse(s.Contains("\n"), "one line");
            }
            StringAssert.Contains(all[0], "1.210.014");
            StringAssert.Contains(all[0], "1.211.003");
            StringAssert.Contains(all[1], "Steam build 24675677");
            StringAssert.Contains(all[2], "1.22.83031");
            StringAssert.Contains(all[3], "Steam build 20241558");
            StringAssert.Contains(all[3], "1.23.90000");
            Assert.IsTrue(all[0].Length <= 200 && all[2].Length <= 200, "HUD notices stay short");
        }

        private static string RepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "DeepEngineers.sln"))) return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return null;
        }
    }
}
