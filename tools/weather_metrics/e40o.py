"""E40 Phase O: the offline sweeps of the cloud look in the weather player (no game start).

  python tools/build_weather_player.py --series e40-o        build the player, run the sweeps, print the tables
  python tools/build_weather_player.py --series e40-o --no-build
  python tools/weather_metrics/e40o.py --selftest

The scene 'layers' of the player draws Kerbin's layers of Clouds-Kerbin.cfg (parsed here, not copied) with the pass of the game (shader, type
table, shape noise, soft edge) on the ground frame of K-T2d (264 m above the sea, 18 degrees up, 60 degrees field of view) with constant maps, at
12 sites of the noise 104 km apart. The look knobs of the game (CloudLookTuning: layerSigmaScale, layerSoftnessScale, layerNoise*Scale,
layerCoverageShift.<name>, layers.densityRemap, coverageShift) go through the job's tuning list, i.e. through the code of the game. The type
override of a candidate (cloud.typeFair / typeOverride act in the module's FetchStep, not in the player) is the job's constant type map.

EXPECTED RESULTS, written before the first run (E40-phaseO-result.md has the table of what came out and where it missed):
  P1  C0 (the current numbers) at map 0.665 and shift 0 / -0.08 / -0.15: the player's mean closed share (tau >= 0.92 over the sky above the horizon) equals the CPU oracle's
      ensemble mean (0.73 / 0.55 / 0.36, CloudCoverOracleTests) within 0.10; if it does, the pass and the CPU agree and the game's 0.994 / 0.984 / 0.970 is not the pass at a constant
      map 0.665 (the map along the view must be higher, or something else differs).
  P2  C0 on the rain deck at map 0.859 type 0.229 (camera inside the Fog and Stratus blend): closed share at least 0.95 (the CPU says 1.00; the game drew 0.00). If the player draws it,
      the game's missing deck is not the shader's inside case.
  P3  Candidates (A1: closed share between map - 0.20 and map + 0.15 at maps 0.50 / 0.665 / 0.80 and shift 0, and a fall of at least 0.10 per -0.08 of shift): C0 fails A1 (it is too
      closed at 0.50 and 0.665 and falls too little); C1 alone (humilis) does not satisfy the fall; C3 (interior remap) leaves the closed share as it is (the remap acts on the
      density in the interior, not on the coverage of the sky): it does not help A1; C4 (softness x2.33, sigma x0.7, coverage -0.10, noise x1.57 / size x0.6) satisfies the
      shift part; C5 (C1 + C2 + C3) satisfies A1 best. At most 3 sweeps per constant group: sweep 1 = the six sets, sweep 2 = three softness values on the best, sweep 3 reserved.
  P4  A3 (column cover): the union of the four layers at the low pick (maps 0.406 / 0.665 / 0.110 / 0.501) draws more than the field's cover 0.473 by at least 0.25 under C0, and
      with cloud.coverCombine = 1 (the layer maps scaled so that their random-overlap union is the field's cover) within 0.15 of it.
"""
import argparse
import json
import math
import os
import shutil
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40o-in")
RESULTS = os.path.join(BUILD, "e40o-results.json")
CFG = os.path.join(REPO, "GameData", "ReDefinition", "Atmospheres", "Bodies", "Clouds-Kerbin.cfg")
R = 600000.0
MAP_W, MAP_H = 8, 4
SIZE = (256, 144)
SITES = 8
SITE_STEP_DEG = 10.0
GROUND_H = 264.0
GROUND_PITCH = 18.0
FOV = 60.0
CLOSED = 0.92          # tau of an opacity of 0.6, the picture proxy of K-T2d (T09Readout.ClosedChange)
DEEP = 4.605           # tau of an opacity of 0.99


# --------------------------------------------------------------------------------------------------------------------------------
# Clouds-Kerbin.cfg
# --------------------------------------------------------------------------------------------------------------------------------
def parse_nodes(text):
    """A ConfigNode text as a tree: {'name', 'values': [(key, value)], 'children': [node]}."""
    root = {"name": "", "values": [], "children": []}
    stack = [root]
    pending = None
    for raw in text.splitlines():
        line = raw.split("//")[0].strip()
        if not line:
            continue
        if line == "{":
            node = {"name": pending or "", "values": [], "children": []}
            stack[-1]["children"].append(node)
            stack.append(node)
            pending = None
        elif line == "}":
            stack.pop()
        elif "=" in line:
            k, v = line.split("=", 1)
            stack[-1]["values"].append((k.strip(), v.strip()))
        else:
            pending = line.split()[0].lstrip("@").split("[")[0]
    return root


