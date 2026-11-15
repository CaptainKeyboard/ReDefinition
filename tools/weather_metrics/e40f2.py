"""E40 F2: terraces from above and the fine static pattern of the layer march (player series e40-f2-cause, e40-f2-fix).

    python tools/build_weather_player.py --series e40-f2-cause
    python tools/build_weather_player.py --series e40-f2-fix
    python tools/weather_metrics/e40f2.py --table NAME     the table of a finished run again (offline)

The scene is `weave` (SoftTable cases, tests/WeatherPlayer/Assets/Runner/WeaveScene.cs): a still camera, the pass as the game runs it, the mean of the last eight frames.
The reference of this series is NOT the scene's converged picture (a fixed grid of 256 points, no history, full size: it carries its own iso-step lines and all of the detail the
half-size target loses) but the SAME pipeline with 256 grid points (`seq1m256` with the case's resolution and history): what the game's own `ref<name>` frames are. The residual
mean8(case) - mean8(ref) is then the error of the step count alone, with the offset sequence and the history of the case.

Measures over the cloud pixels (alpha of the reference 0.05 or more), on the display luma, per view:
  err3   rms of hp3(residual), hp3(x) = x - gaussian(x, 3 px)                    what the eye sees as terraces and weave
  err15  rms of hp1.5(residual), periods up to about 6 px                        the fine pattern
  fine   share of the power of the residual at periods under 6 px (2D spectrum)  a lattice has most of it here
  tstd   temporal std of the last three frames
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, register_series  # noqa: E402
from weather_metrics import e40gc as gc  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40wv as wv  # noqa: E402

BUILD = gc.BUILD
PREFIX = "e40-f2"
VIEWS = "ground,above,l4above"
REF = {50: "seq1m256+r50", 33: "seq1m256+r33", 100: "seq1m256+r100"}

# What the cause run asks: the step count, the target size, the history, the offset, the light and the noise, one thing at a time.
CAUSE = [
    "seq1m256+r50", "seq1m256+r33",
    "seq1+r50", "seq1m32+r50", "seq1m24+r50", "seq1m96+r50",
    "seq1+r33", "seq1m24+r33",
    "seq1+r50+acc00", "seq1+r50+ds1", "seq1+r50+jit",
    "seq1+r50+dbg1", "seq1+r50+dbg2", "seq1m256+r50+dbg1", "seq1m256+r50+dbg2",
]
CAUSE_STAGES = [("f2-cause", VIEWS, "pr50", CAUSE)]
# The length of the history: a bias of the step count does not change with it, the spread of a few offsets does.
CAUSE2 = ["seq1m256+r50", "seq1+r50", "seq1+r50+acc10", "seq1+r50+acc05", "seq1+r50+acc02", "seq1+r50+acc02+clip1000", "seq1m24+r50+acc02", "seq1+r33+acc05"]
CAUSE2_STAGES = [("f2-cause2", VIEWS, "pr50", CAUSE2)]
# Which stage makes the mesh: the light, the shape noise, the erosion switched off (dbg bits 1, 2, 4), the light marched at every lit step, the light's taps; each against its own 256-point reference.
CAUSE3 = [m + "+r50" + x for m in ("seq1m256", "seq1") for x in ("", "+dbg1", "+dbg2", "+dbg4", "+dbg7", "+ls1+lc0", "+lq0", "+lq2", "+sun1")]
CAUSE3_STAGES = [("f2-cause3", "above,ground", "pr50", CAUSE3)]
# Filled in by the fix: the cases of the new march at both targets.
# The fix: the pass before F2 (the camera's grid scaled by the uv, gradient noise for both), the own grid alone, the default (own grid, blue-noise tile for the step offset and the cone), the old
# grid with the tile, the white hash.
FIX = ["seq1+r%d%s" % (r, x) for r in (50, 33) for x in ("+og+ds2+cr0", "+ds2+cr0", "", "+og", "+ds1+cr2")]
FIX_STAGES = [("f2-fix", "above,ground", "pr50", FIX + ["seq1m256" + c[len("seq1"):] for c in FIX])] if FIX else []


# The smooth cloud body of each view (x, y of a 192 x 128 crop; no rim in it).
SMOOTH = {"above": (896, 640), "ground": (0, 0), "l4above": (768, 192)}


def ref_of(case):
    """The same case with 256 grid points (every modifier kept, so that a switched-off light or noise has its own reference); the accumulation modifiers stay too."""
    base, _, rest = case.partition("+")
    return "seq1m256" + ("+" + rest if rest else "")


def band(x, lo=0.8, hi=2.0):
    """Periods of about 2 to 5 px (the mesh of the march target's pitch): the difference of two gaussians."""
    from scipy.ndimage import gaussian_filter
    return gaussian_filter(x, lo, mode="nearest") - gaussian_filter(x, hi, mode="nearest")


def hp(x, sigma):
    from scipy.ndimage import gaussian_filter
    return x - gaussian_filter(x, sigma, mode="nearest")


def fine_share(r, mask, period=6.0):
    """Share of the power (Hann window over the whole frame) at periods under `period` px."""
    h, w = r.shape
    win = np.outer(np.hanning(h), np.hanning(w))
    p = np.abs(np.fft.fft2(np.where(mask, r, 0.0) * win)) ** 2
    fy = np.fft.fftfreq(h)[:, None]
    fx = np.fft.fftfreq(w)[None, :]
    rad = np.hypot(fx, fy)
    tot = p[rad > 1.0 / 32.0].sum()
    return float(p[rad > 1.0 / period].sum() / max(tot, 1e-300))


def lum(folder, name, alias):
    a = wv.load(folder, name)
    return gn.luma(gn.full_display(a, alias)), np.clip(a[..., 3], 0.0, 1.0)


def collect(ctx, stages=None, json_path=None):
    metrics, lines, results = [], [], {}
    lines.append("E40-F2 error of the step count (display luma, case mean8 minus the same pipeline at 256 points; cloud mask)")
    lines.append("   %-8s %-26s err3    err15   fine  tstd" % ("view", "case"))
    for label, _, media, cases in stages or CAUSE_STAGES + FIX_STAGES:
        folder = ctx.folder(label)
        for medium in media.split(","):
            for view in VIEWS.split(","):
                alias = gc.VIEWS[view]
                for case in cases:
                    if label != "f2-cause" and case.startswith("seq1m256"):
                        continue   # a reference of the run (the cause run reads its own list as it stands)
                    ref_name ="wv-%s-%s-%s-mean8" % (view, medium, ref_of(case))
                    name = "wv-%s-%s-%s-mean8" % (view, medium, case)
                    if not (os.path.isfile(os.path.join(folder, name + ".npy")) and os.path.isfile(os.path.join(folder, ref_name + ".npy"))):
                        continue
                    ref, alpha = lum(folder, ref_name, alias)
                    mean, _ = lum(folder, name, alias)
                    mask = alpha > 0.05
                    f0 = gn.luma(gn.full_display(wv.load(folder, name[:-len("-mean8")] + "-f0"), alias))
                    f1 = gn.luma(gn.full_display(wv.load(folder, name[:-len("-mean8")] + "-f1"), alias))
                    f2 = gn.luma(gn.full_display(wv.load(folder, name[:-len("-mean8")] + "-f2"), alias))
                    r = mean - ref
                    st = dict(lat=float(np.sqrt((band(r)[mask] ** 2).mean())), latref=float(np.sqrt((band(ref)[mask] ** 2).mean())), latm=float(np.sqrt((band(mean)[mask] ** 2).mean())),
                              latf=float(np.sqrt((band(f0)[mask] ** 2).mean())), err3=float(np.sqrt((hp(r, 3.0)[mask] ** 2).mean())), err15=float(np.sqrt((hp(r, 1.5)[mask] ** 2).mean())),
                              fine=fine_share(hp(r, 3.0), mask), tstd=float(np.stack([f0, f1, f2]).std(axis=0)[mask].mean()), mask=float(mask.mean()))
                    # The smooth cloud body of the view (a fixed crop of 192 x 128 where the picture holds no edge): the error of the mean against its reference and how much of the mean's power
                    # sits in its single strongest spatial line (a lattice puts a few percent there, noise a fraction of a percent).
                    sx, sy = SMOOTH.get(view, (0, 0))
                    box = (slice(sy, sy + 128), slice(sx, sx + 192))
                    sm_err = float(hp(r, 3.0)[box].std())
                    spec = np.abs(np.fft.fft2(hp(mean, 3.0)[box] * np.outer(np.hanning(128), np.hanning(192)))) ** 2
                    spec[0, 0] = 0.0
                    rad = np.hypot(np.fft.fftfreq(128)[:, None], np.fft.fftfreq(192)[None, :])
                    st.update(smerr=sm_err, smline=float(spec[rad > 0.12].max() / spec[rad > 0.12].sum()), smlow=float(spec[(rad > 0.02) & (rad <= 0.12)].sum() / spec[rad > 0.02].sum()))
                    results["%s|%s|%s" % (view, medium, case)] = st
                    for k, v in st.items():
                        metrics.append(Metric.info("e40f2.%s.%s.%s.%s" % (view, medium, case, k), v))
                    lines.append("   %-8s %-26s %.4f  %.4f  %.2f  %.4f  lat %.4f smooth: err %.4f line %.3f low %.2f" % (view, case, st["err3"], st["err15"], st["fine"], st["tstd"], st["lat"],
                                                                                                                         st["smerr"], st["smline"], st["smlow"]))
    with open(json_path or os.path.join(BUILD, "e40f2-results.json"), "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


register_series("e40-f2-cause", [wv.weave_stage(*s) for s in CAUSE_STAGES], collect, exclusive=False, who="e40-f2")
register_series("e40-f2-cause3", [wv.weave_stage(*s, extra=("-frames", "60")) for s in CAUSE3_STAGES], lambda ctx: collect(ctx, CAUSE3_STAGES, os.path.join(BUILD, "e40f2-cause3-results.json")),
                exclusive=False, who="e40-f2")
register_series("e40-f2-cause2",[wv.weave_stage(*s, extra=("-frames", "260")) for s in CAUSE2_STAGES], lambda ctx: collect(ctx, CAUSE2_STAGES, os.path.join(BUILD, "e40f2-cause2-results.json")),
                exclusive=False, who="e40-f2")
if FIX_STAGES:
    register_series("e40-f2-fix", [wv.weave_stage(*s) for s in FIX_STAGES], lambda ctx: collect(ctx, FIX_STAGES, os.path.join(BUILD, "e40f2-fix-results.json")), exclusive=False, who="e40-f2")

# The cost of the pass at 1707 x 960 (the player's timestamp queries, a timing run): the pass before F2 against the default, at both targets; each case twice (control) so that the noise of the run shows.
COST_CASES = ["seq1+r50+og+ds2+cr0", "seq1+r50", "seq1+r50+og+ds2+cr0+ctl", "seq1+r50+ctl", "seq1+r33+og+ds2+cr0", "seq1+r33"]
COST_STAGES = [("f2-cost", "ground,above", "pr50", COST_CASES)]
register_series("e40-f2-cost", [gc.cost_stage(*s) for s in COST_STAGES], lambda ctx: gc.collect_cost(ctx, COST_STAGES, os.path.join(BUILD, "e40f2-cost-results.json")), exclusive=True, who="e40-f2")
