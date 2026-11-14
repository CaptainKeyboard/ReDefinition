"""E40 L2 E2: the bare march through layers that are empty along a ray, and what the soft density costs on top (player series e40-wm-cost, e40-wm-temporal).

    python tools/build_weather_player.py --series e40-wm-cost
    python tools/build_weather_player.py --series e40-wm-temporal
    python tools/weather_metrics/e40wm.py --report         the cost table of the last run
    python tools/weather_metrics/e40wm.py --selftest

The scene of e40gc.py's l4ground / l4above (the four layers of Clouds-Kerbin.cfg in fair weather, the same two cameras) with maps that are NOT constant: every layer's coverage map is a
512 x 256 field (the size of the generator's maps) with the statistics of a fair-weather sky: the rain deck holds a few small patches of cover far from the camera, the cumulus
layer broad cells, the cumulonimbus layer a few isolated cells, the cirrus layer streaks over a third of the globe. Each layer has cover SOMEWHERE inside the horizon and is
empty along most rays; this is the case the game start soft59 showed (layers.dropEmpty left out 0 of 4 layers) and the constant maps of e40gc.py cannot show.
The views are named r4ground and r4above; a case is a SoftTable case (see e40wv.py).
"""
import argparse
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40gc as gc  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402

BUILD = gc.BUILD
INPUT = os.path.join(BUILD, "e40wm-in")
COST_JSON = os.path.join(BUILD, "e40wm-cost-results.json")
MAP_W, MAP_H = 512, 256
VIEWS = {"r4ground": "ground", "r4above": "above"}
FAIR = [("Kerbin-weather-rain", 0.0), ("Kerbin-cumulus", 0.25), ("Kerbin-cumulonimbus", 0.0), ("Kerbin-cirrus", 0.0)]


