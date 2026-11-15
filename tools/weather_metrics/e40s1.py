"""E40 S1: the size and the edge of the cells of a fair-weather cumulus field, from above, ours against True Volumetric Clouds (player series e40-s1).

    python tools/weather_metrics/e40s1.py --prepare <tag> <maps-dir> <cfg or ->   copies the maps of the times of SITES as that tag (cfg: the Clouds-Kerbin.cfg of this tag, - = the shipped one)
    python tools/build_weather_player.py --series e40-s1 [--no-build]
    python tools/weather_metrics/e40s1.py --measure-tvc            the same numbers for the TVC frame of K-T2 (start 47)
    python tools/weather_metrics/e40s1.py --selftest

<maps-dir> is the dump of tests/ReDefinition.Tests/Atmospheres/T09/SkyMapsExplore.cs (the maps the game hands to the layer pass; per time and layer u<ut>_<layer>_cover.u8 / _type.u8 and
u<ut>_humidity.u8). Each tag and time is one scene, drawn twice: the cumulus layer alone ("cu": the cells without the other layers) and the whole stack ("all"), from the K-T2 views of the
TVC frames (8364 m, 25 degrees down, east), from 10 km (30 down) and from 40 km straight down. The numbers of the cumulus-only pictures go to the log and to build/weather-series/e40s1-results.json:
the picture is put on the plane of the cloud tops (an orthographic grid, 30 m a cell) and measured there: cloud share, the size of the cells (the autocorrelation length of the cloud mask, the
area-weighted diameter of the components), the gaps, the width of an edge (10 to 90 percent of the cover signal across the 0.5 contour) and the brightness of a cloud against a gap.
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
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "e40s1-in")
PLACE = os.path.join(INPUT, "place.json")
RESULTS = os.path.join(BUILD, "e40s1-results.json")
TILES = os.path.join(BUILD, "e40s1-tiles")
TVC_DIR = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "K-T2")
TVC_ABOVE = os.path.join(TVC_DIR, "t09-tvc1707-above-tvc-1707x960-natural.png")
TVC_GROUND = os.path.join(TVC_DIR, "t09-tvc1707-ground-tvc-1707x960-natural.png")
R = 600000.0
MAP_W, MAP_H = 512, 256
SIZE = (1708, 960)
# site -> (the time of the maps, latitude, longitude). ksc: the K-T2 site of the TVC frames; fairA and fairB: places of the real maps (SkyMapsExplore, seed 664216370) where the cumulus layer holds
# 0.5 to 0.55 of the sky in a 100 km window, with humilis / mediocris (A) or stratocumulus / humilis (B) and nothing of the rain deck, the cumulonimbus or the cirrus layer: a fair-weather field.
SITES = {"ksc": (1686000, -0.0972, -74.5577), "fairA": (1686000, 0.4, -98.5), "fairB": (1616400, -6.7, 158.6)}
STACKS = {"cu": ["Kerbin-cumulus"], "all": ["Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus"]}
# view -> (height above sea level in m, yaw from north in degrees, pitch). above8 and ground are the K-T2 views of the TVC frames.
VIEWS = {"above8": (8364.0, 90.0, -25.0), "above10": (10264.0, 90.0, -30.0), "nadir40": (40264.0, 90.0, -90.0), "ground": (264.0, 90.0, 18.0)}
PLANE_M = 2500.0          # the plane (above the sea) the pictures are put on: the tops of a fair-weather cumulus
DX = 30.0                 # metres of a cell of the orthographic grid
NADIR_PART = 0.5          # the share of the half-width of the nadir picture on the plane that is measured
EDGE_LO, EDGE_HI = 0.4, 0.6
CLOUD_ALPHA = 0.5


def camera(view, size=SIZE, site="ksc"):
    height, yaw, pitch = VIEWS[view]
    la, lo = math.radians(SITES[site][1]), math.radians(SITES[site][2])
    pos = np.array([math.cos(la) * math.cos(lo), math.cos(la) * math.sin(lo), math.sin(la)]) * (R + height)
    return pr.Camera(pos, yaw, pitch, size[0], size[1], gn.FOV)


def jid(tag, site, stack, view):
    return "ty-s1-%s-%s-%s-%s" % (tag, site, stack, view)


def map_name(tag, ut, layer, kind):
    return "%s-u%d-%s-%s.r8" % (tag, ut, layer, kind)


def place():
    with open(PLACE, encoding="utf-8") as f:
        return json.load(f)["scenes"]


def layer_data(cfg):
    previous = e40ty.CFG_PATH
    e40ty.CFG_PATH = cfg or e40o.CFG
    try:
        return {s["name"]: s for s in e40o.kerbin_layers(e40ty.CFG_PATH)}, e40ty.cfg_extras()
    finally:
        e40ty.CFG_PATH = previous


def job_layer(layer, extras, tag, ut):
    lk, tk = extras[layer["name"]]
    jl = e40o.job_layer(layer, map_name(tag, ut, layer["name"], "cover"), map_name(tag, ut, layer["name"], "type"))
    jl.update(lk)
    for t in jl["types"]:
        t.update(tk.get(t["name"], {}))
    return jl


def build_jobs():
    jobs, heights = [], []
    for tag, cfg in scenes_of():
        layers_by_name, extras = layer_data(cfg)
        for site, (ut, _, _) in SITES.items():
            for stack, names in STACKS.items():
                for view in VIEWS:
                    cam = camera(view, SIZE, site)
                    layers = [job_layer(layers_by_name[n], extras, tag, ut) for n in names]
                    j = lay.job(jid(tag, site, stack, view), "lum", cam, layers, SIZE[0], SIZE[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H,
                                sun=lay.sun_vector(cam.pos, 0.0, e40ty.SUN_ELEVATION), sunColour=[2.35, 2.35, 2.35], ambientTop=e40ty.AMBIENT_TOP, ambientBottom=e40ty.AMBIENT_BOTTOM,
                                soft=e40ty.SOFTNESS, maxSteps=256, minStep=4.0, jitter=0.0, earlyOut=0.001, sunSteps=3)
                    jobs.append(j)
                    # the height variation of the game (HeightHook: the module's switches on, the humidity of the time)
                    heights.append(dict(id=j["id"], variation=1, physics=1, topUpdraft=1, ragged=1, humidityMap="%s-u%d-humidity.r8" % (tag, ut), updraftMap="", width=MAP_W, height=MAP_H,
                                        seed=664216370 & 0xFFFFFF))
    return jobs, heights


def scenes_of():
    """[(tag, cfg path or None)] of the prepared tags."""
    return [(s[0], s[1] or None) for s in place()] if os.path.isfile(PLACE) else []


def prepare(tag, folder, cfg):
    os.makedirs(INPUT, exist_ok=True)
    for ut in sorted({s[0] for s in SITES.values()}):
        shutil.copyfile(os.path.join(folder, "u%d_humidity.u8" % ut), os.path.join(INPUT, "%s-u%d-humidity.r8" % (tag, ut)))
        for layer in STACKS["all"]:
            for kind in ("cover", "type"):
                shutil.copyfile(os.path.join(folder, "u%d_%s_%s.u8" % (ut, layer, kind)), os.path.join(INPUT, map_name(tag, ut, layer, kind)))
    kept = [s for s in (place() if os.path.isfile(PLACE) else []) if s[0] != tag]
    with open(PLACE, "w", encoding="utf-8") as f:
        json.dump(dict(scenes=kept + [[tag, "" if cfg in (None, "-") else os.path.abspath(cfg)]]), f)
    write_inputs()


def write_inputs():
    jobs, heights = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    with open(os.path.join(INPUT, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": heights}, f)


# --------------------------------------------------------------------------------------------------------------------------------
# The measurement: the picture on the plane of the cloud tops
# --------------------------------------------------------------------------------------------------------------------------------
def to_plane(img, cam, x_range, y_range, dx=DX, plane_m=PLANE_M, exclude=()):
    """Resamples an image (h, w, c) onto the plane at plane_m above the sea: a grid of x (east) in x_range and y (north) in y_range metres from the camera's sub-point, dx metres a cell.
    Returns (grid (ny, nx, c), valid (ny, nx))."""
    from scipy.ndimage import map_coordinates
    up, east, north = cam.frame
    p = cam.pos
    xs = np.arange(x_range[0], x_range[1], dx)
    ys = np.arange(y_range[1], y_range[0], -dx)             # the far rows on top
    gx, gy = np.meshgrid(xs, ys)
    rp = R + plane_m
    # the point at the arc length r in the azimuth of (gx, gy) from the sub-point: on the sphere of the plane
    dist = np.hypot(gx, gy)
    theta = dist / rp
    az = np.arctan2(gx, gy)
    direction = np.cos(az)[..., None] * north + np.sin(az)[..., None] * east
    sub = p / np.linalg.norm(p)
    target = (np.cos(theta)[..., None] * sub + np.sin(theta)[..., None] * direction) * rp
    d = target - p
    fwd = d @ cam.forward
    xc = (d @ cam.right) / np.maximum(fwd, 1e-6)
    yc = (d @ cam.cam_up) / np.maximum(fwd, 1e-6)
    half = math.tan(math.radians(cam.fov_v) * 0.5)
    aspect = cam.width / float(cam.height)
    u = (xc / (half * aspect) + 1.0) * 0.5 * cam.width - 0.5
    v = (1.0 - yc / half) * 0.5 * cam.height - 0.5
    valid = (fwd > 0) & (u >= 0) & (u <= cam.width - 1) & (v >= 0) & (v <= cam.height - 1)
    for u0, u1, v0, v1 in exclude:
        valid &= ~((u >= u0) & (u <= u1) & (v >= v0) & (v <= v1))
    chans = [map_coordinates(img[..., c], [v, u], order=1, mode="nearest") for c in range(img.shape[2])]
    return np.stack(chans, axis=-1), valid


def window_of(cam, near_deg, far_deg, half_width_m, plane_m=PLANE_M):
    """(x_range, y_range) in metres of the strip of the plane between the rays at near_deg and far_deg below the horizon along the camera's yaw (the camera looks east: x ahead)."""
    h = np.linalg.norm(cam.pos) - R - plane_m
    near = h / math.tan(math.radians(near_deg))
    far = h / math.tan(math.radians(far_deg))
    return near, far


