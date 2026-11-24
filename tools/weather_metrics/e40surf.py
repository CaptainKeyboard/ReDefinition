"""E40 SURFACE: one factor at a time on the parity-checked views of game start 64 (series e40-surf; package SURFACE, 2026-11-15).

    python tools/weather_metrics/e40pp.py --prepare --state now --start 64 --log <K-T2 soft60 log>      (the views' jobs and maps, once)
    python tools/weather_metrics/e40surf.py --prepare [--views gclock,sclock,tyhumg,tycumg] [--variants a,b]
    python tools/build_weather_player.py --series e40-surf --no-build
    python tools/weather_metrics/e40surf.py --report                                                   (the table of the last run, deltas against the base)

The base view is e40pp's replay of the game's frame (label 'player (parity checked ...)' for the views that pass the check); each variant changes ONE factor of its job and nothing
else: the same camera, sun, maps, aerial perspective, display stage and march. Each frame is laid over the game's own frame without clouds (e40pp.compose) and measured by
surface_visibility.measure on the cloud pixels (the same mask for every variant of a view: the base's).
"""
import argparse
import copy
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
from weather_metrics import surface_visibility as sv  # noqa: E402

REPO = e40pp.REPO
BUILD = e40pp.BUILD
INPUT = os.path.join(BUILD, "e40surf-in")
RESULTS = os.path.join(BUILD, "e40surf-results.json")
LABEL = "surf-a"
DEFAULT_VIEWS = ("gclock", "sclock", "tyhumg", "tycumg")


def each_layer(job, f):
    for layer in job.get("layers", []):
        f(layer)


def each_type(job, f):
    for layer in job.get("layers", []):
        for t in layer.get("types", []):
            f(t)


def setter(name, value):
    def f(layer):
        layer[name] = value
    return f


def scaler(name, factor):
    def f(layer):
        if name in layer:
            layer[name] = layer[name] * factor
    return f


def v_aerial(job):
    job["far"]["aerialPerspective"] = 0


def v_lm2off(job):
    each_layer(job, setter("lightCellWidth", 0.0))


def v_ambhalf(job):
    for key in ("ambientTop", "ambientBottom"):
        job[key] = [x * 0.5 for x in job[key]]


def v_ambzero(job):
    for key in ("ambientTop", "ambientBottom"):
        job[key] = [x * 0.0 for x in job[key]]


def v_noms(job):
    each_layer(job, setter("ambientMs", 0.0))


def v_gainhalf(job):
    job["ambientGain"] = job.get("ambientGain", 0.25) * 0.5 if job.get("ambientGain", 0) else 0.125


def v_noise16(job):
    each_layer(job, scaler("noiseAmount", 1.6))


def v_scale2(job):
    each_layer(job, scaler("noiseScale", 2.0))


def v_ero2(job):
    each_type(job, scaler("erosion", 2.0))


def v_nosmooth(job):
    each_layer(job, setter("shapeSmoothing", 0.0))
    each_type(job, setter("shapeSmoothing", 0.0))


def v_nolod(job):
    job.setdefault("noise", {})["noLod"] = 1
    job["noise"]["noFine"] = 0


def v_soft02(job):
    job["soft"] = 0.2


def v_soft10(job):
    job["soft"] = 1.0


def v_sharp(job):
    # the edge variants together: soft 0.2, no smoothing
    v_soft02(job)
    v_nosmooth(job)


VARIANTS = {
    "base": ("the replay as e40pp makes it", None),
    "aerial0": ("(a) the aerial perspective off (far.aerialPerspective 0)", v_aerial),
    "lm2off": ("(b) the cell diffusion of LM2 off (layers' lightCellWidth 0: the slab diffusion term)", v_lm2off),
    "ambhalf": ("(b) the ambient light (top and bottom) halved", v_ambhalf),
    "ambzero": ("(b) no ambient light", v_ambzero),
    "noms": ("(b) the sky's light inside the clouds off (ambientMs 0)", v_noms),
    "gainhalf": ("(b) the ambient gain halved", v_gainhalf),
    "noise16": ("(c) the shape noise 1.6 times stronger (noiseAmount)", v_noise16),
    "scale2": ("(c) the shape noise twice as fine (noiseScale x 2)", v_scale2),
    "ero2": ("(c) the erosion of every type doubled", v_ero2),
    "nosmooth": ("(c) no shape smoothing", v_nosmooth),
    "nolod": ("(c) no distance LOD of the noise (noLod)", v_nolod),
    "soft02": ("(e) cloudSoftness 0.2 (0.5 is the default)", v_soft02),
    "soft10": ("(e) cloudSoftness 1.0", v_soft10),
    "sharp": ("(c, e) soft 0.2 and no smoothing", v_sharp),
}


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
            # the maps and heights of the view under the variant's names
            for fn in os.listdir(e40pp.INPUT):
                if fn.startswith(base_tag + "-"):
                    shutil.copyfile(os.path.join(e40pp.INPUT, fn), os.path.join(INPUT, new_tag + fn[len(base_tag):]))
            text = json.dumps(jobs[base_id]).replace(base_tag + "-", new_tag + "-")
            job = json.loads(text)
            job["id"] = "gn-%s-prod-f0" % new_tag
            fn = VARIANTS[variant][1]
            if fn:
                fn(job)
            spec = json.loads(json.dumps(specs[base_id]).replace(base_tag + "-", new_tag + "-"))
            spec["id"] = job["id"]
            out_jobs.append(job)
            out_specs.append(spec)
            out_stages[name] = dict(stages[view], view=view, variant=variant)
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


