"""The series of the cloud light (package T03), run through tools/build_weather_player.py --series T03-light (the judged table),
T03-fit (the parameter sweep that fixes the look constants) and T03-views (the pictures):

  The scene 'light' of the weather player draws the own cloud system's layer pass with the light of package T03 (the shader
  ReDefinition/AtmospheresLayers and its include ReDefinitionAtmospheresCloudLight.cginc) on the synthetic scenes of the parity harness.
  This module writes the jobs, reads the arrays the scene wrote, builds the capture of each check in the shape the harness reads
  (tools/weather_metrics/parity_checks.py) and judges it with the harness's function and the threshold of tests/data/parity/checks.json
  (no threshold is set here). The reference figures come from the brute-force renderer (parity_ref.py) at the full quality:

      python tools/weather_metrics/parity_run.py reference build/parity-ref-full --quality full

  The captures are also written to build/weather-series/T03-ours/<check id>/, so that parity_run.py table prints the
  reference / TVC / ours table.

Scenes: the harness's 'single-cloud' cell (an ellipsoid of 800 m radius with a flat base at 2000 m altitude, extinction 0.02 per m, seen from
8 km, field of view 16 degrees) as the analytic cell of the shader (_CellA/_CellB: the cell replaces the layers' density, so that the
density field of both renderers is the same); the harness's slab scenes; the T01 view table for the pictures. The sun is pi (a white
Lambertian surface lit head-on returns its albedo), the ambient the harness's environment (zenith 0.30, horizon 0.55, ground 0.12).

Look constants: the octave numbers a, b, c, the multiple scattering brightness, the powder strength and length and the ambient occlusion are
fixed by the sweep (T03-fit; at most three rounds) against the reference; MODEL below holds the result and CloudLightSettings carries the same.
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
from weather_metrics import core  # noqa: E402
from weather_metrics import parity_checks as pc  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import parity_run as run  # noqa: E402
from weather_metrics import layers as L  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
# The length of the sun march of the parity and hold-out jobs: 4000 m is the shipped default (the length these rows were measured at); T14 re-runs them at
# 9000 m (LayerFrame.SunMarchLongM, the key cloudSunMarchLong) with T03_SUN_MARCH=9000 so that the owner can decide the product default. The inputs and the
# pictures of another length live in folders of their own.
SUN_MARCH_M = float(os.environ.get("T03_SUN_MARCH", "4000"))
SUN_TAG = "" if SUN_MARCH_M == 4000.0 else "-m%d" % int(SUN_MARCH_M)
INPUT = os.path.join(BUILD, "T03-in" + SUN_TAG)
FIT_INPUT = os.path.join(BUILD, "T03-fit-in")
VIEWS_INPUT = os.path.join(BUILD, "T03-views-in")
OURS = os.path.join(BUILD, "T03-ours" + SUN_TAG)
PICTURES = os.path.join(BUILD, "T03-pictures" + SUN_TAG)
REFERENCE = os.environ.get("T03_REFERENCE", os.path.join(REPO, "build", "parity-ref-cell"))
R = pr.KERBIN_RADIUS
SUN_E = math.pi
ENV = pr.Environment(0.30, 0.55, 0.12)
CELL_CENTRE = [0.0, 0.0, R + 2000.0]

# The light's numbers as the parity run fixed them (CloudLightSettings holds the same values).
MODEL = dict(forwardG=0.85, backG=-0.3, forwardWeight=0.92, albedo=0.999, octaves=4, octaveA=0.3081, octaveB=0.5482, octaveC=0.898, msBrightness=2.9358, diffusion=2.0, diffusionTau=25.0, diffusionDecay=0.08,
             powder=0.8075, powderLength=400.0, ambientFloor=0.05, ambientOcclusion=1.2765, skyTint=0.7, ambientGain=0.25, sunSteps=4, moonSteps=2,
             upSteps=2, coneSpread=0.0, tapRatio=2.0, cacheFillSteps=12, msIsotropy=0.7552)
# The harness environment as the shader's ambient: the sky's mean over the sphere at the cell's top, the ground's share lower down.
AMBIENT_TOP, AMBIENT_BOTTOM = 0.30, 0.15


def vec(v):
    return [float(x) for x in v]


# --------------------------------------------------------------------------------------------------------------------------------
# Jobs
# --------------------------------------------------------------------------------------------------------------------------------
def flat_sun(azimuth, elevation):
    """The sun direction of the harness on its flat planet: azimuth from north (+y) towards east (+x), elevation above the horizon (z)."""
    az, el = math.radians(azimuth), math.radians(elevation)
    return [math.cos(el) * math.sin(az), math.cos(el) * math.cos(az), math.sin(el)]


def cell_camera(w, h=None):
    cam = pr.Camera([0.0, -8000.0, R + 2000.0], 0.0, 0.0, w, h or int(w * 0.75), 16.0)
    cam.forward = np.array([0.0, 1.0, 0.0])
    cam.right = np.array([1.0, 0.0, 0.0])
    cam.cam_up = np.array([0.0, 0.0, 1.0])
    return cam


def light_job(jid, cam, layers, width, height, sun, aid=0, planet=R, cell=False, sun_colour=None, amb_top=None, amb_bottom=None,
              layer_kw=None, **over):
    """One job of the light scene: the layer-pass job of the camera and layers plus the light's numbers (MODEL, overridden by over)."""
    kw = dict(maxSteps=128, minStep=1.0, stepGrowth=0.0, jitter=0.0, earlyOut=0.0, sunMarch=SUN_MARCH_M, sunSteps=int(MODEL["sunSteps"]),
              costLeap=1.0, costCut=0.0, lightStride=1)   # exact: no leaps, no reuse of the light march (T01c's cost controls are off)
    kw.update(layer_kw or {})
    sc = sun_colour if sun_colour is not None else [SUN_E] * 3
    top = amb_top if amb_top is not None else [AMBIENT_TOP] * 3
    bot = amb_bottom if amb_bottom is not None else [AMBIENT_BOTTOM] * 3
    lj = L.job(jid, "lum", cam, layers, width, height, planet=planet, surface=1, sun=sun, sunColour=vec(sc), ambientTop=vec(top),
               ambientBottom=vec(bot), **kw)
    j = dict(id=jid, mode="lum", aid=int(aid), useDefaults=0, layerJob=lj)
    j.update(MODEL)
    j.update(over)
    j["sunSteps"] = int(j["sunSteps"])
    if cell:
        j.update(cellCentre=CELL_CENTRE, cellRadius=800.0, cellVertical=560.0, cellSigma=over.get("cellSigma", 0.02))
    return j


