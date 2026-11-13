"""E40 grain investigation of the own clouds: where the per-pixel speckle of the layer march comes from and what removes it (player series e40-gn).

    python tools/build_weather_player.py --series e40-gn

The scene is the cumulus layer of Clouds-Kerbin.cfg (the numbers after f8f8cafe: 500 m cells, amount 1) with the light of K-T2d start 52, in two views (from the
ground at 264 m looking 18 degrees up, from above at 8364 m looking 25 degrees down), marched at 854 x 480: the size of the game's half-size march target at
1707 x 960, so a pixel here is a march texel there.

Every variant is marched for several frame numbers (the frame number moves the per-pixel step offset). The measures, all on the DISPLAY value (the march's radiance
through CloudOutputStage, laid over a smooth backdrop of sky and sea, 0..1) and all over the same masks, which come from the converged march of the view:

  tstd     temporal noise: the standard deviation over the frames of a pixel's display luma, mean over the mask. 1/255 is 0.0039.
  hf       spatial grain of one frame: mean |3 x 3 Laplacian| of the display luma over the mask; hfx is hf minus the converged picture's hf over the same mask (the
           structure the cloud really has) and minus nothing else (the backdrop is smooth: its hf is below 0.0005, row 'backdrop').
  bias     mean over the frames minus the converged picture, mean over the mask (signed), and rms of that difference per pixel (rmsb): what a perfect temporal mean
           would still get wrong.
  astd     temporal standard deviation of the alpha (hit or miss of the ray), mean over the mask.
  steps    density reads per pixel (aux), the cost proxy of the player; the GPU ms are the game's and the cost scene's.

Masks: 'closed' (converged alpha above 0.97), 'rim' (converged alpha above 0.03, within 3 pixels of a pixel with alpha below 0.5 or itself between 0.03 and 0.97),
'cloud' (converged alpha above 0.03).
"""
import argparse
import json
import math
import os
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
INPUT = os.path.join(BUILD, "e40gn-in")
RESULTS = os.path.join(BUILD, "e40gn-results.json")
R = 600000.0
MAP_W, MAP_H = 8, 4
SIZE = (854, 480)
FOV = 60.0
VIEWS = {"ground": (264.0, 18.0), "above": (8364.0, -25.0)}
SITE = 1
SUN_COLOUR = [2.1185, 1.8782, 1.4687]
AMBIENT_TOP = [0.2357, 0.2432, 0.2470]
AMBIENT_BOTTOM = [0.1178, 0.1216, 0.1235]
WHITE = 0.7486
SUN_ELEVATION = 45.6
CUMULUS = e40o.CUMULUS
MAP_VALUE = 0.665

# The march of the product (LayerFrame defaults, Medium step quality, OwnCloudOptions) and a converged one.
PRODUCT = dict(maxSteps=48, minStep=25.0, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1, stepGrowth=0.0002)
CONVERGED = dict(maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3, productCost=0, stepGrowth=0.0, refineBudget=0.1, refineFraction=0.0625)


def prod(**kw):
    d = dict(PRODUCT)
    d.update(kw)
    return d


# name -> (march fields, tuning keys, frames). The first block is the diagnosis (existing switches only); the second the candidates (switches of this package).
def variants():
    v = {
        "conv": (dict(CONVERGED), [], 1),
        "prod": (prod(), [], 8),
        "j0": (prod(jitter=0.0), [], 1),
        "nolight": (prod(costDebug=1), [], 4),
        "ign": (prod(ignDither=1), [], 8),
        "steps4x": (prod(maxSteps=192, minStep=6.0), [], 4),
        "bis6": (prod(refineBisect=6, refineStepBudget=0.5, refineFraction=0.0625), [], 4),
        "refine": (prod(refineBudget=0.5, refineFraction=0.0625), [], 4),
        "sigma01": (prod(), [("cloud.layerSigmaScale." + CUMULUS, 0.1)], 4),
        "sigma01conv": (dict(CONVERGED), [("cloud.layerSigmaScale." + CUMULUS, 0.1)], 1),
    }
    v.update(CANDIDATES)
    return v


