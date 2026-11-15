"""E40 PUFF: where the small scales of the cloud's surface are lost, one factor at a time on the parity-checked views of game start 64 (series e40-puff; package PUFF, 2026-11-15).

    python tools/weather_metrics/e40pp.py --prepare --state now --start 64 --log <K-T2 soft60 log>      (the views' jobs and maps, once)
    python tools/weather_metrics/e40puff.py --prepare [--views tyhumg,tycong,tycumg,gclock,sclock] [--variants base,light,...]
    python tools/build_weather_player.py --series e40-puff --no-build
    python tools/weather_metrics/e40puff.py --report [--pictures <folder>]                              (the table of the last run, deltas against the base)

Same method as e40surf.py (each variant changes ONE factor of the base view's job: the same camera, sun, maps, haze, display stage and march; every frame is laid over the game's
own frame without clouds and measured on the same mask), with the puffiness measures of puffiness.py instead of the surface-visibility ones. The variants that need no change of the
job but another march (the render target's size) are separate stages with the base views. The keys cloud.billow.* are the product's tuning keys (CloudLookTuning).
"""
import argparse
import json
import os
import shutil
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40pp  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import puffiness as pf  # noqa: E402

REPO = e40pp.REPO
BUILD = e40pp.BUILD
INPUT = os.path.join(BUILD, "e40puff-in")
LABEL = "puff-a"
LABEL_RES = "puff-r"
DEFAULT_VIEWS = ("tyhumg", "tycong", "tycumg", "gclock", "sclock")


def each_layer(job, f):
    for layer in job.get("layers", []):
        f(layer)


def each_type(job, f):
    for layer in job.get("layers", []):
        for t in layer.get("types", []):
            f(t)


def tune_dotted(pairs):
    """A variant that sets tuning keys (the product's own, e.g. cloud.billow.light)."""
    def f(job):
        for k, v in pairs:
            job.setdefault("tuning", []).append({"key": k, "value": float(v)})
    return f


def v_ero0(job):
    each_type(job, lambda t: t.__setitem__("erosion", 0.0))


def v_ero2(job):
    each_type(job, lambda t: t.__setitem__("erosion", t.get("erosion", 0.0) * 2.0))


def v_nosmooth(job):
    each_layer(job, lambda l: l.__setitem__("shapeSmoothing", 0.0))
    each_type(job, lambda t: t.__setitem__("shapeSmoothing", 0.0))


def v_soft03(job):
    job["soft"] = 0.3


def v_nolod(job):
    job.setdefault("noise", {})["noLod"] = 1


# name -> (what, the change of the job or None, the case of the march: None = the base's)
VARIANTS = {
    "base": ("the replay as e40pp makes it", None, None),
    "ero0": ("(a) no erosion (every type's erosion 0): what the erosion contributes now", v_ero0, None),
    "ero2": ("(a) the erosion of every type doubled", v_ero2, None),
    "contrast2": ("(a) the erosion detail's contrast about its mean doubled (cloud.billow.contrast 2)", tune_dotted([("cloud.billow.contrast", 2.0)]), None),
    "contrast3": ("(a) the erosion detail's contrast x 3 (the volume's d has a spread of 0.09: x 3.2 makes it uniform like the shape volume; cloud.billow.contrast 3)",
                  tune_dotted([("cloud.billow.contrast", 3.0)]), None),
    "top1": ("(a) the erosion's gain toward the top 1 (cloud.billow.top: erosion x (1 + h))", tune_dotted([("cloud.billow.top", 1.0)]), None),
    "fine08": ("(a) the fine octave at weight 0.8 (0.4 now)", tune_dotted([("cloud.billow.fine", 0.8)]), None),
    "fine0": ("(a) no fine octave (weight 0)", tune_dotted([("cloud.billow.fine", 0.0)]), None),
    "fine4": ("(a) the fine octave at 4 times the frequency (2.37 now)", tune_dotted([("cloud.billow.fineFreq", 4.0)]), None),
    "nolod": ("(a) no distance LOD of the detail", v_nolod, None),
    "light": ("(b) billow shading: the direct light of a sample follows the erosion detail there (cloud.billow.light 1)", tune_dotted([("cloud.billow.light", 1.0)]), None),
    "nosmooth": ("(c) no shape smoothing", v_nosmooth, None),
    "soft03": ("(c) the soft density's width 0.3 (0.5 now)", v_soft03, None),
    "sharp": ("(d) the composite upsamples with a Catmull-Rom spline (cloud.billow.sharpen 1)", tune_dotted([("cloud.billow.sharpen", 1.0)]), None),
    "res50": ("(d) the march target half the size (r50; a third now)", None, "r50"),
    "res100": ("(d) the march target the full size (r100)", None, "r100"),
    "puff1": ("(e) contrast 3 + top 0.5", tune_dotted([("cloud.billow.contrast", 3.0), ("cloud.billow.top", 0.5)]), None),
    "puff2": ("(e) contrast 3 + top 0.5 + light tap + sharpen 1", tune_dotted([("cloud.billow.contrast", 3.0), ("cloud.billow.top", 0.5), ("cloud.billow.light", 1.0),
                                                                                  ("cloud.billow.sharpen", 1.0)]), None),
}


