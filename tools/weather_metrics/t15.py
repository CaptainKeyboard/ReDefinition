"""The series of package T15 (cheap forms of the entry refinement, the direct / scatter split of the deep storm cell, the cost of the cloud
resolution levels), run through tools/build_weather_player.py --series <name>:

  T15-bisect-cost     timing run (exclusive), scene 'bisect-cost': ms of the cloud pass at 1707x960 per view for the refinement off, on every ray, 2 to 4 bisection
                      steps, in the full mode (plain), the 4 x 4 block mode (recon4: cloudResolution 4) and the edge mode (edge: the refinement only in the flagged
                      blocks, the bisection steps elsewhere), and for the resolution levels 2 and 3; every variant twice.
  T15-bisect-quality  scene 'bisect-quality': per view the error of each variant against the reference (the mean of four full marches with the refinement on every
                      ray) in the band around the contour, in the thick interior and in the empty sky, the seam of the edge mode, and the pictures
                      t15-<view>-<variant>.png, t15-<view>-aid-error-<group>.png in the player's output folder (refine15).
  T15-bisect-levels   scene 'bisect-levels': the resolution levels 1 to 4 (and level 4 with the bisection and with the refinement) through two turns:
                      ghosting, trail and shimmer.
  T15-split           scene 'light': the deep storm cell (radius 4000 m, 8 km wide, as in the T14 storm cells) as direct light only (single scattering), multiple
                      scattering, ambient and total, against the T11 reference renderer's order 1 (direct) and the rest of its total; pictures t15-<cell>-*.png;
                      and the combinations of the sun march 9000 m with more ambient taps and the light cache against the reference base radiance.

THRESHOLDS and their sources (fixed BEFORE the first run; a sweep changes the number of steps only, at most 3 sweeps):
  group           the three variants that bound a group: full (the refinement on every ray), none (no refinement), and the cheap candidates of the group
                  plain   full, none; candidates b2, b3, b4  (2, 3, 4 bisection steps, everywhere)
                  recon4  u4full, u4none; candidates u4b2, u4b3, u4b4  (the same at cloudResolution 4)
                  edge    eN (the edge mode, refinement in every block), eNone (the edge mode, no refinement); candidates eB0, eB2, eB3, eB4 (refinement only in
                          the blocks the edge mask flags; elsewhere 0, 2, 3, 4 bisection steps)
  interior        interior_err(candidate) <= 1.15 x interior_err(full of the group)     owner order of the package: within 15 percent of the full refinement
                                                                                        (the T12 brief's "within 15 percent of full resolution")
  band            band_rms(candidate) <= 1.15 x band_rms(full of the group)             the same
  added cost      (ms(candidate) - ms(none)) <= 0.40 x (ms(full) - ms(none))             owner order of the package: at most 40 percent of the refinement's added cost
                                                                                        (ms: the median of two passes of 50 frames at 1707 x 960, exclusive)
  empty sky       empty_leak(candidate) <= max(0.0001, 1.15 x empty_leak(full))          T12 brief: the empty sky exact (0.0001), never worse than the full refinement
  seam (edge)     seam(candidate) <= 0.01                                                T12 row (the border between a flagged and an unflagged block)
  A candidate GOES when interior, band, added cost and empty sky hold on the views where the refinement has anything to gain (the views where
  interior_err(none) > 1.15 x interior_err(full) or band_rms(none) > 1.15 x band_rms(full)); the table prints the verdict per view and per candidate.
  Resolution levels, the direct / scatter split and the combinations carry no pass/fail limit: their numbers are for the owner's decision.

    python tools/weather_metrics/t15.py summary         # the verdict table from the latest T15-bisect-cost and T15-bisect-quality runs
    python tools/weather_metrics/t15.py refs            # the reference renders of the storm cells (total, direct) into build/parity-ref-holdout
"""
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
# Sweep 1: 2, 3, 4 bisection steps. Sweep 2 (cumulative: the table of sweep 1 plus the bisection with the step budget, c0, c3, u4c0, u4c3, eC3).
SWEEP = os.environ.get("T15_SWEEP", "2")
NAN = float("nan")

