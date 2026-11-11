"""The series of the volume pass (package C21), run through tools/build_weather_player.py --series <name>:

  C21-scene   the scene 'volume': determinism of the window, the views V1 to V6 with the pass on and off (luma_ratio, base_darkness,
              diff_vs_clear, edge_contrast, the overlap of the on/off difference with the expected region), the half-resolution A/B
              (full, half, quarter, vertical-half), the allocation count of the per-frame preparation, and the contact sheet
  C21-cost    the scene 'volume-cost' (a timing run, exclusive lock): ms of the march and the composite at 1707x960 and 2560x1440, the
              steps 16/32/64 table, half and full size, and the curtain inside the cell
  C21-turn    the player's camera turn (2, then 12 degrees a frame) at the 25 km view with the volume pass (-volume 1) through DLSS K/M/L,
              FSR 3 and XeSS at render scale 0.667, the analytic motion on and off, and the pass off as the floor

The thresholds are the rows of tests/series/rows/C21.json (series 'player-C21'): one place for the limits. FSR 4 and AMD are owner-only.
Frame generation replay (DLSS-G, FSR-FG, XeSS-FG duplicates) is tools/replay_fg.ps1 on a recorded dump and is not part of these series.
"""
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402

ROWS = os.path.join(os.path.dirname(os.path.dirname(HERE)), "tests", "series", "rows", "C21.json")
SCALE = "0.6666667"


def thresholds():
    """{metric: row} of the player rows of C21.json."""
    with open(ROWS, encoding="utf-8") as f:
        rows = json.load(f)["rows"]
    return {r["metric"]: r for r in rows if r.get("series") == "player-C21"}


def judged(name, value, rows):
    """A Metric for value against the row named name (info where the row has no limit)."""
    row = rows.get(name)
    if row is None:
        return Metric.info(name, value)
    if "max" in row:
        return Metric.at_most(name, value, row["max"])
    if "min" in row:
        return Metric.at_least(name, value, row["min"])
    if "equals" in row:
        return Metric.flag(name, value == row["equals"])
    return Metric.info(name, value)


def scene_collect(ctx):
    """The metric lines of the scene 'volume' as the player judged them."""
    return core.player_judged("scene")(ctx)


register_series("C21-scene", [Stage("scene", ["-mode", "scene", "-scene", "volume"])], scene_collect, exclusive=False, who="C21")
register_series("C21-cost", [Stage("scene", ["-mode", "scene", "-scene", "volume-cost"], size=(1707, 960))], scene_collect, exclusive=True, who="C21")

# The turn runs: label -> (player arguments, whether the volume is drawn).
TURNS = {
    "dlss_K": ["-upscale", "dlss", "-scale", SCALE, "-preset", "K"],
    "dlss_M": ["-upscale", "dlss", "-scale", SCALE, "-preset", "M"],
    "dlss_L": ["-upscale", "dlss", "-scale", SCALE, "-preset", "L"],
    "fsr3": ["-upscale", "fsr3", "-scale", SCALE, "-bundle", "{bundle}"],
    "xess": ["-upscale", "xess", "-scale", SCALE],
    "dlss_K_noanalytic": ["-upscale", "dlss", "-scale", SCALE, "-preset", "K", "-vset", "volume.analytic=0"],
    "fsr3_noanalytic": ["-upscale", "fsr3", "-scale", SCALE, "-bundle", "{bundle}", "-vset", "volume.analytic=0"],
    "dlss_K_fixedign": ["-upscale", "dlss", "-scale", SCALE, "-preset", "K", "-vset", "volume.kwJitter=0"],
    "fsr3_fixedign": ["-upscale", "fsr3", "-scale", SCALE, "-bundle", "{bundle}", "-vset", "volume.kwJitter=0"],
    "dlss_K_off": ["-upscale", "dlss", "-scale", SCALE, "-preset", "K", "-slab", "0"],
    "fsr3_off": ["-upscale", "fsr3", "-scale", SCALE, "-bundle", "{bundle}", "-slab", "0"],
    "native": ["-upscale", "native"],
}


def turn_stages(labels=None):
    out = []
    for label, extra in TURNS.items():
        if labels is not None and label not in labels:
            continue
        out.append(Stage(label, ["-mode", "turn", "-view", "far", "-volume", "1"] + extra, upscaler=label != "native"))
    return out


def turn_collect(ctx):
    rows = thresholds()
    out = []
    figures = {}
    for label in TURNS:
        if label in ctx.folders:
            figures[label] = core.turn_figures(ctx.folder(label))
    for label in ("dlss_K", "dlss_M", "dlss_L", "fsr3"):
        f = figures.get(label, {})
        for name in ("ghost_move", "trail_px", "shimmer"):
            out.append(judged("%s.%s" % (name, label), f.get(name, float("nan")), rows))
        out.append(Metric.info("ghost_still.%s" % label, f.get("ghost_still", float("nan"))))
    xess = figures.get("xess", {})
    if "xess" in ctx.folders:
        for name in ("ghost_move", "ghost_still", "trail_px", "shimmer"):
            out.append(judged("%s.xess" % name, xess.get(name, float("nan")), rows))
    # The analytic motion on against off, the keyed IGN against the fixed one, the pass on against the floor (off).
    for upscaler, on in (("dlss_K", "dlss_K"), ("fsr3", "fsr3")):
        for variant in ("noanalytic", "fixedign", "off"):
            other = figures.get("%s_%s" % (on, variant), {})
            for name in ("ghost_move", "trail_px", "shimmer"):
                value, base = other.get(name, float("nan")), figures.get(on, {}).get(name, float("nan"))
                out.append(Metric.info("%s.%s_%s" % (name, on, variant), value))
                out.append(Metric.info("delta_%s.%s_%s" % (name, on, variant), value - base if not (math.isnan(value) or math.isnan(base)) else float("nan")))
    native = figures.get("native", {})
    for name in ("ghost_move", "trail_px", "shimmer"):
        out.append(Metric.info("%s.native" % name, native.get(name, float("nan"))))
    mv = core.motion_match(ctx.folder("dlss_K")) if "dlss_K" in ctx.folders else float("nan")
    out.append(judged("mv_match", mv, rows))
    return out


register_series("C21-turn", turn_stages(), turn_collect, exclusive=False, who="C21")
# The same runs in two halves, each inside the 20 minutes of one lock.
register_series("C21-turn-a", turn_stages(["native", "dlss_K", "dlss_M", "dlss_L", "fsr3"]), turn_collect, exclusive=False, who="C21")
register_series("C21-turn-b", turn_stages(["xess", "dlss_K_noanalytic", "fsr3_noanalytic", "dlss_K_fixedign", "fsr3_fixedign", "dlss_K_off", "fsr3_off"]),
                turn_collect, exclusive=False, who="C21")
