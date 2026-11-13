"""E40 follow-up: the base floor of the height variation (LayerGeometry.LocalBaseTop, BaseHeight.cginc) in the weather player, no game start.

  python tools/build_weather_player.py --series e40-lfix        build the player, run the jobs, print the table
  python tools/build_weather_player.py --series e40-lfix --no-build
  python tools/weather_metrics/e40lfix.py --selftest

The rain deck L0 (Kerbin-weather-rain, the Fog and Stratus blend 96 to 594 m at type 0.229) at the high pick (map 0.859), alone, camera 264 m above the sea looking 18 degrees up
(and straight up for the zenith). The variants carry the height variation through <input>/heights.json (HeightHook, the mod's own HeightMapBuilder):
  ctl  no entry (no height maps): the BEFORE case, the old numbers
  off  all four switches off (maps of zeros)
  dry  a dry place: root +500 m, top term +1200 m (the base stays above the floor)
  wet  the humid-place maximum: root -500 m (byte -104 of 127 of a 610 m range: -499.5), top term -144 m (an updraft of 0: byte -15 of 127 of 1200: -142 m), no variation, no ragged unit

EXPECTED RESULTS, written before the first run (the unit test CloudInsideDeckTests gave: wet deck 0 to 355 m with the floor, -404 to -49 m without it; tau of the 18 degree ray through
the wet deck 4.62 noise free; zenith tau of the deck at the high pick, min over 8 sites of the oracle, 8.77):
  F1  compiled: the shader with the three added lines compiles (the player builds); a compile error would fail the build.
  F2  ctl, post and pre fix: closed share (tau >= 0.92) 1.000 at 8 sites, zenith tau >= 8.77 (the camera lies inside the deck 96 to 594 m; the part above the camera is 330 m).
  F3  wet, PRE fix (b7ed89b3): the deck lies at -404 to -49 m, under the ground: closed share under 0.05, zenith tau under 0.05 (the game's 0.000).
  F4  wet, POST fix: the deck lies at 0 to about 355 m, the camera (264 m) is inside it with 91 m of deck above: closed share at 18 degrees at least 0.95 (CPU 4.62 noise free), median tau
      of the sky 3.5 to 5.5, zenith tau 1.0 to 2.0 (4.62 times sin 18 degrees = 1.43; it is not 8.77: the lifted deck is thinner above the camera).
  F5  off and dry, post against pre: tau planes of the aux jobs identical (maximum difference 0); the natural pictures of ctl, off, dry and of the four-layer stack identical (0 pixels
      differ); because the base stays above the floor (dry) or the offsets are zero (off) the shader's lift is 0.
  F6  wet, post: the aid picture shows a closed grey deck over the whole sky; pre: the aid picture is clear.
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
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import e40o  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40lfix-in")
RESULTS = os.path.join(BUILD, "e40lfix-results.json")
e40o.INPUT = INPUT      # the constant maps of e40o are written into this series' input folder

R = e40o.R
MAP_W, MAP_H = e40o.MAP_W, e40o.MAP_H
SIZE = e40o.SIZE
SITES = e40o.SITES
RAIN = "Kerbin-weather-rain"
MAP, TYPE = 0.859, 0.229
VARIANTS = ("ctl", "off", "dry", "wet")
PIC = (1280, 720)
STACK = (640, 360)
TAG = os.environ.get("E40_LFIX_TAG", "post")


def raw_map(name, fn):
    """An R8 map of the job's map size (8 x 4, row 0 = north); fn(column, row) -> 0..1."""
    os.makedirs(INPUT, exist_ok=True)
    data = np.zeros(MAP_W * MAP_H, dtype=np.uint8)
    for r in range(MAP_H):
        for c in range(MAP_W):
            data[r * MAP_W + c] = int(round(min(max(fn(c, r), 0.0), 1.0) * 255.0))
    data.tofile(os.path.join(INPUT, name))
    return name


def in_block(c, r):
    """The 8 sites lie at longitude 0 to 70 degrees on the equator: columns 2 to 5 (longitude -90 to 90) and rows 1 and 2 (latitude -45 to 45) hold all of them."""
    return 2 <= c <= 5 and 1 <= r <= 2


