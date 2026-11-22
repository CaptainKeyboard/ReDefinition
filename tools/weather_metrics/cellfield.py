"""CLOUD FIELD: the arrangement of the single clouds (cloud.field), the NumPy twin of src/Atmospheres/Render/Layers/Core/CloudField.cs and the lattice change of CloudCells.cginc.

    python tools/weather_metrics/cellfield.py --selftest
    python tools/weather_metrics/cellfield.py --calibrate     the radius table of the field lattice (a Monte Carlo of the lattice below; CloudCells.cs holds the same numbers)
    python tools/weather_metrics/cellfield.py --stats         the metrics of a field lattice against today's lattice (what the player's pictures are measured with, planar)

The field u(x) is horizontal, tileable (TILE_M) and rank-uniform: a Gaussian field with the power spectrum |k|^-b between the wavelengths LAMBDA_MAX_M and LAMBDA_MIN_M (spectral
synthesis: amplitude |k|^(-b/2), the phase a hash of the wavenumber, the real part of the inverse FFT), then its value replaced by its rank (a CDF table), so the share of the points with
u < f is f for every f. A cell of the lattice is alive where u at its centre is below its alive probability (with the share FIELD_SPECKLE of the cells deciding by their own hash instead), so
the map's share of living cells is kept and they cluster (coarse octaves) and leave gaps.
"""
import argparse
import math
import sys

import numpy as np

TILE_M = 102400.0
SIZE = 1024
SEED = 0x51ED
# The size and slope of the spectrum: the sources are in CloudField.cs (Cahalan & Joseph 1989; Neggers et al. 2003; Wood & Field 2011).
LAMBDA_MIN_M, LAMBDA_MAX_M = 400.0, 20000.0
SLOPE_B = 2.6

JITTER, ALIVE_F0, ALIVE_MAX, EDGE_X = 1.0, 0.25, 0.85, 0.725
R_MAX = 0.8


def hash32(x):
    x = np.asarray(x, dtype=np.uint32)
    with np.errstate(over="ignore"):
        x = x ^ (x >> np.uint32(16))
        x = x * np.uint32(0x7FEB352D)
        x = x ^ (x >> np.uint32(15))
        x = x * np.uint32(0x846CA68B)
        x = x ^ (x >> np.uint32(16))
    return x


def unit(h):
    return (np.asarray(h, dtype=np.uint32) >> np.uint32(8)).astype(np.float64) * (1.0 / 16777216.0)


def make_field(slope=SLOPE_B, lam_min=LAMBDA_MIN_M, lam_max=LAMBDA_MAX_M, seed=SEED, size=SIZE, tile_m=TILE_M):
    """The rank-uniform tileable field, size x size, values 0..1."""
    k = np.fft.fftfreq(size, 1.0 / size)
    kx, ky = np.meshgrid(k, k, indexing="xy")
    kk = np.hypot(kx, ky)
    kmin, kmax = tile_m / lam_max, tile_m / lam_min
    amp = np.where((kk >= kmin) & (kk <= kmax), np.maximum(kk, 1e-9) ** (-0.5 * slope), 0.0)
    ix = (np.round(kx).astype(np.int64) & 0xFFFFFFFF).astype(np.uint32)
    iy = (np.round(ky).astype(np.int64) & 0xFFFFFFFF).astype(np.uint32)
    with np.errstate(over="ignore"):
        h = hash32(ix * np.uint32(0x9E3779B1) ^ iy * np.uint32(0x85EBCA6B) ^ np.uint32(seed) * np.uint32(0xC2B2AE35))
    phase = 2.0 * math.pi * unit(h)
    f = np.real(np.fft.ifft2(amp * np.exp(1j * phase)))
    order = np.argsort(f.ravel(), kind="stable")
    rank = np.empty(f.size)
    rank[order] = (np.arange(f.size) + 0.5) / f.size
    return rank.reshape(f.shape)


def sample(field, x, y, stretch=1.0, ang=0.0):
    """Bilinear, tiling lookup at metres (x east, y north) of the field; stretch > 1 lengthens the features along the direction ang (rad from east): the streets."""
    if stretch != 1.0:
        c, s = math.cos(ang), math.sin(ang)
        a, b = x * c + y * s, -x * s + y * c
        a = a / stretch
        x, y = a * c - b * s, a * s + b * c
    n = field.shape[0]
    gx = x / TILE_M * n - 0.5
    gy = y / TILE_M * n - 0.5
    x0 = np.floor(gx).astype(np.int64)
    y0 = np.floor(gy).astype(np.int64)
    fx, fy = gx - x0, gy - y0
    i0, i1 = x0 % n, (x0 + 1) % n
    j0, j1 = y0 % n, (y0 + 1) % n
    return (field[j0, i0] * (1 - fx) * (1 - fy) + field[j0, i1] * fx * (1 - fy) + field[j1, i0] * (1 - fx) * fy + field[j1, i1] * fx * fy)