def cell_job(jid, az, el, w=96, **over):
    cam = cell_camera(w)
    return light_job(jid, cam, [L.layer(1412.0, 2588.0, 0.02)], cam.width, cam.height, flat_sun(az, el), cell=True, **over)


def slab_job(jid, tau, thickness, base=2000.0, el=45.0, **over):
    cam = pr.Camera([0.0, 0.0, R + 2.0], 0.0, 90.0, 3, 3, 10.0)
    return light_job(jid, cam, [L.layer(base, base + thickness, tau / thickness)], 3, 3, flat_sun(200.0, el), **over)


def phase_job(jid, **over):
    cam = pr.Camera([0.0, 0.0, R + 2.0], 0.0, 0.0, 4001, 1, 10.0)
    return light_job(jid, cam, [L.layer(2000.0, 3000.0, 0.001)], 4001, 1, [0.0, 0.0, 1.0], aid=3, **over)


# --------------------------------------------------------------------------------------------------------------------------------
# Reading
# --------------------------------------------------------------------------------------------------------------------------------
def plane(folder, jid, ch):
    return np.load(os.path.join(folder, "%s-%s.npy" % (jid, ch))).astype(np.float64)


class Img(object):
    """The arrays the harness's helper functions read: alpha and the cloud's own radiance (grey)."""

    def __init__(self, alpha, cloud):
        self.alpha, self.cloud = alpha, cloud


def cell_img(folder, jid):
    r, g, b, a = (plane(folder, jid, c) for c in "rgba")
    return Img(a, (r + g + b) / 3.0), (r, g, b, a)


def reference_column():
    return run.load_column(REFERENCE) if os.path.isdir(REFERENCE) else {}


def reference_numbers():
    """The figures of the full-quality reference run: ratios and dips, and its sun term sums."""
    col = reference_column()
    out = {}
    if "R06-silver-lining" in col:
        c = col["R06-silver-lining"]
        out["silver"] = float(c["edge_near_sun"]) / float(c["edge_far_sun"])
        out["silver_near"], out["silver_far"] = float(c["edge_near_sun"]), float(c["edge_far_sun"])
    if "R08-base-top" in col:
        c = col["R08-base-top"]
        out["base_top"] = float(c["top"]) / float(c["base"])
        out["top"], out["base"] = float(c["top"]), float(c["base"])
    if "R09-powder" in col:
        c = col["R09-powder"]
        out["powder"] = float(c["edge_j"]) / float(c["interior_j"])
        out["edge_j"], out["interior_j"] = float(c["edge_j"]), float(c["interior_j"])
    if "R07-light-steps" in col:
        out["sun_term_ref64"] = float(col["R07-light-steps"]["sun_term_ref64"])
    return out


# --------------------------------------------------------------------------------------------------------------------------------
# The fit: the look constants against the reference (series T03-fit)
# --------------------------------------------------------------------------------------------------------------------------------
FIT_FIELDS = ("octaveA", "octaveB", "octaveC", "msBrightness", "powder", "powderLength", "ambientOcclusion", "ambientGain", "diffusion", "diffusionTau", "msIsotropy")
FIT_CASES = (("near", 0.0, 8.0), ("far", 75.0, 8.0), ("side", 90.0, 50.0), ("back", 180.0, 35.0), ("ns", 0.0, 20.0))
# The octaves may not scatter more than the perfect diffuser's radiance E / pi, which is 4 times the isotropic source E / 4 pi that their weights are
# measured in (CloudLightMath.EnergyBound; FX-T03: the earlier fit had no bound, reached 6.6 and made a cloud towards the sun 2.4 times too bright).
ENERGY_CAP = 4.0
# The image-level views of the fit: the reference images (parity-ref-images) of these cases are compared region by region.
FIT_IMAGE_VIEWS = (("ns", "near-sun"), ("side", "side"), ("back", "behind"))
FIT_W = 64


def fit_jobs(sets, w=FIT_W):
    jobs = []
    for k, p in enumerate(sets):
        for name, az, el in FIT_CASES:
            jobs.append(cell_job("F%03d-%s" % (k, name), az, el, w=w, **p))
    return jobs


def octave_energy(p):
    """The sum of the octave weights of a parameter set (CloudLightMath.OctaveEnergy)."""
    return 1.0 + p["msBrightness"] * sum(p["octaveB"] ** n for n in range(1, int(MODEL["octaves"])))


def fit_sets(n, seed, ranges):
    rng = np.random.default_rng(seed)
    sets = []
    while len(sets) < n:
        p = {f: float(rng.uniform(*ranges[f])) for f in FIT_FIELDS}
        if octave_energy(p) <= ENERGY_CAP:
            sets.append(p)
    return sets


FIT_RANGES = dict(octaveA=(0.2, 0.98), octaveB=(0.2, 0.9), octaveC=(0.1, 0.9), msBrightness=(0.2, 3.0), powder=(0.0, 1.0), powderLength=(50.0, 600.0),
                  ambientOcclusion=(0.0, 2.5), ambientGain=(0.2, 3.0), diffusion=(0.0, 4.0), diffusionTau=(2.0, 40.0), msIsotropy=(0.0, 1.0))


def _ref_image(view):
    z = np.load(os.path.join(REPO, "build", "parity-ref-images", view + ".npz"))
    return z["alpha"], z["cloud"]


def region_means(alpha, lum, ref_alpha):
    """Mean radiance over the cloud's core, its top, middle and bottom thirds and its rim (alpha between 0.15 and 0.8 of the reference)."""
    core = (ref_alpha > 0.9) & (alpha > 0.9)
    ys = np.where(ref_alpha.max(axis=1) > 0.5)[0]
    y0, y1 = ys.min(), ys.max()
    out = {"core": float(lum[core].mean())}
    for name, (f0, f1) in (("top", (0.0, 0.33)), ("mid", (0.33, 0.66)), ("bot", (0.66, 1.0))):
        m = np.zeros_like(core)
        rows = slice(y0 + int(f0 * (y1 - y0)), y0 + int(f1 * (y1 - y0)) + 1)
        m[rows] = core[rows]
        out[name] = float(lum[m].mean()) if m.any() else out["core"]
    rim = (ref_alpha > 0.15) & (ref_alpha < 0.8) & (alpha > 0.1)
    out["rim"] = float(lum[rim].mean()) if rim.any() else out["core"]
    return out


