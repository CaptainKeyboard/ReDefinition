"""The series of the far view (package T06), run through tools/build_weather_player.py --series T06-far:

  T06-far  the scene 'layers' of the weather player (the own cloud system's layer shader) on the far views: horizon from 10 km, a ground view
           of a cumulus field, orbit at 300 km and at 230 km, and a camera above two stacked decks, each in four variants (base: the near
           march; haze: the aerial perspective; mean: the shape noise's mean at wide spacing; far: haze, mean and the march policy). The rows:

    haze.model_error        the shader's hazed colour against the same fog formula evaluated here from the first-hit distance, on an opaque deck
    haze.alpha_change       the haze leaves the alpha alone (a regression guard)
    haze.blue_over_red_*    the haze's own effect on the colour: bluer at the far end of the deck than at the near end
    rim.px_error / rim.sag  the deck's upper rim against the analytic horizon of the outer sphere: how far the rim row is off, and the curvature
    grain.*                 the single-pixel grain of the alpha in the orbit and the horizon views, base against shape mean, over the same pixels
    sweep.*                 nadir views of a 24 km square from 10 to 400 km: the far view (haze, mean, policy) against the near march with the
                            aerial perspective (haze): coverage and luminance per km through the hand-over band 200 to 260 km, the largest step
                            between neighbours, the far view against the near march at 230 km and across the band
    view.*                  the pictures exist (natural and aid, one name each)

  python tools/weather_metrics/farview.py sheet <frames folder> <out folder>   composes the BEFORE / AFTER / STEPS pictures per view
"""
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import looksheet as LK  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "T06-in")
R = pr.KERBIN_RADIUS
SIZE = (800, 450)
VARIANTS = ["base", "haze", "mean", "far"]
AIR_RAYLEIGH = (5.802e-6, 13.558e-6, 33.1e-6)
AIR_MIE, AIR_HR, AIR_HM = 4.44e-6, 8000.0, 1200.0
HORIZON = (0.62, 0.68, 0.76)
PRODUCT = dict(maxSteps=48, minStep=25.0, stepGrowth=0.0002, jitter=1.0, earlyOut=0.01, sunSteps=3, refineBudget=0.5, refineFraction=0.125)
# The fewest samples in a span of a layer in the far view. The attribution at 230 km of the second sweep (entry refinement on): 4 samples gave a
# luminance 9.5 percent below the dense march and 16 samples 6.7 percent; without the refinement 4 samples gave 6.6 percent. The third sweep takes 8,
# with the refinement off (the product's default).
FAR_MIN_SAMPLES = 8
# The measurement jobs (the sweep and the attribution) march as the product does by default: the entry refinement is a look option and is off.
MEASURE = dict(PRODUCT, refineBudget=-1.0)
SWEEP_TILE = 128
SWEEP_FOOTPRINT_M = 12000.0   # the half width of the picture on the sea-level sphere: the view is a 24 km square straight down
SWEEP_ALTS = ([10, 20, 40, 60, 100, 150, 190] + list(range(200, 261)) + [270, 300, 350, 400])


# --------------------------------------------------------------------------------------------------------------------------------
# The air model (the shader's FarAirTau and the C# FarViewMath.AirTau in numpy)
# --------------------------------------------------------------------------------------------------------------------------------
def chord(h0, h1, length, height):
    h0 = np.maximum(h0, 0.0)
    h1 = np.maximum(h1, 0.0)
    dh = h1 - h0
    flat = np.abs(dh) < 1e-3 * height
    safe = np.where(flat, 1.0, dh)
    exact = length * height / safe * (np.exp(-h0 / height) - np.exp(-h1 / height))
    return np.where(flat, length * np.exp(-0.5 * (h0 + h1) / height), exact)


