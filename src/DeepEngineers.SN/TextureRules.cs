using System;
using System.Globalization;
using System.IO;
using DeepEngineers.Wire;

namespace DeepEngineers.SN
{
    // S3 Subnautica side, Unity-free part 3 of 4: SE's DDS files read in place (header parsing, mip skipping), the
 // CPU half of the texture conversion (repack of the GPU-decoded _cm/_ng/_add into MarmosetUBER's _SpecTex, _BumpMap,
 // _MultiColorMask and _Illum, mip chains) and the paint colour rules (SE's colourisation of a grey texel, Subnautica's
 // ColorCustomizer specular rule). ARCHITECTURE §5.8 "Texture slots"; CONTRACT §2.5 "Texture colour space";
 // docs/recon/probe-ship-render.md §2.

 /// <summary>Pixel formats the plugin understands in an SE DDS file.</summary>
    internal enum DdsFormat
    {
        Unknown = 0, BC1, BC2, BC3, BC4, BC5, BC6H, BC7, RGBA8, BGRA8, BGRX8, R8,
    }

    /// <summary>The texture's role in an SE material (MaterialDef string order).</summary>
    internal enum TexRole { ColorMetal = 0, NormalGloss = 1, AddMaps = 2, AlphaMask = 3 }

    /// <summary>A parsed DDS header (DX10 extension included) and the layout of its mip chain.</summary>
    internal sealed class DdsInfo
    {
        public int Width, Height, MipCount;
        public DdsFormat Format;
        /// <summary>True when the file declares an sRGB format (DXGI *_SRGB); legacy FourCC files carry no colour space.</summary>
        public bool SrgbDeclared;
        public bool HasDx10;
        public uint Dxgi;
        public string FourCC = "";
        public int DataOffset;
        public long FileLength;

        public bool Compressed { get { return Format >= DdsFormat.BC1 && Format <= DdsFormat.BC7; } }
        public int BlockBytes { get { return Format == DdsFormat.BC1 || Format == DdsFormat.BC4 ? 8 : 16; } }
        public int PixelBytes { get { return Format == DdsFormat.R8 ? 1 : 4; } }

        public long MipBytes(int level)
        {
            int w = Math.Max(1, Width >> level), h = Math.Max(1, Height >> level);
            if (Compressed) return (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * BlockBytes;
            return (long)w * h * PixelBytes;
        }

        public long MipOffset(int level)
        {
            long off = DataOffset;
            for (int i = 0; i < level; i++) off += MipBytes(i);
            return off;
        }

        public long ChainBytes(int first, int count)
        {
            long n = 0;
            for (int i = first; i < first + count; i++) n += MipBytes(i);
            return n;
        }

        public string FormatName
        {
            get
            {
                return Format + (HasDx10 ? "(dxgi " + Dxgi + ")" : "(" + FourCC + ")") + (SrgbDeclared ? " sRGB" : "");
            }
        }
    }

    /// <summary>Which mip levels to read so that the largest side is ≤ the cap (SE files are full chains: skipping is free).</summary>
    internal struct SkipPlan
    {
        public int FirstLevel, Width, Height, MipCount;
        public long Offset, Length;

        public override string ToString()
        {
            return Width + "x" + Height + " mips=" + MipCount + " skipped=" + FirstLevel + " bytes=" + Length;
        }
    }

    /// <summary>DDS parsing and mip skipping (Unity-free; the worker thread reads the files).</summary>
    internal static class DdsReader
    {
        public const int HeaderBytes = 128;
        public const int Dx10HeaderBytes = 20;
        private const uint Magic = 0x20534444; // "DDS "
        private const uint FlagMipCount = 0x20000;
        private const uint PfFourCC = 0x4, PfRgb = 0x40, PfLuminance = 0x20000, PfAlpha = 0x2;
        private const uint Caps2Cubemap = 0x200, Caps2Volume = 0x200000;

