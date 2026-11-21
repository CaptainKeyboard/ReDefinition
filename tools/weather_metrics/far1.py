"""FAR1: where the march's time goes and what the far field's levers give (wolken-m2a-technik.md section 12.4), in the weather player with the switches the march already has.

    python tools/weather_metrics/far1.py --prepare                        (the views' jobs from e40pp's inputs, at 1707 x 960 and 2560 x 1440)
    python tools/build_weather_player.py --series far1-cost --no-build    (march ms of every case)
    python tools/build_weather_player.py --series far1-pic --no-build     (the frames at 2560 x 1440)
    python tools/weather_metrics/far1.py --pictures <folder>              (the PNGs of the last far1-pic run, with speaking names)

The views are e40pp's gclock place (Kerbin, the map's cover, the single-cloud lattice), moved as e40puff's variants do. Every case is the accepted march plus SoftTable tokens:
dbg<bits> the cost debug bits (1 no light, 2 no shape noise, 4 no erosion), ng<N> the points a span may take at most (256 accepted), tl / dlod / cg the distance LODs of T16
(light taps, detail, step growth along the span).
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
INPUT = os.path.join(e40pp.BUILD, "far1-in")
INPUT_PIC = os.path.join(e40pp.BUILD, "far1-in-2560")
COST_SIZE = (1707, 960)
PIC_SIZE = (2560, 1440)
PLACE = "gclock"

VIEWS = {
    "far_g10": ("ground, 30 m, pitch 4, sun 10 behind", e40puff.compose(e40puff.camera_at(30.0, 4.0), e40puff.sun_behind(10.0))),
    "far_a15": ("above, 9 km, pitch -15, sun 15 behind", e40puff.compose(e40puff.camera_at(9000.0, -15.0), e40puff.sun_behind(15.0))),
    "far_d15": ("straight down from 15 km, sun 15", e40puff.compose(e40puff.camera_at(15000.0, -90.0), e40puff.sun_behind(15.0))),
}
WORDS = {"far_g10": "ground-30m-pitch4-sun10-behind", "far_a15": "above-9km-pitch-15-sun15-behind", "far_d15": "topdown-15km-straight-down-sun15"}

COST_CASES = [
    ("today", ""),
    ("no light", "+dbg1"),
    ("no erosion", "+dbg4"),
    ("no light, no erosion", "+dbg5"),
    ("no light, no noise", "+dbg7"),
    ("cap 128", "+ng128"),
    ("cap 96", "+ng96"),
    ("cap 64", "+ng64"),
    ("cap 48", "+ng48"),
    ("cap 32", "+ng32"),
    ("cap 64, no light", "+ng64+dbg1"),
    ("tap lod", "+tl"),
    ("detail lod", "+dlod"),
    ("step growth", "+cg"),
    ("all three lods", "+cg+tl+dlod"),
    ("cap 64, all lods", "+ng64+cg+tl+dlod"),
    # A span's points by distance (sg<N>: the step grows by N / 1000000 per m of distance from the camera), at the accepted cap and below it.
    ("by distance 200", "+sg200"),
    ("by distance 500", "+sg500"),
    ("by distance 1000", "+sg1000"),
    ("by distance 500, cap 128", "+sg500+ng128"),
    ("by distance 500, cap 96", "+sg500+ng96"),
    ("by distance 500, cap 64", "+sg500+ng64"),
    ("by distance 1000, cap 128", "+sg1000+ng128"),
    ("by distance 1000, cap 96", "+sg1000+ng96"),
    ("by distance 1000, cap 64", "+sg1000+ng64"),
    ("by distance 1000, cap 96, no light", "+sg1000+ng96+dbg1"),
    # Without the leap over empty grid points (lp0), to separate what the leap does to thin layers.
    ("no leap", "+lp0"),
    ("by distance 1000, no leap", "+sg1000+lp0"),
    ("by distance 1000, cap 96, no leap", "+sg1000+ng96+lp0"),
]
PIC_CASES = [("accepted", ""), ("noleap", "+lp0"), ("bydistance1000-noleap", "+sg1000+lp0"), ("bydistance1000-cap96-noleap", "+sg1000+ng96+lp0"), ("cap128", "+ng128"), ("cap96", "+ng96"), ("cap64", "+ng64"), ("cap48", "+ng48"), ("cap32", "+ng32"), ("lods", "+cg+tl+dlod"),
             ("cap64-lods", "+ng64+cg+tl+dlod"), ("bydistance200", "+sg200"), ("bydistance500", "+sg500"), ("bydistance1000", "+sg1000"),
             ("bydistance500-cap128", "+sg500+ng128"), ("bydistance500-cap96", "+sg500+ng96"), ("bydistance500-cap64", "+sg500+ng64"),
             ("bydistance1000-cap128", "+sg1000+ng128"), ("bydistance1000-cap96", "+sg1000+ng96"), ("bydistance1000-cap64", "+sg1000+ng64")]


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
    marches = ",".join(march(t) for _, t in COST_CASES)
    return [Stage("cost", ["-mode", "scene", "-scene", "soft-cost", "-in", INPUT, "-views", tags(VIEWS), "-media", "pr50", "-marches", marches,
                           "-width", str(COST_SIZE[0]), "-height", str(COST_SIZE[1])], size=COST_SIZE, proxy=True, timeout=1500)]


def pic_stages():
    if not os.path.isfile(os.path.join(INPUT_PIC, "jobs.json")):
        return []
    still = ",".join(march(t) for _, t in PIC_CASES)
    return [Stage("still", ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT_PIC, "-views", tags(VIEWS), "-media", "pr50", "-marches", still, "-scenarios", "static",
                            "-dumpframes", "39", "-motion", "all", "-light", "product", "-width", str(PIC_SIZE[0]), "-height", str(PIC_SIZE[1])],
                  size=PIC_SIZE, proxy=False, timeout=3600)]


def collect_cost(ctx):
    rows = {}
    for k, v in ctx.player_metrics("cost").items():
        if not k.startswith("softcost.march."):
            continue
        view, _medium, m = k[len("softcost.march."):].split(".", 2)
        rows[(view, m)] = v[0]
    lines = ["FAR1 march ms at %d x %d (median of the scene's two rounds), and the case over today" % COST_SIZE]
    metrics = []
    for view in sorted(set(v for v, _ in rows)):
        lines.append("  " + view)
        base = rows.get((view, march("")), float("nan"))
        for name, t in COST_CASES:
            m = rows.get((view, march(t)))
            if m is None:
                lines.append("    %-22s missing" % name)
                continue
            lines.append("    %-22s %7.2f  x%.2f" % (name, m, m / base))
            metrics.append(Metric.info("far1.%s.%s" % (view, name.replace(" ", "_").replace(",", "")), m))
    print("\n".join(lines))
    return metrics


def collect_pic(ctx):
    return [Metric.info("far1.pic.stages", float(len(ctx.folders)))]


register_series("far1-cost", cost_stages(), collect_cost, exclusive=True, who="far1")
register_series("far1-pic", pic_stages(), collect_pic, exclusive=False, who="far1")

GROUND = np.array([0.20, 0.24, 0.15])


def picture(folder, view, m_tail):
    """The last frame of a view and a march, composed through the view's display stage over the game's frame without clouds (ground view) or a flat ground colour (views from above)."""
    name = e40puff.tag_of(PLACE, view)
    st = e40puff.prepared().get(name)
    case = "gs-%s-pr50-static-%s-acc20-f39" % (e40pp.view_tag(name), march(m_tail))
    if st is None or not os.path.isfile(os.path.join(folder, case + ".npy")):
        return None
    m = e40ty.load_lum(folder, case)
    off = None
    if view == "far_g10":
        off = e40pp.game_frame(e40pp.VIEWS[name], "off_a", st.get("start", e40pp.GAME_START))
        off = e40puff.at_size(off, m.shape[1], m.shape[0]) if off is not None else None
    if off is None:
        off = np.ones(m.shape[:2] + (3,)) * GROUND
    return e40pp.compose(m, off, st)


def save(img, path):
    from PIL import Image
    Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype("uint8")).save(path)


def pictures(out, root):
    e40puff.INPUT = INPUT_PIC
    os.makedirs(out, exist_ok=True)
    for view in VIEWS:
        for name, t in PIC_CASES:
            img = picture(os.path.join(root, "far1-pic-still"), view, t)
            if img is None:
                print("missing %s %s" % (view, name))
                continue
            save(img, os.path.join(out, "FAR1-%s-%s-player-2560x1440.png" % (WORDS[view], name)))


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
