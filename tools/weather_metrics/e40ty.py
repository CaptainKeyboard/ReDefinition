"""E40 B3: the look of each cloud type in the weather player (no game): one sheet per type family (player series e40-ty).

    python tools/build_weather_player.py --series e40-ty
    python tools/build_weather_player.py --series e40-ty --no-build
    python tools/weather_metrics/e40ty.py --selftest

The scene is ONE layer of the shipped Clouds-Kerbin.cfg at a time (the cfg's numbers, read here, are what the job carries: the soft water content with the owner's softness 0.5, the
type's density scale and light response by height, the layer's light factors) with a constant coverage map and the type map set to the named type, drawn by the product's own layer pass
(the type-sheet scene, converged), with the sun as the product gives it: the white sun outside the air and Kerbin's air above each sample in the shader, the sun 45.6 degrees up.
Three views per type: from the ground (264 m, 25 degrees up), from above the layer, and looking at the sun. The sheet also shows the same sun view WITHOUT the layer (the sky behind: how
much sky and sun come through the cloud) and the optical depth of the ground view (false colour: blue thin, green about 1, red thick; grey: no cloud).

The display stage is the game's (CloudOutputStage: the stage white over the sun's luminance, gamma 2.2) with the white point following the sun at the clouds (E40 B3, cloud.whitePoint 1).
The sun's disc is drawn four times its real size so that it can be seen in a tile of this size.
"""
import argparse
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics.e40ci import acf_widths  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40ty-in")
INPUT_AUX = os.path.join(BUILD, "e40ty-aux-in")
RESULTS = os.path.join(BUILD, "e40ty-results.json")
R = 600000.0
MAP_W, MAP_H = 8, 4
SIZE = (1708, 960)
AUX_SIZE = (854, 480)
FOV = 60.0
SUN_ELEVATION = 45.6
SOFTNESS = 0.5
AMBIENT_TOP = [0.2357, 0.2432, 0.2470]
AMBIENT_BOTTOM = [0.1178, 0.1216, 0.1235]
WHITE = 0.7486          # the stage's white (the scene sun's display value; the pass's white sun 2.35 over pi)
GAMMA = 2.2
SITE_LON_DEG = 10.0
TILE = (512, 288)

# family, tile key, layer, type name, coverage map, view heights (ground, above), ground pitch, above pitch. The coverage map is the constant value of the whole sky.
GROUND, ABOVE_LAYER = 264.0, 8364.0
TYPES = [
    ("cumulus", "humilis", "Kerbin-cumulus", "Cumulus humilis", 0.45, (GROUND, ABOVE_LAYER), (25.0, -25.0)),
    ("cumulus", "congestus", "Kerbin-cumulus", "Cumulus congestus", 0.45, (GROUND, ABOVE_LAYER + 1500.0), (25.0, -25.0)),
    ("stratocumulus", "stratocumulus", "Kerbin-cumulus", "Stratocumulus", 0.6, (GROUND, ABOVE_LAYER), (25.0, -25.0)),
    ("stratocumulus", "altocumulus", "Kerbin-cumulus", "Altocumulus", 0.6, (GROUND, ABOVE_LAYER), (25.0, -25.0)),
    ("stratus", "stratus", "Kerbin-weather-rain", "Stratus", 0.8, (GROUND, ABOVE_LAYER), (25.0, -25.0)),
    ("stratus", "nimbostratus", "Kerbin-weather-rain", "Nimbostratus", 0.8, (GROUND, ABOVE_LAYER), (25.0, -25.0)),
    ("fog", "fog", "Kerbin-weather-rain", "Fog", 0.8, (20.0, ABOVE_LAYER), (4.0, -25.0)),
    # The cumulonimbus layer's clear-sky rule (K 0.687) lets a map value of 0.85 close the whole sky into one wall; 0.78 leaves gaps between the towers.
    ("cumulonimbus", "calvus", "Kerbin-cumulonimbus", "Cumulonimbus calvus", 0.78, (GROUND, 13500.0), (6.0, -30.0)),
    ("cumulonimbus", "incus", "Kerbin-cumulonimbus", "Cumulonimbus incus", 0.78, (GROUND, 13500.0), (6.0, -30.0)),
    # The same types where the map stands high, a strong cell: the height-shaped threshold (LIFT) is in map units, so the stronger the cell the higher the tower stands (E40 L5 shape).
    ("cumulonimbus", "calvus-strong", "Kerbin-cumulonimbus", "Cumulonimbus calvus", 0.9, (GROUND, 13500.0), (6.0, -30.0)),
    ("cumulonimbus", "incus-strong", "Kerbin-cumulonimbus", "Cumulonimbus incus", 0.9, (GROUND, 13500.0), (6.0, -30.0)),
    ("cirrus", "cirrus", "Kerbin-cirrus", "Cirrus", 0.65, (GROUND, 12500.0), (35.0, -35.0)),
    ("cirrus", "cirrostratus", "Kerbin-cirrus", "Cirrostratus", 0.8, (GROUND, 12500.0), (35.0, -35.0)),
]
# E40TY_ONLY=<family>[,<family>]: only these families of the table (a sweep of one family's keys without the others).
if os.environ.get("E40TY_ONLY"):
    TYPES = [t for t in TYPES if t[0] in os.environ["E40TY_ONLY"].split(",")]
# E40TY_MAP=<value>: the constant coverage map of every selected type (the sheet's 0.65 / 0.8 close most of the sky; a cirrus sky with gaps is a map value of 0.3 to 0.4).
if os.environ.get("E40TY_MAP"):
    TYPES = [t[:4] + (float(os.environ["E40TY_MAP"]),) + t[5:] for t in TYPES]
# The comparison tiles of True Volumetric Clouds (game frames of the K-T2 series, its own weather): the family they are put beside.
TVC_DIR = os.path.join("C:", os.sep, "Users", "franz", "IdeaProjects", "ReDefinition", ".local", "work", "series-pictures", "K-T2")
TVC = {"cumulus": "t09-tvc1707-ground-tvc-1707x960-natural.png", "stratocumulus": "t09-tvc1707-above-tvc-1707x960-natural.png"}
FAMILIES = ("cumulus", "stratocumulus", "stratus", "fog", "cumulonimbus", "cirrus")
VIEWS = ("ground", "above", "sun")

