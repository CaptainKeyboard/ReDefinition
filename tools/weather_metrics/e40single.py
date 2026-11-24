"""CLOUDR2 single-cloud bench: ONE isolated cumulus over the sea, to be judged next to a photograph of one cumulus (series e40-single; player only, nothing in the shipped pass).

    python tools/weather_metrics/e40single.py --prepare [--source <e40pp-in folder>] [--map-width 4096]
    python tools/build_weather_player.py --series e40-single --no-build
    python tools/weather_metrics/e40single.py --pictures <folder> [--root <player output folder>] [--photo <the reference photograph>]

The job is the cumulus layer of the game-start-64 view `tyhumg` (its type table, light, aerial perspective and march are the product's) with everything else removed: the other layers are dropped, there is
no height variation (the flat base the photograph has), the cover map is one soft disc of the cell's diameter and the type map is one constant (mediocris 128, congestus 191 over the
layer's five types). The map is finer than the game's (4096 x 2048: a texel of 0.9 km; the game's 512 x 256 texel is 7.4 km, wider than the cell). The layer's coverage threshold is 0.5
(0.35 in the product) so that the disc's core is solid; the edge of the disc is ragged by the shape noise. The camera stands 30 m over the sea at the stated distance behind the cell, looking at
it along the ground, pitched so that the horizon lies at 96.5 % of the frame's height as in the photograph, with the vertical field of view of `FOV`; the sun stands behind the camera, 45 degrees to its left (135 degrees from the view direction), at an
elevation of 25 degrees (front light, as in the photograph); the cells ending in b have the sun ahead to the left (45 degrees from the view direction: back light). Below the horizon the picture holds a plain sea, above it a plain blue gradient: only the cloud is rendered.

Several cells (seeds of the shape noise) and distances are rendered, and all are reported: none is picked for its look. The pictures are the frames at their native size and, next to the
photograph, 900 x 600 windows at 1:1 of the heads (the top of the cloud), the base and the rim.
"""
import argparse
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40pp  # noqa: E402
from weather_metrics import e40puff as puff  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import e40v1  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402

BUILD = e40pp.BUILD
INPUT = os.path.join(BUILD, "e40single-in")
MAIN = e40pp.MAIN
SOURCE_JOB = "gn-pp-tyhumg-prod-f0"
SOURCE_FALLBACK = os.path.join(MAIN, ".local", "work", "PUFF", "build", "weather-series", "e40pp-in")
PHOTO = os.path.join(MAIN, ".local", "work", "real-clouds", "owner-ref", "single-cumulus-side-sea.jpg")
SIZE = puff.SIZE

FOV = 42.0               # the vertical field of view of the camera, degrees
CAMERA_ALT = 30.0        # m over the sea
HORIZON_AT = 0.965       # the horizon's height in the frame, from the top, as in the photograph
SUN_AZIMUTH = 135.0      # degrees to the left of the view direction, measured from the direction towards the cloud: 135 = behind the camera to the left (front light, the photograph); 45 = ahead to the left (back light)
SUN_ELEVATION = 25.0
COVERAGE = 0.5
CUMULUS_BASE = 600.0     # m over the sea (1700 in the product's layer); bench only
MAP_W, MAP_H = 4096, 2048
TYPE_BYTE = {"m": 128, "c": 191}   # the type axis over the cumulus layer's five types: 0.5 = mediocris, 0.75 = congestus

# scene -> (type, seed, distance km, diameter km)
SCENES = {
    "m1d6": ("m", 1, 6.0, 5.5), "m2d6": ("m", 2, 6.0, 5.5), "m3d6": ("m", 3, 6.0, 5.5),
    "m1d54": ("m", 1, 5.4, 5.5), "m1d8": ("m", 1, 8.0, 5.5),
    "c1d11": ("c", 1, 11.0, 6.0), "c2d11": ("c", 2, 11.0, 6.0), "c3d11": ("c", 3, 11.0, 6.0),
    # the same cells with the sun ahead to the left (back light), for the comparison with the front light
    "m1d6b": ("m", 1, 6.0, 5.5, 45.0), "m2d6b": ("m", 2, 6.0, 5.5, 45.0),
}
CEILING_SCENES = ("m1d6", "m2d6", "m3d6", "c1d11", "m1d6b")