def air_tau(o, d, t, strength=1.0):
    """The optical depth per channel along o + s d, s in [0, t] (arrays: d is N x 3, t is N), o a vector relative to the planet's centre."""
    top = R + 7.0 * max(AIR_HR, AIR_HM)
    b = d @ o
    c = (np.linalg.norm(o) - top) * (np.linalg.norm(o) + top)
    disc = b * b - c
    hit = disc > 0
    s = np.sqrt(np.where(hit, disc, 0.0))
    a0, a1 = -b - s, -b + s
    s0 = np.maximum(0.0, a0)
    s1 = np.minimum(t, a1)
    ok = hit & (s1 > s0)
    seg = np.where(ok, (s1 - s0) / 6.0, 0.0)

    def alt(sv):
        p = o[None, :] + d * sv[:, None]
        return np.linalg.norm(p, axis=1) - R

    prev = alt(s0)
    sum_r = np.zeros_like(t)
    sum_m = np.zeros_like(t)
    for k in range(6):
        nxt = alt(s0 + seg * (k + 1))
        sum_r += chord(prev, nxt, seg, AIR_HR)
        sum_m += chord(prev, nxt, seg, AIR_HM)
        prev = nxt
    ray = np.array(AIR_RAYLEIGH)
    tau = ray[None, :] * sum_r[:, None] + AIR_MIE * sum_m[:, None]
    return np.where(ok[:, None], tau * strength, 0.0)


# --------------------------------------------------------------------------------------------------------------------------------
# Jobs
# --------------------------------------------------------------------------------------------------------------------------------
def far_block(variant):
    """base: the near march; haze: the aerial perspective; mean: the shape's mean at wide spacing; far: both and the march policy."""
    return dict(haze=int(variant in ("haze", "dense", "far")), shapeMean=int(variant in ("mean", "far")), policy=int(variant == "far"),
                minSamples=FAR_MIN_SAMPLES if variant == "far" else 0, inscatter=list(HORIZON), lodStart=1.0, lodEnd=3.0)


def views():
    """The picture views: id, site, altitude, pitch, yaw, fov, sun, layers (built per job), light, space."""
    day = dict(LK.DAY)
    day["skyHorizon"] = list(HORIZON)
    day["skyZenith"] = [0.26, 0.42, 0.75]
    return [
        dict(id="ho", title="Horizon from 10 km over a stratocumulus deck", site="above", alt=10000.0, pitch=-8.0, fov=60.0, sun=(120.0, 45.0),
             layers=lambda: [LK.deck_layer("above.r8", 0.8, 1000.0, 2400.0)], light=day),
        dict(id="hg", title="A cumulus field at 500 m to the horizon", site="cu-side", alt=500.0, pitch=3.0, fov=60.0, sun=(180.0, 35.0),
             layers=lambda: [LK.cu_layer(), LK.cirrus_layer("cirrus.r8")], light=day, maxSteps=96),
        dict(id="ol", title="Orbit at 300 km at the limb", site="orbit", alt=300000.0, pitch=-44.0, fov=55.0, sun=(120.0, 35.0),
             layers=lambda: [LK.deck_layer("orbit.r8", 0.6, 1000.0, 2400.0), LK.cirrus_layer()], light=day, space=1, maxSteps=64),
        dict(id="oh", title="Orbit at 230 km, the hand-over altitude", site="orbit", alt=230000.0, pitch=-30.0, fov=60.0, sun=(120.0, 40.0),
             layers=lambda: [LK.deck_layer("orbit.r8", 0.6, 1000.0, 2400.0), LK.cirrus_layer()], light=day, space=1, maxSteps=64),
        dict(id="ab", title="12 km above two stacked decks", site="above", alt=12000.0, pitch=-22.0, fov=60.0, sun=(120.0, 40.0),
             layers=lambda: [LK.deck_layer("above.r8", 0.8, 1000.0, 2400.0), LK.cirrus_layer("cirrus.r8")], light=day, maxSteps=64),
    ]


def camera_at(site, alt, pitch, yaw, w, h, fov):
    lat, lon = site
    up = np.array([math.cos(math.radians(lat)) * math.cos(math.radians(lon)), math.cos(math.radians(lat)) * math.sin(math.radians(lon)),
                   math.sin(math.radians(lat))])
    return pr.Camera(up * (R + alt), yaw, pitch, w, h, fov)


