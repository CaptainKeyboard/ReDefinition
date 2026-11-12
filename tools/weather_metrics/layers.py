"""The series of the layer core (package T01), run through tools/build_weather_player.py --series T01-layers:

  T01-layers   the scene 'layers' of the weather player draws the own cloud system's layer shell (the shader ReDefinition/AtmospheresLayers,
               the type table, the maps, the stack) on the synthetic scenes of the parity harness (tests/data/parity/checks.json, packages
               T01: R01 horizon, R03 type table, R04 coverage map, R05 stacking, R17 depth) and on the view table of the package. This
               module writes the jobs (the cameras come from the harness's own camera, so that both look along the same rays), reads the
               arrays the scene wrote, builds the capture of each check in the shape the harness reads and judges it with the harness's
               function and threshold (parity_checks.run_check; no threshold is set here). The captures are also written to
               build/weather-series/T01-ours/<check id>/ so that

                   python tools/weather_metrics/parity_run.py reference <folder>
                   python tools/weather_metrics/parity_run.py table --reference <folder> --ours build/weather-series/T01-ours

               prints the reference / TVC / ours table.

Two scenes differ from the harness's reference scenes and are measured against the harness's analytic expectation of their own geometry:
the flat planes of 'two-slab' are a sphere of the Kerbin radius (the curvature changes the alpha of the harness's two-slab view by at most
3e-5), and the mountain is a sphere placed on the curved ground 12 km from the camera, its top 1200 m above sea level (the harness's
flat mountain would stand 750 m below the curved ground at 30 km).

Extra rows of the package (not checks of the harness): the stacking of two thin layers (the harness's two slabs are opaque, alpha > 0.997),
the gas-giant pass through the hollow shell against the chord lengths, and the boundary crossing with the march's fixed sampling.
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
from weather_metrics import parity_checks as pc  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import parity_run as run  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "T01-in")
OURS = os.path.join(BUILD, "T01-ours")
R = pr.KERBIN_RADIUS

MAP_W, MAP_H = 1024, 512
RAMP_W, RAMP_H = 4096, 2048


# --------------------------------------------------------------------------------------------------------------------------------
# Jobs
# --------------------------------------------------------------------------------------------------------------------------------
def vec(v):
    return [float(x) for x in v]


def camera_fields(cam):
    return dict(position=vec(cam.pos), forward=vec(cam.forward), right=vec(cam.right), up=vec(cam.cam_up), fovV=float(cam.fov_v))


def slab_type(name="slab", base=0.0, top=1.0, profile=(1.0, 1.0), erosion=0.0):
    return dict(name=name, baseShare=float(base), topShare=float(top), erosion=float(erosion), profile=[float(p) for p in profile])


def layer(inner, outer, sigma, types=None, coverage=1.0, softness=0.15, kind=0, cov_map="", type_map="", noise=0.0, noise_scale=0.0,
          fade_near=0.0, fade_far=0.0, name=None):
    return dict(name=name or "layer", inner=float(inner), outer=float(outer), sigma=float(sigma), coverage=float(coverage),
                softness=float(softness), kind=kind, fadeNear=float(fade_near), fadeFar=float(fade_far), coverageMap=cov_map, typeMap=type_map,
                noiseAmount=float(noise), noiseScale=float(noise_scale), shift=0.0, emission=[0.0, 0.0, 0.0], types=types or [slab_type()])


def job(job_id, mode, cam, layers, width, height, planet=R, surface=1, sun=(0.0, 0.0, 1.0), **kw):
    j = dict(id=job_id, mode=mode, width=int(width), height=int(height), planetRadius=float(planet), hasSurface=int(surface), sun=vec(sun),
             rayLimit=0.0, maxSteps=256, sunSteps=3, earlyOut=0.0, minStep=1.0, stepGrowth=0.0, jitter=0.0, sunMarch=4000.0, resolution=1.0,
             layerMask=0, mapWidth=0, mapHeight=0, layers=layers, occluder=[0.0, 0.0, 0.0, 0.0])
    j.update(camera_fields(cam))
    j.update(kw)
    return j


def sun_vector(pos, azimuth, elevation):
    return pr.Sun.at(np.asarray(pos, dtype=np.float64), azimuth, elevation).direction


def vertical_camera(position, forward, east_hint, width, height, fov):
    """A camera looking along `forward` with `east_hint` as its right vector (for the profile probes)."""
    cam = pr.Camera(position, 0.0, 0.0, width, height, fov)
    cam.forward = np.asarray(forward, dtype=np.float64)
    cam.right = np.asarray(east_hint, dtype=np.float64)
    cam.cam_up = np.cross(cam.right, cam.forward)
    return cam


# --------------------------------------------------------------------------------------------------------------------------------
# Maps
# --------------------------------------------------------------------------------------------------------------------------------
def texel_unit_vectors(w, h):
    """Unit vectors at the texel centres of an equirectangular map (row 0 = north, column 0 = longitude -180), as (h, w, 3)."""
    lat = np.radians(90.0 - (np.arange(h) + 0.5) * 180.0 / h)[:, None]
    lon = np.radians(-180.0 + (np.arange(w) + 0.5) * 360.0 / w)[None, :]
    return np.stack([np.cos(lat) * np.cos(lon) + 0 * lon, np.cos(lat) * np.sin(lon) + 0 * lon, np.sin(lat) + 0 * lon], axis=-1)


def write_r8(name, values):
    data = np.clip(np.rint(np.asarray(values, dtype=np.float64) * 255.0), 0, 255).astype(np.uint8)
    os.makedirs(INPUT, exist_ok=True)
    data.tofile(os.path.join(INPUT, name))
    return name


def coverage_map_file():
    """The harness's ranked test map (seed 7) as a map where cloud is the HIGH value: 1 - the harness's lookup, at the texel centres."""
    grid = pr.value_noise_map(7)
    u = texel_unit_vectors(MAP_W, MAP_H).reshape(-1, 3)
    return write_r8("coverage-seed7.r8", 1.0 - pr.sphere_map_lookup(grid, u).reshape(MAP_H, MAP_W))


def blob_map_file(lat0=20.0, lon0=30.0, radius_deg=3.0):
    c = np.array([math.cos(math.radians(lat0)) * math.cos(math.radians(lon0)), math.cos(math.radians(lat0)) * math.sin(math.radians(lon0)), math.sin(math.radians(lat0))])
    u = texel_unit_vectors(MAP_W, MAP_H)
    ang = np.degrees(np.arccos(np.clip(u @ c, -1.0, 1.0)))
    return write_r8("blob-20n30e.r8", (ang < radius_deg).astype(np.float64))


def ramp_map_file():
    """The type map of the ramp test: the type value grows with the east distance x = R lon from 0 to 1 over 10 km, along the equator."""
    lon = np.radians(-180.0 + (np.arange(RAMP_W) + 0.5) * 360.0 / RAMP_W)
    ramp = np.clip(R * lon / 10000.0, 0.0, 1.0)
    return write_r8("ramp-type.r8", np.tile(ramp[None, :], (RAMP_H, 1)))


def smooth_map_file(name, seed, lo=0.0, hi=1.0):
    grid = pr.value_noise_map(seed, rank_uniform=True)
    u = texel_unit_vectors(MAP_W, MAP_H).reshape(-1, 3)
    return write_r8(name, lo + (hi - lo) * pr.sphere_map_lookup(grid, u).reshape(MAP_H, MAP_W))


# --------------------------------------------------------------------------------------------------------------------------------
# The jobs of the checks
# --------------------------------------------------------------------------------------------------------------------------------
CURVE = run.CURVE
RAMP_TYPES = (1500.0, 3000.0, 2500.0, 5000.0)
RAMP_LAYER = (1400.0, 5100.0, 0.01)
PROFILE = (2000.0, 3000.0, 0.02)
STACK = ((1500.0, 1900.0, 2.0), (3000.0, 3400.0, 3.0))
THIN_STACK = ((1500.0, 1900.0, 0.3), (3000.0, 3400.0, 0.5))
BOUNDARY_FRAMES = 31
MOUNTAIN = dict(base=1500.0, top=2100.0, sigma=5.0 / 600.0, top_altitude=1200.0, distance=12000.0, radius=30000.0)


def horizon_cases():
    caps = {}
    for case in ("below", "inside", "above"):
        cam = run.cap_horizon(case)["camera"]
        caps[case] = cam
    return caps


def ramp_samples():
    """The probe places of the ramp test: texel centres of the ramp map on its linear part (m = 0, 2, ..., 10 texels east of lon 0)."""
    texel_deg = 360.0 / RAMP_W
    xs = [(m + 0.5) * math.radians(texel_deg) * R for m in (0, 2, 4, 6, 8, 10)]
    return xs


WINDOW_BACK, WINDOW_LENGTH = 150.0, 600.0


def ramp_value(x):
    """The type value the 8-bit ramp map holds at east distance x: the ramp rounded to a byte."""
    return float(np.rint(np.clip(x / 10000.0, 0.0, 1.0) * 255.0) / 255.0)


def build_jobs():
    jobs = []
    # R01: the horizon, three cameras
    for case, cam in horizon_cases().items():
        c = pr.Camera([0.0, 0.0, R + cam["h_cam"]], 0.0, cam["pitch"], 3, 480, cam["fov_v"])
        b, t, s = cam["layers"][0]
        jobs.append(job("R01-%s" % case, "aux", c, [layer(b, t, s)], 3, 480))
    # R03: the vertical profile: 20 columns, each a vertical ray up from the layer's base whose length grows with the column
    base, top, sigma = PROFILE
    c = vertical_camera([0.0, 0.0, R + base], [0.0, 0.0, 1.0], [1.0, 0.0, 0.0], 20, 1, 0.01)
    jobs.append(job("R03-profile", "aux", c, [layer(base, top, sigma, [slab_type("curve", 0.0, 1.0, CURVE)])], 20, 1, rayLimit=-(top - base)))
    # R03: the type ramp: six vertical probes of 741 columns each
    lo, hi, sig = RAMP_LAYER
    thick = hi - lo
    types = [slab_type("A", (RAMP_TYPES[0] - lo) / thick, (RAMP_TYPES[2] - lo) / thick), slab_type("B", (RAMP_TYPES[1] - lo) / thick, (RAMP_TYPES[3] - lo) / thick)]
    for k, x in enumerate(ramp_samples()):
        lon = x / R
        up = np.array([math.cos(lon), math.sin(lon), 0.0])
        east = np.array([-math.sin(lon), math.cos(lon), 0.0])
        ramp_layers = [layer(lo, hi, sig, types, cov_map="", type_map="ramp-type.r8")]
        # The whole column (coarse, for the total), then a narrow window up from below the expected base and one down from above the
        # expected top (about 2.3 m a step). The windows are placed from the lerp and only help to resolve; the crossing is measured.
        # The field of view is tiny so that the 741 columns stay on one vertical line.
        t = ramp_value(x)
        base_an = RAMP_TYPES[0] + (RAMP_TYPES[1] - RAMP_TYPES[0]) * t
        top_an = RAMP_TYPES[2] + (RAMP_TYPES[3] - RAMP_TYPES[2]) * t
        c = vertical_camera(up * (R + lo), up, east, 741, 1, 1e-4)
        jobs.append(job("R03-ramp-%d" % k, "aux", c, ramp_layers, 741, 1, rayLimit=-thick, mapWidth=RAMP_W, mapHeight=RAMP_H))
        c = vertical_camera(up * (R + base_an - WINDOW_BACK), up, east, 741, 1, 1e-4)
        jobs.append(job("R03-rampb-%d" % k, "aux", c, ramp_layers, 741, 1, rayLimit=-WINDOW_LENGTH, mapWidth=RAMP_W, mapHeight=RAMP_H))
        c = vertical_camera(up * (R + top_an + WINDOW_BACK), -up, east, 741, 1, 1e-4)
        jobs.append(job("R03-rampt-%d" % k, "aux", c, ramp_layers, 741, 1, rayLimit=-WINDOW_LENGTH, mapWidth=RAMP_W, mapHeight=RAMP_H))
    # R04: the coverage fraction (nadir, 1500 km above) and the marker blob
    cam = pr.Camera([0.0, 0.0, R + 1500000.0], 0.0, -90.0, 96, 96, 30.0)
    jobs.append(job("R04-coverage", "aux", cam, [layer(2200.0, 2500.0, 12.0 / 300.0, coverage=0.4, softness=0.01, cov_map="coverage-seed7.r8", type_map="")],
                    96, 96, maxSteps=64, mapWidth=MAP_W, mapHeight=MAP_H))
    lat0, lon0 = 20.0, 30.0
    cvec = np.array([math.cos(math.radians(lat0)) * math.cos(math.radians(lon0)), math.cos(math.radians(lat0)) * math.sin(math.radians(lon0)), math.sin(math.radians(lat0))])
    cam = pr.Camera(cvec * (R + 1500000.0), 0.0, -90.0, 96, 96, 30.0)
    jobs.append(job("R04-blob", "aux", cam, [layer(2200.0, 2500.0, 0.04, coverage=0.5, softness=0.1, cov_map="blob-20n30e.r8")], 96, 96, maxSteps=64,
                    mapWidth=MAP_W, mapHeight=MAP_H))
    # R05: the stack, the layers alone and together (bit mask), and the same with thin layers
    cam = pr.Camera([0.0, 0.0, R + 2.0], 0.0, 40.0, 16, 16, 30.0)
    for tag, spec in (("", STACK), ("thin-", THIN_STACK)):
        layers = [layer(b, t, tau / (t - b)) for (b, t, tau) in spec]
        for suffix, mask in (("1", 1), ("2", 2), ("12", 0)):
            jobs.append(job("R05-%s%s" % (tag, suffix), "aux", cam, layers, 16, 16, layerMask=mask))
    # R05: the boundary crossing: a vertical path through the lower boundary of a slab, 20 m/s at 60 frames per second
    slab_base, thickness, tau = 2000.0, 300.0, 6.0
    sun = sun_vector([0, 0, R], 200.0, 38.0)
    for i in range(BOUNDARY_FRAMES):
        h = 1995.0 + i * (20.0 / 60.0)
        cam = pr.Camera([0.0, 0.0, R + h], 0.0, 45.0, 1, 6, 50.0)
        # The production sampling: 48 steps, 3 sun steps, the interleaved gradient noise with the frame number fixed.
        jobs.append(job("R05-boundary-%02d" % i, "lum", cam, [layer(slab_base, slab_base + thickness, tau / thickness, kind=0)], 1, 6, sun=sun,
                        maxSteps=48, minStep=25.0, stepGrowth=0.0002, jitter=1.0, earlyOut=0.01))
        jobs.append(job("R05-boundaryfix-%02d" % i, "lum", cam, [layer(slab_base, slab_base + thickness, tau / thickness, kind=0)], 1, 6, sun=sun,
                        maxSteps=256, minStep=1.0, jitter=0.0))
    # R17: the mountain
    m = MOUNTAIN
    cam = pr.Camera([0.0, 0.0, R + 100.0], 0.0, 4.0, 320, 240, 50.0)
    phi = m["distance"] / R
    centre = (R + m["top_altitude"] - m["radius"]) * np.array([0.0, math.sin(phi), math.cos(phi)])
    jobs.append(job("R17-mountain", "aux", cam, [layer(m["base"], m["top"], m["sigma"])], 320, 240, occluder=[float(centre[0]), float(centre[1]), float(centre[2]), m["radius"]]))
    # Gas giant: rays through the hollow shell, impact parameters across the inner sphere and the shell
    jobs.append(gas_job(3000000.0))
    return jobs


def gas_job(far):
    """A row of 64 rays from 3000 km away across the planet's limb: the impact parameters b run from R - 3000 to R + 3500 m. The body has
    no surface, so every ray goes through the hollow shell. The width is 64 and the height 1 (aspect 64): the horizontal field spans
    (B1 - B0) at the distance far."""
    cam = vertical_camera([(GAS_B0 + GAS_B1) / 2.0, -far, 0.0], [0.0, 1.0, 0.0], [1.0, 0.0, 0.0], 64, 1, 0.01)
    cam.cam_up = np.array([0.0, 0.0, 1.0])
    tan_half_v = (GAS_B1 - GAS_B0) / (2.0 * 64.0 * far)
    j = job("GAS", "aux", cam, [layer(1500.0, 2500.0, 0.02)], 64, 1, surface=0, maxSteps=256)
    j["fovV"] = 2.0 * math.degrees(math.atan(tan_half_v))
    return j


GAS_B0, GAS_B1 = R - 3000.0, R + 3500.0


def write_inputs():
    coverage_map_file()
    blob_map_file()
    ramp_map_file()
    smooth_map_file("view-coverage.r8", 21)
    smooth_map_file("view-type.r8", 33)
    jobs = build_jobs() + picture_jobs()
    os.makedirs(INPUT, exist_ok=True)
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


# --------------------------------------------------------------------------------------------------------------------------------
# The view table (pictures)
# --------------------------------------------------------------------------------------------------------------------------------
CUMULUS = [0.0, 0.55, 0.95, 1.0, 0.8, 0.4, 0.0]
STRATUS = [0.35, 0.8, 1.0, 0.8, 0.45, 0.1, 0.0]
ANVIL = [0.1, 0.6, 1.0, 1.0, 0.95, 0.9, 0.7, 0.4, 0.0]


def weather_types():
    return [slab_type("stratus", 0.0, 0.3, STRATUS, 0.2), slab_type("cumulus", 0.0, 0.65, CUMULUS, 0.5), slab_type("cumulonimbus", 0.0, 1.0, ANVIL, 0.6)]


def deck(inner=1000.0, outer=3000.0, sigma=0.012, coverage=0.62, name="deck"):
    return layer(inner, outer, sigma, weather_types(), coverage=coverage, softness=0.18, cov_map="view-coverage.r8", type_map="view-type.r8", noise=0.45,
                 noise_scale=700.0, name=name)


def cirrus():
    return layer(8000.0, 8400.0, 0.0025, [slab_type("cirrus", 0.0, 1.0, [0.2, 0.8, 1.0, 0.7, 0.2, 0.0])], coverage=0.7, softness=0.25, kind=1,
                 cov_map="view-coverage.r8", type_map="", noise=0.6, noise_scale=1500.0, name="cirrus")


def view_site():
    """A place (lat, lon in degrees) where the view table's coverage map clouds about 60 % of the sky within 3.5 degrees (a broken field,
    not a hole and not a solid deck): the first of the candidates on a 5 degree grid whose covered share is nearest to 0.6 at coverage 0.62."""
    grid = pr.value_noise_map(21)
    best, best_err = (0.0, 0.0), 9.0
    for lat in range(-30, 31, 5):
        for lon in range(-175, 180, 5):
            offs = np.linspace(-3.5, 3.5, 9)
            la, lo = np.meshgrid(lat + offs, lon + offs)
            u = np.stack([np.cos(np.radians(la)) * np.cos(np.radians(lo)), np.cos(np.radians(la)) * np.sin(np.radians(lo)), np.sin(np.radians(la))], axis=-1).reshape(-1, 3)
            share = float((pr.sphere_map_lookup(grid, u) > 0.38).mean())
            if abs(share - 0.6) < best_err:
                best, best_err = (float(lat), float(lon)), abs(share - 0.6)
    return best


def picture_jobs():
    views = []

    def add(view_id, pos_lat_lon, h, pitch, yaw, layers, fov=60.0, sun=(120.0, 28.0), size=(640, 360), **kw):
        lat, lon = pos_lat_lon
        up = np.array([math.cos(math.radians(lat)) * math.cos(math.radians(lon)), math.cos(math.radians(lat)) * math.sin(math.radians(lon)), math.sin(math.radians(lat))])
        cam = pr.Camera(up * (R + h), yaw, pitch, size[0], size[1], fov)
        views.append(job("V-%s" % view_id, "color", cam, layers, size[0], size[1], sun=sun_vector(cam.pos, sun[0], sun[1]), maxSteps=64, minStep=20.0,
                         stepGrowth=0.0002, jitter=0.0, earlyOut=0.01, sunSteps=4, mapWidth=MAP_W, mapHeight=MAP_H, **kw))

    site = view_site()
    add("ground", site, 2.0, 14.0, 0.0, [deck()])
    add("inside", site, 2000.0, 8.0, 0.0, [deck()])
    add("above", site, 8000.0, -14.0, 0.0, [deck()])
    add("orbit-edge", site, 350000.0, -48.0, 0.0, [deck(), cirrus()], fov=70.0)
    add("stack-below", site, 2.0, 30.0, 0.0, [deck(1000.0, 2400.0, 0.014, 0.45), cirrus()])
    add("stack-above", site, 12000.0, -20.0, 0.0, [deck(1000.0, 2400.0, 0.014, 0.45), cirrus()])
    return views


# --------------------------------------------------------------------------------------------------------------------------------
# Reading the arrays, the captures
# --------------------------------------------------------------------------------------------------------------------------------
def plane(folder, job_id, name):
    return np.load(os.path.join(folder, "%s-%s.npy" % (job_id, name))).astype(np.float64)


def capture_horizon(folder, case, cam):
    return {"alpha": plane(folder, "R01-%s" % case, "alpha"), "camera": cam}


def capture_profile(folder):
    base, top, sigma = PROFILE
    tau = plane(folder, "R03-profile", "tau")[0]
    n = len(tau)
    return {"heights": (np.arange(n) + 0.5) / n, "tau_cum": tau, "curve": CURVE, "sigma_thickness": sigma * (top - base)}


def capture_ramp(folder):
    lo, hi, _ = RAMP_LAYER
    n = 741
    heights = lo + (hi - lo) * (np.arange(n) + 0.5) / n
    base, top, ramp = [], [], []
    lengths = WINDOW_LENGTH * (np.arange(n) + 0.5) / n
    for k, x in enumerate(ramp_samples()):
        t = ramp_value(x)
        base_an = RAMP_TYPES[0] + (RAMP_TYPES[1] - RAMP_TYPES[0]) * t
        top_an = RAMP_TYPES[2] + (RAMP_TYPES[3] - RAMP_TYPES[2]) * t
        tot = float(np.max(plane(folder, "R03-ramp-%d" % k, "tau")[0]))
        up = np.maximum.accumulate(plane(folder, "R03-rampb-%d" % k, "tau")[0])
        down = np.maximum.accumulate(plane(folder, "R03-rampt-%d" % k, "tau")[0])
        base.append(float(base_an - WINDOW_BACK + np.interp(0.001 * tot, up, lengths)))
        top.append(float(top_an + WINDOW_BACK - np.interp(0.001 * tot, down, lengths)))
        ramp.append(t)
    return {"ramp": np.array(ramp), "base": np.array(base), "top": np.array(top), "types": list(RAMP_TYPES)}


def capture_coverage(folder):
    alpha_img = plane(folder, "R04-coverage", "alpha")
    cam = pr.Camera([0, 0, R + 1500000.0], 0, -90, 96, 96, 30)
    d = cam.ray_directions()
    o = np.broadcast_to(cam.pos, d.shape)
    t0, _ = pr.DensityField.hit_sphere(o, d, np.zeros(3), R + 2500.0)
    hit = ~np.isnan(t0)
    grid = pr.value_noise_map(7)
    pts = o[hit] + d[hit] * t0[hit][:, None]
    u = pts / np.linalg.norm(pts, axis=1, keepdims=True)
    expected = float((pr.sphere_map_lookup(grid, u) < 0.4).mean())
    return {"alpha": alpha_img.ravel()[hit].reshape(1, -1), "map_mean": expected}


def capture_orientation(folder):
    alpha = plane(folder, "R04-blob", "alpha")
    lat0, lon0 = 20.0, 30.0
    cvec = np.array([math.cos(math.radians(lat0)) * math.cos(math.radians(lon0)), math.cos(math.radians(lat0)) * math.sin(math.radians(lon0)), math.sin(math.radians(lat0))])
    cam = pr.Camera(cvec * (R + 1500000.0), 0, -90, 96, 96, 30)
    ys, xs = np.nonzero(alpha > 0.5)
    w = alpha[ys, xs]
    px, py = float((xs * w).sum() / w.sum()), float((ys * w).sum() / w.sum())
    lat, lon = run.sphere_pixel_to_latlon(cam, 2500.0, px, py)
    return {"measured_latlon": [lat, lon], "true_latlon": [lat0, lon0]}


def capture_stack(folder, tag=""):
    return {"alpha1": plane(folder, "R05-%s1" % tag, "alpha"), "alpha2": plane(folder, "R05-%s2" % tag, "alpha"), "alpha12": plane(folder, "R05-%s12" % tag, "alpha")}


def boundary_luminance(folder, prefix):
    """Total radiance of the six directions, averaged: the march's radiance plus what lies behind it (the harness's environment)."""
    out = []
    env = pr.Environment(0.30, 0.55, 0.12)
    for i in range(BOUNDARY_FRAMES):
        jid = "%s-%02d" % (prefix, i)
        rgb = plane(folder, jid, "g")[:, 0]
        a = plane(folder, jid, "a")[:, 0]
        cam = pr.Camera([0.0, 0.0, R + 1995.0], 0.0, 45.0, 1, 6, 50.0)
        d = cam.ray_directions()
        up = cam.pos / np.linalg.norm(cam.pos)
        behind = env.radiance(d, up[None, :])
        out.append(float(np.mean(rgb + (1.0 - a) * behind)))
    return {"luminance": np.array(out)}


def step_edges(a):
    """Per row of an image: the column (pixel centres at i + 0.5) of the largest step between neighbours, and the size of the step."""
    d = np.diff(a, axis=1)
    idx = np.argmax(np.abs(d), axis=1)
    size = d[np.arange(len(d)), idx]
    return idx + 1.0, size


def capture_depth(folder):
    alpha = plane(folder, "R17-mountain", "alpha")
    depth = plane(folder, "R17-mountain", "depth")
    mask = (depth > 0).astype(np.float64)
    ex_d, size_d = step_edges(mask)
    ex_a, size_a = step_edges(alpha)
    rows = (np.abs(size_d) > 0.5) & (np.abs(size_a) > 0.3)
    return {"cloud_edge_x": ex_a[rows], "depth_edge_x": ex_d[rows], "rows": int(rows.sum()), "depth_rows": int((np.abs(size_d) > 0.5).sum())}


def gas_expected():
    """The optical depth of the gas giant's rays: sigma times the chord lengths inside the shell, from the impact parameter."""
    n = 64
    b = GAS_B0 + (GAS_B1 - GAS_B0) * (np.arange(n) + 0.5) / n
    ro, ri = R + 2500.0, R + 1500.0
    chord = lambda r: np.where(b < r, 2.0 * np.sqrt(np.maximum(r * r - b * b, 0.0)), 0.0)
    return b, 0.02 * (chord(ro) - chord(ri))


