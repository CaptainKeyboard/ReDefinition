"""The self-test of the parity harness's brute-force reference renderer (package T11): analytic results, the planted defects found, every check function
on a good and a defective record. It tests the Python tool only, so it is a Python test (about 30 s).

    python -m unittest tools.tests.test_parity_run
"""
import unittest

from tools.weather_metrics import parity_run as run


class ReferenceRendererSelfTest(unittest.TestCase):
    def test_the_fast_self_test_passes_and_finds_the_planted_defects(self):
        table = run.selftest(quick=True, fast=True)
        text = table.text()
        self.assertTrue(table.ok, text)
        names = [row[0] for row in table.rows]
        # A planted defect row passes only when its check FAILS on the defective record.
        self.assertIn("planted un-normalised phase: integral check FAILS", names)
        self.assertTrue(any(n.startswith("planted repeating noise tile") for n in names), text)


if __name__ == "__main__":
    unittest.main()
