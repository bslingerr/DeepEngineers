"""launcher_release.py backup: what the release zip step takes (scope de / full), the um-compatible zip, pruning."""
import json
import os
import tempfile
import unittest
import zipfile

import fixtures as F
import launcher_release as R

class BackupScope(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = os.path.join(self.tmp.name, "SpaceEngineers")
        saves = os.path.join(self.root, "Saves", F.FAKE_USER)
        F.write(os.path.join(self.root, "SpaceEngineers.cfg"), "cfg")
        F.write(os.path.join(self.root, "Storage", "WaterClientSettings.xml"), "<Volume>1</Volume>")
        F.write(os.path.join(self.root, "Storage", "other-mod.cfg"), "x")
        F.write(os.path.join(self.root, "SpaceEngineers_20261018_1.log"), "log")
        F.write(os.path.join(self.root, "ShaderCache2", "a.bin"), "cache")
        F.write(os.path.join(saves, "LastSession.sbl"), "last")
        F.write(os.path.join(saves, "Dive", "Sandbox_config.sbc"), "<Name>DeepEngineers.World</Name>")
        F.write(os.path.join(saves, "Dive", "SANDBOX_0_0_0_.sbs"), "sector")
        F.write(os.path.join(saves, "Dive", "Backup", "2026", "Sandbox.sbc"), "old")
        F.write(os.path.join(saves, "Plain", "Sandbox_config.sbc"), "<Name>2200451495.sbm</Name>")

    def tearDown(self):
        self.tmp.cleanup()

    def rels(self, scope):
        return sorted(r.replace(os.sep, "/") for _f, r in R.se_appdata_files(self.root, scope))

    def test_de_scope(self):
        self.assertEqual(self.rels("de"), ["Saves/%s/Dive/SANDBOX_0_0_0_.sbs" % F.FAKE_USER,
                                           "Saves/%s/Dive/Sandbox_config.sbc" % F.FAKE_USER,
                                           "Saves/%s/LastSession.sbl" % F.FAKE_USER,
                                           "SpaceEngineers.cfg", "Storage/WaterClientSettings.xml"])

    def test_full_scope_skips_caches_and_logs(self):
        r = self.rels("full")
        self.assertIn("Saves/%s/Plain/Sandbox_config.sbc" % F.FAKE_USER, r)
        self.assertIn("Saves/%s/Dive/Backup/2026/Sandbox.sbc" % F.FAKE_USER, r)
        self.assertNotIn("ShaderCache2/a.bin", r)
        self.assertNotIn("SpaceEngineers_20261018_1.log", r)

    def test_zip_format_and_prune(self):
        dst = os.path.join(self.tmp.name, "backups", "se-appdata")
        import datetime
        paths = []
        for i in range(5):
            t = datetime.datetime(2026, 10, 18, 10, 0, i)
            paths.append(R.write_zip(dst, self.root, R.se_appdata_files(self.root, "de"), "test", now=t)[0])
        with zipfile.ZipFile(paths[-1]) as z:
            man = json.loads(z.read("_um_manifest.json"))
            self.assertEqual(man["created"], "20261018-100004")
            self.assertIn("SpaceEngineers.cfg", man["files"])
            self.assertEqual(z.read("Storage/WaterClientSettings.xml"), b"<Volume>1</Volume>")
        gone = R.prune(dst, 3)
        self.assertEqual(gone, ["20261018-100000.zip", "20261018-100001.zip"])
        self.assertEqual(sorted(os.listdir(dst)), ["20261018-100002.zip", "20261018-100003.zip",
                                                   "20261018-100004.zip"])
        self.assertEqual(R.prune(dst, 0), [])

    def test_same_second_does_not_overwrite(self):
        import datetime
        dst = os.path.join(self.tmp.name, "b")
        t = datetime.datetime(2026, 10, 18, 10, 0, 0)
        a = R.write_zip(dst, self.root, [], "x", now=t)[0]
        b = R.write_zip(dst, self.root, [], "x", now=t)[0]
        self.assertNotEqual(a, b)

    def test_stamp_without_a_time(self):
        # the installed 1.0.0 crashed here on a player's PC: a class import shadowed the datetime module
        import re
        self.assertRegex(R._stamp(None), r"^\d{8}-\d{6}$")
        path = R.write_zip(os.path.join(self.tmp.name, "c"), self.root, [], "x")[0]
        self.assertTrue(os.path.isfile(path))

if __name__ == "__main__":
    unittest.main()