def jobs(sites):
    out = []
    for v in views():
        cam = camera_at(sites[v["site"]], v["alt"], v["pitch"], v.get("yaw", LK.YAW_EAST), SIZE[0], SIZE[1], v["fov"])
        for variant in VARIANTS:
            j = L.job("FV-%s-%s" % (v["id"], variant), "color", cam, v["layers"](), SIZE[0], SIZE[1], sun=L.sun_vector(cam.pos, v["sun"][0], v["sun"][1]),
                      mapWidth=LK.MAP_W, mapHeight=LK.MAP_H, **PRODUCT)
            j["maxSteps"] = v.get("maxSteps", 48)
            j.update(v["light"])
            j["far"] = far_block(variant)
            if v.get("space"):
                j["space"] = 1
            out.append(j)
        # The grain probes: the march's own radiance and alpha (lum), base against the shape mean.
        if v["id"] in ("ol", "oh", "ho", "hg"):
            for variant in ("base", "mean"):
                j = L.job("FL-%s-%s" % (v["id"], variant), "lum", cam, v["layers"](), SIZE[0], SIZE[1], sun=L.sun_vector(cam.pos, v["sun"][0], v["sun"][1]),
                          mapWidth=LK.MAP_W, mapHeight=LK.MAP_H, **PRODUCT)
                j["maxSteps"] = v.get("maxSteps", 48)
                j.update(v["light"])
                j["far"] = far_block(variant)
                out.append(j)
    # The haze model probe and the rim probe: an opaque uniform deck seen from 10 km at 8 degrees down, over a 960 x 540 picture.
    sites_cam = camera_at(sites["above"], 10000.0, -8.0, LK.YAW_EAST, 960, 540, 60.0)
    sun = L.sun_vector(sites_cam.pos, 120.0, 45.0)
    for jid, mode, haze in (("FH-off", "lum", 0), ("FH-on", "lum", 1), ("FH-aux", "aux", 0)):
        layer = L.layer(1000.0, 2400.0, 0.02, [L.slab_type("slab", 0.0, 1.0, (1.0, 1.0), 0.0)], coverage=1.0, softness=0.3, name="opaque")
        j = L.job(jid, mode, sites_cam, [layer], 960, 540, sun=sun, **dict(PRODUCT, refineBudget=0.0))
        j["maxSteps"] = 128
        j["far"] = dict(haze=haze, shapeMean=0, inscatter=list(HORIZON))
        out.append(j)
    # The sweep: nadir views of a fixed footprint from 10 to 400 km, the base march and the far view.
    site = sites["orbit"]
    for alt in SWEEP_ALTS:
        fov = 2.0 * math.degrees(math.atan(SWEEP_FOOTPRINT_M / (alt * 1000.0)))
        cam = camera_at(site, alt * 1000.0, -90.0, LK.YAW_EAST, SWEEP_TILE, SWEEP_TILE, fov)
        sunv = L.sun_vector(cam.pos, 120.0, 40.0)
        for variant in ("haze", "dense", "far"):
            j = L.job("SW-%d-%s" % (alt, variant), "lum", cam, [LK.deck_layer("orbit.r8", 0.6, 1000.0, 2400.0), LK.cirrus_layer()], SWEEP_TILE, SWEEP_TILE,
                      sun=sunv, mapWidth=LK.MAP_W, mapHeight=LK.MAP_H, **MEASURE)
            j["maxSteps"] = 64
            if variant == "dense":
                # the reference: a march at the near view's resolution whatever the distance (no growth of the step with it, 25 m steps)
                j["stepGrowth"] = 0.0
                j["maxSteps"] = 128
            j.update(LK.DAY)
            j["far"] = far_block(variant)
            out.append(j)
    # The attribution of the far view's difference to the dense march at 230 km: each part of the far view alone and in steps (one altitude).
    cam = camera_at(site, 230000.0, -90.0, LK.YAW_EAST, SWEEP_TILE, SWEEP_TILE, 2.0 * math.degrees(math.atan(SWEEP_FOOTPRINT_M / 230000.0)))
    sunv = L.sun_vector(cam.pos, 120.0, 40.0)
    for name, block, over in DIAGNOSIS:
        j = L.job("DG-%s" % name, "lum", cam, [LK.deck_layer("orbit.r8", 0.6, 1000.0, 2400.0), LK.cirrus_layer()], SWEEP_TILE, SWEEP_TILE, sun=sunv,
                  mapWidth=LK.MAP_W, mapHeight=LK.MAP_H, **MEASURE)
        j["maxSteps"] = 64
        j.update(LK.DAY)
        j.update(over)
        j["far"] = dict(dict(haze=1, shapeMean=0, policy=0, minSamples=0, inscatter=list(HORIZON), lodStart=1.0, lodEnd=3.0), **block)
        out.append(j)
    return out