def find(node, name):
    for c in node["children"]:
        if c["name"] == name:
            return c
        r = find(c, name)
        if r is not None:
            return r
    return None


def val(node, key, default=None):
    for k, v in node["values"]:
        if k == key:
            return v
    return default


def kerbin_layers(cfg=CFG):
    """The four layers of Kerbin's stack (rain deck, cumulus, cumulonimbus, cirrus) from the cfg as dicts of the job: name, group, geometry, numbers, types with
    their profile sampled at 128 points (the table's resolution). The snow deck is the rain deck's alternative of the same group and is not in the stack."""
    with open(cfg, encoding="utf-8") as f:
        root = parse_nodes(f.read())
    clouds = find(root, "CLOUDS")
    out = []
    for layer in [c for c in clouds["children"] if c["name"] == "LAYER"]:
        if val(layer, "name") == "Kerbin-weather-snow":
            continue
        types = []
        for t in [c for c in layer["children"] if c["name"] == "TYPE"]:
            keys = []
            for c in t["children"]:
                if c["name"] == "PROFILE":
                    for k, v in c["values"]:
                        x, y = v.split()
                        keys.append((float(x), float(y)))
            xs = [k[0] for k in keys]
            ys = [k[1] for k in keys]
            profile = list(np.interp(np.linspace(0.0, 1.0, 128), xs, ys))
            types.append(lay.slab_type(val(t, "name"), float(val(t, "baseShare")), float(val(t, "topShare")), profile, float(val(t, "erosion", 0.3))))
        kind = {"Weather": 0, "Cheap": 1, "Fog": 2, "Emissive": 3}[val(layer, "kind", "Weather")]
        out.append(dict(name=val(layer, "name"), group=val(layer, "group", ""), inner=float(val(layer, "innerAltitudeM")), outer=float(val(layer, "outerAltitudeM")),
                        sigma=float(val(layer, "sigmaPerM")), coverage=float(val(layer, "coverage")), softness=float(val(layer, "edgeSoftness")),
                        noise=float(val(layer, "noiseAmount")), scale=float(val(layer, "noiseScaleM")), kind=kind, types=types))
    return out


def job_layer(spec, cov_map, type_map):
    return lay.layer(spec["inner"], spec["outer"], spec["sigma"], spec["types"], coverage=spec["coverage"], softness=spec["softness"], kind=spec["kind"],
                     cov_map=cov_map, type_map=type_map, noise=spec["noise"], noise_scale=spec["scale"], name=spec["name"])


# --------------------------------------------------------------------------------------------------------------------------------
# Jobs
# --------------------------------------------------------------------------------------------------------------------------------
def const_map(value):
    """A constant R8 map file; returns its name."""
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    name = "k%03d.r8" % v
    os.makedirs(INPUT, exist_ok=True)
    np.full(MAP_W * MAP_H, v, dtype=np.uint8).tofile(os.path.join(INPUT, name))
    return name


def site_position(site, height=GROUND_H):
    lon = math.radians(site * SITE_STEP_DEG)
    return np.array([math.cos(lon), math.sin(lon), 0.0]) * (R + height)


def camera(site, size=SIZE, pitch=GROUND_PITCH, height=GROUND_H, yaw=0.0):
    return pr.Camera(site_position(site, height), yaw, pitch, size[0], size[1], FOV)


def tuning(pairs):
    return [dict(key=k, value=float(v)) for k, v in pairs]


# The six candidates of the plan (section 2, O4). 'keys' are the game's tuning keys (they go through CloudLayerDef.Effective*), 'type' the constant type value the
# module's FetchStep would give the cumulus layer at no precipitation (cloud.typeFair.Kerbin-cumulus) or None.
CUMULUS = "Kerbin-cumulus"
NOISE_KEYS = [("cloud.layerNoiseAmountScale." + CUMULUS, 0.55 / 0.35), ("cloud.layerNoiseSizeScale." + CUMULUS, 0.6)]
CANDIDATES = {
    "C0": dict(keys=[], type=None, note="current"),
    "C1": dict(keys=[], type=0.25, note="type: fair weather shows Cumulus humilis"),
    "C2": dict(keys=NOISE_KEYS, type=None, note="shaping: noise amount 0.35 -> 0.55, scale 5000 -> 3000 (the renormalisation of the plan is moot: the volume is ranked)"),
    "C3": dict(keys=[("layers.densityRemap", 0.6)], type=None, note="density: the erosion also thins the interior (densityRemap 0.6)"),
    "C4": dict(keys=[("cloud.layerCoverageShift." + CUMULUS, -0.10), ("cloud.layerSoftnessScale." + CUMULUS, 0.35 / 0.15), ("cloud.layerSigmaScale." + CUMULUS, 0.035 / 0.05)] + NOISE_KEYS, type=None,
               note="cfg: L1 coverage 0.25, softness 0.35, sigma 0.035, noise 0.55 / 3000 (the proposals of the plan, section 3)"),
    "C5": dict(keys=NOISE_KEYS + [("layers.densityRemap", 0.6)], type=0.25, note="combination: C1 + C2 + C3"),
}
MAPS = (0.50, 0.665, 0.80)
SHIFTS = (0.0, -0.08, -0.15)


