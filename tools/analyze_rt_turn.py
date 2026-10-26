"""Measures the raytracing test player's camera turn (RtTurnRunner).

Each folder is one run (rr, denoiser, plain). Each frame of the turn is compared with the
reference of the same pose, rendered after the camera stood there a while. The mean error
of the tone-mapped luminance is taken over the whole frame. It is also taken over what came
into view in this frame (behind a silhouette or past the frame's edge), over what came into
view within the last four frames, and over the rest. A smear or a ghost left by a temporal filter shows as
error where something just came into view. Unity's motion vectors are checked against
the motion the depth and the cameras give, as Ray Reconstruction relies on them.

Images of the error go into each folder as error_<frame>.png: the frame, the reference,
the error magnified, and the mask of what came into view.

Usage: python tools/analyze_rt_turn.py <folder> [<folder> ...]
"""

import os
import sys

import numpy as np

try:
    from PIL import Image
except ImportError:
    Image = None

LOOK_BACK = 4
DEPTH_TOLERANCE = 0.05


def read_meta(folder):
    """Per frame name in meta.txt: its size, whether Ray Reconstruction produced it, and its view and projection."""
    frames = {}
    for line in open(os.path.join(folder, 'meta.txt'), encoding='utf-8'):
        if line.startswith('#') or not line.strip():
            continue
        parts = line.split()
        values = [float(v) for v in parts[4:36]]
        frames[parts[0]] = {
            'width': int(parts[1]), 'height': int(parts[2]), 'reconstructed': parts[3] == '1',
            'view': np.array(values[:16]).reshape(4, 4), 'projection': np.array(values[16:32]).reshape(4, 4),
        }
    return frames


def load(folder, name, frame, what, channels, dtype):
    """One buffer of a frame (<name>_<what>.bin) as a float array of the frame's size, or None where it is missing."""
    path = os.path.join(folder, '%s_%s.bin' % (name, what))
    if not os.path.exists(path):
        return None
    data = np.fromfile(path, dtype=dtype)
    return data.reshape(frame['height'], frame['width'], channels).astype(np.float32)


def luminance(colour):
    """Rec. 709 luminance, tone-mapped by y / (1 + y), so bright pixels do not dominate the error."""
    y = colour[..., 0] * 0.2126 + colour[..., 1] * 0.7152 + colour[..., 2] * 0.0722
    y = np.maximum(y, 0.0)
    return y / (1.0 + y)


class Geometry:
    """World positions from a frame's depth, and where they lie in another frame."""

    def __init__(self, frame, depth, flip):
        """Unprojects every pixel of the depth through the frame's cameras; flip says the rows run bottom up."""
        h, w = depth.shape
        self.w, self.h, self.flip = w, h, flip
        xs = (np.arange(w) + 0.5) / w * 2.0 - 1.0
        ys = (np.arange(h) + 0.5) / h * 2.0 - 1.0
        if not flip:
            ys = -ys
        x, y = np.meshgrid(xs, ys)
        clip = np.stack([x, y, depth, np.ones_like(depth)], axis=-1)
        inverse = np.linalg.inv(frame['projection'] @ frame['view'])
        world = clip @ inverse.T
        self.world = world[..., :3] / world[..., 3:4]
        self.sky = depth <= 0.0

    def into(self, frame):
        """Pixel coordinates and view distance of this frame's points in another frame."""
        world = np.concatenate([self.world, np.ones(self.world.shape[:2] + (1,))], axis=-1)
        view = world @ frame['view'].T
        clip = view @ frame['projection'].T
        ndc = clip[..., :2] / clip[..., 3:4]
        px = (ndc[..., 0] * 0.5 + 0.5) * self.w - 0.5
        py = (ndc[..., 1] * 0.5 + 0.5) * self.h - 0.5
        if not self.flip:
            py = self.h - 1 - py
        return px, py, -view[..., 2], clip[..., 3] > 0


