"""LV2: the light volume filled by propagation (cloud.lightVolume.fill 1) against the direct fill and full light (wolken-m2a-technik.md section 11.5).

    python tools/weather_metrics/lv2.py --prepare                         (the views of lv1.py plus the view from above at a high sun, at 1707 x 960 and 2560 x 1440)
    python tools/build_weather_player.py --series lv2-cost --no-build     (march and fill ms, six alternating rounds)
    python tools/build_weather_player.py --series lv2-err --no-build      (the volume of the view from above under each fill and the references, dumped)
    python tools/build_weather_player.py --series lv2-pic --no-build      (the frames at 2560 x 1440)
    python tools/weather_metrics/lv2.py --errors                          (the fills' optical depth against the fine references)
    python tools/weather_metrics/lv2.py --pictures <folder>               (the PNGs of the last lv2-pic run)

The cases are lv1.py's march plus SoftTable tokens; lvm<N> the fill, lvb<N> the band in percent.
"""
import argparse
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40pp  # noqa: E402
from weather_metrics import e40puff  # noqa: E402
from weather_metrics import lv1  # noqa: E402

BASE = lv1.BASE
INPUT = os.path.join(e40pp.BUILD, "lv2-in")
INPUT_PIC = os.path.join(e40pp.BUILD, "lv2-in-2560")
COST_SIZE, PIC_SIZE = lv1.COST_SIZE, lv1.PIC_SIZE
PLACE = lv1.PLACE

VIEWS = dict(lv1.VIEWS)
VIEWS["lv_a60"] = ("above, 9 km, pitch -15, sun 60 behind", e40puff.compose(e40puff.camera_at(9000.0, -15.0), e40puff.sun_behind(60.0)))
COST_VIEWS = ("lv_g10", "lv_g60", "lv_a15", "lv_a60", "lv_d15", "lv_d60")
WORDS = {"lv_g10": "ground-30m-pitch4-sun10-behind", "lv_g60": "ground-30m-pitch4-sun60-behind", "lv_a15": "above-9km-pitch-15-sun15-behind",
         "lv_a60": "above-9km-pitch-15-sun60-behind", "lv_d15": "topdown-15km-straight-down-sun15", "lv_d60": "topdown-15km-straight-down-sun60"}

# The cost cases around the centre 4 km, N 4, far taps 3, band 0.15 with the default fill (direct, the march's own far taps, stored as transmittance with k 4), each lever varied
# alone; the propagation fill and LV1's read (optical depth stored) at the centre and at 16 km; the keyword's own overhead (1000 km: nothing far); today and the old cache.
COST_CASES = [
    ("today", ""),
    ("old cache", "+lcache3"),
    ("lv 1000 km", "+lv1000+lvn4+lvt3"),
    ("16 km", "+lv16+lvn4+lvt3"),
    ("8 km", "+lv8+lvn4+lvt3"),
    ("4 km", "+lv4+lvn4+lvt3"),
    ("2 km", "+lv2+lvn4+lvt3"),
    ("4 km N all", "+lv4+lvn0+lvt3"),
    ("4 km N 8", "+lv4+lvn8+lvt3"),
    ("4 km N 2", "+lv4+lvn2+lvt3"),
    ("4 km taps 1", "+lv4+lvn4+lvt1"),
    ("4 km taps 0", "+lv4+lvn4+lvt0"),
    ("prop 4 km", "+lv4+lvn4+lvt3+lvm1+lvh8"),
    ("prop 16 km", "+lv16+lvn4+lvt3+lvm1+lvh8"),
    ("prop 4 km N all", "+lv4+lvn0+lvt3+lvm1+lvh8"),
    ("tau read 4 km", "+lv4+lvn4+lvt3+lvh0"),
]

# The error dumps (view from above, low sun, every slice refreshed every frame): the direct fill, propagation and both references.
ERR_CASES = [("direct", "+lv4+lvn0+lvm0+lvh0"), ("prop", "+lv4+lvn0+lvm1+lvh0"), ("ref-top", "+lv4+lvn0+lvm2+lvh0"), ("ref-direct", "+lv4+lvn0+lvm3+lvh0")]

