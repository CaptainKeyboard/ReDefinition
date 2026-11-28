"""A preview of the sky from the weather map as seen from above, from the dump of the test DumpTheMapSkyOfKerbin (2026-11-28).

    python tools/weather_metrics/mapsky_preview.py <dump folder> <out folder> [<ut> ...]

The dump holds, per time, each layer's cover before the meso pattern (<layer>_plan) and after it (<layer>_cover), 512 x 256 bytes, row 0 at the north pole. This tool
draws no clouds: it lays the layers' covers over a dark sea as opaque white in the share the cover gives (a layer's weight below), which is what the far view's mean
of a field comes to, and prints what a picture from orbit is judged by: the share of the planet under cloud and how much of it is clear.

Per time it writes <ut>_maps.png (the parts and the layers, plan beside cover), <ut>_globe.png (two orthographic views of the composite, 0 and 180 degrees east) and
prints: per layer the mean of plan and cover and the shares of texels below 0.05 and above 0.95; of the composite the mean, the clear share (below 0.1) and the
closed share (above 0.7), each weighted by the texel's area.
"""
import os
import sys

import numpy as np
from PIL import Image

W, H = 512, 256
# What a closed cover of a layer hides of what lies below it, seen from above (the stratiform sheet and the towers are opaque, a closed cumulus field nearly, cirrus thin).
LAYERS = [("Kerbin-weather-rain", 1.0), ("Kerbin-cumulus", 0.9), ("Kerbin-cumulonimbus", 1.0), ("Kerbin-cirrus", 0.35)]
PARTS = ["cover", "stratiform", "cumuliform", "part_low", "part_sc", "part_ac", "part_rising", "part_tall", "part_high", "rh_low", "rh_mid", "rh_high"]


def load(folder, name):
    path = os.path.join(folder, name + ".u8")
    if not os.path.exists(path):
        return None
    return np.fromfile(path, dtype=np.uint8).reshape(H, W).astype(float) / 255.0


def area_weights():
    lat = (0.5 - (np.arange(H) + 0.5) / H) * np.pi
    return np.repeat(np.cos(lat)[:, None], W, axis=1)


def composite(folder, tag, suffix):
    clear = np.ones((H, W))
    for name, weight in LAYERS:
        c = load(folder, tag + name + suffix)
        if c is not None:
            clear *= 1.0 - weight * c
    return 1.0 - clear


def globe(cloud, lon0_deg, size=720):
    """An orthographic view of the composite over a dark sea, centred on the equator at lon0."""
    y, x = np.mgrid[0:size, 0:size]
    u = (x + 0.5) / size * 2.0 - 1.0
    v = 1.0 - (y + 0.5) / size * 2.0
    r2 = u * u + v * v
    inside = r2 < 1.0
    z = np.sqrt(np.clip(1.0 - r2, 0.0, 1.0))
    lat = np.arcsin(np.clip(v, -1.0, 1.0))
    lon = np.radians(lon0_deg) + np.arctan2(u, z)
    col = ((np.degrees(lon) + 180.0) % 360.0) / 360.0 * W
    row = (0.5 - lat / np.pi) * H
    c = cloud[np.clip(row.astype(int), 0, H - 1), np.clip(col.astype(int), 0, W - 1)]
    # the sea at 0.06, the cloud at 0.85, a little limb darkening; display gamma 2.2
    lum = (0.06 + (0.85 - 0.06) * c) * (0.55 + 0.45 * z)
    lum = np.where(inside, lum, 0.0)
    return (np.clip(lum, 0.0, 1.0) ** (1.0 / 2.2) * 255.0).astype(np.uint8)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    folder, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    tags = sorted({f.split("_")[0] for f in os.listdir(folder) if f.startswith("u") and f.endswith(".u8")})
    if len(sys.argv) > 3:
        tags = ["u" + t for t in sys.argv[3:]]
    weights = area_weights()
    for t in tags:
        tag = t + "_"
        print("== " + t)
        tiles = []
        for name in PARTS:
            a = load(folder, tag + name)
            if a is not None:
                tiles.append(a)
                print("  %-12s mean %.3f" % (name, float((a * weights).sum() / weights.sum())))
        for name, _ in LAYERS:
            plan, cover = load(folder, tag + name + "_plan"), load(folder, tag + name + "_cover")
            if plan is None or cover is None:
                continue
            tiles += [plan, cover]
            for label, a in (("plan", plan), ("cover", cover)):
                print("  %-22s %-5s mean %.3f  below 0.05 %.3f  above 0.95 %.3f" % (name, label, float((a * weights).sum() / weights.sum()),
                                                                                 float(((a < 0.05) * weights).sum() / weights.sum()),
                                                                                 float(((a > 0.95) * weights).sum() / weights.sum())))
        for suffix in ("_plan", "_cover"):
            c = composite(folder, tag, suffix)
            print("  composite%-6s mean %.3f  clear (below 0.1) %.3f  closed (above 0.7) %.3f" % (suffix, float((c * weights).sum() / weights.sum()),
                                                                                             float(((c < 0.1) * weights).sum() / weights.sum()),
                                                                                             float(((c > 0.7) * weights).sum() / weights.sum())))
            views = np.hstack([globe(c, 0.0), globe(c, 180.0)])
            Image.fromarray(views).save(os.path.join(out, t + "_globe" + suffix + ".png"))
        while len(tiles) % 4:
            tiles.append(np.zeros((H, W)))
        rows = [np.hstack(tiles[i:i + 4]) for i in range(0, len(tiles), 4)]
        Image.fromarray((np.vstack(rows) * 255.0).astype(np.uint8)).save(os.path.join(out, t + "_maps.png"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
