"""Reference implementation of the sea-ice pattern (package D35) and the tables it checks.

The pattern code exists three times: src/Atmospheres/Ice/IceHash.cs and IceMask.cs (CPU, the orbit picture), the shader
unity/Assets/ReDefinitionAtmospheres/Shaders/ReDefinitionAtmospheresIceOverlay.shader (GPU, the near-ground overlay) and this file
(an independent re-implementation that makes the golden tables of the unit tests). A change to one of them is a change to all.

    python tools/weather_metrics/ice.py golden            prints the golden table as C# array literals (paste into IceHashTests)
    python tools/weather_metrics/ice.py check <file.json> compares a GPU dump (rows of the series probe) with this implementation
    python tools/weather_metrics/ice.py coverage <file.json> prints the coverage rows of a series result (field against rendered)

As a module of the weather player's series it registers the series "ice" (python tools/build_weather_player.py --series ice): the
scene ice-check runs the overlay shader's hash, noise and coverage passes on the player's GPU against the CPU code (IceCheckScene).
"""
import json
import math
import struct
import sys

M32 = 0xFFFFFFFF

# lowbias32 of Chris Wellons' hash-prospector (The Unlicense); the same mixer as PrecipMath.Hash.
MIX_MULTIPLIER_A = 0x7FEB352D
MIX_MULTIPLIER_B = 0x846CA68B
JITTER_Y_SALT = 0x9E3779B9
JITTER_Z_SALT = 0x7F4A7C15
VALUE_SALT = 0x6A09E667
DITHER_SALT = 0xBB67AE85
JITTER_LOW = 0.1
JITTER_SPAN = 0.8


def f32(x):
    """Rounds a Python float to a 32-bit float."""
    return struct.unpack("f", struct.pack("f", x))[0]


def mix(v):
    v &= M32
    v ^= v >> 16
    v = (v * MIX_MULTIPLIER_A) & M32
    v ^= v >> 15
    v = (v * MIX_MULTIPLIER_B) & M32
    v ^= v >> 16
    return v


def cell(x, y, z, seed):
    return mix((mix((mix((seed + (z & M32)) & M32) + (y & M32)) & M32) + (x & M32)) & M32)


def unit(h):
    return (h >> 8) * (1.0 / 16777216.0)


def worley(px, py, pz, seed):
    """Returns (f1, f2, cell hash, cell value) at a point in cell units."""
    ix, iy, iz = math.floor(px), math.floor(py), math.floor(pz)
    f1 = f2 = 1e9
    nearest = 0
    for dz in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                cx, cy, cz = ix + dx, iy + dy, iz + dz
                h = cell(cx, cy, cz, seed)
                jx = JITTER_LOW + JITTER_SPAN * unit(h)
                jy = JITTER_LOW + JITTER_SPAN * unit(mix((h + JITTER_Y_SALT) & M32))
                jz = JITTER_LOW + JITTER_SPAN * unit(mix((h + JITTER_Z_SALT) & M32))
                qx, qy, qz = cx + jx - px, cy + jy - py, cz + jz - pz
                d = math.sqrt(qx * qx + qy * qy + qz * qz)
                if d < f1:
                    f2, f1, nearest = f1, d, h
                elif d < f2:
                    f2 = d
    return f1, f2, nearest, unit(mix(nearest ^ VALUE_SALT))


def value_noise(px, py, pz, seed):
    fx, fy, fz = math.floor(px), math.floor(py), math.floor(pz)
    tx, ty, tz = px - fx, py - fy, pz - fz
    tx, ty, tz = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty), tz * tz * (3 - 2 * tz)

    def corner(x, y, z):
        return unit(cell(x, y, z, seed)) * 2 - 1

    def lerp(a, b, t):
        return a + (b - a) * t

    ix, iy, iz = int(fx), int(fy), int(fz)
    x00 = lerp(corner(ix, iy, iz), corner(ix + 1, iy, iz), tx)
    x10 = lerp(corner(ix, iy + 1, iz), corner(ix + 1, iy + 1, iz), tx)
    x01 = lerp(corner(ix, iy, iz + 1), corner(ix + 1, iy, iz + 1), tx)
    x11 = lerp(corner(ix, iy + 1, iz + 1), corner(ix + 1, iy + 1, iz + 1), tx)
    return lerp(lerp(x00, x10, ty), lerp(x01, x11, ty), tz)