VIEWS = ("ground", "inside", "above", "orbit-edge", "stack-below")
GROUPS = {
    "plain": dict(full="full", none="none", cands=("b2", "b3", "b4", "c0", "c3")),
    "recon4": dict(full="u4full", none="u4none", cands=("u4b2", "u4b3", "u4b4", "u4c0", "u4c3")),
    "edge": dict(full="eN", none="eNone", cands=("eB0", "eB2", "eB3", "eB4", "eC3")),
}
LIMIT_BAND = LIMIT_INTERIOR = 1.15
LIMIT_COST = 0.40
LIMIT_LEAK = 0.0001
LIMIT_SEAM = 0.01
# Below this much added cost of the full refinement a view has nothing to save and the ratio of added costs is noise.
MIN_ADDED_MS = 0.05


def metrics_of(path):
    """{name: value} of a series json written by the runner."""
    with open(path, encoding="utf-8") as f:
        return {m["metric"]: m["value"] for m in json.load(f)}


def latest(series):
    files = sorted(glob.glob(os.path.join(BUILD, series + "-2*.json")))
    return files[-1] if files else None


def cost_table(values):
    """{(view, variant): (mean ms, spread)} from the two passes of the cost scene (tags bisectcost and bisectcostb)."""
    rows = {}
    for k, v in values.items():
        m = re.match(r"bisectcostb?\.total\.(V-[a-z-]+)\.([A-Za-z0-9]+)\.1707x960$", k)
        if m:
            rows.setdefault((m.group(1)[2:], m.group(2)), []).append(v)
    return {key: (sum(v) / len(v), max(v) - min(v)) for key, v in rows.items()}


def added_cost_ratio(table, view, group, var):
    g = GROUPS[group]
    full, none, cand = (table.get((view, name), (NAN, NAN))[0] for name in (g["full"], g["none"], var))
    denominator = full - none
    if denominator != denominator or denominator < MIN_ADDED_MS:
        return NAN
    return (cand - none) / denominator


# ------------------------------------------------------------------------------------------------------------------------------------------------
# Running as a script: the verdict table
# ------------------------------------------------------------------------------------------------------------------------------------------------
def verdict_table():
    cost_path, quality_path = latest("T15-bisect-cost"), latest("T15-bisect-quality")
    if not cost_path or not quality_path:
        print("no T15-bisect-cost / T15-bisect-quality result in", BUILD)
        return 2
    table = cost_table(metrics_of(cost_path))
    q = metrics_of(quality_path)

    def qv(view, var, field):
        return q.get("refq15.%s.%s.%s" % (view, var, field), q.get("bisq.%s.%s.%s" % (view, var, field), NAN))

    print("cost: %s\nquality: %s\n" % (os.path.basename(cost_path), os.path.basename(quality_path)))
    print("%-7s %-11s %-6s %9s %9s %9s %9s %9s %9s %7s  %s" % ("group", "view", "var", "int/full", "band/full", "added", "leak", "seam", "gain?", "recov", "verdict"))
    goes = {}
    for group, g in GROUPS.items():
        for view in VIEWS:
            fi, fb, fl = qv(view, g["full"], "interior_err"), qv(view, g["full"], "band_rms"), qv(view, g["full"], "empty_leak")
            ni, nb = qv(view, g["none"], "interior_err"), qv(view, g["none"], "band_rms")
            gain = (ni > LIMIT_INTERIOR * fi) or (nb > LIMIT_BAND * fb)
            for var in g["cands"]:
                ci, cb = qv(view, var, "interior_err"), qv(view, var, "band_rms")
                if ci != ci:
                    continue
                cl, seam = qv(view, var, "empty_leak"), qv(view, var, "seam")
                ri, rb = ci / fi if fi else NAN, cb / fb if fb else NAN
                added = added_cost_ratio(table, view, group, var)
                recov = ((ni - ci) / (ni - fi)) if ni != fi else NAN
                ok = (ri <= LIMIT_INTERIOR and rb <= LIMIT_BAND and (added == added and added <= LIMIT_COST)
                      and cl <= max(LIMIT_LEAK, LIMIT_BAND * fl) and (group != "edge" or seam != seam or seam <= LIMIT_SEAM))
                if gain:
                    goes.setdefault((group, var), []).append(ok)
                print("%-7s %-11s %-6s %9.2f %9.2f %9.2f %9.5f %9.4f %9s %7.2f  %s" % (group, view, var, ri, rb, added, cl, seam if seam == seam else 0.0,
                                                                                         "gain" if gain else "none", recov, "ok" if ok else "FAIL"))
    print("\nper candidate on the views with a gain:")
    for (group, var), flags in sorted(goes.items()):
        print("  %-7s %-6s  %d of %d views  %s" % (group, var, sum(flags), len(flags), "GO" if all(flags) else "NO GO"))
    return 0


