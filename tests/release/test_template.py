"""The committed template world (tools/release/world-template) and its helpers."""
import math
import os
import tempfile
import unittest

import de_install as I
import fixtures as F
import make_template_world as T

class TemplateFiles(unittest.TestCase):
    def test_committed_template_passes_its_check(self):
        self.assertEqual(T.check(T.DEFAULT_OUT), [])

    def test_files_present(self):
        self.assertEqual(sorted(os.listdir(F.TEMPLATE)), sorted(["Sandbox.sbc", "Sandbox_config.sbc",
                                                                 "SANDBOX_0_0_0_.sbs", T.VX2]))

    def test_check_catches_a_planted_grid_and_owner_name(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = os.path.join(tmp, "w")
            import shutil
            shutil.copytree(F.TEMPLATE, out)
            p = os.path.join(out, "SANDBOX_0_0_0_.sbs")
            with open(p, encoding="utf-8") as f:
                t = f.read()
            t = t.replace("</SectorObjects>", '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid" />'
                                              "</SectorObjects>").replace("Engineer", "bslinger")
            with open(p, "w", encoding="utf-8") as f:
                f.write(t)
            probs = T.check(out)
            self.assertTrue(any("grids" in x for x in probs), probs)
            self.assertTrue(any("bslinger" in x for x in probs), probs)

class Helpers(unittest.TestCase):
    def test_orientation_faces_site_north_with_up_away_from_the_planet(self):
        q = T.quat_from_forward_up(T.CHAR_FORWARD, T.CHAR_UP)
        self.assertAlmostEqual(math.sqrt(sum(c * c for c in q)), 1.0, places=12)
        f = T.rotate(q, (0.0, 0.0, -1.0))
        u = T.rotate(q, (0.0, 1.0, 0.0))
        for a, b in zip(f, T.CHAR_FORWARD):
            self.assertAlmostEqual(a, b, places=12)
        for a, b in zip(u, T.CHAR_UP):
            self.assertAlmostEqual(a, b, places=12)

    def test_identity_orientation(self):
        q = T.quat_from_forward_up((0.0, 0.0, -1.0), (0.0, 1.0, 0.0))
        self.assertEqual(tuple(round(c, 12) for c in q), (0.0, 0.0, 0.0, 1.0))

    def test_character_above_the_anchor(self):
        c = T.CENTRE
        d = math.sqrt(sum((a - b) ** 2 for a, b in zip(T.CHAR_POS, c)))
        self.assertAlmostEqual(d - 60000.00390625, 49.99609375, places=6)    # 50 m above the sea surface Rs

    def test_world_patch(self):
        sbc = "<A><SessionName>DeepEngineers</SessionName><WorldId>x</WorldId></A>"
        out = I.patch_world_text("Sandbox.sbc", sbc, 'Tom & "Jerry"', "id-1")
        self.assertIn("<SessionName>Tom &amp; &quot;Jerry&quot;</SessionName>", out)
        self.assertIn("<WorldId>id-1</WorldId>", out)
        self.assertEqual(I.patch_world_text("Sandbox_config.sbc", "<WorldId>x</WorldId>", "n", "id"),
                         "<WorldId>x</WorldId>")

    def test_world_names(self):
        self.assertEqual(I.valid_world_name("  Planet 4546B. "), "Planet 4546B")
        for bad in ("", "a/b", "a:b", "CON", "x" * 65, "a?"):
            self.assertIsNone(I.valid_world_name(bad), bad)

if __name__ == "__main__":
    unittest.main()
