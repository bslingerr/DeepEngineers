"""S9 one-click installer: the Workshop subscribe-and-wait flow, the guided host-save wait, the Desktop and
Start-menu entries (fake helper and the real PowerShell one), the uninstall of all of it, and install.ps1's bootstrap of
the pinned interpreter with its failure paths (a hash mismatch, no network, a partial download, an interrupted unpack,
a blocked python.exe, an existing Python, a folder run from inside the zip, a non-ASCII profile path). Local files only:
the "embeddable zip" is built from the running interpreter's own files and served through a file:// URL."""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile

import de_install as I
import de_pins as P
import fixtures as F
from test_install import InstallBase

REPO = F.REPO
INSTALL_PS1 = os.path.join(REPO, "tools", "release", "root", "install.ps1")
WINDOWS = os.name == "nt" and shutil.which("powershell.exe") is not None

class Clock:
    """A fake clock: sleep advances time and lets the test change the world on the n-th poll."""

    def __init__(self, on_sleep=None):
        self.t, self.n, self.on_sleep = 0.0, 0, on_sleep

    def clock(self):
        return self.t

    def sleep(self, s):
        self.t += s
        self.n += 1
        if self.on_sleep:
            self.on_sleep(self.n)

def workshop_dir(m, item):
    return os.path.join(m.steam, "steamapps", "workshop", "content", P.SE_APP_ID, item)

def steam_downloads(m, water_version=P.WATER_MOD_VERSION):
    F.write(os.path.join(workshop_dir(m, P.WATER_MOD_ID), *P.WATER_MOD_VERSION_FILE),
            'namespace Jakaria { public static class WaterData { public const string Version = "%s"; } }\n' %
            water_version)
    F.write(os.path.join(workshop_dir(m, P.TEXT_HUD_API_ID), "metadata.mod"), "<ModMetadata />\n")

class WorkshopWait(InstallBase):
    machine_kw = {"workshop": False}

    def waiting(self, on_sleep, **kw):
        clock, opened = Clock(on_sleep), []
        rc, rep, c = self.install(wait=True, open_url=opened.append, sleep=clock.sleep, clock=clock.clock, **kw)
        return rc, rep, opened, clock

    def test_opens_both_pages_and_continues_when_steam_has_downloaded(self):
        rc, rep, opened, clock = self.waiting(lambda n: n == 3 and steam_downloads(self.m))
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(opened, ["steam://url/CommunityFilePage/%s" % P.WATER_MOD_ID,
                                  "steam://url/CommunityFilePage/%s" % P.TEXT_HUD_API_ID])
        self.assertEqual(clock.n, 3)
        self.assertEqual([s for s, _t in self.statuses(rep, "workshop")], ["INFO", "INFO", "INFO", "PASS", "PASS"])

    def test_timeout_fails_with_the_subscribe_words(self):
        rc, rep, opened, clock = self.waiting(None)
        self.assertEqual(rc, 1)
        self.assertGreaterEqual(clock.t, 900)
        fails = [t for s, t in self.statuses(rep, "workshop") if s == "FAIL"]
        self.assertEqual(len(fails), 2)
        self.assertIn("subscribe to it (%s)" % P.workshop_url(P.WATER_MOD_ID), fails[0])

    def test_a_different_water_mod_version_stops_clearly(self):
        rc, rep, opened, clock = self.waiting(lambda n: n == 1 and steam_downloads(self.m, "3.24"))
        self.assertEqual(rc, 1)
        self.assertTrue(any("Water Mod version 3.24" in t and "pinned to %s" % P.WATER_MOD_VERSION in t
                            for s, t in self.statuses(rep, "workshop") if s == "FAIL"))

    def test_no_wait_without_the_flag(self):
        rc, rep, c = self.install()
        self.assertEqual(rc, 1)
        self.assertFalse(any(s == "INFO" for s, _t in self.statuses(rep, "workshop")))

