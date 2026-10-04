"""Frame generation's input dump (FrameGenerationDump.cpp): frames.txt and the texture
files beside it, written by the synthetic dumps and turn_to_fg_dump.py and read by
analyze_fg_inputs.py.

Per frame, frames.txt has a frame line with the packet's fields, the camera's position
and basis, frame generation's four matrices (row-major) and one line per texture with
its size and DXGI format. The textures lie beside it as colour-NN.bin, depth-NN.bin,
motion-NN.bin and, where there, frame-NN.bin, rows top-down.
"""

import os
import re

# The formats the game's dump names: 8-bit RGBA, R32 float depth, R16G16 float motion.
COLOUR_FORMAT = 28
DEPTH_FORMAT = 41
MOTION_FORMAT = 34

MATRICES = ("viewToClip", "clipToView", "clipToPrevClip", "prevClipToClip")


def number(value):
    return "%.9g" % value


def entry(index, packet_index, size, mv_scale, near, far, fov, dt_ms, matrices, flipped=True, jitter=(0.0, 0.0),
          reset=False, vectors=(0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0, 1), textures=("colour", "depth", "motion")):
    """The lines of one frame. matrices: the four of MATRICES, 16 values each;
    vectors: position, up, right, forward."""
    w, h = size
    lines = ["frame %d packetIndex %d render %dx%d reset %d jitter %s %s mvScale %s %s near %s far %s fov %s dtMs %s "
             "flipped %d" % (index, packet_index, w, h, 1 if reset else 0, number(jitter[0]), number(jitter[1]),
                             number(mv_scale[0]), number(mv_scale[1]), number(near), number(far), number(fov),
                             number(dt_ms), 1 if flipped else 0),
             "position %s %s %s up %s %s %s right %s %s %s forward %s %s %s" % tuple(number(v) for v in vectors)]
    for name, matrix in zip(MATRICES, matrices):
        lines.append(name + "".join(" " + number(v) for v in matrix))
    formats = {"colour": COLOUR_FORMAT, "depth": DEPTH_FORMAT, "motion": MOTION_FORMAT, "frame": COLOUR_FORMAT}
    for name in textures:
        lines.append("texture %s %dx%d format %d bytesPerTexel 4" % (name, w, h, formats[name]))
    return lines


def write_index(folder, lines):
    with open(os.path.join(folder, "frames.txt"), "w", encoding="utf-8") as file:
        file.write("\n".join(lines) + "\n")


def read_index(folder):
    """frames.txt as a list of frames: index, render, jitter, mvScale, near, far, fov,
    dtMs, flipped, reset, the matrices that are there as flat lists, and textures,
    name -> (width, height, format)."""
    frames = []
    for line in open(os.path.join(folder, "frames.txt"), encoding="utf-8").read().splitlines():
        if line.startswith("frame "):
            parts = line.split()
            f = {"index": int(parts[1])}
            m = re.search(r"render (\d+)x(\d+)", line)
            f["render"] = (int(m.group(1)), int(m.group(2)))
            for key in ("jitter", "mvScale"):
                i = parts.index(key)
                f[key] = (float(parts[i + 1]), float(parts[i + 2]))
            for key in ("near", "far", "fov", "dtMs", "flipped", "reset"):
                f[key] = float(parts[parts.index(key) + 1])
            f["textures"] = {}
            frames.append(f)
        elif line.split(" ")[0] in MATRICES:
            name, *values = line.split()
            frames[-1][name] = [float(v) for v in values]
        elif line.startswith("texture "):
            parts = line.split()
            w, h = (int(v) for v in parts[2].split("x"))
            frames[-1]["textures"][parts[1]] = (w, h, int(parts[4]))
    return frames
