"""E40 FL-FIX: the cloud shadow reads the layer pass's shape noise (shadow.shapeNoise), in the weather player, shader against the CPU twin, no game start.

  python tools/build_weather_player.py --series e40-ff        build the player, run the scene, print the table
  python tools/build_weather_player.py --series e40-ff --no-build

The scene cloud-shadow with -shadownoise 1 (tests/ReDefinition.Tests/Atmospheres/T04/Probe/CloudShadowNoiseScene.cs): a slab 20 m thick at 2000 m, vertical optical depth 1,
coverage 0.35, softness 0.15, noise amount 1 at a constant map of 0.667, the sun at 45 degrees, camera 1500 m up looking down. The noise scale is 100 m (many cells along the
row) and 500 m (the shipped cumulus layer's), the sun disc has 1 or 8 taps. With the noise on, the ground factor along the middle row of the picture is compared with
CloudShadowField.DirectTransmission with CloudShadowShapeNoise at the same place; with the noise off (the shader's amounts 0) the row is one number.

EXPECTED RESULTS, written before the first run:
  F1  compiled: the shadow shader with the noise file included compiles and the renderer initialises (shadow_compiled); the noise volumes are up (noise_volumes_ready).
  F2  noise on, all four cases: median relative difference of the shader's factor against the CPU twin at most 0.05 (the old parity tolerance of the T04 scene, R12). If any
      case misses, shadow.shapeNoise stays off by default (CloudShadowShapeNoise.Default false) and the row says so.
  F3  noise on: the factor along the row spans at least 0.10 (cells); noise off: at most 0.03 (one number, the threshold alone at the map 0.667: exp(-0.6) = 0.549 at the
      vertical, lower at 45 degrees).
  F4  noise off against the map alone: the same parity (the switch off is the shadow before the noise).
  F5  the pictures keep the names shadow-e40-ff-*.png (natural) and -aid.png.
"""
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40o  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))


def collect(ctx):
    folder = ctx.folder("shadow")
    metrics = [Metric.given(name, value, limit, verdict) for name, (value, limit, verdict) in ctx.player_metrics("shadow").items()]
    pics = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(pics, exist_ok=True)
    copied = 0
    for name in sorted(os.listdir(folder)):
        if name.endswith(".png") and "e40-ff" in name:
            shutil.copyfile(os.path.join(folder, name), os.path.join(pics, name))
            copied += 1
    print("pictures: %d copied to %s" % (copied, pics))
    return metrics


register_series("e40-ff", [Stage("shadow", ["-mode", "scene", "-scene", "cloud-shadow", "-shadownoise", "1"], size=(1280, 720), proxy=False, timeout=1800)],
                collect, exclusive=False, who="e40-ff")


def collect_cost(ctx):
    """The GPU milliseconds of the shadow's passes without (N0) and with (N1) the shape noise, and the difference N1 - N0 per case."""
    got = ctx.player_metrics("cost")
    metrics = [Metric.given(name, value, limit, verdict) for name, (value, limit, verdict) in got.items()]
    print("COST (median of 90 frames, ms; build = depth to half size + the shadow at half size):")
    for name in sorted(got):
        if name.endswith(".N0.build"):
            other = name.replace(".N0.", ".N1.")
            if other in got:
                a, b = got[name][0], got[other][0]
                print("  %-44s N0 %.3f  N1 %.3f  +%.3f ms (x%.2f)" % (name[len("cost."):-len(".N0.build")], a, b, b - a, b / a if a else float("nan")))
                metrics.append(Metric.info("e40ff.cost_delta." + name[len("cost."):-len(".N0.build")], b - a))
    return metrics


# Timing: exclusive (the GPU lock), after the parity series. EXPECTED RESULTS, written before the first run: the noise adds ALU and one 3D read per sample to the half-size shadow
# pass; N1 - N0 at 1707 x 960, one layer, quality 1 (8 steps, 4 taps), looking straight down: between 0.05 and 0.6 ms; the horizon view (half the pixels are sky) about half of it.
register_series("e40-ffc", [Stage("cost", ["-mode", "scene", "-scene", "cloud-shadow-cost", "-shadownoise", "1"], size=(1280, 720), proxy=True, timeout=2400)],
                collect_cost, exclusive=True, who="e40-ffc")
