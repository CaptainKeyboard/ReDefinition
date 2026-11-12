"""The metric scripts of the parity harness (package T11): one function per check of the parity table (tvc-parity.md 4.3).

Every function takes the recorded data of one column (a dict of numbers and numpy arrays: the frames and figures a player scene, a
game series or the reference renderer wrote) and the check's spec from tests/data/parity/checks.json, and returns a Measure: the
measured value, the verdict, the reference value and the figures behind it. The scripts need no game; they run on recorded frames
(`load_capture`) or on arrays in memory.

Columns of the result table (parity_run.py): reference (the analytic result or the brute-force renderer of parity_ref.py), TVC (the
same function applied to TVC's recorded frames, one data point, never the pass/fail truth) and ours (the own pass).

THE FIXED REFERENCE NUMBERS (decided before the checks were built; the source of each is named; a number is never loosened afterwards)
  analytic         a formula; the check computes it
  published        a figure of the literature (the citation is given; [VERIFY] marks one that was quoted from memory and not opened)
  measured         a figure measured with parity_ref.py in this package's first reference run (the run is `parity_run.py numbers`)
  plan             a threshold of the plan itself (tvc-parity 4.3) that stays as proposed because its reference run agrees with it
  Sun angular diameter                      9.3 mrad (0.533 deg)                    published: mean angular diameter of the solar disc,
                                                                                    0.5334 deg (any astronomy reference, e.g. Allen's Astrophysical Quantities)
  Beer-Lambert                              T(2h) = T(h)^2 within 1 % (self-test), 2 % (pass)  analytic: T = exp(-tau)
  HG normalisation                          integral over the sphere = 1 within 1 %  analytic: integral of the Henyey-Greenstein function
  Horizon row error                         <= 2 px (pass), <= 1 px (reference)      analytic: horizon dip angle acos(R / (R + h)); the
                                                                                    2 px are one pixel of the reference's own quantisation plus one of the march step
  Repetition autocorrelation at the tile    <= 0.15                                  measured: reference noise, 8 seeds, 40 x 20 km: |r| <= 0.070
                                            distance                                (a repeating tile gives 0.79 to 0.83); the proposed 0.15 stays
  Noise spectrum slope (power law, 1D)      band 1.3 to 2.6 in each of three octaves published: liquid water path / radiance spectra of clouds
                                            from 300 m to 2.4 km                    scale as k^-5/3 (Davis et al. 1996, J. Atmos. Sci. 53; Cahalan and
                                                                                    Snider 1989) and the cloud perimeter dimension 1.35 (Lovejoy 1982,
                                                                                    Science 216) gives a profile exponent 2.3 [VERIFY: quoted from
                                                                                    memory]; widened by the spread of the estimate over seeds (0.3)
  Vertical optical-depth profile            within 5 % of the table integral         plan (analytic: the integral of the authored curve)
  Type ramp (P09)                           within 2 % of the lerp                   plan (analytic)
  Coverage fraction                         within 3 points of the map mean          plan (analytic: C by construction of the ranked test map)
  Marker blob position                      within 0.5 deg                           plan (analytic: great-circle distance)
  Stacking alpha                            1 - (1-a1)(1-a2) within 2 %              analytic (transmittance product)
  Boundary crossing                         luminance change <= 3 % per frame at 20 m/s  plan; the reference changes by less than 1 %
  Silver lining ratio                       >= 2.0 and equal to the reference within 15 %  plan floor; measured: the reference run gives 18.0 (edge
                                                                                    source function with the sun 8 deg against 75 deg from the view
                                                                                    axis; the analytic phase ratio is 145)
  Light-step error                          <= 10 % at the configured steps          plan; measured: 1 step 45 %, 2 steps 3.8 %, 4 steps 0.9 %, 8 steps
                                                                                    0.2 % against 64 steps; 64 against 256 steps 0.003 %
  Base / top luminance ratio (cumulus)      top >= base, base > 0, ratio within 20 % of the reference  measured: 1.84 (cell of 800 m radius, sun 50 deg
                                                                                    from the side, order 64); single scattering alone gives 7.3. The
                                                                                    plan's photographic band 2 to 6 has no citation and is not used [VERIFY]
  Ambient floor                             >= 3 % of the sun share                  plan
  Powder dip                                same direction as the reference, within 25 %  measured: edge over interior source function 0.79 with multiple
                                                                                    scattering (a dip), 1.78 with single scattering only
  Light cache                               direct <= 16 frames, ambient <= 300, jump <= 3 %  plan ([proposed] by the plan; TVC converges in about 12 frames)
  Shadow displacement                       within 1 % of h / tan(e)                 analytic: shadow geometry
  Shadow penumbra width                     within 25 % of h * 9.3 mrad / sin^2(e) + 2 px  analytic (plan has h * 0.0093 for the overhead sun; the
                                                                                    factor 1/sin^2(e) is the geometry for a sun at elevation e)
  Shadow darkness                           within 10 % of exp(-tau)                 analytic
  Shadow vs tau                             within 10 % of the reference curve at tau 0.1, 1, 5, 20  reference renderer (direct light and diffuse floor)
  Ocean equals land / disc place            5 % / 1 deg                              plan (analytic)
  Convergence frames                        <= reconstruction cycle + 4              plan
  ghost_move / trail_px                     0.012 / 0.14                             the C21 gates (tests/series/rows/C21.json)
  Origin shift                              <= 2 %                                   plan
  Cost                                      <= TVC's ms + 10 %                       plan (tvc-parity Q11), TVC measured by T09's series
  mv_match / FG duplicates                  >= 0.98 / baseline + 20 %                the C21 acceptance
  Depth edge error                          <= 1 px                                  plan (the pixel grid)
  Hand-over                                 <= 5 % per km, jump <= 3 %               plan
  Rain streak angle                         within 3 deg of atan(horizontal / vertical relative speed)  analytic
  Droplet and wetness time constants        +-10 %                                   plan (the config's own constants)
  Warp catch-up                             within 5 % of the stepwise result        plan
  Lightning delay                           within 5 % of d / c (c = 343 m/s at 20 C)  published: speed of sound in air at 20 C, 343.2 m/s
  Aerial perspective                        within 15 % of L0 T + Lsky (1 - T), T = exp(-3.912 d / V)  analytic with the Koschmieder relation
                                                                                    (visual range V at the 2 % contrast threshold; Koschmieder 1924)
  Exposure invariance                       within 3 %                               plan
  RT pack ratio                             within 10 %                              plan
  Robustness                                warp 0 errors, revert valid, 0 leaks, jitter <= 0.25 px  plan

The multiple-scattering order of the reference is measured, not chosen: parity_run.py numbers prints the cell's mean cloud radiance
against the order (1: 0.015, 8: 0.256, 32: 0.465, 64: 0.507, 128: 0.517; the change per doubling falls from 26 % to 9 % to 2 %), so the
reference run uses order 128 (the remaining truncation is below 1 %); a 4x longer reference (steps and light steps) differs by 0.02 %
(single scattering) and 0.5 % (order 16).
"""
import json
import math
import os

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
CHECKS_PATH = os.path.join(REPO, "tests", "data", "parity", "checks.json")

