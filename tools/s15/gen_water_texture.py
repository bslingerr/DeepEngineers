"""S15 part B: our own water texture for the DE_4546B sea (docs/S15-BRIEF.md "B"; CONTRACT §5.4 "S15 conventions",
format water.material). Python 3.10 stdlib only; run with python -I.

 python -I tools/s15/gen_water_texture.py --write writes src/DeepEngineers.WorldMod/Textures/DE_4546B_Water.dds (near)
 and DE_4546B_WaterFar.dds (far)
 python -I tools/s15/gen_water_texture.py --verify the file is byte-identical to the generator's, its mean is the pin
 python -I tools/s15/gen_water_texture.py --measure F the mean sRGB/alpha of a DDS (DXT1/DXT5 or 32-bit), e.g. the
 Water Mod's own WaterSquare.dds (read only, never copied)
 python -I tools/s15/gen_water_texture.py --selftest

The texture is generated, never derived from a game file: a tileable value-noise field (four octaves, period 256 px) around
one mean colour, 256 x 256 BGRA8 (uncompressed DDS, DDPF_RGB | DDPF_ALPHAPIXELS) with its full mip chain (box filter).

Two textures, one material each: the near one (DE_4546B_Water) below FarSwapDownM, the far one (DE_4546B_WaterFar) the S15 plugin
swaps in above FarSwapUpM (the Water Mod has one surface material per planet and no distance term: its billboards take
Settings.Texture, WaterRenderComponent.cs:412 of the mod). Both means come from run 20261024-054404 (S15b at SE's real noon,
both pictures at noon), which drew our texture of mean INGAME_TEXTURE as NOON_BAND_SE (compare.py's near_water band of SE's
picture 50 m above the switch) and as NOON_AIR30 (the sea pixels 30 km straight down). In linear light a texture of mean t
is drawn as t x gain per channel, gain = lin(seen) / lin(INGAME_TEXTURE), so:
 near = the texture Subnautica's near water at the switch is drawn from (NOON_BAND_SN, its own picture 50 m below): the
 band (T5's tolerance) first, the project's continuity at the switch;
 far = the texture the ending's hue is drawn from at the luma of the project's opaque JWater2 view from 40 km (77): the
 deep blue from 30 km up (FAR_TARGET).
The earlier means (WaterSquare.dds' through the band pair of: (79, 121, 121); run 20261024-003024's, taken at SE's
night) are superseded. The resulting means are pinned in S15Conventions S15Conventions (water.material) and here (TARGET_MEAN_SRGB); --verify checks the file against both.
"""
import argparse
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(ROOT, "src", "DeepEngineers.WorldMod", "Textures", "DE_4546B_Water.dds")
OUT_FAR = os.path.join(ROOT, "src", "DeepEngineers.WorldMod", "Textures", "DE_4546B_WaterFar.dds")

