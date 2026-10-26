"""Tests of the DeepEngineers.World mod (S1 world mod: DE_4546B, site version 1). No game is launched.

 python -I tests/worldmod/test_worldmod.py (unittest; about 25 s)

What is tested:
 * gen_planet_files.py: deterministic (two runs byte-identical), the files in src/DeepEngineers.WorldMod decode to
 the spec, and (when Pillow is installed) an independent decoder reads the same pixels.
 * check_planet.py: PASS on the repository's mod folder; the read-only cross-checks against the decompiled SE
 sources, the installed Water Mod sources and the stock SE content when those exist on this machine (else SKIP).
 * Seeded failures: each mutant below, applied to a temporary copy of the mod folder, must turn its named check red
 (ROADMAP section 3 rule 1).
 * The spawn numbers of docs/worldmod/DE_LAB_WORLD.md equal what check_planet.spawn_numbers computes.
 * deploy_worldmod.ps1 parses (PowerShell parser only; the script is never run here) and writes only under
 %APPDATA%\\SpaceEngineers\\Mods\\DeepEngineers.World.
The C# half (SE's own definition serializer) is tests/worldmod/DeepEngineers.WorldMod.Tests.
"""
import hashlib
import io
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "worldmod"))   # -I drops the script dir; our own folders only
sys.path.insert(0, os.path.join(REPO, "tools", "worldmap"))
sys.path.insert(0, os.path.join(REPO, "tools", "planet"))

import check_planet as cp      # noqa: E402
import gen_planet_files as gen  # noqa: E402
import site_v1                  # noqa: E402
import sculpt_de4546b as sculpt  # noqa: E402

# json). Its pin is the sculptHash `sculpt_planet.py --verify
# src/DeepEngineers.WorldMod` prints (gen.SCULPT_HASHES; the window's chart keys change with it); the per-pixel rules are
# check_planet's sculpt and material rules. SCULPT_TABLE mirrors the CustomMaterialTable values (Grass 7 since Stage C).
SCULPT_HASHES = gen.SCULPT_HASHES
SCULPT_TABLE = {1, 2, 3, 4, 5, 6, 7}
BIOME_G = set(range(0, cp.BIOME_NAMES_COUNT + 1))
FACES = ("front", "back", "left", "right", "up", "down")

def mod_face_samples(face, kind):
    """The decoded samples (bytes) of a repository face file: kind "height" (16-bit) or "mat" (8-bit RGBA)."""
    name = face + (".png" if kind == "height" else "_mat.png")
    return cp.png_read(read(os.path.join(MOD, PDIR_REL, name)))

def mat_faces_with(value):
    """The png.<face>_mat.png check ids of the faces whose R channel uses the given table value."""
    out = set()
    for face in FACES:
        info = mod_face_samples(face, "mat")
        ch = {2: 3, 6: 4}[info["color_type"]]
        if value in set(info["samples"][0::ch]):
            out.add("png.%s_mat.png" % face)
    return out

MOD = os.path.join(REPO, "src", "DeepEngineers.WorldMod")
DOC = os.path.join(REPO, "docs", "worldmod", "DE_LAB_WORLD.md")
DEPLOY = os.path.join(REPO, "tools", "worldmod", "deploy_worldmod.ps1")
PDIR_REL = os.path.join("Data", "PlanetDataFiles", "DE_4546B")

def read(path, mode="rb"):
    with open(path, mode, **({} if "b" in mode else {"encoding": "utf-8"})) as f:
        return f.read()

