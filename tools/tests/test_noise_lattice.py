"""The numpy twin of the lattice table of the untiling warp (E40 C4, tools/weather_metrics/noise_model.py) against the vector value noise it replaces in the shader.

    python -m unittest tools.tests.test_noise_lattice
"""
import unittest

import numpy as np

from tools.weather_metrics import noise_model as nm


class WarpLatticeTable(unittest.TestCase):
    def points(self, origin, count=500):
        rng = np.random.default_rng(5)
        centre = np.asarray(origin) + nm.LATTICE_N // 2
        return centre + rng.uniform(-(nm.LATTICE_N // 2 - 1), nm.LATTICE_N // 2 - 1, size=(count, 3))

    def test_a_table_read_is_the_value_noise_inside_the_window(self):
        origin, seed = (-22, 40, 7), 4242
        p = self.points(origin)
        exact = nm.value_vec(p, seed)
        self.assertLess(np.abs(nm.lattice_read(nm.lattice_window(origin, seed), p) - exact).max(), 1e-9)
        # the formats of the GPU texture: bytes hold a corner to 0.002 (the read to 0.0045), half floats to 0.0005
        self.assertLess(np.abs(nm.lattice_read(nm.lattice_window(origin, seed, precision="byte"), p) - exact).max(), 0.0045)
        self.assertLess(np.abs(nm.lattice_read(nm.lattice_window(origin, seed, precision="half"), p) - exact).max(), 0.0006)

    def test_the_window_is_the_table_wrapped(self):
        table = nm.lattice_window((10, -3, 5), 9)
        # the texel (x 6, y 61, z 5) holds the cell (10 + (6 - 10) mod 64, -3 + (61 + 3) mod 64, 5) = (70, -3, 5)
        fx, fy, fz = nm.hash_vec(nm.hash3(np.array([70]), np.array([-3]), np.array([5]), 9))
        self.assertTrue(np.allclose(table[5, 61, 6], [fx[0], fy[0], fz[0]], atol=0.0))


if __name__ == "__main__":
    unittest.main()