def field(seed, mean, amp, cells, octaves=3, lo=0.0, hi=1.0):
    """A coverage field of MAP_W x MAP_H (uint8): mean + amp * fractal noise of cells (map texels per feature at the coarsest octave), clipped to lo..hi."""
    from scipy.ndimage import zoom
    rng = np.random.default_rng(seed)
    total = np.zeros((MAP_H, MAP_W))
    weight = 0.0
    for o in range(octaves):
        n = max(2, int(round(MAP_W / (cells / (2 ** o)))))
        grid = rng.normal(size=(max(2, n // 2), n))
        up = zoom(grid, (MAP_H / grid.shape[0], MAP_W / grid.shape[1]), order=3, mode="grid-wrap")
        up = up[:MAP_H, :MAP_W]
        up = (up - up.mean()) / max(up.std(), 1e-6)
        total += up * (0.55 ** o)
        weight += 0.55 ** o
    total /= weight
    return np.clip(np.rint(np.clip(mean + amp * total, lo, hi) * 255.0), 0, 255).astype(np.uint8)


# The fields: (seed, mean, amp, cells, octaves). The numbers make a fair sky: the share of the map above the threshold a layer needs (about 0.3 for the cumulus layer) differs per layer.
FIELDS = {
    "Kerbin-weather-rain": (11, 0.00, 0.22, 22, 2),
    "Kerbin-cumulus": (23, 0.28, 0.30, 26, 3),
    "Kerbin-cumulonimbus": (37, 0.05, 0.26, 20, 2),
    "Kerbin-cirrus": (41, 0.20, 0.30, 34, 2),
}


def write_map(name, array):
    os.makedirs(INPUT, exist_ok=True)
    array.tofile(os.path.join(INPUT, name))
    return name


def type_map(value):
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    return write_map("t%03d.r8" % v, np.full((MAP_H, MAP_W), v, dtype=np.uint8))


def build_jobs():
    specs = {s["name"]: s for s in e40o.kerbin_layers()}
    extras = e40ty.cfg_extras()
    jobs = []
    stats = {}
    for key, base in VIEWS.items():
        layer_list = []
        for name, typ in FAIR:
            lk, tk = extras[name]
            seed, mean, amp, cells, octs = FIELDS[name]
            arr = field(seed, mean, amp, cells, octs)
            stats[name] = dict(min=float(arr.min()) / 255.0, max=float(arr.max()) / 255.0, mean=float(arr.mean()) / 255.0)
            jl = e40o.job_layer(specs[name], write_map("cov-%s.r8" % name, arr), type_map(typ))
            jl.update(lk)
            for t in jl["types"]:
                t.update(tk.get(t["name"], {}))
            layer_list.append(jl)
        cam = gn.camera(base)
        light = dict(sun=lay.sun_vector(cam.pos, 0.0, gn.SUN_ELEVATION), sunColour=gn.SUN_COLOUR, ambientTop=gn.AMBIENT_TOP, ambientBottom=gn.AMBIENT_BOTTOM)
        args = gn.prod()
        args.update(light)
        jobs.append(lay.job("gn-%s-prod-f0" % key, "lum", cam, layer_list, gn.SIZE[0], gn.SIZE[1], planet=gn.R, mapWidth=MAP_W, mapHeight=MAP_H, frameNumber=0, **args))
    return jobs, stats


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs, stats = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    with open(os.path.join(INPUT, "stats.json"), "w", encoding="utf-8") as f:
        json.dump(stats, f, indent=1)
    return jobs


def ensure_inputs():
    path = os.path.join(INPUT, "jobs.json")
    sources = [os.path.abspath(__file__), e40ty.__file__, e40o.CFG, gc.__file__]
    if not os.path.isfile(path) or os.path.getmtime(path) < max(os.path.getmtime(s) for s in sources):
        write_inputs()


B48 = "seq1"
B24 = "seq1m24"
# Step 1, what the game start showed, in the player with maps that hold cover somewhere: soft against hard; layers.dropEmpty and the tile test alone; the third-size target and the cut span.
STEP1 = [B48, B48 + "+drop", B48 + "+skip", B48 + "+skip+drop", B48 + "+r33", B48 + "+r33+skip", B48 + "+r33+drop", "tseq1m16+r33", "tseq1m16+r33+skip"]
# Step 2, where the soft density's extra cost goes: the cost debug masks (1 no light, 2 no shape noise, 4 no erosion) on hard and on the product's soft 0.5.
ATTR = [B48, B48 + "+dbg1", B48 + "+dbg2", B48 + "+dbg3", B48 + "+dbg7"]
COST_STAGES = [
    ("cost-step1", "r4ground,r4above", "hard,pr50", STEP1),
    ("cost-attr", "r4ground,r4above", "hard,pr50", ATTR),
]


def cost_stage(label, views, media, cases):
    return Stage(label, ["-mode", "scene", "-scene", "soft-cost", "-in", INPUT, "-views", views, "-media", media, "-marches", ",".join(cases)], size=(1707, 960), proxy=True, timeout=1700)


def collect_cost(ctx, stages=None, json_path=None):
    return gc.collect_cost(ctx, stages or COST_STAGES, json_path or COST_JSON)


def report(path=None):
    with open(path or COST_JSON, encoding="utf-8") as f:
        rows = json.load(f)["rows"]
    for k in sorted(rows):
        r = rows[k]
        print("%-52s march %6.3f | resolve %6.3f | composite %6.3f | total %6.3f" % (k, r.get("march", float("nan")), r.get("resolve", float("nan")), r.get("composite", float("nan")), r.get("total", float("nan"))))


def selftest():
    a = field(11, 0.0, 0.22, 22, 2)
    assert a.shape == (MAP_H, MAP_W) and a.dtype == np.uint8
    b = field(23, 0.28, 0.30, 26, 3)
    assert b.max() > b.min() and abs(float(b.mean()) / 255.0 - 0.28) < 0.12
    print("selftest passed (rain deck max %.2f, cumulus mean %.2f)" % (a.max() / 255.0, b.mean() / 255.0))


ensure_inputs()
register_series("e40-wm-cost", [cost_stage(*s) for s in COST_STAGES], collect_cost, exclusive=True, who="e40-wm")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--report", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.report:
        report()
