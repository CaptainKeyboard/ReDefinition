"""E40 C1: what the clouds cost with the game's real maps (player series e40-c1-cost, e40-c1-aux, e40-c1-sheet).

    python tools/weather_metrics/e40c1.py --prepare <maps-dir> [<fallback-maps-dir>]
    python tools/build_weather_player.py --series e40-c1-cost [--no-build]
    python tools/build_weather_player.py --series e40-c1-aux  [--no-build]
    python tools/build_weather_player.py --series e40-c1-sheet [--no-build]
    python tools/weather_metrics/e40c1.py --report            the cost and the ray statistics side by side (needs the series to have run)
    python tools/weather_metrics/e40c1.py --selftest

<maps-dir> is the dump of tests/ReDefinition.Tests/Atmospheres/T09/SkyMapsExplore.cs (SKYMAPS_OUT; SKYMAPS_UTS=1686000,1881000), the maps the game hands to the layer pass at the K-T2 site
(KSC): per time and layer `_nocombine` (the layer maps before the combination of E40 F1: what the game had at start 60) and `_cover` (after it: the product's default), the type map. The
fallback dump (SKYMAPS_FALLBACK=1: every layer carries the field's shared cover, which is what start 61's fair scene looked like) is the second folder.

Scenes: clock (ut 1686000, field 0.64), fair (ut 1881000, the generator's own maps), fairfb (ut 1881000 with the shared cover in every layer). Variants: before (`_nocombine` maps and the
tuning keys cloud.coverMeaning 0: the sky of start 60) and now (`_cover` maps, the product's defaults: start 61). Views are those of K-T2 (T09Probe.Views): ground (200 m AGL, 18 degrees up,
east), inside (4550 m AGL, 8 up, east), above (8300 m, 25 down, east), sky3 (200 m, 38 up, west).

The cost series runs the scene `soft-cost` (SoftDensityScene.cs; the product pass at 1707 x 960: march, resolve, composite) with the product march; the aux series draws the ray statistics (steps,
optical depth, alpha per pixel) at a third of the size with the layers scene; the sheet series draws the pictures.
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
from weather_metrics import e40gc as gc  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
COST_IN = os.path.join(BUILD, "e40c1-in")
AUX_IN = os.path.join(BUILD, "e40c1-aux-in")
SHEET_IN = os.path.join(BUILD, "e40c1-sheet-in")
COST_JSON = os.path.join(BUILD, "e40c1-cost-results.json")
AUX_JSON = os.path.join(BUILD, "e40c1-aux-results.json")
R = 600000.0
MAP_W, MAP_H = 512, 256
SITE_LAT, SITE_LON = -0.0972, -74.5577
STACK = ["Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus"]
# scene -> (ut, fallback dump); variant -> (map file suffix of the dump, tuning keys of the job)
SCENES = {"clock": (1686000, False), "fair": (1881000, False), "fairfb": (1881000, True)}
VARIANTS = {"before": ("nocombine", [("cloud.coverMeaning", 0), ("cloud.coverCombine", 0)], None), "now": ("cover", [], None)}
# Probes of the sky now: the same maps with a noise switch off (what each stage of the shape model costs). variant -> (map suffix, tuning keys, job noise)
PROBES = {"nu": ("cover", [], dict(noUntile=1)), "nc": ("cover", [], dict(noCurl=1)), "nf": ("cover", [], dict(noFine=1)), "nd": ("cover", [], dict(disabled=1)),
          "nh": ("cover", [], None)}
# The jobs of these variants have no height variation (T13); every other job has it as the game does (cloudBaseVariation, cloudBasePhysics, cloudTopFromUpdraft, baseRagged are on).
NO_HEIGHT = ("nh",)
ALL = dict(VARIANTS)
ALL.update(PROBES)
# view -> (height above sea level in m, yaw from north in degrees, pitch): the K-T2 views, the ground at KSC's 64 m.
VIEWS = {"ground": (264.0, 90.0, 18.0), "inside": (4614.0, 90.0, 8.0), "above": (8364.0, 90.0, -25.0), "sky3": (264.0, 270.0, 38.0)}
COST_SIZE = (1707, 960)
AUX_SIZE = (569, 320)


def tag(scene, variant, view):
    return "%s_%s_%s" % (scene, variant, view)


def tags(scenes=None, variants=None, views=None):
    return [tag(s, v, w) for s in (scenes or SCENES) for v in (variants or VARIANTS) for w in (views or VIEWS)]


def map_name(scene, suffix, layer, kind):
    return "%s-%s-%s-%s.r8" % (scene, suffix, layer, kind)


def camera(view, size):
    height, yaw, pitch = VIEWS[view]
    la, lo = math.radians(SITE_LAT), math.radians(SITE_LON)
    pos = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)]) * (R + height)
    return pr.Camera(pos, yaw, pitch, size[0], size[1], gn.FOV)


def job_layers(scene, variant):
    specs = {s["name"]: s for s in e40o.kerbin_layers()}
    extras = e40ty.cfg_extras()
    out = []
    for name in STACK:
        lk, tk = extras[name]
        jl = e40o.job_layer(specs[name], map_name(scene, ALL[variant][0], name, "cover"), map_name(scene, ALL[variant][0], name, "type"))
        jl.update(lk)
        for t in jl["types"]:
            t.update(tk.get(t["name"], {}))
        out.append(jl)
    return out


def build_job(scene, variant, view, mode, size, jid, **extra):
    cam = camera(view, size)
    args = gn.prod()
    args.update(extra)
    tuning = ALL[variant][1]
    if tuning:
        args["tuning"] = e40o.tuning(tuning)
    if ALL[variant][2]:
        args["noise"] = dict(ALL[variant][2])
    return lay.job(jid, mode, cam, job_layers(scene, variant), size[0], size[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H, frameNumber=0, soft=0.5,
                   sun=lay.sun_vector(cam.pos, 0.0, gn.SUN_ELEVATION), sunColour=gn.SUN_COLOUR, ambientTop=gn.AMBIENT_TOP, ambientBottom=gn.AMBIENT_BOTTOM, **args)


def cost_jobs():
    return [build_job(s, v, w, "lum", COST_SIZE, "gn-%s-prod-f0" % tag(s, v, w)) for s in SCENES for v in ALL for w in VIEWS]


# The aux jobs: the whole stack and, for the variant now, each layer alone (layerMask bit i = layer i is marched).
def aux_id(scene, variant, view, mask):
    return "c1aux-%s-m%d" % (tag(scene, variant, view), mask)


def aux_jobs():
    """Per scene, variant and view: the aux pass (steps, tau, alpha) of the whole stack and, for the variant now, of each layer alone and of the whole stack without the leap over the
    empty reach (id suffix nr: the result must be the same, with more steps); the same views as lum jobs with the aid picture 4 (light taps): `dense` with a fresh sun march at every
    lit sample (cut-off 0, stride 1: taps = 3 x the lit samples) and `taps` with the product's reuse."""
    jobs = []
    for s in SCENES:
        for v in VARIANTS:
            for w in VIEWS:
                for mask in ([0, 1, 2, 4, 8] if v == "now" else [0]):
                    jobs.append(build_job(s, v, w, "aux", AUX_SIZE, aux_id(s, v, w, mask), layerMask=mask))
                    jobs.append(build_job(s, v, w, "lum", AUX_SIZE, aux_id(s, v, w, mask).replace("c1aux", "c1dense"), layerMask=mask, aid=4, costCut=0.0, lightStride=1))
                jobs.append(build_job(s, v, w, "lum", AUX_SIZE, aux_id(s, v, w, 0).replace("c1aux", "c1taps"), aid=4))
                if v == "now":
                    jobs.append(build_job(s, v, w, "aux", AUX_SIZE, aux_id(s, v, w, 0) + "nr", noReach=1))
    return jobs