# ---- the lattice -------------------------------------------------------------------------------------------------------------------

def pareto_size(unit_value, alpha, smin, smax):
    """A truncated Pareto size factor (the number of clouds falls as s^-(alpha+1)): smin..smax; unit_value uniform 0..1."""
    t = (smin / smax) ** alpha
    return smin * (1.0 - unit_value * (1.0 - t)) ** (-1.0 / alpha)


def cells(pitch, ncell, f, field, params, seed=1):
    """The alive cells of a planar lattice of ncell x ncell cells: centres, size factors, centre field values."""
    ci, cj = np.meshgrid(np.arange(ncell), np.arange(ncell), indexing="xy")
    ci = ci.ravel().astype(np.uint32)
    cj = cj.ravel().astype(np.uint32)
    with np.errstate(over="ignore"):
        h = hash32(cj * np.uint32(0x9E3779B1) ^ ci * np.uint32(0x85EBCA6B) ^ np.uint32(seed) * np.uint32(0xC2B2AE35))
    jx = (unit(hash32(h ^ np.uint32(0xA511E9B3))) - 0.5) * JITTER
    jy = (unit(hash32(h ^ np.uint32(0x63D83595))) - 0.5) * JITTER
    cx = (ci + 0.5) * pitch
    cy = (cj + 0.5) * pitch
    alive_p = min(ALIVE_MAX, f / (ALIVE_F0 if params is None else params.get("f0", ALIVE_F0)))
    own = unit(h)
    if params is None:
        alive = own < alive_p
        s = 1.0 + 0.5 * (2.0 * unit(hash32(h ^ np.uint32(0x2C1B3C6D))) - 1.0)
        uc = np.full(own.shape, 0.5)
    else:
        uc = sample(field, cx, cy, params["stretch"], params["ang"])
        pick = unit(hash32(h ^ np.uint32(0x7A3C5E11))) < params["m"]
        v = np.where(pick, uc, own)
        alive = v < alive_p
        depth = np.clip(1.0 - v / max(alive_p, 1e-6), 0.0, 1.0)
        s = pareto_size(unit(hash32(h ^ np.uint32(0x2C1B3C6D))), params["alpha"], params["smin"], params["smax"])
        s = s * (1.0 + params["grow"] * (2.0 * depth - 1.0))
    return (cx + jx * pitch)[alive], (cy + jy * pitch)[alive], s[alive]


def mask(pitch, ncell, f, field, params, rprof, res=1024):
    """The cloud mask of the lattice on res x res pixels over ncell*pitch metres: a disc of the visible radius rprof*s*pitch, discs capped at R_MAX*EDGE_X pitches."""
    from scipy.spatial import cKDTree
    x, y, s = cells(pitch, ncell, f, field, params)
    if len(x) == 0:
        return np.zeros((res, res), bool)
    span = ncell * pitch
    px = (np.arange(res) + 0.5) * span / res
    gx, gy = np.meshgrid(px, px, indexing="xy")
    # tile the centres once on each side so the mask wraps
    xs = np.concatenate([x + dx * span for dx in (-1, 0, 1) for dy in (-1, 0, 1)])
    ys = np.concatenate([y + dy * span for dx in (-1, 0, 1) for dy in (-1, 0, 1)])
    ss = np.tile(s, 9)
    rad = np.minimum(rprof * ss, R_MAX * EDGE_X) * pitch
    tree = cKDTree(np.c_[xs, ys])
    d, idx = tree.query(np.c_[gx.ravel(), gy.ravel()], k=9)
    inside = (d < rad[idx]).any(axis=1)
    return inside.reshape(res, res)


def calibrate_radius(pitch, ncell, f, field, params, lo=0.02, hi=0.58, rounds=12):
    """The visible radius in pitches at which the mask's cover is f (bisection; the cover is monotone in the radius)."""
    if f <= 0:
        return 0.0
    for _ in range(rounds):
        mid = 0.5 * (lo + hi)
        c = mask(pitch, ncell, f, field, params, mid, 512).mean()
        if c < f:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


# ---- metrics -----------------------------------------------------------------------------------------------------------------------

