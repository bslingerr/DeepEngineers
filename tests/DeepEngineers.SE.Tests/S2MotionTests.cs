using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// de.motion: the plan the SE plugin follows one sample per update for the S2.N2 bob traces
 /// and the S2.N7 (b) dive, checked against the arguments the launcher's s2 session sends and Wire's state machine.
 /// </summary>
    [TestClass]
    public class S2MotionTests
    {
        /// <summary>The band is a live setting since (default +1,000 / +1,100 m); these tests pin the mechanics at +300 / +350 m.</summary>
        [TestInitialize]
        public void PinTheTestBand()
        {
            Assert.IsTrue(ViewThresholds.SetBand(300.0, 350.0, "test"));
        }

        private static IDictionary<string, object> Args(string json) { return (IDictionary<string, object>)MiniJson.Parse(json); }

        [TestMethod]
        public void Bob_N2TraceA_StaysInsideTheBand_TraceB_CrossesIt()
        {
            bool stop;
            string err;
            // launcher p_n2: {"kind": "bob", "x": 0, "z": 0, "centreY": -0.125, "amplitudeM": 0.05, "hz": 1, "seconds": 30}
            MotionPlan a = MotionPlan.Parse(Args("{\"kind\": \"bob\", \"x\": 0.0, \"z\": 0.0, \"centreY\": -0.125, \"amplitudeM\": 0.05, \"hz\": 1.0, \"seconds\": 30.0, \"label\": \"n2a\"}"),
                                            Vec3.Zero, out stop, out err);
            Assert.IsNotNull(a, err);
            Assert.AreEqual(1800, a.Ticks);
            Assert.AreEqual("n2a", a.Label);
            double min = double.MaxValue, max = double.MinValue;
            for (int k = 0; k <= a.Ticks; k++)
            {
                Vec3 q = a.PointAt(k);
                Assert.AreEqual(0.0, q.X); Assert.AreEqual(0.0, q.Z);
                min = Math.Min(min, q.Y); max = Math.Max(max, q.Y);
            }
            Assert.IsTrue(min > ViewThresholds.UnderwaterEnterY && max <= ViewThresholds.UnderwaterExitY,
                          "trace (a) stays inside the hysteresis band: " + min + " .. " + max);
            MotionPlan b = MotionPlan.Parse(Args("{\"kind\": \"bob\", \"centreY\": -0.125, \"amplitudeM\": 0.1, \"hz\": 5, \"seconds\": 30}"), Vec3.Zero, out stop, out err);
            Assert.IsNotNull(b, err);
            Assert.AreEqual(-0.025, b.PointAt(3).Y, 1e-9, "5 Hz: the crest at t = 0.05 s (k = 3), above the -0.05 m exit");
            Assert.AreEqual(-0.225, b.PointAt(9).Y, 1e-9, "the trough at t = 0.15 s (k = 9), below the -0.20 m entry");

            // trace (a) through Wire's machine, from Underwater: 0 switches (S2.N2 (a), the real plugin's input path)
            var sim = ViewSimLite.Underwater();
            int switches = 0;
            uint prev = sim.Last.ViewState;
            for (int k = 0; k <= a.Ticks; k++)
            {
                ViewOutputs o = sim.Step(a.PointAt(k).Y);
                if ((o.ViewState == ViewState.Underwater) != (prev == ViewState.Underwater)) switches++;
                prev = o.ViewState;
            }
            Assert.AreEqual(0, switches, "S2.N2 (a) from below");
        }

        [TestMethod]
        public void Line_N7Dive_FiveMetresPerSecond_AndDefaults()
        {
            bool stop;
            string err;
            // launcher p_n7: {"kind": "line", "fromSn": [x, 10, z], "toSn": [x, -20, z], "speedMps": 5}
            MotionPlan l = MotionPlan.Parse(Args("{\"kind\": \"line\", \"fromSn\": [-1029, 10, -1116], \"toSn\": [-1029, -20, -1116], \"speedMps\": 5}"),
                                            Vec3.Zero, out stop, out err);
            Assert.IsNotNull(l, err);
            Assert.AreEqual(360, l.Ticks, "30 m at 5 m/s = 6 s = 360 updates");
            Assert.AreEqual(10.0, l.PointAt(0).Y, 1e-12);
            Assert.AreEqual(-20.0, l.PointAt(l.Ticks).Y, 1e-12);
            Assert.AreEqual(5.0 / 60.0, l.PointAt(1).Y - l.PointAt(2).Y, 1e-12, "5/60 m per update");
            Assert.AreEqual(-20.0, l.PointAt(l.Ticks + 50).Y, 1e-12, "clamped at the end");
            Assert.AreEqual(-1029.0, l.PointAt(100).X, 1e-12);

            MotionPlan d = MotionPlan.Parse(Args("{\"kind\": \"line\", \"toSn\": \"0,-5,0\"}"), new Vec3(0, 5, 0), out stop, out err);
            Assert.IsNotNull(d, err);
            Assert.AreEqual(120, d.Ticks, "from the camera (default), 10 m at the default 5 m/s");
            Assert.AreEqual(5.0, d.PointAt(0).Y, 1e-12);
        }

        [TestMethod]
        public void Refusals_And_Stop()
        {
            bool stop;
            string err;
            foreach (string bad in new[]
            {
                "{}", "{\"kind\": \"spiral\"}", "{\"kind\": \"line\"}", "{\"kind\": \"line\", \"toSn\": [1, 2]}",
                "{\"kind\": \"line\", \"toSn\": [0, 0, 0], \"speedMps\": 0}", "{\"kind\": \"bob\", \"seconds\": 0}",
                "{\"kind\": \"bob\", \"hz\": 100}", "{\"kind\": \"bob\", \"amplitudeM\": -1}", "{\"kind\": \"bob\", \"seconds\": 100000}",
            })
            {
                Assert.IsNull(MotionPlan.Parse(Args(bad), Vec3.Zero, out stop, out err), bad);
                Assert.IsFalse(stop);
                Assert.IsNotNull(err, bad);
            }
            Assert.IsNull(MotionPlan.Parse(Args("{\"kind\": \"stop\"}"), Vec3.Zero, out stop, out err));
            Assert.IsTrue(stop);
        }

        /// <summary>A minimal driver of Wire's machine (ready Subnautica, design F, render ack one step late).</summary>
        private sealed class ViewSimLite
        {
            private ViewMachineState _s;
            private ulong _tick = 1000;
            private uint _ackNext;
            private bool _inZone;
            public ViewOutputs Last;

            public ViewOutputs Step(double y)
            {
                var i = new ViewInputs
                {
                    Tick = _tick, NowUs = (long)_tick * 16667, BridgeUp = true, OnSite = true, RhoInfM = 100, Y = y, Vy = 0,
                    Peer = PeerState.Alive, SnPhase = SnPhase.Active, ZOrderOk = true, Foreground = true,
                    SnFlags = SnStatusFlags.AreaBuilt | SnStatusFlags.StreamingSettled | SnStatusFlags.RectOk | SnStatusFlags.UiHidden,
                    PresentedSeTick = _tick - 1, AreaBuiltSeTick = _tick - 1, RenderAckSeq = _ackNext, DisplayMode = DisplayMode.F,
                    MarginsReady = true,
                };
                i.Zone = SiteZones.Classify(true, 100, ref _inZone);
                ViewStateMachine.Step(ref _s, ref i, out Last);
                _ackNext = Last.RenderSeq;
                _tick++;
                return Last;
            }

            public static ViewSimLite Underwater()
            {
                var s = new ViewSimLite();
                for (int k = 0; k < 600 && s.Last.ViewState != ViewState.Underwater; k++) s.Step(-1.0);
                for (int k = 0; k < 30; k++) s.Step(-1.0);
                Assert.AreEqual(ViewState.Underwater, s.Last.ViewState, "setup Underwater");
                return s;
            }
        }
    }
}