# The pictures: full light against each hand-over distance with propagation (all six views); for the views from above also the direct fill, the diagnosis (no voxels) and the read
# point further towards the sun.
PIC_STILL = [("full", ""), ("lv16km", "+lv16+lvn8"), ("lv8km", "+lv8+lvn8"), ("lv4km", "+lv4+lvn8"), ("lv2km", "+lv2+lvn8"),
             ("lv16km-propagation", "+lv16+lvn8+lvm1+lvh8"), ("lv8km-propagation", "+lv8+lvn8+lvm1+lvh8")]
# The matrix of the views from above and the ground view at 8 km: fill (direct / propagation) x read (optical depth / transmittance with k 8 and 16).
PIC_ABOVE = [("lv8km-direct-tau", "+lv8+lvn8+lvm0+lvh0"), ("lv8km-direct-T8", "+lv8+lvn8+lvm0+lvh8"),
             ("lv8km-prop-tau", "+lv8+lvn8+lvm1+lvh0"), ("lv8km-prop-T8", "+lv8+lvn8+lvm1+lvh8"), ("lv8km-prop-T16", "+lv8+lvn8+lvm1+lvh16"),
             ("lv8km-direct-T4", "+lv8+lvn8+lvm0+lvh4"), ("lv8km-direct-T16", "+lv8+lvn8+lvm0+lvh16"), ("full-repeat", "+ctl")]
MATRIX_VIEWS = ("lv_a15", "lv_a60", "lv_g10")
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
    return [Stage("cost", ["-mode", "scene", "-scene", "soft-cost", "-in", INPUT, "-views", tags(COST_VIEWS), "-media", "pr50", "-marches", ",".join(march(t) for _, t in COST_CASES),
                           "-width", str(COST_SIZE[0]), "-height", str(COST_SIZE[1]), "-rounds", str(rounds)],
                  size=COST_SIZE, proxy=True, timeout=900 + 700 * rounds)]


def light(size):
    return ["-light", "product", "-width", str(size[0]), "-height", str(size[1])]


def err_stages():
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")):
        return []
    return [Stage("err", ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", tags(("lv_a15", "lv_g10")), "-media", "pr50",
                          "-marches", ",".join(march(t) for _, t in ERR_CASES), "-scenarios", "static", "-dumpframes", "39", "-motion", "all", "-lvdump", "1"] + light(COST_SIZE),
                  size=COST_SIZE, proxy=False, timeout=1800)]


def pic_stages():
    """LV2_STAGES=a,b keeps only those stages (still, above, move, sun)."""
    wanted = [s for s in os.environ.get("LV2_STAGES", "").split(",") if s]
    return [st for st in all_pic_stages() if not wanted or st.label in wanted]


def all_pic_stages():
    if not os.path.isfile(os.path.join(INPUT_PIC, "jobs.json")):
        return []
    lt = light(PIC_SIZE)
    common = ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT_PIC, "-media", "pr50", "-motion", "all"]
    return [
        Stage("still", common + ["-views", tags(COST_VIEWS), "-marches", ",".join(march(t) for _, t in PIC_STILL), "-scenarios", "static", "-dumpframes", "39"] + lt,
              size=PIC_SIZE, proxy=False, timeout=3600),
        Stage("above", common + ["-views", tags(MATRIX_VIEWS), "-marches", ",".join(march(t) for _, t in [("full", "")] + PIC_ABOVE), "-scenarios", "static",
                                 "-dumpframes", "39"] + lt,
              size=PIC_SIZE, proxy=False, timeout=3600),
        Stage("move", common + ["-views", tags(("lv_g10", "lv_a15")), "-marches", ",".join(march(t) for _, t in PIC_MOVE), "-scenarios", "lvmove16"] + lt,
              size=PIC_SIZE, proxy=False, timeout=3600),
        Stage("sun", common + ["-views", tags(("lv_g10",)), "-marches", ",".join(march(t) for _, t in PIC_SUN), "-scenarios", "lvsun16"] + lt,
              size=PIC_SIZE, proxy=False, timeout=3600),
    ]


