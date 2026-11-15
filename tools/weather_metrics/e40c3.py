"""E40 C3: what the clock views cost on the maps the game marched, and where the milliseconds go (series e40-c3-cost).

    python tools/weather_metrics/e40c2.py --prepare-all <series-pictures/K-T2/maps>      the game's dumps as scenes (C2)
    python tools/weather_metrics/e40c3.py --prepare                                      the aux inputs from the same maps
    python tools/build_weather_player.py --series e40-c3-cost [--no-build]
    python tools/weather_metrics/e40c3.py --report                                       the table: player ms against the game's, the rays per view
    python tools/weather_metrics/e40c3.py --selftest

The cost stage draws every game scene in its own view with the product pass (march target a third) and the cost levers of the cases below; the aux stage draws the ray
statistics (steps, optical depth, alpha per pixel) of the whole stack and of each layer alone, and the lit samples (a fresh sun march at each: three taps).
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
from weather_metrics import e40c1 as c1  # noqa: E402
from weather_metrics import e40c2 as c2  # noqa: E402
from weather_metrics import e40gc as gc  # noqa: E402

BUILD = c1.BUILD
AUX_IN = os.path.join(BUILD, "e40c3-aux-in")
COST_JSON = os.path.join(BUILD, "e40c3-cost-results.json")
AUX_JSON = os.path.join(BUILD, "e40c3-aux-results.json")
# The game's measurements of start 62 (soft60, busy minus the no-layer baseline, ms): the clock views and the fair weather's two views.
GAME = {"koffground": 4.98, "koffinside": 6.62, "koffabove": 7.02, "koffsky3": 4.00, "fwoffground": 0.40, "fwoffabove": 3.55}
TVC = c1.TVC
LEAP = 1.0   # layers.leapScale of the aux jobs (the product's default)

# The product's pass: the march target a third, the leap of layers.leapScale 1.0 (the player's cases name it; a case without lp is the pass before E40 C2).
PROD = c1.R33 + "+lp100"
R33 = PROD
# The attribution: the product, each layer alone (lm; 16: no layer, the floor of the resolve and composite), no light (dbg1), no shape noise (dbg2), neither (dbg3), the target of before C1 (r50).
CASES_ATTR = [PROD, c1.BASE + "+lp100", PROD + "+lm16"] + [PROD + "+lm%d" % m for m in (1, 2, 4, 8)] + [PROD + "+dbg%d" % d for d in (1, 2, 3)]
CASES_ATTR2 = [PROD + "+lm%d+dbg%d" % (m, d) for m in (1, 2, 4, 8) for d in (1, 2, 3)]
# The levers that exist (switches of the product or the player): the spacing of the grid points by the noise scale, the tile pass, the step growth and the tap LOD of T16, the leap, a smaller
# march target, the light's stride and cut-off, the light quality Medium, the pass without the leap over the empty reach.
CASES_LEV = [PROD + "+" + m for m in ("sp10", "sp10+spm12", "sp10+spm6", "skip", "cg", "tl", "cg+tl", "r25", "ls3", "lc100", "lq1", "nr")] + [c1.R33 + "+lp200", c1.R33]
# Run 4: the distance LOD of the step (cloudStepGrowth, cg<N>: the step grows N / 1000000 per m along a span; cg alone 40), the early-out (eo<N>: N / 1000, the product 10), and the switches together.
CASES_CUT = [PROD + "+" + m for m in ("cg5", "cg10", "cg20", "cg", "cg80", "eo30", "eo100", "cg20+r25", "cg20+lq1", "cg20+sp10", "r25+lq1", "cg20+r25+lq1", "cg+r25+lq1")]
# The cases of the run: the product, the single levers and their combinations (the attribution of the earlier runs stays in the results files of those runs).
CASES = [PROD] + [PROD + "+" + m for m in ("r25", "lq1", "sp10", "tl")] + CASES_CUT
# E40C3_PROD=1: the cost stage alone with the product's case (a cfg change measured against the game's maps, E40 CB: the anvils drawn into them by the player's setup).
PROD_ONLY = os.environ.get("E40C3_PROD") == "1"
if PROD_ONLY:
    CASES = [PROD]
# The pictures and the error against the converged picture (series stage c3-sheet): the product and the levers that may stay in the picture's error.
SHEET_CASES = [PROD] + [PROD + "+" + m for m in ("cg10", "cg20", "cg", "eo30", "eo100", "r25", "lq1", "sp10", "cg20+r25", "cg20+r25+lq1", "r25+lq1")]
SHEET_FULL = c1.SHEET_FULL


def scene_names():
    return list(c2.scenes())


def aux_id(scene, view, mask):
    return "c3aux-%s-m%d" % (c1.tag(scene, "now", view), mask)


def aux_jobs():
    jobs = []
    for scene in scene_names():
        for view in c2.scene_views(scene):
            for mask in (0, 1, 2, 4, 8):
                jobs.append(c1.build_job(scene, "now", view, "aux", c1.AUX_SIZE, aux_id(scene, view, mask), layerMask=mask, leapScale=LEAP))
                jobs.append(c1.build_job(scene, "now", view, "lum", c1.AUX_SIZE, aux_id(scene, view, mask).replace("c3aux", "c3dense"), layerMask=mask, aid=4, costCut=0.0, lightStride=1, leapScale=LEAP))
            jobs.append(c1.build_job(scene, "now", view, "lum", c1.AUX_SIZE, aux_id(scene, view, 0).replace("c3aux", "c3taps"), aid=4, leapScale=LEAP))
            jobs.append(c1.build_job(scene, "now", view, "aux", c1.AUX_SIZE, aux_id(scene, view, 0) + "nl", leapScale=0.0))
    return jobs


def prepare():
    """Copies the scenes' maps beside the aux jobs and writes the jobs (the cost stage uses the C2 folder as it is)."""
    os.makedirs(AUX_IN, exist_ok=True)
    for name in os.listdir(c2.IN):
        if name.endswith(".r8"):
            shutil.copyfile(os.path.join(c2.IN, name), os.path.join(AUX_IN, name))
    write_aux()


