"""Checks frame generation's inputs from an input dump (FrameGenerationDump.cpp).

For each pair of consecutive frames in <folder> (the "-inputs" folder beside a screen
recording in ReDefinitionCaptures), it runs three checks.

Motion vectors against the colour: the previous frame's colour, fetched where each
pixel's motion vector says it was, compared with this frame's colour. Also with the
vectors scaled (0.5, 1.5, 2) and with no motion: the scale that fits best is what
the vectors really describe.

Motion vectors against the camera: for a pixel of the still world, the motion that
depth and clipToPrevClip give, as frame generation computes camera motion, compared
with the delivered vector. The two have to agree; frame generation gets both.

Measured motion: block matching of the colour per tile, against the mean vector
there.

Pixels are split by linear depth into near (the vessel, closer than --near metres)
and far (ground and scenery); depth 0 (nothing drawn) is counted apart.

Usage: python tools/analyze_fg_inputs.py <folder> [--near 15]
"""

import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fg_dump  # noqa: E402


def load_frames(folder):
    """The dump's frames from its index, with matrices as 4x4 arrays, colour as grey (RGB mean), depth and
    motion (float16 pairs) read from the per-frame .bin files."""
    frames = fg_dump.read_index(folder)
    for f in frames:
        for name in fg_dump.MATRICES:
            if name in f:
                f[name] = np.array(f[name], dtype=np.float64).reshape(4, 4)
    for f in frames:
        n = f["index"]
        tex = f["textures"]
        w, h, fmt = tex["colour"]
        raw = np.fromfile(os.path.join(folder, f"colour-{n:02d}.bin"), dtype=np.uint8).reshape(h, w, 4)
        if fmt in (87, 88, 90, 91):   # BGRA
            raw = raw[:, :, [2, 1, 0, 3]]
        f["colour"] = raw[:, :, :3].astype(np.float32).mean(axis=2)
        w, h, _ = tex["depth"]
        f["depth"] = np.fromfile(os.path.join(folder, f"depth-{n:02d}.bin"), dtype=np.float32).reshape(h, w)
        w, h, _ = tex["motion"]
        f["motion"] = np.fromfile(os.path.join(folder, f"motion-{n:02d}.bin"), dtype=np.float16).reshape(h, w, 2).astype(np.float32)
    return frames


def bilinear(image, x, y):
    """The grey image sampled bilinearly at pixel positions x, y (arrays), clamped to its edges."""
    h, w = image.shape
    x = np.clip(x, 0, w - 1.001)
    y = np.clip(y, 0, h - 1.001)
    x0 = np.floor(x).astype(int)
    y0 = np.floor(y).astype(int)
    fx = x - x0
    fy = y - y0
    a = image[y0, x0] * (1 - fx) + image[y0, x0 + 1] * fx
    b = image[y0 + 1, x0] * (1 - fx) + image[y0 + 1, x0 + 1] * fx
    return a * (1 - fy) + b * fy


def linear_depth(frame, depth):
    """View distance from device depth through the frame's viewToClip; infinity where nothing is drawn (0)."""
    # viewToClip, row vectors: clip.z = c * z + d, clip.w = z.
    m = frame["viewToClip"]
    c, d = m[2, 2], m[3, 2]
    with np.errstate(divide="ignore", invalid="ignore"):
        return np.where(depth > 0, d / (depth - c), np.inf)


