"""The series of the noise and shape model (package T02), run through tools/build_weather_player.py --series T02-noise:

  T02-noise   the scene 'layers' of the weather player draws the own cloud system's layer shell with the real shape model (the shader
              ReDefinition/AtmospheresLayers and its include ReDefinitionAtmospheresNoise.cginc, the generated volumes) on the deck of the
              parity checks R02 (tests/data/parity/checks.json) and judges
                R02-noise-spectrum    the spectrum slope of the alpha stripes in three octave bands (band 1.3 to 2.6)
                R02-noise-repetition  the autocorrelation of a 40 x 20 km optical-depth map at the volume's tile distance (<= 0.15)
              with the harness's own functions and thresholds (parity_checks.run_check; nothing is set here). Added rows of the package:
                the repetition at 2 tiles and at 20 km, and the same with the untiling off (the check must be able to fail: >= 0.5)
                the pass against the CPU model on the same rays (noise_model.py: optical depth error)
                the flow map: the direction the pattern moves in against the map's direction (<= 5 deg) in two patches
                the generation time and the bytes of the volumes at the Medium tier (reported, limits [proposed])
              and writes the pictures of the view table (a 40 km deck from 8 km, an edge at 500 m: natural + aid, with the A/B variants
              curl off, untiling off, noise off) and the aid plots (spectrum, autocorrelation map).

The reference column of R02 is the harness's own (parity_run.py reference); the TVC column is TV-NOISE-STRIPE of tests/data/parity/tvc-capture.md.
The captures are written to build/weather-series/T02-ours/<check id>/ so that
    python tools/weather_metrics/parity_run.py table --reference <folder> --ours build/weather-series/T02-ours
prints the reference / TVC / ours table.
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
from weather_metrics import noise_model as nm  # noqa: E402
from weather_metrics import parity_checks as pc  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import parity_run as run  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "T02-in")
OURS = os.path.join(BUILD, "T02-ours")
R = pr.KERBIN_RADIUS

SCALE = 375.0                      # the noise scale of the test deck: the shape's finest lattice cell (m)
DECK = (2200.0, 2500.0)            # the deck of the harness's noise scenes
HIGH = 300000.0                    # the camera's height above sea level for the nadir jobs (m)
MEASURE_STEPS = 24                 # march steps through the 300 m deck of the measurement jobs (12.5 m)
CPU_W, CPU_H = 128, 64             # the pixels of the job that is compared with the CPU model
FLOW_W, FLOW_H = 4096, 2048
# The flow map's four quadrants around longitude 0, the equator: the direction (deg, east through north) of each. Distinct, so that a
# swapped hemisphere, a flipped row order or a mirrored east would each give an error far above 5 degrees.
FLOW_DIRECTIONS = {"NW": 30.0, "NE": 135.0, "SW": 210.0, "SE": 300.0}


def flow_share(angle_deg):
    return (math.cos(math.radians(angle_deg)), math.sin(math.radians(angle_deg)))
SPEC_TILES = 8                     # every realisation of the spectrum check: 8 tiles of 12 stripes (96 stripes over 96 km)

FLOW_AMPLITUDE = 2000.0
FLOW_TIMES = (0.0, 24.0)           # seconds; the loop is 120 s, so the phases are 0 and 0.2: 400 m of travel


# --------------------------------------------------------------------------------------------------------------------------------
# Jobs (the layout of tools/weather_metrics/layers.py, which the same scene reads)
# --------------------------------------------------------------------------------------------------------------------------------
def vec(v):
    return [float(x) for x in v]


def camera_fields(cam):
    return dict(position=vec(cam.pos), forward=vec(cam.forward), right=vec(cam.right), up=vec(cam.cam_up), fovV=float(cam.fov_v))


def slab_type(name, base, top, profile, erosion):
    return dict(name=name, baseShare=float(base), topShare=float(top), erosion=float(erosion), profile=[float(p) for p in profile])


def layer(inner, outer, sigma, types, coverage, softness, noise_scale, noise_amount=1.0, **kw):
    d = dict(name="deck", inner=float(inner), outer=float(outer), sigma=float(sigma), coverage=float(coverage), softness=float(softness), kind=0,
             fadeNear=0.0, fadeFar=0.0, coverageMap="", typeMap="", noiseAmount=float(noise_amount), noiseScale=float(noise_scale), shift=0.0,
             emission=[0.0, 0.0, 0.0], types=types, flowAmplitude=0.0, flowLooped=0, colourStrength=0.0, rise=0.0, drift=[0.0, 0.0, 0.0])
    d.update(kw)
    return d


def job(job_id, mode, cam, layers, width, height, noise=None, sun=(0.0, 0.0, 1.0), **kw):
    j = dict(id=job_id, mode=mode, width=int(width), height=int(height), planetRadius=float(R), hasSurface=1, sun=vec(sun), rayLimit=0.0,
             maxSteps=MEASURE_STEPS, sunSteps=3, earlyOut=0.0, minStep=1.0, stepGrowth=0.0, jitter=0.0, sunMarch=4000.0, resolution=1.0, layerMask=0,
             mapWidth=0, mapHeight=0, layers=layers, occluder=[0.0, 0.0, 0.0, 0.0])
    j.update(camera_fields(cam))
    if noise is not None:
        j["noise"] = noise
    j.update(kw)
    return j


def deck_layer(tau_vertical, erosion=0.5, **kw):
    """The deck of the checks as a layer of the pass: the coverage threshold at the middle of the noise (coverage 0), the layer default
    soft edge 0.15 on the noise value, vertical optical depth tau_vertical when fully covered."""
    thick = DECK[1] - DECK[0]
    return layer(DECK[0], DECK[1], tau_vertical / thick, [slab_type("cumulus", 0.0, 1.0, (1.0, 1.0), erosion)], 0.0, 0.15, SCALE, **kw)


def nadir(width, height, pixel_m, north_m=0.0):
    """A camera 300 km above longitude 0 at `north_m` north of the equator looking straight down (image columns run east), with square
    pixels of `pixel_m` on the deck's middle."""
    dist = HIGH - 0.5 * (DECK[0] + DECK[1])
    half = height * pixel_m * 0.5 / dist
    lat = north_m / R
    pos = [(R + HIGH) * math.cos(lat), 0.0, (R + HIGH) * math.sin(lat)]
    return pr.Camera(pos, 0.0, -90.0, width, height, 2.0 * math.degrees(math.atan(half)))


