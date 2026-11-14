"""E40 fog and noise scale: the weather player's rows for the two problems of start 55 (no game start).

  python tools/build_weather_player.py --series e40-fn            build the player, run the jobs, print the table, write the pictures
  python tools/build_weather_player.py --series e40-fn --no-build
  python tools/weather_metrics/e40fn.py --selftest

Same scene, poses, light and display stage as e40fl.py (it imports its helpers). Pictures: .local/work/series-pictures/E40/e40-fn-*.png (natural) and -aid (optical depth).

Groups (job id prefix):
  cb   the cumulonimbus layer alone (map 0.66, type value 0.25) at the noise scales 8000 m (the cfg before this package) / 5000 / 3000 / 2000 / 1000 / 800 m at the noise amounts
       0.4 (cfg) and 1.0, both views, eight sites: cloud share and cloud/clear changes per row (e40fl.structure), the same through tools/weather_metrics/cloudstruct.py against
       the backdrop as the cloud-free partner (the measure the game's and the TVC pictures use), and the body width estimated from them (above view: the footprint of the view
       at the layer, about 22.9 km, times the share, over half the changes per row)
  fg   the weather deck at the vessel of start 55 (264 m, 18 degrees up), eight sites, four cases of the new rule: 'fair' (the rule's coverage 0: nothing draws), 'fog' (map 0.4056,
       type 0: the old rule's fair-weather draw, now only where the air is near saturation), 'light' (map 0.4056, type 0.5: light precipitation, Stratus), 'rain' (map 0.8595,
       type 0.8: nimbostratus). Closed share (tau >= 0.92) below the horizon (the ground region) and above it (the sky).

EXPECTED RESULTS, written before the first run:
  N1  cb, above view, amount 0.4: the changes per row fall with the scale; 8000 m gives under 2 per row (bodies of 32 to 64 km in a view 23 km wide), 800 m over 6 (start 55:
      10.4 at the ground view). The estimated body width at 3000 m lies between 8 and 35 km; at 8000 m over 35 km.
  N2  cb, amount 1.0 against 0.4 at the same scale: the cloud share rises (the share is map + coverage - 0.5 at amount 1), the changes per row within a factor 2.
  N3  nd (second run, after N1 showed that the 23 km view cannot hold a body of 10 to 30 km): the mean chord of the cloud along a row, straight down from 80 km (footprint 85 km),
      at the cfg's 8000 m / amount 0.4 and map 0.66 is over 35 km (the unit error of the cumulus layer: bodies of 4 to 8 times the scale); at 3000 m it lies between 10 and 30 km,
      the size of a cumulonimbus cell. If the cfg's chord is already 10 to 30 km the scale stays. The rain deck at 10 km and the cirrus at 15 km: a chord over 35 km (stratiform
      decks and cirrus fields of 40 to 120 km are not wrong); where one is under 15 km it is measured as too small and named.
  F1  fg: 'fair' closes 0.000 of the ground region and 0.000 of the sky; 'fog' closes at least 0.15 of the ground region and under 0.02 of the sky (E40 fl: 0.36 / 0.002);
      'light' closes under 0.10 of the ground region (the camera is above the Stratus base, E40 fl: 0.26 at map 0.4056, so this may fail: kept as a row); 'rain' closes at least
      0.5 of the sky (a deck overhead).
"""
import argparse
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import cloudstruct as cs  # noqa: E402
from weather_metrics import e40fl as fl  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import layers as lay  # noqa: E402

REPO = fl.REPO
BUILD = fl.BUILD
INPUT = os.path.join(BUILD, "e40fn-in")
RESULTS = os.path.join(BUILD, "e40fn-results.json")
SIZE = fl.SIZE
MAP_W, MAP_H = fl.MAP_W, fl.MAP_H
CB = fl.CB
RAIN = fl.RAIN
SPECS = fl.SPECS
SCALES = (8000.0, 5000.0, 3000.0, 2000.0, 1000.0, 800.0)
AMOUNTS = (0.4, 1.0)
SITES = 8
FOOTPRINT_KM = 22.9


