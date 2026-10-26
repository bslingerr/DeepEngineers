using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S11 "Creatures and hazards" (ROADMAP §14 "### S11"; docs/S11-BRIEF.md): the meaning S11 gives to C8 records whose
 // `unit` is not 1 (S6's SN_HP damage stays exactly as pinned by `ext s6 combat 1`), to the `extra` pad of C2 Explosion
 // event rows (0 for every type but Thruster and BulletStreak in `ext s7p fx 1`), and the data tables of the slice:
 // the weapon classes against Subnautica's own creature health (A), the explosion clusters (A, stacked warheads), the
 // grab and push tables (C), the hazard table DamageType -> SE damage (D), the healing and oxygen rules (E). Layout 1.0
 // is unchanged (hash 0x4f3a35f6fc1eaf31); every earlier extension pin is unchanged. Pinned on its own by the canonical
 // text below, built from the tables of this file (a changed number changes the hash and turns S11ConventionsTests
 // red), as S6, S7 and S8a pinned theirs. Python twin: tools/s11/s11_ref.py (text, hash, rules, vectors in
 // tests/s11/vectors/).s11` (dev), which
 // never changes the pin.
 // =================================================================================================================

 /// <summary>One row of the weapon-class table: a class against a creature tier.</summary>
    public struct S11WeaponRow
    {
        public int Class, Tier;
        /// <summary>Damage multiplier, per mille (1000 = SE's own number through k_char).</summary>
        public int MultPermille;
        /// <summary>The health this class can never take a creature of this tier below, per mille of its maxHealth (500 = half).</summary>
        public int FloorPermille;

        public S11WeaponRow(int cls, int tier, int mult, int floor) { Class = cls; Tier = tier; MultPermille = mult; FloorPermille = floor; }
        public double Mult { get { return MultPermille / 1000.0; } }
        public double Floor { get { return FloorPermille / 1000.0; } }
    }

    /// <summary>One row of the grab table (ROADMAP §14 S11 point 2: only grids of the creature's own size class are held).</summary>
    public struct S11GrabRow
    {
        /// <summary>Subnautica TechType name and value (TechType.cs) of the creature.</summary>
        public string Creature;
        public int TechType;
        /// <summary>The public Transform field of the creature's class the held object follows (ReaperLeviathan.seamothAttachPoint, SeaDragon.exosuitAttachPoint).</summary>
        public string AttachField;
        /// <summary>The public float field of the creature's class with its hold damage per second (seamothDamagePerSecond, exosuitDamagePerSecond).</summary>
        public string DpsField;
        /// <summary>The largest grid bounding volume (m³, the grid's own box) the creature holds.</summary>
        public int MaxVolumeM3;
        /// <summary>The largest edge of the grid's own box, decimetres.</summary>
        public int MaxEdgeDm;
        /// <summary>The hold lasts HoldMinMs + random × HoldRandMs (ReaperLeviathan.GrabVehicle: Invoke ReleaseVehicle 8 + Random.value·5 s).</summary>
        public int HoldMinMs, HoldRandMs;
        /// <summary>No new grab before this long after a release (ReaperLeviathan.GetCanGrabVehicle: 10 s).</summary>
        public int CooldownMs;
        /// <summary>The creature's own hold damage per second × this per mille, every IntervalMs.</summary>
        public int DpsPermille, IntervalMs;
        /// <summary>Subnautica DamageType names that make the creature let go (ReaperLeviathan.OnTakeDamage: Electrical, Poison), comma-separated, or "none".</summary>
        public string ReleaseOn;
        /// <summary>

 /// attach point over this long from where it was at the grab, as Subnautica lerps a held vehicle onto the attach point
 /// </summary>
        public int BlendMs;

        public double MaxEdgeM { get { return MaxEdgeDm / 10.0; } }
    }

    /// <summary>One row of the push table: a leviathan bite on a grid it cannot hold pushes the grid.</summary>
    public struct S11PushRow
    {
        public string Creature;
        /// <summary>Subnautica TechType value (TechType.cs).</summary>
        public int TechType;
        /// <summary>Impulse = creature mass × closing speed along the bite × this per mille.</summary>
        public int FactorPermille;
        /// <summary>The grid's velocity change is capped here (cm/s).</summary>
        public int MaxDvCmS;
    }

    /// <summary>One row of the scale table: a leviathan's size and its bite on SE grids.</summary>
    public struct S11ScaleRow
    {
        public string Creature;
        public int TechType;
        /// <summary>The root transform's scale, per mille (2500 = 2.5 x).</summary>
        public int FactorPermille;
        /// <summary>Its Normal hits on SE grids x this per mille (SE side).</summary>
        public int BiteMultPermille;
        public double Factor { get { return FactorPermille / 1000.0; } }
        public double BiteMult { get { return BiteMultPermille / 1000.0; } }
    }

    /// <summary>

 /// you go"): the SN_HP a holding creature takes from SE weapons during one hold accumulates (format rage.hold).
 /// </summary>
    public struct S11RageRow
    {
        public string Creature;
        public int TechType;
        /// <summary>At this share of the creature's own LiveMixin.maxHealth (per mille) taken during one hold it lets go and flees.</summary>
        public int ReleasePermille;
        /// <summary>A lower hit makes it angrier once per hold: its hold damage x this per mille...</summary>
        public int AngerPermille;
        /// <summary>... and its planned hold this much longer.</summary>
        public int ExtendMs;
        /// <summary>After a release by damage it flees this long (no grab, no attack).</summary>
        public int FleeMs;
        public double Release { get { return ReleasePermille / 1000.0; } }
        public double Anger { get { return AngerPermille / 1000.0; } }
    }

    /// <summary>One row of the hazard table (ROADMAP §14 S11 point 4): a Subnautica DamageType -> SE damage.</summary>
    public struct S11HazardRow
    {
        /// <summary>Subnautica DamageType name and value (DamageType.cs).</summary>
        public string Name;
        public int SnType;
        /// <summary>The SE damage type (MyStringHash.GetOrCompute).</summary>
        public string SeType;
        /// <summary>SE health points per SN_HP, per mille (1000 = k_char).</summary>
        public int KPermille;
        /// <summary>At most this many SE health points per second from this type (a flood guard; the excess is counted).</summary>
        public int CapPerS;
        /// <summary>The SE HUD notice key shown while the hazard acts.</summary>
        public string Effect;
    }

    /// <summary>The S11 conventions (CONTRACT §5.4 "S11 conventions"): the constants and the tables, and the canonical text.</summary>
    public static class S11Conventions
    {
        /// <summary>The first line of the canonical text (<c>ext s11 creatures &lt;n&gt;</c>).</summary>
        public const string Header = "ext s11 creatures 5";
        public const int ExtensionVersion = 5;
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4.</summary>
        public const ulong PinnedHash = 0xa10efc0920e7f8e8UL;
        public const int CanonicalLines = 133, CanonicalBytes = 12020;

        /// <summary>C8 <c>unit</c> values S11 adds (S6's SN_HP damage is 1, <see cref="DamageUnit.SnHp"/>).</summary>
        public static class Unit
        {
            public const uint HazardHp = 17;
            public const uint HealHp = 18;
            public const uint OxygenS = 19;
            public const uint EmpS = 20;
            public const uint Grab = 21;
            public const uint Push = 22;
            /// <summary>: a mirrored SE missile met a creature in Subnautica (format missile.hit, c8.s11missile).</summary>
            public const uint MissileHit = 23;
            public static bool IsS11(uint unit) { return unit >= HazardHp && unit <= MissileHit; }
            public static string Name(uint unit)
            {
                switch (unit)
                {
                    case HazardHp: return "hazard";
                    case HealHp: return "heal";
                    case OxygenS: return "oxygen";
                    case EmpS: return "emp";
                    case Grab: return "grab";
                    case Push: return "push";
                    case MissileHit: return "missile";
                    case 1: return "damage";
                    default: return "unit" + unit.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        /// <summary>C8 <c>flags</c> bits S11 adds (S6 uses bits 0-4).</summary>
        public static class Flags
        {
            /// <summary>The record comes from an S11 sampler (temperature, lava, base air), not from a game TakeDamage call.</summary>
            public const ushort ViaSampler = 256;
            /// <summary>The game hit the character section (S6 kind 2), not the ghost Player.</summary>
            public const ushort ViaSection = 512;
        }

        public static class WeaponClass
        {
            public const int Handheld = 1, SmallGrid = 2, LargeGrid = 3, Explosion = 4;
            public static readonly string[] Names = { "", "Handheld", "SmallGrid", "LargeGrid", "Explosion" };
            public static string Name(int c) { return c >= 1 && c <= 4 ? Names[c] : "class" + c.ToString(CultureInfo.InvariantCulture); }
            public static int Parse(string s)
            {
                if (string.IsNullOrEmpty(s)) return 0;
                s = s.Trim().ToLowerInvariant();
                switch (s)
                {
                    case "handheld": case "hand": case "1": return Handheld;
                    case "smallgrid": case "small": case "smallturret": case "2": return SmallGrid;
                    case "largegrid": case "large": case "largeturret": case "3": return LargeGrid;
                    case "explosion": case "blast": case "warhead": case "4": return Explosion;
                    default: return 0;
                }
            }
        }

        public static class Tier
        {
            public const int Small = 1, Large = 2, Leviathan = 3;
            /// <summary>The smallest maxHealth of a tier (LiveMixin.maxHealth read at run time; reaper 5,000, ghost 8,000, boneshark below 300).</summary>
            public const int LargeMinHp = 200, LeviathanMinHp = 2000;
            public static readonly string[] Names = { "", "Small", "Large", "Leviathan" };
            public static string Name(int t) { return t >= 1 && t <= 3 ? Names[t] : "tier" + t.ToString(CultureInfo.InvariantCulture); }
            public static int Parse(string s)
            {
                if (string.IsNullOrEmpty(s)) return 0;
                switch (s.Trim().ToLowerInvariant())
                {
                    case "small": case "1": return Small;
                    case "large": case "2": return Large;
                    case "leviathan": case "lev": case "3": return Leviathan;
                    default: return 0;
                }
            }
        }

        /// </summary>
        public static readonly S11WeaponRow[] WeaponTable =
        {
            new S11WeaponRow(WeaponClass.Handheld, Tier.Small, 1000, 0),
            new S11WeaponRow(WeaponClass.Handheld, Tier.Large, 1000, 0),
            new S11WeaponRow(WeaponClass.Handheld, Tier.Leviathan, 1000, 500),
            new S11WeaponRow(WeaponClass.SmallGrid, Tier.Small, 1000, 0),
            new S11WeaponRow(WeaponClass.SmallGrid, Tier.Large, 1000, 0),
            new S11WeaponRow(WeaponClass.SmallGrid, Tier.Leviathan, 1000, 250),
            new S11WeaponRow(WeaponClass.LargeGrid, Tier.Small, 1000, 0),
            new S11WeaponRow(WeaponClass.LargeGrid, Tier.Large, 1000, 0),
            new S11WeaponRow(WeaponClass.LargeGrid, Tier.Leviathan, 1000, 0),
            new S11WeaponRow(WeaponClass.Explosion, Tier.Small, 1000, 0),
            new S11WeaponRow(WeaponClass.Explosion, Tier.Large, 1000, 0),
            new S11WeaponRow(WeaponClass.Explosion, Tier.Leviathan, 1000, 0),
        };

        /// <summary>Explosion events and clusters (stacked warheads;</summary>
        public static class Blast
        {
            /// <summary>An Explosion row's <c>extra</c> = round(yield / YieldUnitSe) clamped 1..65535 (SE damage units); 0 = absent.</summary>
            public const int YieldUnitSe = 10;
            /// <summary>Two explosions join when their centres lie within max(R, r) + JoinPadM.</summary>
            public const int JoinPadM = 2;
            /// <summary>Subnautica's picture: rows joining an open cluster within this window after its first row play as one escalated effect.</summary>
            public const int ChainWindowMs = 600;
            /// <summary>The escalated effect's radius is capped here.</summary>
            public const int VisualRadiusCapM = 200;
            /// <summary>The creature-damage overlap radius is capped here (S7p's MaxBlastRadiusM).</summary>
            public const int DamageRadiusCapM = 100;
            /// <summary>At most this many cluster rows per SE tick and weapon class (each a sum; the rest join the nearest).</summary>
            public const int MaxGroupsPerTick = 16;
            /// <summary>An Explosion row's <c>subtype</c> bits 8-11 = the weapon class of its origin (<c>format event.class</c>); the low byte keeps S7's explosion type.</summary>
            public const int ClassShift = 8;
        }

        public static readonly S11GrabRow[] GrabTable =
        {
            // 5 again: the class back to its x2.5 size (700 x (2.5/3)^3 = 405 -> 400 m3, 18 x 2.5/3 = 15 m)
            new S11GrabRow { Creature = "ReaperLeviathan", TechType = 2540, AttachField = "seamothAttachPoint", DpsField = "seamothDamagePerSecond", MaxVolumeM3 = 400, MaxEdgeDm = 150,
                             HoldMinMs = 8000, HoldRandMs = 5000, CooldownMs = 10000, DpsPermille = 1000, IntervalMs = 1000, BlendMs = 1000, ReleaseOn = "Electrical,Poison" },
            new S11GrabRow { Creature = "SeaDragon", TechType = 2553, AttachField = "exosuitAttachPoint", DpsField = "exosuitDamagePerSecond", MaxVolumeM3 = 2900, MaxEdgeDm = 310,
                             HoldMinMs = 3000, HoldRandMs = 0, CooldownMs = 10000, DpsPermille = 1000, IntervalMs = 1000, BlendMs = 250, ReleaseOn = "none" },
        };

        public static readonly S11PushRow[] PushTable =
        {
            // 5, x 2.5/2, x 1.8/1.5)
            new S11PushRow { Creature = "ReaperLeviathan", TechType = 2540, FactorPermille = 1000, MaxDvCmS = 600 }, //: x2.5 (720 x 2.5/3)
            new S11PushRow { Creature = "GhostLeviathan", TechType = 2562, FactorPermille = 1000, MaxDvCmS = 1000 },
            new S11PushRow { Creature = "GhostLeviathanJuvenile", TechType = 2565, FactorPermille = 1000, MaxDvCmS = 480 },
            new S11PushRow { Creature = "SeaDragon", TechType = 2553, FactorPermille = 1000, MaxDvCmS = 1000 },
        };

        /// <summary>

 /// Seamoth). The creature's root transform is set to Factor × one (absolute, so a saved and reloaded creature never
 /// compounds); its colliders, mouth trigger, attach point and animation follow the transform, its LiveMixin health does
 /// not. BiteMult multiplies its Normal hits on SE grids (SE side, never on the character), bites and the grab's
 /// per-second hold damage alike.
 /// 15 SN_HP x 2.5 = 37.5 per hold second, 80 x 2.5 = 200 per bite, on a SmallBlockDrill of 4,381): the multiplier is its
 /// own per-creature value, not the size factor, sized so a bite takes a share of a typical functional small-grid block
 /// (2,000-5,000 integrity, S6 k = 1): the reaper's 80 x 11 = 880 (a quarter), the ghost's 85 x 14 = 1,190 (a third),
 /// the juvenile's 85 x 7 = 595 (a sixth), the sea dragon's bite
 /// x 5.8 = 1,740 (a half). A large grid's k = 25
 /// keeps the same share of its blocks.s11 scale</c> and <c>de.s11 bite</c>.
 /// </summary>
    public static readonly S11ScaleRow[] ScaleTable =
    {
        new S11ScaleRow { Creature = "ReaperLeviathan", TechType = 2540, FactorPermille = 2500, BiteMultPermille = 11000 }, // 5 would be better" (x3 clipped the ground)
        new S11ScaleRow { Creature = "GhostLeviathan", TechType = 2562, FactorPermille = 2500, BiteMultPermille = 14000 },
        new S11ScaleRow { Creature = "GhostLeviathanJuvenile", TechType = 2565, FactorPermille = 1800, BiteMultPermille = 7000 },
        new S11ScaleRow { Creature = "SeaDragon", TechType = 2553, FactorPermille = 2500, BiteMultPermille = 5800 },
    };

    /// <summary>
 /// (format rage.hold): Reaper 15 % of its maxHealth (5,000: 750 SN_HP, a rocket and a half at 500), Sea Dragon
 /// 20 %; below it the creature gets angrier once per hold (x1.5 hold damage, +4 s / +2 s). Defaults
 /// them live with <c>de.s11 {action: rage,...}</c> (Subnautica), which never changes the pin.
 /// </summary>
    public static readonly S11RageRow[] RageTable =
    {

        new S11RageRow { Creature = "ReaperLeviathan", TechType = 2540, ReleasePermille = 300, AngerPermille = 1500, ExtendMs = 4000, FleeMs = 10000 },
        new S11RageRow { Creature = "SeaDragon", TechType = 2553, ReleasePermille = 350, AngerPermille = 1500, ExtendMs = 2000, FleeMs = 10000 },
    };

    /// <summary>
 /// a reaper seen hanging in the air beside his flying
 /// ship): creatures never leave the water with a grid (format surface.release). Subnautica's own creatures stay below by
 /// physics: Locomotion pushes only below y = 0 and WorldForces applies its above-water gravity
 /// FleeOnDamage clamps its flee target to the ocean level.
 /// </summary>
    public static class Surface
    {
        /// <summary>
 /// Review 4: the depths scale with the bodies (a x3 reaper is ~10 m tall): a hold lets go when the top of the
 /// creature's or the grid's bounds comes within this of the surface; a following creature is steered at least its own
 /// half height plus this deep.
 /// </summary>
        public const int MarginCm = 200;
    }

    /// <summary>

 /// or at their range, and the creatures are not in
 /// SE: Subnautica tests each mirrored missile against its creatures and SE explodes it there (format missile.hit).
 /// </summary>
    public static class Missile
    {
        /// <summary>A missile's first seen pose: an overlap of this radius (a missile born inside a creature, e.g. from a held ship).</summary>
        public const int HitRadiusCm = 100;
        /// <summary>A missile's first seen pose: a cast this far back along -forward (the flight before its mirror existed).</summary>
        public const int BackCastCm = 3000;
        /// <summary>
 /// A health-pool slug (railgun, artillery: explosion damage 0) deals HealthPool x this per mille as a yield. Review note
 ///: 10 % of a large slug's 50,000 was a reaper's whole 5,000; 2 % = 1,000 (a large slug), 160 (a small one).
 /// </summary>
        public const int SlugPermille = 20;
        /// <summary>Review 6: at a missile's second pose the cast behind its first reaches speed x this (a 2,000 m/s slug: 200 m).</summary>
        public const int BackCastMs = 100;
        /// <summary>c8.s11missile snDamageType: a hit on the body, or on the creature that holds the grid the missile left.</summary>
        public const int HitBody = 0, HitHolder = 1;
        /// <summary>The radius of a slug's synthetic Explosion row.</summary>
        public const int SlugRadiusCm = 300;
        /// <summary>The struck creature takes the missile's blast at d = 0 when it arrives within this long of the hit.</summary>
        public const int AttributionMs = 1500;
    }

    /// <summary>The SE hold of a grabbed grid (a spring-damper force, never a teleport; SE owns the physics).</summary>
        public static class Hold
        {
            public const int OmegaMilli = 6000;
            public const int ZetaPermille = 1000;
            public const int MaxAccelCmS2 = 15000;
            public const int AngOmegaMilli = 4000;
            public const int MaxAngAccelMilli = 12000;
            /// <summary>No hold record for this long: SE lets go (a lost release record never holds a grid forever).</summary>
            public const int TimeoutMs = 1000;
            /// <summary>
 /// Subnautica sends the attach pose this often while holding (catch-up: every frame at 60 fps). S11b
 /// 20261024-030926: at 50 ms (17 records/s arrived) the forced grab's 1 -> 40 m/s step left 2.95 m = 50 m/s x 59 ms,
 /// the jump between two records; at 20 ms it is about a frame (0.8 m at 50 m/s).
 /// </summary>
            public const int RecordIntervalMs = 20;
            /// <summary>
 /// S11b 20261024-000956 (the bite anchor and a 450 m/s2 cap still trailed 27 m: the x3 reaper's attach point moved at
 /// 40 m/s and the 12.5 t wall broke on the cap's forces): the hold is kinematic (<c>format hold.kinematic</c>).
 /// </summary>
            public const int SnapCm = 100;
            public const int ServoKpMilli = 10000;
            public const int ServoKRotMilli = 8000;
            public const int MaxAngVelMilli = 6000;
            public const int LeadMaxMs = 100;
            /// <summary>
 /// A mouth faster than this between two records is a teleport (no velocity). 20261024-030926: at 50 m/s (the old
 /// guard) the jitter of the arrival times pushed the 40 m/s segment's estimates to 49.6 m/s and over, zeroing the
 /// lead; the velocity now uses Subnautica's own emit times (DamageRecord.snTimeSec).
 /// </summary>
            public const int MaxMouthSpeedCmS = 15000;
            /// <summary>Review 1: at a release the grid keeps at most this speed (the kinematic hold set the mouth's, up to 150 m/s).</summary>
            public const int ReleaseSpeedCmS = 1000;
        }

        public static class GrabPhase
        {
            public const int Start = 1, Hold = 2, Release = 3;
            public static string Name(int p) { return p == Start ? "start" : p == Hold ? "hold" : p == Release ? "release" : "phase" + p.ToString(CultureInfo.InvariantCulture); }
        }

        public static readonly S11HazardRow[] HazardTable =
        {
            new S11HazardRow { Name = "Heat", SnType = 1, SeType = "DeepHeat", KPermille = 1000, CapPerS = 50, Effect = "heat" },
            new S11HazardRow { Name = "Poison", SnType = 5, SeType = "DeepPoison", KPermille = 1000, CapPerS = 30, Effect = "poison" },
            new S11HazardRow { Name = "Acid", SnType = 6, SeType = "DeepAcid", KPermille = 1000, CapPerS = 30, Effect = "acid" },
            new S11HazardRow { Name = "Cold", SnType = 10, SeType = "DeepCold", KPermille = 1000, CapPerS = 30, Effect = "cold" },
            new S11HazardRow { Name = "Fire", SnType = 14, SeType = "DeepFire", KPermille = 1000, CapPerS = 50, Effect = "fire" },
            new S11HazardRow { Name = "Smoke", SnType = 16, SeType = "DeepSmoke", KPermille = 1000, CapPerS = 10, Effect = "smoke" },
        };

        public static class Heal
        {
            public const int KPermille = 1000;
            public const int CapPerS = 100;
            public const int MaxRecordHp = 500;
        }

        public static class Oxygen
        {
            /// <summary>Subnautica's diver tank without equipment (S2b SwimModel.OxygenCapacity 45 s).</summary>
            public const int TankS = 45;
            /// <summary>OxygenManager.oxygenUnitsPerSecondSurface: where the diver can breathe (a powered base, the surface).</summary>
            public const int BaseRefillPerS = 30;
            public const int MaxRecordS = 120;
        }

        public static class OxygenSource
        {
            public const int Bubble = 1, Area = 2, Base = 3, Other = 4;
            public static string Name(int s) { return s == Bubble ? "bubble" : s == Area ? "area" : s == Base ? "base" : s == Other ? "other" : "source" + s.ToString(CultureInfo.InvariantCulture); }
        }

        public static class Emp
        {
            /// <summary>The grid's power producers stay off for EMPBlast.disableElectronicsTime × this per mille.</summary>
            public const int SecondsPermille = 1000;
            public const int MinS = 1, MaxS = 30;
        }

        public static class Lava
        {
            /// <summary>TemperatureDamage.ApplyLavaDamage: 2.5 SN_HP Heat every 0.1 s while touching lava (25/s).</summary>
            public const int ProbeIntervalMs = 100;
            public const int HeatMilliHpPerProbe = 2500;
            /// <summary>The probe reaches this far from the character capsule's surface (cm).</summary>
            public const int ReachCm = 40;
        }

        public static class Limits
        {
            public const int MaxHazardHp = 500;
            public const int MaxPushNs = 10000000;
            public const int MaxEmpS = 60;
        }

        /// <summary>Pass 5: the C7 S11 block (SnStatus 0xC0-0xC3, inside pad_8c after the S16 block 0xBC-0xBF): Subnautica's damage FX state.</summary>
        public static class C7
        {
            public const int Offset = 0xC0, Size = 4, Version = 1;
        }

        public static class C7Flags
        {
            /// <summary>Subnautica's DamageFX.main and at least one DamageScreenFX on its camera were found (its damage FX can play).</summary>
            public const int DamageFxOk = 1;
            /// <summary>A damage FX played within the last HitFadeMs.</summary>
            public const int DamageFxShown = 2;
        }

        /// <summary>

 /// (<c>MyGuiControlBloodOverlay</c> draws <c>MyRenderComponentCharacter.GetHUDBloodAlpha</c>) rises to 0.8 on any
 /// health drop, however small (a 0.2 HP heat tick each second keeps it up), decays only in the character render
 /// component's Draw, and stays at 0.3-1.3 below 20 % health. S11 sets it from the last drop instead (<c>format hud.blood</c>).
 /// </summary>
        public static class Hud
        {
            public const int HitFadeMs = 800;
            public const int FullHitHp = 10;
            public const int MinAlphaPermille = 200;
            public const int MaxAlphaPermille = 800;
        }

        /// <summary>

 /// piece of metal block)"): in his session the reaper grabbed a 4-small-block split fragment (1 x 1 x 0.5 m) and a
 /// single large block (2.5 m cube); every split piece of the session had 1-4 blocks, his ship 149-150 and the base 477.
 /// A grid with fewer blocks or a smaller box is debris (<c>format debris.grid</c>).
 /// </summary>
        public static class Debris
        {
            public const int MinBlocks = 8;
            public const int MinVolumeM3 = 8;
        }

        // ---------------------------------------------------------------------------------------------- lookups

        public static S11WeaponRow? WeaponRow(int cls, int tier)
        {
            foreach (S11WeaponRow r in WeaponTable) if (r.Class == cls && r.Tier == tier) return r;
            return null;
        }

        public static bool TryGrabRow(string creature, out S11GrabRow row)
        {
            foreach (S11GrabRow r in GrabTable) if (string.Equals(r.Creature, creature, StringComparison.OrdinalIgnoreCase)) { row = r; return true; }
            row = default(S11GrabRow);
            return false;
        }

        public static bool TryGrabRow(int techType, out S11GrabRow row)
        {
            foreach (S11GrabRow r in GrabTable) if (r.TechType == techType) { row = r; return true; }
            row = default(S11GrabRow);
            return false;
        }

        public static bool TryPushRow(int techType, out S11PushRow row)
        {
            foreach (S11PushRow r in PushTable) if (r.TechType == techType) { row = r; return true; }
            row = default(S11PushRow);
            return false;
        }

        public static bool TryPushRow(string creature, out S11PushRow row)
        {
            foreach (S11PushRow r in PushTable) if (string.Equals(r.Creature, creature, StringComparison.OrdinalIgnoreCase)) { row = r; return true; }
            row = default(S11PushRow);
            return false;
        }

        public static bool TryScaleRow(string creature, out S11ScaleRow row)
        {
            foreach (S11ScaleRow r in ScaleTable) if (string.Equals(r.Creature, creature, StringComparison.OrdinalIgnoreCase)) { row = r; return true; }
            row = default(S11ScaleRow);
            return false;
        }

        public static bool TryScaleRow(int techType, out S11ScaleRow row)
        {
            foreach (S11ScaleRow r in ScaleTable) if (r.TechType == techType) { row = r; return true; }
            row = default(S11ScaleRow);
            return false;
        }

        public static bool TryRageRow(int techType, out S11RageRow row)
        {
            foreach (S11RageRow r in RageTable) if (r.TechType == techType) { row = r; return true; }
            row = default(S11RageRow);
            return false;
        }

        public static bool TryRageRow(string creature, out S11RageRow row)
        {
            foreach (S11RageRow r in RageTable) if (string.Equals(r.Creature, creature, StringComparison.OrdinalIgnoreCase)) { row = r; return true; }
            row = default(S11RageRow);
            return false;
        }

        public static bool TryHazardRow(int snType, out S11HazardRow row)
        {
            foreach (S11HazardRow r in HazardTable) if (r.SnType == snType) { row = r; return true; }
            row = default(S11HazardRow);
            return false;
        }

        // ---------------------------------------------------------------------------------------------- canonical text

        public static readonly string[] FormatLines =
        {
            "blast.damage hp = yield * mult(Explosion, tier) * max(0, (R - d) / R), d = the distance to the nearest point of the creature's own colliders in the overlap (Collider.ClosestPoint; a non-convex mesh collider by its bounds; its LiveMixin transform without one; 0 for the creature a missile struck, missile.hit), R = min(cluster R, DamageRadiusCapM)",
            "blast.group join when |c - C| <= max(R, r) + JoinPadM; R = cbrt(sum r^3); C = yield-weighted centre; yield = sum",
            "c8.s11 unit HazardHp|HealHp: amount SN_HP, kind 2 character; OxygenS: amount SN seconds, snDamageType = OxygenSource; EmpS: amount seconds, kind 1 grid",
            "c8.s11grab unit Grab: snDamageType = GrabPhase, snWorldHit = attach point, attackDirSn = attach forward, localHit = attach up, dealerInstanceId = grab key, amount = planned hold s (start) else 1",
            "c8.s11push unit Push: amount = impulse N*s, attackDirSn = push direction (SN world), snWorldHit = bite point",
            "danger.applied min(base * mult, max(0, health - floor * maxHealth)); tier by LiveMixin.maxHealth read at run time",
            "event.class Explosion: subtype bits 8-11 = the WeaponClass of the origin (a character Handheld, a small grid SmallGrid, a warhead, a large grid or none Explosion); 0 = absent (Explosion); groups never mix classes",
            "event.yield Explosion: extra = clamp(round(yield / YieldUnitSe), 1, 65535); 0 = absent (yield = intensity * 1000)",
            "hold.force (S11.M12 only, the pre-kinematic rule) m*(clamp(w^2*(xt - x) + 2*z*w*(vt - v), aMax) - g) at the centre of mass, x and v = the bite anchor and its velocity, aMax = MaxAccelCmS2 / 100 * max(1, scale); -F_buoy*(1 - holdShare) at the centre of buoyancy",
            "hold.kinematic every SE tick: xt = the last attach point (the jaw centre: seamothAttachPoint / exosuitAttachPoint, where Subnautica holds a Seamoth or an Exosuit) + its velocity (between the last two records by Subnautica's emit times snTimeSec, else their arrival; over MaxMouthSpeedCmS none) * min(age, LeadMaxMs); the anchor = the grid's centre of bounds (PositionComp.LocalAABB centre, grid-local); the target xb = a + (xt - a) * f, a = the anchor at the start, f = min(1, held / blendMs of the grab row), vb = its rate; the orientation = attach * slerp(relative-at-start, aligned, f), aligned = the grid's longest box axis on the attach forward and its shortest on the attach up (signs nearest the start); the error = |xb - anchor|; over SnapCm the grid's matrix is set (anchor on xb); then the anchor's velocity = vb + (xb - anchor) * ServoKpMilli / 1000, the angular velocity = clamp(rotation vector of (target * current^-1) * ServoKRotMilli / 1000, MaxAngVelMilli / 1000), both set on the root grid (no force: SE's physics owns the rest); a grid with mechanical subgrids is never held (Subgrids); at any release the root grid's linear speed is capped at ReleaseSpeedCmS, its angular velocity zeroed and, at a surface release, its upward component removed; while it holds, Subnautica plays the creature's animation at 1 / its scale (the jaw swings at the unscaled creature's speed)",
            "oxygen.litres SE litres = seconds / TankS * suit OxygenCapacity, capped at the capacity",
            "push.dv min(impulse / grid mass, MaxDvCmS / 100) along the push direction at the bite point; impulse = mass * scale^3 * closing * factor",
            "scale.creature root localScale = factor * one (absolute; Creature.Start resets a non-growing creature to one first); BiteMult (its own value, not the factor) on Normal hits on SE grids, bites and hold damage",
            "debris.grid a grid is debris (never hunted, bitten, grabbed or pushed: no EcoTarget, no bite, no grab start, no push) when blocks < MinBlocks or its box volume < MinVolumeM3 m3; SE counts MyCubeGrid.BlocksCount, Subnautica estimates blocks = sum of its skin box volumes / block edge^3 (0.5 m small, 2.5 m large; unknown: the volume alone); the character is never debris",
            "c7.s11 SnStatus 0xc0-0xc3 written by Subnautica every frame: version, flags (DamageFxOk, DamageFxShown), 2 reserved; version 0 = absent (SE keeps its own overlay)",
            "hud.blood SE blood alpha: the game's own while dead; the per-hit pulse = clamp(drop / FullHitHp, Min, Max) * (1 - dt / HitFadeMs) after the last health drop, 0 from HitFadeMs on (0 under Subnautica's picture with a fresh c7.s11 DamageFxOk); the game's low-health floor (health ratio <= 0.2: (0.2 - ratio) / 0.2 + 0.3) kept: max(pulse, floor)",
            "c8.s11missile unit MissileHit: kind 1, targetEntityId = rootEntityId = the missile's SE entity id, snWorldHit = the hit point, attackDirSn = the flight direction, dealerTechType and dealerInstanceId = the struck creature, amount 1, snDamageType = HitBody 0 or HitHolder 1 (the missile left the grid that creature holds)",
            "missile.hit Subnautica sweeps every mirrored missile (GridAdd Missile) each frame from its last pose to this one (Physics.DefaultRaycastLayers, triggers ignored; its first pose also by an overlap of HitRadiusCm and a cast of BackCastCm along -forward, its second by a cast of speed * BackCastMs behind the first): the nearest collider of a live creature (LiveMixin + Creature) before any other solid (the ghost Player passes) is a hit, one MissileHit record per missile; a holder's jaw within the held grid's reach of the first flight is a HitHolder; SE refuses a hit more than 1 m behind the missile's launch point (MyMissile.Origin, Behind); a HitBody moves the missile to the hit point and explodes it (MyMissile.Explode: its own Explosion row); a HitHolder explodes it where it is with its damage set to 0 (the player's hull untouched) and raises one synthetic Explosion row of its damage at the hit point; a health-pool slug (HasHealthPool) raises one synthetic Explosion row of yield HealthPool * SlugPermille / 1000, radius SlugRadiusCm / 100, its launcher's class; the struck creature takes that blast at d = 0 (its hit within AttributionMs and JoinPadM of the centre)",
            "rage.hold during a hold the SN_HP the holder takes from SE weapons (danger.applied) accumulates; at ReleasePermille / 1000 * maxHealth it lets go (a release record) and flees for FleeMs (Scared + 1, Aggression held at 0, LastTarget cleared, swims away kept under the surface.release follow depth, no grab), once per hold; the first lower hit makes it angrier, once per hold: hold damage * AngerPermille / 1000, the planned hold + ExtendMs, Aggression + 1; retuned live (de.s11 rage), never the pin",
            "surface.release depths scale with the bodies: a creature's top is the higher of its root and its head (the attach point) plus its girth (half its thinnest collider-bounds extent); while it holds a grid it lets go when its top or the held grid's comes within MarginCm / 100 m of the surface (Subnautica: SN y = the altitude above the Water Mod sea sphere; SE: the Water Mod GetDepth at the top of the grid's bounds; SE then drops that grab key's later hold records); no grab starts there; a creature that holds, flees or hunts an SE grid section (LastTarget) is steered no higher than -(its girth + MarginCm / 100) m at SplineFollowing.GoTo (the last door of SwimTo, Attack and Overshoot), and at every physics step (Locomotion.ManagedFixedUpdate) loses its upward speed while its top is within MarginCm / 100 m of the surface; nothing of S11 ever drives it upward",
            "fx.sn under Subnautica's picture a hazard record or a bite on the character plays DamageFX.AddHudDamage(hp / 100, point, type) at most every 250 ms per type; its splatters move under the S16 overlay canvas; leaving the picture clears them",
        };

        private static string _text;
        private static ulong _hash;

        private static void C(StringBuilder sb, string group, string name, long v)
        {
            sb.Append("const ").Append(group).Append(' ').Append(name).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        /// <summary>
 /// The canonical text (CONTRACT §5.4 "S11 conventions"; ASCII, '\n' after every line, the last included), in the
 /// order written here: the header, the const groups, the table rows (weapon, grab, push, hazard), the format lines
 /// sorted by key. The Python twin writes the same bytes from its own copy of the tables.
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            var sb = new StringBuilder(6000);
            sb.Append(Header).Append('\n');
            C(sb, "S11Unit", "HazardHp", Unit.HazardHp);
            C(sb, "S11Unit", "HealHp", Unit.HealHp);
            C(sb, "S11Unit", "OxygenS", Unit.OxygenS);
            C(sb, "S11Unit", "EmpS", Unit.EmpS);
            C(sb, "S11Unit", "Grab", Unit.Grab);
            C(sb, "S11Unit", "Push", Unit.Push);
            C(sb, "S11Unit", "MissileHit", Unit.MissileHit);
            C(sb, "S11Flags", "ViaSampler", Flags.ViaSampler);
            C(sb, "S11Flags", "ViaSection", Flags.ViaSection);
            for (int c = 1; c <= 4; c++) C(sb, "WeaponClass", WeaponClass.Names[c], c);
            for (int t = 1; t <= 3; t++) C(sb, "CreatureTier", Tier.Names[t], t);
            C(sb, "CreatureTierMinHp", "Large", Tier.LargeMinHp);
            C(sb, "CreatureTierMinHp", "Leviathan", Tier.LeviathanMinHp);
            foreach (S11WeaponRow r in WeaponTable)
                sb.Append("weapon ").Append(WeaponClass.Name(r.Class)).Append(' ').Append(Tier.Name(r.Tier))
                  .Append(" multPermille ").Append(r.MultPermille.ToString(CultureInfo.InvariantCulture))
                  .Append(" floorPermille ").Append(r.FloorPermille.ToString(CultureInfo.InvariantCulture)).Append('\n');
            C(sb, "Blast", "YieldUnitSe", Blast.YieldUnitSe);
            C(sb, "Blast", "JoinPadM", Blast.JoinPadM);
            C(sb, "Blast", "ChainWindowMs", Blast.ChainWindowMs);
            C(sb, "Blast", "VisualRadiusCapM", Blast.VisualRadiusCapM);
            C(sb, "Blast", "DamageRadiusCapM", Blast.DamageRadiusCapM);
            C(sb, "Blast", "MaxGroupsPerTick", Blast.MaxGroupsPerTick);
            C(sb, "Blast", "ClassShift", Blast.ClassShift);
            foreach (S11GrabRow g in GrabTable)
                sb.Append("grab ").Append(g.Creature).Append(' ').Append(g.TechType.ToString(CultureInfo.InvariantCulture)).Append(" attach ").Append(g.AttachField).Append(" dps ").Append(g.DpsField)
                  .Append(" maxVolumeM3 ").Append(g.MaxVolumeM3.ToString(CultureInfo.InvariantCulture))
                  .Append(" maxEdgeDm ").Append(g.MaxEdgeDm.ToString(CultureInfo.InvariantCulture))
                  .Append(" holdMinMs ").Append(g.HoldMinMs.ToString(CultureInfo.InvariantCulture))
                  .Append(" holdRandMs ").Append(g.HoldRandMs.ToString(CultureInfo.InvariantCulture))
                  .Append(" cooldownMs ").Append(g.CooldownMs.ToString(CultureInfo.InvariantCulture))
                  .Append(" dpsPermille ").Append(g.DpsPermille.ToString(CultureInfo.InvariantCulture))
                  .Append(" intervalMs ").Append(g.IntervalMs.ToString(CultureInfo.InvariantCulture))
                  .Append(" blendMs ").Append(g.BlendMs.ToString(CultureInfo.InvariantCulture))
                  .Append(" release ").Append(g.ReleaseOn).Append('\n');
            foreach (S11PushRow p in PushTable)
                sb.Append("push ").Append(p.Creature).Append(' ').Append(p.TechType.ToString(CultureInfo.InvariantCulture)).Append(" factorPermille ").Append(p.FactorPermille.ToString(CultureInfo.InvariantCulture))
                  .Append(" maxDvCmS ").Append(p.MaxDvCmS.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (S11ScaleRow r in ScaleTable)
                sb.Append("scale ").Append(r.Creature).Append(' ').Append(r.TechType.ToString(CultureInfo.InvariantCulture))
                  .Append(" factorPermille ").Append(r.FactorPermille.ToString(CultureInfo.InvariantCulture))
                  .Append(" biteMultPermille ").Append(r.BiteMultPermille.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (S11RageRow r in RageTable)
                sb.Append("rage ").Append(r.Creature).Append(' ').Append(r.TechType.ToString(CultureInfo.InvariantCulture))
                  .Append(" releasePermille ").Append(r.ReleasePermille.ToString(CultureInfo.InvariantCulture))
                  .Append(" angerPermille ").Append(r.AngerPermille.ToString(CultureInfo.InvariantCulture))
                  .Append(" extendMs ").Append(r.ExtendMs.ToString(CultureInfo.InvariantCulture))
                  .Append(" fleeMs ").Append(r.FleeMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
            C(sb, "Hold", "OmegaMilli", Hold.OmegaMilli);
            C(sb, "Hold", "ZetaPermille", Hold.ZetaPermille);
            C(sb, "Hold", "MaxAccelCmS2", Hold.MaxAccelCmS2);
            C(sb, "Hold", "AngOmegaMilli", Hold.AngOmegaMilli);
            C(sb, "Hold", "MaxAngAccelMilli", Hold.MaxAngAccelMilli);
            C(sb, "Hold", "TimeoutMs", Hold.TimeoutMs);
            C(sb, "Hold", "RecordIntervalMs", Hold.RecordIntervalMs);
            C(sb, "Hold", "SnapCm", Hold.SnapCm);
            C(sb, "Hold", "ServoKpMilli", Hold.ServoKpMilli);
            C(sb, "Hold", "ServoKRotMilli", Hold.ServoKRotMilli);
            C(sb, "Hold", "MaxAngVelMilli", Hold.MaxAngVelMilli);
            C(sb, "Hold", "LeadMaxMs", Hold.LeadMaxMs);
            C(sb, "Hold", "MaxMouthSpeedCmS", Hold.MaxMouthSpeedCmS);
            C(sb, "Hold", "ReleaseSpeedCmS", Hold.ReleaseSpeedCmS);
            C(sb, "GrabPhase", "Start", GrabPhase.Start);
            C(sb, "GrabPhase", "Hold", GrabPhase.Hold);
            C(sb, "GrabPhase", "Release", GrabPhase.Release);
            foreach (S11HazardRow h in HazardTable)
                sb.Append("hazard ").Append(h.Name).Append(' ').Append(h.SnType.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(h.SeType)
                  .Append(" kPermille ").Append(h.KPermille.ToString(CultureInfo.InvariantCulture))
                  .Append(" capPerS ").Append(h.CapPerS.ToString(CultureInfo.InvariantCulture))
                  .Append(" effect ").Append(h.Effect).Append('\n');
            C(sb, "Heal", "KPermille", Heal.KPermille);
            C(sb, "Heal", "CapPerS", Heal.CapPerS);
            C(sb, "Heal", "MaxRecordHp", Heal.MaxRecordHp);
            C(sb, "Oxygen", "TankS", Oxygen.TankS);
            C(sb, "Oxygen", "BaseRefillPerS", Oxygen.BaseRefillPerS);
            C(sb, "Oxygen", "MaxRecordS", Oxygen.MaxRecordS);
            C(sb, "OxygenSource", "Bubble", OxygenSource.Bubble);
            C(sb, "OxygenSource", "Area", OxygenSource.Area);
            C(sb, "OxygenSource", "Base", OxygenSource.Base);
            C(sb, "OxygenSource", "Other", OxygenSource.Other);
            C(sb, "Emp", "SecondsPermille", Emp.SecondsPermille);
            C(sb, "Emp", "MinS", Emp.MinS);
            C(sb, "Emp", "MaxS", Emp.MaxS);
            C(sb, "Lava", "ProbeIntervalMs", Lava.ProbeIntervalMs);
            C(sb, "Lava", "HeatMilliHpPerProbe", Lava.HeatMilliHpPerProbe);
            C(sb, "Lava", "ReachCm", Lava.ReachCm);
            C(sb, "Limits", "MaxHazardHp", Limits.MaxHazardHp);
            C(sb, "Limits", "MaxPushNs", Limits.MaxPushNs);
            C(sb, "Limits", "MaxEmpS", Limits.MaxEmpS);
            C(sb, "C7", "Offset", C7.Offset);
            C(sb, "C7", "Size", C7.Size);
            C(sb, "C7", "Version", C7.Version);
            C(sb, "C7Flags", "DamageFxOk", C7Flags.DamageFxOk);
            C(sb, "C7Flags", "DamageFxShown", C7Flags.DamageFxShown);
            C(sb, "Hud", "HitFadeMs", Hud.HitFadeMs);
            C(sb, "Hud", "FullHitHp", Hud.FullHitHp);
            C(sb, "Hud", "MinAlphaPermille", Hud.MinAlphaPermille);
            C(sb, "Hud", "MaxAlphaPermille", Hud.MaxAlphaPermille);
            C(sb, "Debris", "MinBlocks", Debris.MinBlocks);
            C(sb, "Debris", "MinVolumeM3", Debris.MinVolumeM3);
            C(sb, "Surface", "MarginCm", Surface.MarginCm);
            C(sb, "Missile", "HitRadiusCm", Missile.HitRadiusCm);
            C(sb, "Missile", "BackCastCm", Missile.BackCastCm);
            C(sb, "Missile", "SlugPermille", Missile.SlugPermille);
            C(sb, "Missile", "SlugRadiusCm", Missile.SlugRadiusCm);
            C(sb, "Missile", "AttributionMs", Missile.AttributionMs);
            C(sb, "Missile", "BackCastMs", Missile.BackCastMs);
            C(sb, "Missile", "HitBody", Missile.HitBody);
            C(sb, "Missile", "HitHolder", Missile.HitHolder);
            var formats = (string[])FormatLines.Clone();
            Array.Sort(formats, string.CompareOrdinal);
            foreach (string f in formats) sb.Append("format ").Append(f).Append('\n');
            _text = sb.ToString();
            return _text;
        }

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/>.</summary>
        public static ulong Hash()
        {
            if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
            return _hash;
        }
    }
}