class HostSaveWait(InstallBase):
    machine_kw = {"host": False}

    def test_install_never_starts_subnautica(self):
        #: the release ships a fresh Freedom save; the guided "make the save yourself" wait is gone
        clock, opened = Clock(), []
        rc, rep, c = self.install(wait=True, open_url=opened.append, sleep=clock.sleep, clock=clock.clock)
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(opened, [])
        self.assertTrue(any(s == "PASS" and "slot0000" in t for s, t in self.statuses(rep, "hostsave")))
        with open(c.paths_path, encoding="utf-8") as f:
            self.assertEqual(json.load(f)["snHostSlot"], "slot0000")

    def test_without_the_shipped_save_the_first_play_makes_it(self):
        # a release folder without hostsave/slot (an old zip): no FAIL, the Subnautica plugin creates the save at the
 # first Play (HostSave.CreateSave); the install goes on
        shutil.rmtree(os.path.join(self.rel, "hostsave"))
        rj = os.path.join(self.rel, "release.json")
        with open(rj, encoding="utf-8") as f:
            doc = json.load(f)
        doc["files"] = [e for e in doc["files"] if not e["path"].startswith("hostsave/")]
        with open(rj, "w", encoding="utf-8") as f:
            json.dump(doc, f, indent=1)
        clock = Clock()
        rc, rep, c = self.install(wait=True, open_url=lambda u: None, sleep=clock.sleep, clock=clock.clock)
        self.assertEqual(rc, 0, rep.rows)
        self.assertTrue(any("first Play" in t for s, t in self.statuses(rep, "hostsave") if s == "INFO"), rep.rows)
        self.assertFalse(any(s == "FAIL" for s, _t in self.statuses(rep, "hostsave")))

class FakeShortcuts:
    """The PowerShell helper's contract without PowerShell: writes the temporary.lnk, reports the destination and the
 target of a shortcut already there (the bytes' last line)."""

    def __init__(self, desktop, programs):
        self.base = {"desktop": desktop, "programs": programs}

    def __call__(self, job, workdir):
        out = []
        for l in job["links"]:
            F.write(l["tmp"], ("LNK\n" + l["target"]).encode("utf-8"))
            dest = os.path.join(self.base[l["where"]], l["rel"])
            existing = ""
            if os.path.isfile(dest):
                with open(dest, "rb") as f:
                    existing = f.read().decode("utf-8", "replace").split("\n")[-1]
            out.append({"dest": dest, "existingTarget": existing})
        return {"links": out}

class Shortcuts(InstallBase):
    def setUp(self):
        super().setUp()
        self.desktop = os.path.join(self.tmp.name, "Users", "player", "OneDrive", "Bureau Jörg")
        self.programs = os.path.join(self.tmp.name, "Users", "player", "Start Menu", "Programme")
        os.makedirs(self.desktop)
        os.makedirs(self.programs)

    def kw(self, real=False):
        return {"shortcuts": True, "shortcut_dirs": {"desktop": self.desktop, "programs": self.programs},
                "shortcut_maker": None if real else FakeShortcuts(self.desktop, self.programs)}

    def test_created_recorded_repaired_and_removed(self):
        mine = F.write(os.path.join(self.desktop, "DeepEngineers.lnk"), b"the player's own shortcut")
        rc, rep, c = self.install(**self.kw())
        self.assertEqual(rc, 0, rep.rows)
        links = [os.path.join(self.desktop, "DeepEngineers.lnk"),
                 os.path.join(self.programs, "DeepEngineers", "DeepEngineers.lnk"),
                 os.path.join(self.programs, "DeepEngineers", "Uninstall DeepEngineers.lnk")]
        for p in links:
            self.assertTrue(os.path.isfile(p), p)
        with open(links[2], "rb") as f:
            self.assertTrue(f.read().endswith(os.path.join(self.rel, "Uninstall.cmd").encode("utf-8")))
        ents = [e for e in self.manifest(c)["entries"] if e.get("group") == "shortcuts"]
        self.assertEqual(sorted(e["origin"] for e in ents if e["kind"] == "file"), ["created", "created", "replaced"])
        # second run: unchanged; a deleted one is repaired
        rc, rep, c = self.install(**self.kw())
        self.assertIn("3 unchanged", self.statuses(rep, "shortcuts")[0][1])
        os.remove(links[1])
        rc, rep, c = self.install(**self.kw())
        self.assertIn("1 repaired", self.statuses(rep, "shortcuts")[0][1])
        rc, rep, c = self.uninstall()
        self.assertEqual(rc, 0, rep.rows)
        self.assertFalse(os.path.exists(os.path.join(self.programs, "DeepEngineers")))
        with open(mine, "rb") as f:
            self.assertEqual(f.read(), b"the player's own shortcut")   # the player's file put back
        self.assertEqual(os.listdir(self.programs), [])

    def test_dry_run_writes_nothing(self):
        rc, rep, c = self.install(dry_run=True, **self.kw())
        self.assertEqual(self.statuses(rep, "shortcuts")[0][0], "INFO")
        self.assertEqual(os.listdir(self.desktop), [])

    def test_a_helper_failure_is_a_warning(self):
        def broken(job, workdir):
            raise OSError("COM refused")
        kw = self.kw()
        kw["shortcut_maker"] = broken
        rc, rep, c = self.install(**kw)
        self.assertEqual(rc, 0, rep.rows)
        self.assertIn("Play.cmd", self.statuses(rep, "shortcuts")[0][1])

    @unittest.skipUnless(WINDOWS, "Windows PowerShell")
    def test_real_powershell_helper_with_a_non_ascii_desktop(self):
        self.desktop = os.path.join(self.tmp.name, "Рабочий стол Jörg")
        os.makedirs(self.desktop)
        rc, rep, c = self.install(**self.kw(real=True))
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(self.statuses(rep, "shortcuts")[0][0], "PASS", rep.rows)
        lnk = os.path.join(self.desktop, "DeepEngineers.lnk")
        with open(lnk, "rb") as f:
            self.assertEqual(f.read(4), b"\x4c\x00\x00\x00")   # a shell link header
        rc, rep, c = self.install(**self.kw(real=True))
        self.assertIn("3 unchanged", self.statuses(rep, "shortcuts")[0][1])
        rc, rep, c = self.uninstall()
        self.assertFalse(os.path.exists(lnk))

