"""E40 soft density: the cloud as a water content instead of a hard surface, on the unchanged grid march (player series e40-gs-temporal, e40-gs-cost).

    python tools/build_weather_player.py --series e40-gs-temporal
    python tools/build_weather_player.py --series e40-gs-cost

The scene, the views, the masks and the measures are e40gn.py's and e40gt.py's (the pass as the game runs it: the half-size march laid up to 1708 x 960); what
changes is the MEDIUM (SoftTable.Media of tests/WeatherPlayer/Assets/Runner/SoftDensityScene.cs), and every medium is judged against its OWN converged picture:

  hard      the density as it is (extinction x share of the soft edge 0.15 x profile)
  sig01     a tenth of the extinction
  pr50      the product's own water content with the owner's softness 0.5; pr100 with 1.0
The media of step (a) that built the water content out of the old density (e50p2, e100p3, s30k2 .. s100k2, s50k2f, c50k2 .. c100k2w) set its exponent through
layers.edgePower, which went with E40 step (c); pr50 and pr100 are what they reproduced.

Marches: seq1 (the product's grid march, 48 grid points at most), seq1m32, seq1m24 (32 and 24 grid points; still camera, history only).
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40gt as gt  # noqa: E402

MEDIA = ["hard", "sig01", "pr50", "pr100"]
MARCHES = ["seq1", "seq1m32", "seq1m24", "seq1m16", "tseq1m48", "tseq1m24", "tseq1m16"]
CASES = [("seq1", "plain")] + [(m, "acc20") for m in MARCHES]
# The second round (e40-gs2-*): the product's water content with the widths 0.5 and 1, fewer grid points, the span cut to the types present. (Its media s100k2,
# c50k2, c100k2 and c100k2w, the outline of the cover meaning and the sun colour at the cloud, went with layers.edgePower, E40 step (c).)
MEDIA2 = ["hard", "pr50", "pr100"]
SCENARIOS = {"static": [36, 37, 38, 39], "fly": [37, 38], "turnfast": [37, 38], "flyfast": [37, 38], "cut": [0, 2, 5, 10]}
MOTION = ("fly", "turnfast", "flyfast")
PREFIX = "e40-gs"


def aid(m):
    """The aid picture of a converged march: the ray's optical depth (from the alpha) as a heat map."""
    a = np.clip(m[..., 3], 0.0, 0.9999)
    return gn.heat(-np.log(1.0 - a))


