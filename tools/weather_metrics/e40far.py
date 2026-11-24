"""E40 FL far: the light of far clouds in the weather player (no game): the same cloud seen from the ground at 2, 6, 12, 25 and 50 km (player series e40-fl-far).

    python tools/build_weather_player.py --series e40-fl-far
    python tools/build_weather_player.py --series e40-fl-far --no-build
    python tools/weather_metrics/e40far.py --selftest

The scene 'far-sheet' draws ONE cumulus cell of the shipped Clouds-Kerbin.cfg (a Gaussian bump in the coverage map, e-folding radius SIGMA_KM, the type map constant: Cumulus humilis or
Cumulus congestus) with the pass of the game from CAMERA_HEIGHT above the sea, DISTANCES_KM from the cell's near side, the sun at one azimuth (east: the cell is lit from the side) and
two elevations (45.6 degrees and 12 degrees). Aerial perspective off: the radiance is the cloud's own light. Each picture is made twice, with the converged march and with the product's (48 steps, 25 m,
step growth, light reuse; the mean of PRODUCT_FRAMES frames). A third group switches single light terms off or changes them at the same distances (the 'chain' variants of VARIANTS).

The measures are taken over the closed part of the cloud (alpha above 0.9): the luminance of its radiance relative to W (the white card facing the sun outside the air), the same as the
display value of the output stage, the median first-hit distance and optical depth (the pass's aux).
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
from weather_metrics import e40o  # noqa: E402
from weather_metrics import horizon_sky as hs  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40far-in")
RESULTS = os.path.join(BUILD, "e40far-results.json")
R = 600000.0
SIZE = (1024, 576)
FOV = 60.0
CAMERA_HEIGHT = 264.0
SITE_LON_DEG = 10.0
MAP = (4096, 2048)
SIGMA_KM = float(os.environ.get("E40FAR_SIGMA", 3.0))        # the cell's e-folding radius
CORE_GAIN = 1.5                                              # the coverage reaches 1 where the Gaussian is above 1 / this
EDGE_KM = float(os.environ.get("E40FAR_EDGE", 3.0))          # the cell's visible radius: the camera's distance to the centre is the label's distance plus this
DISTANCES_KM = (2, 6, 12, 25, 50)
# azimuth, elevation: hi and lo from the east (the cell is lit from the side), fw toward the sun (the camera looks north, the sun is north), bk the sun behind the camera
SUNS = {"hi": (90.0, 45.6), "lo": (90.0, 12.0), "fw": (0.0, 45.6), "bk": (180.0, 45.6)}
# E40FAR_SUNS=hi,lo: only these suns (a before / after aerial perspective picture at two sun heights in a quarter of the jobs).
SUNS = {k: v for k, v in SUNS.items() if k in os.environ.get("E40FAR_SUNS", ",".join(SUNS)).split(",")}
VARIANT_SUNS = tuple(s for s in ("hi", "fw") if s in SUNS)    # the suns the chain variants run at (congestus)
CLOUDS = (("humilis", "Kerbin-cumulus", "Cumulus humilis"), ("congestus", "Kerbin-cumulus", "Cumulus congestus"))
PRODUCT_FRAMES = 4
WHITE = 0.7486                                              # the stage's white (the scene sun's display value)
GAMMA = 2.2
AMBIENT_TOP = [0.2357, 0.2432, 0.2470]
AMBIENT_BOTTOM = [0.1178, 0.1216, 0.1235]
SOFTNESS = 0.5
# E40FAR_QUICK=1: only the product's march, no chain variants (an aerial perspective comparison in a few minutes). E40FAR_TINT=r,g,b: the far in-scatter colour's gain (body keys aerialPerspectiveTintR/G/B; 1,1,1: none).
QUICK = os.environ.get("E40FAR_QUICK") == "1"
AERIAL_TINT = np.array([float(x) for x in os.environ.get("E40FAR_TINT", "1,1,1").split(",")])
# The sky the pictures' backdrop shows: the game's own (K-T2 start 61, ground_fair off frame, display values at rows 740 and 300 of 960: the horizon and 55 degrees up).
GAME_SKY_HORIZON = np.array([0.663, 0.784, 0.82])
GAME_SKY_ZENITH = np.array([0.184, 0.349, 0.561])
PRODUCT = dict(maxSteps=48, minStep=25.0, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1, stepGrowth=0.0002, accumulate=PRODUCT_FRAMES)
CONVERGED = dict(maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3, productCost=0, stepGrowth=0.0, accumulate=1)
MARCHES = (("prod", PRODUCT),) if QUICK else (("conv", CONVERGED), ("prod", PRODUCT))

# The chain variants (id token after the march name): name -> (job fields, scene tokens in the id). 'conv' and 'prod' are the two marches themselves.
VARIANTS = {
    "oct1": ("conv", {}),                                        # one octave: no multiple scattering
    "ao0": ("conv", {}),                                         # no ambient occlusion and no sky diffusion
    "diff0": ("conv", {}),                                       # no deep-core diffusion
    "nr": ("prod", dict(costCut=0.0, lightStride=1)),            # the product's march with no reuse of the sun march
    "long": ("prod", dict(sunMarch=9000.0)),                     # the long sun march
    "s6": ("prod", dict(sunSteps=6)),                            # six sun taps
    "g0": ("prod", dict(stepGrowth=0.0)),                        # no step growth with distance
    "m96": ("prod", dict(maxSteps=96)),                          # 96 grid points per span
}
# E40FAR_AERIAL: after = the aerial perspective of E40 T1 at Scatterer's air of Kerbin (the law before E40 H1, with E40FAR_TINT as its gain); sky = the colours worked out from the air (E40 H1: the
# product); E40FAR_BACKDROP=model: the sky behind the pictures is the model's at the sun and the camera, not the game's measured one of start 61 (44 degrees).
AERIAL = os.environ.get("E40FAR_AERIAL")
BACKDROP_MODEL = os.environ.get("E40FAR_BACKDROP") == "model"
TAG = os.environ.get("E40FAR_TAG")
NAME = "e40-fl" + ("-" + TAG if TAG else "")
SC_RAYLEIGH = (0.0153407799e-3, 0.0357074998e-3, 0.0875488967e-3)
SC_MIE, SC_HR, SC_HM = 0.0121406196e-3 / 0.9, 3840.0, 1000.0
LAYER_EXTRA = ("softnessScale", "waterPower", "lightAmbient", "filamentStretch", "lightIsotropy", "verticalScale", "ambientMs", "ambientDecay", "lightPlateau", "shapeSmoothing",
               "filamentMinWindMs", "filamentTurnSeconds", "topReach", "topRefMs", "topFullMs", "lightCellWidth")
TYPE_EXTRA = ("baseGain", "riseShare", "densityScale", "shapeSmoothing", "topClose")   # the per-type light factors are deleted (2026-11-24)
CFG_PATH = os.environ.get("E40FAR_CFG") or e40o.CFG
# E40FAR_TUNING=key=value,key=value: the job's tuning keys (the look knobs of the game), E40FAR_LAYER=key=value,...: layer keys of the job (for a cfg candidate without editing the cfg).
TUNING = [dict(key=k, value=float(v)) for k, v in (p.split("=") for p in os.environ.get("E40FAR_TUNING", "").split(",") if p)]
LAYER_OVERRIDE = {k: float(v) for k, v in (p.split("=") for p in os.environ.get("E40FAR_LAYER", "").split(",") if p)}
TYPE_OVERRIDE = {k: float(v) for k, v in (p.split("=") for p in os.environ.get("E40FAR_TYPE", "").split(",") if p)}


def lift_samples(type_node):
    for c in type_node["children"]:
        if c["name"] == "LIFT":
            keys = [tuple(float(x) for x in v.split()) for _, v in c["values"]]
            return [float(v) for v in np.interp(np.linspace(0.0, 1.0, 128), [k[0] for k in keys], [k[1] for k in keys])]
    return None


def cfg_extras():
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
        out[e40o.val(layer, "name")] = (lk, tk)
    return out


def target_position(altitude):
    lon = math.radians(SITE_LON_DEG)
    return np.array([math.cos(lon), math.sin(lon), 0.0]) * (R + altitude)


def camera_for(distance_km, target_altitude, size):
    """The camera CAMERA_HEIGHT above the sea, the cell's centre distance_km + EDGE_KM to its south (along the meridian), looking north, the cell's middle in the picture's middle."""
    lat = -(distance_km + EDGE_KM) * 1000.0 / R
    lon = math.radians(SITE_LON_DEG)
    pos = np.array([math.cos(lat) * math.cos(lon), math.cos(lat) * math.sin(lon), math.sin(lat)]) * (R + CAMERA_HEIGHT)
    to = target_position(target_altitude) - pos
    up = pos / np.linalg.norm(pos)
    pitch = math.degrees(math.asin(float(to @ up) / np.linalg.norm(to)))
    return pr.Camera(pos, 0.0, max(min(pitch, 35.0), -5.0), size[0], size[1], FOV)