# look -> (change of the job or None, march). New packages add their looks here.
CUMULIFORM = puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35)
LOOKS = {
    "base": (None, e40pp.MARCH),
    "cumuliform": (CUMULIFORM, puff.NG96),
    "ceilb": (puff.v_nosmooth, puff.CEIL),
    "ceil": (puff.compose(puff.v_nosmooth, CUMULIFORM), puff.CEIL),
    # the artifact diagnosis (bright ellipses, holes): one factor of the cumuliform look taken away at a time, at the production march with 96 grid points
    "h_nocav": (puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE), puff.NG96),
    "h_noedge": (puff.tune_cumulus(puff.CUMULIFORM1 + puff.CAV35), puff.NG96),
    "h_w0": (puff.compose(CUMULIFORM, lambda job: job.setdefault("noise", {}).__setitem__("noUntile", 1)), puff.NG96),
    "h_wL": (puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.cumuliform.warp", -1.0)]), puff.NG96),
    # CLOUDR2 P2: the GPU tile blend (cloud.noise.blend) instead of the untiling displacement, on the cumuliform look and on the shape volume alone
    "blend": (puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0)]), puff.NG96),
    "blendb": (puff.tune_cumulus([("cloud.noise.blend", 1.0)]), e40pp.MARCH),
    "blendc": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0)])), puff.CEIL),
    # CLOUDR2 P3: the sun march's first taps read the eroded density (cloud.light.erosion), on the blended cumuliform look
    "lite1": (puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0), ("cloud.light.erosion", 1.0)]), puff.NG96),
    "lite2": (puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0), ("cloud.light.erosion", 2.0)]), puff.NG96),
    "lite3": (puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0), ("cloud.light.erosion", 3.0)]), puff.NG96),
    # CLOUDR2 shapes: the types' shape smoothing (1.5 noise scales for mediocris = 375 m, 3 for congestus) is a mip level of the volume, so it removes every head below it; the cumuliform types would
    # not carry it. The same looks with the smoothing 0 at the production march.
    "bns": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0)])), puff.NG96),
    # CLOUDR3 task 4 (congestus towers): the cavity strength and the rim exponent of the cumulus layer around the bench's (cavity 0.35, edgePower 0.5), on the blended cumuliform look without smoothing
    "cg_c0": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + [("cloud.noise.blend", 1.0), ("cloud.cavity.layer.", 0.0)])), puff.NG96),
    "cg_c15": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + [("cloud.noise.blend", 1.0), ("cloud.cavity.layer.", 0.15)])), puff.NG96),
    "cg_c70": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + [("cloud.noise.blend", 1.0), ("cloud.cavity.layer.", 0.7)])), puff.NG96),
    "cg_e10": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + [("cloud.edgePower.layer.", 1.0)] + puff.CAV35 + [("cloud.noise.blend", 1.0)])), puff.NG96),
    "cg_e20": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + [("cloud.edgePower.layer.", 2.0)] + puff.CAV35 + [("cloud.noise.blend", 1.0)])), puff.NG96),
    # CLOUDR3 task 3 (warm light): the same look without the pass's aerial perspective (job far.aerialPerspective 0), to read the colour of the sun's light on the heads without the blue air between the camera and the cloud
    "bnh": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0)]), lambda job: job.setdefault("far", {}).__setitem__("aerialPerspective", 0)), puff.NG96),
    "lns": (puff.compose(puff.v_nosmooth, puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0), ("cloud.light.erosion", 2.0)])), puff.NG96),
    "h_nolod": (puff.compose(CUMULIFORM, lambda job: job.setdefault("noise", {}).__setitem__("noLod", 1)), puff.NG96),
    "h_ero0": (puff.compose(CUMULIFORM, puff.v_ero0), puff.NG96),
}
LOOK_SCENES = {"bnh": ("m1d6", "c1d11"), "cg_c0": ("c1d11", "c2d11"), "cg_c15": ("c1d11", "c2d11"), "cg_c70": ("c1d11", "c2d11"), "cg_e10": ("c1d11", "c2d11"), "cg_e20": ("c1d11", "c2d11"),"bns":("m1d6", "m2d6", "m3d6", "c1d11", "c2d11"), "lns": ("m1d6", "m2d6", "m3d6", "c1d11", "c2d11"),"lite1": ("m1d6", "m2d6", "m3d6", "c1d11"), "lite2": ("m1d6", "m2d6", "m3d6", "c1d11"), "lite3": ("m1d6", "m2d6", "m3d6", "c1d11"),"blendb": ("m1d6", "m2d6", "m3d6"), "blendc": ("m1d6", "m2d6", "m3d6", "c1d11"),"h_nocav": ("m1d6", "m2d6"), "h_noedge": ("m1d6", "m2d6"), "h_w0": ("m1d6", "m2d6"), "h_wL": ("m1d6", "m2d6"), "h_nolod": ("m1d6", "m2d6"), "h_ero0": ("m1d6", "m2d6")}


