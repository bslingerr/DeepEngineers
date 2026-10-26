"""de_install.py end to end on a fake machine: install (downloads through file:// URLs), the manifest, idempotence and
repair, version pins, Workshop detection, download verification, uninstall back to the pre-install state, kept worlds
and kept loaders. No network, no game, nothing outside the temporary folder."""
import io
import json
import os
import re
import shutil
import tempfile
import unittest
import zipfile
from contextlib import redirect_stdout

import de_install as I
import de_pins as P
import fixtures as F

def run(fn, *a, **kw):
    buf = io.StringIO()
    with redirect_stdout(buf):
        rc = fn(*a, **kw)
    return rc, buf.getvalue()

class InstallBase(unittest.TestCase):
    machine_kw = {}

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.m = F.FakeMachine(self.tmp.name, **self.machine_kw)
        # S8S9 review R2/R3: the fake machine has 32 GB and SE's Experimental mode on, like a ready PC
        os.environ["DEEPENG_INSTALL_FAKE_MEMORY_GB"] = "32,20"
        se = os.path.join(self.m.appdata, "SpaceEngineers")
        os.makedirs(se, exist_ok=True)
        if not os.path.isfile(os.path.join(se, "SpaceEngineers.cfg")):
            F.write(os.path.join(se, "SpaceEngineers.cfg"), ExperimentalOption.CFG % "True")
        self.rel = F.make_release(self.tmp.name)
        self.before_sn = F.snapshot(self.m.sn)
        self.before_appdata = F.snapshot(self.m.appdata)

    def tearDown(self):
        self.tmp.cleanup()

    def ctx(self, **kw):
        kw.setdefault("world_name", "Dive Site")
        return F.ctx(self.m, self.rel, **kw)

    def install(self, **kw):
        c = self.ctx(**kw)
        rep = I.Report(quiet=True)
        rc, out = run(I.install, c, rep)
        return rc, rep, c

    def uninstall(self, **kw):
        flags = {k: kw.pop(k) for k in ("remove_worlds", "force_worldmod", "remove_backups", "keep_data") if k in kw}
        c = self.ctx(**kw)
        rep = I.Report(quiet=True)
        rc, out = run(I.uninstall, c, rep, **flags)
        return rc, rep, c

    def statuses(self, rep, step):
        return [(r[0], r[2]) for r in rep.rows if r[1] == step]

    def manifest(self, c):
        with open(c.manifest_path, encoding="utf-8") as f:
            return json.load(f)

