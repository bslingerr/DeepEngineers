using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using DeepEngineers.Wire;
using EventType = DeepEngineers.Wire.S7Conventions.EventType;
using ToolSource = DeepEngineers.Wire.S7Conventions.ToolSource;

namespace DeepEngineers.SN
{
    // =================================================================================================================
 // S7p-gameplay: the Unity-free rules of the Subnautica side.
 // Replay-exact and tested (tests/DeepEngineers.SN.Tests/S7pGameplayTests.cs); no game type, no Unity type.
 // - S7pDamageRules: SE's weapon and tool events -> Subnautica creature damage (P11).
 // - S7pRoomIndex: the room-box classification of a part in O(1) (P14: a 100,669-part grid against 2,250 room boxes
 // stalled Subnautica's main thread 4,474 ms in InteriorChunks.
 // - S7pMirrorBudget: the per-frame millisecond budgets of the S5 mirror's main-thread steps (P14).
 // - S7pAxisFreedom: the camera's motion per axis over a window (P13 trace).
 // =================================================================================================================

 /// <summary>

 /// definitions and decompiled code (read-only; nothing copied), mapped to Subnautica hit points through CONTRACT §4.1's
 /// character factor <c>k_char</c> = 1 SE health point per SN_HP: a creature takes what an SE character would take.
 /// The event rows carry no ammunition id (ext s7 look 1 is unchanged), so the rule keys on what they carry: the event
 /// type, the hand/ship source (the attached entity is the C1 character or a grid), the projectile count
 /// (BulletStreak intensity = count / 8) and the explosion damage (Explosion intensity = min(1, damage / 1,000)).
 /// </summary>
    internal static class S7pDamageRules
    {
        public const string EnvVar = "DEEPENGINEERS_S7P_DAMAGE";
        public const string Cmd = "de.s7p";
        public const double DefaultScale = 1.0;
        public const double MaxScale = 100.0;

        /// <summary>Hand weapon, per projectile: SmallCaliber ProjectileHealthDamage 100 (Content\Data\Ammos.sbc) × AutomaticRifleGun DamageMultiplier 0.3 (Weapons.sbc).</summary>
        public const float HandProjectileHp = 30f;
        /// <summary>Ship weapon, per projectile: LargeCaliber (NATO_25x184mm, the gatling) ProjectileHealthDamage 33, no multiplier.</summary>
        public const float ShipProjectileHp = 33f;
        /// <summary>Explosion: the row's intensity × 1,000 = SE's explosion damage (S7EventRules.ExplosionIntensity; Missile200mm 500 → 0.5).</summary>
        public const float ExplosionHpPerIntensity = 1000f;
        /// <summary>Hand drill: MyDrillBase.BASE_DRILL_DAMAGE 20 per drill, MyHandDrill cooldown 1,000 ms.</summary>
        public const float HandDrillDps = 20f;
        /// <summary>Hand grinder: MyAngleGrinder.BASE_GRINDER_CHARACTER_DAMAGE 4 per ToolCooldownMs 250.</summary>
        public const float HandGrinderDps = 16f;
        /// <summary>Ship grinder and ship welder: MyShipToolBase 20 per UpdateAfterSimulation10 (6 per second).</summary>
        public const float ShipToolDps = 120f;
        /// <summary>Ship drill: BASE_DRILL_DAMAGE 20 every 90 frames (MyShipDrill m_drillFrameCountdown).</summary>
        public const float ShipDrillDps = 20f * 60f / 90f;
        /// <summary>The hand welder (shown as Subnautica's repair tool) never hurts a character in SE.</summary>
        public const float RepairDps = 0f;

        /// <summary>A streak shorter than StreakMaxM is the ammunition's own MaxTrajectory; at the cap the real range is SE's 800 m.</summary>
        public const float FullRangeM = 800f;
        /// <summary>Rays are cast at most this far (the effect radius: creatures beyond are not near the action).</summary>
        public const float MaxRayM = 250f;
        public const float MinContactRadiusM = 0.5f;
        public const float MaxContactRadiusM = 3f;
        /// <summary>The explosion overlap radius is capped here (the event allows 1,000 m; SE's warheads and missiles are far smaller).</summary>
        public const float MaxBlastRadiusM = 100f;
        /// <summary>Tool damage is applied at this cadence (one TakeDamage per creature per step).</summary>
        public const double ToolStepS = 0.25;
        public const int MaxProjectilesPerShot = 8;

        // per-frame caps (drops counted in the s7p.damage row)
        public const int MaxEventsPerFrame = 64;
        public const int MaxHitsPerFrame = 32;
        public const int MaxSustains = 64;
        public const int MaxHitRowsPerSecond = 20;

