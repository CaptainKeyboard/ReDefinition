"""LV3: the lateral spread of the direct light (cloud.lightVolume.spread) judged on replays of pinned GAME captures (master plan 32, wolken-m2a-technik.md 11.7), the player's clouds
composited over the game's own frame.

    python tools/weather_metrics/lv3.py --prepare                       (the replay jobs: each capture x each variant, tuning keys added to the game's own)
    python tools/build_weather_player.py --series lv3-pic --no-build    (the replays: 64 records from the history cut, as the game drew them)
    python tools/build_weather_player.py --series lv3-cost --no-build   (the same replays, then the last record drawn 60 times more, 40 timed; six alternating rounds)
    python tools/weather_metrics/lv3.py --pictures <folder>             (the composites at the pass's 1707 x 960, upscaled to 2560 x 1440, and 1:1 crops)

Captures (series-pictures/K-T2/maps): back-lit ground at the M0 place (ut 1486800, 27.5 degrees off the sun), front-lit ground (ut 1686000, sun behind the camera, start
perfNpic2560dlssqpin1686) and the view from above (ut 1486800).
"""
import argparse
import glob
import json
import os
import shutil
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40pp  # noqa: E402
from weather_metrics import m0pin4  # noqa: E402

MAPS = os.path.join(e40pp.MAIN, ".local", "work", "series-pictures", "K-T2", "maps")
INPUT = os.path.join(e40pp.BUILD, "lv3-in")
CAPTURES = [
    ("ground-backlit-u1486800", "pin-ground-u1486800-perfNpic2560dlssqpin4-20261120121446"),
    ("ground-frontlit-u1686000", "pin-ground-u1686000-perfNpic2560dlssqpin1686-20261120132521"),
    ("above-u1486800", "pin-above-u1486800-perfNpic2560dlssqpin4-20261120121602"),
]

VOL = [("cloud.lightVolume", 1.0)]


def keys(pairs):
    return VOL + pairs


# name -> (what, tuning keys added to the game's). The spread variants keep the full sun march everywhere (hand-over at infinity) unless a hand-over is named.
VARIANTS = {
    "today": ("today's light (the volume off)", []),
    "lv2-4km": ("LV2's default far light at a 4 km hand-over, no spread", keys([("cloud.lightVolume.handOverKm", 4.0)])),
    "spread-blur400": ("spread from the volume blurred over 400 m (sigma), weight over tau 4", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 1.0),
                                                                                                    ("cloud.lightVolume.spreadRadiusM", 400.0)])),
    "spread-blur800": ("spread from the volume blurred over 800 m", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 1.0),
                                                                          ("cloud.lightVolume.spreadRadiusM", 800.0)])),
    "spread-blur400-depth2": ("blur 400 m, the weight grows over tau 2", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 1.0),
                                                                                ("cloud.lightVolume.spreadRadiusM", 400.0), ("cloud.lightVolume.spreadDepth", 2.0)])),
    "spread-farcascade": ("spread from the far cascade (1250 m voxels)", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 2.0)])),
    "spread-propagation": ("spread from the propagation fill as filled", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 3.0),
                                                                              ("cloud.lightVolume.fill", 1.0), ("cloud.lightVolume.storeK", 8.0)])),
    "spread-blur400-vox156": ("blur 400 m on a near cascade of 156 m voxels", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 1.0),
                                                                                    ("cloud.lightVolume.spreadRadiusM", 400.0), ("cloud.lightVolume.voxelM", 156.25)])),
    "spread-blur400-lv8km": ("blur 400 m with the far light from 8 km", keys([("cloud.lightVolume.handOverKm", 8.0), ("cloud.lightVolume.spread", 1.0),
                                                                              ("cloud.lightVolume.spreadRadiusM", 400.0)])),
    # Round 2, after the first pictures (the blur and the far cascade glow from inside and flatten the front-lit cloud; propagation softens the zones and keeps the volume):
    "spread-blur150-max50": ("blur 150 m, at most half the weight", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 1.0),
                                                                          ("cloud.lightVolume.spreadRadiusM", 150.0), ("cloud.lightVolume.spreadMax", 0.5)])),
    "spread-blur400-tau": ("blur 400 m of the optical depth (not of transmittance)", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 1.0),
                                                                                          ("cloud.lightVolume.spreadRadiusM", 400.0), ("cloud.lightVolume.storeK", 0.0)])),
    "spread-propagation-max60": ("propagation, at most 60 % weight", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 3.0),
                                                                           ("cloud.lightVolume.fill", 1.0), ("cloud.lightVolume.storeK", 8.0), ("cloud.lightVolume.spreadMax", 0.6)])),
    "spread-propagation-depth8": ("propagation, the weight grows over tau 8", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 3.0),
                                                                                    ("cloud.lightVolume.fill", 1.0), ("cloud.lightVolume.storeK", 8.0),
                                                                                    ("cloud.lightVolume.spreadDepth", 8.0)])),
    "spread-propagation-vox156": ("propagation on a near cascade of 156 m voxels", keys([("cloud.lightVolume.handOverKm", -1.0), ("cloud.lightVolume.spread", 3.0),
                                                                                         ("cloud.lightVolume.fill", 1.0), ("cloud.lightVolume.storeK", 8.0),
                                                                                         ("cloud.lightVolume.voxelM", 156.25)])),
    "spread-propagation-lv8km": ("propagation spread with its far light from 8 km", keys([("cloud.lightVolume.handOverKm", 8.0), ("cloud.lightVolume.spread", 3.0),
                                                                                          ("cloud.lightVolume.fill", 1.0), ("cloud.lightVolume.storeK", 8.0)])),
    "spread-propagation-lv8km-capped": ("as before, the far read capped at the march's length", keys([("cloud.lightVolume.handOverKm", 8.0), ("cloud.lightVolume.spread", 3.0),
                                                                                                       ("cloud.lightVolume.fill", 1.0), ("cloud.lightVolume.storeK", 8.0),
                                                                                                       ("cloud.lightVolume.capRead", 1.0)])),
    "spread-propagation-vox156-lv8km": ("the recommendation: propagation spread on 156 m voxels with its far light from 8 km",
                                        keys([("cloud.lightVolume.handOverKm", 8.0), ("cloud.lightVolume.spread", 3.0), ("cloud.lightVolume.fill", 1.0),
                                              ("cloud.lightVolume.storeK", 8.0), ("cloud.lightVolume.voxelM", 156.25)])),
}
COST_VARIANTS = ("today", "lv2-4km", "spread-blur400", "spread-farcascade", "spread-propagation", "spread-propagation-vox156", "spread-propagation-lv8km",
                 "spread-propagation-vox156-lv8km")


