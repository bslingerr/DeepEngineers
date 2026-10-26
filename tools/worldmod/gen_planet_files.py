"""Generates the planet data files of the DeepEngineers.World mod: DE_4546B (site version 1).

Our own data, written from scratch (no game file is read or copied):
 Data/PlanetDataFiles/DE_4546B/<face>.png six heightmaps, 2048 x 2048, 16-bit grayscale, every sample 0
 Data/PlanetDataFiles/DE_4546B/<face>_mat.png six material maps, 2048 x 2048, 8-bit RGBA, every pixel
 R = MATERIAL_VALUE, G = 0, B = 0, A = 255
with <face> in front, back, left, right, up, down.

Why these formats:
 * File names: MyCubemapHelpers.GetNameForFace(0..5) = front, back, left, right, up, down; the folder is the
 definition's FolderName, which defaults to the SubtypeId (MyPlanetGeneratorDefinition.Init); files are looked up
 as <mod>/Data/PlanetDataFiles/<folder>/<face>.png, then.dds (MyPlanetTextureMapProvider.GetPath/FindTexture).
 * Heightmaps: loaded with MyImage.Load(path, oneChannel: true); 8 or 16 bits per pixel, square, all six faces the
 same size, else SE logs "Error loading heightmap..." and silently uses an all-zero fallback face
 (MyPlanetTextureMapProvider.GetHeightMap). Value 0 puts the surface at InnerRadius = R(1 + HillParams.Min)
 (MyPlanetShapeProvider). The stock EarthLike faces are 2048 x 2048, 16-bit grayscale (PNG colour type 0).
 * Material maps ("<face>_mat"): loaded with oneChannel: false; a non-grayscale PNG becomes an Rgba32 image read as
 one uint per pixel, byte 0 (R) = material, byte 1 (G) = biome, byte 2 (B) = ore
 (MyPlanetTextureMapProvider.GetPlanetMaps/ReadChannelsFromImage). A grayscale _mat would be loaded as one channel
 and fail the cast. The stock EarthLike _mat files are 2048 x 2048, 8-bit RGBA (colour type 6); we use the same.
 Only the R channel is read here (PlanetMaps Material="true", Ores and Biome false); R = 1 selects the
 CustomMaterialTable entry Value="1" (Sand_02) of Data/PlanetGeneratorDefinitions.sbc. G and B are written as 0.

Deterministic: fixed header, one IDAT chunk, filter type 0 on every row, zlib level 9 with fixed parameters, no
ancillary chunks (no time, no text). Byte-identical output for the same zlib build; the decoded content is identical
on any build (tests/worldmod checks both).

Usage (repository root):
 python -I tools/worldmod/gen_planet_files.py writes into src/DeepEngineers.WorldMod
 python -I tools/worldmod/gen_planet_files.py --out <modDir> writes into another mod folder (tests)
 python -I tools/worldmod/gen_planet_files.py --verify compares the existing files' decoded content with the
 spec; writes nothing; exit 1 on a difference
Stdlib only (struct, zlib, hashlib).
"""
import argparse
import hashlib
import os
import struct
import sys
import zlib

SUBTYPE = "DE_4546B"
FACES = ("front", "back", "left", "right", "up", "down")   # MyCubemapHelpers.GetNameForFace(0..5)
RESOLUTION = 2048                                           # same as the stock EarthLike faces
MATERIAL_VALUE = 1                                          # CustomMaterialTable Value="1" in the.sbc
ZLIB_LEVEL = 9

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
COLOR_GRAY = 0
COLOR_RGBA = 6

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
DEFAULT_MOD_DIR = os.path.join(REPO, "src", "DeepEngineers.WorldMod")
# S8a-2: the repository's planet is the sculpt of tools/planet/sculpt_de4546b.py (seed 4546, written with
# --verify accepts it by its sculptHash (what `sculpt_planet.py --verify src/DeepEngineers.WorldMod` prints
# the window's chart keys change with it) before falling back to this flat spec; a new seed changes this pin.
SCULPT_HASH_PINNED = 0x099b72611d426788
# S8a Stage C: the designed planet (tools/planet/layout_4546b.
# go and the in-game row S8a.C2.ingame; until then the repository keeps the seed-4546 noise sculpt above. Both are accepted;
# drop the noise pin once the design is written (a layout change changes this pin).
SCULPT_HASH_DESIGN_1 = 0xef5c29944aeb480f

# lips and outcrop ore rows
SCULPT_HASH_DESIGN_2 = 0x4a7c319ba065adf3

# the moat), ore fields at SE's EarthLike density, the ring's common ores halved; the repository keeps design 1 until the

SCULPT_HASH_DESIGN = 0x59f91bb12dc43654
SCULPT_HASHES = {SCULPT_HASH_PINNED: "the seed-4546 noise sculpt (S8a-2)", SCULPT_HASH_DESIGN_1: "the designed planet 1 (Stage C, layout_4546b.json)",
                 SCULPT_HASH_DESIGN_2: "the designed planet 2 (owner play 2026-10-23, layout_4546b.json)",
                 SCULPT_HASH_DESIGN: "the designed planet 3, the whole planet (Stage E, layout_4546b.json)"}
FACES = ("front", "back", "left", "right", "up", "down")

def sculpt_hash_of(mod_dir):
    """The sculptHash of the 12 files in mod_dir (sculpt_de4546b.sculpt_hash), or None when a file is missing."""
    planet = os.path.join(REPO, "tools", "planet")
    if planet not in sys.path:
        sys.path.insert(0, planet)
    import sculpt_de4546b as sculpt  # noqa: E402 (our own tool; numpy)
    files = {}
    for face in FACES:
        for name in (face + ".png", face + "_mat.png"):
            path = os.path.join(mod_dir, "Data", "PlanetDataFiles", SUBTYPE, name)
            if not os.path.isfile(path):
                return None
            with open(path, "rb") as fh:
                files[name] = fh.read()
    return sculpt.sculpt_hash(files)[0]