def analyse_pair(prev, cur, near_metres, step):
    """Report lines for one frame pair, per depth region (near, far, nothing drawn) on every step-th pixel:
    the colour error with the vectors scaled, the error with camera motion, and vectors against camera motion."""
    cw_, ch_ = cur["colour"].shape[1], cur["colour"].shape[0]
    mh, mw = cur["motion"].shape[:2]
    dh, dw = cur["depth"].shape
    sx, sy = cur["mvScale"]
    # As the proxy hands them to Streamline: mvecScale = (sx / mw, -sy / mh), and
    # previous uv = uv + vector * mvecScale.
    scale_x = sx / mw
    scale_y = -sy / mh

    ys, xs = np.mgrid[step // 2:ch_:step, step // 2:cw_:step]
    xs = xs.ravel().astype(np.float64)
    ys = ys.ravel().astype(np.float64)
    u = (xs + 0.5) / cw_
    v = (ys + 0.5) / ch_
    mx = np.clip((u * mw).astype(int), 0, mw - 1)
    my = np.clip((v * mh).astype(int), 0, mh - 1)
    vec = cur["motion"][my, mx]
    du = vec[:, 0] * scale_x
    dv = vec[:, 1] * scale_y
    dx = np.clip((u * dw).astype(int), 0, dw - 1)
    dy = np.clip((v * dh).astype(int), 0, dh - 1)
    depth = cur["depth"][dy, dx].astype(np.float64)
    lin = linear_depth(cur, depth)

    # Camera motion from depth and clipToPrevClip (row vectors), Direct3D clip space,
    # y up, rows top-down.
    ndc = np.stack([u * 2 - 1, 1 - v * 2, depth, np.ones_like(u)], axis=1)
    prev_clip = ndc @ cur["clipToPrevClip"]
    with np.errstate(divide="ignore", invalid="ignore"):
        pu = (prev_clip[:, 0] / prev_clip[:, 3] + 1) / 2
        pv = (1 - prev_clip[:, 1] / prev_clip[:, 3]) / 2
    cam_du = pu - u
    cam_dv = pv - v

    current = cur["colour"][ys.astype(int), xs.astype(int)]
    regions = {
        "near": (depth > 0) & (lin < near_metres),
        "far": (depth > 0) & (lin >= near_metres),
        "nothing drawn": depth <= 0,
    }
    lines = []
    for name, mask in regions.items():
        if mask.sum() < 20:
            lines.append(f"  {name:13s} {mask.sum():6d} px: too few")
            continue
        errors = {}
        for k in (-1.0, 0.0, 0.5, 1.0, 1.5, 2.0):
            px = (u[mask] + du[mask] * k) * cw_ - 0.5
            py = (v[mask] + dv[mask] * k) * ch_ - 0.5
            errors[k] = np.abs(bilinear(prev["colour"], px, py) - current[mask]).mean()
        px = (u[mask] + cam_du[mask]) * cw_ - 0.5
        py = (v[mask] + cam_dv[mask]) * ch_ - 0.5
        cam_error = np.abs(bilinear(prev["colour"], px, py) - current[mask]).mean()
        vec_px = np.hypot(du[mask] * cw_, dv[mask] * ch_)
        diff_px = np.hypot((du[mask] - cam_du[mask]) * cw_, (dv[mask] - cam_dv[mask]) * ch_)
        best = min(errors, key=errors.get)
        lines.append(
            f"  {name:13s} {mask.sum():6d} px, vector mean {vec_px.mean():7.2f} px (display),"
            f" vector vs camera motion mean {np.nanmean(diff_px):7.2f} px;"
            f" colour error with vectors x-1 {errors[-1.0]:.2f} x0 {errors[0.0]:.2f} x0.5 {errors[0.5]:.2f} x1 {errors[1.0]:.2f}"
            f" x1.5 {errors[1.5]:.2f} x2 {errors[2.0]:.2f}, with camera motion {cam_error:.2f}; best x{best}")
    return lines


def block_motion(prev, cur, tile=64, search=96, stride=4):
    """Measured motion (to the previous frame) per tile, by block matching."""
    a = cur["colour"]
    b = prev["colour"]
    h, w = a.shape
    out = []
    for ty in range(search + tile, h - search - tile, tile * 3):
        for tx in range(search + tile, w - search - tile, tile * 3):
            block = a[ty:ty + tile:2, tx:tx + tile:2]
            if block.std() < 3:
                continue
            best = (1e9, 0, 0)
            for oy in range(-search, search + 1, stride):
                for ox in range(-search, search + 1, stride):
                    e = np.abs(b[ty + oy:ty + oy + tile:2, tx + ox:tx + ox + tile:2] - block).mean()
                    if e < best[0]:
                        best = (e, ox, oy)
            e, ox, oy = best
            for oy2 in range(oy - stride + 1, oy + stride):
                for ox2 in range(ox - stride + 1, ox + stride):
                    e2 = np.abs(b[ty + oy2:ty + oy2 + tile:2, tx + ox2:tx + ox2 + tile:2] - block).mean()
                    if e2 < best[0]:
                        best = (e2, ox2, oy2)
            out.append((tx + tile // 2, ty + tile // 2, best[1], best[2], best[0]))
    return out


def main():
    """Print the dump's frames, then the analysis of each consecutive pair (and block matching with --blocks)."""
    parser = argparse.ArgumentParser()
    parser.add_argument("folder")
    parser.add_argument("--near", type=float, default=15.0)
    parser.add_argument("--step", type=int, default=4)
    parser.add_argument("--blocks", action="store_true", help="also block-match the colour per tile")
    args = parser.parse_args()

    frames = load_frames(args.folder)
    print(f"{len(frames)} frames; colour {frames[0]['colour'].shape[::-1]}, depth {frames[0]['depth'].shape[::-1]},"
          f" motion {frames[0]['motion'].shape[1::-1]}; mvScale {frames[0]['mvScale']}, near {frames[0]['near']},"
          f" far {frames[0]['far']}")
    for f in frames:
        d = f["depth"]
        print(f"frame {f['index']}: jitter {f['jitter']}, dt {f['dtMs']} ms, reset {int(f['reset'])},"
              f" depth > 0 on {100 * (d > 0).mean():.1f} %")
    for i in range(1, len(frames)):
        prev, cur = frames[i - 1], frames[i]
        print(f"pair {prev['index']} -> {cur['index']}:")
        for line in analyse_pair(prev, cur, args.near, args.step):
            print(line)
        if args.blocks:
            cw_, ch_ = cur["colour"].shape[1], cur["colour"].shape[0]
            mh, mw = cur["motion"].shape[:2]
            sx, sy = cur["mvScale"]
            for x, y, ox, oy, e in block_motion(prev, cur):
                vec = cur["motion"][int(y / ch_ * mh), int(x / cw_ * mw)]
                vx = vec[0] * sx / mw * cw_
                vy = vec[1] * -sy / mh * ch_
                depth = cur["depth"][int(y / ch_ * cur["depth"].shape[0]), int(x / cw_ * cur["depth"].shape[1])]
                print(f"    tile at ({x:4d},{y:4d}): measured ({ox:4d},{oy:4d}) px, vector ({vx:7.2f},{vy:7.2f}) px,"
                      f" match error {e:5.2f}, depth {depth:.5f}")


if __name__ == "__main__":
    sys.exit(main())