# CLOUDR3 task 2: the real-time options on the blended cumuliform look (cavity 0.35, edgePower 0.5): the march target's size over the frame (r33 .. r100, the reconstruction of the product), the grid
# points of a span (ng48 is the product's, ng96, ng128), the Catmull-Rom history (chm) and the types' shape smoothing (sm: today's, the others none). One march string per option, so one player
# start per option. The field view (the game-start view tyhuma with all its layers, the soft-cost scene) is added by prepare() for the same options.
def rt_option(res, grid, chm=False, smooth=False):
    cumuliform = puff.tune_cumulus(puff.CUMULIFORM1 + puff.EDGE + puff.CAV35 + [("cloud.noise.blend", 1.0)])
    march = "seq1+lq2+ng%d+r%d+lp100%s" % (grid, res, "+chm" if chm else "")
    return (cumuliform if smooth else puff.compose(puff.v_nosmooth, cumuliform)), march


RT_OPTIONS = {
    "rt_r33g48": rt_option(33, 48), "rt_r33g96": rt_option(33, 96), "rt_r33g128": rt_option(33, 128),
    "rt_r50g96": rt_option(50, 96), "rt_r66g96": rt_option(66, 96), "rt_r100g96": rt_option(100, 96),
    "rt_r50g128": rt_option(50, 128), "rt_r33g96c": rt_option(33, 96, chm=True), "rt_r50g96c": rt_option(50, 96, chm=True),
    "rt_r33g96s": rt_option(33, 96, smooth=True), "rt_r50g96s": rt_option(50, 96, smooth=True),
}
LOOKS.update(RT_OPTIONS)
LOOK_SCENES.update({k: ("m1d6", "c1d11") for k in RT_OPTIONS})


def unit(v):
    v = np.asarray(v, dtype=np.float64)
    return v / np.linalg.norm(v)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def disc_map(path, diameter_km, centre_map, radius_m):
    """The cover map: 1 in the core, a smooth fall to 0 at the disc's edge (raw R8, row 0 north, column 0 at longitude -180), as a function of the great-circle distance to the cell's centre."""
    h, w = MAP_H, MAP_W
    lon = np.radians(-180.0 + (np.arange(w) + 0.5) * 360.0 / w)[None, :]
    out = np.zeros((h, w), dtype=np.uint8)
    for r0 in range(0, h, 128):
        rows = np.arange(r0, min(h, r0 + 128))
        lat = np.radians(90.0 - (rows + 0.5) * 180.0 / h)[:, None]
        v = np.stack([np.cos(lat) * np.cos(lon), np.cos(lat) * np.sin(lon), np.sin(lat) + 0 * lon], axis=-1)
        arc = np.arccos(np.clip(v @ centre_map, -1.0, 1.0)) * radius_m
        cov = 1.0 - smoothstep(0.45 * diameter_km * 500.0, diameter_km * 500.0, arc)
        out[rows] = np.rint(cov * 255.0).astype(np.uint8)
    out.tofile(path)


def source_folder(arg):
    for p in (arg, e40pp.INPUT, SOURCE_FALLBACK):
        if p and os.path.isfile(os.path.join(p, "jobs.json")):
            return p
    raise SystemExit("no e40pp inputs found (run e40pp.py --prepare, or pass --source)")