        public static bool TryParse(byte[] h, int length, long fileLength, out DdsInfo info, out string error)
        {
            info = null;
            error = null;
            if (h == null || length < HeaderBytes) { error = "file shorter than a DDS header"; return false; }
            if (U32(h, 0) != Magic) { error = "not a DDS file (magic)"; return false; }
            if (U32(h, 4) != 124) { error = "DDS header size " + U32(h, 4) + " (124 expected)"; return false; }
            var d = new DdsInfo
            {
                Height = (int)U32(h, 12), Width = (int)U32(h, 16), FileLength = fileLength, DataOffset = HeaderBytes,
            };
            uint flags = U32(h, 8);
            uint mips = U32(h, 28);
            d.MipCount = (flags & FlagMipCount) != 0 && mips > 0 ? (int)mips : 1;
            uint pfFlags = U32(h, 80);
            uint fourcc = U32(h, 84);
            uint bits = U32(h, 88);
            uint rMask = U32(h, 92), gMask = U32(h, 96), bMask = U32(h, 100), aMask = U32(h, 104);
            uint caps2 = U32(h, 112);
            if ((caps2 & Caps2Cubemap) != 0) { error = "cube maps are not supported"; return false; }
            if ((caps2 & Caps2Volume) != 0) { error = "volume textures are not supported"; return false; }
            if (d.Width <= 0 || d.Height <= 0 || d.Width > 16384 || d.Height > 16384) { error = "size " + d.Width + "x" + d.Height; return false; }
            if ((pfFlags & PfFourCC) != 0)
            {
                d.FourCC = FourCCText(fourcc);
                if (d.FourCC == "DX10")
                {
                    if (length < HeaderBytes + Dx10HeaderBytes) { error = "DX10 header missing"; return false; }
                    d.HasDx10 = true;
                    d.DataOffset = HeaderBytes + Dx10HeaderBytes;
                    d.Dxgi = U32(h, 128);
                    uint dim = U32(h, 132), misc = U32(h, 136), arraySize = U32(h, 140);
                    if (dim != 3) { error = "DX10 resource dimension " + dim + " (2D only)"; return false; }
                    if ((misc & 0x4) != 0) { error = "cube maps are not supported"; return false; }
                    if (arraySize > 1) { error = "texture arrays are not supported (" + arraySize + " slices)"; return false; }
                    bool srgb;
                    d.Format = FromDxgi(d.Dxgi, out srgb);
                    d.SrgbDeclared = srgb;
                }
                else
                {
                    switch (d.FourCC)
                    {
                        case "DXT1": d.Format = DdsFormat.BC1; break;
                        case "DXT2": case "DXT3": d.Format = DdsFormat.BC2; break;
                        case "DXT4": case "DXT5": d.Format = DdsFormat.BC3; break;
                        case "ATI1": case "BC4U": d.Format = DdsFormat.BC4; break;
                        case "ATI2": case "BC5U": d.Format = DdsFormat.BC5; break;
                        default: d.Format = DdsFormat.Unknown; break;
                    }
                }
            }
            else if ((pfFlags & (PfRgb | PfLuminance | PfAlpha)) != 0)
            {
                d.FourCC = "rgb" + bits;
                if (bits == 32 && rMask == 0x000000ff && gMask == 0x0000ff00 && bMask == 0x00ff0000) d.Format = DdsFormat.RGBA8;
                else if (bits == 32 && rMask == 0x00ff0000 && gMask == 0x0000ff00 && bMask == 0x000000ff) d.Format = aMask != 0 ? DdsFormat.BGRA8 : DdsFormat.BGRX8;
                else if (bits == 8) d.Format = DdsFormat.R8;
                else d.Format = DdsFormat.Unknown;
            }
            if (d.Format == DdsFormat.Unknown) { error = "unsupported pixel format " + d.FourCC + (d.HasDx10 ? " dxgi " + d.Dxgi : ""); return false; }
            long need = d.MipOffset(d.MipCount);
            if (fileLength < need) { error = "file of " + fileLength + " B is shorter than its " + d.MipCount + "-level chain (" + need + " B)"; return false; }
            info = d;
            return true;
        }

