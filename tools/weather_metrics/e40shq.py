"""E40 follow-up: the cloud shadow's step policy (shadow.stepPolicy) in the weather player, shader against the CPU twin, no game start.

  python tools/build_weather_player.py --series e40-shq        build the player, run the scene, print the table
  python tools/build_weather_player.py --series e40-shq --no-build

The scene cloud-shadow with -shadowsteps 1 (tests/ReDefinition.Tests/Atmospheres/T04/Probe/CloudShadowStepsScene.cs): a slab 20 m thick at 2000 m, vertical optical depth 1,
sun at 45 degrees, camera 1500 m up looking down. The layer interval is widened by a height reach of 40 m (5 times, 60 steps) or 180 m (19 times, capped at 64 steps), the
policy is scaled (1) or fixed (0), the sun disc has 1 or 8 taps. For each of the 8 cases the picture's ground factor along the middle row is compared with
CloudShadowField.DirectTransmission at the same place with the same policy, steps and taps.

EXPECTED RESULTS, written before the first run:
  S1  compiled: the shadow shader with the per-layer step count and the loop bound 64 compiles and the renderer initialises (shadow_compiled).
  S2  policy 1 and policy 0: median relative difference of the shader's factor against the CPU twin at most 0.05 (the old parity tolerance of the T04 scene, R12) in all cases.
  S3  layer_steps: policy 1: 60 at reach 40, 64 at reach 180; policy 0: 12.
  S4  the pictures keep the names shadow-e40-shq-*.
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
    # The analytic checks of the scene without the step comparison (unchanged scene: the shader as a whole against the analytic expectations).
    metrics += [Metric.given("classic." + name, value, limit, verdict) for name, (value, limit, verdict) in ctx.player_metrics("classic").items()]
    pics = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(pics, exist_ok=True)
    copied = 0
    for name in sorted(os.listdir(folder)):
        if name.endswith(".png") and "e40-shq" in name:
            shutil.copyfile(os.path.join(folder, name), os.path.join(pics, name))
            copied += 1
    print("pictures: %d copied to %s" % (copied, pics))
    return metrics


register_series("e40-shq", [Stage("shadow", ["-mode", "scene", "-scene", "cloud-shadow", "-shadowsteps", "1"], size=(1280, 720), proxy=False, timeout=1800),
                             Stage("classic", ["-mode", "scene", "-scene", "cloud-shadow"], size=(1280, 720), proxy=False, timeout=1800)],
                collect, exclusive=False, who="e40-shq")