def l1_jobs(cands, maps=MAPS, shifts=SHIFTS, tag="s1", extra_keys=None):
    jobs = []
    specs = {s["name"]: s for s in kerbin_layers()}
    for cid, c in cands.items():
        for m in maps:
            for sh in shifts:
                for site in range(SITES):
                    typ = c["type"] if c["type"] is not None else 0.0
                    keys = list(c["keys"]) + [("cloud.coverageShift", sh)] + list(extra_keys or [])
                    jid = "%s-%s-m%03d-s%03d-p%d" % (tag, cid, int(round(m * 1000)), int(round(-sh * 1000)), site)
                    j = lay.job(jid, "aux", camera(site), [job_layer(specs[CUMULUS], const_map(m), const_map(typ))], SIZE[0], SIZE[1], planet=R,
                                maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H, tuning=tuning(keys))
                    jobs.append(j)
    return jobs


def single_jobs(layer_name, m, typ, shifts, site_count, tag, keys=None):
    specs = {s["name"]: s for s in kerbin_layers()}
    out = []
    for sh in shifts:
        for site in range(site_count):
            jid = "%s-m%03d-t%03d-s%03d-p%d" % (tag, int(round(m * 1000)), int(round(typ * 1000)), int(round(-sh * 1000)), site)
            out.append(lay.job(jid, "aux", camera(site), [job_layer(specs[layer_name], const_map(m), const_map(typ))], SIZE[0], SIZE[1], planet=R,
                               maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H, tuning=tuning(list(keys or []) + [("cloud.coverageShift", sh)])))
    return out


# The maps of the two picks of K-T2d start 50 (CPU values at the site) and the field's cover at the site.
PICKS = {
    "low": dict(cover=0.473, maps=(0.4056, 0.6651, 0.1103, 0.5010), types=(0.0, 0.0, 0.0, 0.0)),
    "high": dict(cover=0.672, maps=(0.8595, 0.2058, 0.6609, 0.1774), types=(0.229, 0.229, 0.229, 0.229)),
}


def combine_scale(maps, cover):
    """The factor k with 1 - prod(1 - k m) = cover (1 where the union is already at or under it): CloudLookTuning.CombineFactor."""
    def union(k):
        p = 1.0
        for m in maps:
            p *= 1.0 - min(1.0, k * m)
        return 1.0 - p
    if union(1.0) <= cover:
        return 1.0
    lo, hi = 0.0, 1.0
    for _ in range(40):
        mid = 0.5 * (lo + hi)
        if union(mid) < cover:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


def stack_jobs(cands, tag="st"):
    specs = kerbin_layers()
    jobs = []
    for pick, p in PICKS.items():
        for cid, c in cands.items():
            for combine in (0, 1):
                maps = list(p["maps"])
                if combine:
                    k = combine_scale(maps, p["cover"])
                    maps = [m * k for m in maps]
                types = list(p["types"])
                if c["type"] is not None:
                    # cloud.typeFair.Kerbin-cumulus: the fair type at no precipitation, rising linearly to 1 with the precipitation level (CloudLookTuning.TypeValue).
                    types[1] = c["type"] + (1.0 - c["type"]) * p["types"][1]
                for site in range(SITES):
                    layers_job = [job_layer(specs[i], const_map(maps[i]), const_map(types[i])) for i in range(4)]
                    jid = "%s-%s-%s-c%d-p%d" % (tag, pick, cid, combine, site)
                    jobs.append(lay.job(jid, "aux", camera(site), layers_job, SIZE[0], SIZE[1], planet=R, maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H,
                                        tuning=tuning(list(c["keys"]) + [("cloud.coverageShift", 0.0)])))
    return jobs


