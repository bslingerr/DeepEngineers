"""S9 release lint tests: tools/release/check_release.py on local fixtures (S9.N1 / S9.M1).

 python -I tests/release/run_tests.py test_check_release.py (or python -I tests/release/test_check_release.py)

Fixtures are built in a temporary folder: a release folder, an allowlist, and a small fake decompiled tree written in
the ILSpy style. The planted machine paths, Steam ids and decompiler headers are assembled from pieces at run time so
that this file itself stays clean for the source-release scan. One test also plants a real snippet of the scratchpad
decomp when $DEEPENG_DECOMP names it (skipped otherwise).
"""
import hashlib
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
sys.path.insert(0, str(REPO / "tools" / "release"))

import check_release as CR  # noqa: E402

DRIVE_USERS = "C:" + "\\" + "Users" + "\\"
STEAM_BASE = 0x0110000100000000
OWNER_LIKE_ID = STEAM_BASE + 186000123          # an individual account id (not a real one)
HEADER = "// " + "Decompiled with " + "ICSharpCode" + ".Decompiler 8.2.0"

DECOMP_CLASS = """%s
// Type: GameNamespace.HullPressure
using System;
using UnityEngine;

namespace GameNamespace
{
\tpublic class HullPressure : MonoBehaviour
\t{
\t\tprivate float crushDepthMeters = 200f;

\t\tprivate float accumulatedDamage;

\t\tpublic void UpdateHullIntegrity(float currentDepth, float deltaTime)
\t\t{
\t\t\tif (currentDepth > crushDepthMeters)
\t\t\t{
\t\t\t\tfloat num = (currentDepth - crushDepthMeters) * 0.5f;
\t\t\t\taccumulatedDamage += num * deltaTime;
\t\t\t\tif (accumulatedDamage > 100f)
\t\t\t\t{
\t\t\t\t\tGetComponent<LiveMixin>().TakeDamage(accumulatedDamage, base.transform.position);
\t\t\t\t\taccumulatedDamage = 0f;
\t\t\t\t}
\t\t\t}
\t\t}
\t}
}
""" % HEADER

# the method body without the header, as someone would paste it (other indentation, braces kept)
PLANTED_SNIPPET = """
namespace DeepEngineers.SN
{
    internal sealed class Pressure
    {
        private float crushDepthMeters = 200f;
        private float accumulatedDamage;
        public void UpdateHullIntegrity(float currentDepth, float deltaTime)
        {
            if (currentDepth > crushDepthMeters)
            {
                float num = (currentDepth - crushDepthMeters) * 0.5f;
                accumulatedDamage += num * deltaTime;
                if (accumulatedDamage > 100f)
                {
                    GetComponent<LiveMixin>().TakeDamage(accumulatedDamage, transform.position);
                    accumulatedDamage = 0f;
                }
            }
        }
    }
}
"""

CLEAN_PY = '''"""A clean launcher stand-in."""
import os

def main():
    root = os.path.dirname(os.path.abspath(__file__))
    print("DeepEngineers launcher at", root)
    return 0
'''

def sha256(p: Path) -> str:
    return hashlib.sha256(p.read_bytes()).hexdigest()

class LintFixture(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="de_lint_"))
        self.decomp = self.tmp / "decomp"
        (self.decomp / "game" / "Assembly-CSharp").mkdir(parents=True)
        (self.decomp / "game" / "Assembly-CSharp" / "HullPressure.cs").write_text(DECOMP_CLASS, encoding="utf-8")
        self.repo = self.tmp / "repo"
        (self.repo / "build").mkdir(parents=True)
        (self.repo / "build" / "plugin.dll").write_bytes(b"MZ" + bytes(range(256)) * 8)
        self.rel = self.tmp / "release"
        (self.rel / "plugins").mkdir(parents=True)
        (self.rel / "launcher").mkdir()
        shutil.copy2(self.repo / "build" / "plugin.dll", self.rel / "plugins" / "plugin.dll")
        (self.rel / "README.md").write_text("# DeepEngineers\n\nInstall with Install.cmd.\n", encoding="utf-8")
        (self.rel / "NOTICE.md").write_text("Space Engineers by Keen Software House.\n", encoding="utf-8")
        (self.rel / "Install.cmd").write_text("@echo off\r\necho install\r\n", encoding="utf-8")
        (self.rel / "launcher" / "launcher.py").write_text(CLEAN_PY, encoding="utf-8")
        self.readme_sha = sha256(self.rel / "README.md")
        self.allow = self.tmp / "allowlist.txt"
        self.allow.write_text(
            "# test allowlist\n"
            "README.md required sha256=%s\n"
            "NOTICE.md required\n"
            "Install.cmd required\n"
            "launcher/*.py\n"
            "plugins/plugin.dll required build=build/plugin.dll\n"
            "src/**\n" % self.readme_sha, encoding="utf-8")
        self.lines = []

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def lint(self, *extra, mode="release", folder=None):
        argv = [mode, str(folder or self.rel), "--decomp", str(self.decomp), "--no-um"]
        if mode == "release":
            argv += ["--allowlist", str(self.allow), "--repo", str(self.repo)]
        self.lines = []
        return CR.run(argv + list(extra), out=self.lines.append)

    def text(self):
        return "\n".join(self.lines)