def noise_block(variant):
    """The switches of a variant: '' production, 'nocurl', 'nountile', 'nonoise', 'seedN' (the volumes' seed N)."""
    n = dict(disabled=0, noUntile=0, noCurl=0, sphere=0.0, seed=1, sizeLevel=1)
    if variant == "nocurl":
        n["noCurl"] = 1
    elif variant == "nountile":
        n["noUntile"] = 1
    elif variant == "nonoise":
        n["disabled"] = 1
    elif variant == "nolod":
        n["noLod"] = 1
    elif variant.startswith("seed"):
        n["seed"] = int(variant[4:])
    return n


SPEC_VARIANTS = ("", "nocurl", "nountile", "seed2", "seed3")


def flow_map_file():
    """The flow map of the direction test: the share east/north as RGBA bytes, a direction for each quadrant around longitude 0 and the
    equator (row 0 is the north pole: the north half is the first FLOW_H / 2 rows, longitude -180 is column 0)."""
    os.makedirs(INPUT, exist_ok=True)
    rgba = np.zeros((FLOW_H, FLOW_W, 4), dtype=np.uint8)
    halves = {"N": slice(0, FLOW_H // 2), "S": slice(FLOW_H // 2, FLOW_H), "W": slice(0, FLOW_W // 2), "E": slice(FLOW_W // 2, FLOW_W)}
    for name, angle in FLOW_DIRECTIONS.items():
        share = flow_share(angle)
        rows, cols = halves[name[0]], halves[name[1]]
        rgba[rows, cols, 0] = np.uint8(round((share[0] * 0.5 + 0.5) * 255.0))
        rgba[rows, cols, 1] = np.uint8(round((share[1] * 0.5 + 0.5) * 255.0))
    rgba[:, :, 2] = 128
    rgba[:, :, 3] = 255
    rgba.tofile(os.path.join(INPUT, "flow-patches.rgba"))
    return "flow-patches.rgba"


def build_jobs():
    jobs = []
    # R02 spectrum: tiles of 800 x 240 pixels of 50 m (40 x 12 km); the stripes are every 20th row (1 km apart, as the harness's). The
    # production realisation is SPEC_TILES tiles side by side to the north (96 stripes: the slope estimate of one 12-stripe tile scatters
    # by 0.3 because the stripes are correlated at the 1 to 3 km of the shape's cells; see tools/weather_metrics/noise.py header notes).
    for v in SPEC_VARIANTS:
        for k in range(SPEC_TILES):
            cam = nadir(800, 240, 50.0, north_m=(k - 0.5 * (SPEC_TILES - 1)) * 12000.0)
            jobs.append(job("N-spec-%s-%d" % (v or "on", k), "aux", cam, [deck_layer(1.0)], 800, 240, noise=noise_block(v)))
    # R02 repetition: 200 x 100 pixels of 200 m (40 x 20 km), optical depth 6
    cam = nadir(200, 100, 200.0)
    for v in SPEC_VARIANTS:
        jobs.append(job("N-rep-%s" % (v or "on"), "aux", cam, [deck_layer(6.0)], 200, 100, noise=noise_block(v)))
    # the pass against the CPU model on the same rays: 128 x 64 pixels of 200 m (25.6 x 12.8 km: several tiles and edges), optical depth 1
    cam = nadir(CPU_W, CPU_H, 200.0)
    jobs.append(job("N-cpu", "aux", cam, [deck_layer(1.0)], CPU_W, CPU_H, noise=noise_block("")))
    # the flow map: two phases, a pass each, 400 x 200 pixels of 50 m (20 x 10 km) over longitude 0
    name = flow_map_file()
    cam = nadir(400, 200, 50.0)
    for k, t in enumerate(FLOW_TIMES):
        noise = noise_block("")
        noise.update(flowMap=name, mapWidth=FLOW_W, mapHeight=FLOW_H, flowTime=float(t))
        jobs.append(job("N-flow-%d" % k, "aux", cam, [deck_layer(6.0, flowAmplitude=FLOW_AMPLITUDE, flowLooped=0)], 400, 200, noise=noise))
    return jobs + picture_jobs()


# --------------------------------------------------------------------------------------------------------------------------------
# The view table: pictures
# --------------------------------------------------------------------------------------------------------------------------------
CUMULUS = [0.0, 0.55, 0.95, 1.0, 0.8, 0.4, 0.0]
VIEW_LAYER = (1500.0, 3000.0)


def view_layer(erosion=0.5):
    thick = VIEW_LAYER[1] - VIEW_LAYER[0]
    return layer(VIEW_LAYER[0], VIEW_LAYER[1], 0.006, [slab_type("cumulus", 0.0, 0.7, CUMULUS, erosion)], 0.0, 0.20, SCALE)


def sun_vector(pos, azimuth, elevation):
    return pr.Sun.at(np.asarray(pos, dtype=np.float64), azimuth, elevation).direction


def sphere_xy(east_m, north_m, altitude):
    """Points at east and north distances (m) from longitude 0 on the equator (great-circle approximation) at an altitude."""
    lon = np.asarray(east_m, dtype=np.float64) / R
    lat = np.asarray(north_m, dtype=np.float64) / R
    r = R + altitude
    return np.stack([r * np.cos(lat) * np.cos(lon), r * np.cos(lat) * np.sin(lon), r * np.sin(lat)], axis=1)


EDGE_GAP = 1500.0          # the distance from the camera to the cloud wall of the edge views (m)
EDGE_HEIGHT_BELOW = 300.0  # the camera's height under the layer's middle (m)


def edge_site():
    """A place (east, north in m) found on the CPU model (the same density function as the pass) where the camera at the layer's middle
    height stands in clear air (no cloud within 1.2 km) and a broken cloud wall stands EDGE_GAP to EDGE_GAP + 2 km east of it: a side view of
    the fractal edge from outside the cloud. The score is the share of the forward sample points in the edge band (0.05 < share < 0.95)
    among sites that have both cloud (> 0.6) in at least a third and clear air (< 0.05) in at least a tenth of the points. Returns
    (east, north, score)."""
    vols = nm.Volumes(seed=1).quantized()
    prm = model_params(1, 0)
    alt = 0.5 * (VIEW_LAYER[0] + VIEW_LAYER[1]) - EDGE_HEIGHT_BELOW
    ring = [(r * math.cos(a), r * math.sin(a)) for r in (300.0, 700.0, 1000.0) for a in np.linspace(0.0, 2.0 * math.pi, 12, endpoint=False)]
    wall = [(d * math.cos(a), d * math.sin(a)) for d in np.arange(EDGE_GAP, EDGE_GAP + 2001.0, 250.0) for a in np.radians(np.linspace(-25.0, 25.0, 9))]
    # in the frame of the site: x east, y north; the wall is east of the camera
    best = (None, None, -1.0)
    for north in np.arange(-8000.0, 8001.0, 1000.0):
        for east in np.arange(3000.0, 60000.0, 400.0):
            pts = [(east + dx, north + dy) for dx, dy in ring] + [(east + dx, north + dy) for dx, dy in wall]
            xy = np.array(pts)
            s = share(vols, prm, sphere_xy(xy[:, 0], xy[:, 1], alt))
            clear_ring = (s[:len(ring)] > 0.05).sum() <= 1
            w = s[len(ring):]
            if not clear_ring or (w > 0.6).mean() < 0.25 or (w < 0.05).mean() < 0.05:
                continue
            score = float(((w > 0.05) & (w < 0.95)).mean())
            if score > best[2]:
                best = (float(east), float(north), score)
    return best if best[0] is not None else (6000.0, 0.0, 0.0)


def model_params(seed, layer_index, scale=SCALE, erosion=0.5, **kw):
    return nm.Params(scale=scale, untile=True, curl=True, seed=nm.layer_seed(seed, layer_index), offset=nm.layer_offset(seed, layer_index), erosion=erosion, **kw)


def share(vols, prm, p, coverage=0.0, edge=0.20):
    n = nm.shape_value(vols, p, prm)
    s = np.clip(((n - 0.5) + coverage) / edge + 0.5, 0.0, 1.0)
    d = nm.detail_value(vols, p, prm)
    return nm.erode(s, d, prm.erosion, prm)


def picture_jobs():
    views = []
    size = (960, 540)

    def add(view_id, cam, variant, sun_az=150.0, sun_el=38.0, steps=96, ray_limit=0.0):
        views.append(job("V-%s" % view_id, "color", cam, [view_layer()], size[0], size[1], noise=noise_block(variant), sun=sun_vector(cam.pos, sun_az, sun_el),
                         maxSteps=steps, rayLimit=ray_limit, minStep=12.0, stepGrowth=0.0002, jitter=0.0, earlyOut=0.01, sunSteps=4))

    # a 40 km deck from 8 km: pitch -9 degrees, 60 degrees of view, looking east along the equator
    cam = pr.Camera([R + 8000.0, 0.0, 0.0], 90.0, -9.0, size[0], size[1], 60.0)
    for v in ("", "nocurl", "nountile", "nonoise", "nolod"):
        add("deck40-%s" % (v or "on"), cam, v)
    # the fractal edge from outside the cloud: the camera at the layer's middle height in clear air, the wall 1.5 km (and 2 km) east of it,
    # a side view. Sun toward the wall and low (az 90, 12 deg: the edge is backlit, the silver lining), and the sun behind the camera (the lit face).
    east, north, _ = edge_site()
    alt = 0.5 * (VIEW_LAYER[0] + VIEW_LAYER[1]) - EDGE_HEIGHT_BELOW
    pos = sphere_xy([east], [north], alt)[0]
    # The ray ends at 8 km and the march has 400 steps, so the spacing is 20 m or less: the whole edge is sampled (the LOD does not act).
    cam = pr.Camera(pos, 90.0, 3.0, size[0], size[1], 60.0)
    for v in ("", "nocurl", "nonoise", "nolod"):
        add("edge1500-back-%s" % (v or "on"), cam, v, 90.0, 12.0, 400, 8000.0)
        if v != "nolod":
            add("edge1500-front-%s" % (v or "on"), cam, v, 270.0, 35.0, 400, 8000.0)
    cam = pr.Camera(sphere_xy([east - 500.0], [north], alt)[0], 90.0, 3.0, size[0], size[1], 60.0)
    add("edge2000-back-on", cam, "", 90.0, 12.0, 400, 8000.0)
    return views


# --------------------------------------------------------------------------------------------------------------------------------
# Reading the arrays, the captures and the measurements
# --------------------------------------------------------------------------------------------------------------------------------
def plane(folder, job_id, name):
    return np.load(os.path.join(folder, "%s-%s.npy" % (job_id, name))).astype(np.float64)


def stripes_of(folder, variant):
    return {"alpha_stripes": np.concatenate([plane(folder, "N-spec-%s-%d" % (variant or "on", k), "alpha")[::20] for k in range(SPEC_TILES)]), "dx": 50.0}


def tau_of(folder, variant, lag):
    return {"tau_map": plane(folder, "N-rep-%s" % (variant or "on"), "tau"), "tile_lag": int(lag)}


def shape_period_lag():
    """The shape volume's period in samples of the repetition map (200 m)."""
    return int(round(nm.Params(scale=SCALE).shape_period / 200.0))


def detail_period_lag():
    return int(round(nm.Params(scale=SCALE).detail_period / 200.0))


def displacement(a, b, window):
    """The (dy, dx) shift in pixels of image b against image a over a window (rows, cols slices), by the peak of the cross-correlation
    (FFT, parabolic sub-pixel peak). The pattern moves by +shift: b(p) = a(p - shift)."""
    A = a[window] - a[window].mean()
    B = b[window] - b[window].mean()
    w = np.outer(np.hanning(A.shape[0]), np.hanning(A.shape[1]))
    cc = np.fft.irfft2(np.fft.rfft2(B * w) * np.conj(np.fft.rfft2(A * w)), s=A.shape)
    iy, ix = np.unravel_index(np.argmax(cc), cc.shape)
    out = []
    for i, n, axis in ((iy, A.shape[0], 0), (ix, A.shape[1], 1)):
        lo, hi = cc[(i - 1) % n, ix] if axis == 0 else cc[iy, (i - 1) % n], cc[(i + 1) % n, ix] if axis == 0 else cc[iy, (i + 1) % n]
        mid = cc[iy, ix]
        denom = lo - 2.0 * mid + hi
        frac = 0.5 * (lo - hi) / denom if denom != 0 else 0.0
        pos = i + frac
        out.append(pos - n if pos > n / 2 else pos)
    return out[0], out[1]


def flow_measure(folder):
    """The direction (deg, east through north) and length (m) the pattern moved between the two phases in the four quadrants of the flow map,
    against the map's direction. Returns {quadrant: (measured angle, expected angle, error deg, length m, expected length m)}."""
    a, b = plane(folder, "N-flow-0", "tau"), plane(folder, "N-flow-1", "tau")
    cam = nadir(400, 200, 50.0)
    east = np.array([0.0, 1.0, 0.0])
    north = np.array([0.0, 0.0, 1.0])
    col_dir = np.array([np.dot(cam.right, east), np.dot(cam.right, north)])      # image columns: east/north components
    row_dir = -np.array([np.dot(cam.cam_up, east), np.dot(cam.cam_up, north)])   # rows run down
    out = {}
    # The windows keep 1.5 km (30 pixels) from the quadrant seams (column 200, row 100) and from the image border.
    windows = {"NW": (slice(10, 80), slice(20, 170)), "NE": (slice(10, 80), slice(230, 380)),
               "SW": (slice(120, 190), slice(20, 170)), "SE": (slice(120, 190), slice(230, 380))}
    for name, window in windows.items():
        dy, dx = displacement(a, b, window)
        shift = dx * col_dir * 50.0 + dy * row_dir * 50.0
        got = math.degrees(math.atan2(shift[1], shift[0]))
        want = FLOW_DIRECTIONS[name]
        err = abs((got - want + 180.0) % 360.0 - 180.0)
        out[name] = (got, want, err, float(np.hypot(*shift)), FLOW_AMPLITUDE * (FLOW_TIMES[1] - FLOW_TIMES[0]) / 120.0)
    return out


def cpu_rays(cam, prm_job):
    d = cam.ray_directions()
    return np.asarray(cam.pos), d


def cpu_parity(folder):
    """The pass's optical depth against the CPU model's on the same rays (the shader's march, the model in double precision)."""
    cam = nadir(CPU_W, CPU_H, 200.0)
    origin, dirs = cpu_rays(cam, None)
    vols = nm.Volumes(seed=1).quantized()
    prm = model_params(1, 0)
    thick = DECK[1] - DECK[0]
    ref = nm.planet_ray_tau(vols, prm, origin, dirs, R, DECK[0], DECK[1], 1.0 / thick, MEASURE_STEPS, 1.0, 0.0).reshape(CPU_H, CPU_W)
    got = plane(folder, "N-cpu", "tau")
    m = ref > 0.1
    rel = np.abs(got[m] - ref[m]) / ref[m]
    edge = (ref > 0.05) & (ref < 0.95)       # rays through the partly covered edge band (the vertical optical depth is 1)
    erel = np.abs(got[edge] - ref[edge]) / ref[edge]
    return {"max_abs": float(np.max(np.abs(got - ref))), "mean_rel": float(rel.mean()) if m.any() else float("nan"),
            "p95_rel": float(np.percentile(rel, 95)) if m.any() else float("nan"), "n": int(m.sum()), "n_edge": int(edge.sum()),
            "edge_p95_rel": float(np.percentile(erel, 95)) if edge.any() else float("nan"), "ref_max": float(ref.max()), "got": got, "ref": ref}


# --------------------------------------------------------------------------------------------------------------------------------
# Aid pictures
# --------------------------------------------------------------------------------------------------------------------------------
def aid_plots(folder, out_dir):
    """The spectrum of the alpha stripes with the band and a -5/3, -2 line, and the autocorrelation map of the optical depth with the tile
    distances marked. PNG files in out_dir; returns their names."""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    os.makedirs(out_dir, exist_ok=True)
    fig, ax = plt.subplots(1, 2, figsize=(12, 4.5))
    for variant, colour, label in (("", "#1b6ca8", "production"), ("nountile", "#c0392b", "untiling off"), ("nocurl", "#2e8b57", "curl off")):
        s = stripes_of(folder, variant)["alpha_stripes"]
        f = s - s.mean(axis=1, keepdims=True)
        p = (np.abs(np.fft.rfft(f, axis=1)) ** 2).mean(axis=0)
        k = np.fft.rfftfreq(s.shape[1], 50.0)
        m = k > 0
        ax[0].loglog(k[m], p[m], color=colour, lw=1.0, label=label)
    kk = np.array([1 / 2400.0, 1 / 300.0])
    anchor = None
    s = stripes_of(folder, "")["alpha_stripes"]
    f = s - s.mean(axis=1, keepdims=True)
    p = (np.abs(np.fft.rfft(f, axis=1)) ** 2).mean(axis=0)
    k = np.fft.rfftfreq(s.shape[1], 50.0)
    i0 = int(np.argmin(np.abs(k - 1 / 1200.0)))
    for slope, ls in ((1.3, ":"), (2.0, "--"), (2.6, ":")):
        ax[0].loglog(kk, p[i0] * (kk / k[i0]) ** (-slope), "k" + ls, lw=0.8, label="slope %.1f" % slope)
    for e in (1 / 2400.0, 1 / 1200.0, 1 / 600.0, 1 / 300.0):
        ax[0].axvline(e, color="#888", lw=0.5)
    ax[0].set_xlabel("frequency (1/m)")
    ax[0].set_ylabel("power of alpha")
    ax[0].set_title("Spectrum of the alpha stripes (bands 2.4-1.2, 1.2-0.6, 0.6-0.3 km)")
    ax[0].legend(fontsize=7)
    tau = plane(folder, "N-rep-on", "tau")
    f = tau - tau.mean()
    ac = np.fft.irfft2(np.abs(np.fft.rfft2(f)) ** 2, s=f.shape)
    ac = ac / ac[0, 0]
    shown = np.fft.fftshift(ac)
    ext = [-100 * 0.2, 100 * 0.2, -50 * 0.2, 50 * 0.2]
    im = ax[1].imshow(shown, extent=ext, cmap="RdBu_r", vmin=-0.5, vmax=0.5, origin="lower")
    for d in (6.0, 12.0):
        ax[1].add_patch(plt.Circle((0, 0), d, fill=False, color="k", lw=0.6, ls="--"))
    ax[1].set_title("Autocorrelation of the optical depth (40 x 20 km); circles: 1 and 2 tiles")
    ax[1].set_xlabel("km")
    ax[1].set_ylabel("km")
    fig.colorbar(im, ax=ax[1], fraction=0.04)
    fig.tight_layout()
    name = "T02-aid-spectrum-autocorrelation.png"
    fig.savefig(os.path.join(out_dir, name), dpi=110)
    plt.close(fig)
    # the same autocorrelation with the untiling off, to see the tile
    tau = plane(folder, "N-rep-nountile", "tau")
    f = tau - tau.mean()
    ac = np.fft.irfft2(np.abs(np.fft.rfft2(f)) ** 2, s=f.shape)
    ac = ac / ac[0, 0]
    fig, a2 = plt.subplots(1, 1, figsize=(6, 3.2))
    im = a2.imshow(np.fft.fftshift(ac), extent=ext, cmap="RdBu_r", vmin=-0.5, vmax=0.5, origin="lower")
    a2.set_title("Autocorrelation with the untiling off: the tile shows")
    fig.colorbar(im, ax=a2, fraction=0.04)
    fig.tight_layout()
    name2 = "T02-aid-autocorrelation-nountile.png"
    fig.savefig(os.path.join(out_dir, name2), dpi=110)
    plt.close(fig)
    return [name, name2]


def difference_pictures(folder, out_dir):
    """The A/B images: for each view the production picture against a variant, the absolute difference and its mean over the region where the
    two differ. Returns {pair: (mean difference over the region, share of pixels that differ)}."""
    from PIL import Image
    out = {}
    pairs = (("deck40-on", "deck40-nocurl"), ("deck40-on", "deck40-nountile"), ("deck40-on", "deck40-nonoise"), ("deck40-on", "deck40-nolod"),
             ("edge1500-back-on", "edge1500-back-nolod"),
             ("edge1500-back-on", "edge1500-back-nocurl"), ("edge1500-back-on", "edge1500-back-nonoise"),
             ("edge1500-front-on", "edge1500-front-nocurl"), ("edge1500-front-on", "edge1500-front-nonoise"))
    for a, b in pairs:
        pa, pb = os.path.join(folder, "V-%s.png" % a), os.path.join(folder, "V-%s.png" % b)
        if not (os.path.isfile(pa) and os.path.isfile(pb)):
            continue
        ia = np.asarray(Image.open(pa).convert("RGB"), dtype=np.float64) / 255.0
        ib = np.asarray(Image.open(pb).convert("RGB"), dtype=np.float64) / 255.0
        d = np.abs(ia - ib).mean(axis=2)
        region = d > 0.02
        out["%s|%s" % (a, b)] = (float(d[region].mean()) if region.any() else 0.0, float(region.mean()))
        both = np.concatenate([ia, ib, np.repeat(np.clip(d * 4.0, 0, 1)[:, :, None], 3, axis=2)], axis=1)
        Image.fromarray(np.uint8(both * 255.0)).save(os.path.join(out_dir, "T02-ab-%s-vs-%s.png" % (a, b)))
    return out


# --------------------------------------------------------------------------------------------------------------------------------
# The series
# --------------------------------------------------------------------------------------------------------------------------------
def collect(ctx):
    spec = pc.load_spec()
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    if not os.path.isfile(os.path.join(folder, "N-spec-on-0-alpha.npy")):
        out.append(Metric.flag("written", False))
        return out
    os.makedirs(OURS, exist_ok=True)
    s = spec["R02-noise-spectrum"]
    lo, hi = s["threshold"]["band"]
    mid = 0.5 * (lo + hi)
    rel = 0.5 * (hi - lo) / mid
    for variant in SPEC_VARIANTS:
        data = stripes_of(folder, variant)
        m = pc.run_check(s["function"], data, s)
        tag = variant or "on"
        if variant == "":
            # The production realisation is the gate: each band inside the harness's band (a reference metric around its middle).
            for i, slope in enumerate(m.detail["slopes"]):
                out.append(Metric.reference("R02-noise-spectrum.b%d" % (i + 1), slope, mid, rel=rel).based("gate"))
            run.write_capture(os.path.join(OURS, "R02-noise-spectrum"), data)
            out.append(Metric.flag("R02-noise-spectrum.all_bands", m.ok).based("gate"))
        elif variant in ("seed2", "seed3"):
            # Other realisations (other volume seeds) are reported: the check judges one realisation, the pass rate over seeds is the model's.
            out.append(Metric.info("R02-noise-spectrum.%s.all_bands" % variant, 1.0 if m.ok else 0.0))
            for i, slope in enumerate(m.detail["slopes"]):
                out.append(Metric.info("R02-noise-spectrum.%s.b%d" % (variant, i + 1), slope))
        else:
            out.append(Metric.info("R02-noise-spectrum.%s.mean" % tag, m.value))
            for i, slope in enumerate(m.detail["slopes"]):
                out.append(Metric.info("R02-noise-spectrum.%s.b%d" % (tag, i + 1), slope))
    r = spec["R02-noise-repetition"]
    limit = r["threshold"]["max"]
    for variant in SPEC_VARIANTS:
        tag = variant or "on"
        for label, lag in (("tile", shape_period_lag()), ("2tiles", 2 * shape_period_lag()), ("20km", 100), ("detail", detail_period_lag())):
            data = tau_of(folder, variant, lag)
            m = pc.run_check(r["function"], data, r)
            value = abs(m.detail["r"])
            if variant == "" and label == "tile":
                run.write_capture(os.path.join(OURS, "R02-noise-repetition"), data)
                out.append(Metric.at_most("R02-noise-repetition", value, limit).based("gate"))
            elif variant == "" and label in ("2tiles", "20km"):
                out.append(Metric.at_most("R02-noise-repetition.%s" % label, value, limit).based("gate"))
            elif variant in ("seed2", "seed3") and label == "tile":
                # A 40 x 20 km map holds about 20 independent blobs, so one realisation scatters; the other seeds are judged the same way.
                out.append(Metric.at_most("R02-noise-repetition.%s" % variant, value, limit).based("gate"))
            elif variant == "nountile" and label == "tile":
                # The check must be able to fail: with the untiling off the volume repeats exactly.
                out.append(Metric.at_least("R02-noise-repetition.tiled_detected", value, 0.5).based("new"))
            else:
                out.append(Metric.info("R02-noise-repetition.%s.%s" % (tag, label), value))
    cp = cpu_parity(folder)
    out.append(Metric.at_most("noise.cpu_model_tau_p95_rel", cp["p95_rel"], 0.03).based("new"))
    # The comparison must include edge rays, where the share and the erosion act: at least 100 of the 8192 rays, and the same limit on them.
    out.append(Metric.at_least("noise.cpu_model_edge_rays", float(cp["n_edge"]), 100.0).based("new"))
    out.append(Metric.at_most("noise.cpu_model_edge_p95_rel", cp["edge_p95_rel"], 0.05).based("new"))
    out.append(Metric.info("noise.cpu_model_tau_mean_rel", cp["mean_rel"]))
    out.append(Metric.info("noise.cpu_model_tau_max_abs", cp["max_abs"]))
    out.append(Metric.info("noise.cpu_model_tau_ref_max", cp["ref_max"]))
    flow = flow_measure(folder)
    for patch, (got, want, err, length, wlen) in flow.items():
        out.append(Metric.at_most("R16-flow-angle.%s" % patch, err, 5.0).based("gate"))
        out.append(Metric.info("R16-flow-angle.%s.measured_deg" % patch, got))
        out.append(Metric.info("R16-flow-angle.%s.expected_deg" % patch, want))
        out.append(Metric.at_most("R16-flow-length.%s" % patch, abs(length - wlen) / wlen, 0.10).based("new"))
        out.append(Metric.info("R16-flow-length.%s.measured_m" % patch, length))
    aids = aid_plots(folder, folder)
    diffs = difference_pictures(folder, folder)
    for pair, (mean_d, share_d) in diffs.items():
        out.append(Metric.info("ab.%s.mean_diff" % pair, mean_d))
        out.append(Metric.info("ab.%s.share" % pair, share_d))
    east, north, score = edge_site()
    out.append(Metric.info("edge.site_score", score))
    out.append(Metric.flag("edge.site_found", score > 0.0))
    for view in ("deck40-on", "deck40-nocurl", "deck40-nountile", "deck40-nonoise", "deck40-nolod", "edge1500-back-nolod", "edge1500-back-on", "edge1500-back-nocurl", "edge1500-back-nonoise",
                 "edge1500-front-on", "edge1500-front-nocurl", "edge1500-front-nonoise", "edge2000-back-on"):
        ok = os.path.isfile(os.path.join(folder, "V-%s.png" % view)) and os.path.isfile(os.path.join(folder, "V-%s-aid.png" % view))
        out.append(Metric.flag("view.%s" % view, ok))
    out.append(Metric.flag("aid.plots", all(os.path.isfile(os.path.join(folder, a)) for a in aids)))
    return out


def write_inputs():
    jobs = build_jobs()
    os.makedirs(INPUT, exist_ok=True)
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def stages():
    jobs = os.path.join(INPUT, "jobs.json")
    newest = max(os.path.getmtime(os.path.join(HERE, name)) for name in ("noise.py", "noise_model.py", "parity_checks.py"))
    if not os.path.isfile(jobs) or os.path.getmtime(jobs) < newest:
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False)]


