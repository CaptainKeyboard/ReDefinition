"""Measures the test player's camera turn through each upscaler (RtTurnRunner -upscale).

    python tools/analyze_upscale_turn.py <folder> [<folder> ...]

Each folder is one run: the upscaled frames at the display size, their depth at the render
size, and references of each pose rendered at twice the display size without jitter,
averaged down to it. Per run:

* error: the mean difference of the tone-mapped luminance against the reference, over
  what came into view in the frame (behind a silhouette or past the frame's edge), within
  the last four frames, and over the rest -- a ghost or smear shows in the first two,
  blur and aliasing in the last;
* settled: the same over the still frames at the end, after ten frames to settle;
* flicker: how much a still frame differs from the one before, over the still frames at
  the end; the reference does not change at all there.

The masks come from analyze_rt_turn.py, from the render size's depth, scaled to the
display size.
"""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_rt_turn as turn  # noqa: E402


def load_colour(folder, name, w, h):
    path = os.path.join(folder, name + '_colour.bin')
    return np.fromfile(path, dtype=np.float16).reshape(h, w, 4).astype(np.float32)[..., :3]


def load_depth(folder, name, aspect):
    path = os.path.join(folder, name + '_depth.bin')
    data = np.fromfile(path, dtype=np.float32)
    h = int(round((data.size / aspect) ** 0.5))
    w = data.size // h
    return data.reshape(h, w)


def scale_mask(mask, w, h):
    ys = (np.arange(h) * mask.shape[0] // h)
    xs = (np.arange(w) * mask.shape[1] // w)
    return mask[ys][:, xs]


def analyse(folder):
    frames = turn.read_meta(folder)
    names = sorted(n for n in frames if n.startswith('turn_'))
    path = [float(v) for v in open(os.path.join(folder, 'path.txt'), encoding='utf-8').read().split()]
    first = names[0]
    w, h = frames[first]['width'], frames[first]['height']
    aspect = w / h
    depths = {n: load_depth(folder, n, aspect) for n in names}
    rh, rw = depths[first].shape
    # The camera's meta at the render size, for the masks.
    small = {n: dict(frames[n], width=rw, height=rh) for n in names}
    lum = {}
    for n in names:
        lum[n] = turn.luminance(load_colour(folder, n, w, h))
    rows = []
    for i, n in enumerate(names):
        ref = 'ref_' + n[len('turn_'):]
        if ref not in frames or i == 0:
            continue
        r = frames[ref]
        reference = load_colour(folder, ref, r['width'], r['height'])
        reference = reference.reshape(h, r['height'] // h, w, r['width'] // w, 3).mean(axis=(1, 3))
        error = np.abs(lum[n] - turn.luminance(reference))
        new = turn.came_into_view(small[n], depths[n], small[names[i - 1]], depths[names[i - 1]], True)
        recent = new.copy()
        for k in range(2, turn.LOOK_BACK + 1):
            if i - k >= 0:
                recent |= turn.came_into_view(small[n], depths[n], small[names[i - k]], depths[names[i - k]], True)
        new = scale_mask(new, w, h)
        recent = scale_mask(recent, w, h)
        rows.append((i, error[new].mean() if new.any() else 0.0, new.mean(), error[recent].mean() if recent.any() else 0.0,
                     error[~recent].mean(), error.mean()))
    # The still frames at the end: from the last move on, ten frames to settle.
    last_move = max(i for i in range(1, len(path)) if path[i] != path[i - 1])
    still = [n for i, n in enumerate(names) if i > last_move + 10]
    flicker = np.mean([np.abs(lum[still[k]] - lum[still[k - 1]]).mean() for k in range(1, len(still))]) if len(still) > 1 else 0.0
    settled = np.mean([r[5] for r in rows if r[0] > last_move + 10]) if rows else 0.0
    moving = [r for r in rows if r[2] > 0.01]
    return {
        'render': '%dx%d' % (rw, rh), 'display': '%dx%d' % (w, h),
        'new': np.mean([r[1] for r in moving]) if moving else 0.0,
        'recent': np.mean([r[3] for r in moving]) if moving else 0.0,
        'rest': np.mean([r[4] for r in moving]) if moving else 0.0,
        'settled': settled, 'flicker': flicker, 'rows': rows,
    }


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    print('%-28s %-9s %-9s %8s %8s %8s %8s %8s' % ('run', 'render', 'display', 'new', 'recent', 'rest', 'settled',
                                                    'flicker'))
    for folder in sys.argv[1:]:
        if not os.path.exists(os.path.join(folder, 'meta.txt')):
            print('%s: no meta.txt' % folder)
            continue
        m = analyse(folder)
        print('%-28s %-9s %-9s %8.4f %8.4f %8.4f %8.4f %8.4f' % (os.path.basename(os.path.normpath(folder)), m['render'],
                                                                 m['display'], m['new'], m['recent'], m['rest'],
                                                                 m['settled'], m['flicker']))


if __name__ == '__main__':
    main()