def march_of(case):
    """The march string of a variant's case: the base's with the resolution case replaced."""
    if case is None:
        return e40pp.MARCH
    return e40pp.MARCH.replace("+r33", "+" + case)


def tag_of(view, variant):
    return view if variant == "base" else "%s-%s" % (view, variant)


def prepare(views, variants):
    if not os.path.isfile(os.path.join(e40pp.INPUT, "jobs.json")):
        raise SystemExit("run e40pp.py --prepare first")
    os.makedirs(INPUT, exist_ok=True)
    with open(os.path.join(e40pp.INPUT, "jobs.json"), encoding="utf-8") as f:
        jobs = {j["id"]: j for j in json.load(f)["jobs"]}
    with open(os.path.join(e40pp.INPUT, "heights.json"), encoding="utf-8") as f:
        specs = {s["id"]: s for s in json.load(f)["specs"]}
    with open(os.path.join(e40pp.INPUT, "stages.json"), encoding="utf-8") as f:
        stages = json.load(f)
    out_jobs, out_specs, out_stages = [], [], {}
    for view in views:
        base_tag = e40pp.view_tag(view)
        base_id = "gn-%s-prod-f0" % base_tag
        for variant in variants:
            name = tag_of(view, variant)
            new_tag = e40pp.view_tag(name)
            for fn in os.listdir(e40pp.INPUT):
                if fn.startswith(base_tag + "-"):
                    shutil.copyfile(os.path.join(e40pp.INPUT, fn), os.path.join(INPUT, new_tag + fn[len(base_tag):]))
            job = json.loads(json.dumps(jobs[base_id]).replace(base_tag + "-", new_tag + "-"))
            job["id"] = "gn-%s-prod-f0" % new_tag
            change, case = VARIANTS[variant][1], VARIANTS[variant][2]
            if change:
                change(job)
            spec = json.loads(json.dumps(specs[base_id]).replace(base_tag + "-", new_tag + "-"))
            spec["id"] = job["id"]
            out_jobs.append(job)
            out_specs.append(spec)
            out_stages[name] = dict(stages[view], view=view, variant=variant, march=march_of(case))
            e40pp.VIEWS[name] = dict(e40pp.VIEWS[view])
            print("%s <- %s" % (name, VARIANTS[variant][0]))
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": out_jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": out_specs}, f)
    with open(os.path.join(INPUT, "stages.json"), "w", encoding="utf-8") as f:
        json.dump(out_stages, f, indent=1)


def prepared():
    path = os.path.join(INPUT, "stages.json")
    if not os.path.isfile(path):
        return {}
    with open(path, encoding="utf-8") as f:
        stages = json.load(f)
    for name, st in stages.items():
        if name not in e40pp.VIEWS:
            e40pp.VIEWS[name] = dict(e40pp.VIEWS[st["view"]])
    return stages


def stage_label(march):
    """The label of the stage that draws a march: the base march's stage is puff-a, another is puff-r followed by the resolution case (r50 or r100)."""
    return LABEL if march == e40pp.MARCH else "%s-%s" % (LABEL_RES, march.split("+r")[1].split("+")[0])


