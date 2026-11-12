"""NumPy mirror of the own cloud system's noise and shape model (package T02): the generators of the tileable 3D volumes, the untiling
warp, the shape and erosion functions of src/Atmospheres/Render/Layers/Noise/ (C#) and unity/.../ReDefinitionAtmospheresNoise.cginc
(HLSL), and the two parity checks R02 on the model alone.

The model is written once per language with the same integer hash, so a volume or a value can be compared across the three. This
module is the tool the constants were tuned with (python tools/weather_metrics/noise_model.py tune) and the source of the golden
numbers of tests/ReDefinition.Tests/Atmospheres/T02 (python tools/weather_metrics/noise_model.py golden).

Model (all lengths in m; `scale` = the layer's NoiseScaleM = the lattice cell of the shape's FINEST octave):
  shape volume   N^3, period Ps = 16 scale, Perlin octaves at 2, 4, 8, 16 cells per period (cells 8, 4, 2, 1 scale) remapped by a
                 Worley fbm at 4, 8, 16 cells (Perlin-Worley, Schneider 2015)
  detail volume  N^3, period Pd = 4 scale, inverted Worley fbm at 2, 4, 8 cells per period
  curl volume    N^3 x 3, period Ps, curl of a vector potential of two Perlin octaves (divergence free)
  untiling       the lookup point is displaced by a vector value noise from the integer hash (no period at all): q = p + A W(p / L)
Technique: Schneider, "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015 (Perlin-Worley, Worley fbm detail,
curl distortion); the fractal-Brownian sum and the two-phase flow map are the common techniques of the shell cloud renderers.
"""
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

U32 = np.uint32


# --------------------------------------------------------------------------------------------------------------------------------
# The integer hash (identical in C# and HLSL)
# --------------------------------------------------------------------------------------------------------------------------------
def lowbias32(x):
    x = np.asarray(x, dtype=U32)
    with np.errstate(over="ignore"):
        x = x ^ (x >> U32(16))
        x = x * U32(0x7FEB352D)
        x = x ^ (x >> U32(15))
        x = x * U32(0x846CA68B)
        x = x ^ (x >> U32(16))
    return x


def hash3(ix, iy, iz, seed):
    """The hash of an integer lattice point (any int array, wrapped to 32 bits) and a seed."""
    with np.errstate(over="ignore"):
        h = (np.asarray(seed, dtype=U32) * U32(0x9E3779B1)) ^ (np.asarray(ix).astype(np.int64) & 0xFFFFFFFF).astype(U32) * U32(0x85EBCA6B) \
            ^ (np.asarray(iy).astype(np.int64) & 0xFFFFFFFF).astype(U32) * U32(0xC2B2AE35) ^ (np.asarray(iz).astype(np.int64) & 0xFFFFFFFF).astype(U32) * U32(0x27D4EB2F)
    return lowbias32(h)


def unit(h):
    """A hash as a float in [0, 1)."""
    return (np.asarray(h, dtype=U32) >> U32(8)).astype(np.float64) * (1.0 / 16777216.0)


def hash_vec(h):
    """Three independent floats in [0, 1) from one hash."""
    with np.errstate(over="ignore"):
        return unit(h), unit(lowbias32(h ^ U32(0xA511E9B3))), unit(lowbias32(h ^ U32(0x63D83595)))


# --------------------------------------------------------------------------------------------------------------------------------
# Periodic generators
# --------------------------------------------------------------------------------------------------------------------------------
def fade(t):
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


def perlin(p, cells, seed):
    """Periodic gradient noise at points p (M, 3) given in lattice units of a period of `cells` cells; about -0.9..0.9."""
    i0 = np.floor(p).astype(np.int64)
    f = p - i0
    u = fade(f)
    acc = 0.0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                ix, iy, iz = (i0[:, 0] + dx) % cells, (i0[:, 1] + dy) % cells, (i0[:, 2] + dz) % cells
                gx, gy, gz = hash_vec(hash3(ix, iy, iz, seed))
                g = np.stack([gx * 2 - 1, gy * 2 - 1, gz * 2 - 1], axis=1)
                g /= np.maximum(np.linalg.norm(g, axis=1, keepdims=True), 1e-6)
                d = f - np.array([dx, dy, dz])
                w = (u[:, 0] if dx else 1 - u[:, 0]) * (u[:, 1] if dy else 1 - u[:, 1]) * (u[:, 2] if dz else 1 - u[:, 2])
                acc = acc + w * np.sum(g * d, axis=1)
    return acc


