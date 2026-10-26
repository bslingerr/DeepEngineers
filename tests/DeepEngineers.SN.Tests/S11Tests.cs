using System;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S11, Subnautica side, the Unity-free rules (src/DeepEngineers.SN/S11/S11SnRules.cs): the grab clock follows the
 /// creature's own timing (hold 8-13 s, a hold record every 50 ms, the damage every second, 10 s cooldown), the oxygen
 /// accumulator never loses or doubles seconds, an EMP counts once per blast and grid, the radiation level is fresh for
 /// 0.3 s, the live weapon arguments, the switch and the mutant names (known to DevMutants).
 /// </summary>
    [TestClass]
    public class S11Tests
    {
        private static S11GrabRow Reaper()
        {
            S11GrabRow r;
            Assert.IsTrue(S11Conventions.TryGrabRow("ReaperLeviathan", out r));
            return r;
        }

        [TestMethod]
        public void GrabClock_HoldRecordsDamageAndEnd()
        {
            S11GrabRow row = Reaper();
            var c = new S11GrabClock(100.0, S11GrabRules.HoldSeconds(row, 0.4), row); // 10 s
            int holds = 0, damage = 0;
            for (double t = 100.0; t < 115.0; t += 0.01)
            {
                if (c.Expired(t)) break;
                if (c.HoldDue(t)) holds++;
                if (c.DamageDue(t)) damage++;
            }
            Assert.AreEqual(110.0, c.EndS, 1e-9);
            Assert.IsTrue(holds >= 495 && holds <= 500, "a hold record every 20 ms over 10 s (20261024-030926): " + holds);
            Assert.AreEqual(9, damage, "a hold damage tick every second after the first (Invoke(\"DamageVehicle\", 1, 1))");
            Assert.IsFalse(S11GrabClock.CooldownOver(110.0, 119.9, row));
            Assert.IsTrue(S11GrabClock.CooldownOver(110.0, 120.0, row));
            Assert.IsTrue(S11GrabClock.CooldownOver(double.NegativeInfinity, 0, row));
            // a long frame never stacks hold records
            var c2 = new S11GrabClock(0, 10, row);
            Assert.IsTrue(c2.HoldDue(5.0));
            Assert.IsFalse(c2.HoldDue(5.0));
        }

        [TestMethod]
        public void FollowWatch_LetsGoWhenSeDoesNotFollow()
        {
            //  SE refused (Static, TooLarge, AlreadyHeld) or let go after a hitch: the grid stays behind the mouth
            var w = new S11FollowWatch();
            Assert.IsFalse(w.Lost(1.0, 0.0, 50.0), "inside the grace the grid is still on its way");
            Assert.IsFalse(w.Lost(1.6, 0.0, 50.0), "the gap starts counting");
            Assert.IsFalse(w.Lost(2.5, 0.0, 50.0));
            Assert.IsTrue(w.Lost(2.6, 0.0, 50.0), "1 s over 15 m: let go");
            var f = new S11FollowWatch();
            Assert.IsFalse(f.Lost(2.0, 0.0, 16.0));
            Assert.IsFalse(f.Lost(2.5, 0.0, 3.0), "back within 15 m resets");
            Assert.IsFalse(f.Lost(3.4, 0.0, 16.0));
            Assert.IsFalse(f.Lost(4.0, 0.0, double.NaN), "no collider seen: no verdict");
            Assert.IsFalse(f.Lost(10.0, 0.0, 2.0), "a held grid that follows is never let go");
        }

        [TestMethod]
        public void Accumulator_SumsExactlyAndCaps()
        {
            var a = new S11Accumulator();
            double sent = 0;
            for (int i = 0; i < 500; i++)
            {
                a.Add(10.0 * 0.02); // OxygenArea 10 units/s at 50 Hz
                sent += a.Take(i * 0.02, S11Conventions.Oxygen.MaxRecordS);
            }
            sent += a.Take(1e9, 1e9);
            Assert.AreEqual(100.0, sent, 1e-9, "10 s in an air pocket = 100 SN seconds, no loss, no double");
            a.Add(500);
            Assert.AreEqual(120.0, a.Take(2e9, 120), "a record carries at most MaxRecordS");
            a.Add(double.NaN); a.Add(-5);
            Assert.AreEqual(380.0, a.Pending, 1e-9);
        }

        [TestMethod]
        public void OnceSet_EmpOncePerBlastAndGrid()
        {
            var s = new S11OnceSet { Capacity = 4 };
            Assert.IsTrue(s.Add(7, 0x1234UL));
            Assert.IsFalse(s.Add(7, 0x1234UL), "the blast's trigger fires per collider and per frame: one record");
            Assert.IsTrue(s.Add(7, 0x9999UL), "another grid");
            Assert.IsTrue(s.Add(8, 0x1234UL), "another blast");
            for (int i = 0; i < 10; i++) s.Add(100 + i, 1UL);
            Assert.AreEqual(4, s.Count, "bounded");
        }

        [TestMethod]
        public void Fresh_RadiationLevel()
        {
            var f = new S11Fresh();
            Assert.AreEqual(0.0, f.Get(1.0));
            f.Seen(0.6, 10.0);
            Assert.AreEqual(0.6, f.Get(10.25));
            Assert.AreEqual(0.0, f.Get(10.31), "older than 0.3 s: RadiatePlayerInRange stopped setting it");
            f.Seen(0.0, 11.0);
            Assert.AreEqual(0.0, f.Get(11.0), "a zero write (the hider) is not a level");
        }

        [TestMethod]
        public void Args_SwitchAndMutants()
        {
            int c, t;
            int? m, fl;
            string err;
            Assert.IsTrue(S11Args.TryWeapon("handheld", "leviathan", null, "0.6", out c, out t, out m, out fl, out err));
            Assert.AreEqual(S11Conventions.WeaponClass.Handheld, c);
            Assert.AreEqual(S11Conventions.Tier.Leviathan, t);
            Assert.IsFalse(m.HasValue);
            Assert.AreEqual(600, fl.Value);
            Assert.IsTrue(S11Args.TryWeapon("large", "3", "1.5", null, out c, out t, out m, out fl, out err));
            Assert.AreEqual(1500, m.Value);
            Assert.IsFalse(S11Args.TryWeapon("rifle", "small", "1", null, out c, out t, out m, out fl, out err));
            Assert.IsFalse(S11Args.TryWeapon("hand", "small", "-1", null, out c, out t, out m, out fl, out err));
            Assert.IsFalse(S11Args.TryWeapon("hand", "small", null, "1.5", out c, out t, out m, out fl, out err));
            Assert.IsFalse(S11Args.TryWeapon("hand", "small", null, null, out c, out t, out m, out fl, out err));
            Assert.IsTrue(S11Switches.Parse(null) && S11Switches.Parse("on") && !S11Switches.Parse("off") && !S11Switches.Parse("0"));
            foreach (string n in S11Mutants.All)
            {
                Assert.IsTrue(n.StartsWith("S11.M", StringComparison.Ordinal));
                Assert.IsTrue(DevMutants.IsKnown(n), n + " known to DevMutants (the S11 hook)");
            }
            Assert.AreEqual(14, S11Mutants.All.Length); // pass 6: S11.M11 (debris filter);: S11.M14 surface, S11.M15 hit test, S11.M16 rage
            CollectionAssert.Contains(S11Mutants.All, "S11.M14");
            CollectionAssert.Contains(S11Mutants.All, "S11.M15");
            CollectionAssert.Contains(S11Mutants.All, "S11.M16");
        }
    }
}
