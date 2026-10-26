using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S5a Unity-free parts of the Subnautica plugin (docs/S5A-STATUS.md §6 "SN log contract"; CONTRACT §5.4 "C1 swim
 /// extension", §2.10, §4.2): the C1 reader's extension decode, the state and visibility mapping with its gates, the
 /// exact log lines (format, invariant culture, the analyzer's key=value grammar), the 0.5 s cadence with change
 /// lines, the OxygenManager mirror arithmetic and its never-dies bounds, the low-O2 monitor twin of LowOxygenAlert,
 /// the breath scheduler's periods and the arms' motion reconstruction.
 /// </summary>
    [TestClass]
    public unsafe class S5aTests
    {
        private static readonly Regex Kv = new Regex(@"^DE\.(Arms|O2|Breath|PDA|SwimWire) (?:[A-Za-z_][A-Za-z0-9_]*=\S+)(?: [A-Za-z_][A-Za-z0-9_]*=\S+)*$");

        private static SwimWireState Ext(byte flags, byte mode = 1, byte fins = 2, byte speedH = 0, byte speedV = 0, sbyte f = 0, sbyte s = 0, sbyte v = 0,
                                         byte o2 = 255, byte dc = 1, sbyte vy = 0)
        {
            return new SwimWireState { Version = 1, Flags = flags, Mode = mode, Fins = fins, SpeedH = speedH, SpeedV = speedV, MoveForward = f, MoveStrafe = s, MoveVertical = v, O2 = o2, DepthClass = dc, VyFrac = vy };
        }

        // ------------------------------------------------------------------------------------- state and visibility

        [TestMethod]
        public void S5a_StateName_Out_Swim_Surface_Fly()
        {
            Assert.AreEqual("out", S5aRules.StateName(false, 0xFF, 1), "no extension: out whatever the bytes say");
            Assert.AreEqual("out", S5aRules.StateName(true, 0, 1), "inWater clear: out");
            Assert.AreEqual("swim", S5aRules.StateName(true, SwimWireFlags.InWater | SwimWireFlags.Underwater, 1));
            Assert.AreEqual("surface", S5aRules.StateName(true, SwimWireFlags.InWater | SwimWireFlags.AtSurface, 2));
            Assert.AreEqual("fly", S5aRules.StateName(true, SwimWireFlags.InWater | SwimWireFlags.AtSurface, 3), "mode 3 wins over the surface flag");
        }

        [TestMethod]
        public void S5a_ArmsVisible_Gates_InWaterAndCameraUnderAreNecessary()
        {
            byte inW = SwimWireFlags.InWater | SwimWireFlags.Underwater;
            // full gate: every condition
            Assert.IsTrue(S5aRules.ArmsVisible(ArmsGate.Full, true, true, inW, true, true, true, true));
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Full, false, true, inW, true, true, true, true), "ghost not ready");
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Full, true, false, inW, true, true, true, true), "no extension");
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Full, true, true, SwimWireFlags.Underwater, true, true, true, true), "inWater clear");
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Full, true, true, inW, false, true, true, true), "camera above the sea");
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Full, true, true, inW, true, false, true, true), "C5 not Underwater/Surfacing");
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Full, true, true, inW, true, true, false, true), "third person");
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Full, true, true, inW, true, true, true, false), "camera not driven");
            StringAssert.Contains(S5aRules.HiddenReason(ArmsGate.Full, true, true, inW, true, false, true, true), "C5");
            Assert.IsNull(S5aRules.HiddenReason(ArmsGate.Full, true, true, inW, true, true, true, true));
            // camera gate: inWater + camera under only
            Assert.IsTrue(S5aRules.ArmsVisible(ArmsGate.Camera, true, true, inW, true, false, false, false));
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Camera, true, true, inW, false, true, true, true), "the contract: never with the camera above");
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.Camera, true, true, 0, true, true, true, true), "the contract: never with inWater clear");
            // dev gates
            Assert.IsTrue(S5aRules.ArmsVisible(ArmsGate.ForceOn, true, false, 0, false, false, false, false));
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.ForceOn, false, false, 0, false, false, false, false), "never before the ghost");
            Assert.IsFalse(S5aRules.ArmsVisible(ArmsGate.ForceOff, true, true, inW, true, true, true, true));
            // SnShows
            Assert.IsTrue(S5aRules.SnShows(true, ViewState.Underwater));
            Assert.IsTrue(S5aRules.SnShows(true, ViewState.Surfacing));
            Assert.IsFalse(S5aRules.SnShows(true, ViewState.Approach));
            Assert.IsFalse(S5aRules.SnShows(false, ViewState.Underwater), "no C5: Subnautica is not shown");
            ArmsGate g;
            Assert.IsTrue(S5aRules.TryParseGate("camera", out g) && g == ArmsGate.Camera);
            Assert.IsTrue(S5aRules.TryParseGate("ON", out g) && g == ArmsGate.ForceOn);
            Assert.IsTrue(S5aRules.TryParseGate("full", out g) && g == ArmsGate.Full);
            Assert.IsFalse(S5aRules.TryParseGate("maybe", out g));
        }

        // ------------------------------------------------------------------------------------- lines

        [TestMethod]
        public void S5a_Lines_ContractFormat_InvariantCulture_SingleSpaceTokens()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE"); // decimal comma: the lines must still use '.'
                SwimWireState s = Ext(SwimWireFlags.InWater | SwimWireFlags.Underwater | SwimWireFlags.Moving | SwimWireFlags.Controls, speedH: 191, speedV: 0, f: 127, s: 0, v: -64, o2: 13, dc: 1, vy: -32);
                string arms = S5aRules.ArmsLine(1234, "swim", true, ref s, "swim fwd loop");
                Assert.AreEqual("DE.Arms tick=1234 state=swim visible=1 moving=1 move=1.000,0.000,-0.504 speedH=0.749 speedV=0.000 vy=-0.252 fins=ultra anim=swim_fwd_loop", arms);
                Assert.IsTrue(Kv.IsMatch(arms), arms);
                string o2 = S5aRules.O2Line(77, 13 / 255.0, 2.294, 45f, true, true);
                Assert.AreEqual("DE.O2 tick=77 mirrored=0.051 snOxygen=2.3 capacity=45.0 override=1 shielded=1", o2);
                Assert.IsTrue(Kv.IsMatch(o2), o2);
                string br = S5aRules.BreathLine(78, "lowO2", 0.05, 20.04);
                Assert.AreEqual("DE.Breath tick=78 event=lowO2 o2=0.050 depth=20.0", br);
                Assert.IsTrue(Kv.IsMatch(br), br);
                string pda = S5aRules.PdaLine(79, 3, 0);
                Assert.AreEqual("DE.PDA tick=79 suppressed=3 played=0 window=60", pda);
                Assert.IsTrue(Kv.IsMatch(pda), pda);
                string wire = S5aRules.SwimWireLine(80, ref s);
                Assert.AreEqual("DE.SwimWire tick=80 version=1 flags=0x53 mode=1 fins=2 speedH=191 speedV=0 move=127,0,-64 o2=13 dc=1 vy=-32", wire);
                Assert.IsTrue(Kv.IsMatch(wire), wire);
                SwimWireState none = default(SwimWireState);
                Assert.AreEqual("DE.SwimWire tick=1 version=0 flags=0x00 mode=0 fins=0 speedH=0 speedV=0 move=0,0,0 o2=0 dc=0 vy=0", S5aRules.SwimWireLine(1, ref none));
                Assert.AreEqual("-", S5aRules.AnimToken(null));
                Assert.AreEqual("-", S5aRules.AnimToken(""));
                Assert.AreEqual("a_b_c", S5aRules.AnimToken("a b=c"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [TestMethod]
        public void S5a_ArmsCadence_EveryHalfSecond_PlusEveryChangeOfStateOrVisible()
        {
            var c = new ArmsLogCadence();
            Assert.IsTrue(c.Due(1000, "out", false), "first call logs");
            Assert.IsFalse(c.Due(1100, "out", false));
            Assert.IsFalse(c.Due(1499, "out", false));
            Assert.IsTrue(c.Due(1500, "out", false), "0.5 s");
            Assert.IsFalse(c.Due(1600, "out", false));
            Assert.IsTrue(c.Due(1601, "swim", false), "state change logs at once");
            Assert.IsTrue(c.Due(1602, "swim", true), "visible change logs at once");
            Assert.IsFalse(c.Due(2000, "swim", true));
            Assert.IsTrue(c.Due(2102, "swim", true), "0.5 s after the last line");
            Assert.AreEqual(2L, c.Changes);
            Assert.AreEqual(5L, c.Lines);
            c.Reset();
            Assert.IsTrue(c.Due(2200, "swim", true), "after a reset the next frame logs");

            var p = new PeriodicDue(500);
            Assert.IsTrue(p.Due(10), "first call fires");
            Assert.IsFalse(p.Due(509));
            Assert.IsTrue(p.Due(510));
            Assert.IsTrue(p.Due(2000), "late: fires once, next due 2500");
            Assert.IsFalse(p.Due(2499));
            Assert.IsTrue(p.Due(2500));
        }

        // ------------------------------------------------------------------------------------- oxygen mirror

        [TestMethod]
        public void S5a_O2Mirror_TargetUnits_FullByte_Floor_Capacity_Delta()
        {
            Assert.AreEqual(45.0, O2Mirror.TargetUnits(255, 45.0, 0.0), 1e-9, "255 = full (unreadable suit / idle): the capacity");
            Assert.AreEqual(13 / 255.0 * 45.0, O2Mirror.TargetUnits(13, 45.0, 0.0), 1e-9, "the choke window's byte 13 -> 2.29 units");
            Assert.AreEqual(0.0, O2Mirror.TargetUnits(0, 45.0, 0.0), 1e-9, "byte 0 with the death path patched: a true zero (the real choke)");
            Assert.AreEqual(1.0, O2Mirror.TargetUnits(0, 45.0, 1.0), 1e-9, "byte 0 without the patch: the floor");
            Assert.AreEqual(1.0, O2Mirror.TargetUnits(2, 45.0, 1.0), 1e-9, "0.35 units -> the floor");
            Assert.AreEqual(75.0, O2Mirror.TargetUnits(255, 75.0, 1.0), 1e-9, "a tank in the host save: its capacity");
            Assert.AreEqual(0.0, O2Mirror.TargetUnits(100, 0.0, 1.0), 1e-9, "no sources yet: nothing");
            Assert.AreEqual(0.0, O2Mirror.Delta(10.0, 10.0005), 0.0, "within epsilon: no write");
            Assert.AreEqual(2.0, O2Mirror.Delta(10.0, 12.0), 1e-12);
            Assert.AreEqual(-3.0, O2Mirror.Delta(10.0, 7.0), 1e-12);
        }

        /// <summary>
 /// The never-dies bound (CONTRACT §1, §4.2): for every byte and capacity the mirrored units stay within
 /// [floor, capacity]; with the unpatched floor (1 unit) they never come near 0, so Player.SuffocationUpdate
 /// (suffocating at NearlyEqual(available, 0)) never starts the 8 s sequence that ends in liveMixin.Kill; with the
 /// death path patched (floor 0) the sequence may start but its callback is skipped by S5aPatches.
 /// </summary>
        [TestMethod]
        public void S5a_Safety_MirroredUnitsStayWithinFloorAndCapacity_ForEveryByte()
        {
            double[] caps = { 45.0, 75.0, 135.0, 0.5 };
            foreach (double cap in caps)
            {
                for (int b = 0; b <= 255; b++)
                {
                    double t0 = O2Mirror.TargetUnits((byte)b, cap, 0.0);
                    Assert.IsTrue(t0 >= 0.0 && t0 <= cap + 1e-9, "patched floor: byte " + b + " cap " + cap + " -> " + t0);
                    double t1 = O2Mirror.TargetUnits((byte)b, cap, S5aRules.O2FloorUnpatched);
                    Assert.IsTrue(t1 >= Math.Min(S5aRules.O2FloorUnpatched, cap) - 1e-9 && t1 <= cap + 1e-9, "unpatched floor: byte " + b + " cap " + cap + " -> " + t1);
                    if (cap >= 1.0) Assert.IsTrue(t1 >= 1.0, "never NearlyEqual(0): byte " + b);
                    Assert.IsTrue(t0 <= t1 + 1e-9, "the floor only raises");
                }
            }
        }

        // ------------------------------------------------------------------------------------- low-O2 monitor

        [TestMethod]
        public void S5a_LowO2Monitor_TwinOfJustDroppedBelow_ThenRefill()
        {
            var m = new LowO2Monitor(new[] { 30f, 10f });
            float low; bool refill;
            m.Update(45f, out low, out refill);
            Assert.IsTrue(float.IsNaN(low) && !refill, "the first sample only primes the monitor");
            m.Update(30f, out low, out refill);
            Assert.IsTrue(float.IsNaN(low), "exactly at the threshold: not below");
            m.Update(29.9f, out low, out refill);
            Assert.AreEqual(30f, low, "previous >= 30 and current < 30");
            Assert.AreEqual(30f, m.Active);
            m.Update(20f, out low, out refill);
            Assert.IsTrue(float.IsNaN(low) && !refill);
            m.Update(2.25f, out low, out refill);
            Assert.AreEqual(10f, low, "the choke window's 0.05 x 45 crosses the 10 s alert");
            Assert.AreEqual(10f, m.Active, "the lowest fired threshold is in force");
            m.Update(2.25f, out low, out refill);
            Assert.IsTrue(float.IsNaN(low) && !refill, "holding: nothing");
            m.Update(45f, out low, out refill);
            Assert.IsTrue(refill, "back above the active threshold: refill");
            Assert.IsTrue(float.IsNaN(m.Active));
            m.Update(45f, out low, out refill);
            Assert.IsFalse(refill, "refill once");
            // a jump straight from full to the choke value crosses both: the smallest is reported
            m.Update(2.25f, out low, out refill);
            Assert.AreEqual(10f, low);
            Assert.AreEqual(3L, m.LowCount);
            Assert.AreEqual(1L, m.RefillCount);
            var none = new LowO2Monitor(null);
            none.Update(0f, out low, out refill);
            Assert.IsTrue(float.IsNaN(low) && !refill, "no thresholds: never");
        }

        // ------------------------------------------------------------------------------------- breath scheduler

        [TestMethod]
        public void S5a_BreathScheduler_PeriodByDepthClass_BubblesEveryDelay_ResetOutOfWater()
        {
            Assert.AreEqual(3.0, S5aRules.BreathPeriodS((byte)SwimDepthClass.Safe));
            Assert.AreEqual(2.25, S5aRules.BreathPeriodS((byte)SwimDepthClass.Unsafe));
            Assert.AreEqual(1.5, S5aRules.BreathPeriodS((byte)SwimDepthClass.Crush));
            Assert.AreEqual(0.0, S5aRules.BreathPeriodS((byte)SwimDepthClass.Surface));
            Assert.AreEqual(0.0, S5aRules.BreathPeriodS(9));

            var b = new BreathScheduler();
            bool breath, bubbles;
            b.Update(1000, false, 1, 20.0, out breath, out bubbles);
            Assert.IsFalse(breath || bubbles);
            b.Update(1000, true, 1, 20.0, out breath, out bubbles);
            Assert.IsFalse(breath || bubbles, "entering: the first breath one period later (Subnautica's timeMonitor interval)");
            int breaths = 0, bursts = 0;
            for (ulong t = 1016; t <= 1000 + 20000; t += 16)
            {
                b.Update(t, true, 1, 20.0, out breath, out bubbles);
                if (breath) breaths++;
                if (bubbles) bursts++;
            }
            Assert.AreEqual(6, breaths, "safe class: a breath every 3 s over 20 s (3, 6, 9, 12, 15, 18 s)");
            Assert.AreEqual(1, bursts, "one bubble burst at delay (20 s)");

            var c = new BreathScheduler();
            c.Update(0, true, (byte)SwimDepthClass.Crush, 20.0, out breath, out bubbles);
            breaths = 0;
            for (ulong t = 16; t <= 6000; t += 16) { c.Update(t, true, (byte)SwimDepthClass.Crush, 20.0, out breath, out bubbles); if (breath) breaths++; }
            Assert.AreEqual(4, breaths, "crush class: every 1.5 s over 6 s (1.5, 3, 4.5, 6 s boundaries; no drift by the 16 ms frame)");

            var s = new BreathScheduler();
            s.Update(0, true, (byte)SwimDepthClass.Surface, 20.0, out breath, out bubbles);
            breaths = 0;
            for (ulong t = 16; t <= 10000; t += 16) { s.Update(t, true, (byte)SwimDepthClass.Surface, 20.0, out breath, out bubbles); if (breath) breaths++; }
            Assert.AreEqual(0, breaths, "surface class: no breath (99,999 s)");

            // leaving the water resets; coming back restarts the period
            var r = new BreathScheduler();
            r.Update(0, true, 1, 20.0, out breath, out bubbles);
            r.Update(2900, true, 1, 20.0, out breath, out bubbles);
            Assert.IsFalse(breath);
            r.Update(2950, false, 1, 20.0, out breath, out bubbles);
            r.Update(3000, true, 1, 20.0, out breath, out bubbles);
            Assert.IsFalse(breath, "re-entered: the period restarts");
            r.Update(5999, true, 1, 20.0, out breath, out bubbles);
            Assert.IsFalse(breath);
            r.Update(6000, true, 1, 20.0, out breath, out bubbles);
            Assert.IsTrue(breath);
            Assert.AreEqual(1L, r.Breaths);
        }

        // ------------------------------------------------------------------------------------- arms motion

        [TestMethod]
        public void S5a_ArmsMotion_SpeedReference_Direction_Glide_Smoothing()
        {
            Assert.AreEqual(5.70, S5aRules.SpeedReferenceMs((byte)SwimFins.Off), 1e-9, "7.6 x 0.75");
            Assert.AreEqual(7.125, S5aRules.SpeedReferenceMs((byte)SwimFins.Basic), 1e-9, "(7.6 + 1.9) x 0.75");
            Assert.AreEqual(8.1, S5aRules.SpeedReferenceMs((byte)SwimFins.Ultra), 1e-9, "(7.6 + 3.2) x 0.75");

            var m = new ArmsMotion();
            SwimWireState fwd = Ext(SwimWireFlags.InWater | SwimWireFlags.Moving, fins: 2, speedH: 255, f: 127);
            m.Update(10.0, ref fwd); // dt large: k = 1, the target at once
            Assert.AreEqual(8.1, m.Vz, 1e-9, "full forward with Ultraglide: 8.1 m/s along +z");
            Assert.AreEqual(0.0, m.Vx, 1e-9);
            Assert.AreEqual(8.1, m.Speed, 1e-9);

            SwimWireState right = Ext(SwimWireFlags.InWater | SwimWireFlags.Moving, fins: 0, speedH: 128, s: 127);
            m.Update(10.0, ref right);
            Assert.AreEqual(0.0, m.Vz, 1e-9);
            Assert.AreEqual(128 / 255.0 * 5.70, m.Vx, 1e-9, "strafe right at half speed, no fins");

            SwimWireState up = Ext(SwimWireFlags.InWater | SwimWireFlags.Moving, fins: 0, speedH: 0, v: 127, vy: 127);
            m.Update(10.0, ref up);
            Assert.AreEqual(5.70, m.Vy, 1e-9, "+vy = rising");
            Assert.AreEqual(0.0, m.Vx, 1e-9, "no horizontal speed");

            SwimWireState glide = Ext(SwimWireFlags.InWater, fins: 0, speedH: 100);
            m.Update(10.0, ref glide);
            Assert.AreEqual(100 / 255.0 * 5.70, m.Vx, 1e-9, "released input: the last direction (right) is kept while the body glides");
            Assert.AreEqual(0.0, m.Vz, 1e-9);

            var sm = new ArmsMotion();
            sm.Update(1.0 / 60.0, ref fwd);
            Assert.AreEqual(8.1 * 4.0 / 60.0, sm.Vz, 1e-9, "one 60 Hz frame at 4/s: 1/15 of the way (ArmsController's smoothing rate)");
            sm.Reset();
            Assert.AreEqual(0.0, sm.Speed);
        }

        // ------------------------------------------------------------------------------------- FrameReader decode

        [TestMethod]
        public void S5a_FrameReader_DecodesTheSwimExtension_Version0Invalid_ResetForgets()
        {
            IntPtr mem = Marshal.AllocHGlobal(0x2000);
            try
            {
                byte* rec = (byte*)mem;
                for (int i = 0; i < 0x2000; i++) rec[i] = 0;
                var reader = new FrameReader(rec, 12000000);
                Assert.IsFalse(reader.SwimSeen);

                SeFrame f = default(SeFrame);
                f.flags = SeFrameFlags.InWorld | SeFrameFlags.CharacterPresent | SeFrameFlags.CharacterSwimming;
                f.tick = 4242; f.qpcCapture = 1000000000; f.worldEpoch = 1;
                f.camPos = new F32x3(-1029f, -20f, -1116f);
                f.camRot = new F32x4(0, 0, 0, 1);
                f.vFovDeg = 70f; f.near = 0.05f; f.aspect = 1.7778f; f.cameraMode = CameraMode.FirstPerson;
                Seqlock.WriteBytes(rec, (byte*)&f, sizeof(SeFrame));
                Assert.AreEqual(C1Poll.Fresh, reader.Poll());
                Assert.IsTrue(reader.SwimSeen);
                Assert.IsFalse(reader.SwimValid, "an SE build before S5a: the pad is zero, version 0");
                Assert.AreEqual((byte)0, reader.SwimVersion);
                Assert.AreEqual(4242UL, reader.SwimTick);

                SwimWireState s = Ext(SwimWireFlags.InWater | SwimWireFlags.Underwater | SwimWireFlags.Moving | SwimWireFlags.Controls | SwimWireFlags.JetpackSuppressed | SwimWireFlags.O2Override,
                                      mode: 1, fins: 2, speedH: 191, speedV: 12, f: 127, s: -64, v: 3, o2: 13, dc: 1, vy: -5);
                SwimWire.Write(ref f, ref s);
                f.tick = 4243; f.qpcCapture += 200000;
                Seqlock.WriteBytes(rec, (byte*)&f, sizeof(SeFrame));
                Assert.AreEqual(C1Poll.Fresh, reader.Poll());
                Assert.IsTrue(reader.SwimValid);
                Assert.AreEqual((byte)1, reader.SwimVersion);
                Assert.AreEqual(4243UL, reader.SwimTick, "the tick of the record the extension came from (the join key)");
                SwimWireState d = reader.Swim;
                Assert.AreEqual(s.Flags, d.Flags); Assert.AreEqual(s.Mode, d.Mode); Assert.AreEqual(s.Fins, d.Fins);
                Assert.AreEqual(s.SpeedH, d.SpeedH); Assert.AreEqual(s.SpeedV, d.SpeedV);
                Assert.AreEqual(s.MoveForward, d.MoveForward); Assert.AreEqual(s.MoveStrafe, d.MoveStrafe); Assert.AreEqual(s.MoveVertical, d.MoveVertical);
                Assert.AreEqual(s.O2, d.O2); Assert.AreEqual(s.DepthClass, d.DepthClass); Assert.AreEqual(s.VyFrac, d.VyFrac);
                Assert.AreEqual("swim", S5aRules.StateName(reader.SwimValid, d.Flags, d.Mode));
                Assert.AreEqual(2L, reader.SwimReads);

                // the extension is read even when the history drops the frame (not in world): the bytes are still SE's
                f.flags = 0; f.tick = 4244; f.qpcCapture += 200000;
                Seqlock.WriteBytes(rec, (byte*)&f, sizeof(SeFrame));
                Assert.AreEqual(C1Poll.Fresh, reader.Poll());
                Assert.AreEqual(PushResult.NotInWorld, reader.LastPush);
                Assert.IsTrue(reader.SwimValid);
                Assert.AreEqual(4244UL, reader.SwimTick);

                // a future version is not valid (the host leaves everything off and logs it)
                ((byte*)&f)[SwimWire.OffVersion] = 7;
                f.tick = 4245; f.qpcCapture += 200000;
                Seqlock.WriteBytes(rec, (byte*)&f, sizeof(SeFrame));
                Assert.AreEqual(C1Poll.Fresh, reader.Poll());
                Assert.IsFalse(reader.SwimValid);
                Assert.AreEqual((byte)7, reader.SwimVersion);

                reader.Reset();
                Assert.IsFalse(reader.SwimSeen);
                Assert.IsFalse(reader.SwimValid);
                Assert.AreEqual(0UL, reader.SwimTick);
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }
    }
}
