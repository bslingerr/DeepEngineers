using System;
using System.Collections.Generic;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7 skinned astronaut and audio polish, Subnautica side, Unity-free parts (CONTRACT §5.4 "S7 conventions": ExtraBones,
 /// <c>format bones.order</c>, <c>bones.unity</c>, <c>audio.bus</c>; ROADMAP S7.N6, S7.N11, S7.M2, S7.M4): the C1 reader
 /// with 81 rows (78 canonical + 3 extra, 64 refused), the canonical slice of an 81-row SkinnedBind, the flat pose of
 /// the extra rows, S7.M4's component-wise SeBones locals caught by the 1 cm position check, the count agreement, the
 /// tearing summary, the S7 mutant names (S7.M2 = S7a.M6) and the event-sound bus rule with its counters.
 /// </summary>
    [TestClass]
    public unsafe class S7SkinnedAudioTests
    {
        [TestCleanup]
        public void Cleanup()
        {
            string e;
            foreach (string m in S7Mutants.All) DevMutants.Set(m, false, out e);
            DevMutants.Set(DevMutants.S7aTimeDirect, false, out e);
        }

        private static Quat Axis(double ax, double ay, double az, double deg)
        {
            double h = deg * Math.PI / 360.0;
            Vec3 v = new Vec3(ax, ay, az).Normalized();
            return new Quat(v.X * Math.Sin(h), v.Y * Math.Sin(h), v.Z * Math.Sin(h), Math.Cos(h));
        }

        private static void PutRows(float[] dst, int off, Quat q, Vec3 t)
        {
            Vec3 r = q.Rotate(Vec3.UnitX), u = q.Rotate(Vec3.UnitY), b = q.Rotate(Vec3.UnitZ);
            dst[off] = (float)r.X; dst[off + 1] = (float)r.Y; dst[off + 2] = (float)r.Z;
            dst[off + 3] = (float)u.X; dst[off + 4] = (float)u.Y; dst[off + 5] = (float)u.Z;
            dst[off + 6] = (float)b.X; dst[off + 7] = (float)b.Y; dst[off + 8] = (float)b.Z;
            dst[off + 9] = (float)t.X; dst[off + 10] = (float)t.Y; dst[off + 11] = (float)t.Z;
        }

        private static void WriteC1(byte* rec, uint seq, ulong tick, uint boneCount, uint bonesVersion)
        {
            SeFrame f = default(SeFrame);
            f.tick = tick;
            f.flags = SeFrameFlags.InWorld | SeFrameFlags.CharacterPresent;
            f.characterId = 7;
            f.charQ = new F32x4(0, 0, 0, 1);
            f.charS = 1f;
            f.boneCount = boneCount;
            f.bonesVersion = bonesVersion;
            *(SeFrame*)rec = f;
            float* bones = (float*)(rec + SeFrameLimits.BonesOffset);
            var row = new float[12];
            for (int i = 0; i < SeFrameLimits.MaxBones; i++)
            {
                PutRows(row, 0, Quat.Identity, new Vec3(0.01 * i, 1.0, -0.5));
                for (int j = 0; j < 12; j++) bones[i * 12 + j] = row[j];
            }
            *(uint*)rec = seq;
        }

        [TestMethod]
        public void C1Reader_81Rows_CanonicalPlusExtra_64Refused()
        {
            var buf = new byte[CharFrameReader.CopyBytes];
            Assert.AreEqual(SeFrameLimits.BonesOffset + 160 * SeFrameLimits.BoneSize, CharFrameReader.CopyBytes, "the whole bone table is copied");
            fixed (byte* b = buf)
            {
                var r = new CharFrameReader(b);
                WriteC1(b, 2, 100, 81, 0xBEEF);
                Assert.IsTrue(r.Poll());
                CharFrame n = r.Newest;
                Assert.IsTrue(n.BonesValid, "81 rows (ExtraBones) are a valid pose");
                Assert.AreEqual(3, n.ExtraCount);
                Assert.AreEqual(0.77f, n.Bones[77 * 12 + 9], 1e-6f, "row 77 is the last canonical one");
                Assert.AreEqual(0.78f, n.Extra[9], 1e-6f, "row 78 is the first extra");
                Assert.AreEqual(0.80f, n.Extra[2 * 12 + 9], 1e-6f);
                WriteC1(b, 4, 101, 78, 0xBEEF);
                Assert.IsTrue(r.Poll());
                Assert.IsTrue(r.Newest.BonesValid);
                Assert.AreEqual(0, r.Newest.ExtraCount, "S5's 78 rows: no extras");
                WriteC1(b, 6, 102, 64, 0xBEEF);
                Assert.IsTrue(r.Poll());
                Assert.IsFalse(r.Newest.BonesValid, "S7.M9's 64 rows are not a pose (Bind instead)");
                WriteC1(b, 8, 103, 161, 0xBEEF);
                Assert.IsTrue(r.Poll());
                Assert.IsFalse(r.Newest.BonesValid);
            }
        }

        [TestMethod]
        public void Bind_81RowsSlicedTo78_ExtrasKept()
        {
            var bind = new float[81 * 12];
            for (int i = 0; i < bind.Length; i++) bind[i] = i;
            float[] canon = SkinnedAstronautRules.CanonBind(bind, 78);
            Assert.AreEqual(78 * 12, canon.Length);
            Assert.AreEqual(78f * 12 - 1, canon[canon.Length - 1]);
            float[] exact = new float[78 * 12];
            Assert.AreSame(exact, SkinnedAstronautRules.CanonBind(exact, 78), "78 rows: the array itself");
            Assert.IsNull(SkinnedAstronautRules.CanonBind(new float[70 * 12], 78));
            float[] extra = SkinnedAstronautRules.ExtraBind(bind, 78);
            Assert.AreEqual(36, extra.Length);
            Assert.AreEqual(78f * 12, extra[0]);
            Assert.IsNull(SkinnedAstronautRules.ExtraBind(exact, 78));
        }

        [TestMethod]
        public void FlatPose_IsTheConjugate_RestFromBindInvertsIt()
        {
            var abs = new float[12];
            Quat q = Axis(0.2, 1, 0.1, 33);
            PutRows(abs, 0, q, new Vec3(0.1, 1.6, 0.3));
            Trs t = SkinnedAstronautRules.FlatLocal(abs, 0, false);
            Assert.AreEqual(-0.3, t.P.Z, 1e-6);
            Assert.IsTrue(Quat.RotationDistance(S7Bones.MirrorRotation(q), t.Q) < 1e-6);
            // a SkinnedBind row is the inverse of the absolute bind: the rest pose is the flat pose of its inverse
            Aff inv = Aff.FromSeRows(abs, 0).Inverse();
            var bindRow = new float[12];
            inv.ToSeRows(bindRow, 0);
            Trs rest = SkinnedAstronautRules.RestFromBind(bindRow, 0);
            Assert.IsTrue(Vec3.Distance(rest.P, t.P) < 1e-5);
            Assert.IsTrue(Quat.RotationDistance(rest.Q, t.Q) < 1e-5);
            // extras blended with alpha like the SeBones locals
            var older = new float[12];
            PutRows(older, 0, q, new Vec3(0.1, 1.4, 0.3));
            var dst = new Trs[1];
            Assert.IsTrue(SkinnedAstronautRules.ExtraLocals(abs, 1, older, 1, 0.5, false, dst));
            Assert.AreEqual(1.5, dst[0].P.Y, 1e-6);
            Assert.IsTrue(SkinnedAstronautRules.ExtraLocals(abs, 1, null, 0, 0.5, false, dst));
            Assert.AreEqual(1.6, dst[0].P.Y, 1e-6, "no older extras: the newer pose");
            Assert.IsFalse(SkinnedAstronautRules.ExtraLocals(abs, 0, null, 0, 1, false, dst));
        }

        /// <summary>
 /// S7.N6.pos on the canonical skeleton: bones at rotated absolutes; the S5 conjugated locals rebuild every bone's
 /// mirrored SE position within 1e-5 m, S7.M4's component-wise locals leave the 1 cm tolerance for some bone.
 /// </summary>
        [TestMethod]
        public void ComponentWiseLocals_S7M4_LeaveTheCentimetre()
        {
            string err;
            CanonSkeleton sk = CanonSkeleton.Embedded(out err);
            Assert.IsNotNull(sk, err);
            int n = sk.Count;
            var abs = new float[n * 12];
            var rnd = new Random(7);
            for (int i = 0; i < n; i++)
                PutRows(abs, i * 12, Axis(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, 10 + 60 * rnd.NextDouble()),
                        new Vec3(rnd.NextDouble() - 0.5, rnd.NextDouble() * 1.8, rnd.NextDouble() - 0.5));
            var good = new Trs[n];
            var bad = new Trs[n];
            CharacterMath.SeBoneLocals(abs, sk.Parents, good);
            SkinnedAstronautRules.ComponentWiseLocals(abs, sk.Parents, bad);
            var ga = new Aff[n];
            var ba = new Aff[n];
            CharacterMath.Absolute(good, sk.Parents, ga);
            CharacterMath.Absolute(bad, sk.Parents, ba);
            double goodMax = 0, badMax = 0;
            for (int i = 0; i < n; i++)
            {
                Vec3 want = S7Bones.MirrorPoint(new Vec3(abs[i * 12 + 9], abs[i * 12 + 10], abs[i * 12 + 11]));
                goodMax = Math.Max(goodMax, (ga[i].T - want).Length);
                badMax = Math.Max(badMax, (ba[i].T - want).Length);
            }
            Assert.IsTrue(goodMax < 1e-5, "conjugated: " + goodMax);
            Assert.IsTrue(badMax > S7Bones.PositionToleranceM, "S7.M4 must be caught by the 1 cm check: " + badMax);
        }

        [TestMethod]
        public void CountsAgree_AndTear()
        {
            Assert.IsTrue(SkinnedAstronautRules.CountsAgree(81, 81, 81, true));
            Assert.IsTrue(SkinnedAstronautRules.CountsAgree(78, 78, 78, false));
            Assert.IsFalse(SkinnedAstronautRules.CountsAgree(64, 81, 64, true), "S7.M9");
            Assert.IsFalse(SkinnedAstronautRules.CountsAgree(81, 78, 81, true), "SkinnedBind not patched");
            Assert.IsFalse(SkinnedAstronautRules.CountsAgree(78, 78, 78, true), "ExtraBones claimed with 78 rows");
            TearInfo t = SkinnedAstronautRules.Tear(new byte[] { 0, 1, 0, 0, 5, 0, 0, 0 }, new[] { 0.5f, 0.5f, 0f, 0f, 1f, 0f, 0f, 0f }, 2, 78);
            Assert.IsTrue(t.Ok);
            Assert.AreEqual(5, t.MaxIndex);
            t = SkinnedAstronautRules.Tear(new byte[] { 0, 1, 0, 0 }, new[] { 0.5f, 0.4f, 0f, 0f }, 1, 78);
            Assert.IsFalse(t.Ok);
            Assert.AreEqual(1, t.BadSum);
            Assert.IsFalse(SkinnedAstronautRules.Tear(null, null, 0, 78).Valid);
        }

        [TestMethod]
        public void S7MutantNames_KnownBySn_M2SwitchesS7aM6()
        {
            foreach (string m in S7Mutants.All) Assert.IsTrue(DevMutants.IsKnown(m), m);
            Assert.IsFalse(DevMutants.IsKnown("S7.M9"), "S7.M9 is SE's");
            string e;
            Assert.IsFalse(S7Mutants.M4);
            Assert.IsTrue(DevMutants.Set("S7.M4", true, out e), e);
            Assert.IsTrue(S7Mutants.M4);
            Assert.IsTrue(DevMutants.Set("S7.M2", true, out e), e);
            Assert.IsTrue(DevMutants.IsOn(DevMutants.S7aTimeDirect), "S7.M2 = S7a.M6");
            Assert.IsTrue(S7Mutants.M2);
            Assert.IsTrue(DevMutants.Set("S7.M2", false, out e), e);
            Assert.IsFalse(DevMutants.IsOn(DevMutants.S7aTimeDirect));
        }

        [TestMethod]
        public void EventSounds_ReviewF18a_RequestsByFlagClass_SplashException()
        {
            byte under = S7Conventions.EventFlags.Underwater, surface = S7Conventions.EventFlags.Surface;
            var c = new EventSoundCounters();
            Assert.IsTrue(c.Count(S7Conventions.EventType.Splash, surface, 1f), "a surface splash plays Vehicle.splashSound (the documented exception)");
            Assert.IsFalse(c.Count(S7Conventions.EventType.Splash, surface, 0f), "not on a muted bus");
            Assert.IsFalse(c.Count(S7Conventions.EventType.Welder, 0, 1f), "a dry tool: lights only");
            Assert.IsTrue(c.Count(S7Conventions.EventType.Welder, under, 1f));
            Assert.AreEqual(1L, c.RequestsUnderwater);
            Assert.AreEqual(3L, c.RequestsDry);
            Assert.AreEqual(1L, c.SplashAllowed);
            Assert.AreEqual(0L, c.AllowedDry, "nothing allowed without Underwater but the splash");
            // a broken rule (what the oracle must catch): a dry request allowed
            c.Record(S7Conventions.EventType.Thruster, 0, 1f, true);
            Assert.AreEqual(1L, c.AllowedDry);
        }

        [TestMethod]
        public void EventSounds_OnlyUnderwaterWhileTheBusIsUp_Counted()
        {
            byte under = S7Conventions.EventFlags.Underwater, surface = S7Conventions.EventFlags.Surface;
            Assert.IsTrue(AudioPolishRules.EventSoundAllowed(under, 1f));
            Assert.IsFalse(AudioPolishRules.EventSoundAllowed(under, 0f), "bus target 0 (above water)");
            Assert.IsFalse(AudioPolishRules.EventSoundAllowed(surface, 1f), "no Underwater flag: lights only");
            Assert.IsTrue(AudioPolishRules.EventSoundInRange(250));
            Assert.IsFalse(AudioPolishRules.EventSoundInRange(250.1));
            var c = new EventSoundCounters();
            Assert.IsTrue(c.Count(S7Conventions.EventType.Thruster, under, 1f));
            Assert.IsTrue(c.Count(S7Conventions.EventType.Thruster, under, 1f));
            Assert.IsFalse(c.Count(S7Conventions.EventType.Explosion, 0, 1f));
            c.Started(S7Conventions.EventType.Splash, surface);
            var s = new Dictionary<string, long>();
            var r = new Dictionary<string, long>();
            c.TakeWindow(s, r);
            Assert.AreEqual(2L, s[S7Conventions.EventType.Name(S7Conventions.EventType.Thruster)]);
            Assert.AreEqual(1L, r[S7Conventions.EventType.Name(S7Conventions.EventType.Explosion)]);
            Assert.AreEqual(1L, c.Above, "a sound started for a row without Underwater is an A2 violation");
            s.Clear();
            c.TakeWindow(s, r);
            Assert.AreEqual(0, s.Count, "the window restarts");
            Assert.AreEqual(3L, c.TotalStarted);
            Assert.AreNotEqual(AudioPolishRules.StateKey(1f, false, false, true), AudioPolishRules.StateKey(0f, false, false, true));
            Assert.IsTrue(AudioPolishRules.RowDue(true, false, 0, 0));
            Assert.IsFalse(AudioPolishRules.RowDue(false, false, 5000, 0));
            Assert.IsTrue(AudioPolishRules.RowDue(false, true, 1000, 0));
            var a = new AmbienceSummary();
            a.Add("x", 2, true);
            a.Add("y", 1, false);
            Assert.AreEqual(2, a.Total);
            Assert.AreEqual(1, a.Playing);
            Assert.AreEqual(1, a.ByInterior["OnlyInside"]);
        }
    }
}
