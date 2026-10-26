using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SE.Tests
{
    /// <summary>
 /// S11, SE side, the game-free rules (src/DeepEngineers.SE/S11/S11SeLogic.cs): the per-type per-second caps and the HUD
 /// notice cadence, the EMP book (only what S11 switched off, extended by a second EMP, everything back at unload), the
 /// grab book (one creature per grid, NoGrab for an unknown key, the timeout and S11.M5), the SE mutants.
 /// </summary>
    [TestClass]
    public class S11Tests
    {
        [TestMethod]
        public void CapBook_PerTypeWindowsAndNotices()
        {
            var b = new S11CapBook();
            Assert.AreEqual(25.0, b.Grant(1, 1000, 25, 50));
            Assert.AreEqual(25.0, b.Grant(1, 1500, 30, 50), "heat capped at 50 HP per second");
            Assert.AreEqual(5.0, b.Capped, 1e-12);
            Assert.AreEqual(10.0, b.Grant(6, 1500, 10, 30), "another type has its own window");
            Assert.AreEqual(30.0, b.Grant(1, 2000, 30, 50), "a new window");
            Assert.IsTrue(b.NoticeDue(1, 0));
            Assert.IsFalse(b.NoticeDue(1, 4999));
            Assert.IsTrue(b.NoticeDue(1, 5000));
            Assert.IsTrue(b.NoticeDue(6, 4999));
        }

        [TestMethod]
        public void EmpBook_OursOnly_ExtendedAndRestored()
        {
            var e = new S11EmpBook();
            Assert.IsTrue(e.Begin(11, 1, 1000, 2.0));
            Assert.IsTrue(e.Begin(12, 1, 1000, 2.0));
            Assert.IsFalse(e.Begin(11, 1, 2000, 2.0), "a second EMP extends, never switches twice");
            var due = new List<long>();
            e.Due(2999, due);
            Assert.AreEqual(0, due.Count);
            e.Due(3000, due);
            CollectionAssert.AreEqual(new long[] { 12 }, due.ToArray(), "12 back at 3,000 ms; 11 extended to 4,000");
            Assert.IsTrue(e.IsOurs(11));
            e.Due(4000, due);
            CollectionAssert.AreEqual(new long[] { 11 }, due.ToArray());
            Assert.AreEqual(0, e.Count);
            e.Begin(21, 2, 0, 30);
            e.Begin(22, 2, 0, 30);
            e.All(due);
            Assert.AreEqual(2, due.Count, "world unload: everything back");
            Assert.AreEqual(0, e.Count);
        }

        [TestMethod]
        public void EmpSave_ASaveInsideTheWindowWritesTheProducerOn()
        {
            //  SE saves before it unloads; a producer S11 switched off is written Enabled, the player's own choice kept
            var e = new S11EmpBook();
            Assert.IsTrue(e.Begin(11, 1, 0, 2.0));
            Assert.IsTrue(S11EmpBook.SavedEnabled(e.IsOurs(11), false), "switched off by the EMP: saved on");
            Assert.IsFalse(S11EmpBook.SavedEnabled(e.IsOurs(12), false), "switched off by the player: saved off");
            Assert.IsTrue(S11EmpBook.SavedEnabled(e.IsOurs(13), true));
            var due = new List<long>();
            e.Due(2000, due);
            Assert.IsFalse(S11EmpBook.SavedEnabled(e.IsOurs(11), false), "after the restore the block's own state is saved");
        }

        [TestMethod]
        public void GrabBook_OneCreaturePerGrid_TimeoutAndMutant()
        {
            var g = new S11GrabBook();
            S11Held h;
            Assert.AreEqual(S11Reject.None, g.Start(7u, 100, 2540, 0, 9.5, out h));
            Assert.AreEqual(S11Reject.AlreadyHeld, g.Start(8u, 100, 2540, 10, 9.5, out h), "one creature per grid");
            Assert.AreEqual(S11Reject.None, g.Start(7u, 100, 2540, 20, 9.5, out h), "the same creature restarts");
            Assert.AreEqual(S11Reject.NoGrab, g.Touch(9u, 100, 30, out h));
            Assert.AreEqual(S11Reject.NoGrab, g.Touch(7u, 101, 30, out h), "the key holds another grid");
            Assert.AreEqual(S11Reject.None, g.Touch(7u, 100, 500, out h));
            Assert.AreEqual(0, g.TimedOut(1500, false).Count);
            CollectionAssert.AreEqual(new uint[] { 7u }, g.TimedOut(1501, false).ToArray(), "1,000 ms without a hold record");
            Assert.AreEqual(0, g.TimedOut(1501, true).Count, "S11.M5: no timeout");
            Assert.IsTrue(g.Remove(7u));
            Assert.AreEqual(0, g.Count);
        }

        [TestMethod]
        public void Mutants_SetAndRefuse()
        {
            S11SeMutants.ClearAll();
            Assert.IsTrue(S11SeMutants.Set("s11.m4", true).StartsWith("S11.M4 ON", StringComparison.Ordinal));
            Assert.IsTrue(S11SeMutants.NoSizeClass);
            Assert.AreEqual("S11.M4", S11SeMutants.Active);
            Assert.IsTrue(S11SeMutants.Set("S11.M1", true).StartsWith("REFUSE", StringComparison.Ordinal), "a Subnautica-side mutant");
            Assert.IsNull(S11SeMutants.Set("S6.M3", true), "not S11's");
            S11SeMutants.ClearAll();
            Assert.AreEqual("", S11SeMutants.Active);
            //: the bite anchor (S11.C6) and the surface rule (S11.C7) on SE; the hit test and the rage rule are Subnautica's
            Assert.IsTrue(S11SeMutants.Set("S11.M13", true).StartsWith("S11.M13 ON", StringComparison.Ordinal));
            Assert.IsTrue(S11SeMutants.Set("S11.M14", true).StartsWith("S11.M14 ON", StringComparison.Ordinal));
            Assert.IsTrue(S11SeMutants.BiteAnchor && S11SeMutants.NoSurface);
            Assert.AreEqual("S11.M13,S11.M14", S11SeMutants.Active);
            Assert.IsTrue(S11SeMutants.Set("S11.M15", true).StartsWith("REFUSE", StringComparison.Ordinal));
            Assert.IsTrue(S11SeMutants.Set("S11.M16", true).StartsWith("REFUSE", StringComparison.Ordinal));
            S11SeMutants.ClearAll();
            Assert.IsFalse(S11SeMutants.BiteAnchor || S11SeMutants.NoSurface);
        }

        [TestMethod]
        public void Rage_LiveTable_AndHoldBook()
        {
            // (format rage.hold): the pinned defaults, a live retune, the book's once-per-hold outcomes
            var t = new S11RageTable();
            S11RageRow r;
            Assert.IsTrue(t.TryGet(2540, out r));
            Assert.AreEqual(300, r.ReleasePermille);
            string err;
            Assert.IsTrue(t.Set(2540, 0.3, 2.0, 6.0, 20.0, out err), err);
            Assert.IsTrue(t.TryGet(2540, out r));
            Assert.AreEqual(300, r.ReleasePermille);
            Assert.AreEqual(2000, r.AngerPermille);
            Assert.IsFalse(t.Set(2562, 0.3, null, null, null, out err), "the ghost has no rage row");
            Assert.IsFalse(t.Set(2540, 1.5, null, null, null, out err), "release 0..1");
            t.Reset();
            Assert.IsTrue(t.TryGet(2540, out r) && r.ReleasePermille == 300);
            var book = new S11HoldRage();
            Assert.AreEqual(S11RageOutcome.Rage, book.Hit(500, 5000, r));
            Assert.AreEqual(S11RageOutcome.None, book.Hit(500, 5000, r), "two rockets: still angry");
            Assert.AreEqual(S11RageOutcome.Release, book.Hit(500, 5000, r), "the third: 1,500 = 30 % of 5,000");
            Assert.AreEqual(S11RageOutcome.None, book.Hit(900, 5000, r), "nothing after the release");
            Assert.AreEqual(1.5, book.DamageMult(r), 1e-12);
        }
    }
}
