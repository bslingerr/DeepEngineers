using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S16, Subnautica side, the Unity-free part (src/DeepEngineers.SN/S16/S16SnRules.cs): one sound per SE event (the first
 /// C5 value is the baseline, several missed at once are one sound, a new epoch or a restarted SE rebaselines, no record
 /// plays nothing), S16.M7's own clock, and the mutant name known to DevMutants (S16 hook).
 /// </summary>
    [TestClass]
    public class S16Tests
    {
        private static S16WireState W(uint epoch, uint creak, uint voice, uint damage, int level)
        {
            return new S16WireState { Version = 1, WorldEpoch = epoch, CreakSeq = creak, VoiceSeq = voice, DamageSeq = damage, CreakLevel = (byte)level };
        }

        [TestMethod]
        public void OneSoundPerEvent()
        {
            var e = new S16SnEvents();
            S16WireState w = W(3, 10, 2, 5, 1);
            Assert.IsFalse(e.Step(true, ref w, 0.0, false).Any, "the first record is the baseline");
            Assert.IsFalse(e.Step(true, ref w, 0.1, false).Any, "unchanged: nothing");
            w = W(3, 11, 2, 5, 1);
            S16SnPlay p = e.Step(true, ref w, 0.2, false);
            Assert.IsTrue(p.Creak && !p.Voice && !p.Damage);
            Assert.AreEqual(11u, p.CreakSeq);
            w = W(3, 14, 3, 6, 3);
            p = e.Step(true, ref w, 0.3, false);
            Assert.IsTrue(p.Creak && p.Voice && p.Damage, "one of each");
            Assert.AreEqual(2, e.Coalesced, "creaks 12 and 13 coalesced into one sound");
            Assert.IsFalse(e.Step(false, ref w, 0.4, false).Any, "no record: nothing");
            w = W(4, 15, 3, 6, 3);
            Assert.IsFalse(e.Step(true, ref w, 0.5, false).Any, "a new epoch rebaselines");
            w = W(4, 0, 0, 0, 0);
            Assert.IsFalse(e.Step(true, ref w, 0.6, false).Any, "a restarted SE (counters back to 0): no sound");
            w = W(4, 1, 0, 0, 1);
            Assert.IsTrue(e.Step(true, ref w, 0.7, false).Creak);
        }

        [TestMethod]
        public void MutantClock_IgnoresTheEvents()
        {
            var e = new S16SnEvents();
            S16WireState w = W(1, 5, 0, 0, 2);
            int creaks = 0;
            for (int i = 0; i < 40; i++)
                if (e.Step(true, ref w, i * 0.05, true).Creak) creaks++;
            Assert.AreEqual(4, creaks, "S16.M7: one creak per 0.5 s while the level is above 0, without any SE event");
            w = W(1, 5, 0, 0, 0);
            Assert.IsFalse(e.Step(true, ref w, 10.0, true).Creak, "level 0: silent");
        }

        [TestMethod]
        public void Volumes_AndMutantName()
        {
            Assert.AreEqual(0.7f, S16SnEvents.CreakVolume(1));
            Assert.AreEqual(1f, S16SnEvents.CreakVolume(3));
            // format voice.5 s after it
            Assert.AreEqual(1.6f, S16VoiceMix.VoiceVolume, 1e-6f);
            Assert.AreEqual(0.25f, S16VoiceMix.EffectVolume(S16SnEvents.CreakVolume(3), 1000.0), 1e-6f, "a level-3 creak 1 s after the voice");
            Assert.AreEqual(1f, S16VoiceMix.EffectVolume(S16SnEvents.CreakVolume(3), 3500.0), 1e-6f, "after DuckMs: full");
            Assert.AreEqual(0.7f, S16VoiceMix.EffectVolume(S16SnEvents.CreakVolume(1), -1.0), 1e-6f, "no voice yet: its own volume");
            Assert.AreEqual("S16.M7", S16SnMutants.CreakClock);
            Assert.AreEqual("S16.M11", S16SnMutants.WrapReadout, "depth 7: the seated readout forced to wrap (S16.SNHUD red)");
            CollectionAssert.Contains(S16SnMutants.All, S16SnMutants.WrapReadout);
            Assert.IsTrue(DevMutants.IsKnown("S16.M7"), "DevMutants knows S16.M7 (S16 hook)");
        }
    }
}