# --------------------------------------------------------------------------------------------------------------------------------
# The series
# --------------------------------------------------------------------------------------------------------------------------------
def threshold_of(spec):
    t = spec.get("threshold", {})
    return t.get("max")


def judged(spec, cid, data, out, name=None):
    """Runs the harness's function on a capture and appends one metric with the harness's threshold."""
    s = spec[cid]
    m = pc.run_check(s["function"], data, s)
    limit = threshold_of(s)
    run.write_capture(os.path.join(OURS, cid), data)
    key = name or cid
    out.append(Metric.at_most(key, m.value if m.value is not None else float("nan"), limit).based("gate"))
    return m


def collect(ctx):
    spec = pc.load_spec()
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    if not os.path.isfile(os.path.join(folder, "R01-below-alpha.npy")):
        out.append(Metric.flag("written", False))
        return out
    os.makedirs(OURS, exist_ok=True)
    cams = horizon_cases()
    for case, cam in cams.items():
        m = judged(spec, "R01-horizon-" + case, capture_horizon(folder, case, cam), out)
        out.append(Metric.info("R01-horizon-%s.row" % case, m.detail.get("row", float("nan"))))
        out.append(Metric.info("R01-horizon-%s.analytic_row" % case, m.detail.get("analytic_row", float("nan"))))
    judged(spec, "R03-vertical-profile", capture_profile(folder), out)
    judged(spec, "R03-type-ramp", capture_ramp(folder), out)
    m = judged(spec, "R04-coverage-fraction", capture_coverage(folder), out)
    out.append(Metric.info("R04-coverage-fraction.fraction", m.detail.get("fraction", float("nan"))))
    judged(spec, "R04-map-orientation", capture_orientation(folder), out)
    judged(spec, "R05-stack-alpha", capture_stack(folder), out)
    thin = capture_stack(folder, "thin-")
    m = pc.run_check("stack_alpha", thin, spec["R05-stack-alpha"])
    out.append(Metric.at_most("R05-stack-alpha.thin", m.value, threshold_of(spec["R05-stack-alpha"])).based("gate"))
    out.append(Metric.info("R05-stack-alpha.thin_alpha12_mean", float(np.mean(thin["alpha12"]))))
    judged(spec, "R05-boundary-crossing", boundary_luminance(folder, "R05-boundary"), out)
    m = pc.run_check("boundary_crossing", boundary_luminance(folder, "R05-boundaryfix"), spec["R05-boundary-crossing"])
    out.append(Metric.at_most("R05-boundary-crossing.fixed_sampling", m.value, threshold_of(spec["R05-boundary-crossing"])).based("gate"))
    depth = capture_depth(folder)
    m = judged(spec, "R17-depth-edge", depth, out)
    out.append(Metric.info("R17-depth-edge.rows", float(depth["rows"])))
    out.append(Metric.info("R17-depth-edge.depth_rows", float(depth["depth_rows"])))
    err = np.abs(np.asarray(depth["cloud_edge_x"]) - np.asarray(depth["depth_edge_x"]))
    out.append(Metric.info("R17-depth-edge.max_px", float(err.max()) if len(err) else float("nan")))
    b, expected = gas_expected()
    tau = plane(folder, "GAS", "tau")[0]
    scale = max(float(expected.max()), 1e-9)
    out.append(Metric.at_most("gas_giant.tau_error", float(np.max(np.abs(tau - expected)) / scale), 0.03).based("new"))
    out.append(Metric.info("gas_giant.tau_max", float(expected.max())))
    for view_id in ("ground", "inside", "above", "orbit-edge", "stack-below", "stack-above"):
        ok = os.path.isfile(os.path.join(folder, "V-%s.png" % view_id)) and os.path.isfile(os.path.join(folder, "V-%s-aid.png" % view_id))
        out.append(Metric.flag("view.%s" % view_id, ok))
    return out


def stages():
    jobs = os.path.join(INPUT, "jobs.json")
    # The inputs are written when missing or older than this file (an import for another series does not rebuild them).
    if not os.path.isfile(jobs) or os.path.getmtime(jobs) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False)]


register_series("T01-layers", stages(), collect, exclusive=False, who="T01")

# The cost of the pass at the view table's jobs, half and full size, 32 to 64 steps (a timing run: exclusive, with the proxy's GPU timestamps).
register_series("T01-cost", [Stage("scene", ["-mode", "scene", "-scene", "layers-cost", "-in", INPUT], size=(1707, 960), proxy=True, timeout=1200)],
                core.player_judged("scene"), exclusive=True, who="T01")
