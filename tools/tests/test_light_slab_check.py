"""The numbers behind the cloud light's anchors (E40 look round 2): the mirror of the shader's light against the two-stream slab.

    python -m unittest tools.tests.test_light_slab_check
"""
import math
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from weather_metrics import light_cpu as C  # noqa: E402
from weather_metrics import light_slab_check as S  # noqa: E402


class TwoStreamAnchors(unittest.TestCase):
    def test_the_anchor_values_of_the_brief(self):
        # The brief: tau 30 passes 0.23 of the sun, tau 100 0.08 (g 0.85, no absorption).
        self.assertAlmostEqual(S.transmission(30.0), 0.229, places=3)
        self.assertAlmostEqual(S.transmission(100.0), 0.0816, places=3)

    def test_a_slab_of_optical_depth_25_has_its_base_on_the_anchor(self):
        rows = {r["tau"]: r for r in S.uniform_table(taus=(25,), height=1200.0)}
        r = rows[25]
        self.assertAlmostEqual(r["base"] / r["anchor_base"], 1.0, delta=0.15)
        # The base's radiance over the sun's irradiance is the slab's transmission.
        self.assertAlmostEqual(r["flux_base"], S.transmission(25.0), delta=0.04)

    def test_a_thick_slab_is_not_too_dark_at_its_base_and_a_little_dim_at_its_top(self):
        r = S.uniform_table(taus=(100,), height=1200.0)[0]
        self.assertGreater(r["base"], r["anchor_base"], "the thick base is brighter than the anchor, not darker")
        self.assertLess(r["top"], r["anchor_top"], "the thick top is dimmer than the anchor")
        self.assertGreater(r["top"] / r["anchor_top"], 0.75)

    def test_the_shipped_rain_deck_types_carry_no_light_factor(self):
        for layer, name, tau, h, sigma, profile, base_light, head_light in S.shipped_types():
            if layer in ("Kerbin-weather-rain", "Kerbin-weather-snow"):
                self.assertEqual(base_light, 1.0, name)
                self.assertEqual(head_light, 1.0, name)


if __name__ == "__main__":
    unittest.main()