def cell_stats(signal, valid, dx=DX):
    """Numbers of a cover signal in 0..1 on a grid (1 = cloud): the cloud share, the cells, the gaps and the edge. valid: the cells inside the picture."""
    from scipy import ndimage
    ok = valid & np.isfinite(signal)
    mask = (signal >= 0.5) & ok
    n = float(ok.sum())
    out = dict(cover=float(mask.sum() / n), n=int(n))

    def components(m):
        lab, count = ndimage.label(m & ok)
        if count == 0:
            return np.zeros(0)
        area = np.bincount(lab.ravel())[1:].astype(np.float64) * dx * dx
        # components that touch the border of the window are cut short: kept (the measure is of a strip), flagged by the count
        return area

    for name, m in (("cloud", mask), ("gap", ~mask)):
        area = components(m)
        keep = area >= 4.0 * dx * dx
        area = area[keep]
        if area.size == 0:
            out[name] = dict(count=0)
            continue
        diam = 2.0 * np.sqrt(area / math.pi)
        order = np.argsort(diam)
        cum = np.cumsum(area[order]) / area.sum()
        out[name] = dict(count=int(area.size), per_100km2=float(area.size / (n * dx * dx / 1e8)), median_diam=float(np.median(diam)),
                         area_weighted_median_diam=float(diam[order][np.searchsorted(cum, 0.5)]), p90_diam=float(np.percentile(diam, 90)))
    # the autocorrelation length of the mask (the 1/e radius of the radial mean), by FFT on the strip with the invalid cells at the mean
    m = np.where(ok, mask.astype(np.float64), mask[ok].mean()) - mask[ok].mean()
    f = np.fft.rfft2(m, s=(2 * m.shape[0], 2 * m.shape[1]))
    ac = np.fft.irfft2(f * np.conj(f))
    ac = np.fft.fftshift(ac) / max(ac.max(), 1e-12)
    cy, cx = ac.shape[0] // 2, ac.shape[1] // 2
    yy, xx = np.indices(ac.shape)
    rr = np.hypot(yy - cy, xx - cx).astype(np.int64)
    radial = np.bincount(rr.ravel(), ac.ravel()) / np.maximum(np.bincount(rr.ravel()), 1)
    below = np.nonzero(radial < 1.0 / math.e)[0]
    out["corr_length_m"] = float(below[0] * dx) if below.size else float("nan")
    # the edge: 10 to 90 percent of the signal across the 0.5 contour = 0.8 / |gradient| at the cells with the signal between EDGE_LO and EDGE_HI
    gy_, gx_ = np.gradient(signal, dx)
    grad = np.hypot(gx_, gy_)
    edge = (signal >= EDGE_LO) & (signal <= EDGE_HI) & ok
    out["edge_width_m"] = float(0.8 / np.median(grad[edge])) if edge.any() and np.median(grad[edge]) > 0 else float("nan")
    out["edge_cells"] = int(edge.sum())
    return out


