"""The series of the height variation of the cloud layers (package T13), run through tools/build_weather_player.py --series T13-height.

The layer scene of the weather player (tests/WeatherPlayer LayersScene, the mod's own LayerRenderer and shader) draws the same views twice
or more: BEFORE (no height maps: the type table's base and top, one level) and AFTER (the height maps of the mod's HeightMapBuilder from
synthetic humidity and updraft maps), and the A/B variants of the four switches. tests/WeatherPlayer/Assets/Runner/HeightHook.cs reads
<input>/heights.json (one entry per job id) and gives the stack its maps; a job without an entry is the BEFORE picture.

REFERENCE EXPECTATION (written before the build; sources in brackets):
  * Cumulus bases lie at one level locally but not ruler-straight: the standard deviation of the base altitude within a field is 50 to 300 m and
    above 20 m [proposed band; Lawrence 2005 / the condensation level: bases follow temperature and humidity at 125 m per K of dew-point spread].
  * Larger cloud fields have a lower base by the configured trend (L0 test HeightMapTests; the player shows it in the aid picture).
  * Tops differ with the updraft of the cell: the rendered top altitude correlates with the updraft at the cell cores >= 0.7 [proposed].
  * The underside is rough: the high-pass energy of the lower boundary of the cloud mass is >= 2x that of the smooth-base render [proposed].
  * Three layers (low cumulus, mid altocumulus, high cirrus) are visible together from below and from above, each inside its band.
  * Crossing a layer at 20 m/s changes the picture's luminance by <= 3 % per frame [brief].
  * All four switches off is the BEFORE picture (identity).

AID VIEW: the altitude of the first cloud hit of every pixel in false colour (aux mode of the layer scene: the distance of the first dense
sample): the base from below and from the side, the top from above. Pictures are named t13-<view>-before.png, -after.png, -aid-base.png.
"""
import json
import math
import os
import shutil
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "T13-in")
PICTURES = os.path.join(BUILD, "T13-pictures")
R = pr.KERBIN_RADIUS
MAP_W, MAP_H = 4096, 2048
TEXEL_KM = 2.0 * math.pi * R / 1000.0 / MAP_W
SIZE = (800, 450)
POP_SIZE = (96, 54)
POP_STEP_M = 20.0 / 60.0
POP_FRAMES = 30
POP_WINDOWS = (1000.0, 1250.0, 1500.0, 1750.0, 2000.0)
# The coarse sweep over the whole band the base and top can lie in (the fine windows may miss the crossing): 4 m a frame is 12x the spec's step,
# so its limit is 12x the spec's 3 %.
POP_COARSE_START, POP_COARSE_END, POP_COARSE_STEP = 1000.0, 3600.0, 4.0
POP_COARSE_FACTOR = POP_COARSE_STEP / POP_STEP_M
SKY_LUMA = 0.35
SUN = [3.0, 3.0, 3.0]
DAY = dict(sunColour=SUN, ambientTop=[0.35, 0.42, 0.55], ambientBottom=[0.14, 0.15, 0.16], skyHorizon=[0.34, 0.42, 0.55],
           skyZenith=[0.14, 0.26, 0.55], exposure=2.0, discGain=60.0, aureole=6.0)

CUMULUS = L.CUMULUS
CIRRUS = [0.2, 0.8, 1.0, 0.7, 0.2, 0.0]
ALTO = [0.0, 0.7, 1.0, 0.9, 0.5, 0.0]

SWITCHES_ON = dict(variation=1, physics=1, topUpdraft=1, ragged=1)
VARIANTS = {
    "after": dict(SWITCHES_ON),
    "novar": dict(SWITCHES_ON, variation=0),
    "nophys": dict(SWITCHES_ON, physics=0),
    "norag": dict(SWITCHES_ON, ragged=0),
    "toponly": dict(variation=0, physics=0, topUpdraft=1, ragged=0),
    "notop": dict(SWITCHES_ON, topUpdraft=0),
    "alloff": dict(variation=0, physics=0, topUpdraft=0, ragged=0),
}


# --------------------------------------------------------------------------------------------------------------------------------
# Maps and sites
# --------------------------------------------------------------------------------------------------------------------------------
def fbm(seed, first=2, last=7, persistence=0.6):
    """Layered noise on the map grid (octave k has 8 * 2^k rows), ranked to a uniform distribution on [0, 1]."""
    from PIL import Image
    rng = np.random.default_rng(seed)
    total = np.zeros((MAP_H, MAP_W), dtype=np.float32)
    amp = 1.0
    for k in range(first, last + 1):
        grid = rng.random((8 * 2 ** k, 16 * 2 ** k)).astype(np.float32)
        total += amp * np.asarray(Image.fromarray(grid).resize((MAP_W, MAP_H), Image.Resampling.BICUBIC), dtype=np.float32)
        amp *= persistence
    order = np.argsort(total.ravel(), kind="stable")
    ranks = np.empty(total.size, dtype=np.float32)
    ranks[order] = np.arange(total.size, dtype=np.float32) / (total.size - 1)
    return ranks.reshape(MAP_H, MAP_W)