# name, far block overrides, job overrides: the dense reference, the product's near march, and the parts of the far view
DIAGNOSIS = [
    ("dense", {}, dict(stepGrowth=0.0, maxSteps=128)),
    ("near", {}, {}),
    ("ms4", dict(minSamples=4), {}),
    ("ms8", dict(minSamples=8), {}),
    ("ms16", dict(minSamples=16), {}),
    ("ms8-mean", dict(minSamples=8, shapeMean=1), {}),
    ("ms8-policy", dict(minSamples=8, policy=1), {}),
    ("ms8-look", dict(minSamples=8), dict(refineBudget=0.5, refineFraction=0.125)),
    ("far", dict(minSamples=FAR_MIN_SAMPLES, shapeMean=1, policy=1), {}),
]


def write_inputs():
    sites, cu, deck, above = LK.site_positions()
    LK.INPUT = INPUT
    os.makedirs(INPUT, exist_ok=True)
    LK.write_maps(cu, deck, above)
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs(sites)}, f)


# --------------------------------------------------------------------------------------------------------------------------------
# Rows
# --------------------------------------------------------------------------------------------------------------------------------
def plane(folder, job_id, name):
    return np.load(os.path.join(folder, "%s-%s.npy" % (job_id, name))).astype(np.float64)


def luminance(r, g, b):
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def box3(a):
    p = np.pad(a, 1, mode="edge")
    return sum(p[i:i + a.shape[0], j:j + a.shape[1]] for i in range(3) for j in range(3)) / 9.0


def grain(folder, job_id, other):
    """The mean absolute difference of the alpha to its 3 x 3 mean, over the pixels that carry cloud (alpha above 0.05) in either of the two
    renders: the single-pixel grain, over the same pixels for both."""
    a = plane(folder, job_id, "a")
    mask = (a > 0.05) | (plane(folder, other, "a") > 0.05)
    return float(np.mean(np.abs(a - box3(a))[mask])) if mask.any() else float("nan")


def haze_rows(folder, out):
    cam = camera_at(LK_SITES["above"], 10000.0, -8.0, LK.YAW_EAST, 960, 540, 60.0)
    d = cam.ray_directions()
    first = plane(folder, "FH-aux", "first").reshape(-1)
    alpha = plane(folder, "FH-aux", "alpha").reshape(-1)
    off = [plane(folder, "FH-off", c).reshape(-1) for c in "rgba"]
    on = [plane(folder, "FH-on", c).reshape(-1) for c in "rgba"]
    mask = (alpha > 0.98) & (first > 0)
    # the shader takes the weighted mean distance of the cloud on the ray: the first hit plus about half a step (at most 640 m at the rim)
    t = first + 100.0
    tau = air_tau(cam.pos, d, np.where(mask, t, 1.0))
    trans = np.exp(-tau)
    errs = []
    for c in range(3):
        expected = off[c] * trans[:, c] + off[3] * np.array(HORIZON)[c] * (1.0 - trans[:, c])
        err = np.abs(on[c] - expected)[mask] / np.maximum(expected[mask], 1e-3)
        errs.append(err)
    err = np.concatenate(errs)
    out.append(Metric.at_most("haze.model_error", float(np.percentile(err, 99)), 0.03).based("gate"))
    out.append(Metric.info("haze.model_error_mean", float(err.mean())))
    out.append(Metric.at_most("haze.alpha_change", float(np.abs(on[3] - off[3]).max()), 1e-5).based("gate"))
    out.append(Metric.info("haze.transmittance_blue_min", float(trans[mask, 2].min())))
    out.append(Metric.info("haze.transmittance_red_min", float(trans[mask, 0].min())))
    # the blue goes first: the hazed deck is bluer at the far end than at the near end
    near, far = mask & (first < np.percentile(first[mask], 10)), mask & (first > np.percentile(first[mask], 90))
    # the haze's own effect on the colour: blue over red of the hazed deck divided by the same of the unhazed deck, at the far end against the near end
    ratio = lambda m: float(np.mean(on[2][m] / np.maximum(on[0][m], 1e-6)) / np.mean(off[2][m] / np.maximum(off[0][m], 1e-6)))
    # the same ratio from the fog formula and the unhazed colours (the model), so that the row does not depend on how white the cloud light is
    expected = [off[c] * trans[:, c] + off[3] * np.array(HORIZON)[c] * (1.0 - trans[:, c]) for c in range(3)]
    ratio_model = lambda m: float(np.mean(expected[2][m] / np.maximum(expected[0][m], 1e-6)) / np.mean(off[2][m] / np.maximum(off[0][m], 1e-6)))
    out.append(Metric.at_least("haze.blue_over_red_far_vs_near", ratio(far) / ratio(near), 1.02).based("new"))
    out.append(Metric.at_most("haze.blue_over_red_vs_model", abs(ratio(far) / ratio(near) - ratio_model(far) / ratio_model(near)), 0.01).based("gate"))
    out.append(Metric.info("haze.blue_over_red_near", ratio(near)))
    out.append(Metric.info("haze.blue_over_red_far", ratio(far)))


