"""The series of package T16 (visibility culling and distance LOD of the own clouds), run through tools/build_weather_player.py --series <name>:

  T16-cost     timing run (exclusive), scene 'cull-cost': ms of the cloud pass (the tile pass, the march, the reconstruction) per view (ground, inside, above,
               orbit-edge, sky3) at 1707x960 and 2560x1440 for every switch alone and all together, in the full mode and in the 4 x 4 block mode (cloudResolution
               4); every variant twice. Also the CPU ms of building the bound maps (refresh_ms).
  T16-quality  scene 'cull-quality' (640x360): per view and variant the error against the reference (the mean of four full marches of the baseline without the
               switches), the A/B difference to the same frame without the switch, the share of tiles and layer-tile pairs dropped by reason, and the pictures
               t16-<view>-before / after / aid-skip / aid-lod / aid-diff.png in the player's output folder (cull16).
  T16-sweep    scene 'cull-sweep': camera sweeps (altitude 0 to 12 km, a layer crossing, a turn of 360 degrees, a distance of 300 km, a turn under three layers):
               the largest frame-to-frame change of the mean luminance that the switch adds to the baseline's own.
  T16-levels   scene 'cull-levels': the 4 x 4 block mode through two turns with and without the switches: ghost_move, trail_px, shimmer.

THE SUCCESS CRITERIA, fixed BEFORE the first run (a sweep changes thresholds of the switches' numbers only, at most 3 sweeps per constant group):
  baseline        a switch is compared with the same view, size and mode WITHOUT the switch and with the same haze and shape mean (off; haze for the distance cut;
                  mean for the detail LOD; hm for all). The block mode (u4) is its own baseline: the reconstruction is part of the fair comparison, never the full
                  pass alone. The T12 lesson: a comparison against a weaker baseline misleads.
  C1 cost         all vs hm: ms(all) <= 0.70 x ms(hm) on at least 3 of the 7 views (ground, inside, above, orbit-edge, sky3, horizon, stack-below) at 1707x960, in the full mode AND in the 4 x 4 block mode. Source: K-T2 start 46
                  (1707x960, Release, A4500 Medium): the plain pass costs 4.08 / 4.48 / 4.47 / 4.12 ms (ground / inside / above / sky3), the 4 x 4 level 0.377 of
                  it (1.54 / 1.69 / 1.69 / 1.55 ms); the worst-case budget of T05 (MAINREPO .local/plans/atmospheres/T05-cost-budget.md) is 1.2 ms at 1707x960 with reconstruction: the
                  reduction needed is 22 / 29 / 29 / 22 percent, rounded up to 30 percent.
  C1b per switch  a switch is "useful" when it saves 10 percent or more (ratio <= 0.90) on at least 2 of the 7 views at 1707x960 within C2.
  Levers of the measurement (no settings): the haze rows use eight times the optical depth of the Earth coefficients (the distance cut cannot act at Earth's
  haze in any view), the terrain test (skipT) is not exercised by the scenes (they bind no scene depth: a gap in what is measured, said in the report).
  C2 error        interior_err <= 1.15 x the baseline's own + 0.002 and band_rms <= 1.15 x the baseline's own + 0.002 (owner order of T15: within 15 percent; the
                  absolute term is two steps of the 8 bit display), empty_leak <= the baseline's + 0.0005; and for the switches that claim to be exact
                  (skip, skipG, skipT, skipM, closed): cloud_lost <= 0.0005 (share of pixels that lose more than 1 percent of alpha) and ab_changed <= 0.002.
  C3 no pop       the largest frame-to-frame change a switch adds to the baseline's own, relative to the mean luminance, <= 0.03 on every path (brief: 3 percent).
  C4 tile pass    the tile pass costs <= 0.1 ms at 1707x960 (brief).
  C5 reconstruction  u4all vs u4hm: ghost_move, trail_px and shimmer <= 1.2 x the baseline's (brief: baseline + 20 percent).
Not measured in the player: DLSS, FSR, XeSS and frame generation (they ride in the game series K-T3); the report says so.

    python tools/weather_metrics/cull.py summary     # the verdict table from the latest T16-cost, T16-quality, T16-sweep, T16-levels runs
"""
import glob
import json
import math
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "T16-in")
NAN = float("nan")