SIZE = 256
SEED = 4546
OCTAVES = ((4, 0.50), (8, 0.28), (16, 0.15), (32, 0.07))   # lattice cells per period, weight
CONTRAST = 0.16          # +- relative brightness of the pattern around the mean (linear light)
# the measurement (sRGB means of the band regions, Wire S7tBandTargets; the T4 colour intensity change 5 -> 8)
SN_NEAR_WATER = (65, 97, 122)
SE_NEAR_WATER = (27, 68, 104)
SE_INTENSITY_THEN, SE_INTENSITY_NOW = 5.0, 8.0
ENDING_SEA = (34, 59, 75)          # S15Conventions.Ending.Sea: the sea of Subnautica's ending from orbit (measure_ending.py)
# --measure on the Water Mod 3.23's Textures/WaterSquare.dds (2048 x 2048 DXT5), the project's install,
MEASURED_REFERENCE = (51, 105, 124)   # (50.7, 105.1, 124.3), alpha 255
MEASURED_REFERENCE_ALPHA = 255
TARGET_MEAN_SRGB = (110, 156, 184)   # S15Conventions.Water.MeanR/G/B (the near pin); derive reproduces it
FAR_MEAN_SRGB = (62, 96, 122)        # S15Conventions.WaterFar.MeanR/G/B (the far pin); derive_far reproduces it
# run 20261024-054404 (SE's real noon): the texture deployed then and what the game drew of it
NOON_TEXTURE = (79, 121, 121)
NOON_BAND_SE = (71.0, 99.3, 108.7)   # SE's near_water band 50 m above the switch (compare.py bands)
NOON_BAND_SN = (99.3, 128.7, 165.6)  # Subnautica's near_water band 50 m below the switch: the near target
NOON_AIR30 = (62, 105, 104)          # the sea pixels from 30 km straight down
FAR_TARGET = (48, 83, 105)           # the ending's hue (34, 59, 75) at luma 77, the project's opaque JWater2 view from 40 km
# the in-game measurement (run 20261024-003024, captures/air-5.png, the 600 x 600 centre's median; SE's noon, straight down)
INGAME_TEXTURE = (79, 121, 121)      # the texture that was deployed then (its pinned mean)
INGAME_SEA = (30, 58, 57)            # at SE's night (the ambient light): not used
BAND_DERIVED = (79, 121, 121)       # the band-pair derivation (band_derived): the pin again since run 20261024-041144
MEAN_TOL = 1                       # per channel, sRGB levels

# ---------------------------------------------------------------------------------------------------------- colour
def lin(c8):
    c = c8 / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4

def srgb(x):
    x = min(1.0, max(0.0, x))
    c = 12.92 * x if x <= 0.0031308 else 1.055 * (x ** (1.0 / 2.4)) - 0.055
    return int(round(c * 255.0))

def luma(c):
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]

def target_sea():
    """The ending's hue at the near water's brightness (sRGB luma ratio), rounded."""
    k = luma(SN_NEAR_WATER) / luma(ENDING_SEA)
    return tuple(int(round(c * k)) for c in ENDING_SEA)

def band_gains():
    k = SE_INTENSITY_NOW / SE_INTENSITY_THEN
    return tuple(lin(s) / (lin(e) * k) for s, e in zip(target_sea(), SE_NEAR_WATER))

def band_derived(reference=MEASURED_REFERENCE):
    """The band-pair derivation: WaterSquare's linear mean times the band pair's gain, back to sRGB."""
    return tuple(srgb(lin(r) * g) for r, g in zip(reference, band_gains()))

def gains():
    """The Water Mod's in-game gain per channel: what it drew of our texture (INGAME_SEA) over the texture's mean."""
    return tuple(lin(o) / lin(t) for o, t in zip(INGAME_SEA, INGAME_TEXTURE))

def _through(texture, seen, target):
    """The texture mean the game draws as target, given that it drew texture as seen (linear gain per channel)."""
    return tuple(srgb(lin(t) * lin(g) / lin(s)) for t, s, g in zip(texture, seen, target))

def derive():
    """The near pin: Subnautica's near water at the switch through the noon band gain."""
    return _through(NOON_TEXTURE, NOON_BAND_SE, NOON_BAND_SN)

def derive_far():
    """The far pin: the ending's hue at the project's 40 km luma through the noon 30 km gain."""
    return _through(NOON_TEXTURE, NOON_AIR30, FAR_TARGET)

def predicted(texture=TARGET_MEAN_SRGB):
    """What the game should draw of a texture of this mean (the same view and light as the measurement)."""
    return tuple(srgb(lin(t) * g) for t, g in zip(texture, gains()))

# ---------------------------------------------------------------------------------------------------------- pattern
def _hash(ix, iy, octave):
    h = (ix * 374761393 + iy * 668265263 + octave * 2246822519 + SEED * 3266489917) & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    h ^= h >> 16
    return (h & 0xFFFF) / 65535.0

def _smooth(t):
    return t * t * (3.0 - 2.0 * t)