def collect_cost(ctx):
    rows = {}
    for k, v in ctx.player_metrics("cost").items():
        if not k.startswith("softcost."):
            continue
        _, part, rest = k.split(".", 2)
        view, _medium, m = rest.split(".", 2)
        rows.setdefault((view, m), {})[part] = v[0]
    lines = ["LV2 cost at %d x %d: march | fill | march + fill (ms; medians over the rounds' frames; min-max of the rounds' march + fill medians)" % COST_SIZE]
    metrics = []
    for view in sorted(set(v for v, _ in rows)):
        lines.append("  " + view)
        for name, t in COST_CASES:
            r = rows.get((view, march(t)))
            if r is None:
                lines.append("    %-18s missing" % name)
                continue
            m, f = r.get("march", float("nan")), r.get("fill", 0.0)
            per_m = [r[p] for p in sorted(p for p in r if p.startswith("marchr"))]
            per_f = [r[p] for p in sorted(p for p in r if p.startswith("fillr"))]
            sums = sorted(a + (per_f[i] if i < len(per_f) else 0.0) for i, a in enumerate(per_m))
            spread = " (%.2f-%.2f, %d rounds)" % (sums[0], sums[-1], len(sums)) if sums else ""
            lays = " lays %d" % r["lvlays"] if "lvlays" in r else ""
            lines.append("    %-18s %7.2f | %6.2f | %7.2f%s%s" % (name, m, f, m + f, spread, lays))
            metrics.append(Metric.info("lv2.%s.%s.march" % (view, name.replace(" ", "_")), m))
            metrics.append(Metric.info("lv2.%s.%s.fill" % (view, name.replace(" ", "_")), f))
    print("\n".join(lines))
    return metrics


def collect_any(ctx):
    return [Metric.info("lv2.stages", float(len(ctx.folders)))]


register_series("lv2-cost", cost_stages(), collect_cost, exclusive=True, who="lv2")
register_series("lv2-err", err_stages(), collect_any, exclusive=False, who="lv2")
register_series("lv2-pic", pic_stages(), collect_any, exclusive=False, who="lv2")


def load_volume(folder, view, tail):
    name = "lv-%s-%s-f39" % (e40pp.view_tag(e40puff.tag_of(PLACE, view)), march(tail))
    path = os.path.join(folder, name + ".npy")
    if not os.path.isfile(path):
        return None, None
    with open(os.path.join(folder, name + ".json"), encoding="utf-8") as f:
        d = json.load(f)
    v = np.load(path)
    k = d.get("storeK", 0.0)
    if k > 0:
        v = np.where(v == 0.0, 0.0, -k * np.log(np.maximum(v, 6.2e-5)))
    return v, d