class FreshInstall(InstallBase):
    def test_install_writes_everything_and_records_it(self):
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertFalse(rep.failed(), rep.rows)
        pulsar = os.path.join(self.m.localappdata, "DeepEngineers", "Pulsar")
        se_dir = os.path.join(pulsar, "Legacy", "Local", "DeepEngineers.SE")
        sn_dir = os.path.join(self.m.sn, "BepInEx", "plugins", "DeepEngineers")
        for name in ("plugin.dll", "DeepEngineers.Wire.dll"):
            self.assertEqual(F.sha_file(os.path.join(se_dir, name)), F.sha_file(os.path.join(self.rel, "plugins", name)))
        for name in ("DeepEngineers.SN.dll", "DeepEngineers.Wire.dll"):
            self.assertEqual(F.sha_file(os.path.join(sn_dir, name)), F.sha_file(os.path.join(self.rel, "plugins", name)))
        self.assertTrue(os.path.isfile(os.path.join(pulsar, "Legacy.exe")))
        for rel in self.m.pack_files:
            self.assertTrue(os.path.isfile(os.path.join(self.m.sn, *rel.split("/"))), rel)
        with open(os.path.join(pulsar, "Legacy", "Profiles", "Current.xml"), encoding="utf-8") as f:
            self.assertIn("<string>DeepEngineers.SE.dll</string>", f.read())
        mod = os.path.join(self.m.appdata, "SpaceEngineers", "Mods", "DeepEngineers.World")
        self.assertTrue(os.path.isfile(os.path.join(mod, "Data", "WaterConfig.xml")))
        world = os.path.join(self.m.appdata, "SpaceEngineers", "Saves", F.FAKE_USER, "Dive Site")
        with open(os.path.join(world, "Sandbox.sbc"), encoding="utf-8") as f:
            sbc = f.read()
        self.assertIn("<SessionName>Dive Site</SessionName>", sbc)
        self.assertNotIn("<WorldId>%s</WorldId>" % "6e1e5c3a-0d8f-4b52-9a51-454600000001", sbc)
        with open(os.path.join(world, "Sandbox_config.sbc"), encoding="utf-8") as f:
            self.assertIn("<SessionName>Dive Site</SessionName>", f.read())
        # manifest: every created file with its hash, the owned Pulsar tree, the profile, the world
        man = self.manifest(c)
        files = {os.path.normcase(e["path"]): e for e in man["entries"] if e["kind"] == "file"}
        for e in files.values():
            self.assertEqual(F.sha_file(e["path"]), e["sha256"], e["path"])
        self.assertIn(os.path.normcase(os.path.join(sn_dir, "DeepEngineers.SN.dll")), files)
        self.assertTrue(any(e["kind"] == "tree" and e["origin"] == "created" for e in man["entries"]))
        self.assertTrue(any(e["kind"] == "world" and e["name"] == "Dive Site" for e in man["entries"]))
        self.assertTrue(any(e["kind"] == "loader-dir" for e in man["entries"]))
        # paths.json for the release launcher
        with open(c.paths_path, encoding="utf-8") as f:
            pj = json.load(f)
        self.assertEqual(os.path.normcase(pj["subnautica"]), os.path.normcase(self.m.sn))
        self.assertEqual((pj["snHostSlot"], pj["snHostSession"]), ("slot0003", "11111111-2222-4333-8444-555555555555"))
        self.assertEqual(pj["steamUserId"], F.FAKE_USER)
        self.assertTrue(pj["pulsarOwned"])
        # the downloads were verified and cached
        self.assertTrue(os.path.isfile(os.path.join(c.downloads, self.m.specs["pulsar"]["file"])))

    def test_second_run_changes_nothing_and_repairs(self):
        self.install()
        sn_plugin = os.path.join(self.m.sn, "BepInEx", "plugins", "DeepEngineers", "DeepEngineers.SN.dll")
        after_first = F.snapshot(self.m.sn)
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(F.snapshot(self.m.sn), after_first)
        self.assertTrue(any("kept as it is" in t for _s, t in self.statuses(rep, "world")))
        os.remove(sn_plugin)
        with open(os.path.join(self.m.sn, "winhttp.dll"), "wb") as f:
            f.write(b"broken")
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(F.snapshot(self.m.sn), after_first)
        self.assertTrue(any("repaired" in t for _s, t in self.statuses(rep, "plugins")))
        st = I.Report(quiet=True)
        self.assertEqual(run(I.status, c, st)[0], 0, st.rows)

    def test_uninstall_restores_the_pre_install_state(self):
        F.write(os.path.join(self.m.sn, "steam_appid.txt"), "user's own file")       # the pack replaces it
        self.before_sn = F.snapshot(self.m.sn)
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        with open(os.path.join(self.m.sn, "steam_appid.txt"), encoding="utf-8") as f:
            self.assertEqual(f.read(), "264710")
        # what BepInEx and our plugin write at run time
        F.write(os.path.join(self.m.sn, "BepInEx", "LogOutput.log"), "log")
        F.write(os.path.join(self.m.sn, "BepInEx", "cache", "chainloader_typeloader.dat"), b"c")
        F.write(os.path.join(self.m.sn, "BepInEx", "config", "deepengineers.sn.cfg"), "cfg")
        F.write(os.path.join(self.m.localappdata, "DeepEngineers", "backups", "se-appdata", "20261018-100000.zip"), b"z")
        rc, rep, c = self.uninstall()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(F.snapshot(self.m.sn), self.before_sn)
        self.assertEqual(F.snapshot(self.m.appdata), self.before_appdata)
        local = os.path.join(self.m.localappdata, "DeepEngineers")
        self.assertEqual(sorted(os.listdir(local)), ["backups"])           # backups kept unless --remove-backups
        self.assertFalse(os.path.exists(os.path.join(local, "Pulsar")))

    def test_uninstall_keeps_data_that_was_there_before(self):
        # S9-integrator: an earlier DeepEngineers setup (the development PC) keeps its own data
        local = os.path.join(self.m.localappdata, "DeepEngineers")
        F.write(os.path.join(local, "logs", "launcher-20261017-120000.jsonl"), "{}\n")
        F.write(os.path.join(local, "census", "allowlist.json"), "{}")
        F.write(os.path.join(local, "registry-backup.json"), "{}")
        before = F.snapshot(local)
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(self.manifest(c)["localBefore"], ["census", "logs", "registry-backup.json"])
        F.write(os.path.join(local, "settings-backup.json"), "{}")       # the release launcher's first run
        F.write(os.path.join(local, "logs", "launcher-20261018-120000.jsonl"), "{}\n")
        rc, rep, c = self.install()                                         # a repair run keeps the first record
        self.assertEqual(self.manifest(c)["localBefore"], ["census", "logs", "registry-backup.json"])
        rc, rep, c = self.uninstall()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(sorted(os.listdir(local)), ["census", "logs", "registry-backup.json"])
        for rel, h in before.items():
            self.assertEqual(F.sha_file(os.path.join(local, *rel.split("/"))), h, rel)
        self.assertTrue(any("before the install" in t for _s, t in self.statuses(rep, "data")))

    def test_installer_logs_alone_are_not_earlier_data(self):
        local = os.path.join(self.m.localappdata, "DeepEngineers")
        F.write(os.path.join(local, "logs", "check-20261018-100000.jsonl"), "{}\n")   # Install.cmd -Check ran first
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(self.manifest(c)["localBefore"], [])
        rc, rep, c = self.uninstall()
        self.assertEqual(rc, 0, rep.rows)
        self.assertFalse(os.path.exists(local))

    def test_uninstall_keeps_a_played_world_and_the_world_mod(self):
        self.install()
        world = os.path.join(self.m.appdata, "SpaceEngineers", "Saves", F.FAKE_USER, "Dive Site")
        F.write(os.path.join(world, "SANDBOX_0_0_0_.sbsB5"), b"played")
        rc, rep, c = self.uninstall(remove_backups=True)
        self.assertEqual(rc, 0, rep.rows)
        self.assertTrue(os.path.isfile(os.path.join(world, "Sandbox.sbc")))
        self.assertTrue(os.path.isfile(os.path.join(self.m.appdata, "SpaceEngineers", "Mods", "DeepEngineers.World",
                                                    "metadata.mod")))
        self.assertTrue(self.statuses(rep, "worldmod"))
        # S8S9 review R1: while entries are kept, install\ (the originals + the manifest) stays so a later
 # Uninstall.cmd can finish; the manifest holds only the kept entries
        inst = os.path.join(self.m.localappdata, "DeepEngineers", "install")
        self.assertTrue(os.path.isdir(inst), "install\ kept while entries are kept")
        man = json.load(open(os.path.join(inst, "install-manifest.json"), encoding="utf-8"))
        self.assertTrue(man["entries"], "the kept entries stay recorded")
        self.assertTrue(all(e.get("group") in ("world", "worldmod") for e in man["entries"]), [e.get("group") for e in man["entries"]])

    def test_uninstall_remove_worlds(self):
        self.install()
        world = os.path.join(self.m.appdata, "SpaceEngineers", "Saves", F.FAKE_USER, "Dive Site")
        with open(os.path.join(world, "Sandbox.sbc"), "a", encoding="utf-8") as f:
            f.write("\n")
        rc, rep, c = self.uninstall(remove_worlds=True)
        self.assertEqual(rc, 0, rep.rows)
        self.assertFalse(os.path.exists(os.path.join(world, "Sandbox.sbc")))
        self.assertFalse(os.path.exists(os.path.join(self.m.appdata, "SpaceEngineers", "Mods", "DeepEngineers.World")))

    def test_uninstall_keeps_bepinex_used_by_another_mod(self):
        self.install()
        other = os.path.join(self.m.sn, "BepInEx", "plugins", "OtherMod", "Other.dll")
        F.write(other, b"MZ other mod")
        rc, rep, c = self.uninstall()
        self.assertEqual(rc, 0, rep.rows)
        self.assertTrue(os.path.isfile(other))
        self.assertTrue(os.path.isfile(os.path.join(self.m.sn, "BepInEx", "core", "BepInEx.dll")))
        self.assertTrue(os.path.isfile(os.path.join(self.m.sn, "winhttp.dll")))
        self.assertFalse(os.path.exists(os.path.join(self.m.sn, "BepInEx", "plugins", "DeepEngineers")))
        self.assertTrue(any("other mods" in t for _s, t in self.statuses(rep, "bepinex")))

    def test_dry_run_writes_nothing(self):
        rc, rep, c = self.install(dry_run=True)
        self.assertEqual(F.snapshot(self.m.sn), self.before_sn)
        self.assertEqual(F.snapshot(self.m.appdata), self.before_appdata)
        self.assertFalse(os.path.exists(c.manifest_path))
        # S9-integrator: on a PC without BepInEx the dry run plans the plugins instead of failing them
        self.assertEqual(rc, 0, rep.rows)
        self.assertFalse(rep.failed(), rep.rows)
        self.assertEqual([s for s, _t in self.statuses(rep, "plugins")], ["PASS"])

    def test_world_name_is_never_overwritten(self):
        world = os.path.join(self.m.appdata, "SpaceEngineers", "Saves", F.FAKE_USER, "Dive Site")
        F.write(os.path.join(world, "Sandbox.sbc"), "the user's own world")
        rc, rep, c = self.install()
        with open(os.path.join(world, "Sandbox.sbc"), encoding="utf-8") as f:
            self.assertEqual(f.read(), "the user's own world")
        self.assertTrue(any("not ours" in t for _s, t in self.statuses(rep, "world")))