class InterpreterFolder(InstallBase):
    def test_python_is_not_earlier_data_and_survives_the_python_side_of_uninstall(self):
        py = os.path.join(self.m.localappdata, "DeepEngineers", "python")
        F.write(os.path.join(py, "python.exe"), b"MZ the interpreter install.ps1 unpacked")
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(self.manifest(c)["localBefore"], [])
        rc, rep, c = self.uninstall()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(os.listdir(os.path.join(self.m.localappdata, "DeepEngineers")), ["python"])

# ----------------------------------------------------------------------------------------------------------------
# install.ps1: the interpreter bootstrap
# ----------------------------------------------------------------------------------------------------------------

def fake_embeddable(path: str):
    """A zip shaped like python.org's embeddable package, made of the running interpreter's files: python.exe, its DLLs
 and a._pth naming the running interpreter's Lib and DLLs folders (absolute), so the unpacked python.exe works."""
    base = sys.base_prefix
    tag = "python%d%d" % sys.version_info[:2]
    names = ["python.exe", tag + ".dll", "python3.dll", "vcruntime140.dll", "vcruntime140_1.dll"]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for n in names:
            if os.path.isfile(os.path.join(base, n)):
                z.write(os.path.join(base, n), n)
        z.writestr(tag + "._pth", "%s\r\n%s\r\n.\r\n" % (os.path.join(base, "Lib"), os.path.join(base, "DLLs")))
        z.writestr("LICENSE.txt", "test")
    return path

@unittest.skipUnless(WINDOWS and os.path.isfile(os.path.join(sys.base_prefix, "python%d%d.dll" % sys.version_info[:2])),
                     "Windows PowerShell and a CPython install to build the fake embeddable zip from")