register_series("T02-noise", stages(), collect, exclusive=False, who="T02")


# --------------------------------------------------------------------------------------------------------------------------------
# The cost of the shape model: T01's view table at half size with and without the noise (a timing run: exclusive, the proxy's GPU timestamps)
# --------------------------------------------------------------------------------------------------------------------------------
COST_INPUT = os.path.join(BUILD, "T02-cost-in")
COST_KEY = "noisecost.%s.ms"
COST_VIEWS = ("V-ground", "V-inside", "V-above", "V-orbit-edge", "V-stack-below", "V-stack-above")
# What each variant of a view switches off (the job id suffix; the scene layers-noise-cost reads "-noero" itself).
COST_VARIANTS = (("", ""), ("-nonoise", "nonoise"), ("-nountile", "nountile"), ("-nocurl", "nocurl"), ("-noero", ""), ("-nolod", "nolod"))


def write_cost_inputs():
    """T01's view-table jobs (tools/weather_metrics/layers.py), once per variant of COST_VARIANTS, in the production sampling of the layer cost
    table (scene layers-noise-cost)."""
    import copy
    import shutil
    from weather_metrics import layers as ly
    ly.write_inputs()
    os.makedirs(COST_INPUT, exist_ok=True)
    for name in ("view-coverage.r8", "view-type.r8"):
        shutil.copy2(os.path.join(ly.INPUT, name), os.path.join(COST_INPUT, name))
    jobs = []
    for j in ly.picture_jobs():
        for suffix, variant in COST_VARIANTS:
            a = copy.deepcopy(j)
            a["id"] = j["id"] + suffix
            a["noise"] = noise_block(variant)
            jobs.append(a)
    with open(os.path.join(COST_INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def collect_cost(ctx):
    """The cost of each part of the noise per view: the milliseconds of the pass with everything on, and the saving of switching a part off
    (nonoise = the whole model, nountile = the warp, nocurl = the curl fetch, noero = the erosion, nolod = the distance LOD: negative saving
    means the LOD makes the pass cheaper)."""
    out = list(core.player_judged("scene")(ctx))
    metrics = {m.name: m.value for m in out}
    for v in COST_VIEWS:
        on = metrics.get(COST_KEY % v)
        if not isinstance(on, (int, float)):
            continue
        out.append(Metric.info("noise_cost.%s.on_ms" % v, on))
        for suffix, _ in COST_VARIANTS[1:]:
            off = metrics.get(COST_KEY % (v + suffix))
            if isinstance(off, (int, float)):
                out.append(Metric.info("noise_cost.%s%s.ms" % (v, suffix), off))
                out.append(Metric.info("noise_cost.%s%s.on_minus" % (v, suffix), on - off))
    return out


def cost_stages():
    jobs = os.path.join(COST_INPUT, "jobs.json")
    newest = max(os.path.getmtime(os.path.abspath(__file__)), os.path.getmtime(os.path.join(HERE, "layers.py")))
    if not os.path.isfile(jobs) or os.path.getmtime(jobs) < newest:
        write_cost_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "layers-noise-cost", "-in", COST_INPUT], size=(1707, 960), proxy=True, timeout=1500)]


register_series("T02-cost", cost_stages(), collect_cost, exclusive=True, who="T02")