def texel(lat, lon):
    return (90.0 - lat) / 180.0 * MAP_H, (lon + 180.0) / 360.0 * MAP_W


def write_map(name, values):
    data = np.clip(np.rint(np.asarray(values, dtype=np.float64) * 255.0), 0, 255).astype(np.uint8)
    os.makedirs(INPUT, exist_ok=True)
    data.tofile(os.path.join(INPUT, name))
    return name


def box_share(integral, row, col, half_rows, half_cols):
    r0 = np.clip(np.rint(row - half_rows).astype(int), 0, MAP_H - 1)
    r1 = np.clip(np.rint(row + half_rows).astype(int), 1, MAP_H)
    c0 = np.clip(np.rint(col - half_cols).astype(int), 0, MAP_W - 1)
    c1 = np.clip(np.rint(col + half_cols).astype(int), 1, MAP_W)
    area = np.maximum((r1 - r0) * (c1 - c0), 1)
    return (integral[r1, c1] - integral[r0, c1] - integral[r1, c0] + integral[r0, c0]) / area


def find_site(values, coverage, ahead_km, half_km, want_ahead, near_km=None, want_near=None, yaw=90.0):
    """The place where the share of cloud in a box ahead_km ahead (half-width half_km) is nearest to want_ahead (and the share within near_km
    of the camera nearest to want_near)."""
    mask = (values > 1.0 - coverage).astype(np.float64)
    integral = np.zeros((MAP_H + 1, MAP_W + 1))
    integral[1:, 1:] = mask.cumsum(axis=0).cumsum(axis=1)
    lats = np.arange(-6.0, 6.01, 0.25)
    lons = np.arange(-150.0, 150.01, 0.25)
    la, lo = np.meshgrid(lats, lons, indexing="ij")
    row, col = texel(la, lo)
    half = half_km / TEXEL_KM
    yr = math.radians(yaw)
    ahead = box_share(integral, row - math.cos(yr) * ahead_km / TEXEL_KM, col + math.sin(yr) * ahead_km / TEXEL_KM, half, half)
    err = np.abs(ahead - want_ahead)
    if near_km is not None:
        nh = near_km / TEXEL_KM
        err = err + np.abs(box_share(integral, row, col, nh, nh) - want_near)
    i = np.unravel_index(int(np.argmin(err)), err.shape)
    return float(la[i]), float(lo[i])


def cu_layer(name="cumulus", cov_map="cu.r8", coverage=0.3, inner=1400.0, outer=3200.0):
    return L.layer(inner, outer, 0.03, [L.slab_type("cumulus", 0.0, 0.8, CUMULUS, 0.5)], coverage=coverage, softness=0.3, cov_map=cov_map,
                   noise=0.35, noise_scale=700.0, name=name)


def alto_layer(cov_map="alto.r8"):
    return L.layer(4500.0, 5600.0, 0.012, [L.slab_type("altocumulus", 0.0, 1.0, ALTO, 0.4)], coverage=0.35, softness=0.3, cov_map=cov_map,
                   noise=0.4, noise_scale=900.0, name="alto")


def cirrus_layer(cov_map="cirrus.r8"):
    return L.layer(10000.0, 10600.0, 0.0025, [L.slab_type("cirrus", 0.0, 1.0, CIRRUS, 0.2)], coverage=0.5, softness=0.25, kind=1, cov_map=cov_map,
                   noise=0.6, noise_scale=1500.0, name="cirrus")


