using System;
using System.IO;
using System.IO.Compression;

namespace DeepEngineers.Terrain
{
    /// <summary>
 /// S8a Stage F2: a minimal PNG reader for the world mod's planet maps (the ring field's base when SE's in-memory maps are
 /// not reachable, and the tests): non-interlaced, 8- or 16-bit grey, 8-bit RGB or RGBA, the five scanline filters. The
 /// zlib stream is inflated by <see cref="DeflateStream"/> after its 2-byte header (the Adler-32 trailer is not checked; the
 /// callers hash what they read).
 /// </summary>
    public static class RingPng
    {
        public sealed class Image
        {
            public int Width, Height, Channels, BitDepth;
            /// <summary>Unfiltered rows, row-major, samples big-endian as in the file.</summary>
            public byte[] Data;
            public int BytesPerPixel { get { return Channels * BitDepth / 8; } }
        }

        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static Image Read(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            for (int i = 0; i < 8; i++)
                if (file.Length < 8 || file[i] != Signature[i]) throw new InvalidDataException("not a PNG: " + path);
            int pos = 8, width = 0, height = 0, depth = 0, colour = -1, interlace = 0;
            var idat = new MemoryStream();
            while (pos + 8 <= file.Length)
            {
                int len = (file[pos] << 24) | (file[pos + 1] << 16) | (file[pos + 2] << 8) | file[pos + 3];
                string type = new string(new[] { (char)file[pos + 4], (char)file[pos + 5], (char)file[pos + 6], (char)file[pos + 7] });
                int data = pos + 8;
                if (len < 0 || data + len > file.Length) throw new InvalidDataException("truncated chunk " + type + " in " + path);
                if (type == "IHDR")
                {
                    width = BigEndian(file, data);
                    height = BigEndian(file, data + 4);
                    depth = file[data + 8];
                    colour = file[data + 9];
                    interlace = file[data + 12];
                }
                else if (type == "IDAT") idat.Write(file, data, len);
                else if (type == "IEND") break;
                pos = data + len + 4;
            }
            int channels = colour == 0 ? 1 : colour == 2 ? 3 : colour == 6 ? 4 : colour == 4 ? 2 : 0;
            if (channels == 0 || interlace != 0 || (depth != 8 && depth != 16) || width <= 0 || height <= 0)
                throw new InvalidDataException("unsupported PNG (colour " + colour + ", depth " + depth + ", interlace " + interlace + "): " + path);
            var im = new Image { Width = width, Height = height, Channels = channels, BitDepth = depth };
            int bpp = im.BytesPerPixel, stride = width * bpp;
            byte[] raw = new byte[(stride + 1) * height];
            idat.Position = 2; // the zlib header (CMF, FLG)
            using (var inflate = new DeflateStream(idat, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < raw.Length)
                {
                    int n = inflate.Read(raw, got, raw.Length - got);
                    if (n <= 0) throw new InvalidDataException("PNG data ends early (" + got + " of " + raw.Length + " bytes): " + path);
                    got += n;
                }
            }
            byte[] outp = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride, prev = dst - stride;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? outp[dst + x - bpp] : 0;
                    int b = y > 0 ? outp[prev + x] : 0;
                    int c = x >= bpp && y > 0 ? outp[prev + x - bpp] : 0;
                    int v = raw[src + x];
                    switch (f)
                    {
                        case 0: break;
                        case 1: v += a; break;
                        case 2: v += b; break;
                        case 3: v += (a + b) >> 1; break;
                        case 4: v += Paeth(a, b, c); break;
                        default: throw new InvalidDataException("bad PNG filter " + f + " in row " + y + ": " + path);
                    }
                    outp[dst + x] = (byte)v;
                }
            }
            im.Data = outp;
            return im;
        }

        /// <summary>A 16-bit grey image's samples, row-major.</summary>
        public static ushort[] Gray16(Image im)
        {
            if (im.Channels != 1 || im.BitDepth != 16) throw new InvalidDataException("not a 16-bit grey image");
            var r = new ushort[im.Width * im.Height];
            for (int i = 0; i < r.Length; i++) r[i] = (ushort)((im.Data[2 * i] << 8) | im.Data[2 * i + 1]);
            return r;
        }

        /// <summary>One 8-bit channel of an image, row-major.</summary>
        public static byte[] Channel(Image im, int channel)
        {
            if (im.BitDepth != 8 || channel < 0 || channel >= im.Channels) throw new InvalidDataException("no 8-bit channel " + channel);
            var r = new byte[im.Width * im.Height];
            for (int i = 0; i < r.Length; i++) r[i] = im.Data[i * im.Channels + channel];
            return r;
        }

        private static int BigEndian(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