def field():
    """Tileable value noise in [-1, 1] (mean ~0), SIZE x SIZE, row-major."""
    out = [0.0] * (SIZE * SIZE)
    total = sum(w for _, w in OCTAVES)
    for o, (cells, w) in enumerate(OCTAVES):
        step = SIZE / cells
        lattice = [[_hash(i, j, o) for i in range(cells)] for j in range(cells)]
        for y in range(SIZE):
            fy = y / step
            j0 = int(fy)
            ty = _smooth(fy - j0)
            r0, r1 = lattice[j0 % cells], lattice[(j0 + 1) % cells]
            base = y * SIZE
            for x in range(SIZE):
                fx = x / step
                i0 = int(fx)
                tx = _smooth(fx - i0)
                a = r0[i0 % cells] + (r0[(i0 + 1) % cells] - r0[i0 % cells]) * tx
                b = r1[i0 % cells] + (r1[(i0 + 1) % cells] - r1[i0 % cells]) * tx
                out[base + x] += (a + (b - a) * ty) * w / total
    m = sum(out) / len(out)
    span = max(abs(v - m) for v in out) or 1.0
    return [(v - m) / span for v in out]

def level0(mean=TARGET_MEAN_SRGB):
    """BGRA bytes of the top level: the mean in linear light modulated by the field; the sRGB mean is corrected to the pin."""
    f = field()
    lm = [lin(c) for c in mean]
    px = []
    for c in range(3):
        px.append([srgb(lm[c] * (1.0 + CONTRAST * v)) for v in f])
    # correct the sRGB mean (the transfer curve shifts it) to the pinned value, one level at a time
    for c in range(3):
        for _ in range(4):
            d = mean[c] - sum(px[c]) / len(px[c])
            if abs(d) < 0.25:
                break
            px[c] = [min(255, max(0, v + int(round(d)))) for v in px[c]]
    out = bytearray(SIZE * SIZE * 4)
    for i in range(SIZE * SIZE):
        out[4 * i] = px[2][i]
        out[4 * i + 1] = px[1][i]
        out[4 * i + 2] = px[0][i]
        out[4 * i + 3] = 255
    return bytes(out)

def mips(top):
    levels = [top]
    w = SIZE
    cur = top
    while w > 1:
        n = w // 2
        nxt = bytearray(n * n * 4)
        for y in range(n):
            for x in range(n):
                for c in range(4):
                    s = (cur[((2 * y) * w + 2 * x) * 4 + c] + cur[((2 * y) * w + 2 * x + 1) * 4 + c] +
                         cur[((2 * y + 1) * w + 2 * x) * 4 + c] + cur[((2 * y + 1) * w + 2 * x + 1) * 4 + c])
                    nxt[(y * n + x) * 4 + c] = (s + 2) // 4
        levels.append(bytes(nxt))
        cur, w = bytes(nxt), n
    return levels

def dds_bytes(mean=TARGET_MEAN_SRGB):
    levels = mips(level0(mean))
    # DDS_HEADER: size 124; flags CAPS|HEIGHT|WIDTH|PITCH|PIXELFORMAT|MIPMAPCOUNT; DDS_PIXELFORMAT 32 B (RGB|ALPHAPIXELS, 32 bpp)
    flags = 0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | 0x20000
    pf = struct.pack("<8I", 32, 0x1 | 0x40, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)
    caps = 0x1000 | 0x8 | 0x400000     # TEXTURE | COMPLEX | MIPMAP
    hdr = struct.pack("<7I", 124, flags, SIZE, SIZE, SIZE * 4, 0, len(levels)) + b"\0" * 44 + pf + struct.pack("<5I", caps, 0, 0, 0, 0)
    assert len(hdr) == 124
    return b"DDS " + hdr + b"".join(levels)

# ---------------------------------------------------------------------------------------------------------- measure
def _rgb565(v):
    r, g, b = (v >> 11) & 31, (v >> 5) & 63, v & 31
    return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))

