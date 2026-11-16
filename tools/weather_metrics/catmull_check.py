"""CLOUDR3 P7: the five-tap Catmull-Rom history fetch of ReDefinitionAtmospheresReconstruct.shader (HistoryCatmull) against the full 16-tap Catmull-Rom filter, on random textures.

    python tools/weather_metrics/catmull_check.py

The five taps are read with bilinear hardware (emulated here); the corner taps of the 16-tap filter are dropped and the sum renormalised, so the two agree to a small error, not exactly
(Jimenez, "Filmic SMAA", SIGGRAPH 2016). The check prints the largest difference and fails above 0.05 of the texture's range, and checks that a constant texture is reproduced exactly.
"""
import sys

import numpy as np


def bilinear(tex, x, y):
    h, w = tex.shape
    fx, fy = x - 0.5, y - 0.5
    x0, y0 = int(np.floor(fx)), int(np.floor(fy))
    tx, ty = fx - x0, fy - y0
    def at(i, j):
        return tex[min(max(j, 0), h - 1), min(max(i, 0), w - 1)]
    return (at(x0, y0) * (1 - tx) * (1 - ty) + at(x0 + 1, y0) * tx * (1 - ty) + at(x0, y0 + 1) * (1 - tx) * ty + at(x0 + 1, y0 + 1) * tx * ty)


def weights(f):
    w0 = f * (-0.5 + f * (1.0 - 0.5 * f))
    w1 = 1.0 + f * f * (-2.5 + 1.5 * f)
    w2 = f * (0.5 + f * (2.0 - 1.5 * f))
    w3 = f * f * (-0.5 + 0.5 * f)
    return w0, w1, w2, w3


def five_tap(tex, px, py):
    cx, cy = np.floor(px - 0.5) + 0.5, np.floor(py - 0.5) + 0.5
    w = [weights(px - cx), weights(py - cy)]
    w0x, w1x, w2x, w3x = w[0]
    w0y, w1y, w2y, w3y = w[1]
    w12x, w12y = w1x + w2x, w1y + w2y
    t12x, t12y = cx + w2x / w12x, cy + w2y / w12y
    t0x, t0y, t3x, t3y = cx - 1.0, cy - 1.0, cx + 2.0, cy + 2.0
    taps = [(t12x, t0y, w12x * w0y), (t0x, t12y, w0x * w12y), (t12x, t12y, w12x * w12y), (t3x, t12y, w3x * w12y), (t12x, t3y, w12x * w3y)]
    s = sum(t[2] for t in taps)
    return sum(bilinear(tex, t[0], t[1]) * t[2] for t in taps) / s


def sixteen_tap(tex, px, py):
    h, w = tex.shape
    cx, cy = np.floor(px - 0.5), np.floor(py - 0.5)
    wx, wy = weights(px - 0.5 - cx), weights(py - 0.5 - cy)
    acc = 0.0
    for j in range(4):
        for i in range(4):
            xi, yj = int(cx) - 1 + i, int(cy) - 1 + j
            acc += tex[min(max(yj, 0), h - 1), min(max(xi, 0), w - 1)] * wx[i] * wy[j]
    return acc


def main():
    rng = np.random.default_rng(7)
    tex = rng.random((32, 32))
    worst = 0.0
    for _ in range(2000):
        px, py = rng.uniform(3, 29, 2)
        worst = max(worst, abs(five_tap(tex, px, py) - sixteen_tap(tex, px, py)))
    flat = np.full((32, 32), 0.37)
    flat_err = max(abs(five_tap(flat, *rng.uniform(3, 29, 2)) - 0.37) for _ in range(200))
    print("largest difference of the five taps against the 16-tap filter on random texture: %.4f; constant texture error %.2e" % (worst, flat_err))
    return 0 if worst < 0.05 and flat_err < 1e-9 else 1


if __name__ == "__main__":
    sys.exit(main())