SPEED_OF_SOUND = 343.2  # m/s at 20 C
SUN_ANGULAR_DIAMETER = 9.3e-3
KOSCHMIEDER = 3.912  # -ln(0.02)


class Measure(object):
    """What a check returns: the value, the verdict, the reference value it was judged against and the figures behind it."""

    def __init__(self, value, ok, reference=None, detail=None):
        self.value = None if value is None else float(value)
        self.ok = bool(ok)
        self.reference = None if reference is None else float(reference)
        self.detail = detail or {}


def load_spec(path=None):
    """{check id: spec} from tests/data/parity/checks.json."""
    with open(path or CHECKS_PATH, encoding="utf-8") as f:
        doc = json.load(f)
    return {c["id"]: c for c in doc["checks"]}


def load_capture(folder):
    """A recorded column: a folder with meta.json (numbers, strings) and one .npy per array; a .png is read as luminance 0..1."""
    out = {}
    meta = os.path.join(folder, "meta.json")
    if os.path.isfile(meta):
        with open(meta, encoding="utf-8") as f:
            out.update(json.load(f))
    for name in sorted(os.listdir(folder)):
        stem, ext = os.path.splitext(name)
        full = os.path.join(folder, name)
        if ext == ".npy":
            out[stem] = np.load(full)
        elif ext == ".png":
            from PIL import Image
            out[stem] = np.asarray(Image.open(full).convert("L"), dtype=np.float64) / 255.0
    return out


def th(spec, key, default=None):
    return spec.get("threshold", {}).get(key, default)