        // Subnautica DamageType ordinals
        public const int SnNormal = 0, SnExplosive = 7, SnDrill = 9;

        /// <summary>True for the event types that can damage a creature.</summary>
        public static bool IsDamageEvent(byte type)
        {
            switch (type)
            {
                case EventType.BulletStreak:
                case EventType.Explosion:
                case EventType.Welder:
                case EventType.Grinder:
                case EventType.Drill:
                case EventType.PrawnDrill:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>BulletStreak intensity = ProjectileCount / 8 (S7EventRules.StreakIntensity) → the count, 1..8.</summary>
        public static int ProjectileCount(float intensity)
        {
            if (!(intensity > 0f) || float.IsInfinity(intensity)) return 1;
            int n = (int)Math.Round(intensity * 8.0, MidpointRounding.AwayFromZero);
            return n < 1 ? 1 : (n > MaxProjectilesPerShot ? MaxProjectilesPerShot : n);
        }

        /// <summary>
 /// The ray length for a shot: the streak row's size is min(MaxTrajectory, StreakMaxM); at the cap the projectile
 /// flies on (SE's projectile ammunition: 800 m) and the creature, which SE does not have, may lie beyond SE's hit.
 /// </summary>
        public static float RayRangeM(float streakSize)
        {
            if (!(streakSize > 0f) || float.IsInfinity(streakSize)) return MaxRayM;
            float r = streakSize >= S7Conventions.EventLimits.StreakMaxM - 1e-3f ? FullRangeM : streakSize;
            return r > MaxRayM ? MaxRayM : r;
        }

        /// <summary>SN_HP per projectile: a hand weapon (attached to a character) or a ship weapon (attached to a grid or the world).</summary>
        public static float ProjectileHp(bool handHeld) { return handHeld ? HandProjectileHp : ShipProjectileHp; }

        /// <summary>
 /// Explosion damage at <paramref name="distance"/> from the centre: intensity × 1,000 × max(0, (r − d) / r), the falloff
 /// of Subnautica's own DamageSystem.RadiusDamage; 0 outside the radius.
 /// </summary>
        public static float ExplosionHp(float intensity, float radius, float distance)
        {
            if (!(intensity > 0f) || !(radius > 0f) || float.IsInfinity(radius) || float.IsNaN(distance)) return 0f;
            float f = Math.Max(0f, (radius - Math.Max(0f, distance)) / radius);
            return Math.Min(intensity, 1f) * ExplosionHpPerIntensity * f;
        }

        /// <summary>Tool damage per second by event type and source (hand or ship).</summary>
        public static float ToolDps(byte type, ushort subtype)
        {
            switch (type)
            {
                case EventType.Drill: return HandDrillDps;
                case EventType.Grinder: return subtype == ToolSource.Ship ? ShipToolDps : HandGrinderDps;
                case EventType.Welder: return ShipToolDps;
                case EventType.PrawnDrill: return ShipDrillDps;
                case EventType.Repair: return RepairDps;
                default: return 0f;
            }
        }

        /// <summary>One tool step's damage: dps × dt × scale (0 for a non-tool, a non-positive step or scale).</summary>
        public static float ToolStepHp(byte type, ushort subtype, double dtS, double scale)
        {
            if (!(dtS > 0) || !(scale > 0)) return 0f;
            return (float)(ToolDps(type, subtype) * dtS * scale);
        }

        /// <summary>The contact sphere of a tool: the row's size (detector sphere radius, hand tool reach), clamped 0.5..3 m.</summary>
        public static float ContactRadius(float size)
        {
            if (float.IsNaN(size) || float.IsInfinity(size)) return MinContactRadiusM;
            return size < MinContactRadiusM ? MinContactRadiusM : (size > MaxContactRadiusM ? MaxContactRadiusM : size);
        }

        /// <summary>Subnautica's DamageType ordinal for an event type: Explosive, Drill, else Normal.</summary>
        public static int SnDamageType(byte type)
        {
            switch (type)
            {
                case EventType.Explosion: return SnExplosive;
                case EventType.Drill:
                case EventType.PrawnDrill: return SnDrill;
                default: return SnNormal;
            }
        }

        /// <summary>
 /// The live switch (<c>de.s7p {damage: …}</c> and the env var): "off" (or false) disables, "on" (or true) = 1, a
 /// number in [0, 100] is the scale (0 = off). Null or empty = the default 1.
 /// </summary>
        public static bool TryParseScale(object v, out double scale, out string error)
        {
            scale = DefaultScale;
            error = null;
            if (v == null) return true;
            if (v is bool) { scale = (bool)v ? DefaultScale : 0.0; return true; }
            string s = v as string;
            double d;
            if (s != null)
            {
                s = s.Trim().ToLowerInvariant();
                if (s.Length == 0 || s == "on" || s == "true" || s == "default") return true;
                if (s == "off" || s == "false") { scale = 0.0; return true; }
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { error = "damage: off | on | a scale in [0, 100]"; return false; }
            }
            else
            {
                try { d = Convert.ToDouble(v, CultureInfo.InvariantCulture); }
                catch (Exception) { error = "damage: off | on | a scale in [0, 100]"; return false; }
            }
            if (double.IsNaN(d) || d < 0 || d > MaxScale) { error = "damage: a scale in [0, 100]"; return false; }
            scale = d;
            return true;
        }

        /// <summary>The scale from the environment (unset or unparsable: the default 1, logged by the caller).</summary>
        public static double ScaleFromEnv(string value, out string error)
        {
            double s;
            if (!TryParseScale(value, out s, out error)) return DefaultScale;
            return s;
        }
    }

    /// <summary>
 /// P14: the interior class of a cell (CONTRACT <c>format interior.class</c>) in O(1) per lookup. Wire's
 /// <see cref="InteriorRules.Classify(int, int, int, IReadOnlyList{CellBox})"/> scans every room box
 /// ship (100,669 parts, 2,250 room boxes) made <c>InteriorChunks.Reclassify</c> test 2.3·10⁸ boxes in one C4 record
 /// (4,474 ms on Subnautica's main thread, SE's link watchdog then restarted the whole stream) and every PartAdd test
 /// 2,250. The index buckets the boxes inflated by one cell (the boundary shell) into 8-cell buckets; a cell scans only
 /// the boxes of its bucket, which are all the boxes within Chebyshev distance 1 of it, so the answer is exactly Wire's
 /// (tested against the linear scan). Boxes that would touch too many buckets, and invalid boxes, are scanned for
 /// every cell (as before). One index per box array instance (a RoomSet replaces the array), cached weakly.
 /// </summary>
    internal sealed class S7pRoomIndex
    {
        /// <summary>Below this many boxes the linear scan is used (no index).</summary>
        public const int MinBoxes = 16;
        public const int BucketShift = 3;
        /// <summary>A box touching more buckets than this goes to the always-scanned list.</summary>
        public const long MaxBucketsPerBox = 4096;

        private struct Key : IEquatable<Key>
        {
            public readonly int X, Y, Z;
            public Key(int x, int y, int z) { X = x; Y = y; Z = z; }
            public bool Equals(Key o) { return X == o.X && Y == o.Y && Z == o.Z; }
            public override bool Equals(object obj) { return obj is Key && Equals((Key)obj); }
            public override int GetHashCode() { unchecked { return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791); } }
        }

        private static readonly ConditionalWeakTable<CellBox[], S7pRoomIndex> Cache = new ConditionalWeakTable<CellBox[], S7pRoomIndex>();

        // statistics (main thread; the s7p.mirror row)
        public static long Builds, Lookups, LinearLookups, BoxTests;
        public static double BuildMsMax, BuildMsTotal;

        private readonly CellBox[] _boxes;
        private readonly Dictionary<Key, int[]> _buckets;
        private readonly int[] _always;

        public int BucketCount { get { return _buckets.Count; } }
        public int AlwaysCount { get { return _always.Length; } }

        private S7pRoomIndex(CellBox[] boxes)
        {
            _boxes = boxes;
            var lists = new Dictionary<Key, List<int>>();
            var always = new List<int>();
            for (int i = 0; i < boxes.Length; i++)
            {
                CellBox b = boxes[i];
                if (!b.IsValid) { always.Add(i); continue; }
                int x0 = Bucket((long)b.MinX - 1), x1 = Bucket((long)b.MaxX + 1);
                int y0 = Bucket((long)b.MinY - 1), y1 = Bucket((long)b.MaxY + 1);
                int z0 = Bucket((long)b.MinZ - 1), z1 = Bucket((long)b.MaxZ + 1);
                long n = ((long)x1 - x0 + 1) * ((long)y1 - y0 + 1) * ((long)z1 - z0 + 1);
                if (n > MaxBucketsPerBox) { always.Add(i); continue; }
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            var k = new Key(x, y, z);
                            List<int> l;
                            if (!lists.TryGetValue(k, out l)) { l = new List<int>(4); lists[k] = l; }
                            l.Add(i);
                        }
            }
            _buckets = new Dictionary<Key, int[]>(lists.Count);
            foreach (KeyValuePair<Key, List<int>> kv in lists) _buckets[kv.Key] = kv.Value.ToArray();
            _always = always.ToArray();
        }