def views():
    """The views: id -> dict(site, alt, pitch, yaw, fov, sun, layers, aux (the aid pictures), variants (the A/B variants drawn))."""
    return {
        "below": dict(site="below", alt=2.0, pitch=20.0, yaw=90.0, fov=70.0, sun=(120.0, 40.0), layers=[cu_layer()], aux=True, ab=True),
        "side": dict(site="side", alt=900.0, pitch=4.0, yaw=90.0, fov=60.0, sun=(180.0, 35.0), layers=[cu_layer()], aux=True, ab=True),
        "above": dict(site="above", alt=6000.0, pitch=-30.0, yaw=90.0, fov=60.0, sun=(120.0, 45.0), layers=[cu_layer()], aux=True, ab=False),
        "down": dict(site="down", alt=30000.0, pitch=-90.0, yaw=90.0, fov=50.0, sun=(120.0, 45.0), layers=[cu_layer()], aux=True, ab=False),
        "sky3": dict(site="sky3", alt=8000.0, pitch=2.0, yaw=90.0, fov=75.0, sun=(150.0, 40.0), layers=[cu_layer(), alto_layer(), cirrus_layer()],
                     aux=True, ab=False),
        # Review T12/T13 C4: the field views. Under a broken cumulus field (clouds overhead, low sun so that the bases are lit from the side) and
        # above the field looking down at an angle; natural pictures plus the aid pictures.
        "fieldbelow": dict(site="fieldbelow", alt=2.0, pitch=25.0, yaw=90.0, fov=80.0, sun=(200.0, 22.0), layers=[cu_layer()], aux=True, ab=True),
        "fieldabove": dict(site="fieldabove", alt=4200.0, pitch=-25.0, yaw=90.0, fov=70.0, sun=(120.0, 30.0), layers=[cu_layer()], aux=True, ab=False),
    }


def write_maps():
    cu = fbm(11, 2, 7, 0.6)
    write_map("cu.r8", cu)
    write_map("alto.r8", fbm(31, 2, 6, 0.55))
    write_map("cirrus.r8", fbm(32, 2, 6, 0.55))
    # The synthetic inputs of the height maps: relative humidity 0.55 to 0.9 on a large scale (moist and dry regions), updraft 0.3 to 6.3 m/s
    # with few strong cells (the rank squared). Both are scene inputs, not the generator's field.
    write_map("humidity.r8", 0.55 + 0.35 * fbm(22, 1, 3, 0.5))
    write_map("updraft.r8", (0.3 + 6.0 * fbm(21, 3, 6, 0.6) ** 2) / 12.0)
    sites = {}
    sites["below"] = find_site(cu, 0.3, 8.0, 6.0, 0.35)
    sites["side"] = find_site(cu, 0.3, 6.0, 3.0, 0.6, 2.0, 0.05)
    sites["above"] = find_site(cu, 0.3, 8.0, 6.0, 0.4)
    sites["down"] = find_site(cu, 0.3, 0.0, 12.0, 0.45)
    sites["sky3"] = find_site(cu, 0.3, 8.0, 6.0, 0.35)
    # A broken field: about 25 % cloud within 6 km around the camera (below), about 30 % in a box 4 km ahead (above). A first sweep with 45 %
    # gave a solid deck below and a featureless white picture above.
    sites["fieldbelow"] = find_site(cu, 0.3, 0.0, 6.0, 0.25)
    sites["fieldabove"] = find_site(cu, 0.3, 4.0, 8.0, 0.3)
    return sites, cu


# --------------------------------------------------------------------------------------------------------------------------------
# Jobs
# --------------------------------------------------------------------------------------------------------------------------------
def camera(site, alt, pitch, yaw, w, h, fov):
    lat, lon = site
    up = np.array([math.cos(math.radians(lat)) * math.cos(math.radians(lon)), math.cos(math.radians(lat)) * math.sin(math.radians(lon)),
                   math.sin(math.radians(lat))])
    return pr.Camera(up * (R + alt), yaw, pitch, w, h, fov), up


def spec(job_id, switches):
    s = dict(id=job_id, humidityMap="humidity.r8", updraftMap="updraft.r8", width=1024, height=512, seed=7)
    s.update(switches)
    return s


