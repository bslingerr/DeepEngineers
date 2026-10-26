"""S9 tests: tools/release/strip_decomp_refs.py (citations out of comments, code untouched) and
tools/release/gen_uninstall.py (UNINSTALL.md from the installer's manifest format).

 python -I tests/release/run_tests.py test_strip_decomp_refs.py
"""
import json
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
import gen_uninstall as GU  # noqa: E402
import strip_decomp_refs as SD  # noqa: E402

CS = '''using System;

namespace DeepEngineers.SE
{
    /// <summary>
    /// Saving blocked: a prefix on <c>MySession.Save(out MySessionSnapshot, string)</c>
    shown from the first update
    ///
    every 60 s. Effects: SE's four, VERIFIED.
    /// Entities &lt;T&gt; stay; our own file src/DeepEngineers.SE/Plugin.cs stays.
    /// </summary>
    internal static class Sample
    {
        // reflection
        private const string Kept = "VERIFIED // not a comment";
        /* block note */
        public static int F(int x) { return x / 2; } // GameMode is obsolete
    }
}
'''

PY = '''"""Module doc stays readable."""
URL = "https://example.invalid/a#b " # VERIFIED

def f():
    """VERIFIED the helper."""
    return URL
'''

SBC = '''<?xml version="1.0"?>
<Definitions>
  <!-- the shader's channel
       Ratio 0.11 : 0.31 : 1 stays. -->
  <Value>1</Value>
</Definitions>
'''

class Strip(unittest.TestCase):
    def test_cs_comments_only(self):
        new, k = SD.strip_source(CS, ".cs")
        self.assertGreaterEqual(k, 5)
        self.assertEqual(SD.code_tokens(CS, ".cs"), SD.code_tokens(new, ".cs"), "code untouched")
        self.assertIn('"VERIFIED MySession.cs:4355 (decomp) // not a comment"', new, "strings untouched")
        for gone in ("MySession.cs:4355;", "IMyUtilities.cs:57", "MyEntity3DSoundEmitter.cs", "MyHandDrill.cs:76",
                     "(decomp Sandbox", "(VERIFIED decomp)"):
            self.assertNotIn(gone, new.replace('"VERIFIED MySession.cs:4355 (decomp) // not a comment"', ""), gone)
        self.assertIn("&lt;T&gt;", new, "entities kept")
        self.assertIn("src/DeepEngineers.SE/Plugin.cs", new, "our own paths kept")
        flat = new.replace("\n    /// ", " ")
        self.assertIn("<c>MySession.Save(out MySessionSnapshot, string)</c> shown from the first update", flat,
                      "a parenthetical that cites the decomp goes as a whole, the sentence around it stays")
        self.assertNotIn("(public", flat)
        self.assertEqual(0, len(CR._CITATION.findall(new.replace(
            '"VERIFIED MySession.cs:4355 (decomp) // not a comment"', ""))))

    def test_py_comments_and_docstrings(self):
        new, k = SD.strip_source(PY, ".py")
        self.assertGreaterEqual(k, 3)
        self.assertEqual(SD.code_tokens(PY, ".py"), SD.code_tokens(new, ".py"))
        self.assertIn('"https://example.invalid/a#b MySession.cs:12"', new)
        self.assertNotIn("MyCockpit.cs:561", new)
        self.assertNotIn("# VERIFIED MySession.cs:12", new)
        compile(new, "x.py", "exec")

    def test_xml_comments(self):
        new, k = SD.strip_xml(SBC)
        self.assertGreaterEqual(k, 1)
        self.assertNotIn("MyRender11.cs:2701", new)
        self.assertIn("Ratio 0.11 : 0.31 : 1 stays", new)
        self.assertEqual(SD.code_tokens(SBC, ".sbc"), SD.code_tokens(new, ".sbc"))

    def test_no_citation_no_change(self):
        text = "// plain comment (no reference)\nint x = 1; // Decomposition stays\n"
        self.assertEqual(SD.strip_source(text, ".cs"), (text, 0))

