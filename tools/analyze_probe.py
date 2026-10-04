"""Measures what the vessel does to the sky around it in a scene probe
(src/Probe/SceneProbe.cs).

    python tools/analyze_probe.py <ReDefinitionCaptures\\<time>-probe> [station]

Each variant -- baseline, then each other mod's component or command buffer switched
off -- ran the camera path twice, with the vessel (captures 00, 01, ...) and with its
renderers hidden (h00, h01, ...); meta.txt names each capture's phase of the path
(steady, moving left, after it, and so on). On the sky beside the vessel the two
differ only by what the vessel does to it: a halo, a trail. Given per capture, in the
image the scene camera holds at the station (opaque, alpha, effects, after or screen,
the finished frame; default screen): the 95th percentile of the difference 1-4, 4-8,
8-16 and 16-32 px from the vessel's silhouette, and 40-80 px as the floor, then each
band over the floor. 1-4 px holds the edge's own antialiasing. Sky is where the image
after the scene camera's lighting is still empty (deferred shading) and the vessel's
mask is empty.

The vessel's mask is drawn at the render size; the screen station is cut at the
display size around the same point, so it lines up with the mask only where both
sizes are one (NativeAA, or no upscaler).

The two passes lie seconds apart: run the probe with -redefinitionProbeFreeze, game
time stopped, or clouds and water drifting between them fill every band.
"""

import os
import sys

import numpy as np
from scipy import ndimage


def read(prefix, name, dtype, channels, crop):
    path = prefix + '-' + name + '.bin'
    if not os.path.exists(path):
        return None
    a = np.fromfile(path, dtype=dtype)
    return a.reshape(crop, crop, channels) if channels > 1 else a.reshape(crop, crop)


def grey(prefix, name, crop):
    if name == 'screen':
        # The finished frame, 8 bits, encoded as the screen shows it.
        a = read(prefix, name, np.uint8, 4, crop)
        return None if a is None else a.astype(np.float32)[..., :3].mean(2) / 255.0
    a = read(prefix, name, np.float16, 4, crop)
    return None if a is None else a.astype(np.float32)[..., :3].mean(2)


def edge_contrast(image, mask):
    """How much more the brightness changes across the mask's edge than elsewhere: the
    vessel drawn in the image where the mask has it. Under 1.5 the station shows no
    vessel there and says nothing."""
    edge = mask ^ ndimage.binary_erosion(mask)
    g = np.hypot(*np.gradient(image))
    return g[edge].mean() / max(g.mean(), 1e-9)


def oriented(image, mask):
    """The image the right way up against the mask: the one whose brightness changes
    most across the mask's edge."""
    edge = mask ^ ndimage.binary_erosion(mask)
    if not edge.any():
        return image
    def contrast(img):
        return np.hypot(*np.gradient(img))[edge].mean()
    flipped = image[::-1]
    return flipped if contrast(flipped) > contrast(image) else image


BANDS = ((1, 4), (4, 8), (8, 16), (16, 32), (40, 80))


def bands(diff, vessel, sky):
    """The 95th percentile of the difference in each band of distance from the
    vessel: a halo is a band above the floor (the last band), on some part of the
    outline if not all of it -- which a mean would average away."""
    dist = ndimage.distance_transform_edt(~vessel)
    out = []
    for lo, hi in BANDS:
        sel = sky & (dist >= lo) & (dist < hi)
        out.append(np.percentile(diff[sel], 95) if sel.sum() > 50 else float('nan'))
    return out


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    folder = sys.argv[1]
    station = sys.argv[2] if len(sys.argv) > 2 else 'screen'
    crop = 768
    lines = open(os.path.join(folder, 'meta.txt'), encoding='utf-8').read().splitlines()
    for line in lines:
        if line.startswith('#') and ', crop ' in line:
            crop = int(line.rsplit(', crop ', 1)[1].split()[0])
    captures = []
    for line in lines:
        p = line.split()
        if p and not line.startswith('#') and not p[2].startswith('h'):
            captures.append((p[0], p[2], p[5] if len(p) > 5 else ''))
    print('station', station, '-- 95th percentile of |image with vessel - image without| on the sky,'
          ' by distance from the vessel; then each band over the floor')
    print('variant'.ljust(20), 'cap phase       ', ' '.join(('%d-%d' % b).rjust(7) for b in BANDS),
          '   ' + ' '.join(('%d-%d' % b).rjust(5) for b in BANDS[:-1]))
    for variant, n, phase in captures:
        with_v = os.path.join(folder, variant, n)
        without = os.path.join(folder, variant, 'h' + n)
        mask = read(with_v, 'mask', np.uint8, 1, crop)
        a = grey(with_v, station, crop)
        b = grey(without, station, crop)
        opaque = grey(with_v, 'opaque', crop)
        if mask is None or a is None or b is None or opaque is None:
            continue
        vessel = mask > 127
        # Both passes of a station come out of the same buffer: one orientation.
        if oriented(a, vessel) is not a:
            a, b = a[::-1], b[::-1]
        if oriented(opaque, vessel) is not opaque:
            opaque = opaque[::-1]
        contrast = edge_contrast(a, vessel)
        if contrast < 1.5:
            print(variant.ljust(20), n.rjust(3), phase.ljust(12), 'invalid: no vessel in the image (edge contrast %.2f)' % contrast)
            continue
        sky = (opaque < 1e-4) & ~vessel
        values = bands(np.abs(a - b), vessel, sky)
        floor = values[-1]
        print(variant.ljust(20), n.rjust(3), phase.ljust(12), ' '.join('%7.4f' % v for v in values),
              '   ' + ' '.join('%5.1f' % (v / floor if floor > 0 else float('nan')) for v in values[:-1]))


if __name__ == '__main__':
    main()
