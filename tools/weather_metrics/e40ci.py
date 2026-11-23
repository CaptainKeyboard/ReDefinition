"""E40 CIRRUS: the structure of the cirrus layer's optical depth from the CPU twin of the layer shader, without the player (a MODEL number, not a game number).

    python tools/weather_metrics/e40ci.py --cfg <Clouds-Kerbin.cfg> [--type Cirrus] [--map 0.4] [--softness 0.5] [--png <file>] [key=value ...]
    python tools/weather_metrics/e40ci.py --selftest

The vertical optical depth of the layer on a horizontal grid (rows north, columns east) is evaluated with the shader's own mapping (noise_model.py: the ranked shape volume read through the
filament stretch along the wind axis and the untiling warp, the erosion detail, CloudWater's soft water content with the layer's softnessScale, the type's profile and rise) and the structure
is measured on it: the share of the grid with an optical depth above 0.03 (the visible threshold of cirrus, Sassen and Griffin 1989), the percentiles of the optical depth where there is
cloud, the widths of the autocorrelation across and along the wind axis (acf_widths) and the width across as an angle for an observer at 30, 45 and 60 degrees elevation below the layer's
middle. Keys after the options replace the cfg's numbers of the layer for a sweep: noiseScaleM, filamentStretch, softnessScale, sigmaPerM, densityScale (of the type), erosion (of the type).
"""
import argparse
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import e40o  # noqa: E402
from weather_metrics import noise_model as nm  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
CLOSED_TAU = 0.92          # CloudWater.ClosedTau
DETAIL_MEAN = 0.5          # CloudSoftDensity.NominalDetailMean
VISIBLE_TAU = 0.03


def acf_widths(field, mask, step_m, axis_en):
    """The autocorrelation widths of a field on a regular grid (rows north, columns east; `mask` 1 where the field is valid): across and along `axis_en` (east, north) as full widths at half
    maximum in m, the elongation, whether the length along is a lower bound (the autocorrelation did not fall to a half inside the grid) and the direction of the longest axis off the axis in degrees."""
    from scipy.ndimage import map_coordinates
    field = (field - (field * mask).sum() / mask.sum()) * mask
    ny, nx = field.shape
    shape = (2 * ny, 2 * nx)
    spec = np.fft.rfft2(field, shape)
    spec_m = np.fft.rfft2(mask, shape)
    acf = np.fft.irfft2(np.abs(spec) ** 2, shape)
    count = np.fft.irfft2(np.abs(spec_m) ** 2, shape)
    acf = np.fft.fftshift(acf / np.maximum(count, 0.05 * count.max()))
    cy, cx = shape[0] // 2, shape[1] // 2
    acf = acf / acf[cy, cx]
    reach = int(min(cy, cx) - 2)
    radii = np.arange(0, reach, 0.5)
    half = []
    bounded = []
    angles = np.radians(np.arange(0.0, 180.0, 3.0))
    for a in angles:
        # the line through the centre in the direction (east, north) = (cos a, sin a) of the plane: rows are north
        xs = cx + radii * math.cos(a)
        ys = cy + radii * math.sin(a)
        line = map_coordinates(acf, [ys, xs], order=1, mode="nearest")
        below = np.where(line < 0.5)[0]
        if len(below) == 0:
            half.append(radii[-1] * step_m)
            bounded.append(True)
        else:
            i = below[0]
            f = (line[i - 1] - 0.5) / max(line[i - 1] - line[i], 1e-9)
            half.append((radii[i - 1] + f * (radii[i] - radii[i - 1])) * step_m)
            bounded.append(False)
    half = np.array(half)
    wind = math.atan2(axis_en[1], axis_en[0])
    across = half[np.argmin(np.abs(((angles - wind) % math.pi) - math.pi / 2.0))]
    along_i = int(np.argmin(np.abs(((angles - wind + math.pi / 2.0) % math.pi) - math.pi / 2.0)))
    along = half[along_i]
    longest = int(np.argmax(half))
    miss = abs(((math.degrees(angles[longest] - wind) + 90.0) % 180.0) - 90.0)
    return dict(across_m=float(2.0 * across), along_m=float(2.0 * along), elongation=float(along / max(across, 1.0)), along_bounded=bool(bounded[along_i]), longest_axis_off_wind_deg=float(miss))


