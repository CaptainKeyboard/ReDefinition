"""The series of the edge-adaptive resolution of the cloud pass (package T12, the plan's T05b), run through
tools/build_weather_player.py --series T12-cost (timing run, exclusive), T12-quality and T12-temporal:

  T12-cost      scene 'edge-cost': ms per view of the coarse march, the mask passes, the edge march and the reconstruction + composite, for the
                modes of the table at 1707x960 and 2560x1440.
  T12-quality   scene 'edge-quality': per view the error against the reference (the mean of four full marches with the entry refinement) in
                the band of N = 6 pixels around the contour, in the thick interior and in the empty sky, the seams at the block borders, the mask
                and the pictures t12-<view>-before/after/reference/aid-mask/aid-rays/aid-error/aid-steps.png.
  T12-temporal  scene 'edge-temporal': turns of 2 and 12 degrees a frame, a 100 m/s fly-by and a camera cut: ghost_move, trail_px, shimmer,
                the frames to converge after the cut, the mask's cover of the contour.

Modes of the tables (scene EdgeVariant.Table): full = every pixel every frame with the entry refinement; u4 / u2 = the uniform 4 x 4 / 2 x 2 block
modes of T05 as they run today; u4r / u2r = the same with the entry refinement on every ray; e = edge-adaptive (4 x 4 coarse pattern, 2 x 2 pattern
with the refinement in the flagged blocks); eN = e without the step band (the refinement in every block); eT03 .. eP8 = the sweep of the three tuned
numbers (threshold, dilation, persistence), one at a time around e.

THRESHOLDS and their sources (fixed before the first run):
  band_rms.e        <= band_rms.u2                    brief: error in the band no worse than the uniform 2 x 2 mode of T05
  band_rms.e        <= 1.15 x band_rms.full           brief: within 15 percent [proposed] of full resolution (one full march with the refinement)
  interior_err.e    <= 0.02                           brief: luminance error where the transmittance is below 0.05 within 2 percent [proposed]
  empty_leak.e      <= 0.0001                         brief: empty sky exact (alpha 0)
  seam.e            <= 0.01                           brief: luminance step at block borders <= 1 percent [proposed]
  total.e           <= 0.8 (stack-below 1.2) ms at 1707x960, x 2.2 at 2560x1440     T05 cost budget (.local/plans/atmospheres/T05-cost-budget.md)
  mask.e            <= 0.1 ms                         brief
  total.e           <= 0.75 x total.u2r               brief: at least 25 percent [proposed] cheaper than uniform full-ray refinement at equal edge quality
  ghost_move.e      <= 0.012, trail_px.e <= 0.14      C21 thresholds N14, as the T05 rows use them (no upscaler in the scene)
  shimmer.e         <= shimmer.u4r                    brief: sparkling-edge measure no worse than the uniform T05 mode with the same refinement
  converge_frames.e <= 20                             brief: the T05 value, reconstruction cycle (16) + 4 frames
  cover (frame 2 after the cut) >= 0.9                brief: a new edge is drawn at edge resolution within 2 frames [proposed]

    python tools/weather_metrics/edge.py cost <log or stage folder>
    python tools/weather_metrics/edge.py quality <log or stage folder>
    python tools/weather_metrics/edge.py temporal <log or stage folder>
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

if __name__ != "__main__":
    from weather_metrics import Metric, Stage, register_series  # noqa: E402
    from weather_metrics import core  # noqa: E402
    from weather_metrics import layers as L  # noqa: E402

    # Importing layers writes the T01 inputs when they are missing.
    register_series("T12-cost", [Stage("scene", ["-mode", "scene", "-scene", "edge-cost", "-sweep", "1", "-in", L.INPUT], size=(1707, 960), proxy=True, timeout=1500)],
                    core.player_judged("scene"), exclusive=True, who="T12")
    register_series("T12-quality", [Stage("scene", ["-mode", "scene", "-scene", "edge-quality", "-sweep", "1", "-in", L.INPUT], size=(1280, 720), proxy=False,
                                          timeout=1500)], core.player_judged("scene"), exclusive=False, who="T12")
    register_series("T12-temporal", [Stage("scene", ["-mode", "scene", "-scene", "edge-temporal", "-in", L.INPUT], size=(1280, 720), proxy=False,
                                           timeout=1500)], core.player_judged("scene"), exclusive=False, who="T12")

LINE = re.compile(r"\{([^,{}]+),([^,{}]*),([^,{}]*),([^,{}]*)\}")
NAN = float("nan")


def read_lines(path):
    text = ""
    if os.path.isdir(path):
        for name in sorted(os.listdir(path)):
            if name.endswith(".log") or name.endswith(".txt"):
                text += open(os.path.join(path, name), encoding="utf-8", errors="replace").read() + "\n"
    else:
        text = open(path, encoding="utf-8", errors="replace").read()
    out = {}
    # The series runner's own table prints the metrics as "name value -  player info".
    for m in re.finditer(r"^(edge\S+)\s+(-?[0-9.eE+-]+|nan)\s+-\s+player info", text, re.M):
        out[m.group(1)] = float(m.group(2))
    for m in LINE.finditer(text):
        try:
            out[m.group(1)] = float(m.group(2))
        except ValueError:
            pass
    return out


def budget(view, size):
    base = 1.2 if view == "V-stack-below" else 0.8
    return base * (2.2 if size == "2560x1440" else 1.0)


def cost_rows(values):
    """(name, value, limit, mode, source) rows of the cost acceptance."""
    rows = []
    for k, v in sorted(values.items()):
        m = re.match(r"edgecost\.total\.(V-[a-z-]+)\.e\.(\d+x\d+)$", k)
        if not m:
            continue
        view, size = m.group(1), m.group(2)
        rows.append(("cost.%s.%s.e.total_ms" % (view, size), v, budget(view, size), "max", "T05 budget"))
        mask = values.get("edgecost.mask.%s.e.%s" % (view, size), NAN)
        rows.append(("cost.%s.%s.e.mask_ms" % (view, size), mask, 0.1, "max", "brief"))
        ref = values.get("edgecost.total.%s.u2r.%s" % (view, size), NAN)
        rows.append(("cost.%s.%s.e_vs_u2r" % (view, size), v / ref if ref == ref and ref > 0 else NAN, 0.75, "max", "brief: 25 percent cheaper"))
    return rows


def quality_rows(values, mode="e"):
    rows = []
    views = sorted({k.split(".")[1] for k in values if k.startswith("edgeq.")})
    for view in views:
        def g(name, variant=mode):
            return values.get("edgeq.%s.%s.%s" % (view, variant, name), NAN)
        rms_e, rms_u2, rms_full = g("band_rms"), g("band_rms", "u2"), g("band_rms", "full")
        rows.append(("quality.%s.band_rms_e_over_u2" % view, rms_e / rms_u2 if rms_u2 > 0 else NAN, 1.0, "max", "brief: no worse than uniform 2 x 2"))
        rows.append(("quality.%s.band_rms_e_over_full" % view, rms_e / rms_full if rms_full > 0 else NAN, 1.15, "max", "brief: within 15 percent of full"))
        rows.append(("quality.%s.interior_err" % view, g("interior_err"), 0.02, "max", "brief"))
        rows.append(("quality.%s.empty_leak" % view, g("empty_leak"), 0.0001, "max", "brief: empty sky exact"))
        rows.append(("quality.%s.seam" % view, g("seam"), 0.01, "max", "brief"))
    return rows


def temporal_rows(values, mode="e"):
    rows = []
    jobs = sorted({k.split(".")[1] for k in values if k.startswith("edget.")})
    for job in jobs:
        def g(scn, name, variant=mode):
            return values.get("edget.%s.%s.%s.%s" % (job, scn, variant, name), NAN)
        for scn in ("turn2", "turn12", "fly100"):
            rows.append(("temporal.%s.%s.ghost_move" % (job, scn), g(scn, "ghost_move"), 0.012, "max", "C21 N14"))
            rows.append(("temporal.%s.%s.trail_px" % (job, scn), g(scn, "trail_px"), 0.14, "max", "C21 N14"))
            ref = g(scn, "shimmer", "u4r")
            rows.append(("temporal.%s.%s.shimmer_e_over_u4r" % (job, scn), g(scn, "shimmer") / ref if ref > 0 else NAN, 1.0, "max", "brief: no worse than uniform 4 x 4"))
            rows.append(("temporal.%s.%s.cover_min" % (job, scn), g(scn, "cover_min"), 0.0, "info", "mask cover of the contour while moving"))
        rows.append(("temporal.%s.cut.converge_frames" % job, g("cut", "converge_frames"), 20, "max", "T05: 16 + 4"))
        rows.append(("temporal.%s.cut.cover_f2" % job, g("cut", "cover_f2"), 0.9, "min", "brief: edge resolution within 2 frames"))
    return rows


def show(rows):
    print("%-58s %12s %12s  %s" % ("metric", "value", "limit", "verdict"))
    for name, value, limit, mode, source in rows:
        if mode == "info" or value != value:
            verdict = "info" if mode == "info" else "MISSING"
        else:
            verdict = "pass" if (value <= limit if mode == "max" else value >= limit) else "FAIL"
        print("%-58s %12.5f %12.5f  %s  (%s)" % (name, value, limit, verdict, source))


def table(values, prefix, fields, variants_order):
    keys = {}
    for k, v in values.items():
        m = re.match(r"%s\.(V-[a-z-]+|[a-z-]+)\.([A-Za-z0-9]+)\.(?:(V-[a-z-]+)\.)?([a-z_0-9]+)$" % prefix, k)
        if m:
            keys[(m.group(1), m.group(2), m.group(4))] = v
    for view in sorted({k[0] for k in keys}):
        print("\n" + view)
        print("%-8s" % "mode" + "".join("%14s" % f for f in fields))
        for var in variants_order:
            print("%-8s" % var + "".join("%14.5f" % keys.get((view, var, f), NAN) for f in fields))


def cost_table(values):
    keys = {}
    for k, v in values.items():
        m = re.match(r"edgecost\.(coarse|mask|edge|recon|total)\.(V-[a-z-]+)\.([A-Za-z0-9]+)\.(\d+x\d+)$", k)
        if m:
            keys[(m.group(4), m.group(3), m.group(2), m.group(1))] = v
    for size in sorted({k[0] for k in keys}):
        views = sorted({k[2] for k in keys if k[0] == size})
        variants = []
        for k in keys:
            if k[0] == size and k[1] not in variants:
                variants.append(k[1])
        print("\n%s  total ms (coarse + mask + edge + reconstruction and composite); mask / edge ms in brackets" % size)
        print("%-8s" % "mode" + "".join("%26s" % v[2:] for v in views))
        for var in variants:
            cells = []
            for v in views:
                cells.append("%7.2f (%.2f/%.2f)" % (keys.get((size, var, v, "total"), NAN), keys.get((size, var, v, "mask"), 0.0), keys.get((size, var, v, "edge"), 0.0)))
            print("%-8s" % var + "".join("%26s" % c for c in cells))
    show(cost_rows(values))


def quality_table(values):
    variants = []
    for k in values:
        m = re.match(r"edgeq\.[a-z-]+\.([A-Za-z0-9]+)\.band_rms$", k)
        if m and m.group(1) not in variants:
            variants.append(m.group(1))
    views = sorted({k.split(".")[1] for k in values if k.startswith("edgeq.")})
    for view in views:
        print("\n" + view)
        print("%-8s %10s %10s %10s %10s %10s %10s %10s" % ("mode", "band_rms", "interior", "leak", "flicker", "mask", "cover", "seam"))
        for var in variants:
            def g(n):
                return values.get("edgeq.%s.%s.%s" % (view, var, n), NAN)
            print("%-8s %10.5f %10.5f %10.5f %10.5f %10.4f %10.4f %10.5f" % (var, g("band_rms"), g("interior_err"), g("empty_leak"), g("band_flicker"),
                                                                           g("mask_share"), g("contour_cover"), g("seam")))
    show(quality_rows(values))


def temporal_table(values):
    rows = {}
    for k, v in values.items():
        m = re.match(r"edget\.(V-[a-z-]+)\.([a-z0-9]+)\.([A-Za-z0-9]+)\.([a-z_0-9]+)$", k)
        if m:
            rows.setdefault((m.group(1), m.group(2)), {}).setdefault(m.group(3), {})[m.group(4)] = v
    for (job, scn), modes in sorted(rows.items()):
        print("\n%s %s" % (job, scn))
        print("%-6s %10s %10s %10s %10s %10s %10s" % ("mode", "ghost_mv", "trail_px", "shimmer", "cover_min", "converge", "plateau"))
        for mode, m in modes.items():
            print("%-6s %10.4f %10.4f %10.5f %10.4f %10.1f %10.5f" % (mode, m.get("ghost_move", NAN), m.get("trail_px", NAN), m.get("shimmer", NAN),
                                                                    m.get("cover_min", NAN), m.get("converge_frames", NAN), m.get("plateau", NAN)))
    show(temporal_rows(values))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    data = read_lines(sys.argv[2])
    {"cost": cost_table, "quality": quality_table, "temporal": temporal_table}[sys.argv[1]](data)