# The candidates of this package (filled as the switches are built; each is the product's march plus the named tuning keys).
CANDIDATES = {
    # The entry ramp (LayerFrame.EntryRamp) with 3, 4 and 6 bisection steps; with the midpoint rule instead of the dither; on a grid of twice the steps that leaps
    # over empty samples (the same density reads in clear air, a finer search where the clouds are).
    "ramp3": (prod(refineBisect=3, entryRamp=1), [], 4),
    "ramp4": (prod(refineBisect=4, entryRamp=1), [], 8),
    "ramp6": (prod(refineBisect=6, entryRamp=1), [], 8),
    "ramp6j0": (prod(refineBisect=6, entryRamp=1, jitter=0.0), [], 1),
    "ramp4bud": (prod(refineBisect=4, entryRamp=1, refineStepBudget=0.5), [], 4),
    # The bisection alone (no ramp), for the split of the two.
    "bis4": (prod(refineBisect=4), [], 4),
    # A finer grid that leaps over empty samples: 2 x and 4 x the grid points, the leap as long as the product's grid step at most.
    "leap2x": (prod(maxSteps=96, costLeap=2.0), [], 4),
    "leap4x": (prod(maxSteps=192, costLeap=4.0), [], 8),
    "ramp4leap2x": (prod(refineBisect=4, entryRamp=1, maxSteps=96, costLeap=2.0), [], 4),
    "ramp4leap4x": (prod(refineBisect=4, entryRamp=1, maxSteps=192, costLeap=4.0), [], 8),
    "ramp3leap4x": (prod(refineBisect=3, entryRamp=1, maxSteps=192, costLeap=4.0), [], 4),
    "ramp4leap8x": (prod(refineBisect=4, entryRamp=1, maxSteps=256, costLeap=6.0), [], 4),
    # The finer grid with the leap and the step inside a cloud as long as on the coarse grid (LayerFrame.CloudStep).
    "leap4xc": (prod(maxSteps=192, costLeap=4.0, cloudStep=4.0), [], 8),
    "leap5xc": (prod(maxSteps=256, costLeap=5.0, cloudStep=5.33), [], 4),
    "ramp3leap4xc": (prod(refineBisect=3, entryRamp=1, maxSteps=192, costLeap=4.0, cloudStep=4.0), [], 4),
}

# The converged picture a variant is judged against (default: 'conv').
BASE = {"sigma01": "sigma01conv", "sigma01conv": "sigma01conv"}


def const_map(value):
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    name = "k%03d.r8" % v
    os.makedirs(INPUT, exist_ok=True)
    np.full(MAP_W * MAP_H, v, dtype=np.uint8).tofile(os.path.join(INPUT, name))
    return name


def camera(view, site=SITE):
    height, pitch = VIEWS[view]
    lon = math.radians(site * 10.0)
    pos = np.array([math.cos(lon), math.sin(lon), 0.0]) * (R + height)
    return pr.Camera(pos, 0.0, pitch, SIZE[0], SIZE[1], FOV)


def keys(pairs):
    return [dict(key=k, value=float(v)) for k, v in pairs]


def jid_of(view, name, frame=None):
    return "gn-%s-%s" % (view, name) if frame is None else "gn-%s-%s-f%d" % (view, name, frame)


