"""de_steam.py on fake Steam folders: KeyValues, libraries, app manifests, Workshop, Subnautica build/terrain/host
saves, the Steam user."""
import os
import tempfile
import unittest

import de_pins as P
import de_steam as D
import fixtures as F

class VdfTests(unittest.TestCase):
    def test_nested_comments_escapes(self):
        doc = D.parse_vdf('// c\n"a"\n{\n  "b"  "x\\\\y"\n  "c" { "d" "1" } // tail\n  "e" "say \\"hi\\""\n}\n')
        self.assertEqual(doc["a"]["b"], "x\\y")
        self.assertEqual(doc["a"]["c"]["d"], "1")
        self.assertEqual(doc["a"]["e"], 'say "hi"')

    def test_bom_and_case(self):
        doc = D.parse_vdf('\ufeff"AppState" { "BuildID" "42" }')
        self.assertEqual(D.ci_get(doc, "appstate")["BuildID"], "42")
        self.assertEqual(D.ci_get(D.ci_get(doc, "AppState"), "buildid"), "42")

    def test_unquoted_tokens(self):
        doc = D.parse_vdf('root { key value other "q" }')
        self.assertEqual(doc["root"], {"key": "value", "other": "q"})

class LibraryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.m = F.FakeMachine(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def test_libraries_new_format(self):
        libs = D.library_folders(self.m.steam)
        self.assertEqual([os.path.normcase(x) for x in libs],
                         [os.path.normcase(self.m.steam), os.path.normcase(self.m.lib2)])

    def test_libraries_old_format(self):
        F.write(os.path.join(self.m.steam, "steamapps", "libraryfolders.vdf"),
                '"LibraryFolders"\n{\n\t"TimeNextStatsReport"\t\t"1"\n\t"1"\t\t"%s"\n}\n' % self.m.lib2.replace("\\", "\\\\"))
        self.assertIn(os.path.normcase(self.m.lib2), [os.path.normcase(x) for x in D.library_folders(self.m.steam)])

    def test_missing_library_ignored(self):
        F.write(os.path.join(self.m.steam, "steamapps", "libraryfolders.vdf"),
                '"libraryfolders" { "1" { "path" "Z:\\\\nowhere" } }')
        self.assertEqual(D.library_folders(self.m.steam), [os.path.normpath(self.m.steam)])

    def test_steam_root_override(self):
        self.assertEqual(os.path.normcase(D.steam_root({"DEEPENG_STEAM_ROOT": self.m.steam})),
                         os.path.normcase(self.m.steam))
        self.assertIsNone(D.steam_root({"DEEPENG_STEAM_ROOT": os.path.join(self.tmp.name, "nope")}))

    def test_find_apps_across_libraries(self):
        libs = D.library_folders(self.m.steam)
        se, sn = D.find_app(libs, P.SE_APP_ID), D.find_app(libs, P.SN_APP_ID)
        self.assertEqual(os.path.normcase(se.path), os.path.normcase(self.m.se))
        self.assertEqual(os.path.normcase(sn.path), os.path.normcase(self.m.sn))
        self.assertEqual(se.build_id, P.SE_BUILD_ID)
        self.assertEqual(sn.build_id, P.SN_BUILD_ID)
        self.assertEqual((se.auto_update, se.state_flags), (1, 4))
        self.assertIsNone(D.find_app(libs, "12345"))

    def test_workshop_and_water_version(self):
        lib = D.find_app(D.library_folders(self.m.steam), P.SE_APP_ID).library
        wd = D.workshop_item_dir(lib, P.SE_APP_ID, P.WATER_MOD_ID)
        self.assertTrue(D.workshop_item_present(wd))
        self.assertTrue(D.workshop_item_present(D.workshop_item_dir(lib, P.SE_APP_ID, P.TEXT_HUD_API_ID)))
        self.assertFalse(D.workshop_item_present(D.workshop_item_dir(lib, P.SE_APP_ID, "1")))
        self.assertEqual(D.water_mod_version(wd, P.WATER_MOD_VERSION_FILE)[0], P.WATER_MOD_VERSION)

    def test_changeset_and_terrain(self):
        self.assertEqual(D.sn_changeset(self.m.sn), P.SN_CHANGESET)
        r = D.terrain_source(self.m.sn, P.SN_OCTREE_DIR, P.SN_TERRAIN_INDEX_LINES, P.SN_BUILD_ID)
        self.assertEqual((r["files"], r["bytes"], r["rows"], r["index"], r["buildId"]), (3, 35, (7, 19), True,
                                                                                        P.SN_BUILD_ID))
        t = self.m.terrain
        self.assertEqual(D.terrain_matches(r, t["files"], t["bytes"], t["rows"], t["hash"], P.SN_BUILD_ID)[0], True)
        ok, field, _f, _e = D.terrain_matches(r, t["files"], t["bytes"] + 1, t["rows"], t["hash"], P.SN_BUILD_ID)
        self.assertEqual((ok, field), (False, "bytes"))
        ok, field, _f, _e = D.terrain_matches(r, t["files"], t["bytes"], t["rows"], t["hash"] ^ 1, P.SN_BUILD_ID)
        self.assertEqual((ok, field), (False, "manifestHash"))
        ok, field, _f, _e = D.terrain_matches(r, t["files"], t["bytes"], t["rows"], t["hash"], "1")
        self.assertEqual((ok, field), (False, "build"))

    def test_fnv_matches_the_csharp_rule(self):
        # FNV-1a 64 of "" is the offset basis; of "a" the published value
        self.assertEqual(D.fnv1a64(b""), 0xCBF29CE484222325)
        self.assertEqual(D.fnv1a64(b"a"), 0xAF63DC4C8601EC8C)

    def test_host_slots(self):
        slots = D.sn_host_slots(self.m.sn)
        self.assertEqual([s["slot"] for s in slots], ["slot0001", "slot0003"])
        slot, why = D.choose_host_slot(slots, P.SN_CHANGESET, 1)
        self.assertEqual(slot["slot"], "slot0003")          # the Freedom one, not the more-played Survival one
        self.assertIsNone(D.choose_host_slot(slots, P.SN_CHANGESET, 1, slot_wanted="slot0001")[0])
        self.assertIsNone(D.choose_host_slot([s for s in slots if s["gameMode"] == 0], P.SN_CHANGESET, 1)[0])
        self.assertEqual(D.choose_host_slot(slots, P.SN_CHANGESET, 1,
                                            session_wanted="11111111-2222-4333-8444-555555555555")[0]["slot"], "slot0003")

    def test_steam_user(self):
        ids = D.steam_user_ids(self.m.steam)
        self.assertEqual(ids, [F.FAKE_USER])
        saves = os.path.join(self.m.appdata, "SpaceEngineers", "Saves")
        self.assertEqual(D.choose_steam_user(saves, "76561197960265730", ids)[0], "76561197960265730")
        self.assertEqual(D.choose_steam_user(saves, None, ids)[0], F.FAKE_USER)
        self.assertEqual(D.choose_steam_user(saves, None, [])[0], F.FAKE_USER)      # the only Saves folder
        os.makedirs(os.path.join(saves, "76561197960265731"))
        self.assertIsNone(D.choose_steam_user(saves, None, [])[0])

if __name__ == "__main__":
    unittest.main()
