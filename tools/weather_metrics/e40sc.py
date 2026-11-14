"""E40 sun colour of the clouds: the sun at the cloud's altitude instead of the sun above the camera (player series e40-sc).

    python tools/build_weather_player.py --series e40-sc        (the inputs are e40gn's: python -c "from weather_metrics import e40gn; e40gn.write_inputs()" from tools/)

The scene (SunColourScene of tests/WeatherPlayer/Assets/Runner/SunColourScene.cs) draws the soft cloud of e40gn's one-layer scene from the ground (264 m) and from above the layer
(8364 m) with two sun colours, each converged:

  old   the sun colour Scatterer hands over (the white sun 2.35 through the air above the CAMERA, Kerbin's air, sun 45.6 degrees up), no air in the shader
  new   the pass's colour (CloudSunAir: the camera's air divided out, the colour capped at the white sun) and the shader's air above each sample (two components)

Pictures (into .local/work/series-pictures/E40): e40-sc-<view>-<old|new>.png (display values over the job's backdrop), e40-sc-<view>-pair.png (old | new side by side) and the rows
of the deck's median colour (blue over red) under the two, so that the claim "a low camera no longer reddens every cloud and a high camera no longer whitens a low deck" has numbers.
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402

PREFIX = "e40-sc"
VIEWS = ("ground", "above")
VARIANTS = ("old", "new")


def deck_colour(m, view):
    """Median display colour of the pixels the cloud covers by more than half (alpha), and its blue over red."""
    a = m[..., 3]
    rgb = gn.full_display(m, view)
    mask = a > 0.5
    if mask.sum() < 200:
        return None
    med = np.median(rgb[mask], axis=0)
    return med, float(med[2] / max(med[0], 1e-6)), float(mask.mean())


def collect(ctx):
    folder = ctx.folder("colour")
    out_dir = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines, results = [], ["E40-SC: the cloud's colour with the sun as the game gave it (old) and as the pass gives it now (new), converged, display values"], {}
    for view in VIEWS:
        row = {}
        pics = []
        for variant in VARIANTS:
            path = os.path.join(folder, "sc-%s-%s.npy" % (view, variant))
            if not os.path.isfile(path):
                lines.append("   %-6s %-4s: no picture" % (view, variant))
                continue
            m = gn.full_frame(folder, "sc-%s-%s" % (view, variant))
            rgb = gn.full_display(m, view)
            gn.save_png(os.path.join(out_dir, "%s-%s-%s.png" % (PREFIX, view, variant)), rgb)
            pics.append(rgb)
            got = deck_colour(m, view)
            if got is None:
                lines.append("   %-6s %-4s: no cloud pixels" % (view, variant))
                continue
            med, br, share = got
            row[variant] = dict(median=[float(x) for x in med], blue_over_red=br, share=share)
            metrics.append(Metric.info("e40sc.%s.%s.blue_over_red" % (view, variant), br))
            lines.append("   %-6s %-4s: deck median %.3f %.3f %.3f, blue over red %.3f, cloud share %.3f" % ((view, variant) + tuple(med) + (br, share)))
        if len(pics) == 2:
            gn.save_png(os.path.join(out_dir, "%s-%s-pair.png" % (PREFIX, view)), np.concatenate(pics, axis=1))
        results[view] = row
    values = ctx.player_metrics("colour")
    for view in VIEWS:
        for variant in VARIANTS:
            rgb = [values.get("suncolour.%s.%s.%s" % (view, variant, c), (float("nan"),))[0] for c in "rgb"]
            lines.append("   %-6s %-4s: the pass's sun colour %.3f %.3f %.3f (blue over red %.3f)" % ((view, variant) + tuple(rgb) + (rgb[2] / rgb[0] if rgb[0] else float("nan"),)))
            results.setdefault(view, {}).setdefault(variant, {})["sun"] = rgb
    with open(os.path.join(gn.BUILD, "e40sc-results.json"), "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


register_series("e40-sc", [Stage("colour", ["-mode", "scene", "-scene", "sun-colour", "-in", gn.INPUT], size=(1280, 720), proxy=False, timeout=1800)], collect,
                exclusive=False, who="e40-sc")