def rel(a, b):
    """|a - b| / |b| (b 0: |a|)."""
    return abs(a - b) / abs(b) if b else abs(a)


CHECKS = {}


def check(name):
    def wrap(fn):
        CHECKS[name] = fn
        return fn
    return wrap


# --------------------------------------------------------------------------------------------------------------------------------
# P01, P25 shell and horizon
# --------------------------------------------------------------------------------------------------------------------------------
def crossing_row(profile, threshold, from_bottom):
    """The (fractional) row where a column profile first exceeds the threshold, scanning from the bottom or the top; nan if never."""
    n = len(profile)
    rows = range(n - 1, -1, -1) if from_bottom else range(n)
    prev = None
    for r in rows:
        if profile[r] > threshold:
            if prev is None:
                return float(r)
            a0, a1 = profile[prev], profile[r]
            frac = (threshold - a0) / (a1 - a0) if a1 != a0 else 0.0
            return float(prev + (r - prev) * frac)
        prev = r
    return float("nan")


def analytic_alpha_column(radius, h_cam, layers, pitch_deg, fov_v_deg, height):
    """The exact alpha of the central pixel column of a camera at h_cam looking along the meridian: homogeneous layers
    [(base, top, sigma)], the ground cutting the rays (parity_ref geometry, the sphere/ray chords)."""
    from weather_metrics import parity_ref as pr
    field = pr.DensityField(radius, [pr.Layer("l%d" % i, b, t, s) for i, (b, t, s) in enumerate(layers)])
    cam = pr.Camera([0.0, 0.0, radius + h_cam], 0.0, pitch_deg, 3, height, fov_v_deg)
    d = cam.ray_directions().reshape(height, 3, 3)[:, 1, :]
    tau = field.exact_tau(np.broadcast_to(cam.pos, d.shape), d)
    return 1.0 - np.exp(-tau)


