"""Surface visibility of clouds: measurable quantities on the cloud pixels of one frame (package SURFACE, 2026-11-15).

    python tools/weather_metrics/surface_visibility.py game <view> [--start 64]          the game's frame of a PP64 view
    python tools/weather_metrics/surface_visibility.py replay <folder> <view> [...]      the player's replay (e40pp composite) of a view in an output folder
    python tools/weather_metrics/surface_visibility.py picture <png> [--baseline <png>]  any frame; the cloud mask against the baseline frame

The owner's symptom: 'the clouds have no visible surface, they are just a uniform area'. A surface is seen as structure inside the cloud (bulges, lit and shaded sides) and as
contrast to the sky at its edge. The quantities, all on the cloud mask (e40pp.cloud_stats: colour moved away from the frame without clouds and towards grey):

    lit      the sunlit over the shaded luminance, the 90th over the 10th percentile of the smoothed cloud luminance (e40pp.lit_ratio)
    cv8/16/32  the local contrast: the mean over 8 / 16 / 32 px blocks that lie wholly inside the eroded mask of the standard deviation of the luminance over its mean
    hf       the high-frequency share: the energy of the luminance band between 1 and 4 px (difference of Gaussians) over the squared mean luminance, inside the eroded mask
    edge     the outline's 10 - 90 % width in px (e40v1.edge_stats) where the mask has an outline
    dsky     the colour distance of the cloud pixels to the sky behind them (the baseline frame), and the luminance ratio cloud / sky
    dchroma  the chroma (rgb / luminance) of the cloud minus the sky's

Reference expectations (RE-cloud-types-2026-11-15.md): Cu 'base relatively dark and nearly horizontal, sunlit parts brilliant white, sharp outlines' (G1, text): lit above 1 with
the bottom darker than the top (the 'base' row below: the bottom quarter's mean over the top quarter's, expected below 1); Ns / St 'uniform, no outlines' (G1): a low cv is in range
there; Cb, Ac, Ci: no figure (G3): 'unclear'. The verdict words are only in range / out of range / unclear.
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import e40pp  # noqa: E402
from weather_metrics import e40ty  # noqa: E402
from weather_metrics import e40v1 as v1  # noqa: E402

LUMA = v1.LUMA


def eroded(mask, px):
    from scipy.ndimage import binary_erosion
    return binary_erosion(mask, iterations=int(px))


def block_cv(lum, inside, size):
    """The mean of std / mean of the luminance over size x size blocks that lie wholly inside the mask."""
    h, w = lum.shape
    values = []
    for y in range(0, h - size + 1, size):
        for x in range(0, w - size + 1, size):
            if inside[y:y + size, x:x + size].all():
                block = lum[y:y + size, x:x + size]
                values.append(block.std() / max(block.mean(), 1e-4))
    return (float(np.mean(values)), len(values)) if values else (float("nan"), 0)


def band_energy(lum, inside, lo=1.0, hi=4.0):
    from scipy.ndimage import gaussian_filter
    band = gaussian_filter(lum, lo) - gaussian_filter(lum, hi)
    sel = band[inside]
    mean = lum[inside].mean()
    return float((sel ** 2).mean() / max(mean * mean, 1e-8)) if sel.size else float("nan")


def thirds(cloud, lum):
    """The bottom quarter's mean luminance over the top quarter's, per cloud: the rows of the mask's bounding box."""
    rows = np.where(cloud.any(axis=1))[0]
    if rows.size < 8:
        return float("nan")
    top, bottom = rows[0], rows[-1]
    q = max((bottom - top) // 4, 1)
    topq = cloud.copy()
    topq[:top, :] = False
    topq[top + q:, :] = False
    botq = cloud.copy()
    botq[:bottom - q, :] = False
    botq[bottom + 1:, :] = False
    if topq.sum() < 50 or botq.sum() < 50:
        return float("nan")
    return float(lum[botq].mean() / max(lum[topq].mean(), 1e-6))


def measure(img, off, ok=None):
    """The quantities of one frame against its frame without clouds; ok limits the pixels (the vessel and the ground left out)."""
    if ok is None:
        ok = np.ones(img.shape[:2], dtype=bool)
    stats, sig, cloud = e40pp.cloud_stats(img, off, ok)
    out = dict(cover=stats["cover"], n=stats["n"])
    if stats["n"] < 400:
        return out
    lum = img @ LUMA
    inside = eroded(cloud, 6)
    out["lit"] = e40pp.lit_ratio(img, cloud)
    for size in (8, 16, 32):
        cv, count = block_cv(lum, inside, size)
        out["cv%d" % size] = cv
        out["cv%d_blocks" % size] = count
    out["hf"] = band_energy(lum, inside)
    out["base"] = thirds(cloud, lum)
    edge = v1.edge_stats(sig, ok)
    out["edge"] = edge["edge"]
    out["luma"] = float(lum[cloud].mean())
    off_lum = off @ LUMA
    out["dsky_luma"] = float(lum[cloud].mean() / max(off_lum[cloud].mean(), 1e-6))
    out["dsky_colour"] = float(np.sqrt(((img[cloud] - off[cloud]) ** 2).sum(axis=-1)).mean())
    chroma = lambda a: float((a[cloud] / np.maximum((a @ LUMA)[cloud][:, None], 1e-4)).std(axis=-1).mean())
    out["dchroma"] = chroma(img) - chroma(off)
    return out


def words(rows):
    keys = ["cover", "lit", "cv8", "cv16", "cv32", "hf", "base", "edge", "dsky_luma", "dsky_colour", "dchroma"]
    lines = ["%-22s " % "frame" + " ".join("%9s" % k for k in keys)]
    for name, r in rows:
        lines.append("%-22s " % name + " ".join("%9.4f" % r.get(k, float("nan")) for k in keys))
    return "\n".join(lines)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("kind", choices=["game", "replay", "picture"])
    p.add_argument("a")
    p.add_argument("views", nargs="*")
    p.add_argument("--start", type=int, default=64)
    p.add_argument("--baseline")
    p.add_argument("--march", default=e40pp.MARCH if hasattr(e40pp, "MARCH") else "r33")
    args = p.parse_args()
    rows = []
    if args.kind == "game":
        names = [args.a] + args.views
        stages = e40pp.prepared_stages()
        for name in names:
            spec = e40pp.VIEWS[name]
            off, game = e40pp.game_frame(spec, "off_a", args.start), e40pp.game_frame(spec, "soft_a", args.start)
            alt = stages[name]["alt"] if name in stages else 1000.0
            rows.append(("game " + name, measure(game, off, e40pp.measure_mask(name, alt, game.shape))))
    elif args.kind == "replay":
        folder = args.a
        stages = e40pp.prepared_stages()
        for name in args.views:
            spec = e40pp.VIEWS[name.split("@")[0]]
            base = name.split("@")[0]
            off = e40pp.game_frame(spec, "off_a", args.start)
            stage = stages[base]
            m = e40ty.load_lum(folder, e40pp.case_file(base, args.march))
            replay = e40pp.compose(m, off, stage)
            rows.append(("replay " + name, measure(replay, off, e40pp.measure_mask(base, stage["alt"], off.shape))))
    else:
        from PIL import Image
        img = np.asarray(Image.open(args.a).convert("RGB")).astype(float) / 255.0
        off = np.asarray(Image.open(args.baseline).convert("RGB")).astype(float) / 255.0
        rows.append((os.path.basename(args.a), measure(img, off)))
    print(words(rows))
    return 0


if __name__ == "__main__":
    sys.exit(main())
