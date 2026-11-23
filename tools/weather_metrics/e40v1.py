"""E40 V1 "crisp and white in the game": the game's own fair-weather maps drawn in the player at the game's views, and what the outlines and the lit sides of those clouds look like (series e40-v1).

    python tools/weather_metrics/e40v1.py --prepare <series-pictures/K-T2/maps>     copies the fw_off / k_off ground dumps of a K-T2 soft60 start as the scenes fw (fair) and ck (clock)
    python tools/build_weather_player.py --series e40-v1 [--no-build]
    python tools/weather_metrics/e40v1.py --measure-game                            the same numbers for the game's own frames (soft60 start 62, no player)
    python tools/weather_metrics/e40v1.py --selftest

The views are the K-T2 soft60 views: gfair (fair weather, ground view, 264 m, east, 18 degrees up), hfair (the same place, 2 degrees: the horizon), gclock (the clock scene's ground view). The
sun stands where the game's does: the elevation of the probe's row T09s.sun.<scene>.sun_elevation_deg and, as the pictures say (no glare in the east view), in the west (azimuth 270). Variants are
changes of the job's data only (the sun's azimuth, the shape smoothing, the noise scale). The player scene is soft-temporal with `-light product -dump last`: the product's pass as the game runs
it (march target, history, 48 grid points) for a still camera, frame 39, and a converged reference of the same pose. The pictures lay the cloud over the game's own baseline frame (the frame without
the clouds) with the game's display stage, so that a replay frame and the game's frame are comparable pixel by pixel; the numbers measure the same signal in both.
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
from weather_metrics import e40c1 as c1  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import horizon_sky as hs  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40v1-in")
RESULTS = os.path.join(BUILD, "e40v1-results.json")
MAIN = os.environ.get("ATMO_MAINREPO") or os.path.join("C:" + os.sep, "Users", "franz", "IdeaProjects", "ReDefinition")
GAME = os.path.join(MAIN, ".local", "work", "series-pictures", "K-T2")
PICTURES = os.path.join(REPO, ".local", "work", "series-pictures", "E40")
GAME_START = "soft60"
W, H = 1708, 960

# view -> the scene whose maps it draws, the camera's pitch, the sun's elevation (the probe's rows), the pass's sun colour and the output stage's white (the log's "pass sun colour" and "white"),
# the game's baseline picture (no clouds) and its picture with the clouds of this start.
VIEWS = {
    "gfair": dict(scene="fw", pitch=18.0, elev=44.07, sun=2.7863, white=0.7244, off="ground_fair-off_a", on="ground_fair-soft_a"),
    "gfairx": dict(scene="fwx", pitch=18.0, elev=44.07, sun=2.7863, white=0.7244, off="ground_fair-off_a", on="ground_fair-soft_a"),
    "gfairy": dict(scene="fwy", pitch=18.0, elev=44.07, sun=2.7863, white=0.7244, off="ground_fair-off_a", on="ground_fair-soft_a"),
    "gfairr": dict(scene="fwr", pitch=18.0, elev=44.07, sun=2.7863, white=0.7244, off="ground_fair-off_a", on="ground_fair-soft_a"),
    "hfair": dict(scene="fw", pitch=2.0, elev=44.07, sun=2.7863, white=0.7244, off="horizon_fair-off_a", on="horizon_fair-soft_a"),
    "gclock": dict(scene="ck", pitch=18.0, elev=54.23, sun=2.4855, white=0.6672, off="ground-off_a", on="ground-soft_a"),
}
HEIGHT_M = 264.0
HEADING = 90.0
AZIMUTH = 270.0
DUMPS = {"fw": "fw_off-ground-", "ck": "k_off-ground-"}
CUMULUS = "Kerbin-cumulus"


def variants():
    """name -> (azimuth, {layer name: layer changes}, {type changes}). Data only: the job's numbers."""
    return {
        "base": dict(az=AZIMUTH),
        "az90": dict(az=90.0),
        "az0": dict(az=0.0),
        "ss0": dict(az=AZIMUTH, smooth=0.0),
        "ssh": dict(az=AZIMUTH, smooth_scale=0.5),
        "ns125": dict(az=AZIMUTH, noise=125.0),
        "ns500": dict(az=AZIMUTH, noise=500.0),
        # the soft density's width through the job (the medium of the scene does not reach a job that names its own), the erosion in the interior, the aerial perspective
        "sf30": dict(az=AZIMUTH, soft=0.3),
        "sf100": dict(az=AZIMUTH, soft=1.0),
        "dr06": dict(az=AZIMUTH, tuning=[("layers.densityRemap", 0.6)]),
        "dr10": dict(az=AZIMUTH, tuning=[("layers.densityRemap", 1.0)]),
        "ss0dr06": dict(az=AZIMUTH, smooth=0.0, tuning=[("layers.densityRemap", 0.6)]),
        "sshdr06": dict(az=AZIMUTH, smooth_scale=0.5, tuning=[("layers.densityRemap", 0.6)]),
        "haze": dict(az=AZIMUTH, haze=1),
        # the game's far haze on (the product's default), with a lower density to meet the game's thin clouds (coverage threshold up, cumulus extinction down)
        "hzcs10": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.10)]),
        "hzcs20": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.20)]),
        "hzsg05": dict(az=AZIMUTH, haze=1, tuning=[("cloud.layerSigmaScale." + CUMULUS, 0.5)]),
        "hzsg03": dict(az=AZIMUTH, haze=1, tuning=[("cloud.layerSigmaScale." + CUMULUS, 0.3)]),
        "hzssh": dict(az=AZIMUTH, haze=1, smooth_scale=0.5),
        "hzss0": dict(az=AZIMUTH, haze=1, smooth=0.0),
        "hl125": dict(az=AZIMUTH, haze=1, light=1.25),
        "hl135": dict(az=AZIMUTH, haze=1, light=1.35),
        "hl150": dict(az=AZIMUTH, haze=1, light=1.5),
        "hl135ssh": dict(az=AZIMUTH, haze=1, light=1.35, smooth_scale=0.5),
        "cser15": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.10)], erosion=1.5),
        "cser20": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.10)], erosion=2.0),
        "csbase": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.10)]),
        "csssh": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.10)], smooth_scale=0.5),
        "csssq": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.10)], smooth_scale=0.25),
        "csns125": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.10)], noise=125.0),
        "cshl135": dict(az=AZIMUTH, haze=1, tuning=[("cloud.coverageShift", -0.10)], light=1.35),
    }