# E40TY_NOLIGHT=1: the same pictures with every light factor of the cfg at 1 (the control of the type and layer light response), under the names e40-ty-nolight-*.
NOLIGHT = os.environ.get("E40TY_NOLIGHT") == "1"
# E40TY_SHAPE=0: the same pictures without the types' height-shaped threshold and without the fibre stretch (the jobs carry neither; the key layers.profileShape that scaled the
# threshold is gone), under the names e40-ty-shape0-* (the control of the shape keys of E40 L2 D1).
SHAPE_SCALE = float(os.environ["E40TY_SHAPE"]) if os.environ.get("E40TY_SHAPE") else None
NOSHAPE = SHAPE_SCALE == 0.0
# E40TY_SPHERE=<scale>: the noise lattice's vertical scale (the body key sphereScale; 1: as today, below 1 the features are longer in the vertical), under the names e40-ty-sphere<scale>-*.
SPHERE = float(os.environ["E40TY_SPHERE"]) if os.environ.get("E40TY_SPHERE") else None
# E40TY_CFG=<path>: the layers and types are read from that cfg instead of the shipped one (the "before" of a cfg change: a copy of the cfg of the commit before), E40TY_WIND=0: no layer wind
# (the fibres lie along the pole, as before E40 L5), E40TY_TAG=<text>: a part of the picture names (e40-ty-<text>-...).
CFG_PATH = os.environ.get("E40TY_CFG") or e40o.CFG
NOWIND = os.environ.get("E40TY_WIND") == "0"
TAG = os.environ.get("E40TY_TAG")
NAME = ("e40-ty-nolight" if NOLIGHT else "e40-ty-shape%g" % SHAPE_SCALE if SHAPE_SCALE is not None else "e40-ty") + ("-sphere%g" % SPHERE if SPHERE is not None else "") + ("-" + TAG if TAG else "")

# The layer wind of a type's job in m/s as (east, north) (E40 L5 shape): the cirrus fibres lie along it. The sheet's cirrus is blown from the south-west, 25 m/s.
WIND_EN = {"cirrus": (17.7, 17.7), "cirrostratus": (17.7, 17.7)}

LAYER_EXTRA = ("softnessScale", "waterPower", "lightAmbient", "fibreStretch", "lightIsotropy", "verticalScale", "ambientMs", "ambientDecay", "lightPlateau", "shapeSmoothing", "fibreMinWindMs",
               "fibreTurnSeconds", "topReach", "topRefMs", "topFullMs", "anvilType", "anvilSeconds", "anvilUpdraftMs", "anvilScale", "lightCellWidth", "cellPitch",
               "cellBulge", "cellBand", "cellFine")
# E40TY_NOSMOOTH=1: the same pictures without the shape smoothing and the top-from-updraft numbers of the layers (the "before" of E40 L6 shape on the same player), under the tag given.
NOSMOOTH = os.environ.get("E40TY_NOSMOOTH") == "1"
TYPE_EXTRA = ("baseGain", "riseShare", "densityScale", "baseLight", "headLight", "baseAmbient", "headAmbient", "shapeSmoothing", "topClose", "outflow")


def lift_samples(type_node):
    """The type's LIFT node (E40 L2 D1) sampled at the table's 128 points; None where the type has none."""
    for c in type_node["children"]:
        if c["name"] == "LIFT":
            keys = [tuple(float(x) for x in v.split()) for _, v in c["values"]]
            return [float(v) for v in np.interp(np.linspace(0.0, 1.0, 128), [k[0] for k in keys], [k[1] for k in keys])]
    return None


def lift_keys(type_node):
    """The type's LIFT keys as the cfg gives them (height, lift); the player builds the game's curve from them."""
    for c in type_node["children"]:
        if c["name"] == "LIFT":
            return [tuple(float(x) for x in v.split()[:2]) for _, v in c["values"]]
    return []


def cfg_extras():
    """{layer: ({key: float}, {type name: {key: float, 'lift': [128 floats]}})} of the soft, light and shape keys of the shipped Clouds-Kerbin.cfg."""
    with open(CFG_PATH, encoding="utf-8") as f:
        root = e40o.parse_nodes(f.read())
    clouds = e40o.find(root, "CLOUDS")
    out = {}
    for layer in [c for c in clouds["children"] if c["name"] == "LAYER"]:
        lk = {k: float(e40o.val(layer, k)) for k in LAYER_EXTRA if e40o.val(layer, k) is not None}
        tk = {}
        for t in [c for c in layer["children"] if c["name"] == "TYPE"]:
            tk[e40o.val(t, "name")] = {k: float(e40o.val(t, k)) for k in TYPE_EXTRA if e40o.val(t, k) is not None}
            lift = lift_samples(t)
            if lift is not None:
                tk[e40o.val(t, "name")]["lift"] = lift
                keys = lift_keys(t)
                tk[e40o.val(t, "name")]["liftX"] = [k[0] for k in keys]
                tk[e40o.val(t, "name")]["liftY"] = [k[1] for k in keys]
            # CLOUDR1: the type's heap preset (a name), rim exponent and cavity, as the game's type reads them (LayersJobType.heap, edgePower, cavity)
            if e40o.val(t, "heap") is not None:
                tk[e40o.val(t, "name")]["heap"] = str(e40o.val(t, "heap")).strip()
            for k in ("edgePower", "cavity"):
                if e40o.val(t, k) is not None:
                    tk[e40o.val(t, "name")][k] = float(e40o.val(t, k))
        out[e40o.val(layer, "name")] = (lk, tk)
    return out


def const_map(value):
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    name = "k%03d.r8" % v
    for folder in (INPUT, INPUT_AUX):
        os.makedirs(folder, exist_ok=True)
        np.full(MAP_W * MAP_H, v, dtype=np.uint8).tofile(os.path.join(folder, name))
    return name


def camera(height, pitch, size):
    lon = math.radians(SITE_LON_DEG)
    pos = np.array([math.cos(lon), math.sin(lon), 0.0]) * (R + height)
    return pr.Camera(pos, 0.0, pitch, size[0], size[1], FOV)


def view_camera(spec, view, size):
    _, _, _, _, _, heights, pitches = spec
    if view == "ground":
        return camera(heights[0], pitches[0], size)
    if view == "above":
        return camera(heights[1], pitches[1], size)
    # looking at the sun: the ground camera, pitched so that the sun (45.6 degrees up, straight ahead) lies in the upper third
    return camera(heights[0], 38.0, size)


def type_value(layer, name):
    names = [t["name"] for t in layer["types"]]
    return names.index(name) / float(max(len(names) - 1, 1))


def job_layer(spec, extras, layers_by_name):
    family, key, layer_name, type_name, map_value, _, _ = spec
    layer = layers_by_name[layer_name]
    lk, tk = extras[layer_name]
    jl = e40o.job_layer(layer, const_map(map_value), const_map(type_value(layer, type_name)))
    jl.update({k: v for k, v in lk.items() if not (NOLIGHT and k.startswith("light")) and not (NOSHAPE and k in ("fibreStretch", "verticalScale"))
               and not (NOSMOOTH and k in ("shapeSmoothing", "topReach"))})
    if spec[1] in WIND_EN and not NOWIND:
        # the layer's wind in world space: east and north at the site (the map frame: x towards longitude 0, y towards 90 east, z north)
        lon = math.radians(SITE_LON_DEG)
        east, north = WIND_EN[spec[1]]
        jl["wind"] = [-math.sin(lon) * east, math.cos(lon) * east, north]
    for t in jl["types"]:
        t.update({k: v for k, v in tk.get(t["name"], {}).items() if not (NOLIGHT and k in ("baseLight", "headLight", "baseAmbient", "headAmbient")) and not (NOSHAPE and k == "lift")
                  and not (NOSMOOTH and k == "shapeSmoothing")})
    return jl


def jid(key, view, aux=False):
    return ("tx-%s-%s" if aux else "ty-%s-%s") % (key, view)


