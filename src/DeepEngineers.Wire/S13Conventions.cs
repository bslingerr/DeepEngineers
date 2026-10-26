using System;
using System.Globalization;
using System.Text;

namespace DeepEngineers.Wire
{
    // =================================================================================================================
 // S13 "Character and animation v2" (ROADMAP §14 "### S13"; docs/S13-BRIEF.
 // saw in his own play. What both plugins (and the analyzer twin) must agree on is pinned here on its own (layout 1.0
 // and every earlier pin unchanged):
 // - the C1 S13 extension (bytes 0xC0-0xC7 of the C1 pad, free before S13: swim 0x94-0x9F, loco 0xA0-0xAF, interior
 // 0xB0-0xBF): the character's velocity in its own look frame (the body's yaw and the head's pitch), the frame SE now
 // moves the third-person swimmer in (point 5) and the frame Subnautica's ArmsController reads its swim animation
 // from
 // - the near-field rules of the mirrored lights (point 4): SE's spot shadow near plane
 // and the character lamp's
 // and the
 // least-squares fit of Unity's built-in attenuation (HYPOTHESIS: 1 / (1 + 25 (d/r)^2), faded to 0 from (d/r)^2 = 0.8)
 // to SE's spot curve (VERIFIED Content\Shaders\Lighting\LightingModel.hlsli GetAttenuation, read in place:
 // (1 - x^4)^3 / (1 + falloff x^2), x = d / range).
 // The canonical text below is pinned (FNV-1a 64; a changed number changes the hash and turns S13ConventionsTests red);
 // the Python twin is tests/s13/analyze_s13.py (same text, same hash, same fit, checked by its --selftest).
 // =================================================================================================================

 /// <summary>The C1 S13 extension, decoded (CONTRACT §5.4 "S13 conventions").</summary>
    public struct S13CharWireState
    {
        public byte Version, Flags;
        /// <summary>The character's velocity in its look frame, cm/s: + right, + up, + forward.</summary>
        public short VRightCm, VUpCm, VForwardCm;

        public bool Has(byte flag) { return (Flags & flag) != 0; }
        public double RightMs { get { return VRightCm / 100.0; } }
        public double UpMs { get { return VUpCm / 100.0; } }
        public double ForwardMs { get { return VForwardCm / 100.0; } }
    }

    public static class S13Conventions
    {
        public const string Header = "ext s13 char 2";
        /// <summary>FNV-1a 64 of <see cref="CanonicalText"/> as pinned in CONTRACT §5.4 "S13 conventions".</summary>
        public const ulong PinnedHash = 0xdbc2fc8fe4aa4ce7UL;
        public const int CanonicalLines = 43, CanonicalBytes = 2468;

        /// <summary>The C1 extension's place in the C1 pad.</summary>
        public static class C1
        {
            public const int Offset = 0xC0, Size = 8;
            public const byte Version = 1;
            public const int OffVersion = 0xC0, OffFlags = 0xC1, OffRight = 0xC2, OffUp = 0xC4, OffForward = 0xC6;
        }

        /// <summary>The C1 extension's flags.</summary>
        public static class C1Flags
        {
            /// <summary>The look frame was read from the character (else the velocity is 0).</summary>
            public const byte Valid = 1;
            /// <summary>The swim model moved the character this tick along this look frame (third person, S13 point 5).</summary>
            public const byte BodyBasis = 2;
            /// <summary>The swim model set the velocity this tick (C1 swim extension flag Controls).</summary>
            public const byte Swimming = 4;
        }

        /// <summary>Near-field shadow rules of the mirrored lights (millimetres, integers in the text).</summary>
        public static class Light
        {
            /// <summary>The character's own lamp: its shadow near plane (SE ignores the character, its weapon and its seat).</summary>
            public const int NearPlaneCharacterSpotMm = 1000;
            /// <summary>Every other spot: SE's own spot shadow near plane.</summary>
            public const int NearPlaneBlockSpotMm = 500;
            /// <summary>Unity's built-in attenuation constant (HYPOTHESIS, the 1 / (1 + 25 x^2) curve).</summary>
            public const int UnityAttenuationK = 25;
            /// <summary>Unity's fade to zero from this squared normalised distance, per mille (HYPOTHESIS).</summary>
            public const int UnityFadeStartPermille = 800;
            /// <summary>The fit: samples of x = d / SE range over [0, span], the range factor searched in [min, max] by step (per mille).</summary>
            public const int FitSamples = 301, FitSpanPermille = 1500, FitKMinPermille = 1000, FitKMaxPermille = 4000, FitKStepPermille = 10;
            /// </summary>
            public const int EnergySpanPermille = 1000;
        }