def luma(img):
    return img @ np.array([0.2126, 0.7152, 0.0722])


def colour_signal(img, valid):
    """A cover signal from the colours of a picture whose gaps are blue and whose clouds are white (the TVC frame, and ours for the same measure): 0 (gap) to 1 (cloud), from (B - R) between its
    5th and 95th percentile over the valid cells."""
    br = img[..., 2] - img[..., 0]
    lo, hi = np.percentile(br[valid], 5), np.percentile(br[valid], 95)
    return np.clip((hi - br) / max(hi - lo, 1e-6), 0.0, 1.0), (lo, hi)


def measure_picture(img, cam, alpha=None, near=12.0, far=35.0, exclude=()):
    """The numbers of one picture (h, w, 3 display colours; alpha (h, w) of the pass where there is one) on the plane of the tops."""
    if cam.pitch <= -89.0:
        h = np.linalg.norm(cam.pos) - R - PLANE_M
        half = h * math.tan(math.radians(cam.fov_v) * 0.5)
        # the middle of the picture only (rays within 16 degrees of the vertical): the sides of the columns are not seen there
        x_range, y_range = (-half * NADIR_PART * cam.width / cam.height, half * NADIR_PART * cam.width / cam.height), (-half * NADIR_PART, half * NADIR_PART)
    else:
        n, f = window_of(cam, far, near, 0.0)
        # along the camera's yaw (east: +x) from the sub-point; the width of the strip is that of the picture at the near edge
        x_range = (n, f)
        w_half = n * math.tan(math.radians(cam.fov_v) * 0.5) * cam.width / cam.height * 0.8
        y_range = (-w_half, w_half)
    chans = [img] if alpha is None else [img, alpha[..., None]]
    stack = np.concatenate(chans, axis=2)
    grid, valid = to_plane(stack, cam, x_range, y_range, exclude=exclude)
    rgb = grid[..., :3]
    res = dict(window_m=[float(x_range[0]), float(x_range[1]), float(y_range[0]), float(y_range[1])])
    sig, (lo, hi) = colour_signal(rgb, valid)
    res["colour"] = cell_stats(sig, valid)
    if alpha is not None:
        a = np.clip(grid[..., 3], 0.0, 1.0)
        res["alpha"] = cell_stats(a, valid)
        cloud = (a >= CLOUD_ALPHA) & valid
        gap = (a < 0.05) & valid
        if cloud.any() and gap.any():
            res["brightness_cloud_over_gap"] = float(luma(rgb)[cloud].mean() / max(luma(rgb)[gap].mean(), 1e-6))
    cloud = (sig >= 0.5) & valid
    gap = (sig < 0.5) & valid
    if cloud.any() and gap.any():
        res["colour_brightness_cloud"] = float(luma(rgb)[cloud].mean())
        res["colour_brightness_gap"] = float(luma(rgb)[gap].mean())
    return res, (grid, valid)