def cell_maps():
    """The cell's coverage map (a Gaussian of the great-circle distance to the cell's centre, 0 outside to 1 in the core) and its constant type maps."""
    w, h = MAP
    units = lay.texel_unit_vectors(w, h)
    lon = math.radians(SITE_LON_DEG)
    centre = np.array([math.cos(lon), math.sin(lon), 0.0])
    dist_km = np.arccos(np.clip(units @ centre, -1.0, 1.0)) * R / 1000.0
    cover = np.rint(np.clip(np.minimum(1.0, CORE_GAIN * np.exp(-(dist_km / SIGMA_KM) ** 2)), 0.0, 1.0) * 255.0).astype(np.uint8)
    os.makedirs(INPUT, exist_ok=True)
    cover.tofile(os.path.join(INPUT, "cell-cov.r8"))
    return "cell-cov.r8"


def type_map(value):
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    name = "cell-typ%03d.r8" % v
    os.makedirs(INPUT, exist_ok=True)
    np.full(MAP[0] * MAP[1], v, dtype=np.uint8).tofile(os.path.join(INPUT, name))
    return name


def type_value(layer, name):
    names = [t["name"] for t in layer["types"]]
    return names.index(name) / float(max(len(names) - 1, 1))


def target_altitude(layer, type_name):
    t = [x for x in layer["types"] if x["name"] == type_name][0]
    return layer["inner"] + (layer["outer"] - layer["inner"]) * 0.5 * (t["baseShare"] + t["topShare"])


