using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S2 SE side, game-independent units (ROADMAP S2 "SE plugin"): the A′ switch and the configuration, the S2 seeded
 /// failures, the chat syntax, the z-order walker's decision on synthetic window lists, the audio arithmetic and the
 /// composed-pixel probe verdict.
 /// </summary>
    [TestClass]
    public class S2UnitTests
    {
        [TestCleanup]
        public void Cleanup() { Mutants.ClearAll(); }

        // ============================================================================================ config / A'

        [TestMethod]
        public void S2Config_DisplaySwitch_FileEnvAndDefaults()
        {
            S2Config c = S2Config.Parse(null, null, null);
            Assert.IsTrue(c.Enabled && c.Display == DisplayDesign.F && c.AlphaFill && c.SeeThroughKey == "Apps" && c.SeeThroughKey == S2Config.DefaultSeeThroughKey && c.AutoFallback && !c.AlphaReadbackPeriodic);
            Assert.AreEqual("default", c.DisplaySource);

            c = S2Config.Parse("{\"display\": \"aprime\", \"alphaFill\": false, \"seeThroughKey\": \"none\", \"alphaReadback\": \"on\", \"autoFallback\": false, \"other\": 1}", null, null);
            Assert.AreEqual(DisplayDesign.APrime, c.Display);
            Assert.AreEqual(S2Config.FileName, c.DisplaySource);
            Assert.IsFalse(c.AlphaFill);
            Assert.AreEqual("", c.SeeThroughKey, "\"none\" disables the hotkey");
            Assert.IsTrue(c.AlphaReadbackPeriodic);
            Assert.IsFalse(c.AutoFallback);
            Assert.AreEqual(0, c.Notes.Count, "unknown keys are ignored silently");

            // the environment variable wins over the file (A' without editing a file)
            c = S2Config.Parse("{\"display\": \"f\"}", "aprime", null);
            Assert.AreEqual(DisplayDesign.APrime, c.Display);
            Assert.AreEqual(S2Config.DisplayEnvVar, c.DisplaySource);
            c = S2Config.Parse("{\"display\": \"aprime\"}", "F", null);
            Assert.AreEqual(DisplayDesign.F, c.Display);

            // S2 off: config or DEEPENGINEERS_S2=0
            Assert.IsFalse(S2Config.Parse("{\"s2\": false}", null, null).Enabled);
            Assert.IsFalse(S2Config.Parse(null, null, "0").Enabled);
            Assert.IsTrue(S2Config.Parse("{\"s2\": false}", null, "on").Enabled, "the environment wins");
        }

        [TestMethod]
        public void S2Config_DisplayWords_AndBadInput()
        {
            DisplayDesign d;
            string note;
            foreach (string w in new[] { "aprime", "APRIME", "a'", "A′", "a-prime", "a_prime", "fallback", " aprime " })
            {
                Assert.IsTrue(S2Config.TryParseDisplay(w, out d, out note), w);
                Assert.AreEqual(DisplayDesign.APrime, d, w);
            }
            foreach (string w in new[] { "f", "F", "design-f", "seethrough", "see-through" })
            {
                Assert.IsTrue(S2Config.TryParseDisplay(w, out d, out note), w);
                Assert.AreEqual(DisplayDesign.F, d, w);
            }
            Assert.IsFalse(S2Config.TryParseDisplay("c", out d, out note));
            StringAssert.Contains(note, "not implemented");
            Assert.IsFalse(S2Config.TryParseDisplay("bogus", out d, out note));

            S2Config c = S2Config.Parse("{\"display\": \"c\"}", "nonsense", "maybe");
            Assert.AreEqual(DisplayDesign.F, c.Display, "C is not implemented: F kept");
            Assert.AreEqual(3, c.Notes.Count, string.Join(" | ", c.Notes.ToArray()));
            c = S2Config.Parse("{not json", null, null);
            Assert.AreEqual(DisplayDesign.F, c.Display);
            Assert.AreEqual(1, c.Notes.Count);
            StringAssert.Contains(c.Describe(), "notes:");
        }

        // ============================================================================================== mutants

        [TestMethod]
        public void S2Mutants_SwitchesNamesAndMachineBits()
        {
            Mutants.ClearAll();
            Assert.AreEqual(0u, ViewStateAdapter.MutantBits());
            var expect = new Dictionary<string, Func<bool>>
            {
                { "S2.M1", () => S2Mutants.DwmCallDisabled },
                { "S2.M4", () => S2Mutants.NoHysteresis },
                { "S2.M7", () => S2Mutants.MarginsOffInPrime },
                { "S2.M8", () => S2Mutants.NoForegroundAtStart },
                { "S2.M10", () => S2Mutants.SnReadyNeedsSettled },
                { "S2.M11", () => S2Mutants.VolumesWithoutFocusGate },
                { "S2.M13", () => S2Mutants.WaterVolumeByCommand },
            };
            foreach (var kv in expect)
            {
                string r = Mutants.Set(kv.Key.ToLowerInvariant(), true);
                StringAssert.StartsWith(r, kv.Key + " ON", kv.Key);
                Assert.IsTrue(kv.Value(), kv.Key);
                StringAssert.Contains(Mutants.Active, kv.Key);
            }
            Assert.AreEqual(ViewMutants.M4NoHysteresis | ViewMutants.M10SettledAlways | ViewMutants.M11NoAudioGate, ViewStateAdapter.MutantBits(),
                "S2.M4, M10 and M11 reach Wire's state machine as its mutant bits");
            StringAssert.StartsWith(Mutants.Set("S2.M1", false), "S2.M1 OFF");
            Assert.IsFalse(S2Mutants.DwmCallDisabled);
            Mutants.ClearAll();
            Assert.AreEqual("", S2Mutants.Active);
            Assert.AreEqual("", Mutants.Active);

            // not SE-side: nothing switched, a NOT-SE text (C11 de.mutant answers ok=false)
            StringAssert.StartsWith(Mutants.Set("S2.M9", true), "NOT-SE S2.M9");
            foreach (string sn in new[] { "S2.M2", "S2.M3", "S2.M5", "S2.M6", "S2.M12", "S2.M14" })
                StringAssert.StartsWith(Mutants.Set(sn, true), "NOT-SE " + sn);
            Assert.AreEqual("", Mutants.Active);
            StringAssert.Contains(Mutants.Set("S2.M99", true), "S2.M13", "the unknown-id text lists the SE-side S2 mutants");
            // S0/S1 forwarding unchanged
            StringAssert.StartsWith(Mutants.Set("S1.M5", true), "S1.M5 ON");
            StringAssert.StartsWith(Mutants.Set("S0.M5a", true), "S0.M5a ON");
        }

        // =========================================================================================== chat syntax

        [TestMethod]
        public void S2Args_ChatSyntax()
        {
            string usage;
            Dictionary<string, object> a = S2Args.ParseChat("display", new[] { "display" }, out usage);
            Assert.AreEqual(true, a["state"]);
            a = S2Args.ParseChat("display", new[] { "display", "toggle" }, out usage);
            Assert.AreEqual(true, a["toggle"]);
            a = S2Args.ParseChat("display", new[] { "display", "mode", "APrime" }, out usage);
            Assert.AreEqual("aprime", a["mode"]);
            a = S2Args.ParseChat("display", new[] { "display", "margins", "on" }, out usage);
            Assert.AreEqual("on", a["margins"]);
            a = S2Args.ParseChat("display", new[] { "display", "alphafill", "off" }, out usage);
            Assert.AreEqual("off", a["alphaFill"]);
            a = S2Args.ParseChat("display", new[] { "display", "readback" }, out usage);
            Assert.AreEqual("once", a["readback"]);
            a = S2Args.ParseChat("display", new[] { "display", "probe", "255", "0", "255", "30" }, out usage);
            Assert.AreEqual(true, a["probe"]);
            CollectionAssert.AreEqual(new List<object> { 255.0, 0.0, 255.0 }, (List<object>)a["expect"]);
            Assert.AreEqual(30.0, a["tolerance"]);
            a = S2Args.ParseChat("display", new[] { "display", "skip=off", "margins=on" }, out usage);
            Assert.AreEqual("off", a["skip"]);
            Assert.AreEqual("on", a["margins"]);
            Assert.IsNull(S2Args.ParseChat("display", new[] { "display", "margins" }, out usage));
            StringAssert.StartsWith(usage, "/de display");
            Assert.IsNull(S2Args.ParseChat("display", new[] { "display", "probe", "1", "2" }, out usage));

            a = S2Args.ParseChat("view", new[] { "view", "force", "Underwater" }, out usage);
            Assert.AreEqual("underwater", a["force"]);
            a = S2Args.ParseChat("view", new[] { "view", "trace", "on" }, out usage);
            Assert.AreEqual("on", a["trace"]);
            a = S2Args.ParseChat("audio", new[] { "audio" }, out usage);
            Assert.AreEqual(true, a["dump"]);
            a = S2Args.ParseChat("zorder", new[] { "zorder" }, out usage);
            Assert.AreEqual(true, a["reassert"]);
            Assert.IsTrue(S2Args.IsS2Verb("display") && S2Args.IsS2Verb("zorder") && !S2Args.IsS2Verb("teleport"));

            S2State? s;
            Assert.IsTrue(S2Args.TryParseForce("auto", out s) && !s.HasValue);
            Assert.IsTrue(S2Args.TryParseForce("prime", out s) && s == S2State.Prime);
            Assert.IsFalse(S2Args.TryParseForce("deep", out s));
            bool present, ok;
            Assert.IsNull(S2Args.Switch(new Dictionary<string, object> { { "m", "auto" } }, "m", out present, out ok));
            Assert.IsTrue(present && ok);
            Assert.AreEqual(true, S2Args.Switch(new Dictionary<string, object> { { "m", "on" } }, "m", out present, out ok));
            Assert.IsNull(S2Args.Switch(new Dictionary<string, object> { { "m", "sideways" } }, "m", out present, out ok));
            Assert.IsFalse(ok);
        }

        // =============================================================================================== walker

        private static readonly IntPtr Se = new IntPtr(0x1000), Sn = new IntPtr(0x2000);

        private static WindowInfo W(long h, bool visible = true, bool cloaked = false, int w = 800, int hgt = 600, long owner = 0)
        {
            return new WindowInfo { Hwnd = new IntPtr(h), Visible = visible, Cloaked = cloaked, Width = w, Height = hgt, Owner = new IntPtr(owner) };
        }

        [TestMethod]
        public void ZOrderWalker_Decisions_OnSyntheticWindowLists()
        {
            // Subnautica directly under SE
            WalkResult r = ZOrderWalker.Decide(new[] { W(0x2000), W(0x3000) }, Se, Sn);
            Assert.IsTrue(r.Ok);
            Assert.AreEqual(Sn, r.First);
            Assert.AreEqual(0, r.SkippedCount);

            // skipped: invisible, cloaked, zero-area, owned by snHwnd (its IME window), owned by seHwnd
            r = ZOrderWalker.Decide(new[]
            {
                W(0x4001, visible: false), W(0x4002, cloaked: true), W(0x4003, w: 0), W(0x4004, hgt: 0),
                W(0x4005, owner: 0x2000), W(0x4006, owner: 0x1000), W(0x2000),
            }, Se, Sn);
            Assert.IsTrue(r.Ok, r.Describe(Sn, false));
            Assert.AreEqual(6, r.SkippedCount);
            CollectionAssert.AreEqual(new[] { "invisible", "cloaked", "zero-area", "zero-area", "owned-by-sn", "owned-by-se" },
                new List<string>(r.SkippedWhy).GetRange(0, 6));
            StringAssert.Contains(r.Describe(Sn, false), "skipped=6");

            // S2.M9 model: a visible Notepad between the games: red, the Notepad named as the first window
            r = ZOrderWalker.Decide(new[] { W(0x4001, visible: false), W(0x5000), W(0x2000) }, Se, Sn);
            Assert.IsFalse(r.Ok);
            Assert.AreEqual(new IntPtr(0x5000), r.First);
            Assert.AreEqual(1, r.SkippedCount);
            StringAssert.Contains(r.Why, "another window");

            // an owned window of a third process is not skipped
            r = ZOrderWalker.Decide(new[] { W(0x5001, owner: 0x9999), W(0x2000) }, Se, Sn);
            Assert.IsFalse(r.Ok);

            // Subnautica itself not usable
            Assert.IsFalse(ZOrderWalker.Decide(new[] { W(0x2000, visible: false) }, Se, Sn).Ok);
            Assert.IsFalse(ZOrderWalker.Decide(new[] { W(0x2000, cloaked: true) }, Se, Sn).Ok);
            r = ZOrderWalker.Decide(new[] { W(0x2000, w: 0, hgt: 0) }, Se, Sn);
            Assert.IsFalse(r.Ok);
            StringAssert.Contains(r.Why, "minimised");

            // chain ended, unknown handles
            Assert.IsFalse(ZOrderWalker.Decide(new[] { W(0x4001, visible: false) }, Se, Sn).Ok);
            Assert.IsFalse(ZOrderWalker.Decide(new WindowInfo[0], Se, Sn).Ok);
            Assert.IsFalse(ZOrderWalker.Decide(new[] { W(0x2000) }, IntPtr.Zero, Sn).Ok);
            Assert.IsFalse(ZOrderWalker.Decide(new[] { W(0x2000) }, Se, IntPtr.Zero).Ok);

            // more skipped windows than logged: counted, the log list bounded
            var many = new List<WindowInfo>();
            for (int i = 0; i < 40; i++) many.Add(W(0x6000 + i, visible: false));
            many.Add(W(0x2000));
            r = ZOrderWalker.Decide(many, Se, Sn);
            Assert.IsTrue(r.Ok);
            Assert.AreEqual(40, r.SkippedCount);
            StringAssert.Contains(r.Describe(Sn, false), "…");

            // the live walker refuses an invalid seHwnd without throwing
            r = ZOrderWalker.WalkLive(new IntPtr(0x7FFFFFF0), Sn);
            Assert.IsFalse(r.Ok);
            StringAssert.Contains(r.Why, "seHwnd invalid");
        }

        // ================================================================================================ audio

        [TestMethod]
        public void Audio_VolumesFromTheMachinesFactors_AndTheBridgeRule()
        {
            float g, m, h;
            AudioMath.Volumes(0.8f, 0.5f, 1f, 1f, out g, out m, out h);
            Assert.AreEqual(0.8f, g); Assert.AreEqual(0.5f, m); Assert.AreEqual(0.8f, h);
            // underwater (Wire's AudioMix): game 0.7 x config, music 0, HUD = config game volume
            AudioMath.Volumes(0.8f, 0.5f, (float)AudioMix.SeGameUnderwater, (float)AudioMix.SeMusicUnderwater, out g, out m, out h);
            Assert.AreEqual(0.56f, g, 1e-6f); Assert.AreEqual(0f, m); Assert.AreEqual(0.8f, h);
            // halfway through the 300 ms crossfade
            double half = AudioMix.Level(0.0, true, 0, AudioMix.CrossfadeUs / 2);
            Assert.AreEqual(0.5, half, 1e-9);
            AudioMath.Volumes(1f, 1f, (float)AudioMix.Lerp(1.0, AudioMix.SeGameUnderwater, half), (float)AudioMix.Lerp(1.0, 0.0, half), out g, out m, out h);
            Assert.AreEqual(0.85f, g, 1e-6f); Assert.AreEqual(0.5f, m, 1e-6f);

            // bridge up: the machine's gate decides (every update, idempotently)
            bool pending = false;
            Assert.IsTrue(AudioMath.ShouldWrite(true, true, false, ref pending));
            Assert.IsFalse(AudioMath.ShouldWrite(false, true, true, ref pending), "gate closed: SE's own logic owns the volumes");
            // no bridge: SE is left alone, except to write the config back after a dive
            pending = false;
            Assert.IsFalse(AudioMath.ShouldWrite(true, false, false, ref pending));
            Assert.IsTrue(AudioMath.ShouldWrite(true, false, true, ref pending));
            Assert.IsTrue(pending);
            Assert.IsTrue(AudioMath.ShouldWrite(true, false, false, ref pending), "the restore write");
            Assert.IsFalse(pending);
            Assert.IsFalse(AudioMath.ShouldWrite(true, false, false, ref pending), "then nothing");
        }

        [TestMethod]
        public void AudioController_DecisionTakenOncePerUpdate_RestValuesWithoutTheMachine()
        {
            var a = new AudioController();
            var o = new ViewOutputs { SeAudioWrite = true, SeGameFactor = 0.7f, SeMusicFactor = 0f, WaterModFactor = 0f, SeMixActive = true, MixLevel = 1.0 };
            AudioDecision d = a.Tick(ref o, true, true, 0.9f, 0.6f);
            Assert.IsTrue(d.Write && d.MixActive && d.GateOpen);
            Assert.AreEqual(0.63f, d.Game, 1e-6f); Assert.AreEqual(0f, d.Music); Assert.AreEqual(0.9f, d.Hud);
            Assert.AreEqual(0f, a.WaterScale);
            AudioDecision taken;
            Assert.IsTrue(a.TakeForUpdateSound(out taken));
            Assert.AreEqual(0.63f, taken.Game, 1e-6f);
            Assert.IsFalse(a.TakeForUpdateSound(out taken), "one write per decision");
            a.OnWritten(ref taken);
            Assert.IsTrue(a.HasWritten && a.WrittenGame == taken.Game && a.WrittenMusic == 0f);

            // the machine did not step (not in world): rest values, nothing written, Water Mod untouched
            d = a.Tick(ref o, false, true, 0.9f, 0.6f);
            Assert.IsFalse(d.Write || d.MixActive || d.GateOpen);
            Assert.AreEqual(1f, d.WaterScale);
            Assert.AreEqual(0.9f, d.Game);

            // gate closed (Alt+Tab, pause, GUI, loading): nothing written
            o.SeAudioWrite = false;
            d = a.Tick(ref o, true, true, 0.9f, 0.6f);
            Assert.IsFalse(d.Write);
            Assert.IsFalse(a.TakeForUpdateSound(out taken));
            StringAssert.Contains(a.Describe()["gateOpen"].ToString(), "False");
        }

        /// <summary>
 /// Review fix (finding 10): leaving the world underwater (ESC, then Exit to menu): the pause closed the
 /// gate with the underwater mix still in SE's volumes; out of the world the config values are written back once,
 /// as soon as SE may sound (focused or audible unfocused, not loading), then never again.
 /// </summary>
        [TestMethod]
        public void AudioController_LeavingTheWorldUnderwater_RestoresTheConfigOnce()
        {
            var a = new AudioController();
            var uw = new ViewOutputs { SeAudioWrite = true, SeGameFactor = 0.7f, SeMusicFactor = 0f, WaterModFactor = 0f, SeMixActive = true, MixLevel = 1.0 };
            AudioDecision d = a.Tick(ref uw, true, true, 0.9f, 0.6f);
            Assert.IsTrue(d.Write && d.Music == 0f, "underwater mix written");
            // ESC: paused, the gate closes; nothing written (the mix stays in SE's volumes)
            uw.SeAudioWrite = false;
            d = a.Tick(ref uw, true, true, 0.9f, 0.6f);
            Assert.IsFalse(d.Write);
            // Exit to menu: the machine no longer steps; while loading or unfocused nothing is written
            var rest = new ViewOutputs();
            d = a.Tick(ref rest, false, false, 0.9f, 0.6f, false);
            Assert.IsFalse(d.Write, "not allowed yet (loading or muted unfocused)");
            d = a.Tick(ref rest, false, false, 0.9f, 0.6f, true);
            Assert.IsTrue(d.Write, "the restore write at the menu");
            Assert.AreEqual(0.9f, d.Game);
            Assert.AreEqual(0.6f, d.Music);
            Assert.AreEqual(0.9f, d.Hud);
            d = a.Tick(ref rest, false, false, 0.9f, 0.6f, true);
            Assert.IsFalse(d.Write, "once only: SE owns its volumes again");
            bool pending = true;
            Assert.IsFalse(AudioMath.ShouldRestoreOutsideWorld(false, ref pending), "not allowed: kept pending");
            Assert.IsTrue(pending);
            Assert.IsTrue(AudioMath.ShouldRestoreOutsideWorld(true, ref pending));
            Assert.IsFalse(pending);

            // left the world above water (the mix back at 0 before leaving): nothing to restore
            var b = new AudioController();
            var above = new ViewOutputs { SeAudioWrite = true, SeGameFactor = 1f, SeMusicFactor = 1f, WaterModFactor = 1f, SeMixActive = false, MixLevel = 0.0 };
            b.Tick(ref above, true, true, 0.9f, 0.6f);
            Assert.IsFalse(b.Tick(ref rest, false, false, 0.9f, 0.6f, true).Write);
        }

        // ================================================================================================ probe

        [TestMethod]
        public void Probe_Verdict()
        {
            int v;
            var xs = new[] { 1, 2, 3, 4, 5 };
            var ys = new[] { 1, 2, 3, 4, 5 };
            var magenta = new ProbeSpec { HasExpected = true, R = 255, G = 0, B = 255, Tolerance = 24 };
            uint mag = 0x00FF00FFu, black = 0u, hud = 0x00FFFFFFu;
            string t = RenderDisplay.ProbeVerdict(magenta, xs, ys, new[] { mag, mag, mag, hud, mag }, out v);
            Assert.AreEqual(1, v, t);
            StringAssert.StartsWith(t, "HONOURED");
            t = RenderDisplay.ProbeVerdict(magenta, xs, ys, new[] { black, black, black, black, black }, out v);
            Assert.AreEqual(0, v, t);
            StringAssert.StartsWith(t, "NOT HONOURED");
            t = RenderDisplay.ProbeVerdict(magenta, xs, ys, new[] { mag, mag, black, black, black }, out v);
            Assert.AreEqual(-1, v, t);
            t = RenderDisplay.ProbeVerdict(new ProbeSpec(), xs, ys, new[] { black, black, black, black, black }, out v);
            Assert.AreEqual(-1, v);
            StringAssert.Contains(t, "all black");
            t = RenderDisplay.ProbeVerdict(magenta, xs, ys, new[] { NativeWin.CLR_INVALID, mag, mag, mag, mag }, out v);
            Assert.AreEqual(1, v, t);
            StringAssert.Contains(t, "invalid");
        }

        [TestMethod]
        public void APrime_MenuRule()
        {
            Assert.IsTrue(FallbackAPrime.MenuOpen(true, false, false, false));
            Assert.IsTrue(FallbackAPrime.MenuOpen(false, true, false, false));
            Assert.IsTrue(FallbackAPrime.MenuOpen(false, false, true, false));
            Assert.IsTrue(FallbackAPrime.MenuOpen(false, false, false, true));
            Assert.IsFalse(FallbackAPrime.MenuOpen(false, false, false, false));
            Assert.IsTrue(FallbackAPrime.WantLowered(S2State.Underwater, false, false));
            Assert.IsFalse(FallbackAPrime.WantLowered(S2State.Underwater, false, true), "a menu raises SE");
            Assert.IsFalse(FallbackAPrime.WantLowered(S2State.Prime, false, false));
            Assert.IsTrue(FallbackAPrime.WantLowered(S2State.Space, true, false), "the A' toggle of S2.A'");
            Assert.AreEqual(ViewRequest.Lowered | ViewRequest.SceneSkip, ViewStateAdapter.ForcedRequest(S2State.Underwater, DisplayDesign.APrime, false));
            Assert.AreEqual(0u, ViewStateAdapter.ForcedRequest(S2State.Underwater, DisplayDesign.APrime, true));
            Assert.AreEqual(ViewRequest.Margins | ViewRequest.SceneSkip, ViewStateAdapter.ForcedRequest(S2State.Underwater, DisplayDesign.F, false));
            Assert.AreEqual(ViewRequest.Margins | ViewRequest.AlphaFill, ViewStateAdapter.ForcedRequest(S2State.Prime, DisplayDesign.F, false));
            Assert.AreEqual(0u, ViewStateAdapter.ForcedRequest(S2State.Track, DisplayDesign.F, false));
        }

        [TestMethod]
        public void Adapter_StateNames_AndPeerNumbering()
        {
            Assert.AreEqual(S2State.Space, ViewStateAdapter.StateOf(ViewState.Space, ApproachMode.None));
            Assert.AreEqual(S2State.Track, ViewStateAdapter.StateOf(ViewState.Approach, ApproachMode.Track));
            Assert.AreEqual(S2State.Prime, ViewStateAdapter.StateOf(ViewState.Approach, ApproachMode.Prime));
            Assert.AreEqual(S2State.Underwater, ViewStateAdapter.StateOf(ViewState.Underwater, ApproachMode.Prime));
            Assert.AreEqual(S2State.Surfacing, ViewStateAdapter.StateOf(ViewState.Surfacing, ApproachMode.Prime));
            Assert.AreEqual(PeerState.Alive, ViewStateAdapter.PeerOf(PeerStatus.Alive));
            Assert.AreEqual(PeerState.Stalled, ViewStateAdapter.PeerOf(PeerStatus.Stalled));
            Assert.AreEqual(PeerState.Exited, ViewStateAdapter.PeerOf(PeerStatus.Exited));
            Assert.AreEqual(PeerState.Unknown, ViewStateAdapter.PeerOf(PeerStatus.Unknown));
            Assert.AreEqual(DisplayMode.APrime, ViewStateAdapter.DisplayModeOf(DisplayDesign.APrime));
            Assert.AreEqual(DisplayMode.F, ViewStateAdapter.DisplayModeOf(DisplayDesign.F));
            uint pf, fps;
            ViewStateAdapter.Perf(S2State.Underwater, false, out pf, out fps);
            Assert.AreEqual(PerfMode.Full, pf); Assert.AreEqual(30u, fps);
            ViewStateAdapter.Perf(S2State.Space, true, out pf, out fps);
            Assert.AreEqual(PerfMode.Idle, pf); Assert.AreEqual(10u, fps);
            StringAssert.Contains(ViewStateAdapter.BlockedNames(ViewBlock.ZOrder | ViewBlock.Render), "zOrder");
            Assert.AreEqual("none", ViewStateAdapter.BlockedNames(0));
        }
    }
}