def words(name, s):
    c = s.get("colour", {})
    a = s.get("alpha", {})
    lines = ["   %s" % name]
    for kind, st in (("alpha", a), ("colour", c)):
        if not st:
            continue
        cl, gp = st.get("cloud", {}), st.get("gap", {})
        lines.append("      %-6s cover %.2f  corr length %5.0f m  edge 10-90%% %5.0f m  cloud cells: %4s, area-weighted median %5.0f m, p90 %5.0f m  gaps: %4s, median %5.0f m, area-weighted median %5.0f m" % (
            kind, st["cover"], st.get("corr_length_m", float("nan")), st.get("edge_width_m", float("nan")), cl.get("count", 0), cl.get("area_weighted_median_diam", float("nan")),
            cl.get("p90_diam", float("nan")), gp.get("count", 0), gp.get("median_diam", float("nan")), gp.get("area_weighted_median_diam", float("nan"))))
    return "\n".join(lines)


def measure_tvc():
    img = e40ty.load_png(TVC_ABOVE)
    cam = camera("above8", (img.shape[1], img.shape[0]))
    # the rocket stands in the middle of the picture (columns 800 to 910, rows 380 to 620): those cells are not measured
    res, _ = measure_picture(img, cam, exclude=[(800, 910, 380, 620)])
    print(words("TVC start 47, above8 (the K-T2 frame)", res))
    return res