class GeneratorTests(unittest.TestCase):
    def test_two_runs_are_byte_identical(self):
        with tempfile.TemporaryDirectory() as a, tempfile.TemporaryDirectory() as b:
            for d in (a, b):
                with open(os.devnull, "w") as null:
                    old, sys.stdout = sys.stdout, null
                    try:
                        gen.write(d)
                    finally:
                        sys.stdout = old
            for rel in gen.expected_files():
                self.assertEqual(read(os.path.join(a, rel)), read(os.path.join(b, rel)), rel)

    def test_repository_files_decode_to_the_spec(self):
        # the 12 files are a pinned sculpt (gen.SCULPT_HASHES), every height sample lies at or below check_planet's limit (SN y +70,
 # the designed island; the sculpt tool's --verify holds -2 m elsewhere), the crater floor is 0 within rhoInf 2,450 m
 # (check_planet's sculpt rule), the materials use table values only, G a biome index + 1, B an OreMappings value
        files = {}
        for face in FACES:
            for name in (face + ".png", face + "_mat.png"):
                files[name] = read(os.path.join(MOD, PDIR_REL, name))
        shash, _ = sculpt.sculpt_hash(files)
        self.assertIn(shash, SCULPT_HASHES, "sculptHash 0x%016x is not pinned (gen.SCULPT_HASHES)" % shash)
        limit = cp.SCULPT_MAX_SAMPLE
        self.assertEqual(limit, int(sculpt.encode_height(sculpt.np.array([70.0]))[0]))
        with open(os.path.join(MOD, "Data", "PlanetGeneratorDefinitions.sbc"), encoding="utf-8") as fh:
            ore_values = set(sculpt.sbc_ores(fh.read()).keys()) | {0}
        for face in FACES:
            h = mod_face_samples(face, "height")
            self.assertEqual((h["width"], h["height"], h["bit_depth"], h["color_type"]), (2048, 2048, 16, 0), face)
            samples = struct.unpack(">%dH" % (2048 * 2048), h["samples"])
            self.assertLessEqual(max(samples), limit, face)
            if any(samples):
                ok, how = cp.sculpt_rule(face, h["samples"], 2048, 2048)
                self.assertTrue(ok, "%s: %s" % (face, how))
            m = mod_face_samples(face, "mat")
            self.assertEqual((m["width"], m["height"], m["bit_depth"], m["color_type"]), (2048, 2048, 8, 6), face)
            s = m["samples"]
            n = 2048 * 2048
            self.assertTrue(set(s[0::4]) <= SCULPT_TABLE, "%s: R values %s" % (face, sorted(set(s[0::4]))))
            self.assertTrue(set(s[1::4]) <= BIOME_G, face + " G")
            self.assertTrue(set(s[2::4]) <= ore_values, face + " B")
            self.assertEqual(s[3::4], b"\xff" * n, face + " A")

    def test_formats(self):
        h = cp.png_read(gen.heightmap_png())
        self.assertEqual((h["width"], h["height"], h["bit_depth"], h["color_type"], h["interlace"]), (2048, 2048, 16, 0, 0))
        self.assertEqual(h["samples"], bytes(2048 * 2048 * 2))
        self.assertEqual(h["chunks"], ["IHDR", "IDAT", "IEND"], "no ancillary chunk (no time, no text)")
        m = cp.png_read(gen.material_png())
        self.assertEqual((m["width"], m["height"], m["bit_depth"], m["color_type"]), (2048, 2048, 8, 6))
        self.assertEqual(m["samples"], bytes((gen.MATERIAL_VALUE, 0, 0, 255)) * (2048 * 2048))

    def test_independent_decoder_if_available(self):
        try:
            from PIL import Image  # noqa: F401 (optional; not a dependency of the tools)
        except ImportError:
            self.skipTest("Pillow not installed")
        from PIL import Image
        # the independent decoder agrees with ours on the sculpted front face (extrema), and the materials are table values only
        im = Image.open(io.BytesIO(read(os.path.join(MOD, PDIR_REL, "front.png"))))
        self.assertEqual((im.size, im.mode), ((2048, 2048), "I;16"))
        ours = struct.unpack(">%dH" % (2048 * 2048), mod_face_samples("front", "height")["samples"])
        self.assertEqual(im.getextrema(), (min(ours), max(ours)))
        self.assertLessEqual(im.getextrema()[1], cp.SCULPT_MAX_SAMPLE)
        im = Image.open(io.BytesIO(read(os.path.join(MOD, PDIR_REL, "front_mat.png"))))
        self.assertEqual((im.size, im.mode), ((2048, 2048), "RGBA"))
        (rlo, rhi), g, b, a = im.getextrema()
        self.assertTrue(1 <= rlo <= rhi <= 7, (rlo, rhi))
        self.assertTrue(0 <= g[0] <= g[1] <= cp.BIOME_NAMES_COUNT, g)
        self.assertEqual(a, (255, 255))