def capture(folder):
    path = os.path.join(MAPS, folder)
    if not os.path.isdir(path):
        raise SystemExit("capture missing: " + path)
    return path


def prepare():
    caps = [capture(f) for _, f in CAPTURES]
    tmp = INPUT + "-base"
    e40pp.prepare_pin(caps, out=tmp)
    os.makedirs(INPUT, exist_ok=True)
    for fn in os.listdir(tmp):
        if fn not in ("jobs.json", "heights.json", "pin-index.json"):
            shutil.copyfile(os.path.join(tmp, fn), os.path.join(INPUT, fn))
    with open(os.path.join(tmp, "jobs.json"), encoding="utf-8") as f:
        jobs = json.load(f)["jobs"]
    with open(os.path.join(tmp, "heights.json"), encoding="utf-8") as f:
        specs = {s["id"]: s for s in json.load(f)["specs"]}
    with open(os.path.join(tmp, "pin-index.json"), encoding="utf-8") as f:
        index = {i["job"]: i for i in json.load(f)}
    out_jobs, out_specs, out_index = [], [], []
    for n, (view, _) in enumerate(CAPTURES):
        base = jobs[n]
        for name, (_, pairs) in VARIANTS.items():
            job = json.loads(json.dumps(base))
            job["id"] = "pin-%s-%s" % (view, name)
            tuning = {t["key"]: t["value"] for t in job.get("tuning", [])}
            for k, v in pairs:
                tuning[k] = float(v)
            job["tuning"] = [dict(key=k, value=v) for k, v in sorted(tuning.items())]
            out_jobs.append(job)
            spec = dict(specs[base["id"]])
            spec["id"] = job["id"]
            out_specs.append(spec)
            item = dict(index[base["id"]])
            item["job"] = job["id"]
            item["view"], item["variant"] = view, name
            out_index.append(item)
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": out_jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": out_specs}, f)
    with open(os.path.join(INPUT, "pin-index.json"), "w", encoding="utf-8") as f:
        json.dump(out_index, f, indent=1)
    print("%d jobs in %s" % (len(out_jobs), INPUT))


