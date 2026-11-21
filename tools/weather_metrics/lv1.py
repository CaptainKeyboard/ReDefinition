"""LV1: the light volume (cloud.lightVolume, src/Atmospheres/Render/LightCache/LightVolume.cs) in the weather player: its cost against today's march and the old light cache, and its pictures
against "full light everywhere" (wolken-m2a-technik.md section 11.3).

    python tools/weather_metrics/lv1.py --prepare                         (the views' jobs from e40pp's inputs, at 1707 x 960 and 2560 x 1440)
    python tools/build_weather_player.py --series lv1-cost --no-build     (march and fill ms, six alternating rounds; ATMO_COST_ROUNDS overrides)
    python tools/build_weather_player.py --series lv1-pic --no-build      (the frames at 2560 x 1440: still pairs, the moving camera, the rising sun)
    python tools/weather_metrics/lv1.py --pictures <folder>               (the PNGs of the last lv1-pic run, with speaking names)

The views are e40pp's gclock place (Kerbin, the map's cover, the single-cloud lattice as in every puff job), moved as e40puff's variants do. Every march case is the accepted march of the P0c
measurement (tr_base) plus the SoftTable tokens of the light volume: lv<km> on with that hand-over distance (lvinf: on at infinity), lvn<N> slices per frame (0: all), lvt<N> far near taps,
lvw<N> warp factor; lcache3 is the old light cache.
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40pp  # noqa: E402
from weather_metrics import e40puff  # noqa: E402
from weather_metrics import e40ty  # noqa: E402

BASE = "seq1+lq2+c256+cmin6+cnsg+cel+ctap8+blk2+bacc+chm+lp100"
INPUT = os.path.join(e40pp.BUILD, "lv1-in")
INPUT_PIC = os.path.join(e40pp.BUILD, "lv1-in-2560")
COST_SIZE = (1707, 960)
PIC_SIZE = (2560, 1440)
PLACE = "gclock"

# name -> (what, the change of the job). Low sun 10 to 15 degrees behind the camera (front light, the shadows between the heads show), high sun 60 degrees.
VIEWS = {
    "lv_g10": ("ground, 30 m, pitch 4, sun 10 behind", e40puff.compose(e40puff.camera_at(30.0, 4.0), e40puff.sun_behind(10.0))),
    "lv_g60": ("ground, 30 m, pitch 4, sun 60 behind", e40puff.compose(e40puff.camera_at(30.0, 4.0), e40puff.sun_behind(60.0))),
    "lv_a15": ("above, 9 km, pitch -15, sun 15 behind", e40puff.compose(e40puff.camera_at(9000.0, -15.0), e40puff.sun_behind(15.0))),
    "lv_d15": ("straight down from 15 km, sun 15", e40puff.compose(e40puff.camera_at(15000.0, -90.0), e40puff.sun_behind(15.0))),
    "lv_d60": ("straight down from 15 km, sun 60", e40puff.compose(e40puff.camera_at(15000.0, -90.0), e40puff.sun_behind(60.0))),
}
COST_VIEWS = ("lv_g10", "lv_g60", "lv_d15", "lv_d60")
PIC_VIEWS = ("lv_g10", "lv_a15", "lv_d15")

# The cost cases around a centre (hand-over 4 km, 4 slices a frame, 3 far near taps): each lever varied alone, plus today and the old cache.
CENTRE = "+lv4+lvn4+lvt3"
COST_CASES = [
    ("today", ""),
    ("old cache", "+lcache3"),
    ("lv 1000 km", "+lv1000+lvn4+lvt3"),
    ("lv 16 km", "+lv16+lvn4+lvt3"),
    ("lv 8 km", "+lv8+lvn4+lvt3"),
    ("lv 4 km", CENTRE),
    ("lv 2 km", "+lv2+lvn4+lvt3"),
    ("lv 4 km N all", "+lv4+lvn0+lvt3"),
    ("lv 4 km N 8", "+lv4+lvn8+lvt3"),
    ("lv 4 km N 2", "+lv4+lvn2+lvt3"),
    ("lv 4 km taps 1", "+lv4+lvn4+lvt1"),
    ("lv 4 km taps 0", "+lv4+lvn4+lvt0"),
]

# The still pairs: full light everywhere (today), the volume on at infinity (must be the same picture), and each hand-over distance (N 8: every slice refreshed several times over the
# 40 frames of a still view).
PIC_STILL = [("full", ""), ("lvinf", "+lvinf+lvn8"), ("lv1000km", "+lv1000+lvn8"), ("lv16km", "+lv16+lvn8"), ("lv8km", "+lv8+lvn8"), ("lv4km", "+lv4+lvn8"),
             ("lv2km", "+lv2+lvn8")]
# The diagnosis at 4 km (round 1 found the fill's 24 fine taps darker than the march's own layout, now the default): the light cache's fill layout (24 taps, growth 1.15), the near
# cascade alone, a near voxel of 156 m with 64 slices up, no far near taps; full-repeat is today's march once more under another name (the player's case-to-case noise floor).
PIC_DIAG = [("full-repeat", "+ctl"), ("lv4km-fill24r115", "+lv4+lvn8+lvf24+lvr115"), ("lv4km-c1", "+lv4+lvn8+lvc1"), ("lv4km-vox156-z64", "+lv4+lvn8+lvv156+lvz64"),
            ("lv4km-taps0", "+lv4+lvn8+lvt0"), ("lv16km-far2x", "+lv16+lvn8+lvs2"), ("lv8km-far2x", "+lv8+lvn8+lvs2"), ("lv16km-c1", "+lv16+lvn8+lvc1"),
            ("lv8km-c1", "+lv8+lvn8+lvc1")]
# The moving camera (100 m a frame forward; window shifts and the clouds through the band) and the rising sun (1 degree a frame) at 2 slices a frame, without and with the warp factor 8.
PIC_MOVE = [("full", ""), ("lv4km-n4", "+lv4+lvn4")]
PIC_SUN = [("full", ""), ("lv4km-n2", "+lv4+lvn2"), ("lv4km-n2-warp8", "+lv4+lvn2+lvw8")]


def march(tail):
    return BASE + tail


def prepare():
    for size, folder in ((COST_SIZE, INPUT), (PIC_SIZE, INPUT_PIC)):
        for name, (what, change) in VIEWS.items():
            e40puff.VARIANTS[name] = (what, change, BASE)
        e40puff.INPUT = folder
        e40puff.SIZE = size
        e40puff.prepare([PLACE], list(VIEWS))


def tags(views):
    return ",".join(e40pp.view_tag(e40puff.tag_of(PLACE, v)) for v in views)


def cost_stages():
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")):
        return []
    rounds = int(os.environ.get("ATMO_COST_ROUNDS", "6") or 6)
    marches = ",".join(march(t) for _, t in COST_CASES)
    return [Stage("cost", ["-mode", "scene", "-scene", "soft-cost", "-in", INPUT, "-views", tags(COST_VIEWS), "-media", "pr50", "-marches", marches,
                           "-width", str(COST_SIZE[0]), "-height", str(COST_SIZE[1]), "-rounds", str(rounds)],
                  size=COST_SIZE, proxy=True, timeout=600 + 420 * rounds)]


def pic_stages():
    if not os.path.isfile(os.path.join(INPUT_PIC, "jobs.json")):
        return []
    light = ["-light", "product", "-width", str(PIC_SIZE[0]), "-height", str(PIC_SIZE[1])]
    still = ",".join(march(t) for _, t in PIC_STILL + PIC_DIAG)
    move = ",".join(march(t) for _, t in PIC_MOVE)
    sun = ",".join(march(t) for _, t in PIC_SUN)
    return [
        Stage("still", ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT_PIC, "-views", tags(PIC_VIEWS), "-media", "pr50", "-marches", still, "-scenarios", "static",
                        "-dumpframes", "39", "-motion", "all", "-lvdump", "1"] + light, size=PIC_SIZE, proxy=False, timeout=3600),
        Stage("move", ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT_PIC, "-views", tags(("lv_g10", "lv_a15")), "-media", "pr50", "-marches", move,
                       "-scenarios", "lvmove", "-motion", "all"] + light, size=PIC_SIZE, proxy=False, timeout=3600),
        Stage("sun", ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT_PIC, "-views", tags(("lv_g10",)), "-media", "pr50", "-marches", sun,
                      "-scenarios", "lvsun", "-motion", "all"] + light, size=PIC_SIZE, proxy=False, timeout=3600),
    ]


def collect_cost(ctx):
    rows = {}
    for k, v in ctx.player_metrics("cost").items():
        if not k.startswith("softcost."):
            continue
        _, part, rest = k.split(".", 2)
        view, _medium, m = rest.split(".", 2)
        rows.setdefault((view, m), {})[part] = v[0]
    names = {march(t): n for n, t in COST_CASES}
    lines = ["LV1 cost at %d x %d: march | fill | march + fill (ms; medians over the rounds' frames; min-max of the rounds' march + fill medians)" % COST_SIZE]
    metrics = []
    for view in sorted(set(v for v, _ in rows)):
        lines.append("  " + view)
        for name, t in COST_CASES:
            r = rows.get((view, march(t)))
            if r is None:
                lines.append("    %-16s missing" % name)
                continue
            m, f = r.get("march", float("nan")), r.get("fill", 0.0)
            per_m = [r[p] for p in sorted(p for p in r if p.startswith("marchr"))]
            per_f = [r[p] for p in sorted(p for p in r if p.startswith("fillr"))]
            sums = sorted(a + (per_f[i] if i < len(per_f) else 0.0) for i, a in enumerate(per_m))
            spread = " (%.2f-%.2f, %d rounds)" % (sums[0], sums[-1], len(sums)) if sums else ""
            lays = " lays %d" % r["lvlays"] if "lvlays" in r else ""
            lines.append("    %-16s %7.2f | %6.2f | %7.2f%s%s" % (name, m, f, m + f, spread, lays))
            metrics.append(Metric.info("lv1.%s.%s.march" % (view, name.replace(" ", "_")), m))
            metrics.append(Metric.info("lv1.%s.%s.fill" % (view, name.replace(" ", "_")), f))
    print("\n".join(lines))
    return metrics


def collect_pic(ctx):
    return [Metric.info("lv1.pic.stages", float(len(ctx.folders)))]


register_series("lv1-cost", cost_stages(), collect_cost, exclusive=True, who="lv1")
register_series("lv1-pic", pic_stages(), collect_pic, exclusive=False, who="lv1")

GROUND = np.array([0.20, 0.24, 0.15])


def picture(folder, view, m_tail, scenario, frame):
    """The frame of a view, a march and a scenario, composed through the view's display stage over the game's frame without clouds (ground view) or a flat ground colour (views from
    above, where the game's frame is not the ground under the camera)."""
    name = e40puff.tag_of(PLACE, view)
    st = e40puff.prepared().get(name)
    case = "gs-%s-pr50-%s-%s-acc20-f%d" % (e40pp.view_tag(name), scenario, march(m_tail), frame)
    if st is None or not os.path.isfile(os.path.join(folder, case + ".npy")):
        return None
    m = e40ty.load_lum(folder, case)
    if view.startswith("lv_g"):
        off = e40pp.game_frame(e40pp.VIEWS[name], "off_a", st.get("start", e40pp.GAME_START))
        off = e40puff.at_size(off, m.shape[1], m.shape[0]) if off is not None else np.ones(m.shape[:2] + (3,)) * GROUND
    else:
        off = np.ones(m.shape[:2] + (3,)) * GROUND
    return e40pp.compose(m, off, st)


def save(img, path):
    from PIL import Image
    Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype("uint8")).save(path)


