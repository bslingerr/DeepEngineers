using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S14 "Audio v2" (CONTRACT §5.4 "S14 conventions"): the pinned canonical text (FNV-1a 64, lines, bytes; the Python twin
 /// tests/s14/analyze_s14.py computes the same) and the rules both plugins use.
 /// </summary>
    [TestClass]
    public class S14ConventionsTests
    {
        [TestMethod]
        public void CanonicalText_IsPinned()
        {
            string t = S14Conventions.CanonicalText();
            Assert.AreEqual(0x1261b20244b0b097UL, S14Conventions.PinnedHash);
            Assert.AreEqual(S14Conventions.PinnedHash, S14Conventions.Hash(), "a changed rule changes the hash: re-pin CONTRACT, Wire and the twin together");
            Assert.AreEqual(S14Conventions.CanonicalLines, t.Split('\n').Length - 1);
            Assert.AreEqual(S14Conventions.CanonicalBytes, t.Length);
            Assert.IsTrue(t.StartsWith("ext s14 audio 6\neffect LowPassHelmet\nconst Effect UnderwaterCutoffHz 1800\n", StringComparison.Ordinal), "round 4: the lighter muffle");
            StringAssert.Contains(t, "const Muffle EyeYMm 0\nconst Muffle DeadbandMm 50\n");
            StringAssert.Contains(t, "for every modifiable SE sound while the camera is under the sea (S2's camera SN y below Muffle.EyeYMm / 1000", "owner 2026-10-24: the eye, not the view's mix");
            StringAssert.Contains(t, "const Pan OneSidedPermille 20\n");
            StringAssert.Contains(t, "const Chain MaxLowPass 1\n");
            StringAssert.Contains(t, "format pan.stereo ");
            StringAssert.Contains(t, "format pan.own ");
            StringAssert.Contains(t, "format effect.chain ");
            StringAssert.Contains(t, "format jets.old ");
            foreach (char c in t) Assert.IsTrue(c < 128, "ASCII");
            StringAssert.Contains(t, "const Envelope AttackMs 200\n");
            StringAssert.Contains(t, "const Envelope ReleaseMs 300\n");
            StringAssert.Contains(t, "gui HudPlaceBlock\n");
        }

        [TestMethod]
        public void Muffle_FollowsTheEye()
        {
            Assert.IsTrue(S14Rules.EyeUnder(-0.12), "the eye 12 cm under (the waterline band the view's mix ignores coming from above)");
            Assert.IsFalse(S14Rules.EyeUnder(0.03), "the eye above the surface (the view's mix ramps for 300 ms after the exit)");
            Assert.IsFalse(S14Rules.EyeUnder(double.NaN), "no camera: dry");
        }

        [TestMethod]
        public void Jetpack_SilenceOnlyWhenTheSwimLatchHoldsFlyingWithTheJetpackOff()
        {
            Assert.IsTrue(S14Rules.SilenceJetpack(true, true, false, true, false));
            Assert.IsFalse(S14Rules.SilenceJetpack(true, true, true, true, false), "the jetpack runs (head out): SE's sound");
            Assert.IsFalse(S14Rules.SilenceJetpack(true, false, false, true, false), "not swimming: SE's own rule");
            Assert.IsFalse(S14Rules.SilenceJetpack(true, true, false, false, false), "not Flying: SE's own state sounds");
            Assert.IsFalse(S14Rules.SilenceJetpack(false, true, false, true, false), "de.s14 jetpack off");
            Assert.IsFalse(S14Rules.SilenceJetpack(true, true, false, true, true), "S14.M1");
        }

        [TestMethod]
        public void Muffle_ArcadeRouteGuiAndThrusters()
        {
            Assert.IsTrue(S14Rules.ArcadeRoute(true, false, false), "an Arcade world gets the delegate");
            Assert.IsFalse(S14Rules.ArcadeRoute(true, true, false), "a realistic world has SE's own");
            Assert.IsFalse(S14Rules.ArcadeRoute(true, false, true), "S14.M2");
            Assert.IsFalse(S14Rules.ArcadeRoute(false, false, false));
            Assert.IsTrue(S14Rules.IsMuffledGui("HudPlaceBlock") && S14Rules.IsMuffledGui("HudDeleteBlock") && S14Rules.IsMuffledGui("HudRotateBlock"));
            Assert.IsFalse(S14Rules.IsMuffledGui("HudClick"), "menu clicks stay dry");
            Assert.IsFalse(S14Rules.IsMuffledGui("HudVocHealthLow"), "the suit's voice stays dry");
            Assert.IsFalse(S14Rules.IsMuffledGui(null));
            Assert.IsTrue(S14Rules.ThrusterSubmerged(true, -0.5));
            Assert.IsFalse(S14Rules.ThrusterSubmerged(true, 0.5), "a thruster above the surface");
            Assert.IsFalse(S14Rules.ThrusterSubmerged(false, -20), "no underwater mix");
        }

        [TestMethod]
        public void Engine_SourcePerThrusterType()
        {
            const byte se = S14Conventions.Engine.ModeSe, sn = S14Conventions.Engine.ModeSubnautica;
            ushort h = S7Conventions.ThrusterKind.Hydrogen, ion = S7Conventions.ThrusterKind.Ion, atm = S7Conventions.ThrusterKind.Atmospheric;
            Assert.IsFalse(S14Rules.SnPlaysType(se, h, true, true), "default: SE's own sound for every type");
            Assert.IsFalse(S14Rules.SnPlaysType(se, ion, true, true));
            Assert.IsTrue(S14Rules.SnPlaysType(sn, h, true, true), "subnautica: hydrogen = the Cyclops loop");
            Assert.IsTrue(S14Rules.SnPlaysType(sn, ion, true, true), "subnautica: ion = the Seamoth loop");
            Assert.IsFalse(S14Rules.SnPlaysType(sn, atm, true, true), "atmospheric stays SE's");
            Assert.IsFalse(S14Rules.SnPlaysType(sn, h, false, true), "no Cyclops loaded: SE's hydrogen sound");
            Assert.AreEqual((byte)0, S14Rules.C7FlagsOf(se, true, true));
            Assert.AreEqual((byte)(S14Conventions.C7Flags.SnLoops | S14Conventions.C7Flags.Ion), S14Rules.C7FlagsOf(sn, false, true));
            byte f = S14Rules.C7FlagsOf(sn, true, true);
            Assert.IsTrue(S14Rules.SnCovers(true, f, h) && S14Rules.SnCovers(true, f, ion));
            Assert.IsFalse(S14Rules.SnCovers(true, f, atm));
            Assert.IsFalse(S14Rules.SnCovers(false, f, h), "a stale C7: SE keeps its own");
            Assert.IsFalse(S14Rules.SnCovers(true, S14Rules.C7FlagsOf(sn, false, false), h));
            // SE's own emitter volume
            Assert.AreEqual(1.0, S14Rules.SeThrusterVolume(false, false, 0.2, false), "above water: SE's own level");
            Assert.AreEqual(0.0, S14Rules.SeThrusterVolume(true, true, 0.8, false), "covered by Subnautica's loop: faded");
            Assert.AreEqual(0.3, S14Rules.SeThrusterVolume(true, false, 0.3, false), 1e-12, "under water: the envelope's volume");
            Assert.AreEqual(1.0, S14Rules.SeThrusterVolume(true, true, 0.3, true), "S14.M7: SE's own level");
        }

        [TestMethod]
        public void C7Block_RoundTripsAndStaysClearOfS11()
        {
            var st = new SnStatus();
            byte f;
            Assert.IsFalse(S14Wire.TryReadStatus(ref st, out f), "version 0 = absent");
            S11Wire.WriteStatus(ref st, 3);
            S14Wire.WriteStatus(ref st, 7);
            Assert.IsTrue(S14Wire.TryReadStatus(ref st, out f));
            Assert.AreEqual((byte)7, f);
            byte f11;
            Assert.IsTrue(S11Wire.TryReadStatus(ref st, out f11));
            Assert.AreEqual((byte)3, f11, "the S11 block 0xC0-0xC3 untouched");
            Assert.AreEqual(0xC4, S14Conventions.C7.Offset);
            Assert.IsTrue(S14Conventions.C7.Offset + S14Conventions.C7.Size <= 0x100, "inside SnStatus pad_8c");
        }

        [TestMethod]
        public void Envelope_SharedBySeAndSubnautica()
        {
            var e = new S14EngineEnv();
            double t = 0, dt = 1000.0 / 60.0;
            Assert.AreEqual(S14EnvEvent.Start, e.Step(true, 0.5, dt, t, false));
            for (int i = 0; i < 20; i++) { t += dt / 1000; e.Step(true, 0.5, dt, t, false); }
            Assert.AreEqual(1.0, e.Level);
            Assert.AreEqual(S14Rules.EngineVolume(1.0, 0.5), e.Volume(false), 1e-12);
            e.Reset();
            Assert.IsFalse(e.Playing);
            Assert.AreEqual(0.0, e.Level);
        }

        [TestMethod]
        public void Envelope_StepsAtFullScalePerAttackAndRelease()
        {
            double l = 0;
            int frames = 0;
            while (l < 1.0 && frames < 100) { l = S14Rules.Step(l, 1.0, 1000.0 / 60.0, 200, 300); frames++; }
            Assert.AreEqual(12, frames, "200 ms at 60 fps");
            frames = 0;
            while (l > 0.0 && frames < 100) { l = S14Rules.Step(l, 0.0, 1000.0 / 60.0, 200, 300); frames++; }
            Assert.AreEqual(18, frames, "300 ms at 60 fps");
            Assert.AreEqual(0.5, S14Rules.Step(0.4, 0.5, 100, 200, 300), 1e-12, "never past the target");
            Assert.AreEqual(1.0, S14Rules.Step(0.0, 1.0, 10, 0, 0), "no attack time: a jump");
            Assert.AreEqual(0.3, S14Rules.Step(0.3, 0.9, 0, 200, 300), "no time: no change");
        }

        [TestMethod]
        public void EngineGain_QuieterForSmallCorrections()
        {
            Assert.AreEqual(0.25, S14Rules.EngineGain(0), 1e-12);
            Assert.AreEqual(1.0, S14Rules.EngineGain(1), 1e-12);
            Assert.AreEqual(0.25 + 0.75 * Math.Pow(0.2, 1.5), S14Rules.EngineGain(0.2), 1e-12);
            Assert.IsTrue(S14Rules.EngineGain(0.2) < 0.35, "a 20 % correction under 35 % level");
            Assert.AreEqual(0.0, S14Rules.EngineVolume(0, 1), 1e-12);
            Assert.AreEqual(0.5 * S14Rules.EngineGain(0.6), S14Rules.EngineVolume(0.5, 0.6), 1e-12);
            Assert.AreEqual(S14Rules.EngineGain(1), S14Rules.EngineGain(3), "clamped");
        }

        [TestMethod]
        public void Cutoff_RoundTripsSesConversion()
        {
            double f = S14Rules.XAudioFrequency(750);
            Assert.AreEqual(2.0 * Math.Sin(3.14 * 750 / 44100.0), f, 1e-12, "MyAudioEffectDefinition.cs:62");
            Assert.AreEqual(750.0, S14Rules.CutoffHz(f), 1e-6);
            Assert.AreEqual(100.0, S14Rules.CutoffHz(S14Rules.XAudioFrequency(100)), 1e-6);
            Assert.IsTrue(S14Rules.CutoffHz(1.0) > S14Conventions.Effect.DryCutoffMinHz, "XAudio2's open filter reads as dry");
            Assert.IsTrue(double.IsNaN(S14Rules.CutoffHz(double.NaN)));
        }

        [TestMethod]
        public void Listener_AndJets()
        {
            Assert.IsTrue(S14Rules.ListenerOk(0.0, 0.0));
            Assert.IsTrue(S14Rules.ListenerOk(0.05, 2.0));
            Assert.IsFalse(S14Rules.ListenerOk(0.06, 0.0));
            Assert.IsFalse(S14Rules.ListenerOk(0.0, 2.5));
            Assert.IsFalse(S14Rules.ListenerOk(double.NaN, 0.0), "no readback is not a pass");
            // round 5 (jets.length): S7p's flame at the jet's strength;805 m, full thrust)
 // 9.5 x 1.15 x 0.805 = 8.79 m, the Prawn jet's 21 m template had kept its factor at 1 whatever SE's length
            Assert.AreEqual(0.3, S14Rules.JetStrength(0), 1e-12, "the idle floor");
            Assert.AreEqual(1.0, S14Rules.JetStrength(1), 1e-12);
            Assert.AreEqual(0.3 + 0.7 * 0.5, S14Rules.JetStrength(0.5), 1e-12);
            Assert.AreEqual(9.5 * 1.15 * 0.805, S14Rules.JetLengthM(1, 0.805, 0, 1), 1e-4, "SE's flame length (the default length scale)");
            Assert.AreEqual(9.5 * 1.3 * 0.5 * 0.3, S14Rules.JetLengthM(0, 0.5, 1.3, 1), 1e-4, "idle: the floor of SE's length");
            Assert.AreEqual(0.0, S14Rules.JetLengthM(1, 1.0, 0, 0), "knob 0: no flame");
            Assert.IsTrue(S14Rules.JetLengthM(0.9, 1, 0, 1) > S14Rules.JetLengthM(0.1, 1, 0, 1), "longer with thrust");
            Assert.AreEqual(S14Rules.JetLengthM(1, 1, 0, 1) / S7pEffectRules.FlameLifetimeS, S14Rules.JetSpeed(S14Rules.JetLengthM(1, 1, 0, 1)), 1e-3, "the tip in the lifetime");
            Assert.AreEqual(1.6, S14Rules.JetDiameterM(0.8, 1), 1e-6, "SE's flame thickness");
            Assert.AreEqual(0.4, S14Rules.JetShapeRadiusM(0.8), 1e-12);
            Assert.AreEqual(0.02, S14Rules.JetShapeRadiusM(0), 1e-12, "a floor");
            Assert.IsTrue(S14Rules.JetRate(1, 0.8) > S14Rules.JetRate(0, 0.8) && S14Rules.JetRate(0, 0.8) > 0, "idle still emits");
            Assert.IsTrue(S14Rules.JetVisible(400, 1440) && !S14Rules.JetVisible(399, 1440), "VisibleMinPx at 1440 rows");
            Assert.AreEqual(100, S14Rules.JetVisibleMinPx(720), "a quarter at half the height");
            Assert.AreEqual(9500, S14Conventions.Jets.S7pFlameLengthPermille);
            Assert.AreEqual(140, S14Conventions.Jets.S7pLifetimeMs);
            Assert.AreEqual(1150, S14Conventions.Jets.S7pDefaultLengthScalePermille);
        }

        [TestMethod]
        public void Round4_PanMatrix_DownmixesAStereoVoiceOntoTheMonoPan()
        {
            float[] mono = { 0.9f, 0.1f }; // X3DAudio, one channel: a voice on the left
            float[] m = S14Rules.PanMatrix(mono, 2, 1.0);
            CollectionAssert.AreEqual(new[] { 0.45f, 0.45f, 0.05f, 0.05f }, m, "XAudio2's layout: index d x channels + s");
            double l, r;
            S14Rules.MatrixLR(m, 2, 2, out l, out r);
            Assert.AreEqual(0.9, l, 1e-6);
            Assert.AreEqual(0.1, r, 1e-6);
            Assert.IsTrue(S14Rules.MatrixPan(l, r) < 0, "left stays left");
            CollectionAssert.AreEqual(new[] { 0.45f, 0.05f }, S14Rules.PanMatrix(mono, 1, 0.5), "a 1-channel voice SE counted as 2: the mono gains x the distance factor");
            Assert.IsNull(S14Rules.PanMatrix(mono, 3, 1.0));
            Assert.IsNull(S14Rules.PanMatrix(null, 2, 1.0));
            // a matrix with both source channels in one destination (what the s14.pan row flags as one-sided)
            float[] se = { 0.7f, 0.7f, 0f, 0f };
            S14Rules.MatrixLR(se, 2, 2, out l, out r);
            Assert.IsTrue(S14Rules.OneSided(l, r), "both source channels in the left ear");
            Assert.IsFalse(S14Rules.OneSided(0.5, 0.4));
            Assert.IsFalse(S14Rules.OneSided(0, 0), "a silent voice is not one-sided");
            Assert.IsTrue(double.IsNaN(S14Rules.MatrixPan(0, 0)));
        }

        [TestMethod]
        public void Round4_PanJudgedAgainstTheGeometry()
        {
            Assert.IsTrue(S14Rules.PanCentred(0.0) && S14Rules.PanCentred(-0.34) && !S14Rules.PanCentred(0.35) && !S14Rules.PanCentred(double.NaN));
            Assert.IsTrue(S14Rules.PanSignAgrees(0.1, -0.5), "below SignMin: not judged");
            Assert.IsTrue(S14Rules.PanSignAgrees(0.8, 0.3));
            Assert.IsFalse(S14Rules.PanSignAgrees(0.8, -0.3), "a voice on the right heard on the left");
            Assert.IsFalse(S14Rules.PanSignAgrees(-0.5, double.NaN), "a silent voice beyond SignMin");
            Assert.AreEqual(1.0, S14Rules.DistanceFactor(10, 100, false, false), "an inner radius: X3DAudio's curve alone");
            Assert.AreEqual(0.9, S14Rules.DistanceFactor(10, 100, false, true), 1e-12, "SE's linear factor (MyXAudio2.Apply3D)");
            Assert.AreEqual(0.0, S14Rules.DistanceFactor(10, 100, true, true), "silent");
            Assert.AreEqual(0.0, S14Rules.DistanceFactor(200, 100, false, true), "beyond the maximum");
        }

        [TestMethod]
        public void Round4_ChainJetLengthAndKit()
        {
            Assert.AreEqual(1, S14Rules.ChainLength(true, false), "the effect alone");
            Assert.AreEqual(2, S14Rules.ChainLength(true, true), "a Real* recording under the effect: stacked");
            Assert.AreEqual(0, S14Rules.ChainLength(false, false));
            Assert.AreEqual(S14Conventions.Chain.MaxLowPass, S14Rules.ChainLength(true, false));
            Assert.IsTrue(S14Rules.KitThrusterOff(true, true, false, true), "the jets draw the thrusters: no kit puff");
            Assert.IsFalse(S14Rules.KitThrusterOff(true, false, false, true), "de.s14 jets off: the kit as before");
            Assert.IsFalse(S14Rules.KitThrusterOff(true, true, true, true), "S14.M6: the old puffs");
            Assert.IsFalse(S14Rules.KitThrusterOff(true, true, false, false), "no above-water material: the kit as before");
            Assert.IsFalse(S14Rules.KitThrusterOff(false, true, false, true));
        }

        [TestMethod]
        public void Release_RefusesTheDevCommand()
        {
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s14", null), "de.s14 is a developer command (release refuses it)");
            Assert.IsTrue(UserCommandRules.RequestAllowed(true, "de.s14", null));
        }
    }
}