def rim_rows(folder, out):
    cam = camera_at(LK_SITES["above"], 10000.0, -8.0, LK.YAW_EAST, 960, 540, 60.0)
    d = cam.ray_directions()
    b = d @ cam.pos
    top = R + 2400.0
    c = (np.linalg.norm(cam.pos) - top) * (np.linalg.norm(cam.pos) + top)
    hit = ((b * b - c > 0) & (b < 0)).reshape(540, 960)   # the ray meets the sphere in front of the camera
    alpha = plane(folder, "FH-aux", "alpha")
    meas = alpha > 0.05
    # the rim row of each column: the first row (from the top) that carries cloud
    def rim(mask):
        any_ = mask.any(axis=0)
        return np.where(any_, mask.argmax(axis=0), -1).astype(float)

    rm, ra = rim(meas), rim(hit)
    cols = (rm >= 0) & (ra >= 0)
    diff = np.abs(rm - ra)[cols]
    out.append(Metric.at_most("rim.px_error_max", float(diff.max()), 2.0).based("gate"))
    out.append(Metric.info("rim.px_error_mean", float(diff.mean())))
    sag_m = float(np.mean([rm[:20][rm[:20] >= 0].mean(), rm[-20:][rm[-20:] >= 0].mean()]) - rm[470:490][rm[470:490] >= 0].mean())
    sag_a = float(np.mean([ra[:20][ra[:20] >= 0].mean(), ra[-20:][ra[-20:] >= 0].mean()]) - ra[470:490][ra[470:490] >= 0].mean())
    out.append(Metric.info("rim.sag_px_measured", sag_m))
    out.append(Metric.info("rim.sag_px_analytic", sag_a))
    out.append(Metric.at_most("rim.sag_error", abs(sag_m - sag_a), 1.5).based("gate"))
    out.append(Metric.at_least("rim.sag_px_measured_min", sag_m, 3.0).based("new"))