def camera_for(base, distance_km):
    """Position, forward, up, right of the camera at `distance_km` behind the cell along the heading, and the unit vectors of the cell's place (up, heading, left)."""
    r = float(base["planetRadius"])
    c0 = unit(base["position"])
    f0 = np.asarray(base["forward"], dtype=np.float64)
    heading = unit(f0 - c0 * float(f0 @ c0))
    theta = distance_km * 1000.0 / r
    pos_dir = np.cos(theta) * c0 - np.sin(theta) * heading
    tangent = np.sin(theta) * c0 + np.cos(theta) * heading
    up_local = pos_dir
    dip = np.arccos(r / (r + CAMERA_ALT))
    pitch = -dip + np.arctan((2.0 * HORIZON_AT - 1.0) * np.tan(np.radians(FOV) / 2.0))
    forward = np.cos(pitch) * tangent + np.sin(pitch) * up_local
    up = -np.sin(pitch) * tangent + np.cos(pitch) * up_local
    right = unit(np.cross(up, forward))
    return (pos_dir * (r + CAMERA_ALT), forward, up, right), (c0, heading, np.cross(heading, c0))


def sun_vector(c0, heading, left, azimuth=None):
    az, el = np.radians(SUN_AZIMUTH if azimuth is None else azimuth), np.radians(SUN_ELEVATION)
    return np.cos(el) * (np.cos(az) * heading + np.sin(az) * left) + np.sin(el) * c0


def prepare(source=None, map_width=MAP_W):
    global MAP_W, MAP_H
    MAP_W, MAP_H = map_width, map_width // 2
    src = source_folder(source)
    with open(os.path.join(src, "jobs.json"), encoding="utf-8") as f:
        base = next(j for j in json.load(f)["jobs"] if j["id"] == SOURCE_JOB)
    with open(os.path.join(src, "stages.json"), encoding="utf-8") as f:
        base_stage = json.load(f)["tyhumg"]
    os.makedirs(INPUT, exist_ok=True)
    r = float(base["planetRadius"])
    m = np.asarray(base["worldToMap"], dtype=np.float64).reshape(3, 3)
    (_, _, _, _), (c0, heading, left) = camera_for(base, 6.0)
    centre_map = unit(m @ c0)   # the shader's q = mul(_WorldToMap, pc)
    for d in sorted({s[3] for s in SCENES.values()}):
        disc_map(os.path.join(INPUT, "single-cover-d%g.r8" % d), d, centre_map, r)
    for k, b in TYPE_BYTE.items():
        np.full(MAP_W * MAP_H, b, dtype=np.uint8).tofile(os.path.join(INPUT, "single-type-%s.r8" % k))
    cumulus = next(l for l in base["layers"] if l["name"].endswith("cumulus"))
    tint = [float(x) for x in e40v1.tint_of(dict(elev=SUN_ELEVATION))]
    jobs, specs, stages = [], [], {}
    for scene, (typ, seed, dist, diam, *az) in SCENES.items():
        cam, (c0, heading, left) = camera_for(base, dist)
        sun = sun_vector(c0, heading, left, az[0] if az else None)
        for look, (change, march) in LOOKS.items():
            if look.startswith("ceil") and scene not in CEILING_SCENES:
                continue
            if look in LOOK_SCENES and scene not in LOOK_SCENES[look]:
                continue
            name = "s_%s_%s" % (scene, look)
            tag = e40pp.view_tag(name)
            job = json.loads(json.dumps(base))
            job["id"] = "gn-%s-prod-f0" % tag
            job["width"], job["height"] = SIZE
            job["mapWidth"], job["mapHeight"] = MAP_W, MAP_H
            layer = json.loads(json.dumps(cumulus))
            layer["coverage"] = COVERAGE
            # the bench's base: the layer lowered as a whole (its thickness stays) so that the cloud's base stands just above the horizon as in the photograph
            layer["outer"] = CUMULUS_BASE + (layer["outer"] - layer["inner"])
            layer["inner"] = CUMULUS_BASE
            layer["coverageMap"] = "single-cover-d%g.r8" % diam
            layer["typeMap"] = "single-type-%s.r8" % typ
            for key in ("heightMap", "heightWidth", "heightHeight", "heightRoot", "heightTop", "heightVariation", "heightSeed"):
                layer.pop(key, None)
            job["layers"] = [layer]
            job["position"], job["forward"], job["up"], job["right"] = (list(map(float, v)) for v in cam)
            job["fovV"] = FOV
            job["sun"] = list(map(float, sun))
            job["noise"] = dict(job.get("noise", {}), seed=seed)
            if change:
                change(job)
            jobs.append(job)
            stages[name] = dict(white=base_stage["white"], tint=tint, gamma=base_stage["gamma"], exposure=base_stage.get("exposure", 1.0), view=scene, variant=look, march=march,
                                scene=scene, type=typ, seed=seed, distance=dist, diameter=diam)
            print("%s: %s seed %d at %.1f km, cell %.1f km, look %s" % (name, "mediocris" if typ == "m" else "congestus", seed, dist, diam, look))
    # CLOUDR3 task 2: the field view (the game-start view with all its layers, its maps copied from the source) in the real-time options, for the cost series (soft-cost) only
    import shutil
    with open(os.path.join(src, "heights.json"), encoding="utf-8") as f:
        field_spec = next((s for s in json.load(f)["specs"] if s["id"] == SOURCE_JOB), None)
    prefix = e40pp.view_tag("tyhumg")
    for fn in os.listdir(src):
        if fn.startswith(prefix) and not fn.endswith(".json"):
            shutil.copyfile(os.path.join(src, fn), os.path.join(INPUT, fn))
    for look in RT_OPTIONS:
        change, march = LOOKS[look]
        name = "s_field_%s" % look
        job = json.loads(json.dumps(base))
        job["id"] = "gn-%s-prod-f0" % e40pp.view_tag(name)
        job["width"], job["height"] = SIZE
        change(job)
        jobs.append(job)
        if field_spec is not None:
            specs.append(dict(field_spec, id=job["id"]))
        stages[name] = dict(white=base_stage["white"], tint=tint, gamma=base_stage["gamma"], exposure=base_stage.get("exposure", 1.0), view="field", variant=look, march=march,
                            scene="field", type="m", seed=1, distance=0.0, diameter=0.0, field=True)
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)
    with open(os.path.join(INPUT, "stages.json"), "w", encoding="utf-8") as f:
        json.dump(stages, f, indent=1)