        /// <summary>The bucket of a cell coordinate (floor division by 8; long in, clamped to int).</summary>
        private static int Bucket(long c)
        {
            long b = c >> BucketShift; // arithmetic shift = floor for negatives
            return b < int.MinValue ? int.MinValue : (b > int.MaxValue ? int.MaxValue : (int)b);
        }

        /// <summary>The index of a box array (built once per array instance); null for a small or empty array.</summary>
        public static S7pRoomIndex For(CellBox[] boxes)
        {
            if (boxes == null || boxes.Length < MinBoxes) return null;
            S7pRoomIndex ix;
            if (Cache.TryGetValue(boxes, out ix)) return ix;
            long t0 = Stopwatch.GetTimestamp();
            ix = new S7pRoomIndex(boxes);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Builds++;
            BuildMsTotal += ms;
            if (ms > BuildMsMax) BuildMsMax = ms;
            Cache.Add(boxes, ix);
            return ix;
        }

        /// <summary>The class of a cell: Inside (Chebyshev 0 to a box), Boundary (1), Exterior; exactly Wire's rule.</summary>
        public byte Classify(int x, int y, int z)
        {
            Lookups++;
            bool boundary = false;
            int[] list;
            if (_buckets.TryGetValue(new Key(Bucket(x), Bucket(y), Bucket(z)), out list))
            {
                for (int i = 0; i < list.Length; i++)
                {
                    BoxTests++;
                    long d = _boxes[list[i]].Chebyshev(x, y, z);
                    if (d == 0) return S7Conventions.InteriorClass.Inside;
                    if (d == 1) boundary = true;
                }
            }
            for (int i = 0; i < _always.Length; i++)
            {
                BoxTests++;
                long d = _boxes[_always[i]].Chebyshev(x, y, z);
                if (d == 0) return S7Conventions.InteriorClass.Inside;
                if (d == 1) boundary = true;
            }
            return boundary ? S7Conventions.InteriorClass.Boundary : S7Conventions.InteriorClass.Exterior;
        }