def softness_jobs():
    """Sweep 2 of the shape group: three softness values on the best candidate (C4 numbers otherwise)."""
    jobs = []
    for soft in (0.25, 0.35, 0.45):
        c = dict(keys=[("cloud.layerCoverageShift." + CUMULUS, -0.10), ("cloud.layerSoftnessScale." + CUMULUS, soft / 0.15), ("cloud.layerSigmaScale." + CUMULUS, 0.035 / 0.05)] + NOISE_KEYS,
                 type=None)
        jobs += l1_jobs({"soft%03d" % int(round(soft * 100)): c}, tag="s2")
    return jobs


# Sweep 3 of the shape group (the last): the coverage of L1 at the softness sweep 2 favoured (0.25), shifts -0.10, -0.15, -0.20 (coverage 0.25, 0.20, 0.15). EXPECTED, written
# before the run (from sweep 1 and 2): a lower coverage lowers the closed share at every map; at 0.20 the row at map 0.80 enters the A1 band (<= 0.95) while the row at map 0.50 drops
# under 0.30 (the lower edge of its band): at most two of the three A1 rows hold at any coverage.
SWEEP3 = (-0.10, -0.15, -0.20)


def coverage_jobs():
    jobs = []
    for sh in SWEEP3:
        c = dict(keys=[("cloud.layerCoverageShift." + CUMULUS, sh), ("cloud.layerSoftnessScale." + CUMULUS, 0.25 / 0.15), ("cloud.layerSigmaScale." + CUMULUS, 0.035 / 0.05)] + NOISE_KEYS,
                 type=None)
        jobs += l1_jobs({"cov%03d" % int(round(-sh * 100)): c}, tag="s3")
    return jobs


# The grain sweep (P6): the same deck at resolution 1 / 0.5 / 0.25 of the march target (the half-size target of the plain pass is the product's level 1; the smaller ones stand for the
# block levels), jitter 0 / 0.5 / 1 (no, half, full step dither) and 1 or 4 accumulated frames (what the temporal reconstruction converges to), in the ground and the above view.
# EXPECTED, written before the run: hf (the mean absolute Laplacian of the luma over the sky or the deck, divided by the mean luma) falls with the accumulated frames (4 frames at
# least 1.5 times lower than 1 at jitter 1), is lower at jitter 0 than at jitter 1 for one frame (banding in place of grain: no free lunch), and is lower at the smaller march targets.
GRAIN_RES = (1.0, 0.5, 0.25)
GRAIN_JITTER = (0.0, 0.5, 1.0)
GRAIN_ACC = (1, 4)
GRAIN_SIZE = (854, 480)


def grain_jobs():
    specs = {s["name"]: s for s in kerbin_layers()}
    jobs = []
    for view, (height, pitch) in {"ground": (GROUND_H, GROUND_PITCH), "above": (8300.0, -25.0)}.items():
        for res in GRAIN_RES:
            for jit in GRAIN_JITTER:
                for acc in GRAIN_ACC:
                    cam = camera(0, GRAIN_SIZE, pitch=pitch, height=height)
                    sun = lay.sun_vector(cam.pos, 120.0, 40.0)
                    jid = "grain-%s-r%03d-j%03d-a%d" % (view, int(round(res * 100)), int(round(jit * 100)), acc)
                    # A closed sheet in both views (map 0.80, shift 0): the ground view then looks at the underside of a deck, the above view at its top.
                    jobs.append(lay.job(jid, "color", cam, [job_layer(specs[CUMULUS], const_map(0.8), const_map(0.0))], GRAIN_SIZE[0], GRAIN_SIZE[1], planet=R, sun=sun, maxSteps=48,
                                        minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H, jitter=jit, earlyOut=0.01, sunSteps=3, productCost=1, resolution=res, accumulate=acc))
    return jobs


# The horizon sweep (P7): the view 2 degrees up from 264 m at a sheet (map 0.8) and a distance fade of cloud.fadeFarM 0 / 20 / 40 / 80 km (the fade starts at half of it), with and
# without the aerial perspective. The number: the deck's mean alpha in the band 0 to 2 degrees above the horizon over its mean alpha 8 to 12 degrees above it (1: the sheet ends
# in a hard edge at the horizon, 0: it has faded into the sky). EXPECTED, written before the run: 1.0 without a fade (the sheet is closed to the horizon), under 0.5 at 80 km and the
# lowest at 20 km; the aerial perspective changes the colour, not the alpha.
HORIZON_FADES = (0.0, 20000.0, 40000.0, 80000.0)
HORIZON_PITCH = 2.0