# --------------------------------------------------------------------------------------------------------------------------------
# The cumulonimbus tower (E40 L6 shape): one strong cell in a clear sky, seen from 20 to 40 km, with the height maps the game feeds (the tops follow the updraft of the cell)
# --------------------------------------------------------------------------------------------------------------------------------
CB_LAYER = "Kerbin-cumulonimbus"
# (key, the type of the whole cell, or None: the type follows the cell's strength as the precipitation level does in the game, calvus at its edge, incus at its core)
CB_TOWERS = (("calvus-tower", "Cumulonimbus calvus"), ("incus-tower", "Cumulonimbus incus"), ("cell-tower", None))
CB_VIEWS = ("side", "top", "flat")                       # flat: the side view without the updraft's top term (the type table's own top)
CB_MAP = (512, 256)                                      # the equirectangular map of the cell: about 7.4 km per texel at the equator
CB_CELL_KM = 12.0                                      # the e-folding radius of the cell's strength
CB_CORE_GAIN = 1.4                                     # the coverage map reaches its top value where the Gaussian is above 1 / this (a flat core: the texels of the map are 7 km)
CB_COVER_RANGE = (0.3, 1.0)                              # the coverage map outside and at the centre of the cell
CB_UPDRAFT_MS = 7.0                                      # the updraft at the cell's centre (the generator's full-strength cell is 6 m/s; a growing tower more)
# view: (distance from the cell's centre in km to the south, camera height in m, pitch in degrees)
CB_CAMERAS = {"side": (30.0, 400.0, 14.0), "top": (25.0, 16000.0, -33.0), "flat": (30.0, 400.0, 14.0)}
CB_SUN_AZIMUTH = 160.0


def cb_rain_type():
    """The cumulonimbus layer's rainType (the type value at full precipitation; the table goes on past it to the anvil sheet, E40 CB)."""
    with open(CFG_PATH, encoding="utf-8") as f:
        root = e40o.parse_nodes(f.read())
    for layer in [c for c in e40o.find(root, "CLOUDS")["children"] if c["name"] == "LAYER"]:
        if e40o.val(layer, "name") == CB_LAYER and e40o.val(layer, "rainType") is not None:
            return float(e40o.val(layer, "rainType"))
    return 1.0


def cb_cell_files(strength=1.0, tag="cbcell", outside=None):
    """Writes the cell's coverage map (a Gaussian of the great-circle distance to the cell, from CB_COVER_RANGE[0] (or `outside`) outside to [1] at the centre), its type map (the
    precipitation axis: rainType x the same shape) and the updraft map (R8, 0 to 12 m/s). strength scales the cell (its top coverage, type and updraft) for the anvil rows."""
    w, h = CB_MAP
    units = lay.texel_unit_vectors(w, h)
    lon = math.radians(SITE_LON_DEG)
    centre = np.array([math.cos(lon), math.sin(lon), 0.0])
    dist_km = np.arccos(np.clip(units @ centre, -1.0, 1.0)) * R / 1000.0
    bump = np.exp(-(dist_km / CB_CELL_KM) ** 2)
    lo, hi = CB_COVER_RANGE
    if outside is not None:
        lo = outside
    hi = lo + (hi - lo) * strength
    cover = np.rint(np.clip(lo + (hi - lo) * np.minimum(1.0, CB_CORE_GAIN * bump), 0.0, 1.0) * 255.0).astype(np.uint8)
    updraft = np.rint(np.clip(CB_UPDRAFT_MS * strength * bump / 12.0, 0.0, 1.0) * 255.0).astype(np.uint8)
    os.makedirs(INPUT, exist_ok=True)
    cover.tofile(os.path.join(INPUT, tag + "-cov.r8"))
    updraft.tofile(os.path.join(INPUT, tag + "-up.r8"))
    np.rint(np.clip(cb_rain_type() * strength * np.minimum(1.0, CB_CORE_GAIN * bump), 0.0, 1.0) * 255.0).astype(np.uint8).tofile(os.path.join(INPUT, tag + "-typ.r8"))
    return tag + "-cov.r8", tag + "-up.r8"


def cb_const_map(value):
    """A constant R8 map of the cell's size (the type map of a tower row); returns its name."""
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    name = "cbk%03d.r8" % v
    np.full(CB_MAP[0] * CB_MAP[1], v, dtype=np.uint8).tofile(os.path.join(INPUT, name))
    return name


def cb_camera(view, size):
    km, height, pitch = CB_CAMERAS[view]
    lon, lat = math.radians(SITE_LON_DEG), -km * 1000.0 / R
    pos = np.array([math.cos(lat) * math.cos(lon), math.cos(lat) * math.sin(lon), math.sin(lat)]) * (R + height)
    return pr.Camera(pos, 0.0, pitch, size[0], size[1], FOV)


def cb_jobs(layers_by_name, extras):
    """The lum jobs of the cumulonimbus tower rows and the entries of heights.json that give the side and top views their height maps."""
    cov_name, up_name = cb_cell_files()
    layer, (lk, tk) = layers_by_name[CB_LAYER], extras[CB_LAYER]
    jobs, heights = [], []
    for key, type_name in CB_TOWERS:
        for view in CB_VIEWS:
            jl = e40o.job_layer(layer, cov_name, "cbcell-typ.r8" if type_name is None else cb_const_map(type_value(layer, type_name)))
            jl.update({k: v for k, v in lk.items() if not (NOLIGHT and k.startswith("light")) and not (NOSHAPE and k in ("fibreStretch", "verticalScale"))
                       and not (NOSMOOTH and k in ("shapeSmoothing", "topReach"))})
            for t in jl["types"]:
                t.update({k: v for k, v in tk.get(t["name"], {}).items() if not (NOLIGHT and k in ("baseLight", "headLight", "baseAmbient", "headAmbient")) and not (NOSHAPE and k == "lift")
                          and not (NOSMOOTH and k == "shapeSmoothing")})
            cam = cb_camera(view, SIZE)
            jobs.append(lay.job(jid(key, view), "lum", cam, [jl], SIZE[0], SIZE[1], planet=R, mapWidth=CB_MAP[0], mapHeight=CB_MAP[1],
                                sun=lay.sun_vector(cam.pos, CB_SUN_AZIMUTH, SUN_ELEVATION), sunColour=[2.35, 2.35, 2.35], ambientTop=AMBIENT_TOP, ambientBottom=AMBIENT_BOTTOM,
                                soft=SOFTNESS, maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3,
                                **(dict(noise=dict(sphere=SPHERE)) if SPHERE is not None else {})))
            if view != "flat":
                heights.append(dict(id=jid(key, view), variation=0, physics=0, topUpdraft=1, ragged=0, updraftMap=up_name, width=CB_MAP[0], height=CB_MAP[1], seed=1))
    return jobs, heights


