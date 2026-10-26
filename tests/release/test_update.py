"""S9 round 2: the updater without a network: a fake GitHub answer, a fake release zip, a wrong hash, the
private-repository case, the switch, the apply over a release folder and the repair call."""
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
import urllib.error
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
LAUNCHER = os.path.join(REPO, "src", "DeepEngineers.Launcher")
for _p in (LAUNCHER, os.path.join(REPO, "tests", "wire")):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import de_update as U  # noqa: E402

def make_zip(path: str, version: str, top: str = "DeepEngineers-%s") -> str:
    top = top % version if "%s" in top else top
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(top + "/release.json", json.dumps({"version": version, "flavour": "release"}))
        z.writestr(top + "/plugins/plugin.dll", b"new plugin " + version.encode())
        z.writestr(top + "/launcher/launcher.py", "# launcher %s\n" % version)
        z.writestr(top + "/README.md", "README %s\n" % version)
        z.writestr(top + "/SHA256SUMS.txt", "")
    return path

class FakeNet:
    """opener: the latest-release JSON, the zip bytes, the sums text; records the calls."""

    def __init__(self, latest=None, zip_bytes=b"", sums_text="", http_error=None):
        self.latest, self.zip_bytes, self.sums_text, self.http_error = latest, zip_bytes, sums_text, http_error
        self.calls = []

    def __call__(self, url, token, dst=None, timeout=None):
        self.calls.append((url, token, dst))
        if self.http_error is not None:
            raise urllib.error.HTTPError(url, self.http_error, "x", {}, io.BytesIO(b""))
        if dst is None:
            return self.latest
        with open(dst, "wb") as f:
            f.write(self.zip_bytes if url.endswith(".zip") else self.sums_text.encode("utf-8"))
        return dst

def latest_doc(version: str) -> dict:
    name = "DeepEngineers-%s.zip" % version
    return {"tag_name": "v" + version, "assets": [
        {"name": name, "browser_download_url": "https://x/" + name},
        {"name": "SHA256SUMS.txt", "browser_download_url": "https://x/SHA256SUMS.txt"}]}

