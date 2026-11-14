"""E40 ground cost: what the clouds cost on the GPU at the ground, where it goes, and what the levers do (player series e40-gc-cost, e40-gc-temporal).

    python tools/build_weather_player.py --series e40-gc-cost
    python tools/build_weather_player.py --series e40-gc-temporal
    python tools/weather_metrics/e40gc.py --report       the two series' results side by side (needs both to have run)
    python tools/weather_metrics/e40gc.py --selftest

The pass is the product's (the soft water content with the owner's softness, SoftTable's media pr50 = 0.5 and pr100 = 1.0; LayerReconstructor in its mode Low with the history, 0.2),
at 1707 x 960 for the cost and 1708 x 960 for the pictures. The views:

  ground, above   the cumulus layer alone (the scene of the e40-gb series: map 0.665, the layer's first type), the camera at 264 m looking 18 degrees up / at 8364 m looking 25 down
  l4ground, l4above   the four layers of Clouds-Kerbin.cfg in fair weather with the same cameras: rain deck empty (map 0), cumulus map 0.45 with the type 0.25 (humilis), cumulonimbus map 0.15,
                  cirrus map 0.35 (the numbers of the cfg, B3's light and soft keys). A constructed fair-weather sky, not a frame of the game.
  l4empty         the same four layers with every map at 0 (the ground camera): nothing to see, what a clear sky costs

A case is SoftTable.Case of tests/WeatherPlayer/Assets/Runner/SoftDensityScene.cs: a march name and modifiers joined by '+' (r40 = recon.resolution 0.4, jit = recon.jitter 1,
blk2 = cloudResolution 2, ls3 = layers.lightStride 3, lc100 = layers.lightCutoff 0.1, lq1 = cloudLightQuality Medium, ds2 = layers.ditherSequence 2, skip = cloudSkipEmpty,
skipc = cloudSkipClosed, dbg<N> = the cost debug mask, tseq1m16 = layers.spanTight 1 with 16 grid points on the cut span).
"""
import argparse
import json
import math
import os
import re
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40gt as gt  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40gc-in")
COST_JSON = os.path.join(BUILD, "e40gc-cost-results.json")
TEMPORAL_JSON = os.path.join(BUILD, "e40gc-temporal-results.json")
PREFIX = "e40-gc"
MAP_W, MAP_H = 8, 4

CUMULUS_MAP, CUMULUS_TYPE = 0.665, 0.0
# The fair-weather stack: (layer name, map value, type value).
FAIR = [("Kerbin-weather-rain", 0.0, 0.0), ("Kerbin-cumulus", 0.45, 0.25), ("Kerbin-cumulonimbus", 0.15, 0.0), ("Kerbin-cirrus", 0.35, 0.0)]
VIEWS = {"ground": "ground", "above": "above", "l4ground": "ground", "l4above": "above", "l4empty": "ground"}


def const_map(value):
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    name = "k%03d.r8" % v
    os.makedirs(INPUT, exist_ok=True)
    np.full(MAP_W * MAP_H, v, dtype=np.uint8).tofile(os.path.join(INPUT, name))
    return name


def job_layer(spec, extras, cov, typ):
    """The cfg's layer as a job layer with B3's soft and light keys, the coverage and the type map constant."""
    lk, tk = extras[spec["name"]]
    jl = e40o.job_layer(spec, const_map(cov), const_map(typ))
    jl.update(lk)
    for t in jl["types"]:
        t.update(tk.get(t["name"], {}))
    return jl


def build_jobs():
    specs = {s["name"]: s for s in e40o.kerbin_layers()}
    extras = e40ty.cfg_extras()
    jobs = []
    for key, base in VIEWS.items():
        if key in ("ground", "above"):
            layer_list = [job_layer(specs[e40o.CUMULUS], extras, CUMULUS_MAP, CUMULUS_TYPE)]
        else:
            layer_list = [job_layer(specs[name], extras, 0.0 if key == "l4empty" else m, 0.0 if key == "l4empty" else t) for name, m, t in FAIR]
        cam = gn.camera(base)
        light = dict(sun=lay.sun_vector(cam.pos, 0.0, gn.SUN_ELEVATION), sunColour=gn.SUN_COLOUR, ambientTop=gn.AMBIENT_TOP, ambientBottom=gn.AMBIENT_BOTTOM)
        args = gn.prod()
        args.update(light)
        jobs.append(lay.job("gn-%s-prod-f0" % key, "lum", cam, layer_list, gn.SIZE[0], gn.SIZE[1], planet=gn.R, mapWidth=MAP_W, mapHeight=MAP_H, frameNumber=0, **args))
    return jobs


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def ensure_inputs():
    path = os.path.join(INPUT, "jobs.json")
    sources = [os.path.abspath(__file__), e40ty.__file__, e40o.CFG]
    if not os.path.isfile(path) or os.path.getmtime(path) < max(os.path.getmtime(s) for s in sources):
        write_inputs()