def sweep_rows(folder, out):
    # "dense" is the reference: a march at the near view's resolution whatever the distance (25 m steps, 128 steps) with the aerial perspective;
    # "haze" is the product's near march (steps growing with the distance, 64 steps) with the aerial perspective, as it draws from far away;
    # "far" is the far view: at least four samples in a span, the shape's mean at wide spacing and the march policy, with the same aerial perspective.
    cov = {"haze": [], "dense": [], "far": []}
    lum = {"haze": [], "dense": [], "far": []}
    for alt in SWEEP_ALTS:
        for variant in ("haze", "dense", "far"):
            jid = "SW-%d-%s" % (alt, variant)
            a = plane(folder, jid, "a")
            cov[variant].append(float(a.mean()))
            lum[variant].append(float(luminance(plane(folder, jid, "r"), plane(folder, jid, "g"), plane(folder, jid, "b")).mean()))
    alts = np.array(SWEEP_ALTS, dtype=float)
    band = (alts >= 200) & (alts <= 260)
    for variant in ("haze", "dense", "far"):
        c = np.array(cov[variant])
        l = np.array(lum[variant])
        dc = np.abs(np.diff(c[band])) / np.maximum(c[band][:-1], 1e-6)
        dl = np.abs(np.diff(l[band])) / np.maximum(l[band][:-1], 1e-6)
        out.append(Metric.info("sweep.%s.coverage_mean" % variant, float(c[band].mean())))
        out.append(Metric.info("sweep.%s.cov_step_max_per_km" % variant, float(dc.max())))
        out.append(Metric.info("sweep.%s.lum_step_max_per_km" % variant, float(dl.max())))
        # a smooth trend with the noise of the sampling removed: the change per km of the fitted line
        fit_c = np.polyfit(alts[band], c[band], 1)[0] * 1.0 / c[band].mean()
        fit_l = np.polyfit(alts[band], l[band], 1)[0] * 1.0 / l[band].mean()
        out.append(Metric.info("sweep.%s.cov_trend_per_km" % variant, float(abs(fit_c))))
        out.append(Metric.info("sweep.%s.lum_trend_per_km" % variant, float(abs(fit_l))))
    c = np.array(cov["far"])
    l = np.array(lum["far"])
    dc = np.abs(np.diff(c[band])) / c[band][:-1]
    dl = np.abs(np.diff(l[band])) / l[band][:-1]
    # the brief's gates: per km at most 5 percent, between two samples a kilometre apart at most 3 percent; the trend is the fitted line (the noise of
    # the sampling removed), the jump the largest difference of neighbours
    out.append(Metric.at_most("sweep.far.coverage_per_km", float(abs(np.polyfit(alts[band], c[band], 1)[0]) / c[band].mean()), 0.05).based("gate"))
    out.append(Metric.at_most("sweep.far.luminance_per_km", float(abs(np.polyfit(alts[band], l[band], 1)[0]) / l[band].mean()), 0.05).based("gate"))
    out.append(Metric.at_most("sweep.far.coverage_jump", float(dc.max()), 0.03).based("gate"))
    out.append(Metric.at_most("sweep.far.luminance_jump", float(dl.max()), 0.03).based("gate"))
    # the far view against the near march at the same altitude, through the band and at 230 km
    rc = np.abs(np.array(cov["far"]) - np.array(cov["dense"])) / np.array(cov["dense"])
    rl = np.abs(np.array(lum["far"]) - np.array(lum["dense"])) / np.array(lum["dense"])
    i = SWEEP_ALTS.index(230)
    out.append(Metric.at_most("sweep.far_vs_dense_coverage_230km", float(rc[i]), 0.03).based("gate"))
    out.append(Metric.at_most("sweep.far_vs_dense_luminance_230km", float(rl[i]), 0.05).based("gate"))
    out.append(Metric.at_most("sweep.far_vs_dense_coverage_band_max", float(rc[band].max()), 0.03).based("gate"))
    out.append(Metric.at_most("sweep.far_vs_dense_luminance_band_max", float(rl[band].max()), 0.05).based("gate"))
    out.append(Metric.info("sweep.far_vs_dense_coverage_400km", float(rc[-1])))
    out.append(Metric.info("sweep.far_vs_dense_luminance_400km", float(rl[-1])))
    out.append(Metric.info("sweep.coverage_10km", cov["far"][0]))
    out.append(Metric.info("sweep.coverage_400km", cov["far"][-1]))
    out.append(Metric.info("sweep.luminance_10km", lum["far"][0]))
    out.append(Metric.info("sweep.luminance_400km", lum["far"][-1]))


def diag_rows(folder, out):
    """The attribution at 230 km: coverage, luminance and luminance per coverage of each variant, and their difference to the dense march."""
    stats = {}
    for name, _, _ in DIAGNOSIS:
        a = plane(folder, "DG-" + name, "a")
        lum = luminance(plane(folder, "DG-" + name, "r"), plane(folder, "DG-" + name, "g"), plane(folder, "DG-" + name, "b"))
        stats[name] = (float(a.mean()), float(lum.mean()))
    dc, dl = stats["dense"]
    for name, (c, l) in stats.items():
        out.append(Metric.info("diag.%s.coverage" % name, c))
        out.append(Metric.info("diag.%s.luminance" % name, l))
        out.append(Metric.info("diag.%s.lum_per_alpha" % name, l / c))
        out.append(Metric.info("diag.%s.coverage_vs_dense" % name, (c - dc) / dc))
        out.append(Metric.info("diag.%s.luminance_vs_dense" % name, (l - dl) / dl))