def view_azimuth(sun):
    """The camera of the sheets and of the band looks north: the sun's azimuth from the view, folded to 0 to 180 degrees."""
    az = abs(SUNS[sun][0]) % 360.0
    return 360.0 - az if az > 180.0 else az


def aerial_perspective_block(layer, sun="hi"):
    """The far block of a job with Scatterer's air. E40FAR_AERIAL=after: the in-scatter of E40 T1 (short-path colour the sun's own, the horizon's 0.35 blue, E40FAR_TINT its gain); sky: the colours
    worked out from the air at this sun (E40 H1 aerial perspective, horizon_sky.py: the product)."""
    if not AERIAL:
        return None
    ray = np.array(SC_RAYLEIGH)
    layer_altitude = layer["inner"] + 0.25 * (layer["outer"] - layer["inner"])
    tint = white_tint(layer_altitude, SUNS[sun][1])
    air = dict(aerialPerspective=1, shapeMean=0, policy=0, minSamples=0, rayleigh=list(SC_RAYLEIGH), mie=SC_MIE, rayleighHeight=SC_HR, mieHeight=SC_HM, aerialPerspectiveStrength=1.0)
    if AERIAL == "sky":
        far, near, _ = hs.far_near(hs.KERBIN, CAMERA_HEIGHT, layer_altitude, SUNS[sun][1], view_azimuth(sun), WHITE, AMBIENT_TOP)
        return dict(air, inscatter=[float(x) for x in np.array(far) * tint], inscatterNear=[float(x) for x in np.array(near) * tint])

    def inscatter(exponent):
        return np.maximum(2.35 * 0.23 * (ray / ray.mean()) ** exponent, np.array(AMBIENT_TOP))

    return dict(air, inscatter=[float(x) for x in inscatter(0.35) * tint * AERIAL_TINT], inscatterNear=[float(x) for x in inscatter(0.0) * tint])


def job_id(cloud, sun, km, march, variant=None):
    return "fl-%s-%s-d%d-%s%s" % (cloud, sun, km, march, "-" + variant if variant else "")