# --------------------------------------------------------------------------------------------------------------------------------
# The cases
# --------------------------------------------------------------------------------------------------------------------------------
B32 = "seq1m32"
B24 = "seq1m24"
RES = ["r40", "r33", "r40+jit", "r33+jit", "r50+jit"]
LIGHT = ["ls3", "ls4", "lc100", "lc200", "lq0", "lq1", "lq2"]
OTHER = ["blk2", "blk3", "ds2", "skip", "skip+skipc"]
SPAN = ["tseq1m8", "tseq1m12", "tseq1m16", "tseq1m24", "tseq1m32"]


def on(base, mods):
    return [base + "+" + m for m in mods]


# Soft 0.5 (pr50): the grid points of the owner's setting and the levers on the Medium count.
LEVERS50 = [B32] + on(B32, RES + OTHER + LIGHT) + SPAN
# Soft 1.0 (pr100) with its 24 grid points.
LEVERS100 = [B24] + on(B24, ["r40", "r40+jit", "r33+jit", "blk3", "ls3", "lc100", "lq1"]) + ["tseq1m12", "tseq1m16"]
ATTR = [B32, B32 + "+dbg1", B32 + "+dbg3", B32 + "+dbg7"]

COST_STAGES = [
    ("cost-core", "ground,above,l4ground,l4above,l4empty", "hard,pr50,pr100", ["seq1", B32, B24]),
    ("cost-attr", "ground,above,l4ground,l4above,l4empty", "pr50", ATTR),
    ("cost-lev50", "ground,above,l4ground,l4above", "pr50", LEVERS50),
    ("cost-lev100", "ground,l4ground", "pr100", LEVERS100),
]
TEMPORAL_STAGES = [
    ("temporal50", "ground,above,l4ground", "pr50", ["seq1"] + LEVERS50 + [B24]),
    ("temporal100", "ground,l4ground", "pr100", ["seq1m24"] + LEVERS100[1:] + [B32]),
]


# Round 2 (e40-gc2-*): the march below half size on the owner's grid counts, the tile pass and the leap over empty samples against what a clear layer costs, the dropped layers
# (lm: the layers marched, the cost of an ideal drop of the empty ones before the march), combinations.
R2_50 = (["seq1", B32] + on("seq1", ["r33", "r40", "r33+skip", "r40+skip"]) + on(B32, ["r25", "r25+jit", "r33+skip", "r40+skip", "leap2", "leap4", "leap4+skip",
                                                                                      "r33+ls3+lc100", "r33+skip+ls3+lc100"])
         + on(B24, ["r33", "r33+skip"]) + on("tseq1m16", ["r33", "r40", "r33+skip", "r33+jit"]) + on("tseq1m12", ["r33", "r40"]) + ["tseq1m8+r40"])
R2_MASK = on(B32, ["lm2", "lm10", "lm6", "lm14"])
R2_100 = [B24] + on(B24, ["r25", "r33", "r40", "r33+skip", "r33+skip+ls3+lc100"]) + on("tseq1m12", ["r33"]) + on("tseq1m16", ["r33"])
COST2_STAGES = [
    ("cost-r2-50", "ground,above,l4ground,l4above,l4empty", "pr50", R2_50),
    ("cost-r2-mask", "l4ground,l4above", "pr50", [B32] + R2_MASK),
    ("cost-r2-100", "ground,l4ground,l4empty", "pr100", R2_100),
]
TEMPORAL2_STAGES = [
    ("temporal-r2-50", "ground,above,l4ground", "pr50", [c for c in R2_50 if c != "seq1"]),
    ("temporal-r2-100", "ground,l4ground", "pr100", R2_100),
]


