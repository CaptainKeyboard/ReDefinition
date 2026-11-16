"""CLOUDR1: the multi-band scale-invariance measures (tools/weather_metrics/scaleinv.py) on fractional Brownian test surfaces of known Hurst exponent."""
import os
import sys
import unittest

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "weather_metrics"))
import scaleinv as si  # noqa: E402


class ScaleInvarianceMeasureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.res = {}
        for h in (0.3, 0.6, 0.9):
            f = si.fbm(512, h, seed=3)
            cls.res[h] = si.measure((f - f.min()) / (f.max() - f.min()), f, 0.5)

    def test_spectrum_slope_follows_the_hurst_exponent_in_the_bands_that_fit(self):
        for h, res in self.res.items():
            for band in ((2, 8), (8, 32), (32, 128)):
                v = res["S_alpha"][si.band_name(band)]
                self.assertIsNotNone(v)
                self.assertAlmostEqual(-(2 * h + 2), v, delta=0.35, msg="H %.1f band %s" % (h, band))

    def test_outline_dimension_is_ordered_by_roughness_in_every_band(self):
        for band in ((2, 8), (8, 32), (32, 128)):
            d = [self.res[h]["D"][si.band_name(band)] for h in (0.3, 0.6, 0.9)]
            self.assertTrue(d[0] > d[1] > d[2], (band, d))
            self.assertTrue(1.0 <= min(d) and max(d) <= 2.15, (band, d))

    def test_a_band_that_does_not_fit_is_none(self):
        small = np.random.RandomState(1).rand(40, 40)
        res = si.measure(small, small, 0.5)
        self.assertIsNone(res["D"][si.band_name((128, 512))])
        self.assertIsNone(res["S_alpha"][si.band_name((128, 512))])

    def test_a_straight_edge_has_dimension_one(self):
        mask = np.zeros((256, 256), bool)
        mask[:, 100:] = True
        d = si.box_dimensions(mask)
        self.assertAlmostEqual(1.0, d[si.band_name((8, 32))], delta=0.1)


if __name__ == "__main__":
    unittest.main()