# The cumulonimbus with its anvil (E40 CB): a weak, a moderate and a strong cell (the cell's top coverage, its type on the precipitation axis and its updraft scaled by the strength),
# the type following the strength as in the game, the anvils drawn by AnvilMap in the player's setup with a shear between the anvil and the steering level of CB_SHEAR_MS (east).
# Views: from the ground 35 km south of the cell (the whole tower), from the side at the anvil's height 70 km away, and from above.
CB_ANVIL_ROWS = (("weak", 0.6), ("moderate", 0.8), ("strong", 1.0))
CB_ANVIL_VIEWS = ("ground", "anvil", "above")
# A sweep of another family (E40TY_ONLY without cumulonimbus) leaves the tower and anvil rows out: their jobs take most of the run.
if os.environ.get("E40TY_ONLY") and "cumulonimbus" not in os.environ["E40TY_ONLY"].split(","):
    CB_TOWERS = ()
    CB_ANVIL_ROWS = ()
CB_ANVIL_CAMERAS = {"ground": (40.0, 400.0, 13.0), "anvil": (90.0, 9500.0, -1.0), "above": (55.0, 26000.0, -30.0)}
CB_SHEAR_MS = 8.0
# The anvil rows' coverage outside the cell: 0 (a clear sky around one cell; at the tower rows' 0.3 the layer's clear-sky rule keeps the background, K about 0.14 since the cover
# meaning, and the cell drowns in a sheet of the same layer).
CB_ANVIL_OUTSIDE = 0.0


def cb_anvil_camera(view, size):
    km, height, pitch = CB_ANVIL_CAMERAS[view]
    lon, lat = math.radians(SITE_LON_DEG), -km * 1000.0 / R
    pos = np.array([math.cos(lat) * math.cos(lon), math.cos(lat) * math.sin(lon), math.sin(lat)]) * (R + height)
    return pr.Camera(pos, 0.0, pitch, size[0], size[1], FOV)


def cb_anvil_jobs(layers_by_name, extras):
    layer, (lk, tk) = layers_by_name[CB_LAYER], extras[CB_LAYER]
    jobs, heights = [], []
    for key, strength in CB_ANVIL_ROWS:
        tag = "cbanvil-" + key
        cov_name, up_name = cb_cell_files(strength, tag, CB_ANVIL_OUTSIDE)
        for view in CB_ANVIL_VIEWS:
            jl = e40o.job_layer(layer, cov_name, tag + "-typ.r8")
            jl.update({k: v for k, v in lk.items()})
            jl["anvilShearRadS"] = CB_SHEAR_MS / R
            for t in jl["types"]:
                t.update({k: v for k, v in tk.get(t["name"], {}).items()})
            cam = cb_anvil_camera(view, SIZE)
            jobs.append(lay.job(jid(tag, view), "lum", cam, [jl], SIZE[0], SIZE[1], planet=R, mapWidth=CB_MAP[0], mapHeight=CB_MAP[1],
                                sun=lay.sun_vector(cam.pos, CB_SUN_AZIMUTH, SUN_ELEVATION), sunColour=[2.35, 2.35, 2.35], ambientTop=AMBIENT_TOP, ambientBottom=AMBIENT_BOTTOM,
                                soft=SOFTNESS, maxSteps=320, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3))
            heights.append(dict(id=jid(tag, view), variation=0, physics=0, topUpdraft=1, ragged=0, updraftMap=up_name, width=CB_MAP[0], height=CB_MAP[1], seed=1))
    return jobs, heights


# E40TY_HAZE=before|after: the same pictures with the pass's aerial perspective on at Scatterer's air of Kerbin (E40 T1 tint), the in-scatter as the product gives it BEFORE the package (one
# colour, bluer than the sun by the exponent 0.35, inside the stage's white-point division) or AFTER (the short-path colour the sun's own, the horizon colour the same 0.35, both
# multiplied by the tint so that the division cancels). Use it with E40TY_TAG=<text> so that the names differ (the picture names are e40-ty-<tag>-...).
HAZE = os.environ.get("E40TY_HAZE")
# E40TY_HAZE_BLUE=<x>: the body key hazeBlue of the "after" colours (the default is the cfg's 0.35), E40TY_HAZE_NEAR_BLUE=<x>: hazeNearBlue (0).
HAZE_SHARE, HAZE_BLUE, HAZE_NEAR_BLUE = 0.23, float(os.environ.get("E40TY_HAZE_BLUE", 0.35)), float(os.environ.get("E40TY_HAZE_NEAR_BLUE", 0.0))
# E40TY_HAZE_TINT=r,g,b: the body keys hazeTintR / G / B (E40 FL far), a gain on the far in-scatter colour of the "after" haze (1,1,1: none).
HAZE_TINT_GAIN = np.array([float(x) for x in os.environ.get("E40TY_HAZE_TINT", "1,1,1").split(",")])
SC_RAYLEIGH = (0.0153407799e-3, 0.0357074998e-3, 0.0875488967e-3)
SC_MIE, SC_HR, SC_HM = 0.0121406196e-3 / 0.9, 3840.0, 1000.0


def haze_block(layer):
    """The job's far block with the in-scatter colours of the sheet's sun (the grey 2.35 of the pass) for the layer's white tint; None without E40TY_HAZE."""
    if not HAZE:
        return None
    ray = np.array(SC_RAYLEIGH)

    def inscatter(exponent):
        return np.maximum(2.35 * HAZE_SHARE * (ray / ray.mean()) ** exponent, np.array(AMBIENT_TOP))

    far = dict(haze=1, shapeMean=0, policy=0, minSamples=0, rayleigh=list(SC_RAYLEIGH), mie=SC_MIE, rayleighHeight=SC_HR, mieHeight=SC_HM, hazeStrength=1.0)
    if HAZE == "before":
        far["inscatter"] = [float(x) for x in inscatter(HAZE_BLUE)]
        return far
    tint = white_tint(layer["inner"] + 0.25 * (layer["outer"] - layer["inner"]))
    far["inscatter"] = [float(x) for x in inscatter(HAZE_BLUE) * tint * HAZE_TINT_GAIN]
    far["inscatterNear"] = [float(x) for x in inscatter(HAZE_NEAR_BLUE) * tint]
    return far