def worley(p, cells, seed):
    """Periodic Worley F1 (distance to the nearest feature point, in cell units, 0..~1) at points p (M, 3) in lattice units."""
    i0 = np.floor(p).astype(np.int64)
    best = np.full(len(p), 9.0)
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                cx, cy, cz = i0[:, 0] + dx, i0[:, 1] + dy, i0[:, 2] + dz
                fx, fy, fz = hash_vec(hash3(cx % cells, cy % cells, cz % cells, seed))
                d = np.stack([cx + fx - p[:, 0], cy + fy - p[:, 1], cz + fz - p[:, 2]], axis=1)
                best = np.minimum(best, np.sum(d * d, axis=1))
    return np.sqrt(best)


def grid_points(n):
    """The voxel centres of an n^3 volume as unit coordinates in [0, 1), x fastest."""
    c = (np.arange(n) + 0.5) / n
    z, y, x = np.meshgrid(c, c, c, indexing="ij")
    return np.stack([x.ravel(), y.ravel(), z.ravel()], axis=1)


def stretch(v):
    lo, hi = float(v.min()), float(v.max())
    return (v - lo) / (hi - lo)


def equalize(v):
    """Each value replaced by its rank over the volume, (rank + 0.5) / count: uniform 0..1, the median 0.5."""
    flat = np.asarray(v).ravel()
    order = np.argsort(flat, kind="stable")
    ranks = np.empty(len(flat))
    ranks[order] = (np.arange(len(flat)) + 0.5) / len(flat)
    return ranks.reshape(np.shape(v))


SHAPE_PERLIN = (2, 4, 8, 16)
SHAPE_PERLIN_W = (2.8, 0.8, 0.7, 0.9)
SHAPE_WORLEY = (4, 8, 16)
SHAPE_WORLEY_W = (0.625, 0.25, 0.125)
DETAIL_WORLEY = (2, 4, 8)
DETAIL_WORLEY_W = (0.25, 0.3, 0.45)
CURL_CELLS = (2, 4)
CURL_W = (1.0, 0.5)


def worley_fbm(u, cells, weights, seed):
    """Sum of (1 - F1) octaves, 0..1."""
    total = 0.0
    for k, (c, w) in enumerate(zip(cells, weights)):
        total = total + w * np.clip(1.0 - worley(u * c, c, seed + 31 * k) / 0.9, 0.0, 1.0)
    return total / float(sum(weights))


def gen_shape(n, seed=1):
    u = grid_points(n)
    pn = 0.0
    for k, (c, w) in enumerate(zip(SHAPE_PERLIN, SHAPE_PERLIN_W)):
        pn = pn + w * perlin(u * c, c, seed + 7 * k)
    pn = stretch(pn)
    wn = worley_fbm(u, SHAPE_WORLEY, SHAPE_WORLEY_W, seed + 101)
    pw = wn + pn * (1.0 - wn)  # remap(perlin, 0, 1, worley, 1)
    return equalize(pw).reshape(n, n, n)


def gen_detail(n, seed=2):
    return stretch(worley_fbm(grid_points(n), DETAIL_WORLEY, DETAIL_WORLEY_W, seed)).reshape(n, n, n)


def gen_curl(n, seed=3):
    """RGB curl volume (n, n, n, 3), components in -1..1 normalised by the largest magnitude."""
    u = grid_points(n)
    pot = []
    for comp in range(3):
        v = 0.0
        for k, (c, w) in enumerate(zip(CURL_CELLS, CURL_W)):
            v = v + w * perlin(u * c, c, seed + 211 * comp + 13 * k)
        pot.append(v.reshape(n, n, n))
    h = 1.0 / n

    def d(a, axis):  # central difference on the periodic grid, per unit length
        return (np.roll(a, -1, axis) - np.roll(a, 1, axis)) / (2.0 * h)

    # arrays are indexed [z, y, x]: axis 0 = z, 1 = y, 2 = x
    cx = d(pot[2], 1) - d(pot[1], 0)
    cy = d(pot[0], 0) - d(pot[2], 2)
    cz = d(pot[1], 2) - d(pot[0], 1)
    c = np.stack([cx, cy, cz], axis=-1)
    return c / np.max(np.linalg.norm(c, axis=-1))


def to_bytes(v):
    return np.clip(np.rint(v * 255.0), 0, 255).astype(np.uint8)


# --------------------------------------------------------------------------------------------------------------------------------
# Lookups
# --------------------------------------------------------------------------------------------------------------------------------
def trilinear(vol, u):
    """Wrapped trilinear lookup of a (n, n, n[, c]) volume at unit coordinates u (M, 3) (voxel centres at (i + 0.5) / n), x = axis 2."""
    n = vol.shape[0]
    x = u * n - 0.5
    i0 = np.floor(x).astype(np.int64)
    f = x - i0
    out = 0.0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                v = vol[(i0[:, 2] + dz) % n, (i0[:, 1] + dy) % n, (i0[:, 0] + dx) % n]
                w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                out = out + (w[:, None] * v if v.ndim == 2 else w * v)
    return out


