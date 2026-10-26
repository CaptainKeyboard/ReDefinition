"""Frame generation's frames against rendered ones, with NVIDIA's FLIP.

Takes the two recordings of tools/flip_probe.ps1: the reference (no frame
generation, the camera sweep at half step) and the run with frame generation
(full step). Every frame of the second is matched to the reference frame showing
the same camera pose. The match is the one closest in a cheap downscaled comparison,
then the best of the closest few by FLIP. The frame's FLIP error against that one is
taken.

In the reference a rendered frame stands at every half step. Frames of the
frame-generation run that match an even reference frame stand between two of the
game's frames (generated), odd ones on a game frame (rendered). The sweep's
first step is reference frame 1. Both groups are reported apart; what frame
generation adds is the difference between them. Scene animation (clouds, water)
differs between the two runs and sets the floor both groups share.

Writes flip.txt (one line a frame) and the FLIP error maps of the five worst
frames of each group into --out (default: <generated folder>/flip).

Usage: python tools/analyze_flip.py <reference folder> <generated folder> [--out <folder>]
    [--ppd 67] [--candidates 3]
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image
import flip_evaluator as flip


def load_folder(folder):
    """The recording's PNG frames as RGB arrays (0..1), repeats dropped, and per frame its name and game frame
    from frames.txt (-1 where it has none)."""
    names = sorted(n for n in os.listdir(folder) if n.endswith('.png'))
    frames = {}
    table = os.path.join(folder, 'frames.txt')
    if os.path.exists(table):
        with open(table) as f:
            next(f)
            for line in f:
                parts = line.split()
                if len(parts) >= 5:
                    frames[parts[0]] = int(parts[4])
    images, kept = [], []
    previous = None
    for n in names:
        a = np.asarray(Image.open(os.path.join(folder, n)).convert('RGB'), dtype=np.float32) / 255.0
        # The desktop recording repeats a frame where the monitor showed it again.
        if previous is not None and np.abs(a - previous).mean() < 1e-4:
            continue
        previous = a
        images.append(a)
        kept.append((n[:-4], frames.get(n[:-4], -1)))
    return images, kept


def small(a):
    """The image averaged over 8x8 blocks, for the cheap first match against the reference."""
    h, w = a.shape[0] // 8 * 8, a.shape[1] // 8 * 8
    return a[:h, :w].reshape(h // 8, 8, w // 8, 8, 3).mean(axis=(1, 3))


def main():
    """Match every generated frame to its reference pose, write flip.txt and the worst error maps, print both groups."""
    p = argparse.ArgumentParser()
    p.add_argument('reference')
    p.add_argument('generated')
    p.add_argument('--out')
    p.add_argument('--ppd', type=float, default=67.0)
    p.add_argument('--candidates', type=int, default=3)
    args = p.parse_args()
    out = args.out or os.path.join(args.generated, 'flip')
    os.makedirs(out, exist_ok=True)

    ref, ref_names = load_folder(args.reference)
    gen, gen_names = load_folder(args.generated)
    if not ref or not gen:
        sys.exit('no frames')
    ref_small = np.stack([small(a) for a in ref])
    params = {'ppd': args.ppd}

    rows = []
    for k, a in enumerate(gen):
        d = np.abs(ref_small - small(a)[None]).mean(axis=(1, 2, 3))
        best = None
        for j in np.argsort(d)[:args.candidates]:
            error_map, mean, _ = flip.evaluate(ref[j], a, 'LDR', applyMagma=False, parameters=params)
            if best is None or mean < best[1]:
                best = (int(j), float(mean), error_map)
        j, mean, error_map = best
        e = np.asarray(error_map).reshape(a.shape[0], a.shape[1], -1)[..., 0]
        kind = 'generated' if j % 2 == 0 else 'rendered'
        rows.append((k, gen_names[k][0], gen_names[k][1], j, kind, mean,
                     float(np.percentile(e, 99)), float((e > 0.5).mean()), e))

    with open(os.path.join(out, 'flip.txt'), 'w') as f:
        f.write('frame recorded gameFrame referenceFrame kind meanFlip p99Flip fractionAbove0.5\n')
        for r in rows:
            f.write('%d %s %d %d %s %.5f %.5f %.5f\n' % r[:8])

    for kind in ('rendered', 'generated'):
        group = [r for r in rows if r[4] == kind]
        if not group:
            print('%-9s none' % kind)
            continue
        means = np.array([r[5] for r in group])
        above = np.array([r[7] for r in group])
        print('%-9s %3d frames: mean FLIP %.4f (median %.4f, max %.4f), pixels above 0.5: %.3f%% (max %.3f%%)'
              % (kind, len(group), means.mean(), np.median(means), means.max(),
                 100 * above.mean(), 100 * above.max()))
        for r in sorted(group, key=lambda r: -r[5])[:5]:
            e = np.clip(r[8], 0, 1)
            Image.fromarray((e * 255).astype(np.uint8)).save(
                os.path.join(out, '%s-%s-ref%03d.png' % (kind, r[1], r[3])))
    print('per frame: ' + os.path.join(out, 'flip.txt'))


if __name__ == '__main__':
    main()
