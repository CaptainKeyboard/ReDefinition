"""The series of the temporal work (package T05), run through tools/build_weather_player.py --series T05-cost (timing run, exclusive) and
T05-temporal (stability against the reference):

  T05-cost      scene 'layers-cost5': ms of the layer pass on the T01 view table for the cost measures (up-tap reuse or analytic term, step growth
                with the lost transmittance, fewer sun taps) and the reduced modes (prototype A: one pixel of every N x N block per frame plus the
                reconstruction; prototype B: a march target of a fraction of the size), at 1707x960 and 2560x1440.
  T05-temporal  scene 'layers-temporal': the reconstruction against the reference (the mean of four full marches at every pose) along the
                camera paths (still, turns of 2 and 12 degrees a frame, a fly-by at 100 m/s, the cloud's wind), without an upscaler:
                ghost_move, ghost_still, trail_px, shimmer as analyze_weather_player.py defines them, but over the pixels the reference covers
                (alpha above 0.02) and for the display value of the cloud laid over a constant sky.

Both series print the player's lines as they are; the tables are made by:

    python tools/weather_metrics/temporal.py cost <log or stage folder>
    python tools/weather_metrics/temporal.py quality <log or stage folder>
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

if __name__ != "__main__":
    from weather_metrics import Stage, register_series  # noqa: E402
    from weather_metrics import core  # noqa: E402
    from weather_metrics import layers as L  # noqa: E402


def _input():
    L.stages()   # writes the T01 inputs when missing
    return L.INPUT


if __name__ != "__main__":
    register_series("T05-cost", [Stage("scene", ["-mode", "scene", "-scene", "layers-cost5", "-in", L.INPUT], size=(1707, 960), proxy=True, timeout=1500)],
                    core.player_judged("scene"), exclusive=True, who="T05")
    register_series("T05-temporal", [Stage("scene", ["-mode", "scene", "-scene", "layers-temporal", "-in", L.INPUT], size=(1280, 720), proxy=False, timeout=1500)],
                    core.player_judged("scene"), exclusive=False, who="T05")
    register_series("T05-look", [Stage("scene", ["-mode", "scene", "-scene", "layers-look5", "-in", L.INPUT], size=(1280, 720), proxy=False, timeout=1500)],
                    core.player_judged("scene"), exclusive=False, who="T05")


LINE = re.compile(r"\{([^,{}]+),([^,{}]*),([^,{}]*),([^,{}]*)\}")


def read_lines(path):
    text = ""
    if os.path.isdir(path):
        for name in sorted(os.listdir(path)):
            if name.endswith(".log") or name.endswith(".txt"):
                text += open(os.path.join(path, name), encoding="utf-8", errors="replace").read() + "\n"
    else:
        text = open(path, encoding="utf-8", errors="replace").read()
    out = {}
    for m in LINE.finditer(text):
        try:
            out[m.group(1)] = float(m.group(2))
        except ValueError:
            pass
    return out


def cost_table(values):
    keys = {}
    for k, v in values.items():
        m = re.match(r"cost5\.(march|resolve|composite|total)\.(V-[a-z-]+)\.([a-z0-9-]+)\.(\d+x\d+)$", k)
        if m:
            keys[(m.group(4), m.group(3), m.group(2), m.group(1))] = v
    for size in sorted({k[0] for k in keys}):
        views = sorted({k[2] for k in keys if k[0] == size})
        variants = []
        for k in keys:
            if k[0] == size and k[1] not in variants:
                variants.append(k[1])
        print("\n%s  total ms (march + resolve + composite)" % size)
        print("%-12s" % "variant" + "".join("%14s" % v[2:] for v in views))
        for var in variants:
            print("%-12s" % var + "".join("%14.2f" % keys.get((size, var, v, "total"), float("nan")) for v in views))


def quality_table(values):
    rows = {}
    for k, v in values.items():
        m = re.match(r"temporal\.(V-[a-z-]+)\.([a-z0-9]+)\.([a-z0-9]+)\.([a-z_]+)$", k)
        if m:
            rows.setdefault((m.group(1), m.group(2)), {}).setdefault(m.group(3), {})[m.group(4)] = v
    for (job, scn), modes in sorted(rows.items()):
        print("\n%s %s" % (job, scn))
        print("%-6s %10s %10s %10s %10s %8s" % ("mode", "ghost_mv", "ghost_st", "trail_px", "shimmer", "conv"))
        for mode, m in modes.items():
            print("%-6s %10.4f %10.4f %10.4f %10.4f %8s" % (mode, m.get("ghost_move", float("nan")), m.get("ghost_still", float("nan")),
                                                           m.get("trail_px", float("nan")), m.get("shimmer", float("nan")),
                                                           "%d" % m["converge_frames"] if "converge_frames" in m else "-"))
    for k, v in sorted(values.items()):
        if ".origin_shift." in k:
            print("%s = %.5f" % (k, v))


def look_table(values):
    rows = {}
    for k, v in values.items():
        m = re.match(r"look5\.(V-[a-z-]+)\.([a-zA-Z0-9]+)\.(diff|bias|alpha_diff)$", k)
        if m:
            rows.setdefault(m.group(2), {}).setdefault(m.group(1), {})[m.group(3)] = v
    views = sorted({v for r in rows.values() for v in r})
    print("mean absolute display difference to the pass without the measure (bias of the mean in brackets, percent)")
    print("%-8s" % "variant" + "".join("%20s" % v[2:] for v in views))
    for name, r in rows.items():
        print("%-8s" % name + "".join("%12.4f (%+5.1f)" % (r.get(v, {}).get("diff", float("nan")), 100 * r.get(v, {}).get("bias", float("nan"))) for v in views))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    data = read_lines(sys.argv[2])
    {"cost": cost_table, "quality": quality_table, "look": look_table}[sys.argv[1]](data)
