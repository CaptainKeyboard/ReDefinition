"""Finds the stage of a frame that changes the sky beside the vessel, from two RenderDoc
captures of the same camera pose, with the vessel and with its renderers hidden
(tools/renderdoc_probe.ps1), each split into stages by tools/renderdoc_stages.py with
the tags "with" and "without".

    python tools/analyze_renderdoc_pair.py <folder> [--stat p95|median]

The stages of the two are matched by shader, from the last deferred lighting on. For
each, on the sky (nothing drawn in either capture's depth) and outside the vessel
(where the capture with it is nearer), the relative difference of the two in bands
of distance from the vessel: 1-4 px holds the edge's own antialiasing; a band beyond
it above the far ones is a halo, and the first stage where it appears is the one
that makes it. The pose check first: the horizontal and vertical shift that best
matches the two finished frames on the scenery right of the vessel, which has to
be (0, 0).
"""
import argparse
import os
import re

import numpy as np
from PIL import Image
from scipy import ndimage

WIDTH, HEIGHT = 2560, 1440
BANDS = ((1, 4), (4, 8), (8, 16), (16, 32), (32, 64), (64, 128), (150, 300))
STAGE = re.compile(r'(\d\d+) (\d+) (.+?) (TempBuffer 57 2560x1440|ReDefinition_TufxAfterFsr) ')


def stages(folder, tag):
    out = []
    for line in open(os.path.join(folder, tag + '.txt')):
        m = STAGE.match(line)
        if m:
            out.append((int(m.group(1)), m.group(3)))
    return out


def colour(folder, tag, index):
    data = open(os.path.join(folder, '%s_%02d.dds' % (tag, index)), 'rb').read()
    # DDS with the DX10 header: 148 bytes, then RGBA16F.
    return np.frombuffer(data[148:], dtype=np.float16).reshape(HEIGHT, WIDTH, 4)[..., :3].astype(np.float32)


def depth(folder, tag):
    data = open(os.path.join(folder, tag + '_depth.dds'), 'rb').read()
    # D32_FLOAT_S8X24: the depth, then the stencil in a second 32-bit word.
    return np.frombuffer(data[148:], dtype=np.float32).reshape(HEIGHT, WIDTH, 2)[..., 0]


def pose_shift(folder):
    a = np.asarray(Image.open(os.path.join(folder, 'with_final.png')).convert('L')).astype(np.float32)
    b = np.asarray(Image.open(os.path.join(folder, 'without_final.png')).convert('L')).astype(np.float32)
    x0, x1, y0, y1 = 1500, 2100, 600, 1250
    region = a[y0:y1, x0:x1]
    return min(((sx, sy) for sx in range(-40, 41) for sy in range(-8, 9)),
               key=lambda s: np.abs(region - b[y0 + s[1]:y1 + s[1], x0 + s[0]:x1 + s[0]]).mean())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('folder')
    parser.add_argument('--stat', choices=('p95', 'median'), default='p95')
    args = parser.parse_args()
    folder = args.folder

    print('pose shift between the finished frames (px):', pose_shift(folder))
    with_depth, without_depth = depth(folder, 'with'), depth(folder, 'without')
    vessel = with_depth > without_depth + 1e-6
    sky = (with_depth < 1e-7) & (without_depth < 1e-7)
    distance = ndimage.distance_transform_edt(~vessel)
    masks = [sky & (distance >= lo) & (distance < hi) for lo, hi in BANDS]

    with_stages, without_stages = stages(folder, 'with'), stages(folder, 'without')
    first = lambda s: max(i for i, (_, name) in enumerate(s) if 'DeferredShading' in name)
    pairs, j = [], first(without_stages)
    for stage in with_stages[first(with_stages):]:
        k = next((k for k in range(j, len(without_stages)) if without_stages[k][1] == stage[1]), None)
        if k is not None:
            pairs.append((stage, without_stages[k]))
            j = k + 1

    print('%-46s' % ('stage (%s of |with - without| / without)' % args.stat),
          ' '.join('%8s' % ('%d-%d' % b) for b in BANDS))
    for (wi, name), (oi, _) in pairs:
        a, b = colour(folder, 'with', wi), colour(folder, 'without', oi)
        relative = np.abs(a - b).mean(2) / (np.abs(b).mean(2) + 1e-3)
        stat = (lambda v: np.percentile(v, 95)) if args.stat == 'p95' else np.median
        print('%-46s' % ('%02d/%02d %s' % (wi, oi, name[:38])),
              ' '.join('%8.4f' % stat(relative[m]) for m in masks))


if __name__ == '__main__':
    main()