        /// <summary>DXGI_FORMAT codes SE ships (BC7_UNORM 98 / BC7_UNORM_SRGB 99 for nearly every map; the census is in S3-STATUS).</summary>
        public static DdsFormat FromDxgi(uint dxgi, out bool srgb)
        {
            srgb = false;
            switch (dxgi)
            {
                case 70: case 71: return DdsFormat.BC1;
                case 72: srgb = true; return DdsFormat.BC1;
                case 73: case 74: return DdsFormat.BC2;
                case 75: srgb = true; return DdsFormat.BC2;
                case 76: case 77: return DdsFormat.BC3;
                case 78: srgb = true; return DdsFormat.BC3;
                case 79: case 80: return DdsFormat.BC4;
                case 82: case 83: return DdsFormat.BC5;
                case 94: case 95: return DdsFormat.BC6H;
                case 97: case 98: return DdsFormat.BC7;
                case 99: srgb = true; return DdsFormat.BC7;
                case 27: case 28: return DdsFormat.RGBA8;
                case 29: srgb = true; return DdsFormat.RGBA8;
                case 87: return DdsFormat.BGRA8;
                case 91: srgb = true; return DdsFormat.BGRA8;
                case 88: return DdsFormat.BGRX8;
                case 93: srgb = true; return DdsFormat.BGRX8;
                case 61: return DdsFormat.R8;
                default: return DdsFormat.Unknown;
            }
        }

        /// <summary>
 /// Whether the plugin samples this texture as sRGB (Unity <c>linear = false</c>): what SE's shader sees. A DX10 file
 /// says it itself (SE creates its views in the file's format: BC7_UNORM_SRGB is decoded on sampling, so `_add`
 /// stored as 99 reaches SE's shader decoded); a legacy file has no colour space: the colour-metal map is sRGB, the
 /// others linear. Alpha is never converted either way.
 /// </summary>
        public static bool SampleAsSrgb(DdsInfo d, TexRole role)
        {
            if (d.HasDx10) return d.SrgbDeclared;
            return role == TexRole.ColorMetal;
        }

        /// <summary>
 /// Levels to skip so that max(width, height) ≤ <paramref name="maxSide"/>, keeping at least one level, and keeping
 /// the top level of a block-compressed texture a multiple of 4 on both sides (D3D11 rule).
 /// </summary>
        public static SkipPlan PlanSkip(DdsInfo d, int maxSide)
        {
            int k = 0;
            while (k < d.MipCount - 1 && Math.Max(d.Width >> k, d.Height >> k) > maxSide) k++;
            if (d.Compressed)
                while (k > 0 && (((d.Width >> k) & 3) != 0 || ((d.Height >> k) & 3) != 0)) k--;
            var p = new SkipPlan
            {
                FirstLevel = k, Width = Math.Max(1, d.Width >> k), Height = Math.Max(1, d.Height >> k), MipCount = d.MipCount - k,
                Offset = d.MipOffset(k),
            };
            p.Length = d.ChainBytes(k, p.MipCount);
            return p;
        }

        /// <summary>Reads the header and the planned levels of a file (worker thread). <paramref name="onlyFirstLevel"/>: just level k.</summary>
        public static bool TryRead(string path, int maxSide, bool onlyFirstLevel, out DdsInfo info, out SkipPlan plan, out byte[] data, out string error)
        {
            info = null; plan = default(SkipPlan); data = null; error = null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                {
                    var head = new byte[HeaderBytes + Dx10HeaderBytes];
                    int got = ReadFully(fs, head, 0, head.Length);
                    if (!TryParse(head, got, fs.Length, out info, out error)) return false;
                    plan = PlanSkip(info, maxSide);
                    if (onlyFirstLevel) { plan.MipCount = 1; plan.Length = info.MipBytes(plan.FirstLevel); }
                    if (plan.Length > int.MaxValue) { error = "chain too large"; return false; }
                    data = new byte[plan.Length];
                    fs.Position = plan.Offset;
                    int n = ReadFully(fs, data, 0, data.Length);
                    if (n != data.Length) { error = "short read " + n + "/" + data.Length + " B"; return false; }
                    return true;
                }
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static int ReadFully(Stream s, byte[] buf, int off, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, off + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }

        private static string FourCCText(uint v)
        {
            var c = new char[4];
            for (int i = 0; i < 4; i++)
            {
                char ch = (char)((v >> (8 * i)) & 0xff);
                c[i] = ch >= ' ' && ch < 127 ? ch : '?';
            }
            return new string(c);
        }
    }

