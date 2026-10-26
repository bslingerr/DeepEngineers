using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>S13 "Character and animation v2", the Subnautica side's pure rules (CONTRACT §5.4 "S13 conventions").</summary>
    [TestClass]
    public class S13Tests
    {
        private const byte InWater = SwimWireFlags.InWater, Under = SwimWireFlags.Underwater, Grounded = SwimWireFlags.Grounded;

        [TestMethod]
        public void Point2_SwimHoldsSubmergedWhateverTheContact()
        {
            byte swim = (byte)SwimMode.Swim, surface = (byte)SwimMode.Surface;
            Assert.IsTrue(S13Source.Swimming(true, (byte)(InWater | Under | Grounded), swim, false, false, false), "the seabed or a hull under water: still swimming");
            Assert.IsFalse(S13Source.Swimming(true, (byte)(InWater | Under | Grounded), swim, false, false, true), "S13.M2: the S5 rule (SE's standing pose)");
            Assert.IsFalse(S13Source.Swimming(true, (byte)(InWater | Grounded), surface, false, false, false), "head out with ground contact: wading, SE's pose");
            Assert.IsTrue(S13Source.Swimming(true, InWater, surface, false, false, false), "surface swimming");
            Assert.IsFalse(S13Source.Swimming(true, (byte)(InWater | Under), (byte)SwimMode.Fly, false, false, false), "jetpack");
            Assert.IsFalse(S13Source.Swimming(true, (byte)(InWater | Under), swim, true, false, false), "seated");
            Assert.IsFalse(S13Source.Swimming(false, (byte)(InWater | Under), swim, false, false, false), "no extension");
            Assert.IsTrue(S13Source.SubmergedGrounded(true, (byte)(InWater | Under | Grounded), swim, false, false));
            Assert.IsFalse(S13Source.SubmergedGrounded(true, (byte)(InWater | Grounded), surface, false, false));
            // through CharSourceRules (the S5 entry point)
            var i = new CharSourceInputs { BonesValid = true, SwimValid = true, SwimFlags = (byte)(InWater | Under | Grounded), SwimMode = swim, RetargetReady = true };
            Assert.AreEqual(CharSource.Retarget, CharSourceRules.Select(ref i), "grounded under water keeps the swim animation");
        }

        [TestMethod]
        public void Point6_HeldItemSwimIsBlend()
        {
            Assert.AreEqual(CharSource.Blend, S13Source.Select(true, true, true, true, false));
            Assert.AreEqual(CharSource.Retarget, S13Source.Select(true, true, true, false, false), "no SE bones: the full swim");
            Assert.AreEqual(CharSource.SeBones, S13Source.Select(true, true, true, true, true), "S13.M6: SE's bones (2026-10-18)");
            Assert.AreEqual(CharSource.Retarget, S13Source.Select(true, true, false, true, false));
            Assert.AreEqual(CharSource.SeBones, S13Source.Select(false, true, true, true, false), "out of the water: SE's bones");
            Assert.AreEqual(CharSource.Bind, S13Source.Select(false, false, false, false, false));
            Assert.AreEqual("Blend", CharSourceRules.Name(CharSource.Blend));
        }

        [TestMethod]
        public void Point6_BlendMaskIsTheTwoLegChains()
        {
            string err;
            CanonSkeleton sk = CanonSkeleton.Embedded(out err);
            Assert.IsNotNull(sk, err);
            var names = new List<string>();
            foreach (CanonBone b in sk.Bones) names.Add(b.SeBone);
            bool[] m = S13Source.BlendMask(sk.Parents, names);
            Assert.AreEqual(sk.Count, m.Length);
            foreach (string leg in new[] { "SE_RigLThigh", "SE_RigLCalf", "SE_RigLFoot", "SE_RigLR_Foot_tip1", "SE_RigRThigh", "SE_RigRCalf", "SE_RigRFoot", "SE_RigRR_Foot_tip1" })
                Assert.IsTrue(m[sk.IndexOf(leg)], leg);
            foreach (string up in new[] { "SE_RigPelvis", "SE_RigSpine2", "SE_RigRibcage", "SE_RigHead", "SE_RigLPalm", "SE_RigRPalm" })
            {
                int k = sk.IndexOf(up);
                if (k >= 0) Assert.IsFalse(m[k], up + " stays SE's");
            }
            int n = 0;
            for (int k = 0; k < m.Length; k++)
            {
                if (!m[k]) continue;
                n++;
                // every masked bone descends from a thigh
                int a = k;
                bool found = false;
                while (a >= 0) { if (names[a] == "SE_RigLThigh" || names[a] == "SE_RigRThigh") { found = true; break; } a = sk.Parents[a]; }
                Assert.IsTrue(found, names[k]);
            }
            Assert.IsTrue(n >= 8 && n <= 12, "two leg chains: " + n);
            // compose: masked rows from the retarget, the rest from SE
            var ret = new Trs[3]; var se = new Trs[3]; var t = new Trs[3];
            for (int k = 0; k < 3; k++) { ret[k] = Trs.Identity; ret[k].P = new Vec3(1, 0, 0); se[k] = Trs.Identity; se[k].P = new Vec3(2, 0, 0); }
            S13Source.ComposeBlend(new[] { false, true }, ret, se, t);
            Assert.AreEqual(2, t[0].P.X); Assert.AreEqual(1, t[1].P.X); Assert.AreEqual(2, t[2].P.X, "rows past the mask: SE's");
        }

        [TestMethod]
        public void Point5_AnimationFromTheLookFrameVelocity()
        {
            var s = new S13CharWireState { Version = 1, Flags = S13Conventions.C1Flags.Valid, VRightCm = 10, VUpCm = -20, VForwardCm = -250 };
            double x, y, z;
            Assert.IsTrue(S13Anim.Target(true, ref s, false, out x, out y, out z));
            Assert.AreEqual(0.1, x, 1e-9); Assert.AreEqual(-0.2, y, 1e-9); Assert.AreEqual(-2.5, z, 1e-9);
            Assert.IsFalse(S13Anim.Target(true, ref s, true, out x, out y, out z), "S13.M8: S5a's input rule");
            Assert.IsFalse(S13Anim.Target(false, ref s, false, out x, out y, out z), "no extension (an SE before S13)");
            s.Flags = 0;
            Assert.IsFalse(S13Anim.Target(true, ref s, false, out x, out y, out z), "not valid");

            Assert.AreEqual(-1, S13Anim.Agreement(0, -2, 0, 2), 1e-9);
            Assert.AreEqual(1, S13Anim.Agreement(0, -2, 0, -2), 1e-9);
            Assert.IsTrue(double.IsNaN(S13Anim.Agreement(0, 1, 0, 0.2)), "below 0.5 m/s nothing to compare");
            // the motion smoothing toward the target
            var mo = new ArmsMotion();
            for (int k = 0; k < 200; k++) mo.UpdateTarget(1 / 60.0, 0.5, 0, -3);
            Assert.AreEqual(-3, mo.Vz, 0.01);
            Assert.AreEqual(0.5, mo.Vx, 0.01);
        }

        [TestMethod]
        public void Point4_NearPlanesAndFit()
        {
            Assert.AreEqual(1.0f, S13LightRules.NearPlane(LightKind.CharacterSpot, false), 1e-6f, "the suit lamp: the character, its tool and seat never cast (SE's ignore list)");
            Assert.AreEqual(0.5f, S13LightRules.NearPlane(LightKind.BlockSpot, false), 1e-6f, "SE's spot shadow near plane");
            Assert.AreEqual(0.2f, S13LightRules.NearPlane(LightKind.CharacterSpot, true), 1e-6f, "S13.M4: Unity's default");
            Assert.AreEqual(1.5f, S13LightRules.NearPlane(LightKind.CharacterSpot, false, 1.5f), 1e-6f, "de.s13 nearPlane");
            float k, ki;
            S13LightRules.Fit(LightKind.CharacterSpot, 1f, true, false, false, out k, out ki);
            Assert.AreEqual(2.42f, k, 1e-4f);
            Assert.AreEqual(0.504f, ki, 0.001f);
            S13LightRules.Fit(LightKind.BlockSpot, 1f, true, false, false, out k, out ki);
            Assert.AreEqual(1f, k, "block spots unfitted by default (the P10 tuned ranges)");
            S13LightRules.Fit(LightKind.BlockSpot, 1f, true, true, false, out k, out ki);
            Assert.AreEqual(2.42f, k, 1e-4f, "de.s13 blockFit on");
            S13LightRules.Fit(LightKind.CharacterSpot, 1f, true, false, true, out k, out ki);
            Assert.AreEqual(1f, k, "S13.M4");
            S13LightRules.Fit(LightKind.CharacterPoint, 0.3f, true, true, false, out k, out ki);
            Assert.AreEqual(1f, k, "points unchanged");
            S13LightRules.Fit(LightKind.CharacterSpot, 1f, false, false, false, out k, out ki);
            Assert.AreEqual(1f, ki, "lightFit off");
            Assert.AreEqual(1.0, S13LightRules.WorstShadowLength(1f, 10), 1e-9, "a 10 cm caster at 1 m: 1 m on a wall at 10 m");
            Assert.AreEqual(5.0, S13LightRules.WorstShadowLength(0.2f, 10), 1e-5, "at Unity's 0.2 m: 5 m");
        }

        [TestMethod]
        public void Point7_GhostLook()
        {
            Assert.IsTrue(S13GhostRules.Textured(true, true, false));
            Assert.IsFalse(S13GhostRules.Textured(true, false, false), "no normal map yet: flat");
            Assert.IsFalse(S13GhostRules.Textured(true, true, true), "S13.M7");
            Assert.IsFalse(S13GhostRules.Textured(false, true, false), "de.s13 ghost flat");
            float r, g, b, a;
            S13GhostRules.Tint(true, false, out r, out g, out b, out a);
            Assert.IsTrue(g > r && g > b, "green when allowed");
            Assert.AreEqual(S13GhostRules.Alpha, a);
            S13GhostRules.Tint(false, true, out r, out g, out b, out a);
            Assert.IsTrue(r > g, "red when blocked");
            Assert.AreEqual(S13GhostRules.AlphaInside, a, "faded with the camera inside");
            Assert.IsTrue(S13GhostRules.Inside(0, 0, 0, -1, -1, -1, 1, 1, 1, 0));
            Assert.IsTrue(S13GhostRules.Inside(1.2f, 0, 0, -1, -1, -1, 1, 1, 1, 0.3f));
            Assert.IsFalse(S13GhostRules.Inside(1.4f, 0, 0, -1, -1, -1, 1, 1, 1, 0.3f));
        }

        [TestMethod]
        public void Point1_LeakRuleAndCounters()
        {
            Assert.IsTrue(S13Arms.Leak(true, true));
            Assert.IsFalse(S13Arms.Leak(true, false), "fpbody=diver: the diver arms are the body");
            Assert.IsFalse(S13Arms.Leak(false, true));
            var c = new S13CharCounters();
            c.NoteAgreement(0.9); c.NoteAgreement(-1); c.NoteAgreement(double.NaN);
            Assert.AreEqual(2, c.AnimAgreeSamples);
            Assert.AreEqual(1, c.AnimAgreeBad);
            Assert.AreEqual(-1, c.AnimAgreeMin, 1e-9);
            StringAssert.Contains(c.Describe(), "agreeBad=1/2");
        }

        [TestMethod]
        public void Review7_LadderNeverStrokes()
        {
            Assert.IsTrue(S13Source.OnLadder(S7aLoco.Version, S7aConventions.C1LocoState.Ladder, 0));
            Assert.IsTrue(S13Source.OnLadder(S7aLoco.Version, S7aConventions.C1LocoState.Swim, S7aConventions.C1LocoFlags.Ladder));
            Assert.IsFalse(S13Source.OnLadder(0, S7aConventions.C1LocoState.Ladder, 0), "no loco extension");
            Assert.IsFalse(S13Source.OnLadder(S7aLoco.Version, S7aConventions.C1LocoState.Swim, 0));
            var i = new CharSourceInputs { BonesValid = true, SwimValid = true, SwimFlags = (byte)(InWater | Under | Grounded), SwimMode = (byte)SwimMode.Swim, RetargetReady = true, Ladder = true };
            Assert.AreEqual(CharSource.SeBones, CharSourceRules.Select(ref i), "a ladder under water: SE's ladder pose");
            i.Ladder = false;
            Assert.AreEqual(CharSource.Retarget, CharSourceRules.Select(ref i));
        }

        [TestMethod]
        public void Review1_ToolSlotStates()
        {
            Assert.IsTrue(S13ToolSlot.Red(0f, 0f), "(0, 0, 0): SE's red key");
            Assert.IsTrue(S13ToolSlot.Red(1f, 0.3f), "hue 1 = hue 0");
            Assert.IsFalse(S13ToolSlot.Red(0f, -1f), "SE's OldGray");
            Assert.IsFalse(S13ToolSlot.Red(0.5f, 0.5f));
            Assert.AreEqual(S13ToolSlot.NoSlot, S13ToolSlot.State(false, false, true, true, true, 0.5f, false));
            Assert.AreEqual(S13ToolSlot.Foreign, S13ToolSlot.State(true, true, true, true, true, 0.5f, false));
            Assert.AreEqual(S13ToolSlot.Applied, S13ToolSlot.State(true, false, true, true, true, 0.5f, true));
            Assert.AreEqual(S13ToolSlot.Approx, S13ToolSlot.State(true, false, false, true, true, 0.5f, false));
            Assert.AreEqual(S13ToolSlot.Converting, S13ToolSlot.State(true, false, true, true, false, 0.5f, false));
            Assert.AreEqual(S13ToolSlot.None, S13ToolSlot.State(true, false, true, true, true, 0f, false), "nothing paintable");
            Assert.AreEqual(S13ToolSlot.Pending, S13ToolSlot.State(true, false, true, true, true, 0.5f, false));
        }

        [TestMethod]
        public void Review4_GhostProbeVerdict()
        {
            Assert.IsTrue(S13GhostRules.ProbeOk(0.01f, 0.08f, 0.03f, 0.2f, false), "translucent Standard");
            Assert.IsTrue(S13GhostRules.ProbeOk(0f, 0f, 0f, 0.2f, false), "translucent, unlit: Standard's tint is the scene's");
            Assert.IsFalse(S13GhostRules.ProbeOk(0.1f, 0.4f, 0.1f, 1f, false), "alpha 1: a stripped _ALPHABLEND_ON");
            Assert.IsFalse(S13GhostRules.ProbeOk(0.2f, 0.2f, 0.2f, 0.3f, true), "glass without the tint");
            Assert.IsTrue(S13GhostRules.ProbeOk(0.05f, 0.2f, 0.06f, 0.3f, true), "tinted glass");
            Assert.IsFalse(S13GhostRules.ProbeOk(0f, 0f, 0f, float.NaN, false));
        }

        [TestMethod]
        public void Mutants_AreKnown_AndDevOnly()
        {
            foreach (string n in S13SnMutants.All) Assert.IsTrue(DevMutants.IsKnown(n), n);
            Assert.IsFalse(UserCommandRules.RequestAllowed(false, "de.s13", null), "de.s13 is a developer command (release refuses it)");
            Assert.IsTrue(UserCommandRules.RequestAllowed(true, "de.s13", null));
        }
    }
}