class CheckerTests(unittest.TestCase):
    def test_repository_mod_passes(self):
        rep = cp.run(MOD)
        self.assertEqual([], rep.failed)
        self.assertGreaterEqual(len([r for r in rep.results if r[1] == "PASS"]), 40)

    def test_read_only_cross_checks_when_available(self):
        rep = cp.run(MOD, decomp_se=cp.DEFAULT_DECOMP_SE, water_mod=cp.DEFAULT_WATER_MOD, se_content=cp.DEFAULT_SE_CONTENT)
        self.assertEqual([], rep.failed)
        ran = {r[0] for r in rep.results if r[0].startswith(("decomp.", "stock.")) and r[1] == "PASS"}
        if not ran:
            self.skipTest("no decompiled sources, Water Mod or SE content on this machine")

    def test_site_constants(self):
        self.assertEqual((site_v1.R, site_v1.INNER, site_v1.OUTER), (60000.0, 57000.0, 67200.0))
        self.assertEqual(site_v1.RS_EXPECTED, 60000.00390625)
        self.assertEqual(cp.f32_bits(site_v1.WATER["settingsRadius"]), 0x3F86BCA2)
        self.assertEqual(cp.f32_bits(cp.parse_xml_float("1.0526316")), 0x3F86BCA2)

    def test_spawn_numbers(self):
        sp = cp.spawn_numbers((-1000000.0, 250000.0, 1500000.0))
        self.assertEqual(sp["savedMinCorner"], (-1131072.5, 118927.5, 1368927.5))
        self.assertEqual(sp["spawnPlanetPos"], sp["savedMinCorner"])
        self.assertEqual(sp["anchorSn000"], (-1000000.0, 250000.0, 1560000.00390625))
        self.assertEqual(sp["spawnSize"], 120000.0)

    def test_png_reader_rejects_damage(self):
        good = gen.heightmap_png(16)
        cp.png_read(good)
        bad = bytearray(good)
        bad[40] ^= 0xFF                                  # inside IDAT: CRC no longer matches
        with self.assertRaises(cp.PngError):
            cp.png_read(bytes(bad))
        with self.assertRaises(cp.PngError):
            cp.png_read(b"GIF89a" + good[6:])

# ------------------------------------------------------------------------------------------------- seeded failures
def _cut(rel, start, end):
    """S7t integrator: removes the first block from `start` to `end` inclusive."""
    def mutate(d):
        p = os.path.join(d, rel)
        text = read(p, "r")
        i = text.find(start)
        j = text.find(end, i + 1) if i >= 0 else -1
        if i < 0 or j < 0:
            raise AssertionError("mutation block not found in %s: %r..%r" % (rel, start, end))
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(text[:i] + text[j + len(end):])
    return mutate

def _sub(rel, old, new):
    def mutate(d):
        p = os.path.join(d, rel)
        text = read(p, "r")
        if old not in text:
            raise AssertionError("mutation target not found in %s: %r" % (rel, old))
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(text.replace(old, new, 1))
    return mutate

