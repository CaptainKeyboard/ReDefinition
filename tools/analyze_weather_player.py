"""Measures the weather test player (tests/WeatherPlayer, tools/build_weather_player.py) and the game's stored storm-cell frames
with the same metrics, for the spike of the atmosphere plan (.local/plans/seasons-weather-master-plan.md, section 3.5).

Sub-commands:

  stills <folder> [--reference <shots folder>]
      The four views of the player's stills run (<view>_clear/_curtain/_slab/_both) and, per view, the same figures of the stored
      frames of the game's EVE cell (cell-clear/far/mid/inside.png of a run in .local/work/seasons-test/run-cell*/shots). Metrics
      per view, in the sky above the horizon (at 25 km only the rows the cell reaches and the columns of its box):
        luma_mean      mean luma (the coded values, as in the stored screenshots)
        luma_ratio     mean luma of the frame with the cell over that of the clear frame of the same pose
        base_darkness  1 - the same ratio over the lowest 35 % of the band (the cloud base; at 25 km the whole band)
        contrast_gain  local contrast (RMS of the frame minus its blur, over the mean) over that of the clear frame
        diff_vs_clear  mean absolute difference to the clear frame over the clear frame's mean (readability against overcast)
        edge_contrast  at 25 km: the mean outside the cell's box minus the mean inside, over the outside, minus the same of the
                       clear frame (how well the cell's edge shows)
  sheet <folder> <out.png> [--reference <shots folder>]
      A contact sheet: three views, own slab and curtain left, the stored EVE cell right.
  turn <folder> [<folder> ...]
      The player's camera turns through the upscalers (frames at the display size, references at twice that, averaged down).
      In the sky band of the 25 km view: ghost_move (mean luma error against the reference over the moving frames), ghost_still
      (the same over the settled frames), trail_px (share of the band's pixels with an error above 0.04 in the moving frames),
      shimmer (mean absolute difference of two consecutive settled frames), and the share of the pixels the analytic motion and
      depth replaced and the sign agreement of that motion with the sky's own.
  gpu <log or folder> ...
      The WEATHERPLAYER metric lines of the player's logs as a table, with the gate G3 (the slab's passes, 1.5 ms).
"""

import argparse
import glob
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WIDTH, HEIGHT = 1280, 720
# The horizon's row at the displays of both: the camera tilts down 4.95 degrees at a 60 degree field of view (the stored shots'
# horizon is at row 612 of 1440), and the slab view looks level.
HORIZON = {'far': 306, 'mid': 306, 'inside': 306, 'slabin': 360}
# The cell's box at 25 km in columns: the player's (a cell of 6 km radius dead ahead) and the stored frame's (its log's region
# 1018..1632 of 2560 columns).
BOX = {'player': (490, 790), 'stored': (509, 816)}
STORED = {'far': 'cell-far.png', 'mid': 'cell-mid.png', 'inside': 'cell-inside.png'}
LOW_SHARE = 0.35
BLUR_SIGMA = 6.0


def luma(rgb):
    """Rec. 709 luma of coded values."""
    return 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]


def load_player(folder, name):
    """A player frame as a (720, 1280, 3) float array, top row first, or None. Read back bottom-up, RGBA half."""
    path = os.path.join(folder, name + '_colour.bin')
    if not os.path.exists(path):
        return None
    data = np.fromfile(path, dtype=np.float16).astype(np.float32)
    size = data.size // 4
    height = int(round(math.sqrt(size * 9 / 16)))
    width = size // height
    return np.flipud(data.reshape(height, width, 4)[..., :3])