def view_distance(frame, depth, flip):
    """The distance along the view axis of every pixel of the depth, in the frame's own camera."""
    g = Geometry(frame, depth, flip)
    view = np.concatenate([g.world, np.ones(g.world.shape[:2] + (1,))], axis=-1) @ frame['view'].T
    return -view[..., 2]


def came_into_view(now_frame, now_depth, before_frame, before_depth, flip):
    """Pixels whose surface the frame before did not show: past its edge or hidden."""
    g = Geometry(now_frame, now_depth, flip)
    px, py, distance, front = g.into(before_frame)
    h, w = now_depth.shape
    inside = front & (px >= 0) & (px <= w - 1) & (py >= 0) & (py <= h - 1)
    xi = np.clip(np.rint(px).astype(int), 0, w - 1)
    yi = np.clip(np.rint(py).astype(int), 0, h - 1)
    before = view_distance(before_frame, before_depth, flip)[yi, xi]
    seen = inside & (np.abs(before - distance) <= DEPTH_TOLERANCE * np.maximum(distance, 1e-3))
    return ~seen & ~g.sky


def pick_flip(frames, depths, motions, names):
    """The row order the readback has: the one in which Unity's motion vectors agree with
    the cameras' motion. Which surfaces came into view is the same either way."""
    best = None
    moving = [i for i in range(1, len(names)) if motions.get(names[i]) is not None
              and not np.allclose(frames[names[i]]['view'], frames[names[i - 1]]['view'])][:4]
    for flip in (False, True):
        off = np.mean([motion_error(frames[names[i]], depths[names[i]], motions[names[i]], frames[names[i - 1]], flip)
                       for i in moving]) if moving else 0.0
        if best is None or off < best[1]:
            best = (flip, off)
    return best[0]


def motion_error(frame, depth, motion, before_frame, flip):
    """Unity's motion vectors against the camera's motion, in pixels, where both see."""
    g = Geometry(frame, depth, flip)
    px, py, _, front = g.into(before_frame)
    h, w = depth.shape
    x, y = np.meshgrid(np.arange(w), np.arange(h))
    # Unity's vectors: this frame's texture coordinate minus the previous one's.
    expected_u = (x - px) / w
    expected_v = (y - py) / h * (-1.0 if not flip else 1.0)
    valid = front & ~g.sky
    du = (motion[..., 0] - expected_u)[valid] * w
    dv = (motion[..., 1] - expected_v)[valid] * h
    return float(np.median(np.hypot(du, dv))) if du.size else float('nan')