class Bootstrap(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.shared = tempfile.TemporaryDirectory()
        cls.zip = fake_embeddable(os.path.join(cls.shared.name, "python-test-embed.zip"))
        cls.bad = os.path.join(cls.shared.name, "blocked", "python-test-embed.zip")
        os.makedirs(os.path.dirname(cls.bad))
        with zipfile.ZipFile(cls.bad, "w") as z:          # a python.exe that cannot start (what a blocker leaves)
            z.writestr("python.exe", b"MZ not really")

    @classmethod
    def tearDownClass(cls):
        cls.shared.cleanup()

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = self.tmp.name
        self.local = os.path.join(root, "Users", "Jörg Пользователь", "AppData", "Local")
        self.appdata = os.path.join(root, "Users", "Jörg Пользователь", "AppData", "Roaming")
        os.makedirs(self.local)
        os.makedirs(self.appdata)
        self.rel = os.path.join(root, "DeepEngineers-test")
        launcher = os.path.join(REPO, "src", "DeepEngineers.Launcher")
        for n in ("de_install.py", "de_pins.py", "de_steam.py", "launcher_paths.py"):
            F.write(os.path.join(self.rel, "launcher", n), open(os.path.join(launcher, n), "rb").read())
        shutil.copyfile(INSTALL_PS1, os.path.join(self.rel, "install.ps1"))
        self.pin()
        self.de = os.path.join(self.local, "DeepEngineers")

    def tearDown(self):
        self.tmp.cleanup()

    def pin(self, zip_path=None, **over):
        zp = zip_path or self.zip
        pin = {"version": "%d.%d.%d" % sys.version_info[:3], "file": "python-test-embed.zip",
               "url": pathlib.Path(zp).as_uri(), "page": "(test)", "sha256": F.sha_file(zp),
               "size": os.path.getsize(zp)}
        pin.update(over)
        F.write(os.path.join(self.rel, "release.json"), json.dumps({"product": "DeepEngineers", "version": "test",
                                                                     "pins": {"python": pin}}))
        return pin

    def ps(self, *args, system_python=False):
        env = dict(os.environ, LOCALAPPDATA=self.local, APPDATA=self.appdata)
        env.pop("DEEPENG_TEST_NO_SYSTEM_PYTHON", None)
        if not system_python:
            env["DEEPENG_TEST_NO_SYSTEM_PYTHON"] = "1"
        r = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                            os.path.join(self.rel, "install.ps1"), "-NoPause"] + list(args),
                           capture_output=True, env=env, timeout=300)
        return r.returncode, (r.stdout + r.stderr).decode("utf-8", "replace")

    def interpreter_ok(self):
        exe = os.path.join(self.de, "python", "python.exe")
        r = subprocess.run([exe, "-I", "-c", "import ssl, ctypes; print('ok')"], capture_output=True, text=True)
        return r.returncode == 0 and r.stdout.strip() == "ok"

    def leftovers(self):
        d = os.path.join(self.de, "downloads")
        return [n for n in (os.listdir(d) if os.path.isdir(d) else []) if n.endswith(".part")] + \
            [n for n in (os.listdir(self.de) if os.path.isdir(self.de) else []) if n.endswith(".new")]

    def test_fetch_verify_unpack_then_reuse(self):
        before = F.snapshot(self.rel)
        rc, out = self.ps("-BootstrapOnly")
        self.assertEqual(rc, 0, out)
        self.assertIn("SHA-256 verified", out)
        self.assertTrue(self.interpreter_ok())
        with open(os.path.join(self.de, "python", "deepengineers-python.json"), encoding="utf-8") as f:
            self.assertEqual(json.load(f)["sha256"], F.sha_file(self.zip))
        self.assertEqual(F.snapshot(self.rel), before)        # nothing written outside %LOCALAPPDATA%
        rc, out = self.ps("-BootstrapOnly")
        self.assertEqual(rc, 0, out)
        self.assertNotIn("downloading", out)
        self.assertIn("(Python %d.%d.%d, pinned)" % sys.version_info[:3], out)

    def test_hash_mismatch_unpacks_nothing(self):
        self.pin(sha256="0" * 64)
        rc, out = self.ps("-BootstrapOnly")
        self.assertEqual(rc, 1, out)
        self.assertIn("not the pinned", out)
        self.assertFalse(os.path.exists(os.path.join(self.de, "python")))
        self.assertEqual(self.leftovers(), [])
        self.assertEqual(os.listdir(os.path.join(self.de, "downloads")), [])

    def test_no_network_says_what_to_do_or_uses_an_existing_python(self):
        self.pin(url=pathlib.Path(os.path.join(self.shared.name, "nowhere.zip")).as_uri())
        rc, out = self.ps("-BootstrapOnly")
        self.assertEqual(rc, 1, out)
        self.assertIn("FAIL python", out)
        self.assertIn("-OfflineDir", out)
        self.assertEqual(self.leftovers(), [])
        rc, out = self.ps("-BootstrapOnly", system_python=True)
        self.assertEqual(rc, 0, out)
        self.assertIn("using the Python already on this PC", out)

    def test_offline_dir(self):
        self.pin(url="https://invalid.invalid/never.zip")
        rc, out = self.ps("-BootstrapOnly", "-OfflineDir", self.shared.name)
        self.assertEqual(rc, 0, out)
        self.assertTrue(self.interpreter_ok())

    def test_partial_download_and_interrupted_unpack_are_redone(self):
        cache = os.path.join(self.de, "downloads", "python-test-embed.zip")
        with open(self.zip, "rb") as f:
            F.write(cache, f.read()[:1000])
        F.write(os.path.join(self.de, "python.new", "half.dll"), b"x")
        rc, out = self.ps("-BootstrapOnly")
        self.assertEqual(rc, 0, out)
        self.assertIn("ignored", out)
        self.assertEqual(F.sha_file(cache), F.sha_file(self.zip))
        self.assertEqual(self.leftovers(), [])
        self.assertTrue(self.interpreter_ok())

    def test_an_interpreter_that_does_not_start_is_not_kept(self):
        self.pin(zip_path=self.bad)
        rc, out = self.ps("-BootstrapOnly")
        self.assertEqual(rc, 1, out)
        self.assertIn("does not start", out)
        self.assertFalse(os.path.exists(os.path.join(self.de, "python")))
        self.assertEqual(self.leftovers(), [])

    def test_an_unpinned_release_uses_an_existing_python(self):
        self.pin(sha256="")
        rc, out = self.ps("-BootstrapOnly", system_python=True)
        self.assertEqual(rc, 0, out)
        self.assertIn("names no interpreter SHA-256", out)
        self.assertFalse(os.path.exists(os.path.join(self.de, "python")))

    def test_run_from_inside_the_zip(self):
        os.remove(os.path.join(self.rel, "release.json"))
        rc, out = self.ps("-BootstrapOnly")
        self.assertEqual(rc, 1, out)
        self.assertIn("Extract the whole DeepEngineers zip first", out)

    def test_uninstall_removes_the_interpreter_after_the_python_side(self):
        rc, out = self.ps("-BootstrapOnly")
        self.assertEqual(rc, 0, out)
        man = os.path.join(self.de, "install", "install-manifest.json")
        F.write(man, json.dumps({"format": I.MANIFEST_FORMAT, "product": "DeepEngineers", "entries": [],
                                 "localBefore": []}))
        rc, out = self.ps("-Uninstall")
        self.assertEqual(rc, 0, out)
        self.assertIn("PASS python     removed", out)
        self.assertFalse(os.path.exists(self.de), os.listdir(self.de) if os.path.exists(self.de) else None)