def tag(view, variant):
    return "v1-%s-%s" % (view, variant)


def case_file(label, view, variant, medium, march):
    return "gs-%s-%s-static-%s-acc20-f39" % (tag(view, variant), medium, march) if march != "ref" else "gs-%s-%s-static-ref-f39" % (tag(view, variant), medium)


def camera(view):
    spec = VIEWS[view]
    la, lo = math.radians(c1.SITE_LAT), math.radians(c1.SITE_LON)
    pos = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)]) * (c1.R + HEIGHT_M)
    return pr.Camera(pos, HEADING, spec["pitch"], W, H, gn.FOV)


def job_layers(scene, variant):
    specs = {s["name"]: s for s in e40o.kerbin_layers()}
    extras = e40ty.cfg_extras()
    v = variants()[variant]
    out = []
    for name in c1.STACK:
        lk, tk = extras[name]
        jl = e40o.job_layer(specs[name], c1.map_name(scene, "cover", name, "cover"), c1.map_name(scene, "cover", name, "type"))
        jl.update(lk)
        c1.old_dump_type_scale(jl, name, lk, c1.map_name(scene, "cover", name, "type"))
        for t in jl["types"]:
            t.update(tk.get(t["name"], {}))
        if name == CUMULUS:
            if "smooth" in v:
                jl["shapeSmoothing"] = v["smooth"]
                for t in jl["types"]:
                    if "shapeSmoothing" in t:
                        t["shapeSmoothing"] = v["smooth"]
            if "smooth_scale" in v:
                jl["shapeSmoothing"] = jl.get("shapeSmoothing", 0.0) * v["smooth_scale"]
                for t in jl["types"]:
                    if "shapeSmoothing" in t:
                        t["shapeSmoothing"] = t["shapeSmoothing"] * v["smooth_scale"]
            if "noise" in v:
                jl["noiseScale"] = v["noise"]
            if "erosion" in v:
                for t in jl["types"]:
                    t["erosion"] = min(t["erosion"] * v["erosion"], 0.95)
            if "light" in v:
                # the direct light of every cumulus type scaled (the types' baseLight / topLight: the direct part of the source at the base and at the head of the cloud)
                for t in jl["types"]:
                    for key in ("baseLight", "topLight"):
                        t[key] = t.get(key, 1.0) * v["light"]
        out.append(jl)
    return out