LK_SITES = None


def collect(ctx):
    global LK_SITES
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    if not os.path.isfile(os.path.join(folder, "FH-aux-alpha.npy")):
        out.append(Metric.flag("written", False))
        return out
    LK_SITES = LK.site_positions()[0]
    haze_rows(folder, out)
    rim_rows(folder, out)
    for vid in ("ol", "oh", "ho", "hg"):
        base, mean = grain(folder, "FL-%s-base" % vid, "FL-%s-mean" % vid), grain(folder, "FL-%s-mean" % vid, "FL-%s-base" % vid)
        out.append(Metric.info("grain.%s.base" % vid, base))
        out.append(Metric.info("grain.%s.mean" % vid, mean))
        ratio = mean / base if base > 0 else float("nan")
        # the orbit views are where the spacing exceeds the noise (the limb, the nadir through one thin layer): the grain must fall there; the
        # near views are reported (the mean only takes over where the spacing is wide)
        out.append(Metric.at_most("grain.%s.mean_over_base" % vid, ratio, 1.0).based("new") if vid in ("ol", "oh") else Metric.info("grain.%s.mean_over_base" % vid, ratio))
    sweep_rows(folder, out)
    diag_rows(folder, out)
    for v in views():
        ok = all(os.path.isfile(os.path.join(folder, "FV-%s-%s%s.png" % (v["id"], variant, suffix))) for variant in VARIANTS for suffix in ("", "-aid"))
        out.append(Metric.flag("view.%s" % v["id"], ok))
    return out


def stages():
    jobs_file = os.path.join(INPUT, "jobs.json")
    if not os.path.isfile(jobs_file) or os.path.getmtime(jobs_file) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False, timeout=1500)]


register_series("T06-far", stages(), collect, exclusive=False, who="T06")


# --------------------------------------------------------------------------------------------------------------------------------
# The stability of the history (DLSS and frame generation): the temporal reconstruction of package T05 on the far view
# --------------------------------------------------------------------------------------------------------------------------------
INPUT_T = os.path.join(BUILD, "T06-temporal-in")
# The temporal scene of T05 measures the jobs named V-ground and V-stack-below (640 x 360); here V-ground is the near march of the product on
# the orbit view at 230 km and V-stack-below the same view with the far view's parts (aerial perspective, shape mean, four samples a span, the
# finer entry). The rows are renamed in the collector.
TEMPORAL_NAMES = {"V-ground": "base", "V-stack-below": "far"}