    /// <summary>The CPU repack's output: four RGBA32 mip chains (largest level first) and what they contain.</summary>
    internal sealed class DerivedMaps
    {
        public int Width, Height, MipCount;
        /// <summary>_SpecTex: RGB = sRGB-encoded lerp(0.04, cm, metal) (decoded values), A = gloss (_ng.a).</summary>
        public byte[] Spec;
        /// <summary>_BumpMap: DXT5nm layout, X in A and Y in G (R = 255 so the RG·A unpack also reads X), linear.</summary>
        public byte[] Bump;
        /// <summary>_MultiColorMask: R = paint mask (_add.a reduced on metal as SE does), linear.</summary>
        public byte[] Mask;
        /// <summary>_Illum: RGB = sRGB-encoded cm × emissive (_add.g − 1/255), A = 0.</summary>
        public byte[] Illum;
        public float EmissiveMax, PaintMax, MetalMean;
    }

    /// <summary>
 /// The CPU half of the texture conversion (worker thread), on level-0 RGBA32 readbacks of linear render targets:
 /// values are what SE's shader samples (sRGB maps decoded). Missing maps: flat normal and gloss 0.5, no paint, no
 /// emission.
 /// </summary>
    internal static class TextureRepack
    {
        public const float SpecDielectric = 0.04f;
        public const float EmissiveNoise = 1f / 255f;
        public const float MetalThreshold = 0.4f, MetalThresholdMultiply = 0.5f;

        private static readonly float[] SrgbToLinearLut = BuildLut();

        private static float[] BuildLut()
        {
            var t = new float[256];
            for (int i = 0; i < 256; i++) t[i] = (float)SrgbToLinear(i / 255.0);
            return t;
        }