class ExistingLoaders(InstallBase):
    def test_existing_pulsar_and_profile(self):
        user_pulsar = os.path.join(self.tmp.name, "MyPulsar")
        with zipfile.ZipFile(os.path.join(self.m.server, self.m.specs["pulsar"]["file"])) as z:
            z.extractall(user_pulsar)
        prof = os.path.join(user_pulsar, "Legacy", "Profiles", "Current.xml")
        original = '<?xml version="1.0"?>\n<Profile>\n  <Name>Current</Name>\n  <Local />\n  <Mods />\n</Profile>'
        F.write(prof, original)
        rc, rep, c = self.install(pulsar_dir=user_pulsar)
        self.assertEqual(rc, 0, rep.rows)
        with open(prof, encoding="utf-8") as f:
            self.assertIn("<string>DeepEngineers.SE.dll</string>", f.read())
        self.assertFalse(os.path.exists(os.path.join(self.m.localappdata, "DeepEngineers", "Pulsar")))
        rc, rep, c = self.uninstall()
        self.assertEqual(rc, 0, rep.rows)
        with open(prof, encoding="utf-8") as f:
            self.assertEqual(f.read(), original)
        self.assertTrue(os.path.isfile(os.path.join(user_pulsar, "Legacy.exe")))       # not ours: kept
        self.assertFalse(os.path.exists(os.path.join(user_pulsar, "Legacy", "Local", "DeepEngineers.SE")))

    def test_pinned_bepinex_already_installed_is_kept(self):
        with zipfile.ZipFile(os.path.join(self.m.server, self.m.specs["bepinex"]["file"])) as z:
            z.extractall(self.m.sn)
        before = F.snapshot(self.m.sn)
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertTrue(any("already installed" in t for _s, t in self.statuses(rep, "bepinex")))
        rc, rep, c = self.uninstall()
        self.assertEqual(F.snapshot(self.m.sn), before)                   # the user's BepInEx stays

    def test_other_bepinex_is_kept_with_a_warning(self):
        F.write(os.path.join(self.m.sn, "winhttp.dll"), b"another doorstop")
        F.write(os.path.join(self.m.sn, "BepInEx", "core", "BepInEx.dll"), b"another bepinex")
        rc, rep, c = self.install()
        self.assertEqual({s for s, _t in self.statuses(rep, "bepinex")}, {"WARN"})
        with open(os.path.join(self.m.sn, "winhttp.dll"), "rb") as f:
            self.assertEqual(f.read(), b"another doorstop")

    def test_pre_existing_identical_file_then_an_update(self):
        # the development PC: the world mod was deployed by hand before the first install (identical: "present")
        mod = os.path.join(self.m.appdata, "SpaceEngineers", "Mods", "DeepEngineers.World")
        src = os.path.join(self.rel, "worldmod", "DeepEngineers.World")
        shutil.copytree(src, mod)
        before = F.snapshot(self.m.appdata)
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        # a later release changes one file of it: the pre-install copy is kept for the uninstall
        F.write(os.path.join(src, "Data", "WaterConfig.xml"), "<WaterConfig version='2' />\n")
        man = json.load(open(os.path.join(self.rel, "release.json"), encoding="utf-8"))
        for e in man["files"]:
            if e["path"].endswith("Data/WaterConfig.xml"):
                e["sha256"] = F.sha_file(os.path.join(src, "Data", "WaterConfig.xml"))
        F.write(os.path.join(self.rel, "release.json"), json.dumps(man))
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        with open(os.path.join(mod, "Data", "WaterConfig.xml"), encoding="utf-8") as f:
            self.assertIn("version='2'", f.read())
        rc, rep, c = self.uninstall(remove_worlds=True)
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual(F.snapshot(self.m.appdata), before)

