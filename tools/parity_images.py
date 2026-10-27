"""Compares the views' pictures with the raytracing off and on, tile by tile.

The views probe (-redefinitionProbeViews with -redefinitionProbeViewModes Off,Light) writes <shot>-v<i>-Off.png and
<shot>-v<i>-Light.png from the same pose. The raytracing changes the light, so the whole picture may be warmer or
darker; what it must not do is change one part of a surface and not its neighbour: a patch, a line, a black
reflection. Per tile the ratio of the two pictures' luminance is taken against the ratio's median over the
picture; a tile off by more than a factor counts. The share of such tiles is the figure the gate holds.

Usage: python parity_images.py <folder> <shot> [--max-share 5.0] [--tile 40]

Prints one line per pose and exits 1 where a pose's share is over the limit.
"""
import argparse
import glob
import os
import re
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import imaging  # noqa: E402

# The factor a tile's ratio may differ from the picture's median by. Soft shadows, wind in the grass and the
# rays' own shadows move single tiles by this much.
FACTOR = 1.3
# The picture's top and bottom edges hold KSP's interface, the same in both pictures.
SKIP_TOP, SKIP_BOTTOM = 0.10, 0.18
# Tiles darker than this in the picture without rays are left out: their ratio is noise.
DARKEST = 0.04


def luminance(path):
    a = np.asarray(Image.open(path).convert("RGB")).astype(np.float64) / 255.0
    return imaging.luminance(a)


def compare(off_path, on_path, tile):
    off, on = luminance(off_path), luminance(on_path)
    h, w = off.shape
    y0, y1 = int(h * SKIP_TOP), int(h * (1 - SKIP_BOTTOM))
    off, on = off[y0:y1], on[y0:y1]
    th, tw = (off.shape[0] // tile), (off.shape[1] // tile)
    off = off[: th * tile, : tw * tile].reshape(th, tile, tw, tile).mean(axis=(1, 3))
    on = on[: th * tile, : tw * tile].reshape(th, tile, tw, tile).mean(axis=(1, 3))
    valid = off > DARKEST
    ratio = np.where(valid, on / np.maximum(off, 1e-6), 1.0)
    median = float(np.median(ratio[valid])) if valid.any() else 1.0
    relative = ratio / median
    off_by = valid & ((relative > FACTOR) | (relative < 1.0 / FACTOR))
    share = 100.0 * off_by.sum() / max(1, valid.sum())
    worst = np.unravel_index(np.argmax(np.abs(np.log(np.maximum(relative, 1e-6))) * valid), relative.shape)
    return share, median, (int(worst[1] * tile), int(worst[0] * tile + y0))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("folder")
    parser.add_argument("shot")
    parser.add_argument("--max-share", type=float, default=5.0)
    parser.add_argument("--tile", type=int, default=40)
    args = parser.parse_args()
    failed = 0
    poses = sorted(glob.glob(os.path.join(args.folder, args.shot + "-v*-Off.png")), key=lambda p: int(re.search(r"-v(\d+)-Off", p).group(1)))
    if not poses:
        print("FAIL  no pictures of", args.shot, "in", args.folder)
        return 1
    for off in poses:
        on = off.replace("-Off.png", "-Light.png")
        name = re.search(r"(-v\d+)-Off", off).group(1)
        if not os.path.exists(on):
            print("FAIL  %s: no picture with the rays" % name)
            failed += 1
            continue
        share, median, at = compare(off, on, args.tile)
        ok = share <= args.max_share
        print("%s  %s: %.1f %% of the tiles differ from the picture's own ratio (at most %.1f), ratio %.2f, worst at x %d y %d"
              % ("PASS" if ok else "FAIL", name, share, args.max_share, median, at[0], at[1]))
        failed += 0 if ok else 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