def pictures(out, root):
    e40puff.INPUT = INPUT_PIC
    os.makedirs(out, exist_ok=True)
    words = {"lv_g10": "ground-30m-pitch4-sun10-behind", "lv_a15": "above-9km-pitch-15-sun15-behind", "lv_d15": "topdown-15km-straight-down-sun15"}
    lines = []
    for view in PIC_VIEWS:
        ref = picture(os.path.join(root, "lv1-pic-still"), view, "", "static", 39)
        for name, t in PIC_STILL + PIC_DIAG:
            img = picture(os.path.join(root, "lv1-pic-still"), view, t, "static", 39)
            if img is None:
                lines.append("missing %s %s" % (view, name))
                continue
            label = "full-light-everywhere" if name == "full" else name
            save(img, os.path.join(out, "LV1-%s-%s-player-2560x1440.png" % (words[view], label)))
            if ref is not None and name != "full":
                d = np.abs(img - ref)
                save(np.clip(d * 8.0, 0, 1), os.path.join(out, "aid-LV1-%s-%s-minus-full-x8.png" % (words[view], label)))
                lines.append("%s %-8s mean |diff| %.5f, max %.3f, pixels > 2/255: %d" % (view, name, d.mean(), d.max(), int((d.max(axis=2) > 2.0 / 255).sum())))
    for stage, cases, scenario, views, what in (("move", PIC_MOVE, "lvmove", ("lv_g10", "lv_a15"), "camera-100m-per-frame-forward"),
                                                 ("sun", PIC_SUN, "lvsun", ("lv_g10",), "sun-rising-1deg-per-frame")):
        for view in views:
            for name, t in cases:
                for f in (6, 10, 14, 18, 22, 26, 30, 34, 38):
                    img = picture(os.path.join(root, "lv1-pic-" + stage), view, t, scenario, f)
                    if img is None:
                        continue
                    save(img, os.path.join(out, "LV1-seq-%s-%s-%s-f%02d-player-2560x1440.png" % (words[view], what, name, f)))
    print("\n".join(lines))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--prepare", action="store_true")
    p.add_argument("--pictures")
    p.add_argument("--root", default=e40pp.BUILD.replace("weather-series", "WeatherPlayer"))
    a = p.parse_args()
    if a.prepare:
        prepare()
    if a.pictures:
        pictures(a.pictures, a.root)
    return 0


if __name__ == "__main__":
    sys.exit(main())
