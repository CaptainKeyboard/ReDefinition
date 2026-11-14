"""E40 lead investigation: what the own clouds' picture is made of, measured in the weather player (no game start).

  python tools/build_weather_player.py --series e40-fl            build the player, run the jobs, print the table, write the pictures
  python tools/build_weather_player.py --series e40-fl --no-build
  python tools/weather_metrics/e40fl.py --selftest

The scene 'layers' draws Kerbin's layers of Clouds-Kerbin.cfg with the pass of the game at the two poses of K-T2d (ground: 264 m, 18 degrees up; above: 8364 m,
25 degrees down; 60 degrees field of view) with the light of K-T2d start 52 (sun colour, ambient, sun 45.6 degrees up, in front of the camera) and constant maps.
The natural pictures are composed here from the march's radiance (mode lum) with the game's display stage (CloudOutputStage: D = W (c / W) ^ (1 / 2.2)) over a
plain backdrop (a sky gradient and a sea colour read from the game's cloud-free frames); the backdrop is not the game's sky.

Groups (job id prefix):
  sh    the shape: the cumulus layer at map 0.665 with the noise scale 5000 (cfg) / 2000 / 1000 / 700 / 500 m and the noise amount 0.35 (cfg) / 0.7 / 1.0
  cv    the march against a converged march: the product's steps, the entry refinement, the bisection, the step budget, a lower extinction
  mk    the layer mask and the light: rain deck and cumulonimbus, the deck alone by the mask, the deck alone as a stack
  fg    the rain deck at no precipitation (type value 0: Fog) at the low pick, seen from 264 m; and at the type value of Stratus

EXPECTED RESULTS, written before the first run (E40-fable-lead-result.md has what came out):
  X1  sh, above view, cfg numbers (5000 m, 0.35): one or two cloud bodies fill the picture; cloud/clear transitions per picture row under 3 (a sheet with a hole). At
      700 m and amount 1.0: more than 8 transitions per row (a field of cells), the cloud share within 0.15 of map + coverage - 0.5 = 0.515.
  X2  cv: the product's march (48 steps, 25 m, full jitter) of the cfg sheet is darker than the converged march in the above view (mean luma of the closed pixels under
      0.9 of the converged one) and its grain measure hf is at least 3 times the converged one's. The entry refinement brings both within 10 percent / a factor 1.5.
  X3  mk: with the mask on layer 0 the deck shows dark areas (p5 / p50 of the deck's luma under 0.6) where the hidden cumulonimbus stands between it and the sun; the deck
      alone as a stack shows none (p5 / p50 over 0.8). The light march does not read the mask.
  X4  fg: at map 0.4056 and type 0 the fog lies between the camera and the ground over more than 0.3 of the picture below the horizon (tau >= 0.92); at the type value
      0.5 (Stratus, base at 210 m) under 0.05.
What the first run gave (kept as rows, no longer judged): X1 held for the transitions (1.1 against 14.3; TVC's picture 21.6) and missed the share (0.80, the slant view through
a 600 m layer closes more than the horizontal share); X2 missed: the product's march is NOT darker than the converged one (0.997), its grain is 15 to 33 times the converged one's and
none of the refinements removes it; X3 and X4 gave nothing: the rain deck and the towers have a noise scale of 10 and 8 km, so one site is one draw (site 0 is clear of both).

SECOND RUN, expected results written before it (after the switch layers.profileShape was built):
  Y1  The cumulus layer at 500 m / 1.0 from above: with layers.profileShape 0 the tops are flat (standard deviation of the first hit's altitude over the closed pixels under
      40 m); with 0.8 at least three times that, and the cloud share falls by 0.05 to 0.30. Four sites.
  Y2  fg over eight sites: the mean share below the horizon with tau >= 0.92 is at least 0.15 at type 0 (Fog) and under 0.10 at type 0.5 (the camera at 264 m is in the
      first 54 m of the Stratus, whose profile starts at 0).
  Y3  mk over six sites: at one site or more the deck's p5 / p50 with the mask is at least 0.10 lower than the deck alone as a stack (the hidden towers shade it).
What the second run gave (the missed rows are kept as rows, no longer judged): Y1 held for the share (0.79 to 0.67) and the transitions rise (19.5 to 25.2; TVC's picture
21.6), but the relief measure does not show a flat top (136 m at 0, 96 m at 0.8: the slant view sees the clouds' sides, the measure is not one of the top); Y2 held for
the fog (0.36 of the picture below the horizon closed, nothing above it: a deck on the ground under a clear sky) and missed for the Stratus (0.26 below: the camera stands in
the Stratus base and looks along it; above the horizon 0.54, a deck overhead); Y3 missed: the towers of the six sites cover 0 to 4 percent of the picture and the masked deck
differs from the deck alone by 0.004, so the player does not show the shading by hidden layers at these sites (the game's patches are not reproduced here).
"""
import argparse
import json
import math
import os
import shutil
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40fl-in")
RESULTS = os.path.join(BUILD, "e40fl-results.json")
R = 600000.0
MAP_W, MAP_H = 8, 4
SIZE = (640, 360)
FOV = 60.0
VIEWS = {"ground": (264.0, 18.0), "above": (8364.0, -25.0)}
# K-T2d start 52 (T09d.env.frameSun / frameAmbient / sunLight): the pass's sun colour, the ambient, the sun's display value (the output stage's white).
SUN_COLOUR = [2.1185, 1.8782, 1.4687]
AMBIENT_TOP = [0.2357, 0.2432, 0.2470]
AMBIENT_BOTTOM = [0.1178, 0.1216, 0.1235]
WHITE = 0.7486
SUN_ELEVATION = 45.6
CUMULUS = e40o.CUMULUS
RAIN = "Kerbin-weather-rain"
CB = "Kerbin-cumulonimbus"
SCALES = (5000.0, 2000.0, 1000.0, 700.0, 500.0)
AMOUNTS = (0.35, 0.7, 1.0)
CLOSED = 0.92