def _toggle_ores():
    """S8a Stage C: the ore map flag flipped either way (Ores="true" needs OreMappings rows, rows need Ores="true")."""
    def mutate(d):
        p = os.path.join(d, SBC)
        text = read(p, "r")
        if 'Ores="false"' in text:
            text = text.replace('Ores="false"', 'Ores="true"', 1)
        elif 'Ores="true"' in text:
            text = text.replace('Ores="true"', 'Ores="false"', 1)
        else:
            raise AssertionError("PlanetMaps Ores attribute not found")
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    return mutate

def _png(face_file, data):
    def mutate(d):
        with open(os.path.join(d, PDIR_REL, face_file), "wb") as f:
            f.write(data)
    return mutate

def _remove(rel):
    return lambda d: os.remove(os.path.join(d, rel))

def _add(rel, text):
    def mutate(d):
        with open(os.path.join(d, rel), "w", encoding="utf-8") as f:
            f.write(text)
    return mutate

def _corrupt(face_file):
    def mutate(d):
        p = os.path.join(d, PDIR_REL, face_file)
        data = bytearray(read(p))
        data[60] ^= 0x01
        with open(p, "wb") as f:
            f.write(bytes(data))
    return mutate

def _height_one_nonzero():
    raw = bytearray((b"\x00" + b"\x00\x00" * 2048) * 2048)
    raw[1 + 2 * 1000 + 1] = 1                          # row 0, sample 1000 = 1
    import zlib
    ihdr = struct.pack(">IIBBBBB", 2048, 2048, 16, 0, 0, 0, 0)
    chunk = lambda t, b: struct.pack(">I", len(b)) + t + b + struct.pack(">I", zlib.crc32(t + b) & 0xFFFFFFFF)  # noqa: E731
    return gen.PNG_SIGNATURE + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")

