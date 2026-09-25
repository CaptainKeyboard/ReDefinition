"""Writes a synthetic input dump (the format of FrameGenerationDump.cpp) for the
harness replay: fine grain like a runway, its left half moving right by <speed>
pixels a frame with exact motion vectors, its right half still, a still camera and
a flat depth. Frame generation's output for inputs known to be right.

Usage: python tools/make_fg_test_dump.py <folder> <speed> [--frames 8]
"""

import argparse
import os

import numpy as np

W, H = 2560, 1440


def grain(width, height, seed):
    rng = np.random.default_rng(seed)
    g = rng.normal(0, 1, (height, width)).astype(np.float32)
    # Softened a little, as a rendered texture is, then to the runway's grey range.
    k = np.array([0.25, 0.5, 0.25], dtype=np.float32)
    g = np.apply_along_axis(lambda r: np.convolve(r, k, "same"), 1, g)
    g = np.apply_along_axis(lambda c: np.convolve(c, k, "same"), 0, g)
    return np.clip(110 + g * 40, 0, 255).astype(np.uint8)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("folder")
    parser.add_argument("speed", type=float)
    parser.add_argument("--frames", type=int, default=8)
    parser.add_argument("--jitter", type=float, default=0.0,
                        help="each frame's content sampled this many pixels off at random, as jittered rendering does")
    parser.add_argument("--noise", type=float, default=0.0, help="independent noise per frame, grey levels")
    parser.add_argument("--perspective", action="store_true",
                        help="speed grows from 0 at the top to <speed> at the bottom, as the ground's does")
    args = parser.parse_args()
    os.makedirs(args.folder, exist_ok=True)

    moving = grain(W + int(args.speed) * args.frames + 8, H, 1)
    still = grain(W, H, 2)
    near, far, fov = 0.1, 1000.0, 1.0472
    y_scale = 1 / np.tan(fov / 2)
    x_scale = y_scale * H / W
    c = -near / (far - near)
    d = near * far / (far - near)
    view_to_clip = [x_scale, 0, 0, 0, 0, y_scale, 0, 0, 0, 0, c, 1, 0, 0, d, 0]
    clip_to_view = [1 / x_scale, 0, 0, 0, 0, 1 / y_scale, 0, 0, 0, 0, 0, 1 / d, 0, 0, 1, -c / d]
    identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
    fmt = lambda m: " ".join(f"{v:.9g}" for v in m)

    lines = ["# synthetic: grain moving right by %g px a frame on the left half" % args.speed]
    depth = np.full((H, W), 0.01, dtype=np.float32)
    motion = np.zeros((H, W, 2), dtype=np.float16)
    rows = np.arange(H, dtype=np.float32)
    row_speed = args.speed * rows / (H - 1) if args.perspective else np.full(H, args.speed, np.float32)
    # Whole even pixels, the same for each pair of rows: the replay halves the size,
    # and a pair of rows moving apart would not be one moving pixel there.
    row_speed = np.round(row_speed[(np.arange(H) // 2) * 2] / 2) * 2
    motion[:, : W // 2, 0] = (row_speed / W)[:, None]   # previous = uv - speed / W
    for n in range(args.frames):
        left = np.empty((H, W // 2), dtype=np.uint8)
        for y in range(H):
            offset = int(row_speed[y] * (args.frames - n))
            left[y] = moving[y, offset:offset + W // 2]
        grey = np.concatenate([left, still[:, W // 2:]], axis=1).astype(np.float32)
        rng = np.random.default_rng(100 + n)
        if args.jitter > 0:
            jx, jy = rng.uniform(-args.jitter, args.jitter, 2)
            fx, fy = jx % 1.0, jy % 1.0
            shifted = np.roll(np.roll(grey, int(np.floor(jy)), 0), int(np.floor(jx)), 1)
            right = np.roll(shifted, 1, 1)
            down = np.roll(shifted, 1, 0)
            both = np.roll(right, 1, 0)
            grey = (shifted * (1 - fx) + right * fx) * (1 - fy) + (down * (1 - fx) + both * fx) * fy
        if args.noise > 0:
            grey = grey + rng.normal(0, args.noise, grey.shape)
        grey = np.clip(grey, 0, 255).astype(np.uint8)
        rgba = np.stack([grey, grey, grey, np.full_like(grey, 255)], axis=2)
        rgba.tofile(os.path.join(args.folder, f"colour-{n:02d}.bin"))
        depth.tofile(os.path.join(args.folder, f"depth-{n:02d}.bin"))
        motion.tofile(os.path.join(args.folder, f"motion-{n:02d}.bin"))
        lines.append(f"frame {n} packetIndex {n} render {W}x{H} reset 0 jitter 0 0 mvScale -{W} -{H} "
                     f"near {near} far {far} fov {fov} dtMs 16.7 flipped 1")
        lines.append("position 0 0 0 up 0 1 0 right 1 0 0 forward 0 0 1")
        lines.append("viewToClip " + fmt(view_to_clip))
        lines.append("clipToView " + fmt(clip_to_view))
        lines.append("clipToPrevClip " + fmt(identity))
        lines.append("prevClipToClip " + fmt(identity))
        lines.append(f"texture colour {W}x{H} format 28 bytesPerTexel 4")
        lines.append(f"texture depth {W}x{H} format 41 bytesPerTexel 4")
        lines.append(f"texture motion {W}x{H} format 34 bytesPerTexel 4")
    open(os.path.join(args.folder, "frames.txt"), "w", encoding="utf-8").write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
