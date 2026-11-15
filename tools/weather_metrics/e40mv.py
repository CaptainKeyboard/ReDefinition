"""E40 MVCLOUD: the cloud pass's motion vectors for the upscalers and frame generation (player series e40-mvcloud).

    python tools/build_weather_player.py --series e40-mvcloud [--no-build]

The inputs are e40gc.py's (the four-layer stack of the game, views l4ground and l4above, plus the one-layer views ground and above). The scene (MvCloudScene.cs, -scene mv-cloud)
draws the pass as the game runs it and, for the camera flying sideways at 300 and 2400 m/s and turning at 30 degrees a second:
  - reads the shader's motion vector at the cloud pixels (alpha above 0.5) and compares it with the CPU model (LayerMotionModel, the coverage maps of the job read on the CPU);
    err_mean is the mean distance in render pixels, firstshell_err_mean what the first layer's middle sphere for every pixel would be off by (the history and the vector before
    the package), hostvector_err_mean what the host's own sky vector leaves while the camera translates (zero vector, the whole expected vector is the error);
  - measures the GPU milliseconds of the march (first) and the reconstruction (second) with the layers' places and with the first layer's sphere, and of the motion pass (first).
Pass criterion of the package: err_mean below 0.3 px with the layers' places (player judged, the rows named err_mean_limit).
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40gc as gc  # noqa: E402

RESULTS = os.path.join(gc.BUILD, "e40mv-results.json")
VIEWS = "l4ground,l4above,ground,above"


def collect(ctx):
    values = ctx.player_metrics("mv-cloud")
    metrics = [Metric.given(name, value, limit, verdict) for name, (value, limit, verdict) in sorted(values.items())]
    lines = ["E40-MVCLOUD the cloud's motion vector against the model, render pixels at 1707 x 960 (mean | 95th percentile | max over the cloud pixels of 3 frames)"]
    for view in VIEWS.split(","):
        for sc in ("fly300", "fly2400", "turn"):
            for kind, label in ((".layers", "layers"), (".shell1", "shell1")):
                key = "mvcloud.%s.%s%s" % (view, sc, kind)
                if key + ".err_mean" not in values:
                    continue
                v = lambda part: values.get(key + "." + part, (float("nan"),))[0]
                lines.append("   %-9s %-8s %-6s px %5d  err %.3f | %.3f | %.3f   first-shell err %.3f   host vector err %.3f" % (
                    view, sc, label, v("cloud_px"), v("err_mean"), v("err_p95"), v("err_max"), v("firstshell_err_mean"), v("hostvector_err_mean")))
    lines.append("E40-MVCLOUD GPU ms, median of 40 frames in two rounds of opposite order (layers / shell1: first = march, second = reconstruction; motion: first = the pass)")
    for name in sorted(set(k.rsplit(".", 1)[0] for k in values if k.startswith("mvcloud.cost."))):
        lines.append("   %-34s %.3f / %.3f" % (name[len("mvcloud.cost."):], values.get(name + ".first_ms", (float("nan"),))[0], values.get(name + ".second_ms", (float("nan"),))[0]))
    print("\n".join(lines))
    os.makedirs(os.path.dirname(RESULTS), exist_ok=True)
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(dict(lines=lines, values={k: v[0] for k, v in values.items()}), f, indent=1)
    return metrics


register_series("e40-mvcloud", [Stage("mv-cloud", ["-mode", "scene", "-scene", "mv-cloud", "-in", gc.INPUT, "-views", VIEWS], size=(1707, 960), proxy=True, timeout=2400)],
                collect, exclusive=True, who="e40-mv")