        /// <summary>The held-item swim blend (point 6): these canon1 bones and their descendants take the swim retarget.</summary>
        public static readonly string[] BlendRoots = { "SE_RigLThigh", "SE_RigRThigh" };

        private static readonly string[][] C1Fields =
        {
            new[] { "version", "0xC0", "1", "u8" },
            new[] { "flags", "0xC1", "1", "u8" },
            new[] { "vRightCm", "0xC2", "2", "i16" },
            new[] { "vUpCm", "0xC4", "2", "i16" },
            new[] { "vForwardCm", "0xC6", "2", "i16" },
        };

        private static readonly string[] FormatLines =
        {
            "c1.frame look = GetHeadMatrix(includeY false, includeX true): body yaw, head pitch",
            "c1.velocity cm/s round half away from zero, clamp -32767..32767",
            "c1.idle version 1 flags 0 velocity 0 when no character",
            "swim.basis firstPerson camera; thirdPerson look frame",
            "anim.velocity underwater move_speed_xyz = look-frame velocity (ArmsController.GetRelativeVelocity)",
            "source.grounded grounded cancels the swim source only at the surface (eye >= 0)",
            "source.held swimming with a held tool, gun or block = Blend (legs Retarget, the rest SeBones)",
            "light.nearPlane characterSpot 1000 mm, other spots 500 mm, points unchanged",
            "light.fit characterSpot range x k, intensity x energy factor; other kinds unchanged",
            "tool.colour PartAdd colorMaskHSV = the tool entity's Render.ColorMaskHsv",
            "tool.row a character child part is hidden until its first C2 row",
            "texture.root a rooted .mwm resolves relative textures in its own content root first (MyMwmUtils.GetFullMwmContentPath)",
            "ghost.material Standard Fade, the slot's _BumpMap, tint green/red",
            "ghost.alpha 450 permille, 120 permille with the camera inside the bounds grown by 300 mm",
            "arms.splash the dive window never shows the diver view model under fpbody=se",
            "source.ladder a ladder (C1 loco state or flag Ladder) is never a swim source",
            "ghost.probe Standard Fade, else the glass clone, kept when its Blit over (0,0,0,0) is translucent (alpha < 0.9, glass also tinted); else the flat S7a material",
            "arms.leak counted at onPreCull: a view model renderer enabled and active under ForceOff",
            "sounds.dive WaterAmbience diveStart or diveStartSplash (-vUp > 5.5 m/s) when the eye goes under, PlayReachSurfaceSound when it comes out; dives 1 s apart",
        };

        private static string _text;
        private static ulong _hash;

