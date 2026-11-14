"""E40 L3: the cumulus layer with the generator's real cover and type maps, before and after the meso stage and the climate types (player series e40-mix).

    python tools/weather_metrics/e40mix.py --prepare <before-dir> <after-dir> <seed> <time> <lat> <lon>   writes the two map pairs and the jobs
    python tools/build_weather_player.py --series e40-mix
    python tools/build_weather_player.py --series e40-mix --no-build
    python tools/weather_metrics/e40mix.py --selftest

<before-dir> and <after-dir> are the dumps of tests/ReDefinition.Tests/Atmospheres/T09/TypeMixExplore.cs without (TYPEMIX_STAGE=0) and with the stage; the maps are the cumulus layer's cover and
type bytes of one seed and time, 512 x 256, the whole body. The scene is the type-sheet scene of e40-ty with ONE layer (the shipped Clouds-Kerbin.cfg's cumulus layer with its soft and light
keys), the sun 45.6 degrees up. Four views of each map pair at the same place: from the ground looking along the horizon (4 and 12 degrees up) and from above (60 km, 45 degrees down; 200 km,
straight down: about 230 km across). The pictures go to .local/work/series-pictures/E40 as e40-mix-after-views-*.png (the 'before' ones as e40-mix-before-views-*.png).
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
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40mix-in")
PLACE = os.path.join(INPUT, "place.json")
R = 600000.0
MAP_W, MAP_H = 512, 256
SIZE = (1708, 960)
TILE = (854, 480)
LAYER = "Kerbin-cumulus"
GROUND = 264.0
# view key, height above the ground (m), pitch (degrees, up positive)
VIEWS = [("ground4", GROUND, 4.0), ("ground12", GROUND, 12.0), ("above60", 60000.0, -45.0), ("above200", 200000.0, -90.0)]
VARIANTS = ("before", "after")


def place():
    with open(PLACE, encoding="utf-8") as f:
        return json.load(f)


def camera(lat, lon, height, pitch, size):
    la, lo = math.radians(lat), math.radians(lon)
    pos = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)]) * (R + height)
    return pr.Camera(pos, 0.0, pitch, size[0], size[1], e40ty.FOV)


def jid(variant, view):
    # the type-sheet scene renders the jobs whose id starts with "ty-"
    return "ty-mix-%s-%s" % (variant, view)


def build_jobs():
    p = place()
    layers_by_name = {s["name"]: s for s in e40o.kerbin_layers()}
    extras = e40ty.cfg_extras()
    spec = layers_by_name[LAYER]
    lk, tk = extras[LAYER]
    jobs = []
    for variant in VARIANTS:
        for view, height, pitch in VIEWS:
            jl = e40o.job_layer(spec, "%s-cover.r8" % variant, "%s-type.r8" % variant)
            jl.update(lk)
            for t in jl["types"]:
                t.update(tk.get(t["name"], {}))
            cam = camera(p["lat"], p["lon"], height, pitch, SIZE)
            jobs.append(lay.job(jid(variant, view), "lum", cam, [jl], SIZE[0], SIZE[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H,
                                sun=lay.sun_vector(cam.pos, 0.0, e40ty.SUN_ELEVATION), sunColour=[2.35, 2.35, 2.35], ambientTop=e40ty.AMBIENT_TOP, ambientBottom=e40ty.AMBIENT_BOTTOM,
                                soft=e40ty.SOFTNESS, maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3))
    return jobs


def prepare(before, after, seed, time, lat, lon):
    os.makedirs(INPUT, exist_ok=True)
    for variant, folder in (("before", before), ("after", after)):
        for kind in ("cover", "type"):
            src = os.path.join(folder, "s%d_t%d_%s_%s.u8" % (seed, time, LAYER, kind))
            shutil.copyfile(src, os.path.join(INPUT, "%s-%s.r8" % (variant, kind)))
    with open(PLACE, "w", encoding="utf-8") as f:
        json.dump(dict(lat=lat, lon=lon, seed=seed, time=time), f)
    write_inputs()


def write_inputs():
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def collect(ctx):
    sheet = ctx.folder("sheet")
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    layer = {s["name"]: s for s in e40o.kerbin_layers()}[LAYER]
    tint = e40ty.white_tint(layer["inner"] + 0.25 * (layer["outer"] - layer["inner"]))
    p = place()
    lines, tiles, metrics = ["E40-MIX: cumulus layer, real maps (seed %d, time %d) at %.1f N %.1f E, before and after the stage" % (p["seed"], p["time"], p["lat"], p["lon"])], {}, []
    for variant in VARIANTS:
        for view, height, pitch in VIEWS:
            path = os.path.join(sheet, jid(variant, view) + ".npy")
            if not os.path.isfile(path):
                lines.append("   %-7s %-9s: no picture" % (variant, view))
                continue
            m = e40ty.load_lum(sheet, jid(variant, view))
            cam = camera(p["lat"], p["lon"], height, pitch, SIZE)
            img = e40ty.picture(m, cam, tint)
            gn.save_png(os.path.join(out_dir, "e40-mix-%s-views-%s.png" % (variant, view)), img)
            tiles[(variant, view)] = e40ty.box(img, TILE)
            words = e40ty.alpha_words(m[..., 3])
            lines.append("   %-7s %-9s: cloud covers %.2f of the picture, median alpha %.2f" % (variant, view, words["cover"], words["median"]))
            metrics.append(Metric.info("e40mix.%s.%s.cover" % (variant, view), words["cover"]))
    rows = []
    for view, _, _ in VIEWS:
        cols = [e40ty.label(tiles[(v, view)], "%s: %s" % (v, view)) if (v, view) in tiles else np.zeros((TILE[1], TILE[0], 3)) for v in VARIANTS]
        rows.append(np.concatenate(cols, axis=1))
    gn.save_png(os.path.join(out_dir, "e40-mix-after-views.png"), np.concatenate(rows, axis=0))
    print("\n".join(lines))
    return metrics


def stages():
    if os.path.isfile(PLACE):
        write_inputs()
    return [Stage("sheet", ["-mode", "scene", "-scene", "type-sheet", "-in", INPUT], size=(1708, 960), proxy=False, timeout=2400)]


register_series("e40-mix", stages(), collect, exclusive=False, who="e40-mix")


def selftest():
    os.makedirs(INPUT, exist_ok=True)
    kept = open(PLACE, encoding="utf-8").read() if os.path.isfile(PLACE) else None
    with open(PLACE, "w", encoding="utf-8") as f:
        json.dump(dict(lat=8.0, lon=100.0, seed=1, time=0), f)
    try:
        jobs = build_jobs()
    finally:
        if kept is None:
            os.remove(PLACE)
        else:
            with open(PLACE, "w", encoding="utf-8") as f:
                f.write(kept)
    ids = [j["id"] for j in jobs]
    assert len(ids) == len(set(ids)) == len(VARIANTS) * len(VIEWS)
    assert all(j["mapWidth"] == MAP_W and j["mapHeight"] == MAP_H for j in jobs)
    nadir = [j for j in jobs if j["id"] == jid("after", "above200")][0]
    assert abs(math.sqrt(sum(x * x for x in nadir["position"])) - (R + 200000.0)) < 1.0
    print("selftest passed (%d jobs)" % len(jobs))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--prepare", nargs=6, metavar=("BEFORE", "AFTER", "SEED", "TIME", "LAT", "LON"))
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.prepare:
        b, af, s, t, la, lo = a.prepare
        prepare(b, af, int(s), int(t), float(la), float(lo))
        print("prepared", INPUT)
