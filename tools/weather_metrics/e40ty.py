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
    ("cumulonimbus", "calvus", "Kerbin-cumulonimbus", "Cumulonimbus calvus", 0.78, (GROUND, 13500.0), (25.0, -30.0)),
    ("cumulonimbus", "incus", "Kerbin-cumulonimbus", "Cumulonimbus incus", 0.78, (GROUND, 13500.0), (25.0, -30.0)),
    ("cirrus", "cirrus", "Kerbin-cirrus", "Cirrus", 0.65, (GROUND, 12500.0), (35.0, -35.0)),
    ("cirrus", "cirrostratus", "Kerbin-cirrus", "Cirrostratus", 0.8, (GROUND, 12500.0), (35.0, -35.0)),
]
# The comparison tiles of True Volumetric Clouds (game frames of the K-T2 series, its own weather): the family they are put beside.
TVC_DIR = os.path.join("C:", os.sep, "Users", "franz", "IdeaProjects", "ReDefinition", ".local", "work", "series-pictures", "K-T2")
TVC = {"cumulus": "t09-tvc1707-ground-tvc-1707x960-natural.png", "stratocumulus": "t09-tvc1707-above-tvc-1707x960-natural.png"}
FAMILIES = ("cumulus", "stratocumulus", "stratus", "fog", "cumulonimbus", "cirrus")
VIEWS = ("ground", "above", "sun")

# E40TY_NOLIGHT=1: the same pictures with every light factor of the cfg at 1 (the control of the type and layer light response), under the names e40-ty-nolight-*.
NOLIGHT = os.environ.get("E40TY_NOLIGHT") == "1"
# E40TY_SHAPE=<scale>: the same pictures with layers.profileShape at that scale (the types' height-shaped threshold times it; 0: none, and then no fibre stretch either), under the names
# e40-ty-shape<scale>-* (the control and the sweep of the shape keys of E40 L2 D1).
SHAPE_SCALE = float(os.environ["E40TY_SHAPE"]) if os.environ.get("E40TY_SHAPE") else None
NOSHAPE = SHAPE_SCALE == 0.0
# E40TY_SPHERE=<scale>: the noise lattice's vertical scale (the body key sphereScale; 1: as today, below 1 the features are longer in the vertical), under the names e40-ty-sphere<scale>-*.
SPHERE = float(os.environ["E40TY_SPHERE"]) if os.environ.get("E40TY_SPHERE") else None
NAME = ("e40-ty-nolight" if NOLIGHT else "e40-ty-shape%g" % SHAPE_SCALE if SHAPE_SCALE is not None else "e40-ty") + ("-sphere%g" % SPHERE if SPHERE is not None else "")

LAYER_EXTRA = ("softnessScale", "waterPower", "lightAmbient", "fibreStretch", "lightIsotropy")
TYPE_EXTRA = ("baseGain", "riseShare", "densityScale", "baseLight", "headLight", "baseAmbient", "headAmbient")


def lift_samples(type_node):
    """The type's LIFT node (E40 L2 D1) sampled at the table's 128 points; None where the type has none."""
    for c in type_node["children"]:
        if c["name"] == "LIFT":
            keys = [tuple(float(x) for x in v.split()) for _, v in c["values"]]
            return [float(v) for v in np.interp(np.linspace(0.0, 1.0, 128), [k[0] for k in keys], [k[1] for k in keys])]
    return None


def cfg_extras():
    """{layer: ({key: float}, {type name: {key: float, 'lift': [128 floats]}})} of the soft, light and shape keys of the shipped Clouds-Kerbin.cfg."""
    with open(e40o.CFG, encoding="utf-8") as f:
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
    jl.update({k: v for k, v in lk.items() if not (NOLIGHT and k.startswith("light")) and not (NOSHAPE and k == "fibreStretch")})
    for t in jl["types"]:
        t.update({k: v for k, v in tk.get(t["name"], {}).items() if not (NOLIGHT and k in ("baseLight", "headLight", "baseAmbient", "headAmbient")) and not (NOSHAPE and k == "lift")})
    return jl


def jid(key, view, aux=False):
    return ("tx-%s-%s" if aux else "ty-%s-%s") % (key, view)