def horizon_jobs():
    specs = {s["name"]: s for s in kerbin_layers()}
    jobs = []
    for fade in HORIZON_FADES:
        for site in range(SITES):
            cam = camera(site, SIZE, pitch=HORIZON_PITCH)
            jobs.append(lay.job("hz-aux-f%03d-p%d" % (int(round(fade / 1000)), site), "aux", cam, [job_layer(specs[CUMULUS], const_map(0.8), const_map(0.0))], SIZE[0], SIZE[1], planet=R,
                                maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H, tuning=tuning([("cloud.fadeFarM", fade)])))
        for haze in (0, 1):
            size = (854, 480)
            cam2 = camera(0, size, pitch=HORIZON_PITCH)
            sun = lay.sun_vector(cam2.pos, 120.0, 40.0)
            far = dict(haze=haze, shapeMean=0, policy=0, minSamples=0, hazeStrength=1.0, inscatter=[0.62, 0.68, 0.76])
            jobs.append(lay.job("hz-pic-f%03d-h%d" % (int(round(fade / 1000)), haze), "color", cam2, [job_layer(specs[CUMULUS], const_map(0.8), const_map(0.0))], size[0], size[1],
                                planet=R, sun=sun, maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1, far=far,
                                tuning=tuning([("cloud.fadeFarM", fade)])))
    return jobs


def build_jobs():
    jobs = []
    jobs += l1_jobs(CANDIDATES)                                   # sweep 1: the six sets, the 3 x 3 table, 8 sites each
    jobs += single_jobs("Kerbin-weather-rain", 0.859, 0.229, (0.0,), SITES, "L0high")        # P2
    jobs += single_jobs("Kerbin-weather-rain", 0.4056, 0.0, (0.0,), SITES, "L0low")
    for cid in ("C0", "C3", "C5"):                                # A2: the towers under the global key of the candidates (the remap acts on every layer)
        jobs += single_jobs("Kerbin-cumulonimbus", 0.6609, 0.229, (0.0, -0.15), SITES, "L2" + cid, CANDIDATES[cid]["keys"])
    jobs += stack_jobs({k: CANDIDATES[k] for k in ("C0", "C1", "C4", "C5")})                  # A3 with and without the combination
    jobs += softness_jobs()                                       # sweep 2
    jobs += coverage_jobs()                                       # sweep 3
    jobs += grain_jobs()
    jobs += horizon_jobs()
    return jobs


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = build_jobs() + picture_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def picture_jobs():
    """The pictures (natural and aid) of the candidates at the ground frame, map 0.665, shift 0 and -0.15, site 0: 1280 x 720."""
    specs = {s["name"]: s for s in kerbin_layers()}
    size = (1280, 720)
    jobs = []
    for cid, c in CANDIDATES.items():
        for sh in (0.0, -0.15):
            typ = c["type"] if c["type"] is not None else 0.0
            keys = list(c["keys"]) + [("cloud.coverageShift", sh)]
            cam = camera(0, size)
            sun = lay.sun_vector(cam.pos, 120.0, 40.0)
            jobs.append(lay.job("pic-%s-s%03d" % (cid, int(round(-sh * 1000))), "color", cam, [job_layer(specs[CUMULUS], const_map(0.665), const_map(typ))], size[0], size[1],
                                planet=R, sun=sun, maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1,
                                tuning=tuning(keys)))
    # The rain deck at the high pick (inside the Fog and Stratus blend) and the cumulonimbus towers.
    cam = camera(0, size)
    sun = lay.sun_vector(cam.pos, 120.0, 40.0)
    jobs.append(lay.job("pic-L0high", "color", cam, [job_layer(specs["Kerbin-weather-rain"], const_map(0.859), const_map(0.229))], size[0], size[1], planet=R, sun=sun,
                        maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1))
    jobs.append(lay.job("pic-L2high", "color", cam, [job_layer(specs["Kerbin-cumulonimbus"], const_map(0.6609), const_map(0.229))], size[0], size[1], planet=R, sun=sun,
                        maxSteps=48, minStep=25.0, mapWidth=MAP_W, mapHeight=MAP_H, jitter=1.0, earlyOut=0.01, sunSteps=3, productCost=1))
    return jobs


# --------------------------------------------------------------------------------------------------------------------------------
# Reading
# --------------------------------------------------------------------------------------------------------------------------------
_MASKS = {}