def build_jobs():
    layers_by_name = {s["name"]: s for s in e40o.kerbin_layers(CFG_PATH)}
    extras = cfg_extras()
    cov = cell_maps()
    base, variants = [], []
    for cloud, layer_name, type_name in CLOUDS:
        layer = layers_by_name[layer_name]
        lk, tk = extras[layer_name]
        alt = target_altitude(layer, type_name)
        for sun, (az, el) in SUNS.items():
            sun_vec = lay.sun_vector(target_position(alt), az, el)
            for km in DISTANCES_KM:
                cam = camera_for(km, alt, SIZE)
                for march, numbers in MARCHES:
                    jobs_for = [(None, march, numbers)]
                    if cloud == "congestus" and sun in VARIANT_SUNS and not QUICK:
                        jobs_for += [(v, spec[0], spec[1]) for v, spec in VARIANTS.items() if spec[0] == march]
                    for variant, _, extra in jobs_for:
                        jl = e40o.job_layer(layer, cov, type_map(type_value(layer, type_name)))
                        jl.update(lk)
                        jl.update(LAYER_OVERRIDE)
                        for t in jl["types"]:
                            t.update(tk.get(t["name"], {}))
                            if t["name"] == type_name:
                                t.update(TYPE_OVERRIDE)
                        numbers_here = dict(numbers)
                        numbers_here.update(extra if variant else {})
                        j = lay.job(job_id(cloud, sun, km, march, variant), "lum", cam, [jl], SIZE[0], SIZE[1], planet=R, mapWidth=MAP[0], mapHeight=MAP[1], sun=sun_vec,
                                    sunColour=[2.35, 2.35, 2.35], ambientTop=AMBIENT_TOP, ambientBottom=AMBIENT_BOTTOM, soft=SOFTNESS, tuning=TUNING, **numbers_here,
                                    **(dict(far=aerial_perspective_block(layer, sun)) if AERIAL else {}))
                        (variants if variant else base).append(j)
    # the band: a field of the type (constant coverage map) seen from the ground along the horizon, the clouds of every distance in one picture
    band_cov = "band-cov.r8"
    np.full(MAP[0] * MAP[1], int(round(BAND_COVER * 255.0)), dtype=np.uint8).tofile(os.path.join(INPUT, band_cov))
    for cloud, layer_name, type_name in CLOUDS:
        layer = layers_by_name[layer_name]
        lk, tk = extras[layer_name]
        for sun in BAND_SUNS:
            az, el = SUNS[sun]
            cam = band_camera(SIZE)
            sun_vec = lay.sun_vector(cam.pos, az, el)
            for march, numbers in MARCHES:
                jl = e40o.job_layer(layer, band_cov, type_map(type_value(layer, type_name)))
                jl.update(lk)
                jl.update(LAYER_OVERRIDE)
                for t in jl["types"]:
                    t.update(tk.get(t["name"], {}))
                    if t["name"] == type_name:
                        t.update(TYPE_OVERRIDE)
                base.append(lay.job("fl-band-%s-%s-%s" % (cloud, sun, march), "lum", cam, [jl], SIZE[0], SIZE[1], planet=R, mapWidth=MAP[0], mapHeight=MAP[1], sun=sun_vec,
                                    sunColour=[2.35, 2.35, 2.35], ambientTop=AMBIENT_TOP, ambientBottom=AMBIENT_BOTTOM, soft=SOFTNESS, tuning=TUNING, **numbers,
                                    **(dict(far=aerial_perspective_block(layer, sun)) if AERIAL else {})))
    return base, variants


BAND_COVER = float(os.environ.get("E40FAR_BANDCOVER", 0.5))
BAND_SUNS = tuple(s for s in ("hi", "fw", "bk") if s in SUNS)
BAND_PITCH = 6.0
BAND_ELEVATIONS = (0.0, 2.0, 4.0, 8.0, 16.0, 40.0)


def band_camera(size):
    return pr.Camera(target_position(CAMERA_HEIGHT), 0.0, BAND_PITCH, size[0], size[1], FOV)


def write_inputs():
    base, variants = build_jobs()
    os.makedirs(INPUT, exist_ok=True)
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": base + variants}, f)
    return base, variants


# --------------------------------------------------------------------------------------------------------------------------------
# Display (the output stage, as e40ty.py)
# --------------------------------------------------------------------------------------------------------------------------------
KERBIN_RAYLEIGH_TAU = np.array([0.01534, 0.03571, 0.08755]) * 3.84
KERBIN_MIE_TAU = 0.01214 / 0.9
RAYLEIGH_H, MIE_H = 3840.0, 1000.0
LUMA = np.array([0.2126, 0.7152, 0.0722])