# --------------------------------------------------------------------------------------------------------------------------------
# The series
# --------------------------------------------------------------------------------------------------------------------------------
def collect(ctx):
    return evaluate(ctx.folder("sheet"))


def evaluate(sheet):
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    os.makedirs(TILES, exist_ok=True)
    tint = e40ty.white_tint(3000.0)
    lines, metrics, results = ["E40-S1: cells of the cumulus layer, from above, on the plane of the tops (%g m), %g m a cell" % (PLANE_M, DX)], [], {}
    for tag, _ in scenes_of():
        for site in SITES:
            for stack in STACKS:
                for view in VIEWS:
                    path = os.path.join(sheet, jid(tag, site, stack, view) + ".npy")
                    if not os.path.isfile(path):
                        lines.append("   %s %s %s %s: no picture" % (tag, site, stack, view))
                        continue
                    m = e40ty.load_lum(sheet, jid(tag, site, stack, view))
                    cam = camera(view, SIZE, site)
                    img = e40ty.picture(m, cam, tint)
                    gn.save_png(os.path.join(TILES, "%s-%s-%s-%s.png" % (tag, site, stack, view)), img)
                    if stack == "cu" and view in ("above8", "above10", "nadir40"):
                        res, _ = measure_picture(img, cam, alpha=np.clip(m[..., 3], 0.0, 1.0))
                        results["%s.%s.%s" % (tag, site, view)] = res
                        lines.append(words("%s %s %s" % (tag, site, view), res))
                        metrics.append(Metric.info("e40s1.%s.%s.%s.corr" % (tag, site, view), res["alpha"]["corr_length_m"]))
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    return metrics