class Stops(InstallBase):
    def test_se_build_mismatch_stops_before_writing(self):
        F.write(os.path.join(self.m.steam, "steamapps", "appmanifest_244850.acf"),
                F.acf(P.SE_APP_ID, "SpaceEngineers", "99999999"))
        rc, rep, c = self.install()
        self.assertEqual(rc, 2)
        self.assertTrue(any("99999999" in t and P.SE_BUILD_ID in t for _s, t in self.statuses(rep, "se")))
        self.assertEqual(F.snapshot(self.m.sn), self.before_sn)
        self.assertFalse(os.path.exists(c.manifest_path))

    def test_subnautica_changeset_mismatch_stops(self):
        F.write(os.path.join(self.m.sn, *P.SN_CHANGESET_FILE), "90000\n")
        rc, rep, c = self.install()
        self.assertEqual(rc, 2)
        self.assertTrue(rep.failed("sn"))

    def test_terrain_listing_mismatch_stops(self):
        F.write(os.path.join(self.m.sn, *P.SN_OCTREE_DIR, "compiled-batch-3-8-3.optoctrees"), b"x")
        rc, rep, c = self.install()
        self.assertEqual(rc, 2)
        self.assertTrue(rep.failed("terrain"))

    def test_changed_release_folder_stops(self):
        with open(os.path.join(self.rel, "plugins", "plugin.dll"), "ab") as f:
            f.write(b"tampered")
        rc, rep, c = self.install()
        self.assertEqual(rc, 2)
        self.assertTrue(rep.failed("release"))

    def test_auto_update_warning(self):
        F.write(os.path.join(self.m.steam, "steamapps", "appmanifest_244850.acf"),
                F.acf(P.SE_APP_ID, "SpaceEngineers", P.SE_BUILD_ID, auto_update=0))
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertTrue(any(s == "WARN" and "only update this game when I launch it" in t
                            for s, t in self.statuses(rep, "updates")))