        private static void C(StringBuilder sb, string group, string name, int v)
        {
            sb.Append("const ").Append(group).Append(' ').Append(name).Append(' ').Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        /// <summary>
 /// The canonical text (CONTRACT §5.4 "S13 conventions"; ASCII, '\n' after every line): the header, the C1 group,
 /// the C1 field lines, the flags, the light group, the blend roots, the format lines sorted ordinally.
 /// </summary>
        public static string CanonicalText()
        {
            if (_text != null) return _text;
            var sb = new StringBuilder(2048);
            sb.Append(Header).Append('\n');
            C(sb, "C1", "Offset", C1.Offset);
            C(sb, "C1", "Size", C1.Size);
            C(sb, "C1", "Version", C1.Version);
            foreach (string[] f in C1Fields)
                sb.Append("field s13 ").Append(f[0]).Append(' ').Append(f[1]).Append(' ').Append(f[2]).Append(' ').Append(f[3]).Append('\n');
            C(sb, "C1Flags", "Valid", C1Flags.Valid);
            C(sb, "C1Flags", "BodyBasis", C1Flags.BodyBasis);
            C(sb, "C1Flags", "Swimming", C1Flags.Swimming);
            C(sb, "Light", "NearPlaneCharacterSpotMm", Light.NearPlaneCharacterSpotMm);
            C(sb, "Light", "NearPlaneBlockSpotMm", Light.NearPlaneBlockSpotMm);
            C(sb, "Light", "UnityAttenuationK", Light.UnityAttenuationK);
            C(sb, "Light", "UnityFadeStartPermille", Light.UnityFadeStartPermille);
            C(sb, "Light", "FitSamples", Light.FitSamples);
            C(sb, "Light", "FitSpanPermille", Light.FitSpanPermille);
            C(sb, "Light", "FitKMinPermille", Light.FitKMinPermille);
            C(sb, "Light", "FitKMaxPermille", Light.FitKMaxPermille);
            C(sb, "Light", "FitKStepPermille", Light.FitKStepPermille);
            C(sb, "Light", "EnergySpanPermille", Light.EnergySpanPermille);
            foreach (string r in BlendRoots) sb.Append("blend root ").Append(r).Append('\n');
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

    /// <summary>
 /// Codec of the C1 S13 extension (bytes 0xC0-0xC7 of <see cref="SeFrame"/>): 0xC0 u8 version (1), 0xC1 u8 flags
 /// (<see cref="S13Conventions.C1Flags"/>), 0xC2 i16 vRightCm, 0xC4 i16 vUpCm, 0xC6 i16 vForwardCm. A reader without it
 /// sees pad bytes; version 0 = an SE build before S13 (Subnautica then keeps the S5a input rule).
 /// </summary>
    public static unsafe class S13CharWire
    {
        public const int Offset = S13Conventions.C1.Offset, Size = S13Conventions.C1.Size;
        public const byte Version = S13Conventions.C1.Version;

        public static void Write(ref SeFrame f, ref S13CharWireState s)
        {
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                b[S13Conventions.C1.OffVersion] = Version;
                b[S13Conventions.C1.OffFlags] = s.Flags;
                *(short*)(b + S13Conventions.C1.OffRight) = s.VRightCm;
                *(short*)(b + S13Conventions.C1.OffUp) = s.VUpCm;
                *(short*)(b + S13Conventions.C1.OffForward) = s.VForwardCm;
            }
        }

        /// <summary>Zeroes the 8 bytes (version 0: the frame reads as written by an SE build before S13).</summary>
        public static void Erase(ref SeFrame f)
        {
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                for (int k = 0; k < Size; k++) b[Offset + k] = 0;
            }
        }

        public static byte ReadVersion(ref SeFrame f)
        {
            fixed (uint* p = &f.seq) return ((byte*)p)[S13Conventions.C1.OffVersion];
        }

        /// <summary>Decodes a version-1 extension; false (and default) otherwise.</summary>
        public static bool TryRead(ref SeFrame f, out S13CharWireState s)
        {
            s = default(S13CharWireState);
            fixed (uint* p = &f.seq)
            {
                byte* b = (byte*)p;
                s.Version = b[S13Conventions.C1.OffVersion];
                if (s.Version != Version) return false;
                s.Flags = b[S13Conventions.C1.OffFlags];
                s.VRightCm = *(short*)(b + S13Conventions.C1.OffRight);
                s.VUpCm = *(short*)(b + S13Conventions.C1.OffUp);
                s.VForwardCm = *(short*)(b + S13Conventions.C1.OffForward);
                return true;
            }
        }

        /// <summary>m/s → cm/s, rounded half away from zero, clamped to ±32,767 (NaN → 0).</summary>
        public static short Cm(double ms)
        {
            if (double.IsNaN(ms)) return 0;
            double cm = Math.Round(ms * 100.0, MidpointRounding.AwayFromZero);
            if (cm > 32767) return 32767;
            if (cm < -32767) return -32767;
            return (short)cm;
        }

        /// <summary>
 /// The look-frame components of a world velocity (any consistent space): right·v, up·v, forward·v with the frame's
 /// axes as given (unit, orthogonal).
 /// </summary>
        public static S13CharWireState Compose(byte flags, double vx, double vy, double vz,
                                               double rx, double ry, double rz, double ux, double uy, double uz, double fx, double fy, double fz)
        {
            var s = new S13CharWireState { Version = Version, Flags = flags };
            if ((flags & S13Conventions.C1Flags.Valid) == 0) return s;
            s.VRightCm = Cm(vx * rx + vy * ry + vz * rz);
            s.VUpCm = Cm(vx * ux + vy * uy + vz * uz);
            s.VForwardCm = Cm(vx * fx + vy * fy + vz * fz);
            return s;
        }
    }