def planet_dir(mod_dir):
    return os.path.join(mod_dir, "Data", "PlanetDataFiles", SUBTYPE)

def _chunk(tag, data):
    return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

def png_uniform(width, height, bit_depth, color_type, pixel):
    """A PNG whose every pixel is `pixel` (raw sample bytes, big-endian as PNG stores them)."""
    row = b"\x00" + pixel * width                          # filter type 0 (None)
    raw = row * height
    comp = zlib.compressobj(ZLIB_LEVEL, zlib.DEFLATED, 15, 9, zlib.Z_DEFAULT_STRATEGY)
    idat = comp.compress(raw) + comp.flush()
    ihdr = struct.pack(">IIBBBBB", width, height, bit_depth, color_type, 0, 0, 0)  # deflate, adaptive filter, no interlace
    return PNG_SIGNATURE + _chunk(b"IHDR", ihdr) + _chunk(b"IDAT", idat) + _chunk(b"IEND", b"")

def heightmap_png(resolution=RESOLUTION):
    return png_uniform(resolution, resolution, 16, COLOR_GRAY, b"\x00\x00")

def material_png(resolution=RESOLUTION, value=MATERIAL_VALUE):
    return png_uniform(resolution, resolution, 8, COLOR_RGBA, bytes((value, 0, 0, 255)))

def expected_files():
    """{relative path under the mod folder: bytes}."""
    rel = os.path.join("Data", "PlanetDataFiles", SUBTYPE)
    h = heightmap_png()
    m = material_png()
    out = {}
    for face in FACES:
        out[os.path.join(rel, face + ".png")] = h
        out[os.path.join(rel, face + "_mat.png")] = m
    return out

def _decode_rows(data):
    """Minimal decoder for the files written above (one or more IDAT, no interlace, filter 0 only): returns
 (width, height, bit_depth, color_type, raw scanlines without filter bytes)."""
    if data[:8] != PNG_SIGNATURE:
        raise ValueError("not a PNG")
    pos, idat, ihdr = 8, b"", None
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if tag == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
        pos += 12 + length
    w, h, bd, ct, _, _, il = ihdr
    if il != 0:
        raise ValueError("interlaced")
    raw = zlib.decompress(idat)
    bpp = {(16, COLOR_GRAY): 2, (8, COLOR_RGBA): 4}[(bd, ct)]
    stride = 1 + w * bpp
    rows = []
    for y in range(h):
        line = raw[y * stride:(y + 1) * stride]
        if line[0] != 0:
            raise ValueError("unexpected filter type %d" % line[0])
        rows.append(line[1:])
    return w, h, bd, ct, b"".join(rows)

def decoded_digest(data):
    """SHA-256 of (IHDR fields + decoded samples): independent of the zlib build that compressed the file."""
    w, h, bd, ct, samples = _decode_rows(data)
    return hashlib.sha256(struct.pack(">IIBB", w, h, bd, ct) + samples).hexdigest()

def write(mod_dir):
    files = expected_files()
    for rel, data in sorted(files.items()):
        path = os.path.join(mod_dir, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)
        print("wrote %-48s %9d B  sha256 %s" % (rel.replace(os.sep, "/"), len(data), hashlib.sha256(data).hexdigest()[:16]))
    print("zlib %s (runtime %s); %d files" % (zlib.ZLIB_VERSION, zlib.ZLIB_RUNTIME_VERSION, len(files)))
    return 0

def verify(mod_dir):
    try:
        shash = sculpt_hash_of(mod_dir)
    except Exception as e:  # noqa: BLE001 - the sculpt tool unavailable: the flat spec below decides
        print("sculpt hash not computed (%s: %s); comparing with the flat spec" % (type(e).__name__, e))
        shash = None
    if shash is not None and shash in SCULPT_HASHES:
        print("OK        %s: sculptHash 0x%016x = its pin" % (SCULPT_HASHES[shash], shash))
        print("GEN VERIFY PASS")
        return 0
    if shash is not None:
        print("sculptHash 0x%016x is none of the pins %s: comparing with the flat spec" % (shash, ", ".join("0x%016x" % h for h in sorted(SCULPT_HASHES))))
    bad = 0
    for rel, data in sorted(expected_files().items()):
        path = os.path.join(mod_dir, rel)
        if not os.path.isfile(path):
            print("MISSING", rel)
            bad += 1
            continue
        with open(path, "rb") as f:
            have = f.read()
        try:
            same = decoded_digest(have) == decoded_digest(data)
        except Exception as e:  # noqa: BLE001 - report any decode problem as a difference
            print("UNREADABLE", rel, e)
            bad += 1
            continue
        exact = have == data
        print("%-9s %s%s" % ("OK" if same else "DIFFERENT", rel.replace(os.sep, "/"), "" if exact or not same else " (same content, other zlib bytes)"))
        bad += 0 if same else 1
    print("GEN VERIFY %s" % ("PASS" if bad == 0 else "FAIL (%d)" % bad))
    return 0 if bad == 0 else 1

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", default=DEFAULT_MOD_DIR, help="mod folder (default: src/DeepEngineers.WorldMod)")
    ap.add_argument("--verify", action="store_true", help="compare existing files with the spec; write nothing")
    a = ap.parse_args(argv)
    return verify(a.out) if a.verify else write(a.out)

if __name__ == "__main__":
    sys.exit(main())