def build_jobs():
    spec = {s["name"]: s for s in e40o.kerbin_layers()}[CUMULUS]
    jobs = []
    for view in VIEWS:
        cam = camera(view)
        light = dict(sun=lay.sun_vector(cam.pos, 0.0, SUN_ELEVATION), sunColour=SUN_COLOUR, ambientTop=AMBIENT_TOP, ambientBottom=AMBIENT_BOTTOM)
        layer = [e40o.job_layer(spec, const_map(MAP_VALUE), const_map(0.0))]
        for name, (march, tuning, frames) in variants().items():
            args = dict(march)
            args.update(light)
            for frame in range(frames):
                jobs.append(lay.job(jid_of(view, name, frame), "lum", cam, layer, SIZE[0], SIZE[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H, tuning=keys(tuning),
                                    frameNumber=frame, **args))
            aux = dict(args)
            if "refineBudget" not in aux:
                aux["refineBudget"] = -1.0
            jobs.append(lay.job(jid_of(view, name) + "-x", "aux", cam, layer, SIZE[0], SIZE[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H, tuning=keys(tuning), **aux))
    return jobs


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


# --------------------------------------------------------------------------------------------------------------------------------
# Reading and measuring
# --------------------------------------------------------------------------------------------------------------------------------
def plane(folder, jid, name):
    return np.load(os.path.join(folder, "%s-%s.npy" % (jid, name))).astype(np.float64)


def rgba(folder, jid):
    return np.stack([plane(folder, jid, c) for c in "rgba"], axis=-1)


def elevation(view):
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
_BACKDROP = {}


def backdrop(view):
    if view not in _BACKDROP:
        elev = elevation(view)
        t = np.clip(elev / 60.0, 0.0, 1.0)[..., None] ** 0.5
        sky = SKY_HORIZON * (1.0 - t) + SKY_ZENITH * t
        # A soft horizon (2 degrees) so that the backdrop itself has no edge the grain measure would count.
        w = np.clip((elev + 1.0) / 2.0, 0.0, 1.0)[..., None]
        _BACKDROP[view] = sky * w + SEA * (1.0 - w)
    return _BACKDROP[view]


def display(m, view):
    """The display picture of a march result (premultiplied radiance, alpha): the cloud through the game's display stage over the backdrop, (h, w, 3) in 0..1."""
    a = np.clip(m[..., 3:4], 0.0, 1.0)
    colour = encode(m[..., :3] / np.maximum(a, 1e-4))
    return np.clip(colour * a + backdrop(view) * (1.0 - a), 0.0, 1.0)


def luma(rgb):
    return 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]


def save_png(path, rgb):
    from PIL import Image
    Image.fromarray(np.clip(np.rint(rgb * 255.0), 0, 255).astype(np.uint8), "RGB").save(path)


def heat(tau):
    x = np.clip((np.log10(np.maximum(tau, 1e-4)) + 1.0) / 2.5, 0.0, 1.0)[..., None]
    lo, mid, hi = np.array([0.05, 0.05, 0.6]), np.array([0.2, 0.8, 0.4]), np.array([0.95, 0.2, 0.1])
    c = np.where(x < 0.5, lo + (mid - lo) * (x * 2.0), mid + (hi - mid) * ((x - 0.5) * 2.0))
    return np.where((tau > 1e-3)[..., None], c, 0.0)


def dilate(mask, n):
    out = mask.copy()
    for _ in range(n):
        p = np.pad(out, 1, mode="edge")
        out = p[1:-1, 1:-1] | p[:-2, 1:-1] | p[2:, 1:-1] | p[1:-1, :-2] | p[1:-1, 2:]
    return out


def masks(alpha):
    """The three masks of a view from the converged alpha."""
    cloud = alpha > 0.03
    closed = alpha > 0.97
    rim = cloud & (dilate(alpha < 0.5, 3) | (alpha < 0.97))
    return {"closed": closed, "rim": rim, "cloud": cloud}


def lap(l):
    """|3 x 3 Laplacian| of a picture, zero on the border."""
    out = np.zeros_like(l)
    out[1:-1, 1:-1] = np.abs(4.0 * l[1:-1, 1:-1] - l[:-2, 1:-1] - l[2:, 1:-1] - l[1:-1, :-2] - l[1:-1, 2:])
    return out


def measure(frames_rgba, ref_rgba, view, mask_set):
    """The measures of one variant: frames_rgba is a list of (h, w, 4) march results, ref_rgba the converged one. Returns {mask: {measure: value}}."""
    ls = np.stack([luma(display(m, view)) for m in frames_rgba])
    al = np.stack([np.clip(m[..., 3], 0.0, 1.0) for m in frames_rgba])
    ref = luma(display(ref_rgba, view))
    ref_lap = lap(ref)
    tstd = ls.std(axis=0) if len(frames_rgba) > 1 else np.full(ref.shape, np.nan)
    astd = al.std(axis=0) if len(frames_rgba) > 1 else np.full(ref.shape, np.nan)
    mean = ls.mean(axis=0)
    laps = np.stack([lap(l) for l in ls]).mean(axis=0)
    out = {}
    for name, mask in mask_set.items():
        inner = mask.copy()
        inner[0, :] = inner[-1, :] = False
        inner[:, 0] = inner[:, -1] = False
        if inner.sum() < 50:
            out[name] = None
            continue
        diff = (mean - ref)[inner]
        out[name] = dict(tstd=float(tstd[inner].mean()), astd=float(astd[inner].mean()), hf=float(laps[inner].mean()), hfx=float(laps[inner].mean() - ref_lap[inner].mean()),
                         bias=float(diff.mean()), rmsb=float(np.sqrt((diff * diff).mean())), pixels=int(inner.sum()))
    return out


def main_repo():
    return e40o.main_repo()


def crop_box(view):
    """A 240 x 135 crop with the most rim pixels' neighbourhood (chosen once per view from the converged alpha; stored by collect)."""
    return _CROPS.get(view)


_CROPS = {}


def pick_crop(alpha, w=240, h=135):
    m = masks(alpha)["rim"].astype(np.float64)
    c = np.cumsum(np.cumsum(m, axis=0), axis=1)
    c = np.pad(c, ((1, 0), (1, 0)))
    best, box = -1.0, (0, 0)
    for y in range(0, alpha.shape[0] - h, 15):
        for x in range(0, alpha.shape[1] - w, 16):
            s = c[y + h, x + w] - c[y, x + w] - c[y + h, x] + c[y, x]
            if s > best:
                best, box = s, (x, y)
    return box[0], box[1], w, h


def enlarge(rgb, k=4):
    return np.repeat(np.repeat(rgb, k, axis=0), k, axis=1)


def collect(ctx):
    folder = ctx.folder("layers")
    out_dir = os.path.join(main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines, results = [], [], {}
    lines.append("GRAIN (display values 0..1; 1/255 = 0.0039). Columns per mask closed | rim: tstd  hfx  bias  rmsb ; then astd over the cloud mask, steps per pixel")
    for view in VIEWS:
        refs = {}
        for name, (march, tuning, frames) in variants().items():
            base = BASE.get(name, "conv")
            if base not in refs:
                refs[base] = rgba(folder, jid_of(view, base, 0))
            ref = refs[base]
            mask_set = masks(np.clip(ref[..., 3], 0.0, 1.0))
            if view not in _CROPS:
                _CROPS[view] = pick_crop(np.clip(refs["conv"][..., 3], 0.0, 1.0))
            fr = [rgba(folder, jid_of(view, name, f)) for f in range(frames)]
            m = measure(fr, ref, view, mask_set)
            steps = float(plane(folder, jid_of(view, name) + "-x", "steps").mean())
            results["%s-%s" % (view, name)] = dict(measures=m, steps=steps, frames=frames)

            def cell(mask):
                d = m[mask]
                return "   -   " if d is None else "%.4f %.4f %+.4f %.4f" % (d["tstd"], d["hfx"], d["bias"], d["rmsb"])

            astd = m["cloud"]["astd"] if m["cloud"] else float("nan")
            lines.append("   %-7s %-14s %s | %s ; astd %.4f  steps %6.1f" % (view, name, cell("closed"), cell("rim"), astd, steps))
            for mask in ("closed", "rim"):
                if m[mask] is None:
                    continue
                for key in ("tstd", "hfx", "bias", "rmsb"):
                    metrics.append(Metric.info("e40gn.%s.%s.%s.%s" % (view, name, mask, key), m[mask][key]))
            metrics.append(Metric.info("e40gn.%s.%s.steps" % (view, name), steps))
            # Pictures: the first frame, the crop of three consecutive frames side by side (the temporal view), the per-pixel temporal noise as a picture.
            nat0 = display(fr[0], view)
            save_png(os.path.join(out_dir, "e40-gn-%s-%s.png" % (view, name)), nat0)
            tau = plane(folder, jid_of(view, name) + "-x", "tau")
            save_png(os.path.join(out_dir, "e40-gn-%s-%s-aid.png" % (view, name)), heat(tau))
            x, y, w, h = _CROPS[view]
            strip = [enlarge(display(fr[f], view)[y:y + h, x:x + w]) for f in range(min(3, frames))]
            if frames > 1:
                ls = np.stack([luma(display(f_, view)) for f_ in fr])
                noise = np.clip(ls.std(axis=0)[y:y + h, x:x + w] * 8.0, 0.0, 1.0)
                strip.append(enlarge(np.repeat(noise[..., None], 3, axis=-1)))
            save_png(os.path.join(out_dir, "e40-gn-%s-%s-seq.png" % (view, name)), np.concatenate(strip, axis=1))
        b = lap(luma(backdrop(view)))
        lines.append("   %-7s backdrop hf %.5f" % (view, float(b.mean())))
    results["lines"] = lines
    results["crops"] = {k: list(v) for k, v in _CROPS.items()}
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    print("pictures: %s (e40-gn-<view>-<variant>.png natural, -aid optical depth, -seq three frames of the crop and the temporal noise x 8)" % out_dir)
    return metrics


def stages():
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")) or os.path.getmtime(os.path.join(INPUT, "jobs.json")) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("layers", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False, timeout=2400)]


register_series("e40-gn", stages(), collect, exclusive=False, who="e40-gn")


# --------------------------------------------------------------------------------------------------------------------------------
# The pass as the game runs it (scene grain-temporal) and its GPU cost (scene grain-cost)
# --------------------------------------------------------------------------------------------------------------------------------
FULL = (1708, 960)
T_CASES = [("prod", "plain"), ("prod", "acc10"), ("prod", "acc20"), ("prod", "acc30"), ("prod", "b2"), ("leap4xc", "plain"), ("leap4xc", "acc20"), ("leap4x", "plain"),
           ("leap4x", "acc20"), ("ramp4leap4x", "plain"), ("ramp4leap4x", "acc20")]
T_SCENARIOS = {"static": [36, 37, 38, 39], "turn": [37, 38], "fly": [37, 38], "turnfast": [37, 38], "flyfast": [37, 38], "cut": [0, 2, 5, 10]}
T_MOTION = ("turn", "fly", "turnfast", "flyfast")


def full_backdrop(view):
    """The backdrop at the full target's size (a yaw about the vertical and a sideways move of the camera leave every pixel's elevation as it is)."""
    key = view + "-full"
    if key not in _BACKDROP:
        height, pitch = VIEWS[view]
        lon = math.radians(SITE * 10.0)
        pos = np.array([math.cos(lon), math.sin(lon), 0.0]) * (R + height)
        cam = pr.Camera(pos, 0.0, pitch, FULL[0], FULL[1], FOV)
        d = cam.ray_directions().reshape(FULL[1], FULL[0], 3)
        up = cam.pos / np.linalg.norm(cam.pos)
        elev = np.degrees(np.arcsin(np.clip(d @ up, -1.0, 1.0)) + math.acos(R / np.linalg.norm(cam.pos)))
        t = np.clip(elev / 60.0, 0.0, 1.0)[..., None] ** 0.5
        sky = SKY_HORIZON * (1.0 - t) + SKY_ZENITH * t
        w = np.clip((elev + 1.0) / 2.0, 0.0, 1.0)[..., None]
        _BACKDROP[key] = sky * w + SEA * (1.0 - w)
    return _BACKDROP[key]


def full_frame(folder, name):
    """A full target the scene wrote (half floats, the bottom row first), as (h, w, 4) with the top row first."""
    return np.load(os.path.join(folder, name + ".npy")).astype(np.float64)[::-1]


def full_display(m, view):
    a = np.clip(m[..., 3:4], 0.0, 1.0)
    colour = encode(m[..., :3] / np.maximum(a, 1e-4))
    return np.clip(colour * a + full_backdrop(view) * (1.0 - a), 0.0, 1.0)


def collect_temporal(ctx):
    folder = ctx.folder("temporal")
    out_dir = os.path.join(main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines = [], []
    lines.append("THE PASS AS THE GAME RUNS IT (full target 1708 x 960, march target 854 x 480; display values; masks from the converged full-size march of the pose)")
    lines.append("   static: tstd over 4 consecutive frames, hfx, rmsb (the 4-frame mean against the converged picture) for closed | rim.  turn (0.5 deg a frame), fly (5 m a frame):"
                 " err = rms of one frame against the converged picture of its pose over the cloud mask, hfx over the rim mask")
    results = {}
    for view in VIEWS:
        ref0 = full_frame(folder, "gt-%s-static-ref-f%d" % (view, T_SCENARIOS["static"][0]))
        mask_set = masks(np.clip(ref0[..., 3], 0.0, 1.0))
        ref_l = luma(full_display(ref0, view))
        ref_lap = lap(ref_l)
        x, y, w, h = pick_crop(np.clip(ref0[..., 3], 0.0, 1.0), 360, 200)
        for march, mode in T_CASES:
            name = "%s-%s" % (march, mode)
            fr = [full_frame(folder, "gt-%s-static-%s-f%d" % (view, name, i)) for i in T_SCENARIOS["static"]]
            ls = np.stack([luma(full_display(m, view)) for m in fr])
            tstd = ls.std(axis=0)
            mean = ls.mean(axis=0)
            laps = np.stack([lap(l) for l in ls]).mean(axis=0)
            row = {}
            for mk in ("closed", "rim"):
                inner = mask_set[mk].copy()
                inner[0, :] = inner[-1, :] = False
                inner[:, 0] = inner[:, -1] = False
                diff = (mean - ref_l)[inner]
                row[mk] = (float(tstd[inner].mean()), float(laps[inner].mean() - ref_lap[inner].mean()), float(np.sqrt((diff * diff).mean())))
                for key, val in zip(("tstd", "hfx", "rmsb"), row[mk]):
                    metrics.append(Metric.info("e40gn.t.%s.static.%s.%s.%s" % (view, name, mk, key), val))
            motion = {}
            for sc in T_MOTION:
                errs, hfs = [], []
                for i in T_SCENARIOS[sc]:
                    ref = full_frame(folder, "gt-%s-%s-ref-f%d" % (view, sc, i))
                    ms = masks(np.clip(ref[..., 3], 0.0, 1.0))
                    rl = luma(full_display(ref, view))
                    l = luma(full_display(full_frame(folder, "gt-%s-%s-%s-f%d" % (view, sc, name, i)), view))
                    d = (l - rl)[ms["cloud"]]
                    errs.append(float(np.sqrt((d * d).mean())))
                    rim = ms["rim"].copy()
                    rim[0, :] = rim[-1, :] = False
                    rim[:, 0] = rim[:, -1] = False
                    hfs.append(float(lap(l)[rim].mean() - lap(rl)[rim].mean()))
                motion[sc] = (float(np.mean(errs)), float(np.mean(hfs)))
                metrics.append(Metric.info("e40gn.t.%s.%s.%s.err" % (view, sc, name), motion[sc][0]))
                metrics.append(Metric.info("e40gn.t.%s.%s.%s.rim_hfx" % (view, sc, name), motion[sc][1]))
            # The static error of one frame against the converged picture, for the comparison with the motion rows.
            d0 = (ls[0] - ref_l)[mask_set["cloud"]]
            err0 = float(np.sqrt((d0 * d0).mean()))
            # The frames after a cut: the rms of each against the converged picture over the cloud mask (the history fills from frame 0).
            cut = []
            for i in T_SCENARIOS["cut"]:
                dc = (luma(full_display(full_frame(folder, "gt-%s-cut-%s-f%d" % (view, name, i)), view)) - ref_l)[mask_set["cloud"]]
                cut.append(float(np.sqrt((dc * dc).mean())))
                metrics.append(Metric.info("e40gn.t.%s.cut.%s.f%d.err" % (view, name, i), cut[-1]))
            results["%s-%s" % (view, name)] = dict(static=row, motion=motion, err_static=err0, cut=cut)
            lines.append("   %-7s %-18s static %.4f %.4f %.4f | %.4f %.4f %.4f  err %.4f ; err/rim hfx: %s ; cut err %s"
                         % ((view, name) + row["closed"] + row["rim"] + (err0, "  ".join("%s %.4f/%+.4f" % ((sc,) + motion[sc]) for sc in T_MOTION),
                                                                         " ".join("%.4f" % c for c in cut))))
            strip = [enlarge(full_display(fr[k], view)[y:y + h, x:x + w], 2) for k in range(3)]
            noise = np.clip(tstd[y:y + h, x:x + w] * 8.0, 0.0, 1.0)
            strip.append(enlarge(np.repeat(noise[..., None], 3, axis=-1), 2))
            save_png(os.path.join(out_dir, "e40-gn-t-%s-%s-seq.png" % (view, name)), np.concatenate(strip, axis=1))
            save_png(os.path.join(out_dir, "e40-gn-t-%s-%s.png" % (view, name)), full_display(fr[0], view))
            turn = full_frame(folder, "gt-%s-turn-%s-f%d" % (view, name, T_SCENARIOS["turn"][0]))
            save_png(os.path.join(out_dir, "e40-gn-t-%s-%s-turn.png" % (view, name)), full_display(turn, view))
        save_png(os.path.join(out_dir, "e40-gn-t-%s-converged.png" % view), full_display(ref0, view))
    with open(os.path.join(BUILD, "e40gn-temporal-results.json"), "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


def collect_cost(ctx):
    values = ctx.player_metrics("cost")
    metrics, lines = [], ["GPU ms at 1707 x 960 (march / resolve / composite / total), median of 100 frames in two rounds of opposite order"]
    names = sorted(set(k.split(".", 2)[2] for k in values if k.startswith("graincost.total.")))
    for n in names:
        row = [values.get("graincost.%s.%s" % (part, n), (float("nan"),))[0] for part in ("march", "resolve", "composite", "total")]
        lines.append("   %-34s %.3f / %.3f / %.3f / %.3f" % ((n,) + tuple(row)))
        metrics.append(Metric.info("e40gn.cost." + n, row[3]))
    print("\n".join(lines))
    with open(os.path.join(BUILD, "e40gn-cost-results.json"), "w", encoding="utf-8") as f:
        json.dump(dict(lines=lines), f, indent=1)
    return metrics


register_series("e40-gn-temporal", [Stage("temporal", ["-mode", "scene", "-scene", "grain-temporal", "-in", INPUT], size=(1280, 720), proxy=False, timeout=2400)],
                collect_temporal, exclusive=False, who="e40-gn")
register_series("e40-gn-cost", [Stage("cost", ["-mode", "scene", "-scene", "grain-cost", "-in", INPUT], size=(1707, 960), proxy=True, timeout=1500)],
                collect_cost, exclusive=True, who="e40-gn")


def selftest():
    jobs = build_jobs()
    ids = [j["id"] for j in jobs]
    assert len(ids) == len(set(ids)), "job ids repeat"
    assert "gn-ground-prod-f7" in ids and "gn-above-conv-x" in ids
    a = np.zeros((40, 60))
    a[10:30, 20:40] = 1.0
    m = masks(a)
    assert m["closed"].sum() == 400 and m["rim"].sum() > 0 and m["rim"].sum() < 400 and not m["rim"][20, 30]
    flat = np.full((SIZE[1], SIZE[0], 4), 0.0)
    out = measure([flat, flat], flat, "ground", {"all": np.ones((SIZE[1], SIZE[0]), dtype=bool)})
    assert out["all"]["tstd"] == 0.0 and abs(out["all"]["bias"]) < 1e-12
    assert float(lap(luma(backdrop("ground"))).mean()) < 0.0005
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