    /// <summary>
 /// S13 point 4: Unity's built-in light attenuation (HYPOTHESIS model) fitted to SE's spot curve (VERIFIED shader). Pure;
 /// the Python twin computes the same numbers (tests/s13/analyze_s13.py).
 /// </summary>
    public static class S13LightFit
    {
        /// <summary>Unity's built-in point/spot attenuation at x = d / range (HYPOTHESIS: 1 / (1 + 25 x^2), faded from x^2 = 0.8 to 0 at 1).</summary>
        public static double Unity(double x)
        {
            if (!(x >= 0)) return x < 0 ? 1.0 : 0.0;
            double v = x * x;
            if (v >= 1.0) return 0.0;
            double a = 1.0 / (1.0 + S13Conventions.Light.UnityAttenuationK * v);
            double fade = S13Conventions.Light.UnityFadeStartPermille / 1000.0;
            if (v >= fade) a *= 1.0 - (v - fade) / (1.0 - fade);
            return a;
        }

        /// <summary>SE's spot attenuation, hard × diffuse parts (LightingModel.hlsli GetAttenuation), at x = d / range.</summary>
        public static double Se(double x, double falloff)
        {
            if (!(x >= 0)) return 1.0;
            if (x >= 1.0) return 0.0;
            double dq = x * x;
            double h = 1.0 - dq * dq;
            return h * h * h / (1.0 + Math.Max(0.0, falloff) * dq);
        }

        /// <summary>Unity's curve with its range scaled by <paramref name="k"/>, at the SE-normalised distance x.</summary>
        public static double UnityScaled(double x, double k) { return Unity(x / k); }

        /// <summary>The mean squared difference over the fit samples of x ∈ [0, span].</summary>
        public static double Error(double k, double falloff)
        {
            int n = S13Conventions.Light.FitSamples;
            double span = S13Conventions.Light.FitSpanPermille / 1000.0;
            double sum = 0;
            for (int j = 0; j < n; j++)
            {
                double x = span * j / (n - 1);
                double d = UnityScaled(x, k) - Se(x, falloff);
                sum += d * d;
            }
            return sum / n;
        }

        /// <summary>The range factor k minimising <see cref="Error"/> on the per-mille grid (the first minimum wins).</summary>
        public static double RangeFactor(double falloff)
        {
            if (double.IsNaN(falloff) || falloff < 0) falloff = 0;
            int best = S13Conventions.Light.FitKMinPermille;
            double bestE = double.MaxValue;
            for (int km = S13Conventions.Light.FitKMinPermille; km <= S13Conventions.Light.FitKMaxPermille; km += S13Conventions.Light.FitKStepPermille)
            {
                double e = Error(km / 1000.0, falloff);
                if (e < bestE - 1e-15) { bestE = e; best = km; }
            }
            return best / 1000.0;
        }

        /// <summary>
 /// The intensity factor that keeps the mean of the curve over x ∈ [0, energy span] what it was unfitted (k = 1): the

 /// </summary>
        public static double EnergyFactor(double k)
        {
            if (!(k > 0)) return 1.0;
            int n = S13Conventions.Light.FitSamples;
            double span = S13Conventions.Light.EnergySpanPermille / 1000.0;
            double a = 0, b = 0;
            for (int j = 0; j < n; j++)
            {
                double x = span * j / (n - 1);
                a += Unity(x);
                b += UnityScaled(x, k);
            }
            return b > 0 ? a / b : 1.0;
        }

        /// <summary>The shadow length of a caster of size <paramref name="sizeM"/> at <paramref name="casterM"/> from the light, on a surface at <paramref name="surfaceM"/> (similar triangles).</summary>
        public static double ShadowLength(double sizeM, double casterM, double surfaceM)
        {
            if (!(casterM > 0) || !(surfaceM > casterM)) return double.NaN;
            return sizeM * surfaceM / casterM;
        }
    }
}