        public static double SrgbToLinear(double c)
        {
            if (c <= 0.04045) return c / 12.92;
            return Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        public static double LinearToSrgb(double c)
        {
            if (c <= 0) return 0;
            if (c <= 0.0031308) return c * 12.92;
            if (c < 1) return 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            return 1.0;
        }

        public static byte ToByte(double x)
        {
            int v = (int)Math.Round(x * 255.0);
            return (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
        }

        /// <summary>S3.N1d: the largest |8-bit/255 − float| allowed between the conversion target and the float reference (one level).</summary>
        public const double FidelityMaxError = 1.0 / 255.0 + 1e-4;

        /// <summary>
 /// S3.N1d: the 8-bit readback of a blit (RGBA bytes) against the float readback of
 /// the same blit (RGBA floats, clamped to [0, 1] as an 8-bit UNorm target would): largest and mean absolute
 /// difference over every channel. False when the arrays are missing or their sizes disagree.
 /// </summary>
        public static bool FidelityError(byte[] b8, float[] f, out double max, out double mean, out int texels)
        {
            max = mean = 0;
            texels = 0;
            if (b8 == null || f == null || b8.Length == 0 || b8.Length != f.Length) return false;
            double sum = 0;
            for (int i = 0; i < b8.Length; i++)
            {
                double fv = f[i];
                if (double.IsNaN(fv)) return false;
                if (fv < 0) fv = 0;
                if (fv > 1) fv = 1;
                double d = Math.Abs(b8[i] / 255.0 - fv);
                sum += d;
                if (d > max) max = d;
            }
            mean = sum / b8.Length;
            texels = b8.Length / 4;
            return true;
        }

        /// <summary>
 /// SE's paint-mask reduction on metallic texels (unless the material is metalness-colourable): full mask up to
 /// metalness 0.4, falling linearly to none at 0.7.
 /// </summary>
        public static float PaintOnMetal(float paint, float metal)
        {
            float over = Math.Max(0f, Math.Min(1f, metal - MetalThreshold));
            float k = 1f - over / ((1f - MetalThreshold) * MetalThresholdMultiply);
            if (k < 0) k = 0;
            if (k > 1) k = 1;
            return paint * k;
        }

        /// <summary>
 /// Builds the four maps from level-0 RGBA32 readbacks (row order as read back: unchanged). <paramref name="cm"/>
 /// is required; <paramref name="ng"/> and <paramref name="add"/> may be null. <paramref name="normalFlipY"/>
 /// inverts G (the "bump faces the sun" capture decides the convention).
 /// </summary>
        public static DerivedMaps Build(byte[] cm, byte[] ng, byte[] add, int w, int h, bool normalFlipY)
        {
            int n = w * h;
            if (cm == null || cm.Length < n * 4) throw new ArgumentException("colour-metal readback missing or short");
            if (ng != null && ng.Length < n * 4) ng = null;
            if (add != null && add.Length < n * 4) add = null;
            // S10 round 3 E (S10AllocCuts): level 0 is written straight into each map's mip chain (no level-0 copy per map,
 // 16 MB less garbage per 1024² set); the bytes of every level are identical to the copy path's (S3Tests)
            bool s10 = S10AllocCuts.On;
            int fullMips = FullMipCount(w, h);
            long levelBytes = s10 ? ChainBytes(w, h, fullMips) : (long)n * 4;
            var spec = new byte[levelBytes];
            var bump = new byte[levelBytes];
            var mask = new byte[levelBytes];
            var illum = new byte[levelBytes];
            float emax = 0, pmax = 0;
            double msum = 0;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                float r = cm[o] / 255f, g = cm[o + 1] / 255f, b = cm[o + 2] / 255f, metal = cm[o + 3] / 255f;
                msum += metal;
                spec[o] = ToByte(LinearToSrgb(SpecDielectric + (r - SpecDielectric) * metal));
                spec[o + 1] = ToByte(LinearToSrgb(SpecDielectric + (g - SpecDielectric) * metal));
                spec[o + 2] = ToByte(LinearToSrgb(SpecDielectric + (b - SpecDielectric) * metal));
                spec[o + 3] = ng != null ? ng[o + 3] : (byte)128;
                byte nx = ng != null ? ng[o] : (byte)128, ny = ng != null ? ng[o + 1] : (byte)128;
                if (normalFlipY) ny = (byte)(255 - ny);
                bump[o] = 255;
                bump[o + 1] = ny;
                bump[o + 2] = ny;
                bump[o + 3] = nx;
                float paint = add != null ? PaintOnMetal(add[o + 3] / 255f, metal) : 0f;
                if (paint > pmax) pmax = paint;
                mask[o] = ToByte(paint);
                mask[o + 1] = 0;
                mask[o + 2] = 0;
                mask[o + 3] = 255;
                float em = add != null ? Math.Max(0f, add[o + 1] / 255f - EmissiveNoise) : 0f;
                if (em > emax) emax = em;
                illum[o] = ToByte(LinearToSrgb(r * em));
                illum[o + 1] = ToByte(LinearToSrgb(g * em));
                illum[o + 2] = ToByte(LinearToSrgb(b * em));
                illum[o + 3] = 0;
            }
            int mips = fullMips;
            var dm = new DerivedMaps
            {
                Width = w, Height = h,
                Spec = s10 ? FillMips(spec, w, h, true, fullMips) : BuildMips(spec, w, h, true, out mips),
                Bump = s10 ? FillMips(bump, w, h, false, fullMips) : BuildMips(bump, w, h, false, out mips),
                Mask = s10 ? FillMips(mask, w, h, false, fullMips) : BuildMips(mask, w, h, false, out mips),
                Illum = s10 ? FillMips(illum, w, h, true, fullMips) : BuildMips(illum, w, h, true, out mips),
                EmissiveMax = emax, PaintMax = pmax, MetalMean = n > 0 ? (float)(msum / n) : 0f,
            };
            dm.MipCount = s10 ? fullMips : mips;
            return dm;
        }

        /// <summary>Number of levels of a full chain down to 1×1 (Unity's default for a mipmapped texture).</summary>
        public static int FullMipCount(int w, int h)
        {
            int m = 1;
            int s = Math.Max(w, h);
            while (s > 1) { s >>= 1; m++; }
            return m;
        }

        /// <summary>Bytes of an RGBA32 chain of <paramref name="mips"/> levels.</summary>
        public static long ChainBytes(int w, int h, int mips)
        {
            long n = 0;
            for (int i = 0; i < mips; i++) n += (long)Math.Max(1, w >> i) * Math.Max(1, h >> i) * 4;
            return n;
        }

        /// <summary>
 /// A full RGBA32 mip chain from level 0 (2×2 box filter; an odd or 1-pixel side clamps). For sRGB-encoded maps the
 /// RGB average is taken on decoded values; alpha is always linear.
 /// </summary>
        public static byte[] BuildMips(byte[] level0, int w, int h, bool srgbRgb, out int mips)
        {
            mips = FullMipCount(w, h);
            var chain = new byte[ChainBytes(w, h, mips)];
            Buffer.BlockCopy(level0, 0, chain, 0, w * h * 4);
            return FillMips(chain, w, h, srgbRgb, mips);
        }

        /// <summary>
 /// Levels 1.. <paramref name="mips"/> − 1 of a chain whose level 0 is already in place (<see cref="BuildMips"/>'s
 /// filter; S10 round 3 E writes level 0 into the chain directly). Returns <paramref name="chain"/>.
 /// </summary>
        public static byte[] FillMips(byte[] chain, int w, int h, bool srgbRgb, int mips)
        {
            int srcOff = 0, sw = w, sh = h;
            int dstOff = w * h * 4;
            for (int level = 1; level < mips; level++)
            {
                int dw = Math.Max(1, sw >> 1), dh = Math.Max(1, sh >> 1);
                for (int y = 0; y < dh; y++)
                {
                    int y0 = Math.Min(sh - 1, y * 2), y1 = Math.Min(sh - 1, y * 2 + 1);
                    for (int x = 0; x < dw; x++)
                    {
                        int x0 = Math.Min(sw - 1, x * 2), x1 = Math.Min(sw - 1, x * 2 + 1);
                        int a = srcOff + (y0 * sw + x0) * 4, b = srcOff + (y0 * sw + x1) * 4;
                        int c = srcOff + (y1 * sw + x0) * 4, d = srcOff + (y1 * sw + x1) * 4;
                        int o = dstOff + (y * dw + x) * 4;
                        for (int ch = 0; ch < 4; ch++)
                        {
                            if (srgbRgb && ch < 3)
                            {
                                double lin = (SrgbToLinearLut[chain[a + ch]] + SrgbToLinearLut[chain[b + ch]] + SrgbToLinearLut[chain[c + ch]] + SrgbToLinearLut[chain[d + ch]]) * 0.25;
                                chain[o + ch] = ToByte(LinearToSrgb(lin));
                            }
                            else
                            {
                                chain[o + ch] = (byte)((chain[a + ch] + chain[b + ch] + chain[c + ch] + chain[d + ch] + 2) >> 2);
                            }
                        }
                    }
                }
                srcOff = dstOff;
                dstOff += dw * dh * 4;
                sw = dw;
                sh = dh;
            }
            return chain;
        }
    }