def load_layer(cfg_path, layer_name="Kerbin-cirrus"):
    """The numbers of one layer of a Clouds-<body>.cfg that the optical depth needs (the soft density's and the noise's) as a dict; the types by name."""
    with open(cfg_path, encoding="utf-8") as f:
        root = e40o.parse_nodes(f.read())
    for layer in [c for c in e40o.find(root, "CLOUDS")["children"] if c["name"] == "LAYER"]:
        if e40o.val(layer, "name") != layer_name:
            continue

        def num(node, key, default):
            v = e40o.val(node, key)
            return float(v) if v is not None else default

        types = {}
        for t in [c for c in layer["children"] if c["name"] == "TYPE"]:
            keys = []
            for c in t["children"]:
                if c["name"] == "PROFILE":
                    keys = [tuple(float(x) for x in v.split()) for _, v in c["values"]]
            types[e40o.val(t, "name")] = dict(base=num(t, "baseShare", 0.0), top=num(t, "topShare", 1.0), erosion=num(t, "erosion", 0.3), density=num(t, "densityScale", 1.0),
                                              base_gain=num(t, "baseGain", 0.3), rise_share=num(t, "riseShare", 0.5), profile=keys)
        return dict(inner=num(layer, "innerAltitudeM", 9000.0), outer=num(layer, "outerAltitudeM", 11000.0), sigma=num(layer, "sigmaPerM", 2e-4), scale=num(layer, "noiseScaleM", 3000.0),
                    stretch=num(layer, "filamentStretch", 1.0), softness_scale=num(layer, "softnessScale", 1.0), water_power=num(layer, "waterPower", 2.0), types=types)
    raise SystemExit("no layer %s in %s" % (layer_name, cfg_path))


def closed_content(layer, t, softness):
    """CloudWater.Closed for the type: the water content at the outline."""
    mean = 0.0
    n = 128
    keys = np.array(t["profile"])
    for i in range(n):
        h = (i + 0.5) / n
        rise = t["base_gain"] + (1.0 - t["base_gain"]) * min(max(h / t["rise_share"], 0.0), 1.0) if t["rise_share"] > 0.0 else 1.0
        mean += float(np.interp(h, keys[:, 0], keys[:, 1])) * rise
    mean /= n
    tau = layer["sigma"] * (t["top"] - t["base"]) * (layer["outer"] - layer["inner"]) * mean * t["density"]
    c = min(1.0, CLOSED_TAU / max(tau, 1e-9)) ** (1.0 / max(layer["water_power"], 0.1))
    e = t["erosion"] * (1.0 - DETAIL_MEAN)
    return c * (1.0 - e) + e


_VOLUMES = []


def volumes():
    if not _VOLUMES:
        _VOLUMES.append(nm.Volumes(64, 32, 32).quantized())
    return _VOLUMES[0]


def vertical_tau(layer, type_name, map_value, softness, extent_m=50000.0, step_m=100.0, axis_en=(1.0, 1.0), heights=8):
    """The vertical optical depth of the layer on a grid of `extent_m` square centred on the camera's foot, and the grid's step. rows north, columns east."""
    t = layer["types"][type_name]
    width = max(0.02, softness * layer["softness_scale"])
    closed = closed_content(layer, t, softness)
    cc = min(max(map_value, 0.0), 1.0)
    threshold = 1.0 - min(cc, 2.0 * cc - closed * max(width, 1e-3))
    ax = np.array([axis_en[0], axis_en[1], 0.0])
    ax = ax / np.linalg.norm(ax)
    prm = nm.Params(scale=layer["scale"], erosion=t["erosion"], stretch=layer["stretch"], axis=ax)
    vols = volumes()
    n = int(extent_m / step_m)
    xs = (np.arange(n) - n / 2.0 + 0.5) * step_m
    ge, gn = np.meshgrid(xs, xs)
    ge, gn = ge.ravel(), gn.ravel()
    thick = (layer["outer"] - layer["inner"]) * (t["top"] - t["base"])
    keys = np.array(t["profile"])
    tau = np.zeros(ge.shape)
    for j in range(heights):
        h = (j + 0.5) / heights
        alt = layer["inner"] + (t["base"] + h * (t["top"] - t["base"])) * (layer["outer"] - layer["inner"])
        p = np.stack([ge, gn, np.full(ge.shape, alt)], axis=1)
        rank = nm.shape_value(vols, p, prm)
        content = np.clip((rank - threshold) / width + closed, 0.0, 1.0)
        d = nm.detail_value(vols, p, prm)
        e = t["erosion"] * (1.0 - d)
        eroded = np.where(content >= 1.0, 1.0, np.clip((content - e) / np.maximum(1.0 - e, 1e-3), 0.0, 1.0))
        rise = t["base_gain"] + (1.0 - t["base_gain"]) * min(h / t["rise_share"], 1.0) if t["rise_share"] > 0.0 else 1.0
        density = layer["sigma"] * eroded ** layer["water_power"] * float(np.interp(h, keys[:, 0], keys[:, 1])) * rise * t["density"]
        tau += density * thick / heights
    return tau.reshape(n, n), step_m


def measure(tau, step_m, axis_en, middle_m, inner_m=None):
    """The structure numbers of a vertical optical depth grid (see the header)."""
    cloud = tau > VISIBLE_TAU
    out = dict(cover=float(cloud.mean()), tau_p10_p50_p90=[float(x) for x in np.percentile(tau[cloud], [10, 50, 90])] if cloud.any() else None, tau_mean=float(tau.mean()),
               tau_max=float(tau.max()))
    # the ground view's sky: share of the picture with a slant optical depth under 0.1 (the sun disc is not dimmed by more than 10 %)
    out.update(acf_widths(tau, np.ones_like(tau), step_m, axis_en))
    out["across_deg"] = {str(e): float(math.degrees(2.0 * math.atan(0.5 * out["across_m"] / (middle_m / math.sin(math.radians(e)))))) for e in (30, 45, 60)}
    return out