def component_stats(m, px_m):
    """Sizes (areas in km^2, perimeters in km) of the connected components of the mask."""
    from scipy import ndimage
    lab, n = ndimage.label(m)
    if n == 0:
        return np.array([]), np.array([]), lab
    area = np.bincount(lab.ravel())[1:] * (px_m * 1e-3) ** 2
    er = ndimage.binary_erosion(m, border_value=1)
    edge = m & ~er
    per = np.bincount(lab[edge], minlength=n + 1)[1:] * (px_m * 1e-3)
    return area, per, lab


def loglog_slope(area, amin, amax, bins=12):
    """The exponent of the number density of the component areas, n(A) ~ A^-s, from log bins between amin and amax (km^2)."""
    a = area[(area >= amin) & (area <= amax)]
    if len(a) < 30:
        return float("nan")
    e = np.logspace(math.log10(amin), math.log10(amax), bins + 1)
    cnt, _ = np.histogram(a, e)
    w = np.diff(e)
    mid = np.sqrt(e[:-1] * e[1:])
    ok = cnt > 0
    return -np.polyfit(np.log(mid[ok]), np.log(cnt[ok] / w[ok]), 1)[0]


def perimeter_dimension(area, per, amin=0.3):
    """D from P ~ A^(D/2) over the components of area >= amin km^2 (Lovejoy 1982; Cahalan & Joseph 1989)."""
    ok = area >= amin
    if ok.sum() < 20:
        return float("nan")
    return 2.0 * np.polyfit(np.log(area[ok]), np.log(per[ok]), 1)[0]


def clustering_r(m, px_m, lab, n_random=1):
    """The nearest-neighbour index R of the component centroids (Clark & Evans 1954): the mean nearest distance over its value for a Poisson process of the same density (1 random, below 1 clustered, above 1 even)."""
    from scipy import ndimage
    from scipy.spatial import cKDTree
    n = lab.max()
    if n < 20:
        return float("nan")
    c = np.array(ndimage.center_of_mass(m, lab, range(1, n + 1))) * px_m
    d, _ = cKDTree(c).query(c, k=2)
    nn = d[:, 1].mean()
    dens = n / (m.shape[0] * px_m) ** 2
    return nn / (0.5 / math.sqrt(dens))


def fano(m, px_m, lab, box_km):
    """The index of dispersion (variance over mean) of the component centroids' count in square boxes of box_km: 1 for a Poisson process, above 1 clustered, below 1 even."""
    from scipy import ndimage
    n = lab.max()
    if n < 20:
        return float("nan")
    c = np.array(ndimage.center_of_mass(m, lab, range(1, n + 1))) * px_m * 1e-3
    span = m.shape[0] * px_m * 1e-3
    nb = int(span // box_km)
    h, _, _ = np.histogram2d(c[:, 0], c[:, 1], bins=nb, range=[[0, nb * box_km]] * 2)
    return float(h.var() / max(h.mean(), 1e-9))


def stats(m, px_m):
    area, per, lab = component_stats(m, px_m)
    return {
        "cover": float(m.mean()), "components": int(len(area)), "slopeL": 2.0 * loglog_slope(area, 0.05, 20.0) - 1.0, "dimension": perimeter_dimension(area, per),
        "clark_evans": clustering_r(m, px_m, lab), "fano5": fano(m, px_m, lab, 5.0), "fano20": fano(m, px_m, lab, 20.0), "largest_km2": float(area.max()) if len(area) else 0.0,
    }


DEFAULT = {"m": 0.8, "alpha": 1.7, "smin": 0.5, "smax": 1.6, "grow": 0.6, "stretch": 1.0, "ang": 0.0}


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--selftest", action="store_true")
    p.add_argument("--stats", action="store_true")
    p.add_argument("--calibrate", action="store_true")
    p.add_argument("--pitch", type=float, default=1000.0)
    p.add_argument("--set", default="{}")
    p.add_argument("--fs", default="0.1,0.2,0.3,0.5")
    p.add_argument("--slope", type=float, default=SLOPE_B)
    a = p.parse_args()
    if a.selftest:
        fld = make_field(a.slope)
        assert abs(fld.mean() - 0.5) < 1e-3 and abs((fld < 0.3).mean() - 0.3) < 1e-3
        print("selftest ok")
        return 0
    fld = make_field(a.slope)
    ncell = int(round(TILE_M / a.pitch))
    pitch = TILE_M / ncell
    fs = [float(x) for x in a.fs.split(",")]
    for f in fs:
        for name, par in (("lattice", None), ("field", dict(DEFAULT, **eval(a.set)))):
            r = calibrate_radius(pitch, ncell, f, fld, par)
            m = mask(pitch, ncell, f, fld, par, r)
            st = stats(m, TILE_M / m.shape[0])
            print("f %.2f %-8s r %.3f  %s" % (f, name, r, {k: round(v, 3) for k, v in st.items()}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