# Package L2DROP (E40 L2 A2, e40-l2drop-*): layers.dropEmpty on and off in ONE run (the case modifier "drop" is LayerFrame.DropEmpty), the cost and the pictures. The pictures of a
# case and of the same case with "drop" must be identical: collect_drop_compare prints the largest absolute difference of every pair of dumped frames.
DROP_BASES = [B32, "seq1"]
DROP_100 = [B24]
DROPCOST_STAGES = [
    ("cost-drop50", "ground,above,l4ground,l4above,l4empty", "pr50", DROP_BASES + on(B32, ["drop"]) + on("seq1", ["drop", "r33", "r33+drop"]) + on(B32, ["r33", "r33+drop"])),
    ("cost-drop100", "ground,above,l4ground,l4above", "pr100", DROP_100 + on(B24, ["drop", "r33", "r33+drop"])),
]
DROPTEMPORAL_STAGES = [
    ("temporal-drop50", "l4ground,l4above,l4empty", "pr50", [B32] + on(B32, ["drop"]) + ["seq1"] + on("seq1", ["drop"])),
]


def cost_stage(label, views, media, cases):
    return Stage(label, ["-mode", "scene", "-scene", "soft-cost", "-in", INPUT, "-views", views, "-media", media, "-marches", ",".join(cases)],
                 size=(1707, 960), proxy=True, timeout=1700)


def temporal_stage(label, views, media, cases):
    return Stage(label, ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", views, "-media", media, "-marches", ",".join(cases), "-motion", "all"],
                 size=(1280, 720), proxy=False, timeout=3400)


# --------------------------------------------------------------------------------------------------------------------------------
# Cost
# --------------------------------------------------------------------------------------------------------------------------------
PARTS = ("march", "resolve", "composite", "total", "n")