VIEWS = ("ground", "inside", "above", "orbit-edge", "sky3", "horizon", "stack-below")
# (switch, baseline): the first column of every comparison.
PAIRS = (("skip", "off"), ("skipG", "off"), ("skipT", "off"), ("skipM", "off"), ("closed", "off"), ("cut", "haze"), ("growth", "off"), ("tap", "off"),
         ("detail", "mean"), ("cap", "off"), ("all", "hm"))
EXACT = ("skip", "skipG", "skipT", "skipM", "closed")
SIZE = "1707x960"
LIMIT_ALL, LIMIT_USEFUL, USEFUL_VIEWS, ALL_VIEWS = 0.70, 0.90, 2, 3
LIMIT_REL, LIMIT_ABS, LIMIT_LEAK, LIMIT_LOST, LIMIT_CHANGED = 1.15, 0.002, 0.0005, 0.0005, 0.002
LIMIT_POP, LIMIT_TILE_MS, LIMIT_RECON = 0.03, 0.1, 1.2


def metrics_of(path):
    with open(path, encoding="utf-8") as f:
        return {m["metric"]: m["value"] for m in json.load(f)}


def latest(series):
    files = sorted(glob.glob(os.path.join(BUILD, series + "-2*.json")))
    return files[-1] if files else None


def cost_table(values):
    """{(view, variant, size): (mean total ms, spread, mean tile ms)} from the two passes of the cost scene."""
    rows, tiles = {}, {}
    for k, v in values.items():
        m = re.match(r"cullcostb?\.(total|tile)\.V-([a-z0-9-]+)\.([A-Za-z0-9]+)\.(\d+x\d+)$", k)
        if m:
            (rows if m.group(1) == "total" else tiles).setdefault((m.group(2), m.group(3), m.group(4)), []).append(v)
    return {key: (sum(v) / len(v), max(v) - min(v), sum(tiles.get(key, [0.0])) / max(1, len(tiles.get(key, [0.0])))) for key, v in rows.items()}


def prefixed(name, block):
    return ("u4" + name) if block else name


