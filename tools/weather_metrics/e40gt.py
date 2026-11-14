"""E40 grain against TVC: the candidates that remove the grain at no more than the cost of before (player series e40-gt-temporal, e40-gt-cost).

    python tools/build_weather_player.py --series e40-gt-temporal
    python tools/build_weather_player.py --series e40-gt-cost

The scenes, the views, the masks and the measures are e40gn.py's (the pass as the game runs it: the half-size march laid up to 1708 x 960, with a history); the cases are
GrainTable.GtTemporal / GtCost of tests/WeatherPlayer/Assets/Runner/GrainProbeScene.cs (-cases gt):

  seq1          the product's march: the step offset keeps a per-pixel value (the interleaved gradient noise) and advances by the golden ratio per frame (the one
                sequence of the shader since E40 step (c); seq2, tseq2, tramp2 and the whole series e40-s2 of the surface march went with their keys)
  tight         the march searches only the altitudes of the cloud types present around the camera (layers.spanTight)
  t32 / t24     the cut span with 32 / 24 grid points at most
  tleap2        the cut span with twice the grid points and the leap over empty samples

A march over the cut span is judged against the converged march over the cut span (reft): its light taps lie closer than over the whole shell, so the two converged
pictures differ in the light, not in the shape. Additional measures beside e40gn's: blob = rms of (luma - 5 x 5 box mean) of one frame minus the converged picture's over
the rim mask (speckle larger than a pixel, what the eye reads as salt and pepper).
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402

T_CASES = [("prod", "plain"), ("prod", "acc20"), ("seq1", "acc10"), ("tight", "plain"), ("tight", "acc20"), ("tseq1", "acc10"), ("t32seq1", "acc20"),
           ("t24seq1", "acc20"), ("tleap2seq1", "acc20"), ("leap4x", "acc20"), ("leap4xseq1", "acc20"), ("t16seq1", "acc20"), ("t12seq1", "acc20")]


def ref_tag(march):
    return "reft" if march.startswith("t") else "ref"


def box5(l):
    c = np.cumsum(np.cumsum(np.pad(l, 2, mode="edge"), axis=0), axis=1)
    c = np.pad(c, ((1, 0), (1, 0)))
    h, w = l.shape
    return (c[5:5 + h, 5:5 + w] - c[:h, 5:5 + w] - c[5:5 + h, :w] + c[:h, :w]) / 25.0


def blob(l, mask):
    d = (l - box5(l))[mask]
    return float(np.sqrt((d * d).mean()))


def inner(mask):
    m = mask.copy()
    m[:3, :] = m[-3:, :] = False
    m[:, :3] = m[:, -3:] = False
    return m


def collect_temporal(ctx):
    return collect_cases(ctx, T_CASES, "e40-gt")


def collect_cases(ctx, T_CASES, prefix):
    folder = ctx.folder("temporal")
    out_dir = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines, results = [], [], {}
    lines.append("E40-GT: the pass as the game runs it (full target 1708 x 960; display values; masks from the converged march of the pose)")
    lines.append("   static: tstd (4 consecutive frames) closed | rim ; hfx rim ; blob rim (minus the converged picture's) ; rmsb closed | rim ; err = rms of one frame against"
                 " the converged picture ; motion err: turn 30 deg/s, fly 300 m/s, turn 180, fly 2400 ; cut err at frames 0 2 5 10")
    for view in gn.VIEWS:
        refs = {tag: gn.full_frame(folder, "gt-%s-static-%s-f%d" % (view, tag, gn.T_SCENARIOS["static"][0])) for tag in ("ref", "reft")}
        x, y, w, h = gn.pick_crop(np.clip(refs["ref"][..., 3], 0.0, 1.0), 360, 200)
        for tag, ref in refs.items():
            gn.save_png(os.path.join(out_dir, "%s-t-%s-converged-%s.png" % (prefix, view, tag)), gn.full_display(ref, view))
        for march, mode in T_CASES:
            name = "%s-%s" % (march, mode)
            tag = ref_tag(march)
            ref0 = refs[tag]
            mask_set = {k: inner(v) for k, v in gn.masks(np.clip(ref0[..., 3], 0.0, 1.0)).items()}
            ref_l = gn.luma(gn.full_display(ref0, view))
            ref_lap = gn.lap(ref_l)
            fr = [gn.full_frame(folder, "gt-%s-static-%s-f%d" % (view, name, i)) for i in gn.T_SCENARIOS["static"]]
            ls = np.stack([gn.luma(gn.full_display(m, view)) for m in fr])
            tstd = ls.std(axis=0)
            mean = ls.mean(axis=0)
            row = {}
            for mk in ("closed", "rim"):
                m = mask_set[mk]
                diff = (mean - ref_l)[m]
                row[mk] = dict(tstd=float(tstd[m].mean()), hfx=float(gn.lap(ls[0])[m].mean() - ref_lap[m].mean()), rmsb=float(np.sqrt((diff * diff).mean())),
                               blob=blob(ls[0], m) - blob(ref_l, m))
                for key, val in row[mk].items():
                    metrics.append(Metric.info("e40gt.t.%s.static.%s.%s.%s" % (view, name, mk, key), val))
            d0 = (ls[0] - ref_l)[mask_set["cloud"]]
            err0 = float(np.sqrt((d0 * d0).mean()))
            motion = {}
            for sc in gn.T_MOTION:
                errs = []
                for i in gn.T_SCENARIOS[sc]:
                    ref = gn.full_frame(folder, "gt-%s-%s-%s-f%d" % (view, sc, tag, i))
                    ms = gn.masks(np.clip(ref[..., 3], 0.0, 1.0))
                    d = (gn.luma(gn.full_display(gn.full_frame(folder, "gt-%s-%s-%s-f%d" % (view, sc, name, i)), view)) - gn.luma(gn.full_display(ref, view)))[ms["cloud"]]
                    errs.append(float(np.sqrt((d * d).mean())))
                motion[sc] = float(np.mean(errs))
                metrics.append(Metric.info("e40gt.t.%s.%s.%s.err" % (view, sc, name), motion[sc]))
            cut = []
            for i in gn.T_SCENARIOS["cut"]:
                dc = (gn.luma(gn.full_display(gn.full_frame(folder, "gt-%s-cut-%s-f%d" % (view, name, i)), view)) - ref_l)[mask_set["cloud"]]
                cut.append(float(np.sqrt((dc * dc).mean())))
            results["%s-%s" % (view, name)] = dict(static=row, err_static=err0, motion=motion, cut=cut)
            lines.append("   %-7s %-18s tstd %.4f | %.4f ; hfx %+.4f ; blob %+.4f ; rmsb %.4f | %.4f ; err %.4f ; motion %s ; cut %s"
                         % (view, name, row["closed"]["tstd"], row["rim"]["tstd"], row["rim"]["hfx"], row["rim"]["blob"], row["closed"]["rmsb"], row["rim"]["rmsb"], err0,
                            " ".join("%.4f" % motion[sc] for sc in gn.T_MOTION), " ".join("%.4f" % c for c in cut)))
            strip = [gn.enlarge(gn.full_display(fr[k], view)[y:y + h, x:x + w], 2) for k in range(3)]
            noise = np.clip(tstd[y:y + h, x:x + w] * 8.0, 0.0, 1.0)
            strip.append(gn.enlarge(np.repeat(noise[..., None], 3, axis=-1), 2))
            gn.save_png(os.path.join(out_dir, "%s-t-%s-%s-seq.png" % (prefix, view, name)), np.concatenate(strip, axis=1))
            gn.save_png(os.path.join(out_dir, "%s-t-%s-%s.png" % (prefix, view, name)), gn.full_display(fr[0], view))
    with open(os.path.join(gn.BUILD, prefix.replace("-", "") + "-temporal-results.json"), "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


def collect_cost(ctx):
    values = ctx.player_metrics("cost")
    metrics, lines = [], ["E40-GT GPU ms at 1707 x 960, one layer (march / resolve / composite / total), median of 100 frames in two rounds of opposite order"]
    names = sorted(set(k.split(".", 2)[2] for k in values if k.startswith("graincost.total.")))
    for n in names:
        row = [values.get("graincost.%s.%s" % (part, n), (float("nan"),))[0] for part in ("march", "resolve", "composite", "total")]
        lines.append("   %-34s %.3f / %.3f / %.3f / %.3f" % ((n,) + tuple(row)))
        metrics.append(Metric.info("e40gt.cost." + n, row[3]))
    print("\n".join(lines))
    with open(os.path.join(gn.BUILD, "e40gt-cost-results.json"), "w", encoding="utf-8") as f:
        json.dump(dict(lines=lines), f, indent=1)
    return metrics


register_series("e40-gt-temporal", [Stage("temporal", ["-mode", "scene", "-scene", "grain-temporal", "-in", gn.INPUT, "-cases", "gt"], size=(1280, 720), proxy=False,
                                          timeout=3000)], collect_temporal, exclusive=False, who="e40-gt")
register_series("e40-gt-cost", [Stage("cost", ["-mode", "scene", "-scene", "grain-cost", "-in", gn.INPUT, "-cases", "gt"], size=(1707, 960), proxy=True, timeout=1500)],
                collect_cost, exclusive=True, who="e40-gt")


if __name__ == "__main__":
    a = np.zeros((40, 60))
    a[10:30, 20:40] = 1.0
    assert abs(blob(a, inner(a > 0.5))) > 0.0 and ref_tag("tseq1") == "reft" and ref_tag("seq1") == "ref" and ref_tag("leap4x") == "ref"
    assert abs(box5(np.ones((9, 9))) - 1.0).max() < 1e-12
    print("selftest passed")