def analyse(folder):
    """Rows of the errors per turn frame against its reference (all, new, share new, recent, rest, motion, RR),
    and the row order used; writes error_<frame>.png where much came into view and on every fifth frame."""
    frames = read_meta(folder)
    turn = sorted(n for n in frames if n.startswith('turn_'))
    colours, depths, motions = {}, {}, {}
    for name in frames:
        colours[name] = load(folder, name, frames[name], 'colour', 4, np.float16)
        depths[name] = load(folder, name, frames[name], 'depth', 1, np.float32)
        motions[name] = load(folder, name, frames[name], 'motion', 2, np.float16)
        if depths[name] is not None:
            depths[name] = depths[name][..., 0]
    turn = [n for n in turn if colours[n] is not None and depths[n] is not None]
    flip = pick_flip(frames, depths, motions, turn)
    rows = []
    for i, name in enumerate(turn):
        index = name[len('turn_'):]
        reference = 'ref_' + index
        if reference not in frames or colours.get(reference) is None or i == 0:
            continue
        error = np.abs(luminance(colours[name]) - luminance(colours[reference]))
        new = came_into_view(frames[name], depths[name], frames[turn[i - 1]], depths[turn[i - 1]], flip)
        recent = new.copy()
        for k in range(2, LOOK_BACK + 1):
            if i - k < 0:
                break
            recent |= came_into_view(frames[name], depths[name], frames[turn[i - k]], depths[turn[i - k]], flip)
        rest = ~recent
        motion = motion_error(frames[name], depths[name], motions[name], frames[turn[i - 1]], flip) \
            if motions.get(name) is not None else float('nan')
        rows.append((index, error.mean(), error[new].mean() if new.any() else 0.0, new.mean(),
                     error[recent].mean() if recent.any() else 0.0, error[rest].mean(), motion,
                     frames[name]['reconstructed']))
        if Image is not None and (new.mean() > 0.002 or int(index) % 5 == 0):
            def png(values):
                v = np.clip(values, 0.0, 1.0)
                return (v * 255).astype(np.uint8)
            a = png(luminance(colours[name]))
            b = png(luminance(colours[reference]))
            c = png(error * 4.0)
            d = png(new.astype(np.float32) * 0.5 + recent.astype(np.float32) * 0.5)
            image = np.concatenate([np.concatenate([a, b], axis=1), np.concatenate([c, d], axis=1)], axis=0)
            if flip:
                # Rows from the bottom: the halves upright, each in its place.
                image = np.concatenate([np.concatenate([a[::-1], b[::-1]], axis=1),
                                        np.concatenate([c[::-1], d[::-1]], axis=1)], axis=0)
            Image.fromarray(image).save(os.path.join(folder, 'error_%s.png' % index))
    return rows, flip


def main():
    """Analyse every folder given, print its rows and its pass or fail against the limits; exit 1 if any failed."""
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    summary = []
    for folder in sys.argv[1:]:
        if not os.path.exists(os.path.join(folder, 'meta.txt')):
            print('%s: no meta.txt' % folder)
            summary.append((folder, None))
            continue
        rows, flip = analyse(folder)
        print('--- %s (rows %s)' % (os.path.basename(folder), 'bottom up' if flip else 'top down'))
        print('frame  all     new     new%%   recent  rest    motion(px) RR')
        for r in rows:
            print('%s    %.4f  %.4f  %5.2f  %.4f  %.4f  %6.2f     %s' % (r[0], r[1], r[2], r[3] * 100, r[4], r[5], r[6],
                                                                       'yes' if r[7] else 'no'))
        passed = bool(rows)
        if rows:
            fast = [r for r in rows if r[3] > 0.01]
            rest = np.mean([r[5] for r in rows])
            new = np.mean([r[2] for r in fast]) if fast else 0.0
            # Frames without motion vectors give NaN; none with them fails the check.
            measured = [r[6] for r in rows if not np.isnan(r[6])]
            motion = max(measured) if measured else float('nan')
            print('mean: all %.4f, rest %.4f; frames with more than 1 %% new: %d, their new %.4f, recent %.4f' % (
                np.mean([r[1] for r in rows]), rest, len(fast), new, np.mean([r[4] for r in fast]) if fast else 0.0))
            # Unity's own lighting has no history: the turn equals its references. With
            # raytracing, about twice the error measured on 2026-10-01 (rest 0.0021,
            # new 0.0120 with Ray Reconstruction) is a regression; motion vectors off by
            # more than a pixel make every temporal filter smear.
            plain = os.path.basename(os.path.normpath(folder)).endswith('plain')
            limit_rest, limit_new = (0.0005, 0.0005) if plain else (0.005, 0.025)
            passed = rest <= limit_rest and new <= limit_new and motion <= 1.0
            print('%s  %s: rest %.4f (at most %.4f), new %.4f (at most %.4f), motion vectors off by at most %.2f px' % (
                'PASS' if passed else 'FAIL', os.path.basename(os.path.normpath(folder)), rest, limit_rest, new,
                limit_new, motion))
        summary.append((folder, rows if passed else None))
    sys.exit(0 if all(rows for _, rows in summary) else 1)


if __name__ == '__main__':
    main()
