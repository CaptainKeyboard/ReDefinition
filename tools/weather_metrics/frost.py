"""The camera frost's series of the weather player (package D37).

  python tools/build_weather_player.py --series frost          the scene frost-check (shape, atlas, A/B, flake, drop, cost)
  python tools/build_weather_player.py --series frost-turn-a   the camera turn through DLSS K, M and L, each with the frost off and on
  python tools/build_weather_player.py --series frost-turn-b   FSR 3 and XeSS off and on, and the quad fallback under DLSS K and FSR 3
  python tools/weather_metrics/frost.py fg <frost recording> <baseline recording>     the frame generation replay (see below)
  python tools/weather_metrics/frost.py sheet <out.png> <turn folder>...              the settled frames side by side (off, host, quad)

Stage "scene" of "frost" runs the player's scene frost-check, which draws the frost's own pass (FrostPass, FrostAtlas and the shader, the
classes the mod uses) onto an image of the scene's size and judges itself: the atlas is 4 MB and builds in under 60 ms; the
contribution-mask coverage at exposure 0 / 0.4 / 1 against the design table 0 / 12.4 / 33.3 % (16:9, +-10 %) and against the CPU
arithmetic (2 %); not one mask pixel in the clear centre; the picture outside the mask identical with the frost on and off; a flake's
size and refraction; a drop's trail against the gravity direction (down, and rolled to the right) and its wiping of the frost; an empty
command buffer when nothing is on the glass; the cost of an active draw at 2560 x 1440 (at most 0.25 ms, reported as the frame loop
minus the empty-clear baseline).

The turn series use the player's camera turn (2 degrees a frame, then 12) at render scale 0.667 with the frost of
tests/WeatherPlayer/Assets/Runner/FrostTurnOverlay.cs: a fixed state (exposure 0.7, 20 flakes, 12 drops), drawn
  host  onto the upscaled image after the upscaler and onto each reference copy (the mod's composite), or
  quad  in the scene before the upscaler (the fallback).
The ghost figure of a run is the mean absolute luminance difference between the frame and its reference (the same frost over the
reference's content) over the frost region of the picture (radius from the centre at least 1 - growth + 0.05 of the half-diagonal),
over the moving frames; the row is its ratio to the same upscaler's run with the frost off (limit baseline + 20 %). The change the
frost made against the baseline run must be visible (a ratio near 1 over an invisible frost proves nothing).

Frame generation replay: turn_to_fg_dump.py writes a turn folder as the input dump, replay_fg.ps1 plays it through frame generation and
cuts the recording into client/*-real-<n>.png and *-generated.png; `frost.py fg` compares, over the frost region, the generated frames
with the mean of their rendered neighbours for the frost run against the baseline run (limit +20 %):

  python tools/turn_to_fg_dump.py build/weather-player/frost-turn-a-dlss_K_host build/fg-dump-host
  python tools/turn_to_fg_dump.py build/weather-player/frost-turn-a-dlss_K_off  build/fg-dump-off
  powershell tools/replay_fg.ps1 build/fg-dump-host      (and the same for -off; the window stays in front, nothing else on screen)
  python tools/weather_metrics/frost.py fg <recording of host> <recording of off>

The thresholds are the rows of tests/series/rows/D37.json (series 'player-D37'). FSR 4 and AMD are owner-only.
"""
import glob
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import numpy as np  # noqa: E402

import analyze_weather_player as awp  # noqa: E402
from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics.core import player_judged  # noqa: E402

ROWS = os.path.join(os.path.dirname(os.path.dirname(HERE)), "tests", "series", "rows", "D37.json")
SCALE = "0.6666667"
# The state FrostTurnOverlay draws: exposure 0.7, growth = 0.35 * exposure ^ 0.7 of the half-diagonal (FrostShape).
EXPOSURE = 0.7
GROWTH = 0.35 * EXPOSURE ** 0.7
RIM_FROM = 1.0 - GROWTH + 0.05
# A pixel counts as changed by the frost when its luminance differs by more than this from the baseline run's.
VISIBLE_DIFF = 0.005


def thresholds():
    """{metric: row} of the player rows of D37.json."""
    with open(ROWS, encoding="utf-8") as f:
        rows = json.load(f)["rows"]
    return {r["metric"]: r for r in rows if r.get("series") == "player-D37"}


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


def rim_mask(w, h):
    """The frost region of a w x h picture: radius (FrostShape.Radius: 1 in a corner) at least RIM_FROM."""
    aspect = w / h
    x = ((np.arange(w) + 0.5) / w - 0.5) * aspect
    y = (np.arange(h) + 0.5) / h - 0.5
    r = np.hypot(x[None, :], y[:, None]) / (0.5 * math.hypot(aspect, 1.0))
    return r >= RIM_FROM


