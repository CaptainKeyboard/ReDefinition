"""E40 F1: the game's sky at the K-T2 site (KSC) from the generator's real maps, all four layers at once (player series e40-f1).

    python tools/weather_metrics/e40f1.py --prepare <variant> <maps-dir> <ut> [<ut> ...]   copies the maps of those times as that variant
    python tools/build_weather_player.py --series e40-f1 [--no-build]
    python tools/weather_metrics/e40f1.py --selftest

<maps-dir> is the dump of tests/ReDefinition.Tests/Atmospheres/T09/SkyMapsExplore.cs (SKYMAPS_OUT; per time and layer the cover and type bytes after the game's own FetchStep chain). Each variant
(before / after the fix) and time is one scene: the shipped Clouds-Kerbin.cfg's four layers with their soft and light keys and the real maps, the sun 45.6 degrees up, from the ground looking
north (towards the sun side; 12 degrees up and 55 degrees up) and from above (10 km altitude looking 30 degrees down; 40 km straight down: 46 km across). Pictures go to
.local/work/series-pictures/E40 as e40-f1-<variant>-u<ut>.png; the numbers (the share of the picture with no cloud, with a veil and with a closed sky) go to the log and to
build/weather-series/e40f1-results.json.
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
INPUT = os.path.join(BUILD, "e40f1-in")
PLACE = os.path.join(INPUT, "place.json")
RESULTS = os.path.join(BUILD, "e40f1-results.json")
TILES = os.path.join(BUILD, "e40f1-tiles")
TVC = {"ground": "t09-tvc1707-ground-tvc-1707x960-natural.png", "above10": "t09-tvc1707-above-tvc-1707x960-natural.png"}
R = 600000.0
MAP_W, MAP_H = 512, 256
SIZE = (1708, 960)
TILE = (854, 480)
SITE_LAT, SITE_LON = -0.0972, -74.5577
STACK = ["Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus"]
GROUND = 264.0
# view key, height above the ground (m), pitch (degrees, up positive), the part of the picture that counts as sky or ground ("up": rays above 3 degrees; "down": rays below -3)
VIEWS = [("ground", GROUND, 12.0, "up"), ("up", GROUND, 55.0, "up"), ("above10", 10000.0, -30.0, "down"), ("nadir40", 40000.0, -90.0, "down")]
CLEAR, VEIL = 0.10, 0.90          # alpha below CLEAR: no cloud; at or above VEIL: a closed sky
# variant -> tuning keys the player's jobs set (none: the player's compiled defaults, which are the product's)
TUNED = {"before": [("cloud.coverMeaning", 0)], "fallback": [("cloud.coverMeaning", 0)]}


def place():
    with open(PLACE, encoding="utf-8") as f:
        return json.load(f)


def camera(height, pitch, size):
    la, lo = math.radians(SITE_LAT), math.radians(SITE_LON)
    pos = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)]) * (R + height)
    return pr.Camera(pos, 0.0, pitch, size[0], size[1], e40ty.FOV)


def jid(variant, ut, view):
    # the type-sheet scene renders the jobs whose id starts with "ty-"
    return "ty-f1-%s-u%d-%s" % (variant, ut, view)


def map_name(variant, ut, layer, kind):
    return "%s-u%d-%s-%s.r8" % (variant, ut, layer, kind)


def scenes():
    return place()["scenes"]


def job_layer(layer, extras, variant, ut):
    lk, tk = extras[layer["name"]]
    jl = e40o.job_layer(layer, map_name(variant, ut, layer["name"], "cover"), map_name(variant, ut, layer["name"], "type"))
    jl.update(lk)
    if layer["name"] == "Kerbin-cirrus" and not e40ty.NOWIND:
        lon = math.radians(SITE_LON)
        east, north = e40ty.WIND_EN["cirrus"]
        jl["wind"] = [-math.sin(lon) * east, math.cos(lon) * east, north]
    for t in jl["types"]:
        t.update(tk.get(t["name"], {}))
    return jl


def build_jobs():
    layers_by_name = {s["name"]: s for s in e40o.kerbin_layers(e40ty.CFG_PATH)}
    extras = e40ty.cfg_extras()
    jobs = []
    for variant, ut in scenes():
        for view, height, pitch, _ in VIEWS:
            cam = camera(height, pitch, SIZE)
            layers = [job_layer(layers_by_name[n], extras, variant, ut) for n in STACK]
            extra = {}
            if variant in TUNED:
                # a player built before the default changed: the keys of the product's defaults are set by the job (the same table the game's code reads)
                extra["tuning"] = e40o.tuning(TUNED[variant])
            jobs.append(lay.job(jid(variant, ut, view), "lum", cam, layers, SIZE[0], SIZE[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H,
                                sun=lay.sun_vector(cam.pos, 0.0, e40ty.SUN_ELEVATION), sunColour=[2.35, 2.35, 2.35], ambientTop=e40ty.AMBIENT_TOP, ambientBottom=e40ty.AMBIENT_BOTTOM,
                                soft=e40ty.SOFTNESS, maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3, **extra))
    return jobs


def prepare(variant, folder, uts):
    os.makedirs(INPUT, exist_ok=True)
    for ut in uts:
        for layer in STACK:
            for kind in ("cover", "type"):
                shutil.copyfile(os.path.join(folder, "u%d_%s_%s.u8" % (ut, layer, kind)), os.path.join(INPUT, map_name(variant, ut, layer, kind)))
    kept = []
    if os.path.isfile(PLACE):
        kept = [s for s in place()["scenes"] if s[0] != variant]
    with open(PLACE, "w", encoding="utf-8") as f:
        json.dump(dict(scenes=kept + [[variant, ut] for ut in uts]), f)
    write_inputs()


def write_inputs():
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def elevation_of_rays(cam):
    d = cam.ray_directions().reshape(cam.height, cam.width, 3)
    up = cam.pos / np.linalg.norm(cam.pos)
    return np.degrees(np.arcsin(np.clip(d @ up, -1.0, 1.0)))


def shares(alpha, elevation, part):
    """No cloud, a veil and a closed sky as shares of the part of the picture that counts."""
    mask = elevation > 3.0 if part == "up" else elevation < -3.0
    a = alpha[mask]
    return dict(clear=float((a < CLEAR).mean()), veil=float(((a >= CLEAR) & (a < VEIL)).mean()), closed=float((a >= VEIL).mean()), n=int(a.size))


def collect(ctx):
    sheet = ctx.folder("sheet")
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    tint = e40ty.white_tint(3000.0)
    lines, metrics, results = ["E40-F1: the game's sky at the K-T2 site from the real maps"], [], {}
    for variant, ut in scenes():
        tiles = []
        for view, height, pitch, part in VIEWS:
            path = os.path.join(sheet, jid(variant, ut, view) + ".npy")
            if not os.path.isfile(path):
                lines.append("   %-7s u%d %-8s: no picture" % (variant, ut, view))
                continue
            m = e40ty.load_lum(sheet, jid(variant, ut, view))
            cam = camera(height, pitch, SIZE)
            img = e40ty.picture(m, cam, tint)
            s = shares(m[..., 3], elevation_of_rays(cam), part)
            results["%s.u%d.%s" % (variant, ut, view)] = s
            lines.append("   %-7s u%d %-8s: no cloud %.3f, veil %.3f, closed %.3f" % (variant, ut, view, s["clear"], s["veil"], s["closed"]))
            metrics.append(Metric.info("e40f1.%s.u%d.%s.clear" % (variant, ut, view), s["clear"]))
            tiles.append(e40ty.label(e40ty.box(img, TILE), "%s u%d %s: clear %.2f veil %.2f closed %.2f" % (variant, ut, view, s["clear"], s["veil"], s["closed"])))
            os.makedirs(TILES, exist_ok=True)
            gn.save_png(os.path.join(TILES, "%s-u%d-%s.png" % (variant, ut, view)), img)
        if len(tiles) == len(VIEWS):
            top = np.concatenate(tiles[:2], axis=1)
            bottom = np.concatenate(tiles[2:], axis=1)
            gn.save_png(os.path.join(out_dir, "e40-f1-%s-u%d.png" % (variant, ut)), np.concatenate([top, bottom], axis=0))
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    return metrics


def compose(before="before", after="after", fair=1881000, clock=1686000):
    """e40-f1-compare.png: the clock scene (ground and from above, before | after | TVC start 47) and the fair scene (before | after), from the tiles the series wrote."""
    tile = (570, 320)
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    tvc_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "K-T2")
    rows = []
    for ut, name in ((clock, "clock"), (fair, "fair")):
        for view in ("ground", "above10"):
            cells = []
            for variant in (before, after):
                path = os.path.join(TILES, "%s-u%d-%s.png" % (variant, ut, view))
                cells.append(e40ty.label(e40ty.box(e40ty.load_png(path), tile), "%s: %s %s" % (variant, name, view)))
            if name == "clock":
                path = os.path.join(tvc_dir, TVC[view])
                cells.append(e40ty.label(e40ty.box(e40ty.load_png(path), tile), "TVC start 47: clock %s" % view) if os.path.isfile(path) else np.zeros((tile[1], tile[0], 3)))
            else:
                cells.append(np.zeros((tile[1], tile[0], 3)))
            rows.append(np.concatenate(cells, axis=1))
    # the fair scene as the game showed it (every layer carries the field's cover): before | after
    for view in ("ground", "above10"):
        cells = []
        for variant in ("fallback", "fbafter"):
            path = os.path.join(TILES, "%s-u%d-%s.png" % (variant, fair, view))
            cells.append(e40ty.label(e40ty.box(e40ty.load_png(path), tile), "%s: fair %s (every layer the field's cover)" % (variant, view)))
        cells.append(np.zeros((tile[1], tile[0], 3)))
        rows.append(np.concatenate(cells, axis=1))
    path = os.path.join(out_dir, "e40-f1-compare.png")
    gn.save_png(path, np.concatenate(rows, axis=0))
    print("wrote", path)


def stages():
    if os.path.isfile(PLACE):
        write_inputs()
    return [Stage("sheet", ["-mode", "scene", "-scene", "type-sheet", "-in", INPUT], size=(1708, 960), proxy=False, timeout=3600)]


register_series("e40-f1", stages(), collect, exclusive=False, who="e40-f1")


def selftest():
    os.makedirs(INPUT, exist_ok=True)
    kept = open(PLACE, encoding="utf-8").read() if os.path.isfile(PLACE) else None
    with open(PLACE, "w", encoding="utf-8") as f:
        json.dump(dict(scenes=[["before", 1881000], ["after", 1881000]]), f)
    try:
        jobs = build_jobs()
    finally:
        if kept is None:
            os.remove(PLACE)
        else:
            with open(PLACE, "w", encoding="utf-8") as f:
                f.write(kept)
    ids = [j["id"] for j in jobs]
    assert len(ids) == len(set(ids)) == 2 * len(VIEWS)
    assert all(len(j["layers"]) == len(STACK) and j["mapWidth"] == MAP_W for j in jobs)
    nadir = [j for j in jobs if j["id"] == jid("after", 1881000, "nadir40")][0]
    assert abs(math.sqrt(sum(x * x for x in nadir["position"])) - (R + 40000.0)) < 1.0
    cam = camera(GROUND, 55.0, SIZE)
    el = elevation_of_rays(cam)
    assert abs(el[SIZE[1] // 2, SIZE[0] // 2] - 55.0) < 0.5
    flat = shares(np.zeros(el.shape), el, "up")
    assert flat["clear"] == 1.0 and flat["closed"] == 0.0
    print("selftest passed (%d jobs)" % len(jobs))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--prepare", nargs="+", metavar=("VARIANT", "MAPS"))
    parser.add_argument("--compose", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.compose:
        compose()
    elif a.prepare:
        prepare(a.prepare[0], a.prepare[1], [int(x) for x in a.prepare[2:]])
        print("prepared", INPUT)
