"""The numpy twin of the soft water content's layer numbers (tools/weather_metrics/noise_model.py) against CloudLayerDef's formulas (E40 soft density, package B1).

    python -m unittest tools.tests.test_soft_density
"""
import unittest

import numpy as np

from tools.weather_metrics import noise_model as nm


class SoftLayerNumbers(unittest.TestCase):
    def test_rain_deck_numbers(self):
        # Kerbin's rain deck (coverage 0.5, edge 0.2, amount 0.3) with the water content 0.3 at the outline, the owner's softness 0.5 and the layer's scale 1.5:
        # w = 0.75, coverage' = 0.5 + 0.1 + (0.3 - 0.5) 0.3 0.75, edge' = 0.3 x 0.75, K = (1 - 0.5 - 0.1 - 0.15) + 0.3 x 0.3 x 0.75.
        cov, edge, k = nm.soft_layer_numbers(0.5, 0.2, 0.3, 0.3, 0.5, 1.5)
        self.assertAlmostEqual(cov, 0.555, places=9)
        self.assertAlmostEqual(edge, 0.225, places=9)
        self.assertAlmostEqual(k, 0.3175, places=9)

    def test_the_old_share_with_these_numbers_is_the_water_content(self):
        c, e, a, closed, w = 0.35, 0.15, 1.0, 0.34, 0.5
        cov, edge, _ = nm.soft_layer_numbers(c, e, a, closed, w)
        rng = np.random.default_rng(3)
        for _ in range(200):
            m, n = rng.random(), rng.random()
            share = np.clip(((n - 0.5) * a + m + cov - 1.0) / edge + 0.5, 0.0, 1.0)
            start = 0.5 + (1.0 - m - c - 0.5 * e) / a
            water = np.clip((n - start) / w + closed, 0.0, 1.0)
            self.assertAlmostEqual(float(share), float(water), places=9)

    def test_clear_sky(self):
        self.assertEqual(float(nm.clear_sky(0.0, 0.2)), -0.2)
        self.assertEqual(float(nm.clear_sky(0.9, 0.2)), 0.9)
        self.assertEqual(float(nm.clear_sky(0.3, 0.0)), 0.3)
        self.assertAlmostEqual(float(nm.clear_sky(0.2, 0.2)), 0.2)


if __name__ == "__main__":
    unittest.main()
