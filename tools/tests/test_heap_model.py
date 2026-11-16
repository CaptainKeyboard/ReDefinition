"""CLOUDR1 P2: the NumPy twin of the heap volume (tools/weather_metrics/noise_model.py gen_heap_levels, heap_value) against the numbers the C# twin
(src/Atmospheres/Render/Layers/Noise/HeapVolumes.cs, tests HeapVolumeTests) is checked against, and the scale-invariance of the cascade."""
import os
import sys
import unittest

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "weather_metrics"))
import noise_model as nm  # noqa: E402


def slice_slope(volume, lo, hi):
    """Slope of the radially averaged power spectrum of the volume's z slices over the wavenumbers lo..hi (cycles per period)."""
    spectra = []
    for z in range(0, volume.shape[0], 4):
        a = volume[z] - volume[z].mean()
        spectra.append(np.abs(np.fft.fft2(a)) ** 2)
    power = np.mean(spectra, axis=0)
    n = power.shape[0]
    ky, kx = np.meshgrid(np.fft.fftfreq(n) * n, np.fft.fftfreq(n) * n, indexing="ij")
    k = np.hypot(kx, ky)
    ks = np.arange(lo, hi + 1)
    p = [power[(k >= i - 0.5) & (k < i + 0.5)].mean() for i in ks]
    return float(np.polyfit(np.log(ks), np.log(p), 1)[0])


class HeapModelTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.levels = nm.gen_heap_levels(16, 1)

    def test_golden_numbers_of_the_csharp_twin(self):
        self.assertEqual(5, len(self.levels))
        self.assertEqual(0xC8EA9DAD82F6101D, nm.fnv1a(self.levels[0].tobytes()))
        self.assertEqual(0x83D897480C7869F4, nm.fnv1a(self.levels[1].tobytes()))
        self.assertEqual([164, 180, 255, 255], self.levels[0][3, 5, 7].tolist())
        self.assertEqual([61, 59, 224, 210], self.levels[0][15, 15, 15].tolist())

    def test_trilinear_read_golden(self):
        pts = np.array([[1200.3, -800.7, 3400.1], [-5000.0, 2500.5, 17.9], [333.3, 444.4, 555.5]])
        got = nm.heap_value(self.levels, pts, 250.0, 1, 0.0)
        self.assertTrue(np.allclose(got, [0.507010133615, 0.552574862745, 0.632325261581], atol=1e-9))
        got = nm.heap_value(self.levels, pts, 250.0, 2, 1.5)
        self.assertTrue(np.allclose(got, [0.268435270754, 0.267516915882, 0.244216577071], atol=1e-9))

    def test_rank_uniform(self):
        field = self.levels[0][..., 0].ravel() / 255.0
        hist, _ = np.histogram(field, bins=8, range=(0.0, 1.0))
        self.assertTrue(np.all(np.abs(hist / field.size - 0.125) < 0.03))

    def test_octave_ladder_has_lacunarity_two(self):
        self.assertEqual([2, 4, 8, 16], nm.heap_octaves(64))
        self.assertEqual([2, 4, 8, 16, 32], nm.heap_octaves(128))
        self.assertEqual([2], nm.heap_octaves(8))


class ScaleInvarianceTests(unittest.TestCase):
    """The cascade is self-similar: the power spectrum of a slice is a power law over the octaves the volume holds, steeper for the smoother preset."""

    @classmethod
    def setUpClass(cls):
        cls.billow = nm.heap_field(64, 64, nm.HEAP_HURST[0], 1)
        cls.rough = nm.heap_field(64, 64, nm.HEAP_HURST[1], 18)

    def test_spectrum_follows_the_nominal_power_law_over_the_whole_range(self):
        # Hurst 0.65: P(k) falls as k^-(2 H + 2) = k^-3.3 (measured -3.22 over k = 2..16). The bands are not equal: the octaves are cellular (a Poisson floor at low k, a
        # roll-off of the smooth domes at high k), so the band slopes run from about -2.2 (k 2-4) to -3.8 (k 8-16); the whole-range slope is the nominal one.
        whole = slice_slope(self.billow, 2, 16)
        self.assertTrue(-3.6 < whole < -2.8, whole)
        for lo, hi in ((2, 4), (4, 8), (8, 16)):
            self.assertTrue(-4.4 < slice_slope(self.billow, lo, hi) < -1.8, (lo, hi))

    def test_rougher_preset_has_the_shallower_spectrum(self):
        self.assertLess(slice_slope(self.billow, 2, 16), slice_slope(self.rough, 2, 16))


if __name__ == "__main__":
    unittest.main()