def verdict_table():
    cost_path, quality_path, sweep_path, levels_path = (latest(s) for s in ("T16-cost", "T16-quality", "T16-sweep", "T16-levels"))
    if not cost_path or not quality_path:
        print("no T16-cost / T16-quality result in", BUILD)
        return 2
    table = cost_table(metrics_of(cost_path))
    q = metrics_of(quality_path)
    sweep = metrics_of(sweep_path) if sweep_path else {}
    levels = metrics_of(levels_path) if levels_path else {}

    def qv(view, var, field):
        return q.get("cullq.%s.%s.%s" % (view, var, field), NAN)

    print("cost: %s\nquality: %s\nsweep: %s\nlevels: %s\n" % tuple(os.path.basename(p) if p else "-" for p in (cost_path, quality_path, sweep_path, levels_path)))
    summary = {}
    for size in ("1707x960", "2560x1440"):
        for block in (False, True):
            print("== %s, %s ==" % (size, "4 x 4 block mode" if block else "full mode"))
            print("%-8s %-11s %8s %8s %6s %7s | %8s %8s %8s %8s %8s %8s  %s" % ("switch", "view", "base ms", "ms", "ratio", "tile ms", "int/base", "band/base", "leak", "lost",
                                                                              "ab_chg", "pop max", "verdict"))
            for name, base in PAIRS:
                var, bvar = prefixed(name, block), prefixed(base, block)
                ratios, oks = [], []
                for view in VIEWS:
                    b, v = table.get((view, bvar, size)), table.get((view, var, size))
                    if not b or not v:
                        continue
                    ratio = v[0] / b[0] if b[0] > 0 else NAN
                    ii, bi = qv(view, var, "interior_err"), qv(view, bvar, "interior_err")
                    br, bb = qv(view, var, "band_rms"), qv(view, bvar, "band_rms")
                    leak, bleak = qv(view, var, "empty_leak"), qv(view, bvar, "empty_leak")
                    lost, changed = qv(view, var, "cloud_lost"), qv(view, var, "ab_changed")
                    pops = [sweep.get("cullsweep.%s.%s.pop" % (p, name), NAN) for p in ("altitude", "crossing", "turn", "distance", "travel", "sky3turn")] if not block else []
                    pop = max([x for x in pops if x == x] or [NAN])
                    err_ok = ((ii != ii or ii <= LIMIT_REL * bi + LIMIT_ABS) and (br != br or br <= LIMIT_REL * bb + LIMIT_ABS)
                              and (leak != leak or leak <= bleak + LIMIT_LEAK))
                    if name in EXACT and not block:
                        err_ok = err_ok and (lost != lost or lost <= LIMIT_LOST) and (changed != changed or changed <= LIMIT_CHANGED)
                    pop_ok = pop != pop or pop <= LIMIT_POP
                    ratios.append((ratio, err_ok and pop_ok))
                    print("%-8s %-11s %8.3f %8.3f %6.2f %7.3f | %8.2f %8.2f %8.5f %8.5f %8.5f %8.4f  %s" % (
                        var, view, b[0], v[0], ratio, v[2], ii / bi if bi else NAN, br / bb if bb else NAN, leak, lost, changed, pop,
                        "ok" if (err_ok and pop_ok) else "ERROR"))
                if size == SIZE and ratios:
                    limit = LIMIT_ALL if name == "all" else LIMIT_USEFUL
                    need = ALL_VIEWS if name == "all" else USEFUL_VIEWS
                    good = sum(1 for r, ok in ratios if r == r and r <= limit and ok)
                    summary[(name, block)] = (good, len(ratios), need, all(ok for _, ok in ratios))
            print()
    print("per switch at %s (cost on at least %d views within the error limits; the error flag over all views):" % (SIZE, USEFUL_VIEWS))
    for (name, block), (good, n, need, clean) in sorted(summary.items()):
        label = prefixed(name, block)
        verdict = ("%s" % ("C1 MET" if good >= need else "C1 NOT MET")) if name == "all" else ("useful" if good >= need else "not useful")
        print("  %-10s %d of %d views at the limit, errors %s: %s" % (label, good, n, "within limits" if clean else "OVER a limit on some view", verdict))
    # C4
    tiles = [v[2] for (view, var, size), v in table.items() if size == SIZE and var in ("skip", "closed", "all") and v[2] > 0]
    print("\nC4 tile pass at %s: %s (limit %.2f ms)" % (SIZE, ("max %.3f ms" % max(tiles)) if tiles else "no reading", LIMIT_TILE_MS))
    # C5
    print("\nC5 reconstruction (u4all / u4hm, limit %.2f):" % LIMIT_RECON)
    for key in sorted(levels):
        m = re.match(r"culllevels\.(V-[a-z0-9]+)\.(turn\d+)\.u4all\.(ghost_move|trail_px|shimmer)$", key)
        if m:
            base = levels.get(key.replace(".u4all.", ".u4hm."), NAN)
            print("  %-10s %-7s %-10s all %.5f  hm %.5f  ratio %.2f  %s" % (m.group(1), m.group(2), m.group(3), levels[key], base, levels[key] / base if base else NAN,
                                                                         "ok" if base and levels[key] <= LIMIT_RECON * base else "OVER"))
    return 0


if __name__ == "__main__" and len(sys.argv) > 1 and sys.argv[1] == "summary":
    sys.exit(verdict_table())

# ------------------------------------------------------------------------------------------------------------------------------------------------
# The series (importing this module registers them)
# ------------------------------------------------------------------------------------------------------------------------------------------------
import numpy as np  # noqa: E402,F401

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402


def horizon_job():
    """A ground view along the horizon (pitch 1 degree): the decks reach out to the horizon, tens to hundreds of km away, under a second layer above."""
    lat, lon = L.view_site()
    up = np.array([math.cos(math.radians(lat)) * math.cos(math.radians(lon)), math.cos(math.radians(lat)) * math.sin(math.radians(lon)), math.sin(math.radians(lat))])
    cam = pr.Camera(up * (L.R + 2.0), 0.0, 1.0, 640, 360, 60.0)
    low = L.deck(1000.0, 3000.0, 0.012, 0.62, "low")
    high = L.deck(4000.0, 5500.0, 0.010, 0.55, "high")
    high["shift"] = 70.0
    return L.job("V-horizon", "color", cam, [low, high], 640, 360, sun=L.sun_vector(cam.pos, 120.0, 28.0), maxSteps=64, minStep=20.0, stepGrowth=0.0002,
                 jitter=0.0, earlyOut=0.01, sunSteps=4, mapWidth=L.MAP_W, mapHeight=L.MAP_H)


