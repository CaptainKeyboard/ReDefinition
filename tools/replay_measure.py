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
        # The frame as presented where the dump has it; the HUD-less colour otherwise.
        name = f"frame-{n:02d}.bin" if os.path.exists(os.path.join(dump, f"frame-{n:02d}.bin")) else f"colour-{n:02d}.bin"
        raw = np.fromfile(os.path.join(dump, name), dtype=np.uint8)
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
    # The whole window against every dumped frame. The replay window stands at the
    # screen's top left corner, so its client area lies within the first rows and
    # columns of the capture.
    best = None
    for ref in refs:
        for oy in range(0, min(64, first.shape[0] - h + 1)):
            for ox in range(0, min(48, first.shape[1] - w + 1)):
                e = np.abs(first[oy:oy + h:4, ox:ox + w:4] - ref[::4, ::4]).mean()
                if best is None or e < best[0]:
                    best = (e, ox, oy)
    _, ox, oy = best
    client = os.path.join(folder, "client")
    os.makedirs(client, exist_ok=True)
    real, generated = [], []
    items = []
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
        items.append((is_real, k, g))
    print(f"window at ({ox},{oy}); {len(real)} rendered, {len(generated)} generated frames")
    if real and generated:
        print(f"runway detail: rendered {np.mean(real):.2f}, generated {np.mean(generated):.2f}"
              f" ({100 * (1 - np.mean(generated) / np.mean(real)):.0f} % lost)")
    shadow_edges(dump, refs, items)
    large_scale(items)


def large_scale(items):
    """Generated frames against the mean of their rendered neighbours, the grain
    softened away: what is left are patches and ghosts, in the lower part of the
    picture where the ground is."""
    values = []
    for i in range(1, len(items) - 1):
        if items[i][0] or not items[i - 1][0] or not items[i + 1][0]:
            continue
        if items[i + 1][1] != items[i - 1][1] + 1:
            continue
        d = items[i][2] - (items[i - 1][2] + items[i + 1][2]) / 2
        low = np.abs(blur(d, 8))[int(d.shape[0] * 0.4):]
        values.append((low.mean(), np.percentile(low, 99)))
    if values:
        v = np.array(values)
        print(f"large-scale deviation on the ground: mean {v[:, 0].mean():.2f}, 99th percentile {v[:, 1].mean():.2f}")


def blur(a, s):
    r = 3 * s
    k = np.exp(-0.5 * (np.arange(-r, r + 1) / s) ** 2)
    k /= k.sum()
    p = np.pad(a, r, mode="edge")
    p = sum(k[i] * p[:, i:i + a.shape[1]] for i in range(2 * r + 1))
    return sum(k[i] * p[i:i + a.shape[0], :] for i in range(2 * r + 1))


def shadow_edges(dump, refs, items):
    """Generated frames against the mean of the two rendered frames around them,
    softened so the grain does not count, at the edges of shadows on the moving ground."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import analyze_fg_inputs as A
    frames = A.load_frames(dump)
    H, W = frames[0]["colour"].shape
    mid = len(frames) // 2
    m = frames[mid]["motion"]
    # Motion and depth at the render size, looked up for each pixel of the half-size replay.
    rows = np.arange(H // 2) * m.shape[0] // (H // 2)
    cols = np.arange(W // 2) * m.shape[1] // (W // 2)
    at = lambda a: a[rows][:, cols]
    speed = at(np.hypot(m[..., 0] * W, m[..., 1] * H))
    ground = (speed > 6) & (at(frames[mid]["depth"].astype(np.float32)) > 0)
    soft = [blur(r, 4) for r in refs]
    gy, gx = np.gradient(soft[mid])
    edge = (np.hypot(gx, gy) > 1.2) & ground
    edge[: edge.shape[0] * 2 // 5] = False
    errors = []
    for i in range(1, len(items) - 1):
        if items[i][0] or not items[i - 1][0] or not items[i + 1][0]:
            continue
        a, b = items[i - 1][1], items[i + 1][1]
        if b != a + 1:
            continue
        d = np.abs(blur(items[i][2], 4) - (soft[a] + soft[b]) / 2)[edge]
        errors.append((d.mean(), np.percentile(d, 95)))
    if errors:
        e = np.array(errors)
        print(f"shadow edges on the ground ({edge.sum()} px): generated against its neighbours mean {e[:, 0].mean():.2f},"
              f" 95th percentile {e[:, 1].mean():.2f} ({len(e)} frames)")


if __name__ == "__main__":
    sys.exit(main())