class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="de-update-test-")
        self.release = os.path.join(self.tmp, "release")
        self.local = os.path.join(self.tmp, "local")
        os.makedirs(os.path.join(self.release, "plugins"))
        os.makedirs(self.local)
        with open(os.path.join(self.release, "release.json"), "w", encoding="utf-8") as f:
            json.dump({"version": "1.0.0"}, f)
        with open(os.path.join(self.release, "plugins", "plugin.dll"), "wb") as f:
            f.write(b"old plugin")
        self.lines = []
        self.repairs = []
        self._repair = U.repair_install
        U.repair_install = lambda root, py, log=print: (self.repairs.append(root), 0)[1]
        os.environ.pop("DEEPENGINEERS_UPDATE", None)

    def tearDown(self):
        U.repair_install = self._repair
        shutil.rmtree(self.tmp, ignore_errors=True)
        os.environ.pop("DEEPENGINEERS_UPDATE", None)

    def log(self, s):
        self.lines.append(s)

    def test_version_compare(self):
        self.assertTrue(U.newer("1.2.0", "1.1.9"))
        self.assertTrue(U.newer("v2.0", "1.9.9"))
        self.assertFalse(U.newer("1.0.0", "1.0.0"))
        self.assertFalse(U.newer("1.0.0", "1.0.1"))
        self.assertTrue(U.newer("1.0.1", None))
        self.assertFalse(U.newer("", "1.0"))

    def test_private_repository_is_unavailable_not_a_stop(self):
        net = FakeNet(http_error=404)
        code = U.run_update(self.release, self.local, log=self.log, opener=net)
        self.assertEqual(code, 0)
        self.assertTrue(any("check unavailable" in l and "private" in l for l in self.lines), self.lines)
        self.assertEqual(self.repairs, [])

    def test_switch_off(self):
        os.environ["DEEPENGINEERS_UPDATE"] = "off"
        net = FakeNet(latest=latest_doc("9.9.9"))
        self.assertEqual(U.run_update(self.release, self.local, log=self.log, opener=net), 0)
        self.assertEqual(net.calls, [])

    def test_up_to_date(self):
        net = FakeNet(latest=latest_doc("1.0.0"))
        self.assertEqual(U.run_update(self.release, self.local, log=self.log, opener=net), 0)
        self.assertTrue(any("latest release" in l for l in self.lines))

    def test_check_only_reports_newer(self):
        net = FakeNet(latest=latest_doc("1.1.0"))
        self.assertEqual(U.run_update(self.release, self.local, check_only=True, log=self.log, opener=net), U.EXIT_NEWER)
        self.assertEqual(len(net.calls), 1)

    def test_wrong_hash_is_discarded(self):
        zpath = make_zip(os.path.join(self.tmp, "DeepEngineers-1.1.0.zip"), "1.1.0")
        with open(zpath, "rb") as f:
            data = f.read()
        net = FakeNet(latest=latest_doc("1.1.0"), zip_bytes=data, sums_text="%s  DeepEngineers-1.1.0.zip\n" % ("0" * 64))
        code = U.run_update(self.release, self.local, yes=True, log=self.log, opener=net)
        self.assertEqual(code, 1)
        self.assertTrue(any("mismatch" in l for l in self.lines), self.lines)
        with open(os.path.join(self.release, "plugins", "plugin.dll"), "rb") as f:
            self.assertEqual(f.read(), b"old plugin")
        self.assertFalse(os.path.exists(os.path.join(self.local, "updates", "DeepEngineers-1.1.0.zip")))

    def test_good_update_applies_and_repairs(self):
        zpath = make_zip(os.path.join(self.tmp, "DeepEngineers-1.1.0.zip"), "1.1.0")
        with open(zpath, "rb") as f:
            data = f.read()
        sums = "%s  DeepEngineers-1.1.0.zip\n" % hashlib.sha256(data).hexdigest()
        net = FakeNet(latest=latest_doc("1.1.0"), zip_bytes=data, sums_text=sums)
        code = U.run_update(self.release, self.local, yes=True, log=self.log, opener=net)
        self.assertEqual(code, U.EXIT_UPDATED, self.lines)
        with open(os.path.join(self.release, "plugins", "plugin.dll"), "rb") as f:
            self.assertEqual(f.read(), b"new plugin 1.1.0")
        self.assertEqual(U.installed_version(self.release), "1.1.0")
        self.assertTrue(os.path.isfile(os.path.join(self.release, "launcher", "launcher.py")))
        self.assertEqual(self.repairs, [self.release])

    def test_declined_keeps_the_install(self):
        zpath = make_zip(os.path.join(self.tmp, "DeepEngineers-1.1.0.zip"), "1.1.0")
        with open(zpath, "rb") as f:
            data = f.read()
        net = FakeNet(latest=latest_doc("1.1.0"), zip_bytes=data, sums_text="x")
        code = U.run_update(self.release, self.local, ask=lambda prompt: "n", log=self.log, opener=net)
        self.assertEqual(code, 0)
        self.assertEqual(len(net.calls), 1)

    def test_zip_not_newer_than_installed_is_refused(self):
        zpath = make_zip(os.path.join(self.tmp, "DeepEngineers-1.1.0.zip"), "0.9.0")   # the asset name lies
        with open(zpath, "rb") as f:
            data = f.read()
        sums = "%s  DeepEngineers-1.1.0.zip\n" % hashlib.sha256(data).hexdigest()
        net = FakeNet(latest=latest_doc("1.1.0"), zip_bytes=data, sums_text=sums)
        self.assertEqual(U.run_update(self.release, self.local, yes=True, log=self.log, opener=net), 1)
        self.assertEqual(self.repairs, [])

    def test_token_file_is_sent(self):
        with open(os.path.join(self.local, "github-token.txt"), "w", encoding="utf-8") as f:
            f.write("ghp_test\n")
        net = FakeNet(latest=latest_doc("1.0.0"))
        U.run_update(self.release, self.local, log=self.log, opener=net)
        self.assertEqual(net.calls[0][1], "ghp_test")

    def test_unsafe_zip_path_refused(self):
        zpath = os.path.join(self.tmp, "bad.zip")
        with zipfile.ZipFile(zpath, "w") as z:
            z.writestr("DeepEngineers-1.1.0/release.json", json.dumps({"version": "1.1.0"}))
            z.writestr("DeepEngineers-1.1.0/../evil.txt", "x")
        with self.assertRaises(ValueError):
            U.apply_zip(zpath, self.release, log=self.log)

if __name__ == "__main__":
    unittest.main()
