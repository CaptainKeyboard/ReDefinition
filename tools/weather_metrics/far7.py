"""FAR7: the leap over empty grid points with a span's points by distance (layers.spanGrowthPerM): what passes over the thin deck, and the fix (wolken-m2a-technik.md 12.7).

    python tools/weather_metrics/far1.py --prepare                         (once: FAR1's view jobs, which this series reuses)
    python tools/build_weather_player.py --series far7-check [--no-build]   (the view from above: the leap's switches and the aid's numbers; the ground view's sample counts)
    python tools/build_weather_player.py --series far7-pic --no-build       (the three views: the fix against no leap, sg500 / sg1000 at 256 / 192 / 128 points)
    python tools/build_weather_player.py --series far7-cost --no-build      (march ms of the same cases at 1707 x 960)
    python tools/weather_metrics/far7.py --pictures <folder> [--stage check|pic]

Tokens beyond FAR1's: ld<bits> the leap's switches (LayerFrame.LeapDebug; without the token the march's own: 136 with sg, else 0; 1 no empty-reach skip, 2 no leap growth,
8 end at the altitude where cloud can begin, 16 in-slab cap, 32 thinnest-type cap, 64 return by the start's step, 128 restart after an ended leap, 256 no leap past the span's end);
gf<N> the step grows only beyond N m; aid<N> the pass's aid output (8-10: the first leap of a ray that passed over cloud, 11: samples and light taps); full: every pixel marched
every frame without history (the aid's numbers as the pass wrote them).
"""
import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import numpy as np  # noqa: E402

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40pp  # noqa: E402
from weather_metrics import e40puff  # noqa: E402
from weather_metrics import far1  # noqa: E402

CHECK_CASES = [
    ("accepted", ""),
    ("sg1000", "+sg1000"),
    ("sg1000-noleap", "+sg1000+lp0"),
    ("sg1000-ld0", "+sg1000+ld0"),
    ("sg1000-gap-restart-end", "+sg1000+ld392"),
    ("sg1000-gap-thin-restart", "+sg1000+ld168"),
    ("sg1000-gap", "+sg1000+ld8"),
    ("sg1000-end", "+sg1000+ld256"),
]
CHECK_FULL = [
    ("full-sg1000-noleap", "+sg1000+lp0+full"),
    ("full-sg1000", "+sg1000+full"),
    ("full-sg1000-ld0", "+sg1000+ld0+full"),
    ("full-sg1000-ld392", "+sg1000+ld392+full"),
    ("full-sg1000-ld168", "+sg1000+ld168+full"),
    ("aid9-sg1000-ld0", "+sg1000+ld0+full+aid9"),
    ("aid10-sg1000-ld0", "+sg1000+ld0+full+aid10"),
    ("aid9-sg1000", "+sg1000+full+aid9"),
    ("aid10-sg1000", "+sg1000+full+aid10"),
    ("aid10-accepted", "+full+aid10"),
]
GROUND_AID = [
    ("aid11-accepted", "+full+aid11"),
    ("aid11-sg1000", "+sg1000+full+aid11"),
    ("aid11-sg1000-noleap", "+sg1000+lp0+full+aid11"),
    ("aid11-sg1000-gf2000", "+sg1000+gf2000+full+aid11"),
    ("aid11-sg1000-gf5000", "+sg1000+gf5000+full+aid11"),
]


def pic_cases():
    cases = [("accepted", ""), ("sg1000-noleap", "+sg1000+lp0"), ("sg1000-old", "+sg1000+ld0")]
    for g in (500, 1000):
        for n in (256, 192, 128):
            cases.append(("sg%d-ng%d" % (g, n), "+sg%d%s" % (g, "" if n == 256 else "+ng%d" % n)))
    cases += [("sg1000-ng128-noleap", "+sg1000+ng128+lp0"), ("sg1000-gf2000", "+sg1000+gf2000"), ("sg1000-gf5000", "+sg1000+gf5000")]
    return cases


def cost_cases():
    cases = [("today", ""), ("today ld136", "+ld136"), ("sg1000 old", "+sg1000+ld0"), ("sg1000 noleap", "+sg1000+lp0"), ("sg1000 ld392", "+sg1000+ld392"),
             ("sg1000 ld168", "+sg1000+ld168")]
    for g in (500, 1000):
        for n in (256, 192, 128):
            cases.append(("sg%d ng%d" % (g, n), "+sg%d%s" % (g, "" if n == 256 else "+ng%d" % n)))
    cases += [("today noleap", "+lp0"), ("sg1000 gf2000", "+sg1000+gf2000"), ("sg1000 gf5000", "+sg1000+gf5000"), ("sg1000 noreach", "+sg1000+ld137")]
    return cases