if __name__ == "__main__" and len(sys.argv) > 1 and sys.argv[1] == "summary":
    sys.exit(verdict_table())

# ------------------------------------------------------------------------------------------------------------------------------------------------
# The series (importing this module registers them)
# ------------------------------------------------------------------------------------------------------------------------------------------------
import numpy as np  # noqa: E402

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import light as Lt  # noqa: E402
from weather_metrics import light_holdout as H  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import parity_run as run  # noqa: E402
from weather_metrics import t14  # noqa: E402


def values_of(ctx, label="scene"):
    return {name: v[0] for name, v in ctx.player_metrics(label).items()}


def cost_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    values = values_of(ctx)
    table = cost_table(values)
    views = sorted({k[0] for k in table})
    for (view, var), (ms, spread) in sorted(table.items()):
        out.append(Metric.info("refine15.cost_ms.%s.%s" % (view, var), ms))
        out.append(Metric.info("refine15.cost_spread.%s.%s" % (view, var), spread))
    for group in GROUPS:
        for view in views:
            for var in GROUPS[group]["cands"]:
                if (view, var) not in table:
                    continue
                ratio = added_cost_ratio(table, view, group, var)
                if ratio == ratio:
                    out.append(Metric.at_most("refine15.added_cost.%s.%s" % (view, var), ratio, LIMIT_COST).based("owner"))
                else:
                    out.append(Metric.info("refine15.added_cost.%s.%s" % (view, var), ratio))
    # The resolution levels: lv1 is 'none', lv2 and lv3 the block modes, lv4 'u4none'; the ratio to level 1.
    for view in views:
        base = table.get((view, "none"), (NAN, NAN))[0]
        for level, var in ((2, "lv2"), (3, "lv3"), (4, "u4none")):
            if (view, var) in table:
                out.append(Metric.info("levels.cost_ms.%s.L%d" % (view, level), table[(view, var)][0]))
                out.append(Metric.info("levels.cost_ratio.%s.L%d" % (view, level), table[(view, var)][0] / base))
        out.append(Metric.info("levels.cost_ms.%s.L1" % view, base))
    return out


def quality_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    values = values_of(ctx)

    def qv(view, var, field):
        return values.get("bisq.%s.%s.%s" % (view, var, field), NAN)

    for view in VIEWS:
        for name in {k.split(".")[2] for k in values if k.startswith("bisq.%s." % view) and k.split(".")[2] != "ref"}:
            for field in ("band_rms", "interior_err", "empty_leak", "band_flicker", "seam", "mask_share"):
                v = qv(view, name, field)
                if v == v:
                    out.append(Metric.info("refq15.%s.%s.%s" % (view, name, field), v))
        for group, g in GROUPS.items():
            fi, fb, fl = qv(view, g["full"], "interior_err"), qv(view, g["full"], "band_rms"), qv(view, g["full"], "empty_leak")
            ni, nb = qv(view, g["none"], "interior_err"), qv(view, g["none"], "band_rms")
            gain = (ni > LIMIT_INTERIOR * fi) or (nb > LIMIT_BAND * fb)
            out.append(Metric.info("refine15.gain_view.%s.%s" % (view, group), 1.0 if gain else 0.0))
            for var in g["cands"]:
                ci, cb, cl, seam = qv(view, var, "interior_err"), qv(view, var, "band_rms"), qv(view, var, "empty_leak"), qv(view, var, "seam")
                if ci != ci:
                    continue
                recov = (ni - ci) / (ni - fi) if ni != fi else NAN
                out.append(Metric.info("refine15.interior_recovery.%s.%s" % (view, var), recov))
                out.append(Metric.at_most("refine15.interior_vs_full.%s.%s" % (view, var), ci / fi, LIMIT_INTERIOR).based("owner"))
                out.append(Metric.at_most("refine15.band_vs_full.%s.%s" % (view, var), cb / fb, LIMIT_BAND).based("owner"))
                out.append(Metric.at_most("refine15.empty_leak.%s.%s" % (view, var), cl, max(LIMIT_LEAK, LIMIT_BAND * fl)).based("gate"))
                if group == "edge" and seam == seam:
                    out.append(Metric.at_most("refine15.seam.%s.%s" % (view, var), seam, LIMIT_SEAM).based("gate"))
    return out


