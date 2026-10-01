"""Measures frame generation on the test player's camera turn: each frame it generated
between two frames of the turn against the reference of the pose halfway between them,
beside what a reset would show there instead, a copy of the newer frame.

    python tools/measure_fg_turn.py <recording client folder> <turn folder> <first turn frame>

The recording is replay_fg.ps1's, of a dump turn_to_fg_dump.py wrote from <turn folder>
starting at <first turn frame>; replay_measure.py has named its frames real-<n> and
generated. The references are the turn's mid_<index> frames, encoded as the dump encodes
the colour and at the replay's half size. Errors are mean absolute differences of 8-bit
grey values, over the frame and over its outer tenth on each side.
"""

import glob
import os
import sys

import numpy as np
from PIL import Image


def grey(image):
    return image[..., :3].astype(np.float32).mean(axis=2)


def reference(turn, name, w, h):
    raw = np.fromfile(os.path.join(turn, name + '_colour.bin'), dtype=np.float16).reshape(h, w, 4).astype(np.float32)
    encoded = (np.clip(raw[::-1, :, :3], 0.0, 1.0) ** (1.0 / 2.2) * 255.0 + 0.5).astype(np.uint8).astype(np.float32)
    g = encoded.mean(axis=2)
    return g.reshape(h // 2, 2, w // 2, 2).mean(axis=(1, 3))


def errors(a, b):
    d = np.abs(a - b)
    h, w = d.shape
    edge = np.zeros_like(d, dtype=bool)
    edge[:, :w // 10] = edge[:, -w // 10:] = True
    return d.mean(), d[edge].mean()


def main():
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    recording, turn, first = sys.argv[1], sys.argv[2], int(sys.argv[3])
    sizes = {}
    for line in open(os.path.join(turn, 'meta.txt'), encoding='utf-8'):
        parts = line.split()
        if parts and not parts[0].startswith('#'):
            sizes[parts[0]] = (int(parts[1]), int(parts[2]))
    path = [float(v) for v in open(os.path.join(turn, 'path.txt'), encoding='utf-8').read().split()]
    files = sorted(glob.glob(os.path.join(recording, '*.png')))
    print('pair     turn  step   generated  edge    copy   edge    real check')
    previous_real = None
    seen = set()
    for i, f in enumerate(files):
        label = os.path.basename(f)[4:-4]
        if label.startswith('real-'):
            previous_real = int(label[5:])
            continue
        if label != 'generated' or previous_real is None or i + 1 >= len(files):
            continue
        following = os.path.basename(files[i + 1])[4:-4]
        if not following.startswith('real-'):
            continue
        newer = int(following[5:])
        if newer != previous_real + 1 or newer in seen:
            continue
        seen.add(newer)
        index = first + newer
        name = 'mid_%03d' % index
        if name not in sizes:
            continue
        w, h = sizes[name]
        truth = reference(turn, name, w, h)
        generated = grey(np.array(Image.open(f)))
        copy = grey(np.array(Image.open(files[i + 1])))
        if generated.shape != truth.shape:
            print('sizes differ: %s against %s' % (generated.shape, truth.shape))
            return
        real_truth = reference(turn, 'turn_%03d' % index, w, h)
        g_all, g_edge = errors(generated, truth)
        c_all, c_edge = errors(copy, truth)
        check, _ = errors(copy, real_truth)
        print('%2d-%2d   %3d  %5.1f   %7.2f  %6.2f   %6.2f  %6.2f   %5.2f' % (
            newer - 1, newer, index, path[index] - path[index - 1], g_all, g_edge, c_all, c_edge, check))


if __name__ == '__main__':
    main()