def sky_mask(site, pitch=GROUND_PITCH):
    """The pixels above the horizon (the ray's elevation above minus the dip of the horizon) of the camera at that pitch, and the elevation in degrees."""
    if (site, pitch) in _MASKS:
        return _MASKS[(site, pitch)]
    cam = camera(site, pitch=pitch)
    d = cam.ray_directions().reshape(SIZE[1], SIZE[0], 3)
    up = cam.pos / np.linalg.norm(cam.pos)
    dip = math.acos(R / (R + GROUND_H))
    elev = np.arcsin(np.clip(d @ up, -1.0, 1.0))
    _MASKS[(site, pitch)] = (elev > -dip, np.degrees(elev))
    return _MASKS[(site, pitch)]


def closed_share(folder, jid, site, threshold=CLOSED):
    tau = np.load(os.path.join(folder, "%s-tau.npy" % jid)).astype(np.float64)
    mask, _ = sky_mask(site)
    return float((tau[mask] >= threshold).mean())


def hf_energy(path, view):
    """The mean absolute 3 x 3 Laplacian of the luma of a picture divided by its mean luma, over the upper 55 percent of a ground picture (the sky and the deck) and the whole
    of an above picture."""
    from PIL import Image
    im = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255.0
    luma = 0.2126 * im[..., 0] + 0.7152 * im[..., 1] + 0.0722 * im[..., 2]
    if view == "ground":
        luma = luma[: int(luma.shape[0] * 0.55)]
    lap = np.abs(4.0 * luma[1:-1, 1:-1] - luma[:-2, 1:-1] - luma[2:, 1:-1] - luma[1:-1, :-2] - luma[1:-1, 2:])
    return float(lap.mean() / max(luma.mean(), 1e-6))


def horizon_ratio(folder, jid):
    """(alpha in the band 0 to 2 degrees above the horizon, alpha 8 to 12 degrees above it, their ratio) of an aux job at the first site."""
    near_all, far_all = [], []
    bands = {(2.0, 4.0): [], (4.0, 8.0): []}
    for site in range(SITES):
        tau = np.load(os.path.join(folder, "%s-p%d-tau.npy" % (jid, site))).astype(np.float64)
        mask, elev = sky_mask(site, HORIZON_PITCH)
        # The elevation here is above the camera's horizontal; the horizon is dip below it.
        dip = math.degrees(math.acos(R / (R + GROUND_H)))
        above = elev + dip
        alpha = 1.0 - np.exp(-tau)
        near_all.append(alpha[(above >= 0.0) & (above < 2.0)].mean())
        far_all.append(alpha[(above >= 8.0) & (above < 12.0)].mean())
        for (lo, hi), acc in bands.items():
            acc.append(alpha[(above >= lo) & (above < hi)].mean())
    n, f = float(np.mean(near_all)), float(np.mean(far_all))
    HORIZON_BANDS[jid] = {"2-4": float(np.mean(bands[(2.0, 4.0)])), "4-8": float(np.mean(bands[(4.0, 8.0)]))}
    return n, f, (n / f if f > 1e-9 else float("nan"))


HORIZON_BANDS = {}


def mean_sd(values):
    a = np.asarray(values, dtype=np.float64)
    return float(a.mean()), float(a.std())


def table_l1(folder, tag, cand):
    """{(map, shift): (mean, sd)} of the closed share over the sites."""
    out = {}
    for m in MAPS:
        for sh in SHIFTS:
            vals = [closed_share(folder, "%s-%s-m%03d-s%03d-p%d" % (tag, cand, int(round(m * 1000)), int(round(-sh * 1000)), s), s) for s in range(SITES)]
            out[(m, sh)] = mean_sd(vals)
    return out


def a1(table):
    """A1: at maps 0.50 / 0.665 / 0.80 and shift 0 the mean closed share lies between map - 0.20 and map + 0.15, and falls by at least 0.10 per -0.08 of shift (map 0.665)."""
    rows = {}
    for m in MAPS:
        v = table[(m, 0.0)][0]
        rows["within@%.3f" % m] = (m - 0.20) <= v <= (m + 0.15)
    f1 = table[(0.665, 0.0)][0] - table[(0.665, -0.08)][0]
    f2 = table[(0.665, -0.08)][0] - table[(0.665, -0.15)][0]
    rows["fall1>=0.10"] = f1 >= 0.10
    rows["fall2>=0.10"] = f2 >= 0.10
    return rows, f1, f2


