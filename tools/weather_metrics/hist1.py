"""HIST1: the reconstruction's history under a translating camera, moved by the layers' middle spheres (today) or by the march's own distance (recon.trueDistance, token rtd),
at the 2 x 2 and the 3 x 3 rotation, against the converged full march of the same pose.

    python tools/build_weather_player.py --series hist1 --no-build     (the frames 37 and 38 of every case and scenario, with their references)
    python tools/weather_metrics/hist1.py --pictures <folder>          (reference, case and error pictures with speaking names, and the error table)

The views are the far1 inputs (build/weather-series/far1-in-2560: e40pp's gclock place from the ground and from above). The scenarios are soft-temporal's: static, fly (5 m
sideways per frame) and flyfast (40 m per frame). The error number guards the pictures; the look is judged on the 1:1 crops.
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40pp  # noqa: E402

INPUT = os.path.join(e40pp.BUILD, "far1-in-2560")
SIZE = (2560, 1440)
VIEWS = {"pp-gclock-far_g10": "ground-30m", "pp-gclock-far_a15": "above-9km"}
SCENARIOS = ["fly", "flyfast"]
FRAMES = {"static": [39], "fly": [38], "flyfast": [38]}
HEAD = "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+"
TAIL = "+bacc+chm+lp100"
CASES = [
    ("2x2-today", HEAD + "blk2" + TAIL),
    ("2x2-truedistance", HEAD + "blk2" + TAIL + "+rtd"),
    ("3x3-today", HEAD + "blk3" + TAIL),
    ("3x3-truedistance", HEAD + "blk3" + TAIL + "+rtd"),
    ("4x4-today", HEAD + "blk4" + TAIL),
    ("4x4-truedistance", HEAD + "blk4" + TAIL + "+rtd"),
]


# HIST1W: what softens the moved history at 3 x 3 with the true distance: the clip's width (clip<N>: N / 10 standard deviations; 9990 = none) and the fresh sample's weight
# (acc<NN>: NN / 100). The first case is the still reference's march (the accepted 2 x 2).
W_VIEWS = {"pp-gclock-far_a15": "above-9km"}
W_SCENARIOS = ["static", "fly", "flyfast"]
W_CASES = [
    ("2x2-today", HEAD + "blk2" + TAIL),
    ("3x3-td", HEAD + "blk3" + TAIL + "+rtd"),
    ("3x3-td-clip2sigma", HEAD + "blk3" + TAIL + "+rtd+clip20"),
    ("3x3-td-noclip", HEAD + "blk3" + TAIL + "+rtd+clip9990"),
    ("3x3-td-fresh40", HEAD + "blk3" + TAIL + "+rtd+acc40"),
    ("3x3-td-fresh40-clip2sigma", HEAD + "blk3" + TAIL + "+rtd+acc40+clip20"),
    ("3x3-td-fresh100-noclip", HEAD + "blk3" + TAIL + "+rtd+acc100+clip9990"),
    ("2x2-td", HEAD + "blk2" + TAIL + "+rtd"),
]
# HIST1A: the count of marched samples (rag) with the true distance, from a cut (hist1a: forty frames, the last 32 moving) and settled (hist1as: 120 frames at the first pose
# before them).
A_SCENARIOS = ["static", "fly", "flyfast"]
A_CASES = [
    ("2x2-today", HEAD + "blk2" + TAIL),
    ("2x2-td-count", HEAD + "blk2" + TAIL + "+rtd+rag"),
    ("3x3-today", HEAD + "blk3" + TAIL),
    ("3x3-td", HEAD + "blk3" + TAIL + "+rtd"),
    ("3x3-td-count", HEAD + "blk3" + TAIL + "+rtd+rag"),
    ("4x4-td-count", HEAD + "blk4" + TAIL + "+rtd+rag"),
]
SETS = {"hist1": ("motion", VIEWS, SCENARIOS, CASES, []), "hist1w": ("weights", W_VIEWS, W_SCENARIOS, W_CASES, []),
        "hist1a": ("cut", VIEWS, A_SCENARIOS, A_CASES, []), "hist1as": ("settled", VIEWS, A_SCENARIOS, A_CASES, ["-warm", "120"])}


def stages(name="hist1"):
    stage, views, scenarios, cases, extra = SETS[name]
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")):
        return []
    return [Stage(stage, ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", ",".join(views), "-media", "pr50", "-marches", ",".join(m for _, m in cases),
                             "-scenarios", ",".join(scenarios), "-dumpframes", "39", "-stillref", "1", "-motion", "all", "-light", "product", "-width", str(SIZE[0]), "-height", str(SIZE[1])] + extra,
                  size=SIZE, proxy=False, timeout=3000)]


def collect(ctx):
    return [Metric.info("hist1.stages", float(len(ctx.folders)))]


register_series("hist1", stages("hist1"), collect, exclusive=False, who="hist1")
register_series("hist1w", stages("hist1w"), collect, exclusive=False, who="hist1")
register_series("hist1a", stages("hist1a"), collect, exclusive=False, who="hist1")
register_series("hist1as", stages("hist1as"), collect, exclusive=False, who="hist1")


def load(folder, name):
    path = os.path.join(folder, name + ".npy")
    if not os.path.isfile(path):
        return None
    return np.load(path).astype(np.float64)[::-1]


def display(a):
    """Premultiplied radiance over a mid grey, with a plain display curve: a picture to compare cases in, not the product's display stage."""
    rgb = a[..., :3] + (1.0 - np.clip(a[..., 3:4], 0, 1)) * 0.18
    return np.clip(rgb / (1.0 + rgb) * 1.6, 0, 1) ** (1 / 2.2)