# The march of the product (LayerFrame defaults, Medium step quality) and a converged one.
PRODUCT = dict(maxSteps=48, minStep=25.0, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1, stepGrowth=0.0002)
CONVERGED = dict(maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3, productCost=0, stepGrowth=0.0, refineBudget=0.1, refineFraction=0.0625)


def const_map(value):
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    name = "k%03d.r8" % v
    os.makedirs(INPUT, exist_ok=True)
    np.full(MAP_W * MAP_H, v, dtype=np.uint8).tofile(os.path.join(INPUT, name))
    return name


def camera(view, site=0):
    height, pitch = VIEWS[view]
    lon = math.radians(site * 10.0)
    pos = np.array([math.cos(lon), math.sin(lon), 0.0]) * (R + height)
    return pr.Camera(pos, 0.0, pitch, SIZE[0], SIZE[1], FOV)


def light(cam):
    return dict(sun=lay.sun_vector(cam.pos, 0.0, SUN_ELEVATION), sunColour=SUN_COLOUR, ambientTop=AMBIENT_TOP, ambientBottom=AMBIENT_BOTTOM)


def keys(pairs):
    return [dict(key=k, value=float(v)) for k, v in pairs]


def pair(jid, view, layer_list, march, tuning=None, site=0, **kw):
    """The lum and the aux job of one picture."""
    cam = camera(view, site)
    out = []
    for mode, suffix in (("lum", ""), ("aux", "-x")):
        args = dict(march)
        args.update(light(cam))
        args.update(kw)
        if mode == "aux" and "refineBudget" not in args:
            args["refineBudget"] = -1.0
        out.append(lay.job(jid + suffix, mode, cam, layer_list, SIZE[0], SIZE[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H, tuning=keys(tuning or []), **args))
    return out


def scale_keys(scale, amount, name=CUMULUS, spec=None):
    spec = spec or SPECS[name]
    return [("cloud.layerNoiseSizeScale." + name, scale / spec["scale"]), ("cloud.layerNoiseAmountScale." + name, amount / spec["noise"])]


SPECS = {s["name"]: s for s in e40o.kerbin_layers()}


def shape_jobs():
    jobs = []
    cu = SPECS[CUMULUS]
    for view in VIEWS:
        for scale in SCALES:
            for amount in AMOUNTS:
                jid = "sh-%s-n%04d-a%03d" % (view, int(scale), int(round(amount * 100)))
                jobs += pair(jid, view, [e40o.job_layer(cu, const_map(0.665), const_map(0.0))], PRODUCT, scale_keys(scale, amount))
        # The fair type humilis (type value 0.25) at the candidate numbers, and the candidate at the maps 0.5 and 0.8.
        jobs += pair("sh-%s-n0700-a100-humilis" % view, view, [e40o.job_layer(cu, const_map(0.665), const_map(0.25))], PRODUCT, scale_keys(700.0, 1.0))
        for m in (0.5, 0.8):
            jobs += pair("sh-%s-n0700-a100-m%03d" % (view, int(m * 100)), view, [e40o.job_layer(cu, const_map(m), const_map(0.0))], PRODUCT, scale_keys(700.0, 1.0))
            jobs += pair("sh-%s-n5000-a035-m%03d" % (view, int(m * 100)), view, [e40o.job_layer(cu, const_map(m), const_map(0.0))], PRODUCT, scale_keys(5000.0, 0.35))
    return jobs


# The march variants of the convergence group: name -> (march fields, extra tuning keys).
def march_variants():
    def prod(**kw):
        d = dict(PRODUCT)
        d.update(kw)
        return d
    return {
        "conv": (dict(CONVERGED), []),
        "prod": (prod(), []),
        "refine": (prod(refineBudget=0.5), []),
        "bisect3": (prod(refineBisect=3), []),
        "budget": (prod(refineStepBudget=0.5), []),
        "bisect3budget": (prod(refineBisect=3, refineStepBudget=0.5), []),
        "steps96": (prod(maxSteps=96), []),
        "sigma04": (prod(), [("cloud.layerSigmaScale." + CUMULUS, 0.4)]),
        "sigma04conv": (dict(CONVERGED), [("cloud.layerSigmaScale." + CUMULUS, 0.4)]),
    }


CV_SHAPES = {"cfg": (5000.0, 0.35), "cand": (700.0, 1.0)}


def convergence_jobs():
    jobs = []
    cu = SPECS[CUMULUS]
    for view in VIEWS:
        for shape, (scale, amount) in CV_SHAPES.items():
            for name, (march, extra) in march_variants().items():
                jid = "cv-%s-%s-%s" % (view, shape, name)
                jobs += pair(jid, view, [e40o.job_layer(cu, const_map(0.8 if shape == "cfg" else 0.665), const_map(0.0))], march, scale_keys(scale, amount) + extra)
            # Four more frames of the product's march (the frame number moves the dither): what a temporal mean of four frames would show.
            for frame in range(1, 4):
                cam = camera(view)
                args = dict(PRODUCT)
                args.update(light(cam))
                jobs.append(lay.job("cv-%s-%s-prod-f%d" % (view, shape, frame), "lum", cam, [e40o.job_layer(cu, const_map(0.8 if shape == "cfg" else 0.665), const_map(0.0))],
                                    SIZE[0], SIZE[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H, tuning=keys(scale_keys(scale, amount)), frameNumber=frame, **args))
    return jobs


MASK_SITES = 6
FOG_SITES = 8
PS_SITES = 4
PS_VALUES = (0.0, 0.4, 0.8)
PS_TYPES = (("sc", 0.0), ("hum", 0.25))
PS_SHAPE = (500.0, 1.0)
OLD_SHAPE = (5000.0, 0.35)


def mask_jobs():
    deck = e40o.job_layer(SPECS[RAIN], const_map(0.8595), const_map(0.229))
    tower = e40o.job_layer(SPECS[CB], const_map(0.6609), const_map(0.229))
    jobs = []
    for site in range(MASK_SITES):
        jobs += pair("mk-both-s%d" % site, "above", [deck, tower], PRODUCT, site=site)
        jobs += pair("mk-masked-s%d" % site, "above", [deck, tower], PRODUCT, site=site, layerMask=1)
        jobs += pair("mk-alone-s%d" % site, "above", [deck], PRODUCT, site=site)
    return jobs


def fog_jobs():
    jobs = []
    for name, typ in (("fog", 0.0), ("stratus", 0.5)):
        for site in range(FOG_SITES):
            jobs += pair("fg-%s-s%d" % (name, site), "ground", [e40o.job_layer(SPECS[RAIN], const_map(0.4056), const_map(typ))], PRODUCT, site=site)
    return jobs


def profile_jobs():
    """The switch layers.profileShape on the cumulus layer at the candidate noise numbers, two types, both views, four sites; and the cfg numbers at the same sites."""
    jobs = []
    cu = SPECS[CUMULUS]
    for view in VIEWS:
        for site in range(PS_SITES):
            # 'cfg' is the layer's numbers before the lead fix (5000 m, 0.35), named explicitly so that the row keeps its meaning after the cfg changed.
            jobs += pair("ps-%s-cfg-s%d" % (view, site), view, [e40o.job_layer(cu, const_map(0.665), const_map(0.0))], PRODUCT, scale_keys(*OLD_SHAPE), site=site)
            for tname, typ in PS_TYPES:
                for ps in PS_VALUES:
                    jobs += pair("ps-%s-%s-p%02d-s%d" % (view, tname, int(round(ps * 10)), site), view, [e40o.job_layer(cu, const_map(0.665), const_map(typ))], PRODUCT,
                                 scale_keys(*PS_SHAPE) + [("layers.profileShape", ps)], site=site)
    return jobs


# The colour group (third run): the light the pass is given. 'game' is K-T2d start 52's (the sun colour through Scatterer's air, KSP's flat grey ambient); 'grey' the sun pulled
# to grey of the same luminance; 'sky' the game's sun with the ambient set to the sky's and the sea's display colour read from the cloud-free frames of start 52
# (e40-52-ground_low-off: the sky at 46 / 24 / 4 degrees is 0.23 0.40 0.62 / 0.32 0.52 0.72 / 0.62 0.76 0.82, the sea 0.31 0.47 0.585); 'greysky' both.
SKY_AMBIENT = [0.33, 0.52, 0.72]
SEA_AMBIENT = [0.31, 0.47, 0.585]
SUN_LUMA = 0.2126 * SUN_COLOUR[0] + 0.7152 * SUN_COLOUR[1] + 0.0722 * SUN_COLOUR[2]
# The third run (shader before light.ambientColumnTop) gave, ground / above, r g b of the closed pixels: game 0.336 0.317 0.281 / 0.589 0.554 0.487 (b/r 0.83), sky THE SAME
# to three digits, grey and greysky 0.321 0.319 0.313 / 0.561 0.557 0.546 (b/r 0.97): the ambient did not reach the picture at all. Cause (READ): the analytic occlusion took the
# column above a sample to the layer's outer altitude (7.4 km for the cumulus layer), not to the cloud's top. FOURTH RUN, expected results written before it:
#   Z1  'gameold' and 'skyold' (the column to the layer's top, as before) agree within 0.005 in every channel in both views (the dead ambient, reproduced by the switch).
#   Z2  with the column to the type's top the sky ambient reaches the tops: above view, 'sky' against 'skyold': b/r higher by at least 0.03 and luma by at least 3 percent;
#       the ground view (undersides, the whole cloud above the sample) changes by less than the above view.
COLOUR_VARIANTS = {
    "gameold": dict(ambientLayerColumn=1),
    "skyold": dict(ambientTop=SKY_AMBIENT, ambientBottom=SEA_AMBIENT, ambientLayerColumn=1),
    "game": {},
    "sky": dict(ambientTop=SKY_AMBIENT, ambientBottom=SEA_AMBIENT),
    "skyg05": dict(ambientTop=SKY_AMBIENT, ambientBottom=SEA_AMBIENT, ambientGain=0.5),
    "grey": dict(sunColour=[SUN_LUMA] * 3),
    "greysky": dict(sunColour=[SUN_LUMA] * 3, ambientTop=SKY_AMBIENT, ambientBottom=SEA_AMBIENT),
}


def colour_jobs():
    jobs = []
    cu = SPECS[CUMULUS]
    for view in VIEWS:
        for name, extra in COLOUR_VARIANTS.items():
            jobs += pair("co-%s-%s" % (view, name), view, [e40o.job_layer(cu, const_map(0.665), const_map(0.25))], PRODUCT, scale_keys(*PS_SHAPE) + [("layers.profileShape", 0.8)],
                         site=1, **extra)
    return jobs


def build_jobs():
    return shape_jobs() + convergence_jobs() + mask_jobs() + fog_jobs() + profile_jobs() + colour_jobs()


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


# --------------------------------------------------------------------------------------------------------------------------------
# Reading
# --------------------------------------------------------------------------------------------------------------------------------
def plane(folder, jid, name):
    return np.load(os.path.join(folder, "%s-%s.npy" % (jid, name))).astype(np.float64)


def rgba(folder, jid):
    return np.stack([plane(folder, jid, c) for c in "rgba"], axis=-1)


def elevation(view):
    """The elevation of every pixel's ray above the horizon line of the camera, in degrees, as (h, w)."""
    cam = camera(view)
    d = cam.ray_directions().reshape(SIZE[1], SIZE[0], 3)
    up = cam.pos / np.linalg.norm(cam.pos)
    dip = math.acos(R / np.linalg.norm(cam.pos))
    return np.degrees(np.arcsin(np.clip(d @ up, -1.0, 1.0)) + dip)


def encode(c, white=WHITE, gamma=2.2):
    """CloudOutputStage.Encode per channel."""
    return white * np.power(np.maximum(c / white, 0.0), 1.0 / gamma)


SKY_ZENITH = np.array([0.16, 0.33, 0.62])
SKY_HORIZON = np.array([0.62, 0.78, 0.88])
SEA = np.array([0.10, 0.20, 0.33])


def backdrop(view):
    elev = elevation(view)
    t = np.clip(elev / 60.0, 0.0, 1.0)[..., None] ** 0.5
    sky = SKY_HORIZON * (1.0 - t) + SKY_ZENITH * t
    return np.where((elev > 0.0)[..., None], sky, SEA)


def natural(folder, jid, view):
    """The display picture of a lum job: the cloud through the game's display stage over the backdrop, as (h, w, 3) in 0..1."""
    m = rgba(folder, jid)
    a = np.clip(m[..., 3:4], 0.0, 1.0)
    colour = encode(m[..., :3] / np.maximum(a, 1e-4))
    return np.clip(colour * a + backdrop(view) * (1.0 - a), 0.0, 1.0)


def luma(rgb):
    return 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]


def save_png(path, rgb):
    from PIL import Image
    Image.fromarray(np.clip(np.rint(rgb * 255.0), 0, 255).astype(np.uint8), "RGB").save(path)


def heat(tau):
    """The optical depth as a false colour (blue thin, green 1, red 30 and more), black where there is none."""
    x = np.clip((np.log10(np.maximum(tau, 1e-4)) + 1.0) / 2.5, 0.0, 1.0)[..., None]
    lo, mid, hi = np.array([0.05, 0.05, 0.6]), np.array([0.2, 0.8, 0.4]), np.array([0.95, 0.2, 0.1])
    c = np.where(x < 0.5, lo + (mid - lo) * (x * 2.0), mid + (hi - mid) * ((x - 0.5) * 2.0))
    return np.where((tau > 1e-3)[..., None], c, 0.0)


def transitions(mask, rows):
    """The mean number of cloud/clear changes along the given picture rows."""
    m = mask[rows]
    return float((m[:, 1:] != m[:, :-1]).sum(axis=1).mean())


def structure(alpha, view):
    """(cloud share, transitions per row) over the region of the view that looks at the layer: the rows below the horizon for the above view, the rows above it for the ground view."""
    elev = elevation(view)[:, SIZE[0] // 2]
    rows = np.where(elev < -3.0)[0] if view == "above" else np.where(elev > 3.0)[0]
    mask = alpha >= 0.6
    return float(mask[rows].mean()), transitions(mask, rows)


def picture_structure(path, size=SIZE):
    """The same two numbers for a picture of the game (the TVC reference, our frames): a pixel is cloud where its colour is near grey (saturation under 0.18) and
    brighter than 0.35; over the lower 60 percent of the picture (the above pose looks 25 degrees down)."""
    from PIL import Image
    im = np.asarray(Image.open(path).convert("RGB").resize(size, Image.BILINEAR), dtype=np.float64) / 255.0
    mx, mn = im.max(axis=-1), im.min(axis=-1)
    mask = ((mx - mn) / np.maximum(mx, 1e-3) < 0.18) & (mx > 0.35)
    rows = np.arange(int(size[1] * 0.4), size[1])
    return float(mask[rows].mean()), transitions(mask, rows)


def hf(l, mask):
    """The grain measure: mean |3 x 3 Laplacian| of the luma over the masked pixels (whose four neighbours are masked too) divided by their mean luma."""
    lap = np.abs(4.0 * l[1:-1, 1:-1] - l[:-2, 1:-1] - l[2:, 1:-1] - l[1:-1, :-2] - l[1:-1, 2:])
    inner = mask[1:-1, 1:-1] & mask[:-2, 1:-1] & mask[2:, 1:-1] & mask[1:-1, :-2] & mask[1:-1, 2:]
    if inner.sum() < 50:
        return float("nan")
    return float(lap[inner].mean() / max(l[1:-1, 1:-1][inner].mean(), 1e-6))


def main_repo():
    return e40o.main_repo()


TVC_ABOVE = os.path.join(".local", "work", "series-pictures", "K-T2", "t09-tvc1707-above-tvc-1707x960-natural.png")
OURS_ABOVE = os.path.join(".local", "work", "series-pictures", "E40", "e40-52-above_low-kC0-1707x960-natural-half.png")


def collect(ctx):
    folder = ctx.folder("layers")
    out_dir = os.path.join(main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines, results = [], [], {}

    def pictures(jid, view, name):
        nat = natural(folder, jid, view)
        save_png(os.path.join(out_dir, "e40-fl-%s.png" % name), nat)
        tau = plane(folder, jid + "-x", "tau")
        save_png(os.path.join(out_dir, "e40-fl-%s-aid.png" % name), heat(tau))
        return nat

    # ---- the shape ----
    lines.append("SHAPE (cumulus layer, map 0.665, type 0): cloud share / cloud-clear transitions per picture row")
    shape = {}
    for view in VIEWS:
        for scale in SCALES:
            row = []
            for amount in AMOUNTS:
                jid = "sh-%s-n%04d-a%03d" % (view, int(scale), int(round(amount * 100)))
                share, trans = structure(plane(folder, jid + "-x", "alpha"), view)
                shape[jid] = (share, trans, float(plane(folder, jid + "-x", "steps").mean()))
                row.append("a%.2f %.2f / %4.1f" % (amount, share, trans))
                metrics.append(Metric.info("e40fl.%s.share" % jid, share))
                metrics.append(Metric.info("e40fl.%s.transitions" % jid, trans))
                pictures(jid, view, jid)
            lines.append("   %-6s scale %5.0f m: %s" % (view, scale, "   ".join(row)))
        for extra in ("n0700-a100-humilis", "n0700-a100-m050", "n0700-a100-m080", "n5000-a035-m050", "n5000-a035-m080"):
            jid = "sh-%s-%s" % (view, extra)
            share, trans = structure(plane(folder, jid + "-x", "alpha"), view)
            shape[jid] = (share, trans, float(plane(folder, jid + "-x", "steps").mean()))
            lines.append("   %-6s %-22s %.2f / %4.1f" % (view, extra, share, trans))
            metrics.append(Metric.info("e40fl.%s.share" % jid, share))
            metrics.append(Metric.info("e40fl.%s.transitions" % jid, trans))
            pictures(jid, view, jid)
    for label, rel in (("TVC reference (t09-tvc1707-above)", TVC_ABOVE), ("our game frame (e40-52-above_low-kC0)", OURS_ABOVE)):
        path = os.path.join(main_repo(), rel)
        if os.path.isfile(path):
            share, trans = picture_structure(path)
            lines.append("   picture %-40s %.2f / %4.1f (grey-and-bright pixels of the lower 60 percent)" % (label, share, trans))
            metrics.append(Metric.info("e40fl.picture.%s.transitions" % ("tvc" if "TVC" in label else "ours"), trans))
            results["picture-" + ("tvc" if "TVC" in label else "ours")] = (share, trans)
        else:
            lines.append("   picture %s: not found (%s)" % (label, path))
    cfg_t = shape["sh-above-n5000-a035"][1]
    cand_t = shape["sh-above-n0700-a100"][1]
    metrics.append(Metric.info("e40fl.X1.cfg_transitions_above", cfg_t))
    metrics.append(Metric.info("e40fl.X1.cand_transitions_above", cand_t))
    metrics.append(Metric.info("e40fl.X1.cand_share_above", shape["sh-above-n0700-a100"][0]))
    results["shape"] = shape

    # ---- the profile shape (second run) ----
    lines.append("PROFILE SHAPE (cumulus layer at %d m / %.1f, map 0.665; mean over %d sites): cloud share / transitions per row / top relief in m (above view: standard deviation"
                 " of the first hit's altitude over the closed pixels)" % (PS_SHAPE[0], PS_SHAPE[1], PS_SITES))
    prof = {}

    def site_numbers(jid_format, view):
        shares, trans, relief = [], [], []
        for site in range(PS_SITES):
            jid = jid_format % site
            alpha = plane(folder, jid + "-x", "alpha")
            s, t = structure(alpha, view)
            shares.append(s)
            trans.append(t)
            if view == "above":
                cam = camera(view, site)
                d = cam.ray_directions().reshape(SIZE[1], SIZE[0], 3)
                first = plane(folder, jid + "-x", "first")
                closed = (alpha > 0.97) & (first > 0.0)
                if closed.sum() > 200:
                    p = cam.pos[None, :] + d[closed] * first[closed][:, None]
                    relief.append(float(np.std(np.linalg.norm(p, axis=1) - R)))
        return float(np.mean(shares)), float(np.mean(trans)), (float(np.mean(relief)) if relief else float("nan"))

    for view in VIEWS:
        prof["%s-cfg" % view] = site_numbers("ps-" + view + "-cfg-s%d", view)
        lines.append("   %-6s cfg (5000 m / 0.35)      %.2f / %4.1f / %6.1f" % ((view,) + prof["%s-cfg" % view]))
        pictures("ps-%s-cfg-s1" % view, view, "ps-%s-cfg-s1" % view)
        for tname, _ in PS_TYPES:
            for ps in PS_VALUES:
                key = "%s-%s-p%02d" % (view, tname, int(round(ps * 10)))
                prof[key] = site_numbers("ps-" + key + "-s%d", view)
                lines.append("   %-6s %-4s profileShape %.1f    %.2f / %4.1f / %6.1f" % ((view, tname, ps) + prof[key]))
                for i, nm in enumerate(("share", "transitions", "relief_m")):
                    metrics.append(Metric.info("e40fl.ps.%s.%s" % (key, nm), prof[key][i]))
                for site in (0, 1):
                    pictures("ps-%s-s%d" % (key, site), view, "ps-%s-s%d" % (key, site))
    flat, domed = prof["above-sc-p00"], prof["above-sc-p08"]
    metrics.append(Metric.info("e40fl.Y1.flat_top_relief_m", flat[2]))
    metrics.append(Metric.info("e40fl.Y1.relief_ratio", domed[2] / max(flat[2], 1e-6)))
    metrics.append(Metric.reference("e40fl.Y1.share_fall", flat[0] - domed[0], 0.175, rel=0.0, floor=0.125))
    results["profile"] = prof

    # ---- the march against the converged march ----
    lines.append("MARCH (luma of the closed pixels over the converged march's / grain hf / hf over the converged one's / mean steps per pixel)")
    conv = {}
    for view in VIEWS:
        for shp in CV_SHAPES:
            ref_id = "cv-%s-%s-conv" % (view, shp)
            ref_nat = natural(folder, ref_id, view)
            ref_alpha = plane(folder, ref_id + "-x", "alpha")
            for name in march_variants():
                jid = "cv-%s-%s-%s" % (view, shp, name)
                base_id = "cv-%s-%s-sigma04conv" % (view, shp) if name == "sigma04" else ref_id
                base_nat = natural(folder, base_id, view) if base_id != ref_id else ref_nat
                base_alpha = plane(folder, base_id + "-x", "alpha") if base_id != ref_id else ref_alpha
                m = rgba(folder, jid)
                nat = natural(folder, jid, view)
                mask = (m[..., 3] > 0.97) & (base_alpha > 0.97)
                # The radiance (before the display stage) decides the bias; the display luma the grain the eye sees.
                rad = luma(m[..., :3] / np.maximum(m[..., 3:4], 1e-4))
                bm = rgba(folder, base_id)
                brad = luma(bm[..., :3] / np.maximum(bm[..., 3:4], 1e-4))
                ratio = float(rad[mask].mean() / max(brad[mask].mean(), 1e-9)) if mask.sum() > 50 else float("nan")
                g, bg = hf(luma(nat), mask), hf(luma(base_nat), mask)
                steps = float(plane(folder, jid + "-x", "steps").mean())
                conv[jid] = (ratio, g, g / bg if bg and bg == bg else float("nan"), steps, int(mask.sum()))
                lines.append("   %-34s %.3f / %.4f / %5.2f / %6.1f   (%d pixels)" % (jid, ratio, g, conv[jid][2], steps, mask.sum()))
                metrics.append(Metric.info("e40fl.%s.radiance_ratio" % jid, ratio))
                metrics.append(Metric.info("e40fl.%s.hf" % jid, g))
                metrics.append(Metric.info("e40fl.%s.steps" % jid, steps))
                pictures(jid, view, jid)
            # The mean of four frames of the product's march.
            acc = rgba(folder, "cv-%s-%s-prod" % (view, shp))
            for frame in range(1, 4):
                acc = acc + rgba(folder, "cv-%s-%s-prod-f%d" % (view, shp, frame))
            acc /= 4.0
            a = np.clip(acc[..., 3:4], 0.0, 1.0)
            nat4 = np.clip(encode(acc[..., :3] / np.maximum(a, 1e-4)) * a + backdrop(view) * (1.0 - a), 0.0, 1.0)
            mask = (acc[..., 3] > 0.97) & (ref_alpha > 0.97)
            g4 = hf(luma(nat4), mask)
            lines.append("   %-34s   -   / %.4f (mean of 4 frames of the product's march)" % ("cv-%s-%s-prod-x4" % (view, shp), g4))
            metrics.append(Metric.info("e40fl.cv-%s-%s-prod-x4.hf" % (view, shp), g4))
            save_png(os.path.join(out_dir, "e40-fl-cv-%s-%s-prod-x4.png" % (view, shp)), nat4)
    p = conv["cv-above-cfg-prod"]
    metrics.append(Metric.info("e40fl.X2.prod_radiance_ratio_above_cfg", p[0]))
    metrics.append(Metric.info("e40fl.X2.prod_grain_over_converged_above_cfg", p[2]))
    r = conv["cv-above-cfg-refine"]
    metrics.append(Metric.info("e40fl.X2.refine_radiance_ratio_above_cfg", r[0]))
    metrics.append(Metric.info("e40fl.X2.refine_grain_over_converged_above_cfg", r[2]))
    results["march"] = conv

    # ---- the mask and the light ----
    lines.append("MASK (rain deck 0.8595 / 0.229 and cumulonimbus 0.6609 / 0.229 from above): luma p5 / p50 of the deck's closed pixels")
    mk = {}
    worst_gap = 0.0
    for site in range(MASK_SITES):
        deck_alpha = plane(folder, "mk-alone-s%d-x" % site, "alpha")
        row = {}
        for name in ("both", "masked", "alone"):
            jid = "mk-%s-s%d" % (name, site)
            nat = pictures(jid, "above", jid)
            m = rgba(folder, jid)
            mask = (m[..., 3] > 0.97) & (deck_alpha > 0.97)
            if name == "both":
                mask &= plane(folder, "mk-masked-s%d-x" % site, "alpha") > 0.97
            if mask.sum() < 200:
                row[name] = (float("nan"), float("nan"), float("nan"))
                continue
            l = luma(nat)[mask]
            p5, p50 = float(np.percentile(l, 5)), float(np.percentile(l, 50))
            row[name] = (p5, p50, p5 / p50)
        mk[site] = row
        tower_share = float((plane(folder, "mk-both-s%d-x" % site, "tau") - plane(folder, "mk-masked-s%d-x" % site, "tau") > 0.5).mean())
        lines.append("   site %d (towers in %.2f of the picture): p5 / p50 both %.3f  masked %.3f  alone %.3f" % (site, tower_share, row["both"][2], row["masked"][2], row["alone"][2]))
        gap = row["alone"][2] - row["masked"][2]
        if gap == gap:
            worst_gap = max(worst_gap, gap)
    metrics.append(Metric.info("e40fl.Y3.mask_shading_gap", worst_gap))
    results["mask"] = {str(k): v for k, v in mk.items()}

    # ---- the fog at no precipitation ----
    lines.append("FOG (rain deck at the low pick, map 0.4056, from 264 m, %d sites): share of the picture below the horizon with tau >= 0.92; the same above the horizon" % FOG_SITES)
    fg = {}
    elev = elevation("ground")
    below, above = elev < -0.5, elev > 0.5
    for name in ("fog", "stratus"):
        b, a = [], []
        for site in range(FOG_SITES):
            jid = "fg-%s-s%d" % (name, site)
            tau = plane(folder, jid + "-x", "tau")
            b.append(float((tau[below] >= CLOSED).mean()))
            a.append(float((tau[above] >= CLOSED).mean()))
        fg[name] = (float(np.mean(b)), float(np.mean(a)), b, a)
        best = int(np.argmax(b if name == "fog" else a))
        pictures("fg-%s-s%d" % (name, best), "ground", "fg-%s-s%d" % (name, best))
        lines.append("   type %-8s below %.3f (per site %s)   above %.3f (per site %s)" % (name, fg[name][0], " ".join("%.2f" % v for v in b), fg[name][1], " ".join("%.2f" % v for v in a)))
        metrics.append(Metric.info("e40fl.fg.%s.below" % name, fg[name][0]))
        metrics.append(Metric.info("e40fl.fg.%s.above" % name, fg[name][1]))
    metrics.append(Metric.at_least("e40fl.Y2.fog_below_horizon", fg["fog"][0], 0.15))
    metrics.append(Metric.info("e40fl.Y2.stratus_below_horizon", fg["stratus"][0]))
    results["fog"] = fg

    # ---- the colour (third run; no expectation judged: the colour is the owner's choice, the rows are the evidence) ----
    lines.append("COLOUR (cumulus humilis at 500 m / 1.0, profileShape 0.8, site 1): display colour r g b of the closed pixels (median), blue over red")
    co = {}
    for view in VIEWS:
        for name in COLOUR_VARIANTS:
            jid = "co-%s-%s" % (view, name)
            nat = pictures(jid, view, jid)
            closed = plane(folder, jid + "-x", "alpha") > 0.97
            med = [float(np.median(nat[..., c][closed])) for c in range(3)]
            co[jid] = med
            lines.append("   %-18s %.3f %.3f %.3f   b/r %.2f" % (jid, med[0], med[1], med[2], med[2] / max(med[0], 1e-6)))
            metrics.append(Metric.info("e40fl.%s.blue_over_red" % jid, med[2] / max(med[0], 1e-6)))
            metrics.append(Metric.info("e40fl.%s.luma" % jid, 0.2126 * med[0] + 0.7152 * med[1] + 0.0722 * med[2]))
    results["colour"] = co

    def co_luma(jid):
        return 0.2126 * co[jid][0] + 0.7152 * co[jid][1] + 0.0722 * co[jid][2]

    dead = max(abs(co["co-%s-gameold" % v][c] - co["co-%s-skyold" % v][c]) for v in VIEWS for c in range(3))
    metrics.append(Metric.at_most("e40fl.Z1.old_column_sky_minus_game", dead, 0.005))
    a_new, a_old = co["co-above-sky"], co["co-above-skyold"]
    # Z2 missed in the fourth run (b/r +0.0008, luma x1.001; the gain 0.5 the same): with the column corrected the occlusion exp(-1.28 * 0.05 per m * depth) still lets the
    # sky reach only the first 15 m of a cloud, and the march's samples lie deeper. The rows stay as the record; the ambient needs a term that enters like scattered light.
    metrics.append(Metric.info("e40fl.Z2.above_blue_over_red_gain", a_new[2] / a_new[0] - a_old[2] / a_old[0]))
    metrics.append(Metric.info("e40fl.Z2.above_luma_ratio", co_luma("co-above-sky") / co_luma("co-above-skyold")))
    metrics.append(Metric.info("e40fl.Z2.ground_luma_ratio", co_luma("co-ground-sky") / co_luma("co-ground-skyold")))

    results["lines"] = lines
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    print("pictures: %s (e40-fl-*.png, natural and -aid)" % out_dir)
    return metrics


def stages():
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")) or os.path.getmtime(os.path.join(INPUT, "jobs.json")) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("layers", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False, timeout=1800)]


register_series("e40-fl", stages(), collect, exclusive=False, who="e40-fl")


def selftest():
    jobs = build_jobs()
    ids = [j["id"] for j in jobs]
    assert len(ids) == len(set(ids)), "job ids repeat"
    assert "sh-above-n0700-a100" in ids and "cv-ground-cand-bisect3-x" in ids and "mk-masked-s5" in ids and "fg-fog-s7-x" in ids and "ps-above-hum-p08-s3" in ids
    masked = [j for j in jobs if j["id"] == "mk-masked-s2"][0]
    assert masked["layerMask"] == 1 and len(masked["layers"]) == 2
    shaped = dict((d["key"], d["value"]) for d in [j for j in jobs if j["id"] == "ps-ground-sc-p04-s0"][0]["tuning"])
    assert abs(shaped["layers.profileShape"] - 0.4) < 1e-9 and abs(shaped["cloud.layerNoiseSizeScale." + CUMULUS] * SPECS[CUMULUS]["scale"] - 500.0) < 1e-6
    k = dict((d["key"], d["value"]) for d in [j for j in jobs if j["id"] == "sh-above-n0700-a100"][0]["tuning"])
    cu = SPECS[CUMULUS]
    assert abs(k["cloud.layerNoiseSizeScale." + CUMULUS] * cu["scale"] - 700.0) < 1e-6 and abs(k["cloud.layerNoiseAmountScale." + CUMULUS] * cu["noise"] - 1.0) < 1e-9
    assert abs(encode(np.array([WHITE]))[0] - WHITE) < 1e-12
    m = np.zeros((4, 8), dtype=bool)
    m[:, 2:4] = True
    assert transitions(m, np.arange(4)) == 2.0
    elev = elevation("above")
    assert elev[0, 320] > elev[-1, 320] and elev[-1, 320] < -40.0
    print("selftest passed (%d jobs)" % len(jobs))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--write", action="store_true", help="write the jobs and exit")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.write:
        print(len(write_inputs()), "jobs")