def sky3_job():
    """A ground view under three layers (a low deck, a middle deck, cirrus), each with its own map shift so that the layers are not copies of one map."""
    lat, lon = L.view_site()
    up = np.array([math.cos(math.radians(lat)) * math.cos(math.radians(lon)), math.cos(math.radians(lat)) * math.sin(math.radians(lon)), math.sin(math.radians(lat))])
    cam = pr.Camera(up * (L.R + 2.0), 0.0, 35.0, 640, 360, 60.0)
    low = L.deck(800.0, 2000.0, 0.012, 0.62, "low")
    mid = L.deck(3000.0, 4400.0, 0.010, 0.55, "mid")
    mid["shift"] = 70.0
    high = L.cirrus()
    high["shift"] = 140.0
    return L.job("V-sky3", "color", cam, [low, mid, high], 640, 360, sun=L.sun_vector(cam.pos, 120.0, 28.0), maxSteps=64, minStep=20.0, stepGrowth=0.0002,
                 jitter=0.0, earlyOut=0.01, sunSteps=4, mapWidth=L.MAP_W, mapHeight=L.MAP_H)


def write_inputs():
    """The T16 inputs: the view table of T01 plus the three-layer sky, with the maps of T01 (copied)."""
    jobs = L.write_inputs()
    os.makedirs(INPUT, exist_ok=True)
    for name in glob.glob(os.path.join(L.INPUT, "*.r8")):
        shutil.copy(name, INPUT)
    views = [j for j in jobs if j["id"] in ("V-ground", "V-inside", "V-above", "V-orbit-edge", "V-stack-below")] + [sky3_job(), horizon_job()]
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": views}, f)
    return views


def stages(scene, size, timeout=1500, proxy=False):
    jobs = os.path.join(INPUT, "jobs.json")
    if not os.path.isfile(jobs) or os.path.getmtime(jobs) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", scene, "-in", INPUT], size=size, proxy=proxy, timeout=timeout)]


def values_of(ctx, label="scene"):
    return {name: v[0] for name, v in ctx.player_metrics(label).items()}


def cost_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    values = values_of(ctx)
    table = cost_table(values)
    for (view, var, size), (ms, spread, tile) in sorted(table.items()):
        out.append(Metric.info("cull16.cost_ms.%s.%s.%s" % (view, var, size), ms))
        out.append(Metric.info("cull16.cost_spread.%s.%s.%s" % (view, var, size), spread))
        if tile > 0:
            out.append(Metric.at_most("cull16.tile_ms.%s.%s.%s" % (view, var, size), tile, LIMIT_TILE_MS).based("gate") if size == SIZE else
                       Metric.info("cull16.tile_ms.%s.%s.%s" % (view, var, size), tile))
    for block in (False, True):
        for name, base in PAIRS:
            var, bvar = prefixed(name, block), prefixed(base, block)
            for view in VIEWS:
                for size in ("1707x960", "2560x1440"):
                    b, v = table.get((view, bvar, size)), table.get((view, var, size))
                    if b and v and b[0] > 0:
                        out.append(Metric.info("cull16.ratio.%s.%s.%s" % (view, var, size), v[0] / b[0]))
    for key, v in values.items():
        if key.startswith("cullcost.refresh_"):
            out.append(Metric.info("cull16." + key[len("cullcost."):], v))
    return out


def quality_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    for name, v in sorted(values_of(ctx).items()):
        if name.startswith("cullq."):
            out.append(Metric.info("cullq16." + name[len("cullq."):], v))
    return out


def sweep_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    for name, v in sorted(values_of(ctx).items()):
        if name.startswith("cullsweep.") and name.endswith(".pop"):
            out.append(Metric.at_most("cullsweep16." + name[len("cullsweep."):], v, LIMIT_POP).based("owner"))
        elif name.startswith("cullsweep."):
            out.append(Metric.info("cullsweep16." + name[len("cullsweep."):], v))
    return out


def levels_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    values = values_of(ctx)
    for name, v in sorted(values.items()):
        if not name.startswith("culllevels."):
            continue
        out.append(Metric.info("lv16." + name[len("culllevels."):], v))
        if ".u4all." in name:
            base = values.get(name.replace(".u4all.", ".u4hm."), NAN)
            if base == base and base > 0:
                out.append(Metric.at_most("lv16.ratio." + name[len("culllevels."):], v / base, LIMIT_RECON).based("owner"))
    return out


register_series("T16-cost", stages("cull-cost", (1707, 960), 1200, True), cost_collect, exclusive=True, who="T16")
register_series("T16-quality", stages("cull-quality", (1280, 720)), quality_collect, exclusive=False, who="T16")
register_series("T16-sweep", stages("cull-sweep", (1280, 720)), sweep_collect, exclusive=False, who="T16")
register_series("T16-levels", stages("cull-levels", (1280, 720)), levels_collect, exclusive=False, who="T16")
