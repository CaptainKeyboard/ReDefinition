"""The series of package T15 (the resolution levels, the direct / scatter split of the deep storm cell, the combinations of the sun march),
run through tools/build_weather_player.py --series <name>. The series T15-bisect-cost and T15-bisect-quality (the cheap forms of the entry refinement) went with the
entry refinement (E40 step (c)):

  T15-levels          scene 'recon-levels' (it was 'bisect-levels'): the resolution levels 1 to 4 through two turns: ghosting, trail and shimmer.
  T15-split           scene 'light': the deep storm cell (radius 4000 m, 8 km wide, as in the T14 storm cells) as direct light only (single scattering), multiple
                      scattering, ambient and total, against the T11 reference renderer's order 1 (direct) and the rest of its total; pictures t15-<cell>-*.png;
                      and the combinations of the sun march 9000 m with more ambient taps and the light cache against the reference base radiance.

Resolution levels, the direct / scatter split and the combinations carry no pass/fail limit: their numbers are for the owner's decision.

    python tools/weather_metrics/t15.py refs            # the reference renders of the storm cells (total, direct) into build/parity-ref-holdout
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
NAN = float("nan")


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


def levels_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    for name, v in sorted(values_of(ctx).items()):
        if name.startswith("levels."):
            out.append(Metric.info("lv15." + name[len("levels."):], v))
    return out


register_series("T15-levels", [Stage("scene", ["-mode", "scene", "-scene", "recon-levels", "-in", L.INPUT], size=(1280, 720), proxy=False,
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