def index():
    path = os.path.join(INPUT, "pin-index.json")
    if not os.path.isfile(path):
        return []
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def cost_input():
    """The cost runs replay the cost variants only (a second input folder with those jobs)."""
    folder = INPUT + "-cost"
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")):
        return None
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(INPUT, "jobs.json"), encoding="utf-8") as f:
        jobs = [j for j in json.load(f)["jobs"] if j["id"].rsplit("-", 0)[0] and any(j["id"].endswith("-" + v) for v in COST_VARIANTS)]
    for fn in os.listdir(INPUT):
        if fn != "jobs.json" and not os.path.exists(os.path.join(folder, fn)):
            shutil.copyfile(os.path.join(INPUT, fn), os.path.join(folder, fn))
    with open(os.path.join(folder, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return folder


def stages(cost):
    idx = index()
    if not idx:
        return []
    size = (idx[0]["width"], idx[0]["height"])
    if not cost:
        return [Stage("replay", ["-mode", "scene", "-scene", "pin-replay", "-in", INPUT, "-repeat", "1"], size=size, proxy=False, timeout=3600)]
    rounds = int(os.environ.get("ATMO_COST_ROUNDS", "6") or 6)
    folder = cost_input()
    return [Stage("cost", ["-mode", "scene", "-scene", "pin-replay", "-in", folder, "-repeat", "1", "-pinTime", str(rounds)], size=size, proxy=True, timeout=1200 + 900 * rounds)]


def collect_pic(ctx):
    return [Metric.info("lv3.replays", float(sum(1 for _ in glob.glob(os.path.join(ctx.folder("replay"), "*-r0-cloud.npy")))))]


def collect_cost(ctx):
    rows = {}
    for k, v in ctx.player_metrics("cost").items():
        if not k.startswith("pintime."):
            continue
        _, part, job = k.split(".", 2)
        rows.setdefault(job, {})[part] = v[0]
    lines = ["LV3 cost on the pinned replays (pass size): march | fill + blur | sum (ms; medians; min-max of the rounds' sums)"]
    metrics = []
    for view, _ in CAPTURES:
        lines.append("  " + view)
        for name in COST_VARIANTS:
            r = rows.get("pin-%s-%s" % (view, name))
            if r is None:
                lines.append("    %-24s missing" % name)
                continue
            sums = sorted(r[p] for p in r if p.startswith("sumr"))
            lines.append("    %-24s %7.2f | %6.2f | %7.2f (%s, %d rounds)" % (name, r["march"], r["fill"], r["march"] + r["fill"],
                                                                               "%.2f-%.2f" % (sums[0], sums[-1]) if sums else "-", len(sums)))
            metrics.append(Metric.info("lv3.%s.%s" % (view, name), r["march"] + r["fill"]))
    print("\n".join(lines))
    return metrics


register_series("lv3-pic", stages(False), collect_pic, exclusive=False, who="lv3")
register_series("lv3-cost", stages(True), collect_cost, exclusive=True, who="lv3")

# 1:1 crop windows (x, y, w, h at the pass's 1707 x 960) per view: the big cumulus and a mid-distance cumulus.
CROPS = {}


def composite_of(root, job, cap):
    cloud = os.path.join(root, "lv3-pic-replay", job + "-r0-cloud.npy")
    if not os.path.isfile(cloud):
        return None
    with open(os.path.join(cap, "pass.json"), encoding="utf-8") as f:
        pj = json.load(f)
    scene = m0pin4.npy(os.path.join(cap, "scene.npy"))
    return m0pin4.composite(m0pin4.npy(cloud), scene, pj)


def save(img, path, size=None):
    from PIL import Image
    pic = Image.fromarray((np.clip(img[..., :3], 0, 1) * 255 + 0.5).astype("uint8"))
    if size is not None:
        pic = pic.resize(size, Image.BICUBIC)
    pic.save(path)


def pictures(out, root):
    os.makedirs(out, exist_ok=True)
    lines = []
    for view, folder in CAPTURES:
        cap = capture(folder)
        ref = composite_of(root, "pin-%s-today" % view, cap)
        game = m0pin4.npy(os.path.join(cap, "composed.npy"))
        save(game, os.path.join(out, "LV3-%s-game-own-composite-2560x1440.png" % view), (2560, 1440))
        for name in VARIANTS:
            img = composite_of(root, "pin-%s-%s" % (view, name), cap)
            if img is None:
                lines.append("missing %s %s" % (view, name))
                continue
            save(img, os.path.join(out, "LV3-%s-%s-player-on-game-frame-2560x1440.png" % (view, name)), (2560, 1440))
            for cname, (x, y, w, h) in CROPS.get(view, {}).items():
                save(img[y:y + h, x:x + w], os.path.join(out, "LV3-%s-%s-crop-%s-1to1-pass-pixels.png" % (view, name, cname)))
            if ref is not None and name != "today":
                d = np.abs(img[..., :3] - ref[..., :3])
                lines.append("%-26s %-24s mean |diff| against today %.5f" % (view, name, d.mean()))
    print("\n".join(lines))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--prepare", action="store_true")
    p.add_argument("--pictures")
    p.add_argument("--crops", help="json {view: {name: [x, y, w, h]}} at the pass's size")
    p.add_argument("--root", default=e40pp.BUILD.replace("weather-series", "WeatherPlayer"))
    a = p.parse_args()
    if a.crops:
        CROPS.update(json.loads(a.crops))
    if a.prepare:
        prepare()
    if a.pictures:
        pictures(a.pictures, a.root)
    return 0


if __name__ == "__main__":
    sys.exit(main())