class ExperimentalOption(InstallBase):
    CFG = ('<?xml version="1.0"?>\n<MyConfig><dictionary>\n  <item>\n    <Key>ExperimentalMode</Key>\n    <Value>\n'
           '      <Value xsi:type="xsd:string">%s</Value>\n    </Value>\n  </item>\n  <item>\n    <Key>WindowMode</Key>\n'
           '    <Value>\n      <Value xsi:type="xsd:string">1</Value>\n    </Value>\n  </item>\n</dictionary></MyConfig>\n')
    CFG_FULLSCREEN = CFG.replace('<Key>WindowMode</Key>\n    <Value>\n      <Value xsi:type="xsd:string">1',
                                 '<Key>WindowMode</Key>\n    <Value>\n      <Value xsi:type="xsd:string">2')

    def test_exclusive_fullscreen_fails_with_the_instruction(self):
        # second-PC install: exclusive Fullscreen is never composited (no see-through) and the A' fallback
 # minimises SE; the dev PC runs "Fullscreen window" (1)
        se = os.path.join(self.m.appdata, "SpaceEngineers")
        self.assertEqual(I.se_window_mode(se), "1")
        cfg = F.write(os.path.join(se, "SpaceEngineers.cfg"), self.CFG_FULLSCREEN % "True")
        self.assertEqual(I.se_window_mode(se), "2")
        before = F.sha_file(cfg)
        rc, rep, c = self.install()
        self.assertEqual(rc, 1, rep.rows)
        self.assertIn("FAIL", [s for s, _t in self.statuses(rep, "options")])
        self.assertTrue(any("Fullscreen window" in t for s, t in self.statuses(rep, "options") if s == "FAIL"))
        self.assertEqual(F.sha_file(cfg), before, "the installer never changes game options")

    def test_reads_the_option(self):
        se = os.path.join(self.m.appdata, "SpaceEngineers")
        os.remove(os.path.join(se, "SpaceEngineers.cfg"))   # the fixture writes one (option on) since
        self.assertIsNone(I.se_experimental_mode(se))
        F.write(os.path.join(se, "SpaceEngineers.cfg"), self.CFG % "False")
        self.assertIs(I.se_experimental_mode(se), False)
        F.write(os.path.join(se, "SpaceEngineers.cfg"), self.CFG % "True")
        self.assertIs(I.se_experimental_mode(se), True)

    def test_off_fails_with_the_instruction_and_never_changed(self):
        # S8S9 review R3: a stranger's SE has the option off by default and cannot load the world: FAIL, not WARN
        cfg = F.write(os.path.join(self.m.appdata, "SpaceEngineers", "SpaceEngineers.cfg"), self.CFG % "False")
        before = F.sha_file(cfg)
        rc, rep, c = self.install()
        self.assertEqual(rc, 1, rep.rows)
        self.assertEqual([s for s, _t in self.statuses(rep, "options")], ["FAIL", "PASS"])   # experimental FAIL, window mode PASS
        self.assertTrue(any("Experimental mode" in t for _s, t in self.statuses(rep, "options")))
        self.assertEqual(F.sha_file(cfg), before, "the installer never changes game options")

    def test_memory_is_information_only(self):
        # no memory figure stops an install any more
        os.environ["DEEPENG_INSTALL_FAKE_MEMORY_GB"] = "12,6"
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        self.assertEqual([s for s, _t in self.statuses(rep, "memory")], ["INFO"])
        self.assertIn("12 GB of RAM, 6.0 GB free", [t for _s, t in self.statuses(rep, "memory")][0])