def const_map(value):
    v = int(round(min(max(value, 0.0), 1.0) * 255.0))
    name = "k%03d.r8" % v
    os.makedirs(INPUT, exist_ok=True)
    np.full(MAP_W * MAP_H, v, dtype=np.uint8).tofile(os.path.join(INPUT, name))
    return name


def cb_jobs():
    jobs = []
    cb = SPECS[CB]
    for view in fl.VIEWS:
        for scale in SCALES:
            for amount in AMOUNTS:
                for site in range(SITES):
                    jid = "cb-%s-n%04d-a%03d-s%d" % (view, int(scale), int(round(amount * 100)), site)
                    jobs += fl.pair(jid, view, [e40o.job_layer(cb, const_map(0.66), const_map(0.25))], fl.PRODUCT, fl.scale_keys(scale, amount, name=CB, spec=cb), site=site)
    return jobs


FG_CASES = (("fair", 0.0, 0.0), ("fog", 0.4056, 0.0), ("light", 0.4056, 0.5), ("rain", 0.8595, 0.8))


def fg_jobs():
    jobs = []
    for name, cover, typ in FG_CASES:
        for site in range(SITES):
            jobs += fl.pair("fg-%s-s%d" % (name, site), "ground", [e40o.job_layer(SPECS[RAIN], const_map(cover), const_map(typ))], fl.PRODUCT, site=site)
    return jobs


# The nadir group: the camera straight down from 80 km (above every layer), 60 degrees of view = a footprint of about 85 km at the middle of the cumulonimbus layer, so
# that a body of 10 to 30 km is seen whole. The measure is the mean chord of the cloud along a picture row (cloud share x width / half the changes per row).
NADIR_H, NADIR_PITCH = 80000.0, -89.0
NADIR_SCALES = (8000.0, 5000.0, 3000.0, 2000.0, 1000.0)
NADIR_MAPS = (0.5, 0.66, 0.8)
NADIR_SITES = 8


def nadir_camera(site):
    lon = math.radians(site * 10.0)
    pos = np.array([math.cos(lon), math.sin(lon), 0.0]) * (fl.R + NADIR_H)
    return fl.pr.Camera(pos, 0.0, NADIR_PITCH, SIZE[0], SIZE[1], fl.FOV)


def nadir_pair(jid, layer, tuning, site):
    cam = nadir_camera(site)
    out = []
    for mode, suffix in (("lum", ""), ("aux", "-x")):
        args = dict(fl.PRODUCT)
        args.update(fl.light(cam))
        out.append(lay.job(jid + suffix, mode, cam, [layer], SIZE[0], SIZE[1], planet=fl.R, mapWidth=MAP_W, mapHeight=MAP_H, tuning=fl.keys(tuning), **args))
    return out


def nadir_jobs():
    jobs = []
    cb = SPECS[CB]
    for cover in NADIR_MAPS:
        for scale in NADIR_SCALES:
            for amount in AMOUNTS:
                for site in range(NADIR_SITES):
                    jid = "nd-cb-m%03d-n%04d-a%03d-s%d" % (int(round(cover * 100)), int(scale), int(round(amount * 100)), site)
                    jobs += nadir_pair(jid, e40o.job_layer(cb, const_map(cover), const_map(0.25)), fl.scale_keys(scale, amount, name=CB, spec=cb), site)
    # The other layers at the cfg's numbers (the rain deck and the cirrus: their bodies are the question of the brief, not changed unless these rows say so).
    for name, typ in ((RAIN, 0.5), ("Kerbin-cirrus", 0.0)):
        spec = SPECS[name]
        for site in range(NADIR_SITES):
            jobs += nadir_pair("nd-%s-s%d" % (name.split("-")[-1], site), e40o.job_layer(spec, const_map(0.66), const_map(typ)), [], site)
    return jobs


def chord_km(alpha, width_km=85.0):
    """(cloud share, changes per row, mean chord in km) of a nadir alpha plane (all rows, alpha >= 0.6)."""
    mask = alpha >= 0.6
    share = float(mask.mean())
    trans = float((mask[:, 1:] != mask[:, :-1]).sum(axis=1).mean())
    chord = float("inf") if trans < 0.05 and share > 0.05 else (share * width_km / (trans / 2.0) if trans >= 0.05 else float("nan"))
    return share, trans, chord