def collect_temporal(ctx):
    folder = ctx.folder("temporal")
    out_dir = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines, results = [], [], {}
    lines.append("E40-GS: the pass as the game runs it (full target 1708 x 960; display values; every medium against its own converged picture)")
    lines.append("   static: tstd (4 consecutive frames) closed | rim ; hfx rim ; blob rim ; err = rms of one frame against the converged picture ; share of the picture"
                 " closed | rim | cloud ; motion err fly 300 m/s, turn 180 deg/s, fly 2400 m/s ; cut err at frames 0 2 5 10")
    crops = {}
    for view in gn.VIEWS:
        for medium in MEDIA:
            name0 = "gs-%s-%s-static-ref-f%d" % (view, medium, SCENARIOS["static"][0])
            if not os.path.isfile(os.path.join(folder, name0 + ".npy")):
                continue
            ref0 = gn.full_frame(folder, name0)
            alpha = np.clip(ref0[..., 3], 0.0, 1.0)
            if view not in crops:
                crops[view] = gn.pick_crop(alpha, 360, 200)
            x, y, w, h = crops[view]
            mask_set = {k: gt.inner(v) for k, v in gn.masks(alpha).items()}
            shares = {k: float(v.mean()) for k, v in mask_set.items()}
            ref_rgb = gn.full_display(ref0, view)
            ref_l = gn.luma(ref_rgb)
            ref_lap = gn.lap(ref_l)
            gn.save_png(os.path.join(out_dir, "%s-t-%s-%s-converged.png" % (PREFIX, view, medium)), ref_rgb)
            gn.save_png(os.path.join(out_dir, "%s-t-%s-%s-converged-aid.png" % (PREFIX, view, medium)), aid(ref0))
            for march, mode in CASES:
                name = "%s-%s" % (march, mode)
                first = "gs-%s-%s-static-%s-f%d" % (view, medium, name, SCENARIOS["static"][0])
                if not os.path.isfile(os.path.join(folder, first + ".npy")):
                    continue
                fr = [gn.full_frame(folder, "gs-%s-%s-static-%s-f%d" % (view, medium, name, i)) for i in SCENARIOS["static"]]
                ls = np.stack([gn.luma(gn.full_display(m, view)) for m in fr])
                tstd = ls.std(axis=0)
                row = {}
                for mk in ("closed", "rim", "cloud"):
                    m = mask_set[mk]
                    if m.sum() < 50:
                        row[mk] = dict(tstd=float("nan"), hfx=float("nan"), blob=float("nan"))
                        continue
                    row[mk] = dict(tstd=float(tstd[m].mean()), hfx=float(gn.lap(ls[0])[m].mean() - ref_lap[m].mean()), blob=gt.blob(ls[0], m) - gt.blob(ref_l, m))
                    for key, val in row[mk].items():
                        metrics.append(Metric.info("e40gs.t.%s.%s.static.%s.%s.%s" % (view, medium, name, mk, key), val))
                d0 = (ls[0] - ref_l)[mask_set["cloud"]]
                err0 = float(np.sqrt((d0 * d0).mean()))
                metrics.append(Metric.info("e40gs.t.%s.%s.static.%s.err" % (view, medium, name), err0))
                motion, cut = {}, []
                if march == "seq1":
                    for sc in MOTION:
                        errs = []
                        for i in SCENARIOS[sc]:
                            ref = gn.full_frame(folder, "gs-%s-%s-%s-ref-f%d" % (view, medium, sc, i))
                            ms = gn.masks(np.clip(ref[..., 3], 0.0, 1.0))
                            d = (gn.luma(gn.full_display(gn.full_frame(folder, "gs-%s-%s-%s-%s-f%d" % (view, medium, sc, name, i)), view))
                                 - gn.luma(gn.full_display(ref, view)))[ms["cloud"]]
                            errs.append(float(np.sqrt((d * d).mean())))
                        motion[sc] = float(np.mean(errs))
                        metrics.append(Metric.info("e40gs.t.%s.%s.%s.%s.err" % (view, medium, sc, name), motion[sc]))
                    for i in SCENARIOS["cut"]:
                        dc = (gn.luma(gn.full_display(gn.full_frame(folder, "gs-%s-%s-cut-%s-f%d" % (view, medium, name, i)), view)) - ref_l)[mask_set["cloud"]]
                        cut.append(float(np.sqrt((dc * dc).mean())))
                results["%s-%s-%s" % (view, medium, name)] = dict(static=row, err_static=err0, motion=motion, cut=cut, shares=shares)
                lines.append("   %-6s %-7s %-14s tstd %.4f | %.4f ; hfx %+.4f ; blob %+.4f ; err %.4f ; share %.3f | %.3f | %.3f ; motion %s ; cut %s"
                             % (view, medium, name, row["closed"]["tstd"], row["rim"]["tstd"], row["rim"]["hfx"], row["rim"]["blob"], err0, shares["closed"],
                                shares["rim"], shares["cloud"], " ".join("%.4f" % motion[sc] for sc in motion), " ".join("%.4f" % c for c in cut)))
                # Three consecutive frames of the crop and the temporal noise x 8.
                strip = [gn.enlarge(gn.full_display(fr[k], view)[y:y + h, x:x + w], 2) for k in range(3)]
                noise = np.clip(tstd[y:y + h, x:x + w] * 8.0, 0.0, 1.0)
                strip.append(gn.enlarge(np.repeat(noise[..., None], 3, axis=-1), 2))
                gn.save_png(os.path.join(out_dir, "%s-t-%s-%s-%s-seq.png" % (PREFIX, view, medium, name)), np.concatenate(strip, axis=1))
                gn.save_png(os.path.join(out_dir, "%s-t-%s-%s-%s.png" % (PREFIX, view, medium, name)), gn.full_display(fr[0], view))
    with open(os.path.join(gn.BUILD, "e40gs-temporal-results.json"), "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


def collect_cost(ctx):
    values = ctx.player_metrics("cost")
    metrics, lines = [], ["E40-GS GPU ms at 1707 x 960, one layer, history 0.2 (march / total), median of 80 frames in two rounds of opposite order"]
    names = sorted(set(k.split(".", 2)[2] for k in values if k.startswith("softcost.total.")))
    for n in names:
        row = [values.get("softcost.%s.%s" % (part, n), (float("nan"),))[0] for part in ("march", "total")]
        lines.append("   %-34s %.3f / %.3f" % ((n,) + tuple(row)))
        metrics.append(Metric.info("e40gs.cost." + n, row[1]))
    print("\n".join(lines))
    with open(os.path.join(gn.BUILD, "e40gs-cost-results.json"), "w", encoding="utf-8") as f:
        json.dump(dict(lines=lines), f, indent=1)
    return metrics


# The third round (e40-gs3-*) measured the sampling keys on the hard and on the soft medium (E40-soft-density-result.md section 4), for the decision which of them a
# soft medium still needs: prod (a new white offset per frame, layers.ditherSequence 0), seq2 (the sequence over the gradient noise), ign (cloudHashDither off), refine
# (cloudEntryRefinement), ramp3 (3 bisections and the entry ramp: cloudRefineBisect, layers.entryRamp), leap4xc (layers.cloudStep) and surf3 (the surface march) went
# with their keys (E40 step (c)); what is left of the round is the finer grid with the leap (layers.gridFine, layers.emptyLeap): leap4x.
MARCHES3 = ["seq1", "leap4x"]
ROUND3 = ["-media", "hard,pr50", "-marches", ",".join(MARCHES3)]
CASES += [(m, "acc20") for m in MARCHES3 if m != "seq1"]
register_series("e40-gs3-temporal", [Stage("temporal", ["-mode", "scene", "-scene", "soft-temporal", "-in", gn.INPUT] + ROUND3, size=(1280, 720), proxy=False,
                                           timeout=3000)], lambda ctx: collect_temporal(ctx), exclusive=False, who="e40-gs")
register_series("e40-gs3-cost", [Stage("cost", ["-mode", "scene", "-scene", "soft-cost", "-in", gn.INPUT] + ROUND3, size=(1707, 960), proxy=True, timeout=1500)],
                lambda ctx: collect_cost(ctx), exclusive=True, who="e40-gs")
ROUND2 = ["-media", ",".join(MEDIA2), "-marches", ",".join(MARCHES)]
register_series("e40-gs2-temporal", [Stage("temporal", ["-mode", "scene", "-scene", "soft-temporal", "-in", gn.INPUT] + ROUND2, size=(1280, 720), proxy=False,
                                           timeout=3000)], collect_temporal, exclusive=False, who="e40-gs")
register_series("e40-gs2-cost", [Stage("cost", ["-mode", "scene", "-scene", "soft-cost", "-in", gn.INPUT] + ROUND2, size=(1707, 960), proxy=True, timeout=1500)],
                collect_cost, exclusive=True, who="e40-gs")
# The product path (E40 soft density, package B1; series e40-gb-*, pictures e40-gb-*): pr50 and pr100 are the product's own density (CloudLayerDef's effective numbers with the
# owner's softness 0.5 and 1.0, no rewrite of the job's layer numbers), hard is today (layers.softDensity 0). The owner's favourites: pr50 on the grid march seq1 (48 points), pr100 on
# seq1m24 (24 points). (The rows s50k2 and s100k2 of step (a) that the product path reproduced went with layers.edgePower, E40 step (c).)
ROUND_GB = ["-media", "hard,pr50,pr100", "-marches", "seq1,seq1m24"]


def collect_gb(ctx):
    """collect_temporal with the pictures named e40-gb-*."""
    global PREFIX
    old, PREFIX = PREFIX, "e40-gb"
    try:
        return collect_temporal(ctx)
    finally:
        PREFIX = old


register_series("e40-gb-temporal", [Stage("temporal", ["-mode", "scene", "-scene", "soft-temporal", "-in", gn.INPUT] + ROUND_GB, size=(1280, 720), proxy=False,
                                          timeout=3000)], collect_gb, exclusive=False, who="e40-gb")
register_series("e40-gb-cost", [Stage("cost", ["-mode", "scene", "-scene", "soft-cost", "-in", gn.INPUT] + ROUND_GB, size=(1707, 960), proxy=True, timeout=1500)],
                collect_cost, exclusive=True, who="e40-gb")
register_series("e40-gs-temporal", [Stage("temporal", ["-mode", "scene", "-scene", "soft-temporal", "-in", gn.INPUT], size=(1280, 720), proxy=False, timeout=3000)],
                collect_temporal, exclusive=False, who="e40-gs")
register_series("e40-gs-cost", [Stage("cost", ["-mode", "scene", "-scene", "soft-cost", "-in", gn.INPUT], size=(1707, 960), proxy=True, timeout=1500)],
                collect_cost, exclusive=True, who="e40-gs")


if __name__ == "__main__":
    a = np.zeros((20, 30, 4))
    a[5:15, 10:20, 3] = 0.9
    h = aid(a)
    assert h.shape == (20, 30, 3) and h[0, 0].max() == 0.0 and h[10, 15].max() > 0.0
    print("selftest passed")