SBC = "Data/PlanetGeneratorDefinitions.sbc"
WC = "Data/WaterConfig.xml"
MUTANTS = [
    # (name, mutation, checks that must FAIL)
    ("hill min -0.01 (EarthLike's)", _sub(SBC, 'Min="-0.05"', 'Min="-0.01"'), {"sbc.hillParams", "derived.radii"}),
    ("hill max 0.07 (old world-mapping value)", _sub(SBC, 'Max="0.12"', 'Max="0.07"'), {"sbc.hillParams", "derived.atmosphere"}),
    ("misspelt GravityFalloffPower", _sub(SBC, "<GravityFalloffPower>7</GravityFalloffPower>",
                                          "<GravityFallOffPower>7</GravityFallOffPower>"), {"sbc.schema.known", "sbc.values"}),
    ("misspelt HillParams attribute", _sub(SBC, 'Max="0.12"', 'Mac="0.12"'), {"sbc.schema.known"}),
    ("SurfaceDetail added", _sub(SBC, "</CloudLayers>", "</CloudLayers>\n    <SurfaceDetail />"), {"sbc.schema.forbidden"}),
    ("WeatherGenerators added", _sub(SBC, "</CloudLayers>", "</CloudLayers>\n    <WeatherGenerators />"), {"sbc.schema.forbidden"}),
    ("element outside the spec (AllowMeteors)", _sub(SBC, "</CloudLayers>", "</CloudLayers>\n    <AllowMeteors>false</AllowMeteors>"),
     {"sbc.schema.spec"}),
    ("FolderName set", _sub(SBC, "<Public>true</Public>", "<Public>true</Public>\n    <FolderName>Other</FolderName>"),
     {"sbc.folder", "sbc.schema.spec"}),
    ("CloudLayers removed", _cut(SBC, "<CloudLayers>", "</CloudLayers>"), {"sbc.cloudLayers"}),
    ("not public", _sub(SBC, "<Public>true</Public>", "<Public>false</Public>"), {"sbc.public"}),
    ("oxygen density 1.0", _sub(SBC, "<OxygenDensity>0.9</OxygenDensity>", "<OxygenDensity>1.0</OxygenDensity>"), {"sbc.atmosphere"}),
    ("falloff 5", _sub(SBC, "<GravityFalloffPower>7</GravityFalloffPower>", "<GravityFalloffPower>5</GravityFalloffPower>"),
     {"sbc.gravityFalloffPower"}),
    ("subtype renamed", _sub(SBC, "<SubtypeId>DE_4546B</SubtypeId>", "<SubtypeId>DE_4546b</SubtypeId>"), {"sbc.definition"}),
    ("ore map switched (Ores on without OreMappings rows, or off with them)", _toggle_ores(), {"sbc.planetMaps"}),
    ("custom material value 2 (the _mat R=1 is then unmapped)", _sub(SBC, 'Value="1"', 'Value="2"'), "@matR1"),
    ("default surface material missing", _sub(SBC, '<DefaultSurfaceMaterial Material="Stone" MaxDepth="5" />', ""),
     {"sbc.defaultMaterials"}),
    ("water radius 1.0526315 (one ulp low)", _sub(WC, "<Radius>1.0526316</Radius>", "<Radius>1.0526315</Radius>"),
     {"water.radius.bits", "water.radius.rs", "water.settings"}),
    ("water radius 1.0614035 (sea 3,500 m above the floor)", _sub(WC, "<Radius>1.0526316</Radius>", "<Radius>1.0614035</Radius>"),
     {"water.radius.bits", "water.settings"}),
    ("CrushDamage omitted (Water Mod default 0.5)", _sub(WC, "<CrushDamage>0</CrushDamage>", ""), {"water.settings"}),
    ("waves 1 m", _sub(WC, "<WaveHeight>0</WaveHeight>", "<WaveHeight>1</WaveHeight>"), {"water.settings"}),
    ("fish on", _sub(WC, "<EnableFish>false</EnableFish>", "<EnableFish>true</EnableFish>"), {"water.settings"}),
    ("misspelt TideHeight", _sub(WC, "<TideHeight>0</TideHeight>", "<Tideheight>0</Tideheight>"),
     {"water.schema.known", "water.settings"}),
    ("PlanetConfig for another subtype", _sub(WC, 'SubtypeId="DE_4546B"', 'SubtypeId="EarthLike"'), {"water.planetConfig"}),
    ("PlanetConfig TypeId without prefix", _sub(WC, 'TypeId="MyObjectBuilder_PlanetGeneratorDefinition"',
                                                'TypeId="PlanetGeneratorDefinition"'), {"water.planetConfig"}),
    ("heightmap 8-bit", _png("front.png", gen.png_uniform(2048, 2048, 8, 0, b"\x00")), {"png.front.png"}),
    ("heightmap 1024", _png("left.png", gen.heightmap_png(1024)), {"png.left.png", "png.facesSameSize"}),
    ("heightmap one non-zero sample", _png("up.png", _height_one_nonzero()), {"png.up.png"}),
    ("material map grayscale", _png("back_mat.png", gen.png_uniform(2048, 2048, 8, 0, b"\x01")), {"png.back_mat.png"}),
    ("material map R=0", _png("right_mat.png", gen.material_png(value=0)), {"png.right_mat.png"}),
    ("material map damaged (CRC)", _corrupt("down_mat.png"), {"png.down_mat.png"}),
    ("face missing", _remove(os.path.join(PDIR_REL, "back.png")), {"files.present", "png.back.png"}),
    ("modinfo.sbmi added", _add("modinfo.sbmi", "<MyObjectBuilder_ModInfo/>"), {"files.noPublishOrCode", "files.exact"}),
    ("script added", _add("Data/Hello.cs", "// code"), {"files.noPublishOrCode", "files.exact"}),
    # S16 hook: the pressure hull definitions (Data/CubeBlocks_S16.sbc, tools/worldmod/s16_blocks.py)
    ("S16 Computer on a non-terminal tier block", _sub("Data/CubeBlocks_S16.sbc", '<Component Subtype="Motor" Count="6" />',
                                                       '<Component Subtype="Computer" Count="6" />'), {"s16.blocks.components"}),
    ("S16 Mk2 alloy made without uranium", _sub("Data/CubeBlocks_S16.sbc", 'SubtypeId="Uranium"', 'SubtypeId="Iron"'), {"s16.alloys"}),
    ("S16 small block with CubeSize Large", _sub("Data/CubeBlocks_S16.sbc", "<CubeSize>Small</CubeSize>", "<CubeSize>Large</CubeSize>"),
     {"s16.blocks"}),
    ("S16 misspelt element", _sub("Data/CubeBlocks_S16.sbc", "<IsAirTight>false</IsAirTight>", "<IsAirtight>false</IsAirtight>"), {"s16.schema.known"}),
    ("S16 critical component not the alloy", _sub("Data/CubeBlocks_S16.sbc", '<CriticalComponent Subtype="DE_HullAlloyMk2"',
                                                  '<CriticalComponent Subtype="SteelPlate"'), {"s16.blocks.components"}),
    ("S16 tier block missing from the category", _sub("Data/CubeBlocks_S16.sbc", "<string>CubeBlock/DE_PressureHullMk2Small</string>", ""),
     {"s16.category"}),
    ("S16 alloy not in the assembler class", _sub("Data/CubeBlocks_S16.sbc", '<Entry Class="Components" BlueprintSubtypeId="DE_HullAlloyMk1" />', ""),
     {"s16.alloys"}),
    ("S16 a game model copied into the mod", _add("Data/GeneratorSmall.mwm", "x"), {"s16.assets", "files.exact"}),
    ("S16 definitions removed", _remove("Data/CubeBlocks_S16.sbc"), {"files.present", "s16.parse"}),
    # S17 hook: the fish items and their cooking (Data/PhysicalItems_DE_Food.sbc, Data/Blueprints_DE_Food.sbc, tools/worldmod/s17_food.py)
    ("S17 a raw fish with a Health stat", _sub("Data/PhysicalItems_DE_Food.sbc", '<Stat Name="Food" Value="0.00267" Time="30" />',
                                              '<Stat Name="Health" Value="0.01" Time="5" />'), {"s17.items"}),
    ("S17 a cooking blueprint in another class", _sub("Data/Blueprints_DE_Food.sbc", 'Class="FoodProcessorRations" BlueprintSubtypeId="DE_CookFish_Peeper"',
                                                     'Class="Components" BlueprintSubtypeId="DE_CookFish_Peeper"'), {"s17.blueprints"}),
    ("S17 items removed", _remove("Data/PhysicalItems_DE_Food.sbc"), {"files.present", "s17.parse"}),
    # S14 hook: the Water Mod weapon override (Data/WaterConfig.xml <BlockConfigs>, tools/worldmod/s14_weapons.py)
    ("S14 a railgun left off under water", _sub(WC, 'SubtypeId="LargeRailgun" Volume="210" FunctionalUnderwater="true"',
                                               'SubtypeId="LargeRailgun" Volume="210" FunctionalUnderwater="false"'), {"s14.weapons.values"}),
    ("S14 a turret's volume changed (buoyancy)", _sub(WC, 'SubtypeId="" Volume="23" FunctionalUnderwater="true"',
                                                    'SubtypeId="" Volume="24" FunctionalUnderwater="true"'), {"s14.weapons.values"}),
    ("S14 the gatling gun entry removed", _sub(WC, '<BlockConfig TypeId="SmallGatlingGun" SubtypeId="" Volume="0.3125" FunctionalUnderwater="true" '
                                                'FunctionalAboveWater="true" PlayDamageEffect="false" IsPressurized="false"/>', ""), {"s14.weapons.entries"}),
    ("S14 misspelt FunctionalUnderwater", _sub(WC, 'SubtypeId="SmallRailgun" Volume="1.875" FunctionalUnderwater=',
                                                'SubtypeId="SmallRailgun" Volume="1.875" FunctionalUnderWater='), {"s14.weapons.known", "s14.weapons.values"}),
    ("S14 override removed", _cut(WC, "<BlockConfigs>", "</BlockConfigs>"), {"s14.weapons.parse", "s14.weapons.entries"}),
]