def levels_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    for name, v in sorted(values_of(ctx).items()):
        if name.startswith("levels."):
            out.append(Metric.info("lv15." + name[len("levels."):], v))
    return out


register_series("T15-bisect-cost", [Stage("scene", ["-mode", "scene", "-scene", "bisect-cost", "-sweep", SWEEP, "-in", L.INPUT], size=(1707, 960), proxy=True,
                                          timeout=1500)], cost_collect, exclusive=True, who="T15")
register_series("T15-bisect-quality", [Stage("scene", ["-mode", "scene", "-scene", "bisect-quality", "-sweep", SWEEP, "-in", L.INPUT], size=(1280, 720),
                                             proxy=False, timeout=1500)], quality_collect, exclusive=False, who="T15")
register_series("T15-bisect-levels", [Stage("scene", ["-mode", "scene", "-scene", "bisect-levels", "-in", L.INPUT], size=(1280, 720), proxy=False,
                                            timeout=1500)], levels_collect, exclusive=False, who="T15")


# ------------------------------------------------------------------------------------------------------------------------------------------------
# The direct / scatter split of the deep storm cell and the base radiance combinations
# ------------------------------------------------------------------------------------------------------------------------------------------------
SPLIT_INPUT = os.path.join(BUILD, "T15-split-in")
SPLIT_PICTURES = os.path.join(BUILD, "T15-pictures")
STORM_CELLS = tuple(c for c in t14.LOOK_CELLS if c[0].startswith("storm"))
# The sun march and the ambient taps: name, light job overrides, sun march (m). 'default' is the shipped 4 km without taps (the defaults stay as they are).
COMBOS = (
    ("default", {}, 4000.0),
    ("sun9", {}, 9000.0),
    ("sun9ao2", dict(aoTaps=1, upReuse=2), 9000.0),
    ("sun9ao0", dict(aoTaps=1, upReuse=0), 9000.0),
    ("sun9ao0up4", dict(aoTaps=1, upReuse=0, upSteps=4), 9000.0),
    ("sun9ao0up8", dict(aoTaps=1, upReuse=0, upSteps=8), 9000.0),
    ("sun9ao0s8", dict(aoTaps=1, upReuse=0, sunSteps=8), 9000.0),
    ("sun9cacheao0", dict(aoTaps=1, upReuse=0, cache=1), 9000.0),
    ("sun9cacheao0up4", dict(aoTaps=1, upReuse=0, upSteps=4, cache=1), 9000.0),
)
# Single scattering only: one octave, no diffusion term (the ambient comes separately in the aid-4 channels).
DIRECT_ONLY = dict(octaves=1, diffusion=0.0)
SPLIT_FOR = ("default", "sun9", "sun9ao0up4")


def storm_reference_direct(spec):
    """The reference renderer's single scattering (order 1) of a storm cell, saved next to the total (H.REF_DIR/<name>-direct.npz)."""
    name, az, el, radius, sigma = spec
    q = dict(run.QUALITY["full"])
    q["order"] = 1
    field = pr.scene_single_cloud(radius_m=radius, centre_alt=t14.centre_alt(spec), sigma=sigma)
    cam = pr.Camera([0, -8000.0 * H.distance_scale(radius), run.F + t14.centre_alt(spec)], 0, 0, H.HOLD_W, int(H.HOLD_W * 0.75), 16)
    sun = pr.Sun.at(cam.pos, az, el)
    res = run.renderer(field, sun, q, seed=5).render(cam)
    np.savez(os.path.join(H.REF_DIR, name + "-direct.npz"), alpha=res.alpha, cloud=res.cloud)