def build_jobs():
    layers_by_name = {s["name"]: s for s in e40o.kerbin_layers()}
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
                                      tuning=[dict(key="layers.profileShape", value=SHAPE_SCALE)] if SHAPE_SCALE is not None else [],
                                      **(dict(noise=dict(sphere=SPHERE)) if SPHERE is not None else {})))
    return lum, aux


def write_inputs():
    lum, aux = build_jobs()
    for folder, jobs in ((INPUT, lum), (INPUT_AUX, aux)):
        os.makedirs(folder, exist_ok=True)
        with open(os.path.join(folder, "jobs.json"), "w", encoding="utf-8") as f:
            json.dump({"jobs": jobs}, f)
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


def collect(ctx):
    sheet = ctx.folder("sheet")
    aux = ctx.folder("aux")
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    layers_by_name = {s["name"]: s for s in e40o.kerbin_layers()}
    metrics, lines, results = [], ["E40-TY: every type of the shipped cfg, one layer at a time, converged, soft %.1f; the white tint is that of the sun at the lower quarter of the layer"
                                   % SOFTNESS], {}
    tiles = {}
    for spec in TYPES:
        family, key = spec[0], spec[1]
        layer = layers_by_name[spec[2]]
        tint = white_tint(layer["inner"] + 0.25 * (layer["outer"] - layer["inner"]))   # CloudOutputStage.NominalCloudAltitudeM
        results.setdefault(key, {})["white_tint"] = [float(x) for x in tint]
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
        apath = os.path.join(aux, jid(key, "ground", True) + "-tau.npy")
        if os.path.isfile(apath):
            tau = np.load(apath).astype(np.float64)
            cam = view_camera(spec, "ground", AUX_SIZE)
            row["aid"] = box(heat_picture(tau, cam), TILE)
            gn.save_png(os.path.join(out_dir, "%s-%s-%s-aid.png" % (NAME, family, key)), row["aid"])
            results.setdefault(key, {})["tau_ground_p10_p50_p90"] = [float(x) for x in np.percentile(tau[tau > 0.03], [10, 50, 90])] if (tau > 0.03).any() else None
        tiles[key] = row
    # The sheets: one per family, a row per type: ground | above | sun | the sky behind it | optical depth | (TVC)
    for family in FAMILIES:
        rows = []
        for spec in [s for s in TYPES if s[0] == family]:
            key = spec[1]
            r = tiles.get(key, {})
            cols = []
            for name, text in (("ground", "ground"), ("above", "from above"), ("sun", "toward the sun"), ("sky", "the sky without the cloud"), ("aid", "optical depth (blue thin, green 1, red thick)")):
                cols.append(label(r[name], "%s: %s" % (key, text)) if name in r else np.zeros((TILE[1], TILE[0], 3)))
            rows.append(np.concatenate(cols, axis=1))
        if family in TVC and os.path.isfile(os.path.join(TVC_DIR, TVC[family])):
            tvc = label(box(load_png(os.path.join(TVC_DIR, TVC[family])), TILE), "True Volumetric Clouds, game frame (its own weather)")
            pad = np.zeros((TILE[1], TILE[0] * 4, 3))
            rows.append(np.concatenate([tvc, pad], axis=1))
        if rows:
            gn.save_png(os.path.join(out_dir, "%s-%s-sheet.png" % (NAME, family)), np.concatenate(rows, axis=0))
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
    assert len(lum) == len(TYPES) * len(VIEWS) and len(aux) == len(TYPES) * 2
    layers = {s["name"]: s for s in e40o.kerbin_layers()}
    for spec in TYPES:
        names = [t["name"] for t in layers[spec[2]]["types"]]
        assert spec[3] in names, spec[3]
    # the cfg's soft and light keys reach the job: the Stratus type carries its density scale, the cumulus layer its light factors
    stratus = [j for j in lum if j["id"] == "ty-stratus-ground"][0]["layers"][0]
    assert abs([t for t in stratus["types"] if t["name"] == "Stratus"][0]["densityScale"] - 0.22) < 1e-6
    assert stratus["softnessScale"] == 1.5 and lum[0]["soft"] == SOFTNESS
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
