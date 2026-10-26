"""The installer's pins (de_pins.py) against the plugins' own pins in the C# sources and the launcher, and the pinned
zips against the copies the development PC installed from. Read-only."""
import hashlib
import os
import re
import unittest
import zipfile

import de_pins as P
import fixtures as F

REPO = F.REPO

def src(*parts):
    with open(os.path.join(REPO, *parts), encoding="utf-8") as f:
        return f.read()

def const(text, name):
    m = re.search(r"\b%s\s*=\s*([^;]+);" % re.escape(name), text)
    return m.group(1).strip() if m else None

class PinsAgainstSources(unittest.TestCase):
    def test_se_version_gate(self):
        self.assertEqual(int(const(src("src", "DeepEngineers.SE", "VersionGate.cs"), "ExpectedGameVersionConst")),
                         P.SE_GAME_VERSION)
        self.assertIn("Steam build %s" % P.SE_BUILD_ID, src("src", "DeepEngineers.SE", "VersionGate.cs"))

    def test_subnautica_terrain_pins(self):
        site = src("src", "DeepEngineers.Wire", "SiteV1.cs")
        self.assertEqual(const(site, "SnManifestBuildId"), P.SN_BUILD_ID)
        self.assertEqual(int(const(site, "SnManifestFiles")), P.SN_TERRAIN_FILES)
        self.assertEqual(int(const(site, "SnManifestBytes").rstrip("L")), P.SN_TERRAIN_BYTES)
        self.assertEqual((int(const(site, "SnBatchRowFirst")), int(const(site, "SnBatchRowLast"))), P.SN_TERRAIN_ROWS)
        man = src("src", "DeepEngineers.Terrain", "SnSourceManifest.cs")
        self.assertEqual(int(const(man, "public const ulong ExpectedHash").rstrip("UL"), 16), P.SN_TERRAIN_HASH)
        for line in P.SN_TERRAIN_INDEX_LINES:
            self.assertIn('"%s"' % line, man)
        self.assertIn('"%s"' % "Build18", man)

    def test_changeset_reading(self):
        self.assertIn("plastic_status.ignore", src("src", "DeepEngineers.SN", "HelloInfo.cs"))
        self.assertIn('("%s")' % P.SN_CHANGESET, src("src", "DeepEngineers.SN", "HelloInfo.cs"))

    def test_water_mod(self):
        self.assertEqual(const(src("src", "DeepEngineers.SE", "WaterModClient.cs"), "public const string PinnedVersion"),
                         '"%s"' % P.WATER_MOD_VERSION)
        self.assertEqual(const(src("src", "DeepEngineers.Launcher", "launcher.py").replace("\n", ";\n"),
                               "WATER_MOD_PINNED"), '"%s"' % P.WATER_MOD_VERSION)

    def test_freedom_mode_value(self):
        rules = src("src", "DeepEngineers.SN", "HostSaveRules.cs")
        self.assertRegex(rules, r"Freedom\s*=\s*%d" % P.SN_HOST_GAME_MODE_FREEDOM)

    def test_download_specs_are_complete(self):
        for spec in P.DOWNLOADS:
            self.assertTrue(spec["url"].startswith("https://github.com/"), spec["url"])
            self.assertTrue(spec["url"].endswith("/" + spec["file"]))
            self.assertRegex(spec["sha256"], r"^[0-9a-f]{64}$")
            self.assertGreater(spec["size"], 0)

class PinsAgainstLocalCopies(unittest.TestCase):
    """The third_party zips are the files the development PC installed; skipped where they are absent."""

    def zip_path(self, *parts):
        p = os.path.join(REPO, "third_party", *parts)
        if not os.path.isfile(p):
            self.skipTest("no local copy %s" % p)
        return p

    def test_pulsar_zip(self):
        p = self.zip_path("pulsar", P.PULSAR["file"])
        with open(p, "rb") as f:
            data = f.read()
        self.assertEqual((hashlib.sha256(data).hexdigest(), len(data)), (P.PULSAR["sha256"], P.PULSAR["size"]))
        with zipfile.ZipFile(p) as z:
            self.assertEqual(hashlib.sha256(z.read(P.PULSAR["marker"])).hexdigest(), P.PULSAR["marker_sha256"])

    def test_bepinex_pack_zip(self):
        p = self.zip_path("bepinex-subnautica", P.BEPINEX_PACK["file"])
        with open(p, "rb") as f:
            data = f.read()
        self.assertEqual((hashlib.sha256(data).hexdigest(), len(data)),
                         (P.BEPINEX_PACK["sha256"], P.BEPINEX_PACK["size"]))
        with zipfile.ZipFile(p) as z:
            for rel, h in P.BEPINEX_PACK["markers"].items():
                self.assertEqual(hashlib.sha256(z.read(rel)).hexdigest(), h, rel)

if __name__ == "__main__":
    unittest.main()