class SeededFailureTests(unittest.TestCase):
    def test_every_mutant_turns_its_check_red(self):
        unmutated = cp.run(MOD)
        self.assertEqual([], unmutated.failed, "the unmutated folder must pass")
        for name, mutate, must_fail in MUTANTS:
            if must_fail == "@matR1":
                must_fail = mat_faces_with(1)    # the faces whose sand (R=1) loses its table row
                self.assertTrue(must_fail, "the sculpt uses Sand_02 (R=1) on at least one face")
            with self.subTest(mutant=name), tempfile.TemporaryDirectory() as tmp:
                d = os.path.join(tmp, "DeepEngineers.World")
                shutil.copytree(MOD, d)
                mutate(d)
                failed = cp.run(d).ids("FAIL")
                missing = must_fail - failed
                self.assertFalse(missing, "%s: expected FAIL of %s, got FAIL of %s" % (name, sorted(missing), sorted(failed)))

    def test_cli_exit_codes(self):
        with tempfile.TemporaryDirectory() as tmp:
            d = os.path.join(tmp, "DeepEngineers.World")
            shutil.copytree(MOD, d)
            ok = subprocess.run([sys.executable, "-I", os.path.join(REPO, "tools", "worldmod", "check_planet.py"), "--mod", d],
                                capture_output=True, text=True)
            self.assertEqual(ok.returncode, 0, ok.stdout[-2000:])
            self.assertIn("CHECK PLANET PASS", ok.stdout)
            _sub(WC, "<CrushDamage>0</CrushDamage>", "<CrushDamage>0.5</CrushDamage>")(d)
            bad = subprocess.run([sys.executable, "-I", os.path.join(REPO, "tools", "worldmod", "check_planet.py"), "--mod", d],
                                 capture_output=True, text=True)
            self.assertEqual(bad.returncode, 1)
            self.assertIn("CHECK PLANET FAIL", bad.stdout)