class NoHostSave(InstallBase):
    machine_kw = {"host": False}

    def test_shipped_save_goes_into_a_free_slot(self):
        #: a blank PC had to make the Freedom save by hand; now the release ships one
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        texts = [t for _s, t in self.statuses(rep, "hostsave")]
        self.assertTrue(any("installed as slot0000" in t for t in texts), texts)
        self.assertTrue(any("renderer host save slot0000" in t for t in texts), texts)
        slot = os.path.join(self.m.sn, "SNAppData", "SavedGames", "slot0000")
        for n in ("gameinfo.json", "global-objects.bin", "scene-objects.bin", "screenshot.jpg"):
            self.assertEqual(F.sha_file(os.path.join(slot, n)), F.sha_file(os.path.join(F.HOSTSAVE, n)))
        with open(c.paths_path, encoding="utf-8") as f:
            self.assertEqual(json.load(f)["snHostSlot"], "slot0000")
        # a second run keeps it; the uninstall removes it
        rc2, rep2, _c2 = self.install()
        self.assertEqual(rc2, 0, rep2.rows)
        self.assertTrue(any("renderer host save slot0000" in t for _s, t in self.statuses(rep2, "hostsave")))
        self.uninstall()
        self.assertFalse(os.path.exists(os.path.join(slot, "gameinfo.json")))

