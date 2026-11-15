"""The photon-walk references of the cloud light (E40 ML light): the walk itself against what it must reproduce, and the model's single scattering against the walk.

    python -m unittest tools.tests.test_light_mc
"""
import math
import os
import sys
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from weather_metrics import light_mc_fit as F  # noqa: E402
from weather_metrics import light_mc_sphere as S  # noqa: E402


class PhotonWalk(unittest.TestCase):
    def test_the_walk_of_a_thin_sphere_is_the_single_scattering_the_model_marches(self):
        # tau 2: almost all light is scattered once. The walk's image and the model's march agree on the single scattering (the geometry, the units and the phase are the same).
        res = S.run(2.0, 20000, seed=3)
        p = S.model_params()
        for k, a in enumerate(S.VIEWS):
            mi, mi1 = S.model_image(p, 2.0, a)
            walk1 = S.stats(res["img1"][k])[0]
            model1 = S.stats(mi1)[0]
            self.assertAlmostEqual(model1 / walk1, 1.0, delta=0.12, msg="alpha %g" % a)

    def test_a_thick_sphere_sends_most_of_the_light_back_and_the_front_is_brighter_than_the_shadow_side(self):
        res = S.run(60.0, 20000, seed=4)
        front = S.stats(res["img1"][0] + res["img2"][0])[0]
        back = S.stats(res["img1"][6] + res["img2"][6])[0]
        self.assertGreater(front, 5.0 * back)
        # the sunlit centre of a thick cell reads like a card facing the sun (0.5 to 1.1 W)
        centre = S.centre(res["img1"][0] + res["img2"][0])
        self.assertTrue(0.5 < centre < 1.1, centre)

    def test_the_fit_geometry_reproduces_the_model_march(self):
        p = S.model_params()
        geo = F.sphere_geometry(50.0, 60.0, steps=96)
        mine = F.model_sphere(p, geo).reshape(S.NPIX, S.NPIX)
        theirs, _ = S.model_image(p, 50.0, 60.0, steps=96)
        self.assertTrue(np.allclose(mine, theirs, rtol=1e-6, atol=1e-9))


if __name__ == "__main__":
    unittest.main()
