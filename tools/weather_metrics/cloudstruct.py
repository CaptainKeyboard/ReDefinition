"""One comparable measure of the cloud picture's structure for the player, the game and the TVC reference frames (E40 FL-FIX).

  python tools/weather_metrics/cloudstruct.py --selftest

The old numbers were not comparable: the player's 'transitions per row' (e40fl.structure) counted the layer's alpha at 640 x 360 over the rows below the horizon,
the pictures' (e40fl.picture_structure) a grey-and-bright colour mask at a resize of the file over the lower 60 percent, with the sea's sun glint counted as cloud.
This measure is the same for every source:

  1. every picture and its cloud-free partner (the same pose without clouds) is reduced to one grid, 320 x 180, by the box average (the blur against the game's grain:
     one grid cell is the average of the pixels that fall into it, so the grain of the game's half-size march is gone at the scale the cells are counted at);
  2. a cell is cloud where the picture differs from the cloud-free partner by more than DIFF (0.12) in some channel: the glint, the sea, the sky and the terrain
     that both frames hold cancel, only what the clouds add or take away remains; a 3 x 3 majority filter takes out single cells;
  3. over the same window, the rows from 45 to 100 percent of the height (below the horizon in every source: the horizon stands at 22 to 28 percent) and the
     columns outside the middle 8 percent (the vessel), it counts the cloud share and the mean number of cloud/clear changes per row.

The floor is the same measure applied to two cloud-free frames of the same pose where the sources have two (the game's starts), and to the old colour mask on the
cloud-free frame (the glint the old measure counted as cloud); a source with no repeat has no floor and says so.
"""
import argparse
import os

import numpy as np

WORK = (320, 180)
ROWS = (0.45, 1.0)
VESSEL = (0.46, 0.54)
DIFF = 0.12


def load(path, size=WORK):
    """A picture file as (h, w, 3) floats 0..1 on the common grid (box average)."""
    from PIL import Image
    im = Image.open(path).convert("RGB").resize(size, Image.BOX)
    return np.asarray(im, dtype=np.float64) / 255.0


def to_grid(arr, size=WORK):
    """An (h, w, 3) float array of any size on the common grid (box average through PIL, 8 bit)."""
    from PIL import Image
    im = Image.fromarray(np.clip(np.rint(arr * 255.0), 0, 255).astype(np.uint8), "RGB").resize(size, Image.BOX)
    return np.asarray(im, dtype=np.float64) / 255.0


def majority3(mask):
    """A 3 x 3 majority of a boolean mask (the edge repeats)."""
    p = np.pad(mask.astype(np.int32), 1, mode="edge")
    s = sum(p[i:i + mask.shape[0], j:j + mask.shape[1]] for i in range(3) for j in range(3))
    return s >= 5


def cloud_mask(pic, clear, diff=DIFF):
    return majority3(np.abs(pic - clear).max(axis=-1) > diff)


def window(shape):
    h, w = shape
    rows = np.arange(int(h * ROWS[0]), int(h * ROWS[1]))
    cols = np.array([c for c in range(w) if not (VESSEL[0] * w <= c < VESSEL[1] * w)])
    return rows, cols


def structure(mask):
    """(cloud share, transitions per row) of a mask over the window; the changes are counted along the rows with the vessel's columns cut out."""
    rows, cols = window(mask.shape)
    m = mask[np.ix_(rows, cols)]
    # the cut at the vessel joins two columns that are not neighbours: the change across it is not counted
    gap = int(np.searchsorted(cols, int(VESSEL[0] * mask.shape[1])))
    changes = (m[:, 1:] != m[:, :-1])
    changes[:, gap - 1] = False
    return float(m.mean()), float(changes.sum(axis=1).mean())


def measure(pic, clear):
    """(share, transitions) of a picture against its cloud-free partner, both on the common grid."""
    return structure(cloud_mask(pic, clear))


def legacy(img):
    """The old colour mask (grey, bright) over the same window, for the glint it counts on a cloud-free frame."""
    mx, mn = img.max(axis=-1), img.min(axis=-1)
    return structure(((mx - mn) / np.maximum(mx, 1e-3) < 0.18) & (mx > 0.35))


def selftest():
    rng = np.random.RandomState(1)
    h, w = WORK[1], WORK[0]
    clear = np.dstack([np.full((h, w), v) for v in (0.2, 0.35, 0.55)])
    # cells of 8 x 8 alternating in checkerboard, 3 cells wide per 24 columns: known transitions
    pic = clear.copy()
    for j in range(0, w, 16):
        pic[:, j:j + 8] = 0.9
    s, t = measure(pic, clear)
    assert abs(s - 0.5) < 0.03, s
    assert 33 < t < 40, t   # 20 stripes = 40 changes, minus those in the cut vessel columns
    # grain that averages out on the grid is no cloud
    grainy = np.clip(clear + rng.normal(0, 0.04, clear.shape), 0, 1)
    s2, t2 = measure(grainy, clear)
    assert s2 < 0.01 and t2 < 0.1, (s2, t2)
    # the same picture is its own floor
    assert structure(cloud_mask(clear, clear)) == (0.0, 0.0)
    print("selftest passed (%.2f %.1f, %.3f %.2f)" % (s, t, s2, t2))


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--selftest", action="store_true")
    if p.parse_args().selftest:
        selftest()
