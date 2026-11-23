"""Measures of how a lookup hides the tiling of a periodic volume (package CLOUDR1): the strain of the lookup, the correlation at the period, the elongation coherence of the blobs.

    python tools/weather_metrics/tiling.py        prints the table for the shape volume's untiling displacement, the cumuliform's halved displacement and the blended lookup

strain       the singular values of the Jacobian d q / d p of a lookup q(p), by central differences: 1 everywhere is no distortion; the largest above 1.5 or the smallest below 0.67 is a pulled or
             squeezed pattern; a smallest value near 0 is a fold.
tile corr    the correlation of the field with itself shifted by one period (1: the tile repeats) against the correlation at a lag that is no multiple of it (the level of an unrelated place).
coherence    R = |sum of area x exp(2 i theta)| / sum of area over the elongated blobs of a thresholded slice: 0 their long axes point everywhere, 1 they are all parallel (a repeated tile).
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import noise_model as nm  # noqa: E402


def strain(lookup, points, h=5.0):
    """(largest singular value p99, smallest p1, anisotropy p95, share of points with the largest above 1.5) of the Jacobian of lookup(points (M, 3)) -> (M, 3)."""
    j = np.zeros((len(points), 3, 3))
    for k in range(3):
        dp = np.zeros(3)
        dp[k] = h
        j[:, :, k] = (lookup(points + dp) - lookup(points - dp)) / (2.0 * h)
    s = np.linalg.svd(j, compute_uv=False)
    return (float(np.percentile(s[:, 0], 99)), float(np.percentile(s[:, 2], 1)), float(np.percentile(s[:, 0] / s[:, 2], 95)), float(np.mean(s[:, 0] > 1.5)))


def tile_correlation(field, points, period_m):
    """Correlation of field(points) with field(points + period along x), and with a lag of 0.37 periods."""
    f0 = field(points)
    out = []
    for lag in (period_m, 0.37 * period_m):
        out.append(float(np.corrcoef(f0, field(points + np.array([lag, 0.0, 0.0])))[0, 1]))
    return tuple(out)


def orientation_coherence(img, cover=0.4, min_px=60):
    """Orientation coherence R of the elongated blobs (axis ratio above 1.3) of img thresholded to the top `cover` share, and their number."""
    from scipy import ndimage as ndi
    mask = img > np.quantile(img, 1.0 - cover)
    lab, n = ndi.label(mask)
    zs, ws = [], []
    for i in range(1, n + 1):
        ys, xs = np.nonzero(lab == i)
        if len(ys) < min_px:
            continue
        ev, evec = np.linalg.eigh(np.cov(np.vstack([xs, ys])))
        if np.sqrt(ev[1] / max(ev[0], 1e-9)) < 1.3:
            continue
        zs.append(np.exp(2j * np.arctan2(evec[1, 1], evec[0, 1])))
        ws.append(len(ys))
    if not zs:
        return float("nan"), 0
    zs, ws = np.array(zs), np.array(ws, dtype=float)
    return float(abs(np.sum(zs * ws) / np.sum(ws))), len(zs)


def main():
    scale, radius = 250.0, 600000.0
    levels = nm.gen_cumuliform_levels(64, 1)
    rng = np.random.RandomState(9)
    pts = np.stack([rng.uniform(0, 40000, 6000), rng.uniform(0, 40000, 6000), radius + rng.uniform(1000, 6000, 6000)], axis=1)
    prm = nm.Params(scale=scale, untile=True, seed=3, offset=(1234.5, 777.7, 31.1))
    prm0 = nm.Params(scale=scale, untile=False, seed=3, offset=(1234.5, 777.7, 31.1))
    period = nm.CUMULIFORM_PERIOD_SCALES * scale

    def lookup_share(x, share):
        q, q0 = nm.lookup_point(x, prm), nm.lookup_point(x, prm0)
        d = q - q0
        up = x / np.linalg.norm(x, axis=1, keepdims=True)
        return q0 + share * (d - up * np.sum(d * up, axis=1, keepdims=True))

    cumuliform = lambda q: nm.cumuliform_value(levels, q, scale, 1)
    read = lambda x: cumuliform(x)
    blended = lambda x: nm.blend_macro(read, prm0_lookup(x), period, 3 + 5003)
    prm0_lookup = lambda x: nm.lookup_point(x, prm0)
    print("%-36s strain smax p99 / smin p1 / anisotropy p95 / share > 1.5 | tile corr at the period / at 0.37 of it" % "lookup")
    rows = (("untiling displacement (shape, old cumuliform)", lambda x: nm.lookup_point(x, prm), lambda x: cumuliform(nm.lookup_point(x, prm))),
            ("cumuliform: half of the horizontal part", lambda x: lookup_share(x, 0.5), lambda x: cumuliform(lookup_share(x, 0.5))),
            ("blended (offset, rotation, 4 taps)", prm0_lookup, blended))
    for name, look, field in rows:
        s = strain(look, pts[:3000])
        c = tile_correlation(field, pts[:6000], period)
        print("%-36s %.2f / %.2f / %.2f / %.1f %% | %.2f / %.2f" % ((name,) + s[:3] + (100.0 * s[3],) + c))


if __name__ == "__main__":
    main()
