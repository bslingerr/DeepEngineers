using System;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S7a "Subnautica's picture above water": the SE character's movement state out of the water crosses to Subnautica
 // in the C1 SeFrame record, inside the pad bytes 0xA0-0xAF of layout 1.0 (CONTRACT §5.4 "C1 locomotion extension").
 // The wire layout is unchanged (layout 1.0, hash 0x4f3a35f6fc1eaf31): Layout.cs still describes 0x94-0xFF as one pad
 // and the S5a swim extension holds 0x94-0x9F (SwimWire.cs), so a reader without this file sees pad bytes. The
 // placement is pinned by the S7a conventions text (`field loco...` lines, S7aConventions.CanonicalText) and its
 // tests. A byte at 0xA0 (`version`) of 0 means "no extension written" (an SE build before S7a): Subnautica then
 // keeps S5's character sources.
 //
 // Written by the SE plugin with every C1 record (LocoWireFill, beside SwimWireFill); read by the Subnautica plugin's
 // C1 readers (FrameReader, CharFrameReader) for the out-of-water animator drive (S5aHost, S7aAnimRules) and the
 // CharacterMirror source rule. Nothing here moves or hurts the ghost Player (CONTRACT §1).
 // =================================================================================================================

 /// <summary>The decoded C1 locomotion extension (one SE tick's movement state of the controlled character).</summary>
    public struct LocoWireState
    {
        /// <summary><c>S7aConventions.C1LocoState</c> (0 None … 13 Swim).</summary>
        public byte State;
        /// <summary><c>S7aConventions.C1LocoFlags</c> (grounded, crouching, jetpack, inWater, seated, dead, ladder).</summary>
        public byte Flags;
        /// <summary>Linear velocity along the character's horizontal forward, cm/s.</summary>
        public short VForwardCm;
        /// <summary>Along the horizontal right, cm/s (+ right).</summary>
        public short VStrafeCm;
        /// <summary>Along the character's up, cm/s (+ up).</summary>
        public short VUpCm;
        /// <summary>Yaw rate about the character's up, centidegrees per simulation second (+ = turning right).</summary>
        public short YawRateCdeg;
        /// <summary>The raw <c>MyCharacter.GetCurrentMovementState</c> value (<c>MyCharacterMovementEnum: ushort</c>; diagnostic).</summary>
        public ushort SeMovement;

        public bool Has(byte flag) { return (Flags & flag) != 0; }
    }

    /// <summary>
 /// Codec of the C1 locomotion extension (CONTRACT §5.4 "C1 locomotion extension"; bytes 0xA0-0xAF of
 /// <see cref="SeFrame"/>):
 /// <code>
 /// 0xA0 u8 version (1) 0xA1 u8 state 0xA2 u8 flags 0xA3 u8 pad (0)
 /// 0xA4 i16 vForwardCm 0xA6 i16 vStrafeCm 0xA8 i16 vUpCm 0xAA i16 yawRateCdeg
 /// 0xAC u16 seMovement 0xAE u16 pad2 (0)
 /// </code>
 /// Little-endian like every wire field. Written every C1 record (every SE tick); allocation-free.
 /// </summary>
    public static unsafe class S7aLoco
    {
        public const int Version = 1;
        /// <summary>First byte inside <see cref="SeFrame"/> (right after the S5a swim extension, 0x94-0x9F).</summary>
        public const int Offset = 0xA0;
        public const int Size = 16;

        public const int OffVersion = 0xA0, OffState = 0xA1, OffFlags = 0xA2, OffPad = 0xA3, OffForward = 0xA4, OffStrafe = 0xA6,
            OffUp = 0xA8, OffYawRate = 0xAA, OffSeMovement = 0xAC, OffPad2 = 0xAE;

        // MyCharacterMovementEnum
 /// <summary><c>MovementTypeMask</c>: the mode (Standing 0, Sitting 1, Crouching 2, Flying 3, Falling 4, Jump 5, Died 6, Ladder 7).</summary>
        public const ushort ModeMask = 15;
        /// <summary>The direction bits (Forward 16, Backward 32, Left 64, Right 128, Up 256, Down 512).</summary>
        public const ushort DirectionMask = 1008;
        /// <summary>The speed bits (Normal 0, Fast 1024, VeryFast 2048).</summary>
        public const ushort SpeedMask = 3072;
        public const ushort SpeedFast = 1024, SpeedVeryFast = 2048;
        /// <summary><c>MyCharacterMovementEnum.LadderOut</c> (mode bits 0).</summary>
        public const ushort LadderOut = 16384;
        public const ushort ModeStanding = 0, ModeSitting = 1, ModeCrouching = 2, ModeFlying = 3, ModeFalling = 4, ModeJump = 5,
            ModeDied = 6, ModeLadder = 7;

        /// <summary>Writes <paramref name="s"/> into the frame's extension bytes (version 1; the pad bytes 0).</summary>
        public static void Write(ref SeFrame f, ref LocoWireState s)
        {
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                b[OffVersion] = Version;
                b[OffState] = s.State;
                b[OffFlags] = s.Flags;
                b[OffPad] = 0;
                *(short*)(b + OffForward) = s.VForwardCm;
                *(short*)(b + OffStrafe) = s.VStrafeCm;
                *(short*)(b + OffUp) = s.VUpCm;
                *(short*)(b + OffYawRate) = s.YawRateCdeg;
                *(ushort*)(b + OffSeMovement) = s.SeMovement;
                *(ushort*)(b + OffPad2) = 0;
            }
        }

        /// <summary>The "no character" form (CONTRACT: all zero except <c>version</c> 1 and <c>state</c> None).</summary>
        public static void Clear(ref SeFrame f)
        {
            var none = default(LocoWireState);
            Write(ref f, ref none);
        }

        /// <summary>Zeroes the 16 bytes (version 0: a reader treats the frame as written by an SE build before S7a).</summary>
        public static void Erase(ref SeFrame f)
        {
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                for (int k = 0; k < Size; k++) b[Offset + k] = 0;
            }
        }

        /// <summary>The raw version byte (0 = no extension).</summary>
        public static byte ReadVersion(ref SeFrame f)
        {
            fixed (uint* p = &f.seq) return ((byte*)p)[OffVersion];
        }

        /// <summary>Reads the extension; false (and a default state) unless the version byte is 1.</summary>
        public static bool TryRead(ref SeFrame f, out LocoWireState s)
        {
            s = default(LocoWireState);
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                if (b[OffVersion] != Version) return false;
                s.State = b[OffState];
                s.Flags = b[OffFlags];
                s.VForwardCm = *(short*)(b + OffForward);
                s.VStrafeCm = *(short*)(b + OffStrafe);
                s.VUpCm = *(short*)(b + OffUp);
                s.YawRateCdeg = *(short*)(b + OffYawRate);
                s.SeMovement = *(ushort*)(b + OffSeMovement);
            }
            return true;
        }

        /// <summary>
 /// The CONTRACT's <c>state</c> mapping (§5.4 "C1 locomotion extension"; first match wins): no character → None;
 /// the swim latch on → Swim; mode Died → Dead; Sitting → Seated; Ladder or the LadderOut bit → Ladder; Flying →
 /// Fly; Falling → Fall; Jump → Jump; Crouching: direction 0 → CrouchIdle, else CrouchWalk; Standing: direction 0
 /// (rotating in place included) → Idle, else speed Normal → Walk, Fast → Run, VeryFast → Sprint; anything else None.
 /// </summary>
        public static byte StateFromSe(ushort movement, bool present, bool swimLatch)
        {
            if (!present) return S7aConventions.C1LocoState.None;
            if (swimLatch) return S7aConventions.C1LocoState.Swim;
            int mode = movement & ModeMask;
            int dir = movement & DirectionMask;
            int speed = movement & SpeedMask;
            if (mode == ModeDied) return S7aConventions.C1LocoState.Dead;
            if (mode == ModeSitting) return S7aConventions.C1LocoState.Seated;
            if (mode == ModeLadder || (movement & LadderOut) != 0) return S7aConventions.C1LocoState.Ladder;
            if (mode == ModeFlying) return S7aConventions.C1LocoState.Fly;
            if (mode == ModeFalling) return S7aConventions.C1LocoState.Fall;
            if (mode == ModeJump) return S7aConventions.C1LocoState.Jump;
            if (mode == ModeCrouching) return dir == 0 ? S7aConventions.C1LocoState.CrouchIdle : S7aConventions.C1LocoState.CrouchWalk;
            if (mode == ModeStanding)
            {
                if (dir == 0) return S7aConventions.C1LocoState.Idle;
                if ((speed & SpeedVeryFast) != 0) return S7aConventions.C1LocoState.Sprint;
                if ((speed & SpeedFast) != 0) return S7aConventions.C1LocoState.Run;
                return S7aConventions.C1LocoState.Walk;
            }
            return S7aConventions.C1LocoState.None;
        }

        /// <summary>Metres per second as cm/s: rounded half away from zero, clamped to ±32,767 (NaN → 0).</summary>
        public static short Cm(double mps) { return Centi(mps); }

        /// <summary>Degrees per second as centidegrees per second: rounded half away from zero, clamped to ±32,767 (NaN → 0).</summary>
        public static short Cdeg(double degps) { return Centi(degps); }

        private static short Centi(double v)
        {
            if (double.IsNaN(v)) return 0;
            double x = Math.Round(v * 100.0, MidpointRounding.AwayFromZero);
            if (x >= 32767.0) return 32767;
            if (x <= -32767.0) return -32767;
            return (short)x;
        }

        /// <summary>A readable one-line form for logs and dumps (allocates).</summary>
        public static string Describe(ref LocoWireState s)
        {
            return "state=" + S7aConventions.C1LocoState.Name(s.State) + " flags=0x" + s.Flags.ToString("x2") +
                   " vF=" + s.VForwardCm + " vS=" + s.VStrafeCm + " vU=" + s.VUpCm + " yawRate=" + s.YawRateCdeg + " se=" + s.SeMovement;
        }
    }
}