def references():
    os.makedirs(H.REF_DIR, exist_ok=True)
    for spec in STORM_CELLS:
        if not os.path.isfile(os.path.join(H.REF_DIR, spec[0] + ".npz")):
            t14.look_reference(spec)
            print("reference", spec[0], flush=True)
        if not os.path.isfile(os.path.join(H.REF_DIR, spec[0] + "-direct.npz")):
            storm_reference_direct(spec)
            print("reference direct", spec[0], flush=True)


def split_jobs():
    jobs = []
    for spec in STORM_CELLS:
        for vname, over, march in COMBOS:
            for aid in (0, 4):
                o = dict(over)
                if o.get("cache"):
                    o.update(cacheMin=[-8000.0, -8000.0, Lt.R + t14.STORM_ALT_M - 8000.0], cacheSize=[16000.0] * 3, cacheFillSteps=12)
                jobs.append(t14.look_job("%s_%s_%d" % (spec[0], vname, aid), spec, aid=aid, layer_kw=dict(sunMarch=march), **o))
            if vname in SPLIT_FOR:
                o = dict(over)
                o.update(DIRECT_ONLY)
                if o.get("cache"):
                    o.update(cacheMin=[-8000.0, -8000.0, Lt.R + t14.STORM_ALT_M - 8000.0], cacheSize=[16000.0] * 3, cacheFillSteps=12)
                jobs.append(t14.look_job("%s_%s_do" % (spec[0], vname), spec, aid=4, layer_kw=dict(sunMarch=march), **o))
    return jobs


