using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeepEngineers.SN;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;
using InteriorClass = DeepEngineers.Wire.S7Conventions.InteriorClass;
using ToolSource = DeepEngineers.Wire.S7Conventions.ToolSource;

namespace DeepEngineers.SN.Tests
{
    /// <summary>
 /// S7p-gameplay, the Subnautica side's rules (ROADMAP "### S7p Polish pass" P11, P13, P14): the creature damage

 /// ship: 100,669 parts against 2,250 room boxes), the mirror budgets and the camera's per-axis motion.
 /// </summary>
    [TestClass]
    public class S7pGameplayTests
    {
        // ------------------------------------------------------------------------------------------ P14 room index

        private static CellBox[] RandomBoxes(Random r, int n, int span, int maxSide, bool withOdd)
        {
            var b = new CellBox[n];
            for (int i = 0; i < n; i++)
            {
                int x = r.Next(-span, span), y = r.Next(-span, span), z = r.Next(-span, span);
                b[i] = new CellBox(x, y, z, x + r.Next(0, maxSide), y + r.Next(0, maxSide), z + r.Next(0, maxSide));
            }
            if (withOdd && n > 3)
            {
                b[0] = new CellBox(5, 5, 5, 4, 4, 4);                       // invalid (Max < Min): scanned for every cell
                b[1] = new CellBox(-2000, -3, -2000, 2000, 3, 2000);        // huge: the always-scanned list
                b[2] = new CellBox(int.MaxValue - 2, 0, 0, int.MaxValue, 1, 1); // inflation at the int edge
            }
            return b;
        }

        [TestMethod]
        public void RoomIndex_EqualsWireLinearScan_OnRandomSets()
        {
            var r = new Random(20261017);
            for (int set = 0; set < 40; set++)
            {
                int n = S7pRoomIndex.MinBoxes + r.Next(0, 400);
                CellBox[] boxes = RandomBoxes(r, n, 60, 6, set % 3 == 0);
                S7pRoomIndex ix = S7pRoomIndex.For(boxes);
                Assert.IsNotNull(ix, "an index for " + n + " boxes");
                Assert.AreSame(ix, S7pRoomIndex.For(boxes), "one index per array instance");
                for (int k = 0; k < 3000; k++)
                {
                    int x = r.Next(-70, 70), y = r.Next(-70, 70), z = r.Next(-70, 70);
                    if (k % 50 == 0) { CellBox b = boxes[r.Next(boxes.Length)]; x = b.MaxX + 1; y = b.MinY - 1; z = b.MinZ; } // shell cells
                    Assert.AreEqual(InteriorRules.Classify(x, y, z, boxes), ix.Classify(x, y, z), "set " + set + " cell (" + x + "," + y + "," + z + ")");
                }
            }
        }

        [TestMethod]
        public void RoomIndex_ClassifyLocal_EqualsWire_SmallAndLargeSets_AndMetres()
        {
            var r = new Random(7);
            foreach (int n in new[] { 0, 1, 5, S7pRoomIndex.MinBoxes - 1, S7pRoomIndex.MinBoxes, 300 })
            {
                CellBox[] boxes = n == 0 ? new CellBox[0] : RandomBoxes(r, n, 30, 5, false);
                foreach (double gs in new[] { 2.5, 0.5 })
                    for (int k = 0; k < 2000; k++)
                    {
                        double tx = (r.NextDouble() - 0.5) * 80 * gs, ty = (r.NextDouble() - 0.5) * 80 * gs, tz = (r.NextDouble() - 0.5) * 80 * gs;
                        if (k % 7 == 0) { tx = Math.Round(tx / gs) * gs + gs / 2; } // exactly on a half cell: rounding away from zero, as Wire
                        Assert.AreEqual(InteriorRules.ClassifyLocal(tx, ty, tz, gs, boxes), S7pRoomIndex.ClassifyLocal(tx, ty, tz, gs, boxes), "n " + n + " gs " + gs);
                    }
            }
            Assert.AreEqual(InteriorClass.Exterior, S7pRoomIndex.ClassifyLocal(1, 2, 3, 2.5, null));
        }

        /// <summary>