@check("horizon_row")
def horizon_row(data, spec):
    """The farthest cloud row (alpha > 0.05) of the central column against the analytic row of the same shell. Value: px."""
    alpha = np.asarray(data["alpha"], dtype=np.float64)
    h, w = alpha.shape
    cam = data["camera"]
    analytic = analytic_alpha_column(cam["radius"], cam["h_cam"], [tuple(l) for l in cam["layers"]], cam["pitch"], cam["fov_v"], h)
    from_bottom = bool(cam.get("from_bottom", True))
    thr = th(spec, "alpha", 0.05)
    got = crossing_row(alpha[:, w // 2], thr, from_bottom)
    ref = crossing_row(analytic, thr, from_bottom)
    err = abs(got - ref) if not (math.isnan(got) or math.isnan(ref)) else float("nan")
    return Measure(err, err <= th(spec, "max", 2.0), 0.0, {"row": got, "analytic_row": ref})


# --------------------------------------------------------------------------------------------------------------------------------
# P02, P04, P11, P12 noise and erosion
# --------------------------------------------------------------------------------------------------------------------------------
def spectrum_slope(stripes, dx, lo, hi):
    """The negative slope of the mean 1D power spectrum of the stripes (rows) in the frequency band [lo, hi) in cycles per metre."""
    f = stripes - stripes.mean(axis=1, keepdims=True)
    p = (np.abs(np.fft.rfft(f, axis=1)) ** 2).mean(axis=0)
    k = np.fft.rfftfreq(stripes.shape[1], dx)
    m = (k >= lo) & (k < hi)
    return -float(np.polyfit(np.log(k[m]), np.log(p[m]), 1)[0])


@check("noise_spectrum")
def noise_spectrum(data, spec):
    """Spectrum slope of the alpha stripes in three octave bands; the value is the mean slope, the verdict needs all three in the band."""
    stripes = np.asarray(data["alpha_stripes"], dtype=np.float64)
    dx = float(data["dx"])
    edges = th(spec, "band_edges_per_m", [1 / 2400.0, 1 / 1200.0, 1 / 600.0, 1 / 300.0])
    slopes = [spectrum_slope(stripes, dx, edges[i], edges[i + 1]) for i in range(3)]
    lo, hi = th(spec, "band", [1.5, 2.5])
    return Measure(np.mean(slopes), all(lo <= s <= hi for s in slopes), None, {"slopes": slopes})


def autocorr(f, lag):
    """Normalised autocorrelation of a 2D map along its second axis at a lag in samples."""
    a, b = f[:, :-lag].ravel(), f[:, lag:].ravel()
    a, b = a - a.mean(), b - b.mean()
    return float((a * b).sum() / math.sqrt(float((a * a).sum() * (b * b).sum())))


@check("noise_repetition")
def noise_repetition(data, spec):
    """Autocorrelation at the tile distance of a 40 x 20 km stripe of optical depth."""
    r = autocorr(np.asarray(data["tau_map"], dtype=np.float64), int(data["tile_lag"]))
    return Measure(abs(r), abs(r) <= th(spec, "max", 0.15), None, {"r": r})


# --------------------------------------------------------------------------------------------------------------------------------
# P06-P09 type table and curves, P13 coverage map, P20 stacking
# --------------------------------------------------------------------------------------------------------------------------------
@check("vertical_profile")
def vertical_profile(data, spec):
    """The cumulative optical depth by height against the table integral of the authored curve; value: worst error / total tau."""
    h = np.asarray(data["heights"], dtype=np.float64)  # 0..1 over the layer
    tau = np.asarray(data["tau_cum"], dtype=np.float64)
    curve = np.asarray(data["curve"], dtype=np.float64)  # the profile sampled on 0..1
    x = np.linspace(0.0, 1.0, len(curve))
    cum = np.concatenate([[0.0], np.cumsum(0.5 * (curve[1:] + curve[:-1]) * np.diff(x))]) * float(data["sigma_thickness"])
    ref = np.interp(h, x, cum)
    err = float(np.max(np.abs(tau - ref)) / max(ref[-1], 1e-12))
    return Measure(err, err <= th(spec, "max", 0.05), 0.0, {"total": float(ref[-1])})


@check("type_ramp_lerp")
def type_ramp_lerp(data, spec):
    """Base and top measured at the type-map ramp against the lerp of the two types' heights; value: worst error / thickness."""
    t = np.asarray(data["ramp"], dtype=np.float64)
    base = np.asarray(data["base"], dtype=np.float64)
    top = np.asarray(data["top"], dtype=np.float64)
    b0, b1, t0, t1 = data["types"]  # base and top of type A and B
    eb = np.abs(base - (b0 + (b1 - b0) * t))
    et = np.abs(top - (t0 + (t1 - t0) * t))
    err = float(max(eb.max(), et.max()) / max(abs(t0 - b0), 1e-9))
    return Measure(err, err <= th(spec, "max", 0.02), 0.0)


@check("coverage_fraction")
def coverage_fraction(data, spec):
    """The fraction of nadir pixels with alpha > 0.5 against the mean of the test map; value: the difference in fraction points."""
    a = np.asarray(data["alpha"], dtype=np.float64)
    frac = float((a > th(spec, "alpha", 0.5)).mean())
    err = abs(frac - float(data["map_mean"]))
    return Measure(err, err <= th(spec, "max", 0.03), data["map_mean"], {"fraction": frac})


def great_circle_deg(lat1, lon1, lat2, lon2):
    a, b, c, d = map(math.radians, (lat1, lon1, lat2, lon2))
    s = math.sin(a) * math.sin(c) + math.cos(a) * math.cos(c) * math.cos(b - d)
    return math.degrees(math.acos(max(-1.0, min(1.0, s))))


@check("map_orientation")
def map_orientation(data, spec):
    """A marker blob's measured place against its latitude and longitude; value: great-circle distance in degrees."""
    m, t = data["measured_latlon"], data["true_latlon"]
    e = great_circle_deg(m[0], m[1], t[0], t[1])
    return Measure(e, e <= th(spec, "max", 0.5), 0.0)


@check("stack_alpha")
def stack_alpha(data, spec):
    """Combined alpha of two layers against 1 - (1-a1)(1-a2); value: the worst absolute difference."""
    a1, a2, a12 = (np.asarray(data[k], dtype=np.float64) for k in ("alpha1", "alpha2", "alpha12"))
    err = float(np.max(np.abs(a12 - (1.0 - (1.0 - a1) * (1.0 - a2)))))
    return Measure(err, err <= th(spec, "max", 0.02), 0.0)


@check("boundary_crossing")
def boundary_crossing(data, spec):
    """The largest frame-to-frame relative luminance change of a camera path through a layer boundary at 20 m/s."""
    lum = np.asarray(data["luminance"], dtype=np.float64)
    step = np.abs(np.diff(lum)) / np.maximum(np.abs(lum[:-1]), 1e-9)
    err = float(step.max())
    return Measure(err, err <= th(spec, "max", 0.03), 0.0)


# --------------------------------------------------------------------------------------------------------------------------------
# P26-P34 lighting
# --------------------------------------------------------------------------------------------------------------------------------
@check("phase_integral")
def phase_integral(data, spec):
    """The integral of a phase function over the sphere from samples (mu, p(mu)); value: |integral - 1|."""
    mu = np.asarray(data["mu"], dtype=np.float64)
    p = np.asarray(data["p"], dtype=np.float64)
    from weather_metrics.parity_ref import trapz
    total = 2.0 * math.pi * float(trapz(p, mu))
    return Measure(abs(total - 1.0), abs(total - 1.0) <= th(spec, "max", 0.01), 0.0, {"integral": total})


@check("silver_lining")
def silver_lining(data, spec):
    """Edge luminance with the sun near the view axis against 60 to 90 deg from it; the ratio must reach the minimum and, with a
    reference ratio given, equal it within the relative tolerance."""
    ratio = float(data["edge_near_sun"]) / max(float(data["edge_far_sun"]), 1e-12)
    ok = ratio >= th(spec, "min", 2.0)
    ref = data.get("ref_ratio")
    if ref is not None:
        ok = ok and rel(ratio, float(ref)) <= th(spec, "ref_rel", 0.15)
    return Measure(ratio, ok, ref)


@check("beer_lambert_slab")
def beer_lambert_slab(data, spec):
    """T(2h) = T(h)^2 for a homogeneous slab; value: the relative difference."""
    t1, t2 = float(data["T_h"]), float(data["T_2h"])
    err = rel(t2, t1 * t1)
    return Measure(err, err <= th(spec, "max", 0.02), 0.0)


@check("light_step_error")
def light_step_error(data, spec):
    """The sun term at the configured step count against the 64-step reference; value: relative error."""
    err = rel(float(data["sun_term"]), float(data["sun_term_ref64"]))
    return Measure(err, err <= th(spec, "max", 0.10), 0.0)


@check("base_top_ratio")
def base_top_ratio(data, spec):
    """Top / base luminance of a cumulus cell: the sunlit top is not darker than the base (min), the base is not black, and the ratio
    is within the tolerance of the reference's ratio."""
    base, top = float(data["base"]), float(data["top"])
    ratio = top / max(base, 1e-12)
    ok = ratio >= th(spec, "min", 1.0) and base > 0.0
    ref = data.get("ref_ratio")
    if ref is not None:
        ok = ok and rel(ratio, float(ref)) <= th(spec, "ref_rel", 0.20)
    return Measure(ratio, ok, ref)


@check("ambient_floor")
def ambient_floor(data, spec):
    """The ambient light of the darkest cloud part as a share of the sun share."""
    share = float(data["ambient"]) / max(float(data["sun_share"]), 1e-12)
    return Measure(share, share >= th(spec, "min", 0.03), None)


@check("powder_dip")
def powder_dip(data, spec):
    """Source function at thin edges over the interior's: the dip (ratio below 1) must be in the reference's direction and size."""
    dip = float(data["edge_j"]) / max(float(data["interior_j"]), 1e-12)
    ref = float(data["ref_dip"])
    ok = (dip < 1.0) == (ref < 1.0) and rel(dip, ref) <= th(spec, "ref_rel", 0.25)
    return Measure(dip, ok, ref)


def frames_to_settle(seq, tol):
    """The first frame index from which the sequence stays within tol (relative) of its final value."""
    seq = np.asarray(seq, dtype=np.float64)
    final = seq[-1]
    bad = np.nonzero(np.abs(seq - final) > tol * max(abs(final), 1e-12))[0]
    return int(bad[-1] + 1) if len(bad) else 0


@check("light_cache_direct")
def light_cache_direct(data, spec):
    n = frames_to_settle(data["luminance"], th(spec, "tol", 0.05))
    return Measure(n, n <= th(spec, "max", 16))


@check("light_cache_ambient")
def light_cache_ambient(data, spec):
    n = frames_to_settle(data["luminance"], th(spec, "tol", 0.05))
    return Measure(n, n <= th(spec, "max", 300))


@check("light_cache_jump")
def light_cache_jump(data, spec):
    lum = np.asarray(data["luminance"], dtype=np.float64)
    err = float((np.abs(np.diff(lum)) / np.maximum(np.abs(lum[:-1]), 1e-9)).max())
    return Measure(err, err <= th(spec, "max", 0.03))


# --------------------------------------------------------------------------------------------------------------------------------
# P44-P49 shadows
# --------------------------------------------------------------------------------------------------------------------------------
@check("shadow_displacement")
def shadow_displacement(data, spec):
    """The shadow centroid's horizontal shift against h / tan(e); value: relative error."""
    expected = float(data["height_m"]) / math.tan(math.radians(float(data["elevation_deg"])))
    err = rel(float(data["shift_m"]), expected)
    return Measure(err, err <= th(spec, "max", 0.01), expected)


def width_10_90(x, y):
    """Distance between the 10 % and 90 % levels of a monotone edge profile y(x)."""
    y = np.asarray(y, dtype=np.float64)
    lo, hi = y.min(), y.max()
    a, b = lo + 0.1 * (hi - lo), lo + 0.9 * (hi - lo)
    xs = np.asarray(x, dtype=np.float64)
    order = np.argsort(y)
    return abs(float(np.interp(b, y[order], xs[order]) - np.interp(a, y[order], xs[order])))


def disc_edge_share():
    """The 10-90 % width of the shadow edge of a uniform disc, as a share of the disc's full width (0.687): the area of a unit disc
    beyond a chord at offset u is (acos(u) - u sqrt(1 - u^2)) / pi."""
    u = np.linspace(-1.0, 1.0, 20001)
    f = (np.arccos(u) - u * np.sqrt(1.0 - u * u)) / math.pi
    xs = np.interp([0.9, 0.1], f[::-1], u[::-1])
    return float(abs(xs[0] - xs[1]) / 2.0)


DISC_1090 = disc_edge_share()


@check("shadow_penumbra")
def shadow_penumbra(data, spec):
    """The 10-90 % width of the shadow edge of a hard-edged slab against h * 9.3 mrad / sin^2(e), plus the pixels' own softness."""
    e = math.radians(float(data["elevation_deg"]))
    expected = float(data["height_m"]) * SUN_ANGULAR_DIAMETER / (math.sin(e) ** 2)
    slack = th(spec, "pixels", 2.0) * float(data["pixel_m"])
    got = width_10_90(data["x_m"], data["profile"])
    # The edge of a uniform disc passing a straight edge falls from 90 % to 10 % over DISC_1090 of the disc's full width.
    expected_1090 = expected * DISC_1090
    ok = abs(got - expected_1090) <= th(spec, "rel", 0.25) * expected_1090 + slack
    return Measure(got, ok, expected_1090, {"slack_m": slack, "full_width_m": expected})


@check("shadow_darkness")
def shadow_darkness(data, spec):
    """The shadow's luminance share against exp(-tau of the sun path); value: relative error."""
    got = float(data["shadow"]) / max(float(data["lit"]), 1e-12)
    ref = math.exp(-float(data["tau_sun"]))
    err = rel(got, ref)
    return Measure(err, err <= th(spec, "max", 0.10), ref, {"factor": got})


@check("shadow_receivers")
def shadow_receivers(data, spec):
    """Terrain, a vessel and a kerbal take the same factor: the largest relative spread of the factors."""
    f = np.array([float(v) for v in data["factors"].values()])
    err = float((f.max() - f.min()) / max(f.mean(), 1e-12))
    return Measure(err, err <= th(spec, "max", 0.10))


@check("shadow_vs_tau")
def shadow_vs_tau(data, spec):
    """The ground's shadow factor against tau against the reference curve; value: the worst relative error."""
    got = np.asarray(data["factors"], dtype=np.float64)
    ref = np.asarray(data["ref_factors"], dtype=np.float64)
    err = float(np.max(np.abs(got - ref) / np.maximum(ref, 1e-9)))
    return Measure(err, err <= th(spec, "max", 0.10), 0.0)


@check("ocean_land_equal")
def ocean_land_equal(data, spec):
    err = rel(float(data["water"]), float(data["land"]))
    return Measure(err, err <= th(spec, "max", 0.05), 0.0)


@check("disc_shadow_place")
def disc_shadow_place(data, spec):
    m, t = data["measured_latlon"], data["true_latlon"]
    e = great_circle_deg(m[0], m[1], t[0], t[1])
    return Measure(e, e <= th(spec, "max", 1.0), 0.0)


# --------------------------------------------------------------------------------------------------------------------------------
# P52-P62 temporal, cost, upscaler, depth
# --------------------------------------------------------------------------------------------------------------------------------
@check("convergence_frames")
def convergence_frames(data, spec):
    seq = np.asarray(data["error_to_reference"], dtype=np.float64)
    below = np.nonzero(seq > th(spec, "tol", 0.02))[0]
    n = int(below[-1] + 1) if len(below) else 0
    limit = float(data["cycle_frames"]) + th(spec, "slack", 4)
    return Measure(n, n <= limit, limit)


@check("ghost_metrics")
def ghost_metrics(data, spec):
    """The C21 figures: ghost_move and trail_px per upscaler; the verdict needs every given figure under its limit."""
    limits = {"ghost_move": th(spec, "ghost_move", 0.012), "trail_px": th(spec, "trail_px", 0.14)}
    worst = 0.0
    ok = True
    for name, lim in limits.items():
        for key, v in data.items():
            if key.startswith(name + "."):
                worst = max(worst, float(v) / lim)
                ok = ok and float(v) <= lim
    return Measure(worst, ok, 1.0, {"worst_share_of_limit": worst})


@check("origin_shift")
def origin_shift(data, spec):
    err = rel(float(data["after"]), float(data["before"]))
    return Measure(err, err <= th(spec, "max", 0.02), 0.0)


@check("cost_vs_tvc")
def cost_vs_tvc(data, spec):
    limit = float(data["tvc_ms"]) * (1.0 + th(spec, "over", 0.10))
    return Measure(float(data["ms"]), float(data["ms"]) <= limit, limit)


@check("mv_match")
def mv_match(data, spec):
    v = float(data["mv_match"])
    return Measure(v, v >= th(spec, "min", 0.98), th(spec, "min", 0.98))


@check("fg_duplicates")
def fg_duplicates(data, spec):
    limit = float(data["baseline"]) * (1.0 + th(spec, "over", 0.20))
    return Measure(float(data["duplicates"]), float(data["duplicates"]) <= limit, limit)


@check("depth_edge")
def depth_edge(data, spec):
    """The cloud's occlusion edge against the depth discontinuity, per row, in pixels (mean absolute)."""
    err = float(np.mean(np.abs(np.asarray(data["cloud_edge_x"], dtype=np.float64) - np.asarray(data["depth_edge_x"], dtype=np.float64))))
    return Measure(err, err <= th(spec, "max", 1.0), 0.0)


@check("handover_per_km")
def handover_per_km(data, spec):
    """Luminance and coverage over an altitude sweep: the worst relative change per km and the worst frame jump."""
    alt = np.asarray(data["altitude_m"], dtype=np.float64)
    lum = np.asarray(data["luminance"], dtype=np.float64)
    per_km = np.abs(np.diff(lum)) / np.maximum(np.abs(lum[:-1]), 1e-9) / np.maximum(np.diff(alt) / 1000.0, 1e-9)
    jump = np.abs(np.diff(lum)) / np.maximum(np.abs(lum[:-1]), 1e-9)
    worst, wj = float(per_km.max()), float(jump.max())
    return Measure(worst, worst <= th(spec, "max", 0.05) and wj <= th(spec, "jump", 0.03), None, {"jump": wj})


# --------------------------------------------------------------------------------------------------------------------------------
# P79-P86 precipitation side
# --------------------------------------------------------------------------------------------------------------------------------
@check("rain_streak_angle")
def rain_streak_angle(data, spec):
    """The streak angle from vertical against atan(horizontal / vertical relative velocity) of gravity, wind and the vessel."""
    fall = float(data["fall_speed"])
    wind = np.asarray(data["wind"], dtype=np.float64)
    vessel = np.asarray(data["vessel"], dtype=np.float64)
    rel_v = wind - vessel  # horizontal air velocity relative to the vessel (x, y)
    ref = math.degrees(math.atan2(float(np.hypot(rel_v[0], rel_v[1])), fall - float(data.get("vessel_up", 0.0))))
    err = abs(float(data["angle_deg"]) - ref)
    return Measure(err, err <= th(spec, "max", 3.0), ref)


@check("density_gate_monotone")
def density_gate_monotone(data, spec):
    c = np.asarray(data["coverage"], dtype=np.float64)
    d = np.asarray(data["density"], dtype=np.float64)
    order = np.argsort(c)
    bad = int((np.diff(d[order]) < -1e-9).sum())
    return Measure(bad, bad == 0, 0.0)


@check("droplet_time_constant")
def droplet_time_constant(data, spec):
    """A measured time constant (build-up in rain, drying) against the config's; value: relative error."""
    err = rel(float(data["measured_s"]), float(data["config_s"]))
    return Measure(err, err <= th(spec, "max", 0.10), 0.0)


@check("wet_diffuse_factor")
def wet_diffuse_factor(data, spec):
    err = rel(float(data["wet_over_dry"]), float(data["wet_diffuse"]))
    return Measure(err, err <= th(spec, "max", 0.10), 0.0)


@check("warp_catchup")
def warp_catchup(data, spec):
    err = rel(float(data["jump"]), float(data["stepwise"]))
    return Measure(err, err <= th(spec, "max", 0.05), 0.0)


@check("lightning_delay")
def lightning_delay(data, spec):
    expected = float(data["distance_m"]) / float(data.get("speed_of_sound", SPEED_OF_SOUND))
    err = rel(float(data["delay_s"]), expected)
    return Measure(err, err <= th(spec, "max", 0.05), expected)


@check("lightning_flash")
def lightning_flash(data, spec):
    r = float(data["flash_lum"]) / max(float(data["base_lum"]), 1e-12)
    return Measure(r, r > th(spec, "min", 2.0))


@check("lightning_gate")
def lightning_gate(data, spec):
    """No bolt at 5x warp or above, none where coverage <= 0.1; value: the number of bolts that broke the rule."""
    bad = int(data["bolts_above_warp"]) + int(data["bolts_below_coverage"])
    return Measure(bad, bad == 0, 0.0)


# --------------------------------------------------------------------------------------------------------------------------------
# P87-P99 integration, settings
# --------------------------------------------------------------------------------------------------------------------------------
def aerial_perspective_reference(l_cloud, l_sky, d_m, visual_range_m):
    """The Koschmieder aerial-perspective curve: L = L0 T + Lsky (1 - T), T = exp(-3.912 d / V)."""
    t = np.exp(-KOSCHMIEDER * np.asarray(d_m, dtype=np.float64) / visual_range_m)
    return l_cloud * t + l_sky * (1.0 - t)


@check("aerial_perspective")
def aerial_perspective(data, spec):
    """A deck's luminance at 10, 30 and 60 km against the analytic curve; value: the worst relative error."""
    ref = aerial_perspective_reference(float(data["l_near"]), float(data["l_sky"]), data["d_m"], float(data["visual_range_m"]))
    got = np.asarray(data["luminance"], dtype=np.float64)
    err = float(np.max(np.abs(got - ref) / np.maximum(ref, 1e-9)))
    return Measure(err, err <= th(spec, "max", 0.15), 0.0, {"reference": ref.tolist()})


@check("exposure_invariance")
def exposure_invariance(data, spec):
    err = rel(float(data["ratio_fixed"]), float(data["ratio_auto"]))
    return Measure(err, err <= th(spec, "max", 0.03), 0.0)


@check("rt_pack_ratio")
def rt_pack_ratio(data, spec):
    ray = np.asarray(data["ray_factor"], dtype=np.float64)
    scr = np.asarray(data["screen_factor"], dtype=np.float64)
    err = float(np.max(np.abs(ray - scr) / np.maximum(scr, 1e-9)))
    return Measure(err, err <= th(spec, "max", 0.10), 0.0)


@check("robust_warp")
def robust_warp(data, spec):
    n = int(data["errors"])
    return Measure(n, n == 0 and float(data.get("seconds", 60)) >= th(spec, "seconds", 60), 0.0)


@check("robust_revert")
def robust_revert(data, spec):
    return Measure(float(data["first_frame_valid"]), float(data["first_frame_valid"]) == 1.0, 1.0)


@check("robust_leaks")
def robust_leaks(data, spec):
    n = int(data["leaked_objects"])
    return Measure(n, n == 0, 0.0)


@check("robust_precision")
def robust_precision(data, spec):
    v = float(data["jitter_px"])
    return Measure(v, v <= th(spec, "max", 0.25), 0.25)


def run_check(name, data, spec):
    """Runs one check function on one column's data."""
    return CHECKS[name](data, spec)
