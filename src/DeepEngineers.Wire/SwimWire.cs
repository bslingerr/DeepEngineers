using System;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S5a "Arms and breath": the swim state SE already knows (SwimController, CONTRACT §2.10) crosses to Subnautica in
 // the C1 SeFrame record, inside the pad bytes 0x94-0x9F of layout 1.0 (CONTRACT §5.4 "C1 swim extension"). The wire
 // layout is unchanged (layout 1.0, hash 0x4f3a35f6fc1eaf31, pinned by the S0 layout tests): Layout.cs still
 // describes 0x94-0xFF as one pad, so a reader without this file sees pad bytes and ignores them. The extension's own
 // placement is pinned here (offsets, sizes and the canonical text below) and by tests/DeepEngineers.Wire.Tests/
 // SwimWireTests.cs and the Python twin tests/s5a/analyze_s5a.py (its C1_SWIM table), so the two sides cannot drift
 // apart without a red test. A byte at 0x94 (`version`) of 0 means "no extension written" (an SE build before S5a).
 //
 // Written by the SE plugin with every C1 record (every SE tick, 60 Hz; values from the swim controller's last tick,
 // see SwimWireFill in DeepEngineers.SE); read by the Subnautica plugin's C1 consumer for the ghost's arms
 // (ArmsController), the OxygenManager mirror and the breath/choke sounds. Nothing here moves, hurts or saves the
 // ghost Player (CONTRACT §1).
 // =================================================================================================================

 /// <summary>The S5a fins setting (<c>/de swim fins off|basic|ultra</c>; C1 swim extension byte <c>fins</c>).</summary>
    public static class SwimFins
    {
        /// <summary>No fins: the S2b numbers (7.6 / 6.3 m/s maxima).</summary>
        public const uint Off = 0;
        /// <summary>Fins: +1.9 m/s (<see cref="SwimThresholds.FinsSpeedAdjust"/>).</summary>
        public const uint Basic = 1;
        /// <summary>Ultraglide fins: +3.2 m/s (<see cref="SwimThresholds.UltraglideFinsSpeedAdjust"/>);</summary>
        public const uint Ultra = 2;

        /// <summary>The SE controller's default.</summary>
        public const uint Default = Ultra;

        /// <summary>The max-speed modifier of a setting, m/s (unknown values read as no fins).</summary>
        public static double Modifier(uint fins)
        {
            switch (fins)
            {
                case Basic: return SwimThresholds.FinsSpeedAdjust;
                case Ultra: return SwimThresholds.UltraglideFinsSpeedAdjust;
                default: return 0.0;
            }
        }

        public static string Name(uint fins)
        {
            switch (fins)
            {
                case Off: return "off";
                case Basic: return "basic";
                case Ultra: return "ultra";
                default: return "?" + fins;
            }
        }

        /// <summary><c>off|none|0</c>, <c>basic|fins|1</c>, <c>ultra|ultraglide|2</c> (case-insensitive); false otherwise.</summary>
        public static bool TryParse(string text, out uint fins)
        {
            fins = Off;
            if (text == null) return false;
            switch (text.Trim().ToLowerInvariant())
            {
                case "off": case "none": case "0": fins = Off; return true;
                case "basic": case "fins": case "1": fins = Basic; return true;
                case "ultra": case "ultraglide": case "2": fins = Ultra; return true;
                default: return false;
            }
        }
    }

    /// <summary>Bits of the C1 swim extension byte <c>flags</c> (0x95).</summary>
    public static class SwimWireFlags
    {
        /// <summary>The swim latch is on (modes Swim, Surface, Fly): the arms may show.</summary>
        public const byte InWater = 1 << 0;
        /// <summary>The eye is below the sea (Subnautica's isUnderwater): Dive numbers, breathing stops.</summary>
        public const byte Underwater = 1 << 1;
        /// <summary>Mode Surface (latch on, eye at or above 0): the head is out.</summary>
        public const byte AtSurface = 1 << 2;
        /// <summary>The character touches something (the grounded exit, the contact acceleration).</summary>
        public const byte Grounded = 1 << 3;
        /// <summary>A move input is held this tick (any axis).</summary>
        public const byte Moving = 1 << 4;
        /// <summary>The swim rule forbids SE's jetpack this tick (latch on, head under).</summary>
        public const byte JetpackSuppressed = 1 << 5;
        /// <summary>The swim model set the velocity this tick (modes Swim, Surface).</summary>
        public const byte Controls = 1 << 6;
        /// <summary>The <c>o2</c> byte is the dev override (<c>/de swim o2 &lt;0..1&gt;</c>), not the suit.</summary>
        public const byte O2Override = 1 << 7;
    }

    /// <summary>The decoded C1 swim extension (one SE tick's swim state as the SE controller knows it).</summary>
    public struct SwimWireState
    {
        /// <summary><see cref="SwimWire.Version"/>; 0 = no extension written (an SE build before S5a).</summary>
        public byte Version;
        /// <summary><see cref="SwimWireFlags"/>.</summary>
        public byte Flags;
        /// <summary><see cref="SwimMode"/> (0 Walk, 1 Swim, 2 Surface, 3 Fly).</summary>
        public byte Mode;
        /// <summary><see cref="SwimFins"/> (0 off, 1 basic, 2 ultra).</summary>
        public byte Fins;
        /// <summary>Horizontal speed over the current steady top speed, 0..255 = 0..1 (see <see cref="SwimWire.SpeedReference"/>).</summary>
        public byte SpeedH;
        /// <summary>|vertical speed| over the current steady top speed, 0..255 = 0..1.</summary>
        public byte SpeedV;
        /// <summary>Body-relative move input, −127..127 = −1..1: forward (+ forward), strafe (+ right), vertical (+ up).</summary>
        public sbyte MoveForward, MoveStrafe, MoveVertical;
        /// <summary>The suit's oxygen fraction (or the override), 0..255 = 0..1; 255 when the suit cannot be read.</summary>
        public byte O2;
        /// <summary><see cref="SwimDepthClass"/> of the eye (0 surface, 1 safe, 2 unsafe, 3 crush).</summary>
        public byte DepthClass;
        /// <summary>Signed vertical velocity over the steady top speed, −127..127 = −1..1 (+ rising).</summary>
        public sbyte VyFrac;

        public bool Has(byte flag) { return (Flags & flag) != 0; }
    }

    /// <summary>
 /// Codec of the C1 swim extension (CONTRACT §5.4 "C1 swim extension"; bytes 0x94-0x9F of <see cref="SeFrame"/>):
 /// <code>
 /// 0x94 u8 version (1) 0x95 u8 flags (SwimWireFlags) 0x96 u8 mode (SwimMode) 0x97 u8 fins (SwimFins)
 /// 0x98 u8 speedH (0..255) 0x99 u8 speedV (0..255) 0x9A i8 moveForward (-127..127) 0x9B i8 moveStrafe
 /// 0x9C i8 moveVertical 0x9D u8 o2 (0..255) 0x9E u8 depthClass 0x9F i8 vyFrac (-127..127)
 /// </code>
 /// 0xA0-0xFF stay pad. Fractions: <c>byte = round(clamp01(x) × 255)</c>; axes: <c>sbyte = round(clamp(x, −1, 1) × 127)</c>.
 /// Written every C1 record (every SE tick). Allocation-free.
 /// </summary>
    public static unsafe class SwimWire
    {
        public const int Version = 1;
        /// <summary>First byte inside <see cref="SeFrame"/> (the start of layout 1.0's C1 pad).</summary>
        public const int Offset = 0x94;
        public const int Size = 12;
        /// <summary>The C1 pad of layout 1.0 (<c>P(0x94, 0x6C)</c> in Layout.cs) that holds the extension.</summary>
        public const int PadOffset = 0x94, PadSize = 0x6C;

        public const int OffVersion = 0x94, OffFlags = 0x95, OffMode = 0x96, OffFins = 0x97, OffSpeedH = 0x98, OffSpeedV = 0x99,
            OffMoveForward = 0x9A, OffMoveStrafe = 0x9B, OffMoveVertical = 0x9C, OffO2 = 0x9D, OffDepthClass = 0x9E, OffVyFrac = 0x9F;

        /// <summary>
 /// The canonical text of the extension (its own pin, outside the layout-1.0 hash): one <c>ext</c> line, then one
 /// <c>field</c> line per byte in offset order; FNV-1a 64 of it is <see cref="Hash"/>.
 /// </summary>
        public const string CanonicalText =
            "ext C1 swim 1 0x94 12\n" +
            "field swim version 0x94 1 u8\n" +
            "field swim flags 0x95 1 u8\n" +
            "field swim mode 0x96 1 u8\n" +
            "field swim fins 0x97 1 u8\n" +
            "field swim speedH 0x98 1 u8\n" +
            "field swim speedV 0x99 1 u8\n" +
            "field swim moveForward 0x9a 1 i8\n" +
            "field swim moveStrafe 0x9b 1 i8\n" +
            "field swim moveVertical 0x9c 1 i8\n" +
            "field swim o2 0x9d 1 u8\n" +
            "field swim depthClass 0x9e 1 u8\n" +
            "field swim vyFrac 0x9f 1 i8\n";

        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> (pinned by SwimWireTests and analyze_s5a.py).</summary>
        public static ulong Hash { get { return LayoutHash.Fnv1a64Ascii(CanonicalText); } }

        /// <summary>Writes <paramref name="s"/> into the frame's extension bytes (<see cref="Version"/> is forced).</summary>
        public static void Write(ref SeFrame f, ref SwimWireState s)
        {
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                b[OffVersion] = Version;
                b[OffFlags] = s.Flags;
                b[OffMode] = s.Mode;
                b[OffFins] = s.Fins;
                b[OffSpeedH] = s.SpeedH;
                b[OffSpeedV] = s.SpeedV;
                b[OffMoveForward] = (byte)s.MoveForward;
                b[OffMoveStrafe] = (byte)s.MoveStrafe;
                b[OffMoveVertical] = (byte)s.MoveVertical;
                b[OffO2] = s.O2;
                b[OffDepthClass] = s.DepthClass;
                b[OffVyFrac] = (byte)s.VyFrac;
            }
        }

        /// <summary>Zeroes the extension bytes (no extension: a reader sees version 0).</summary>
        public static void Clear(ref SeFrame f)
        {
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                for (int k = 0; k < Size; k++) b[Offset + k] = 0;
            }
        }

        /// <summary>Reads the extension; false (and a default state) when the frame carries none (version 0 or unknown).</summary>
        public static bool TryRead(ref SeFrame f, out SwimWireState s)
        {
            s = default(SwimWireState);
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                s.Version = b[OffVersion];
                if (s.Version != Version) { s = default(SwimWireState); s.Version = b[OffVersion]; return false; }
                s.Flags = b[OffFlags];
                s.Mode = b[OffMode];
                s.Fins = b[OffFins];
                s.SpeedH = b[OffSpeedH];
                s.SpeedV = b[OffSpeedV];
                s.MoveForward = (sbyte)b[OffMoveForward];
                s.MoveStrafe = (sbyte)b[OffMoveStrafe];
                s.MoveVertical = (sbyte)b[OffMoveVertical];
                s.O2 = b[OffO2];
                s.DepthClass = b[OffDepthClass];
                s.VyFrac = (sbyte)b[OffVyFrac];
            }
            return true;
        }

        /// <summary>A fraction in [0, 1] as a byte (clamped, rounded half away from zero; NaN → 255 = "full / unknown").</summary>
        public static byte Fraction(double x)
        {
            if (double.IsNaN(x)) return 255;
            if (x <= 0.0) return 0;
            if (x >= 1.0) return 255;
            return (byte)Math.Round(x * 255.0, MidpointRounding.AwayFromZero);
        }

        public static double FromFraction(byte b) { return b / 255.0; }

        /// <summary>An axis in [−1, 1] as a signed byte (clamped; NaN → 0).</summary>
        public static sbyte Axis(double x)
        {
            if (double.IsNaN(x)) return 0;
            if (x <= -1.0) return -127;
            if (x >= 1.0) return 127;
            return (sbyte)Math.Round(x * 127.0, MidpointRounding.AwayFromZero);
        }

        public static double FromAxis(sbyte b) { return b / 127.0; }

        /// <summary>
 /// The speed reference of <see cref="SwimWireState.SpeedH"/> / <see cref="SwimWireState.SpeedV"/>: the steady top
 /// speed of the motor's last step, <c>maxSpeed × (1 − 0.1 × 2.5)</c> (the cap after the physics damping; CONTRACT
 /// §2.10 "Implied": 5.70 m/s without fins forward, 8.1 m/s with Ultraglide), so a full-speed swim reads 255.
 /// <paramref name="maxSpeed"/> is <see cref="SwimOutputs.MaxSpeed"/> of the last tick with <see cref="SwimOutputs.SnStep"/>
 /// (it already holds the direction maxima, fins, tool and surface factors); 0 or NaN gives 0 (no reference).
 /// </summary>
        public static double SpeedReference(double maxSpeed)
        {
            if (double.IsNaN(maxSpeed) || maxSpeed <= 0.0) return 0.0;
            return maxSpeed * Math.Max(0.0, 1.0 - SwimThresholds.SnStepDt * SwimThresholds.SwimDrag);
        }

        /// <summary><paramref name="speed"/> over <paramref name="reference"/> as a byte fraction (0 without a reference).</summary>
        public static byte SpeedFraction(double speed, double reference)
        {
            if (!(reference > 0.0) || double.IsNaN(speed)) return 0;
            return Fraction(Math.Abs(speed) / reference);
        }

        /// <summary>Signed <paramref name="speed"/> over <paramref name="reference"/> as a signed byte (0 without a reference).</summary>
        public static sbyte SignedFraction(double speed, double reference)
        {
            if (!(reference > 0.0) || double.IsNaN(speed)) return 0;
            return Axis(speed / reference);
        }

        /// <summary>A readable one-line form for logs and dumps (allocates).</summary>
        public static string Describe(ref SwimWireState s)
        {
            return "v=" + s.Version + " flags=0x" + s.Flags.ToString("x2") + " mode=" + SwimMode.Name(s.Mode) + " fins=" + SwimFins.Name(s.Fins) +
                   " speedH=" + s.SpeedH + " speedV=" + s.SpeedV + " move=" + s.MoveForward + "," + s.MoveStrafe + "," + s.MoveVertical +
                   " o2=" + s.O2 + " dc=" + s.DepthClass + " vy=" + s.VyFrac;
        }
    }
}
