"""E40 C4: the untiling warp as a table read (series e40-c4-noise).

    python tools/weather_metrics/e40c2.py --prepare-all <series-pictures/K-T2/maps>       the game's dumped maps as scenes (C2)
    python tools/build_weather_player.py --series e40-c4-noise [--no-build]               cost of the product pass on those scenes, error and temporal noise of its picture
    python tools/weather_metrics/e40c4.py --save-before                                    keeps this run's pictures as "before" (run it with the player of the commit before the change)
    python tools/weather_metrics/e40c4.py --report                                         the cost table of the last run
    python tools/weather_metrics/e40c4.py --selftest

The cases are the product's pass (layers.warpTable 1: the warp's lattice values from a table of half floats around the camera), the same with the values hashed at every sample (wt0, the pass
as it was) and with a table of bytes (wt2). The cost stages draw the game's maps of the four clock views and the two fair-weather views in each case and, for the clock's views, the pass
with the warp off (the floor). The sheet stage draws the four clock views as a still camera with the history (frames 36 to 39) and as the converged picture. The collector writes, per
view and case: the error of the last frame against the converged picture and the temporal noise (the run-to-run noise of the pass), and the difference of the table's last frame from the
hashed one's (same camera, same history, same jitter), over the cloud pixels of the converged picture (luma of the display). With a run of the commit before the change kept
(--save-before) it also writes the difference from that run's pictures.
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
from weather_metrics import e40c3 as c3  # noqa: E402
from weather_metrics import e40gc as gc  # noqa: E402

BUILD = c1.BUILD
# The inputs of the stages: a copy of the C2 / C3 folder (the game's maps, the jobs of the views and of the probe variants) of its own, because other series (e40v1) rewrite the shared jobs.json at import.
IN = os.path.join(BUILD, "e40c4-in")
BEFORE = os.path.join(BUILD, "c4-before")
COST_JSON = os.path.join(BUILD, "e40c4-cost-results.json")
SHEET_JSON = os.path.join(BUILD, "e40c4-sheet-results.json")
PROD = c3.PROD
HASH = PROD + "+wt0"
BYTES = PROD + "+wt2"
CASES = [PROD, HASH, BYTES]
NAMES = {PROD: "table", HASH: "hash", BYTES: "bytes"}
FRAMES = (36, 37, 38, 39)
LABEL = "e40-c4-noise"


def prepare_inputs():
    """Writes the jobs of the views and the probe variants (C3) and copies the folder as this series' own."""
    c3.write_cost_inputs()
    shutil.rmtree(IN, ignore_errors=True)
    shutil.copytree(c2.IN, IN)


def sheet_tags():
    return [c1.tag(s, "now", view) for s in c3.sheet_scenes() for view in c2.scene_views(s)]


def cost_stage():
    views = [c1.tag(s, "now", v) for s in c3.scene_names() for v in c2.scene_views(s)]
    return Stage("c4-cost", ["-mode", "scene", "-scene", "soft-cost", "-in", IN, "-views", ",".join(views), "-media", "pr50", "-marches", ",".join(CASES)], size=(1707, 960), proxy=True, timeout=3400)


def probe_stage():
    views = [c1.tag(s, "nu", view) for s in c3.sheet_scenes() for view in c2.scene_views(s)]
    return Stage("c4-probe", ["-mode", "scene", "-scene", "soft-cost", "-in", IN, "-views", ",".join(views), "-media", "pr50", "-marches", PROD], size=(1707, 960), proxy=True, timeout=3400)


def sheet_stage():
    return Stage("c4-sheet", ["-mode", "scene", "-scene", "soft-temporal", "-in", IN, "-views", ",".join(sheet_tags()), "-media", "pr50", "-marches", ",".join(CASES)], size=(1280, 720), proxy=False, timeout=3400)


def stages():
    if not c3.scene_names():
        return []
    prepare_inputs()
    return [cost_stage(), probe_stage(), sheet_stage()]


def ref_name(key):
    return "gs-%s-pr50-static-ref-f36" % key


def frame_names(key, case):
    return ["gs-%s-pr50-static-%s-acc20-f%d" % (key, case, i) for i in FRAMES]


def display(folder, name, cam, tint):
    from weather_metrics import e40ty
    return e40ty.picture(e40ty.load_lum(folder, name), cam, tint)


def save_before(folder):
    """Keeps the sheet stage's arrays of the player's folder (the product case) as the pictures of the commit before the change."""
    os.makedirs(BEFORE, exist_ok=True)
    n = 0
    for key in sheet_tags():
        for name in [ref_name(key)] + frame_names(key, PROD):
            src = os.path.join(folder, name + ".npy")
            if os.path.isfile(src):
                shutil.copyfile(src, os.path.join(BEFORE, name + ".npy"))
                n += 1
    print("kept %d arrays in %s" % (n, BEFORE))


def rms(a, b, mask):
    return float(np.sqrt(((a - b)[mask] ** 2).mean()))