def fit_scores(folder, sets, ref):
    """Per set: the figures and the normalised violation (1 = on the edge of the tolerance; below 1 = inside) of the checks. FX-T03: the score
    also holds the image-level comparison with the reference images (core, thirds and rim of the near-sun, side and behind views), so that
    a set cannot be right at the four edge figures and wrong in the body (the earlier fit: 2.4 times too bright towards the sun)."""
    out = []
    refs = {case: _ref_image(view) for case, view in FIT_IMAGE_VIEWS}
    for k, p in enumerate(sets):
        imgs = {name: cell_img(folder, "F%03d-%s" % (k, name))[0] for name, _, _ in FIT_CASES}
        near, far = run.edge_source(imgs["near"])[0], run.edge_source(imgs["far"])[0]
        silver = near / max(far, 1e-12)
        base, top = run.base_top(imgs["side"])
        ratio = top / max(base, 1e-12)
        e, i = run.powder_pair(imgs["back"])
        dip = e / max(i, 1e-12)
        v_silver = max(abs(silver - ref["silver"]) / (0.15 * ref["silver"]), (2.0 - silver) / 2.0)
        v_base = abs(ratio - ref["base_top"]) / (0.20 * ref["base_top"])
        v_powder = abs(dip - ref["powder"]) / (0.25 * ref["powder"])
        levels = dict(top=top, base=base, silver_near=near, edge_j=e, interior_j=i)
        v_level = max(abs(levels[k2] / ref[k2] - 1.0) / 0.25 for k2 in levels)
        regions = {}
        v_img = 0.0
        for case, _ in FIT_IMAGE_VIEWS:
            ra, rc = refs[case]
            img = imgs[case]
            rm = region_means(ra, rc, ra)
            om = region_means(img.alpha, img.cloud, ra)
            regions[case] = {r: om[r] / max(rm[r], 1e-9) for r in rm}
            v_img = max(v_img, max(abs(v - 1.0) for v in regions[case].values()) / 0.25)
        energy = octave_energy(p)
        v_energy = max(0.0, energy / ENERGY_CAP - 1.0) * 10.0
        out.append(dict(params=p, silver=silver, base_top=ratio, powder_dip=dip, levels=levels, regions=regions, energy=energy,
                        violation=max(v_silver, v_base, v_powder, v_level, v_img, v_energy),
                        parts=(v_silver, v_base, v_powder, v_level, v_img)))
    return out