def write_maps():
    raw_map("hum-wet.r8", lambda c, r: 1.0 if in_block(c, r) else 0.3)
    raw_map("hum-dry.r8", lambda c, r: 0.05 if in_block(c, r) else 0.9)
    raw_map("up-0.r8", lambda c, r: 0.0)
    raw_map("up-12.r8", lambda c, r: 1.0)


def spec(jid, variant):
    base = dict(id=jid, variation=0, physics=0, topUpdraft=0, ragged=0, topJitter=0.0)
    if variant == "wet":
        # HeightHook caches the built maps by switches, layer and seed only: each variant with maps needs its own seed.
        base.update(physics=1, topUpdraft=1, humidityMap="hum-wet.r8", updraftMap="up-0.r8", seed=2)
    elif variant == "dry":
        base.update(physics=1, topUpdraft=1, humidityMap="hum-dry.r8", updraftMap="up-12.r8")
    return base


def deck_layer():
    s = {x["name"]: x for x in e40o.kerbin_layers()}
    return e40o.job_layer(s[RAIN], e40o.const_map(MAP), e40o.const_map(TYPE))


def build_jobs():
    write_maps()
    specs_by_name = {x["name"]: x for x in e40o.kerbin_layers()}
    jobs, heights = [], []

    def add(job, variant):
        jobs.append(job)
        if variant != "ctl":
            heights.append(spec(job["id"], variant))

    common = dict(planet=R, maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H)
    for v in VARIANTS:
        for site in range(SITES):
            add(lay.job("lf-%s-p%d" % (v, site), "aux", e40o.camera(site), [deck_layer()], SIZE[0], SIZE[1], **common), v)
            zen = pr.Camera(e40o.site_position(site), 0.0, 90.0, 4, 4, 2.0)
            add(lay.job("lz-%s-p%d" % (v, site), "aux", zen, [deck_layer()], 4, 4, **common), v)
    cam = e40o.camera(0, PIC)
    sun = lay.sun_vector(cam.pos, 120.0, 40.0)
    for v in VARIANTS:
        add(lay.job("pic-%s" % v, "color", cam, [deck_layer()], PIC[0], PIC[1], sun=sun, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1, **common), v)
    # The four layers of Kerbin together at the high pick (maps 0.8595 / 0.2058 / 0.6609 / 0.1774, types 0.229): the identity check of the stack.
    cam2 = e40o.camera(0, STACK)
    sun2 = lay.sun_vector(cam2.pos, 120.0, 40.0)
    for v in ("ctl", "off", "dry"):
        stack = [e40o.job_layer(specs_by_name[l["name"]], e40o.const_map(m), e40o.const_map(0.229)) for l, m in zip(e40o.kerbin_layers(), e40o.PICKS["high"]["maps"])]
        add(lay.job("stk-%s" % v, "color", cam2, stack, STACK[0], STACK[1], sun=sun2, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1, **common), v)
    return jobs, heights


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs, heights = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": heights}, f)
    return jobs


def mean_sd(values):
    a = np.asarray(values, dtype=np.float64)
    return float(a.mean()), float(a.std())


def sky_tau(folder, jid, site):
    tau = np.load(os.path.join(folder, "%s-tau.npy" % jid)).astype(np.float64)
    mask, _ = e40o.sky_mask(site)
    return tau[mask]


