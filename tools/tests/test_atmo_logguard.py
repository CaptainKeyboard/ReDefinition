"""The loader guard (tools/atmo_logguard.py) on the real logs of K-T2 starts 35 to 38 (own1707) where they exist, and on excerpts.

    python -m unittest tools.tests.test_atmo_logguard
"""
import glob
import os
import unittest

from tools import atmo_logguard as lg

KT2_LOGS = r"C:\Users\franz\IdeaProjects\ReDefinition\.claude\worktrees\kt2-run\build\atmo-series\K-T2-*-own1707.log"
# start number -> log stamp
STARTS = {35: "180416", 36: "183418", 37: "185638", 38: "192441"}


def replay(path):
    """Feeds a log line by line with a clock that runs one second per 100 lines; returns the abort reason or None."""
    guard = lg.LogGuard(0.0)
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for i, line in enumerate(f):
            reason = guard.feed_text(line, i / 100.0)
            if reason:
                return reason
    return None


def real_log(number):
    hits = [h for h in glob.glob(KT2_LOGS) if "-" + STARTS[number] + "-" in os.path.basename(h)]
    return hits[0] if hits else None


class RealLogs(unittest.TestCase):
    def need(self, number):
        path = real_log(number)
        if path is None:
            self.skipTest("the log of start %d is not on this machine" % number)
        return path

    def test_start_35_died_in_the_menu_no_trigger(self):
        self.assertIsNone(replay(self.need(35)))

    def test_start_36_loaded_fine_no_trigger(self):
        self.assertIsNone(replay(self.need(36)))

    def test_start_37_triggers(self):
        self.assertIsNotNone(replay(self.need(37)))

    def test_start_38_triggers(self):
        self.assertIsNotNone(replay(self.need(38)))


class Rules(unittest.TestCase):
    def test_load_failed_threshold(self):
        g = lg.LogGuard(0)
        self.assertIsNone(g.feed_text("[WRN] LOAD FAILED : x\n" * 20, 1))
        self.assertIn("LOAD FAILED", g.feed_text("[WRN] LOAD FAILED : x\n", 2))

    def test_cannot_replace_then_nre(self):
        g = lg.LogGuard(0)
        text = ("[ERR] PartCompiler: Cannot replace texture 'a' as cannot find texture 'b' to replace\n"
                "[EXC] NullReferenceException: Object reference not set\n"
                "\tPartLoader.ReplaceTextures (UnityEngine.GameObject model)\n")
        self.assertIsNotNone(g.feed_text(text, 1))

    def test_nre_without_the_cannot_replace_line_passes(self):
        g = lg.LogGuard(0)
        text = "[EXC] NullReferenceException: x\n\tPartLoader.ReplaceTextures (a)\n"
        self.assertIsNone(g.feed_text(text, 1))

    def test_cannot_replace_without_nre_passes(self):
        g = lg.LogGuard(0)
        self.assertIsNone(g.feed_text("[ERR] Cannot replace texture 'a' as cannot find texture 'b'\n[LOG] ok\n", 1))

    def test_nre_elsewhere_after_cannot_replace_passes(self):
        g = lg.LogGuard(0)
        text = "[ERR] Cannot replace texture 'a' as cannot find texture 'b'\n[EXC] NullReferenceException: x\n" + "\tSomething.Else (a)\n" * 6
        self.assertIsNone(g.feed_text(text, 1))
        self.assertIsNone(g.feed_text("\tPartLoader.ReplaceTextures (a)\n", 2))

    def test_stall_in_the_loading_scene(self):
        g = lg.LogGuard(0)
        g.feed_text("[LOG] x\n", 200)
        self.assertIsNone(g.tick(250))  # 50 s of silence, 250 s of loading
        self.assertIsNone(g.tick(260))  # exactly 60 s of silence
        self.assertIsNotNone(g.tick(261))

    def test_slow_but_growing_loading_passes(self):
        g = lg.LogGuard(0)
        for t in range(0, 600, 30):
            self.assertIsNone(g.feed_text("[LOG] x\n", t))

    def test_silence_after_loading_passes(self):
        g = lg.LogGuard(0)
        g.feed_text("[LOG] Scene Change : From LOADING to MAINMENU\n", 100)
        self.assertIsNone(g.tick(5000))


class Tail(unittest.TestCase):
    def test_whole_lines_only(self):
        import tempfile
        d = tempfile.mkdtemp()
        p = os.path.join(d, "k.log")
        t = lg.LogTail(p)
        self.assertEqual(t.read_new(), "")
        with open(p, "wb") as f:
            f.write(b"one\ntw")
        self.assertEqual(t.read_new(), "one\n")
        with open(p, "ab") as f:
            f.write(b"o\n")
        self.assertEqual(t.read_new(), "two\n")


if __name__ == "__main__":
    unittest.main()