def write_fit_inputs(sets, folder=FIT_INPUT):
    os.makedirs(folder, exist_ok=True)
    with open(os.path.join(folder, "sets.json"), "w", encoding="utf-8") as f:
        json.dump(sets, f)
    with open(os.path.join(folder, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": fit_jobs(sets)}, f)


def fit_collect(ctx):
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    with open(os.path.join(FIT_INPUT, "sets.json"), encoding="utf-8") as f:
        sets = json.load(f)
    ref = reference_numbers()
    scores = sorted(fit_scores(folder, sets, ref), key=lambda s: s["violation"])
    with open(os.path.join(FIT_INPUT, "scores.json"), "w", encoding="utf-8") as f:
        json.dump(scores, f, indent=1)
    out.append(Metric.info("fit.reference.silver", ref.get("silver", float("nan"))))
    out.append(Metric.info("fit.reference.base_top", ref.get("base_top", float("nan"))))
    out.append(Metric.info("fit.reference.powder", ref.get("powder", float("nan"))))
    for rank, s in enumerate(scores[:5]):
        out.append(Metric.info("fit.best%d.violation" % rank, s["violation"]))
    return out


def fit_stages():
    if not os.path.isfile(os.path.join(FIT_INPUT, "jobs.json")):
        write_fit_inputs(fit_sets(2, 1, FIT_RANGES))
    return [Stage("scene", ["-mode", "scene", "-scene", "light", "-in", FIT_INPUT], size=(1280, 720), proxy=False)]


register_series("T03-fit", fit_stages(), fit_collect, exclusive=False, who="T03")


# --------------------------------------------------------------------------------------------------------------------------------
# The jobs of the judged series
# --------------------------------------------------------------------------------------------------------------------------------
SUN_STEPS_SET = (1, 2, 3, 4, 8)
UNIFORM_STEPS_SET = (1, 2, 3, 4, 8, 16, 64)
CLIMB_FRAMES, CLIMB_FROM, CLIMB_TO = 61, 8000.0, 9000.0
SUN_STEP_FRAMES = 16
# The production sampling of the planet scenes (T01): 48 steps, the step growing with the distance, the interleaved gradient noise, the early out.
PRODUCTION = dict(maxSteps=48, minStep=25.0, stepGrowth=0.0002, jitter=1.0, earlyOut=0.01, costLeap=-1.0, costCut=-1.0, lightStride=-1)
# The side and sunset/night light of the pictures.
SUNSET_COLOUR = [3.0, 1.35, 0.55]
NIGHT_MOON_COLOUR = [0.30, 0.34, 0.50]
# The pictures: id, azimuth, elevation, sun colour, ambient top/bottom colour, extra light (direction, colour).
VIEW_TABLE = (
    ("near-sun", 0.0, 20.0, None, None, None),
    ("side", 90.0, 50.0, None, None, None),
    ("behind", 180.0, 35.0, None, None, None),
    ("low", 90.0, 8.0, None, ([0.20, 0.25, 0.40], [0.12, 0.10, 0.09]), None),
    ("sunset", 90.0, 2.0, None, ([0.08, 0.10, 0.18], [0.10, 0.06, 0.045]), None),
    ("storm", 90.0, 40.0, None, None, None),
    ("night", 90.0, -12.0, [0.0, 0.0, 0.0], ([0.02, 0.025, 0.04], [0.01, 0.012, 0.02]), (flat_sun(0.0, 45.0), NIGHT_MOON_COLOUR)),
)
# Views that look up at a layer (no cell): a thick deck seen from below and a thin cirrus, id -> (base, top, vertical optical depth, sun elevation).
UP_VIEWS = {"deck": (2000.0, 2600.0, 25.0, 25.0), "cirrus": (8000.0, 8400.0, 0.4, 25.0)}


def up_job(jid, vid, aid=0, w=64, harness=False):
    """A view up at a layer. harness: the harness's own grey environment and the white sun, the setting the reference renderer rendered
    (build/parity-ref-images/updecks.json, 16 x 12 px) for the 'deck' and 'cirrus' checks."""
    base, top, tau, el = UP_VIEWS[vid]
    cam = pr.Camera([0.0, 0.0, R + 2.0], 0.0, 35.0, w, int(w * 0.75), 60.0)
    if harness:
        return light_job(jid, cam, [L.layer(base, top, tau / (top - base))], cam.width, cam.height, flat_sun(200.0, el), aid=aid)
    over = dict(SUN_AIR) if vid in AIR_VIEWS else {}
    return light_job(jid, cam, [L.layer(base, top, tau / (top - base))], cam.width, cam.height, flat_sun(200.0, el), aid=aid,
                     amb_top=[0.25, 0.32, 0.5], amb_bottom=[0.10, 0.10, 0.10], **over)


def up_reference(vid):
    path = os.path.join(REPO, "build", "parity-ref-images", "updecks.json")
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as f:
        d = json.load(f)[vid]
    return np.array(d["alpha"]), np.array(d["cloud"])


def up_ratio(folder, vid):
    """Our mean cloud radiance over the reference's, on the 16 x 12 view of the harness setting; (alpha ours, alpha reference, ratio)."""
    ref = up_reference(vid)
    if ref is None or not os.path.isfile(os.path.join(folder, "DK-%s-r.npy" % vid)):
        return None
    r, g, b, a = (plane(folder, "DK-%s" % vid, c) for c in "rgba")
    return float(a.mean()), float(ref[0].mean()), float(((r + g + b) / 3.0).mean() / ref[1].mean())
AB_VARIANTS = (
    ("full", {}),
    ("no-powder", dict(powder=0.0)),
    ("powder", dict(powder=1.0)),
    ("one-octave", dict(octaves=1)),
    ("steps1", dict(sunSteps=1)),
    ("steps2", dict(sunSteps=2)),
    ("steps8", dict(sunSteps=8)),
    ("steps64", dict(sunSteps=64, tapRatio=1.0)),
    ("cone", dict(coneSpread=0.15)),
    ("no-lobes", dict(forwardWeight=0.0, backG=0.0)),
)


# The views whose sun is seen through the air (FX-T03: the shader's SunAir with the compiled default extinction); the others keep the white sun
# of the parity reference. Both the cell views and the extra views below use it for the low sun.
SUN_AIR = dict(sunAirTau=[0.0764, 0.138, 0.295], sunAirHeight=8000.0)
AIR_VIEWS = ("low", "sunset", "deck", "cirrus", "storm")


def view_job(jid, spec, variant=None, aid=0, w=96):
    _, az, el, sun_col, amb, extra = spec
    over = dict(SUN_AIR) if spec[0] in AIR_VIEWS else {}
    over.update(variant or {})
    if spec[0] == "storm":
        over["cellSigma"] = 0.05
    kw = {}
    if sun_col is not None:
        kw["sun_colour"] = sun_col
    if amb is not None:
        kw["amb_top"], kw["amb_bottom"] = amb
    if extra is not None:
        over.update(moonDir0=vec(extra[0]), moonCol0=vec(extra[1]))
    return cell_job(jid, az, el, w=w, aid=aid, **dict(kw, **over))


def climb_camera(h):
    return pr.Camera([0.0, 0.0, R + h], 0.0, -45.0, 24, 24, 50.0)


def build_jobs():
    jobs = [phase_job("P-phase")]
    jobs.append(cell_job("S-near", 0.0, 8.0, w=96))
    jobs.append(cell_job("S-far", 75.0, 8.0, w=96))
    jobs.append(slab_job("B-h", 1.5, 1000.0))
    jobs.append(slab_job("B-2h", 3.0, 2000.0))
    for n in SUN_STEPS_SET:
        jobs.append(cell_job("LS-g%d" % n, 90.0, 50.0, w=96, aid=4, octaves=1, sunSteps=n, tapRatio=2.0, powder=0.0, diffusion=0.0))
    for n in UNIFORM_STEPS_SET:
        jobs.append(cell_job("LS-u%d" % n, 90.0, 50.0, w=96, aid=4, octaves=1, sunSteps=n, tapRatio=1.0, powder=0.0, diffusion=0.0))
    jobs.append(cell_job("LSR", 90.0, 50.0, w=64, aid=4, octaves=1, sunSteps=64, tapRatio=1.0, powder=0.0, diffusion=0.0))
    jobs.append(cell_job("BT", 90.0, 50.0, w=96))
    jobs.append(cell_job("PW", 180.0, 35.0, w=96))
    jobs.append(cell_job("PW-off", 180.0, 35.0, w=96, powder=0.0))
    jobs.append(cell_job("PW-one", 180.0, 35.0, w=96, octaves=1))
    jobs.append(cell_job("AF", 90.0, 50.0, w=96, aid=4))
    jobs.append(cell_job("AF-black", 90.0, 50.0, w=96, aid=4, amb_top=[0.0] * 3, amb_bottom=[0.0] * 3, ambientGain=0.0, ambientFloor=0.0))
    # The analytic slab: view straight up, isotropic phase, single scattering, no ambient (aid 4: the direct part alone).
    cam = pr.Camera([0.0, 0.0, R + 2.0], 0.0, 90.0, 3, 3, 10.0)
    for tag, steps, ratio in [("g%d" % n, n, 2.0) for n in SUN_STEPS_SET] + [("u%d" % n, n, 1.0) for n in UNIFORM_STEPS_SET]:
        jobs.append(light_job("SL-" + tag, cam, [L.layer(2000.0, 3000.0, 0.002)], 3, 3, flat_sun(200.0, 45.0), aid=4, octaves=1, forwardG=0.0,
                              backG=0.0, forwardWeight=1.0, albedo=1.0, sunSteps=steps, tapRatio=ratio, powder=0.0, diffusion=0.0))
    # The sun's step response and the climb: each frame its own job (the pass keeps no state between frames).
    for i in range(SUN_STEP_FRAMES):
        jobs.append(cell_job("SS-%02d" % i, 0.0 if i < 4 else 5.0, 8.0, w=48, aid=4))
    for i in range(SUN_STEP_FRAMES):
        amb = [AMBIENT_TOP * (1.0 if i < 4 else 1.5)] * 3
        jobs.append(cell_job("AS-%02d" % i, 90.0, 50.0, w=48, aid=4, amb_top=amb, amb_bottom=[AMBIENT_BOTTOM * (1.0 if i < 4 else 1.5)] * 3))
    sun = flat_sun(200.0, 30.0)
    for i in range(CLIMB_FRAMES):
        h = CLIMB_FROM + (CLIMB_TO - CLIMB_FROM) * i / (CLIMB_FRAMES - 1)
        cam = climb_camera(h)
        jobs.append(light_job("CL-%02d" % i, cam, [L.layer(2200.0, 2500.0, 0.02)], 24, 24, sun, layer_kw=PRODUCTION))
        jobs.append(light_job("CLS-%02d" % i, cam, [L.layer(2200.0, 2500.0, 0.02)], 24, 24, sun, layer_kw=dict(PRODUCTION, jitter=0.0)))
    jobs.append(dict(cell_job("DF", 90.0, 50.0, w=96), useDefaults=1))
    # Pictures: the view table, the A/B variants of the side view, each natural (aid 0) and with the aid split (aid 4).
    for spec in VIEW_TABLE:
        jobs.append(view_job("V-%s" % spec[0], spec))
        jobs.append(view_job("VA-%s" % spec[0], spec, aid=4))
    for vid in UP_VIEWS:
        jobs.append(up_job("V-%s" % vid, vid))
        jobs.append(up_job("VA-%s" % vid, vid, aid=4))
        jobs.append(up_job("DK-%s" % vid, vid, w=16, harness=True))
    for name, over in AB_VARIANTS:
        jobs.append(view_job("AB-%s" % name, VIEW_TABLE[1], over))
        jobs.append(view_job("ABA-%s" % name, VIEW_TABLE[1], over, aid=4))
    jobs.extend(cache_jobs())
    return jobs


# The light cache A/B: the cell's sun term (single scattering, aid 4) with the per-sample march at a few taps, the cache filled with N taps, and
# the 64-tap uniform march as the truth. The box is 2400 m on a side around the cell's centre: 37.5 m voxels in the plan, 75 m in height.
CACHE_FILL_SET = (4, 8, 16, 32)
CACHE_BOX = [2400.0, 2400.0, 2400.0]


def cache_jobs():
    jobs = []
    lo = [CELL_CENTRE[0] - CACHE_BOX[0] / 2, CELL_CENTRE[1] - CACHE_BOX[1] / 2, CELL_CENTRE[2] - CACHE_BOX[2] / 2]
    for n in CACHE_FILL_SET:
        jobs.append(cell_job("CA-%d" % n, 90.0, 50.0, w=96, aid=4, octaves=1, sunSteps=3, cache=1, cacheMin=lo, cacheSize=CACHE_BOX, cacheFillSteps=n,
                             tapRatio=1.0, powder=0.0, diffusion=0.0))
    return jobs


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


# --------------------------------------------------------------------------------------------------------------------------------
# The captures
# --------------------------------------------------------------------------------------------------------------------------------
def luma(img4):
    r, g, b, a = img4
    return (r + g + b) / 3.0


def cap_phase_ours(folder):
    r = plane(folder, "P-phase", "r")[0]
    g = plane(folder, "P-phase", "g")[0]
    mu = np.linspace(-1.0, 1.0, len(r))
    return {"mu": mu, "p": r}, {"mu": mu, "p": g}


def cap_silver_ours(folder):
    near, _ = cell_img(folder, "S-near")
    far, _ = cell_img(folder, "S-far")
    return {"edge_near_sun": run.edge_source(near)[0], "edge_far_sun": run.edge_source(far)[0]}


def cap_beer_ours(folder):
    return {"T_h": 1.0 - float(plane(folder, "B-h", "a")[1, 1]), "T_2h": 1.0 - float(plane(folder, "B-2h", "a")[1, 1])}


def sun_term(folder, jid, mask):
    """The sum of the direct light over the mask (aid 4: r holds the direct part, premultiplied)."""
    return float(plane(folder, jid, "r")[mask].sum())


def cap_light_steps_ours(folder, tag="g", n=4):
    a = plane(folder, "LS-u64", "a")
    mask = a > 0.5
    return {"sun_term": sun_term(folder, "LS-%s%d" % (tag, n), mask), "sun_term_ref64": sun_term(folder, "LS-u64", mask), "steps": n}


def cap_base_top_ours(folder, jid="BT"):
    img, _ = cell_img(folder, jid)
    base, top = run.base_top(img)
    return {"base": base, "top": top}


def cap_powder_ours(folder, jid="PW"):
    img, _ = cell_img(folder, jid)
    e, i = run.powder_pair(img)
    return {"edge_j": e, "interior_j": i}


def cap_ambient_ours(folder, jid="AF-black"):
    r, g, b, a = (plane(folder, jid, c) for c in "rgba")
    m = a > 0.97
    j = b[m] / a[m]
    return {"ambient": float(j.min()), "sun_share": SUN_E / (4.0 * math.pi)}


def sequence(folder, prefix, count, start=0, channel=None):
    out = []
    for i in range(start, count):
        jid = "%s-%02d" % (prefix, i)
        if channel:
            out.append(float(plane(folder, jid, channel).mean()))
        else:
            out.append(float(luma(tuple(plane(folder, jid, c) for c in "rgba")).mean()))
    return np.array(out)


def slab_expected():
    """The direct light of the analytic slab: S0/4pi * sigma * integral over the depth z of exp(-sigma z) * exp(-sigma (H - z) / sin e)."""
    s0, sigma, h, s = SUN_E, 0.002, 1000.0, math.sin(math.radians(45.0))
    k = sigma * (1.0 - 1.0 / s)
    return s0 / (4.0 * math.pi) * sigma * math.exp(-sigma * h / s) * (math.exp(-k * h) - 1.0) / (-k)


def cell_summary(folder, jid):
    img, planes = cell_img(folder, jid)
    return img, planes


def judge(spec, cid, data, out, name=None, extra_info=None):
    s = spec[cid]
    m = pc.run_check(s["function"], data, s)
    run.write_capture(os.path.join(OURS, cid), {k: v for k, v in data.items() if not k.startswith("ref_")})
    key = name or cid
    out.append(Metric.given(key, m.value if m.value is not None else float("nan"), run.threshold_text(s), "true" if m.ok else "false"))
    if m.reference is not None:
        out.append(Metric.info(key + ".reference", m.reference))
    return m


def collect(ctx):
    spec = pc.load_spec()
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    if not os.path.isfile(os.path.join(folder, "P-phase-r.npy")):
        out.append(Metric.flag("written", False))
        return out
    ref = reference_numbers()
    needed = ("silver", "base_top", "powder", "sun_term_ref64")
    missing = [k for k in needed if k not in ref]
    # No reference, no verdict: the checks that equal the reference need its numbers (run parity_run.py reference ... --quality full).
    out.append(Metric.flag("reference_available", not missing))
    if missing:
        return out
    os.makedirs(OURS, exist_ok=True)
    phase0, phase1 = cap_phase_ours(folder)
    judge(spec, "R06-phase-integral", phase0, out)
    m = pc.run_check("phase_integral", phase1, spec["R06-phase-integral"])
    out.append(Metric.info("R06-phase-integral.octave1_error", m.value))
    d = cap_silver_ours(folder)
    d["ref_ratio"] = ref["silver"]
    judge(spec, "R06-silver-lining", d, out)
    judge(spec, "R07-beer-lambert", cap_beer_ours(folder), out)
    for n in SUN_STEPS_SET:
        m = pc.run_check("light_step_error", cap_light_steps_ours(folder, "g", n), spec["R07-light-steps"])
        out.append(Metric.info("R07-light-steps.geometric%d" % n, m.value))
    for n in UNIFORM_STEPS_SET[:-1]:
        m = pc.run_check("light_step_error", cap_light_steps_ours(folder, "u", n), spec["R07-light-steps"])
        out.append(Metric.info("R07-light-steps.uniform%d" % n, m.value))
    judge(spec, "R07-light-steps", cap_light_steps_ours(folder, "g", MODEL["sunSteps"]), out)
    mask = plane(folder, "LSR", "a") > 0.5
    out.append(Metric.info("R07-light-steps.ours64_vs_reference64", pc.rel(sun_term(folder, "LSR", mask), ref["sun_term_ref64"])))
    for k in ("silver_near", "silver_far", "top", "base", "edge_j", "interior_j"):
        out.append(Metric.info("reference." + k, ref[k]))
    d = cap_base_top_ours(folder)
    d["ref_ratio"] = ref["base_top"]
    judge(spec, "R08-base-top", d, out)
    judge(spec, "R08-ambient-floor", cap_ambient_ours(folder), out)
    m = pc.run_check("ambient_floor", cap_ambient_ours(folder, "AF"), spec["R08-ambient-floor"])
    out.append(Metric.info("R08-ambient-floor.normal_sky", m.value))
    d = cap_powder_ours(folder)
    d["ref_dip"] = ref["powder"]
    judge(spec, "R09-powder", d, out)
    for tag in ("PW-off", "PW-one"):
        c = cap_powder_ours(folder, tag)
        out.append(Metric.info("R09-powder.%s" % tag, c["edge_j"] / c["interior_j"]))
    # The light response of the pass itself: it keeps no state between frames, so the new light is there at the first frame after the step.
    # The step must be a real change (the gates below): without one the settle count would be zero for any pass.
    seq = sequence(folder, "SS", SUN_STEP_FRAMES, start=4, channel="r")
    judge(spec, "R10-cache-direct", {"luminance": seq}, out)
    step = abs(sequence(folder, "SS", 4, channel="r")[-1] - seq[-1]) / seq[-1]
    out.append(Metric.at_least("R10-cache-direct.step_size", step, 0.02).based("gate"))
    amb = sequence(folder, "AS", SUN_STEP_FRAMES, start=4, channel="b")
    judge(spec, "R10-cache-ambient", {"luminance": amb}, out)
    out.append(Metric.at_least("R10-cache-ambient.step_size", abs(sequence(folder, "AS", 4, channel="b")[-1] - amb[-1]) / amb[-1], 0.05).based("gate"))
    # The climb of 1 km at the production sampling (the interleaved gradient noise with the frame fixed), and with the smooth midpoint rule.
    judge(spec, "R10-cache-jump", {"luminance": sequence(folder, "CL", CLIMB_FRAMES)}, out)
    m = pc.run_check("light_cache_jump", {"luminance": sequence(folder, "CLS", CLIMB_FRAMES)}, spec["R10-cache-jump"])
    out.append(Metric.info("R10-cache-jump.midpoint_rule", m.value))
    expected = slab_expected()
    for tag in ["g%d" % n for n in SUN_STEPS_SET] + ["u%d" % n for n in UNIFORM_STEPS_SET]:
        got = float(plane(folder, "SL-" + tag, "r")[1, 1])
        err = abs(got - expected) / expected
        configured = tag in ("g3", "g4")
        out.append((Metric.at_most("SL.%s_error" % tag, err, 0.10).based("gate") if configured else Metric.info("SL.%s_error" % tag, err)))
    out.append(Metric.info("SL.expected", expected))
    # The compiled defaults against the explicit numbers of the same cell.
    a, b = plane(folder, "DF", "g"), plane(folder, "BT", "g")
    out.append(Metric.at_most("defaults_equal_model", float(np.abs(a - b).max()), 1e-4).based("new"))
    out.extend(cache_metrics(folder))
    # The layers seen from below against the reference renderer (FX-T03): a thick deck, a thin cirrus; within 25 % of the reference's radiance.
    for vid in UP_VIEWS:
        u = up_ratio(folder, vid)
        if u is not None:
            out.append(Metric.info("deck.%s.alpha_ours" % vid, u[0]))
            out.append(Metric.info("deck.%s.alpha_reference" % vid, u[1]))
            out.append(Metric.at_most("deck.%s.radiance_error" % vid, abs(u[2] - 1.0), 0.25).based("gate"))
            out.append(Metric.info("deck.%s.radiance_ratio" % vid, u[2]))
    out.extend(pictures(folder))
    for m in out:
        if m.value is not None and not isinstance(m.value, str):
            m.value = float(m.value)   # a numpy float makes the verdict a numpy bool, which the series file cannot hold
    return out


def cache_metrics(folder):
    out = []
    truth = plane(folder, "LS-u64", "r")
    mask = plane(folder, "LS-u64", "a") > 0.5
    t = truth[mask]

    def err(jid):
        # The sum's relative error (the harness's measure) and the pixel RMS error over the cloud pixels, as a share of the mean sun term.
        v = plane(folder, jid, "r")[mask]
        return abs(v.sum() - t.sum()) / t.sum(), float(np.sqrt(np.mean((v - t) ** 2)) / t.mean())

    for n in SUN_STEPS_SET:
        s, p = err("LS-g%d" % n)
        out.append(Metric.info("cache.A.geometric%d.sum_error" % n, s))
        out.append(Metric.info("cache.A.geometric%d.pixel_rms" % n, p))
    for n in CACHE_FILL_SET:
        if os.path.isfile(os.path.join(folder, "CA-%d-r.npy" % n)):
            s, p = err("CA-%d" % n)
            out.append(Metric.info("cache.B.fill%d.sum_error" % n, s))
            out.append(Metric.info("cache.B.fill%d.pixel_rms" % n, p))
    return out


# --------------------------------------------------------------------------------------------------------------------------------
# The pictures: natural and aid views, A/B grids
# --------------------------------------------------------------------------------------------------------------------------------
def tone(c, k=1.3):
    return np.clip(1.0 - np.exp(-np.maximum(c, 0.0) * k), 0.0, 1.0) ** (1.0 / 2.2)


def backdrop(h, w, sky=0.55, ground=0.12):
    """The harness's sky above the horizon row, its ground radiance below (the cell's camera looks along the horizon)."""
    b = np.full((h, w), sky)
    b[h // 2:, :] = ground
    return b


SKY_COLOUR = {"sunset": (0.45, 0.30, 0.28), "night": (0.010, 0.012, 0.025)}
DAY_SKY = (0.30, 0.42, 0.62)


def sky_of(view_id):
    return SKY_COLOUR.get(view_id, DAY_SKY)


def natural_image(folder, jid, sky=None, all_sky=False):
    """The march's radiance over a backdrop: the sky above the horizon row, a darker ground below (the view's own sky colour); all_sky: the
    view looks up and sees sky everywhere."""
    r, g, b, a = (plane(folder, jid, c) for c in "rgba")
    sky = DAY_SKY if sky is None else sky
    h, w = a.shape
    chans = []
    for ch, s in zip((r, g, b), sky):
        back = backdrop(h, w, s, 0.22 * s)
        if all_sky:
            back = np.full((h, w), s)
        chans.append(tone(ch + (1 - a) * back))
    return np.stack(chans, axis=-1)


def heat(x):
    x = np.clip(x, 0.0, 1.0)
    stops = [(0.0, 0.0, 0.0), (0.4, 0.1, 0.5), (0.9, 0.4, 0.1), (1.0, 0.95, 0.5)]
    xs = np.linspace(0.0, 1.0, len(stops))
    out = np.zeros(x.shape + (3,))
    for c in range(3):
        out[..., c] = np.interp(x, xs, [s[c] for s in stops])
    return out


def split_image(folder, jid):
    """The aid views: the direct light (red) against the ambient (blue), and the step count's heat map."""
    r, g, b, a = (plane(folder, jid, c) for c in "rgba")
    scale = max(float(np.percentile(r + b, 99)), 1e-6)
    sp = np.stack([tone(r / scale * 1.5), np.zeros_like(r), tone(b / scale * 1.5)], axis=-1)
    steps = heat(g / max(float(g.max()), 1e-6)) * (a[..., None] > 0.01)
    return sp, steps


def upscale(img, k=4):
    from PIL import Image
    im = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))
    return im.resize((im.width * k, im.height * k), Image.NEAREST)


def sheet(panels, labels, path, gap=4):
    from PIL import Image, ImageDraw
    ims = [upscale(p) for p in panels]
    w = sum(i.width for i in ims) + gap * (len(ims) - 1)
    h = max(i.height for i in ims) + 14
    canvas = Image.new("RGB", (w, h), (24, 24, 24))
    d = ImageDraw.Draw(canvas)
    x = 0
    for im, lab in zip(ims, labels):
        canvas.paste(im, (x, 14))
        d.text((x + 3, 1), lab, fill=(230, 230, 230))
        x += im.width + gap
    os.makedirs(os.path.dirname(path), exist_ok=True)
    canvas.save(path)


def reference_image(view_id):
    """The reference renderer's cloud for a view (saved by light_reference_images), composited over the same backdrop; None if missing."""
    path = os.path.join(REPO, "build", "parity-ref-images", view_id + ".npz")
    if not os.path.isfile(path):
        return None
    z = np.load(path)
    h, w = z["alpha"].shape
    sky = sky_of(view_id)
    chans = [tone(z["cloud"] + (1 - z["alpha"]) * backdrop(h, w, s, 0.22 * s)) for s in sky]
    return np.stack(chans, axis=-1)


def pictures(folder):
    out = []
    for vid in [s[0] for s in VIEW_TABLE] + list(UP_VIEWS):
        if not os.path.isfile(os.path.join(folder, "V-%s-r.npy" % vid)):
            continue
        nat = natural_image(folder, "V-%s" % vid, sky_of(vid), all_sky=vid in UP_VIEWS)
        sp, steps = split_image(folder, "VA-%s" % vid)
        panels, labels = [nat, sp, steps], ["ours natural", "direct (red) | ambient (blue)", "light taps per pixel"]
        refimg = reference_image(vid)
        if refimg is not None:
            panels.append(refimg)
            labels.append("T11 reference")
        sheet(panels, labels, os.path.join(PICTURES, "view-%s.png" % vid))
        out.append(Metric.info("picture.view-%s.mean_alpha" % vid, float(plane(folder, "V-%s" % vid, "a").mean())))
    panels, labels = [], []
    for name, _ in AB_VARIANTS:
        if os.path.isfile(os.path.join(folder, "AB-%s-r.npy" % name)):
            panels.append(natural_image(folder, "AB-%s" % name))
            labels.append(name)
    if panels:
        cols = 5
        rows = [panels[i:i + cols] for i in range(0, len(panels), cols)]
        from PIL import Image, ImageDraw
        ims = [[upscale(p, 3) for p in row] for row in rows]
        w = max(sum(i.width for i in row) + 4 * (len(row) - 1) for row in ims)
        h = sum(max(i.height for i in row) + 14 for row in ims)
        canvas = Image.new("RGB", (w, h), (24, 24, 24))
        d = ImageDraw.Draw(canvas)
        y = 0
        k = 0
        for row in ims:
            x = 0
            for im in row:
                canvas.paste(im, (x, y + 14))
                d.text((x + 3, y + 1), labels[k], fill=(230, 230, 230))
                x += im.width + 4
                k += 1
            y += max(i.height for i in row) + 14
        os.makedirs(PICTURES, exist_ok=True)
        canvas.save(os.path.join(PICTURES, "ab-grid.png"))
        # The difference of each variant to the full model, on the cloud pixels (mean absolute difference of the tone-mapped images).
        base = natural_image(folder, "AB-full")
        a = plane(folder, "AB-full", "a") > 0.05
        for name, _ in AB_VARIANTS:
            if name == "full" or not os.path.isfile(os.path.join(folder, "AB-%s-r.npy" % name)):
                continue
            diff = np.abs(natural_image(folder, "AB-%s" % name) - base).mean(axis=-1)
            out.append(Metric.info("picture.ab.%s.mean_diff" % name, float(diff[a].mean())))
    return out


def stages():
    jobs = os.path.join(INPUT, "jobs.json")
    if not os.path.isfile(jobs) or os.path.getmtime(jobs) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "light", "-in", INPUT], size=(1280, 720), proxy=False)]