def build_job(view, variant):
    spec = VIEWS[view]
    cam = camera(view)
    args = gn.prod()
    v = variants()[variant]
    if "tuning" in v:
        args["tuning"] = e40o.tuning(v["tuning"])
    if v.get("haze"):
        args["far"] = haze_block(spec, v["az"])
    sun = [spec["sun"]] * 3
    return lay.job("gn-%s-prod-f0" % tag(view, variant), "lum", cam, job_layers(spec["scene"], variant), W, H, planet=c1.R, mapWidth=c1.MAP_W, mapHeight=c1.MAP_H, frameNumber=0,
                   soft=v.get("soft", 0.5), sun=lay.sun_vector(cam.pos, v["az"], spec["elev"]), sunColour=sun, ambientTop=gn.AMBIENT_TOP, ambientBottom=gn.AMBIENT_BOTTOM, **args)


SC_RAYLEIGH = (0.0153407799e-3, 0.0357074998e-3, 0.0875488967e-3)
SC_MIE, SC_HR, SC_HM = 0.0121406196e-3 / 0.9, 3840.0, 1000.0
LOWEST_LAYER_QUARTER_M = 0.25 * 4690.0         # the lowest layer's lower quarter, where the white point's sun and the far colour's ray are taken


def haze_block(spec, sun_azimuth=AZIMUTH):
    """The far block of the product at this scene's sun: Scatterer's air of Kerbin, the colours a far cloud fades into and a short path in-scatters worked out from the air along the view
    ray (E40 H1 haze, horizon_sky.py), times the white tint. The camera looks along HEADING, the sun is at sun_azimuth."""
    tint = tint_of(spec)
    relative = abs(sun_azimuth - HEADING) % 360.0
    relative = 360.0 - relative if relative > 180.0 else relative
    far, near, _ = hs.far_near(hs.KERBIN, HEIGHT_M, LOWEST_LAYER_QUARTER_M, spec["elev"], relative, spec["white"], gn.AMBIENT_TOP)
    return dict(haze=1, shapeMean=0, policy=0, minSamples=0, rayleigh=list(SC_RAYLEIGH), mie=SC_MIE, rayleighHeight=SC_HR, mieHeight=SC_HM, hazeStrength=1.0,
                inscatter=[float(x) for x in np.array(far) * tint], inscatterNear=[float(x) for x in np.array(near) * tint])


def all_jobs():
    return [build_job(v, n) for v in VIEWS for n in variants()]


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = all_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    specs = [dict(id=j["id"], variation=1, physics=1, topUpdraft=1, ragged=1, humidityMap="%s-humidity.r8" % scene_of(j["id"]), updraftMap="", seed=664216370 & 0xFFFFFF) for j in jobs]
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": specs}, f)
    return jobs


def scene_of(jid):
    for view, spec in VIEWS.items():
        if "-%s-" % view in jid:
            return spec["scene"]
    raise ValueError(jid)