def errors(root):
    """Each fill's optical depth against its fine reference at the voxel centres the march can read (the direct fill's own read mask: a voxel it leaves at zero is never read),
    per cascade and by altitude band; also the error in transmittance exp(-tau), which is what the picture shows."""
    folder = os.path.join(root, "lv2-err-err")
    out = []
    for view in ("lv_a15", "lv_g10"):
        vols = {n: load_volume(folder, view, t) for n, t in ERR_CASES}
        if any(v[0] is None for v in vols.values()):
            out.append("%s: dumps missing" % view)
            continue
        d = vols["direct"][1]
        nz = d["gridZ"]
        alt = d["alt"]
        mask = vols["direct"][0] != 0.0
        out.append("%s (sun from the view's job; slices %d over %.0f..%.0f m; voxels the march can read: %d)" % (view, nz, alt[0], alt[1], int(mask.sum())))
        for fill, ref in (("direct", "ref-direct"), ("prop", "ref-top"), ("direct", "ref-top")):
            a, b = vols[fill][0], vols[ref][0]
            for c in range(d["cascades"]):
                sl = slice(c * nz, (c + 1) * nz)
                m = mask[sl]
                e = (a[sl] - b[sl])[m]
                te = np.abs(np.exp(-a[sl][m]) - np.exp(-b[sl][m]))
                out.append("  %-6s vs %-10s cascade %d: tau error mean %+.3f, |mean| %.3f, 95%% %.3f; transmittance |error| mean %.4f, 95%% %.4f; ref tau mean %.2f"
                           % (fill, ref, c, e.mean(), np.abs(e).mean(), np.percentile(np.abs(e), 95), te.mean(), np.percentile(te, 95), b[sl][m].mean()))
                # where: by altitude band, transmittance error
                bands = []
                for z0 in range(0, nz, 4):
                    mm = m[z0:z0 + 4]
                    if mm.sum() == 0:
                        continue
                    t = np.abs(np.exp(-a[sl][z0:z0 + 4][mm]) - np.exp(-b[sl][z0:z0 + 4][mm]))
                    h = alt[0] + (z0 + 2) * (alt[1] - alt[0]) / nz
                    bands.append("%.1f km %.3f" % (h / 1000.0, t.mean()))
                out.append("      by altitude (transmittance |error| mean): " + ", ".join(bands))
    print("\n".join(out))


def pictures(out, root):
    e40puff.INPUT = INPUT_PIC
    os.makedirs(out, exist_ok=True)
    lines = []

    def still(stage, view, cases):
        ref = lv1.picture(os.path.join(root, stage), view, "", "static", 39)
        for name, t in cases:
            img = lv1.picture(os.path.join(root, stage), view, t, "static", 39)
            if img is None:
                lines.append("missing %s %s %s" % (stage, view, name))
                continue
            label = "full-light-everywhere" if name == "full" else name
            lv1.save(img, os.path.join(out, "LV2-%s-%s-player-2560x1440.png" % (WORDS[view], label)))
            if ref is not None and name != "full":
                diff = np.abs(img - ref)
                lv1.save(np.clip(diff * 8.0, 0, 1), os.path.join(out, "aid-LV2-%s-%s-minus-full-x8.png" % (WORDS[view], label)))
                lines.append("%s %-30s mean |diff| %.5f, 95%% %.4f, pixels > 4/255: %d" % (view, name, diff.mean(), np.percentile(diff.max(axis=2), 95),
                                                                                         int((diff.max(axis=2) > 4.0 / 255).sum())))
    for view in COST_VIEWS:
        still("lv2-pic-still", view, PIC_STILL)
    for view in MATRIX_VIEWS:
        still("lv2-pic-above", view, PIC_ABOVE)
    for stage, cases, scenario, views, what in (("move", PIC_MOVE, "lvmove16", ("lv_g10", "lv_a15"), "camera-100m-per-frame-forward"),
                                                 ("sun", PIC_SUN, "lvsun16", ("lv_g10",), "sun-rising-1deg-per-frame")):
        for view in views:
            for name, t in cases:
                for f in range(20, 36):
                    img = lv1.picture(os.path.join(root, "lv2-pic-" + stage), view, t, scenario, f)
                    if img is not None:
                        lv1.save(img, os.path.join(out, "LV2-seq-%s-%s-%s-f%02d-player-2560x1440.png" % (WORDS[view], what, name, f)))
    print("\n".join(lines))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--prepare", action="store_true")
    p.add_argument("--errors", action="store_true")
    p.add_argument("--pictures")
    p.add_argument("--root", default=e40pp.BUILD.replace("weather-series", "WeatherPlayer"))
    a = p.parse_args()
    if a.prepare:
        prepare()
    if a.errors:
        errors(a.root)
    if a.pictures:
        pictures(a.pictures, a.root)
    return 0


if __name__ == "__main__":
    sys.exit(main())