def sun_air(sin_el, altitude):
    s = min(max(sin_el, -1.0), 1.0)
    elev = math.degrees(math.asin(s))
    m = 1.0 / (max(s, 0.0) + 0.50572 * max(elev + 6.07995, 0.05) ** -1.6364)
    tau = KERBIN_RAYLEIGH_TAU * math.exp(-altitude / RAYLEIGH_H) + KERBIN_MIE_TAU * math.exp(-altitude / MIE_H)
    return np.exp(-tau * m)


def white_tint(altitude, elevation=45.6):
    sin_el = math.sin(math.radians(elevation))
    sun = sun_air(sin_el, altitude)
    t = min(max((sin_el - 0.05) / 0.35, 0.0), 1.0)
    share = t * t * (3.0 - 2.0 * t)
    return 1.0 + (sun / float(sun @ LUMA) - 1.0) * share


def encode(c, tint):
    return WHITE * np.power(np.maximum(c / (WHITE * tint), 0.0), 1.0 / GAMMA)


MODEL_ELEVATIONS = (-0.9, 0.0, 1.0, 2.0, 4.0, 8.0, 16.0, 30.0, 55.0, 90.0)
_model_sky = {}


def model_sky(sun):
    """The model's sky (display values, camera at CAMERA_HEIGHT, view toward the north) at the ray elevations MODEL_ELEVATIONS above the horizontal, for this sun: the table the backdrop reads."""
    if sun not in _model_sky:
        m = hs.medium(hs.KERBIN)
        rows = []
        for el in MODEL_ELEVATIONS:
            far, _ = hs.evaluate(m, CAMERA_HEIGHT, math.radians(el), math.radians(SUNS[sun][1]), math.radians(view_azimuth(sun)))
            rows.append(hs.display(far, m["sky_exposure"]))
        _model_sky[sun] = np.array(rows)
    return _model_sky[sun]


def backdrop(cam, sun="hi"):
    d = cam.ray_directions().reshape(cam.height, cam.width, 3)
    up = cam.pos / np.linalg.norm(cam.pos)
    dip = math.acos(R / np.linalg.norm(cam.pos))
    elev = np.degrees(np.arcsin(np.clip(d @ up, -1.0, 1.0)) + dip)
    if BACKDROP_MODEL:
        table = model_sky(sun)
        ray = np.degrees(np.arcsin(np.clip(d @ up, -1.0, 1.0)))
        sky = np.stack([np.interp(ray, MODEL_ELEVATIONS, table[:, c]) for c in range(3)], axis=-1)
    else:
        t = np.clip(elev / 60.0, 0.0, 1.0)[..., None] ** 0.5
        sky = GAME_SKY_HORIZON * (1.0 - t) + GAME_SKY_ZENITH * t
    w = np.clip((elev + 1.0) / 2.0, 0.0, 1.0)[..., None]
    return np.clip(sky * w + gn.SEA * (1.0 - w), 0.0, 1.0)


def picture(m, cam, tint, sun="hi"):
    a = np.clip(m[..., 3:4], 0.0, 1.0)
    colour = encode(m[..., :3] / np.maximum(a, 1e-4), tint)
    return np.clip(colour * a + backdrop(cam, sun) * (1.0 - a), 0.0, 1.0)


def load(folder, jid, kind):
    path = os.path.join(folder, "%s-%s.npy" % (jid, kind))
    return np.load(path).astype(np.float64) if os.path.isfile(path) else None


def label(img, text):
    from PIL import Image, ImageDraw
    pil = Image.fromarray(np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8), "RGB")
    ImageDraw.Draw(pil).rectangle([0, 0, pil.width, 14], fill=(0, 0, 0))
    ImageDraw.Draw(pil).text((3, 1), text, fill=(255, 255, 255))
    return np.asarray(pil, dtype=np.float64) / 255.0


