"""The Python twin of the colour a far cloud fades into (E40 H1 aerial perspective): the same numbers as CloudHorizonSkyTests on the C# side.

    python -m unittest tools.tests.test_horizon_sky
"""
import math
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from weather_metrics import horizon_sky as hs  # noqa: E402


class HorizonSky(unittest.TestCase):
    def test_the_twin_gives_the_rows_the_csharp_tests_pin(self):
        hs.selftest()

    def test_start_61_horizon_without_a_fitted_number(self):
        # The frame of K-T2 start 61 without our clouds: 0.663 / 0.784 / 0.820 at row 740 (sun 44 degrees, behind the camera).
        far, _ = hs.evaluate(hs.medium(hs.KERBIN), 92.0, 0.0, math.radians(44.0), math.radians(180.0))
        shown = hs.display(far, 0.2)
        for c, e in enumerate((0.663, 0.784, 0.820)):
            self.assertAlmostEqual(shown[c], e, delta=0.03)

    def test_the_sun_reddens_a_low_horizon_and_a_high_sun_leaves_it_blue(self):
        ratios = []
        for sun in (10.0, 44.0, 80.0):
            far, _ = hs.evaluate(hs.medium(hs.KERBIN), 92.0, 0.0, math.radians(sun), math.radians(180.0))
            ratios.append(far[2] / far[0])
        self.assertLess(ratios[0], ratios[1])
        self.assertLess(ratios[1], ratios[2])

    def test_another_bodys_air_gives_its_own_colour(self):
        far, _ = hs.evaluate(hs.medium(hs.DUNA), 92.0, 0.0, math.radians(44.0), math.radians(180.0))
        self.assertGreater(far[0], far[1])
        self.assertGreater(far[1], far[2])

    def test_the_stage_decodes_the_display_value_back(self):
        far, near, shown = hs.far_near(hs.KERBIN, 264.0, 1500.0, 44.0, 180.0, 0.7486)
        for c in range(3):
            encoded = 0.7486 * (far[c] / 0.7486) ** (1.0 / 2.2)
            self.assertAlmostEqual(encoded, shown[c], places=4)


if __name__ == "__main__":
    unittest.main()
