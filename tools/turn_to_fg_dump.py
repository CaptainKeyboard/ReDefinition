"""Writes frames of the test player's camera turn (RtTurnRunner) as frame generation's
input dump (FrameGenerationDump.cpp), so that tools/replay_fg.ps1 plays them through frame
generation as it plays the game's.

    python tools/turn_to_fg_dump.py <turn folder> <dump folder> [first] [last]

The colour is the frame as it came out (Ray Reconstruction's in the rr run), clamped and
gamma-encoded into 8 bits as the game's HUD-less image; depth and motion vectors as they
were rendered. The dump's rows are top-down, the readback's bottom-up.
"""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fg_dump  # noqa: E402


def main():
    """Write the turn frames from first to last, with their textures and frames.txt, into the dump folder."""
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    source, target = sys.argv[1], sys.argv[2]
    first = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    last = int(sys.argv[4]) if len(sys.argv) > 4 else 10 ** 6
    os.makedirs(target, exist_ok=True)
    rows = []
    for line in open(os.path.join(source, 'meta.txt'), encoding='utf-8'):
        parts = line.split()
        if not parts or not parts[0].startswith('turn_'):
            continue
        index = int(parts[0][len('turn_'):])
        if first <= index <= last:
            rows.append(parts)
    rows.sort(key=lambda p: p[0])
    text = ['# per frame: index, packet fields, then the three textures as raw rows']
    for n, parts in enumerate(rows):
        name, w, h = parts[0], int(parts[1]), int(parts[2])
        values = [float(v) for v in parts[4:]]
        fg = values[32:96]
        vectors = values[96:108]
        colour = np.fromfile(os.path.join(source, name + '_colour.bin'), dtype=np.float16).reshape(h, w, 4)
        colour = colour.astype(np.float32)[::-1]
        encoded = (np.clip(colour[..., :3], 0.0, 1.0) ** (1.0 / 2.2) * 255.0 + 0.5).astype(np.uint8)
        rgba = np.concatenate([encoded, np.full((h, w, 1), 255, np.uint8)], axis=-1)
        rgba.tofile(os.path.join(target, 'colour-%02d.bin' % n))
        rgba.tofile(os.path.join(target, 'frame-%02d.bin' % n))
        depth = np.fromfile(os.path.join(source, name + '_depth.bin'), dtype=np.float32).reshape(h, w)[::-1]
        depth.tofile(os.path.join(target, 'depth-%02d.bin' % n))
        motion = np.fromfile(os.path.join(source, name + '_motion.bin'), dtype=np.uint32).reshape(h, w)[::-1]
        motion.tofile(os.path.join(target, 'motion-%02d.bin' % n))
        # The turn's frame number in place of the game's (RtTurnRunner, 60 degrees, 0.3 to 5000 m).
        text += fg_dump.entry(n, int(name[len('turn_'):]), (w, h), (-w, -h), 0.3, 5000.0, 1.047198, 16.667,
                              [fg[k:k + 16] for k in (0, 16, 32, 48)], vectors=vectors,
                              textures=('colour', 'depth', 'motion', 'frame'))
    fg_dump.write_index(target, text)
    print('%d frames into %s' % (len(rows), target))


if __name__ == '__main__':
    main()