class ReleaseMode(LintFixture):
    def test_clean_release_passes(self):
        self.assertEqual(self.lint(), 0, self.text())
        self.assertIn("CHECK 1 allowlist: PASS", self.text())
        self.assertIn("CHECK 2 decompiled code: PASS", self.text())
        self.assertIn("CHECK 3 machine paths and decompiler markers: PASS", self.text())
        self.assertIn("CHECK 4 um publish check: SKIP", self.text())

    def test_M1_planted_mesh_cache_file_fails_the_allowlist(self):
        (self.rel / "plugins" / "cache").mkdir()
        (self.rel / "plugins" / "cache" / "0001.mesh").write_bytes(b"\x01\x02mesh")
        self.assertEqual(self.lint(), CR.EXIT_ALLOWLIST, self.text())
        self.assertIn("not in the allowlist: plugins/cache/0001.mesh", self.text())

    def test_M1_planted_headerless_ilspy_snippet_fails_check2(self):
        (self.rel / "src").mkdir()
        (self.rel / "src" / "Pressure.cs").write_text(PLANTED_SNIPPET, encoding="utf-8")
        code = self.lint()
        self.assertEqual(code, CR.EXIT_DECOMP, self.text())
        self.assertIn("src/Pressure.cs", self.text())
        self.assertIn("game/Assembly-CSharp/HullPressure.cs", self.text())

    def test_snippet_in_python_comments_only_does_not_fail(self):
        body = "\n".join("# " + line for line in PLANTED_SNIPPET.splitlines())
        (self.rel / "launcher" / "notes.py").write_text(body + "\nX = 1\n", encoding="utf-8")
        self.assertEqual(self.lint(), 0, self.text())

    def test_required_missing_and_hash_mismatch_fail(self):
        (self.rel / "NOTICE.md").unlink()
        (self.rel / "README.md").write_text("changed\n", encoding="utf-8")
        self.assertEqual(self.lint(), CR.EXIT_ALLOWLIST, self.text())
        self.assertIn("required and missing: NOTICE.md", self.text())
        self.assertIn("sha256 differs: README.md", self.text())

    def test_build_equality(self):
        (self.rel / "plugins" / "plugin.dll").write_bytes(b"MZ other build")
        self.assertEqual(self.lint(), CR.EXIT_ALLOWLIST, self.text())
        self.assertIn("differs from the build output", self.text())

    def test_planted_machine_path_fails_check3(self):
        (self.rel / "README.md").write_text("Logs are in " + DRIVE_USERS + "Someone\\AppData\\Local\\x\n", encoding="utf-8")
        self.allow.write_text(self.allow.read_text(encoding="utf-8").replace(" sha256=" + self.readme_sha, ""),
                              encoding="utf-8")
        self.assertEqual(self.lint(), CR.EXIT_PATHS, self.text())
        self.assertIn("user-profile path in README.md", self.text())

    def test_placeholder_user_paths_pass(self):
        (self.rel / "README.md").write_text("Logs: " + DRIVE_USERS + "<you>\\AppData and " + DRIVE_USERS +
                                            "%USERNAME%\\x and " + DRIVE_USERS + "Public\\x\n", encoding="utf-8")
        self.allow.write_text(self.allow.read_text(encoding="utf-8").replace(" sha256=" + self.readme_sha, ""),
                              encoding="utf-8")
        self.assertEqual(self.lint(), 0, self.text())

    def test_steam_ids_text_and_binary(self):
        world = self.rel / "world"
        world.mkdir()
        (world / "Sandbox.sbc").write_text("<SteamId>%d</SteamId>\n" % OWNER_LIKE_ID, encoding="utf-8")
        self.allow.write_text(self.allow.read_text(encoding="utf-8") + "world/*\n", encoding="utf-8")
        self.assertEqual(self.lint(), CR.EXIT_PATHS, self.text())
        self.assertIn("SteamID64 in world/Sandbox.sbc", self.text())
        (world / "Sandbox.sbc").unlink()
        (world / "Sandbox.sbcB5").write_bytes(b"\x00\x01" + OWNER_LIKE_ID.to_bytes(8, "little") + b"\x00")
        self.assertEqual(self.lint(), CR.EXIT_PATHS, self.text())
        self.assertIn("SteamID64 (binary u64)", self.text())
        v, enc = OWNER_LIKE_ID, bytearray()
        while True:
            b = v & 0x7F
            v >>= 7
            enc.append(b | (0x80 if v else 0))
            if not v:
                break
        (world / "Sandbox.sbcB5").write_bytes(b"\x00\x08" + bytes(enc) + b"\x00")
        self.assertEqual(self.lint(), CR.EXIT_PATHS, self.text())
        self.assertIn("SteamID64 (binary varint)", self.text())
        (world / "Sandbox.sbcB5").write_bytes(b"\x00" + STEAM_BASE.to_bytes(8, "little") + b"\x00")
        (world / "base.txt").write_text("STEAMID64_BASE = %d\n" % STEAM_BASE, encoding="utf-8")
        self.assertEqual(self.lint(), 0, "the conversion base is no person's id\n" + self.text())

    def test_decompiler_header_and_dev_games_folder(self):
        (self.rel / "launcher" / "x.py").write_text(HEADER + "\nX = 1\n", encoding="utf-8")
        self.assertEqual(self.lint(), CR.EXIT_PATHS, self.text())
        self.assertIn("decompiler", self.text())
        (self.rel / "launcher" / "x.py").write_text("P = r'" + "E:" + "\\" + "Games\\Subnautica'\n", encoding="utf-8")
        self.assertEqual(self.lint(), CR.EXIT_PATHS, self.text())
        self.assertIn("development PC games folder", self.text())

    def test_utf16_path_in_binary(self):
        (self.rel / "plugins" / "plugin.dll").write_bytes(
            b"MZ\x00\x00" + (DRIVE_USERS + "Someone\\src\\x.pdb").encode("utf-16-le") + b"\x00\x00")
        shutil.copy2(self.rel / "plugins" / "plugin.dll", self.repo / "build" / "plugin.dll")
        self.assertEqual(self.lint(), CR.EXIT_PATHS, self.text())
        self.assertIn("user-profile path in plugins/plugin.dll (utf-16le", self.text())

    def test_exit_bits_add_up(self):
        (self.rel / "stray.bin").write_bytes(b"x")
        (self.rel / "src").mkdir()
        (self.rel / "src" / "Pressure.cs").write_text(PLANTED_SNIPPET + "\n// " + DRIVE_USERS + "Someone\\x\n",
                                                      encoding="utf-8")
        self.assertEqual(self.lint(), CR.EXIT_ALLOWLIST | CR.EXIT_DECOMP | CR.EXIT_PATHS, self.text())

    def test_usage_errors(self):
        self.assertEqual(CR.run(["release", str(self.tmp / "nope"), "--no-um", "--no-decomp"], out=lambda s: None),
                         CR.EXIT_USAGE)
        self.assertEqual(CR.run(["release", str(self.rel), "--no-um", "--decomp", str(self.tmp / "nope"),
                                 "--allowlist", str(self.allow)], out=lambda s: None), CR.EXIT_USAGE)
        self.assertEqual(CR.run(["release", str(self.rel), "--no-um", "--no-decomp",
                                 "--allowlist", str(self.tmp / "none.txt")], out=lambda s: None), CR.EXIT_USAGE)

    def test_um_publish_check_pass_fail_and_absent(self):
        fake = self.tmp / "fake_um.py"
        fake.write_text("import sys\nprint('PASS: fake')\nsys.exit(int(open(%r).read()))\n" % str(self.tmp / "rc.txt"),
                        encoding="utf-8")
        (self.tmp / "rc.txt").write_text("0", encoding="utf-8")
        um = '"%s" -I "%s"' % (sys.executable, fake)
        base = ["release", str(self.rel), "--decomp", str(self.decomp), "--allowlist", str(self.allow),
                "--repo", str(self.repo), "--um", um, "--se-dir", str(self.tmp / "nogame"), "--sn-dir", str(self.tmp / "nogame")]
        lines = []
        self.assertEqual(CR.run(base, out=lines.append), 0, "\n".join(lines))
        self.assertIn("CHECK 4 um publish check: PASS", "\n".join(lines))
        (self.tmp / "rc.txt").write_text("1", encoding="utf-8")
        lines = []
        self.assertEqual(CR.run(base, out=lines.append), CR.EXIT_UM, "\n".join(lines))
        self.assertIsNone(CR.check_um(self.rel, None, [], lambda s: None), "absent CLI: SKIP, not a failure")