class DocAndDeployTests(unittest.TestCase):
    def test_lab_doc_numbers(self):
        text = read(DOC, "r")
        sp = cp.spawn_numbers(tuple(float(c) for c in site_v1.TEST_C))
        for value in ("-1131072.5", "118927.5", "1368927.5", "1.052632", "1.0526316", "120000", "DE_4546B",
                      "60000.00390625", "-1000000", "250000", "1500000", "1560050"):
            self.assertIn(value, text, value)
        self.assertEqual(sp["savedMinCorner"], (-1131072.5, 118927.5, 1368927.5))
        self.assertNotIn("/wrradius prints 1.0526316", text, "the Water Mod's /wrradius does not print the setting")

    def test_deploy_script_targets_only_the_mod_folder(self):
        text = read(DEPLOY, "r")
        self.assertIn("SpaceEngineers\\Mods\\DeepEngineers.World", text)
        for forbidden in ("\\Saves", "steamapps\\common", "workshop\\content", "Remove-Item -Recurse", "rmdir"):
            self.assertNotIn(forbidden, text, forbidden)

    def test_deploy_script_parses(self):
        ps = shutil.which("powershell") or shutil.which("pwsh")
        if not ps:
            self.skipTest("PowerShell not found")
        cmd = ("$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('%s',[ref]$t,[ref]$e);"
               "if($e.Count -eq 0){'PARSE OK'}else{$e|ForEach-Object{$_.ToString()}}" % DEPLOY.replace("'", "''"))
        out = subprocess.run([ps, "-NoProfile", "-NonInteractive", "-Command", cmd], capture_output=True, text=True)
        self.assertIn("PARSE OK", out.stdout, out.stdout + out.stderr)

if __name__ == "__main__":
    unittest.main(verbosity=2)
