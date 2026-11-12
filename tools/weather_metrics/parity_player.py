"""The series of the parity harness' player scene (package T11), run through tools/build_weather_player.py --series T11-scene:

  T11-scene   the scene 'parity': the seven synthetic scenes of tests/data/parity/scenes.json drawn by the GPU reference path (single
              scattering) and compared with the offline renderer (parity_ref.py, order 1) on the same density field: the optical depth
              and the radiance per pixel, the two files of arrays per view, the natural and aid pictures.

The offline renderer is the reference; the GPU path is the player's copy of it, so the comparison is A/B on one density field. Limits are
the rows of tests/series/rows/T11.json (series 'player-T11'). The pictures are in the player's output folder, <id>.png (natural) and
<id>-aid.png (false-colour optical depth, step-count heat map).
"""
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
SCENES = os.path.join(REPO, "tests", "data", "parity", "scenes.json")
ROWS = os.path.join(REPO, "tests", "series", "rows", "T11.json")


def load_scenes(path=SCENES):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def field_of(view, doc):
    """The offline renderer's density field of one view of scenes.json."""
    flat = view["flat"]
    radius = pr.FLAT_RADIUS if flat else doc["planetRadius"]
    layers = []
    for i, (base, top, sigma, mode) in enumerate(view["layers"]):
        if mode == 1:
            centre, rh, rv = view["cell"]
            cell = pr.scene_single_cloud(radius_m=rh, centre_alt=centre, sigma=sigma, planet=radius).layers[0]
            layers.append(cell)
        elif mode == 2:
            edge = view["cell"][0]
            layers.append(pr.Layer("strip", base, top, sigma, coverage=lambda u, e=edge: (u[:, 0] * radius < e).astype(np.float64)))
        else:
            layers.append(pr.Layer("layer%d" % i, base, top, sigma))
    return pr.DensityField(radius, layers)


def render_view(view, doc, view_steps=None, light_steps=None, order=1):
    """The offline renderer's image of one view (order 1 = the single scattering the GPU path draws)."""
    field = field_of(view, doc)
    # A flat view has its ground at z = 0; the offline renderer's flat planet is a sphere of radius FLAT_RADIUS, so z is lifted by it.
    lift = pr.FLAT_RADIUS if view["flat"] else 0.0
    pos = [view["position"][0], view["position"][1], view["position"][2] + lift]
    cam = pr.Camera(pos, 0.0, view["pitch"], doc["width"], doc["height"], view["fov"])
    sun = pr.Sun.at(cam.pos, view["sunAzimuth"], view["sunElevation"])
    ph = doc["phase"]
    env = doc["environment"]
    field.ground_albedo = env["groundAlbedo"]
    renderer = pr.Renderer(field, sun, env=pr.Environment(env["zenith"], env["horizon"], env["ground"]),
                           phase=pr.Phase(ph["g1"], ph["g2"], ph["w1"]), view_steps=view_steps or doc["viewSteps"],
                           light_steps=light_steps or doc["lightSteps"], order=order, sun_disc=1, albedo=ph["albedo"])
    occluders = [((v[0], v[1], v[2] + lift), v[3]) for v in [view["occluder"]]] if "occluder" in view else None
    return renderer.render(cam, occluders=occluders)


def thresholds():
    with open(ROWS, encoding="utf-8") as f:
        rows = json.load(f)["rows"]
    return {r["metric"]: r for r in rows if r.get("series") == "player-T11"}


def compare(folder, view, doc):
    """{metric: value} of the GPU path against the offline renderer for one view."""
    ref = render_view(view, doc)
    tau_g = np.load(os.path.join(folder, view["id"] + "-tau.npy")).astype(np.float64)
    lum_g = np.load(os.path.join(folder, view["id"] + "-lum.npy")).astype(np.float64)
    cloud_g = np.load(os.path.join(folder, view["id"] + "-cloud.npy")).astype(np.float64)
    out = {}
    m = ref.tau > 0.05
    out["tau_rel_error"] = float(np.mean(np.abs(tau_g[m] - ref.tau[m]) / ref.tau[m])) if m.any() else float("nan")
    out["tau_max_abs_error"] = float(np.max(np.abs(tau_g - ref.tau)))
    out["lum_rel_error"] = float(np.mean(np.abs(lum_g - ref.lum) / np.maximum(ref.lum, 1e-3)))
    mc = ref.cloud > 1e-4
    out["cloud_rel_error"] = float(np.mean(np.abs(cloud_g[mc] - ref.cloud[mc]) / ref.cloud[mc])) if mc.any() else float("nan")
    out["covered"] = float(m.mean())
    return out


def collect(ctx):
    doc = load_scenes()
    rows = thresholds()
    out = list(core.player_judged("scene")(ctx))
    folder = ctx.folder("scene")
    for view in doc["views"]:
        if not os.path.isfile(os.path.join(folder, view["id"] + "-tau.npy")):
            out.append(Metric.flag("%s.written" % view["id"], False))
            continue
        for name, value in compare(folder, view, doc).items():
            key = "%s.%s" % (view["id"], name)
            row = rows.get(name)
            # The GPU path lights layer 0 only, so the two-slab view is judged by its optical depth alone; the plane-terrain view
            # has no cloud on any ray (the camera is under the layer, looking down), so only its radiance (the strip's shadow) is judged.
            skipped = (view["scene"] == "two-slab" and name in ("lum_rel_error", "cloud_rel_error")) or \
                      (view["scene"] == "plane-terrain" and name in ("tau_rel_error", "cloud_rel_error"))
            # A figure over an empty set (no pixel with cloud light, as under a thick deck lit from above) is reported, not judged.
            empty = isinstance(value, float) and math.isnan(value) and name in ("tau_rel_error", "cloud_rel_error")
            if name == "covered" or row is None or skipped or empty:
                out.append(Metric.info(key, value))
            elif "max" in row:
                out.append(Metric.at_most(key, value, row["max"]))
            else:
                out.append(Metric.at_least(key, value, row["min"]))
    return out


register_series("T11-scene", [Stage("scene", ["-mode", "scene", "-scene", "parity"], size=(1280, 720), proxy=False)], collect, exclusive=False, who="T11")