def measure(folder, jid, tint):
    """The measures of one picture over its closed part; None where the picture or its closed part is missing."""
    lum, aux = load(folder, jid, "lum"), load(folder, jid, "aux")
    if lum is None:
        return None
    a = lum[..., 3]
    closed = a > 0.9
    covered = a > 0.03
    out = dict(cover=float(covered.mean()), closed=float(closed.mean()), n_closed=int(closed.sum()))
    if closed.sum() < 150:
        return out
    neutral = lum[..., :3] / np.maximum(a[..., None], 1e-4)
    rel = neutral[closed] @ LUMA / WHITE
    shown = encode(neutral[closed], tint) @ LUMA
    out.update(rel=[float(x) for x in np.percentile(rel, [10, 50, 90, 99])], display=[float(x) for x in np.percentile(shown, [10, 50, 90, 99])],
               blue_red=float(np.median(encode(neutral[closed], tint)[:, 2] / np.maximum(encode(neutral[closed], tint)[:, 0], 1e-6))))
    if aux is not None:
        out["first_km_p10_p50"] = [float(np.percentile(aux[..., 3][closed], q)) / 1000.0 for q in (10, 50)]
        out["tau_p50"] = float(np.median(aux[..., 1][closed]))
        out["steps_mean"] = float(aux[..., 2][covered].mean()) if covered.any() else 0.0
    return out


def composite_words(lum, cam, tint, sun="hi"):
    """The cloud composited over the backdrop where alpha is above 0.5: mean rgb and luma, the backdrop's over the same pixels, and the position of green between red and blue ((G-R)/(B-R))."""
    a = lum[..., 3]
    sel = a > 0.5
    img = picture(lum, cam, tint, sun)
    sky = backdrop(cam, sun)
    mean, sky_mean = img[sel].mean(0), sky[sel].mean(0)
    return dict(rgb=[float(x) for x in mean], luma=float(mean @ LUMA), sky_rgb=[float(x) for x in sky_mean], sky_luma=float(sky_mean @ LUMA),
                hue=float((mean[1] - mean[0]) / (mean[2] - mean[0])), sky_hue=float((sky_mean[1] - sky_mean[0]) / (sky_mean[2] - sky_mean[0])))