class SourcesMode(LintFixture):
    def test_sources_mode_finds_the_snippet_and_skips_build_dirs(self):
        src = self.tmp / "tree"
        (src / "src" / "Plugin").mkdir(parents=True)
        (src / "src" / "Plugin" / "Pressure.cs").write_text(PLANTED_SNIPPET, encoding="utf-8")
        (src / "src" / "Plugin" / "obj").mkdir()
        (src / "src" / "Plugin" / "obj" / "Gen.cs").write_text(PLANTED_SNIPPET, encoding="utf-8")
        code = self.lint(mode="sources", folder=src)
        self.assertEqual(code, CR.EXIT_DECOMP, self.text())
        self.assertIn("src/Plugin/Pressure.cs", self.text())
        self.assertNotIn("obj/Gen.cs", self.text())

    def test_baseline_acknowledges_a_reviewed_run(self):
        src = self.tmp / "tree"
        src.mkdir()
        (src / "Pressure.cs").write_text(PLANTED_SNIPPET, encoding="utf-8")
        cand = self.tmp / "baseline.txt"
        self.assertEqual(self.lint("--write-baseline", str(cand), mode="sources", folder=src), CR.EXIT_DECOMP)
        self.assertTrue(cand.is_file())
        self.assertEqual(self.lint("--baseline", str(cand), mode="sources", folder=src), 0, self.text())
        self.assertIn("acknowledged in the baseline", self.text())

    def test_using_directives_and_namespaces_are_not_hits(self):
        src = self.tmp / "tree"
        src.mkdir()
        (src / "Mine.cs").write_text("using System;\nusing UnityEngine;\n\nnamespace GameNamespace\n{\n"
                                     "    public class Mine { }\n}\n", encoding="utf-8")
        self.assertEqual(self.lint(mode="sources", folder=src), 0, self.text())