 /// Reclassify through the index keeps the work per part bounded (Wire's scan tested all 2,250 boxes for every part:
 /// 2.3·10⁸ tests, 4,474 ms on Subnautica's main thread).
 /// </summary>
        [TestMethod]
        public void RoomIndex_WorkshopShip_ReclassifyIsBounded_AndExact()
        {
            // 2,250 room boxes: rooms of 3×3×3 cells spread over a 60 × 30 × 240-cell hull (a large grid, 2.5 m cells)
            var boxes = new List<CellBox>();
            for (int x = 0; x < 60 && boxes.Count < 2250; x += 4)
                for (int y = 0; y < 30 && boxes.Count < 2250; y += 4)
                    for (int z = 0; z < 240 && boxes.Count < 2250; z += 4)
                        boxes.Add(new CellBox(x, y, z, x + 2, y + 2, z + 2));
            Assert.AreEqual(2250, boxes.Count);
            CellBox[] arr = boxes.ToArray();
            var g = new GridRecord { Id = 1, TargetClass = GridClass.LargeGrid };
            var r = new Random(100669);
            for (int i = 0; i < 100669; i++)
            {
                var p = new PartRecord { EntityId = 1, PartKey = (ulong)(i + 1), ModelKey = 7, Role = PartRole.Chunk };
                p.Local[0] = 1; p.Local[4] = 1; p.Local[8] = 1;
                p.Local[9] = (float)(r.Next(-2, 62) * 2.5);
                p.Local[10] = (float)(r.Next(-2, 32) * 2.5);
                p.Local[11] = (float)(r.Next(-2, 242) * 2.5);
                g.PutPart(p);
            }
            long tests0 = S7pRoomIndex.BoxTests, lookups0 = S7pRoomIndex.Lookups;
            var sw = Stopwatch.StartNew();
            g.Rooms = arr;
            int moved = InteriorChunks.Reclassify(g);
            sw.Stop();
            long tests = S7pRoomIndex.BoxTests - tests0, lookups = S7pRoomIndex.Lookups - lookups0;
            Assert.AreEqual(100669, lookups, "one lookup per chunked part");
            Assert.IsTrue(moved > 0, "parts moved to interior/boundary cells");
            Assert.IsTrue(tests <= lookups * 40, "box tests per part bounded (" + tests + " for " + lookups + "; the scan did " + (long)lookups * 2250 + ")");
            Assert.IsTrue(sw.ElapsedMilliseconds < 2000, "Reclassify of 100,669 parts took " + sw.ElapsedMilliseconds + " ms (budget: never a multi-second stall)");
            // exactness on every part: the cell's class is Wire's rule
            foreach (PartRecord p in g.Parts.Values)
            {
                byte want = InteriorRules.ClassifyLocal(p.Local[9], p.Local[10], p.Local[11], 2.5, arr);
                Assert.AreEqual(want, p.Cell.C, "part " + p.PartKey);
            }
        }

        // ------------------------------------------------------------------------------------------ P11 damage rules

        [TestMethod]
        public void Damage_ProjectileCountAndRange()
        {
            Assert.AreEqual(1, S7pDamageRules.ProjectileCount(S7EventRules.StreakIntensity(1)));
            Assert.AreEqual(8, S7pDamageRules.ProjectileCount(S7EventRules.StreakIntensity(8)));
            Assert.AreEqual(8, S7pDamageRules.ProjectileCount(S7EventRules.StreakIntensity(20)), "the streak caps at 8");
            for (int c = 1; c <= 8; c++) Assert.AreEqual(c, S7pDamageRules.ProjectileCount(S7EventRules.StreakIntensity(c)), "round trip " + c);
            Assert.AreEqual(1, S7pDamageRules.ProjectileCount(0f));
            Assert.AreEqual(1, S7pDamageRules.ProjectileCount(float.NaN));
            Assert.AreEqual(S7pDamageRules.MaxRayM, S7pDamageRules.RayRangeM(S7EventRules.StreakLength(800f)), "a capped streak flies on (800 m, rays to 250 m)");
            Assert.AreEqual(30f, S7pDamageRules.RayRangeM(S7EventRules.StreakLength(30f)), "a short trajectory is the range");
            Assert.AreEqual(S7pDamageRules.MaxRayM, S7pDamageRules.RayRangeM(float.NaN));
            Assert.AreEqual(S7pDamageRules.MaxRayM, S7pDamageRules.RayRangeM(0f));
        }

