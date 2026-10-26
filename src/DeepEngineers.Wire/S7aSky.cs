namespace DeepEngineers.Wire
{
    /// <summary>
 /// The S7a sky pole in C6 (CONTRACT §5.4 "S7a conventions": C6 pad bytes 0x3C-0x3F, first written by S7a): i16
 /// <c>poleAzCdeg</c> at 0x3C and i16 <c>poleElCdeg</c> at 0x3E, the sun's rotation pole in SN axes at the camera
 /// (<c>q = M_sn × W_sn</c>, az = atan2(q.x, q.z), el = asin(q.y), centidegrees rounded half away from zero; see
 /// <see cref="SunMath"/>). Valid only while C6 <c>flags</c> bit 2 (<c>S7aConventions.C6FlagsExt.SkyPoleValid</c>) is
 /// set. Layout 1.0 describes the 4 bytes as <c>private uint pad_3c</c>: a reader without S7a sees pad.
 /// </summary>
    public static unsafe class S7aSky
    {
        public const int Offset = 0x3C, Size = 4;
        public const int OffAz = 0x3C, OffEl = 0x3E;

        public static void WritePole(ref SeTime t, short azCdeg, short elCdeg)
        {
            fixed (uint* p = &t.seq)
            {
                byte* b = (byte*)p;
                *(short*)(b + OffAz) = azCdeg;
                *(short*)(b + OffEl) = elCdeg;
            }
        }

        public static void ReadPole(ref SeTime t, out short azCdeg, out short elCdeg)
        {
            fixed (uint* p = &t.seq)
            {
                byte* b = (byte*)p;
                azCdeg = *(short*)(b + OffAz);
                elCdeg = *(short*)(b + OffEl);
            }
        }

        /// <summary>The pad bytes back to zero (no pole).</summary>
        public static void ClearPole(ref SeTime t) { WritePole(ref t, 0, 0); }
    }
}