class Tokenizer(unittest.TestCase):
    def test_normalisation(self):
        a, _ = CR.tokenize("x = this.y + 1.0f; // note\n/* block */ z = 0x10;", ".cs")
        b, _ = CR.tokenize("x = y + 1f;\nz = 16;", ".cs")
        self.assertEqual(a, b)
        t, _ = CR.tokenize("s = \"a // not a comment\"; c = '/';", ".cs")
        self.assertIn('"a // not a comment"', t)
        p, _ = CR.tokenize("x = 1  # comment\ny = '''doc # not'''\n", ".py")
        self.assertNotIn("comment", " ".join(p))
        self.assertIn("'''doc # not'''", p)

    def test_glob(self):
        e = CR.AllowEntry("launcher/*.py", False, None, None, 1)
        self.assertTrue(e.regex.match("launcher/launcher.py"))
        self.assertFalse(e.regex.match("launcher/sub/x.py"))
        e = CR.AllowEntry("worldmod/**", False, None, None, 1)
        self.assertTrue(e.regex.match("worldmod/DeepEngineers.World/Data/x.sbc"))

@unittest.skipUnless(os.environ.get("DEEPENG_DECOMP") and Path(os.environ["DEEPENG_DECOMP"].split(os.pathsep)[0]).is_dir(),
                     "DEEPENG_DECOMP not set: the real-decomp plant runs on the development PC only")
class RealDecomp(unittest.TestCase):
    def test_M1_real_ilspy_snippet_is_caught(self):
        decomp = Path(os.environ["DEEPENG_DECOMP"].split(os.pathsep)[0])
        src = next(p for p in sorted(decomp.rglob("ErrorMessage.cs")))
        lines = src.read_text(encoding="utf-8", errors="replace").splitlines()
        snippet = "\n".join(lines[95:125])                       # a method block, no header
        tmp = Path(tempfile.mkdtemp(prefix="de_lint_real_"))
        try:
            (tmp / "Planted.cs").write_text(snippet, encoding="utf-8")
            out = []
            code = CR.run(["sources", str(tmp), "--decomp", str(decomp)], out=out.append)
            self.assertEqual(code & CR.EXIT_DECOMP, CR.EXIT_DECOMP, "\n".join(out))
            self.assertIn("ErrorMessage.cs", "\n".join(out))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

if __name__ == "__main__":
    unittest.main()