        [TestMethod]
        public void Damage_SeNumbersMappedThroughKChar()
        {
            Assert.AreEqual(30f, S7pDamageRules.ProjectileHp(true), "rifle: SmallCaliber 100 × 0.3");
            Assert.AreEqual(33f, S7pDamageRules.ProjectileHp(false), "gatling: LargeCaliber 33");
            Assert.AreEqual(1.0, CombatConventions.DamageScale.KChar, "k_char = 1 SE HP per SN_HP (CONTRACT §4.1)");
            // a 500-damage missile (intensity 0.5) at its centre, half radius, edge, outside
            float i = S7EventRules.ExplosionIntensity(500f);
            Assert.AreEqual(500f, S7pDamageRules.ExplosionHp(i, 4f, 0f), 1e-3f);
            Assert.AreEqual(250f, S7pDamageRules.ExplosionHp(i, 4f, 2f), 1e-3f);
            Assert.AreEqual(0f, S7pDamageRules.ExplosionHp(i, 4f, 4f), 1e-6f);
            Assert.AreEqual(0f, S7pDamageRules.ExplosionHp(i, 4f, 9f), 1e-6f);
            Assert.AreEqual(1000f, S7pDamageRules.ExplosionHp(S7EventRules.ExplosionIntensity(5000f), 10f, 0f), 1e-3f, "the event caps the damage at 1,000");
            Assert.AreEqual(0f, S7pDamageRules.ExplosionHp(i, 0f, 0f));
            Assert.AreEqual(0f, S7pDamageRules.ExplosionHp(0f, 4f, 0f));
            // tools per second
            Assert.AreEqual(20f, S7pDamageRules.ToolDps(EventType.Drill, ToolSource.Hand));
            Assert.AreEqual(16f, S7pDamageRules.ToolDps(EventType.Grinder, ToolSource.Hand));
            Assert.AreEqual(120f, S7pDamageRules.ToolDps(EventType.Grinder, ToolSource.Ship));
            Assert.AreEqual(120f, S7pDamageRules.ToolDps(EventType.Welder, ToolSource.Ship));
            Assert.AreEqual(20f * 60f / 90f, S7pDamageRules.ToolDps(EventType.PrawnDrill, ToolSource.Ship), 1e-4f);
            Assert.AreEqual(0f, S7pDamageRules.ToolDps(EventType.Repair, ToolSource.Hand), "the hand welder heals blocks, never hurts");
            Assert.AreEqual(0f, S7pDamageRules.ToolDps(EventType.Thruster, 0));
            Assert.AreEqual(4f, S7pDamageRules.ToolStepHp(EventType.Grinder, ToolSource.Hand, 0.25, 1.0), 1e-5f, "one 250 ms step of the hand grinder = SE's 4 per cooldown");
            Assert.AreEqual(8f, S7pDamageRules.ToolStepHp(EventType.Grinder, ToolSource.Hand, 0.25, 2.0), 1e-5f);
            Assert.AreEqual(0f, S7pDamageRules.ToolStepHp(EventType.Grinder, ToolSource.Hand, 0.25, 0.0));
            Assert.AreEqual(0.5f, S7pDamageRules.ContactRadius(0.3f));
            Assert.AreEqual(1.5f, S7pDamageRules.ContactRadius(1.5f));
            Assert.AreEqual(3f, S7pDamageRules.ContactRadius(40f));
            Assert.AreEqual(0.5f, S7pDamageRules.ContactRadius(float.NaN));
            Assert.AreEqual(S7pDamageRules.SnExplosive, S7pDamageRules.SnDamageType(EventType.Explosion));
            Assert.AreEqual(S7pDamageRules.SnDrill, S7pDamageRules.SnDamageType(EventType.PrawnDrill));
            Assert.AreEqual(S7pDamageRules.SnNormal, S7pDamageRules.SnDamageType(EventType.BulletStreak));
            Assert.IsTrue(S7pDamageRules.IsDamageEvent(EventType.BulletStreak));
            Assert.IsFalse(S7pDamageRules.IsDamageEvent(EventType.WeaponMuzzle), "the muzzle row only draws (the streak row carries the shot)");
            Assert.IsFalse(S7pDamageRules.IsDamageEvent(EventType.Splash));
        }

        [TestMethod]
        public void Damage_ScaleSwitch()
        {
            double s;
            string e;
            Assert.IsTrue(S7pDamageRules.TryParseScale(null, out s, out e)); Assert.AreEqual(1.0, s);
            Assert.IsTrue(S7pDamageRules.TryParseScale("off", out s, out e)); Assert.AreEqual(0.0, s);
            Assert.IsTrue(S7pDamageRules.TryParseScale("ON", out s, out e)); Assert.AreEqual(1.0, s);
            Assert.IsTrue(S7pDamageRules.TryParseScale("2.5", out s, out e)); Assert.AreEqual(2.5, s);
            Assert.IsTrue(S7pDamageRules.TryParseScale(0.5, out s, out e)); Assert.AreEqual(0.5, s);
            Assert.IsTrue(S7pDamageRules.TryParseScale(false, out s, out e)); Assert.AreEqual(0.0, s);
            Assert.IsFalse(S7pDamageRules.TryParseScale("-1", out s, out e)); Assert.IsNotNull(e);
            Assert.IsFalse(S7pDamageRules.TryParseScale("101", out s, out e));
            Assert.IsFalse(S7pDamageRules.TryParseScale("lots", out s, out e));
            Assert.AreEqual(1.0, S7pDamageRules.ScaleFromEnv("garbage", out e)); Assert.IsNotNull(e, "an unparsable env value is the default, reported");
            Assert.AreEqual(0.0, S7pDamageRules.ScaleFromEnv("off", out e));
        }

