"""launcher_paths.py: dev mode keeps every S0-S7 value, release mode resolves from paths.json or Steam and hands the
plugins their environment inputs."""
import json
import os
import tempfile
import unittest

import fixtures as F
import launcher_paths as LP

LAUNCHER_DIR = os.path.join(F.REPO, "src", "DeepEngineers.Launcher")

class DevMode(unittest.TestCase):
    def test_dev_defaults_unchanged(self):
        env = {"LOCALAPPDATA": r"C:\L"}
        p = LP.resolve(LAUNCHER_DIR, env)
        self.assertEqual(p["mode"], "dev")
        self.assertEqual(p["seBin64"], r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64")
        self.assertEqual(p["subnautica"], r"C:\Program Files (x86)\Steam\steamapps\common\Subnautica")
        self.assertEqual(p["pulsar"], os.path.join(F.REPO, "third_party", "pulsar", "extracted"))
        self.assertEqual(p["backups"], os.path.join(os.path.expanduser("~"), ".universal-modder", "backups"))
        self.assertEqual(p["manifest"], os.path.join(F.REPO, "docs", "install", "deepengineers-files.txt"))
        self.assertIsNone(p["plugins"])
        self.assertEqual(LP.apply_release_env(p, {}), {})

    def test_dev_env_overrides(self):
        env = {"DEEPENG_SE_BIN64": "X:\\se", "DEEPENG_SUBNAUTICA": "X:\\sn", "DEEPENG_PULSAR": "X:\\p"}
        p = LP.resolve(LAUNCHER_DIR, env)
        self.assertEqual((p["seBin64"], p["subnautica"], p["pulsar"]), ("X:\\se", "X:\\sn", "X:\\p"))

    def test_launcher_module_in_dev_mode(self):
        import launcher
        self.assertFalse(launcher.RELEASE)
        self.assertIsNone(launcher.RELEASE_PLUGINS)
        self.assertEqual(launcher.BACKUP_HINT % "x", "um backup create x")

class ReleaseMode(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.m = F.FakeMachine(self.tmp.name)
        self.rel = os.path.join(self.tmp.name, "rel")
        F.write(os.path.join(self.rel, "release.json"), "{}")
        self.here = os.path.join(self.rel, "launcher")
        os.makedirs(self.here)

    def tearDown(self):
        self.tmp.cleanup()

    def test_from_steam_without_paths_json(self):
        p = LP.resolve(self.here, dict(self.m.env))
        self.assertEqual(p["mode"], "release")
        self.assertEqual(os.path.normcase(p["seBin64"]), os.path.normcase(os.path.join(self.m.se, "Bin64")))
        self.assertEqual(os.path.normcase(p["subnautica"]), os.path.normcase(self.m.sn))
        self.assertEqual(p["pulsar"], os.path.join(self.m.localappdata, "DeepEngineers", "Pulsar"))
        self.assertEqual(p["backups"], os.path.join(self.m.localappdata, "DeepEngineers", "backups"))
        self.assertEqual(p["plugins"], os.path.join(self.rel, "plugins"))
        for v in p.values():                           # nothing of the development PC
            if isinstance(v, str):
                self.assertNotIn(os.path.normcase(F.REPO), os.path.normcase(v))
                self.assertNotIn(os.path.normcase(LP.DEV_SUBNAUTICA), os.path.normcase(v))
                self.assertNotIn(".universal-modder", v)

    def test_from_paths_json_and_env(self):
        pj = {"seBin64": os.path.join(self.m.se, "Bin64"), "subnautica": self.m.sn, "pulsar": "P:\\pulsar",
              "snHostSession": "s-1", "snHostSlot": "slot0003"}
        F.write(os.path.join(self.m.localappdata, "DeepEngineers", "install", "paths.json"), json.dumps(pj))
        env = dict(self.m.env)
        env.pop("DEEPENG_STEAM_ROOT")
        p = LP.resolve(self.here, env)
        self.assertEqual((p["source"], p["pulsar"], p["snHostSlot"]), ("paths.json", "P:\\pulsar", "slot0003"))
        target = {"DEEPENGINEERS_SN_HOST_SLOT": "mine"}
        done = LP.apply_release_env(p, target)
        self.assertEqual(target["DEEPENGINEERS_SN_HOST_SLOT"], "mine")          # the user's own value wins
        self.assertEqual(target["DEEPENGINEERS_SN_HOST_SESSION"], "s-1")
        self.assertEqual(os.path.normcase(target["DEEPENG_SUBNAUTICA"]), os.path.normcase(self.m.sn))
        self.assertNotIn("DEEPENGINEERS_SN_HOST_SLOT", done)

    def test_stale_paths_json_falls_back_to_steam(self):
        F.write(os.path.join(self.m.localappdata, "DeepEngineers", "install", "paths.json"),
                json.dumps({"seBin64": "Q:\\gone", "subnautica": "Q:\\gone2"}))
        p = LP.resolve(self.here, dict(self.m.env))
        self.assertEqual(os.path.normcase(p["subnautica"]), os.path.normcase(self.m.sn))
        self.assertEqual(p["source"], "paths.json + Steam")

if __name__ == "__main__":
    unittest.main()