class PinTool(unittest.TestCase):
    def test_refuses_a_zip_that_is_not_python_orgs(self):
        import S9_pin_python as T
        with tempfile.TemporaryDirectory() as d:
            z = os.path.join(d, "python-3.10.11-embed-amd64.zip")
            with zipfile.ZipFile(z, "w") as zf:
                zf.writestr("python.exe", b"x")
            sha, why = T.check(z)
            self.assertIsNone(sha)
            self.assertIn("size", why)
            spec = dict(P.PYTHON_EMBED, size=os.path.getsize(z), md5=__import__("hashlib").md5(open(z, "rb").read()).hexdigest())
            sha, why = T.check(z, spec)
            self.assertIn("python310.dll missing", why)

    def test_writes_only_the_python_sha_line(self):
        import S9_pin_python as T
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "de_pins.py")
            with open(os.path.join(REPO, "src", "DeepEngineers.Launcher", "de_pins.py"), encoding="utf-8") as f:
                text = f.read()
            if P.PYTHON_EMBED["sha256"]:
                text = text.replace('"sha256": "%s"' % P.PYTHON_EMBED["sha256"], '"sha256": ""')
            F.write(p, text)
            self.assertTrue(T.write_pin("ab" * 32, p))
            with open(p, encoding="utf-8") as f:
                new = f.read()
            self.assertEqual(new.count("ab" * 32), 1)
            self.assertIn('"sha256": "%s"' % P.PULSAR["sha256"], new)
            self.assertEqual(len(new.splitlines()), len(text.splitlines()))

    def test_release_json_carries_the_interpreter_pin(self):
        self.assertEqual(P.as_dict()["python"]["url"], P.PYTHON_EMBED["url"])
        self.assertTrue(P.PYTHON_EMBED["url"].startswith("https://www.python.org/ftp/python/%s/" % P.PYTHON_EMBED["version"]))
        self.assertRegex(P.PYTHON_EMBED["sha256"], r"^([0-9a-f]{64})?$")

if __name__ == "__main__":
    unittest.main()