def stage_list():
    stages = prepared()
    if not stages:
        return []
    views = ",".join(e40pp.view_tag(n) for n in stages)
    return [Stage(LABEL, ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", views, "-media", "pr50", "-marches", e40pp.MARCH] + e40pp.LIGHT, size=(e40pp.W, e40pp.H),
                  proxy=False, timeout=3400)]


def measure_all(folder):
    stages = prepared()
    rows = {}
    bases = {}
    for name, st in stages.items():
        spec = e40pp.VIEWS[name]
        off = e40pp.game_frame(spec, "off_a", st.get("start", e40pp.GAME_START))
        path = os.path.join(folder, e40pp.case_file(name, e40pp.MARCH) + ".npy")
        if off is None or not os.path.isfile(path):
            continue
        m = e40ty.load_lum(folder, e40pp.case_file(name, e40pp.MARCH))
        img = e40pp.compose(m, off, st)
        ok = e40pp.measure_mask(st["view"], st["alt"], off.shape)
        rows[name] = dict(view=st["view"], variant=st["variant"], **sv.measure(img, off, ok))
        rows[name]["image"] = img
        bases.setdefault(st["view"], None)
    return rows


def table(rows):
    keys = ["cover", "lit", "cv8", "cv16", "cv32", "hf", "base", "edge", "dsky_luma", "dchroma"]
    lines = ["%-16s " % "view-variant" + " ".join("%9s" % k for k in keys)]
    for name, r in sorted(rows.items(), key=lambda kv: (kv[1]["view"], kv[1]["variant"] != "base", kv[1]["variant"])):
        base = rows.get(r["view"])
        cells = []
        for k in keys:
            v = r.get(k, float("nan"))
            if r["variant"] != "base" and base is not None and base.get(k) and k in ("lit", "cv8", "cv16", "cv32", "edge"):
                cells.append("%+8.0f%%" % (100.0 * (v / base[k] - 1.0)))
            else:
                cells.append("%9.4f" % v)
        lines.append("%-16s " % name + " ".join(cells))
    return "\n".join(lines)


def pictures(rows, out):
    from PIL import Image
    os.makedirs(out, exist_ok=True)
    by_view = {}
    for name, r in rows.items():
        by_view.setdefault(r["view"], []).append((r["variant"], r["image"]))
    for view, items in by_view.items():
        items.sort(key=lambda t: (t[0] != "base", t[0]))
        tiles = []
        for variant, img in items:
            tile = Image.fromarray((np.clip(img[:int(img.shape[0] * 0.6), :], 0, 1) * 255).astype("uint8")).resize((570, 192))
            tiles.append((variant, tile))
        cols = 3
        rows_n = (len(tiles) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * 574, rows_n * 212), (20, 20, 20))
        from PIL import ImageDraw
        d = ImageDraw.Draw(sheet)
        for i, (variant, tile) in enumerate(tiles):
            x, y = (i % cols) * 574, (i // cols) * 212
            sheet.paste(tile, (x, y + 18))
            d.text((x + 4, y + 3), "%s %s" % (view, variant), fill=(255, 255, 255))
        sheet.save(os.path.join(out, "e40-surf-%s.png" % view))


def collect(ctx):
    folder = ctx.folder(LABEL)
    rows = measure_all(folder)
    print(table(rows))
    metrics = []
    for name, r in rows.items():
        for k in ("lit", "cv8", "cv32", "edge", "base"):
            if k in r:
                metrics.append(Metric.info("e40surf.%s.%s" % (name, k), r[k]))
    return metrics


register_series("e40-surf", stage_list(), collect, exclusive=False, who="e40-surf")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--prepare", action="store_true")
    p.add_argument("--report", action="store_true")
    p.add_argument("--views", default=",".join(DEFAULT_VIEWS))
    p.add_argument("--variants", default=",".join(VARIANTS))
    p.add_argument("--folder")
    p.add_argument("--pictures")
    a = p.parse_args()
    if a.prepare:
        prepare(a.views.split(","), a.variants.split(","))
    if a.report:
        folder = a.folder or os.path.join(BUILD.replace("weather-series", "WeatherPlayer"), "e40-surf-" + LABEL)
        rows = measure_all(folder)
        print(table(rows))
        if a.pictures:
            pictures(rows, a.pictures)
    return 0


if __name__ == "__main__":
    sys.exit(main())
