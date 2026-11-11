"""The metrics every series can use, and the smoke series: the spike's slab scene at the 25 km view through the player, judged against
the values the spike measured (.local/plans/spike-result.md, the 25 km rows and the camera turn of the slab at native resolution).

Stages: stills (the clear frame and the slab with the curtain), turn (the camera turn 2 then 12 degrees a frame, native, slab on) and
time (the proxy's GPU timestamps of the slab's passes). Eight or more named metrics come out: luma_mean, luma_ratio, diff_vs_clear,
edge_contrast, local_contrast, ghost_move, ghost_still, trail_px, shimmer, mv_match, gpu_ms_pass and frame_ms.
"""
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import analyze_weather_player as awp  # noqa: E402
from weather_metrics import Metric, Stage, register_series  # noqa: E402

# The spike's known values (25 km, own slab and curtain; native camera turn with the slab). Rounded to the digits the spike printed,
# so the floor of a metric is half of its last printed digit.
SPIKE = {
    "luma_ratio": (0.965, 0.0005),
    "diff_vs_clear": (0.215, 0.0005),
    "edge_contrast": (0.026, 0.0005),
    "local_contrast": (0.210, 0.0005),
    "ghost_move": (0.0091, 0.00005),
    "ghost_still": (0.0085, 0.00005),
    "trail_px": (0.059, 0.0005),
}
# The spike printed shimmer as 0.0001 (four decimals), so its full-precision value is not known. The metric is judged against a noise
# ceiling: anything the spike could have rounded to 0.0001 (below 0.00015) passes, a shimmer of DLSS size (0.0008) fails.
SHIMMER_CEILING = 0.00015
# Gate G3 of the plan: the slab's passes cost at most 1.5 ms. The frame limit is a sanity ceiling for the whole synthetic frame.
SLAB_MS_LIMIT = 1.5
FRAME_MS_LIMIT = 8.0
# The spike printed the sign agreement and the replaced share of the motion, not this figure; the limit is a new one (the first run
# gave 0.999).
MV_MATCH_LIMIT = 0.98
REL = 0.10


def still_figures(folder, view="far"):
    """The sky-band figures of the view with the slab and the curtain against its clear frame (analyze_weather_player.metrics)."""
    clear = awp.load_player(folder, view + "_clear")
    both = awp.load_player(folder, view + "_both")
    if clear is None or both is None:
        return {}
    figures = awp.metrics(view, both, clear, "player")
    figures["local_contrast"] = figures.pop("contrast_gain")
    return figures


def turn_figures(folder):
    """ghost_move, ghost_still, trail_px, shimmer and the motion match of one turn folder (analyze_weather_player.turn), or {}."""
    import contextlib
    import io

    class Args(object):
        folders = [folder]
    if not os.path.exists(os.path.join(folder, "meta.txt")):
        return {}
    with contextlib.redirect_stdout(io.StringIO()):
        results = awp.turn(Args())
    return next(iter(results.values()), {})


def motion_match(folder):
    """1 minus the mean difference in x between the analytic motion and Unity's raw motion over the pixels the analytic motion
    replaced (moving frames of the turn), over the mean magnitude of the raw motion there; NaN where no pixel was replaced."""
    import numpy as np
    meta = awp.read_meta(folder)
    path = [float(v) for v in open(os.path.join(folder, "path.txt"), encoding="utf-8").read().split()]
    names = sorted(n for n in meta if n.startswith("turn_"))
    w, h = meta[names[0]]
    last_move = max(i for i in range(1, len(path)) if path[i] != path[i - 1])
    difference, magnitude = 0.0, 0.0
    for i, name in enumerate(names):
        if not 6 < i <= last_move:
            continue
        size = awp._render_size(folder, name, w, h)
        motion = awp.read_half(folder, name, "motion", *size, 2)
        raw = awp.read_half(folder, name, "motionraw", *size, 2)
        if motion is None or raw is None:
            continue
        active = np.abs(motion - raw).sum(axis=2) > 1e-6
        if active.any():
            difference += float(np.abs(motion[active][:, 0] - raw[active][:, 0]).mean())
            magnitude += float(np.abs(raw[active][:, 0]).mean())
    return 1.0 - difference / magnitude if magnitude > 0 else float("nan")