def build_jobs():
    layers_by_name = {s["name"]: s for s in e40o.kerbin_layers(CFG_PATH)}
    extras = cfg_extras()
    lum, aux = [], []
    for spec in TYPES:
        for view in VIEWS:
            for mode, size, bucket, aux_flag in (("lum", SIZE, lum, False), ("aux", AUX_SIZE, aux, True)):
                if aux_flag and view == "sun":
                    continue
                cam = view_camera(spec, view, size)
                bucket.append(lay.job(jid(spec[1], view, aux_flag), mode, cam, [job_layer(spec, extras, layers_by_name)], size[0], size[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H,
                                      sun=lay.sun_vector(cam.pos, 0.0, SUN_ELEVATION), sunColour=[2.35, 2.35, 2.35], ambientTop=AMBIENT_TOP, ambientBottom=AMBIENT_BOTTOM,
                                      soft=SOFTNESS, maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3,
                                      **(dict(noise=dict(sphere=SPHERE)) if SPHERE is not None else {}),
                                      **(dict(far=haze_block(layers_by_name[spec[2]])) if HAZE else {})))
    cb, heights = cb_jobs(layers_by_name, extras)
    lum.extend(cb)
    anvil, anvil_heights = cb_anvil_jobs(layers_by_name, extras)
    lum.extend(anvil)
    CB_HEIGHTS[:] = heights + anvil_heights
    return lum, aux


# The heights.json entries of the cumulonimbus tower jobs (filled by build_jobs).
CB_HEIGHTS = []


def write_inputs():
    lum, aux = build_jobs()
    for folder, jobs in ((INPUT, lum), (INPUT_AUX, aux)):
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, "jobs.json"), "w", encoding="utf-8") as f:
            json.dump({"jobs": jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": CB_HEIGHTS}, f)
    return lum, aux


# --------------------------------------------------------------------------------------------------------------------------------
# Display
# --------------------------------------------------------------------------------------------------------------------------------
KERBIN_RAYLEIGH_TAU = np.array([0.01534, 0.03571, 0.08755]) * 3.84     # per km x scale height: the vertical optical depth at sea level
KERBIN_MIE_TAU = 0.01214 / 0.9 * 1.0
RAYLEIGH_H, MIE_H = 3840.0, 1000.0


def sun_air(sin_el, altitude):
    """CloudLightMath.SunAir per channel (the white balance off): the share of the sun's light that reaches a point after the air above it."""
    s = min(max(sin_el, -1.0), 1.0)
    elev = math.degrees(math.asin(s))
    m = 1.0 / (max(s, 0.0) + 0.50572 * max(elev + 6.07995, 0.05) ** -1.6364)
    tau = KERBIN_RAYLEIGH_TAU * math.exp(-altitude / RAYLEIGH_H) + KERBIN_MIE_TAU * math.exp(-altitude / MIE_H)
    return np.exp(-tau * m)


def white_share(sin_el, strength=1.0):
    """CloudOutputStage.WhiteShare."""
    t = min(max((sin_el - 0.05) / 0.35, 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t) * min(max(strength, 0.0), 1.0)


def white_tint(altitude=3000.0, strength=1.0):
    """CloudOutputStage.WhiteTint of the sun a cloud at the nominal altitude gets."""
    sin_el = math.sin(math.radians(SUN_ELEVATION))
    sun = sun_air(sin_el, altitude)
    luma = sun @ np.array([0.2126, 0.7152, 0.0722])
    s = white_share(sin_el, strength)
    return 1.0 + (sun / luma - 1.0) * s


def encode(c, tint):
    return WHITE * np.power(np.maximum(c / (WHITE * tint), 0.0), 1.0 / GAMMA)


def backdrop(cam, with_sun):
    d = cam.ray_directions().reshape(cam.height, cam.width, 3)
    up = cam.pos / np.linalg.norm(cam.pos)
    dip = math.acos(R / np.linalg.norm(cam.pos))
    elev = np.degrees(np.arcsin(np.clip(d @ up, -1.0, 1.0)) + dip)
    t = np.clip(elev / 60.0, 0.0, 1.0)[..., None] ** 0.5
    sky = gn.SKY_HORIZON * (1.0 - t) + gn.SKY_ZENITH * t
    w = np.clip((elev + 1.0) / 2.0, 0.0, 1.0)[..., None]
    out = sky * w + gn.SEA * (1.0 - w)
    if with_sun:
        sun = lay.sun_vector(cam.pos, 0.0, SUN_ELEVATION)
        theta = np.arccos(np.clip(d @ np.asarray(sun), -1.0, 1.0))
        disc = (theta < 4.0 * 0.00465)[..., None]
        glow = (0.5 / (1.0 + (theta / 0.05) ** 2) ** 1.5)[..., None]
        out = np.where(disc, 1.0, out + glow)
    return np.clip(out, 0.0, 1.0)


def load_lum(folder, name):
    """A full target the scene wrote (half floats, the bottom row first), as (h, w, 4) with the top row first."""
    return np.load(os.path.join(folder, name + ".npy")).astype(np.float64)[::-1]


def picture(m, cam, tint, with_sun=True):
    a = np.clip(m[..., 3:4], 0.0, 1.0)
    colour = encode(m[..., :3] / np.maximum(a, 1e-4), tint)
    return np.clip(colour * a + backdrop(cam, with_sun) * (1.0 - a), 0.0, 1.0)


def heat_picture(tau, cam):
    base = backdrop(cam, False) * 0.5
    h = gn.heat(tau)
    return np.where((tau > 1e-3)[..., None], h, base)


def box(img, size):
    from PIL import Image
    return np.asarray(Image.fromarray(np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8), "RGB").resize(size, Image.Resampling.LANCZOS), dtype=np.float64) / 255.0


def label(img, text):
    from PIL import Image, ImageDraw
    pil = Image.fromarray(np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8), "RGB")
    draw = ImageDraw.Draw(pil)
    draw.rectangle([0, 0, pil.width, 16], fill=(0, 0, 0))
    draw.text((4, 2), text, fill=(255, 255, 255))
    return np.asarray(pil, dtype=np.float64) / 255.0


def load_png(path):
    from PIL import Image
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255.0


def alpha_words(alpha):
    """Median alpha over the pixels the cloud covers (alpha above 0.03), the cloud's share of the picture and the share of the cloud that is nearly opaque (alpha above 0.95)."""
    mask = alpha > 0.03
    if mask.sum() == 0:
        return dict(cover=0.0, median=0.0, p10=0.0, p90=0.0, opaque=0.0)
    a = alpha[mask]
    return dict(cover=float(mask.mean()), median=float(np.median(a)), p10=float(np.percentile(a, 10)), p90=float(np.percentile(a, 90)), opaque=float((a > 0.95).mean()))


def plane_points(cam, altitude_m):
    """Where each pixel's ray meets the sphere `altitude_m` above the planet's surface (R, the scene's radius): the east and north offsets in m from the camera's foot, the slant range in m and
    the elevation of the ray above the camera's horizon in degrees (arrays of the picture's shape). A ray that does not reach the sphere gets NaN."""
    d = cam.ray_directions().reshape(cam.height, cam.width, 3)
    up, east, north = cam.frame
    r_sphere = R + altitude_m
    b = d @ cam.pos
    c = float(cam.pos @ cam.pos) - r_sphere * r_sphere
    disc = b * b - c
    t = np.where(disc > 0.0, -b + np.sqrt(np.maximum(disc, 0.0)), np.nan)
    rel = t[..., None] * d
    return rel @ east, rel @ north, t, np.degrees(np.arcsin(np.clip(d @ up, -1.0, 1.0)))


def fibre_stats(tau, cam, altitude_m, axis_en=(1.0, 1.0), min_elevation=20.0, step_m=100.0):
    """The size of the structure in an optical-depth picture (the cirrus rows; a PLAYER number, not a game number): the pixels above `min_elevation` are laid on the plane of the layer's
    middle (`altitude_m`), resampled to a grid of `step_m`, and the autocorrelation of the field (the mean taken out, the empty corners masked out) is measured: the full width at half
    maximum of the autocorrelation across and along the layer's wind axis `axis_en` (east, north), in m, the elongation along / across, the direction of the longest axis against the wind
    axis in degrees, the median slant range of the pixels and the width across as an angle at that range. Features of a Gaussian profile are 1.4 times narrower than the autocorrelation's width.
    Where the autocorrelation does not fall to a half inside the grid the length along is a lower bound (`along_bounded`)."""
    from scipy.interpolate import griddata
    e, n, rng, elev = plane_points(cam, altitude_m)
    use = np.isfinite(e) & (elev >= min_elevation)
    if use.sum() < 1000:
        return None
    pts = np.stack([e[use], n[use]], axis=1)[::3]
    vals = tau[use][::3]
    x0, x1 = float(pts[:, 0].min()), float(pts[:, 0].max())
    y0, y1 = float(pts[:, 1].min()), float(pts[:, 1].max())
    gx = np.arange(x0, x1, step_m)
    gy = np.arange(y0, y1, step_m)
    grid_x, grid_y = np.meshgrid(gx, gy)
    field = griddata(pts, vals, (grid_x, grid_y), method="linear")
    mask = np.isfinite(field).astype(np.float64)
    if mask.sum() < 2000:
        return None
    slant = float(np.nanmedian(rng[use]))
    stats = acf_widths(np.where(mask > 0.0, field, 0.0), mask, step_m, axis_en)
    stats.update(slant_m=slant, across_deg=float(math.degrees(2.0 * math.atan(0.5 * stats["across_m"] / slant))), pixels=int(use.sum()))
    return stats


def card_references(tint):
    """The white card of a frame (E40 L6 light): a Lambertian white surface under the sun of the sheets, as radiance relative to W (the card facing the sun outside the air = 1) and as the
    display value of the stage. 'sky' adds the sheets' ambient (AMBIENT_TOP over W for a card facing up; half of it for a card facing the sun, which sees half the sky)."""
    mu0 = math.sin(math.radians(SUN_ELEVATION))
    sky = AMBIENT_TOP[1] / WHITE

    def shown(r):
        return float(encode(np.array([r * WHITE] * 3), tint) @ np.array([0.2126, 0.7152, 0.0722]))

    return dict(card_sun=1.0, card_sun_display=shown(1.0), card_flat=mu0, card_flat_display=shown(mu0), card_sun_sky=1.0 + 0.5 * sky, card_flat_sky=mu0 + sky,
                card_sun_sky_display=shown(1.0 + 0.5 * sky), card_flat_sky_display=shown(mu0 + sky))


def light_words(m, cam, tint):
    """Percentiles of the closed part's luminance (alpha above 0.9) relative to W and as the display value, the sky pixels next to it, and the cards of the frame; None without a closed part."""
    a = m[..., 3]
    core = a > 0.9
    covered = a > 0.03
    # What the cloud adds to the picture, premultiplied (its radiance times its alpha), relative to W: the thin types have no closed part.
    veil = float((m[..., :3][covered] @ np.array([0.2126, 0.7152, 0.0722])).mean() / WHITE) if covered.sum() > 200 else float("nan")
    if core.sum() < 200:
        return dict(veil_rel=veil)
    neutral = m[..., :3] / np.maximum(a[..., None], 1e-4)
    luma = neutral[core] @ np.array([0.2126, 0.7152, 0.0722]) / WHITE
    shown = encode(neutral[core], tint) @ np.array([0.2126, 0.7152, 0.0722])
    pct = [10, 50, 90, 99]
    back = backdrop(cam, False)
    free = a < 0.05
    sky_display = float(np.median(back[free] @ np.array([0.2126, 0.7152, 0.0722]))) if free.sum() > 200 else float("nan")
    out = dict(veil_rel=veil, rel=[float(x) for x in np.percentile(luma, pct)], display=[float(x) for x in np.percentile(shown, pct)], sky_display=sky_display,
               sky_rel=float((sky_display / WHITE) ** GAMMA) if sky_display == sky_display else float("nan"))
    out.update(card_references(tint))
    return out


def collect(ctx):
    sheet = ctx.folder("sheet")
    aux = ctx.folder("aux")
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    layers_by_name = {s["name"]: s for s in e40o.kerbin_layers(CFG_PATH)}
    metrics, lines, results = [], ["E40-TY: every type of the shipped cfg, one layer at a time, converged, soft %.1f; the white tint is that of the sun at the lower quarter of the layer"
                                   % SOFTNESS], {}
    tiles = {}
    for spec in TYPES:
        family, key = spec[0], spec[1]
        layer = layers_by_name[spec[2]]
        tint = white_tint(layer["inner"] + 0.25 * (layer["outer"] - layer["inner"]))   # CloudOutputStage.NominalCloudAltitudeM
        results.setdefault(key, {})["white_tint"] = [float(x) for x in tint]
        results[key]["card"] = card_references(tint)
        row = {}
        for view in VIEWS:
            path = os.path.join(sheet, jid(key, view) + ".npy")
            if not os.path.isfile(path):
                lines.append("   %-14s %-6s: no picture" % (key, view))
                continue
            m = load_lum(sheet, jid(key, view))
            cam = view_camera(spec, view, SIZE)
            img = picture(m, cam, tint)
            plain = picture(m, cam, np.ones(3))
            if view == "sun":
                row["sky"] = box(picture(np.zeros_like(m), cam, tint), TILE)
                gn.save_png(os.path.join(out_dir, "%s-%s-%s-sky.png" % (NAME, family, key)), row["sky"])
            gn.save_png(os.path.join(out_dir, "%s-%s-%s-%s.png" % (NAME, family, key, view)), img)
            row[view] = box(img, TILE)
            words = alpha_words(m[..., 3])
            neutral = m[..., :3] / np.maximum(m[..., 3:4], 1e-4)
            core = m[..., 3] > 0.9
            blue_red = float(np.median(encode(neutral[core], tint)[:, 2] / np.maximum(encode(neutral[core], tint)[:, 0], 1e-6))) if core.sum() > 200 else float("nan")
            blue_red_plain = float(np.median(encode(neutral[core], np.ones(3))[:, 2] / np.maximum(encode(neutral[core], np.ones(3))[:, 0], 1e-6))) if core.sum() > 200 else float("nan")
            results.setdefault(key, {})[view] = dict(alpha=words, blue_over_red=blue_red, blue_over_red_without_tint=blue_red_plain,
                                                      luma_p10_p50_p90=[float(x) for x in np.percentile((encode(neutral[core], tint) @ np.array([0.2126, 0.7152, 0.0722])), [10, 50, 90])] if core.sum() > 200 else None)
            lines.append("   %-14s %-6s: cloud covers %.2f of the picture, median alpha %.2f (p10 %.2f, p90 %.2f), nearly opaque (above 0.95) %.2f of it, blue over red of its closed part %.2f (without the tint %.2f)"
                         % (key, view, words["cover"], words["median"], words["p10"], words["p90"], words["opaque"], blue_red, blue_red_plain))
            metrics.append(Metric.info("e40ty.%s.%s.alpha_median" % (key, view), words["median"]))
            lw = light_words(m, cam, tint)
            results[key][view]["light"] = lw
            lines.append("   %-14s %-6s: the cloud adds (premultiplied, alpha above 0.03) %.3f of W on average" % (key, view, lw["veil_rel"]))
            if "rel" in lw:
                lines.append("   %-14s %-6s: closed part (alpha above 0.9) radiance relative to W (the card facing the sun outside the air): p10 %.2f p50 %.2f p90 %.2f p99 %.2f = display %.2f %.2f %.2f %.2f; "
                             "the sky pixels next to it: radiance %.2f display %.2f; white card in the same frame: facing the sun %.2f (display %.2f), horizontal %.2f (display %.2f), both with the sky %.2f / %.2f"
                             % ((key, view) + tuple(lw["rel"]) + tuple(lw["display"]) + (lw["sky_rel"], lw["sky_display"], lw["card_sun"], lw["card_sun_display"], lw["card_flat"], lw["card_flat_display"],
                                                                                          lw["card_sun_sky"], lw["card_flat_sky"])))
        apath = os.path.join(aux, jid(key, "ground", True) + "-tau.npy")
        if os.path.isfile(apath):
            tau = np.load(apath).astype(np.float64)
            cam = view_camera(spec, "ground", AUX_SIZE)
            row["aid"] = box(heat_picture(tau, cam), TILE)
            gn.save_png(os.path.join(out_dir, "%s-%s-%s-aid.png" % (NAME, family, key)), row["aid"])
            results.setdefault(key, {})["tau_ground_p10_p50_p90"] = [float(x) for x in np.percentile(tau[tau > 0.03], [10, 50, 90])] if (tau > 0.03).any() else None
            if family == "cirrus":
                fs = fibre_stats(tau, cam, 0.5 * (layer["inner"] + layer["outer"]), axis_en=WIND_EN.get(key, (1.0, 1.0)))
                results[key]["fibres_ground"] = fs
                if fs:
                    metrics.append(Metric.info("e40ty.%s.fibre_across_m" % key, fs["across_m"]))
                    metrics.append(Metric.info("e40ty.%s.fibre_elongation" % key, fs["elongation"]))
                    lines.append("   %-14s ground: PLAYER structure of the optical depth on the layer's middle plane (elevation above 20 degrees, %d px): width across the wind axis %.0f m "
                                 "(autocorrelation FWHM; %.1f degrees at the median range %.1f km), along it %s%.0f m, elongation %s%.1f, the longest axis %.0f degrees off the wind"
                                 % (key, fs["pixels"], fs["across_m"], fs["across_deg"], fs["slant_m"] / 1000.0, ">" if fs["along_bounded"] else "", fs["along_m"],
                                    ">" if fs["along_bounded"] else "", fs["elongation"], fs["longest_axis_off_wind_deg"]))
        tiles[key] = row
    # The sheets: one per family, a row per type: ground | above | sun | the sky behind it | optical depth | (TVC)
    for family in FAMILIES:
        rows = []
        for spec in [s for s in TYPES if s[0] == family]:
            key = spec[1]
            r = tiles.get(key, {})
            cols = []
            for name, text in (("ground", "ground"), ("above", "from above"), ("sun", "toward the sun"), ("sky", "the sky without the cloud"), ("aid", "optical depth (blue thin, green 1, red thick)")):
                if name == "ground" and key in results:
                    c = results[key]["card"]
                    text = "ground, white card %.2f sun / %.2f flat (sky incl. %.2f / %.2f)" % (c["card_sun_display"], c["card_flat_display"], c["card_sun_sky_display"], c["card_flat_sky_display"])
                cols.append(label(r[name], "%s: %s" % (key, text)) if name in r else np.zeros((TILE[1], TILE[0], 3)))
            rows.append(np.concatenate(cols, axis=1))
        if family in TVC and os.path.isfile(os.path.join(TVC_DIR, TVC[family])):
            tvc = label(box(load_png(os.path.join(TVC_DIR, TVC[family])), TILE), "True Volumetric Clouds, game frame (its own weather)")
            pad = np.zeros((TILE[1], TILE[0] * 4, 3))
            rows.append(np.concatenate([tvc, pad], axis=1))
        if rows:
            gn.save_png(os.path.join(out_dir, "%s-%s-sheet.png" % (NAME, family)), np.concatenate(rows, axis=0))
    # The cumulonimbus tower sheet: a row per type, the side view from 30 km, the view from above from 25 km, and the side view without the updraft's top term.
    cb_layer = layers_by_name[CB_LAYER]
    cb_tint = white_tint(cb_layer["inner"] + 0.25 * (cb_layer["outer"] - cb_layer["inner"]))
    cb_rows = []
    for key, type_name in CB_TOWERS:
        cols = []
        for view, text in (("side", "side view, 30 km, tops follow the updraft"), ("top", "from above, 25 km"), ("flat", "side view, the type table's own top")):
            path = os.path.join(sheet, jid(key, view) + ".npy")
            if not os.path.isfile(path):
                lines.append("   %-14s %-6s: no picture" % (key, view))
                cols.append(np.zeros((TILE[1] * 2, TILE[0] * 2, 3)))
                continue
            m = load_lum(sheet, jid(key, view))
            img = picture(m, cb_camera(view, SIZE), cb_tint, with_sun=False)
            gn.save_png(os.path.join(out_dir, "%s-cbtower-%s-%s.png" % (NAME, key, view)), img)
            cols.append(label(box(img, (TILE[0] * 2, TILE[1] * 2)), "%s: %s" % (key, text)))
            words = alpha_words(m[..., 3])
            # the highest row of the picture the cloud reaches, as an elevation angle above the camera's horizon for the side views
            rows_hit = np.where((m[..., 3] > 0.5).any(axis=1))[0]
            top_deg = None
            if view != "top" and len(rows_hit):
                cam = cb_camera(view, SIZE)
                top_deg = CB_CAMERAS[view][2] + (0.5 - (rows_hit[0] + 0.5) / SIZE[1]) * FOV
            results.setdefault(key, {})["tower_" + view] = dict(alpha=words, top_elevation_deg=top_deg)
            lines.append("   %-14s %-6s: cloud covers %.2f of the picture%s" % (key, view, words["cover"], "" if top_deg is None else ", the highest opaque row at %.1f degrees above the horizontal" % top_deg))
        cb_rows.append(np.concatenate(cols, axis=1))
    if cb_rows:
        gn.save_png(os.path.join(out_dir, "%s-cbtower-sheet.png" % NAME), np.concatenate(cb_rows, axis=0))
    # The anvil sheet (E40 CB): a row per cell strength, the ground view, the side view at the anvil's height, the view from above.
    anvil_rows = []
    for key, strength in CB_ANVIL_ROWS:
        tag = "cbanvil-" + key
        cols = []
        for view, text in (("ground", "from the ground, 40 km"), ("anvil", "side at the anvil's height, 90 km"), ("above", "from above, 26 km up, 55 km south")):
            path = os.path.join(sheet, jid(tag, view) + ".npy")
            if not os.path.isfile(path):
                lines.append("   %-18s %-6s: no picture" % (tag, view))
                cols.append(np.zeros((TILE[1] * 2, TILE[0] * 2, 3)))
                continue
            m = load_lum(sheet, jid(tag, view))
            img = picture(m, cb_anvil_camera(view, SIZE), cb_tint, with_sun=False)
            gn.save_png(os.path.join(out_dir, "%s-%s-%s.png" % (NAME, tag, view)), img)
            cols.append(label(box(img, (TILE[0] * 2, TILE[1] * 2)), "%s cell (strength %.1f): %s" % (key, strength, text)))
            words = alpha_words(m[..., 3])
            # the cloud's width in the picture at its widest row and at the row of its base (opaque columns), for the side views: anvil against tower
            opaque = m[..., 3] > 0.5
            widths = opaque.sum(axis=1)
            results.setdefault(tag, {})[view] = dict(alpha=words, widest_px=int(widths.max()) if widths.size else 0)
            lines.append("   %-18s %-6s: cloud covers %.2f of the picture, the widest opaque row %d px" % (tag, view, words["cover"], int(widths.max())))
        anvil_rows.append(np.concatenate(cols, axis=1))
    if anvil_rows:
        gn.save_png(os.path.join(out_dir, "%s-cbanvil-sheet.png" % NAME), np.concatenate(anvil_rows, axis=0))
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


def stages():
    # Always written afresh: the cfg and the E40TY_* switches are read here, and neither moves the script's time stamp.
    write_inputs()
    return [Stage("sheet", ["-mode", "scene", "-scene", "type-sheet", "-in", INPUT], size=(1708, 960), proxy=False, timeout=2400),
            Stage("aux", ["-mode", "scene", "-scene", "layers", "-in", INPUT_AUX], size=(1280, 720), proxy=False, timeout=1800)]


register_series("e40-ty", stages(), collect, exclusive=False, who="e40-ty")


def selftest():
    lum, aux = build_jobs()
    ids = [j["id"] for j in lum + aux]
    assert len(ids) == len(set(ids)), "job ids are unique"
    n_anvil = len(CB_ANVIL_ROWS) * len(CB_ANVIL_VIEWS)
    assert len(lum) == len(TYPES) * len(VIEWS) + len(CB_TOWERS) * len(CB_VIEWS) + n_anvil and len(aux) == len(TYPES) * 2
    assert len(CB_HEIGHTS) == len(CB_TOWERS) * (len(CB_VIEWS) - 1) + n_anvil and all(h["id"] in ids and not h["id"].endswith("-flat") for h in CB_HEIGHTS), "the flat control has no height maps"
    # the anvil rows carry the layer's anvil keys and the shear; the weak cell's maps are weaker than the strong cell's
    anvil_job = [j for j in lum if j["id"] == jid("cbanvil-strong", "ground")][0]["layers"][0]
    assert anvil_job.get("anvilType", -1) >= 0 and anvil_job["anvilShearRadS"] > 0, "the anvil keys reach the job"
    weak = np.fromfile(os.path.join(INPUT, "cbanvil-weak-cov.r8"), dtype=np.uint8)
    strong = np.fromfile(os.path.join(INPUT, "cbanvil-strong-cov.r8"), dtype=np.uint8)
    assert weak.max() < strong.max(), (weak.max(), strong.max())
    # the cell's maps: the strongest texel at the cell's place, the coverage falling to the outside value, the updraft at its centre value
    cov = np.fromfile(os.path.join(INPUT, "cbcell-cov.r8"), dtype=np.uint8).reshape(CB_MAP[1], CB_MAP[0])
    up = np.fromfile(os.path.join(INPUT, "cbcell-up.r8"), dtype=np.uint8).reshape(CB_MAP[1], CB_MAP[0])
    row, col = np.unravel_index(np.argmax(cov), cov.shape)
    assert abs(((col + 0.5) / CB_MAP[0] * 360.0 - 180.0) - SITE_LON_DEG) < 360.0 / CB_MAP[0] and abs(row + 0.5 - CB_MAP[1] / 2.0) <= 1.0, (row, col)
    assert cov.min() == int(round(CB_COVER_RANGE[0] * 255.0)) and 0.75 * CB_UPDRAFT_MS < float(up.max()) * 12.0 / 255.0 <= CB_UPDRAFT_MS + 0.05, (cov.min(), up.max())
    # the cameras: the side view is level with the ground 30 km south of the cell and looks at it (the cell is north of the camera)
    cam = cb_camera("side", SIZE)
    toward = np.array([math.cos(math.radians(SITE_LON_DEG)), math.sin(math.radians(SITE_LON_DEG)), 0.0]) * R - cam.pos
    assert np.dot(cam.forward, toward / np.linalg.norm(toward)) > 0.9, "the side camera looks at the cell"
    layers = {s["name"]: s for s in e40o.kerbin_layers(CFG_PATH)}
    for spec in TYPES:
        names = [t["name"] for t in layers[spec[2]]["types"]]
        assert spec[3] in names, spec[3]
    # the cfg's soft and light keys reach the job: the Stratus type carries its density scale, the cumulus layer its light factors
    stratus = [j for j in lum if j["id"] == "ty-stratus-ground"][0]["layers"][0]
    assert abs([t for t in stratus["types"] if t["name"] == "Stratus"][0]["densityScale"] - 0.22) < 1e-6
    assert stratus["softnessScale"] == 1.5 and lum[0]["soft"] == SOFTNESS
    # the fibre measure on a synthetic field of known size: white noise smoothed with sigma 400 m across and 4 km along the north axis, seen from the ground at 35 degrees (the sheet's view).
    # The autocorrelation of such a field is Gaussian with sigma times 1.414, so its FWHM is 2.355 x 1.414 x sigma: 1.33 km across, 13.3 km along.
    from scipy.ndimage import gaussian_filter, map_coordinates
    step, half_extent = 50.0, 100000.0
    size = int(2 * half_extent / step)
    white = np.random.RandomState(3).standard_normal((size, size))
    field = gaussian_filter(white, sigma=(4000.0 / step, 400.0 / step), mode="wrap")   # axis 0: north (rows), axis 1: east
    spec = [s for s in TYPES if s[1] == "cirrus"]
    cam = view_camera(spec[0] if spec else TYPES[0], "ground", AUX_SIZE)
    e, n, _, _ = plane_points(cam, 9500.0)
    tau = np.where(np.isfinite(e), map_coordinates(field, [np.nan_to_num(n) / step + size / 2.0, np.nan_to_num(e) / step + size / 2.0], order=1, mode="wrap"), 0.0)
    fs = fibre_stats(tau, cam, 9500.0, axis_en=(0.0, 1.0))
    assert fs is not None and abs(fs["across_m"] / 1333.0 - 1.0) < 0.3 and fs["elongation"] > 4.0, fs
    tint = white_tint()
    assert abs(tint @ np.array([0.2126, 0.7152, 0.0722]) - 1.0) < 1e-6 and tint[2] < 1.0 < tint[0]
    # a cloud top lit by the sun at 3000 m comes out neutral with the tint (the three channels equal)
    sun = sun_air(math.sin(math.radians(SUN_ELEVATION)), 3000.0) * WHITE
    shown = encode(sun, tint)
    assert np.allclose(shown, shown[0], rtol=1e-6), shown
    print("selftest passed (%d + %d jobs, tint %s)" % (len(lum), len(aux), np.round(tint, 3)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--write", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.write:
        print([len(x) for x in write_inputs()], "jobs")