    /// <summary>A colour as four floats (no Unity type).</summary>
    internal struct Rgba
    {
        public float R, G, B, A;
        public Rgba(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }

        public override string ToString()
        {
            var ci = CultureInfo.InvariantCulture;
            return "(" + R.ToString("0.####", ci) + "," + G.ToString("0.####", ci) + "," + B.ToString("0.####", ci) + "," + A.ToString("0.####", ci) + ")";
        }
    }

    /// <summary>
 /// Paint (ARCHITECTURE §5.8 "Paint"; probe-ship-render §2.4): SE's ColorMaskHSV (hue, saturation offset, value
 /// offset) applied the way SE colourises a texel (the texel's sRGB value kept, hue and saturation replaced, offsets
 /// added and clamped, back to linear), evaluated on SE's standard grey (sRGB 144/255); <c>_Color</c> is that result
 /// divided by the grey, in linear, handed to Unity as the gamma-space value Unity converts back (linear colour
 /// space); <c>_SpecColor</c> follows <c>ColorCustomizer.SetColors</c>: lerp(colour, lerp(dark 0.39, light 1.0,
 /// luminance), 1 − 0.5·luminance) on the gamma-space colour. Approximate until S3.C13 compares with SE's export.
 /// S7t-colour-mask: the hue/sat/val arguments are SE's shader key, i.e. the part's ColorMaskHSV
 /// plus SE's run-time ColorizeHSV (0, 0.8, -0.1) (<see cref="ColorMaskRules.ShaderKey"/>, VERIFIED): the caller adds it
 /// (MarmosetMaterials.ApplyMaps). This
 /// tint is now the fallback (approx mode, a set not converted yet, the bake budget full); block slots in use get SE's
 /// colour per texel (ColorMaskPaint).
 /// </summary>
    internal static class PaintRules
    {
        public const double GreySrgb = 144.0 / 255.0;
        public const float SpecDark = 0.39f, SpecLight = 1f;