class Tree(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="de_strip_"))
        self.repo = self.tmp / "repo"
        for d in ("src/A", "src/A/bin", "src/A/obj", "docs/recon", "tests/out", "third_party/x", "tools"):
            (self.repo / d).mkdir(parents=True)
        (self.repo / "DeepEngineers.sln").write_text("sln\n", encoding="utf-8")
        (self.repo / "src" / "A" / "Sample.cs").write_text(CS, encoding="utf-8")
        (self.repo / "src" / "A" / "bin" / "x.dll").write_bytes(b"MZ")
        (self.repo / "src" / "A" / "obj" / "Gen.cs").write_text("// obj\n", encoding="utf-8")
        (self.repo / "docs" / "recon" / "SE.md").write_text("decomp notes MySession.cs:1\n", encoding="utf-8")
        (self.repo / "docs" / "ROADMAP.md").write_text("Read (VERIFIED decomp MySession.cs:9) here.\n", encoding="utf-8")
        (self.repo / "tests" / "out" / "o.txt").write_text("out\n", encoding="utf-8")
        (self.repo / "tools" / "t.py").write_text(PY, encoding="utf-8")
        self.out = self.tmp / "out"

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_tool(self, *extra):
        lines = []
        code = SD.run([str(self.repo), str(self.out)] + list(extra), out=lines.append)
        return code, "\n".join(lines)

    def test_copy_skips_and_strips(self):
        code, text = self.run_tool()
        self.assertEqual(code, 0, text)
        self.assertTrue((self.out / "src" / "A" / "Sample.cs").is_file())
        self.assertFalse((self.out / "src" / "A" / "bin").exists())
        self.assertFalse((self.out / "src" / "A" / "obj").exists())
        self.assertFalse((self.out / "docs" / "recon").exists(), "recon notes stay private by default")
        self.assertFalse((self.out / "tests" / "out").exists())
        self.assertFalse((self.out / "third_party").exists())
        self.assertNotIn("MyHandDrill.cs:76", (self.out / "src" / "A" / "Sample.cs").read_text(encoding="utf-8"))
        self.assertIn("MySession.cs:9", (self.out / "docs" / "ROADMAP.md").read_text(encoding="utf-8"),
                      "docs untouched without --docs")
        code, text = self.run_tool()
        self.assertEqual(code, 2, "a non-empty output folder is refused")

    def test_docs_and_recon_options(self):
        code, text = self.run_tool("--docs", "--keep-recon")
        self.assertEqual(code, 0, text)
        self.assertNotIn("MySession.cs:9", (self.out / "docs" / "ROADMAP.md").read_text(encoding="utf-8"))
        self.assertTrue((self.out / "docs" / "recon" / "SE.md").is_file())

    def test_code_change_is_refused_per_file(self):
        real = SD.strip_source
        try:
            SD.strip_source = lambda text, ext: (text.replace("x / 2", "x / 3"), 1)
            code, text = self.run_tool()
        finally:
            SD.strip_source = real
        self.assertEqual(code, 1, text)
        self.assertIn("KEPT UNCHANGED", text)
        self.assertIn("x / 2", (self.out / "src" / "A" / "Sample.cs").read_text(encoding="utf-8"), "original copied")

    def test_output_inside_repo_refused(self):
        lines = []
        self.assertEqual(SD.run([str(self.repo), str(self.repo / "copy")], out=lines.append), 2)

class Uninstall(unittest.TestCase):
    def test_every_installer_kind_is_described(self):
        kinds = GU.recorded_kinds(REPO / "src" / "DeepEngineers.Launcher" / "de_install.py")
        if not kinds:
            self.skipTest("de_install.py not present")
        self.assertFalse(kinds - set(GU.KINDS), "UNINSTALL.md must describe every manifest kind")

    def test_committed_page_is_current(self):
        lines = []
        self.assertEqual(GU.run(["--check"], out=lines.append), 0, "\n".join(lines))

    def test_generic_page_contents(self):
        text = GU.render(REPO)
        for must in ("Uninstall.cmd", "install-manifest.json", "-RemoveWorlds", "restore-settings", "DE_4546B",
                     "plugin.dll", "DeepEngineers.SN.dll", "Current.xml", "winhttp.dll", "## 5. Check"):
            self.assertIn(must, text, must)
        for k in GU.KINDS:
            self.assertIn("`%s`" % k, text)
        lines = []
        tmp = Path(tempfile.mkdtemp(prefix="de_un_"))
        try:
            (tmp / "U.md").write_text(text, encoding="utf-8")
            self.assertEqual(CR.run(["sources", str(tmp), "--no-decomp"], out=lines.append), 0,
                             "the page carries no machine path\n" + "\n".join(lines))
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_manifest_mode_tokenises_profile_paths(self):
        tmp = Path(tempfile.mkdtemp(prefix="de_un_"))
        try:
            local = tmp / "Local"
            env = {"LOCALAPPDATA": str(local), "APPDATA": str(tmp / "Roaming"), "USERPROFILE": str(tmp)}
            entries = [
                {"kind": "tree", "group": "pulsar", "path": str(local / "DeepEngineers" / "Pulsar"), "origin": "created"},
                {"kind": "file", "group": "plugins", "path": str(tmp / "Sub" / "BepInEx" / "plugins" / "DeepEngineers" /
                                                                "DeepEngineers.SN.dll"), "origin": "created", "sha256": "0" * 64},
                {"kind": "entry", "group": "profile", "path": str(local / "DeepEngineers" / "Pulsar" / "Legacy" / "Profiles" /
                                                                  "Current.xml"), "origin": "created", "text": "<string>x</string>"},
                {"kind": "world", "group": "world", "path": str(tmp / "Roaming" / "SpaceEngineers" / "Saves" / "1" / "DeepEngineers")},
            ]
            man = {"format": 1, "product": "DeepEngineers", "installed": "2026-10-18T10:00:00Z", "entries": entries}
            text = GU.render(REPO, man, env=env)
            self.assertIn("## 6. This install's manifest (newest first)", text)
            self.assertIn("%LOCALAPPDATA%" + os.sep + "DeepEngineers" + os.sep + "Pulsar", text)
            self.assertIn("%APPDATA%" + os.sep + "SpaceEngineers", text)
            first_row = text.split("## 6.")[1].split("| 1 |")[1].splitlines()[0]
            self.assertIn("`world`", first_row, "newest first")
            mf = tmp / "m.json"
            mf.write_text(json.dumps(man), encoding="utf-8")
            lines = []
            self.assertEqual(GU.run(["--manifest", str(mf), "--out", str(tmp / "U.md")], out=lines.append), 0)
            man["entries"].append({"kind": "mystery", "path": "x"})
            mf.write_text(json.dumps(man), encoding="utf-8")
            self.assertEqual(GU.run(["--manifest", str(mf), "--out", str(tmp / "U.md")], out=lines.append), 2)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

if __name__ == "__main__":
    unittest.main()