def value_vec(p, seed):
    """Smooth vector value noise of the integer hash at points p (M, 3) in lattice units, each component -1..1 (no period)."""
    i0 = np.floor(p).astype(np.int64)
    u = fade(p - i0)
    acc = 0.0
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                fx, fy, fz = hash_vec(hash3(i0[:, 0] + dx, i0[:, 1] + dy, i0[:, 2] + dz, seed))
                w = (u[:, 0] if dx else 1 - u[:, 0]) * (u[:, 1] if dy else 1 - u[:, 1]) * (u[:, 2] if dz else 1 - u[:, 2])
                acc = acc + w[:, None] * np.stack([fx * 2 - 1, fy * 2 - 1, fz * 2 - 1], axis=1)
    return acc


class Params(object):
    """The constants of the model. Everything that is a length is a multiple of the layer's scale."""

    def __init__(self, scale=375.0, untile=True, warp_amp=8.0, warp_cell=7.0, curl=True, curl_amp=0.6, seed=1, offset=(0.0, 0.0, 0.0),
                 erosion=0.5, hard_base=1.0, hard_gain=4.0):
        self.scale, self.untile, self.warp_amp, self.warp_cell, self.curl, self.curl_amp = scale, untile, warp_amp, warp_cell, curl, curl_amp
        self.seed, self.offset, self.erosion, self.hard_base, self.hard_gain = seed, np.asarray(offset, dtype=np.float64), erosion, hard_base, hard_gain

    @property
    def shape_period(self):
        return 16.0 * self.scale

    @property
    def detail_period(self):
        return 4.0 * self.scale


class Volumes(object):
    def __init__(self, n_shape=64, n_detail=32, n_curl=32, seed=1):
        self.shape = gen_shape(n_shape, seed)
        self.detail = gen_detail(n_detail, seed + 1)
        self.curl = gen_curl(n_curl, seed + 2)

    def quantized(self):
        """The volumes as the GPU holds them: bytes (the curl as 0.5 + 0.5 c), read back as floats."""
        self.shape = to_bytes(self.shape) / 255.0
        self.detail = to_bytes(self.detail) / 255.0
        self.curl = to_bytes(0.5 + 0.5 * self.curl) / 255.0 * 2.0 - 1.0
        return self


def lookup_point(p, prm):
    """The point the volumes are read at: the layer offset, then the untiling displacement."""
    q = p + prm.offset
    if prm.untile:
        lw = prm.warp_cell * prm.scale
        q = q + prm.warp_amp * prm.scale * value_vec(q / lw, prm.seed + 4001)
    return q


def shape_value(vols, p, prm):
    """The base shape noise at points p (M, 3) in m: 0..1."""
    q = lookup_point(p, prm)
    return trilinear(vols.shape, q / prm.shape_period)


def detail_value(vols, p, prm):
    q = lookup_point(p, prm)
    if prm.curl:
        c = trilinear(vols.curl, q / prm.shape_period)
        q = q + c * (prm.curl_amp * prm.scale)
    return trilinear(vols.detail, q / prm.detail_period)


def erode(s, d, erosion, prm):
    """The eroded coverage share c from the soft share s (0..1), the detail d (0..1) and the type's erosion; with the edge hardness."""
    e = erosion
    er = e * (1.0 - d)
    c = np.clip((s - er) / np.maximum(1.0 - er, 1e-3), 0.0, 1.0)
    h = prm.hard_base + prm.hard_gain * e
    return np.clip(c * h, 0.0, 1.0)


def column_tau(vols, xs, ys, prm, sigma_h, base=2200.0, top=2500.0, coverage=0.0, amount=1.0, edge=0.15, steps=12):
    """Vertical optical depth through the deck at ground positions (xs, ys), flat: the harness's own geometry."""
    m = len(xs)
    tau = np.zeros(m)
    hs = base + (top - base) * (np.arange(steps) + 0.5) / steps
    for h in hs:
        p = np.stack([xs, ys, np.full(m, h)], axis=1)
        n = shape_value(vols, p, prm)
        # the shader's: cov = 1 (no map) + (n - 0.5) * amount, s = saturate((cov + coverage - 1) / edge + 0.5)
        s = np.clip(((n - 0.5) * amount + coverage) / edge + 0.5, 0.0, 1.0)
        if prm.erosion > 0:
            d = detail_value(vols, p, prm)
            s = erode(s, d, prm.erosion, prm)
        tau += sigma_h * s * (top - base) / steps
    return tau


