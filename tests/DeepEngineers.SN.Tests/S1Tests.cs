using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S1 Unity-free parts of the Subnautica plugin (ROADMAP S1 SN plugin; CONTRACT §2.8, §5.4 C1/C7): the C1 snapshot
 /// history and the interpolation / extrapolation / hold decisions on synthetic C1 sequences, the presentedSeTick rule
 /// and its freshness bound, the ghost Player rule, the C1 seqlock reader on a real record, the S1.N1 synthetic oracle
 /// with its seeded failure (D = 0), the host-save decision table (slot selection, Freedom / invisible) and the switches.
 /// </summary>
    [TestClass]
    public unsafe class S1Tests
    {
        private const long Freq = 12000000; // a QPC frequency whose tick (1/60 s) and D (1.25 ticks) are whole counts
        private static readonly double T = Freq / 60.0; // one SE tick in QPC units

        private static CamSnapshot Snap(ulong tick, double qpc, double x = 0, double y = -150, double z = 0, float yawDeg = 0f,
                                        uint flags = SeFrameFlags.InWorld, uint epoch = 1, float fov = 70f)
        {
            double h = yawDeg * Math.PI / 360.0;
            return new CamSnapshot
            {
                Tick = tick, Qpc = (long)qpc, Flags = flags, WorldEpoch = epoch,
                Px = x, Py = y, Pz = z,
                Qx = 0f, Qy = (float)Math.Sin(h), Qz = 0f, Qw = (float)Math.Cos(h),
                VFovDeg = fov, Near = 0.05f, Aspect = 16f / 9f,
            };
        }

        private static CamHistory History(params CamSnapshot[] s)
        {
            var h = new CamHistory();
            foreach (CamSnapshot x in s)
            {
                string why;
                PushResult r = h.Push(x, out why);
                Assert.IsTrue(r == PushResult.First || r == PushResult.Pushed, "setup push " + r + " " + why);
            }
            return h;
        }

        // ------------------------------------------------------------------------------------- interpolation decisions

        [TestMethod]
        public void Sampler_Bracketed_LerpsPosition_NewestOrientation_PresentedIsNewerBracketTick()
        {
            double q0 = 1e9;
            CamHistory h = History(Snap(100, q0, x: 0, yawDeg: 0), Snap(101, q0 + T, x: 1, yawDeg: 1), Snap(102, q0 + 2 * T, x: 2, yawDeg: 2),
                                   Snap(103, q0 + 3 * T, x: 3, yawDeg: 3, fov: 65f));
            var sampler = new FrameSampler(Freq);
            // render time t = now - 1.25 T placed half-way between ticks 101 and 102
            long now = (long)(q0 + 1.5 * T + 1.25 * T);
            CamSample s = sampler.Sample(h, now);
            Assert.AreEqual(SampleOutcome.Bracketed, s.Outcome);
            Assert.AreEqual(0.5, s.Alpha, 1e-6, "alpha");
            Assert.AreEqual(1.5, s.Px, 1e-6, "position lerped with alpha");
            Assert.AreEqual(102UL, s.PresentedTick, "presentedSeTick = newer bracket tick");
            Assert.AreEqual(101UL, s.OlderTick);
            Assert.AreEqual(103UL, s.NewestTick);
            Assert.AreEqual(65f, s.VFovDeg, "lens from the newest snapshot");
            Assert.AreEqual(3.0, CamMath.YawDeg(s.Qx, s.Qy, s.Qz, s.Qw), 1e-4, "orientation = newest snapshot, never interpolated");
            Assert.IsFalse(s.Stale);
        }

        [TestMethod]
        public void Sampler_Extrapolates_AtMostOneTick_ThenHolds_StaleAfter100ms()
        {
            double q0 = 1e9;
            CamHistory h = History(Snap(10, q0, x: 0), Snap(11, q0 + T, x: 2));
            var sampler = new FrameSampler(Freq);
            // t = newest + 0.5 T -> extrapolated with the last velocity (2 m per tick)
            CamSample e = sampler.Sample(h, (long)(q0 + T + 0.5 * T + 1.25 * T));
            Assert.AreEqual(SampleOutcome.Extrapolated, e.Outcome);
            Assert.AreEqual(3.0, e.Px, 1e-6, "newest + v*dt");
            Assert.AreEqual(1.5, e.Alpha, 1e-6);
            Assert.AreEqual(11UL, e.PresentedTick, "extrapolated: presented = newest tick");
            // exactly one tick past: still extrapolated
            CamSample e1 = sampler.Sample(h, (long)(q0 + T + T + 1.25 * T));
            Assert.AreEqual(SampleOutcome.Extrapolated, e1.Outcome, "dt = 1 tick");
            // beyond one tick: held at the newest position (CONTRACT §5.1 C1: no extrapolation beyond one tick)
            CamSample hd = sampler.Sample(h, (long)(q0 + T + 1.2 * T + 1.25 * T));
            Assert.AreEqual(SampleOutcome.Held, hd.Outcome);
            Assert.AreEqual(2.0, hd.Px, 1e-9);
            Assert.AreEqual(11UL, hd.PresentedTick);
            Assert.IsFalse(hd.Stale, "newest is 2.45 ticks old (< 100 ms)");
            CamSample st = sampler.Sample(h, (long)(q0 + T + 0.101 * Freq));
            Assert.AreEqual(SampleOutcome.Held, st.Outcome);
            Assert.IsTrue(st.Stale, "newest older than 100 ms -> stale (held, counted)");
        }

        [TestMethod]
        public void Sampler_SingleSnapshot_And_BeforeHistory_Hold_EmptyIsNone()
        {
            var sampler = new FrameSampler(Freq);
            Assert.AreEqual(SampleOutcome.None, sampler.Sample(new CamHistory(), 123456).Outcome, "no snapshot: None, nothing presented");
            Assert.AreEqual(0UL, sampler.Sample(new CamHistory(), 123456).PresentedTick);
            double q0 = 1e9;
            CamHistory one = History(Snap(5, q0, x: 7));
            CamSample a = sampler.Sample(one, (long)(q0 + 0.3 * T)); // t before the only snapshot
            Assert.AreEqual(SampleOutcome.Held, a.Outcome);
            Assert.AreEqual(7.0, a.Px);
            Assert.AreEqual(5UL, a.PresentedTick);
            CamSample b = sampler.Sample(one, (long)(q0 + 1.25 * T + 0.5 * T)); // t past it, no velocity: held
            Assert.AreEqual(SampleOutcome.Held, b.Outcome);
            CamHistory two = History(Snap(5, q0, x: 0), Snap(6, q0 + T, x: 1));
            CamSample c = sampler.Sample(two, (long)(q0 + 0.5 * T)); // t before the oldest: hold the oldest
            Assert.AreEqual(SampleOutcome.Held, c.Outcome);
            Assert.AreEqual(0.0, c.Px);
            Assert.AreEqual(5UL, c.PresentedTick, "held before the history: presented = the held snapshot's tick");
            Assert.AreEqual(1.0, CamMath.YawDeg(0, 0, 0, 1) + 1.0, 1e-12, "sanity");
        }

        [TestMethod]
        public void History_Snaps_OnJump_Turn_Teleport_And_Resets_OnEpochAndClock()
        {
            double q0 = 1e9;
            string why;
            var h = History(Snap(1, q0, x: 0), Snap(2, q0 + T, x: 1));
            Assert.AreEqual(PushResult.Snap, h.Push(Snap(3, q0 + 2 * T, x: 25), out why), "jump > 20 m");
            Assert.AreEqual(1, h.Count, "history restarts at the jump");
            StringAssert.Contains(why, "jump");
            var sampler = new FrameSampler(Freq);
            CamSample s = sampler.Sample(h, (long)(q0 + 2 * T + 0.1 * T));
            Assert.AreEqual(25.0, s.Px, "snap: the camera is at the new place at once (held, never interpolated across the jump)");

            Assert.AreEqual(PushResult.Pushed, h.Push(Snap(4, q0 + 3 * T, x: 44.9), out why), "19.9 m: not a jump");
            Assert.AreEqual(PushResult.Snap, h.Push(Snap(5, q0 + 4 * T, x: 44.9, yawDeg: 46f), out why), "turn > 45 deg");
            StringAssert.Contains(why, "turn");
            Assert.AreEqual(PushResult.Snap, h.Push(Snap(6, q0 + 5 * T, x: 44.9, yawDeg: 46f, flags: SeFrameFlags.InWorld | SeFrameFlags.Teleported), out why),
                "SE teleport flag");
            Assert.AreEqual(PushResult.ResetEpoch, h.Push(Snap(7, q0 + 6 * T, x: 44.9, yawDeg: 46f, epoch: 2), out why), "world epoch change");
            Assert.AreEqual(PushResult.ResetClock, h.Push(Snap(3, q0 + 7 * T, x: 44.9, yawDeg: 46f, epoch: 2), out why), "tick backwards");
            Assert.AreEqual(PushResult.ResetClock, h.Push(Snap(4, q0 + 6.5 * T, x: 44.9, yawDeg: 46f, epoch: 2), out why), "newer tick with an older QPC");
            Assert.AreEqual(1, h.Count);
        }

        [TestMethod]
        public void History_SameTick_ReplacedOrIgnored_InvalidAndNotInWorldRejected_CapacityFour()
        {
            double q0 = 1e9;
            string why;
            var h = History(Snap(1, q0, x: 0), Snap(2, q0 + T, x: 1));
            Assert.AreEqual(PushResult.Replaced, h.Push(Snap(2, q0 + T + 100, x: 1.5), out why), "same tick, newer QPC: replaced");
            Assert.AreEqual(1.5, h.Newest.Px);
            Assert.AreEqual(PushResult.Ignored, h.Push(Snap(2, q0 + T, x: 9), out why), "same tick, not newer: ignored");
            Assert.AreEqual(PushResult.NotInWorld, h.Push(Snap(3, q0 + 2 * T, x: 2, flags: 0), out why), "inWorld clear");
            CamSnapshot bad = Snap(3, q0 + 2 * T, x: double.NaN);
            Assert.AreEqual(PushResult.Invalid, h.Push(bad, out why), "NaN position");
            CamSnapshot badFov = Snap(3, q0 + 2 * T, fov: 0f);
            Assert.AreEqual(PushResult.Invalid, h.Push(badFov, out why), "FOV 0");
            CamSnapshot badQ = Snap(3, q0 + 2 * T); badQ.Qw = 0; badQ.Qy = 0;
            Assert.AreEqual(PushResult.Invalid, h.Push(badQ, out why), "zero quaternion");
            for (ulong t = 3; t < 10; t++) h.Push(Snap(t, q0 + t * T - T, x: t - 1), out why);
            Assert.AreEqual(CamHistory.Capacity, h.Count, "keeps the last 4 snapshots");
            Assert.AreEqual(6UL, h[0].Tick);
            Assert.AreEqual(9UL, h.Newest.Tick);
        }

        [TestMethod]
        public void GhostRule_PelvisWhenPresentAndNotSeated_LerpedWithCameraAlpha_ElseCamera()
        {
            double q0 = 1e9;
            uint present = SeFrameFlags.InWorld | SeFrameFlags.CharacterPresent;
            CamSnapshot a = Snap(1, q0, x: 0); a.Flags = present; a.CharacterId = 7; a.PelX = 10; a.PelY = -151; a.PelZ = 0;
            a.CameraMode = CameraMode.FirstPerson;
            CamSnapshot b = Snap(2, q0 + T, x: 1); b.Flags = present; b.CharacterId = 7; b.PelX = 12; b.PelY = -151; b.PelZ = 0;
            b.CameraMode = CameraMode.FirstPerson;
            var sampler = new FrameSampler(Freq);
            long now = (long)(q0 + 0.25 * T + 1.25 * T);
            CamSample s = sampler.Sample(History(a, b), now);
            Assert.IsTrue(s.PlayerAtPelvis);
            Assert.AreEqual(10.5, s.PlayerX, 1e-6, "pelvis lerped with the camera alpha (0.25)");

            CamSnapshot b2 = b; b2.CharacterId = 8;
            CamSample s2 = sampler.Sample(History(a, b2), now);
            Assert.AreEqual(12.0, s2.PlayerX, 1e-9, "another character at the newer end: its pelvis, not lerped");

            CamSnapshot seated = b; seated.Flags = present | SeFrameFlags.CharacterSeated;
            CamSample s3 = sampler.Sample(History(a, seated), now);
            Assert.IsFalse(s3.PlayerAtPelvis, "seated: the Player goes to the camera");
            Assert.AreEqual(s3.Px, s3.PlayerX, 1e-12);

            CamSnapshot none = b; none.Flags = SeFrameFlags.InWorld;
            CamSample s4 = sampler.Sample(History(a, none), now);
            Assert.IsFalse(s4.PlayerAtPelvis, "no character (spectator): the camera");
            Assert.AreEqual(0.25, s4.PlayerX, 1e-6);

            //  (ROADMAP S1.N8): spectator with a character standing elsewhere -> the camera, not the pelvis
            CamSnapshot spectator = b; spectator.CameraMode = CameraMode.Spectator;
            CamSample s5 = sampler.Sample(History(a, spectator), now);
            Assert.IsFalse(s5.PlayerAtPelvis, "spectator (cameraMode 4) with a character present and not seated: the camera");
            Assert.AreEqual(s5.Px, s5.PlayerX, 1e-12);
            CamSnapshot third = b; third.CameraMode = CameraMode.ThirdPerson;
            Assert.IsTrue(sampler.Sample(History(a, third), now).PlayerAtPelvis, "third person of the character: the pelvis");
            foreach (uint mode in new[] { CameraMode.Cockpit, CameraMode.Other })
            {
                CamSnapshot m = b; m.CameraMode = mode;
                Assert.IsFalse(sampler.Sample(History(a, m), now).PlayerAtPelvis, "cameraMode " + mode + ": the camera");
            }
        }

        [TestMethod]
        public void Sampler_Extrapolation_UsesTickVelocity_NeverOvershootsOneTick()
        {
            //  SE stamps qpcCapture after a variable-length simulation; a 0.3-tick QPC span for one tick of
 // motion (2 m) must not extrapolate 3x one tick's motion
            double q0 = 1e9;
            CamHistory h = History(Snap(10, q0, x: 0), Snap(11, q0 + 0.3 * T, x: 2));
            var sampler = new FrameSampler(Freq);
            CamSample e = sampler.Sample(h, (long)(q0 + 0.3 * T + 0.9 * T + 1.25 * T));
            Assert.AreEqual(SampleOutcome.Extrapolated, e.Outcome);
            Assert.AreEqual(2.0 + 2.0 * 0.9, e.Px, 1e-6, "velocity 2 m per tick from the tick difference, 0.9 tick past the newest");
            Assert.IsTrue(e.Px <= 4.0 + 1e-9, "at most one tick's motion past the newest snapshot");
            // two ticks between the snapshots (one tick missed): half the per-snapshot motion per tick
            CamHistory h2 = History(Snap(20, q0, x: 0), Snap(22, q0 + 2 * T, x: 4));
            CamSample e2 = sampler.Sample(h2, (long)(q0 + 2 * T + 1.0 * T + 1.25 * T));
            Assert.AreEqual(SampleOutcome.Extrapolated, e2.Outcome);
            Assert.AreEqual(6.0, e2.Px, 1e-6, "2 m per tick, one tick past");
        }

        [TestMethod]
        public void DriveGate_HoldsBeyondTheZone_AboveTheApproachBand_BelowTheWarpMargin_AndOnC5FarOrOffSite()
        {
            //  CONTRACT §7 "Leaving the site... Idle"; ARCHITECTURE §4.2; OutOfBoundsWarp ±8,192 m
            var g = new DriveGate();
            bool changed;
            Assert.IsFalse(g.Open, "closed until a sample decides");
            Assert.IsTrue(g.Decide(-1029, -150, -1116, 1000, out changed) && changed, "WM-2 point: open");
            Assert.IsTrue(g.Decide(7850, -100, 0, 1001, out changed) && !changed, "7,850 m: inside the 7,900 m exit hysteresis");
            Assert.IsFalse(g.Decide(7950, -100, 0, 1002, out changed), "7,950 m: FAR, held");
            Assert.IsTrue(changed);
            StringAssert.Contains(g.Reason, "FAR");
            Assert.IsFalse(g.Decide(7850, -100, 0, 1003, out changed), "7,850 m again: still held (re-entry at 7,800 m)");
            Assert.IsTrue(g.Decide(7790, -100, 0, 1004, out changed) && changed, "7,790 m: open again");
            Assert.IsTrue(g.Decide(0, 320, 0, 1005, out changed), "+320 m: inside the +350 m exit band");
            Assert.IsFalse(g.Decide(0, 351, 0, 1006, out changed), "+351 m: above the Approach band, held");
            Assert.IsFalse(g.Decide(0, 310, 0, 1007, out changed), "+310 m: re-entry needs <= +300 m");
            Assert.IsTrue(g.Decide(0, 299, 0, 1008, out changed), "+299 m: open");
            Assert.IsTrue(g.Decide(0, -3000, 0, 1009, out changed), "the floor at -3,000 m: open");
            Assert.IsFalse(g.Decide(0, -7950, 0, 1010, out changed), "-7,950 m: beyond the OutOfBoundsWarp margin, held");
            Assert.IsFalse(g.Decide(0, 300000, 0, 1011, out changed), "space above the site: held");
            Assert.IsFalse(g.Decide(1e6, -100, 0, 1012, out changed), "1,000 km away: held");
            Assert.IsTrue(g.Decide(0, -100, 0, 1013, out changed), "back at the anchor: open");

            g.NoteC5(Zone.Far, ViewState.Approach, 2000);
            Assert.IsFalse(g.Decide(0, -100, 0, 2100, out changed), "fresh C5 zone FAR: held whatever the snapshot");
            StringAssert.Contains(g.Reason, "C5 zone FAR");
            g.NoteC5(Zone.OffSite, ViewState.Approach, 3000);
            Assert.IsFalse(g.Decide(0, -100, 0, 3500, out changed), "fresh C5 off-site (another planet's ocean): held");
            Assert.IsTrue(g.Decide(0, -100, 0, 3000 + DriveGate.C5FreshMs + 1, out changed), "C5 older than 1 s: the snapshot decides");
            g.NoteC5(Zone.Core, ViewState.Approach, 5000);
            Assert.IsTrue(g.Decide(0, -100, 0, 5001, out changed), "C5 CORE: open");
            g.NoteC5(Zone.TileVoid, ViewState.Approach, 6000);
            Assert.IsTrue(g.Decide(2040, -100, 0, 6001, out changed), "C5 TILE VOID: open");
            // S7a-1: with a fresh C5 the band lives in SE's view state (a live setting, +1,000 / +1,100 m default)
            g.NoteC5(Zone.Core, ViewState.Approach, 6100);
            Assert.IsTrue(g.Decide(0, 950, 0, 6101, out changed), "C5 Approach at +950 m: driven (the +350 m rule is the stale-C5 fallback)");
            Assert.IsTrue(g.Decide(0, 1900, 0, 6102, out changed), "C5 Approach by predicted arrival at +1,900 m: driven");
            g.NoteC5(Zone.Core, ViewState.Space, 6200);
            Assert.IsFalse(g.Decide(0, 950, 0, 6201, out changed), "C5 Space: held whatever the altitude");
            StringAssert.Contains(g.Reason, "C5 view Space");
            Assert.IsFalse(g.Decide(0, 1200, 0, 6202, out changed));
            g.NoteC5(Zone.Core, ViewState.Approach, 6300);
            Assert.IsTrue(g.Decide(0, 1050, 0, 6301, out changed) && changed, "C5 Approach again: open at +1,050 m");
            Assert.IsFalse(g.Decide(0, 1050, 0, 6300 + DriveGate.C5FreshMs + 1, out changed), "C5 stale: the +350 m fallback holds");
            g.Reset();
            Assert.IsFalse(g.Open, "reset (new SE generation): closed until the next sample");
            Assert.IsFalse(g.Decide(double.NaN, -100, 0, 7000, out changed), "NaN: held");
        }

        [TestMethod]
        public void CamTrace_Format_And_BatchedWriter()
        {
            // s 1, 6, 7: one parseable record per frame with the transform, the commanded pose and the FOVs
            var e = new CamTraceEntry
            {
                Frame = 123, TickMs = 456, SnFrame = 789, QpcUsed = 1000000123, NewestQpc = 1000000000, NewestTick = 501, PresentedTick = 501,
                OlderTick = 500, Outcome = SampleOutcome.Bracketed, Alpha = 0.25,
                X = -1029.0001f, Y = -150f, Z = -1116f, Qx = 0f, Qy = 0f, Qz = 0f, Qw = 1f,
                Tx = -1029f, Ty = -150f, Tz = -1116f, Tqx = 0f, Tqy = 0f, Tqz = 0f, Tqw = 1f,
                FovSeen = 69.5f, FovApplied = 70f, Fov = 70f, C1Fov = 70f,
            };
            var sb = new System.Text.StringBuilder();
            CamTraceFormat.AppendRecord(sb, ref e, Freq, new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc));
            string line = sb.ToString();
            Assert.IsTrue(line.EndsWith("}\n"), "one complete JSONL line");
            var rec = (Dictionary<string, object>)Json.Parse(line.TrimEnd('\n'));
            Assert.AreEqual("DE.CamTrace", rec["event"]);
            string text = (string)rec["text"];
            foreach (string k in new[] { "f=123 ", "qpcUsed=1000000123 ", "qpcFreq=" + Freq + " ", "newestTick=501 ", "presented=501 ", "older=500 ",
                                         "outcome=Bracketed ", "alpha=0.2500 ", " x=-1029.00012 ", " qw=1 ", " tx=-1029 ", " tqw=1 ", " fov=70 ",
                                         " fovSeen=69.5 ", " fovApplied=70 ", " c1Fov=70" })
                StringAssert.Contains(text, k);

            var batches = new List<string>();
            var w = new CamTraceWriter(Freq, b => batches.Add(b.ToString()));
            for (int i = 0; i < 300; i++) { e.Frame = i; w.Add(ref e); }
            Assert.AreEqual(300, w.Buffered);
            w.Flush();
            Assert.AreEqual(0, w.Buffered, "handed to the writer thread");
            for (int i = 0; i < 5; i++) { e.Frame = 1000 + i; w.Add(ref e); }
            w.Stop(); // joins the thread, then writes the rest here
            string all = string.Join("", batches.ToArray());
            int lines = all.Split('\n').Length - 1;
            Assert.AreEqual(305, lines, "every buffered frame written once");
            Assert.AreEqual(305L, w.Written);
            Assert.AreEqual(0L, w.Dropped);
            var full = new CamTraceWriter(Freq, b => { });
            for (int i = 0; i < CamTraceWriter.Capacity + 7; i++) full.Add(ref e);
            Assert.AreEqual(7L, full.Dropped, "beyond the capacity: dropped and counted, never blocking");
        }

        // ------------------------------------------------------------------------------------- S1.N1 synthetic oracle

        private sealed class SimResult
        {
            public long Frames, Bracketed, Extrapolated, Held;
            public ulong WorstLag; // currentSeTick - presentedSeTick
            public bool Monotonic = true;
            public bool PresentedIsNewerBracket = true;
        }

        /// <summary>
 /// SE publishes one snapshot per 1/60 s (capture jitter up to <paramref name="jitterMs"/>, visible to Subnautica
 /// 0.5 ms later); Subnautica presents at <paramref name="fps"/> and samples at its frame time with delay D.
 /// </summary>
        private static SimResult Simulate(double delayTicks, double fps, double jitterMs, int frames, int seed)
        {
            var rng = new Random(seed);
            var sampler = new FrameSampler(Freq, delayTicks);
            var h = new CamHistory();
            var r = new SimResult();
            double transport = 0.0005 * Freq;
            var capture = new List<double>();
            for (int k = 0; k < frames * 60 / fps + 200; k++) capture.Add(1e9 + k * T + rng.NextDouble() * jitterMs * 1e-3 * Freq);
            int next = 0;
            ulong lastPresented = 0;
            for (int f = 0; f < frames; f++)
            {
                double now = 1e9 + 10 * T + f * Freq / fps;
                while (next < capture.Count && capture[next] + transport <= now)
                {
                    string why;
                    h.Push(Snap((ulong)next, capture[next], x: next * 0.5), out why);
                    next++;
                }
                CamSample s = sampler.Sample(h, (long)now);
                r.Frames++;
                if (s.Outcome == SampleOutcome.Bracketed) r.Bracketed++;
                else if (s.Outcome == SampleOutcome.Extrapolated) r.Extrapolated++;
                else r.Held++;
                ulong seTick = (ulong)Math.Floor((now - 1e9) / T); // the tick SE is on at this instant
                if (seTick - s.PresentedTick > r.WorstLag) r.WorstLag = seTick - s.PresentedTick;
                if (s.PresentedTick < lastPresented) r.Monotonic = false;
                lastPresented = s.PresentedTick;
                if (s.Outcome == SampleOutcome.Bracketed && s.PresentedTick != s.OlderTick + 1) r.PresentedIsNewerBracket = false;
            }
            return r;
        }

        [TestMethod]
        public void N1_Synthetic_Steady60Hz_ExtrapolatedUnder1Percent_FreshnessBound_And_SeededFailureD0()
        {
            foreach (double fps in new[] { 60.0, 144.0, 165.0 })
            {
                SimResult ok = Simulate(FrameSampler.DelayTicks, fps, 2.0, 10000, 42);
                double ext = (double)ok.Extrapolated / ok.Frames;
                Console.WriteLine("S1.N1 synthetic " + fps + " fps: frames=" + ok.Frames + " bracketed=" + ok.Bracketed + " extrapolated=" + ok.Extrapolated +
                                  " held=" + ok.Held + " worstLag=" + ok.WorstLag + " ticks");
                Assert.IsTrue(ext < 0.01, fps + " fps: extrapolated " + ext.ToString("P2") + " (S1.N1 threshold < 1 %)");
                Assert.IsTrue(ok.Held == 0, fps + " fps: held " + ok.Held);
                Assert.IsTrue(ok.Monotonic, fps + " fps: presentedSeTick never goes backwards");
                Assert.IsTrue(ok.PresentedIsNewerBracket, fps + " fps: bracketed frames present b.tick = a.tick + 1");
                Assert.IsTrue(ok.WorstLag <= 4, fps + " fps: presentedSeTick >= tick - 4 (CONTRACT C7 freshness), worst lag " + ok.WorstLag);
            }
            SimResult thirty = Simulate(FrameSampler.DelayTicks, 30.0, 2.0, 3000, 7);
            Assert.IsTrue(thirty.WorstLag <= 4, "30 fps: freshness bound holds, worst lag " + thirty.WorstLag);

            // seeded failure: no delay (D = 0) -> render time always at or past the newest snapshot
            SimResult mutant = Simulate(0.0, 144.0, 2.0, 10000, 42);
            double mext = (double)(mutant.Extrapolated + mutant.Held) / mutant.Frames;
            Console.WriteLine("S1.N1 MUTANT D=0 144 fps: frames=" + mutant.Frames + " bracketed=" + mutant.Bracketed + " extrapolated=" + mutant.Extrapolated +
                              " held=" + mutant.Held);
            Assert.IsTrue(mext > 0.5, "MUTANT D=0 must turn the < 1 % check red: extrapolated+held " + mext.ToString("P1"));
        }

        // ------------------------------------------------------------------------------------- C1 reader on a real record

        [TestMethod]
        public void FrameReader_SeqlockRecord_Fresh_Unchanged_Torn_ResetIgnoresPresentContent()
        {
            IntPtr mem = Marshal.AllocHGlobal(0x2000);
            try
            {
                byte* rec = (byte*)mem;
                for (int i = 0; i < 0x2000; i++) rec[i] = 0;
                var reader = new FrameReader(rec, Freq);
                Assert.AreEqual(C1Poll.Never, reader.Poll(), "seq 0: never written");

                SeFrame f = default(SeFrame);
                f.flags = SeFrameFlags.InWorld;
                f.tick = 500; f.qpcCapture = 1000000000; f.worldEpoch = 1;
                f.camPos = new F32x3(-1029f, -150f, -1116f);
                f.camRot = new F32x4(0, 0, 0, 1);
                f.vFovDeg = 70f; f.near = 0.05f; f.aspect = 1.7778f;
                Seqlock.WriteBytes(rec, (byte*)&f, sizeof(SeFrame));
                Assert.AreEqual(C1Poll.Fresh, reader.Poll());
                Assert.AreEqual(PushResult.First, reader.LastPush);
                Assert.AreEqual(C1Poll.Unchanged, reader.Poll());
                Assert.AreEqual(-1029.0, reader.History.Newest.Px, 1e-9);

                f.tick = 501; f.qpcCapture += (long)T; f.camPos = new F32x3(-1028f, -150f, -1116f);
                Seqlock.WriteBytes(rec, (byte*)&f, sizeof(SeFrame));
                Assert.AreEqual(C1Poll.Fresh, reader.Poll());
                Assert.AreEqual(2, reader.History.Count);

                // a writer stuck mid-write (seq odd) for every try: torn, the last coherent copy is kept
                uint* seq = (uint*)rec;
                uint saved = *seq;
                *seq = saved | 1u;
                Assert.AreEqual(C1Poll.Torn, reader.Poll());
                Assert.AreEqual(1L, reader.Torn);
                Assert.AreEqual(501UL, reader.History.Newest.Tick, "last coherent copy kept");
                *seq = saved;

                // new SE generation: the content present now is never taken for a new frame
                reader.Reset();
                Assert.AreEqual(0, reader.History.Count);
                Assert.AreEqual(C1Poll.Unchanged, reader.Poll(), "present C1 ignored after Reset");
                f.tick = 10; f.qpcCapture += (long)(5 * T);
                Seqlock.WriteBytes(rec, (byte*)&f, sizeof(SeFrame));
                Assert.AreEqual(C1Poll.Fresh, reader.Poll(), "the new SE's first write is taken");
                Assert.AreEqual(10UL, reader.History.Newest.Tick);
            }
            finally
            {
                Marshal.FreeHGlobal(mem);
            }
        }

        // ------------------------------------------------------------------------------------- helpers

        [TestMethod]
        public void LatencyHistogram_Percentiles_And_CamMath()
        {
            var hst = new LatencyHistogram();
            Assert.IsTrue(double.IsNaN(hst.Percentile(0.5)));
            for (int i = 1; i <= 100; i++) hst.Add(i); // 1..100 ms
            Assert.AreEqual(50.25, hst.Percentile(0.50), 1e-9, "p50 bin upper edge");
            Assert.AreEqual(95.25, hst.Percentile(0.95), 1e-9);
            hst.Add(1000);
            Assert.AreEqual(1000.0, hst.MaxMs);
            Assert.AreEqual(1000.0, hst.Percentile(1.0), "overflow bin reports the max");

            Assert.AreEqual(90.0, CamMath.YawDeg(0f, (float)Math.Sin(Math.PI / 4), 0f, (float)Math.Cos(Math.PI / 4)), 1e-4, "yaw +90 = SN east (+X)");
            Assert.AreEqual(-90.0, CamMath.PitchDeg((float)Math.Sin(Math.PI / 4), 0f, 0f, (float)Math.Cos(Math.PI / 4)), 1e-4,
                            "Unity +90 about X looks down");
            Assert.AreEqual(30.0, CamMath.AngleDeg(0, 0, 0, 1, 0, (float)Math.Sin(Math.PI / 12), 0, (float)Math.Cos(Math.PI / 12)), 1e-4);
            Assert.AreEqual(0.0, CamMath.AngleDeg(0, 0, 0, 1, 0, 0, 0, -1), 1e-9, "q and -q are the same rotation");
            Assert.AreEqual(20.0, CamMath.YawDelta(170, -170), 1e-9);
        }

        // ------------------------------------------------------------------------------------- host save decision table

        private static HostSlot Slot(string name, string session, int mode, bool corrupted = false)
        {
            return new HostSlot { Slot = name, Session = session, GameMode = mode, ChangeSet = 83031, GameTime = 15, Corrupted = corrupted };
        }

        [TestMethod]
        public void HostSave_SlotSelection_DecisionTable()
        {
            string host = HostSaveRules.DefaultSessionId;
            var other = Slot("slot0001", "11111111-2222-3333-4444-555555555555", LegacyGameMode.Survival);
            var hostSlot = Slot("slot0000", host, LegacyGameMode.Freedom);

            HostSlotChoice c = HostSaveRules.SelectSlot(new[] { other, hostSlot }, host, null);
            Assert.IsTrue(c.Ok && c.Slot.Slot == "slot0000" && c.Matches == 1, "the owner's Freedom slot by session: " + c);
            Assert.IsTrue(HostSaveRules.SelectSlot(new[] { hostSlot }, host.ToUpperInvariant(), null).Ok, "session compared case-insensitively");
            Assert.IsTrue(HostSaveRules.SelectSlot(new[] { hostSlot }, null, null).Ok, "no session given: the compiled default");

            var table = new[]
            {
                new { Slots = new HostSlot[0], Slot = (string)null, Reason = "no-saves" },
                new { Slots = new[] { other }, Slot = (string)null, Reason = "session-not-found" },
                new { Slots = new[] { Slot("slot0000", host, LegacyGameMode.Creative) }, Slot = (string)null, Reason = "mode" },
                new { Slots = new[] { Slot("slot0000", host, LegacyGameMode.Survival) }, Slot = (string)null, Reason = "mode" },
                new { Slots = new[] { Slot("slot0000", host, LegacyGameMode.Hardcore) }, Slot = (string)null, Reason = "mode" },
                new { Slots = new[] { Slot("slot0000", host, LegacyGameMode.Freedom, corrupted: true) }, Slot = (string)null, Reason = "corrupted" },
                new { Slots = new[] { hostSlot, Slot("slot0002", host, LegacyGameMode.Freedom) }, Slot = (string)null, Reason = "ambiguous" },
                new { Slots = new[] { hostSlot, other }, Slot = "slot0009", Reason = "slot-not-found" },
                new { Slots = new[] { hostSlot, other }, Slot = "slot0001", Reason = "session-mismatch" },
                // S1.M3: a throwaway Creative copy named explicitly -> refused at the menu, no camera driving
                new { Slots = new[] { hostSlot, Slot("slot0002", host, LegacyGameMode.Creative) }, Slot = "slot0002", Reason = "mode" },
            };
            foreach (var row in table)
            {
                HostSlotChoice r = HostSaveRules.SelectSlot(row.Slots, host, row.Slot);
                Assert.IsFalse(r.Ok, "must refuse: " + row.Reason);
                Assert.AreEqual(row.Reason, r.Reason, "reason (" + r + ")");
            }
            HostSlotChoice named = HostSaveRules.SelectSlot(new[] { hostSlot, Slot("slot0002", host, LegacyGameMode.Freedom) }, host, "SLOT0002");
            Assert.IsTrue(named.Ok && named.Slot.Slot == "slot0002", "ambiguity resolved by naming the slot (S1.M4 throwaway copy): " + named);
        }

        [TestMethod]
        public void HostSave_LoadedMode_FreedomAndNotInvisible_ElseRefuse()
        {
            HostModeCheck ok = HostSaveRules.CheckLoaded(GameModeOptionBits.Freedom, 0, false);
            Assert.IsTrue(ok.Ok);
            Assert.AreEqual("DE.HostSave mode=Freedom invisible=false", ok.Line, "the S1.N5 pass line");

            HostModeCheck creative = HostSaveRules.CheckLoaded(GameModeOptionBits.Creative, 0, true);
            Assert.IsFalse(creative.Ok);
            Assert.AreEqual("mode", creative.Reason);
            StringAssert.Contains(creative.Line, "REFUSE");

            HostModeCheck cheat = HostSaveRules.CheckLoaded(GameModeOptionBits.Freedom, GameModeOptionBits.NoAggression, false);
            Assert.IsFalse(cheat.Ok, "Freedom with the NoAggression cheat word");
            Assert.AreEqual("invisible", cheat.Reason);

            HostModeCheck invisible = HostSaveRules.CheckLoaded(GameModeOptionBits.Freedom, 0, true);
            Assert.IsFalse(invisible.Ok, "IsInvisible() true");
            StringAssert.StartsWith(invisible.Line, "DE.HostSave mode=Freedom invisible=true");

            Assert.IsFalse(HostSaveRules.CheckLoaded(GameModeOptionBits.Survival, 0, false).Ok, "Survival");
            Assert.IsFalse(HostSaveRules.CheckLoaded(GameModeOptionBits.Hardcore, 0, false).Ok, "Hardcore");
            Assert.IsTrue(HostSaveRules.CheckLoaded(GameModeOptionBits.Freedom, GameModeOptionBits.NoCost, false).Ok, "other cheats do not refuse");
            Assert.AreEqual(0x80, GameModeOptionBits.Creative & GameModeOptionBits.NoAggression, "Creative contains NoAggression (VERIFIED enum)");
            Assert.AreEqual(2, GameModeOptionBits.Freedom, "Freedom = NoSurvival");
        }

        [TestMethod]
        public void Switches_Args_CapturePaths_MutantNames()
        {
            Func<string, string> saved = S1Switches.Reader;
            try
            {
                var env = new Dictionary<string, string>();
                S1Switches.Reader = n => { string v; return env.TryGetValue(n, out v) ? v : null; };
                Assert.IsTrue(S1Switches.AutoLoad, "auto-load on by default");
                Assert.AreEqual(HostSaveRules.DefaultSessionId, S1Switches.HostSession);
                Assert.IsNull(S1Switches.HostSlot);
                Assert.IsFalse(S1Switches.Trace);
                env[S1Switches.AutoLoadVar] = "0";
                env[S1Switches.HostSessionVar] = " abc ";
                env[S1Switches.HostSlotVar] = "slot0003";
                env[S1Switches.TraceVar] = "on";
                env[S1Switches.TestRunInBackgroundOffVar] = "1";
                Assert.IsFalse(S1Switches.AutoLoad);
                Assert.AreEqual("abc", S1Switches.HostSession);
                Assert.AreEqual("slot0003", S1Switches.HostSlot);
                Assert.IsTrue(S1Switches.Trace);
                Assert.AreEqual(BuildFlavor.Dev, S1Switches.TestRunInBackgroundOff, "test hook only in dev builds");
            }
            finally
            {
                S1Switches.Reader = saved;
            }

            var args = (Dictionary<string, object>)Json.Parse("{\"x\": -1029, \"y\": \"-150\", \"z\": -1116.5, \"clear\": \"off\"}");
            double d;
            bool b;
            Assert.IsTrue(S1Args.TryDouble(S1Args.Get(args, "x", 0), out d) && d == -1029);
            Assert.IsTrue(S1Args.TryDouble(S1Args.Get(args, "y", 1), out d) && d == -150, "numeric strings accepted");
            Assert.IsTrue(S1Args.TryBool(S1Args.Get(args, "clear", -1), out b) && !b);
            object relayed = S1Args.Unwrap(Json.Parse("{\"argv\": [\"1\", \"2\", \"3\"]}"));
            Assert.IsTrue(S1Args.TryDouble(S1Args.Get(relayed, "z", 2), out d) && d == 3, "SE chat relay {argv: [...]}");
            Assert.IsNull(S1Args.Get(relayed, "clear", -1));

            string n, e;
            Assert.IsTrue(CapturePaths.TryNormalise("s1/wm2", 222, out n, out e) && n == "s1\\wm2.png", n + " " + e);
            Assert.IsTrue(CapturePaths.TryNormalise("x.PNG", 222, out n, out e) && n == "x.PNG");
            foreach (string bad in new[] { "", "..\\x.png", "a\\..\\b.png", "C:\\x.png", "\\x.png", "a\\\\b.png", "a?.png", new string('a', 300) })
                Assert.IsFalse(CapturePaths.TryNormalise(bad, 222, out n, out e), "refused: '" + bad + "'");

            foreach (string m in new[] { "S1.M4", "S1.M6", "S1.M7", "S1.M8", "S1.M10" }) Assert.IsTrue(DevMutants.IsKnown(m), m);
        }
    }
}