def collect_sheet(ctx):
    from weather_metrics import e40gn as gn
    from weather_metrics import e40o, e40ty
    folder = ctx.folder("c4-sheet")
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    tint = e40ty.white_tint(3000.0)
    results, lines = {}, ["E40-C4 the product pass as the game runs it (still camera, frames 36 to 39; luma of the display over the cloud pixels of the converged picture)",
                          "   err: rms of the last frame against the converged picture (drawn with the table); tstd: temporal noise over the 4 frames; fdiff: rms of the last two frames;",
                          "   xhash: rms of the last frame against the hashed case's last frame; xbefore: against the last frame of the commit before (--save-before); xref: the converged picture against that run's"]
    for scene in c3.sheet_scenes():
        for view in c2.scene_views(scene):
            key = c1.tag(scene, "now", view)
            if not os.path.isfile(os.path.join(folder, ref_name(key) + ".npy")):
                continue
            cam = c1.camera(view, c3.SHEET_FULL)
            ref = e40ty.load_lum(folder, ref_name(key))
            ref_l = gn.luma(e40ty.picture(ref, cam, tint))
            mask = np.clip(ref[..., 3], 0.0, 1.0) > 0.03
            last = {}
            for case in [HASH, PROD, BYTES]:
                names = frame_names(key, case)
                if not os.path.isfile(os.path.join(folder, names[-1] + ".npy")):
                    continue
                ls = np.stack([gn.luma(display(folder, n, cam, tint)) for n in names])
                last[case] = ls[-1]
                row = dict(err=rms(ls[-1], ref_l, mask), tstd=float(ls.std(axis=0)[mask].mean()), fdiff=rms(ls[-1], ls[-2], mask), cloud=float(mask.mean()))
                if HASH in last and case != HASH:
                    row["xhash"] = rms(ls[-1], last[HASH], mask)
                    row["xhash_max"] = float(np.abs(ls[-1] - last[HASH])[mask].max())
                if case in (PROD, HASH) and os.path.isfile(os.path.join(BEFORE, frame_names(key, PROD)[-1] + ".npy")):
                    old = gn.luma(display(BEFORE, frame_names(key, PROD)[-1], cam, tint))
                    row["xbefore"] = rms(ls[-1], old, mask)
                    if case == PROD:
                        old_ref = gn.luma(display(BEFORE, ref_name(key), cam, tint))
                        row["xref"] = rms(ref_l, old_ref, mask)
                        row["xref_max"] = float(np.abs(ref_l - old_ref)[mask].max())
                results["%s|%s" % (view, NAMES[case])] = row
                lines.append("   %-7s %-6s " % (view, NAMES[case]) + " ".join("%s %.4f" % (k, v) for k, v in row.items() if k != "cloud") + "  cloud %.2f" % row["cloud"])
            if PROD in last and HASH in last:
                diff = np.abs(last[PROD] - last[HASH])
                gn.save_png(os.path.join(out_dir, "e40-c4-%s-diff.png" % view), np.repeat(np.clip(diff * 8.0, 0.0, 1.0)[..., None], 3, axis=2))
                gn.save_png(os.path.join(out_dir, "e40-c4-%s-table.png" % view), display(folder, frame_names(key, PROD)[-1], cam, tint))
                gn.save_png(os.path.join(out_dir, "e40-c4-%s-hash.png" % view), display(folder, frame_names(key, HASH)[-1], cam, tint))
    with open(SHEET_JSON, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    return [Metric.info("e40c4.sheet.%s.%s" % (k.replace("|", "."), m), v) for k, r in results.items() for m, v in r.items()]


def collect(ctx):
    metrics = gc.collect_cost(ctx, [("c4-cost", "", "", []), ("c4-probe", "", "", [])], COST_JSON)
    try:
        ctx.folder("c4-sheet")
    except KeyError:
        return metrics
    return metrics + collect_sheet(ctx)


def report():
    with open(COST_JSON, encoding="utf-8") as f:
        rows = json.load(f)["rows"]
    print("player ms (march + resolve + composite) at 1707 x 960 on the game's maps; TVC %s; C3 product 4.96 / 6.68 / 6.26 / 5.01" % c1.TVC)
    for scene in c3.scene_names():
        for view in c2.scene_views(scene):
            cells = []
            for variant, case in (("now", HASH), ("now", PROD), ("now", BYTES), ("nu", PROD)):
                row = rows.get("%s|pr50|%s" % (c1.tag(scene, variant, view), case))
                cells.append("%s %s" % ({HASH: "hash", PROD: "table", BYTES: "bytes"}[case] if variant == "now" else "no warp", "%.2f" % row["total"] if row else "-"))
            print("   %-26s %s" % (scene, "   ".join(cells)))


def selftest():
    assert len(CASES) == len(set(CASES)) and set(NAMES) == set(CASES)
    print("selftest passed")


register_series("e40-c4-noise", stages(), collect, exclusive=True, who="e40-c4")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--save-before", action="store_true")
    parser.add_argument("--report", action="store_true")
    parser.add_argument("--folder", default=os.path.join(BUILD, "..", "WeatherPlayer", "%s-c4-sheet" % LABEL))
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.save_before:
        save_before(os.path.normpath(a.folder))
    elif a.report:
        report()
