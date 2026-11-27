"""The sky from the weather map (master plan section 34): what it costs, measured in the weather player on replays of the game's own pinned captures.

    python tools/weather_metrics/wms_cost.py --prepare "<maps glob of one or more starts>"   the captures (plain and the wms sets) as cost jobs
    python tools/build_weather_player.py --series wms-cost --no-build                        one run of two alternating rounds (exclusive GPU lock); three runs give six
    python tools/weather_metrics/wms_cost.py --report                                        march ms per capture over every wms-cost run, today against the map sky

Each capture (pin-<view>[-<set>]-u<ut>-<start>-<utc>, written by T09ProbePin with OwnCloudModule's dump) becomes one job with everything the game ran with: the layer maps, the
height maps with their ranges, the tuning keys in force (cloud.weatherMapSky and its rule keys for the sets), the camera, the sun and the
game's depth, resampled to the cost size 1707 x 960 as WM1 measured. Ids: wms-<ut>-<view>-<set> (set plain = today).
"""
import argparse
import glob
import json
import os
import re
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
COST_IN = os.path.join(BUILD, "wms-cost-in")
COST_SIZE = (1707, 960)
ROUNDS = int(os.environ.get("WMS_ROUNDS", "2") or 2)
TAG = re.compile(r"^pin-(?P<view>ground|inside|above|sky3)(?:-(?P<set>[A-Za-z0-9]+))?-u(?P<ut>\d+)-(?P<start>[^-]+)-(?P<utc>\d{14})$")


def prepare(pattern, sets=("plain", "wms")):
    from weather_metrics import e40pp
    caps = {}
    for path in sorted(glob.glob(pattern)):
        m = TAG.match(os.path.basename(os.path.normpath(path)))
        if m and os.path.isfile(os.path.join(path, "pass.json")) and (m.group("set") or "plain") in sets:
            caps[(m.group("ut"), m.group("view"), m.group("set") or "plain")] = path
    if not caps:
        raise SystemExit("no captures match " + pattern)
    keys = sorted(caps)
    jobs = e40pp.prepare_pin([caps[k] for k in keys], out=COST_IN)
    for (ut, view, s), job in zip(keys, jobs):
        # the wm-cost scene takes only jobs whose id begins with "wm-"
        job["id"] = "wm-wms-%s-%s-%s" % (ut, view, s)
        for k in ("recordCamera", "recordRotation", "recordPlanetCenter", "hostJitter"):
            job.pop(k, None)
        if job.get("depthFile"):
            d = np.load(os.path.join(COST_IN, job["depthFile"]))
            yy = (np.arange(COST_SIZE[1]) * d.shape[0] / COST_SIZE[1]).astype(int)
            xx = (np.arange(COST_SIZE[0]) * d.shape[1] / COST_SIZE[0]).astype(int)
            name = job["depthFile"].replace(".npy", "-%dx%d.npy" % COST_SIZE)
            np.save(os.path.join(COST_IN, name), d[yy][:, xx].astype(np.float32))
            job["depthFile"] = name
        job["width"], job["height"] = COST_SIZE
        sky = [t for t in job.get("tuning", []) if t["key"].startswith("cloud.weatherMapSky")]
        print("%-28s <- %s  (%s)" % (job["id"], os.path.basename(caps[(ut, view, s)]), ", ".join("%s %g" % (t["key"], t["value"]) for t in sky) or "switch off"))
    with open(os.path.join(COST_IN, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)


def collect_cost(ctx):
    return [Metric.info(k, v[0]) for k, v in sorted(ctx.player_metrics("cost").items()) if k.startswith("wmcost.")]


register_series("wms-cost", [Stage("cost", ["-mode", "scene", "-scene", "wm-cost", "-in", COST_IN, "-rounds", str(ROUNDS)], size=COST_SIZE, proxy=True, timeout=2400)],
                collect_cost, exclusive=True, who="wms-cost")


def report():
    per = {}
    runs = sorted(glob.glob(os.path.join(BUILD, "wms-cost-*.json")))
    for p in runs:
        with open(p, encoding="utf-8") as f:
            for r in json.load(f):
                if r["metric"].startswith("wmcost.marchr"):
                    _, _, rest = r["metric"].split(".", 2)
                    per.setdefault(rest, []).append(r["value"])
    print("wms-cost: march ms per round over %d runs (median, min-max, n); ratio = set / plain of the same view" % len(runs))
    for key in sorted(per):
        jid, light = key.rsplit(".", 1)
        xs = per[key]
        med = float(np.median(xs))
        base = per.get(re.sub(r"-[^-]+$", "-plain", jid) + "." + light)
        ratio = med / float(np.median(base)) if base else float("nan")
        print("  %-30s %-8s %7.2f (%6.2f-%6.2f, n %d)  ratio %.2f" % (jid, light, med, min(xs), max(xs), len(xs), ratio))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--prepare", metavar="GLOB")
    ap.add_argument("--sets", default="plain,wms", help="the capture sets that become jobs (plain = today)")
    ap.add_argument("--report", action="store_true")
    a = ap.parse_args()
    if a.prepare:
        prepare(a.prepare, tuple(a.sets.split(",")))
    if a.report:
        report()


if __name__ == "__main__":
    main()