def jobs_and_specs():
    sites, _ = write_maps()
    jobs, specs, meta = [], [], {}
    for vid, v in views().items():
        cam, _ = camera(sites[v["site"]], v["alt"], v["pitch"], v["yaw"], SIZE[0], SIZE[1], v["fov"])
        sun = L.sun_vector(cam.pos, v["sun"][0], v["sun"][1])
        meta[vid] = dict(site=sites[v["site"]], alt=v["alt"], pitch=v["pitch"], yaw=v["yaw"], fov=v["fov"])
        names = ["before", "after"] + (["novar", "nophys", "norag", "notop"] if v["ab"] else []) + (["alloff", "before2"] if vid == "below" else [])
        for name in names:
            jid = "t13-%s-%s" % (vid, name)
            j = L.job(jid, "color", cam, v["layers"], SIZE[0], SIZE[1], sun=sun, maxSteps=96, minStep=25.0, stepGrowth=0.0002, jitter=1.0,
                      earlyOut=0.01, sunSteps=3, mapWidth=MAP_W, mapHeight=MAP_H)
            j.update(DAY)
            j.update(sunMarch=9000.0)
            jobs.append(j)
            if name not in ("before", "before2"):
                specs.append(spec(jid, VARIANTS[name]))
        if vid == "sky3":
            for bit, lname in ((1, "low"), (2, "mid"), (4, "high")):
                jid = "t13-sky3-aux-%s" % lname
                j = L.job(jid, "aux", cam, v["layers"], SIZE[0], SIZE[1], sun=sun, maxSteps=256, minStep=8.0, stepGrowth=0.0, jitter=0.0,
                          earlyOut=0.0, mapWidth=MAP_W, mapHeight=MAP_H, layerMask=bit)
                jobs.append(j)
                specs.append(spec(jid, VARIANTS["after"]))
        if v["aux"]:
            auxnames = ["before", "after"] + (["norag"] if vid in ("side", "below") else []) + (["toponly"] if vid == "down" else [])
            for name in auxnames:
                jid = "t13-%s-aux-%s" % (vid, name)
                j = L.job(jid, "aux", cam, v["layers"], SIZE[0], SIZE[1], sun=sun, maxSteps=256, minStep=2.0, stepGrowth=0.0, jitter=0.0,
                          earlyOut=0.0, mapWidth=MAP_W, mapHeight=MAP_H)
                jobs.append(j)
                if name != "before":
                    specs.append(spec(jid, VARIANTS[name]))
    # The pop test: a camera looking up at the cumulus field, climbing at 20 m/s in 60 fps steps through the layer's band, in windows.
    site = sites["below"]
    for wi, start in enumerate(POP_WINDOWS):
        for k in range(POP_FRAMES):
            cam, _ = camera(site, start + k * POP_STEP_M, 80.0, 90.0, POP_SIZE[0], POP_SIZE[1], 50.0)
            jid = "t13-pop-%d-%02d" % (wi, k)
            j = L.job(jid, "lum", cam, [cu_layer()], POP_SIZE[0], POP_SIZE[1], sun=L.sun_vector(cam.pos, 120.0, 40.0), maxSteps=96, minStep=25.0,
                      stepGrowth=0.0002, jitter=1.0, earlyOut=0.01, sunSteps=3, mapWidth=MAP_W, mapHeight=MAP_H, sunMarch=9000.0)
            j.update(DAY)
            jobs.append(j)
            specs.append(spec(jid, VARIANTS["after"]))
    n = int((POP_COARSE_END - POP_COARSE_START) / POP_COARSE_STEP)
    for k in range(n):
        cam, _ = camera(site, POP_COARSE_START + k * POP_COARSE_STEP, 80.0, 90.0, POP_SIZE[0], POP_SIZE[1], 50.0)
        jid = "t13-popc-%03d" % k
        j = L.job(jid, "lum", cam, [cu_layer()], POP_SIZE[0], POP_SIZE[1], sun=L.sun_vector(cam.pos, 120.0, 40.0), maxSteps=96, minStep=25.0,
                  stepGrowth=0.0002, jitter=1.0, earlyOut=0.01, sunSteps=3, mapWidth=MAP_W, mapHeight=MAP_H, sunMarch=9000.0)
        j.update(DAY)
        jobs.append(j)
        specs.append(spec(jid, VARIANTS["after"]))
    return jobs, specs, meta


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs, specs, meta = jobs_and_specs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)
    with open(os.path.join(INPUT, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f)


# --------------------------------------------------------------------------------------------------------------------------------
# Measurement
# --------------------------------------------------------------------------------------------------------------------------------
def load_aux(folder, jid):
    alpha = np.load(os.path.join(folder, jid + "-alpha.npy"))
    first = np.load(os.path.join(folder, jid + "-first.npy"))
    return alpha, first


def first_hit_points(view_id, meta, first):
    """The planet-frame points of the first hit of every pixel (NaN where there is none), and the pixel's elevation angle above the local horizon."""
    m = meta[view_id]
    cam, up = camera(m["site"], m["alt"], m["pitch"], m["yaw"], SIZE[0], SIZE[1], m["fov"])
    d = cam.ray_directions().reshape(SIZE[1], SIZE[0], 3)
    t = np.where(first >= 0.0, first, np.nan)
    pts = cam.pos[None, None, :] + d * t[..., None]
    elev = np.degrees(np.arcsin(np.clip(d @ up, -1.0, 1.0)))
    return pts, elev


def hit_altitude(pts):
    return np.linalg.norm(pts, axis=-1) - R


def sample_map(name, pts):
    """The synthetic map (0..1 as bytes) at the latitude and longitude of the points."""
    data = np.fromfile(os.path.join(INPUT, name), dtype=np.uint8).reshape(MAP_H, MAP_W) / 255.0
    n = pts / np.linalg.norm(pts, axis=-1, keepdims=True)
    lat = np.degrees(np.arcsin(np.clip(n[..., 2], -1, 1)))
    lon = np.degrees(np.arctan2(n[..., 1], n[..., 0]))
    r = np.clip(((90.0 - lat) / 180.0 * MAP_H).astype(int), 0, MAP_H - 1)
    c = np.clip(((lon + 180.0) / 360.0 * MAP_W).astype(int), 0, MAP_W - 1)
    return data[r, c]


def false_colour(values, lo, hi):
    """A rainbow (blue low, red high) for values between lo and hi; NaN is mid-grey."""
    x = np.clip((values - lo) / max(hi - lo, 1e-6), 0.0, 1.0)
    r = np.clip(1.5 - np.abs(4.0 * x - 3.0), 0.0, 1.0)
    g = np.clip(1.5 - np.abs(4.0 * x - 2.0), 0.0, 1.0)
    b = np.clip(1.5 - np.abs(4.0 * x - 1.0), 0.0, 1.0)
    img = np.stack([r, g, b], axis=-1)
    img[np.isnan(values)] = 0.35
    return (img * 255.0).astype(np.uint8)


def write_aid(path, altitude, lo, hi, label):
    from PIL import Image, ImageDraw
    img = Image.fromarray(false_colour(altitude, lo, hi))
    draw = ImageDraw.Draw(img)
    draw.rectangle([0, 0, 330, 16], fill=(0, 0, 0))
    draw.text((4, 3), "%s  %d m (blue) to %d m (red)" % (label, lo, hi), fill=(255, 255, 255))
    img.save(path)


def bottom_profile(alpha):
    """The lowest row of the cloud mass in every column (rows from the top) for the columns that have cloud above the lowest row's neighbourhood."""
    mask = alpha > 0.5
    rows = np.full(mask.shape[1], np.nan)
    for c in range(mask.shape[1]):
        idx = np.nonzero(mask[:, c])[0]
        if idx.size:
            rows[c] = idx.max()
    return rows


def highpass_energy(profile, window=15):
    """The rms of the profile minus its moving average over the columns that have a value and whose whole window has one."""
    ok = ~np.isnan(profile)
    kernel = np.ones(window) / window
    filled = np.where(ok, profile, 0.0)
    mean = np.convolve(filled, kernel, mode="same")
    full = np.convolve(ok.astype(float), kernel, mode="same") > 0.999
    use = ok & full
    if use.sum() < 20:
        return float("nan")
    return float(np.sqrt(np.mean((profile[use] - mean[use]) ** 2)))


def highpass_rms(img, window=25):
    """The rms of an image minus its box mean over window x window pixels, over the pixels that have a value and whose window is full of them."""
    ok = ~np.isnan(img)
    filled = np.where(ok, img, 0.0)

    def box(a):
        c = np.cumsum(np.cumsum(np.pad(a, ((1, 0), (1, 0))), axis=0), axis=1)
        h = window // 2
        r1 = np.clip(np.arange(a.shape[0]) + h + 1, 0, a.shape[0])
        r0 = np.clip(np.arange(a.shape[0]) - h, 0, a.shape[0])
        c1 = np.clip(np.arange(a.shape[1]) + h + 1, 0, a.shape[1])
        c0 = np.clip(np.arange(a.shape[1]) - h, 0, a.shape[1])
        return c[np.ix_(r1, c1)] - c[np.ix_(r0, c1)] - c[np.ix_(r1, c0)] + c[np.ix_(r0, c0)]

    n = box(ok.astype(float))
    mean = box(filled) / np.maximum(n, 1.0)
    use = ok & (n >= 0.999 * window * window)
    if use.sum() < 200:
        return float("nan")
    return float(np.sqrt(np.mean((img[use] - mean[use]) ** 2)))


BASE_INNER, BASE_BAND = 1400.0, 1000.0


def read_png(path):
    from PIL import Image
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255.0


def luminance(lum_r, lum_g, lum_b, lum_a):
    return float(np.mean(0.2126 * lum_r + 0.7152 * lum_g + 0.0722 * lum_b + (1.0 - lum_a) * SKY_LUMA))


def collect(ctx):
    folder = ctx.folder("scene")
    with open(os.path.join(INPUT, "meta.json"), encoding="utf-8") as f:
        meta = json.load(f)
    out = []
    os.makedirs(PICTURES, exist_ok=True)
    # pictures: the natural before/after and the aid pictures under their unique names
    for vid, v in views().items():
        for name in ["before", "after"] + (["novar", "nophys", "norag", "notop"] if v["ab"] else []) + (["alloff"] if vid == "below" else []):
            src = os.path.join(folder, "t13-%s-%s.png" % (vid, name))
            ok = os.path.isfile(src)
            out.append(Metric.flag("t13.%s.%s.picture" % (vid, name), ok))
            if ok:
                shutil.copy(src, os.path.join(PICTURES, "t13-%s-%s.png" % (vid, name)))

    stats = {}
    for vid in ("below", "side", "above", "down", "sky3", "fieldbelow", "fieldabove"):
        for name in ("before", "after", "norag", "toponly"):
            jid = "t13-%s-aux-%s" % (vid, name)
            if not os.path.isfile(os.path.join(folder, jid + "-alpha.npy")):
                continue
            alpha, first = load_aux(folder, jid)
            pts, elev = first_hit_points(vid, meta, first)
            alt = hit_altitude(pts)
            hit = (alpha > 0.5) & (first >= 0.0)
            alt_hit = np.where(hit, alt, np.nan)
            stats[(vid, name)] = dict(alpha=alpha, alt=alt_hit, pts=pts, elev=elev, hit=hit)
            lo, hi = {"below": (900.0, 1700.0), "side": (900.0, 1700.0), "above": (2000.0, 3600.0), "down": (2000.0, 3600.0),
                                 "fieldbelow": (900.0, 1700.0), "fieldabove": (2000.0, 3600.0)}.get(vid, (1000.0, 11000.0))
            write_aid(os.path.join(PICTURES, "t13-%s-aid-base%s.png" % (vid, "" if name == "after" else "-" + name)), alt_hit, lo, hi,
                      "first hit altitude, %s" % name)

    # Base spread (from below and from the side): the standard deviation of the first-hit altitude of the pixels that see the cloud base (a ray
    # pointing up by at least 15 degrees from below; the side view's base band is the lowest hits in each column).
    # Only underside hits count: the ray enters the cloud from below (the altitude is under inner + the physics and variation reach), not through
    # a vertical coverage wall further up.
    # The pixels of the flat base: where the BEFORE render's first hit lies on the type table's base (inner altitude + 40 m), not on a coverage wall
    # further up. The same pixels are read in the AFTER render, so the spread is the base offset itself (hits that moved by more than BASE_BAND
    # entered the cloud somewhere else and are dropped).
    def flat_base(b):
        # Rays 8 degrees or more above the horizon reach the base within 10 km of the camera: the footprint spans several wavelengths of the variation.
        return b["hit"] & (b["elev"] >= 8.0) & (b["alt"] < BASE_INNER + 40.0)

    a, b = stats.get(("below", "after")), stats.get(("below", "before"))
    if a is not None and b is not None:
        both = flat_base(b) & a["hit"]
        delta = (a["alt"] - b["alt"])[both]
        delta = delta[np.abs(delta) < BASE_BAND]
        floor = float(np.std(b["alt"][flat_base(b)]))
        std = float(np.std(delta)) if delta.size > 100 else float("nan")
        out.append(Metric.info("base.below.n_px", float(delta.size)))
        out.append(Metric.info("base.below.mean_offset_m", float(np.mean(delta)) if delta.size else float("nan")))
        out.append(Metric.info("base.below.before.std_m", floor))
        out.append(Metric.at_least("base.below.after.std_m", std, 50.0).based("gate"))
        out.append(Metric("base.below.after.std_m_max", std, 300.0, "max").based("gate"))
        out.append(Metric.at_least("base.below.std_ratio_after_over_before", std / max(floor, 1.0), 3.0).based("new"))

    # Tops (from the satellite view straight down): the first-hit altitude of cloudy pixels against the updraft map at the hit, at the cell cores
    # (the updraft above its 80th percentile among the cloudy pixels). Measured with only the top switch on, so that the variation and the
    # physics (which move the whole cloud) do not hide it; the full render's correlation is reported as info.
    # The top of a pixel is read as the change of the first hit against the BEFORE render at the same pixel (the shape noise's erosion moves the
    # first hit by some 200 m by itself and is the same in both).
    s, s0 = stats.get(("down", "toponly")), stats.get(("down", "before"))
    if s is not None and s0 is not None:
        sel = s["hit"] & s0["hit"]
        if sel.sum() > 500:
            top = (s["alt"] - s0["alt"])[sel]
            up = sample_map("updraft.r8", s0["pts"][sel]) * 12.0
            # A cell is a block of 50 x 50 pixels (1.5 km at the satellite view's 30 m pixel) with at least 60 % cloudy pixels: the block means of
            # the top change and of the updraft are the cell's values (the single pixel's first hit carries the erosion noise of the cloud's edge).
            full = np.where(s["hit"] & s0["hit"], s["alt"] - s0["alt"], np.nan)
            upfull = np.full(full.shape, np.nan)
            upfull[sel.reshape(full.shape)] = up
            bt, bu = [], []
            for y0 in range(0, full.shape[0] - 49, 50):
                for x0 in range(0, full.shape[1] - 49, 50):
                    blk = full[y0:y0 + 50, x0:x0 + 50]
                    if np.mean(~np.isnan(blk)) >= 0.6:
                        bt.append(np.nanmean(blk))
                        bu.append(np.nanmean(upfull[y0:y0 + 50, x0:x0 + 50]))
            out.append(Metric.info("top.down.n_cells", float(len(bt))))
            # Two gates, both at the brief's 0.7. HISTORY: the first definition (the cores below, per pixel) gave 0.09; the block means (written after
            # that first result) gave 0.73 and later 0.79-0.81. The brief's own definition ("at cell centres") stays as the second gate and is
            # reported as it is (0.12 to 0.22 in the earlier runs).
            out.append(Metric.at_least("top.down.corr_blocks_1500m", float(np.corrcoef(bt, bu)[0, 1]) if len(bt) > 10 else float("nan"), 0.7).based("gate"))
            core = up >= np.percentile(up, 80.0)
            out.append(Metric.at_least("top.down.corr_cores_brief", float(np.corrcoef(top[core], up[core])[0, 1]), 0.7).based("gate"))
            out.append(Metric.info("top.down.corr_all_toponly", float(np.corrcoef(top, up)[0, 1])))
            out.append(Metric.info("top.down.delta_std_m", float(np.std(top))))
    s = stats.get(("down", "after"))
    if s is not None:
        sel = s["hit"]
        if sel.sum() > 500:
            top = s["alt"][sel]
            up = sample_map("updraft.r8", s["pts"][sel]) * 12.0
            core = up >= np.percentile(up, 80.0)
            out.append(Metric.info("top.down.n_px", float(sel.sum())))
            out.append(Metric.info("top.down.corr_all", float(np.corrcoef(top, up)[0, 1])))
            out.append(Metric.info("top.down.corr_cores_full", float(np.corrcoef(top[core], up[core])[0, 1])))
            out.append(Metric.info("top.down.std_m", float(np.std(top))))
    sb_ = stats.get(("down", "before"))
    if sb_ is not None and sb_["hit"].sum() > 500:
        out.append(Metric.info("top.down.before.std_m", float(np.std(sb_["alt"][sb_["hit"]]))))

    # Ragged underside: the high-pass energy (rms of the altitude minus its 25-pixel moving mean, along rows and columns) of the first-hit
    # altitude of the underside from below, ragged over smooth base.
    energies = {}
    if b is not None:
        mask = flat_base(b)
        for name in ("before", "after", "norag"):
            s = stats.get(("below", name))
            if s is None:
                continue
            alt = np.where(mask & s["hit"], s["alt"], np.nan)
            energies[name] = highpass_rms(alt)
            out.append(Metric.info("rough.below.%s.rms_m" % name, energies[name]))
    # The gate is a real ratio: the ragged render over the render with the same variation and physics but a smooth base (the "norag" variant). Source:
    # the T13 brief, acceptance "Underside roughness ... above the flat-base render by >= 2x [proposed]". The ratio against the BEFORE render
    # (energy 0.03 m, a flat plane) is reported as info only and is not clamped.
    if "after" in energies and "norag" in energies and energies["norag"] > 0:
        out.append(Metric.at_least("rough.below.ragged_over_smooth", energies["after"] / energies["norag"], 2.0).based("gate"))
    if "after" in energies and "before" in energies and energies["before"] > 0:
        out.append(Metric.info("rough.below.ragged_over_before", energies["after"] / energies["before"]))

    # A/B: each switch changes the picture (it is not a no-op), and the identity of all-off against before.
    def diff(a_name, b_name):
        pa = os.path.join(folder, a_name + ".png")
        pb = os.path.join(folder, b_name + ".png")
        if not (os.path.isfile(pa) and os.path.isfile(pb)):
            return float("nan")
        return float(np.mean(np.abs(read_png(pa) - read_png(pb))))

    # The renderer's own repeat difference (the same job twice) is the floor; all switches off may not exceed it.
    floor = diff("t13-below-before2", "t13-below-before")
    out.append(Metric.info("ab.repeat_floor.mean_abs", floor))
    alloff = diff("t13-below-alloff", "t13-below-before")
    out.append(Metric.at_most("ab.alloff_equals_before.mean_abs", alloff, 1.5 * floor + 1e-6).based("new"))
    # The row-file form of the same test: the excess over the limit 1.5 x floor + 1e-6 (the floor is measured per run, a row cannot carry it), must be 0.
    out.append(Metric.at_most("ab.alloff_excess_over_floor_limit", max(0.0, alloff - (1.5 * floor + 1e-6)) if alloff == alloff and floor == floor else float("nan"),
                              0.0).based("new"))
    out.append(Metric.info("ab.fieldbelow.after_vs_before.mean_abs", diff("t13-fieldbelow-after", "t13-fieldbelow-before")))
    for vid in ("below", "side"):
        out.append(Metric.at_least("ab.%s.after_differs_from_before" % vid, diff("t13-%s-after" % vid, "t13-%s-before" % vid), 0.002).based("new"))
        for name in ("novar", "nophys", "norag", "notop"):
            out.append(Metric.info("ab.%s.%s_vs_after.mean_abs" % (vid, name), diff("t13-%s-%s" % (vid, name), "t13-%s-after" % vid)))

    # Three layers: each layer's first hits lie in its band (from below: the lowest hit of the sky3 view per layer is checked by the layer mask
    # of the aux job at the camera; here the natural picture exists and the aux altitude of the three-layer view is spread over the bands).
    # One aux job per layer (the layer mask draws one layer), all with the height variation on: the layer's hits cover at least 2 % of the picture and
    # lie inside the layer's band widened by the height reach (the base can sink below the inner altitude by up to the reach, the top rise above).
    for k, (lname, lo, hi) in enumerate((("low", 1400.0, 3200.0), ("mid", 4500.0, 5600.0), ("high", 10000.0, 10600.0))):
        jid = "t13-sky3-aux-%s" % lname
        if not os.path.isfile(os.path.join(folder, jid + "-alpha.npy")):
            continue
        alpha, first = load_aux(folder, jid)
        pts, _ = first_hit_points("sky3", meta, first)
        alts = hit_altitude(pts)[(alpha > 0.5) & (first >= 0.0)]
        out.append(Metric.at_least("layers.sky3.%s.share_px" % lname, float(alts.size) / alpha.size, 0.02).based("new"))
        # The reach of HeightTuning: down = root range + variation range + ragged, up = root range + variation range + top range (the thin high
        # layer has twice the variation and no physics: 110 + 880 + 110 and 110 + 880 + 1200).
        reach = 1210.0 if lname == "high" else 1270.0
        top_reach = 2530.0 if lname == "high" else 2590.0
        out.append(Metric.at_least("layers.sky3.%s.share_in_band" % lname,
                                   float(np.mean((alts >= lo - reach) & (alts <= hi + top_reach))) if alts.size else float("nan"), 0.98).based("new"))
        out.append(Metric.info("layers.sky3.%s.altitude_range_m" % lname, float(np.ptp(alts)) if alts.size else float("nan")))

    # No pop: the largest relative luminance change between frames 1/60 s apart at 20 m/s, over every window.
    worst, frames = 0.0, 0
    worst_c, frames_c = 0.0, 0
    nc = int((POP_COARSE_END - POP_COARSE_START) / POP_COARSE_STEP)
    groups = [("fine", [("t13-pop-%d-%02d" % (wi, k)) for k in range(POP_FRAMES)]) for wi in range(len(POP_WINDOWS))]
    groups.append(("coarse", ["t13-popc-%03d" % k for k in range(nc)]))
    for kind, ids in groups:
        series = []
        for jid in ids:
            try:
                r = np.load(os.path.join(folder, jid + "-r.npy"))
                g = np.load(os.path.join(folder, jid + "-g.npy"))
                bb = np.load(os.path.join(folder, jid + "-b.npy"))
                aa = np.load(os.path.join(folder, jid + "-a.npy"))
            except OSError:
                series = []
                break
            series.append(luminance(r, g, bb, aa))
        for i in range(1, len(series)):
            change = abs(series[i] - series[i - 1]) / max(series[i - 1], 1e-6)
            if kind == "fine":
                worst = max(worst, change)
                frames += 1
            else:
                worst_c = max(worst_c, change)
                frames_c += 1
    out.append(Metric.at_least("pop.frames_compared", float(frames), 100.0).based("new"))
    out.append(Metric.at_most("pop.max_luma_change_per_frame", worst if frames else float("nan"), 0.03).based("gate"))
    out.append(Metric.at_least("pop.coarse_frames_compared", float(frames_c), 100.0).based("new"))
    out.append(Metric.at_most("pop.coarse_max_luma_change_per_4m_step", worst_c if frames_c else float("nan"), 0.03 * POP_COARSE_FACTOR).based("gate"))
    return out


def stages():
    jobs_file = os.path.join(INPUT, "jobs.json")
    if not os.path.isfile(jobs_file) or os.path.getmtime(jobs_file) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False)]


register_series("T13-height", stages(), collect, exclusive=False, who="T13")