# The points of the golden table: lattice cells (negative too) and points whose coordinates are exact in a float.
HASH_CELLS = [(0, 0, 0), (1, 2, 3), (-1, -2, -3), (17, -5, 40), (-100, 250, -7), (2047, 2047, 2047), (-2048, 0, 99), (123456, -654321, 31)]
HASH_SEEDS = [0, 12345, 0xDEADBEEF]
POINTS = [(0.0, 0.0, 0.0), (0.5, 0.5, 0.5), (3.25, -1.75, 8.5), (-12.125, 40.5, -0.0625), (100.375, 200.625, -300.25), (7.0, 7.0, 7.0),
          (-0.015625, 0.015625, 0.5), (199.9375, -0.4375, 33.3125)]


def golden():
    lines = []
    lines.append("// IceHash.Cell(x, y, z, seed): x, y, z, seed, expected")
    rows = []
    for seed in HASH_SEEDS:
        for (x, y, z) in HASH_CELLS:
            rows.append("            new long[] { %d, %d, %d, %d, %d }" % (x, y, z, seed, cell(x, y, z, seed)))
    lines.append("        private static readonly long[][] CellTable =\n        {\n" + ",\n".join(rows) + "\n        };")
    lines.append("// IceHash.WorleyAt(px, py, pz, 12345): px, py, pz, f1, f2, cell hash, cell value")
    rows = []
    for (px, py, pz) in POINTS:
        f1, f2, h, v = worley(px, py, pz, 12345)
        rows.append("            new double[] { %r, %r, %r, %r, %r, %d, %r }" % (px, py, pz, f1, f2, h, v))
    lines.append("        private static readonly double[][] WorleyTable =\n        {\n" + ",\n".join(rows) + "\n        };")
    lines.append("// IceHash.ValueNoise(px, py, pz, 777): px, py, pz, expected")
    rows = []
    for (px, py, pz) in POINTS:
        rows.append("            new double[] { %r, %r, %r, %r }" % (px, py, pz, value_noise(px, py, pz, 777)))
    lines.append("        private static readonly double[][] NoiseTable =\n        {\n" + ",\n".join(rows) + "\n        };")
    return "\n".join(lines)


def check(path):
    """Compares a dump written by the series probe (rows of {x, y, z, f1, f2, hash, value} read back from the GPU) with this file."""
    with open(path) as handle:
        dump = json.load(handle)
    worst = 0.0
    wrong_hash = 0
    for row in dump.get("points", []):
        f1, f2, h, v = worley(row["x"], row["y"], row["z"], dump.get("seed", 12345))
        worst = max(worst, abs(f1 - row["f1"]), abs(f2 - row["f2"]), abs(v - row["value"]))
        if h != row["hash"]:
            wrong_hash += 1
    print("points %d, worst float error %.3g, wrong hashes %d" % (len(dump.get("points", [])), worst, wrong_hash))
    return 0 if worst < 1e-3 and wrong_hash == 0 else 1


def coverage(path):
    with open(path) as handle:
        rows = json.load(handle)
    worst = 0.0
    for row in rows.get("coverage", []):
        error = abs(row["rendered"] - row["field"])
        worst = max(worst, error)
        print("%-24s field %.3f rendered %.3f error %.3f %s" % (row["name"], row["field"], row["rendered"], error, "ok" if error <= 0.05 else "OVER"))
    return 0 if worst <= 0.05 else 1


try:
    from weather_metrics import Stage, register_series
    from weather_metrics.core import player_judged

    register_series("ice", [Stage("scene", ["-mode", "scene", "-scene", "ice-check"])], player_judged("scene"), exclusive=False)
except ImportError:
    pass  # run as a script: the golden, check and coverage commands need no series


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else "golden"
    if command == "golden":
        print(golden())
    elif command == "check" and len(sys.argv) > 2:
        sys.exit(check(sys.argv[2]))
    elif command == "coverage" and len(sys.argv) > 2:
        sys.exit(coverage(sys.argv[2]))
    else:
        print(__doc__)
        sys.exit(2)