def load_stored(folder, name):
    """A stored frame (PNG, 2560 x 1440) averaged down to 1280 x 720 as floats 0 to 1."""
    image = Image.open(os.path.join(folder, name)).convert('RGB')
    a = np.asarray(image).astype(np.float32) / 255.0
    h, w = a.shape[:2]
    return a.reshape(h // 2, 2, w // 2, 2, 3).mean(axis=(1, 3)) if (w, h) == (2560, 1440) else a


def blur(a, sigma):
    """Separable Gaussian blur of a 2-D array."""
    radius = int(sigma * 3)
    k = np.exp(-0.5 * (np.arange(-radius, radius + 1) / sigma) ** 2)
    k /= k.sum()
    p = np.pad(a, radius, mode='edge')
    p = np.apply_along_axis(lambda r: np.convolve(r, k, mode='valid'), 1, p)
    p = np.apply_along_axis(lambda c: np.convolve(c, k, mode='valid'), 0, p)
    return p


def region(view, source):
    """Rows and columns of the sky band for the view: at 25 km the 70 rows above the horizon in the cell's box, else everything
    above the horizon."""
    h0 = HORIZON[view]
    if view == 'far':
        x0, x1 = BOX[source]
        return slice(h0 - 48, h0 - 6), slice(x0, x1)
    return slice(0, h0 - 6), slice(0, WIDTH)


def local_contrast(y):
    return float(np.sqrt(np.mean((y - blur(y, BLUR_SIGMA)) ** 2)) / max(float(y.mean()), 1e-6))


def metrics(view, cell, clear, source):
    """The figures of one view: cell and clear are frames (720, 1280, 3)."""
    rows, cols = region(view, source)
    c, k = luma(cell)[rows, cols], luma(clear)[rows, cols]
    out = {'luma_mean': float(c.mean()), 'luma_ratio': float(c.mean() / k.mean())}
    low = slice(int(c.shape[0] * (1 - LOW_SHARE)) if view != 'far' else 0, c.shape[0])
    out['base_darkness'] = float(1.0 - c[low].mean() / k[low].mean())
    out['contrast_gain'] = local_contrast(c)  # absolute: the clear overcast has almost none, a ratio would explode
    out['diff_vs_clear'] = float(np.abs(c - k).mean() / k.mean())
    if view == 'far':
        x0, x1 = BOX[source]
        w = x1 - x0

        def edge(frame):
            band = luma(frame)[rows, :]
            inside = band[:, x0 + int(0.25 * w):x1 - int(0.25 * w)].mean()
            outside = np.concatenate([band[:, x0 - int(0.5 * w):x0 - int(0.1 * w)],
                                      band[:, x1 + int(0.1 * w):x1 + int(0.5 * w)]], axis=1).mean()
            return (outside - inside) / outside
        out['edge_contrast'] = float(edge(cell) - edge(clear))
    return out


NAMES = ('luma_mean', 'luma_ratio', 'base_darkness', 'contrast_gain', 'diff_vs_clear', 'edge_contrast')


def stills(args):
    """Print the table of the player's views against the stored cell."""
    rows = []
    clear_stored = load_stored(args.reference, 'cell-clear.png')
    for view in ('far', 'mid', 'inside', 'slabin'):
        clear = load_player(args.folder, view + '_clear')
        if clear is None:
            continue
        for variant in ('both', 'slab', 'curtain'):
            frame = load_player(args.folder, '%s_%s' % (view, variant))
            if frame is not None:
                rows.append((view, 'own ' + variant, metrics(view, frame, clear, 'player')))
        if view in STORED:
            rows.append((view, 'EVE cell', metrics(view, load_stored(args.reference, STORED[view]), clear_stored, 'stored')))
        rows.append((view, 'clear', metrics(view, clear, clear, 'player')))
    print('%-8s %-14s' % ('view', 'source') + ''.join('%15s' % n for n in NAMES))
    for view, source, m in rows:
        print('%-8s %-14s' % (view, source) + ''.join('%15s' % ('%.4f' % m[n] if n in m else '-') for n in NAMES))
    return rows


def sheet(args):
    """The contact sheet of the three views: own cell (slab and curtain) left, the stored EVE cell right."""
    cell = (640, 360)
    sheet_image = Image.new('RGB', (cell[0] * 2 + 3 * 10, cell[1] * 3 + 4 * 10 + 3 * 18), (24, 24, 24))
    draw = ImageDraw.Draw(sheet_image)
    for i, view in enumerate(('far', 'mid', 'inside')):
        own = load_player(args.folder, view + '_both')
        eve = load_stored(args.reference, STORED[view])
        y = 10 + i * (cell[1] + 28)
        for j, (label, frame) in enumerate((('own slab + curtain, ' + view, own), ('EVE cell as steered now, ' + view, eve))):
            x = 10 + j * (cell[0] + 10)
            draw.text((x, y), label, fill=(230, 230, 230))
            image = Image.fromarray((np.clip(frame, 0, 1) * 255).astype(np.uint8)).resize(cell, Image.LANCZOS)
            sheet_image.paste(image, (x, y + 16))
    sheet_image.save(args.out)
    print('wrote', args.out)


def read_meta(folder):
    frames = {}
    for line in open(os.path.join(folder, 'meta.txt'), encoding='utf-8'):
        if line.startswith('#') or not line.strip():
            continue
        parts = line.split()
        frames[parts[0]] = (int(parts[1]), int(parts[2]))
    return frames


def read_half(folder, name, what, w, h, channels):
    path = os.path.join(folder, '%s_%s.bin' % (name, what))
    if not os.path.exists(path):
        return None
    return np.flipud(np.fromfile(path, dtype=np.float16).astype(np.float32).reshape(h, w, channels))


def turn(args):
    """The sky-band figures of each turn folder."""
    print('%-34s %8s %8s %8s %8s %9s %9s' % ('run', 'ghost_mv', 'ghost_st', 'trail_px', 'shimmer', 'replaced', 'mv_signok'))
    results = {}
    for folder in args.folders:
        label = os.path.basename(os.path.normpath(folder))
        if not os.path.exists(os.path.join(folder, 'meta.txt')):
            print('%-34s no meta.txt' % label)
            continue
        meta = read_meta(folder)
        path = [float(v) for v in open(os.path.join(folder, 'path.txt'), encoding='utf-8').read().split()]
        names = sorted(n for n in meta if n.startswith('turn_'))
        w, h = meta[names[0]]
        sx = w / WIDTH
        h0 = int(HORIZON['far'] * sx)
        rows = slice(int((HORIZON['far'] - 48) * sx), h0 - 4)
        focal = 623.5 * sx

        def columns(i):
            """The cell's columns in turn frame i: its box follows the camera's yaw."""
            centre = w / 2 - focal * math.tan(math.radians(path[i]))
            return slice(max(0, int(centre - 190 * sx)), min(w, int(centre + 190 * sx)))
        last_move = max(i for i in range(1, len(path)) if path[i] != path[i - 1])
        moving_err, still_err, trail, lumas = [], [], [], {}
        replaced, signok = [], []
        for i, n in enumerate(names):
            colour = read_half(folder, n, 'colour', w, h, 4)
            if colour is None:
                continue
            y = luma(colour[..., :3])
            lumas[i] = y
            ref = 'ref_' + n[len('turn_'):]
            if ref in meta:
                rw, rh = meta[ref]
                r = read_half(folder, ref, 'colour', rw, rh, 4)
                if r is not None:
                    r = r[..., :3].reshape(h, rh // h, w, rw // w, 3).mean(axis=(1, 3))
                    err = np.abs(y - luma(r))[rows, columns(i)]
                    if i > 6 and i <= last_move:
                        moving_err.append(err.mean())
                        trail.append(float((err > 0.04).mean()))
                    elif i > last_move + 10:
                        still_err.append(err.mean())
            m = None
            if 6 < i <= last_move:
                m = read_half(folder, n, 'motion', *_render_size(folder, n, w, h), 2)
                raw = read_half(folder, n, 'motionraw', *_render_size(folder, n, w, h), 2)
                if m is not None and raw is not None:
                    active = (np.abs(m - raw).sum(axis=2) > 1e-6)
                    replaced.append(float(active.mean()))
                    if active.any():
                        sky = ~active
                        sky_mean = raw[sky][:, 0].mean() if sky.any() else 0.0
                        signok.append(float(np.sign(m[active][:, 0].mean()) == np.sign(sky_mean) and np.sign(m[active][:, 1].mean()) == np.sign(raw[sky][:, 1].mean())))
        still = [k for k in lumas if k > last_move + 10]
        shimmer = float(np.mean([np.abs(lumas[still[j]] - lumas[still[j - 1]])[rows, columns(still[j])].mean() for j in range(1, len(still))])) \
            if len(still) > 1 else float('nan')
        results[label] = dict(ghost_move=float(np.mean(moving_err)) if moving_err else float('nan'),
                              ghost_still=float(np.mean(still_err)) if still_err else float('nan'),
                              trail_px=float(np.mean(trail)) if trail else float('nan'), shimmer=shimmer,
                              replaced=float(np.mean(replaced)) if replaced else float('nan'),
                              mv_signok=float(np.mean(signok)) if signok else float('nan'))
        r = results[label]
        print('%-34s %8.4f %8.4f %8.4f %8.4f %9.4f %9.2f' % (label, r['ghost_move'], r['ghost_still'], r['trail_px'], r['shimmer'],
                                                           r['replaced'], r['mv_signok']))
    return results


def _render_size(folder, name, w, h):
    """The size of the depth and motion buffers: the file's size tells (the aspect is the display's)."""
    size = os.path.getsize(os.path.join(folder, name + '_motion.bin')) // 4
    rh = int(round(math.sqrt(size * h / w)))
    return size // rh, rh


def gpu(args):
    """The metric lines of the player's logs as a table, and the gate G3 over the slab totals."""
    lines = []
    for item in args.paths:
        for path in (glob.glob(os.path.join(item, '*.log')) if os.path.isdir(item) else [item]):
            for line in open(path, encoding='utf-8', errors='replace'):
                if line.startswith('WEATHERPLAYER {'):
                    lines.append((os.path.basename(path), line[len('WEATHERPLAYER {'):].rstrip().rstrip('}')))
    worst = {}
    for log, text in lines:
        name, value, limit, ok = text.split(',')
        print('%-14s %-62s %8s %6s %s' % (log, name, value, limit, ok))
        if name.startswith('gpu_ms.slab_total.') and '.s32.r0.5.' in name:
            worst[name] = float(value)
    return worst


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    default_reference = os.path.join(ROOT, '.local', 'work', 'seasons-test', 'run-cell13', 'shots')
    p = sub.add_parser('stills')
    p.add_argument('folder')
    p.add_argument('--reference', default=default_reference)
    p = sub.add_parser('sheet')
    p.add_argument('folder')
    p.add_argument('out')
    p.add_argument('--reference', default=default_reference)
    p = sub.add_parser('turn')
    p.add_argument('folders', nargs='+')
    p = sub.add_parser('gpu')
    p.add_argument('paths', nargs='+')
    p = sub.add_parser('reference')
    p.add_argument('--reference', default=default_reference)
    args = parser.parse_args()
    if args.command == 'reference':
        # The stored cell's own figures against the stored clear frame, without the player.
        clear = load_stored(args.reference, 'cell-clear.png')
        print('%-8s' % 'view' + ''.join('%15s' % n for n in NAMES))
        for view, name in STORED.items():
            m = metrics(view, load_stored(args.reference, name), clear, 'stored')
            print('%-8s' % view + ''.join('%15s' % ('%.4f' % m[n] if n in m else '-') for n in NAMES))
        return
    {'stills': stills, 'sheet': sheet, 'turn': turn, 'gpu': gpu}[args.command](args)


if __name__ == '__main__':
    main()
