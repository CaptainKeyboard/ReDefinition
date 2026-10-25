"""Writes a synthetic input dump (the format of FrameGenerationDump.cpp) for the
harness replay that stands for the vessel on the pad while the camera turns: a sky
with soft cloud structure, depth 0 (nothing drawn, reversed Z), moving right by
<speed> pixels a frame with exact motion vectors, and in front of it, still, a light
bar 60 px wide (the rocket) and a dark pole 6 px wide, both at depth 0.0064 with
zero motion, their edges antialiased over one pixel. Inputs known to be right: what
frame generation draws beside the bar and the pole is its own.

Usage: python tools/make_bar_dump.py <folder> [--speed 30] [--frames 8] [--hard] [--dilate]
"""

import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fg_dump  # noqa: E402

W, H = 2560, 1440


def clouds(width, height, seed):
    """Soft noise of the size given: coarse normal noise on a 16 px grid, interpolated bilinearly."""
    rng = np.random.default_rng(seed)
    g = rng.normal(0, 1, (height // 16 + 2, width // 16 + 2)).astype(np.float32)
    # Large soft blobs: coarse noise scaled up smoothly.
    ys = np.linspace(0, g.shape[0] - 1.001, height)
    xs = np.linspace(0, g.shape[1] - 1.001, width)
    y0, x0 = ys.astype(int), xs.astype(int)
    fy, fx = (ys - y0)[:, None], (xs - x0)[None, :]
    a = g[y0][:, x0]
    b = g[y0][:, x0 + 1]
    c = g[y0 + 1][:, x0]
    d = g[y0 + 1][:, x0 + 1]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def main():
    """Write the frames of the moving sky behind the still bar and pole, and their frames.txt, into the folder."""
    parser = argparse.ArgumentParser()
    parser.add_argument("folder")
    parser.add_argument("--speed", type=int, default=30)
    parser.add_argument("--frames", type=int, default=8)
    parser.add_argument("--hard", action="store_true", help="edges on whole pixels: no pixel mixes bar and sky")
    parser.add_argument("--dilate", action="store_true",
                        help="a pixel the bar or pole covers at all gets their depth and motion, not the sky's")
    args = parser.parse_args()
    os.makedirs(args.folder, exist_ok=True)
    speed = args.speed - args.speed % 2

    span = W + speed * args.frames + 8
    c = clouds(span, H, 3)
    rows = np.linspace(0, 1, H, dtype=np.float32)[:, None]
    sky = np.stack([40 + 50 * rows + 20 * c, 70 + 60 * rows + 20 * c, 140 + 50 * rows + 15 * c], axis=2)

    def coverage(x0, x1):
        # Horizontal coverage of [x0, x1) per pixel column, edges antialiased.
        xs = np.arange(W, dtype=np.float32)
        return np.clip(np.minimum(xs + 1, x1) - np.maximum(xs, x0), 0, 1)

    edge = 0.0 if args.hard else 0.5
    bar = coverage(1200 + edge, 1260 + edge)
    pole = coverage(800 + edge, 806 + edge)
    bar_colour = np.array([200, 195, 185], np.float32)
    pole_colour = np.array([25, 25, 28], np.float32)
    in_front = np.maximum(bar, pole) > (0.0 if args.dilate else 0.5)

    near, far, fov = 0.21, 750000.0, 1.0472
    y_scale = 1 / np.tan(fov / 2)
    x_scale = y_scale * H / W
    view_to_clip = [x_scale, 0, 0, 0, 0, y_scale, 0, 0, 0, 0, 0, 1, 0, 0, near, 0]
    clip_to_view = [1 / x_scale, 0, 0, 0, 0, 1 / y_scale, 0, 0, 0, 0, 0, 1 / near, 0, 0, 1, 0]
    identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]

    depth = np.zeros((H, W), dtype=np.float32)
    depth[:, in_front] = 0.0064
    motion = np.zeros((H, W, 2), dtype=np.float16)
    motion[:, ~in_front, 0] = speed / W   # previous = uv - speed / W: moving right

    lines = ["# synthetic: sky moving right by %d px a frame behind a still bar and pole" % speed]
    for n in range(args.frames):
        offset = speed * (args.frames - n)
        frame = sky[:, offset:offset + W].copy()
        for cov, col in ((bar, bar_colour), (pole, pole_colour)):
            frame = frame * (1 - cov[None, :, None]) + col[None, None, :] * cov[None, :, None]
        grey = np.clip(frame, 0, 255).astype(np.uint8)
        rgba = np.concatenate([grey, np.full((H, W, 1), 255, np.uint8)], axis=2)
        rgba.tofile(os.path.join(args.folder, f"colour-{n:02d}.bin"))
        depth.tofile(os.path.join(args.folder, f"depth-{n:02d}.bin"))
        motion.tofile(os.path.join(args.folder, f"motion-{n:02d}.bin"))
        lines += fg_dump.entry(n, n, (W, H), (-W, -H), near, far, fov, 16.7,
                               [view_to_clip, clip_to_view, identity, identity])
    fg_dump.write_index(args.folder, lines)


if __name__ == "__main__":
    main()