def render(tau, step_m, size=(854, 480), pitch=35.0, fov=60.0, height_m=9500.0):
    """A crude ground view of the grid for a first look (plane-parallel: a ray at elevation e crosses the layer at the horizontal offset height / tan e; the slant optical depth is the
    vertical one over sin e; the cloud adds a white veil of the slant alpha over a blue sky). Not the product's light."""
    w, h = size
    half = math.tan(math.radians(fov) / 2.0)
    xs = ((np.arange(w) + 0.5) / w * 2.0 - 1.0) * half * w / h
    ys = (1.0 - (np.arange(h) + 0.5) / h * 2.0) * half
    gx, gy = np.meshgrid(xs, ys)
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    # the camera looks north: forward = (0, cp, sp), up = (0, -sp, cp), right = east
    dx = gx
    dy = cp - gy * sp
    dz = sp + gy * cp
    norm = np.sqrt(dx * dx + dy * dy + dz * dz)
    elev = np.degrees(np.arcsin(dz / norm))
    t = height_m / np.maximum(dz, 1e-3)
    e = dx * t
    n = dy * t
    from scipy.ndimage import map_coordinates
    size_n = tau.shape[0]
    field = map_coordinates(tau, [n / step_m + size_n / 2.0, e / step_m + size_n / 2.0], order=1, mode="constant", cval=0.0)
    alpha = 1.0 - np.exp(-field / np.maximum(np.sin(np.radians(elev)), 0.15))
    alpha = np.where(elev > 12.0, alpha, 0.0)
    k = np.clip(elev / 70.0, 0.0, 1.0)[..., None]
    sky = np.array([0.42, 0.58, 0.82]) * (1.0 - k) + np.array([0.15, 0.33, 0.72]) * k
    return np.clip(sky * (1.0 - 0.8 * alpha[..., None]) + np.array([0.92, 0.93, 0.95]) * 0.8 * alpha[..., None], 0.0, 1.0)


def save_png(path, img):
    from PIL import Image
    Image.fromarray(np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8), "RGB").save(path)


def run(cfg, type_name, map_value, softness, overrides, wind_en=(17.7, 17.7), extent=50000.0, step=100.0, png=None):
    layer = load_layer(cfg)
    for k, v in overrides.items():
        if k == "noiseScaleM":
            layer["scale"] = v
        elif k == "filamentStretch":
            layer["stretch"] = v
        elif k == "softnessScale":
            layer["softness_scale"] = v
        elif k == "sigmaPerM":
            layer["sigma"] = v
        elif k == "densityScale":
            layer["types"][type_name]["density"] = v
        elif k == "erosion":
            layer["types"][type_name]["erosion"] = v
        else:
            raise SystemExit("unknown key " + k)
    tau, st = vertical_tau(layer, type_name, map_value, softness, extent, step, wind_en)
    middle = 0.5 * (layer["inner"] + layer["outer"]) - 264.0
    m = measure(tau, st, wind_en, middle)
    if png:
        save_png(png, render(tau, st, height_m=middle))
    return m, tau


def selftest():
    # a synthetic stripe field: the autocorrelation width across the wind axis is the stripe's, along it the long one
    rng = np.random.RandomState(1)
    from scipy.ndimage import gaussian_filter
    f = gaussian_filter(rng.standard_normal((600, 600)), sigma=(60, 6), mode="wrap")     # rows north: long north-south stripes
    m = acf_widths(f, np.ones_like(f), 100.0, (0.0, 1.0))
    assert abs(m["across_m"] / (2.355 * 1.414 * 600.0) - 1.0) < 0.15, m
    assert m["elongation"] > 5.0 and m["longest_axis_off_wind_deg"] < 10.0, m
    layer = load_layer(os.path.join(REPO, "GameData", "ReDefinition", "Atmospheres", "Bodies", "Clouds-Kerbin.cfg"))
    t = layer["types"]["Cirrus"]
    c = closed_content(layer, t, 0.5)
    assert 0.9 < c <= 1.0, c      # the cirrus column is thin (tau 0.5 below 0.92): the outline's water content is full
    print("selftest passed (across %.0f m, elongation %.1f, closed %.3f)" % (m["across_m"], m["elongation"], c))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    ap.add_argument("--cfg", default=os.path.join(REPO, "GameData", "ReDefinition", "Atmospheres", "Bodies", "Clouds-Kerbin.cfg"))
    ap.add_argument("--type", default="Cirrus")
    ap.add_argument("--map", type=float, default=0.4)
    ap.add_argument("--softness", type=float, default=0.5)
    ap.add_argument("--png")
    ap.add_argument("--step", type=float, default=100.0)
    ap.add_argument("--extent", type=float, default=50000.0)
    ap.add_argument("keys", nargs="*")
    a = ap.parse_args()
    if a.selftest:
        selftest()
        sys.exit(0)
    over = {k: float(v) for k, v in (s.split("=") for s in a.keys)}
    res, _ = run(a.cfg, a.type, a.map, a.softness, over, extent=a.extent, step=a.step, png=a.png)
    print(res)