class HostSaveWithoutScreenshot(InstallBase):
    def test_screenshot_added_to_the_chosen_slot(self):
        #: Subnautica's menu skips a slot without screenshot.jpg (the 1.0.0 shipped save): the install adds one
        rc, rep, c = self.install()
        self.assertEqual(rc, 0, rep.rows)
        with open(c.paths_path, encoding="utf-8") as f:
            name = json.load(f)["snHostSlot"]
        shot = os.path.join(self.m.sn, "SNAppData", "SavedGames", name, "screenshot.jpg")
        self.assertEqual(F.sha_file(shot), F.sha_file(os.path.join(F.HOSTSAVE, "screenshot.jpg")))
        self.assertTrue(any("had no screenshot.jpg" in t for _s, t in self.statuses(rep, "hostsave")), rep.rows)
        # a slot that has one keeps its own
        F.write(shot, b"\xff\xd8 the game's own")
        before = F.sha_file(shot)
        rc2, rep2, _c2 = self.install()
        self.assertEqual(rc2, 0, rep2.rows)
        self.assertEqual(F.sha_file(shot), before)

class MissingThings(InstallBase):
    machine_kw = {"workshop": False, "host": False}

    def test_workshop_and_host_save_fail_with_instructions(self):
        rc, rep, c = self.install()
        self.assertEqual(rc, 1)
        ws = " ".join(t for _s, t in self.statuses(rep, "workshop"))
        self.assertIn(P.workshop_url(P.WATER_MOD_ID), ws)
        self.assertIn(P.workshop_url(P.TEXT_HUD_API_ID), ws)
        self.assertIn("Freedom", " ".join(t for _s, t in self.statuses(rep, "hostsave")))
        # the rest is installed anyway (a second run after subscribing completes it)
        self.assertTrue(os.path.isfile(os.path.join(self.m.sn, "BepInEx", "plugins", "DeepEngineers",
                                                    "DeepEngineers.SN.dll")))

class WrongWaterMod(InstallBase):
    machine_kw = {"water_version": "3.24"}

    def test_water_mod_version_fails(self):
        rc, rep, c = self.install()
        self.assertEqual(rc, 1)
        self.assertTrue(any("3.24" in t and P.WATER_MOD_VERSION in t for _s, t in self.statuses(rep, "workshop")))

class Downloads(InstallBase):
    def test_hash_mismatch_installs_nothing(self):
        specs = {k: dict(v) for k, v in self.m.specs.items()}
        specs["bepinex"]["sha256"] = "0" * 64
        rc, rep, c = self.install(specs=specs)
        self.assertEqual(rc, 1)
        self.assertTrue(any("not the pinned" in t for _s, t in self.statuses(rep, "bepinex")))
        self.assertFalse(os.path.exists(os.path.join(self.m.sn, "winhttp.dll")))
        self.assertFalse(os.path.exists(os.path.join(c.downloads, specs["bepinex"]["file"])))

    def test_unreachable_url_prints_the_manual_route(self):
        specs = {k: dict(v) for k, v in self.m.specs.items()}
        specs["pulsar"]["url"] = (specs["pulsar"]["url"].rsplit("/", 1)[0] + "/missing.zip")
        rc, rep, c = self.install(specs=specs)
        self.assertEqual(rc, 1)
        text = " ".join(t for _s, t in self.statuses(rep, "pulsar"))
        self.assertIn("by hand", text)
        self.assertIn(specs["pulsar"]["sha256"], text)

    def test_offline_dir(self):
        specs = {k: dict(v) for k, v in self.m.specs.items()}
        for s in specs.values():
            s["url"] = "https://invalid.invalid/never-used.zip"
        rc, rep, c = self.install(specs=specs, offline_dir=self.m.server)
        self.assertEqual(rc, 0, rep.rows)

    def test_zip_slip_refused(self):
        bad = os.path.join(self.tmp.name, "bad.zip")
        with zipfile.ZipFile(bad, "w") as z:
            z.writestr("../evil.dll", b"x")
        with zipfile.ZipFile(bad) as z:
            with self.assertRaises(I.Abort):
                I.safe_members(z)

if __name__ == "__main__":
    unittest.main()