def collect(ctx):
    folder = ctx.folder("layers")
    metrics, lines, results = [], [], {"tag": TAG}
    for v in VARIANTS:
        closed = [float((sky_tau(folder, "lf-%s-p%d" % (v, s), s) >= e40o.CLOSED).mean()) for s in range(SITES)]
        med = [float(np.median(sky_tau(folder, "lf-%s-p%d" % (v, s), s))) for s in range(SITES)]
        zen = [float(np.load(os.path.join(folder, "lz-%s-p%d-tau.npy" % (v, s))).astype(np.float64).mean()) for s in range(SITES)]
        results[v] = dict(closed=closed, median_tau=med, zenith=zen)
        lines.append("%s [%s]: closed share %.3f +- %.3f (min %.3f), median tau of the sky %.2f, zenith tau mean %.2f min %.2f max %.2f" % (
            v, TAG, *mean_sd(closed), min(closed), float(np.mean(med)), float(np.mean(zen)), min(zen), max(zen)))
        metrics.append(Metric.info("lfix.%s.closed" % v, float(np.mean(closed))))
        metrics.append(Metric.info("lfix.%s.median_tau" % v, float(np.mean(med))))
        metrics.append(Metric.info("lfix.%s.zenith_min" % v, min(zen)))
        metrics.append(Metric.info("lfix.%s.zenith_mean" % v, float(np.mean(zen))))
    # Expectations of F2, F3 and F4 for this build.
    if TAG == "post":
        metrics.append(Metric.at_least("lfix.F2.ctl_closed", float(np.mean(results["ctl"]["closed"])), 0.999))
        metrics.append(Metric.at_least("lfix.F2.ctl_zenith_min", min(results["ctl"]["zenith"]), 8.77))
        metrics.append(Metric.at_least("lfix.F4.wet_closed", float(np.mean(results["wet"]["closed"])), 0.95))
        metrics.append(Metric.at_least("lfix.F4.wet_median_tau", float(np.mean(results["wet"]["median_tau"])), 3.5))
        metrics.append(Metric.at_most("lfix.F4.wet_median_tau_max", float(np.mean(results["wet"]["median_tau"])), 5.5))
        metrics.append(Metric.at_least("lfix.F4.wet_zenith_min", min(results["wet"]["zenith"]), 1.0))
        metrics.append(Metric.at_most("lfix.F4.wet_zenith_max", max(results["wet"]["zenith"]), 2.0))
    else:
        metrics.append(Metric.at_most("lfix.F3.wet_closed", float(np.mean(results["wet"]["closed"])), 0.05))
        metrics.append(Metric.at_most("lfix.F3.wet_zenith_max", max(results["wet"]["zenith"]), 0.05))
    # Keep everything the comparison between the two builds needs.
    main = e40o.main_repo()
    keep = os.path.join(main, ".local", "work", "LFIXlogs", TAG)
    os.makedirs(keep, exist_ok=True)
    pics = os.path.join(main, ".local", "work", "series-pictures", "E40")
    os.makedirs(pics, exist_ok=True)
    copied = []
    for name in sorted(os.listdir(folder)):
        if name.endswith(".png"):
            shutil.copyfile(os.path.join(folder, name), os.path.join(keep, name))
            if name.startswith("pic-"):
                target = os.path.join(pics, "e40-lfix-%s-%s" % (TAG, name))
                shutil.copyfile(os.path.join(folder, name), target)
                copied.append(target)
        elif name.endswith("-tau.npy"):
            shutil.copyfile(os.path.join(folder, name), os.path.join(keep, name))
    results["pictures"] = copied
    results["lines"] = lines
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    shutil.copyfile(RESULTS, os.path.join(keep, "results.json"))
    print("\n".join(lines))
    print("pictures: %d copied to %s" % (len(copied), pics))
    return metrics


def stages():
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")) or os.path.getmtime(os.path.join(INPUT, "jobs.json")) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("layers", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False, timeout=1800)]


register_series("e40-lfix", stages(), collect, exclusive=False, who="e40-lfix")


def selftest():
    jobs, heights = build_jobs()
    ids = [j["id"] for j in jobs]
    assert len(ids) == len(set(ids)), "job ids are unique"
    assert not any(h["id"].endswith("-ctl") or "-ctl-" in h["id"] for h in heights), "the control has no entry"
    assert sum(1 for h in heights if h["id"].startswith("lf-wet")) == SITES
    w = spec("x", "wet")
    assert w["variation"] == 0 and w["ragged"] == 0 and w["physics"] == 1 and w["topJitter"] == 0.0
    print("selftest passed (%d jobs, %d height entries)" % (len(jobs), len(heights)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--write", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.write:
        print(len(write_inputs()), "jobs")