def save(img, path):
    from PIL import Image
    Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype("uint8")).save(path)


def pictures(out, root, name="hist1"):
    stage, VIEWS, SCENARIOS, CASES, _extra = SETS[name]
    folder = os.path.join(root, name + "-" + stage)
    os.makedirs(out, exist_ok=True)
    rows = []
    for view, word in VIEWS.items():
        for sc in SCENARIOS:
            for f in FRAMES[sc]:
                ref = load(folder, "gs-%s-pr50-%s-ref-f%d" % (view, sc, FRAMES[sc][0] if sc == "static" else f))
                if ref is None:
                    print("missing reference %s %s f%d" % (view, sc, f))
                    continue
                if f == FRAMES[sc][-1]:
                    save(display(ref), os.path.join(out, "HIST1-%s-%s-f%d-reference-full-march-2560x1440.png" % (word, sc, f)))
                for name, march in CASES:
                    a = load(folder, "gs-%s-pr50-%s-%s-acc20-f%d" % (view, sc, march, f))
                    if a is None:
                        print("missing %s %s %s f%d" % (view, sc, name, f))
                        continue
                    err = np.abs(display(a) - display(ref)).mean(axis=2)
                    rows.append((word, sc, f, name, err.mean(), np.percentile(err, 99)))
                    if f == FRAMES[sc][-1]:
                        save(display(a), os.path.join(out, "HIST1-%s-%s-f%d-%s-2560x1440.png" % (word, sc, f, name)))
                        save(np.repeat(np.clip(err * 4.0, 0, 1)[..., None], 3, axis=2), os.path.join(out, "HIST1-%s-%s-f%d-%s-error-x4-2560x1440.png" % (word, sc, f, name)))
    print("view, scenario, frame, case: mean and 99th percentile of the display error against the full march of the same pose")
    for r in rows:
        print("  %-11s %-8s f%-2d %-18s %.5f  %.4f" % r)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--pictures")
    p.add_argument("--set", default="hist1")
    p.add_argument("--root", default=e40pp.BUILD.replace("weather-series", "WeatherPlayer"))
    a = p.parse_args()
    if a.pictures:
        pictures(a.pictures, a.root, a.set)
    return 0


if __name__ == "__main__":
    sys.exit(main())