        /// <summary>
 /// The class of a part from its grid-local translation (metres) and the grid size: Wire's
 /// <see cref="InteriorRules.ClassifyLocal"/> with the index (the linear scan for fewer than <see cref="MinBoxes"/> boxes).
 /// </summary>
        public static byte ClassifyLocal(double tx, double ty, double tz, double gridSize, CellBox[] boxes)
        {
            if (boxes == null || boxes.Length == 0) return S7Conventions.InteriorClass.Exterior;
            S7pRoomIndex ix = For(boxes);
            if (ix == null)
            {
                LinearLookups++;
                BoxTests += boxes.Length;
                return InteriorRules.ClassifyLocal(tx, ty, tz, gridSize, boxes);
            }
            return ix.Classify(InteriorRules.CellOf(tx, gridSize), InteriorRules.CellOf(ty, gridSize), InteriorRules.CellOf(tz, gridSize));
        }

        public static string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            return "builds=" + Builds + " buildMsMax=" + BuildMsMax.ToString("0.###", ci) + " lookups=" + Lookups + " linear=" + LinearLookups + " boxTests=" + BoxTests;
        }
    }

    /// <summary>
 /// P14: the per-frame budgets of the S5 mirror's main-thread steps (ARCHITECTURE §5.13 frame budget; the C4/C3 drain
 /// keeps its 4 ms and the uploads their 2 ms). Live through <c>de.s7p {mirrorMs: …}</c>.
 /// </summary>
    internal static class S7pMirrorBudget
    {
        /// <summary>Child parts (Dynamic subparts, tools, floating objects) created per frame: time budget …</summary>
        public const double ChildCreateMs = 2.0;
        /// <summary>… and a hard count cap (a GameObject, its renderers and SkyAppliers each).</summary>
        public const int MaxChildrenPerFrame = 48;
        /// <summary>Cell builds posted per frame (each clones its cell's parts on the main thread): time budget.</summary>
        public const double PostMs = 2.0;
        /// <summary>Worker results taken per frame (a prepared model marks its parts' cells): time budget.</summary>
        public const double ResultsMs = 3.0;

        /// <summary>The current scale of the budgets (1 = the defaults; <c>de.s7p {mirrorMs: x}</c> sets ChildCreateMs = x etc. proportionally).</summary>
        public static double Scale = 1.0;

        public static double ChildMs { get { return ChildCreateMs * Scale; } }
        public static double PostBudgetMs { get { return PostMs * Scale; } }
        public static double ResultsBudgetMs { get { return ResultsMs * Scale; } }

        /// <summary>True while a step started at <paramref name="startTicks"/> may run one more unit (the first always runs).</summary>
        public static bool Within(long startTicks, long nowTicks, long ticksPerSecond, double budgetMs, int doneUnits)
        {
            if (doneUnits <= 0) return true;
            if (ticksPerSecond <= 0) return false;
            return (nowTicks - startTicks) * 1000.0 / ticksPerSecond < budgetMs;
        }

        /// <summary>A child may still be created this frame: under the time budget and the count cap.</summary>
        public static bool ChildAllowed(long startTicks, long nowTicks, long ticksPerSecond, int createdThisFrame)
        {
            if (createdThisFrame >= MaxChildrenPerFrame) return false;
            return Within(startTicks, nowTicks, ticksPerSecond, ChildMs, createdThisFrame);
        }
    }

    /// <summary>
 /// P13: how far a camera turned on each axis over a window (yaw about world up, pitch = elevation of the forward
 /// vector, roll of the up vector about forward), summed as absolute increments (yaw and roll unwrapped). A camera "stuck
 /// on one axis" shows input on that axis (SE side) with ≈ 0° of motion while the other axis moves.
 /// </summary>
    internal sealed class S7pAxisFreedom
    {
        private bool _have;
        private double _yaw, _pitch, _roll;
        private double _sumYaw, _sumPitch, _sumRoll;
        private int _samples;

        /// <summary>Adds one orientation (degrees).</summary>
        public void Add(double yawDeg, double pitchDeg, double rollDeg)
        {
            if (double.IsNaN(yawDeg) || double.IsNaN(pitchDeg) || double.IsNaN(rollDeg) || double.IsInfinity(yawDeg) || double.IsInfinity(pitchDeg) || double.IsInfinity(rollDeg)) return;
            if (_have)
            {
                _sumYaw += Math.Abs(Wrap(yawDeg - _yaw));
                _sumPitch += Math.Abs(pitchDeg - _pitch);
                _sumRoll += Math.Abs(Wrap(rollDeg - _roll));
            }
            _yaw = yawDeg; _pitch = pitchDeg; _roll = rollDeg;
            _have = true;
            _samples++;
        }

        /// <summary>The window's sums; the window restarts (the last orientation is kept as the next reference).</summary>
        public void Take(out double yawDeg, out double pitchDeg, out double rollDeg, out int samples)
        {
            yawDeg = _sumYaw; pitchDeg = _sumPitch; rollDeg = _sumRoll; samples = _samples;
            _sumYaw = _sumPitch = _sumRoll = 0;
            _samples = 0;
        }

        public void Reset() { _have = false; _sumYaw = _sumPitch = _sumRoll = 0; _samples = 0; }

        /// <summary>An angle difference in (−180, 180].</summary>
        public static double Wrap(double d)
        {
            d %= 360.0;
            if (d > 180.0) d -= 360.0;
            else if (d <= -180.0) d += 360.0;
            return d;
        }

        /// <summary>Yaw, pitch and roll (degrees) of a rotation given as its forward (f) and up (u) unit vectors in a Y-up frame.</summary>
        public static void Angles(double fx, double fy, double fz, double ux, double uy, double uz, out double yaw, out double pitch, out double roll)
        {
            yaw = Math.Atan2(fx, fz) * 180.0 / Math.PI;
            double h = Math.Sqrt(fx * fx + fz * fz);
            pitch = Math.Atan2(fy, h) * 180.0 / Math.PI;
            // roll: the up vector against the "level" up of the same forward (Y-up world)
            double rx = fz, ry = 0, rz = -fx; // right of a level camera = up × forward (unnormalised)
            double rl = Math.Sqrt(rx * rx + rz * rz);
            if (rl < 1e-9) { roll = 0; return; }
            rx /= rl; rz /= rl;
            // level up = forward × right
            double lx = fy * rz - fz * ry, ly = fz * rx - fx * rz, lz = fx * ry - fy * rx;
            double s = ux * rx + uy * ry + uz * rz;      // component of up along right
            double c = ux * lx + uy * ly + uz * lz;      // component along level up
            roll = Math.Atan2(-s, c) * 180.0 / Math.PI;
        }

        /// <summary>True when every component is finite and the norm is usable.</summary>
        public static bool FiniteQuat(float x, float y, float z, float w)
        {
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsNaN(w)) return false;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z) || float.IsInfinity(w)) return false;
            return x * x + y * y + z * z + w * w > 1e-12f;
        }
    }
}
