"""E40 P1: the cumulus layer on two bodies of different size, from the generator's real maps (player series e40-body).

    python tools/weather_metrics/e40body.py --prepare <home-dir> <second-dir> <seed> <time> <lat> <lon>   writes the two map pairs and the jobs
    python tools/build_weather_player.py --series e40-body
    python tools/build_weather_player.py --series e40-body --no-build
    python tools/weather_metrics/e40body.py --selftest

<home-dir> and <second-dir> are the dumps of tests/ReDefinition.Tests/Atmospheres/T09/TypeMixExplore.cs (TYPEMIX_BODY unset: the home planet; TYPEMIX_BODY=second: the second body) with the
stage on. Home planet: radius 600 km, troposphere 10.5 km. Second body: radius 500 km, troposphere 7.5 km; its cumulus layer is the home layer with the heights and the noise size scaled by 7.5 / 10.5
(the cfg of a body has its own; this is the template of the law that heights follow the troposphere), and the cameras' heights scale the same way, so a picture of one is the picture of the other
in units of the troposphere. Views: along the horizon from the ground (4 and 12 degrees up), and from above (60 km and 200 km on the home planet: 45 and 143 km on the second body).
The pictures go to .local/work/series-pictures/E40 as e40-body-<view>.png (the two bodies side by side) and e40-body-<body>-<view>.png.
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
INPUT = os.path.join(BUILD, "e40body-in")
PLACE = os.path.join(INPUT, "place.json")
MAP_W, MAP_H = 512, 256
SIZE = (1708, 960)
TILE = (854, 480)
LAYER = "Kerbin-cumulus"
GROUND = 264.0
# the bodies: planet radius (m) and the troposphere depth against the home planet's
BODIES = {"home": dict(radius=600000.0, ratio=1.0), "second": dict(radius=500000.0, ratio=7.5 / 10.5)}
VARIANTS = ("home", "second")
# view key, height above the ground on the home planet (m), pitch (degrees, up positive)
VIEWS = [("ground4", GROUND, 4.0), ("ground12", GROUND, 12.0), ("above60", 60000.0, -45.0), ("above200", 200000.0, -90.0)]


def place():
    with open(PLACE, encoding="utf-8") as f:
        return json.load(f)


def camera(body, lat, lon, height, pitch, size):
    b = BODIES[body]
    la, lo = math.radians(lat), math.radians(lon)
    pos = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)]) * (b["radius"] + height * b["ratio"])
    return pr.Camera(pos, 0.0, pitch, size[0], size[1], e40ty.FOV)


def jid(body, view):
    # the type-sheet scene renders the jobs whose id starts with "ty-"
    return "ty-body-%s-%s" % (body, view)


def scaled_spec(body):
    spec = dict({s["name"]: s for s in e40o.kerbin_layers()}[LAYER])
    r = BODIES[body]["ratio"]
    spec["inner"] *= r
    spec["outer"] *= r
    spec["scale"] *= r
    return spec


def build_jobs():
    p = place()
    extras = e40ty.cfg_extras()
    lk, tk = extras[LAYER]
    jobs = []
    for body in VARIANTS:
        spec = scaled_spec(body)
        for view, height, pitch in VIEWS:
            jl = e40o.job_layer(spec, "%s-cover.r8" % body, "%s-type.r8" % body)
            jl.update(lk)
            for t in jl["types"]:
                t.update(tk.get(t["name"], {}))
            cam = camera(body, p["lat"], p["lon"], height, pitch, SIZE)
            jobs.append(lay.job(jid(body, view), "lum", cam, [jl], SIZE[0], SIZE[1], planet=BODIES[body]["radius"], mapWidth=MAP_W, mapHeight=MAP_H,
                                sun=lay.sun_vector(cam.pos, 0.0, e40ty.SUN_ELEVATION), sunColour=[2.35, 2.35, 2.35], ambientTop=e40ty.AMBIENT_TOP, ambientBottom=e40ty.AMBIENT_BOTTOM,
                                soft=e40ty.SOFTNESS, maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3))
    return jobs


def prepare(home, second, seed, time, lat, lon):
    os.makedirs(INPUT, exist_ok=True)
    for body, folder in (("home", home), ("second", second)):
        for kind in ("cover", "type"):
            src = os.path.join(folder, "s%d_t%d_%s_%s.u8" % (seed, time, LAYER, kind))
            shutil.copyfile(src, os.path.join(INPUT, "%s-%s.r8" % (body, kind)))
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
    p = place()
    lines, tiles, metrics = ["E40-BODY: cumulus layer, real maps (seed %d, time %d) at %.1f N %.1f E on the home planet and the second body" % (p["seed"], p["time"], p["lat"], p["lon"])], {}, []
    for body in VARIANTS:
        spec = scaled_spec(body)
        tint = e40ty.white_tint(spec["inner"] + 0.25 * (spec["outer"] - spec["inner"]))
        for view, height, pitch in VIEWS:
            path = os.path.join(sheet, jid(body, view) + ".npy")
            if not os.path.isfile(path):
                lines.append("   %-7s %-9s: no picture" % (body, view))
                continue
            m = e40ty.load_lum(sheet, jid(body, view))
            cam = camera(body, p["lat"], p["lon"], height, pitch, SIZE)
            kept_radius = e40ty.R
            e40ty.R = BODIES[body]["radius"]  # the backdrop's horizon dip is the body's
            try:
                img = e40ty.picture(m, cam, tint)
            finally:
                e40ty.R = kept_radius
            gn.save_png(os.path.join(out_dir, "e40-body-%s-%s.png" % (body, view)), img)
            tiles[(body, view)] = e40ty.box(img, TILE)
            words = e40ty.alpha_words(m[..., 3])
            lines.append("   %-7s %-9s: cloud covers %.2f of the picture, median alpha %.2f" % (body, view, words["cover"], words["median"]))
            metrics.append(Metric.info("e40body.%s.%s.cover" % (body, view), words["cover"]))
    for view, _, _ in VIEWS:
        cols = [e40ty.label(tiles[(b, view)], "%s: %s" % (b, view)) if (b, view) in tiles else np.zeros((TILE[1], TILE[0], 3)) for b in VARIANTS]
        gn.save_png(os.path.join(out_dir, "e40-body-%s.png" % view), np.concatenate(cols, axis=1))
    print("\n".join(lines))
    return metrics


def stages():
    if os.path.isfile(PLACE):
        write_inputs()
    return [Stage("sheet", ["-mode", "scene", "-scene", "type-sheet", "-in", INPUT], size=(1708, 960), proxy=False, timeout=2400)]


register_series("e40-body", stages(), collect, exclusive=False, who="e40-body")


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
    nadir = [j for j in jobs if j["id"] == jid("second", "above200")][0]
    expected = BODIES["second"]["radius"] + 200000.0 * BODIES["second"]["ratio"]
    assert abs(math.sqrt(sum(x * x for x in nadir["position"])) - expected) < 1.0
    home = scaled_spec("home")
    second = scaled_spec("second")
    assert abs(second["outer"] / home["outer"] - 7.5 / 10.5) < 1e-9 and abs(second["scale"] / home["scale"] - 7.5 / 10.5) < 1e-9
    print("selftest passed (%d jobs)" % len(jobs))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--prepare", nargs=6, metavar=("HOME", "SECOND", "SEED", "TIME", "LAT", "LON"))
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.prepare:
        h, s2, s, t, la, lo = a.prepare
        prepare(h, s2, int(s), int(t), float(la), float(lo))
        print("prepared", INPUT)