def write_aux():
    jobs = aux_jobs()
    os.makedirs(AUX_IN, exist_ok=True)
    with open(os.path.join(AUX_IN, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    scene_of = {}
    for scene in scene_names():
        for view in c2.scene_views(scene):
            scene_of[c1.tag(scene, "now", view)] = scene
    specs = []
    for j in jobs:
        key = [k for k in scene_of if "-%s-" % k in j["id"] or j["id"].endswith(k)][0]
        specs.append(dict(id=j["id"], variation=1, physics=1, topUpdraft=1, ragged=1, humidityMap="%s-humidity.r8" % scene_of[key], updraftMap="", seed=664216370 & 0xFFFFFF))
    with open(os.path.join(AUX_IN, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)


# What a feature of the density costs per read (the probe variants of e40c1: untiling warp off, curl off, fine octave off, no height variation at all) and the ragged base alone off.
# A probe variant is the same maps and the same view with one noise switch changed (the picture differs; only its cost is read).
c1.ALL.setdefault("nrg", ("cover", [], None))
PROBE_VARIANTS = ["nu", "nc", "nf", "nh", "nrg"]


def write_cost_inputs():
    """The jobs of the cost stage and the probe stage: every scene in its own view as the product, and the probe variants of the clock's views."""
    listed = [(scene, "now", c1.build_job(scene, "now", view, "lum", c1.COST_SIZE, "gn-%s-prod-f0" % c1.tag(scene, "now", view))) for scene in scene_names() for view in c2.scene_views(scene)]
    listed += [(scene, v, c1.build_job(scene, v, view, "lum", c1.COST_SIZE, "gn-%s-prod-f0" % c1.tag(scene, v, view)))
               for scene in sheet_scenes() for view in c2.scene_views(scene) for v in PROBE_VARIANTS]
    os.makedirs(c2.IN, exist_ok=True)
    with open(os.path.join(c2.IN, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": [j for _, _, j in listed]}, f)
    specs = [dict(id=j["id"], variation=0 if v == "nh" else 1, physics=0 if v == "nh" else 1, topUpdraft=0 if v == "nh" else 1, ragged=0 if v in ("nh", "nrg") else 1,
                  humidityMap="%s-humidity.r8" % scene, updraftMap="", seed=664216370 & 0xFFFFFF) for scene, v, j in listed]
    with open(os.path.join(c2.IN, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)


def cost_stage():
    views = [c1.tag(s, "now", v) for s in scene_names() for v in c2.scene_views(s)]
    write_cost_inputs()
    return Stage("c3-cost", ["-mode", "scene", "-scene", "soft-cost", "-in", c2.IN, "-views", ",".join(views), "-media", "pr50", "-marches", ",".join(CASES)],
                 size=(1707, 960), proxy=True, timeout=3400)


def probe_stage():
    views = [c1.tag(s, v, view) for s in sheet_scenes() for view in c2.scene_views(s) for v in PROBE_VARIANTS]
    return Stage("c3-probe", ["-mode", "scene", "-scene", "soft-cost", "-in", c2.IN, "-views", ",".join(views), "-media", "pr50", "-marches", PROD], size=(1707, 960), proxy=True, timeout=3400)


def aux_stage():
    if scene_names() and os.path.isdir(AUX_IN):
        write_aux()
    return Stage("c3-aux", ["-mode", "scene", "-scene", "layers", "-in", AUX_IN], size=(1280, 720), proxy=False, timeout=3000)


def sheet_scenes():
    return [s for s in scene_names() if s.startswith("koff")]


def sheet_stage():
    views = [c1.tag(s, "now", v) for s in sheet_scenes() for v in c2.scene_views(s)]
    return Stage("c3-sheet", ["-mode", "scene", "-scene", "soft-temporal", "-in", c2.IN, "-views", ",".join(views), "-media", "pr50", "-marches", ",".join(SHEET_CASES)],
                 size=(1280, 720), proxy=False, timeout=3400)


MOTION_CASES = [PROD, PROD + "+r25", PROD + "+cg20+r25", PROD + "+r25+lq1"]
MOTION = {"fly": [37, 38], "turnfast": [37, 38], "flyfast": [37, 38], "cut": [0, 2, 5, 10]}
MOTION_JSON = os.path.join(BUILD, "e40c3-motion-results.json")


def motion_stage():
    views = [c1.tag(s, "now", v) for s in sheet_scenes() for v in c2.scene_views(s)]
    return Stage("c3-motion", ["-mode", "scene", "-scene", "soft-temporal", "-in", c2.IN, "-views", ",".join(views), "-media", "pr50", "-marches", ",".join(MOTION_CASES), "-motion", "all"],
                 size=(1280, 720), proxy=False, timeout=3400)


def collect_motion(ctx):
    """The error against the converged picture of the same pose while the camera moves (fly 300 m/s sideways, turn 180 degrees a second, fly 2400 m/s) and after a cut (the history reset), over the cloud
    pixels of the converged picture."""
    from weather_metrics import e40gn as gn
    from weather_metrics import e40ty
    folder = ctx.folder("c3-motion")
    tint = e40ty.white_tint(3000.0)
    results, lines = {}, ["E40-C3 the pass in motion (error against the converged picture of the pose; mean over the frames of the scenario)"]
    for scene in sheet_scenes():
        for view in c2.scene_views(scene):
            key = c1.tag(scene, "now", view)
            cam = c1.camera(view, SHEET_FULL)
            for sc, frames in MOTION.items():
                refs = {}
                for i in frames:
                    path = "gs-%s-pr50-%s-ref-f%d" % (key, sc, i if sc != "cut" else 0)
                    if not os.path.isfile(os.path.join(folder, path + ".npy")):
                        continue
                    ref = e40ty.load_lum(folder, path)
                    refs[i] = (gn.luma(e40ty.picture(ref, cam, tint)), np.clip(ref[..., 3], 0.0, 1.0) > 0.03)
                for case in MOTION_CASES:
                    errs = []
                    for i, (ref_l, mask) in refs.items():
                        name = "gs-%s-pr50-%s-%s-acc20-f%d" % (key, sc, case, i)
                        if not os.path.isfile(os.path.join(folder, name + ".npy")) or not mask.any():
                            continue
                        l = gn.luma(e40ty.picture(e40ty.load_lum(folder, name), cam, tint))
                        errs.append(float(np.sqrt(((l - ref_l)[mask] ** 2).mean())))
                    if errs:
                        results["%s|%s|%s" % (view, sc, case)] = errs
    for view in c1.VIEWS:
        for case in MOTION_CASES:
            cells = []
            for sc in MOTION:
                e = results.get("%s|%s|%s" % (view, sc, case))
                cells.append("%s %s" % (sc, " ".join("%.4f" % x for x in e) if e else "-"))
            lines.append("   %-7s %-20s %s" % (view, short(case), " | ".join(cells)))
    with open(MOTION_JSON, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    return []


def stages():
    if not scene_names():
        return []
    if PROD_ONLY:
        return [cost_stage()]
    return [cost_stage(), probe_stage(), aux_stage(), sheet_stage(), motion_stage()]


SHEET_JSON = os.path.join(BUILD, "e40c3-sheet-results.json")


def short(case):
    return case.replace(PROD, "prod").replace(c1.BASE, "r50")


def collect_sheet(ctx):
    """The pass as the game runs it (a still camera, frames 36 to 39 with the history): the error of each case against the converged picture and its temporal noise, over the cloud pixels of the
    converged picture (luma of the display), and one sheet per view with the cases next to the converged picture."""
    from weather_metrics import e40gn as gn
    from weather_metrics import e40o, e40ty
    folder = ctx.folder("c3-sheet")
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    tint = e40ty.white_tint(3000.0)
    results, lines = {}, ["E40-C3 the pass as the game runs it (static camera, frames 36 to 39; error and temporal noise over the cloud pixels of the converged picture, luma of the display)"]
    for scene in sheet_scenes():
        for view in c2.scene_views(scene):
            key = c1.tag(scene, "now", view)
            ref_path = os.path.join(folder, "gs-%s-pr50-static-ref-f36.npy" % key)
            if not os.path.isfile(ref_path):
                continue
            cam = c1.camera(view, SHEET_FULL)
            ref = e40ty.load_lum(folder, "gs-%s-pr50-static-ref-f36" % key)
            mask = np.clip(ref[..., 3], 0.0, 1.0) > 0.03
            ref_img = e40ty.picture(ref, cam, tint)
            ref_l = gn.luma(ref_img)
            gn.save_png(os.path.join(folder, "%s-converged.png" % key), ref_img)
            for case in SHEET_CASES:
                frames = [e40ty.load_lum(folder, "gs-%s-pr50-static-%s-acc20-f%d" % (key, case, i)) for i in (36, 37, 38, 39)]
                imgs = [e40ty.picture(m, cam, tint) for m in frames]
                ls = np.stack([gn.luma(i) for i in imgs])
                err = float(np.sqrt(((ls[-1] - ref_l)[mask] ** 2).mean())) if mask.any() else float("nan")
                tstd = float(ls.std(axis=0)[mask].mean()) if mask.any() else float("nan")
                results["%s|%s" % (view, case)] = dict(err=err, tstd=tstd, cloud=float(mask.mean()))
                lines.append("   %-8s %-22s err %.4f  tstd %.4f  cloud share %.2f" % (view, short(case), err, tstd, mask.mean()))
                gn.save_png(os.path.join(folder, "%s-%s.png" % (key, case.replace("+", "_"))), imgs[-1])
    with open(SHEET_JSON, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    return []


def collect(ctx):
    metrics = gc.collect_cost(ctx, [("c3-cost", "", "", []), ("c3-probe", "", "", [])], COST_JSON)
    for label, collector in (("c3-sheet", collect_sheet), ("c3-motion", collect_motion)):
        try:
            ctx.folder(label)
        except KeyError:
            continue
        metrics += collector(ctx)
    if PROD_ONLY:
        return metrics
    results = {}
    folder = ctx.folder("c3-aux")
    lines = ["E40-C3 ray statistics (aux pass, %dx%d, product march; steps = density reads per ray; lit = samples with a fresh sun march, taps = the sun march's reads with the product's reuse)" % c1.AUX_SIZE]
    for scene in scene_names():
        for view in c2.scene_views(scene):
            key = c1.tag(scene, "now", view)
            path = os.path.join(folder, aux_id(scene, view, 0) + "-steps.npy")
            if not os.path.isfile(path):
                continue
            st = c1.ray_stats(folder, aux_id(scene, view, 0))
            per = {}
            for li, name in enumerate(c1.STACK):
                p = os.path.join(folder, aux_id(scene, view, 1 << li) + "-steps.npy")
                if os.path.isfile(p):
                    alone = c1.ray_stats(folder, aux_id(scene, view, 1 << li))
                    per[name] = dict(steps=alone["steps"], clear=alone["clear"], tau=alone["tau"],
                                     lit=float(np.load(os.path.join(folder, aux_id(scene, view, 1 << li).replace("c3aux", "c3dense") + "-g.npy")).astype(np.float64).mean() / 3.0))
            st["per_layer"] = per
            st["lit"] = float(np.load(os.path.join(folder, aux_id(scene, view, 0).replace("c3aux", "c3dense") + "-g.npy")).astype(np.float64).mean() / 3.0)
            st["taps"] = float(np.load(os.path.join(folder, aux_id(scene, view, 0).replace("c3aux", "c3taps") + "-g.npy")).astype(np.float64).mean())
            nl = os.path.join(folder, aux_id(scene, view, 0) + "nl-steps.npy")
            st["steps_noleap"] = float(np.load(nl).astype(np.float64).mean()) if os.path.isfile(nl) else float("nan")
            results[key] = st
            lines.append("   %-26s steps %6.1f (no leap %6.1f; clear %.2f partial %.2f early %.2f) lit %5.1f taps %5.1f | alone steps/lit/clear: %s" % (
                key, st["steps"], st["steps_noleap"], st["clear"], st["partial"], st["early"], st["lit"], st["taps"],
                "  ".join("%s %.1f/%.1f/%.2f" % (n.split("-")[-1][:5], per[n]["steps"], per[n]["lit"], per[n]["clear"]) for n in c1.STACK if n in per)))
            metrics.append(Metric.info("e40c3.aux.%s.steps" % key, st["steps"]))
    with open(AUX_JSON, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    return metrics


def report(only=None):
    rows = {}
    for name in sorted(os.listdir(BUILD)):
        if name.startswith("e40c3-cost-results") and name.endswith(".json"):
            with open(os.path.join(BUILD, name), encoding="utf-8") as f:
                rows.update(json.load(f)["rows"])
    cases = [c for c in sorted({k.split("|", 2)[2] for k in rows}, key=lambda c: (len(c), c)) if not only or only in c]
    print("player ms (march + resolve + composite) at 1707 x 960 on the game's maps; game = start 62 soft (busy minus baseline); TVC %s" % TVC)
    scenes = [s for s in scene_names() if s.startswith("koff")] + [s for s in scene_names() if not s.startswith("koff")]
    print("   %-22s" % "case" + "".join("%11s" % s[:-8].replace("off", "")[:10] for s in scenes))
    for c in cases:
        cells = []
        for scene in scenes:
            key = c1.tag(scene, "now", c2.scene_views(scene)[0])
            row = rows.get("%s|pr50|%s" % (key, c))
            cells.append("%11s" % ("%.2f" % row["total"] if row and "total" in row else "-"))
        print("   %-22s" % c.replace(R33, "r33").replace("seq1+lq2", "r50") + "".join(cells))
    print("   %-22s" % "game (start 62)" + "".join("%11s" % ([v for k, v in GAME.items() if s.startswith(k)] or ["-"])[0] for s in scenes))
    for v in PROBE_VARIANTS:
        cells = []
        for scene in scenes:
            row = rows.get("%s|pr50|%s" % (c1.tag(scene, v, c2.scene_views(scene)[0]), PROD))
            cells.append("%11s" % ("%.2f" % row["total"] if row and "total" in row else "-"))
        print("   %-22s" % ("probe " + v) + "".join(cells))


def selftest():
    assert len(CASES) == len(set(CASES))
    for case in CASES:
        assert all(t for t in case.split("+"))
    print("selftest passed")


register_series("e40-c3-cost", stages(), collect, exclusive=True, who="e40-c3")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--prepare", action="store_true")
    parser.add_argument("--report", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.prepare:
        prepare()
        print("prepared", AUX_IN)
    elif a.report:
        report()
