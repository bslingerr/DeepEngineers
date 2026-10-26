using DeepEngineers.SE;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S7 interiors, the SE side's game-independent parts (CONTRACT §5.4 "Swim gate"): the swim exit reason <c>room</c>

 /// camera's C1 interior answer of a typical entry/recompute/breach sequence through Wire's hold rule (the probe itself
 /// reads SE's gas system and is exercised by the real run, S7.N1).
 /// </summary>
    [TestClass]
    public class S7InteriorSeTests
    {
        [TestMethod]
        public void SwimExitRoom_IsNamed_AndRestoresLikeASurfacing()
        {
            Assert.AreEqual("room", SwimExit.Name(SwimExit.Room));
            Assert.AreEqual(15u, SwimExit.Room);
            var p = new SwimSuitPolicy();
            p.OnEnter(true, true);
            SwimRestore r = p.OnExit(SwimExit.Room, false);
            Assert.IsTrue(r.SetJetpack && r.JetpackTo, "the jetpack comes back as it was");
            Assert.IsTrue(r.SetDampeners && r.DampenersTo, "the dampeners too");
            Assert.IsFalse(p.Active);
            var q = new SwimSuitPolicy();
            q.OnEnter(false, true);
            SwimRestore s = q.OnExit(SwimExit.Surface, false);
            Assert.AreEqual(s.SetJetpack, r.SetJetpack);
            Assert.AreEqual(s.SetDampeners, r.SetDampeners);
        }

        [TestMethod]
        public void CameraAnswer_EntryRecomputeBreachReseal()
        {
            var hold = new RoomFlagHold();
            var outside = new RoomProbe[0];
            var inside = new[] { new RoomProbe { GridId = 42, HasRoom = true, Airtight = true, RoomIndex = 1, RoomO2 = 0.8f } };
            var recompute = new[] { new RoomProbe { GridId = 42, Processing = true } };
            var breached = new[] { new RoomProbe { GridId = 42, HasRoom = true, Airtight = false, RoomIndex = 1 } };
            Assert.IsFalse(hold.Decide(outside, 0, true).InRoom);
            C1Interior a = hold.Decide(inside, 1, true);
            Assert.IsTrue(a.InRoom);
            Assert.AreEqual(42UL, a.RoomGridId);
            Assert.AreEqual(S7InteriorWire.RoomO2Byte(0.8f), a.RoomO2);
            Assert.IsTrue(hold.Decide(recompute, 1, true).InRoom, "a wall removed: the gas system recomputes, the flag holds");
            C1Interior b = hold.Decide(breached, 1, true);
            Assert.IsFalse(b.InRoom);
            Assert.IsTrue(b.Has(S7Conventions.C1InteriorFlags.Breached));
            Assert.IsFalse(hold.Decide(recompute, 1, true).InRoom, "re-placed: still the breach until the recompute ends");
            Assert.IsTrue(hold.Decide(inside, 1, true).InRoom, "re-sealed");
            Assert.IsFalse(hold.Decide(inside, 1, false).InRoom, "pressurisation off (S7.N1b): never in a room");
        }
    }
}