def smoke(ctx):
    """The metrics of the smoke series."""
    out = []
    figures = still_figures(ctx.folder("stills"))
    turned = turn_figures(ctx.folder("turn"))
    timing = ctx.player_metrics("time")
    out.append(Metric.info("luma_mean", figures.get("luma_mean", float("nan"))))
    for name in ("luma_ratio", "diff_vs_clear", "edge_contrast", "local_contrast"):
        known, floor = SPIKE[name]
        out.append(Metric.reference(name, figures.get(name, float("nan")), known, REL, floor))
    for name in ("ghost_move", "ghost_still", "trail_px"):
        known, floor = SPIKE[name]
        out.append(Metric.reference(name, turned.get(name, float("nan")), known, REL, floor))
    out.append(Metric.at_most("shimmer", turned.get("shimmer", float("nan")), SHIMMER_CEILING).based("noise"))
    # mv_match: where the analytic motion replaced pixels, how well it agrees with Unity's own motion at those pixels (1 is exact; the
    # slab is far, so a camera turn moves it as it moves the sky). NaN, and so a failure, where nothing was replaced.
    out.append(Metric.at_least("mv_match", motion_match(ctx.folder("turn")), MV_MATCH_LIMIT).based("new"))
    slab =timing.get("gpu_ms.slab_total.far.slab.s32.r0.5.1707x960")
    frame = timing.get("gpu_ms.frame.far.both.s32.r0.5.1707x960")
    out.append(Metric.at_most("gpu_ms_pass", slab[0] if slab else float("nan"), SLAB_MS_LIMIT).based("gate"))
    out.append(Metric.at_most("frame_ms", frame[0] if frame else float("nan"), FRAME_MS_LIMIT).based("new"))
    return out


register_series("smoke", [
    Stage("stills", ["-mode", "stills", "-views", "far"]),
    Stage("turn", ["-mode", "turn", "-view", "far", "-upscale", "native"]),
    Stage("time", ["-mode", "time", "-views", "far", "-width", "1707", "-height", "960"], size=(1707, 960)),
], smoke, exclusive=True)


def player_judged(label):
    """A collector for a scene that judges itself: its {metric,value,limit,pass} lines as they are, the player's verdict included."""
    def collect(ctx):
        return [Metric.given(name, value, limit, verdict) for name, (value, limit, verdict) in ctx.player_metrics(label).items()]
    return collect


register_series("world", [Stage("scene", ["-mode", "scene", "-scene", "world-check"])], player_judged("scene"), exclusive=False)


register_series("contrib", [Stage("scene", ["-mode", "scene", "-scene", "contrib-check"])], player_judged("scene"), exclusive=False)


MATRIX = os.path.join(os.path.dirname(HERE), "..", "tests", "series", "interaction-matrix.json")


def interaction_player_rows(matrix_path=MATRIX):
    """The player rows of tests/series/interaction-matrix.json."""
    import json
    with open(matrix_path, encoding="utf-8") as f:
        return json.load(f)["player_rows"]


def interaction_collect(ctx):
    """Each player row's turn figures against the expectation written first in the matrix: a known value (relative tolerance, floor)
    or the figure of another row (same_as)."""
    rows = interaction_player_rows()
    measured = {}
    for row in rows:
        measured[row["id"]] = turn_figures(ctx.folder(row["id"]))
    out = []
    for row in rows:
        for name, expected in row["expect"].items():
            value = measured[row["id"]].get(name, float("nan"))
            if isinstance(expected, dict):
                known = measured[expected["same_as"]].get(name, float("nan"))
                out.append(Metric.reference("%s.%s" % (row["id"], name), value, known, expected["rel"], 0.0))
            else:
                known, rel, floor = expected
                out.append(Metric.reference("%s.%s" % (row["id"], name), value, known, rel, floor))
    return out


def _interaction_stages():
    return [Stage(row["id"], row["stage"], upscaler=bool(row.get("upscaler")), proxy=bool(row.get("proxy", True)))
            for row in interaction_player_rows()]


register_series("interaction-player", _interaction_stages(), interaction_collect, exclusive=False)
