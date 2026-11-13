"""E40 FL-FIX: the cumulus layer's noise scale (300 / 500 / 700 m) judged with ONE picture measure for the player, the game and the TVC frames (cloudstruct.py).

  python tools/build_weather_player.py --series e40-cmp
  python tools/build_weather_player.py --series e40-cmp --no-build

The player draws the cumulus layer alone (map 0.665, type 0, the light of K-T2d start 52) from the above pose at four sites at 300 m / 1.0, 500 m / 1.0, 700 m / 1.0 and
700 m / 0.7 (the look set C2 of the shipped base). Each natural picture is measured against the backdrop it was composed over (the cloud-free partner) with
cloudstruct.measure, and so are the game's above_low frames of starts 51, 52 and 53 (against their own 'off' frames) and the TVC reference frame (against the own system's
cloud-free frame of the same pose). The floor rows are the measure of two cloud-free frames of different starts, and the old colour mask on a cloud-free frame (the glint).

EXPECTED RESULTS, written before the first run (from a first reading of the saved pictures with the same measure, one site each): TVC 12.2 transitions per row at share 0.85;
game start 52 4.4 (the sheet), start 53 9.1 (500 m); player at 500 m 8 (two sites), at 700 m 5.9, at 1000 m 3.4: the transitions fall with the scale, 500 m is the nearer to
TVC; 300 m is nearer still (above 8) at a share not lower than 0.55. The floors are 0 for the player (one backdrop) and 0 between the game's off frames.
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import cloudstruct as cs  # noqa: E402
from weather_metrics import e40fl  # noqa: E402
from weather_metrics import e40o  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
INPUT = os.path.join(e40fl.BUILD, "e40cmp-in")
SITES = 4
CONFIGS = (("n0300-a100", 300.0, 1.0), ("n0500-a100", 500.0, 1.0), ("n0700-a100", 700.0, 1.0), ("n0700-a070", 700.0, 0.7))
PICS = os.path.join(".local", "work", "series-pictures")


def build_jobs():
    # const_map writes the maps into e40fl.INPUT: this series' own folder for the time of the build.
    saved, e40fl.INPUT = e40fl.INPUT, INPUT
    try:
        jobs = []
        cu = e40fl.SPECS[e40fl.CUMULUS]
        for name, scale, amount in CONFIGS:
            for site in range(SITES):
                jobs += e40fl.pair("cmp-%s-s%d" % (name, site), "above", [e40o.job_layer(cu, e40fl.const_map(0.665), e40fl.const_map(0.0))], e40fl.PRODUCT,
                                   e40fl.scale_keys(scale, amount), site=site)
    finally:
        e40fl.INPUT = saved
    return jobs


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def stages():
    write_inputs()
    return [Stage("layers", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False, timeout=1800)]


def picture(*parts):
    return cs.load(os.path.join(e40o.main_repo(), PICS, *parts))


def collect(ctx):
    folder = ctx.folder("layers")
    out_dir = os.path.join(e40o.main_repo(), PICS, "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines = [], []
    backdrop = cs.to_grid(e40fl.backdrop("above"))
    lines.append("COMMON MEASURE (cloudstruct: 320 x 180 box grid, difference from the cloud-free partner over 0.12, rows 45 to 100 percent, vessel columns cut): share / transitions per row")
    for name, scale, amount in CONFIGS:
        rows = []
        for site in range(SITES):
            jid = "cmp-%s-s%d" % (name, site)
            nat = e40fl.natural(folder, jid, "above")
            e40fl.save_png(os.path.join(out_dir, "e40-cmp-above-%s-s%d.png" % (name, site)), nat)
            rows.append(cs.measure(cs.to_grid(nat), backdrop))
        share, trans = float(np.mean([r[0] for r in rows])), float(np.mean([r[1] for r in rows]))
        lines.append("   player %4.0f m / %.1f   mean of %d sites  %.2f / %5.1f   (sites: %s)" % (scale, amount, SITES, share, trans, ", ".join("%.2f/%.1f" % r for r in rows)))
        metrics.append(Metric.info("e40cmp.player.%s.share" % name, share))
        metrics.append(Metric.info("e40cmp.player.%s.transitions" % name, trans))
    refs = (
        ("tvc", "TVC reference frame", ("K-T2", "t09-tvc1707-above-tvc-1707x960-natural.png"), ("K-T2", "t09-own1707-above-off-1707x960-natural.png")),
        ("game51", "game start 51 above_low kC0", ("E40", "e40-51-above_low-kC0-1707x960-natural-half.png"), ("E40", "e40-51-above_low-off-1707x960-natural-half.png")),
        ("game52", "game start 52 above_low kC0", ("E40", "e40-52-above_low-kC0-1707x960-natural-half.png"), ("E40", "e40-52-above_low-off-1707x960-natural-half.png")),
        ("game53", "game start 53 above_low kC0", ("E40", "e40-fl-game53-above_low-kC0-1707x960-natural-half.png"), ("K-T2d", "t09-order52-above_low-off-1707x960-natural-half.png")),
    )
    for key, label, pic, clear in refs:
        try:
            share, trans = cs.measure(picture(*pic), picture(*clear))
        except (IOError, OSError) as e:
            lines.append("   %-34s not found (%s)" % (label, e))
            continue
        lines.append("   %-34s %.2f / %5.1f" % (label, share, trans))
        metrics.append(Metric.info("e40cmp.%s.share" % key, share))
        metrics.append(Metric.info("e40cmp.%s.transitions" % key, trans))
    try:
        a = picture("E40", "e40-52-above_low-off-1707x960-natural-half.png")
        b = picture("K-T2d", "t09-order52-above_low-off-1707x960-natural-half.png")
        c = picture("K-T2", "t09-own1707-above-off-1707x960-natural.png")
        floor = cs.measure(a, b)
        glint = cs.legacy(c)
        lines.append("   floor: cloud-free start 52 against start 53 %.2f / %.1f; the player's backdrop against itself 0.00 / 0.0; old colour mask on the cloud-free frame (glint) %.2f / %.1f"
                     % (floor + glint))
        metrics.append(Metric.info("e40cmp.floor.game.transitions", floor[1]))
        metrics.append(Metric.info("e40cmp.floor.legacy_glint.transitions", glint[1]))
    except (IOError, OSError) as e:
        lines.append("   floor: not found (%s)" % e)
    print("\n".join(lines))
    return metrics


register_series("e40-cmp", stages(), collect, exclusive=False, who="e40-cmp")
