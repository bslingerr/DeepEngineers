"""The first Play's preset question: launcher.s10_ask_preset and s10_resolve_preset with an injected `ask`.

 set PYTHONPATH=src\\DeepEngineers.Launcher && python -m unittest tests.release.test_s10_preset
"""
import os
import sys
import tempfile
import unittest

_p = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "src", "DeepEngineers.Launcher")
if _p not in sys.path:
    sys.path.insert(0, _p)
import launcher as LCH  # noqa: E402

FACTS = ("mid", 16.2, False, ["AMD Radeon RX 6900 XT"])

class AskPreset(unittest.TestCase):
    def ask_with(self, answers):
        it = iter(answers)
        return lambda prompt: next(it)

    def test_enter_keeps_the_recommendation(self):
        out = []
        v = LCH.s10_ask_preset(*FACTS, ask=self.ask_with([""]), out=out.append)
        self.assertEqual(v, "mid")
        self.assertTrue(any("MID (recommended)" in s for s in out), out)
        self.assertTrue(any("RX 6900 XT" in s and "16 GB" in s for s in out), out)

    def test_a_choice_wins_whatever_the_case(self):
        self.assertEqual(LCH.s10_ask_preset(*FACTS, ask=self.ask_with([" HIGH "]), out=lambda s: None), "high")
        self.assertEqual(LCH.s10_ask_preset(*FACTS, ask=self.ask_with(["low"]), out=lambda s: None), "low")

    def test_an_unknown_answer_is_asked_again_then_kept(self):
        out = []
        self.assertEqual(LCH.s10_ask_preset(*FACTS, ask=self.ask_with(["ultra", "mid"]), out=out.append), "mid")
        self.assertTrue(any("type low, mid or high" in s for s in out), out)
        self.assertEqual(LCH.s10_ask_preset(*FACTS, ask=self.ask_with(["a", "b", "c", "d"]), out=lambda s: None, tries=3), "mid")

    def test_no_console_or_timeout_keeps_the_recommendation(self):
        self.assertEqual(LCH.s10_ask_preset(*FACTS, ask=lambda prompt: None, out=lambda s: None), "mid")

        def eof(prompt):
            raise EOFError()
        self.assertEqual(LCH.s10_ask_preset(*FACTS, ask=eof, out=lambda s: None), "mid")

    def test_a_shared_chip_is_named(self):
        out = []
        LCH.s10_ask_preset("low", 15.9, True, ["AMD Radeon 780M"], ask=lambda p: "", out=out.append)
        self.assertTrue(any("shared graphics chip" in s and "780M" in s for s in out), out)

class ResolvePreset(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="de-preset-")
        self._file = LCH.S10_PRESET_FILE
        self._auto = LCH.s10_auto_preset
        LCH.S10_PRESET_FILE = os.path.join(self.tmp, "s10-preset.txt")
        LCH.s10_auto_preset = lambda: FACTS
        self._env = {k: os.environ.get(k) for k in ("DEEPENGINEERS_S10_PRESET", "DEEPENGINEERS_S10_ASK")}
        for k in self._env:
            os.environ.pop(k, None)

    def tearDown(self):
        LCH.S10_PRESET_FILE = self._file
        LCH.s10_auto_preset = self._auto
        for k, v in self._env.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v

    def read(self):
        with open(LCH.S10_PRESET_FILE, encoding="utf-8") as f:
            return f.read().strip()

    def test_first_play_asks_and_writes_the_answer(self):
        asked = []
        v = LCH.s10_resolve_preset(None, ask=lambda p: asked.append(p) or "high")
        self.assertEqual(v, "high")
        self.assertEqual(self.read(), "high")
        self.assertEqual(len(asked), 1)
        self.assertIn("Keep MID?", asked[0])

    def test_an_existing_file_is_never_asked_about(self):
        with open(LCH.S10_PRESET_FILE, "w", encoding="utf-8") as f:
            f.write("low\n")
        asked = []
        self.assertEqual(LCH.s10_resolve_preset(None, ask=lambda p: asked.append(p) or "high"), "low")
        self.assertEqual(asked, [])

    def test_the_exported_variable_wins_without_a_question(self):
        os.environ["DEEPENGINEERS_S10_PRESET"] = "high"
        asked = []
        self.assertEqual(LCH.s10_resolve_preset(None, ask=lambda p: asked.append(p) or "low"), "high")
        self.assertEqual(asked, [])
        self.assertFalse(os.path.exists(LCH.S10_PRESET_FILE))

    def test_ask_off_keeps_the_silent_pick(self):
        os.environ["DEEPENGINEERS_S10_ASK"] = "off"
        asked = []
        self.assertEqual(LCH.s10_resolve_preset(None, ask=lambda p: asked.append(p) or "low"), "mid")
        self.assertEqual(asked, [])
        self.assertEqual(self.read(), "mid")

    def test_no_ask_callable_is_the_old_silent_pick(self):
        self.assertEqual(LCH.s10_resolve_preset(None), "mid")
        self.assertEqual(self.read(), "mid")

    def test_console_ask_without_a_terminal_is_none(self):
        class NoTty:
            def isatty(self):
                return False
        saved = sys.stdin
        sys.stdin = NoTty()
        try:
            self.assertIsNone(LCH.s10_console_ask(timeout_s=1)("x"))
        finally:
            sys.stdin = saved

if __name__ == "__main__":
    unittest.main()