register_series("T03-light", stages(), collect, exclusive=False, who="T03")


# --------------------------------------------------------------------------------------------------------------------------------
# The cost series (T03-cost): the pass at 1707 x 960 with the light variants, and the cache fill
# --------------------------------------------------------------------------------------------------------------------------------
COST_INPUT = os.path.join(BUILD, "T03-cost-in")
COST_VIEWS = ("V-ground", "V-inside", "V-above", "V-stack-below")
COST_VARIANTS = (
    ("default", {}),
    ("ao-taps", dict(aoTaps=1)),
    ("cone", dict(coneSpread=0.15)),
    ("sun-air", dict(SUN_AIR)),
    ("steps3", dict(sunSteps=3)),
    ("light-minimal", dict(octaves=1, ambientOcclusion=0.0, sunSteps=1, diffusion=0.0)),
    ("steps2", dict(sunSteps=2)),
    ("octaves1", dict(octaves=1)),
    ("octaves2", dict(octaves=2)),
    ("no-ao", dict(ambientOcclusion=0.0)),
    ("taps1", dict(sunSteps=1)),
    ("cache", dict(cache=1)),
    ("cache-octaves1-no-ao", dict(cache=1, octaves=1, ambientOcclusion=0.0)),
)


def cost_jobs():
    L.write_inputs()
    os.makedirs(COST_INPUT, exist_ok=True)
    for name in ("view-coverage.r8", "view-type.r8"):
        shutil.copy(os.path.join(L.INPUT, name), os.path.join(COST_INPUT, name))
    jobs = []
    for lj in L.picture_jobs():
        if lj["id"] not in COST_VIEWS:
            continue
        lj["sunColour"] = [3.0, 2.9, 2.7]
        lj["ambientTop"] = [0.35, 0.42, 0.55]
        lj["ambientBottom"] = [0.14, 0.15, 0.16]
        for vname, over in COST_VARIANTS:
            j = dict(id="%s|%s" % (lj["id"], vname), mode="lum", aid=0, useDefaults=0, layerJob=lj)
            j.update(MODEL)
            j.update(over)
            if over.get("cache"):
                p = lj["position"]
                f = lj["forward"]
                c = [p[k] + 8000.0 * f[k] for k in range(3)]
                j.update(cacheMin=[c[k] - 16000.0 for k in range(3)], cacheSize=[32000.0] * 3, cacheFillSteps=12)
            jobs.append(j)
    with open(os.path.join(COST_INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def cost_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    return out


def cost_stages():
    if not os.path.isfile(os.path.join(COST_INPUT, "jobs.json")):
        cost_jobs()
    return [Stage("scene", ["-mode", "scene", "-scene", "light-cost", "-in", COST_INPUT], size=(1707, 960), proxy=True, timeout=1200)]


register_series("T03-cost", cost_stages(), cost_collect, exclusive=True, who="T03")


# --------------------------------------------------------------------------------------------------------------------------------
# Commands
# --------------------------------------------------------------------------------------------------------------------------------
def reference_images():
    """The reference renderer's cloud (alpha and the cloud's own radiance) at the full quality for the view table's three day views."""
    q = run.QUALITY["full"]
    out = os.path.join(REPO, "build", "parity-ref-images")
    os.makedirs(out, exist_ok=True)
    for spec in VIEW_TABLE[:3]:
        res = run.cell_scene(q, spec[1], spec[2], w=64)
        np.savez(os.path.join(out, spec[0] + ".npz"), alpha=res.alpha, cloud=res.cloud)
        print("reference image", spec[0], flush=True)


def fit_round(n):
    """Writes the sweep of round n: round 1 over the full ranges, rounds 2 and 3 around the best sets of the round before."""
    scores_path = os.path.join(FIT_INPUT, "scores.json")
    if n <= 1 or not os.path.isfile(scores_path):
        sets = fit_sets(200, 12, FIT_RANGES)
    else:
        with open(scores_path, encoding="utf-8") as f:
            best = json.load(f)[:5]
        width = 0.25 if n == 2 else 0.1
        rng = np.random.default_rng(100 + n)
        sets = []
        for k in range(120):
            base = best[k % len(best)]["params"]
            for _try in range(50):
                cand = {f: float(np.clip(base[f] + rng.uniform(-1, 1) * width * (FIT_RANGES[f][1] - FIT_RANGES[f][0]), *FIT_RANGES[f]))
                        for f in FIT_FIELDS}
                if octave_energy(cand) <= ENERGY_CAP:
                    break
            sets.append(cand)
    write_fit_inputs(sets)
    print("round %d: %d sets" % (n, len(sets)))


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "cost-jobs":
        print(len(cost_jobs()), "jobs")
    elif cmd == "jobs":
        print(len(write_inputs()), "jobs")
    elif cmd == "ref-images":
        reference_images()
    elif cmd == "fit-round":
        fit_round(int(sys.argv[2]))
    else:
        print("usage: light.py jobs | ref-images | fit-round <n>")
