using System;
using System.Collections.Generic;
using DeepEngineers.Wire;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DeepEngineers.Wire.Tests
{
    /// <summary>
 /// S11 rules against the Python twin's vectors (tests/s11/vectors/*.tsv, tools/s11/s11_ref.py) plus the slice's own
 /// hand checks: A (a handheld weapon never kills a leviathan, a warhead does; N stacked warheads are N yields), C (the
 /// size class, the hold, the push), D (the hazard set and the per-second cap), E (heal, oxygen litres), the payload
 /// checks of the S11 C8 units.
 /// </summary>
    [TestClass]
    public class S11RulesTests
    {
        // ------------------------------------------------------------------------------------------ A

        [TestMethod]
        public void Danger_Vectors()
        {
            var t = new S11WeaponTable();
            int n = 0;
            foreach (string[] f in S11Vectors.Lines("danger.tsv"))
            {
                int cls = S11Vectors.I(f[0]);
                double b = S11Vectors.D(f[1]), h = S11Vectors.D(f[2]), mh = S11Vectors.D(f[3]);
                Assert.AreEqual(S11Vectors.I(f[4]), S11Danger.TierOf(mh), "tier of " + mh);
                S11Vectors.Near(S11Vectors.D(f[5]), t.Applied(cls, b, h, mh), "applied " + string.Join(" ", f));
                n++;
            }
            Assert.IsTrue(n >= 90);
        }

        [TestMethod]
        public void Danger_HandheldNeverKillsALeviathan_WarheadAndLargeTurretDo()
        {
            var t = new S11WeaponTable();
            double hp = 5000;
            for (int i = 0; i < 100000; i++)
            {
                double d = t.Applied(S11Conventions.WeaponClass.Handheld, 30, hp, 5000);
                if (d <= 0) break;
                hp -= d;
            }
            Assert.AreEqual(2500.0, hp, 1e-9, "the rifle stops at half a reaper");
            Assert.AreEqual(0.0, t.Applied(S11Conventions.WeaponClass.Handheld, 30, 2500, 5000), "and never goes below");
            Assert.AreEqual(2500.0, t.Applied(S11Conventions.WeaponClass.Explosion, S11BlastRules.DamageHp(15000, 22.4415, 10, 1000), 2500, 5000), 1e-9,
                            "a warhead at 10 m finishes it");
            Assert.AreEqual(33.0, t.Applied(S11Conventions.WeaponClass.LargeGrid, 33, 2500, 5000), "a large turret keeps hurting");
            Assert.AreEqual(30.0, t.Applied(S11Conventions.WeaponClass.Handheld, 30, 300, 300), "a stalker-sized creature dies to the rifle");
            // the live retune (de.s11 weapon)
            string err;
            Assert.IsTrue(t.Set(S11Conventions.WeaponClass.Handheld, S11Conventions.Tier.Leviathan, 500, 900, out err));
            Assert.AreEqual(15.0, t.Applied(S11Conventions.WeaponClass.Handheld, 30, 5000, 5000));
            Assert.AreEqual(0.0, t.Applied(S11Conventions.WeaponClass.Handheld, 30, 4500, 5000));
            Assert.IsFalse(t.Set(5, 1, 1000, 0, out err));
            Assert.IsFalse(t.Set(1, 1, 1000, 1001, out err));
            t.Reset();
            Assert.AreEqual(500, t.Floor(1, 3));
            Assert.AreEqual(S11Conventions.WeaponClass.Handheld, S11Danger.ClassOf(false, true, true));
            Assert.AreEqual(S11Conventions.WeaponClass.LargeGrid, S11Danger.ClassOf(false, false, true));
            Assert.AreEqual(S11Conventions.WeaponClass.SmallGrid, S11Danger.ClassOf(false, false, false));
            Assert.AreEqual(S11Conventions.WeaponClass.Explosion, S11Danger.ClassOf(true, true, false));
        }

        [TestMethod]
        public void Blast_Vectors()
        {
            int groups = 0;
            foreach (string[] f in S11Vectors.Lines("blast.tsv"))
            {
                switch (f[0])
                {
                    case "code":
                        Assert.AreEqual(S11Vectors.I(f[2]), (int)S11BlastRules.YieldCode(S11Vectors.D(f[1])), "code " + f[1]);
                        break;
                    case "cls":
                        Assert.AreEqual(S11Vectors.I(f[2]), (int)S11BlastRules.ClassBits(S11Vectors.I(f[1])), "class bits " + f[1]);
                        break;
                    case "clsof":
                        Assert.AreEqual(S11Vectors.I(f[2]), S11BlastRules.ClassOfSubtype((ushort)S11Vectors.I(f[1])), "class of subtype " + f[1]);
                        break;
                    case "origin":
                        Assert.AreEqual(S11Vectors.I(f[3]), S11BlastRules.ClassOfOrigin(f[1] == "1", f[2] == "1"), "origin " + f[1] + f[2]);
                        break;
                    case "yieldof":
                        Assert.AreEqual(S11Vectors.D(f[3]), S11BlastRules.YieldOf((ushort)S11Vectors.I(f[1]), (float)S11Vectors.D(f[2])), "yieldof " + f[1]);
                        break;
                    case "damage":
                        S11Vectors.Near(S11Vectors.D(f[5]), S11BlastRules.DamageHp(S11Vectors.D(f[1]), S11Vectors.D(f[2]), S11Vectors.D(f[3]), S11Vectors.I(f[4])), "damage");
                        break;
                    case "group":
                    {
                        int mg = S11Vectors.I(f[1]);
                        var batch = new List<S11Blast>();
                        foreach (string e in f[2].Split(';'))
                        {
                            string[] p = e.Split(',');
                            batch.Add(new S11Blast(S11Vectors.D(p[0]), S11Vectors.D(p[1]), S11Vectors.D(p[2]), S11Vectors.D(p[3]), S11Vectors.D(p[4]), 0));
                        }
                        List<S11Blast> got = S11BlastRules.Group(batch, mg);
                        string[] want = f[3].Split(';');
                        Assert.AreEqual(want.Length, got.Count, "groups of " + f[2]);
                        double ySum = 0;
                        int nSum = 0;
                        for (int i = 0; i < want.Length; i++)
                        {
                            string[] p = want[i].Split(',');
                            S11Vectors.Near(new Vec3(S11Vectors.D(p[0]), S11Vectors.D(p[1]), S11Vectors.D(p[2])), got[i].Centre, "centre", 1e-9);
                            S11Vectors.Near(S11Vectors.D(p[3]), got[i].R, "radius", 1e-9);
                            S11Vectors.Near(S11Vectors.D(p[4]), got[i].Yield, "yield", 1e-12);
                            S11Vectors.Near(S11Vectors.D(p[5]), got[i].SumR3, "sumR3", 1e-12);
                            Assert.AreEqual(S11Vectors.I(p[6]), got[i].Count, "count");
                            ySum += got[i].Yield;
                            nSum += got[i].Count;
                        }
                        double yIn = 0;
                        foreach (S11Blast b in batch) yIn += b.Yield;
                        S11Vectors.Near(yIn, ySum, "nothing dropped: yields", 1e-12);
                        Assert.AreEqual(batch.Count, nSum, "nothing dropped: counts");
                        groups++;
                        break;
                    }
                }
            }
            Assert.IsTrue(groups >= 10);
        }

        [TestMethod]
        public void Blast_FiveStackedWarheads_ActAsFiveYields()
        {
            var batch = new List<S11Blast>();
            for (int i = 0; i < 5; i++) batch.Add(new S11Blast(0, -20, 0, 22.4415, 15000, 0));
            List<S11Blast> g = S11BlastRules.Group(batch, S11Conventions.Blast.MaxGroupsPerTick);
            Assert.AreEqual(1, g.Count);
            Assert.AreEqual(75000.0, g[0].Yield);
            Assert.AreEqual(5, g[0].Count);
            Assert.AreEqual(22.4415 * Math.Pow(5, 1.0 / 3.0), g[0].R, 1e-9);
            // one creature 30 m away: one warhead alone does not reach it, five do
            Assert.AreEqual(0.0, S11BlastRules.DamageHp(15000, 22.4415, 30, 1000));
            Assert.IsTrue(S11BlastRules.DamageHp(g[0].Yield, g[0].R, 30, 1000) > 5000, "five warheads kill a reaper at 30 m");
            Assert.AreEqual(1500, S11BlastRules.YieldCode(15000));
            Assert.AreEqual(15000.0, S11BlastRules.YieldOf(1500, 1f));
            Assert.AreEqual(1000.0, S11BlastRules.YieldOf(0, 1f), "an older SE: S7's capped intensity");
        }

        [TestMethod]
        public void Blast_HandheldRocket_KeepsTheFloor_WarheadDoesNot()
        {
            //  SE's hand-held and small-grid rocket launchers make Explosion rows; their class comes from the origin
            var t = new S11WeaponTable();
            double hp = S11BlastRules.DamageHp(20000, 30, 1, 1000);
            int hand = S11BlastRules.ClassOfSubtype((ushort)(S11BlastRules.ClassBits(S11BlastRules.ClassOfOrigin(true, false)) | 5));
            Assert.AreEqual(S11Conventions.WeaponClass.Handheld, hand);
            Assert.AreEqual(2500.0, t.Applied(hand, hp, 5000, 5000), 1e-9, "a hand rocket stops at half a reaper");
            int small = S11BlastRules.ClassOfOrigin(false, true);
            Assert.AreEqual(1250.0, t.Applied(small, hp, 2500, 5000), 1e-9, "a small-grid rocket stops at a quarter");
            Assert.AreEqual(5000.0, t.Applied(S11BlastRules.ClassOfSubtype(5), hp, 5000, 5000), 1e-9, "a warhead (no class bits) kills");
            Assert.AreEqual(0.0, S11GrabRules.AngleDeg(Quat.Identity, Quat.Identity), 1e-9);
            Assert.AreEqual(90.0, S11GrabRules.AngleDeg(Quat.Identity, new Quat(0, Math.Sqrt(0.5), 0, Math.Sqrt(0.5))), 1e-9);
            Assert.AreEqual(180.0, S11GrabRules.AngleDeg(Quat.Identity, new Quat(1, 0, 0, 0)), 1e-9);
            Assert.AreEqual(0.0, S11GrabRules.AngleDeg(new Quat(0, 0, 0, -1), Quat.Identity), 1e-9, "q and -q are one orientation");
        }

        [TestMethod]
        public void Chain_Vectors()
        {
            var chains = new Dictionary<int, S11BlastChain>();
            int steps = 0;
            foreach (string[] f in S11Vectors.Lines("chain.tsv"))
            {
                int ci = S11Vectors.I(f[0]);
                long t = long.Parse(f[1]);
                S11BlastChain ch;
                if (!chains.TryGetValue(ci, out ch)) chains[ci] = ch = new S11BlastChain();
                var esc = new List<S11Blast>();
                if (f[2] == "close")
                {
                    ch.Close(t, esc);
                    string want = f.Length > 4 ? f[4] : "";
                    string[] w = want.Length == 0 ? new string[0] : want.Split(';');
                    Assert.AreEqual(w.Length, esc.Count, "escalations at " + t + " case " + ci);
                    for (int i = 0; i < w.Length; i++)
                    {
                        string[] p = w[i].Split(',');
                        S11Vectors.Near(new Vec3(S11Vectors.D(p[0]), S11Vectors.D(p[1]), S11Vectors.D(p[2])), esc[i].Centre, "esc centre", 1e-9);
                        S11Vectors.Near(S11Vectors.D(p[3]), esc[i].R, "esc radius", 1e-9);
                        S11Vectors.Near(S11Vectors.D(p[4]), esc[i].Yield, "esc yield");
                        Assert.AreEqual(S11Vectors.I(p[5]), esc[i].Count, "esc count");
                    }
                }
                else
                {
                    ch.Close(t, esc);
                    string[] p = f[3].Split(',');
                    bool play = ch.Admit(new S11Blast(S11Vectors.D(p[0]), S11Vectors.D(p[1]), S11Vectors.D(p[2]), S11Vectors.D(p[3]), S11Vectors.D(p[4]), t), t);
                    Assert.AreEqual(S11Vectors.I(f[4]) == 1, play, "admit at " + t + " case " + ci);
                }
                steps++;
            }
            Assert.IsTrue(steps >= 60);
        }

        // ------------------------------------------------------------------------------------------ C

        [TestMethod]
        public void Grab_Vectors()
        {
            int n = 0;
            foreach (string[] f in S11Vectors.Lines("grab.tsv"))
            {
                S11GrabRow g;
                S11PushRow p;
                switch (f[0])
                {
                    case "fits":
                        Assert.IsTrue(S11Conventions.TryGrabRow(f[1], out g));
                        Assert.AreEqual(f[5] == "1", S11GrabRules.SizeFits(g, S11Vectors.D(f[2]), S11Vectors.D(f[3]), S11Vectors.D(f[4])), "fits " + string.Join(" ", f));
                        break;
                    case "hold":
                        Assert.IsTrue(S11Conventions.TryGrabRow(f[1], out g));
                        S11Vectors.Near(S11Vectors.D(f[3]), S11GrabRules.HoldSeconds(g, S11Vectors.D(f[2])), "hold");
                        break;
                    case "holddmg":
                        Assert.IsTrue(S11Conventions.TryGrabRow(f[1], out g));
                        S11Vectors.Near(S11Vectors.D(f[3]), S11GrabRules.HoldDamage(g, S11Vectors.D(f[2])), "holddmg");
                        break;
                    case "release":
                        Assert.IsTrue(S11Conventions.TryGrabRow(f[1], out g));
                        Assert.AreEqual(f[3] == "1", S11GrabRules.ReleasesOn(g, S11Vectors.I(f[2])), "release " + f[1] + " " + f[2]);
                        break;
                    case "accel":
                        S11Vectors.Near(S11Vectors.V(f[5]), S11GrabRules.HoldAccel(S11Vectors.V(f[1]), S11Vectors.V(f[2]), S11Vectors.V(f[3]), S11Vectors.V(f[4])), "accel", 1e-9);
                        break;
                    case "ang":
                        S11Vectors.Near(S11Vectors.V(f[4]), S11GrabRules.AngAccel(S11Vectors.Q(f[1]), S11Vectors.Q(f[2]), S11Vectors.V(f[3]),
                            S11Conventions.Hold.AngOmegaMilli / 1000.0, S11Conventions.Hold.ZetaPermille / 1000.0, S11Conventions.Hold.MaxAngAccelMilli / 1000.0), "ang", 1e-9);
                        break;
                    case "servo":
                        S11Vectors.Near(S11Vectors.V(f[4]), S11GrabRules.ServoVelocity(S11Vectors.V(f[1]), S11Vectors.V(f[2]), S11Vectors.V(f[3])), "servo", 1e-9);
                        break;
                    case "servoang":
                        S11Vectors.Near(S11Vectors.V(f[3]), S11GrabRules.ServoAngular(S11Vectors.Q(f[1]), S11Vectors.Q(f[2])), "servoang", 1e-9);
                        break;
                    case "lead":
                        S11Vectors.Near(S11Vectors.V(f[4]), S11GrabRules.TargetNow(S11Vectors.V(f[1]), S11Vectors.V(f[2]), S11Vectors.D(f[3])), "lead " + f[3], 1e-9);
                        break;
                    case "snap":
                        Assert.AreEqual(f[2] == "1", S11GrabRules.Snaps(S11Vectors.D(f[1])), "snap " + f[1]);
                        break;
                    case "amax":
                        S11Vectors.Near(S11Vectors.D(f[2]), S11GrabRules.MaxHoldAccel(S11Vectors.D(f[1])), "amax " + f[1]);
                        break;
                    case "debris":
                        Assert.AreEqual(f[3] == "1", S11DebrisRules.IsDebris(S11Vectors.D(f[1]), S11Vectors.D(f[2])), "debris " + string.Join(" ", f));
                        break;
                    case "estblocks":
                        S11Vectors.Near(S11Vectors.D(f[3]), S11DebrisRules.EstimateBlocks(S11Vectors.D(f[1]), S11Vectors.D(f[2])), "estblocks");
                        break;
                    case "effmass":
                        S11Vectors.Near(S11Vectors.D(f[3]), S11PushRules.EffectiveMass(S11Vectors.D(f[1]), S11Vectors.D(f[2])), "effective mass");
                        break;
                    case "share":
                        Assert.AreEqual(S11Vectors.D(f[3]), S11GrabRules.BuoyancyShare(f[1] == "1", S11Vectors.D(f[2])), "share");
                        break;
                    case "push":
                        Assert.IsTrue(S11Conventions.TryPushRow(f[1], out p));
                        double j = S11PushRules.Impulse(p, S11Vectors.D(f[2]), S11Vectors.D(f[3]));
                        S11Vectors.Near(S11Vectors.D(f[5]), j, "impulse");
                        S11Vectors.Near(S11Vectors.D(f[6]), S11PushRules.Dv(p, j, S11Vectors.D(f[4])), "dv");
                        break;
                    case "blend":
                        S11Vectors.Near(S11Vectors.D(f[3]), S11GrabRules.BlendFactor(S11Vectors.D(f[1]), S11Vectors.D(f[2])), "blend " + f[1] + " " + f[2]);
                        break;
                    case "blendxt":
                        S11Vectors.Near(S11Vectors.V(f[6]), S11GrabRules.BlendTarget(S11Vectors.V(f[1]), S11Vectors.V(f[2]), S11Vectors.D(f[4])), "blend target", 1e-9);
                        S11Vectors.Near(S11Vectors.V(f[7]), S11GrabRules.BlendVelocity(S11Vectors.V(f[1]), S11Vectors.V(f[2]), S11Vectors.V(f[3]), S11Vectors.D(f[4]), S11Vectors.D(f[5])), "blend velocity", 1e-9);
                        break;
                    case "surf":
                        Assert.AreEqual(f[2] == "1", S11SurfaceRules.TopReleases(S11Vectors.D(f[1])), "surf " + f[1]);
                        break;
                    case "followy":
                        S11Vectors.Near(S11Vectors.D(f[3]), S11SurfaceRules.FollowY(S11Vectors.D(f[1]), S11Vectors.D(f[2])), "followy " + f[1] + " " + f[2]);
                        break;
                    case "relv":
                        S11Vectors.Near(S11Vectors.V(f[4]), S11GrabRules.ReleaseVelocity(S11Vectors.V(f[1]), S11Vectors.V(f[2]), f[3] == "1"), "relv " + string.Join(" ", f), 1e-9);
                        break;
                    case "backcast":
                        S11Vectors.Near(S11Vectors.D(f[2]), S11MissileRules.BackCastM(S11Vectors.D(f[1])), "backcast " + f[1]);
                        break;
                    case "behind":
                        Assert.AreEqual(f[4] == "1", S11MissileRules.Behind(S11Vectors.V(f[1]), S11Vectors.V(f[2]), S11Vectors.V(f[3])), "behind " + string.Join(" ", f));
                        break;
                    case "rage":
                    {
                        S11RageRow rr;
                        Assert.IsTrue(S11Conventions.TryRageRow(f[1], out rr));
                        var book = new S11HoldRage();
                        string[] hits = f[3].Split(','), want = f[4].Split(';');
                        for (int i = 0; i < hits.Length; i++)
                        {
                            string[] w = want[i].Split(':');
                            Assert.AreEqual(S11Vectors.I(w[0]), (int)book.Hit(S11Vectors.D(hits[i]), S11Vectors.D(f[2]), rr), "rage " + string.Join(" ", f) + " hit " + i);
                            S11Vectors.Near(S11Vectors.D(w[1]), book.DamageMult(rr), "rage mult " + i);
                        }
                        break;
                    }
                    case "slug":
                        S11Vectors.Near(S11Vectors.D(f[2]), S11MissileRules.SlugYield(S11Vectors.D(f[1])), "slug " + f[1]);
                        break;
                    case "attr":
                        Assert.AreEqual(f[3] == "1", S11MissileRules.Attributed(S11Vectors.D(f[1]), S11Vectors.D(f[2])), "attr " + f[1] + " " + f[2]);
                        break;
                    case "first":
                    {
                        int[] kinds = f[1] == "-" ? new int[0] : Array.ConvertAll(f[1].Split(','), S11Vectors.I);
                        Assert.AreEqual(S11Vectors.I(f[2]), S11MissileRules.FirstCreature(kinds, kinds.Length), "first " + f[1]);
                        break;
                    }
                    default:
                        Assert.Fail("unknown grab row " + f[0]);
                        break;
                }
                n++;
            }
            Assert.IsTrue(n >= 100);
        }

        [TestMethod]
        public void Grab_SkinExtents_RotatedBoxes()
        {
            var s = new SkinSectionData();
            s.Boxes.Add(SkinBoxData.Aligned(new Vec3(0, 0, 0), new Vec3(1.25, 0.25, 2.5), 0));
            var r = new SkinBoxData { Centre = new Vec3(3, 0, 0), Half = new Vec3(1, 1, 0.1), E0 = new Vec3(Math.Sqrt(0.5), Math.Sqrt(0.5), 0), E1 = new Vec3(-Math.Sqrt(0.5), Math.Sqrt(0.5), 0), E2 = new Vec3(0, 0, 1), Flags = 1 };
            s.Boxes.Add(r);
            Vec3 min, max;
            Assert.IsTrue(S11GrabRules.SkinExtents(new[] { s }, out min, out max));
            Assert.AreEqual(-1.25, min.X, 1e-12);
            Assert.AreEqual(3 + Math.Sqrt(2), max.X, 1e-12);
            Assert.AreEqual(Math.Sqrt(2), max.Y, 1e-12);
            Assert.AreEqual(-2.5, min.Z, 1e-12);
            Assert.IsFalse(S11GrabRules.SkinExtents(new SkinSectionData[0], out min, out max));
            S11GrabRow reaper;
            Assert.IsTrue(S11Conventions.TryGrabRow("ReaperLeviathan", out reaper));
            Assert.IsTrue(S11GrabRules.SizeFits(reaper, 3, 2, 5), "a seamoth-sized small ship");
            Assert.IsTrue(S11GrabRules.SizeFits(reaper, 5.7, 4.1, 9.7), "the owner's small ship (session 2026-10-23) fits the scaled reaper");
            Assert.IsTrue(S11GrabRules.SizeFits(reaper, 12.5, 12.5, 2.5), "2026-10-24: a 5 x 5 large wall (391 m3) fits the x2.5 reaper (400 m3)");
            Assert.IsFalse(S11GrabRules.SizeFits(reaper, 8, 8, 8), "a 512 m3 grid is bitten and pushed");
            Assert.IsFalse(S11GrabRules.SizeFits(reaper, 16, 2.5, 2.5), "a 16 m edge is over the reaper's 15 m");
            var sc = new S11ScaleTable();
            Assert.AreEqual(2.5, sc.Factor(2540), "owner 2026-10-24: reaper x2.5 (x3 clipped the ground)");
            Assert.AreEqual(11.0, sc.BiteMult(2540), "pass 5: the bite multiplier is its own value, not the factor");
            Assert.AreEqual(1.0, sc.Factor(2550), "a crabsquid is never scaled");
            string err;
            Assert.IsTrue(sc.Set(2540, 3.0, 1.0, out err));
            Assert.AreEqual(3.0, sc.Factor(2540));
            Assert.AreEqual(1.0, sc.BiteMult(2540));
            Assert.IsFalse(sc.Set(2540, 11, null, out err));
            Assert.IsFalse(sc.Set(9999, 2, null, out err));
            Assert.IsTrue(sc.Set(2553, null, 14, out err), "bite up to 50");
            Assert.IsFalse(sc.Set(2553, null, 51, out err));
            sc.Reset();
            Assert.AreEqual(11.0, sc.BiteMult(2540));
            Assert.AreEqual(5.8, sc.BiteMult(2553), 1e-12);
            Assert.AreEqual(0.0, S11HudRules.BloodAlpha(800, 80), "the overlay is gone 800 ms after the last drop");
            Assert.AreEqual(0.2, S11HudRules.BloodAlpha(0, 0.2), 1e-12, "a 0.2 HP heat tick is faint");
            Assert.IsFalse(S11HazardRules.Forwarded(12), "radiation is off (owner 2026-10-23)");
            Assert.IsTrue(S11GrabRules.TimedOut(0, 1001));
            Assert.IsFalse(S11GrabRules.TimedOut(0, 1000));
            Assert.AreEqual(Vec3.Zero, S11GrabRules.TargetVelocity(Vec3.Zero, new Vec3(200, 0, 0), 1.0), "a teleport is no velocity (over 150 m/s)");
            Assert.AreEqual(new Vec3(50, 0, 0), S11GrabRules.TargetVelocity(Vec3.Zero, new Vec3(1, 0, 0), 0.02), "a 50 m/s mouth is followed");
            Assert.AreEqual(new Vec3(2, 0, 0), S11GrabRules.TargetVelocity(Vec3.Zero, new Vec3(0.1, 0, 0), 0.05));
        }

        // ------------------------------------------------------------------------------------------ D/E

        [TestMethod]
        public void Hazard_Vectors()
        {
            int n = 0;
            foreach (string[] f in S11Vectors.Lines("hazard.tsv"))
            {
                switch (f[0])
                {
                    case "fwd": Assert.AreEqual(f[2] == "1", S11HazardRules.Forwarded(S11Vectors.I(f[1])), "fwd " + f[1]); break;
                    case "se": S11Vectors.Near(S11Vectors.D(f[3]), S11HazardRules.SeAmount(S11Vectors.I(f[1]), S11Vectors.D(f[2])), "se"); break;
                    case "heat": S11Vectors.Near(S11Vectors.D(f[4]), S11HazardRules.HeatPerSecond(S11Vectors.D(f[1]), S11Vectors.D(f[2]), S11Vectors.D(f[3])), "heat"); break;
                    case "rate":
                    {
                        var w = new S11RateWindow();
                        double cap = S11Vectors.D(f[2]);
                        foreach (string step in f[3].Split(';'))
                        {
                            string[] p = step.Split(',');
                            S11Vectors.Near(S11Vectors.D(p[2]), w.Grant(long.Parse(p[0]), S11Vectors.D(p[1]), cap), "grant at " + p[0]);
                        }
                        break;
                    }
                    case "heal": S11Vectors.Near(S11Vectors.D(f[4]), S11HealRules.SeHeal(S11Vectors.D(f[1]), S11Vectors.D(f[2]), S11Vectors.D(f[3])), "heal"); break;
                    case "litres": S11Vectors.Near(S11Vectors.D(f[3]), S11OxygenRules.Litres(S11Vectors.D(f[1]), S11Vectors.D(f[2])), "litres"); break;
                    case "emp": S11Vectors.Near(S11Vectors.D(f[2]), S11EmpRules.Seconds(S11Vectors.D(f[1])), "emp"); break;
                    case "blood": S11Vectors.Near(S11Vectors.D(f[3]), S11HudRules.BloodAlpha(S11Vectors.D(f[1]), S11Vectors.D(f[2])), "blood " + f[1] + " " + f[2]); break;
                    case "lowhp": S11Vectors.Near(S11Vectors.D(f[2]), S11HudRules.LowHealthFloor(S11Vectors.D(f[1]), 0.2), "lowhp " + f[1]); break;
                    case "bloodmix": S11Vectors.Near(S11Vectors.D(f[4]), S11HudRules.Combine(S11Vectors.D(f[1]), S11Vectors.D(f[2]), f[3] == "1"), "bloodmix " + string.Join(" ", f)); break;
                    case "snpaints": Assert.AreEqual(f[4] == "1", S11HudRules.SnPaints(f[1] == "1", f[2] == "1", S11Vectors.I(f[3])), "snpaints " + string.Join(" ", f)); break;
                    default: Assert.Fail("unknown hazard row " + f[0]); break;
                }
                n++;
            }
            Assert.IsTrue(n >= 40);
            Assert.AreEqual(100.0, S11OxygenRules.Next(95, 10, 100), "capped at the capacity");
        }

        [TestMethod]
        public void Records_Vectors()
        {
            int n = 0;
            foreach (string[] f in S11Vectors.Lines("records.tsv"))
            {
                var r = default(DamageRecord);
                r.unit = (uint)S11Vectors.I(f[0]);
                r.amount = (float)S11Vectors.D(f[1]);
                r.targetKind = (uint)S11Vectors.I(f[2]);
                r.snDamageType = (ushort)S11Vectors.I(f[3]);
                Vec3 l = S11Vectors.V(f[4]), w = S11Vectors.V(f[5]), d = S11Vectors.V(f[6]);
                r.localHit = new F32x3((float)l.X, (float)l.Y, (float)l.Z);
                r.snWorldHit = new F32x3((float)w.X, (float)w.Y, (float)w.Z);
                r.attackDirSn = new F32x3((float)d.X, (float)d.Y, (float)d.Z);
                r.dealerInstanceId = (uint)S11Vectors.I(f[7]);
                //: the target and root entity ids (the missile hit checks them; 1 for the other units)
                r.targetEntityId = f.Length > 10 ? ulong.Parse(f[9], S11Vectors.Inv) : 1UL;
                r.rootEntityId = f.Length > 10 ? ulong.Parse(f[10], S11Vectors.Inv) : 1UL;
                Assert.AreEqual((S11Reject)S11Vectors.I(f[8]), S11Records.CheckPayload(r), "record " + string.Join(" ", f));
                n++;
            }
            Assert.IsTrue(n >= 20);
        }
    }
}