def stage_list():
    """One stage per distinct march: the views of the job variants with the base march, the resolution variants (the base job) with theirs."""
    stages = prepared()
    out = []
    by_march = {}
    for name, st in stages.items():
        by_march.setdefault(st["march"], []).append(name)
    for march, names in sorted(by_march.items()):
        views = ",".join(e40pp.view_tag(n) for n in names)
        out.append(Stage(stage_label(march), ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", views, "-media", "pr50", "-marches", march] + e40pp.LIGHT,
                         size=(e40pp.W, e40pp.H), proxy=False, timeout=3400))
    return out


def measure_all(folder_of):
    """folder_of: stage label -> the player's output folder of that stage; the frames are found by the view's march."""
    stages = prepared()
    rows = {}
    for name, st in stages.items():
        spec = e40pp.VIEWS[name]
        off = e40pp.game_frame(spec, "off_a", st.get("start", e40pp.GAME_START))
        folder = folder_of(stage_label(st["march"]))
        path = os.path.join(folder, e40pp.case_file(name, st["march"]) + ".npy")
        if off is None or not os.path.isfile(path):
            continue
        m = e40ty.load_lum(folder, e40pp.case_file(name, st["march"]))
        img = e40pp.compose(m, off, st)
        ok = e40pp.measure_mask(st["view"], st["alt"], off.shape)
        r = pf.measure(img, off, None, ok)
        rows[name] = dict(view=st["view"], variant=st["variant"], full=r, **pf.summary(r))
        rows[name]["image"] = img
    return rows


KEYS = ["cover", "fine", "D8_64", "L4", "bumps", "S4", "S16"]


def table(rows):
    lines = ["%-18s " % "view-variant" + " ".join("%9s" % k for k in KEYS)]
    for name, r in sorted(rows.items(), key=lambda kv: (kv[1]["view"], kv[1]["variant"] != "base", kv[1]["variant"])):
        lines.append("%-18s " % name + " ".join("%9.4f" % r.get(k, float("nan")) for k in KEYS))
    return "\n".join(lines)


def pictures(rows, out):
    from PIL import Image, ImageDraw
    os.makedirs(out, exist_ok=True)
    by_view = {}
    for name, r in rows.items():
        by_view.setdefault(r["view"], []).append((r["variant"], r["image"]))
    for view, items in by_view.items():
        items.sort(key=lambda t: (t[0] != "base", t[0]))
        cols = 3
        rows_n = (len(items) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * 574, rows_n * 400), (20, 20, 20))
        d = ImageDraw.Draw(sheet)
        for i, (variant, img) in enumerate(items):
            tile = Image.fromarray((np.clip(img, 0, 1) * 255).astype("uint8")).resize((570, 380))
            x, y = (i % cols) * 574, (i // cols) * 400
            sheet.paste(tile, (x, y + 18))
            d.text((x + 4, y + 3), "working view (parity checked on the base only): %s %s" % (view, variant), fill=(255, 255, 255))
        sheet.save(os.path.join(out, "e40-puff-%s.png" % view))


def collect(ctx):
    rows = measure_all(ctx.folder)
    print(table(rows))
    metrics = []
    for name, r in rows.items():
        for k in KEYS:
            v = r.get(k)
            if v is not None and v == v:
                metrics.append(Metric.info("e40puff.%s.%s" % (name, k), v))
    return metrics


register_series("e40-puff", stage_list(), collect, exclusive=False, who="e40-puff")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--prepare", action="store_true")
    p.add_argument("--report", action="store_true")
    p.add_argument("--views", default=",".join(DEFAULT_VIEWS))
    p.add_argument("--variants", default=",".join(VARIANTS))
    p.add_argument("--root", help="the player's output folder, default build/WeatherPlayer; a stage's folder is <root>/e40-puff-<label>")
    p.add_argument("--pictures")
    a = p.parse_args()
    if a.prepare:
        prepare(a.views.split(","), a.variants.split(","))
    if a.report:
        root = a.root or BUILD.replace("weather-series", "WeatherPlayer")
        rows = measure_all(lambda label: os.path.join(root, "e40-puff-" + label))
        print(table(rows))
        if a.pictures:
            pictures(rows, a.pictures)
    return 0


if __name__ == "__main__":
    sys.exit(main())