def _dxt_colour_block(blk):
    c0, c1, bits = struct.unpack("<HHI", blk)
    p0, p1 = _rgb565(c0), _rgb565(c1)
    if c0 > c1:
        pal = [p0, p1, tuple((2 * a + b) // 3 for a, b in zip(p0, p1)), tuple((a + 2 * b) // 3 for a, b in zip(p0, p1))]
    else:
        pal = [p0, p1, tuple((a + b) // 2 for a, b in zip(p0, p1)), (0, 0, 0)]
    return [pal[(bits >> (2 * i)) & 3] for i in range(16)]

def _dxt5_alpha_block(blk):
    a0, a1 = blk[0], blk[1]
    bits = int.from_bytes(blk[2:8], "little")
    if a0 > a1:
        pal = [a0, a1] + [((6 - i) * a0 + (1 + i) * a1) // 7 for i in range(6)]
    else:
        pal = [a0, a1] + [((4 - i) * a0 + (1 + i) * a1) // 5 for i in range(4)] + [0, 255]
    return [pal[(bits >> (3 * i)) & 7] for i in range(16)]

def measure(path):
    """Mean sRGB (and alpha) of the top level of a DDS: DXT1, DXT5 or 32-bit BGRA."""
    with open(path, "rb") as f:
        b = f.read()
    if b[:4] != b"DDS ":
        raise ValueError("not a DDS file")
    h, w = struct.unpack("<2I", b[12:20])
    fourcc = b[84:88]
    data = b[128:]
    s = [0, 0, 0, 0]
    n = 0
    if fourcc in (b"DXT1", b"DXT5"):
        bs = 8 if fourcc == b"DXT1" else 16
        nb = max(1, (w + 3) // 4) * max(1, (h + 3) // 4)
        for k in range(nb):
            blk = data[k * bs:(k + 1) * bs]
            if fourcc == b"DXT5":
                al, col = _dxt5_alpha_block(blk[:8]), _dxt_colour_block(blk[8:16])
            else:
                al, col = [255] * 16, _dxt_colour_block(blk)
            for i in range(16):
                s[0] += col[i][0]; s[1] += col[i][1]; s[2] += col[i][2]; s[3] += al[i]
            n += 16
    else:
        for i in range(w * h):
            s[2] += data[4 * i]; s[1] += data[4 * i + 1]; s[0] += data[4 * i + 2]; s[3] += data[4 * i + 3]
        n = w * h
    return tuple(round(v / n, 1) for v in s), (w, h, fourcc.decode("ascii", "replace"))

def top_mean(data_bytes):
    top = data_bytes[128:128 + SIZE * SIZE * 4]
    s = [0, 0, 0]
    for i in range(SIZE * SIZE):
        s[2] += top[4 * i]; s[1] += top[4 * i + 1]; s[0] += top[4 * i + 2]
    return tuple(v / (SIZE * SIZE) for v in s)

# ---------------------------------------------------------------------------------------------------------- commands
def verify():
    ok = True
    for path, mean in ((OUT, TARGET_MEAN_SRGB), (OUT_FAR, FAR_MEAN_SRGB)):
        if not os.path.isfile(path):
            print("FAIL missing %s (python -I tools/s15/gen_water_texture.py --write)" % path)
            return 1
        with open(path, "rb") as f:
            have = f.read()
        same = have == dds_bytes(mean)
        print("%s %s byte-identical to the generator (%d bytes)" % ("PASS" if same else "FAIL", os.path.basename(path), len(have)))
        ok &= same
        m = top_mean(have)
        near = all(abs(a - b) <= MEAN_TOL for a, b in zip(m, mean))
        print("%s top-level mean sRGB (%.1f, %.1f, %.1f) vs the pin %s +-%d" % ("PASS" if near else "FAIL", m[0], m[1], m[2], mean, MEAN_TOL))
        ok &= near
    df = derive_far()
    print("%s the far pin is derive_far() = %s" % ("PASS" if df == FAR_MEAN_SRGB else "FAIL", df))
    ok &= df == FAR_MEAN_SRGB
    d = derive()
    print("%s the near pin is derive() = %s" % ("PASS" if d == TARGET_MEAN_SRGB else "FAIL", d))
    ok &= d == TARGET_MEAN_SRGB
    return 0 if ok else 1

def selftest():
    res = []

    def check(c, what):
        res.append(bool(c))
        print("  %s %s" % ("PASS" if c else "FAIL", what))

    check(all(abs(srgb(lin(v)) - v) <= 0 for v in range(256)), "sRGB round trip of every level")
    check(band_derived() == BAND_DERIVED == NOON_TEXTURE, "the band pair's derivation is the texture the noon run drew (%s)" % (BAND_DERIVED,))
    check(derive() == TARGET_MEAN_SRGB and derive_far() == FAR_MEAN_SRGB, "derive() = %s (near), derive_far() = %s (far): the pins" % (TARGET_MEAN_SRGB, FAR_MEAN_SRGB))
    back = _through(TARGET_MEAN_SRGB, NOON_TEXTURE, NOON_BAND_SE)
    check(max(abs(a - b) for a, b in zip(back, NOON_BAND_SN)) <= 1, "the near texture is drawn at the switch as Subnautica's near water %s (predicted %s)" % (NOON_BAND_SN, back))
    fc = [v / sum(FAR_TARGET) for v in FAR_TARGET]
    ec = [v / sum(ENDING_SEA) for v in ENDING_SEA]
    check(max(abs(x - y) for x, y in zip(fc, ec)) < 0.003 and abs(luma(FAR_TARGET) - 77) < 1, "the far target %s: the ending's hue at luma 77" % (FAR_TARGET,))
    f = field()
    check(abs(sum(f) / len(f)) < 1e-9 and max(f) <= 1.0 + 1e-12 and min(f) >= -1.0 - 1e-12, "the field is zero-mean in [-1, 1]")
    tile = all(abs(f[y * SIZE] - f[y * SIZE + SIZE - 1]) < 0.2 for y in range(0, SIZE, 16))
    check(tile, "the field wraps (left and right columns close)")
    b = dds_bytes()
    check(b[:4] == b"DDS " and struct.unpack("<I", b[4:8])[0] == 124 and struct.unpack("<I", b[28:32])[0] == 9, "DDS header, 9 mip levels")
    check(len(b) == 128 + sum((SIZE >> k) ** 2 * 4 for k in range(9)), "uncompressed BGRA8 size %d" % len(b))
    m = top_mean(b)
    check(all(abs(a - t) <= MEAN_TOL for a, t in zip(m, TARGET_MEAN_SRGB)), "top-level mean (%.1f, %.1f, %.1f)" % m)
    check(dds_bytes() == b, "deterministic")
    ok = all(res)
    print("S15 WATER TEXTURE SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(res), len(res)))
    return 0 if ok else 1

def main(argv):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--verify", action="store_true")
    ap.add_argument("--measure", default=None, metavar="DDS")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.measure:
        mean, info = measure(a.measure)
        print("mean sRGB+alpha %s of %s (%d x %d %s)" % (mean, a.measure, info[0], info[1], info[2]))
        print("the band derivation with it: %s" % (band_derived(tuple(int(round(v)) for v in mean[:3])),))
        return 0
    if a.write:
        os.makedirs(os.path.dirname(OUT), exist_ok=True)
        for path, mean in ((OUT, TARGET_MEAN_SRGB), (OUT_FAR, FAR_MEAN_SRGB)):
            b = dds_bytes(mean)
            with open(path, "wb") as f:
                f.write(b)
            print("wrote %s (%d bytes)" % (path, len(b)))
        return verify()
    if a.verify:
        return verify()
    if a.selftest:
        return selftest()
    ap.print_help()
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