def collect_cost(ctx, stages=None, json_path=None):
    rows = {}
    for label, _, _, _ in stages or COST_STAGES:
        try:
            values = ctx.player_metrics(label)
        except KeyError:
            continue
        for k, v in values.items():
            if not k.startswith("softcost."):
                continue
            _, part, rest = k.split(".", 2)
            view, medium, case = rest.split(".", 2)
            rows.setdefault("%s|%s|%s" % (view, medium, case), {})[part] = v[0]
    metrics = []
    lines = ["E40-GC GPU ms at 1707 x 960 (march | resolve | composite | total; median of 40 frames in two rounds of opposite order)"]
    for key in sorted(rows):
        r = rows[key]
        lines.append("   %-60s %6.3f | %6.3f | %6.3f | %6.3f" % ((key,) + tuple(r.get(p, float("nan")) for p in PARTS[:4])))
        metrics.append(Metric.info("e40gc.cost." + key.replace("|", "."), r.get("total", float("nan"))))
    with open(json_path or COST_JSON, "w", encoding="utf-8") as f:
        json.dump(dict(rows=rows, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


# --------------------------------------------------------------------------------------------------------------------------------
# Temporal
# --------------------------------------------------------------------------------------------------------------------------------
STATIC = [36, 37, 38, 39]
MOTION = {"fly": [37, 38], "turnfast": [37, 38], "flyfast": [37, 38]}
CUT = [0, 2, 5, 10]


def have(folder, name):
    return os.path.isfile(os.path.join(folder, name + ".npy"))


def collect_temporal(ctx, stages=None, json_path=None):
    out_dir = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines, results = [], [], {}
    lines.append("E40-GC: the pass as the game runs it (full target 1708 x 960; display values; each case against its own medium's converged picture)")
    lines.append("   static: tstd (4 consecutive frames) closed | rim ; hfx rim ; err = rms of one frame against the converged picture ; share closed | rim | cloud ;"
                 " motion err fly 300 m/s, turn 180 deg/s, fly 2400 m/s ; cut err at frames 0 2 5 10")
    crops = {}
    for label, _, media, cases in stages or TEMPORAL_STAGES:
        folder = ctx.folder(label)
        for medium in media.split(","):
            for view in [v for v in VIEWS if os.path.isfile(os.path.join(folder, "gs-%s-%s-static-ref-f%d.npy" % (v, medium, STATIC[0])))]:
                alias = VIEWS[view]
                ref0 = gn.full_frame(folder, "gs-%s-%s-static-ref-f%d" % (view, medium, STATIC[0]))
                alpha = np.clip(ref0[..., 3], 0.0, 1.0)
                if view not in crops:
                    crops[view] = gn.pick_crop(alpha, 360, 200)
                x, y, w, h = crops[view]
                mask_set = {k: gt.inner(v) for k, v in gn.masks(alpha).items()}
                shares = {k: float(v.mean()) for k, v in mask_set.items()}
                ref_rgb = gn.full_display(ref0, alias)
                ref_l = gn.luma(ref_rgb)
                ref_lap = gn.lap(ref_l)
                gn.save_png(os.path.join(out_dir, "%s-t-%s-%s-converged.png" % (PREFIX, view, medium)), ref_rgb)
                for case in cases:
                    for mode in ("plain", "acc20"):
                        if mode == "plain" and case != "seq1":
                            continue
                        name = "%s-%s" % (case, mode)
                        if not have(folder, "gs-%s-%s-static-%s-f%d" % (view, medium, name, STATIC[0])):
                            continue
                        fr = [gn.full_frame(folder, "gs-%s-%s-static-%s-f%d" % (view, medium, name, i)) for i in STATIC]
                        ls = np.stack([gn.luma(gn.full_display(m, alias)) for m in fr])
                        tstd = ls.std(axis=0)
                        row = {}
                        for mk in ("closed", "rim", "cloud"):
                            m = mask_set[mk]
                            if m.sum() < 50:
                                row[mk] = dict(tstd=float("nan"), hfx=float("nan"), blob=float("nan"))
                                continue
                            row[mk] = dict(tstd=float(tstd[m].mean()), hfx=float(gn.lap(ls[0])[m].mean() - ref_lap[m].mean()), blob=gt.blob(ls[0], m) - gt.blob(ref_l, m))
                        d0 = (ls[0] - ref_l)[mask_set["cloud"]]
                        err0 = float(np.sqrt((d0 * d0).mean()))
                        motion, cut = {}, []
                        for sc, frames in MOTION.items():
                            errs = []
                            for i in frames:
                                nm = "gs-%s-%s-%s-%s-f%d" % (view, medium, sc, name, i)
                                if not have(folder, nm):
                                    continue
                                ref = gn.full_frame(folder, "gs-%s-%s-%s-ref-f%d" % (view, medium, sc, i))
                                ms = gn.masks(np.clip(ref[..., 3], 0.0, 1.0))
                                d = (gn.luma(gn.full_display(gn.full_frame(folder, nm), alias)) - gn.luma(gn.full_display(ref, alias)))[ms["cloud"]]
                                errs.append(float(np.sqrt((d * d).mean())))
                            if errs:
                                motion[sc] = float(np.mean(errs))
                        for i in CUT:
                            nm = "gs-%s-%s-cut-%s-f%d" % (view, medium, name, i)
                            if have(folder, nm):
                                dc = (gn.luma(gn.full_display(gn.full_frame(folder, nm), alias)) - ref_l)[mask_set["cloud"]]
                                cut.append(float(np.sqrt((dc * dc).mean())))
                        results["%s|%s|%s" % (view, medium, name)] = dict(static=row, err_static=err0, motion=motion, cut=cut, shares=shares)
                        for mk in row:
                            for key, val in row[mk].items():
                                metrics.append(Metric.info("e40gc.t.%s.%s.%s.%s.%s" % (view, medium, name, mk, key), val))
                        metrics.append(Metric.info("e40gc.t.%s.%s.%s.err" % (view, medium, name), err0))
                        lines.append("   %-8s %-6s %-26s tstd %.4f | %.4f ; hfx %+.4f ; err %.4f ; share %.3f | %.3f | %.3f ; motion %s ; cut %s"
                                     % (view, medium, name, row["closed"]["tstd"], row["rim"]["tstd"], row["rim"]["hfx"], err0, shares["closed"], shares["rim"], shares["cloud"],
                                        " ".join("%.4f" % motion[sc] for sc in motion), " ".join("%.4f" % c for c in cut)))
                        strip = [gn.enlarge(gn.full_display(fr[k], alias)[y:y + h, x:x + w], 2) for k in range(3)]
                        noise = np.clip(tstd[y:y + h, x:x + w] * 8.0, 0.0, 1.0)
                        strip.append(gn.enlarge(np.repeat(noise[..., None], 3, axis=-1), 2))
                        gn.save_png(os.path.join(out_dir, "%s-t-%s-%s-%s-seq.png" % (PREFIX, view, medium, name)), np.concatenate(strip, axis=1))
                        gn.save_png(os.path.join(out_dir, "%s-t-%s-%s-%s.png" % (PREFIX, view, medium, name)), gn.full_display(fr[0], alias))
    with open(json_path or TEMPORAL_JSON, "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines, crops={k: list(v) for k, v in crops.items()}), f, indent=1)
    print("\n".join(lines))
    return metrics


# --------------------------------------------------------------------------------------------------------------------------------
# The two series side by side
# --------------------------------------------------------------------------------------------------------------------------------
def report():
    cost, temporal = {}, {}
    for path in (COST_JSON, os.path.join(BUILD, "e40gc2-cost-results.json")):
        if os.path.isfile(path):
            with open(path, encoding="utf-8") as f:
                cost.update(json.load(f)["rows"])
    for path in (TEMPORAL_JSON, os.path.join(BUILD, "e40gc2-temporal-results.json")):
        if os.path.isfile(path):
            with open(path, encoding="utf-8") as f:
                temporal.update(json.load(f)["results"])
    out = ["view | medium | case | ms ground-pass total (march) | tstd closed | tstd rim | err static | err fly | err turnfast | err flyfast | err cut(2)"]
    keys = sorted(set(k for k in temporal) | set(k for k in cost))
    for key in keys:
        view, medium, case = key.split("|", 2)
        c = cost.get(key) or cost.get("%s|%s|%s" % (view, medium, case.replace("-acc20", "")))
        t = temporal.get(key) or temporal.get("%s|%s|%s-acc20" % (view, medium, case))
        ms = "%.3f (%.3f)" % (c["total"], c["march"]) if c else "-"
        if t:
            mo = t["motion"]
            cut = t["cut"][1] if len(t["cut"]) > 1 else float("nan")
            out.append("%s | %s | %s | %s | %.4f | %.4f | %.4f | %.4f | %.4f | %.4f | %.4f" % (view, medium, case, ms, t["static"]["closed"]["tstd"], t["static"]["rim"]["tstd"],
                                                                                          t["err_static"], mo.get("fly", float("nan")), mo.get("turnfast", float("nan")),
                                                                                          mo.get("flyfast", float("nan")), cut))
        else:
            out.append("%s | %s | %s | %s | | | | | | |" % (view, medium, case, ms))
    print("\n".join(out))


TVC_GROUND = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "K-T2", "t09-tvc1707-ground-tvc-1707x960-natural.png")


def sheet(name, view, medium, labelled, tvc=True, tile=(1100, 619)):
    """A 2-wide sheet of full frames: TVC's game frame (the K-T2 tile, the look only), then each (label, case) of ours at the medium; written as e40-gc-sheet-<name>.png."""
    from PIL import Image, ImageDraw
    out_dir = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "E40")
    tiles = []
    if tvc and os.path.isfile(TVC_GROUND):
        tiles.append(("TVC (game, its own scene and weather; reference for the look only)", Image.open(TVC_GROUND).convert("RGB")))
    for text, case in labelled:
        mode = "plain" if case.endswith("-plain") else "acc20"
        base = case[:-6] if case.endswith("-plain") else case
        path = os.path.join(out_dir, "%s-t-%s-%s-%s-%s.png" % (PREFIX, view, medium, base, mode))
        tiles.append((text, Image.open(path).convert("RGB")))
    rows = (len(tiles) + 1) // 2
    img = Image.new("RGB", (tile[0] * 2, (tile[1] + 20) * rows), (0, 0, 0))
    draw = ImageDraw.Draw(img)
    for i, (text, pic) in enumerate(tiles):
        x, y = (i % 2) * tile[0], (i // 2) * (tile[1] + 20)
        draw.text((x + 6, y + 4), text, fill=(255, 255, 255))
        img.paste(pic.resize(tile, Image.Resampling.LANCZOS), (x, y + 20))
    path = os.path.join(out_dir, "%s-sheet-%s.png" % (PREFIX, name))
    img.save(path)
    print(path)


DROP_FRAME = re.compile(r"^gs-(?P<view>[a-z0-9]+)-(?P<medium>[a-z0-9]+)-(?P<scenario>[a-z]+)-(?P<case>.+)\+drop-(?P<mode>plain|acc20)-f(?P<frame>\d+)$")


def drop_pairs(folder):
    """(frame name with the drop, the same frame without it) for every dumped frame of a case run with 'drop' and without."""
    pairs = []
    for fn in sorted(os.listdir(folder)):
        if not fn.endswith(".npy"):
            continue
        name = fn[:-4]
        m = DROP_FRAME.match(name)
        if m:
            plain = "gs-%s-%s-%s-%s-%s-f%s" % (m.group("view"), m.group("medium"), m.group("scenario"), m.group("case"), m.group("mode"), m.group("frame"))
            if os.path.isfile(os.path.join(folder, plain + ".npy")):
                pairs.append((name, plain))
    return pairs


def collect_drop_compare(ctx):
    """The pictures with layers.dropEmpty against the same case without it: the largest absolute difference of the half floats over every pair of frames (exact: 0)."""
    metrics = collect_temporal(ctx, DROPTEMPORAL_STAGES, os.path.join(BUILD, "e40l2drop-temporal-results.json"))
    worst, lines = 0.0, ["E40-L2DROP: frames with layers.dropEmpty against the same case without it (max abs difference over RGBA of the half floats; 0 = identical)"]
    for label, _, _, _ in DROPTEMPORAL_STAGES:
        folder = ctx.folder(label)
        for dropped, plain in drop_pairs(folder):
            a = np.load(os.path.join(folder, dropped + ".npy")).astype(np.float64)
            b = np.load(os.path.join(folder, plain + ".npy")).astype(np.float64)
            diff = float(np.abs(a - b).max()) if a.shape == b.shape else float("inf")
            worst = max(worst, diff)
            lines.append("   %-70s max abs diff %.8f" % (dropped, diff))
            metrics.append(Metric.info("e40l2drop.diff." + dropped.replace("+", "_"), diff))
    lines.append("   pairs %d, worst %.8f" % (len(lines) - 1, worst))
    metrics.append(Metric.info("e40l2drop.diff.worst", worst))
    print("\n".join(lines))
    with open(os.path.join(BUILD, "e40l2drop-compare.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    return metrics


ensure_inputs()
register_series("e40-l2drop-cost", [cost_stage(*s) for s in DROPCOST_STAGES],
                lambda ctx: collect_cost(ctx, DROPCOST_STAGES, os.path.join(BUILD, "e40l2drop-cost-results.json")), exclusive=True, who="e40-gc")
register_series("e40-l2drop-temporal", [temporal_stage(*s) for s in DROPTEMPORAL_STAGES], collect_drop_compare, exclusive=False, who="e40-gc")
register_series("e40-gc-cost", [cost_stage(*s) for s in COST_STAGES], collect_cost, exclusive=True, who="e40-gc")
register_series("e40-gc-temporal", [temporal_stage(*s) for s in TEMPORAL_STAGES], collect_temporal, exclusive=False, who="e40-gc")
register_series("e40-gc2-cost", [cost_stage(*s) for s in COST2_STAGES], lambda ctx: collect_cost(ctx, COST2_STAGES, os.path.join(BUILD, "e40gc2-cost-results.json")),
                exclusive=True, who="e40-gc")
register_series("e40-gc2-temporal", [temporal_stage(*s) for s in TEMPORAL2_STAGES],
                lambda ctx: collect_temporal(ctx, TEMPORAL2_STAGES, os.path.join(BUILD, "e40gc2-temporal-results.json")), exclusive=False, who="e40-gc")


def selftest():
    for cases in (LEVERS50, LEVERS100, ATTR):
        assert len(set(cases)) == len(cases), "a case twice"
    for label, views, media, cases in COST_STAGES + TEMPORAL_STAGES + COST2_STAGES + TEMPORAL2_STAGES + DROPCOST_STAGES + DROPTEMPORAL_STAGES:
        assert len(set(cases)) == len(cases), label
        assert all(v in VIEWS for v in views.split(",")), label
        assert all("," not in c for c in cases)
    jobs = build_jobs()
    assert sorted(j["id"] for j in jobs) == sorted("gn-%s-prod-f0" % v for v in VIEWS)
    assert len([j for j in jobs if j["id"] == "gn-l4ground-prod-f0"][0]["layers"]) == 4
    print("selftest passed (%d jobs)" % len(jobs))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--report", action="store_true")
    parser.add_argument("--sheet", nargs="+", metavar="NAME VIEW MEDIUM [LABEL=CASE ...]", help="e.g. --sheet r40 ground pr50 'today=seq1' 'r40 jit=seq1m32+r40+jit'")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.report:
        report()
    elif a.sheet:
        sheet(a.sheet[0], a.sheet[1], a.sheet[2], [tuple(s.split("=", 1)) for s in a.sheet[3:]])