def split_stages():
    os.makedirs(SPLIT_INPUT, exist_ok=True)
    with open(os.path.join(SPLIT_INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": split_jobs()}, f)
    return [Stage("scene", ["-mode", "scene", "-scene", "light", "-in", SPLIT_INPUT], size=(1280, 720), proxy=False)]


def load_ref(name):
    path = os.path.join(H.REF_DIR, name + ".npz")
    if not os.path.isfile(path):
        return None
    z = np.load(path)
    return z["alpha"], z["cloud"]


def grey(plane_r, plane_g, plane_b):
    return (plane_r + plane_g + plane_b) / 3.0


def components(folder, cell, variant):
    """(alpha, direct, multiple, ambient, total) of one variant of a cell, or None. Direct: the single scattering of the DIRECT_ONLY job's aid-4 channel;
    multiple: the rest of the direct channel of the variant's own aid-4 job (the octaves above the first and the diffusion term); ambient: its blue channel."""
    p = lambda jid, c: Lt.plane(folder, jid, c)
    need = ["%s_%s_0" % (cell, variant), "%s_%s_4" % (cell, variant), "%s_%s_do" % (cell, variant)]
    if not all(os.path.isfile(os.path.join(folder, n + "-r.npy")) for n in need):
        return None
    total_img = Lt.cell_img(folder, need[0])[0]
    a = p(need[1], "a")
    d_all, amb = p(need[1], "r"), p(need[1], "b")
    d_single = p(need[2], "r")
    return a, d_single, np.maximum(d_all - d_single, 0.0), amb, total_img.cloud


def look_collect(ctx):
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    os.makedirs(SPLIT_PICTURES, exist_ok=True)
    sky = Lt.DAY_SKY
    for spec in STORM_CELLS:
        cell = spec[0]
        ref_total, ref_direct = load_ref(cell), load_ref(cell + "-direct")
        base_ref = None
        if ref_total is not None:
            base_ref, top_ref = run.base_top(type("R", (), {"alpha": ref_total[0], "cloud": ref_total[1]}))
            out.append(Metric.info("split.%s.reference.base" % cell, base_ref))
            out.append(Metric.info("split.%s.reference.top" % cell, top_ref))
        rows = {}
        for vname, _, _ in COMBOS:
            jid = "%s_%s_0" % (cell, vname)
            if not os.path.isfile(os.path.join(folder, jid + "-r.npy")):
                continue
            img = Lt.cell_img(folder, jid)[0]
            base, top = run.base_top(img)
            rows[vname] = img
            out.append(Metric.info("combo.%s.%s.base" % (cell, vname), base))
            out.append(Metric.info("combo.%s.%s.top" % (cell, vname), top))
            if base_ref:
                out.append(Metric.info("combo.%s.%s.base_over_reference" % (cell, vname), base / base_ref))
                rm = Lt.region_means(ref_total[0], ref_total[1], ref_total[0])
                om = Lt.region_means(img.alpha, img.cloud, ref_total[0])
                for region in rm:
                    out.append(Metric.info("combo.%s.%s.ratio_%s" % (cell, vname, region), om[region] / max(rm[region], 1e-9)))
        # The split: ours against the reference, region by region (core, thirds, rim) and as base / top.
        for vname in SPLIT_FOR:
            comp = components(folder, cell, vname)
            if comp is None:
                continue
            a, d, ms, amb, total = comp
            checks = {"direct": d, "multiple": ms, "ambient": amb, "total_sum": d + ms + amb}
            out.append(Metric.info("split.%s.%s.sum_over_total" % (cell, vname), float(np.mean((d + ms + amb)[a > 0.5]) / max(np.mean(total[a > 0.5]), 1e-9))))
            if ref_total is None or ref_direct is None:
                continue
            ra, rtot = ref_total
            rdir = ref_direct[1]
            rscatter = np.maximum(rtot - rdir, 0.0)
            ours = {"direct": d, "scatter": ms + amb, "multiple": ms, "ambient": amb, "total": d + ms + amb}
            refs = {"direct": rdir, "scatter": rscatter, "total": rtot}
            for part in ("direct", "scatter", "total"):
                rm = Lt.region_means(ra, refs[part], ra)
                om = Lt.region_means(a, ours[part], ra)
                for region in rm:
                    out.append(Metric.info("split.%s.%s.%s.ratio_%s" % (cell, vname, part, region), om[region] / max(rm[region], 1e-12)))
                b_o, t_o = run.base_top(type("R", (), {"alpha": a, "cloud": ours[part]}))
                b_r, t_r = run.base_top(type("R", (), {"alpha": ra, "cloud": refs[part]}))
                out.append(Metric.info("split.%s.%s.%s.base_over_ref" % (cell, vname, part), b_o / max(b_r, 1e-12)))
                out.append(Metric.info("split.%s.%s.%s.top_over_ref" % (cell, vname, part), t_o / max(t_r, 1e-12)))
            out.append(Metric.info("split.%s.%s.multiple_share_of_scatter" % (cell, vname), float(np.mean(ms[a > 0.5]) / max(np.mean((ms + amb)[a > 0.5]), 1e-12))))
            # Pictures: natural (the component over the day sky's backdrop) and aid (the component alone, one scale for all panels).
            h, w = a.shape
            scale = max(float(np.percentile(rtot[ra > 0.5], 99)), 1e-6)
            names = [("direct", d, rdir), ("scatter", ms + amb, rscatter), ("total", d + ms + amb, rtot)]
            nat, aid, diff, labels = [], [], [], []
            for label, mine, theirs in names:
                for tag, arr, alpha in (("ours", mine, a), ("reference", theirs, ra)):
                    chans = [Lt.tone(arr + (1 - alpha) * Lt.backdrop(h, w, s, 0.22 * s)) for s in sky]
                    nat.append(np.stack(chans, axis=-1))
                    aid.append(Lt.heat(arr / scale))
                    labels.append("%s %s" % (label, tag))
                diff.append(np.stack([np.clip((mine - theirs) / scale * 4.0, 0, 1), np.zeros_like(mine), np.clip((theirs - mine) / scale * 4.0, 0, 1)], axis=-1))
            Lt.sheet(nat, labels, os.path.join(SPLIT_PICTURES, "t15-%s-%s-split-natural.png" % (cell, vname)))
            Lt.sheet(aid, labels, os.path.join(SPLIT_PICTURES, "t15-%s-%s-split-aid.png" % (cell, vname)))
            Lt.sheet(diff, ["direct: ours more red, ref more blue", "scatter", "total"], os.path.join(SPLIT_PICTURES, "t15-%s-%s-split-aid-diff.png" % (cell, vname)))
            for label, mine, theirs in names:
                chans = [Lt.tone(mine + (1 - a) * Lt.backdrop(h, w, s, 0.22 * s)) for s in sky]
                Lt.upscale(np.stack(chans, axis=-1), 4).save(os.path.join(SPLIT_PICTURES, "t15-%s-%s-%s-ours.png" % (cell, vname, label)))
                chans = [Lt.tone(theirs + (1 - ra) * Lt.backdrop(h, w, s, 0.22 * s)) for s in sky]
                Lt.upscale(np.stack(chans, axis=-1), 4).save(os.path.join(SPLIT_PICTURES, "t15-%s-%s-%s-reference.png" % (cell, vname, label)))
        panels = [Lt.natural_image(folder, "%s_%s_0" % (cell, v), sky) for v, _, _ in COMBOS if v in rows]
        labels = [v for v, _, _ in COMBOS if v in rows]
        if ref_total is not None:
            ra, rc = ref_total
            h, w = ra.shape
            panels.append(np.stack([Lt.tone(rc + (1 - ra) * Lt.backdrop(h, w, s, 0.22 * s)) for s in sky], axis=-1))
            labels.append("T11 reference")
        if panels:
            Lt.sheet(panels, labels, os.path.join(SPLIT_PICTURES, "t15-%s-combos-grid.png" % cell))
    return out


register_series("T15-split", split_stages(), look_collect, exclusive=False, who="T15")

COMBO_COST_INPUT = os.path.join(BUILD, "T15-combo-cost-in")


def combo_cost_stages():
    """The cost of the combinations on the four views of the T14 light-cost series (same jobs, the T15 combination table)."""
    L.write_inputs()
    os.makedirs(COMBO_COST_INPUT, exist_ok=True)
    for name in ("view-coverage.r8", "view-type.r8"):
        src = os.path.join(L.INPUT, name)
        if os.path.isfile(src):
            with open(src, "rb") as f, open(os.path.join(COMBO_COST_INPUT, name), "wb") as g:
                g.write(f.read())
    jobs = []
    for lj0 in L.picture_jobs():
        if lj0["id"] not in t14.COST_VIEWS:
            continue
        for vname, over, march in COMBOS:
            lj = dict(lj0)
            lj["sunColour"] = [3.0, 2.9, 2.7]
            lj["ambientTop"] = [0.35, 0.42, 0.55]
            lj["ambientBottom"] = [0.14, 0.15, 0.16]
            lj["sunMarch"] = march
            j = dict(id="%s|%s" % (lj["id"], vname), mode="lum", aid=0, useDefaults=0, layerJob=lj)
            j.update(Lt.MODEL)
            j.update(over)
            if over.get("cache"):
                p, f = lj["position"], lj["forward"]
                c = [p[k] + 8000.0 * f[k] for k in range(3)]
                j.update(cacheMin=[c[k] - 16000.0 for k in range(3)], cacheSize=[32000.0] * 3, cacheFillSteps=12)
            jobs.append(j)
    with open(os.path.join(COMBO_COST_INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return [Stage("scene", ["-mode", "scene", "-scene", "light-cost", "-in", COMBO_COST_INPUT], size=(1707, 960), proxy=True, timeout=1500)]


def combo_cost_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    rows, views, variants = t14.light_cost_table(values_of(ctx), show=False)
    for view in views:
        base = rows.get((view, "default"), NAN)
        for var in variants:
            if (view, var) in rows:
                out.append(Metric.info("combo.cost_ms.%s.%s" % (view, var), rows[(view, var)]))
                out.append(Metric.info("combo.cost_ratio.%s.%s" % (view, var), rows[(view, var)] / base))
    return out


register_series("T15-combo-cost", combo_cost_stages(), combo_cost_collect, exclusive=True, who="T15")

if __name__ == "__main__" and len(sys.argv) > 1 and sys.argv[1] == "refs":
    references()