def turn_frames(folder):
    """(meta, names, w, h, last_move) of a turn folder."""
    meta = awp.read_meta(folder)
    path = [float(v) for v in open(os.path.join(folder, "path.txt"), encoding="utf-8").read().split()]
    names = sorted(n for n in meta if n.startswith("turn_"))
    w, h = meta[names[0]]
    last_move = max(i for i in range(1, len(path)) if path[i] != path[i - 1])
    return meta, names, w, h, last_move


def luma_of(folder, name, w, h):
    colour = awp.read_half(folder, name, "colour", w, h, 4)
    return None if colour is None else awp.luma(colour[..., :3])


def rim_figures(folder):
    """ghost_move, ghost_still and shimmer over the frost region: the frame against its reference, and frame to frame once the turn
    has stopped. {} where the folder holds no turn."""
    if not os.path.exists(os.path.join(folder, "meta.txt")):
        return {}
    meta, names, w, h, last_move = turn_frames(folder)
    mask = rim_mask(w, h)
    moving, still, shimmer = [], [], []
    previous = None
    for i, n in enumerate(names):
        y = luma_of(folder, n, w, h)
        if y is None:
            continue
        ref = "ref_" + n[len("turn_"):]
        if ref in meta:
            rw, rh = meta[ref]
            r = awp.read_half(folder, ref, "colour", rw, rh, 4)
            if r is not None:
                r = r[..., :3].reshape(h, rh // h, w, rw // w, 3).mean(axis=(1, 3))
                err = float(np.abs(y - awp.luma(r))[mask].mean())
                if 6 < i <= last_move:
                    moving.append(err)
                elif i > last_move + 10:
                    still.append(err)
        if i > last_move + 10:
            if previous is not None:
                shimmer.append(float(np.abs(y - previous)[mask].mean()))
            previous = y
    nan = float("nan")
    return dict(ghost_move=float(np.mean(moving)) if moving else nan, ghost_still=float(np.mean(still)) if still else nan,
                shimmer=float(np.mean(shimmer)) if shimmer else nan)


def visible_change(folder, base):
    """The share of the frost region's pixels that the frost changed against the baseline run of the same upscaler (the moving frames,
    at most ten of them)."""
    if not (os.path.exists(os.path.join(folder, "meta.txt")) and os.path.exists(os.path.join(base, "meta.txt"))):
        return float("nan")
    _, names, w, h, last_move = turn_frames(folder)
    mask = rim_mask(w, h)
    picks = [i for i in range(7, last_move + 1)][:10]
    shares = []
    for i in picks:
        a, b = luma_of(folder, names[i], w, h), luma_of(base, names[i], w, h)
        if a is not None and b is not None:
            shares.append(float((np.abs(a - b)[mask] > VISIBLE_DIFF).mean()))
    return float(np.mean(shares)) if shares else float("nan")


def ratio(frost, base):
    if math.isnan(frost) or math.isnan(base) or base <= 1e-9:
        return float("nan")
    return frost / base


# label -> player arguments. The off run is the baseline of its upscaler; "_host" and "_quad" add -frost.
UPSCALERS = {
    "dlss_K": ["-upscale", "dlss", "-scale", SCALE, "-preset", "K"],
    "dlss_M": ["-upscale", "dlss", "-scale", SCALE, "-preset", "M"],
    "dlss_L": ["-upscale", "dlss", "-scale", SCALE, "-preset", "L"],
    "fsr3": ["-upscale", "fsr3", "-scale", SCALE, "-bundle", "{bundle}"],
    "xess": ["-upscale", "xess", "-scale", SCALE],
}
TURN = ["-mode", "turn", "-view", "far"]


def turn_stages(wanted):
    """wanted: [(upscaler, variant)] with variant off, host or quad."""
    out = []
    for upscaler, variant in wanted:
        extra = TURN + UPSCALERS[upscaler] + ([] if variant == "off" else ["-frost", variant])
        out.append(Stage("%s_%s" % (upscaler, variant), extra, upscaler=True))
    return out


A_RUNS = [(u, v) for u in ("dlss_K", "dlss_M", "dlss_L") for v in ("off", "host")]
B_RUNS = [(u, v) for u in ("fsr3", "xess") for v in ("off", "host")] + [("dlss_K", "off"), ("dlss_K", "quad"), ("fsr3", "quad")]


def turn_collect(ctx):
    rows = thresholds()
    out = []
    labels = set(ctx.folders)
    for upscaler in UPSCALERS:
        base_label = upscaler + "_off"
        if base_label not in labels:
            continue
        base = rim_figures(ctx.folder(base_label))
        for variant in ("host", "quad"):
            label = "%s_%s" % (upscaler, variant)
            if label not in labels:
                continue
            figures = rim_figures(ctx.folder(label))
            for name in ("ghost_move", "ghost_still", "shimmer"):
                out.append(judged("ghost.frost_%s.%s.%s.ratio_to_baseline" % (variant, upscaler, name),
                                  ratio(figures.get(name, float("nan")), base.get(name, float("nan"))), rows))
            out.append(judged("ghost.frost_%s.%s.visible_change" % (variant, upscaler),
                              visible_change(ctx.folder(label), ctx.folder(base_label)), rows))
            out.append(Metric.info("ghost.frost_%s.%s.rim_ghost_move" % (variant, upscaler), figures.get("ghost_move", float("nan"))))
        out.append(Metric.info("ghost.baseline.%s.rim_ghost_move" % upscaler, base.get("ghost_move", float("nan"))))
    return out


register_series("frost", [Stage("scene", ["-mode", "scene", "-scene", "frost-check"])], player_judged("scene"), exclusive=False, who="D37")
register_series("frost-turn-a", turn_stages(A_RUNS), turn_collect, exclusive=False, who="D37")
register_series("frost-turn-b", turn_stages(B_RUNS), turn_collect, exclusive=False, who="D37")


# ---- The frame generation replay

def replay_frames(recording):
    """[(is_real, k, luma)] of a cut recording (replay_measure.py): client/*-real-<k>.png and *-generated.png in file order."""
    from PIL import Image
    items = []
    for path in sorted(glob.glob(os.path.join(recording, "client", "*.png"))):
        name = os.path.basename(path)[:-4]
        m = re.search(r"-real-(\d+)$", name)
        if not m and not name.endswith("-generated"):
            continue
        g = np.asarray(Image.open(path).convert("L"), dtype=np.float32)
        items.append((m is not None, int(m.group(1)) if m else -1, g))
    return items


def replay_rim_deviation(recording):
    """The mean absolute difference, over the frost region and with the grain softened, between each generated frame and the mean of the
    two rendered frames around it; NaN where there is no such frame."""
    sys.path.insert(0, os.path.dirname(os.path.dirname(HERE)) + os.sep + "tools")
    import replay_measure
    items = replay_frames(recording)
    values = []
    for i in range(1, len(items) - 1):
        if items[i][0] or not items[i - 1][0] or not items[i + 1][0]:
            continue
        if items[i + 1][1] != items[i - 1][1] + 1:
            continue
        d = items[i][2] - (items[i - 1][2] + items[i + 1][2]) / 2
        h, w = d.shape
        values.append(float(np.abs(replay_measure.blur(d, 8))[rim_mask(w, h)].mean()))
    return float(np.mean(values)) if values else float("nan")


def fg_main(argv):
    """frost.py fg <frost recording> <baseline recording>: prints the ratio and its verdict against the row."""
    if len(argv) != 2:
        sys.exit(__doc__)
    rows = thresholds()
    frost, base = replay_rim_deviation(argv[0]), replay_rim_deviation(argv[1])
    metric = judged("ghost.fg_replay.ratio_to_baseline", ratio(frost, base), rows)
    print("fg replay: frost %.5f, baseline %.5f" % (frost, base))
    print("%s %.4g (%s) %s" % (metric.name, metric.value if metric.value is not None else float("nan"), metric.limit_text(), "PASS" if metric.passed() else "FAIL"))
    return 0 if metric.passed() else 1


def sheet_main(argv):
    """frost.py sheet <out.png> <turn folder>...: the settled frame (the last turn frame) of each folder side by side, for the A/B
    pictures (frost off, host, quad) of the reviewer."""
    if len(argv) < 2:
        sys.exit(__doc__)
    from PIL import Image
    tiles = []
    for folder in argv[1:]:
        meta, names, w, h, _ = turn_frames(folder)
        colour = awp.read_half(folder, names[-1], "colour", w, h, 4)
        image = np.clip(colour[..., :3], 0.0, 1.0) ** (1.0 / 2.2)
        tiles.append((image[::2, ::2] * 255.0 + 0.5).astype(np.uint8))
    Image.fromarray(np.hstack(tiles)).save(argv[0])
    print("wrote", argv[0])
    return 0


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "fg":
        sys.exit(fg_main(sys.argv[2:]))
    if len(sys.argv) > 1 and sys.argv[1] == "sheet":
        sys.exit(sheet_main(sys.argv[2:]))
    sys.exit(__doc__)
