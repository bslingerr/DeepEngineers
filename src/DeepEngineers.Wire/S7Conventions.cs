using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7 "Interiors, lights, thrusters, audio polish, statics into SE, skinned astronaut": the meaning S7 gives to fields,
 // bits and region bytes that layout 1.0 carries but does not pin (CONTRACT §5.4 "S7 conventions"). Layout 1.0 is
 // unchanged (hash 0x4f3a35f6fc1eaf31): the C1 interior extension lives in the C1 pad 0xB0-0xBF (after the S5a swim and
 // S7a loco extensions), the light rows in the C2 light table (SeLight.flags bit 3 + version nibble, pad_3c = falloff),
 // the effect-event window in the C2 region tail 0x40000-0x4FFFF with its header in SeEntitiesHeader.pad_1c, RoomSet
 // flags in RoomSetHeader.pad_c, the C12 version in BatchStaticsHeader.pad_6 and StaticPrimitive.flags, ExtraBones in
 // CharacterAdd.flags bit 2. Pinned on their own by the canonical text below (FNV-1a 64 0x1f9b9b4a876a97f9, 143 lines,
 // 6,583 bytes), as S5, S6 and S7a pinned theirs. The text is built from the constants of this file, so a changed
 // constant changes the hash and turns S7ConventionsTests red. Python twin: tests/s7/analyze_s7.py --selftest.
 // =================================================================================================================

 /// <summary>The S7 conventions (CONTRACT §5.4 "S7 conventions"): one nested class per <c>const</c> group of the text.</summary>
    public static class S7Conventions
    {
        /// <summary>The first line of the canonical text (<c>ext s7 look &lt;n&gt;</c>).</summary>
        public const string Header = "ext s7 look 1";
        /// <summary>The <c>&lt;n&gt;</c> of <see cref="Header"/>.</summary>
        public const int ExtensionVersion = 1;
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4.</summary>
        public const ulong PinnedHash = 0x1f9b9b4a876a97f9UL;
        /// <summary>Lines and bytes of the canonical text.</summary>
        public const int CanonicalLines = 143, CanonicalBytes = 6583;

        /// <summary>Bone rows (C1, SkinnedBind, CharacterAdd) with <see cref="CharacterFlagsExt.ExtraBones"/>.</summary>
        public static class BoneLimits
        {
            public const int AstronautBones = 81;
            public const int MaxBones = 160;
        }

        /// <summary>C12 <c>BatchStaticsHeader.pad_6</c> = u16 version.</summary>
        public static class C12Ext
        {
            public const int Version = 1;
        }

        /// <summary>The C1 interior extension's place in the C1 pad (<c>pad_94</c>, after the loco extension 0xA0-0xAF).</summary>
        public static class C1Interior
        {
            public const int Version = 1;
            public const int Size = 16;
            public const int Offset = 0xB0;
        }

        /// <summary>C1 interior <c>flags</c> (byte 0xB1).</summary>
        public static class C1InteriorFlags
        {
            /// <summary>The camera's cell is in an airtight room (= C1 flags bit 3).</summary>
            public const byte InRoom = 1;
            /// <summary>The world has <c>EnableOxygen</c> and <c>EnableOxygenPressurization</c>.</summary>
            public const byte Pressurization = 2;
            /// <summary>The camera grid's gas system is recomputing: <see cref="InRoom"/> holds its last value.</summary>
            public const byte Processing = 4;
            /// <summary>The camera cell is in a room of a mirrored grid that is not airtight.</summary>
            public const byte Breached = 8;
            /// <summary>The controlled character's eye cell is in an airtight room (the swim gate).</summary>
            public const byte CharacterInRoom = 16;
        }

        /// <summary>The C2 effect-event window in the C2 region tail.</summary>
        public static class C2Events
        {
            public const int WindowTicks = 8;
            public const int RowSize = 64;
            public const int MaxPerTick = 128;
            public const int MaxRows = 1024;
            /// <summary>C2 region offset of the first event row (0x40000).</summary>
            public const int Offset = 0x40000;
        }

        /// <summary>The C2 header extension in <c>SeEntitiesHeader.pad_1c</c> (0x1C-0x3F).</summary>
        public static class C2Ext
        {
            public const int Version = 1;
            public const int Offset = 0x1C;
            public const int Size = 36;
        }

        /// <summary>C2 header extension <c>flags</c> (byte 0x1D).</summary>
        public static class C2ExtFlags
        {
            /// <summary>The light rows carry S7 meaning.</summary>
            public const byte Lights = 1;
            /// <summary>The event window is written.</summary>
            public const byte Events = 2;
        }

        /// <summary><c>SeLight.flags</c> bits beyond <see cref="SeLightFlags"/> (bits 0-2) and the version nibble (28-31).</summary>
        public static class C2LightFlagsExt
        {
            public const uint Character = 8u;
            public const uint Version1 = 0x10000000u;
            public const uint VersionMask = 0xF0000000u;
        }

        /// <summary><c>CharacterAdd.flags</c> bits beyond <see cref="MirrorCharacterFlags"/> (bits 0-1, nibble 28-31).</summary>
        public static class CharacterFlagsExt
        {
            public const uint ExtraBones = 4u;
        }

        /// <summary>At most this many new events of a type per SE tick (CONTRACT §5.4 "Window and caps").</summary>
        public static class EventCaps
        {
            public const int Drill = 8, Explosion = 8, Grinder = 8, HullBreak = 8, PrawnDrill = 8, Repair = 8, Welder = 8;
            public const int GridImpact = 16, Splash = 16, WeaponMuzzle = 16;
            public const int BulletStreak = 32, Impact = 32;
            public const int Thruster = 64;

            /// <summary>The cap of an <see cref="EventType"/> value; 0 for an unknown type (never accepted).</summary>
            public static int Of(int type)
            {
                switch (type)
                {
                    case EventType.Thruster: return Thruster;
                    case EventType.WeaponMuzzle: return WeaponMuzzle;
                    case EventType.BulletStreak: return BulletStreak;
                    case EventType.Impact: return Impact;
                    case EventType.Explosion: return Explosion;
                    case EventType.Welder: return Welder;
                    case EventType.Grinder: return Grinder;
                    case EventType.Drill: return Drill;
                    case EventType.Repair: return Repair;
                    case EventType.PrawnDrill: return PrawnDrill;
                    case EventType.GridImpact: return GridImpact;
                    case EventType.HullBreak: return HullBreak;
                    case EventType.Splash: return Splash;
                    default: return 0;
                }
            }
        }

        /// <summary>The event row's <c>flags</c> (byte 0x05).</summary>
        public static class EventFlags
        {
            public const byte Attached = 1, Sustain = 2, Stop = 4, Underwater = 8, Surface = 16, Hit = 32, Local = 64;
        }

        /// <summary>Event limits (radii in metres, speeds in cm/s, steps in permille).</summary>
        public static class EventLimits
        {
            public const int SustainRefreshTicks = 15;
            public const int SurfaceBandCm = 50;
            public const int SustainStepPermille = 50;
            public const int StreakMaxM = 60;
            public const int SplashMinSpeedCmS = 200;
            public const int EffectRadiusM = 250;
            public const int SustainLifetimeMs = 500;
            public const int ExplosionRadiusM = 1000;
        }

        /// <summary>The event row's <c>type</c> (byte 0x04).</summary>
        public static class EventType
        {
            public const byte Thruster = 1;
            public const byte WeaponMuzzle = 2;
            public const byte BulletStreak = 3;
            public const byte Impact = 4;
            public const byte Explosion = 5;
            /// <summary>The ship welder.</summary>
            public const byte Welder = 6;
            /// <summary>The hand or ship grinder.</summary>
            public const byte Grinder = 7;
            /// <summary>The hand drill.</summary>
            public const byte Drill = 8;
            /// <summary>The hand welder, shown as Subnautica's repair tool.</summary>
            public const byte Repair = 9;
            /// <summary>The ship drill, shown as the Prawn drill arm.</summary>
            public const byte PrawnDrill = 10;
            public const byte GridImpact = 11;
            public const byte HullBreak = 12;
            public const byte Splash = 13;

            /// <summary>True for 1..13.</summary>
            public static bool IsValid(int type) { return type >= Thruster && type <= Splash; }

            public static string Name(int type)
            {
                switch (type)
                {
                    case Thruster: return "Thruster";
                    case WeaponMuzzle: return "WeaponMuzzle";
                    case BulletStreak: return "BulletStreak";
                    case Impact: return "Impact";
                    case Explosion: return "Explosion";
                    case Welder: return "Welder";
                    case Grinder: return "Grinder";
                    case Drill: return "Drill";
                    case Repair: return "Repair";
                    case PrawnDrill: return "PrawnDrill";
                    case GridImpact: return "GridImpact";
                    case HullBreak: return "HullBreak";
                    case Splash: return "Splash";
                    default: return "Unknown(" + type.ToString(CultureInfo.InvariantCulture) + ")";
                }
            }
        }

        /// <summary>The class of a mirrored chunk against its entity's room boxes (<c>format interior.class</c>).</summary>
        public static class InteriorClass
        {
            public const byte Exterior = 0, Boundary = 1, Inside = 2;

            public static string Name(int c)
            {
                switch (c)
                {
                    case Exterior: return "exterior";
                    case Boundary: return "boundary";
                    case Inside: return "inside";
                    default: return "unknown(" + c.ToString(CultureInfo.InvariantCulture) + ")";
                }
            }
        }

        /// <summary>The light table (C2 lights, Subnautica pool).</summary>
        public static class LightLimits
        {
            public const int MaxShadowedSpots = 2;
            public const int RadiusM = 60;
            public const int MaxLights = 64;
        }

        /// <summary>The RoomSet box count above which a grid is sent as one box per room with <see cref="RoomSetFlags.Truncated"/>.</summary>
        public static class RoomLimits
        {
            public const int MaxBoxes = 4096;
        }

        /// <summary><c>RoomSetHeader.pad_c</c> = u32 flags.</summary>
        public static class RoomSetFlags
        {
            public const uint Truncated = 1u;
            public const uint Version1 = 0x10000000u;
        }

        /// <summary><c>StaticPrimitive.flags</c> (byte 0x01).</summary>
        public static class StaticFlags
        {
            public const byte Census = 1, AllowListed = 2, SkewApprox = 4;
        }

        /// <summary>The statics stream (metres, milliseconds, primitive counts).</summary>
        public static class StaticsLimits
        {
            public const int BatchM = 160;
            public const int CreateRadiusM = 300;
            public const int ScanRadiusM = 320;
            public const int CloseRadiusM = 350;
            public const int MaxPerHost = 1024;
            public const int MinRescanMs = 2000;
            public const int MaxPerBatch = 4096;
        }

        /// <summary>Thruster event <c>subtype</c>.</summary>
        public static class ThrusterKind
        {
            public const ushort Other = 0, Ion = 1, Atmospheric = 2, Hydrogen = 3;
        }

        /// <summary>Tool event <c>subtype</c>.</summary>
        public static class ToolSource
        {
            public const ushort Hand = 1, Ship = 2;
        }

        /// <summary>WeaponMuzzle event <c>subtype</c>.</summary>
        public static class WeaponKind
        {
            public const ushort Projectile = 1, Missile = 2;
        }

        // ------------------------------------------------------------------------------------------------ canonical text

        private struct ConstEntry
        {
            public readonly string Group, Name;
            public readonly ulong Value;
            public ConstEntry(string group, string name, ulong value) { Group = group; Name = name; Value = value; }
        }

        private static ConstEntry[] Entries()
        {
            return new[]
            {
                new ConstEntry("BoneLimits", "AstronautBones", BoneLimits.AstronautBones),
                new ConstEntry("BoneLimits", "MaxBones", BoneLimits.MaxBones),
                new ConstEntry("C12Ext", "Version", C12Ext.Version),
                new ConstEntry("C1Interior", "Version", C1Interior.Version),
                new ConstEntry("C1Interior", "Size", C1Interior.Size),
                new ConstEntry("C1Interior", "Offset", C1Interior.Offset),
                new ConstEntry("C1InteriorFlags", "InRoom", C1InteriorFlags.InRoom),
                new ConstEntry("C1InteriorFlags", "Pressurization", C1InteriorFlags.Pressurization),
                new ConstEntry("C1InteriorFlags", "Processing", C1InteriorFlags.Processing),
                new ConstEntry("C1InteriorFlags", "Breached", C1InteriorFlags.Breached),
                new ConstEntry("C1InteriorFlags", "CharacterInRoom", C1InteriorFlags.CharacterInRoom),
                new ConstEntry("C2Events", "WindowTicks", C2Events.WindowTicks),
                new ConstEntry("C2Events", "RowSize", C2Events.RowSize),
                new ConstEntry("C2Events", "MaxPerTick", C2Events.MaxPerTick),
                new ConstEntry("C2Events", "MaxRows", C2Events.MaxRows),
                new ConstEntry("C2Events", "Offset", C2Events.Offset),
                new ConstEntry("C2Ext", "Version", C2Ext.Version),
                new ConstEntry("C2Ext", "Offset", C2Ext.Offset),
                new ConstEntry("C2Ext", "Size", C2Ext.Size),
                new ConstEntry("C2ExtFlags", "Lights", C2ExtFlags.Lights),
                new ConstEntry("C2ExtFlags", "Events", C2ExtFlags.Events),
                new ConstEntry("C2LightFlagsExt", "Character", C2LightFlagsExt.Character),
                new ConstEntry("C2LightFlagsExt", "Version1", C2LightFlagsExt.Version1),
                new ConstEntry("C2LightFlagsExt", "VersionMask", C2LightFlagsExt.VersionMask),
                new ConstEntry("CharacterFlagsExt", "ExtraBones", CharacterFlagsExt.ExtraBones),
                new ConstEntry("EventCaps", "Drill", EventCaps.Drill),
                new ConstEntry("EventCaps", "Explosion", EventCaps.Explosion),
                new ConstEntry("EventCaps", "Grinder", EventCaps.Grinder),
                new ConstEntry("EventCaps", "HullBreak", EventCaps.HullBreak),
                new ConstEntry("EventCaps", "PrawnDrill", EventCaps.PrawnDrill),
                new ConstEntry("EventCaps", "Repair", EventCaps.Repair),
                new ConstEntry("EventCaps", "Welder", EventCaps.Welder),
                new ConstEntry("EventCaps", "GridImpact", EventCaps.GridImpact),
                new ConstEntry("EventCaps", "Splash", EventCaps.Splash),
                new ConstEntry("EventCaps", "WeaponMuzzle", EventCaps.WeaponMuzzle),
                new ConstEntry("EventCaps", "BulletStreak", EventCaps.BulletStreak),
                new ConstEntry("EventCaps", "Impact", EventCaps.Impact),
                new ConstEntry("EventCaps", "Thruster", EventCaps.Thruster),
                new ConstEntry("EventFlags", "Attached", EventFlags.Attached),
                new ConstEntry("EventFlags", "Sustain", EventFlags.Sustain),
                new ConstEntry("EventFlags", "Stop", EventFlags.Stop),
                new ConstEntry("EventFlags", "Underwater", EventFlags.Underwater),
                new ConstEntry("EventFlags", "Surface", EventFlags.Surface),
                new ConstEntry("EventFlags", "Hit", EventFlags.Hit),
                new ConstEntry("EventFlags", "Local", EventFlags.Local),
                new ConstEntry("EventLimits", "SustainRefreshTicks", EventLimits.SustainRefreshTicks),
                new ConstEntry("EventLimits", "SurfaceBandCm", EventLimits.SurfaceBandCm),
                new ConstEntry("EventLimits", "SustainStepPermille", EventLimits.SustainStepPermille),
                new ConstEntry("EventLimits", "StreakMaxM", EventLimits.StreakMaxM),
                new ConstEntry("EventLimits", "SplashMinSpeedCmS", EventLimits.SplashMinSpeedCmS),
                new ConstEntry("EventLimits", "EffectRadiusM", EventLimits.EffectRadiusM),
                new ConstEntry("EventLimits", "SustainLifetimeMs", EventLimits.SustainLifetimeMs),
                new ConstEntry("EventLimits", "ExplosionRadiusM", EventLimits.ExplosionRadiusM),
                new ConstEntry("EventType", "Thruster", EventType.Thruster),
                new ConstEntry("EventType", "WeaponMuzzle", EventType.WeaponMuzzle),
                new ConstEntry("EventType", "BulletStreak", EventType.BulletStreak),
                new ConstEntry("EventType", "Impact", EventType.Impact),
                new ConstEntry("EventType", "Explosion", EventType.Explosion),
                new ConstEntry("EventType", "Welder", EventType.Welder),
                new ConstEntry("EventType", "Grinder", EventType.Grinder),
                new ConstEntry("EventType", "Drill", EventType.Drill),
                new ConstEntry("EventType", "Repair", EventType.Repair),
                new ConstEntry("EventType", "PrawnDrill", EventType.PrawnDrill),
                new ConstEntry("EventType", "GridImpact", EventType.GridImpact),
                new ConstEntry("EventType", "HullBreak", EventType.HullBreak),
                new ConstEntry("EventType", "Splash", EventType.Splash),
                new ConstEntry("InteriorClass", "Exterior", InteriorClass.Exterior),
                new ConstEntry("InteriorClass", "Boundary", InteriorClass.Boundary),
                new ConstEntry("InteriorClass", "Inside", InteriorClass.Inside),
                new ConstEntry("LightLimits", "MaxShadowedSpots", LightLimits.MaxShadowedSpots),
                new ConstEntry("LightLimits", "RadiusM", LightLimits.RadiusM),
                new ConstEntry("LightLimits", "MaxLights", LightLimits.MaxLights),
                new ConstEntry("RoomLimits", "MaxBoxes", RoomLimits.MaxBoxes),
                new ConstEntry("RoomSetFlags", "Truncated", RoomSetFlags.Truncated),
                new ConstEntry("RoomSetFlags", "Version1", RoomSetFlags.Version1),
                new ConstEntry("StaticFlags", "Census", StaticFlags.Census),
                new ConstEntry("StaticFlags", "AllowListed", StaticFlags.AllowListed),
                new ConstEntry("StaticFlags", "SkewApprox", StaticFlags.SkewApprox),
                new ConstEntry("StaticsLimits", "BatchM", StaticsLimits.BatchM),
                new ConstEntry("StaticsLimits", "CreateRadiusM", StaticsLimits.CreateRadiusM),
                new ConstEntry("StaticsLimits", "ScanRadiusM", StaticsLimits.ScanRadiusM),
                new ConstEntry("StaticsLimits", "CloseRadiusM", StaticsLimits.CloseRadiusM),
                new ConstEntry("StaticsLimits", "MaxPerHost", StaticsLimits.MaxPerHost),
                new ConstEntry("StaticsLimits", "MinRescanMs", StaticsLimits.MinRescanMs),
                new ConstEntry("StaticsLimits", "MaxPerBatch", StaticsLimits.MaxPerBatch),
                new ConstEntry("ThrusterKind", "Other", ThrusterKind.Other),
                new ConstEntry("ThrusterKind", "Ion", ThrusterKind.Ion),
                new ConstEntry("ThrusterKind", "Atmospheric", ThrusterKind.Atmospheric),
                new ConstEntry("ThrusterKind", "Hydrogen", ThrusterKind.Hydrogen),
                new ConstEntry("ToolSource", "Hand", ToolSource.Hand),
                new ConstEntry("ToolSource", "Ship", ToolSource.Ship),
                new ConstEntry("WeaponKind", "Projectile", WeaponKind.Projectile),
                new ConstEntry("WeaponKind", "Missile", WeaponKind.Missile),
            };
        }

        /// <summary>One <c>field</c> line: block, name, offset from the record start, size, type.</summary>
        private struct FieldEntry
        {
            public readonly string Block, Name, Type;
            public readonly int Offset, Size;
            public FieldEntry(string block, string name, int offset, int size, string type) { Block = block; Name = name; Offset = offset; Size = size; Type = type; }
        }

        private static FieldEntry[] Fields()
        {
            int c1 = C1Interior.Offset, h = C2Ext.Offset;
            return new[]
            {
                new FieldEntry("interior", "version", c1 + 0x0, 1, "u8"),
                new FieldEntry("interior", "flags", c1 + 0x1, 1, "u8"),
                new FieldEntry("interior", "roomO2", c1 + 0x2, 1, "u8"),
                new FieldEntry("interior", "pad", c1 + 0x3, 1, "u8"),
                new FieldEntry("interior", "roomIndex", c1 + 0x4, 4, "u32"),
                new FieldEntry("interior", "roomGridId", c1 + 0x8, 8, "u64"),
                new FieldEntry("c2ext", "version", h + 0x0, 1, "u8"),
                new FieldEntry("c2ext", "flags", h + 0x1, 1, "u8"),
                new FieldEntry("c2ext", "eventCount", h + 0x2, 2, "u16"),
                new FieldEntry("c2ext", "eventFirstSeq", h + 0x4, 4, "u32"),
                new FieldEntry("c2ext", "eventStream", h + 0x8, 4, "u32"),
                new FieldEntry("c2ext", "eventsDropped", h + 0xC, 4, "u32"),
                new FieldEntry("c2ext", "lightsFound", h + 0x10, 4, "u32"),
                new FieldEntry("c2ext", "tickLow", h + 0x14, 4, "u32"),
                new FieldEntry("c2ext", "pad", h + 0x18, 12, "u8[12]"),
                new FieldEntry("light", "falloff", 0x3C, 4, "f32"),
                new FieldEntry("event", "seq", 0x00, 4, "u32"),
                new FieldEntry("event", "type", 0x04, 1, "u8"),
                new FieldEntry("event", "flags", 0x05, 1, "u8"),
                new FieldEntry("event", "lifetimeMs", 0x06, 2, "u16"),
                new FieldEntry("event", "entityId", 0x08, 8, "u64"),
                new FieldEntry("event", "key", 0x10, 8, "u64"),
                new FieldEntry("event", "pos", 0x18, 12, "f32[3]"),
                new FieldEntry("event", "dir", 0x24, 12, "f32[3]"),
                new FieldEntry("event", "intensity", 0x30, 4, "f32"),
                new FieldEntry("event", "size", 0x34, 4, "f32"),
                new FieldEntry("event", "tickLow", 0x38, 4, "u32"),
                new FieldEntry("event", "subtype", 0x3C, 2, "u16"),
                new FieldEntry("event", "pad", 0x3E, 2, "u16"),
                new FieldEntry("statics", "version", 0x06, 2, "u16"),
                new FieldEntry("prim", "flags", 0x01, 1, "u8"),
                new FieldEntry("roomset", "flags", 0x0C, 4, "u32"),
            };
        }

        /// <summary>The <c>field</c> lines (without the <c>field </c> prefix), in text order.</summary>
        public static string[] FieldLines()
        {
            FieldEntry[] f = Fields();
            var lines = new string[f.Length];
            for (int k = 0; k < f.Length; k++)
                lines[k] = f[k].Block + " " + f[k].Name + " 0x" + f[k].Offset.ToString("x", CultureInfo.InvariantCulture) + " " +
                           f[k].Size.ToString(CultureInfo.InvariantCulture) + " " + f[k].Type;
            return lines;
        }

        /// <summary>The <c>format</c> lines (without the <c>format </c> prefix); the text sorts them by key.</summary>
        public static readonly string[] FormatLines =
        {
            "audio.bus Subnautica event sounds only on Underwater rows while snBusTarget > 0; rows without Underwater draw lights only",
            "audio.muffle postfix MyEntity3DSoundEmitter.SelectEffect: LowPassNoHelmetNoOxy while seMixActive and C1 interior InRoom clear, sounds ModifiableByHelmetFilters only",
            "bones.order rows 0..77 canon1; rows 78..boneCount-1 the model's non-canonical bones in Model.Bones order; boneCount = Model.Bones.Length <= 160",
            "bones.unity Mirror * M^T * Mirror; extra rows posed flat under the Entity from their absolute matrices",
            "event.frame Attached: pos, dir in the SE-local frame of entityId; else SN-world (F, mapdir)",
            "event.key fnv1a64(utf8 thr|<fatKey x16>|<flame index>) | fnv1a64(utf8 tool|<fatKey x16>) | fnv1a64(utf8 tool|e<entity id>); one-shots 0",
            "event.priority over MaxPerTick: Stop rows, Explosion, HullBreak, GridImpact, Splash, Impact, WeaponMuzzle, Repair, Welder, Grinder, Drill, PrawnDrill, Thruster, BulletStreak; newest first within a type",
            "event.sustain a row on start, on |d intensity| >= SustainStepPermille/1000, on an attached move > 0.05 m, else every SustainRefreshTicks; lifetimeMs = SustainLifetimeMs; Stop row at the end; Subnautica fades a sustain whose lifetime ran out",
            "event.window rows = accepted events of ticks [tick-7, tick], seq consecutive from eventFirstSeq; eventStream changes when seq restarts; tickLow = low 32 bits of tick",
            "interior.class Inside: cell round(T/g) inside a room box; Boundary: Chebyshev distance 1 from a room box; else Exterior",
            "interior.flag GetOxygenRoomForCubeGridPosition(cell of the camera) else GetSafeOxygenBlock(camera).Room; IsAirtight; last value held while IsProcessingData",
            "interior.sky Inside: SetCustomSky(MarmoSkies.main.GetSky(BaseInterior)); Boundary: the same while C1 roomGridId = its entity, else SetSky(Auto) with dynamic; Exterior: SetSky(Auto)",
            "light.row ownerId frame as event.frame; a point row per LightOn, a Spot row per ReflectorOn; nearest first within RadiusM; cap MaxLights; Shadows on spot rows only",
            "roomset.box cells [min, max] of airtight room air cells, MyVoxelSegmentation Simple; Truncated: one box per room (its cell AABB)",
            "statics.axes staticAxes(q) = (mapdir^-1(q*x), mapdir^-1(q*y), mapdir^-1(q*(-z))), det +1; size_i *= sqrt((a_i.x^2+a_i.z^2)/s^2+a_i.y^2)",
            "statics.capsule size = (radius, halfSegment, axis 0|1|2); halfSegment = max(0, height/2 - radius); HkCapsuleShape(F^-1(c - a*halfSegment) - o, F^-1(c + a*halfSegment) - o, radius/s(c)); sphere radius/s(c)",
            "statics.host DE.Statics.<bx>.<by>.<bz>.e<epoch>[.p<k>]: MyEntity, no model, Save false, layer 13, HkStaticCompoundShape baked, origin F^-1(mean centre), identity rotation",
        };

        private static string _text;
        private static ulong _hash;

        /// <summary>
 /// The canonical text (CONTRACT §5.4 "S7 conventions"; ASCII, '\n' after every line, the last included):
 /// <see cref="Header"/>; one <c>const &lt;group&gt; &lt;name&gt; &lt;value&gt;</c> line per constant sorted by group
 /// (ordinal), then value, then name; the <c>field</c> lines in their listed order; the <c>format</c> lines sorted by
 /// key (ordinal).
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            ConstEntry[] e = Entries();
            Array.Sort(e, (a, b) =>
            {
                int c = string.CompareOrdinal(a.Group, b.Group);
                if (c != 0) return c;
                c = a.Value.CompareTo(b.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            var sb = new StringBuilder(7000);
            sb.Append(Header).Append('\n');
            foreach (ConstEntry c in e)
                sb.Append("const ").Append(c.Group).Append(' ').Append(c.Name).Append(' ')
                  .Append(c.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (string f in FieldLines()) sb.Append("field ").Append(f).Append('\n');
            var formats = new List<string>(FormatLines);
            formats.Sort(string.CompareOrdinal);
            foreach (string f in formats) sb.Append("format ").Append(f).Append('\n');
            _text = sb.ToString();
            return _text;
        }

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> (compiled; equals <see cref="PinnedHash"/> when nothing drifted).</summary>
        public static ulong Hash
        {
            get
            {
                if (_hash == 0) _hash = LayoutHash.Fnv1a64Ascii(CanonicalText());
                return _hash;
            }
        }
    }
}
