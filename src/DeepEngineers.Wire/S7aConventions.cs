using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7a "Subnautica's picture above water": the meaning S7a gives to fields and bits that layout 1.0 carries but does
 // not pin (CONTRACT §5.4 "S7a conventions", "C5/C7 semantics for S7a", "C1 locomotion extension"). Layout 1.0 is
 // unchanged (hash 0x4f3a35f6fc1eaf31); GridAdd flags bit 3, the C2 entity flags bits 2-3 (ghost row only), C5 flags
 // bit 9, C6 flags bit 2 and its pad bytes 0x3C-0x3F, and the C1 pad bytes 0xA0-0xAF get their S7a meaning here,
 // pinned on their own by the canonical text below (FNV-1a 64 0x63d59c04766f4e03, 59 lines, 2,219 bytes), as S5 and S6
 // pinned theirs. The text is built from the constants of this file (and ViewPicture), so a changed constant changes
 // the hash and turns S7aConventionsTests red. Python twin: tests/s7a/analyze_s7a.py --selftest.
 // =================================================================================================================

 /// <summary>The S7a conventions (CONTRACT §5.4): constants, the canonical text and its hash, two shared radius rules.</summary>
    public static class S7aConventions
    {
        /// <summary>The first line of the canonical text (<c>ext s7a picture &lt;n&gt;</c>).</summary>
        public const string Header = "ext s7a picture 3";
        /// <summary>The <c>&lt;n&gt;</c> of <see cref="Header"/>.</summary>
        public const int ExtensionVersion = 3;   // 2: radii 1,500 / 1,600 m, leave kept under water (F10)
 /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4.</summary>
        public const ulong PinnedHash = 0x4257c11c17ff772aUL;   // ext s7a picture 3
 /// <summary>Lines and bytes of the canonical text.</summary>
        public const int CanonicalLines = 60, CanonicalBytes = 2250;

        /// <summary>
 /// Under the <c>Site</c> picture policy the ±16 m <c>areaBuilt</c> camera box is centred at <c>min(y, +140 m)</c>
 /// (ARCHITECTURE §4.2 thresholds "Near-field box above the world"): the octree rows end at +160 m, Mountain Island
 /// is +155 m. Not part of the canonical text (a tunable threshold, like the S5 budgets).
 /// </summary>
        public const double AreaBoxMaxCentreY = 140.0;

        /// <summary>The C1 locomotion extension's place in the C1 pad (<c>pad_94</c>, after the S5a swim extension 0x94-0x9F).</summary>
        public static class C1Loco
        {
            public const int Version = 1;
            public const int Offset = 0xA0;
            public const int Size = 16;
        }

        /// <summary>C1 loco <c>flags</c> (byte 0xA2).</summary>
        public static class C1LocoFlags
        {
            public const byte Grounded = 1, Crouching = 2, Jetpack = 4, InWater = 8, Seated = 16, Dead = 32, Ladder = 64;
            /// CurrentWeapon != null</c>); the diver arms hide.</summary>
            public const byte ToolHeld = 128;
        }

        /// <summary>C1 loco <c>state</c> (byte 0xA1): the SE character's movement state as S7a maps it.</summary>
        public static class C1LocoState
        {
            public const byte None = 0, Idle = 1, Walk = 2, Run = 3, Sprint = 4, CrouchIdle = 5, CrouchWalk = 6, Jump = 7, Fall = 8,
                Fly = 9, Ladder = 10, Seated = 11, Dead = 12, Swim = 13;

            /// <summary>A foot state (the out-of-water Retarget states): Idle, Walk, Run, Sprint, Jump, Fall.</summary>
            public static bool IsFoot(byte state)
            {
                return (state >= Idle && state <= Sprint) || state == Jump || state == Fall;
            }

            public static string Name(byte state)
            {
                switch (state)
                {
                    case None: return "None";
                    case Idle: return "Idle";
                    case Walk: return "Walk";
                    case Run: return "Run";
                    case Sprint: return "Sprint";
                    case CrouchIdle: return "CrouchIdle";
                    case CrouchWalk: return "CrouchWalk";
                    case Jump: return "Jump";
                    case Fall: return "Fall";
                    case Fly: return "Fly";
                    case Ladder: return "Ladder";
                    case Seated: return "Seated";
                    case Dead: return "Dead";
                    case Swim: return "Swim";
                    default: return "Unknown(" + state.ToString(CultureInfo.InvariantCulture) + ")";
                }
            }
        }

        /// <summary>C2 entity <c>flags</c> bits beyond <see cref="SeEntityFlags"/> (bits 0-1), on the placement ghost's row only.</summary>
        public static class C2FlagsExt
        {
            public const uint GhostShown = 4u, GhostAllowed = 8u;
        }

        /// <summary>C5 <c>flags</c> bits beyond <see cref="SeStateFlags"/> (bits 0-8).</summary>
        public static class C5FlagsExt
        {
            /// <summary>Bit 9: the view machine's effective picture policy is <see cref="ViewPicture.Site"/>.</summary>
            public const uint PictureSite = 512u;
        }

        /// <summary>C6 <c>flags</c> bits beyond <see cref="SeTimeFlags"/> (bits 0-1).</summary>
        public static class C6FlagsExt
        {
            /// <summary>Bit 2: the sky pole in the pad bytes 0x3C-0x3F is valid.</summary>
            public const uint SkyPoleValid = 4u;
        }

        /// <summary>The C6 sky pole's place in <c>SeTime.pad_3c</c>.</summary>
        public static class C6Sky
        {
            public const int Offset = 0x3C;
            public const int Size = 4;
        }

        /// <summary>The block-placement ghost: a synthetic one-part grid.</summary>
        public static class Ghost
        {
            /// <summary><c>MyEntityIdentifier.ConstructId(ENTITY, 0x44454700000001)</c> ("DEG").</summary>
            public const ulong EntityId = 0x0144454700000001UL;
            public const ulong PartKey = 1UL;
            public const int MaxParts = 32;
        }

        /// <summary>GridAdd <c>flags</c> bits beyond <see cref="MirrorGridFlags"/> (bits 0-2, version nibble 28-31).</summary>
        public static class GridFlagsExt
        {
            /// <summary>Bit 3: the placement ghost (never a proxy, never counted by the S5 analyzers).</summary>
            public const uint Ghost = 8u;
        }

        /// <summary>S7a limits.</summary>
        public static class Limits
        {
            public const int SunHoldMaxCdeg = 200;
            public const int PictureDwellMs = 250;
            public const int FallAnimMs = 450;
            public const int GroundAnimScalePermille = 1000;
            public const int MirrorEnterAboveWaterM = 1500;
            public const int MirrorLeaveAboveWaterM = 1600;
        }

        // ------------------------------------------------------------------------------------------------ shared rules

 /// <summary>
 /// The S5 entity-set radii under S7a (CONTRACT §5.4 "C5/C7 semantics for S7a", entity set): under the <c>Site</c>
 /// picture policy, enter 1,500 m while the camera is above the sea (<paramref name="camSnY"/> &gt; 0; a ship on the
 /// sea right below the 1 km band) and leave 1,600 m at any altitude; otherwise S5's 250 / 300 m (<see cref="MirrorBudgets"/>).
 /// A NaN altitude counts as not above the sea.
 /// </summary>
        public static void MirrorRadius(bool pictureSite, double camSnY, out double enterM, out double leaveM)
        {
            if (pictureSite)
            {
                enterM = camSnY > 0.0 ? Limits.MirrorEnterAboveWaterM : MirrorBudgets.EnterRadiusMetres;
                leaveM = Limits.MirrorLeaveAboveWaterM;
                return;
            }
            enterM = MirrorBudgets.EnterRadiusMetres;
            leaveM = MirrorBudgets.LeaveRadiusMetres;
        }

        /// <summary>
 /// The y of the ±16 m <c>areaBuilt</c> camera box (CONTRACT §5.4 "C5/C7 semantics for S7a"): <c>min(y, +140 m)</c>
 /// under the <c>Site</c> policy (a camera at +300 m asks for the world's top layer under it), the camera's y otherwise.
 /// </summary>
        public static double AreaBoxCentreY(bool pictureSite, double camSnY)
        {
            return pictureSite ? Math.Min(camSnY, AreaBoxMaxCentreY) : camSnY;
        }

        /// <summary>
 /// S7a-1b (HYPOTHESIS "areaBuilt above the world" settled): <c>IsRangeActiveAndBuilt</c> answers false for
 /// a box far below the camera (the clip level is built only near the camera), so the +140 m clamp never made
 /// <c>areaBuilt</c> true at 1 km. Under the <c>Site</c> policy a camera above the near field (higher than the clamp
 /// plus the box half-size) has only air around it: built means the streamer is idle (<c>IsWorldSettled</c>).
 /// </summary>
        public static bool AirAboveTheNearField(bool pictureSite, double camSnY, double boxHalfM)
        {
            return pictureSite && camSnY > AreaBoxMaxCentreY + boxHalfM;
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
                new ConstEntry("C1Loco", "Version", C1Loco.Version),
                new ConstEntry("C1Loco", "Offset", C1Loco.Offset),
                new ConstEntry("C1Loco", "Size", C1Loco.Size),
                new ConstEntry("C1LocoFlags", "Grounded", C1LocoFlags.Grounded),
                new ConstEntry("C1LocoFlags", "Crouching", C1LocoFlags.Crouching),
                new ConstEntry("C1LocoFlags", "Jetpack", C1LocoFlags.Jetpack),
                new ConstEntry("C1LocoFlags", "InWater", C1LocoFlags.InWater),
                new ConstEntry("C1LocoFlags", "Seated", C1LocoFlags.Seated),
                new ConstEntry("C1LocoFlags", "Dead", C1LocoFlags.Dead),
                new ConstEntry("C1LocoFlags", "Ladder", C1LocoFlags.Ladder),
                new ConstEntry("C1LocoFlags", "ToolHeld", C1LocoFlags.ToolHeld),
                new ConstEntry("C1LocoState", "None", C1LocoState.None),
                new ConstEntry("C1LocoState", "Idle", C1LocoState.Idle),
                new ConstEntry("C1LocoState", "Walk", C1LocoState.Walk),
                new ConstEntry("C1LocoState", "Run", C1LocoState.Run),
                new ConstEntry("C1LocoState", "Sprint", C1LocoState.Sprint),
                new ConstEntry("C1LocoState", "CrouchIdle", C1LocoState.CrouchIdle),
                new ConstEntry("C1LocoState", "CrouchWalk", C1LocoState.CrouchWalk),
                new ConstEntry("C1LocoState", "Jump", C1LocoState.Jump),
                new ConstEntry("C1LocoState", "Fall", C1LocoState.Fall),
                new ConstEntry("C1LocoState", "Fly", C1LocoState.Fly),
                new ConstEntry("C1LocoState", "Ladder", C1LocoState.Ladder),
                new ConstEntry("C1LocoState", "Seated", C1LocoState.Seated),
                new ConstEntry("C1LocoState", "Dead", C1LocoState.Dead),
                new ConstEntry("C1LocoState", "Swim", C1LocoState.Swim),
                new ConstEntry("C2FlagsExt", "GhostShown", C2FlagsExt.GhostShown),
                new ConstEntry("C2FlagsExt", "GhostAllowed", C2FlagsExt.GhostAllowed),
                new ConstEntry("C5FlagsExt", "PictureSite", C5FlagsExt.PictureSite),
                new ConstEntry("C6FlagsExt", "SkyPoleValid", C6FlagsExt.SkyPoleValid),
                new ConstEntry("C6Sky", "Offset", C6Sky.Offset),
                new ConstEntry("C6Sky", "Size", C6Sky.Size),
                new ConstEntry("Ghost", "EntityId", Ghost.EntityId),
                new ConstEntry("Ghost", "PartKey", Ghost.PartKey),
                new ConstEntry("Ghost", "MaxParts", Ghost.MaxParts),
                new ConstEntry("GridFlagsExt", "Ghost", GridFlagsExt.Ghost),
                new ConstEntry("Limits", "SunHoldMaxCdeg", Limits.SunHoldMaxCdeg),
                new ConstEntry("Limits", "PictureDwellMs", Limits.PictureDwellMs),
                new ConstEntry("Limits", "FallAnimMs", Limits.FallAnimMs),
                new ConstEntry("Limits", "GroundAnimScalePermille", Limits.GroundAnimScalePermille),
                new ConstEntry("Limits", "MirrorEnterAboveWaterM", Limits.MirrorEnterAboveWaterM),
                new ConstEntry("Limits", "MirrorLeaveAboveWaterM", Limits.MirrorLeaveAboveWaterM),
                new ConstEntry("ViewPicture", "Site", ViewPicture.Site),
                new ConstEntry("ViewPicture", "Waterline", ViewPicture.Waterline),
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
            int c1 = C1Loco.Offset, c6 = C6Sky.Offset;
            return new[]
            {
                new FieldEntry("loco", "version", c1 + 0x0, 1, "u8"),
                new FieldEntry("loco", "state", c1 + 0x1, 1, "u8"),
                new FieldEntry("loco", "flags", c1 + 0x2, 1, "u8"),
                new FieldEntry("loco", "pad", c1 + 0x3, 1, "u8"),
                new FieldEntry("loco", "vForwardCm", c1 + 0x4, 2, "i16"),
                new FieldEntry("loco", "vStrafeCm", c1 + 0x6, 2, "i16"),
                new FieldEntry("loco", "vUpCm", c1 + 0x8, 2, "i16"),
                new FieldEntry("loco", "yawRateCdeg", c1 + 0xA, 2, "i16"),
                new FieldEntry("loco", "seMovement", c1 + 0xC, 2, "u16"),
                new FieldEntry("loco", "pad2", c1 + 0xE, 2, "u16"),
                new FieldEntry("sky", "poleAzCdeg", c6 + 0x0, 2, "i16"),
                new FieldEntry("sky", "poleElCdeg", c6 + 0x2, 2, "i16"),
            };
        }

        /// <summary>The <c>field</c> lines (without the <c>field </c> prefix), in text order.</summary>
        public static string[] FieldLines()
        {
            FieldEntry[] f = Fields();
            var lines = new string[f.Length];
            for (int k = 0; k < f.Length; k++)
                lines[k] = f[k].Block + " " + f[k].Name + " 0x" + f[k].Offset.ToString("x2", CultureInfo.InvariantCulture) + " " +
                           f[k].Size.ToString(CultureInfo.InvariantCulture) + " " + f[k].Type;
            return lines;
        }

        /// <summary>The <c>format</c> lines (without the <c>format </c> prefix); the text sorts them by key.</summary>
        public static readonly string[] FormatLines =
        {
            "ghost C2 kind 1 pose = gizmo SpaceDefault.m_worldMatrixAdd; PartAdd key 1 class 3 NoShadows local = translation(ModelOffset) model = MyCubeBlockDefinition.Model|lod0",
            "pole q = M_sn x W_sn, M = normalize(U-axis*(axis.U)), W = normalize(axis x U); az = atan2(q.x,q.z), el = asin(q.y), centi-degrees rounded half away from zero",
            "sky c = frac(H/2pi+0.5); n = normalize(up-q*(up.q)); r = -(q x n); SunDirection = atan2(-r.x,-r.z); NorthPoleOffset = atan2(-n.x*cos(SunDirection)+n.z*sin(SunDirection),n.y)",
            "time DayNightCycle paused; timePassedAsDouble += frac(sTarget-sNow)*1200 unless behind by <= SunHoldMaxCdeg of cycle angle (hold)",
        };

        private static string _text;
        private static ulong _hash;

        /// <summary>
 /// The canonical text (CONTRACT §5.4; ASCII, '\n' after every line, the last included): <see cref="Header"/>; one
 /// <c>const &lt;group&gt; &lt;name&gt; &lt;value&gt;</c> line per constant (the groups above plus <see cref="ViewPicture"/>)
 /// sorted by group (ordinal), then value, then name; the <c>field</c> lines in their listed order; the
 /// <c>format</c> lines sorted by key (ordinal).
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
            var sb = new StringBuilder(2400);
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