def compose(before, after, name, tiles_dir=TILES):
    """e40-s1-<name>.png: per site the cumulus layer alone (before | after | the TVC frame of K-T2 start 47) from the K-T2 view above, from 40 km straight down (before | after), and the whole stack
    from the ground and from 10 km (before | after | the TVC ground frame)."""
    tile = (570, 320)
    rows = []
    blank = np.zeros((tile[1], tile[0], 3))

    def cell(tag, site, stack, view, text):
        path = os.path.join(tiles_dir, "%s-%s-%s-%s.png" % (tag, site, stack, view))
        return e40ty.label(e40ty.box(e40ty.load_png(path), tile), text) if os.path.isfile(path) else blank

    def tvc(path, text):
        return e40ty.label(e40ty.box(e40ty.load_png(path), tile), text) if os.path.isfile(path) else blank

    for site in SITES:
        rows.append(np.concatenate([cell(before, site, "cu", "above8", "%s before: cumulus alone, from above" % site), cell(after, site, "cu", "above8", "after"),
                                    tvc(TVC_ABOVE, "TVC start 47, from above (its own sky)")], axis=1))
        rows.append(np.concatenate([cell(before, site, "cu", "nadir40", "%s before: 40 km straight down" % site), cell(after, site, "cu", "nadir40", "after"), blank], axis=1))
        rows.append(np.concatenate([cell(before, site, "all", "above10", "%s before: whole stack, 10 km" % site), cell(after, site, "all", "above10", "after"), blank], axis=1))
        rows.append(np.concatenate([cell(before, site, "all", "ground", "%s before: whole stack, ground" % site), cell(after, site, "all", "ground", "after"),
                                    tvc(TVC_GROUND, "TVC start 47, ground")], axis=1))
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, "e40-s1-%s.png" % name)
    gn.save_png(path, np.concatenate(rows, axis=0))
    print("wrote", path)


def stages():
    if os.path.isfile(PLACE):
        write_inputs()
    return [Stage("sheet", ["-mode", "scene", "-scene", "type-sheet", "-in", INPUT], size=(1708, 960), proxy=False, timeout=3600)]


register_series("e40-s1", stages(), collect, exclusive=False, who="e40-s1")


# --------------------------------------------------------------------------------------------------------------------------------
# The cost (series e40-s1-cost): the product pass at 1707 x 960 on the whole stack, the K-T2 views at the three sites, per tag (the product's march, the game's height variation)
# --------------------------------------------------------------------------------------------------------------------------------
COST_IN = os.path.join(BUILD, "e40s1-cost-in")
COST_JSON = os.path.join(BUILD, "e40s1-cost-results.json")
COST_SIZE = (1707, 960)
COST_VIEWS = {"ground": (264.0, 90.0, 18.0), "inside": (4614.0, 90.0, 8.0), "above": (8364.0, 90.0, -25.0), "sky3": (264.0, 270.0, 38.0)}
COST_BASE = "seq1+lq2"      # the game's default march (48 grid points, light quality High)


def cost_id(tag, site, view):
    return "%s_%s_%s" % (tag, site, view)


def cost_jobs():
    jobs, heights = [], []
    for tag, cfg in scenes_of():
        layers_by_name, extras = layer_data(cfg)
        for site, (ut, la, lo) in SITES.items():
            for view, (height, yaw, pitch) in COST_VIEWS.items():
                p = np.array([math.cos(math.radians(la)) * math.cos(math.radians(lo)), math.cos(math.radians(la)) * math.sin(math.radians(lo)), math.sin(math.radians(la))]) * (R + height)
                cam = pr.Camera(p, yaw, pitch, COST_SIZE[0], COST_SIZE[1], gn.FOV)
                layers = [job_layer(layers_by_name[n], extras, tag, ut) for n in STACKS["all"]]
                j = lay.job("gn-%s-prod-f0" % cost_id(tag, site, view), "lum", cam, layers, COST_SIZE[0], COST_SIZE[1], planet=R, mapWidth=MAP_W, mapHeight=MAP_H, frameNumber=0, soft=0.5,
                            sun=lay.sun_vector(cam.pos, 0.0, gn.SUN_ELEVATION), sunColour=gn.SUN_COLOUR, ambientTop=gn.AMBIENT_TOP, ambientBottom=gn.AMBIENT_BOTTOM, **gn.prod())
                jobs.append(j)
                heights.append(dict(id=j["id"], variation=1, physics=1, topUpdraft=1, ragged=1, humidityMap="%s-u%d-humidity.r8" % (tag, ut), updraftMap="", seed=664216370 & 0xFFFFFF))
    return jobs, heights


