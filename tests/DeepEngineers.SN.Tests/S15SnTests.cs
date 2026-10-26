using System;
using System.Collections.Generic;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S15, Subnautica side, the Unity-free part (src/DeepEngineers.SN/S15/S15SnRules.cs): one line per SE event (the first C5 value
 /// is the baseline, several missed at once are one line, a new epoch rebaselines), one line at a time (the gap after S15's
 /// own and S16's voice, at most two waiting), the audio groups across the surface and the line watch, the sky bodies (the
 /// bound planet never drawn, a body below the horizon hidden, S15.M6 doubles the size), the disc's colour, and the mutant
 /// names known to DevMutants (S15 hook).
 /// </summary>
    [TestClass]
    public class S15SnTests
    {
        private static S15WireState W(uint epoch, uint welcome, uint up, uint down, uint small)
        {
            return new S15WireState { Version = 2, WorldEpoch = epoch, WelcomeSeq = welcome, PowerUpSeq = up, PowerDownSeq = down, SmallWelcomeSeq = small };
        }

        [TestMethod]
        public void OneLinePerEvent_OneAtATime()
        {
            var v = new S15SnVoice();
            S15SnLine line;
            S15WireState w = W(2, 5, 1, 0, 3);
            Assert.IsFalse(v.Step(true, ref w, 0.0, out line), "the first record is the baseline");
            w = W(2, 6, 1, 0, 3);
            Assert.IsTrue(v.Step(true, ref w, 0.1, out line));
            Assert.AreEqual(S15Conventions.Event.Welcome, line.Event);
            Assert.AreEqual(6u, line.Seq);
            v.Heard(0.1);
            w = W(2, 6, 2, 1, 3);   // engine powering up and shutting down at once
            Assert.IsFalse(v.Step(true, ref w, 0.2, out line), "inside the gap after the last line");
            Assert.AreEqual(2, v.Waiting);
            Assert.IsTrue(v.Step(true, ref w, 0.1 + S15Conventions.Voice.QueueGapMs / 1000.0 + 0.01, out line));
            Assert.AreEqual(S15Conventions.Event.EnginePowerUp, line.Event, "first come, first played");
            v.Heard(4.0);
            Assert.IsFalse(v.Step(true, ref w, 4.5, out line));
            Assert.IsTrue(v.Step(true, ref w, 7.6, out line));
            Assert.AreEqual(S15Conventions.Event.EnginePowerDown, line.Event);
            Assert.AreEqual(0, v.Waiting);
        }

        [TestMethod]
        public void Queue_DropsTheNewest_KeepsTheWelcome_AndS16Counts()
        {
            var v = new S15SnVoice();
            S15SnLine line;
            S15WireState w = W(1, 0, 0, 0, 0);
            v.Step(true, ref w, 0.0, out line);
            v.Heard(0.0);   // S16's hull voice
            w = W(1, 1, 1, 1, 0);
            Assert.IsFalse(v.Step(true, ref w, 1.0, out line), "S16's voice holds the line back");
            Assert.AreEqual(S15Conventions.Voice.QueueMax, v.Waiting);
            Assert.AreEqual(1L, v.Dropped, "three at once, two wait: the newest (engine shutting down) dropped (review HIGH 2)");
            Assert.IsTrue(v.Step(true, ref w, 3.6, out line));
            Assert.AreEqual(S15Conventions.Event.Welcome, line.Event, "the welcome first, never dropped");
            v.Heard(3.6);
            Assert.IsFalse(v.Step(true, ref w, 3.7, out line), "one at a time");
            Assert.IsTrue(v.Step(true, ref w, 3.6 + S15Conventions.Voice.QueueGapMs / 1000.0 + 0.01, out line));
            Assert.AreEqual(S15Conventions.Event.EnginePowerUp, line.Event);
        }

        [TestMethod]
        public void Queue_AWelcomeReplacesTheNewestOtherLine()
        {
            var v = new S15SnVoice();
            S15SnLine line;
            S15WireState w = W(1, 0, 0, 0, 0);
            v.Step(true, ref w, 0.0, out line);
            v.Heard(0.0);
            w = W(1, 0, 1, 1, 0);
            v.Step(true, ref w, 0.5, out line);          // engine up and down wait (full)
            w = W(1, 1, 1, 1, 1);
            v.Step(true, ref w, 0.6, out line);          // the Cyclops's and the Seamoth's welcome on a full queue
            Assert.AreEqual(2L, v.Dropped, "both welcomes replace the newest other lines");
            Assert.IsTrue(v.Step(true, ref w, 10.0, out line));
            Assert.AreEqual(S15Conventions.Event.Welcome, line.Event);
            v.Heard(10.0);
            Assert.IsTrue(v.Step(true, ref w, 20.0, out line));
            Assert.AreEqual(S15Conventions.Event.SmallWelcome, line.Event);
        }

        [TestMethod]
        public void VoiceClock_OneVoiceAtATime_BothWays()
        {
            S15SnVoiceClock.Reset();
            bool voice = true;
            S15SnVoiceClock.HoldS16(ref voice, 1.0);
            Assert.IsTrue(voice, "no S15 line: S16's hull voice goes");
            Assert.AreEqual(-1.0, S15SnVoiceClock.SinceVoiceMs(1.0, -1.0), "no voice yet");
            S15SnVoiceClock.LastS15S = 2.0;                 // an S15 line plays
            voice = true;
            S15SnVoiceClock.HoldS16(ref voice, 2.5);
            Assert.IsFalse(voice, "S16's hull voice waits QueueGapMs after an S15 line");
            Assert.IsTrue(S15SnVoiceClock.HeldS16);
            voice = false;
            S15SnVoiceClock.HoldS16(ref voice, 2.0 + S15Conventions.Voice.QueueGapMs / 1000.0 + 0.01);
            Assert.IsTrue(voice, "and is released after the gap (held, never dropped)");
            Assert.AreEqual(500.0, S15SnVoiceClock.SinceVoiceMs(2.5, 1.0), 1e-6, "the creaks' duck sees the S15 line");
            Assert.AreEqual(250.0, S15SnVoiceClock.SinceVoiceMs(2.5, 2.25), 1e-6, "or S16's own voice, the later one");
            S15SnVoiceClock.Reset();
        }

        [TestMethod]
        public void Unmapped_PlacesTheDiscOnTheSeDirection_KeepsVisibility()
        {
            // S15.M3: the disc goes to the SE-world direction; visibility stays on the mapped one
            S15SkyFrame f = Frame(new Vec3(0, 150, 0));
            Vec3 up = (SiteMap.SiteV1Test.SnToSe(new Vec3(0, 150, 0)) - SiteMap.SiteV1Test.C).Normalized();
            var b = new S15Body { Name = "Moon", Rel = up * 1e6, RadiusM = 19000 };
            S15SnBodyView a = S15SnSkyRules.View(ref f, b, 0, 150, false, false);
            S15SnBodyView m = S15SnSkyRules.View(ref f, b, 0, 150, false, true);
            Assert.AreEqual(a.Visible, m.Visible);
            Assert.AreEqual(a.Dir.X, m.Dir.X, 1e-12);
            Assert.AreEqual(a.Dir.Y, m.Dir.Y, 1e-12);
            Assert.AreEqual(a.Dir.Z, m.Dir.Z, 1e-12);
            Assert.AreEqual(a.Dir.X, a.Place.X, 1e-12);
            Vec3 se = (b.Rel - f.CamRel) / (b.Rel - f.CamRel).Length;
            Assert.AreEqual(se.X, m.Place.X, 1e-12);
            Assert.AreEqual(se.Y, m.Place.Y, 1e-12);
        }

        [TestMethod]
        public void EventsBlock_CarriesNoLines()
        {
            // the events block (version 2) carries the sonar only
            var v = new S15SnVoice();
            S15SnLine line;
            S15WireState w = W(1, 0, 0, 0, 0);
            var e = new S15EventsState { Version = (byte)S15Conventions.Events.Version, WorldEpoch = 1, SonarSeq = 4 };
            Assert.IsFalse(v.Step(true, ref w, e, 0.0, out line));
            e.SonarSeq = 5;
            Assert.IsFalse(v.Step(true, ref w, e, 1.0, out line), "a sonar ping is no voice line");
            Assert.AreEqual(0, v.Waiting);
        }

        [TestMethod]
        public void Groups_MusicAndVoiceLiveAcrossTheSurface()
        {
            var g = new S15SnGroups();
            float m, vo;
            g.Step(0, 1f, out m, out vo);
            Assert.AreEqual(1f, m, 1e-6, "under water: the player's level");
            g.Step(100, 0f, out m, out vo);                               // above water: the fade starts
            Assert.IsTrue(m < 1f && m > 0.9f, "a 2 s crossfade, not a cut");
            g.Step(100 + S15Conventions.Mix.CrossfadeMs / 2, 0f, out m, out vo);
            // the step at 100 ms already counted the first 100 ms: 1 - 1100 x 0.4 / 2000 = 0.78
            Assert.AreEqual(0.78f, m, 0.005f, "about half way after half the crossfade: the surface swing takes CrossfadeMs (run 20261024-133305: 0.8 s)");
            g.Step(100 + S15Conventions.Mix.CrossfadeMs - 300, 0f, out m, out vo);
            Assert.IsTrue(m > 0.6f + 1e-3f, "not there before CrossfadeMs");
            g.Step(100 + S15Conventions.Mix.CrossfadeMs, 0f, out m, out vo);
            Assert.AreEqual(0.6f, m, 1e-6, "the music above water at the pinned level");
            Assert.AreEqual(0.6f, vo, 1e-6, "the voice above water: a line under way plays on");
            Assert.AreEqual(0.6f, g.MusicTarget, 1e-6);
            g.MusicAbove = 0.3f;                                          // de.s15 music 0.3
            g.Step(200 + 2 * S15Conventions.Mix.CrossfadeMs, 0f, out m, out vo);
            Assert.AreEqual(0.3f, m, 1e-6, "owner-tunable");
            g.Step(300 + 4 * S15Conventions.Mix.CrossfadeMs, 1f, out m, out vo);
            Assert.AreEqual(1f, m, 1e-6, "back under water");
            Assert.AreEqual(S15Conventions.Mix.CrossfadeMs / 0.4f, S15SnGroups.SwingMs(0.6f), 1e-3f);
            Assert.AreEqual((float)S15Conventions.Mix.CrossfadeMs, S15SnGroups.SwingMs(0f), 1e-3f, "a full swing: the crossfade time itself");
            Assert.AreEqual(S15Conventions.Mix.CrossfadeMs / 0.05f, S15SnGroups.SwingMs(1f), 1e-3f, "no swing: bounded");
        }

        [TestMethod]
        public void LineWatch_CompleteAcrossTheSurface()
        {
            var w = new S15SnLineWatch { LengthMs = 2500 };
            w.Sample(400, 1f, false, 1f);
            w.Sample(1500, 0.8f, false, 0f);                              // the camera surfaced meanwhile
            w.Sample(2400, 0.6f, false, 0f);
            Assert.IsTrue(w.Crossed && w.Complete && !w.Muted);
            Assert.AreEqual(0.6f, w.VoiceMin, 1e-6);
            var cut = new S15SnLineWatch { LengthMs = 2500 };
            cut.Sample(600, 0f, true, 0f);                                // the old rule: bus:/ faded and muted (S15.M19)
            Assert.IsFalse(cut.Complete);
            Assert.IsTrue(cut.Muted && cut.VoiceMin == 0f);
        }

        [TestMethod]
        public void Splash_AfterADiveOnly()
        {
            var s = new S15SnSplash();
            Assert.IsFalse(s.Step(5, 0.0));
            Assert.IsFalse(s.Step(-2, 1.0));
            Assert.IsFalse(s.Step(3, 1.5), "under the sea less than SplashMinUnderMs");
            Assert.IsFalse(s.Step(-2, 2.0));
            Assert.IsFalse(s.Step(-20, 2.5));
            Assert.IsTrue(s.Step(50, 3.2), "a teleport out counts");
            Assert.IsFalse(s.Step(51, 3.3));
        }

        [TestMethod]
        public void Epoch_And_Absence()
        {
            var v = new S15SnVoice();
            S15SnLine line;
            S15WireState w = W(1, 3, 0, 0, 0);
            v.Step(true, ref w, 0, out line);
            Assert.IsFalse(v.Step(false, ref w, 1, out line), "no record: nothing");
            w = W(2, 9, 0, 0, 0);
            Assert.IsFalse(v.Step(true, ref w, 2, out line), "a new world epoch rebaselines");
            w = W(2, 12, 0, 0, 0);
            Assert.IsTrue(v.Step(true, ref w, 3, out line), "three missed at once: one line");
            Assert.AreEqual(2L, v.Coalesced);
        }

        private static S15SkyFrame Frame(Vec3 camSn)
        {
            SiteMap map = SiteMap.SiteV1Test;
            return S15Sky.FrameAt(map, map.SnToSe(camSn), false);
        }

        [TestMethod]
        public void Sky_BodiesAndHorizon()
        {
            SiteMap map = SiteMap.SiteV1Test;
            S15SkyFrame f = Frame(new Vec3(0, 150, 0));
            Vec3 up = (map.SnToSe(new Vec3(0, 150, 0)) - map.C).Normalized();
            var high = new S15Body { Rel = up * 1e6, RadiusM = 20000, Name = "Moon" };
            S15SnBodyView v = S15SnSkyRules.View(ref f, high, 0, 150, false);
            Assert.IsTrue(v.Visible);
            Assert.IsTrue(v.Dir.Y > 0.999);
            Assert.AreEqual(Math.Asin(20000 / (1e6 - 60150.0039)), v.AngRadius, 1e-6);
            var below = new S15Body { Rel = up * -1e6, RadiusM = 20000, Name = "Under" };
            Assert.IsFalse(S15SnSkyRules.View(ref f, below, 1, 150, false).Visible, "under the sea");
            var bound = new S15Body { Rel = Vec3.Zero, RadiusM = 60000, Flags = (byte)S15Conventions.BodyFlags.Bound, Name = "DE_4546B" };
            Assert.IsFalse(S15SnSkyRules.View(ref f, bound, 2, 150, false).Visible, "the bound planet is the ground");
            S15SnBodyView m6 = S15SnSkyRules.View(ref f, high, 0, 150, true);
            Assert.AreEqual(2 * v.AngRadius, m6.AngRadius, 1e-12, "S15.M6 doubles the size");
            var invalid = f;
            invalid.Flags = 0;
            Assert.IsFalse(S15SnSkyRules.View(ref invalid, high, 0, 150, false).Visible, "SE's sky off");
        }

        [TestMethod]
        public void Disc_SizeAndColour()
        {
            Assert.AreEqual(900.0, S15SnSkyRules.DiscDistance(1000.0), 1e-9);
            Assert.AreEqual(900.0 * Math.Tan(0.01), S15SnSkyRules.DiscRadius(900.0, 0.01), 1e-12);
            Assert.AreEqual((byte)65, S15SnSkyRules.Channel(65, 197, 1.0, 0.0, true), "the centre lit full: the palette");
            Assert.AreEqual((byte)197, S15SnSkyRules.Channel(65, 197, 1.0, 1.0, true), "the edge of an atmosphere: the limb colour");
            Assert.AreEqual((byte)65, S15SnSkyRules.Channel(65, 197, 1.0, 1.0, false), "no atmosphere: no limb");
            Assert.IsTrue(S15SnSkyRules.Channel(150, 150, 0.06, 0, false) <= 10, "the night side");
        }

        [TestMethod]
        public void Mutants_KnownToDevMutants()
        {
            foreach (string m in S15SnMutants.All) Assert.IsTrue(DevMutants.IsKnown(m), m + " (S15 hook in DevMutants)");
        }
    }
}