# --------------------------------------------------------------------------------------------------------------------------------
# The two checks on the model
# --------------------------------------------------------------------------------------------------------------------------------
def check_spectrum(vols, prm, seed_rows=12, tau_v=1.0, **kw):
    from weather_metrics import parity_checks as pc
    xs = np.arange(0, 40000, 50.0)
    ys = np.arange(0, 12000, 1000.0)
    X, Y = np.meshgrid(xs, ys)
    tau = column_tau(vols, X.ravel(), Y.ravel(), prm, tau_v / 300.0, **kw).reshape(X.shape)
    alpha = 1.0 - np.exp(-tau)
    edges = [0.000416667, 0.000833333, 0.001666667, 0.003333333]
    return [pc.spectrum_slope(alpha, 50.0, edges[i], edges[i + 1]) for i in range(3)], alpha


def check_repetition(vols, prm, lags_m=(), tau_v=6.0, **kw):
    from weather_metrics import parity_checks as pc
    xs, ys = np.arange(0, 40000, 200.0), np.arange(0, 20000, 200.0)
    X, Y = np.meshgrid(xs, ys)
    tau = column_tau(vols, X.ravel(), Y.ravel(), prm, tau_v / 300.0, **kw).reshape(X.shape)
    out = {}
    for lag in lags_m:
        out[lag] = pc.autocorr(tau, int(round(lag / 200.0)))
    return out, tau


def layer_offset(seed, layer):
    """The base offset of a layer's lookup in m (LayerNoise.BaseOffset)."""
    a, b, c = hash_vec(hash3(layer, 17, 29, seed))
    return np.array([float(a), float(b), float(c)]) * 1.0e5


def layer_seed(seed, layer):
    """The 24-bit seed of a layer's untiling displacement (LayerNoise.LayerSeed)."""
    return int(hash3(layer, 3, 5, seed)) & 0xFFFFFF


def planet_ray_tau(vols, prm, origin, dirs, planet_r, inner, outer, sigma, max_steps, min_step, growth, coverage=0.0, amount=1.0, edge=0.15, jitter=0.5):
    """The optical depth of rays from `origin` along `dirs` (N, 3) through one layer (inner..outer altitude) by the shader's march: the
    sphere spans of the layer, the midpoint rule, count = clamp(ceil(len / step), 1, max_steps), the density sigma * share (the slab type
    with profile 1, erosion prm.erosion). The ground ends the ray."""
    o = np.asarray(origin, dtype=np.float64)
    n = len(dirs)
    b = dirs @ o
    c = o @ o

    def chord(r):
        disc = b * b - (c - r * r)
        s = np.sqrt(np.maximum(disc, 0.0))
        return np.where(disc > 0, -b - s, np.nan), np.where(disc > 0, -b + s, np.nan)

    o0, o1 = chord(planet_r + outer)
    i0, i1 = chord(planet_r + inner)
    g0, g1 = chord(planet_r)
    tmax = np.where(np.isnan(g0), 1e30, np.where(g0 > 0, g0, np.where(g1 > 0, g1, 1e30)))
    lo = np.maximum(o0, 0.0)
    hi = np.minimum(o1, tmax)
    end = np.where(np.isnan(i0), hi, np.minimum(i0, hi))
    lo = np.where(np.isnan(lo), 0.0, lo)
    end = np.where(np.isnan(end), lo, end)
    length = np.maximum(end - lo, 0.0)
    step = np.maximum(min_step * (1.0 + lo * growth), 1e-3)
    count = np.clip(np.ceil(length / step), 1, max_steps).astype(np.int64)
    dt = length / count
    tau = np.zeros(n)
    for k in range(int(count.max())):
        act = (k < count) & (length > 0)
        if not act.any():
            break
        t = lo + dt * (k + jitter)
        pts = o[None, :] + dirs * t[:, None]
        pts = pts[act]
        n_shape = shape_value(vols, pts, prm)
        s = np.clip(((n_shape - 0.5) * amount + coverage) / edge + 0.5, 0.0, 1.0)
        d = detail_value(vols, pts, prm)
        s = erode(s, d, prm.erosion, prm)
        tau[act] += sigma * s * dt[act]
    return tau


def tune():
    vols = Volumes()
    print("volumes: shape %s mean %.3f, detail mean %.3f" % (vols.shape.shape, vols.shape.mean(), vols.detail.mean()))
    for untile in (False, True):
        for seed in (1, 2, 3):
            prm = Params(untile=untile, seed=seed, offset=(seed * 1234.5, seed * 777.7, seed * 31.1))
            sl, _ = check_spectrum(vols, prm)
            lags = (prm.shape_period, 2 * prm.shape_period, prm.detail_period, 20000.0)
            rep, _ = check_repetition(vols, prm, lags)
            print("untile=%s seed=%d slopes %s repetition %s" % (untile, seed, np.round(sl, 2), {int(k): round(v, 3) for k, v in rep.items()}))


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "tune":
        tune()
