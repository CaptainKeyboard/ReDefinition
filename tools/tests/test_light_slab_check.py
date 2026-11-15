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

    def test_the_base_of_a_uniform_deck_lies_under_the_lambertian_anchor_where_the_photon_walk_has_it(self):
        # E40 LM2: the deck's cell diffusion is fitted to the photon walk of a slab (light_mc_cell.py), whose base seen at 53 degrees is not Lambertian: at tau 100 and the sun at
        # 45 degrees it is 0.74 of the Lambertian two-stream anchor of the sun's light (walk 0.043 W, T mu0 = 0.058). The deck's base lies at 0.81 (tau 10) to 0.58 (tau 100) of the
        # anchor of the sun and the sky (E40 L4 clean / L6 light had it on the anchor, 1.28 to 1.43 times the walk's at tau 100); the top at 0.85 to 1.1 of it from tau 25 up.
        for r in S.uniform_table(taus=(10, 25, 60, 100, 200), height=1200.0):
            self.assertTrue(0.5 < r["base"] / r["anchor_base"] < 0.95, "tau %d: %.2f" % (r["tau"], r["base"] / r["anchor_base"]))
        for r in S.uniform_table(taus=(25, 60, 100, 200), height=1200.0):
            self.assertGreater(r["top"] / r["anchor_top"], 0.8, "tau %d" % r["tau"])
            self.assertLess(r["top"] / r["anchor_top"], 1.15, "tau %d" % r["tau"])

    def test_the_sky_light_of_a_thick_top_is_the_two_stream_reflection_not_the_first_optical_depth(self):
        # E40 L6 light: the sky's share of the top of a thick slab (ambient part of the mirror) was 0.04 W against the two-stream's R L_sky / W = 0.30; with the sky's diffusion it
        # is within 20 % of it, and with ambientMs 0 it is the old 0.04.
        slab = S.Slab(1200.0, lambda z: 100.0 / 1200.0)
        mu0 = math.sin(math.radians(S.SUN_ELEVATION))
        want = (1.0 - S.transmission(100.0)) * S.AMBIENT_TOP / S.WHITE
        for ms, low, high in ((S.product_params().ambientMs, 0.8, 1.2), (0.0, 0.0, 0.2)):
            p = C.Params(ambientMs=ms, ambientDecay=0.11)
            got = 0.0
            n, mv = 160, math.sin(math.radians(53.0))
            ds = slab.h / n / mv
            trans = 1.0
            for i in range(n):
                z = (1.0 - (i + 0.5) / n) * slab.h
                ext = 1.0 - math.exp(-slab.sigma(z) * ds)
                _, amb = S.source(p, slab, z, mu0, 0.0, S.WHITE * math.pi)
                got += trans * amb * ext
                trans *= 1.0 - ext
            self.assertTrue(low <= got / S.WHITE / want <= high, "ms %s: %.3f of the two-stream" % (ms, got / S.WHITE / want))

    def test_no_type_carries_a_base_light_factor(self):
        # E40 LM2: the cell diffusion has the sun's incidence on the cloud's surface, which the cumulus layer's 1.25 of E40 ML light made up for; no type carries a factor on the
        # direct light of its base any more.
        for layer, name, tau, h, sigma, profile, base_light, head_light, base_amb, head_amb in S.shipped_types():
            self.assertEqual(base_light, 1.0, name)
            if layer == "Kerbin-cumulus":
                self.assertEqual(head_light, 1.0, name)

    def test_the_shipped_rain_deck_types_carry_no_light_factor(self):
        for layer, name, tau, h, sigma, profile, base_light, head_light, base_amb, head_amb in S.shipped_types():
            if layer in ("Kerbin-weather-rain", "Kerbin-weather-snow"):
                self.assertEqual(base_light, 1.0, name)
                self.assertEqual(head_light, 1.0, name)


if __name__ == "__main__":
    unittest.main()
