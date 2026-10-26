using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S2 Unity-free parts of the Subnautica plugin (ROADMAP S2 SN plugin; ARCHITECTURE §4, §5.1-§5.3, §5.13; CONTRACT §1,
 /// §5.4 C5/C7): the recipe chosen from C5 (Idle / Track / Full, no SE, SE stalled, Unfocused, S2.M6, S2.M14), the
 /// M12, the snPhase mapping
 /// (ReadyIdle / WarmingUp / Active), the mute rule and the crossfade (S2.M3), the rectangle comparison and the
 /// correction budget, the window style bits, the biome tile test, the calibration colour parser, the switches and
 /// the frame pacing used by S2.N3/S2.N8.
 /// </summary>
    [TestClass]
    public class S2Tests
    {
        /// <summary>UWE.FreezeTime for one id, as the decomp has it: Begin adds the id once, End removes it.</summary>
        private sealed class FakeFreeze : IFreezeApi
        {
            public bool Held { get; set; }
            public int BeginCalls, EndCalls;
            public void Begin() { BeginCalls++; Held = true; }
            public void End() { EndCalls++; Held = false; }
        }

        private static RecipeInputs Live(uint perfMode, uint targetFps = 0, bool fg = true)
        {
            return new RecipeInputs
            {
                C5Valid = true, PeerAlive = true, PerfMode = perfMode, TargetFps = targetFps, SeForeground = fg,
                UserVSync = 1, UserTargetFps = 144,
            };
        }

        // ------------------------------------------------------------------------------------- recipes

        [TestMethod]
        public void S2Recipe_FromC5_EveryPerfMode_NoSe_Stalled_Unfocused()
        {
            var none = new RecipeInputs { UserVSync = 1, UserTargetFps = 144 };
            RecipeDecision d = PerfRecipeRules.Select(ref none);
            Assert.AreEqual(PerfMode.Idle, d.Recipe, "no C5 yet (ReadyIdle before SE): Idle");
            Assert.AreEqual(0, d.VSyncCount); Assert.AreEqual(10, d.TargetFrameRate);
            Assert.IsFalse(d.CamerasOn); Assert.IsTrue(d.BelowNormal); Assert.IsTrue(d.IdleCause);

            RecipeInputs stalled = Live(PerfMode.Full);
            stalled.PeerAlive = false;
            d = PerfRecipeRules.Select(ref stalled);
            Assert.AreEqual(PerfMode.Idle, d.Recipe, "SE stalled: Idle whatever C5 last said (ARCHITECTURE §5.14)");
            StringAssert.Contains(d.Why, "SE not alive");

            RecipeInputs i = Live(PerfMode.Idle, 10);
            d = PerfRecipeRules.Select(ref i);
            Assert.AreEqual("(0, 10)", d.Pair); Assert.IsFalse(d.CamerasOn); Assert.IsTrue(d.BelowNormal); Assert.IsTrue(d.IdleCause);

            i = Live(PerfMode.Track, 10);
            d = PerfRecipeRules.Select(ref i);
            Assert.AreEqual(PerfMode.Track, d.Recipe);
            Assert.AreEqual("(0, 10)", d.Pair, "Track (0, 10)");
            Assert.IsFalse(d.CamerasOn, "Track: cameras off");
            Assert.IsFalse(d.BelowNormal, "Track: Normal priority");
            Assert.IsFalse(d.IdleCause, "Track: unfrozen");

            i = Live(PerfMode.Full, 0);
            d = PerfRecipeRules.Select(ref i);
            Assert.AreEqual(PerfMode.Full, d.Recipe);
            Assert.AreEqual("(1, -1)", d.Pair, "Full, targetFps 0: vsync at the monitor rate (start with (1, -1))");
            Assert.IsTrue(d.CamerasOn); Assert.IsFalse(d.BelowNormal); Assert.IsFalse(d.IdleCause);

            i = Live(PerfMode.Full, 120);
            Assert.AreEqual("(0, 120)", PerfRecipeRules.Select(ref i).Pair, "Full, CPU-bound variant (0, 120)");
            i = Live(PerfMode.Full, 100000);
            Assert.AreEqual("(0, 500)", PerfRecipeRules.Select(ref i).Pair, "absurd targetFps clamped");

            // S2 policy, the fallback (Waterline): the scene is skipped (C5 seeThrough) exactly Underwater
            i = Live(PerfMode.Full, 0, fg: false);
            i.ViewState = ViewState.Underwater;
            i.SeeThrough = true;
            d = PerfRecipeRules.Select(ref i);
            Assert.AreEqual("(0, 30)", d.Pair, "Unfocused overlay underwater: (0, 30)");
            Assert.IsTrue(d.CamerasOn);
            i.TargetFps = 30;
            Assert.AreEqual("(0, 30)", PerfRecipeRules.Select(ref i).Pair, "the S2 SE machine's targetFps 30 gives the same pair");
            i = Live(PerfMode.Full, 0, fg: false);
            i.ViewState = ViewState.Approach; i.ApproachMode = ApproachMode.Prime;
            Assert.AreEqual("(1, -1)", PerfRecipeRules.Select(ref i).Pair, "unfocused in Prime: Full as usual (Waterline: no see-through in Prime)");
            i = Live(PerfMode.Track, 10, fg: false);
            Assert.AreEqual("(0, 10)", PerfRecipeRules.Select(ref i).Pair, "Unfocused changes only Full");

            // S7a Site: the (0, 30) rule follows C5 seeThrough (Subnautica's picture shown) in any view: Approach under the
 // picture latch included; Prime without the latch (Subnautica warming, SE's picture) stays Full as usual
            i = Live(PerfMode.Full, 0, fg: false);
            i.ViewState = ViewState.Approach; i.ApproachMode = ApproachMode.Track; i.SeeThrough = true;
            Assert.AreEqual("(0, 30)", PerfRecipeRules.Select(ref i).Pair, "site: unfocused with the picture shown in Track");
            StringAssert.Contains(PerfRecipeRules.Select(ref i).Why, "seeThrough");
            i.SeeThrough = false;
            Assert.AreEqual("(1, -1)", PerfRecipeRules.Select(ref i).Pair, "site: latch off (SE's picture): Full as usual");
            i.SeForeground = true; i.SeeThrough = true;
            Assert.AreEqual("(1, -1)", PerfRecipeRules.Select(ref i).Pair, "site: focused: Full as usual");

            i = Live(7);
            d = PerfRecipeRules.Select(ref i);
            Assert.AreEqual(PerfMode.Idle, d.Recipe, "unknown perfMode: Idle");
            StringAssert.Contains(d.Why, "unknown C5 perfMode 7");
        }

        [TestMethod]
        public void S2Recipe_SeededFailures_M6IdleRecipeOff_M14VSyncLeftInIdle()
        {
            RecipeInputs i = Live(PerfMode.Idle, 10);
            i.IdleRecipeOff = true;
            RecipeDecision d = PerfRecipeRules.Select(ref i);
            Assert.AreEqual("(1, 144)", d.Pair, "S2.M6: the user's pair stays (S2.N3 frame-rate check red)");
            Assert.IsTrue(d.CamerasOn, "S2.M6: cameras stay on");
            Assert.IsFalse(d.BelowNormal);
            Assert.IsFalse(d.IdleCause, "S2.M6: Idle is no freeze cause");
            StringAssert.Contains(d.Why, "S2.M6");

            i = Live(PerfMode.Idle, 10);
            i.VSyncLeftInIdle = true;
            d = PerfRecipeRules.Select(ref i);
            Assert.AreEqual(1, d.VSyncCount, "S2.M14: vSyncCount 1 in Idle (Unity ignores targetFrameRate 10: S2.N3 red)");
            Assert.AreEqual(10, d.TargetFrameRate);
            Assert.IsTrue(d.IdleCause, "S2.M14 changes only the pair");

            i = Live(PerfMode.Full, 0);
            i.IdleRecipeOff = true; i.VSyncLeftInIdle = true;
            Assert.AreEqual("(1, -1)", PerfRecipeRules.Select(ref i).Pair, "the Idle mutants never touch Full");
        }

        [TestMethod]
        public void S2Recipe_ExpectedPerfModeOfTheViewState()
        {
            // S2 policy, the fallback (C5 bit 9 clear): Track in Approach/Track
            Assert.AreEqual(PerfMode.Idle, PerfRecipeRules.ExpectedPerfMode(ViewState.Space, ApproachMode.None, false));
            Assert.AreEqual(PerfMode.Track, PerfRecipeRules.ExpectedPerfMode(ViewState.Approach, ApproachMode.Track, false));
            Assert.AreEqual(PerfMode.Full, PerfRecipeRules.ExpectedPerfMode(ViewState.Approach, ApproachMode.Prime, false));
            Assert.AreEqual(PerfMode.Full, PerfRecipeRules.ExpectedPerfMode(ViewState.Underwater, ApproachMode.None, false));
            Assert.AreEqual(PerfMode.Full, PerfRecipeRules.ExpectedPerfMode(ViewState.Surfacing, ApproachMode.Prime, false));
            Assert.AreEqual("Approach/Prime", PerfRecipeRules.ViewName(ViewState.Approach, ApproachMode.Prime));

            // S7a Site (C5 bit 9 set): Idle in Space, Full in every other state (Subnautica renders whenever its picture can
 // be shown, so the band swap never waits for a recipe); the same table as Wire's ViewStateMachine.PerfFor
            Assert.AreEqual(PerfMode.Idle, PerfRecipeRules.ExpectedPerfMode(ViewState.Space, ApproachMode.None, true));
            Assert.AreEqual(PerfMode.Full, PerfRecipeRules.ExpectedPerfMode(ViewState.Approach, ApproachMode.Track, true));
            Assert.AreEqual(PerfMode.Full, PerfRecipeRules.ExpectedPerfMode(ViewState.Approach, ApproachMode.Prime, true));
            Assert.AreEqual(PerfMode.Full, PerfRecipeRules.ExpectedPerfMode(ViewState.Underwater, ApproachMode.None, true));
            Assert.AreEqual(PerfMode.Full, PerfRecipeRules.ExpectedPerfMode(ViewState.Surfacing, ApproachMode.None, true));
            for (uint v = ViewState.Space; v <= ViewState.Surfacing; v++)
                foreach (uint a in new[] { ApproachMode.None, ApproachMode.Track, ApproachMode.Prime })
                {
                    if (v == ViewState.Approach && a == ApproachMode.None) continue;
                    uint c5a = v == ViewState.Approach ? a : ApproachMode.None;
                    Assert.AreEqual(ViewStateMachine.PerfFor(v, a, ViewPicture.Site), PerfRecipeRules.ExpectedPerfMode(v, c5a, true), "site " + v + "/" + a);
                    Assert.AreEqual(ViewStateMachine.PerfFor(v, a, ViewPicture.Waterline), PerfRecipeRules.ExpectedPerfMode(v, c5a, false), "waterline " + v + "/" + a);
                }
        }

        [TestMethod]
        public void S2Freeze_TruthTable_OneFeedbackPanelPair()
        {
            // every combination of (Idle, Paused, SE stalled), visited in Gray-code order and back to all-false
            bool[][] seq =
            {
                new[] { false, false, false }, new[] { true, false, false }, new[] { true, true, false }, new[] { false, true, false },
                new[] { false, true, true }, new[] { true, true, true }, new[] { true, false, true }, new[] { false, false, true },
                new[] { false, false, false },
            };
            var api = new FakeFreeze();
            var owner = new FreezeOwnerCore(api);
            bool prevWant = false;
            int rises = 0, falls = 0;
            foreach (bool[] c in seq)
            {
                owner.Update(c[0], c[1], c[2]);
                bool want = c[0] || c[1] || c[2];
                Assert.AreEqual(want, api.Held, "frozen = Idle ∨ Paused ∨ SE stalled for " + string.Join(",", Array.ConvertAll(c, x => x ? "1" : "0")));
                Assert.AreEqual(want, owner.Wanted);
                if (want && !prevWant) rises++;
                if (!want && prevWant) falls++;
                prevWant = want;
            }
            Assert.AreEqual(rises, api.BeginCalls, "Begin only when the whole condition becomes true");
            Assert.AreEqual(falls, api.EndCalls, "End only when the whole condition becomes false");
            Assert.AreEqual(1, rises);
            Assert.AreEqual(0, owner.Reasserts);

            // a frame with no change makes no call
            int calls = api.BeginCalls + api.EndCalls;
            Assert.IsFalse(owner.Update(false, false, false));
            Assert.AreEqual(calls, api.BeginCalls + api.EndCalls);
        }

        [TestMethod]
        public void S2Freeze_EscAndResumeInSpace_SingleOwnerStaysFrozen_MutantS2M12Unfreezes()
        {
            // S2.T-A1 step: in Space (Idle), ESC (Paused), resume: Subnautica must stay frozen
            var api = new FakeFreeze();
            var owner = new FreezeOwnerCore(api);
            owner.Update(true, false, false);
            owner.Update(true, true, false);
            owner.Update(true, false, false);
            Assert.IsTrue(api.Held, "single owner: still frozen in Space after ESC and resume");
            Assert.AreEqual(1, api.BeginCalls);
            Assert.AreEqual(0, api.EndCalls);

            var apiM = new FakeFreeze();
            var mutant = new FreezeOwnerCore(apiM) { PerCauseMutant = true };
            mutant.Update(true, false, false);
            mutant.Update(true, true, false);
            Assert.IsTrue(apiM.Held);
            mutant.Update(true, false, false);
            Assert.IsFalse(apiM.Held, "S2.M12 (one pair per cause): End(FeedbackPanel) for Paused unfreezes although Idle holds -> S2.T-A1 red");
            StringAssert.Contains(mutant.LastReason, "S2.M12");

            // the same with a stall ending during Idle
            var api2 = new FakeFreeze();
            var single2 = new FreezeOwnerCore(api2);
            var apiM2 = new FakeFreeze();
            var mutant2 = new FreezeOwnerCore(apiM2) { PerCauseMutant = true };
            foreach (bool[] c in new[] { new[] { true, false, false }, new[] { true, false, true }, new[] { true, false, false } })
            {
                single2.Update(c[0], c[1], c[2]);
                mutant2.Update(c[0], c[1], c[2]);
            }
            Assert.IsTrue(api2.Held);
            Assert.IsFalse(apiM2.Held, "S2.M12 red on a stall ending in Space too");
        }

        [TestMethod]
        public void S2Freeze_EntryRemovedOutside_IsBegunAgain_ReleaseEnds()
        {
            var api = new FakeFreeze();
            var owner = new FreezeOwnerCore(api);
            owner.Update(true, false, false);
            api.Held = false; // someone ended FreezeTime(FeedbackPanel)
            Assert.IsTrue(owner.Update(true, false, false), "a call was made");
            Assert.IsTrue(api.Held, "begun again");
            Assert.AreEqual(1, owner.Reasserts);
            Assert.IsTrue(owner.Release(), "disposal ends our entry");
            Assert.IsFalse(api.Held);
            Assert.IsFalse(owner.Release(), "nothing left to release");

            var api2 = new FakeFreeze();
            var m = new FreezeOwnerCore(api2) { PerCauseMutant = true };
            m.Update(true, false, false);
            api2.Held = false;
            m.Update(true, false, false);
            Assert.IsFalse(api2.Held, "the mutant never re-asserts (it must be able to fail)");
            Assert.AreEqual("Idle+SEStalled", FreezeOwnerCore.Causes(true, false, true));
            Assert.AreEqual("none", FreezeOwnerCore.Causes(false, false, false));
        }

        // ------------------------------------------------------------------------------------- phases

        [TestMethod]
        public void S2Phase_ReadyIdleForSpace_WarmingUpForTrack_WarmingUpThenActiveForPrimeUnderwaterSurfacing()
        {
            uint R = SnPhase.ReadyIdle, W = SnPhase.WarmingUp, A = SnPhase.Active;
            Assert.IsFalse(PhaseRules.WarmWanted(true, true, ViewState.Space, ApproachMode.None));
            // CONTRACT §5.4 table: Approach/Track → WarmingUp (the Track recipe never counts as Full)
            Assert.IsTrue(PhaseRules.WarmWanted(true, true, ViewState.Approach, ApproachMode.Track), "Track asks for more than Idle");
            Assert.AreEqual(W, PhaseRules.Target(R, true, false, true, false, 0), "Track (Track recipe applied, never Full): WarmingUp");
            Assert.AreEqual(W, PhaseRules.Target(A, true, false, true, false, 50), "Prime → Track: Full no longer applied: WarmingUp");
            Assert.IsTrue(PhaseRules.WarmWanted(true, true, ViewState.Approach, ApproachMode.Prime));
            Assert.IsTrue(PhaseRules.WarmWanted(true, true, ViewState.Underwater, ApproachMode.None));
            Assert.IsTrue(PhaseRules.WarmWanted(true, true, ViewState.Surfacing, ApproachMode.None));
            Assert.IsFalse(PhaseRules.WarmWanted(false, true, ViewState.Underwater, ApproachMode.None), "no C5 of this SE");
            Assert.IsFalse(PhaseRules.WarmWanted(true, false, ViewState.Underwater, ApproachMode.None), "SE not alive");

            Assert.AreEqual(R, PhaseRules.Target(R, true, false, false, true, 100), "Space with Full (an S1 SE): ReadyIdle");
            Assert.AreEqual(W, PhaseRules.Target(R, true, false, true, false, 0), "Prime before Full is applied");
            Assert.AreEqual(W, PhaseRules.Target(W, true, false, true, true, 1), "one presented frame is not warm yet");
            Assert.AreEqual(A, PhaseRules.Target(W, true, false, true, true, PhaseRules.WarmFrames), "warm");
            Assert.AreEqual(A, PhaseRules.Target(R, true, false, true, true, 5), "straight to Active when frames were already presented");
            Assert.AreEqual(A, PhaseRules.Target(A, true, false, true, true, 0), "Active kept while Full stays applied (no flapping)");
            Assert.AreEqual(W, PhaseRules.Target(A, true, false, true, false, 0), "Full lost: WarmingUp");
            Assert.AreEqual(R, PhaseRules.Target(A, true, false, false, true, 9), "back to Space: ReadyIdle");

            Assert.AreEqual(SnPhase.Quitting, PhaseRules.Target(SnPhase.Quitting, true, true, true, true, 9), "Quitting is never changed");
            Assert.AreEqual(A, PhaseRules.Target(A, true, true, false, false, 0), "quitting freezes the phase until the bridge sets Quitting");
            Assert.AreEqual(SnPhase.LoadingSave, PhaseRules.Target(SnPhase.LoadingSave, true, false, true, true, 9), "S1's phases are S1's");
            Assert.AreEqual(SnPhase.Menu, PhaseRules.Target(SnPhase.Menu, false, false, true, true, 9));
            Assert.AreEqual(R, PhaseRules.Target(R, false, false, true, true, 9), "not in the host world: unchanged");
        }

        [TestMethod]
        public void S2StreamingSettled_NeedsTheProbesEdgeAfterAJump()
        {
            Assert.IsTrue(SettleRules.Report(true, false, false), "no jump probe running: the raw IsWorldSettled");
            Assert.IsFalse(SettleRules.Report(false, false, false));
            Assert.IsFalse(SettleRules.Report(true, true, false), "right after a jump: the stale idle is not reported");
            Assert.IsTrue(SettleRules.Report(true, true, true), "busy -> idle edge (or 1 s idle) seen by the probe");
            Assert.IsFalse(SettleRules.Report(false, true, true), "busy again");
        }

        // ------------------------------------------------------------------------------------- audio

        [TestMethod]
        public void S2Mix_MuteRule_EffectiveTarget_SeededFailureM3()
        {
            string why;
            Assert.IsTrue(MixRules.ShouldMute(false, true, false, 1f, 1f, out why) && why == "no live SE");
            Assert.IsTrue(MixRules.ShouldMute(true, false, false, 1f, 1f, out why) && why == "SE not foreground", "muted when C5 says not foreground");
            Assert.IsTrue(MixRules.ShouldMute(true, true, true, 1f, 1f, out why) && why == "quitting");
            Assert.IsTrue(MixRules.ShouldMute(true, true, false, 0f, 0f, out why), "above water (target 0, faded out): muted");
            Assert.IsFalse(MixRules.ShouldMute(true, true, false, 0.4f, 0f, out why), "fading out above water: audible until 0 (crossfade)");
            Assert.IsFalse(MixRules.ShouldMute(true, true, false, 0f, 1f, out why), "fading in");
            Assert.IsFalse(MixRules.ShouldMute(true, true, false, 1f, 1f, out why), "underwater");

            Assert.AreEqual(0f, MixRules.EffectiveTarget(true, 0f, false));
            Assert.AreEqual(1f, MixRules.EffectiveTarget(true, 0f, true), "S2.M3: target 0 above water taken as 1 (S2.T-A1 peak > 0 above water)");
            Assert.AreEqual(1f, MixRules.EffectiveTarget(true, 3f, false), "clamped to 1");
            Assert.AreEqual(0f, MixRules.EffectiveTarget(true, float.NaN, false));
            Assert.AreEqual(0f, MixRules.EffectiveTarget(false, 1f, true), "no live SE: silent even under S2.M3");
            Assert.IsFalse(MixRules.ShouldMute(true, true, false, 1f, MixRules.EffectiveTarget(true, 0f, true), out why), "S2.M3: never muted above water");

            Assert.AreEqual(300f, MixRules.CrossfadeMs(300f));
            Assert.AreEqual(300f, MixRules.CrossfadeMs(0f), "0: the default 300 ms");
            Assert.AreEqual(300f, MixRules.CrossfadeMs(float.NaN));
            Assert.AreEqual(300f, MixRules.CrossfadeMs(1e6f));
            Assert.AreEqual(150f, MixRules.CrossfadeMs(150f));
        }

        [TestMethod]
        public void S2Mix_Crossfade_Linear300ms_TurnsAroundInPlace()
        {
            var f = new Crossfade(0f);
            Assert.AreEqual(0f, f.Step(1f, 300f, 1000));
            Assert.AreEqual(0.5f, f.Step(1f, 300f, 1150), 1e-6f, "half way after 150 ms");
            Assert.IsTrue(f.Fading);
            Assert.AreEqual(1f, f.Step(1f, 300f, 1300), 1e-6f, "full after 300 ms");
            Assert.AreEqual(1f, f.Step(1f, 300f, 2000), "held");
            Assert.IsFalse(f.Fading);
            Assert.AreEqual(1, f.Fades);
            Assert.AreEqual(0.5f, f.Step(0f, 300f, 2150), 1e-6f, "fade out at the same rate");
            Assert.AreEqual(0.75f, f.Step(1f, 300f, 2225), 1e-6f, "an interrupted fade turns around from where it is");
            Assert.AreEqual(1f, f.Step(1f, 300f, 2400));
            Assert.AreEqual(3, f.Fades);
            f.Set(0f, 3000);
            Assert.AreEqual(0f, f.Value);
            Assert.AreEqual(0f, f.Step(0f, 300f, 3100), "set jumps without a fade");
        }

        // ------------------------------------------------------------------------------------- rectangles

        [TestMethod]
        public void S2Rect_Comparison_Verdicts_And_CorrectionBudget()
        {
            var primary = new RectI(0, 0, 2560, 1440);
            var second = new RectI(2560, 0, 4480, 1080);
            Assert.AreEqual(RectVerdict.Ok, RectRules.Compare(primary, primary, primary, primary), "same rectangle: rectOk");
            Assert.AreEqual(RectVerdict.NoSeRect, RectRules.Compare(primary, default(RectI), primary, default(RectI)), "no C5 seRect yet");
            Assert.AreEqual(RectVerdict.NoSnRect, RectRules.Compare(default(RectI), primary, primary, primary));
            Assert.AreEqual(RectVerdict.CorrectSize, RectRules.Compare(new RectI(0, 0, 1920, 1080), primary, primary, primary),
                            "same monitor, wrong size: Screen.SetResolution(2560, 1440, FullScreenWindow)");
            Assert.AreEqual(RectVerdict.OtherMonitor, RectRules.Compare(second, primary, second, primary), "on the 1920x1080 monitor: not correctable");
            Assert.AreEqual(RectVerdict.SeNotMonitorSized, RectRules.Compare(primary, new RectI(100, 100, 1380, 820), primary, primary), "SE windowed");
            Assert.AreEqual(RectVerdict.CorrectSize, RectRules.Compare(new RectI(0, 0, 2560, 1439), primary, primary, primary), "one pixel off");

            I32x4 wire = new RectI(1, 2, 3, 4).ToI32x4();
            Assert.AreEqual(1, wire.x); Assert.AreEqual(2, wire.y); Assert.AreEqual(3, wire.z); Assert.AreEqual(4, wire.w);
            Assert.AreEqual(new RectI(1, 2, 3, 4), RectI.From(wire), "C5/C7 i32[4] = (left, top, right, bottom)");
            Assert.AreEqual(2560, primary.W); Assert.AreEqual(1440, primary.H);
            Assert.IsTrue(new RectI(5, 5, 5, 9).Empty);

            var c = new RectCorrector();
            Assert.IsFalse(c.ShouldAttempt(RectVerdict.OtherMonitor, true, 0), "only a size mismatch is corrected");
            Assert.IsFalse(c.ShouldAttempt(RectVerdict.CorrectSize, false, 0), "switch off");
            Assert.IsTrue(c.ShouldAttempt(RectVerdict.CorrectSize, true, 1000));
            c.Note(1000);
            Assert.IsFalse(c.ShouldAttempt(RectVerdict.CorrectSize, true, 1000 + RectCorrector.MinSpacingMs - 1), "5 s apart");
            Assert.IsTrue(c.ShouldAttempt(RectVerdict.CorrectSize, true, 1000 + RectCorrector.MinSpacingMs));
            c.Note(7000);
            c.Note(13000);
            Assert.IsTrue(c.Exhausted);
            Assert.IsFalse(c.ShouldAttempt(RectVerdict.CorrectSize, true, 100000), "at most 3 attempts");
        }

        // ------------------------------------------------------------------------------------- styles, biome, calib, switches

        [TestMethod]
        public void S2WindowStyles_Biome_FramePacing()
        {
            long unity = 0x00040000; // WS_EX_APPWINDOW as a fullscreen-window player may carry it
            Assert.IsFalse(WindowStyleRules.HasRequired(unity));
            long styled = WindowStyleRules.WithRequired(unity);
            Assert.IsTrue(WindowStyleRules.HasRequired(styled), WindowStyleRules.Describe(styled));
            Assert.AreEqual(0x08000080L, styled, "NOACTIVATE | TOOLWINDOW, APPWINDOW cleared");
            Assert.IsFalse(WindowStyleRules.HasRequired(0x08000080L | 0x00040000L), "APPWINDOW would keep the taskbar button");
            Assert.IsFalse(WindowStyleRules.HasRequired(0x00000080L), "TOOLWINDOW alone");
            Assert.AreEqual(0x08000088L, WindowStyleRules.WithRequired(0x8), "other bits kept");

            Assert.IsFalse(BiomeRules.OutsideTile(0, 0));
            Assert.IsFalse(BiomeRules.OutsideTile(-1029, -1116), "WM-2 point inside (grandReef)");
            Assert.IsFalse(BiomeRules.OutsideTile(2047.99, -2048), "[-2048, 2048) is inside");
            Assert.IsTrue(BiomeRules.OutsideTile(2048, 0), "x = +2048 is the first column past the map (wraps)");
            Assert.IsTrue(BiomeRules.OutsideTile(2500, -150), "S2.N6 point x = +2,500 m");
            Assert.IsTrue(BiomeRules.OutsideTile(0, 2600), "north: null unpatched");
            Assert.IsTrue(BiomeRules.OutsideTile(0, -2048.01), "south");
            Assert.IsTrue(BiomeRules.OutsideTile(-7799, 0), "OUTER VOID west");
            Assert.IsFalse(BiomeRules.OutsideTile(double.NaN, 0));
            Assert.AreEqual("void", BiomeRules.VoidBiome);
        }

        /// <summary>
 /// Review fixes: the restyle never hides the window while SE shows Subnautica (finding 3); the void
 /// ghost leviathan spawner is skipped outside the tile unless switched on (finding 9); the de.biome query
 /// arguments (finding 8).
 /// </summary>
        [TestMethod]
        public void ReviewFixes_HideRule_VoidSpawner_BiomeQuery()
        {
            Assert.IsFalse(WindowStyleRules.SeShowsSubnautica(false, ViewState.Underwater, SeStateFlags.MarginsOn), "no C5 of a live SE: ReadyIdle");
            Assert.IsFalse(WindowStyleRules.SeShowsSubnautica(true, ViewState.Space, 0), "Space");
            Assert.IsTrue(WindowStyleRules.SeShowsSubnautica(true, ViewState.Approach, 0), "Track/Prime");
            Assert.IsTrue(WindowStyleRules.SeShowsSubnautica(true, ViewState.Underwater, 0));
            Assert.IsTrue(WindowStyleRules.SeShowsSubnautica(true, ViewState.Surfacing, 0));
            Assert.IsTrue(WindowStyleRules.SeShowsSubnautica(true, ViewState.Space, SeStateFlags.MarginsOn), "margins still on (leaving)");
            Assert.IsTrue(WindowStyleRules.SeShowsSubnautica(true, ViewState.Space, SeStateFlags.SeeThrough), "forced see-through / A' lowered");

            Assert.IsTrue(BiomeRules.SuppressVoidSpawner(false, 2500, -50), "OUTER VOID: empty water");
            Assert.IsFalse(BiomeRules.SuppressVoidSpawner(true, 2500, -50), "switch on: vanilla");
            Assert.IsFalse(BiomeRules.SuppressVoidSpawner(false, -1029, -1116), "inside the tile: vanilla");
            Assert.IsFalse(BiomeRules.SuppressVoidSpawner(false, double.NaN, 0));

            double x, y, z;
            string e;
            Assert.IsTrue(BiomeRules.TryParseQuery(Json.Parse("{\"x\": 2500, \"y\": -50, \"z\": 0}"), out x, out y, out z, out e) && x == 2500 && y == -50 && z == 0, e);
            Assert.IsTrue(BiomeRules.TryParseQuery(Json.Parse("{\"argv\": [\"2500\", \"-50\", \"0\"]}"), out x, out y, out z, out e) && x == 2500 && y == -50, e);
            Assert.IsFalse(BiomeRules.TryParseQuery(Json.Parse("{\"x\": 2500}"), out x, out y, out z, out e));
            Assert.IsNotNull(e);
            Assert.IsFalse(BiomeRules.TryParseQuery(Json.Parse("{\"x\": \"a\", \"y\": 0, \"z\": 0}"), out x, out y, out z, out e));
        }

        [TestMethod]
        public void S2FramePacing()
        {

            var p = new FramePacing();
            p.Frame(1000);
            for (ulong t = 1100; t <= 2000; t += 100) p.Frame(t);   // 10 fps idle
            p.Roll(2000);
            Assert.AreEqual(10f, p.LastFps, 0.01f, "10 frames in one second");
            Assert.AreEqual(100UL, p.LastMaxGapMs);
            Assert.AreEqual(100UL, p.LastP99Ms, "S7a: p99 frame gap of the window");
            p.Frame(3500); // a 1.5 s stall
            p.Roll(3500);
            Assert.AreEqual(1500UL, p.LastMaxGapMs);
            Assert.AreEqual(1, p.GapsOver1s, "S2.N8: a heartbeat gap > 1 s is counted");

            // S7a (s7a.picture frameMsP99, nearest rank ceil(0.99 n)): 99 gaps of 10 ms and one of 50 ms: the 99th smallest
 // (10 ms); 196 of 7 ms, 2 of 16 ms, 40 and 41 ms: the 198th smallest (16 ms)
            var q = new FramePacing();
            ulong ms = 10000;
            q.Frame(ms);
            for (int k = 0; k < 99; k++) q.Frame(ms += 10);
            q.Frame(ms += 50);
            q.Roll(ms);
            Assert.AreEqual(10UL, q.LastP99Ms);
            for (int k = 0; k < 198; k++) q.Frame(ms += k < 196 ? 7UL : 16UL);
            q.Frame(ms += 40);
            q.Frame(ms += 41);
            q.Roll(ms);
            Assert.AreEqual(16UL, q.LastP99Ms, "200 gaps: p99 is the 198th smallest");
            q.Roll(ms + 1000);
            Assert.AreEqual(0UL, q.LastP99Ms, "no frames: 0");
        }

        [TestMethod]
        public void S2Calib_ColourArguments()
        {
            bool off;
            byte r, g, b;
            string e;
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"colour\": \"#ff00ff\"}"), out off, out r, out g, out b, out e) && !off && r == 255 && g == 0 && b == 255, e);
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"color\": \"00ff7F\"}"), out off, out r, out g, out b, out e) && r == 0 && g == 255 && b == 127, e);
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"colour\": [12, 34, 56]}"), out off, out r, out g, out b, out e) && r == 12 && g == 34 && b == 56, e);
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"colour\": \"12, 34,56\"}"), out off, out r, out g, out b, out e) && r == 12 && g == 34 && b == 56, e);
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"colour\": \"magenta\"}"), out off, out r, out g, out b, out e) && r == 255 && g == 0 && b == 255, e);
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"colour\": \"off\"}"), out off, out r, out g, out b, out e) && off, e);
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"argv\": [\"OFF\"]}"), out off, out r, out g, out b, out e) && off, "SE chat relay");
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"argv\": [\"255\", \"0\", \"255\"]}"), out off, out r, out g, out b, out e) && r == 255 && b == 255 && !off, e);
            Assert.IsTrue(CalibRules.TryParse(Json.Parse("{\"argv\": [\"0000ff\"]}"), out off, out r, out g, out b, out e) && b == 255 && r == 0, e);
            foreach (string bad in new[] { "{}", "{\"colour\": \"purple-ish\"}", "{\"colour\": [1, 2]}", "{\"colour\": [1, 2, 300]}", "{\"colour\": \"#12345\"}",
                                           "{\"colour\": [1.5, 2, 3]}", "{\"colour\": 7}" })
                Assert.IsFalse(CalibRules.TryParse(Json.Parse(bad), out off, out r, out g, out b, out e), "refused: " + bad);
            Assert.AreEqual("#ff007f", CalibRules.Hex(255, 0, 127));
        }

        [TestMethod]
        public void S2TestC5_Override_Parsing_And_Defaults_LikeTheSeMachine()
        {
            bool off;
            SeState st;
            string d, e;
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Underwater\"}"), out off, out st, out d, out e), e);
            Assert.IsFalse(off);
            Assert.AreEqual(ViewState.Underwater, st.viewState);
            Assert.AreEqual(ApproachMode.None, st.approachMode, "CONTRACT §5.4: approachMode 0 outside Approach (as the integrated SE writes C5)");
            Assert.AreEqual(PerfMode.Full, st.perfMode);
            Assert.AreEqual(0u, st.targetFps);
            Assert.AreEqual(1f, st.snBusTarget, "underwater and foreground: bus 1");
            Assert.AreEqual(300f, st.crossfadeMs);
            Assert.AreNotEqual(0u, st.flags & SeStateFlags.SeForeground);

            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"underwater\", \"foreground\": \"false\"}"), out off, out st, out d, out e), e);
            Assert.AreEqual(30u, st.targetFps, "underwater unfocused: 30");
            Assert.AreEqual(0f, st.snBusTarget);
            Assert.AreNotEqual(0u, st.overlays & Overlays.Unfocused);
            Assert.AreEqual(0u, st.flags & SeStateFlags.SeForeground);

            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Space\", \"paused\": true}"), out off, out st, out d, out e), e);
            Assert.AreEqual(PerfMode.Idle, st.perfMode);
            Assert.AreEqual(10u, st.targetFps);
            Assert.AreNotEqual(0u, st.flags & SeStateFlags.SePaused);
            Assert.AreNotEqual(0u, st.overlays & Overlays.Paused);

            // S2 policy, the fallback: Track is the Track recipe
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Track\", \"picture\": \"waterline\"}"), out off, out st, out d, out e), e);
            Assert.AreEqual(PerfMode.Track, st.perfMode);
            Assert.AreEqual(ApproachMode.Track, st.approachMode);
            Assert.AreEqual(0u, st.flags & (SeStateFlags.MarginsOn | SeStateFlags.SeeThrough | S7aConventions.C5FlagsExt.PictureSite), "waterline Track: SE's picture");
            // S7a Site (the default, as SE's machine): Track is Full under the picture latch, C5 bit 9 set
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Track\"}"), out off, out st, out d, out e), e);
            Assert.AreEqual(PerfMode.Full, st.perfMode, "site Track: Full");
            Assert.AreEqual(0u, st.targetFps);
            Assert.AreEqual(SeStateFlags.MarginsOn | SeStateFlags.SeeThrough | S7aConventions.C5FlagsExt.PictureSite,
                            st.flags & (SeStateFlags.MarginsOn | SeStateFlags.SeeThrough | S7aConventions.C5FlagsExt.PictureSite));
            StringAssert.Contains(d, "picture=site");
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Track\", \"foreground\": false}"), out off, out st, out d, out e), e);
            Assert.AreEqual(30u, st.targetFps, "site Track unfocused with the picture: 30");
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Space\"}"), out off, out st, out d, out e), e);
            Assert.AreEqual(S7aConventions.C5FlagsExt.PictureSite, st.flags & (SeStateFlags.MarginsOn | SeStateFlags.SeeThrough | S7aConventions.C5FlagsExt.PictureSite),
                            "site Space: SE's picture, bit 9 set");
            Assert.IsFalse(S2TestC5.TryParse(Json.Parse("{\"view\": \"Track\", \"picture\": \"orbit\"}"), out off, out st, out d, out e));
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Prime\", \"targetFps\": 120, \"seRect\": [0, 0, 2560, 1440], \"crossfadeMs\": 150, \"busTarget\": 0.5}"),
                                            out off, out st, out d, out e), e);
            Assert.AreEqual(PerfMode.Full, st.perfMode);
            Assert.AreEqual(120u, st.targetFps);
            Assert.AreEqual(new RectI(0, 0, 2560, 1440), RectI.From(st.seRect));
            Assert.AreEqual(150f, st.crossfadeMs);
            Assert.AreEqual(0.5f, st.snBusTarget);
            StringAssert.Contains(d, "Approach/Prime");
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Space\", \"perfMode\": 2}"), out off, out st, out d, out e), e);
            Assert.AreEqual(PerfMode.Full, st.perfMode, "an explicit perfMode (the S1 SE case: Space + Full)");

            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"argv\": [\"Surfacing\"]}"), out off, out st, out d, out e) && st.viewState == ViewState.Surfacing, "SE chat relay");
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"argv\": [\"off\"]}"), out off, out st, out d, out e) && off);
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"off\": true}"), out off, out st, out d, out e) && off);
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"off\"}"), out off, out st, out d, out e) && off);
            foreach (string bad in new[] { "{}", "{\"view\": \"Orbit\"}", "{\"view\": \"Space\", \"perfMode\": 3}", "{\"view\": \"Space\", \"busTarget\": 2}",
                                           "{\"view\": \"Space\", \"seRect\": [1, 2, 3]}", "{\"view\": \"Space\", \"foreground\": \"maybe\"}",
                                           "{\"view\": \"Space\", \"crossfadeMs\": 0}" })
                Assert.IsFalse(S2TestC5.TryParse(Json.Parse(bad), out off, out st, out d, out e), "refused: " + bad);

            // the override drives the recipe exactly like SE's C5 would
            Assert.IsTrue(S2TestC5.TryParse(Json.Parse("{\"view\": \"Underwater\", \"foreground\": false}"), out off, out st, out d, out e), e);
            var ri = new RecipeInputs
            {
                C5Valid = true, PeerAlive = true, PerfMode = st.perfMode, TargetFps = st.targetFps, ViewState = st.viewState,
                ApproachMode = st.approachMode, SeForeground = (st.flags & SeStateFlags.SeForeground) != 0 && (st.overlays & Overlays.Unfocused) == 0,
                SeeThrough = (st.flags & SeStateFlags.SeeThrough) != 0,
            };
            Assert.AreEqual("(0, 30)", PerfRecipeRules.Select(ref ri).Pair);
            StringAssert.Contains(PerfRecipeRules.Select(ref ri).Why, "seeThrough", "the see-through rule, not only the targetFps 30");
        }

        [TestMethod]
        public void S2Switches_Defaults_Overrides_And_MutantNames()
        {
            Func<string, string> saved = S1Switches.Reader;
            try
            {
                var env = new Dictionary<string, string>();
                S1Switches.Reader = n => { string v; return env.TryGetValue(n, out v) ? v : null; };
                Assert.IsTrue(S2Switches.Window && S2Switches.RectCorrect && S2Switches.Recipes, "all on by default");
                Assert.IsFalse(S2Switches.AlwaysDeleteTab, "DeleteTab only as the fallback by default");
                env[S2Switches.WindowVar] = "off";
                env[S2Switches.RectCorrectVar] = "0";
                env[S2Switches.RecipesVar] = "false";
                env[S2Switches.TaskbarVar] = " DeleteTab ";
                Assert.IsFalse(S2Switches.Window);
                Assert.IsFalse(S2Switches.RectCorrect);
                Assert.IsFalse(S2Switches.Recipes);
                Assert.IsTrue(S2Switches.AlwaysDeleteTab);
                StringAssert.Contains(S2Switches.Describe(), "window=OFF");
            }
            finally
            {
                S1Switches.Reader = saved;
            }
            foreach (string m in new[] { "S2.M2", "S2.M3", "S2.M5", "S2.M6", "S2.M12", "S2.M14" }) Assert.IsTrue(DevMutants.IsKnown(m), m);
            Assert.IsFalse(DevMutants.IsKnown("S2.M1"), "S2.M1 (DWM call) is an SE-side mutant");
        }
        /// <summary>
 ///: Subnautica's camera parks at +60 m under the SE camera while its picture cannot be shown. S2's rule
 /// (Space or Track, by view) is <see cref="CameraParkRules.ParkByView"/>, kept for the seeded failure S7a.M2; S7a's
 /// rule parks by C5 perfMode (≠ Full), which gives S2's Space and Track under the Waterline policy (the SE machine
 /// asks Track there) and Space only under Site (Full in Track: the picture can show there).
 /// </summary>
        [TestMethod]
        public void CameraPark_SpaceAndTrackOnly_ClampsAltitude()
        {
            // S2 policy, the fallback, as the view rule (S7a.M2) and as the perfMode rule with the Waterline perf table
            Assert.IsTrue(CameraParkRules.ParkByView(true, ViewState.Space, ApproachMode.None));
            Assert.IsTrue(CameraParkRules.ParkByView(true, ViewState.Approach, ApproachMode.Track));
            Assert.IsFalse(CameraParkRules.ParkByView(true, ViewState.Approach, ApproachMode.Prime));
            Assert.IsFalse(CameraParkRules.ParkByView(true, ViewState.Underwater, ApproachMode.None));
            Assert.IsFalse(CameraParkRules.ParkByView(true, ViewState.Surfacing, ApproachMode.None));
            Assert.IsFalse(CameraParkRules.ParkByView(false, ViewState.Space, ApproachMode.None), "no C5: never park");
            Assert.AreEqual(60f, CameraParkRules.DriveY(true, 26000f));
            Assert.AreEqual(12f, CameraParkRules.DriveY(true, 12f), "already lower than the park altitude");
            Assert.AreEqual(26000f, CameraParkRules.DriveY(false, 26000f));
            for (uint v = ViewState.Space; v <= ViewState.Surfacing; v++)
                foreach (uint a in new[] { ApproachMode.Track, ApproachMode.Prime })
                {
                    uint c5a = v == ViewState.Approach ? a : ApproachMode.None;
                    Assert.AreEqual(CameraParkRules.ParkByView(true, v, c5a), CameraParkRules.Park(true, ViewStateMachine.PerfFor(v, a, ViewPicture.Waterline)),
                                    "waterline: the perfMode rule equals S2's view rule (" + v + "/" + a + ")");
                }

            // S7a Site: parked in Space only (Idle), never while the picture can show (Full from the Approach entry)
            Assert.IsTrue(CameraParkRules.Park(true, PerfMode.Idle));
            Assert.IsFalse(CameraParkRules.Park(true, PerfMode.Full), "site Track/Prime/Underwater/Surfacing: true pose");
            Assert.IsTrue(CameraParkRules.Park(true, PerfMode.Track), "a Waterline Track request still parks");
            Assert.IsFalse(CameraParkRules.Park(false, PerfMode.Idle), "no C5: never park");
            var track = new PerfInputsForPark { C5Valid = true, View = ViewState.Approach, Approach = ApproachMode.Track, PerfMode = PerfMode.Full };
            Assert.IsFalse(CameraParkRules.Decide(track), "site Track: not parked");
            track.ParkByView = true;
            Assert.IsTrue(CameraParkRules.Decide(track), "S7a.M2: parked by view while the picture shows in Track (S7a.N1 red)");
            Assert.IsTrue(DevMutants.IsKnown(CameraParkRules.DevMutantS7aM2), "S7a.M2 is a known SN mutant");
        }

        /// <summary>S7a: mirrored chunks are culled at the above-water leave radius under Site above the sea, else at 200 m.</summary>
 /// <summary>S7a-1b: above the near field under Site, areaBuilt means the streamer is idle (air around the camera).</summary>
        [TestMethod]
        public void S7aAreaBuilt_AirAboveTheNearField_UnderSiteOnly()
        {
            Assert.IsTrue(S7aConventions.AirAboveTheNearField(true, 1000.0, 16.0), "1 km under Site: air");
            Assert.IsTrue(S7aConventions.AirAboveTheNearField(true, 156.1, 16.0), "just above the clamp + half box");
            Assert.IsFalse(S7aConventions.AirAboveTheNearField(true, 156.0, 16.0), "at the clamp + half box: the camera box decides");
            Assert.IsFalse(S7aConventions.AirAboveTheNearField(true, 140.0, 16.0));
            Assert.IsFalse(S7aConventions.AirAboveTheNearField(false, 1000.0, 16.0), "Waterline: never");
            Assert.AreEqual(140.0, S7aConventions.AreaBoxMaxCentreY);
        }

        [TestMethod]
        public void S7aMirrorCull_AboveWaterUnderSite_1050m()
        {
            Assert.AreEqual(1600.0, MirrorCullRules.DistanceM(true, 300.0, 200.0), "ext s7a picture 2: 1,600 m for the 1 km band");
            Assert.AreEqual(200.0, MirrorCullRules.DistanceM(true, -5.0, 200.0), "under water: the water visibility");
            Assert.AreEqual(200.0, MirrorCullRules.DistanceM(true, 0.0, 200.0), "at the waterline");
            Assert.AreEqual(200.0, MirrorCullRules.DistanceM(false, 300.0, 200.0), "Waterline policy: S5's distance");
            double e, l;
            S7aConventions.MirrorRadius(true, 300.0, out e, out l);
            Assert.AreEqual(l, MirrorCullRules.DistanceM(true, 300.0, 200.0), "the SE entity set's leave radius");
        }

    }
}