def collect(ctx):
    base_folder, var_folder = ctx.folder("base"), ctx.folder("variants")
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    layers_by_name = {s["name"]: s for s in e40o.kerbin_layers(CFG_PATH)}
    metrics, lines, results = [], ["E40-FL-FAR: one cumulus cell, camera %.0f m above the sea at %s km from its near side (centre + %.1f km), cell sigma %.1f km, soft %.1f"
                                   % (CAMERA_HEIGHT, "/".join(str(k) for k in DISTANCES_KM), EDGE_KM, SIGMA_KM, SOFTNESS)], {}
    tiles = {}
    for cloud, layer_name, type_name in CLOUDS:
        layer = layers_by_name[layer_name]
        alt = target_altitude(layer, type_name)
        for sun, (az, el) in SUNS.items():
            tint = white_tint(layer["inner"] + 0.25 * (layer["outer"] - layer["inner"]), el)
            for march in ("conv", "prod"):
                row = []
                for km in DISTANCES_KM:
                    jid = job_id(cloud, sun, km, march)
                    m = measure(base_folder, jid, tint)
                    results.setdefault(jid, m)
                    cam = camera_for(km, alt, SIZE)
                    lum = load(base_folder, jid, "lum")
                    if lum is not None:
                        img = picture(lum[::1], cam, tint, sun)
                        gn.save_png(os.path.join(out_dir, "%s-%s-%s-d%d-%s.png" % (NAME, cloud, sun, km, march)), img)
                        row.append(label(img, "%s %s %s %d km" % (cloud, sun, march, km)))
                    if m is None:
                        lines.append("   %-34s no picture" % jid)
                    elif "rel" not in m:
                        lines.append("   %-34s cover %.3f, closed %.3f (%d px): too small a closed part" % (jid, m["cover"], m["closed"], m["n_closed"]))
                    else:
                        lines.append("   %-34s cover %.3f closed %.3f  rel p10/50/90/99 %.2f %.2f %.2f %.2f  display p50/p90 %.2f %.2f  first %.1f km  tau p50 %.1f  steps %.0f"
                                     % (jid, m["cover"], m["closed"], m["rel"][0], m["rel"][1], m["rel"][2], m["rel"][3], m["display"][1], m["display"][2],
                                        m.get("first_km_p10_p50", [0, float("nan")])[1], m.get("tau_p50", float("nan")), m.get("steps_mean", float("nan"))))
                        metrics.append(Metric.info("e40far.%s.rel_p90" % jid, m["rel"][2]))
                        if AERIAL and lum is not None:
                            c = composite_words(lum, cam, tint, sun)
                            results[jid]["composite"] = c
                            lines.append("   %-34s AERIAL PERSPECTIVE: cloud over the sky (alpha above 0.5) rgb %.3f %.3f %.3f luma %.3f, sky behind it rgb %.3f %.3f %.3f luma %.3f, (G-R)/(B-R) %.2f against %.2f"
                                         % ((jid,) + tuple(c["rgb"]) + (c["luma"],) + tuple(c["sky_rgb"]) + (c["sky_luma"], c["hue"], c["sky_hue"])))
                if row:
                    tiles[(cloud, sun, march)] = np.concatenate(row, axis=1)
    # one sheet per cloud and sun: the conv row over the prod row
    for cloud, _, _ in CLOUDS:
        for sun in SUNS:
            rows = [tiles[(cloud, sun, march)] for march in ("conv", "prod") if (cloud, sun, march) in tiles]
            if rows:
                gn.save_png(os.path.join(out_dir, "%s-%s-%s-sheet.png" % (NAME, cloud, sun)), np.concatenate(rows, axis=0))
    # the band: the field along the horizon, by the elevation of the pixel's ray
    cam = band_camera(SIZE)
    elev = np.degrees(np.arcsin(np.clip(cam.ray_directions().reshape(SIZE[1], SIZE[0], 3) @ (cam.pos / np.linalg.norm(cam.pos)), -1.0, 1.0)))
    for cloud, layer_name, type_name in CLOUDS:
        layer = layers_by_name[layer_name]
        for sun in BAND_SUNS:
            tint = white_tint(layer["inner"] + 0.25 * (layer["outer"] - layer["inner"]), SUNS[sun][1])
            for march in ("conv", "prod"):
                jid = "fl-band-%s-%s-%s" % (cloud, sun, march)
                lum, aux = load(base_folder, jid, "lum"), load(base_folder, jid, "aux")
                if lum is None:
                    lines.append("   %-34s no picture" % jid)
                    continue
                img = picture(lum, cam, tint, sun)
                gn.save_png(os.path.join(out_dir, "%s-band-%s-%s-%s.png" % (NAME, cloud, sun, march)), img)
                a = lum[..., 3]
                neutral = lum[..., :3] / np.maximum(a[..., None], 1e-4)
                shown = encode(neutral, tint) @ LUMA
                parts = []
                for lo, hi in zip(BAND_ELEVATIONS[:-1], BAND_ELEVATIONS[1:]):
                    sel = (elev >= lo) & (elev < hi) & (a > 0.5)
                    if sel.sum() < 100:
                        parts.append("%g-%g: -" % (lo, hi))
                        continue
                    parts.append("%g-%g deg: n %d display p50 %.2f p90 %.2f first %.1f km" % (lo, hi, sel.sum(), np.percentile(shown[sel], 50), np.percentile(shown[sel], 90),
                                                                                              np.median(aux[..., 3][sel]) / 1000.0))
                    results.setdefault(jid + "-band", {})["%g-%g" % (lo, hi)] = dict(n=int(sel.sum()), p50=float(np.percentile(shown[sel], 50)), p90=float(np.percentile(shown[sel], 90)),
                                                                                    first_km=float(np.median(aux[..., 3][sel]) / 1000.0))
                lines.append("   %-34s %s" % (jid, " | ".join(parts)))
    # the chain variants of the congestus at the high sun
    layer = layers_by_name["Kerbin-cumulus"]
    tint = white_tint(layer["inner"] + 0.25 * (layer["outer"] - layer["inner"]), SUNS["hi"][1])
    for sun, variant, km in [(s, v, k) for s in VARIANT_SUNS for v in VARIANTS for k in DISTANCES_KM]:
        march = VARIANTS[variant][0]
        if True:
            jid = job_id("congestus", sun, km, march, variant)
            m = measure(var_folder, jid, tint)
            results.setdefault(jid, m)
            if m is None or "rel" not in m:
                lines.append("   %-34s %s" % (jid, "no picture" if m is None else "too small a closed part"))
                continue
            lines.append("   %-34s rel p10/50/90/99 %.2f %.2f %.2f %.2f  display p50/p90 %.2f %.2f  first %.1f km  steps %.0f"
                         % (jid, m["rel"][0], m["rel"][1], m["rel"][2], m["rel"][3], m["display"][1], m["display"][2],
                            m.get("first_km_p10_p50", [0, float("nan")])[1], m.get("steps_mean", float("nan"))))
            metrics.append(Metric.info("e40far.%s.rel_p90" % jid, m["rel"][2]))
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


