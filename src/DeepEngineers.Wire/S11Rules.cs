using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S11 "Creatures and hazards": the game-free rules both plugins and the Python twin (tools/s11/s11_ref.py) agree on.
 // Every number comes from S11Conventions (pinned) or from the caller (a creature's maxHealth read at run time, SE's
 // own explosion damage, the grid's own box). Replay-exact; vectors in tests/s11/vectors/ (S11RulesTests).
 // =================================================================================================================

 /// <summary>Why SE did not apply an S11 record (after S6's header and dedup checks, which keep their DamageReject).</summary>
    public enum S11Reject
    {
        None = 0,
        Unit = 1,
        Amount = 2,
        Kind = 3,
        Type = 4,
        Vector = 5,
        Entity = 6,
        NotForwarded = 7,
        TooLarge = 8,
        NotGrabbable = 9,
        NoGrab = 10,
        AlreadyHeld = 11,
        CannotDie = 12,
        NotApplied = 13,
        NoPower = 14,
        Off = 15,
        Static = 16,
        /// <summary>Pass 6: a grid remnant (format debris.grid): never grabbed or pushed.</summary>
        Debris = 17,
        /// <summary>: a MissileHit record whose missile SE no longer has (it exploded elsewhere first).</summary>
        MissileGone = 18,
        /// <summary>Review 5: the hit lies behind the missile's launch point along its flight.</summary>
        Behind = 19,
        /// <summary>Review 7: a grid with mechanical subgrids (rotors, pistons, connectors) is never held (only the root would move).</summary>
        Subgrids = 20,
    }

    // ================================================================================================= A: danger model

 /// <summary>The weapon-class rule (A): SE's own damage number × the class multiplier, never below the class floor.</summary>
    public static class S11Danger
    {
        /// <summary>The creature tier by its LiveMixin.maxHealth (read at run time, never shipped).</summary>
        public static int TierOf(double maxHealth)
        {
            if (double.IsNaN(maxHealth)) return S11Conventions.Tier.Small;
            if (maxHealth >= S11Conventions.Tier.LeviathanMinHp) return S11Conventions.Tier.Leviathan;
            if (maxHealth >= S11Conventions.Tier.LargeMinHp) return S11Conventions.Tier.Large;
            return S11Conventions.Tier.Small;
        }

        /// <summary>
 /// The SN_HP actually dealt: min(base × mult, max(0, health − floor × maxHealth)). 0 when the creature is at or
 /// below the class's floor (a handheld weapon never kills a leviathan), for a non-positive or non-finite base.
 /// </summary>
        public static double Applied(double baseHp, double health, double maxHealth, int multPermille, int floorPermille)
        {
            if (!(baseHp > 0) || double.IsInfinity(baseHp) || double.IsNaN(health) || double.IsNaN(maxHealth)) return 0.0;
            double want = baseHp * (multPermille / 1000.0);
            if (!(want > 0)) return 0.0;
            double room = health - (floorPermille / 1000.0) * maxHealth;
            if (!(room > 0)) return 0.0;
            return want < room ? want : room;
        }

        /// <summary>The class of an S7 event row's source: a hand weapon or tool (attached to a character), a small or a large grid, an explosion.</summary>
        public static int ClassOf(bool explosion, bool character, bool largeGrid)
        {
            if (explosion) return S11Conventions.WeaponClass.Explosion;
            if (character) return S11Conventions.WeaponClass.Handheld;
            return largeGrid ? S11Conventions.WeaponClass.LargeGrid : S11Conventions.WeaponClass.SmallGrid;
        }
    }

    /// s11 weapon</c>).</summary>
    public sealed class S11WeaponTable
    {
        private readonly int[] _mult = new int[5 * 4];
        private readonly int[] _floor = new int[5 * 4];
        public int Overrides { get; private set; }

        public S11WeaponTable() { Reset(); }

        public void Reset()
        {
            for (int i = 0; i < _mult.Length; i++) { _mult[i] = 1000; _floor[i] = 0; }
            foreach (S11WeaponRow r in S11Conventions.WeaponTable) { _mult[r.Class * 4 + r.Tier] = r.MultPermille; _floor[r.Class * 4 + r.Tier] = r.FloorPermille; }
            Overrides = 0;
        }

        private static bool Valid(int cls, int tier) { return cls >= 1 && cls <= 4 && tier >= 1 && tier <= 3; }

        public int Mult(int cls, int tier) { return Valid(cls, tier) ? _mult[cls * 4 + tier] : 1000; }
        public int Floor(int cls, int tier) { return Valid(cls, tier) ? _floor[cls * 4 + tier] : 0; }

        /// <summary>Sets one cell (mult 0..100,000 ‰, floor 0..1,000 ‰). False for an unknown class/tier or a value out of range.</summary>
        public bool Set(int cls, int tier, int? multPermille, int? floorPermille, out string error)
        {
            error = null;
            if (!Valid(cls, tier)) { error = "class Handheld|SmallGrid|LargeGrid|Explosion and tier Small|Large|Leviathan"; return false; }
            if (multPermille.HasValue && (multPermille.Value < 0 || multPermille.Value > 100000)) { error = "mult 0..100 (×)"; return false; }
            if (floorPermille.HasValue && (floorPermille.Value < 0 || floorPermille.Value > 1000)) { error = "floor 0..1 (fraction of maxHealth)"; return false; }
            if (multPermille.HasValue) _mult[cls * 4 + tier] = multPermille.Value;
            if (floorPermille.HasValue) _floor[cls * 4 + tier] = floorPermille.Value;
            Overrides++;
            return true;
        }

        public double Applied(int cls, double baseHp, double health, double maxHealth)
        {
            int tier = S11Danger.TierOf(maxHealth);
            return S11Danger.Applied(baseHp, health, maxHealth, Mult(cls, tier), Floor(cls, tier));
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            for (int c = 1; c <= 4; c++)
            {
                if (c > 1) sb.Append("; ");
                sb.Append(S11Conventions.WeaponClass.Name(c)).Append(':');
                for (int t = 1; t <= 3; t++)
                    sb.Append(' ').Append(S11Conventions.Tier.Name(t)).Append(" x").Append((Mult(c, t) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture))
                      .Append(" floor ").Append((Floor(c, t) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    // ================================================================================================= A: explosions

 /// <summary>One explosion or cluster: centre, radius (m), yield (SE damage), time and how many explosions it sums.</summary>
    public struct S11Blast
    {
        public double X, Y, Z, R, Yield, SumR3;
        public int Count;
        public long TMs;

        public S11Blast(double x, double y, double z, double r, double yield, long tMs)
        {
            X = x; Y = y; Z = z; R = r; Yield = yield; TMs = tMs; Count = 1;
            SumR3 = r > 0 ? r * r * r : 0.0;
        }

        public Vec3 Centre { get { return new Vec3(X, Y, Z); } }
    }

    /// </summary>
    public static class S11BlastRules
    {
        /// <summary>The Explosion row's <c>extra</c> for a yield: round(yield / 10) clamped 1..65535; 0 for none.</summary>
        public static ushort YieldCode(double yield)
        {
            if (!(yield > 0) || double.IsInfinity(yield)) return 0;
            double v = Math.Floor(yield / S11Conventions.Blast.YieldUnitSe + 0.5);
            if (v < 1) v = 1;
            if (v > 65535) v = 65535;
            return (ushort)v;
        }

        /// <summary>The yield of a row: its <c>extra</c> × 10 when S11's SE wrote it, else S7's intensity × 1,000 (intensity = min(1, damage / 1,000)).</summary>
        public static double YieldOf(ushort extra, float intensity)
        {
            if (extra != 0) return extra * (double)S11Conventions.Blast.YieldUnitSe;
            if (!(intensity > 0) || float.IsInfinity(intensity)) return 0.0;
            return Math.Min(1.0, intensity) * 1000.0;
        }

        public static double CubeRoot(double x) { return x > 0 ? Math.Pow(x, 1.0 / 3.0) : 0.0; }

        /// <summary>An explosion joins a group when its centre lies within max(R, r) + JoinPadM of the group's centre.</summary>
        public static bool Joins(double gx, double gy, double gz, double gr, double x, double y, double z, double r)
        {
            double dx = x - gx, dy = y - gy, dz = z - gz;
            double lim = Math.Max(gr, r) + S11Conventions.Blast.JoinPadM;
            return dx * dx + dy * dy + dz * dz <= lim * lim;
        }

        /// <summary>Adds an explosion (or a group) into a group: yields add, Σr³ adds (R = cbrt), the centre is the yield-weighted mean.</summary>
        public static void Merge(ref S11Blast g, S11Blast e)
        {
            double y0 = g.Yield, y1 = e.Yield, w = y0 + y1;
            if (w > 0)
            {
                g.X = (g.X * y0 + e.X * y1) / w;
                g.Y = (g.Y * y0 + e.Y * y1) / w;
                g.Z = (g.Z * y0 + e.Z * y1) / w;
            }
            else
            {
                double n0 = g.Count, n1 = e.Count, n = n0 + n1;
                g.X = (g.X * n0 + e.X * n1) / n;
                g.Y = (g.Y * n0 + e.Y * n1) / n;
                g.Z = (g.Z * n0 + e.Z * n1) / n;
            }
            g.Yield = w;
            g.SumR3 += e.SumR3;
            g.R = CubeRoot(g.SumR3);
            g.Count += e.Count;
            if (e.TMs < g.TMs) g.TMs = e.TMs;
        }

        /// <summary>
 /// One batch (one SE tick) into groups, in input order: each explosion joins the first group it joins; else it opens
 /// a group while fewer than <paramref name="maxGroups"/> exist, else joins the nearest group. Nothing is dropped:
 /// the groups' yields and counts sum to the batch's.
 /// </summary>
        public static List<S11Blast> Group(IList<S11Blast> batch, int maxGroups)
        {
            var groups = new List<S11Blast>();
            if (batch == null) return groups;
            if (maxGroups < 1) maxGroups = 1;
            for (int i = 0; i < batch.Count; i++)
            {
                S11Blast e = batch[i];
                int at = -1;
                for (int k = 0; k < groups.Count; k++)
                {
                    S11Blast g = groups[k];
                    if (Joins(g.X, g.Y, g.Z, g.R, e.X, e.Y, e.Z, e.R)) { at = k; break; }
                }
                if (at < 0 && groups.Count < maxGroups) { groups.Add(e); continue; }
                if (at < 0)
                {
                    double best = double.PositiveInfinity;
                    for (int k = 0; k < groups.Count; k++)
                    {
                        S11Blast g = groups[k];
                        double dx = e.X - g.X, dy = e.Y - g.Y, dz = e.Z - g.Z, d = dx * dx + dy * dy + dz * dz;
                        if (d < best) { best = d; at = k; }
                    }
                }
                S11Blast t = groups[at];
                Merge(ref t, e);
                groups[at] = t;
            }
            return groups;
        }

        /// <summary>
 /// The SN_HP a creature at <paramref name="distance"/> from the centre takes: yield × mult × max(0, (R − d) / R) with
 /// R capped at DamageRadiusCapM (Subnautica's own DamageSystem.RadiusDamage falloff, measured to the LiveMixin's transform).
 /// </summary>
        public static double DamageHp(double yield, double radius, double distance, int multPermille)
        {
            if (!(yield > 0) || !(radius > 0) || double.IsNaN(distance) || double.IsInfinity(yield)) return 0.0;
            double r = Math.Min(radius, S11Conventions.Blast.DamageRadiusCapM);
            double d = Math.Max(0.0, distance);
            double f = (r - d) / r;
            if (!(f > 0)) return 0.0;
            return yield * (multPermille / 1000.0) * f;
        }

        /// <summary>The subtype bits of an Explosion row's weapon class (<c>format event.class</c>): class &lt;&lt; 8 for 1..4, else 0.</summary>
        public static ushort ClassBits(int weaponClass)
        {
            if (weaponClass < S11Conventions.WeaponClass.Handheld || weaponClass > S11Conventions.WeaponClass.Explosion) return 0;
            return (ushort)(weaponClass << S11Conventions.Blast.ClassShift);
        }

        /// <summary>The weapon class of an Explosion row: subtype bits 8-11; 0 (an older SE, or none) = Explosion.</summary>
        public static int ClassOfSubtype(ushort subtype)
        {
            int c = (subtype >> S11Conventions.Blast.ClassShift) & 0xF;
            return c >= S11Conventions.WeaponClass.Handheld && c <= S11Conventions.WeaponClass.Explosion ? c : S11Conventions.WeaponClass.Explosion;
        }

        /// <summary>The class of an explosion's origin: a character Handheld, a small grid SmallGrid, anything else Explosion.</summary>
        public static int ClassOfOrigin(bool character, bool smallGrid)
        {
            if (character) return S11Conventions.WeaponClass.Handheld;
            if (smallGrid) return S11Conventions.WeaponClass.SmallGrid;
            return S11Conventions.WeaponClass.Explosion;
        }

        /// <summary>The escalated effect's radius: the cluster's cbrt(Σr³), capped at VisualRadiusCapM.</summary>
        public static double VisualRadius(double sumR3)
        {
            return Math.Min(CubeRoot(sumR3), S11Conventions.Blast.VisualRadiusCapM);
        }
    }

    /// <summary>
 /// Subnautica's picture of a chain of explosions (stacked warheads chain over up to 500 ms: MyWarhead.OnDestroy →
 /// ExplodeDelayed(500)): the first row of a chain plays at once; rows that join an open chain within ChainWindowMs
 /// are absorbed (not played); when the window closes, a chain that absorbed anything plays ONE escalated effect of
 /// the whole sum (radius cbrt(Σr³), capped). Never dropped by a cap: the escalated row is one row.
 /// </summary>
    public sealed class S11BlastChain
    {
        private sealed class Chain { public S11Blast Sum; public long T0; public int Absorbed; }

        private readonly List<Chain> _open = new List<Chain>();
        public long Played, Absorbed, Escalated;
        public int Window = S11Conventions.Blast.ChainWindowMs;

        public int OpenCount { get { return _open.Count; } }

        /// <summary>True: play this row now (it opened a chain). False: it was absorbed into an open chain.</summary>
        public bool Admit(S11Blast b, long nowMs)
        {
            for (int i = 0; i < _open.Count; i++)
            {
                Chain c = _open[i];
                if (nowMs >= c.T0 + Window) continue;
                if (!S11BlastRules.Joins(c.Sum.X, c.Sum.Y, c.Sum.Z, c.Sum.R, b.X, b.Y, b.Z, b.R)) continue;
                S11BlastRules.Merge(ref c.Sum, b);
                c.Absorbed++;
                Absorbed++;
                return false;
            }
            _open.Add(new Chain { Sum = b, T0 = nowMs });
            Played++;
            return true;
        }

        /// <summary>Closes the chains whose window ended; each that absorbed rows adds its escalated sum (R = visual radius) to <paramref name="into"/>.</summary>
        public int Close(long nowMs, List<S11Blast> into)
        {
            int n = 0;
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                Chain c = _open[i];
                if (nowMs < c.T0 + Window) continue;
                _open.RemoveAt(i);
                if (c.Absorbed < 1) continue;
                S11Blast s = c.Sum;
                s.R = S11BlastRules.VisualRadius(s.SumR3);
                s.TMs = nowMs;
                if (into != null) into.Add(s);
                Escalated++;
                n++;
            }
            return n;
        }

        public void Clear() { _open.Clear(); }
    }

    // ================================================================================================= C: grabs and pushes

    public static class S11GrabRules
    {
        /// <summary>The grid's own box (size along its axes, metres) fits the creature's size class: volume and longest edge.</summary>
        public static bool SizeFits(S11GrabRow row, double sx, double sy, double sz)
        {
            if (!(sx >= 0) || !(sy >= 0) || !(sz >= 0) || double.IsInfinity(sx) || double.IsInfinity(sy) || double.IsInfinity(sz)) return false;
            if (row.MaxVolumeM3 <= 0) return false;
            double vol = sx * sy * sz;
            double edge = Math.Max(sx, Math.Max(sy, sz));
            return vol <= row.MaxVolumeM3 + 1e-9 && edge <= row.MaxEdgeM + 1e-9;
        }

        /// <summary>The box around every skin box of a grid (its own frame; rotated boxes by their projected half extents). False when empty.</summary>
        public static bool SkinExtents(IEnumerable<SkinSectionData> sections, out Vec3 min, out Vec3 max)
        {
            double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
            bool any = false;
            if (sections != null)
                foreach (SkinSectionData s in sections)
                {
                    if (s == null) continue;
                    foreach (SkinBoxData b in s.Boxes)
                    {
                        double ex = Math.Abs(b.E0.X) * b.Half.X + Math.Abs(b.E1.X) * b.Half.Y + Math.Abs(b.E2.X) * b.Half.Z;
                        double ey = Math.Abs(b.E0.Y) * b.Half.X + Math.Abs(b.E1.Y) * b.Half.Y + Math.Abs(b.E2.Y) * b.Half.Z;
                        double ez = Math.Abs(b.E0.Z) * b.Half.X + Math.Abs(b.E1.Z) * b.Half.Y + Math.Abs(b.E2.Z) * b.Half.Z;
                        x0 = Math.Min(x0, b.Centre.X - ex); x1 = Math.Max(x1, b.Centre.X + ex);
                        y0 = Math.Min(y0, b.Centre.Y - ey); y1 = Math.Max(y1, b.Centre.Y + ey);
                        z0 = Math.Min(z0, b.Centre.Z - ez); z1 = Math.Max(z1, b.Centre.Z + ez);
                        any = true;
                    }
                }
            min = any ? new Vec3(x0, y0, z0) : Vec3.Zero;
            max = any ? new Vec3(x1, y1, z1) : Vec3.Zero;
            return any;
        }

        /// <summary>The planned hold, seconds: (HoldMinMs + rand01 × HoldRandMs) / 1000.</summary>
        public static double HoldSeconds(S11GrabRow row, double rand01)
        {
            if (double.IsNaN(rand01)) rand01 = 0;
            rand01 = Math.Max(0.0, Math.Min(1.0, rand01));
            return (row.HoldMinMs + rand01 * row.HoldRandMs) / 1000.0;
        }

        /// <summary>The hold damage per tick in SN_HP: the creature's own dps × interval × DpsPermille.</summary>
        public static double HoldDamage(S11GrabRow row, double creatureDps)
        {
            if (!(creatureDps > 0) || double.IsInfinity(creatureDps)) return 0.0;
            return creatureDps * (row.IntervalMs / 1000.0) * (row.DpsPermille / 1000.0);
        }

        /// <summary>True when a Subnautica DamageType (by value) on the creature makes it let go (the row's ReleaseOn names).</summary>
        public static bool ReleasesOn(S11GrabRow row, int snType)
        {
            if (string.IsNullOrEmpty(row.ReleaseOn) || row.ReleaseOn == "none") return false;
            foreach (string n in row.ReleaseOn.Split(','))
            {
                int v = SnDamageTypeValue(n.Trim());
                if (v >= 0 && v == snType) return true;
            }
            return false;
        }

        /// <summary>Subnautica's DamageType values (DamageType.cs: Normal 0 … Smoke 16); −1 for an unknown name.</summary>
        public static int SnDamageTypeValue(string name)
        {
            switch (name)
            {
                case "Normal": return 0;
                case "Heat": return 1;
                case "Pressure": return 2;
                case "Puncture": return 3;
                case "Collide": return 4;
                case "Poison": return 5;
                case "Acid": return 6;
                case "Explosive": return 7;
                case "Electrical": return 8;
                case "Drill": return 9;
                case "Cold": return 10;
                case "Starve": return 11;
                case "Radiation": return 12;
                case "LaserCutter": return 13;
                case "Fire": return 14;
                case "Undefined": return 15;
                case "Smoke": return 16;
                default: return -1;
            }
        }

        /// <summary>
 /// The hold acceleration toward the target: w²(xt − x) + 2ζw(vt − v), its length capped at aMax (m/s²). The SE side
 /// multiplies by the grid's mass and subtracts gravity (CONTRACT §5.4 <c>format hold.force</c>).
 /// </summary>
        public static Vec3 HoldAccel(Vec3 xt, Vec3 x, Vec3 vt, Vec3 v, double omega, double zeta, double aMax)
        {
            Vec3 a = (xt - x) * (omega * omega) + (vt - v) * (2.0 * zeta * omega);
            double l = a.Length;
            if (!(l > aMax) || !(aMax > 0)) return double.IsNaN(l) ? Vec3.Zero : a;
            return a * (aMax / l);
        }

        /// <summary>
 /// The hold's acceleration cap for a creature of size factor <paramref name="scale"/> (<c>format hold.force</c>):
 /// S11b 20261023-220012, the x3 reaper's own grab of a 12.5 m wall: the cap of 150 m/s2 was reached on every hold
 /// tick while its attach point (3 x farther from the head's pivot) moved metres a second.
 /// </summary>
        public static double MaxHoldAccel(double scale)
        {
            double f = double.IsNaN(scale) ? 1.0 : Math.Max(1.0, scale);
            return S11Conventions.Hold.MaxAccelCmS2 / 100.0 * f;
        }

        public static Vec3 HoldAccel(Vec3 xt, Vec3 x, Vec3 vt, Vec3 v)
        {
            return HoldAccel(xt, x, vt, v, S11Conventions.Hold.OmegaMilli / 1000.0, S11Conventions.Hold.ZetaPermille / 1000.0, S11Conventions.Hold.MaxAccelCmS2 / 100.0);
        }

        /// <summary>
 /// The angular acceleration toward the target orientation: w²·(axis·angle of qt·q⁻¹, shortest way) − 2ζw·ω, capped
 /// at aMax (rad/s²).
 /// </summary>
        public static Vec3 AngAccel(Quat qt, Quat q, Vec3 angVel, double omega, double zeta, double aMax)
        {
            Quat e = (qt * q.Conjugate()).Normalized().WithNonNegativeW();
            double s = Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
            Vec3 rot = Vec3.Zero;
            if (s > 1e-12)
            {
                double angle = 2.0 * Math.Atan2(s, e.W);
                rot = new Vec3(e.X / s * angle, e.Y / s * angle, e.Z / s * angle);
            }
            Vec3 a = rot * (omega * omega) - angVel * (2.0 * zeta * omega);
            double l = a.Length;
            if (!(l > aMax) || !(aMax > 0)) return double.IsNaN(l) ? Vec3.Zero : a;
            return a * (aMax / l);
        }

        /// <summary>The rotation vector (axis × angle, the shortest way) that turns <paramref name="q"/> into <paramref name="qt"/>.</summary>
        public static Vec3 RotationVector(Quat qt, Quat q)
        {
            Quat e = (qt * q.Conjugate()).Normalized().WithNonNegativeW();
            double s = Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z);
            if (!(s > 1e-12)) return Vec3.Zero;
            double angle = 2.0 * Math.Atan2(s, e.W);
            return new Vec3(e.X / s * angle, e.Y / s * angle, e.Z / s * angle);
        }

        /// <summary><c>format hold.kinematic</c>: the attach point now (the last record led by its velocity, at most LeadMaxMs).</summary>
        public static Vec3 TargetNow(Vec3 target, Vec3 targetVel, double ageMs)
        {
            double lead = double.IsNaN(ageMs) ? 0.0 : Math.Max(0.0, Math.Min(S11Conventions.Hold.LeadMaxMs, ageMs)) / 1000.0;
            return target + targetVel * lead;
        }

        /// <summary><c>format hold.kinematic</c>: the anchor's velocity (the mouth's plus the error closed at ServoKp per second).</summary>
        public static Vec3 ServoVelocity(Vec3 xt, Vec3 x, Vec3 vt)
        {
            return vt + (xt - x) * (S11Conventions.Hold.ServoKpMilli / 1000.0);
        }

        /// <summary><c>format hold.kinematic</c>: the angular velocity toward the target orientation, capped.</summary>
        public static Vec3 ServoAngular(Quat qt, Quat q)
        {
            Vec3 w = RotationVector(qt, q) * (S11Conventions.Hold.ServoKRotMilli / 1000.0);
            double l = w.Length, cap = S11Conventions.Hold.MaxAngVelMilli / 1000.0;
            if (double.IsNaN(l)) return Vec3.Zero;
            return l > cap ? w * (cap / l) : w;
        }

        /// <summary><c>format hold.kinematic</c>: the grid's matrix is set when the anchor is farther than SnapCm from the mouth.</summary>
        public static bool Snaps(double errorM)
        {
            return errorM > S11Conventions.Hold.SnapCm / 100.0;
        }

        /// <summary>The share of the Water Mod's buoyancy the grab must cancel itself: 1 − (the S7p depth hold cancels it ? its factor: 0).</summary>
        public static double BuoyancyShare(bool holdCancels, double holdFactor)
        {
            if (!holdCancels || double.IsNaN(holdFactor)) return 1.0;
            return 1.0 - Math.Max(0.0, Math.Min(1.0, holdFactor));
        }

        /// <summary>The angle between two orientations, degrees (0..180): 2·acos(|q·qt|).</summary>
        public static double AngleDeg(Quat a, Quat b)
        {
            double la = a.Length, lb = b.Length;
            if (!(la > 0) || !(lb > 0)) return double.NaN;
            double d = Math.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W) / (la * lb);
            if (d > 1) d = 1;
            return 2.0 * Math.Acos(d) * 180.0 / Math.PI;
        }

        public static bool TimedOut(long lastHoldMs, long nowMs)
        {
            return nowMs - lastHoldMs > S11Conventions.Hold.TimeoutMs;
        }

        /// <summary>
 /// <c>format hold.kinematic</c>: the share of the way from the anchor's start position to the
 /// attach point, min(1, held / blendMs) (1 for blendMs ≤ 0: no blend); Subnautica's own lerp of a held vehicle.
 /// </summary>
        public static double BlendFactor(double blendMs, double heldMs)
        {
            if (!(blendMs > 0)) return 1.0;
            if (double.IsNaN(heldMs) || heldMs <= 0) return 0.0;
            return Math.Min(1.0, heldMs / blendMs);
        }

        /// <summary><c>format hold.kinematic</c>: the blended target xb = a + (xt − a)·f.</summary>
        public static Vec3 BlendTarget(Vec3 start, Vec3 xt, double f)
        {
            if (double.IsNaN(f)) f = 1.0;
            f = Math.Max(0.0, Math.Min(1.0, f));
            return start + (xt - start) * f;
        }

        /// <summary><c>format hold.kinematic</c>: the blended target's rate vb = f·vt + (f &lt; 1 ? (xt − a) / blend: 0).</summary>
        public static Vec3 BlendVelocity(Vec3 start, Vec3 xt, Vec3 vt, double f, double blendMs)
        {
            if (double.IsNaN(f)) f = 1.0;
            f = Math.Max(0.0, Math.Min(1.0, f));
            Vec3 v = vt * f;
            if (f < 1.0 && blendMs > 0) v = v + (xt - start) * (1000.0 / blendMs);
            return v;
        }

        /// <summary>
 /// Review 1: the grid's velocity at a release: at most ReleaseSpeedCmS, and at a surface release without its
 /// component along <paramref name="up"/> (unit, away from the planet) when that points up.
 /// </summary>
        public static Vec3 ReleaseVelocity(Vec3 v, Vec3 up, bool surface)
        {
            if (double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z)) return Vec3.Zero;
            if (surface)
            {
                double u = v.X * up.X + v.Y * up.Y + v.Z * up.Z;
                if (u > 0) v = v - up * u;
            }
            double l = v.Length, cap = S11Conventions.Hold.ReleaseSpeedCmS / 100.0;
            return l > cap ? v * (cap / l) : v;
        }

        /// <summary>The target velocity from two consecutive attach points (m/s); zero for dt ≤ 0 or a jump over 50 m/s (a teleport).</summary>
        public static Vec3 TargetVelocity(Vec3 prev, Vec3 cur, double dtS)
        {
            if (!(dtS > 1e-4)) return Vec3.Zero;
            Vec3 v = (cur - prev) / dtS;
            return v.Length > S11Conventions.Hold.MaxMouthSpeedCmS / 100.0 ? Vec3.Zero : v;
        }
    }

    public static class S11PushRules
    {
        /// <summary>The creature's effective mass when its size is scaled: rigidbody mass × factor³ (a bigger body; the rigidbody is left alone).</summary>
        public static double EffectiveMass(double massKg, double scale)
        {
            if (!(massKg > 0) || !(scale > 0) || double.IsInfinity(scale)) return massKg > 0 ? massKg : 0.0;
            return massKg * scale * scale * scale;
        }

        /// <summary>The bite's impulse, N·s: creature mass × max(0, closing speed) × factor, capped at Limits.MaxPushNs.</summary>
        public static double Impulse(S11PushRow row, double creatureMassKg, double closingSpeedMs)
        {
            if (!(creatureMassKg > 0) || double.IsNaN(closingSpeedMs) || double.IsInfinity(creatureMassKg)) return 0.0;
            double j = creatureMassKg * Math.Max(0.0, closingSpeedMs) * (row.FactorPermille / 1000.0);
            return Math.Min(j, S11Conventions.Limits.MaxPushNs);
        }

        /// <summary>The grid's velocity change, m/s: min(impulse / mass, MaxDvCmS / 100).</summary>
        public static double Dv(S11PushRow row, double impulseNs, double gridMassKg)
        {
            if (!(impulseNs > 0) || !(gridMassKg > 0)) return 0.0;
            return Math.Min(impulseNs / gridMassKg, row.MaxDvCmS / 100.0);
        }
    }

    /// s11 scale</c>.</summary>
    public sealed class S11ScaleTable
    {
        private readonly Dictionary<int, int> _factor = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _bite = new Dictionary<int, int>();
        public int Overrides { get; private set; }
        public int Serial { get; private set; }

        public S11ScaleTable() { Reset(); }

        public void Reset()
        {
            _factor.Clear();
            _bite.Clear();
            foreach (S11ScaleRow r in S11Conventions.ScaleTable) { _factor[r.TechType] = r.FactorPermille; _bite[r.TechType] = r.BiteMultPermille; }
            Overrides = 0;
            Serial++;
        }

        /// <summary>The factor of a TechType (1.0 when not in the table: never scaled).</summary>
        public double Factor(int techType) { int v; return _factor.TryGetValue(techType, out v) ? v / 1000.0 : 1.0; }
        public double BiteMult(int techType) { int v; return _bite.TryGetValue(techType, out v) ? v / 1000.0 : 1.0; }
        public bool Has(int techType) { return _factor.ContainsKey(techType); }

        /// <summary>Sets a creature's factor (0.25..10) and/or bite multiplier (0..20); only creatures of the table.</summary>
        public bool Set(int techType, double? factor, double? bite, out string error)
        {
            error = null;
            if (!_factor.ContainsKey(techType)) { error = "scale: a creature of the table (ReaperLeviathan, GhostLeviathan, GhostLeviathanJuvenile, SeaDragon)"; return false; }
            if (factor.HasValue && (double.IsNaN(factor.Value) || factor.Value < 0.25 || factor.Value > 10)) { error = "scale factor 0.25..10"; return false; }
            if (bite.HasValue && (double.IsNaN(bite.Value) || bite.Value < 0 || bite.Value > 50)) { error = "bite 0..50"; return false; }
            if (factor.HasValue) _factor[techType] = (int)Math.Round(factor.Value * 1000.0, MidpointRounding.AwayFromZero);
            if (bite.HasValue) _bite[techType] = (int)Math.Round(bite.Value * 1000.0, MidpointRounding.AwayFromZero);
            Overrides++;
            Serial++;
            return true;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (S11ScaleRow r in S11Conventions.ScaleTable)
                sb.Append(sb.Length > 0 ? "; " : "").Append(r.Creature).Append(" x").Append(Factor(r.TechType).ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" bite x").Append(BiteMult(r.TechType).ToString("0.###", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    // ================================================================================================= D/E: hazards, heal, air

 /// <summary>A one-second window of SE amount granted (the per-second caps of the hazard and heal tables).</summary>
    public struct S11RateWindow
    {
        public long StartMs;
        public double Used;
        public bool Started;

        /// <summary>Grants min(want, cap − used in the current 1,000 ms window); a new window starts when the old one ended.</summary>
        public double Grant(long nowMs, double want, double capPerS)
        {
            if (!(want > 0)) return 0.0;
            if (!Started || nowMs - StartMs >= 1000 || nowMs < StartMs) { StartMs = nowMs; Used = 0; Started = true; }
            double room = capPerS - Used;
            if (!(room > 0)) return 0.0;
            double g = want < room ? want : room;
            Used += g;
            return g;
        }
    }

    public static class S11HazardRules
    {
        public static bool Forwarded(int snType)
        {
            S11HazardRow r;
            return S11Conventions.TryHazardRow(snType, out r) && r.KPermille > 0;
        }

        /// <summary>The SE health points of an SN_HP amount of this type (0 when the type is not in the table).</summary>
        public static double SeAmount(int snType, double snHp)
        {
            S11HazardRow r;
            if (!S11Conventions.TryHazardRow(snType, out r) || !(snHp > 0)) return 0.0;
            return snHp * (r.KPermille / 1000.0);
        }

        /// <summary>TemperatureDamage.UpdateDamage: temperature / minDamageTemperature × baseDamagePerSecond, once per second, at or above the minimum.</summary>
        public static double HeatPerSecond(double temperature, double minDamageTemperature, double baseDamagePerSecond)
        {
            if (!(minDamageTemperature > 0) || double.IsNaN(temperature) || !(temperature >= minDamageTemperature)) return 0.0;
            return temperature / minDamageTemperature * baseDamagePerSecond;
        }
    }

    /// <summary>Pass 6: debris (<c>format debris.grid</c>; S11Conventions.Debris).</summary>
    public static class S11DebrisRules
    {
        /// <summary>A remnant: fewer than MinBlocks blocks (NaN = unknown: the volume alone) or a box under MinVolumeM3.</summary>
        public static bool IsDebris(double blocks, double volumeM3)
        {
            if (double.IsNaN(volumeM3) || volumeM3 < S11Conventions.Debris.MinVolumeM3) return true;
            if (!double.IsNaN(blocks) && blocks + 1e-6 < S11Conventions.Debris.MinBlocks) return true;
            return false;
        }

        /// <summary>Subnautica's estimate: the skin's occupied volume / one block (edge 0.5 m small, 2.5 m large); NaN without an edge.</summary>
        public static double EstimateBlocks(double occupiedM3, double blockEdgeM)
        {
            if (!(blockEdgeM > 0) || double.IsNaN(occupiedM3)) return double.NaN;
            return Math.Max(0.0, occupiedM3) / (blockEdgeM * blockEdgeM * blockEdgeM);
        }
    }

    /// <summary>Pass 5: SE's blood overlay (<c>format hud.blood</c>; S11Conventions.Hud).</summary>
    public static class S11HudRules
    {
        /// <summary>The overlay alpha <paramref name="dtMs"/> after a health drop of <paramref name="dropHp"/> (SE HP); 0 without one.</summary>
        public static double BloodAlpha(double dtMs, double dropHp)
        {
            if (!(dropHp > 0) || double.IsNaN(dtMs) || dtMs < 0 || dtMs >= S11Conventions.Hud.HitFadeMs) return 0.0;
            double a = Math.Max(S11Conventions.Hud.MinAlphaPermille / 1000.0, Math.Min(S11Conventions.Hud.MaxAlphaPermille / 1000.0, dropHp / S11Conventions.Hud.FullHitHp));
            return a * (1.0 - dtMs / S11Conventions.Hud.HitFadeMs);
        }

        /// <summary>

 /// Subnautica paints its own damage FX) and the game's own low-health floor, whichever is higher; the floor always
 /// comes through, under Subnautica's picture too.
 /// </summary>
        public static double Combine(double pulse, double lowHealthFloor, bool snPaints)
        {
            double p = snPaints || double.IsNaN(pulse) ? 0.0 : Math.Max(0.0, pulse);
            double f = double.IsNaN(lowHealthFloor) ? 0.0 : Math.Max(0.0, lowHealthFloor);
            return Math.Max(p, f);
        }

        /// <summary>SE's own low-health floor: (critical - ratio) / critical + 0.3 at or under the critical ratio, else 0.</summary>
        public static double LowHealthFloor(double healthRatio, double critical)
        {
            if (double.IsNaN(healthRatio) || !(critical > 0) || healthRatio > critical) return 0.0;
            return Math.Max(0.0, Math.Min(1.0, critical - healthRatio)) / critical + 0.3;
        }

        /// <summary>SE draws no overlay of its own: Subnautica's picture is on and a fresh C7 S11 block says its damage FX can play.</summary>
        public static bool SnPaints(bool snPicture, bool c7Fresh, int c7Flags)
        {
            return snPicture && c7Fresh && (c7Flags & S11Conventions.C7Flags.DamageFxOk) != 0;
        }
    }

    /// <summary>Pass 5: the C7 S11 block codec (SnStatus 0xC0-0xC3; <c>format c7.s11</c>).</summary>
    public static unsafe class S11Wire
    {
        public static void WriteStatus(ref SnStatus st, byte flags)
        {
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S11Conventions.C7.Offset;
                b[0] = (byte)S11Conventions.C7.Version;
                b[1] = flags;
                b[2] = 0;
                b[3] = 0;
            }
        }

        /// <summary>False when the record carries no S11 block (version 0 or unknown).</summary>
        public static bool TryReadStatus(ref SnStatus st, out byte flags)
        {
            flags = 0;
            fixed (uint* p = &st.seq)
            {
                byte* b = (byte*)p + S11Conventions.C7.Offset;
                if (b[0] != S11Conventions.C7.Version) return false;
                flags = b[1];
            }
            return true;
        }
    }

    public static class S11HealRules
    {
        /// <summary>SE health restored: min(SN_HP × k, max − current), never negative.</summary>
        public static double SeHeal(double snHp, double current, double max)
        {
            if (!(snHp > 0) || double.IsNaN(current) || double.IsNaN(max)) return 0.0;
            double want = snHp * (S11Conventions.Heal.KPermille / 1000.0);
            double room = max - current;
            if (!(room > 0)) return 0.0;
            return want < room ? want : room;
        }
    }

    public static class S11OxygenRules
    {
        /// <summary>SE suit litres for Subnautica oxygen seconds: seconds / TankS × capacity (the S2b tank mapping, SwimO2Rule).</summary>
        public static double Litres(double seconds, double capacityL)
        {
            if (!(seconds > 0) || !(capacityL > 0)) return 0.0;
            return seconds / S11Conventions.Oxygen.TankS * capacityL;
        }

        /// <summary>The suit's next amount: min(capacity, current + litres).</summary>
        public static double Next(double currentL, double litres, double capacityL)
        {
            if (!(litres > 0) || double.IsNaN(currentL)) return currentL;
            return Math.Min(capacityL, currentL + litres);
        }
    }

    public static class S11EmpRules
    {
        /// <summary>The SE power loss, seconds: EMPBlast.disableElectronicsTime × SecondsPermille, clamped MinS..MaxS.</summary>
        public static double Seconds(double disableElectronicsTime)
        {
            if (!(disableElectronicsTime > 0) || double.IsInfinity(disableElectronicsTime)) return S11Conventions.Emp.MinS;
            double s = disableElectronicsTime * (S11Conventions.Emp.SecondsPermille / 1000.0);
            return Math.Max(S11Conventions.Emp.MinS, Math.Min(S11Conventions.Emp.MaxS, s));
        }
    }

    // =================================================================================================: surface, rage, missiles

 /// <summary><c>format surface.release</c>: creatures never leave the water with a grid.</summary>
    public static class S11SurfaceRules
    {
        /// <summary>
 /// Review 4: true when a body's top at <paramref name="topDepthM"/> under the surface (negative above it) is within
 /// MarginCm: the hold lets go (the creature's bounds top or the held grid's), a following creature's rise is stopped.
 /// </summary>
        public static bool TopReleases(double topDepthM)
        {
            if (double.IsNaN(topDepthM)) return false;
            return topDepthM < S11Conventions.Surface.MarginCm / 100.0;
        }

        /// <summary>The steering target's height for a creature that holds, flees or hunts a grid: min(y, −(half height + MarginCm / 100)).</summary>
        public static double FollowY(double y, double halfHeightM)
        {
            double h = double.IsNaN(halfHeightM) || halfHeightM < 0 ? 0.0 : halfHeightM;
            double cap = -(h + S11Conventions.Surface.MarginCm / 100.0);
            if (double.IsNaN(y)) return cap;
            return y < cap ? y : cap;
        }

        /// <summary>SE: the Water Mod's GetDepth (negative below the surface) as a depth (positive below); NaN for none.</summary>
        public static double DepthOfWaterModValue(double getDepth)
        {
            return double.IsNaN(getDepth) || double.IsInfinity(getDepth) ? double.NaN : -getDepth;
        }
    }

    public enum S11RageOutcome { None = 0, Rage = 1, Release = 2 }

    /// <summary>
 /// <c>format rage.hold</c>: one hold's book of the damage its creature takes from SE weapons. Each outcome at most once per
 /// hold: the first hit below the threshold → Rage, the hit that reaches ReleasePermille × maxHealth → Release (after which
 /// nothing more: the hold is over).
 /// </summary>
    public sealed class S11HoldRage
    {
        public double Taken;
        public bool Raged, Released;
        public int Hits;

        public S11RageOutcome Hit(double appliedHp, double maxHealth, S11RageRow row)
        {
            if (Released || !(appliedHp > 0) || double.IsInfinity(appliedHp)) return S11RageOutcome.None;
            Hits++;
            Taken += appliedHp;
            if (maxHealth > 0 && row.ReleasePermille > 0 && Taken >= row.ReleasePermille / 1000.0 * maxHealth - 1e-9)
            {
                Released = true;
                return S11RageOutcome.Release;
            }
            if (!Raged)
            {
                Raged = true;
                return S11RageOutcome.Rage;
            }
            return S11RageOutcome.None;
        }

        /// <summary>The hold damage multiplier now: AngerPermille / 1000 after the rage, else 1.</summary>
        public double DamageMult(S11RageRow row) { return Raged && row.AngerPermille > 0 ? row.AngerPermille / 1000.0 : 1.0; }

        /// <summary>The release threshold in SN_HP.</summary>
        public static double Threshold(double maxHealth, S11RageRow row) { return maxHealth > 0 ? row.ReleasePermille / 1000.0 * maxHealth : double.NaN; }
    }

    /// <summary>The live rage table: the pinned defaults plus <c>de.s11 {action: rage, creature, release, anger, extend, flee}</c>.</summary>
    public sealed class S11RageTable
    {
        private readonly Dictionary<int, S11RageRow> _rows = new Dictionary<int, S11RageRow>();
        public int Overrides { get; private set; }

        public S11RageTable() { Reset(); }

        public void Reset()
        {
            _rows.Clear();
            foreach (S11RageRow r in S11Conventions.RageTable) _rows[r.TechType] = r;
            Overrides = 0;
        }

        public bool TryGet(int techType, out S11RageRow row) { return _rows.TryGetValue(techType, out row); }

        /// <summary>release 0..1 (fraction of maxHealth), anger 1..10 (x), extend 0..60 s, flee 0..120 s; only creatures of the table.</summary>
        public bool Set(int techType, double? release, double? anger, double? extendS, double? fleeS, out string error)
        {
            error = null;
            S11RageRow r;
            if (!_rows.TryGetValue(techType, out r)) { error = "rage: a creature of the table (ReaperLeviathan, SeaDragon)"; return false; }
            if (release.HasValue && (double.IsNaN(release.Value) || release.Value < 0 || release.Value > 1)) { error = "rage release 0..1 (fraction of maxHealth)"; return false; }
            if (anger.HasValue && (double.IsNaN(anger.Value) || anger.Value < 1 || anger.Value > 10)) { error = "rage anger 1..10 (x)"; return false; }
            if (extendS.HasValue && (double.IsNaN(extendS.Value) || extendS.Value < 0 || extendS.Value > 60)) { error = "rage extend 0..60 s"; return false; }
            if (fleeS.HasValue && (double.IsNaN(fleeS.Value) || fleeS.Value < 0 || fleeS.Value > 120)) { error = "rage flee 0..120 s"; return false; }
            if (release.HasValue) r.ReleasePermille = (int)Math.Round(release.Value * 1000.0, MidpointRounding.AwayFromZero);
            if (anger.HasValue) r.AngerPermille = (int)Math.Round(anger.Value * 1000.0, MidpointRounding.AwayFromZero);
            if (extendS.HasValue) r.ExtendMs = (int)Math.Round(extendS.Value * 1000.0, MidpointRounding.AwayFromZero);
            if (fleeS.HasValue) r.FleeMs = (int)Math.Round(fleeS.Value * 1000.0, MidpointRounding.AwayFromZero);
            _rows[techType] = r;
            Overrides++;
            return true;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (S11RageRow d in S11Conventions.RageTable)
            {
                S11RageRow r = _rows[d.TechType];
                sb.Append(sb.Length > 0 ? "; " : "").Append(r.Creature).Append(" release ").Append((r.ReleasePermille / 1000.0).ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" anger x").Append((r.AngerPermille / 1000.0).ToString("0.###", CultureInfo.InvariantCulture))
                  .Append(" extend ").Append((r.ExtendMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)).Append(" s flee ")
                  .Append((r.FleeMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)).Append(" s");
            }
            return sb.ToString();
        }
    }

    /// <summary><c>format missile.hit</c>: SE missiles meeting Subnautica's creatures.</summary>
    public static class S11MissileRules
    {
        /// <summary>A health-pool slug's yield (SE damage units): HealthPool × SlugPermille / 1000; 0 without a pool.</summary>
        public static double SlugYield(double healthPool)
        {
            if (!(healthPool > 0) || double.IsInfinity(healthPool)) return 0.0;
            return healthPool * (S11Conventions.Missile.SlugPermille / 1000.0);
        }

        /// <summary>Review 6: the cast behind a missile's first pose, measured at its second: max(BackCastCm, speed × BackCastMs).</summary>
        public static double BackCastM(double speedMs)
        {
            double b = S11Conventions.Missile.BackCastCm / 100.0;
            if (!(speedMs > 0) || double.IsInfinity(speedMs)) return b;
            return Math.Max(b, speedMs * S11Conventions.Missile.BackCastMs / 1000.0);
        }

        /// <summary>Review 5: a hit more than 1 m behind the launch point along the flight (SE refuses it).</summary>
        public static bool Behind(Vec3 hit, Vec3 origin, Vec3 flight)
        {
            double l = flight.Length;
            if (!(l > 1e-9)) return false;
            Vec3 d = hit - origin;
            return (d.X * flight.X + d.Y * flight.Y + d.Z * flight.Z) / l < -1.0;
        }

        /// <summary>The struck creature takes an explosion at d = 0 when it lies within JoinPadM of its hit, AttributionMs at most after it.</summary>
        public static bool Attributed(double hitAgeMs, double distanceM)
        {
            if (double.IsNaN(hitAgeMs) || double.IsNaN(distanceM) || hitAgeMs < 0) return false;
            return hitAgeMs <= S11Conventions.Missile.AttributionMs && distanceM <= S11Conventions.Blast.JoinPadM;
        }

        /// <summary>
 /// The verdict on a sorted list of what a missile's segment met (nearest first): the index of the first creature when it
 /// comes before any other solid, else −1. Kinds: 0 pass (the ghost Player, the missile's own view), 1 creature, 2 solid.
 /// </summary>
        public static int FirstCreature(int[] kinds, int n)
        {
            if (kinds == null) return -1;
            for (int i = 0; i < n && i < kinds.Length; i++)
            {
                if (kinds[i] == 1) return i;
                if (kinds[i] == 2) return -1;
            }
            return -1;
        }
    }

    // ================================================================================================= records

 /// <summary>The S11 payload checks SE makes after S6's header and dedup checks (CONTRACT §5.4 <c>format c8.s11*</c>).</summary>
    public static class S11Records
    {
        private static bool Finite(F32x3 v) { return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z)); }

        private static bool Unitish(F32x3 v)
        {
            if (!Finite(v)) return false;
            double l = Math.Sqrt((double)v.x * v.x + (double)v.y * v.y + (double)v.z * v.z);
            return l > 0.5 && l < 1.5;
        }

        public static S11Reject CheckPayload(in DamageRecord r)
        {
            uint u = r.unit;
            if (!S11Conventions.Unit.IsS11(u)) return S11Reject.Unit;
            float a = r.amount;
            if (float.IsNaN(a) || float.IsInfinity(a) || !(a > 0f)) return S11Reject.Amount;
            if (!Finite(r.snWorldHit) || !Finite(r.localHit) || !Finite(r.attackDirSn)) return S11Reject.Vector;
            switch (u)
            {
                case S11Conventions.Unit.HazardHp:
                    if (a > S11Conventions.Limits.MaxHazardHp) return S11Reject.Amount;
                    if (r.targetKind != DamageTargetKind.Character) return S11Reject.Kind;
                    if (!S11HazardRules.Forwarded(r.snDamageType)) return S11Reject.NotForwarded;
                    return S11Reject.None;
                case S11Conventions.Unit.HealHp:
                    if (a > S11Conventions.Heal.MaxRecordHp) return S11Reject.Amount;
                    if (r.targetKind != DamageTargetKind.Character) return S11Reject.Kind;
                    return S11Reject.None;
                case S11Conventions.Unit.OxygenS:
                    if (a > S11Conventions.Oxygen.MaxRecordS) return S11Reject.Amount;
                    if (r.targetKind != DamageTargetKind.Character) return S11Reject.Kind;
                    if (r.snDamageType < S11Conventions.OxygenSource.Bubble || r.snDamageType > S11Conventions.OxygenSource.Other) return S11Reject.Type;
                    return S11Reject.None;
                case S11Conventions.Unit.EmpS:
                    if (a > S11Conventions.Limits.MaxEmpS) return S11Reject.Amount;
                    if (r.targetKind != DamageTargetKind.GridSection) return S11Reject.Kind;
                    return S11Reject.None;
                case S11Conventions.Unit.Grab:
                    if (r.targetKind != DamageTargetKind.GridSection) return S11Reject.Kind;
                    if (r.snDamageType < S11Conventions.GrabPhase.Start || r.snDamageType > S11Conventions.GrabPhase.Release) return S11Reject.Type;
                    if (r.dealerInstanceId == 0) return S11Reject.Entity;
                    if (r.snDamageType != S11Conventions.GrabPhase.Release && (!Unitish(r.attackDirSn) || !Unitish(r.localHit))) return S11Reject.Vector;
                    if (a > 600f) return S11Reject.Amount;
                    return S11Reject.None;
                case S11Conventions.Unit.Push:
                    if (a > S11Conventions.Limits.MaxPushNs) return S11Reject.Amount;
                    if (r.targetKind != DamageTargetKind.GridSection) return S11Reject.Kind;
                    if (!Unitish(r.attackDirSn)) return S11Reject.Vector;
                    return S11Reject.None;
                case S11Conventions.Unit.MissileHit:
                    if (a > 10f) return S11Reject.Amount;
                    if (r.snDamageType != S11Conventions.Missile.HitBody && r.snDamageType != S11Conventions.Missile.HitHolder) return S11Reject.Type;
                    if (r.targetKind != DamageTargetKind.GridSection) return S11Reject.Kind;
                    if (r.targetEntityId == 0 || r.targetEntityId != r.rootEntityId) return S11Reject.Entity;
                    if (!Unitish(r.attackDirSn)) return S11Reject.Vector;
                    return S11Reject.None;
            }
            return S11Reject.Unit;
        }
    }
}
