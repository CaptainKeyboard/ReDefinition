"""Cuts a replay recording (tools/replay_fg.ps1) to the replay window, deletes the
rest, names each frame after the dumped frame it shows or the pair it was generated
between, and prints the detail of the runway and of the vessel.

Usage: python tools/replay_measure.py <recording folder> <dump folder>
"""

import glob
import os
import sys

import numpy as np
from PIL import Image


def dump_frames(dump):
    frames = []
    w = h = 0
    for line in open(os.path.join(dump, "frames.txt"), encoding="utf-8"):
        if line.startswith("texture colour "):
            w, h = (int(v) for v in line.split()[2].split("x"))
            break
    n = 0
    while os.path.exists(os.path.join(dump, f"colour-{n:02d}.bin")):
        raw = np.fromfile(os.path.join(dump, f"colour-{n:02d}.bin"), dtype=np.uint8)
        g = raw.reshape(h, w, 4)[:, :, :3].astype(np.float32).mean(2)
        frames.append(g.reshape(h // 2, 2, w // 2, 2).mean((1, 3)))
        n += 1
    return frames


def detail(g):
    return np.abs(4 * g[1:-1, 1:-1] - g[:-2, 1:-1] - g[2:, 1:-1] - g[1:-1, :-2] - g[1:-1, 2:]).mean()


def main():
    folder, dump = sys.argv[1], sys.argv[2]
    refs = dump_frames(dump)
    h, w = refs[0].shape
    files = sorted(glob.glob(os.path.join(folder, "[0-9][0-9][0-9].png")))
    first = np.asarray(Image.open(files[0]).convert("L"), dtype=np.float32)
    # Where the window's client area lies in the capture: matched on a patch.
    patch_y, patch_x = h // 2, w // 3
    best = None
    for ref in refs:
        patch = ref[patch_y:patch_y + 80, patch_x:patch_x + 160]
        for oy in range(0, first.shape[0] - h + 1):
            for ox in range(0, first.shape[1] - w + 1, 1):
                e = np.abs(first[patch_y + oy:patch_y + oy + 80, patch_x + ox:patch_x + ox + 160] - patch).mean()
                if best is None or e < best[0]:
                    best = (e, ox, oy)
        if best[0] < 4:
            break
    _, ox, oy = best
    client = os.path.join(folder, "client")
    os.makedirs(client, exist_ok=True)
    real, generated = [], []
    for f in files:
        im = Image.open(f).convert("RGB").crop((ox, oy, ox + w, oy + h))
        os.remove(f)
        g = np.asarray(im.convert("L"), dtype=np.float32)
        errs = [np.abs(g - r).mean() for r in refs]
        k = int(np.argmin(errs))
        is_real = errs[k] < 4
        name = f"real-{k}" if is_real else "generated"
        im.save(os.path.join(client, os.path.basename(f)[:-4] + "-" + name + ".png"))
        runway = detail(g[int(h * 0.72):, :int(w * 0.3)])
        (real if is_real else generated).append(runway)
    print(f"window at ({ox},{oy}); {len(real)} rendered, {len(generated)} generated frames")
    if real and generated:
        print(f"runway detail: rendered {np.mean(real):.2f}, generated {np.mean(generated):.2f}"
              f" ({100 * (1 - np.mean(generated) / np.mean(real)):.0f} % lost)")


if __name__ == "__main__":
    sys.exit(main())