def prepare(maps_folder):
    """Copies the baseline ground dump of the fair scene (fw_off-ground-u*) and of the clock scene (k_off-ground-u*) as the scenes fw and ck."""
    os.makedirs(INPUT, exist_ok=True)
    for scene, prefix in DUMPS.items():
        found = sorted(e for e in os.listdir(maps_folder) if e.startswith(prefix) and os.path.isdir(os.path.join(maps_folder, e)))
        if not found:
            raise SystemExit("no dump %s* in %s" % (prefix, maps_folder))
        dump = os.path.join(maps_folder, found[-1])
        for layer in c1.STACK:
            for kind in ("cover", "type"):
                shutil.copyfile(os.path.join(dump, "%s-%s.u8" % (layer, kind)), os.path.join(INPUT, c1.map_name(scene, "cover", layer, kind)))
        shutil.copyfile(os.path.join(dump, "humidity.u8"), os.path.join(INPUT, "%s-humidity.r8" % scene))
        print("scene %s <- %s" % (scene, found[-1]))
    # The orientation check of the dump: the fair scene's maps flipped in x (fwx), in y (fwy) and rolled by half the width (fwr), drawn at the fair ground view.
    for variant, op in (("fwx", lambda a: a[:, ::-1]), ("fwy", lambda a: a[::-1, :]), ("fwr", lambda a: np.roll(a, c1.MAP_W // 2, axis=1))):
        names = [c1.map_name("fw", "cover", layer, kind) for layer in c1.STACK for kind in ("cover", "type")] + ["fw-humidity.r8"]
        for name in names:
            data = np.fromfile(os.path.join(INPUT, name), dtype=np.uint8).reshape(c1.MAP_H, c1.MAP_W)
            np.ascontiguousarray(op(data)).tofile(os.path.join(INPUT, name.replace("fw-", variant + "-", 1)))
    write_inputs()


# --------------------------------------------------------------------------------------------------------------------------------
# The picture and the numbers
# --------------------------------------------------------------------------------------------------------------------------------
LUMA = np.array([0.2126, 0.7152, 0.0722])


def tint_of(spec):
    """CloudOutputStage.WhiteTint of the sun a cloud at the lower quarter of the lowest layer gets, for this scene's sun."""
    sin_el = math.sin(math.radians(spec["elev"]))
    sun = e40ty.sun_air(sin_el, 0.25 * 4690.0)
    luma = sun @ LUMA
    s = e40ty.white_share(sin_el)
    return 1.0 + (sun / luma - 1.0) * s


def encode(c, white, tint):
    return white * np.power(np.maximum(c / (white * tint), 0.0), 1.0 / e40ty.GAMMA)


def game_frame(name):
    return e40ty.load_png(os.path.join(GAME, "t09-%s-%s-1707x960-natural.png" % (GAME_START, name)))


def compose(view, m, off):
    """The player's frame over the game's baseline frame: the stage's display value of the cloud, laid over with its coverage (the game's pass 4 blend)."""
    spec = VIEWS[view]
    a = np.clip(m[:, :off.shape[1], 3:4], 0.0, 1.0)
    colour = encode(m[:, :off.shape[1], :3] / np.maximum(a, 1e-4), spec["white"], tint_of(spec))
    return np.clip(colour * a + off * (1.0 - a), 0.0, 1.0), a[..., 0]


def rocket_mask(shape):
    """True where the picture is measured: not the vessel and the pad in the middle, not the ground and the sea below the horizon (the clouds only)."""
    h, w = shape[:2]
    ok = np.ones((h, w), dtype=bool)
    ok[:, 770:940] = False
    ok[int(h * 0.80):, :] = False
    return ok


def signal(img, off):
    """What the clouds add to the picture: the length of the colour difference to the baseline."""
    return np.sqrt(((img - off) ** 2).sum(axis=-1))


def smooth(a, n):
    k = np.ones(n) / n
    a = np.apply_along_axis(lambda r: np.convolve(r, k, mode="same"), 1, a)
    return np.apply_along_axis(lambda r: np.convolve(r, k, mode="same"), 0, a)


def edge_stats(sig, ok):
    """Width of an outline (10 to 90 percent of the cloud's own signal, pixels) and the share of the signal's power above 0.1 cycles per pixel (the high-frequency energy)."""
    s95 = float(np.percentile(sig[ok], 95))
    gy, gx = np.gradient(sig)
    g = np.hypot(gx, gy)
    sel = ok & (sig > 0.3 * s95) & (sig < 0.7 * s95)
    sel[:2], sel[-2:], sel[:, :2], sel[:, -2:] = False, False, False, False
    width = 0.8 * s95 / float(np.median(g[sel])) if sel.sum() > 200 else float("nan")
    z = np.where(ok, sig, 0.0)
    z = z - z[ok].mean() * ok
    f = np.fft.rfft2(z * np.hanning(z.shape[0])[:, None] * np.hanning(z.shape[1])[None, :])
    p = np.abs(f) ** 2
    ky = np.fft.fftfreq(z.shape[0])[:, None]
    kx = np.fft.rfftfreq(z.shape[1])[None, :]
    k = np.hypot(kx, ky)
    total = float(p.sum())
    hf = float(p[k > 0.1].sum() / max(total, 1e-12))
    mid = float(p[(k > 0.03) & (k <= 0.1)].sum() / max(total, 1e-12))
    return dict(edge=width, hf=hf, mid=mid, s95=s95, cloud=float(((sig > 0.25 * s95) & ok).sum() / max(ok.sum(), 1)))


def luma_stats(img, off, sig, ok, white):
    """Luminance (display values) of the cloud pixels: the sunlit side (P90), the median, the base (P10), the sky next to them, and the same against the white point."""
    s95 = float(np.percentile(sig[ok], 95))
    cloud = ok & (sig > 0.25 * s95)
    sky = ok & (sig < 0.03 * s95)
    luma = img @ LUMA
    sky_l = float(np.median((off @ LUMA)[sky])) if sky.sum() > 200 else float("nan")
    if cloud.sum() < 200:
        return dict(p10=float("nan"), p50=float("nan"), p90=float("nan"), sky=sky_l)
    c = luma[cloud]
    return dict(p10=float(np.percentile(c, 10)), p50=float(np.percentile(c, 50)), p90=float(np.percentile(c, 90)), sky=sky_l,
                p90_sky=float(np.percentile(c, 90) / sky_l), p50_sky=float(np.percentile(c, 50) / sky_l), p90_white=float(np.percentile(c, 90) / white))


def measure_frame(view, img, off):
    ok = rocket_mask(img.shape)
    sig = signal(img, off)
    out = edge_stats(sig, ok)
    out.update(luma_stats(img, off, sig, ok, VIEWS[view]["white"]))
    # what the clouds add, in absolute terms (the baseline is the game's own, so the two frames are comparable), and the colour of the sunlit side (the brightest tenth of the cloud pixels)
    out["sig_mean"] = float(sig[ok].mean())
    out["share10"], out["share20"] = float((sig[ok] > 0.10).mean()), float((sig[ok] > 0.20).mean())
    s95 = float(np.percentile(sig[ok], 95))
    cloud = ok & (sig > 0.25 * s95)
    luma = img @ LUMA
    if cloud.sum() > 200:
        top = cloud & (luma >= np.percentile(luma[cloud], 90))
        out["rgb90"] = [float(x) for x in img[top].mean(axis=0)]
    return out


def measure_game():
    """The numbers of the game's own frames of the three views."""
    rows = {}
    for view, spec in VIEWS.items():
        if view not in ("gfair", "hfair", "gclock"):
            continue
        for kind in ("on",):
            path = os.path.join(GAME, "t09-%s-%s-1707x960-natural.png" % (GAME_START, spec[kind]))
            if not os.path.isfile(path):
                continue
            rows["game|%s" % view] = measure_frame(view, game_frame(spec["on"]), game_frame(spec["off"]))
    return rows


def words(label, r):
    rgb = r.get("rgb90")
    return "   %-40s edge %6.1f px hf %.4f mid %.4f | sig mean %.3f >.1 %.2f >.2 %.2f | luma P10 %.3f P50 %.3f P90 %.3f P90/white %.2f rgb90 %s" % (
        label, r["edge"], r["hf"], r["mid"], r.get("sig_mean", float("nan")), r.get("share10", float("nan")), r.get("share20", float("nan")), r.get("p10", float("nan")),
        r.get("p50", float("nan")), r.get("p90", float("nan")), r.get("p90_white", float("nan")), "%.2f %.2f %.2f" % tuple(rgb) if rgb else "-")


# --------------------------------------------------------------------------------------------------------------------------------
# The series
# --------------------------------------------------------------------------------------------------------------------------------
LIGHT = ["-light", "product", "-dump", "last"]
R33 = c1.R33
R50 = c1.BASE + "+r50"
R100 = c1.BASE + "+r100"
# label -> (variants, media, cases)
STAGES = {
    "v1-a": (["base", "az90", "az0", "ss0"], "pr50", [R33, R50, R100]),
    "v1-d": (["base"], "pr50", [R33 + "+acc0", R33 + "+acc50", R33 + "+acc100", "seq1m128+lq2+r33"]),
    "v1-e": (["ssh", "ns125", "ns500"], "pr50", [R33, R100]),
    "v1-h": (["sf30", "sf100", "dr06", "dr10", "ss0dr06", "sshdr06", "haze"], "pr50", [R33]),
    "v1-o": (["base"], "pr50", [R33]),
    "v1-i": (["hzcs10", "hzcs20", "hzsg05", "hzsg03", "hzssh", "hzss0"], "pr50", [R33]),
    "v1-j": (["hl125", "hl135", "hl150", "hl135ssh"], "pr50", [R33]),
    "v1-k": (["cser15", "cser20", "csbase", "csssh", "csssq", "csns125", "cshl135"], "pr50", [R33]),
    # the same jobs as v1-k's csbase and v1-h's haze, built from the cfg of the tree the run is made in: run before and after a cfg change under different labels (E40V1_ONLY)
    "v1-z": (["csbase", "haze"], "pr50", [R33]),
}
MAIN_VIEWS = ["gfair", "hfair", "gclock"]
# E40V1_ONLY=v1-i,v1-j: run these stages only (the others stay as the last run left them)
ONLY = [x for x in os.environ.get("E40V1_ONLY", "").split(",") if x]
STAGE_VIEWS = {"v1-o": ["gfairx", "gfairy", "gfairr"]}


def stage_views(label):
    return STAGE_VIEWS.get(label, MAIN_VIEWS)


def stage_list():
    if os.path.isfile(os.path.join(INPUT, "jobs.json")):
        write_inputs()
    out = []
    for label, (names, medium, cases) in STAGES.items():
        if ONLY and label not in ONLY:
            continue
        views = ",".join(tag(v, n) for v in stage_views(label) for n in names)
        out.append(Stage(label, ["-mode", "scene", "-scene", "soft-temporal", "-in", INPUT, "-views", views, "-media", medium, "-marches", ",".join(cases)] + LIGHT, size=(W, H), proxy=False,
                         timeout=3400))
    return out


def collect(ctx):
    os.makedirs(PICTURES, exist_ok=True)
    rows, lines, metrics = {}, ["E40-V1: the game's maps in the player (still camera, frame 39); edge = 10-90 percent width of the cloud signal over the baseline, hf = power share above 0.1 cycles/px"], []
    for key, r in measure_game().items():
        rows[key] = r
        lines.append(words(key, r))
    for label, (names, medium, cases) in STAGES.items():
        try:
            folder = ctx.folder(label)
        except KeyError:
            continue
        for view in stage_views(label):
            off = game_frame(VIEWS[view]["off"])
            for n in names:
                for march in ["ref"] + cases:
                    path = os.path.join(folder, case_file(label, view, n, medium, march) + ".npy")
                    if not os.path.isfile(path):
                        continue
                    m = e40ty.load_lum(folder, case_file(label, view, n, medium, march))
                    img, alpha = compose(view, m, off)
                    r = measure_frame(view, img, off)
                    # the coverage's own edge: the alpha image, measured as the signal
                    ok = rocket_mask(img.shape)
                    a_edge = edge_stats(alpha, ok)
                    r["alpha_edge"], r["alpha_hf"] = a_edge["edge"], a_edge["hf"]
                    key = "%s|%s|%s|%s" % (view, n, medium, march)
                    rows[key] = r
                    lines.append(words(key.replace(c1.BASE, "p"), r) + "  alphaedge %.1f" % r["alpha_edge"])
                    from weather_metrics import e40gn as _gn
                    _gn.save_png(os.path.join(folder, "v1-%s.png" % key.replace("|", "_").replace("+", "_")), img)
                    metrics.append(Metric.info("e40v1.%s.edge" % key, r["edge"]))
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(rows, f, indent=1)
    print("\n".join(lines))
    return metrics


register_series("e40-v1", stage_list(), collect, exclusive=False, who="e40-v1")

# The cost of the pass on the game's own maps (e40c2's scenes: six baseline dumps at the K-T2 views) with the product march only, for a before / after of a cfg change: run once with
# E40TY_CFG=<the cfg before> and once without; the results file is e40v1-cost-results.json (copy it between the runs).
COST_RESULTS = os.path.join(BUILD, "e40v1-cost-results.json")


def cost_stage_list():
    from weather_metrics import e40c2 as c2
    views = [c1.tag(s, "now", v) for s in c2.scenes() for v in c2.scene_views(s)]
    if not views:
        return []
    c2.write_inputs()
    return [Stage("v1-cost", ["-mode", "scene", "-scene", "soft-cost", "-in", c2.IN, "-views", ",".join(views), "-media", "pr50", "-marches", R33], size=(1707, 960), proxy=True,
                  timeout=1700)]


def collect_cost(ctx):
    from weather_metrics import e40gc as gc
    return gc.collect_cost(ctx, [("v1-cost", "", "", [])], COST_RESULTS)


register_series("e40-v1-cost", cost_stage_list(), collect_cost, exclusive=True, who="e40-v1")


def selftest():
    jobs = all_jobs()
    assert len({j["id"] for j in jobs}) == len(jobs) == len(VIEWS) * len(variants())
    assert all(len(j["layers"]) == len(c1.STACK) for j in jobs)
    base = [j for j in jobs if j["id"].endswith("v1-gfair-base-prod-f0")][0]
    ss0 = [j for j in jobs if "gfair-ss0" in j["id"]][0]
    cu = [l for l in ss0["layers"] if l["name"] == CUMULUS][0]
    assert cu["shapeSmoothing"] == 0.0 and all(t.get("shapeSmoothing", 0.0) == 0.0 for t in cu["types"])
    assert [l for l in base["layers"] if l["name"] == CUMULUS][0]["noiseScale"] == 250.0
    # the sun: west, 44 degrees up
    cam = camera("gfair")
    sun = np.asarray(base["sun"])
    up = cam.pos / np.linalg.norm(cam.pos)
    assert abs(math.degrees(math.asin(float(sun @ up))) - 44.07) < 0.01
    # a synthetic cloud: a blurred disc over a flat sky has the width of its blur
    n = 400
    y, x = np.mgrid[0:n, 0:n]
    r = np.hypot(x - n / 2, y - n / 2)
    for width in (4.0, 16.0):
        sig = np.clip((120.0 - r) / width + 0.5, 0.0, 1.0)
        ok = np.ones((n, n), dtype=bool)
        e = edge_stats(sig, ok)
        assert abs(e["edge"] - 0.8 * width) < 0.2 * width, (width, e)
    print("selftest passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--prepare", metavar="MAPS")
    parser.add_argument("--measure-game", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.prepare:
        prepare(a.prepare)
    elif a.measure_game:
        for k, r in measure_game().items():
            print(words(k, r))