        public static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            h = h - Math.Floor(h);
            double hh = h * 6.0;
            int i = (int)Math.Floor(hh);
            double f = hh - i;
            double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
        }

        private static double Sat(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }

        /// <summary>SE's colourised grey texel (linear RGB) for a shader key (ColorMaskHSV + ColorizeHSV; see the class summary).</summary>
        public static void ColouriseGrey(float hue, float satOffset, float valOffset, out double r, out double g, out double b)
        {
            double hh = Sat(hue);
            double ss = Sat(satOffset);
            double vv = Sat(GreySrgb + valOffset);
            double sr, sg, sb;
            HsvToRgb(hh, ss, vv, out sr, out sg, out sb);
            r = TextureRepack.SrgbToLinear(sr);
            g = TextureRepack.SrgbToLinear(sg);
            b = TextureRepack.SrgbToLinear(sb);
        }

        /// <summary>Unity's gamma-space value whose linear conversion is <paramref name="linear"/> (values above 1 by the 2.2 power).</summary>
        public static double GammaFor(double linear)
        {
            if (linear <= 0) return 0;
            if (linear <= 0.0031308) return linear * 12.92;
            if (linear < 1) return 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
            return Math.Pow(linear, 1.0 / 2.2);
        }

        /// <summary>The <c>_Color</c> to set (gamma-space RGBA, A = 1) for a part colour (clamped at 2× the grey).</summary>
        public static Rgba MaterialColour(float hue, float satOffset, float valOffset)
        {
            double r, g, b;
            ColouriseGrey(hue, satOffset, valOffset, out r, out g, out b);
            double grey = TextureRepack.SrgbToLinear(GreySrgb);
            return new Rgba((float)GammaFor(Math.Min(2.0, r / grey)), (float)GammaFor(Math.Min(2.0, g / grey)), (float)GammaFor(Math.Min(2.0, b / grey)), 1f);
        }

        public static float Luminance(Rgba c) { return 0.299f * c.R + 0.587f * c.G + 0.114f * c.B; }

        /// <summary><c>ColorCustomizer.SetColors</c>' specular rule for one colour.</summary>
        public static Rgba SpecularFor(Rgba main)
        {
            float lum = Luminance(main);
            float tl = SpecDark + (SpecLight - SpecDark) * (lum < 0 ? 0 : (lum > 1 ? 1 : lum)); // Color.Lerp clamps t
            float k = 1f - 0.5f * lum;
            if (k < 0) k = 0;
            if (k > 1) k = 1;
            return new Rgba(main.R + (tl - main.R) * k, main.G + (tl - main.G) * k, main.B + (tl - main.B) * k, main.A + (1f - main.A) * k);
        }
    }
}