def stages():
    # Always written afresh: the environment switches are read here and do not move the script's time stamp.
    base, variants = write_inputs()
    in_base, in_var = INPUT + "-base", INPUT + "-var"
    for folder, jobs in ((in_base, base), (in_var, variants)):
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, "jobs.json"), "w", encoding="utf-8") as f:
            json.dump({"jobs": jobs}, f)
        for name in os.listdir(INPUT):
            if name.endswith(".r8"):
                target = os.path.join(folder, name)
                if not os.path.isfile(target) or os.path.getmtime(target) < os.path.getmtime(os.path.join(INPUT, name)):
                    with open(os.path.join(INPUT, name), "rb") as src, open(target, "wb") as dst:
                        dst.write(src.read())
    return [Stage("base", ["-mode", "scene", "-scene", "far-sheet", "-in", in_base], size=(1024, 576), proxy=False, timeout=3000),
            Stage("variants", ["-mode", "scene", "-scene", "far-sheet", "-in", in_var], size=(1024, 576), proxy=False, timeout=3000)]


register_series("e40-fl-far", stages(), collect, exclusive=False, who="e40-fl-far")


def selftest():
    base, variants = build_jobs()
    ids = [j["id"] for j in base + variants]
    assert len(ids) == len(set(ids)), "job ids are unique"
    assert len(base) == len(CLOUDS) * len(SUNS) * len(DISTANCES_KM) * 2 + len(CLOUDS) * len(BAND_SUNS) * 2, len(base)
    assert len(variants) == len(VARIANTS) * len(DISTANCES_KM) * len(VARIANT_SUNS), len(variants)
    assert all(j["id"].startswith("fl-") for j in base + variants)
    # the camera looks at the cell: the cell's middle lies in the picture's middle, and the distance to the cell's centre is the label's plus the edge
    layers = {s["name"]: s for s in e40o.kerbin_layers(CFG_PATH)}
    layer = layers["Kerbin-cumulus"]
    alt = target_altitude(layer, "Cumulus congestus")
    for km in DISTANCES_KM:
        cam = camera_for(km, alt, SIZE)
        to = target_position(alt) - cam.pos
        assert np.dot(cam.forward, to / np.linalg.norm(to)) > 0.97 if km >= 6 else True, km
        ground = math.acos(float(cam.pos @ target_position(0.0)) / np.linalg.norm(cam.pos) / R) * R
        assert abs(ground / 1000.0 - (km + EDGE_KM)) < 0.01 * (km + EDGE_KM), (km, ground)
    cov = np.fromfile(os.path.join(INPUT, "cell-cov.r8"), dtype=np.uint8).reshape(MAP[1], MAP[0])
    rows, cols = np.where(cov == cov.max())
    assert abs(((cols.mean() + 0.5) / MAP[0] * 360.0 - 180.0) - SITE_LON_DEG) < 360.0 / MAP[0] and abs(rows.mean() + 0.5 - MAP[1] / 2.0) <= 1.0, (rows.mean(), cols.mean())
    # the variants carry their numbers
    nr = [j for j in variants if j["id"] == job_id("congestus", "hi", 6, "prod", "nr")][0]
    assert nr["costCut"] == 0.0 and nr["lightStride"] == 1 and nr["maxSteps"] == 48 and nr["accumulate"] == PRODUCT_FRAMES
    long_ = [j for j in variants if j["id"] == job_id("congestus", "hi", 6, "prod", "long")][0]
    assert long_["sunMarch"] == 9000.0
    tint = white_tint(3000.0)
    assert abs(float(tint @ LUMA) - 1.0) < 1e-6
    print("selftest passed (%d + %d jobs)" % (len(base), len(variants)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--write", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.write:
        print([len(x) for x in write_inputs()], "jobs")