        // ------------------------------------------------------------------------------------------ P14 budgets

        [TestMethod]
        public void MirrorBudget_FirstUnitAlwaysRuns_ThenTimeAndCountCaps()
        {
            long f = 1000000; // ticks per second
            Assert.IsTrue(S7pMirrorBudget.Within(0, 999999999, f, 2.0, 0), "the first unit of a frame always runs");
            Assert.IsTrue(S7pMirrorBudget.Within(0, 1000, f, 2.0, 5), "1 ms < 2 ms");
            Assert.IsFalse(S7pMirrorBudget.Within(0, 2000, f, 2.0, 5), "2 ms reached");
            Assert.IsTrue(S7pMirrorBudget.ChildAllowed(0, 100, f, 0));
            Assert.IsFalse(S7pMirrorBudget.ChildAllowed(0, 100, f, S7pMirrorBudget.MaxChildrenPerFrame), "the count cap");
            double keep = S7pMirrorBudget.Scale;
            try
            {
                S7pMirrorBudget.Scale = 2.0;
                Assert.AreEqual(4.0, S7pMirrorBudget.ChildMs, 1e-9);
                Assert.AreEqual(6.0, S7pMirrorBudget.ResultsBudgetMs, 1e-9);
            }
            finally { S7pMirrorBudget.Scale = keep; }
        }

        // ------------------------------------------------------------------------------------------ P13 axis freedom

        [TestMethod]
        public void AxisFreedom_AnglesAndWindows()
        {
            double y, p, r;
            S7pAxisFreedom.Angles(0, 0, 1, 0, 1, 0, out y, out p, out r);
            Assert.AreEqual(0, y, 1e-9); Assert.AreEqual(0, p, 1e-9); Assert.AreEqual(0, r, 1e-9);
            S7pAxisFreedom.Angles(1, 0, 0, 0, 1, 0, out y, out p, out r);
            Assert.AreEqual(90, y, 1e-9, "east");
            double c = Math.Sqrt(0.5);
            S7pAxisFreedom.Angles(0, c, c, 0, c, -c, out y, out p, out r);
            Assert.AreEqual(45, p, 1e-9, "looking 45° up");
            Assert.AreEqual(0, r, 1e-9);
            S7pAxisFreedom.Angles(0, 0, 1, 1, 0, 0, out y, out p, out r);
            Assert.AreEqual(90, Math.Abs(r), 1e-9, "rolled 90°");

            var a = new S7pAxisFreedom();
            a.Add(170, 0, 0);
            a.Add(-170, 0, 0);  // 20° across the ±180 seam, not 340°
            a.Add(-170, 10, 0);
            a.Add(-170, double.NaN, 0); // ignored
            int n;
            a.Take(out y, out p, out r, out n);
            Assert.AreEqual(20, y, 1e-9);
            Assert.AreEqual(10, p, 1e-9);
            Assert.AreEqual(0, r, 1e-9);
            Assert.AreEqual(3, n);
            a.Add(-170, 10, 0); // the next window starts from the last orientation
            a.Take(out y, out p, out r, out n);
            Assert.AreEqual(0, y + p + r, 1e-12);
            Assert.AreEqual(1, n);
            Assert.AreEqual(-179, S7pAxisFreedom.Wrap(181), 1e-9);
            Assert.AreEqual(180, S7pAxisFreedom.Wrap(-180), 1e-9);
            Assert.IsTrue(S7pAxisFreedom.FiniteQuat(0, 0, 0, 1));
            Assert.IsFalse(S7pAxisFreedom.FiniteQuat(float.NaN, 0, 0, 1));
            Assert.IsFalse(S7pAxisFreedom.FiniteQuat(0, float.PositiveInfinity, 0, 1));
            Assert.IsFalse(S7pAxisFreedom.FiniteQuat(0, 0, 0, 0), "a zero quaternion cannot be normalised");
        }
    }
}