def scene_of(jid):
    for s in SCENES:
        if "-%s_" % s in jid or "_%s_" % s in jid:
            return s
    raise ValueError(jid)


def write_jobs(folder, jobs):
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    # The height variation of the game (HeightHook: the module's switches on, the humidity of the time).
    specs = [dict(id=j["id"], variation=1, physics=1, topUpdraft=1, ragged=1, humidityMap="%s-humidity.r8" % scene_of(j["id"]), updraftMap="", seed=664216370 & 0xFFFFFF)
             for j in jobs if not any("_%s_" % v in j["id"] for v in NO_HEIGHT)]
    with open(os.path.join(folder, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)


def prepare(real, fallback):
    for folder in (COST_IN, AUX_IN, SHEET_IN):
        os.makedirs(folder, exist_ok=True)
        for scene, (ut, fb) in SCENES.items():
            source = fallback if fb else real
            if source is None:
                continue
            shutil.copyfile(os.path.join(source, "u%d_humidity.u8" % ut), os.path.join(folder, "%s-humidity.r8" % scene))
            for suffix in sorted({v[0] for v in VARIANTS.values()}):
                for layer in STACK:
                    for kind, file_kind in (("cover", suffix), ("type", "type")):
                        shutil.copyfile(os.path.join(source, "u%d_%s_%s.u8" % (ut, layer, file_kind)), os.path.join(folder, map_name(scene, suffix, layer, kind)))
    write_inputs()


def write_inputs():
    write_jobs(COST_IN, cost_jobs())
    write_jobs(AUX_IN, aux_jobs())


# --------------------------------------------------------------------------------------------------------------------------------
# The cases of the cost series
# --------------------------------------------------------------------------------------------------------------------------------
# Stage A: the product against the sky before and now, and the levers that are exact or nearly (the tile pass, the march target a third of the size).
#   BASE: the game's default: 48 grid points, the light quality High (4 taps, 4 octaves; the scene's own default has 3 taps).
BASE = "seq1+lq2"
#   nr: without the leap over the empty reach (E40 C1; the product leaps); skip: cloudSkipEmpty (the tile pass).
CASES_A = [BASE, BASE + "+nr", BASE + "+r33", BASE + "+r33+nr", BASE + "+skip"]
# Stage B: where the time goes in the sky now: each layer alone (lm), no light (dbg1), no shape noise (dbg2), no light and no noise (dbg3).
CASES_B = [BASE + "+" + m for m in ("lm1", "lm2", "lm4", "lm8", "dbg1", "dbg2", "dbg3")]
# Stage C: the owner's 1.0 / 24 setting.
CASES_C = ["seq1m24+lq2", "seq1m24+lq2+nr", "seq1m24+lq2+r33", "seq1m24+lq2+r25"]
# Stage D (E40 C2, the product's march target r33 as the base): why the game costs more than the player and the candidate fixes.
#   mn<N>  a moon at N / 100 percent of the sun (the game's moon is lit by any body in the sky; the player had none)     mc<N>  light.moonMinShare N / 1000 (a fainter moon is left out)
#   ev     the caches flushed before the pass (no effect: 5.03 -> 4.94 at the clock's ground)                           lp<NN>  the longest leap over empty grid points, NN / 100 noise scales
R33 = BASE + "+r33"
CASES_D = [R33, R33 + "+mn300", R33 + "+mn150+mc20", R33 + "+lp100", R33 + "+lp200", R33 + "+lp400", R33 + "+lp200+sp10", R33 + "+sp10", R33 + "+mn300+lp200"]
# The owner's 1.0 / 24 (pr100) with the same levers.
CASES_E = ["seq1m24+lq2+r33", "seq1m24+lq2+r33+mn300", "seq1m24+lq2+r33+lp100", "seq1m24+lq2+r33+lp200", "seq1m24+lq2+r33+lp400"]


def cost_stage(label, views, medium, cases):
    return Stage(label, ["-mode", "scene", "-scene", "soft-cost", "-in", COST_IN, "-views", ",".join(views), "-media", medium, "-marches", ",".join(cases)],
                 size=(1707, 960), proxy=True, timeout=1700)


COST_STAGES = [
    ("c1-a", tags(), "pr50", CASES_A),
    ("c1-b", tags(["clock", "fairfb"], ["now"]), "pr50", CASES_B),
    ("c1-c", tags(variants=["now"]), "pr100", CASES_C),
    ("c1-e", tags(["clock", "fairfb"], ["now"] + list(PROBES)), "pr50", [BASE]),
]
# The levers on the sky now at the clock and the game-like fair scene (series e40-c1-lev): the march target below a third, the light reuse, the owner's 1.0 / 24 setting.
LEV_JSON = os.path.join(BUILD, "e40c1-lev-results.json")
#   sp<NN>: the widest grid spacing of a span in the layer's noise scales NN / 10, spm<N> the fewest points such a span keeps (E40 C1, layers.spanSpacing)
CASES_LEV = [BASE + "+sp10", BASE + "+sp10+spm12", BASE + "+sp10+spm30", BASE + "+sp20", BASE + "+r33", BASE + "+r33+sp10", BASE + "+r25", BASE + "+r25+sp10",
             BASE + "+r33+cg", BASE + "+r33+tl", "seq1m24+lq2+r25+sp10"]
LEVER_STAGES = [("c1-lev", tags(["clock", "fairfb"], ["now"]), "pr50", CASES_LEV)]
if CASES_D:
    COST_STAGES.append(("c1-d", tags(["clock", "fairfb"], ["now"]), "pr50", CASES_D))
    COST_STAGES.append(("c1-e2", tags(["clock", "fairfb"], ["now"]), "pr100", CASES_E))


def collect_cost(ctx, stages=None, json_path=None):
    metrics = gc.collect_cost(ctx, [(s[0], "", "", []) for s in stages or COST_STAGES], json_path or COST_JSON)
    rows = {}
    for label, _, _, _ in stages or COST_STAGES:
        try:
            values = ctx.player_metrics(label)
        except KeyError:
            continue
        for k, v in values.items():
            if k.startswith("softcost.tiles"):
                rows[k] = v[0]
    if rows:
        with open((json_path or COST_JSON).replace(".json", "-tiles.json"), "w", encoding="utf-8") as f:
            json.dump(rows, f, indent=1)
        print("\n".join("   %-80s %.3f" % (k, v) for k, v in sorted(rows.items())))
    return metrics


# --------------------------------------------------------------------------------------------------------------------------------
# The ray statistics
# --------------------------------------------------------------------------------------------------------------------------------
EARLY = 0.999        # alpha at or above: the ray ended on the early-out (the march sets 1.0 there)
CLEAR = 0.02         # alpha below: no cloud on the ray


def ray_stats(folder, jid):
    steps = np.load(os.path.join(folder, jid + "-steps.npy")).astype(np.float64)
    alpha = np.load(os.path.join(folder, jid + "-alpha.npy")).astype(np.float64)
    tau = np.load(os.path.join(folder, jid + "-tau.npy")).astype(np.float64)
    marched = steps > 0
    clear = alpha < CLEAR
    early = alpha >= EARLY
    part = ~clear & ~early
    n = float(steps.size)

    def mean(mask):
        return float(steps[mask].mean()) if mask.any() else 0.0

    return dict(steps=float(steps.mean()), marched=float(marched.mean()), steps_marched=mean(marched), clear=float(clear.mean()), early=float(early.mean()), partial=float(part.mean()),
                steps_clear=mean(clear), steps_early=mean(early), steps_partial=mean(part), share_clear=float(steps[clear].sum() / max(steps.sum(), 1.0)),
                share_early=float(steps[early].sum() / max(steps.sum(), 1.0)), share_partial=float(steps[part].sum() / max(steps.sum(), 1.0)), tau=float(tau.mean()), n=n)


def collect_aux(ctx):
    folder = ctx.folder("c1-aux")
    results, metrics, lines = {}, [], ["E40-C1 ray statistics (aux pass, %dx%d, product march; steps = density reads per ray)" % AUX_SIZE]
    lines.append("   %-34s %7s %6s %6s %6s | %7s %7s %7s | share of steps clear/partial/early" % ("scene_variant_view", "steps", "clear", "part", "early", "s.clear", "s.part", "s.early"))
    for s in SCENES:
        for v in VARIANTS:
            for w in VIEWS:
                path = os.path.join(folder, aux_id(s, v, w, 0) + "-steps.npy")
                if not os.path.isfile(path):
                    continue
                st = ray_stats(folder, aux_id(s, v, w, 0))
                per = {}
                if v == "now":
                    for li, name in enumerate(STACK):
                        if os.path.isfile(os.path.join(folder, aux_id(s, v, w, 1 << li) + "-steps.npy")):
                            per[name] = ray_stats(folder, aux_id(s, v, w, 1 << li))["steps"]
                st["per_layer"] = per
                # the lit samples (a fresh sun march at each: 3 taps), per ray, of the stack and of each layer; the taps of the product's reuse
                dense = {}
                for mask in ([0, 1, 2, 4, 8] if v == "now" else [0]):
                    p = os.path.join(folder, aux_id(s, v, w, mask).replace("c1aux", "c1dense") + "-g.npy")
                    if os.path.isfile(p):
                        dense[mask] = float(np.load(p).astype(np.float64).mean() / 3.0)
                p = os.path.join(folder, aux_id(s, v, w, 0).replace("c1aux", "c1taps") + "-g.npy")
                st["dense"] = dense
                st["taps"] = float(np.load(p).astype(np.float64).mean()) if os.path.isfile(p) else float("nan")
                if v == "now" and os.path.isfile(os.path.join(folder, aux_id(s, v, w, 0) + "nr-steps.npy")):
                    nr = ray_stats(folder, aux_id(s, v, w, 0) + "nr")
                    st["steps_noreach"] = nr["steps"]
                    for plane in ("alpha", "tau"):
                        a = np.load(os.path.join(folder, aux_id(s, v, w, 0) + "-%s.npy" % plane)).astype(np.float64)
                        b = np.load(os.path.join(folder, aux_id(s, v, w, 0) + "nr-%s.npy" % plane)).astype(np.float64)
                        st["maxdiff_" + plane] = float(np.abs(a - b).max())
                results[tag(s, v, w)] = st
                lines.append("   %-34s %7.1f %6.3f %6.3f %6.3f | %7.1f %7.1f %7.1f | %.2f / %.2f / %.2f%s" % (
                    tag(s, v, w), st["steps"], st["clear"], st["partial"], st["early"], st["steps_clear"], st["steps_partial"], st["steps_early"],
                    st["share_clear"], st["share_partial"], st["share_early"], ("   per layer " + " ".join("%.1f" % per[n] for n in STACK if n in per)) if per else ""))
                metrics.append(Metric.info("e40c1.aux.%s.steps" % tag(s, v, w), st["steps"]))
                if 0 in dense:
                    lines.append("      lit samples per ray %.1f (per layer %s), sun-march taps with the reuse %.1f%s" % (
                        dense[0], " ".join("%.1f" % dense.get(1 << li, float("nan")) for li in range(len(STACK))), st["taps"],
                        ("; without the reach leap %.1f steps, largest difference alpha %.2g tau %.2g" % (st["steps_noreach"], st["maxdiff_alpha"], st["maxdiff_tau"])) if "steps_noreach" in st else ""))
    with open(AUX_JSON, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    return metrics


# --------------------------------------------------------------------------------------------------------------------------------
# The report
# --------------------------------------------------------------------------------------------------------------------------------
TVC = {"ground": 2.59, "inside": 4.77, "above": 5.30, "sky3": 2.58}


def report():
    cost = {}
    for path in (COST_JSON, LEV_JSON):
        if os.path.isfile(path):
            with open(path, encoding="utf-8") as f:
                cost.update(json.load(f)["rows"])
    aux = {}
    if os.path.isfile(AUX_JSON):
        with open(AUX_JSON, encoding="utf-8") as f:
            aux = json.load(f)
    cases = sorted({k.split("|", 2)[2] for k in cost})
    print("total GPU ms of the pass (march + resolve + composite), 1707 x 960, TVC busy-delta ground 2.59 inside 4.77 above 5.30 sky3 2.58")
    for medium in ("pr50", "pr100"):
        used = [c for c in cases if any(k.endswith("|" + c) and ("|%s|" % medium) in k for k in cost)]
        if not used:
            continue
        print("\n%s" % medium)
        print("   %-30s" % "scene_variant_view" + "".join("%18s" % c for c in used) + "   steps/ray")
        for s in SCENES:
            for v in ALL:
                for w in VIEWS:
                    key = tag(s, v, w)
                    cells = []
                    for c in used:
                        row = cost.get("%s|%s|%s" % (key, medium, c))
                        cells.append("%18s" % ("%.2f" % row["total"] if row and "total" in row else "-"))
                    if all(c.strip() == "-" for c in cells):
                        continue
                    print("   %-30s" % key + "".join(cells) + "   %s" % ("%.1f" % aux[key]["steps"] if key in aux else "-"))


def selftest():
    jobs = cost_jobs()
    assert len(jobs) == len(SCENES) * len(ALL) * len(VIEWS)
    ids = [j["id"] for j in jobs]
    assert len(set(ids)) == len(ids) and all(i.startswith("gn-") and i.endswith("-prod-f0") for i in ids)
    assert all(len(j["layers"]) == len(STACK) and j["mapWidth"] == MAP_W for j in jobs)
    before = [j for j in jobs if "_before_" in j["id"]][0]
    assert before["tuning"] and not [j for j in jobs if "_now_" in j["id"]][0].get("tuning")
    aux = aux_jobs()
    assert len({j["id"] for j in aux}) == len(aux)
    cam = camera("ground", COST_SIZE)
    assert abs(math.sqrt(sum(x * x for x in jobs[0]["position"])) - (R + 264.0)) < 1.0 and abs(cam.pitch - 18.0) < 1e-9
    for label, views, medium, cases in COST_STAGES:
        assert all(("gn-%s-prod-f0" % v) in ids for v in views)
    print("selftest passed (%d cost jobs, %d aux jobs)" % (len(jobs), len(aux)))


# --------------------------------------------------------------------------------------------------------------------------------
# The pictures: the pass as the game runs it (history, a still camera, frames 36 to 39), the sky now against the fixes and against the converged picture
# --------------------------------------------------------------------------------------------------------------------------------
SHEET_CASES = [R33, R33 + "+lp100", R33 + "+lp200", R33 + "+lp400", R33 + "+lp200+sp10", R33 + "+mn300", R33 + "+sp10"]
SHEET_SCENES = ("clock", "fairfb")
SHEET_FULL = (1708, 960)
OUT_PICTURES = os.path.join("E40")


def sheet_stage():
    views = tags(list(SHEET_SCENES), ["now"])
    return Stage("c1-sheet", ["-mode", "scene", "-scene", "soft-temporal", "-in", COST_IN, "-views", ",".join(views), "-media", "pr50", "-marches", ",".join(SHEET_CASES)],
                 size=(1280, 720), proxy=False, timeout=3400)


def collect_sheet(ctx):
    folder = ctx.folder("c1-sheet")
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    tint = e40ty.white_tint(3000.0)
    metrics, lines, results = [], ["E40-C1 the pass as the game runs it (static camera, frames 36 to 39; error and temporal noise over the cloud pixels of the converged picture, luma of the display)"], {}
    tile = (570, 320)
    for scene in SHEET_SCENES:
        rows = []
        for view in VIEWS:
            key = tag(scene, "now", view)
            ref_path = os.path.join(folder, "gs-%s-pr50-static-ref-f36.npy" % key)
            if not os.path.isfile(ref_path):
                continue
            cam = camera(view, SHEET_FULL)
            ref = e40ty.load_lum(folder, "gs-%s-pr50-static-ref-f36" % key)
            mask = np.clip(ref[..., 3], 0.0, 1.0) > 0.03
            ref_img = e40ty.picture(ref, cam, tint)
            ref_l = gn.luma(ref_img)
            cells = []
            for case in SHEET_CASES:
                frames = [e40ty.load_lum(folder, "gs-%s-pr50-static-%s-acc20-f%d" % (key, case, i)) for i in (36, 37, 38, 39)]
                imgs = [e40ty.picture(m, cam, tint) for m in frames]
                ls = np.stack([gn.luma(i) for i in imgs])
                err = float(np.sqrt(((ls[-1] - ref_l)[mask] ** 2).mean())) if mask.any() else float("nan")
                tstd = float(ls.std(axis=0)[mask].mean()) if mask.any() else float("nan")
                results["%s.%s" % (key, case)] = dict(err=err, tstd=tstd, cloud=float(mask.mean()))
                lines.append("   %-26s %-26s err %.4f  tstd %.4f  cloud share %.2f" % (key, case, err, tstd, mask.mean()))
                metrics.append(Metric.info("e40c1.sheet.%s.%s.err" % (key, case), err))
                cells.append(e40ty.label(e40ty.box(imgs[-1], tile), "%s %s %s: err %.4f tstd %.4f" % (scene, view, case.replace(R33, "r33"), err, tstd)))
                gn.save_png(os.path.join(folder, "%s-%s.png" % (key, case.replace("+", "_"))), imgs[-1])
            cells.append(e40ty.label(e40ty.box(ref_img, tile), "%s %s converged" % (scene, view)))
            rows.append(np.concatenate(cells, axis=1))
        if rows:
            path = os.path.join(out_dir, "e40-c1-%s-sheet.png" % scene)
            gn.save_png(path, np.concatenate(rows, axis=0))
            lines.append("   wrote " + path)
    with open(os.path.join(BUILD, "e40c1-sheet-results.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    return metrics


def stages():
    if os.path.isdir(COST_IN) and os.path.isfile(os.path.join(COST_IN, "jobs.json")):
        write_inputs()
    return [cost_stage(*s) for s in COST_STAGES]


def aux_stages():
    return [Stage("c1-aux", ["-mode", "scene", "-scene", "layers", "-in", AUX_IN], size=(1280, 720), proxy=False, timeout=3000)]


register_series("e40-c1-cost", stages(), collect_cost, exclusive=True, who="e40-c1")
register_series("e40-c1-lev", [cost_stage(*s) for s in LEVER_STAGES], lambda ctx: collect_cost(ctx, LEVER_STAGES, LEV_JSON), exclusive=True, who="e40-c1")
register_series("e40-c1-aux", aux_stages(), collect_aux, exclusive=False, who="e40-c1")
register_series("e40-c1-sheet", [sheet_stage()], collect_sheet, exclusive=False, who="e40-c1")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--report", action="store_true")
    parser.add_argument("--prepare", nargs="+", metavar="MAPS")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.report:
        report()
    elif a.prepare:
        prepare(a.prepare[0], a.prepare[1] if len(a.prepare) > 1 else None)
        print("prepared", COST_IN, AUX_IN)