def prepared():
    path = os.path.join(INPUT, "stages.json")
    if not os.path.isfile(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def label_of(march):
    import zlib
    return "single-m%08x" % (zlib.crc32(march.encode("ascii")) & 0xFFFFFFFF) + puff.SUFFIX


def stage_list():
    by_march = {}
    only = [s for s in os.environ.get("ATMO_SINGLE_LOOKS", "").split(",") if s]   # e.g. h_nocav,h_w0: render only these looks
    for name, st in prepared().items():
        if (only and st["variant"] not in only) or st.get("field"):
            continue
        by_march.setdefault(st["march"], []).append(name)
    out = []
    for march, names in sorted(by_march.items()):
        views = ",".join(e40pp.view_tag(n) for n in names)
        out.append(Stage(label_of(march), ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", views, "-media", "pr50", "-marches", march] + puff.light_args(),
                         size=SIZE, proxy=False, timeout=3400 + (3000 if SIZE != (puff.REF_W, puff.REF_H) else 0)))
    return out


# ---------------------------------------------------------------------------------------------------------------------------------
# Pictures
# ---------------------------------------------------------------------------------------------------------------------------------
def backdrop(job, w, h):
    """The plain sky above and the plain sea below the horizon, as seen by the job's camera."""
    r = float(job["planetRadius"])
    pos = np.asarray(job["position"], dtype=np.float64)
    f, u, rt = (np.asarray(job[k], dtype=np.float64) for k in ("forward", "up", "right"))
    t = np.tan(np.radians(job["fovV"]) / 2.0)
    ys = (1.0 - 2.0 * (np.arange(h) + 0.5) / h) * t
    xs = (2.0 * (np.arange(w) + 0.5) / w - 1.0) * t * w / h
    d = f[None, None, :] + xs[None, :, None] * rt[None, None, :] + ys[:, None, None] * u[None, None, :]
    d /= np.linalg.norm(d, axis=-1, keepdims=True)
    up_local = pos / np.linalg.norm(pos)
    dip = np.arccos(r / np.linalg.norm(pos))
    elev = np.degrees(np.arcsin(np.clip(d @ up_local, -1.0, 1.0)) + dip)
    k = np.clip(elev / 45.0, 0.0, 1.0)[..., None] ** 0.7
    sky = (1.0 - k) * np.array([0.22, 0.37, 0.62]) + k * np.array([0.05, 0.17, 0.50])
    sea = np.array([0.04, 0.10, 0.18]) * (1.0 + 0.5 * np.clip(-elev / 8.0, 0.0, 1.0))[..., None]
    return np.where((elev >= 0.0)[..., None], sky, sea)


CROP_W, CROP_H = puff.CROP_W, puff.CROP_H


def windows(alpha):
    """The three native windows on the cloud of a frame: 'heads' (centred on the upper edge's densest part), 'base' (the lower edge) and 'rim' (the right edge)."""
    a = np.asarray(alpha, dtype=np.float32) > 0.3
    h, w = a.shape
    if not a.any():
        return {k: (0, 0) for k in ("heads", "base", "rim")}
    ys, xs = np.nonzero(a)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    cx = int(np.median(xs[ys < y0 + (y1 - y0) * 0.35])) if (ys < y0 + (y1 - y0) * 0.35).any() else (x0 + x1) // 2

    def clamp(x, y):
        return (int(min(max(x, 0), w - CROP_W)), int(min(max(y, 0), h - CROP_H)))
    return {"heads": clamp(cx - CROP_W // 2, y0 - 40), "base": clamp((x0 + x1) // 2 - CROP_W // 2, y1 - CROP_H + 60), "rim": clamp(x1 - CROP_W + 60, (y0 + y1) // 2 - CROP_H // 2)}


def photo_windows(photo):
    """The photograph, scaled to the frame's width, and its three windows chosen like the frame's (its cloud is the pixels that are bright and not blue)."""
    from PIL import Image
    pic = Image.open(photo).convert("RGB")
    w, h = SIZE
    pic = pic.resize((w, int(round(pic.height * w / pic.width))), Image.LANCZOS)
    arr = np.asarray(pic, dtype=np.float64) / 255.0
    cloud = (arr.min(axis=-1) > 0.35).astype(np.float32)
    return arr, windows(cloud)


def write_all(out, photo=None):
    from PIL import Image
    folder_root = os.environ.get("ATMO_WP_ROOT") or e40pp.BUILD.replace("weather-series", "WeatherPlayer")
    stages = prepared()
    with open(os.path.join(INPUT, "jobs.json"), encoding="utf-8") as f:
        jobs = {j["id"]: j for j in json.load(f)["jobs"]}
    os.makedirs(os.path.join(out, "full"), exist_ok=True)
    os.makedirs(os.path.join(out, "crops"), exist_ok=True)
    photo_arr, photo_win = (None, None)
    if photo and os.path.isfile(photo):
        photo_arr, photo_win = photo_windows(photo)
        for k, (x, y) in photo_win.items():
            Image.fromarray((photo_arr[y:y + CROP_H, x:x + CROP_W] * 255).astype("uint8")).save(os.path.join(out, "crops", "photo-%s.png" % k))
    rows = []
    for name, st in sorted(stages.items()):
        if st.get("field"):
            continue
        folder =os.path.join(folder_root, "e40-single-" + label_of(st["march"]))
        case = e40pp.case_file(name, st["march"])
        path = os.path.join(folder, case + ".npy")
        if not os.path.isfile(path):
            print("missing frame: %s" % path)
            continue
        m = e40ty.load_lum(folder, case)
        job = jobs["gn-%s-prod-f0" % e40pp.view_tag(name)]
        h, w = m.shape[:2]
        # the stage's white is the display value of a sunlit white cloud in the game's frame (0.58): the game's own post stage lifts it; the bench divides it out so that a sunlit white reads as 1
        img = np.clip(e40pp.compose(m, backdrop(job, w, h) * st["white"], st) / st["white"], 0.0, 1.0)
        alpha = np.asarray(m[..., 3], dtype=np.float32)
        win = windows(alpha)
        pic = (np.clip(img, 0, 1) * 255).astype("uint8")
        Image.fromarray(pic).save(os.path.join(out, "full", "%s.png" % name))
        for k, (x, y) in win.items():
            Image.fromarray(pic[y:y + CROP_H, x:x + CROP_W]).save(os.path.join(out, "crops", "%s-%s.png" % (name, k)))
        a = alpha > 0.3
        ys, xs = np.nonzero(a)
        width = (xs.max() - xs.min() + 1) / float(w) if a.any() else 0.0
        height = (ys.max() - ys.min() + 1) / float(h) if a.any() else 0.0
        rows.append("%-18s width %.2f of the frame, height %.2f, top at %.2f, alpha mean in the cloud %.2f" % (name, width, height, ys.min() / float(h) if a.any() else 0.0,
                                                                                                         float(alpha[a].mean()) if a.any() else 0.0))
    print("\n".join(rows))
    return rows


def collect(ctx):
    out = os.path.join(e40pp.MAIN, ".local", "work", "series-pictures", "CLOUDR2", "single%s" % puff.SUFFIX)
    rows = write_all(out, PHOTO)
    return [Metric.info("e40single.cells", float(len(rows)))]


register_series("e40-single", stage_list(), collect, exclusive=False, who="e40-single")


# The GPU time of the pass for one cell in several looks (soft-cost scene: march | resolve | composite, median of 40 frames, proxy timestamps; a timing run takes the GPU exclusively).
COST_LOOKS = ("base", "cumuliform", "blend", "blendb", "lite2", "bns", "lns") + tuple(RT_OPTIONS)
COST_SCENES = ("m1d6", "c1d11", "field")


def cost_label(march):
    return "single-cost-" + label_of(march)[len("single-"):]


def cost_stage_list():
    by_march = {}
    for name, st in prepared().items():
        only = [s for s in os.environ.get("ATMO_SINGLE_LOOKS", "").split(",") if s]   # the same filter as the pictures
        if st["variant"] in COST_LOOKS and st["scene"] in COST_SCENES and (not only or st["variant"] in only):
            by_march.setdefault(st["march"], []).append(name)
    return [Stage(cost_label(march), ["-mode", "scene", "-scene", "soft-cost", "-in", INPUT, "-views", ",".join(e40pp.view_tag(n) for n in names), "-media", "pr50", "-marches", march,
                                      "-width", str(SIZE[0]), "-height", str(SIZE[1])], size=SIZE, proxy=True, timeout=2400)
            for march, names in sorted(by_march.items())]


def collect_cost(ctx):
    rows = {}
    for label in sorted({cost_label(st["march"]) for st in prepared().values()}):
        for k, v in ctx.player_metrics(label).items():
            if not k.startswith("softcost."):
                continue
            _, part, rest = k.split(".", 2)
            rows.setdefault(rest.split(".", 1)[0], {})[part] = v[0]
    metrics, lines = [], ["single cloud: GPU ms of the pass at %d x %d (march | resolve | composite | total)" % SIZE]
    for view in sorted(rows):
        r = rows[view]
        lines.append("   %-34s %7.3f | %7.3f | %7.3f | %7.3f" % (view, r.get("march", float("nan")), r.get("resolve", float("nan")), r.get("composite", float("nan")), r.get("total", float("nan"))))
        metrics.append(Metric.info("e40single.cost." + view, r.get("total", float("nan"))))
    print("\n".join(lines))
    return metrics


register_series("e40-single-cost", cost_stage_list(), collect_cost, exclusive=True, who="e40-single")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--prepare", action="store_true")
    p.add_argument("--source")
    p.add_argument("--map-width", type=int, default=MAP_W)
    p.add_argument("--pictures")
    p.add_argument("--root", help="the player's output folder, default build/WeatherPlayer")
    p.add_argument("--photo", default=PHOTO)
    a = p.parse_args()
    if a.prepare:
        prepare(a.source, a.map_width)
    if a.pictures:
        if a.root:
            os.environ["ATMO_WP_ROOT"] = a.root
        write_all(a.pictures, a.photo)
    return 0


if __name__ == "__main__":
    sys.exit(main())