def tags(views):
    return ",".join(e40pp.view_tag(e40puff.tag_of(far1.PLACE, v)) for v in views)


def still_stage(name, cases, views):
    if not os.path.isfile(os.path.join(far1.INPUT_PIC, "jobs.json")):
        return []
    marches = ",".join(far1.march(t) for _, t in cases)
    return [Stage(name, ["-mode", "scene", "-scene", "soft-temporal", "-in", far1.INPUT_PIC, "-views", tags(views), "-media", "pr50", "-marches", marches, "-scenarios", "static",
                         "-dumpframes", "39", "-dump", "last", "-light", "product", "-width", str(far1.PIC_SIZE[0]), "-height", str(far1.PIC_SIZE[1])],
                  size=far1.PIC_SIZE, proxy=False, timeout=3600)]


def cost_stages():
    if not os.path.isfile(os.path.join(far1.INPUT, "jobs.json")):
        return []
    marches = ",".join(far1.march(t) for _, t in cost_cases())
    return [Stage("cost", ["-mode", "scene", "-scene", "soft-cost", "-in", far1.INPUT, "-views", tags(far1.VIEWS), "-media", "pr50", "-marches", marches,
                           "-width", str(far1.COST_SIZE[0]), "-height", str(far1.COST_SIZE[1])], size=far1.COST_SIZE, proxy=True, timeout=1500)]


def collect_cost(ctx):
    rows = {}
    for k, v in ctx.player_metrics("cost").items():
        if not k.startswith("softcost.march."):
            continue
        view, _medium, m = k[len("softcost.march."):].split(".", 2)
        rows[(view, m)] = v[0]
    lines = ["FAR7 march ms at %d x %d (median of the scene's two rounds), and the case over today" % far1.COST_SIZE]
    metrics = []
    for view in sorted(set(v for v, _ in rows)):
        lines.append("  " + view)
        base = rows.get((view, far1.march("")), float("nan"))
        for name, t in cost_cases():
            m = rows.get((view, far1.march(t)))
            if m is None:
                lines.append("    %-22s missing" % name)
                continue
            lines.append("    %-22s %7.2f  x%.2f" % (name, m, m / base))
            metrics.append(Metric.info("far7.%s.%s" % (view, name.replace(" ", "_")), m))
    print("\n".join(lines))
    return metrics


def collect_count(ctx):
    return [Metric.info("far7.stages", float(len(ctx.folders)))]


register_series("far7-check", still_stage("still", CHECK_CASES + CHECK_FULL, ["far_a15"]) + still_stage("ground", GROUND_AID, ["far_g10"]), collect_count,
                exclusive=False, who="far7")
register_series("far7-pic", still_stage("still", pic_cases(), list(far1.VIEWS)), collect_count, exclusive=False, who="far7")
register_series("far7-cost", cost_stages(), collect_cost, exclusive=True, who="far7")


def raw(folder, view, tail):
    """The dumped frame of a case as float32 (height, width, 4), row 0 at the top."""
    name = e40puff.tag_of(far1.PLACE, view)
    case = "gs-%s-pr50-static-%s-acc20-f39" % (e40pp.view_tag(name), far1.march(tail))
    path = os.path.join(folder, case + ".npy")
    if not os.path.isfile(path):
        return None
    return np.load(path).astype(np.float32)[::-1]


def pictures(out, root, stage):
    e40puff.INPUT = far1.INPUT_PIC
    os.makedirs(out, exist_ok=True)
    folder = os.path.join(root, "far7-%s-still" % stage)
    cases = CHECK_CASES if stage == "check" else pic_cases()
    views = ["far_a15"] if stage == "check" else list(far1.VIEWS)
    for view in views:
        for name, t in cases:
            img = far1.picture(folder, view, t)
            if img is None:
                print("missing %s %s" % (view, name))
                continue
            far1.save(img, os.path.join(out, "FAR7-%s-%s-player-2560x1440.png" % (far1.WORDS[view], name)))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--pictures")
    p.add_argument("--stage", default="pic")
    p.add_argument("--root", default=e40pp.BUILD.replace("weather-series", "WeatherPlayer"))
    a = p.parse_args()
    if a.pictures:
        pictures(a.pictures, a.root, a.stage)
    return 0


if __name__ == "__main__":
    sys.exit(main())