def collect(ctx):
    folder = ctx.folder("layers")
    results = {"candidates": {}, "notes": {}}
    metrics = []
    lines = []
    for cid, c in CANDIDATES.items():
        t = table_l1(folder, "s1", cid)
        rows, f1, f2 = a1(t)
        results["candidates"][cid] = {"note": c["note"], "table": {"%.3f/%.2f" % k: v for k, v in t.items()}, "a1": {k: bool(v) for k, v in rows.items()}, "fall": [f1, f2]}
        lines.append("%s %s" % (cid, c["note"]))
        for sh in SHIFTS:
            lines.append("   shift %5.2f: " % sh + "  ".join("map %.3f %.3f+-%.3f" % (m, t[(m, sh)][0], t[(m, sh)][1]) for m in MAPS))
        lines.append("   A1 " + " ".join("%s=%s" % (k, "ok" if v else "FAIL") for k, v in rows.items()) + "  falls %.3f %.3f" % (f1, f2))
        for m in MAPS:
            for sh in SHIFTS:
                metrics.append(Metric.info("e40o.%s.m%03d.s%03d" % (cid, int(round(m * 1000)), int(round(-sh * 1000))), t[(m, sh)][0]))
    # P2, the rain deck
    for tag, label in (("L0high", "rain deck high pick (0.859, 0.229)"), ("L0low", "rain deck low pick (0.4056, 0)")):
        m, typ = (0.859, 0.229) if tag == "L0high" else (0.4056, 0.0)
        vals = [closed_share(folder, "%s-m%03d-t%03d-s000-p%d" % (tag, int(round(m * 1000)), int(round(typ * 1000)), s), s) for s in range(SITES)]
        results["notes"][tag] = mean_sd(vals)
        lines.append("%s: closed %.3f +- %.3f" % (label, *mean_sd(vals)))
        metrics.append(Metric.info("e40o.%s" % tag, mean_sd(vals)[0]))
    # A2, the towers
    for cid in ("C0", "C3", "C5"):
        row = []
        for sh in (0.0, -0.15):
            vals = [closed_share(folder, "L2%s-m661-t229-s%03d-p%d" % (cid, int(round(-sh * 1000)), s), s) for s in range(SITES)]
            row.append(mean_sd(vals)[0])
        results["notes"]["L2" + cid] = row
        lines.append("towers (L2 0.661, 0.229) %s: closed shift 0 %.3f, shift -0.15 %.3f" % (cid, row[0], row[1]))
        metrics.append(Metric.info("e40o.L2.%s.s000" % cid, row[0]))
        metrics.append(Metric.info("e40o.L2.%s.s150" % cid, row[1]))
    # A3, the column cover
    for pick, p in PICKS.items():
        for cid in ("C0", "C1", "C4", "C5"):
            row = []
            for combine in (0, 1):
                vals = [closed_share(folder, "st-%s-%s-c%d-p%d" % (pick, cid, combine, s), s) for s in range(SITES)]
                row.append(mean_sd(vals)[0])
            results["notes"]["stack-%s-%s" % (pick, cid)] = row
            lines.append("column cover %s pick (field %.3f) %s: layers as they are %.3f, combined %.3f" % (pick, p["cover"], cid, row[0], row[1]))
            metrics.append(Metric.info("e40o.cover.%s.%s.c0" % (pick, cid), row[0]))
            metrics.append(Metric.info("e40o.cover.%s.%s.c1" % (pick, cid), row[1]))
    # sweep 2
    for soft in (0.25, 0.35, 0.45):
        cid = "soft%03d" % int(round(soft * 100))
        t = table_l1(folder, "s2", cid)
        rows, f1, f2 = a1(t)
        results["notes"]["sweep2-" + cid] = {"table": {"%.3f/%.2f" % k: v for k, v in t.items()}, "a1": {k: bool(v) for k, v in rows.items()}, "fall": [f1, f2]}
        lines.append("sweep 2 softness %.2f: " % soft + "  ".join("m%.3f/s%.2f %.3f" % (m, sh, t[(m, sh)][0]) for m in MAPS for sh in (0.0, -0.15)) + "  A1 "
                     + " ".join("%s=%s" % (k, "ok" if v else "FAIL") for k, v in rows.items()))
    # sweep 3
    for sh in SWEEP3:
        cid = "cov%03d" % int(round(-sh * 100))
        t = table_l1(folder, "s3", cid)
        rows, f1, f2 = a1(t)
        results["notes"]["sweep3-" + cid] = {"table": {"%.3f/%.2f" % k: v for k, v in t.items()}, "a1": {k: bool(v) for k, v in rows.items()}, "fall": [f1, f2]}
        lines.append("sweep 3 L1 coverage %.2f: " % (0.35 + sh) + "  ".join("m%.3f/s%.2f %.3f" % (m, s2, t[(m, s2)][0]) for m in MAPS for s2 in (0.0, -0.15)) + "  A1 "
                     + " ".join("%s=%s" % (k, "ok" if v else "FAIL") for k, v in rows.items()))
    # the grain sweep
    grain = {}
    for view in ("ground", "above"):
        for res in GRAIN_RES:
            for jit in GRAIN_JITTER:
                for acc in GRAIN_ACC:
                    jid = "grain-%s-r%03d-j%03d-a%d" % (view, int(round(res * 100)), int(round(jit * 100)), acc)
                    grain[jid] = hf_energy(os.path.join(folder, jid + ".png"), view)
                    metrics.append(Metric.info("e40o.grain.%s" % jid, grain[jid]))
        base = grain["grain-%s-r100-j100-a1" % view]
        lines.append("grain %s (hf = mean |Laplacian| of luma / mean luma over the sky or deck region; resolution 1 jitter 1 one frame = %.4f):" % (view, base))
        for res in GRAIN_RES:
            lines.append("   resolution %.2f: " % res + "  ".join("j%.1f a%d %.4f" % (jit, acc, grain["grain-%s-r%03d-j%03d-a%d" % (view, int(round(res * 100)), int(round(jit * 100)), acc)])
                                                              for jit in GRAIN_JITTER for acc in GRAIN_ACC))
    results["grain"] = grain
    # the horizon sweep
    hz = {}
    for fade in HORIZON_FADES:
        hz[fade] = horizon_ratio(folder, "hz-aux-f%03d" % int(round(fade / 1000)))     # the mean over the sites
        metrics.append(Metric.info("e40o.horizon.f%03d" % int(round(fade / 1000)), hz[fade][2]))
        extra = HORIZON_BANDS["hz-aux-f%03d" % int(round(fade / 1000))]
        lines.append("horizon fade %5.0f m: alpha in 0-2 / 2-4 / 4-8 / 8-12 deg above the horizon %.3f / %.3f / %.3f / %.3f, ratio %.3f" % (
            fade, hz[fade][0], extra["2-4"], extra["4-8"], hz[fade][1], hz[fade][2]))
    results["horizon"] = {str(k): v for k, v in hz.items()}
    # pictures
    out_dir = os.path.join(main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    copied = []
    keep_grain = ("grain-ground-r100-j100-a1", "grain-ground-r100-j100-a4", "grain-ground-r025-j100-a1", "grain-above-r100-j100-a1", "grain-above-r100-j100-a4", "grain-above-r025-j100-a1",
                  "grain-above-r100-j000-a1")
    for name in sorted(os.listdir(folder)):
        if name.endswith(".png") and (name.startswith("pic-") or name.startswith("hz-pic-") or name[:-4].replace("-aid", "") in keep_grain):
            target = "e40-o-%s" % name
            shutil.copyfile(os.path.join(folder, name), os.path.join(out_dir, target))
            copied.append(os.path.join(out_dir, target))
    results["pictures"] = copied
    results["lines"] = lines
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    print("pictures: %d copied to %s" % (len(copied), out_dir))
    return metrics


def main_repo():
    env = os.environ.get("ATMO_MAINREPO")
    if env:
        return env
    return r"C:\Users\franz\IdeaProjects\ReDefinition"


def stages():
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")) or os.path.getmtime(os.path.join(INPUT, "jobs.json")) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("layers", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False, timeout=1800)]


register_series("e40-o", stages(), collect, exclusive=False, who="e40-phaseO")


def selftest():
    layers = kerbin_layers()
    assert [l["name"] for l in layers] == ["Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus"], [l["name"] for l in layers]
    cu = layers[1]
    assert abs(cu["sigma"] - 0.05) < 1e-9 and abs(cu["coverage"] - 0.35) < 1e-9 and abs(cu["softness"] - 0.15) < 1e-9 and len(cu["types"]) == 5
    assert abs(layers[0]["types"][1]["profile"][0] - 0.0) < 1e-9 and layers[0]["scale"] == 10000.0
    k = combine_scale([0.4056, 0.6651, 0.1103, 0.5010], 0.473)
    p = 1.0
    for m in (0.4056, 0.6651, 0.1103, 0.5010):
        p *= 1.0 - k * m
    assert abs((1.0 - p) - 0.473) < 1e-6, (k, p)
    assert combine_scale([0.1], 0.5) == 1.0
    mask, elev = sky_mask(0)
    assert mask.any() and not mask.all() and elev.max() > 40.0
    print("selftest passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--write", action="store_true", help="write the jobs and exit")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.write:
        print(len(write_inputs()), "jobs")