def build_jobs():
    return cb_jobs() + fg_jobs() + nadir_jobs()


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def body_width_km(share, trans):
    """The estimated width of one body in km from the share and the changes per row: share x footprint over half the changes (the changes are two per body)."""
    if trans < 0.05:
        return float("inf") if share > 0.05 else float("nan")
    return share * FOOTPRINT_KM / (trans / 2.0)


def collect(ctx):
    folder = ctx.folder("layers")
    out_dir = os.path.join(fl.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines, results = [], [], {}

    def pictures(jid, view, name):
        nat = fl.natural(folder, jid, view)
        fl.save_png(os.path.join(out_dir, "e40-fn-%s.png" % name), nat)
        fl.save_png(os.path.join(out_dir, "e40-fn-%s-aid.png" % name), fl.heat(fl.plane(folder, jid + "-x", "tau")))
        return nat

    lines.append("CUMULONIMBUS alone (map 0.66, type 0.25), mean over %d sites: cloud share / changes per row (e40fl measure) | cloudstruct share / changes | body width km (above)" % SITES)
    cb = {}
    for view in fl.VIEWS:
        clear = cs.to_grid(fl.backdrop(view))
        for amount in AMOUNTS:
            for scale in SCALES:
                sh, tr, csh, ctr = [], [], [], []
                for site in range(SITES):
                    jid = "cb-%s-n%04d-a%03d-s%d" % (view, int(scale), int(round(amount * 100)), site)
                    s, t = fl.structure(fl.plane(folder, jid + "-x", "alpha"), view)
                    sh.append(s)
                    tr.append(t)
                    nat = fl.natural(folder, jid, view)
                    a, b = cs.measure(cs.to_grid(nat), clear)
                    csh.append(a)
                    ctr.append(b)
                    if site in (0, 3):
                        pictures(jid, view, jid)
                row = (float(np.mean(sh)), float(np.mean(tr)), float(np.mean(csh)), float(np.mean(ctr)))
                cb["%s-n%04d-a%03d" % (view, int(scale), int(round(amount * 100)))] = row
                width = body_width_km(row[0], row[1]) if view == "above" else float("nan")
                lines.append("   %-6s amount %.1f scale %5.0f m: %.2f / %5.1f | %.2f / %5.1f | %s" % (view, amount, scale, row[0], row[1], row[2], row[3], "%.1f" % width if view == "above" else "-"))
                key = "e40fn.cb.%s.n%04d.a%03d" % (view, int(scale), int(round(amount * 100)))
                metrics.append(Metric.info(key + ".share", row[0]))
                metrics.append(Metric.info(key + ".transitions", row[1]))
                metrics.append(Metric.info(key + ".cloudstruct_share", row[2]))
                metrics.append(Metric.info(key + ".cloudstruct_transitions", row[3]))
                if view == "above":
                    metrics.append(Metric.info(key + ".body_width_km", width if math.isfinite(width) else 999.0))
    results["cb"] = cb

    lines.append("WEATHER DECK at the vessel (264 m, 18 degrees up), mean over %d sites: closed share (tau >= %.2f) below the horizon (ground region) / above it (sky)" % (SITES, fl.CLOSED))
    fg = {}
    elev = fl.elevation("ground")[:, SIZE[0] // 2]
    below, above = elev < -1.0, elev > 1.0
    for name, cover, typ in FG_CASES:
        b, a = [], []
        for site in range(SITES):
            jid = "fg-%s-s%d" % (name, site)
            tau = fl.plane(folder, jid + "-x", "tau")
            closed = tau >= fl.CLOSED
            b.append(float(closed[below].mean()))
            a.append(float(closed[above].mean()))
            if site in (0, 4):
                pictures(jid, "ground", jid)
        fg[name] = (float(np.mean(b)), float(np.mean(a)))
        lines.append("   %-6s (map %.4f, type %.2f): ground region %.3f   sky %.3f" % (name, cover, typ, fg[name][0], fg[name][1]))
        metrics.append(Metric.info("e40fn.F1.%s.ground_closed" % name, fg[name][0]))
        metrics.append(Metric.info("e40fn.F1.%s.sky_closed" % name, fg[name][1]))
    results["fg"] = fg

    lines.append("NADIR (80 km straight down, footprint 85 km), mean over %d sites: share / changes per row / mean chord km" % NADIR_SITES)
    nd = {}
    for cover in NADIR_MAPS:
        for amount in AMOUNTS:
            for scale in NADIR_SCALES:
                rows = []
                for site in range(NADIR_SITES):
                    jid = "nd-cb-m%03d-n%04d-a%03d-s%d" % (int(round(cover * 100)), int(scale), int(round(amount * 100)), site)
                    rows.append(chord_km(fl.plane(folder, jid + "-x", "alpha")))
                    if site == 0 and cover in (0.66, 0.8):
                        m = fl.rgba(folder, jid)
                        a = np.clip(m[..., 3:4], 0.0, 1.0)
                        nat = np.clip(fl.encode(m[..., :3] / np.maximum(a, 1e-4)) * a + fl.SEA * (1.0 - a), 0.0, 1.0)
                        fl.save_png(os.path.join(out_dir, "e40-fn-%s.png" % jid), nat)
                share, trans = float(np.mean([r[0] for r in rows])), float(np.mean([r[1] for r in rows]))
                chords = [r[2] for r in rows if math.isfinite(r[2])]
                chord = float(np.mean(chords)) if chords else float("nan")
                nd["m%03d-n%04d-a%03d" % (int(round(cover * 100)), int(scale), int(round(amount * 100)))] = (share, trans, chord)
                lines.append("   cb map %.2f amount %.1f scale %5.0f m: %.2f / %5.1f / %5.1f km" % (cover, amount, scale, share, trans, chord))
                key = "e40fn.nd.cb.m%03d.a%03d.n%04d" % (int(round(cover * 100)), int(round(amount * 100)), int(scale))
                metrics.append(Metric.info(key + ".share", share))
                metrics.append(Metric.info(key + ".transitions", trans))
                metrics.append(Metric.info(key + ".chord_km", chord if math.isfinite(chord) else 999.0))
    for name in ("weather-rain", "cirrus"):
        rows = [chord_km(fl.plane(folder, "nd-%s-s%d-x" % (name.split("-")[-1], s), "alpha")) for s in range(NADIR_SITES)]
        chords = [r[2] for r in rows if math.isfinite(r[2])]
        lines.append("   %-14s at the cfg: %.2f / %5.1f / %s km" % (name, float(np.mean([r[0] for r in rows])), float(np.mean([r[1] for r in rows])),
                                                                     "%.1f" % float(np.mean(chords)) if chords else "nan (no cloud or no edge in view)"))
        metrics.append(Metric.info("e40fn.nd.%s.share" % name, float(np.mean([r[0] for r in rows]))))
        metrics.append(Metric.info("e40fn.nd.%s.chord_km" % name, float(np.mean(chords)) if chords else 999.0))
    results["nd"] = nd
    results["lines"] = lines
    with open(RESULTS, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("\n".join(lines))
    print("pictures: %s (e40-fn-*.png, natural and -aid)" % out_dir)
    return metrics


def stages():
    if not os.path.isfile(os.path.join(INPUT, "jobs.json")) or os.path.getmtime(os.path.join(INPUT, "jobs.json")) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("layers", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False, timeout=1800)]


register_series("e40-fn", stages(), collect, exclusive=False, who="e40-fn")


def selftest():
    jobs = build_jobs()
    ids = [j["id"] for j in jobs]
    assert len(ids) == len(set(ids)), "job ids repeat"
    assert "cb-above-n3000-a040-s7-x" in ids and "fg-fair-s0" in ids and "fg-rain-s7-x" in ids
    k = dict((d["key"], d["value"]) for d in [j for j in jobs if j["id"] == "cb-ground-n2000-a100-s1"][0]["tuning"])
    assert abs(k["cloud.layerNoiseSizeScale." + CB] * SPECS[CB]["scale"] - 2000.0) < 1e-6
    assert abs(k["cloud.layerNoiseAmountScale." + CB] * SPECS[CB]["noise"] - 1.0) < 1e-9
    assert body_width_km(0.4, 2.0) == 0.4 * FOOTPRINT_KM
    print("selftest passed (%d jobs)" % len(jobs))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--write", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.write:
        print(len(write_inputs()), "jobs")