def write_cost_inputs():
    jobs, heights = cost_jobs()
    os.makedirs(COST_IN, exist_ok=True)
    for name in os.listdir(INPUT):
        if name.endswith(".r8"):
            shutil.copyfile(os.path.join(INPUT, name), os.path.join(COST_IN, name))
    with open(os.path.join(COST_IN, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    with open(os.path.join(COST_IN, "heights.json"), "w", encoding="utf-8") as f:
        json.dump({"specs": heights}, f)


def cost_stages():
    if os.path.isfile(PLACE):
        write_cost_inputs()
    views = [cost_id(tag, site, view) for tag, _ in scenes_of() for site in SITES for view in COST_VIEWS]
    return [Stage("s1-cost", ["-mode", "scene", "-scene", "soft-cost", "-in", COST_IN, "-views", ",".join(views), "-media", "pr50", "-marches", COST_BASE],
                  size=(1707, 960), proxy=True, timeout=3400)]


def collect_cost(ctx):
    from weather_metrics import e40gc as gc
    return gc.collect_cost(ctx, [("s1-cost", "", "", [])], COST_JSON)


register_series("e40-s1-cost", cost_stages(), collect_cost, exclusive=True, who="e40-s1")


def selftest():
    # a synthetic field of round cells 1000 m across on a 2500 m plane, seen from the K-T2 above camera: the measure finds the size back
    cam = camera("above8")
    n, f = window_of(cam, 35.0, 12.0, 0.0)
    assert f > n > 5000.0
    rng = np.random.default_rng(1)
    nx = ny = 600
    sig = np.zeros((ny, nx))
    yy, xx = np.indices(sig.shape)
    for _ in range(120):
        cx, cy = rng.uniform(0, nx), rng.uniform(0, ny)
        sig = np.maximum(sig, np.clip(1.5 - np.hypot(xx - cx, yy - cy) * DX / 500.0, 0.0, 1.0))
    st = cell_stats(sig, np.ones_like(sig, dtype=bool))
    assert 0.2 < st["cover"] < 0.9 and 300.0 < st["cloud"]["median_diam"] < 4000.0, st
    # a ramp from 0 to 1 over 200 cells has 160 cells between 10 and 90 percent
    ramp = np.clip(np.linspace(-3.0, 3.0, 200)[None, :].repeat(40, axis=0) / 6.0 + 0.5, 0.0, 1.0)
    st = cell_stats(ramp, np.ones_like(ramp, dtype=bool))
    assert abs(st["edge_width_m"] - 0.8 * 200.0 * DX) < 0.1 * 0.8 * 200.0 * DX, st["edge_width_m"]
    print("selftest passed")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--measure-tvc", action="store_true")
    parser.add_argument("--compose", nargs=3, metavar=("BEFORE_TAG", "AFTER_TAG", "NAME"), help="the side-by-side picture of two tags of the last run")
    parser.add_argument("--remeasure", metavar="SHEET_FOLDER", help="measures the pictures of a finished run again (build/WeatherPlayer/e40-s1-sheet)")
    parser.add_argument("--prepare", nargs="+", metavar=("TAG", "MAPS"))
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.compose:
        compose(a.compose[0], a.compose[1], a.compose[2], TILES)
    elif a.remeasure:
        evaluate(a.remeasure)
    elif a.measure_tvc:
        measure_tvc()
    elif a.prepare:
        prepare(a.prepare[0], a.prepare[1], a.prepare[2])
        print("prepared", INPUT)
