"""tools/release/make_release.py on the repository's current build, into a temporary folder: the layout, the hashes
of release.json and SHA256SUMS.txt, the release-mode launcher. Skipped when no matching
Release build exists (both plugins and one Wire.dll)."""
import hashlib
import io
import json
import os
import tempfile
import unittest
from contextlib import redirect_stdout

import fixtures as F
import make_release as M

SE_BIN = os.path.join(F.REPO, "src", "DeepEngineers.SE", "bin", "Release", "net48")
SN_BIN = os.path.join(F.REPO, "src", "DeepEngineers.SN", "bin", "Release", "net472")

def sha(p):
    with open(p, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()

class MakeRelease(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        need = [os.path.join(SE_BIN, n) for n in M.SE_FILES] + [os.path.join(SN_BIN, n) for n in
                                                                 M.SN_FILES + ("DeepEngineers.Wire.dll",)]
        if not all(os.path.isfile(p) for p in need):
            raise unittest.SkipTest("no Release build")
        if sha(os.path.join(SE_BIN, "DeepEngineers.Wire.dll")) != sha(os.path.join(SN_BIN, "DeepEngineers.Wire.dll")):
            raise unittest.SkipTest("the SE and SN outputs come from two builds (a build is running)")
        cls.tmp = tempfile.TemporaryDirectory()
        cls.out = os.path.join(cls.tmp.name, "DeepEngineers-test")
        old = os.environ.get("LOCALAPPDATA")
        os.environ["LOCALAPPDATA"] = os.path.join(cls.tmp.name, "local")      # the smoke run's launcher log
        try:
            buf = io.StringIO()
            with redirect_stdout(buf):
                cls.rc = M.main(["--out", cls.out, "--allow-missing-docs", "--version", "test", "--flavour", "dev"])
            cls.log = buf.getvalue()
        finally:
            if old is None:
                os.environ.pop("LOCALAPPDATA", None)
            else:
                os.environ["LOCALAPPDATA"] = old

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_passes(self):
        self.assertEqual(self.rc, 0, self.log)

    def test_layout(self):
        for rel in M.ROOT_FILES + ("release.json", "SHA256SUMS.txt", "plugins/plugin.dll", "plugins/DeepEngineers.SN.dll",
                                   "plugins/DeepEngineers.Wire.dll", "worldmod/DeepEngineers.World/metadata.mod",
                                   "world-template/DeepEngineers/Sandbox.sbc", "launcher/launcher.py",
                                   "launcher/de_install.py", "launcher/layout_py.py"):
            self.assertTrue(os.path.isfile(os.path.join(self.out, *rel.split("/"))), rel)
        for base, dirs, files in os.walk(self.out):
            self.assertNotIn("__pycache__", dirs)
            for n in files:
                self.assertFalse(n.endswith((".pdb", ".pyc")), n)

    def test_hashes(self):
        with open(os.path.join(self.out, "release.json"), encoding="utf-8") as f:
            doc = json.load(f)
        listed = {e["path"] for e in doc["files"]}
        on_disk = set(F.snapshot(self.out)) - {"release.json", "SHA256SUMS.txt"}
        self.assertEqual(listed, on_disk)
        for e in doc["files"]:
            self.assertEqual(sha(os.path.join(self.out, *e["path"].split("/"))), e["sha256"], e["path"])
        self.assertEqual(doc["pins"]["se"]["buildId"], "24675677")
        self.assertEqual(doc["files"] and doc["version"], "test")
        with open(os.path.join(self.out, "SHA256SUMS.txt"), encoding="utf-8") as f:
            sums = dict(reversed(line.rstrip("\n").split(" *", 1)) for line in f if line.strip())
        self.assertIn("release.json", sums)
        self.assertEqual(sums["plugins/plugin.dll"], sha(os.path.join(SE_BIN, "plugin.dll")))

    def test_plugins_are_the_build(self):
        self.assertEqual(sha(os.path.join(self.out, "plugins", "DeepEngineers.SN.dll")),
                         sha(os.path.join(SN_BIN, "DeepEngineers.SN.dll")))

if __name__ == "__main__":
    unittest.main()