def write_temporal_inputs():
    import shutil
    sites, _, _, _ = LK.site_positions()
    os.makedirs(INPUT_T, exist_ok=True)
    for name in os.listdir(INPUT):
        if name.endswith(".r8"):
            shutil.copy(os.path.join(INPUT, name), os.path.join(INPUT_T, name))
    out = []
    cam = camera_at(sites["orbit"], 230000.0, -30.0, LK.YAW_EAST, 640, 360, 60.0)
    sun = L.sun_vector(cam.pos, 120.0, 40.0)
    for jid, variant in (("V-ground", "base"), ("V-stack-below", "far")):
        j = L.job(jid, "color", cam, [LK.deck_layer("orbit.r8", 0.6, 1000.0, 2400.0), LK.cirrus_layer()], 640, 360, sun=sun, mapWidth=LK.MAP_W,
                  mapHeight=LK.MAP_H, **PRODUCT)
        j.update(LK.DAY)
        j["far"] = far_block(variant)
        j["far"]["policy"] = 0   # the scene sets the step numbers itself
        out.append(j)
    with open(os.path.join(INPUT_T, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": out}, f)


def collect_temporal(ctx):
    metrics = ctx.player_metrics("scene")
    out = []
    ok = metrics.get("temporal_available", (0.0, "", ""))[0] == 1.0
    out.append(Metric.flag("temporal_available", ok))
    # an absent flag is not a pass: the scene writes it only when it ends by its own guard or by finishing
    out.append(Metric.flag("temporal_completed", "temporal_completed" in metrics and metrics["temporal_completed"][0] != 0.0))
    # The gates of T05's rows (the limits of C21's N14) on the far job in the 4 x 4 reconstruction: the ghost and the trail on the 12 degree turn,
    # the shimmer on the still camera. At 230 km a pixel is about 1.3 km: only the still camera and the two turns move anything; the fly-by (1.7 m a
    # frame) and the wind (0.5 and 25 m a frame) move less than a hundredth of a pixel and read as the still camera, so they are reported only.
    gates = {("turn12", "ghost_move"): 0.012, ("turn12", "trail_px"): 0.14, ("static", "shimmer"): 0.0015}
    seen = set()
    for key, (value, _, _) in sorted(metrics.items()):
        parts = key.split(".")
        if len(parts) == 5 and parts[0] == "temporal" and parts[1] in TEMPORAL_NAMES:
            name = "temporal.%s.%s.%s.%s" % (TEMPORAL_NAMES[parts[1]], parts[2], parts[3], parts[4])
            limit = gates.get((parts[2], parts[4])) if parts[1] == "V-stack-below" and parts[3] == "b4" else None
            if limit is not None:
                seen.add((parts[2], parts[4]))
                out.append(Metric.at_most(name, value, limit).based("gate"))
            else:
                out.append(Metric.info(name, value))
    # every gate must have been measured
    out.append(Metric.flag("temporal.gates_measured", seen == set(gates)))
    return out


def temporal_stages():
    if not os.path.isfile(os.path.join(INPUT_T, "jobs.json")) or os.path.getmtime(os.path.join(INPUT_T, "jobs.json")) < os.path.getmtime(os.path.abspath(__file__)):
        write_temporal_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "layers-temporal", "-in", INPUT_T], size=(1280, 720), proxy=False, timeout=1500)]


register_series("T06-temporal", temporal_stages(), collect_temporal, exclusive=False, who="T06")


# --------------------------------------------------------------------------------------------------------------------------------
# Pictures
# --------------------------------------------------------------------------------------------------------------------------------
def sheet(frames, out_dir):
    """Per view: <id>-BEFORE.png (natural | aid of the base march), <id>-AFTER.png (natural | aid of the far view), <id>-STEPS.png (the four natural pictures)."""
    from PIL import Image, ImageDraw
    os.makedirs(out_dir, exist_ok=True)
    for v in views():
        def load(variant, aid):
            return Image.open(os.path.join(frames, "FV-%s-%s%s.png" % (v["id"], variant, "-aid" if aid else ""))).convert("RGB")

        for tag, variant in (("BEFORE", "base"), ("AFTER", "far")):
            nat, aid = load(variant, False), load(variant, True)
            aid = aid.crop((0, 0, nat.width, nat.height))
            img = Image.new("RGB", (nat.width * 2 + 6, nat.height), (24, 24, 24))
            img.paste(nat, (0, 0))
            img.paste(aid, (nat.width + 6, 0))
            img.save(os.path.join(out_dir, "FV-%s-%s.png" % (v["id"], tag)))
        first = load("base", False)
        w, h = first.width // 2, first.height // 2
        img = Image.new("RGB", (w * 2 + 6, h * 2 + 6), (24, 24, 24))
        for k, variant in enumerate(VARIANTS):
            tile = load(variant, False).resize((w, h))
            ImageDraw.Draw(tile).text((6, 4), variant, fill=(255, 255, 255))
            img.paste(tile, ((k % 2) * (w + 6), (k // 2) * (h + 6)))
        img.save(os.path.join(out_dir, "FV-%s-STEPS.png" % v["id"]))


if __name__ == "__main__":
    if len(sys.argv) >= 4 and sys.argv[1] == "sheet":
        sheet(sys.argv[2], sys.argv[3])
    elif len(sys.argv) >= 2 and sys.argv[1] == "inputs":
        write_inputs()
    else:
        print(__doc__)
