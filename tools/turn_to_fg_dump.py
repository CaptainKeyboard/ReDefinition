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


def main():
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
        text.append('frame %d packetIndex %d render %dx%d reset 0 jitter 0.000000 0.000000 mvScale %.3f %.3f near 0.300000 '
                    'far 5000.000000 fov 1.047198 dtMs 16.667 flipped 1' % (n, n + 1, w, h, -w, -h))
        text.append('position %.6f %.6f %.6f up %.6f %.6f %.6f right %.6f %.6f %.6f forward %.6f %.6f %.6f' % tuple(vectors))
        for label, offset in (('viewToClip', 0), ('clipToView', 16), ('clipToPrevClip', 32), ('prevClipToClip', 48)):
            text.append(label + ''.join(' %.9g' % v for v in fg[offset:offset + 16]))
        text.append('texture colour %dx%d format 28 bytesPerTexel 4' % (w, h))
        text.append('texture depth %dx%d format 39 bytesPerTexel 4' % (w, h))
        text.append('texture motion %dx%d format 33 bytesPerTexel 4' % (w, h))
        text.append('texture frame %dx%d format 28 bytesPerTexel 4' % (w, h))
    with open(os.path.join(target, 'frames.txt'), 'w', encoding='utf-8') as file:
        file.write('\n'.join(text) + '\n')
    print('%d frames into %s' % (len(rows), target))


if __name__ == '__main__':
    main()
