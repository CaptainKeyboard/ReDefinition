"""The series of package T14 (cloud resolution slider, contour gate of the entry refinement, light cache, sun march), run through
tools/build_weather_player.py --series <name>:

  T14-refine-cost     timing run (exclusive), scene 'refine-cost': ms of the layer pass at 1707x960 per view for the entry refinement off, on every ray and
                      gated at the contours (alpha step of the 2 x 2 quad), in the full mode and in the 4 x 4 block mode; each variant twice.
  T14-refine-quality  scene 'refine-quality': per view the error of each variant against the reference (the mean of four full marches with the refinement
                      on every ray) in the band around the contour, in the thick interior and in the empty sky, plus pictures
                      t14-<view>-before/after/reference/aid-error/aid-refined.png in the player's output folder.
  T14-light-cost      timing run (exclusive), scene 'light-cost': ms of the pass per view for the sun march 4000 m and 9000 m, the ambient taps with reuse 0, 1, 2,
                      and the light cache.
  T14-light-look      scene 'light': a deep cell seen from the side, against the sun and with the sun behind: the dark base (base / top ratio and the regions)
                      for the same variants, against the T11 reference renderer, and the pictures
                      t14-<view>-before/after/grid/aid-direct/aid-ambient.png (natural plus aid).

THRESHOLDS and their sources (fixed before the first run; sweeps change the gate's number only, at most 3 sweeps):
  refine cost ratio      total(gated) / total(refinement on every ray) <= 0.85      the gate must save at least the 15 percent of the refinement's cost that
                                                                                   the two-march scheme pays back on the contour quads [proposed]
  refine band recovery   band_rms(gated) <= 0.5 x band_rms(refinement off)          the gate must keep at least half of what the refinement gains at the edge
  refine band vs full    band_rms(gated) <= 1.15 x band_rms(refinement on every ray) T12 brief: within 15 percent of full resolution
  refine interior        interior_err(gated) <= 0.02                                T12 brief: luminance error where alpha is above 0.95 within 2 percent
  refine empty sky       empty_leak(gated) <= 0.0001                                T12 brief: empty sky exact
Light variants carry no pass/fail limit: their numbers (ms, base / top, region ratios to the reference) are for the owner's decision.

    python tools/weather_metrics/t14.py refine-cost <log or stage folder>
    python tools/weather_metrics/t14.py light-cost <log or stage folder>
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
SWEEP = os.environ.get("T14_SWEEP", "1")
PICK = os.environ.get("T14_PICK", "g12")

LINE = re.compile(r"\{([^,{}]+),([^,{}]*),([^,{}]*),([^,{}]*)\}")
NAN = float("nan")

REFINE_VIEWS = ("ground", "inside", "above", "orbit-edge", "stack-below")

# Light variants: name, light job overrides, sun march of the layer job. The deep-cell looks and the cost use the same table.
LIGHT_VARIANTS = (
    ("default", {}, 4000.0),
    ("sun9", {}, 9000.0),
    ("ao0", dict(aoTaps=1, upReuse=0), 4000.0),
    ("ao1", dict(aoTaps=1, upReuse=1), 4000.0),
    ("ao2", dict(aoTaps=1, upReuse=2), 4000.0),
    ("ao2sun9", dict(aoTaps=1, upReuse=2), 9000.0),
    ("cache", dict(cache=1), 4000.0),
    ("cachesun9", dict(cache=1), 9000.0),
)
# The deep cells of the look: name, sun azimuth, sun elevation, cell radius (m), extinction (per m). Radius 1100 m and extinction 0.06 per m: a core of
# optical depth about 90 (the hold-out set's H4-dense and H6-large together).
LOOK_CELLS = (
    ("deepside", 90.0, 40.0, 1100.0, 0.06),
    ("deepback", 0.0, 25.0, 1100.0, 0.06),
    ("deepfront", 180.0, 40.0, 1100.0, 0.06),
    # Storm cells: radius 4000 m (8 km wide, 5.6 km tall, centre at 5000 m) and extinction 0.012 per m (optical depth about 67 through the core). The
    # sun march of 4 km is shorter than the cell, so these are the cells where the two lengths can differ at all.
    ("stormside", 90.0, 40.0, 4000.0, 0.012),
    ("stormback", 0.0, 25.0, 4000.0, 0.012),
    ("stormfront", 180.0, 40.0, 4000.0, 0.012),
)
STORM_ALT_M = 5000.0


def centre_alt(spec):
    return STORM_ALT_M if spec[0].startswith("storm") else 2000.0
LOOK_INPUT = os.path.join(BUILD, "T14-look-in")
LOOK_PICTURES = os.path.join(BUILD, "T14-pictures")
COST_INPUT = os.path.join(BUILD, "T14-light-cost-in")
COST_VIEWS = ("V-ground", "V-inside", "V-above", "V-stack-below")


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


def median(values):
    v = sorted(x for x in values if x == x)
    return v[len(v) // 2] if v else NAN


def refine_cost_table(values, show=True):
    """total ms per (view, variant): the median of the two passes over the table; the ratios to 'full' (refinement on every ray) and to 'none'."""
    rows = {}
    for k, v in values.items():
        m = re.match(r"refcostb?\.total\.(V-[a-z-]+)\.([a-z0-9]+)\.1707x960$", k)
        if m:
            rows.setdefault((m.group(1), m.group(2)), []).append(v)
    views = sorted({k[0] for k in rows})
    variants = []
    for (view, var) in rows:
        if var not in variants:
            variants.append(var)
    table = {key: (sum(v) / len(v), max(v) - min(v)) for key, v in rows.items()}
    if show:
        print("%-12s" % "ms (mean of 2, spread)" + "".join("%18s" % v[2:] for v in views))
        for var in variants:
            print("%-12s" % var + "".join("%11.3f (%4.2f)" % table.get((view, var), (NAN, NAN)) for view in views))
        print("\nratio to full (refinement on every ray)")
        for var in variants:
            print("%-12s" % var + "".join("%18.3f" % (table.get((view, var), (NAN, 0))[0] / table.get((view, "full"), (NAN, 0))[0]) for view in views))
    return table, views, variants


def light_cost_table(values, show=True):
    rows = {}
    for k, v in values.items():
        m = re.match(r"cost\.(march|composite|total)\.(V-[a-z-]+)\|([a-z0-9-]+)$", k)
        if m and m.group(1) == "total":
            rows[(m.group(2), m.group(3))] = v
    views = sorted({k[0] for k in rows})
    variants = []
    for (view, var) in rows:
        if var not in variants:
            variants.append(var)
    if show:
        print("%-12s" % "total ms" + "".join("%16s" % v[2:] for v in views))
        for var in variants:
            print("%-12s" % var + "".join("%16.3f" % rows.get((view, var), NAN) for view in views))
        print("\nratio to default")
        for var in variants:
            print("%-12s" % var + "".join("%16.3f" % (rows.get((view, var), NAN) / rows.get((view, "default"), NAN)) for view in views))
    return rows, views, variants


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    data = read_lines(sys.argv[2])
    {"refine-cost": refine_cost_table, "light-cost": light_cost_table}[sys.argv[1]](data)
    sys.exit(0)

# ------------------------------------------------------------------------------------------------------------------------------------------------
# The series (importing this module registers them; running it as a script prints the tables above)
# ------------------------------------------------------------------------------------------------------------------------------------------------
import numpy as np  # noqa: E402

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import light as Lt  # noqa: E402
from weather_metrics import light_holdout as H  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import parity_run as run  # noqa: E402


def refine_cost_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    values = {name: v[0] for name, v in ctx.player_metrics("scene").items()}
    table, views, variants = refine_cost_table(values, show=False)
    for view in views:
        full = table.get((view, "full"), (NAN, NAN))[0]
        for var in variants:
            t = table.get((view, var))
            if t is None:
                continue
            out.append(Metric.info("refine.cost_ms.%s.%s" % (view, var), t[0]))
            out.append(Metric.info("refine.cost_spread.%s.%s" % (view, var), t[1]))
            if var not in ("full", "none", "u4full", "u4none"):
                ratio = t[0] / table.get((view, "u4full" if var.startswith("u4") else "full"), (NAN, NAN))[0]
                out.append(Metric.info("refine.cost_ratio.%s.%s" % (view, var), ratio))
                if var == PICK:
                    out.append(Metric.at_most("refine.cost_ratio.%s" % view, ratio, 0.85).based("new"))
    return out


def refine_quality_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    values = {name: v[0] for name, v in ctx.player_metrics("scene").items()}
    for view in REFINE_VIEWS:
        def q(var, field):
            return values.get("refq.%s.%s.%s" % (view, var, field), NAN)
        for var in {k.split(".")[2] for k in values if k.startswith("refq.%s." % view) and k.split(".")[2] != "ref"}:
            if var in ("full", "none", "u4full", "u4none"):
                continue
            pre = "u4" if var.startswith("u4") else ""
            recovery = q(var, "band_rms") / q(pre + "none", "band_rms")
            vs_full = q(var, "band_rms") / q(pre + "full", "band_rms")
            out.append(Metric.info("refine.band_recovery.%s.%s" % (view, var), recovery))
            out.append(Metric.info("refine.band_vs_full.%s.%s" % (view, var), vs_full))
            if var == PICK:
                out.append(Metric.at_most("refine.band_recovery.%s" % view, recovery, 0.5).based("new"))
                out.append(Metric.at_most("refine.band_vs_full.%s" % view, vs_full, 1.15).based("gate"))
                out.append(Metric.at_most("refine.interior_err.%s" % view, q(var, "interior_err"), 0.02).based("gate"))
                out.append(Metric.at_most("refine.empty_leak.%s" % view, q(var, "empty_leak"), 0.0001).based("gate"))
    return out


register_series("T14-refine-cost", [Stage("scene", ["-mode", "scene", "-scene", "refine-cost", "-sweep", SWEEP, "-in", L.INPUT], size=(1707, 960), proxy=True,
                                           timeout=1500)], refine_cost_collect, exclusive=True, who="T14")
register_series("T14-refine-quality", [Stage("scene", ["-mode", "scene", "-scene", "refine-quality", "-sweep", SWEEP, "-in", L.INPUT], size=(1280, 720),
                                             proxy=False, timeout=1500)], refine_quality_collect, exclusive=False, who="T14")


# ------------------------------------------------------------------------------------------------------------------------------------------------
# The light: cost
# ------------------------------------------------------------------------------------------------------------------------------------------------
def light_cost_jobs():
    L.write_inputs()
    os.makedirs(COST_INPUT, exist_ok=True)
    for name in ("view-coverage.r8", "view-type.r8"):
        src = os.path.join(L.INPUT, name)
        if os.path.isfile(src):
            with open(src, "rb") as f, open(os.path.join(COST_INPUT, name), "wb") as g:
                g.write(f.read())
    jobs = []
    for lj0 in L.picture_jobs():
        if lj0["id"] not in COST_VIEWS:
            continue
        for vname, over, march in LIGHT_VARIANTS:
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
    with open(os.path.join(COST_INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def light_cost_collect(ctx):
    out = list(core.player_judged("scene")(ctx))
    values = {name: v[0] for name, v in ctx.player_metrics("scene").items()}
    rows, views, variants = light_cost_table(values, show=False)
    for view in views:
        base = rows.get((view, "default"), NAN)
        for var in variants:
            if (view, var) in rows:
                out.append(Metric.info("light.cost_ms.%s.%s" % (view, var), rows[(view, var)]))
                out.append(Metric.info("light.cost_ratio.%s.%s" % (view, var), rows[(view, var)] / base))
    return out


def light_cost_stages():
    light_cost_jobs()
    return [Stage("scene", ["-mode", "scene", "-scene", "light-cost", "-in", COST_INPUT], size=(1707, 960), proxy=True, timeout=1500)]


register_series("T14-light-cost", light_cost_stages(), light_cost_collect, exclusive=True, who="T14")


# ------------------------------------------------------------------------------------------------------------------------------------------------
# The light: the look of the dark base of a deep cell
# ------------------------------------------------------------------------------------------------------------------------------------------------
def look_jobs():
    jobs = []
    for spec in LOOK_CELLS:
        for vname, over, march in LIGHT_VARIANTS:
            for aid in (0, 4):
                o = dict(over)
                alt = centre_alt(spec)
                if o.get("cache"):
                    # The atlas box around the cell: the product's 4000 m for the 1100 m cells, 16000 m for the storm cells (the grid is 64 x 64 x 32).
                    box = 16000.0 if alt > 3000.0 else 4000.0
                    o.update(cacheMin=[-box / 2, -box / 2, Lt.R + alt - box / 2], cacheSize=[box] * 3, cacheFillSteps=12)
                jobs.append(look_job("%s_%s_%d" % (spec[0], vname, aid), spec, aid=aid, layer_kw=dict(sunMarch=march), **o))
    return jobs


def look_camera(spec, w):
    name, az, el, radius, sigma = spec
    alt = centre_alt(spec)
    cam = pr.Camera([0.0, -8000.0 * H.distance_scale(radius), Lt.R + alt], 0.0, 0.0, w, int(w * 0.75), 16.0)
    cam.forward = np.array([0.0, 1.0, 0.0])
    cam.right = np.array([1.0, 0.0, 0.0])
    cam.cam_up = np.array([0.0, 0.0, 1.0])
    return cam


def look_job(jid, spec, **over):
    """light_holdout.hold_job with the cell's centre at its own altitude."""
    name, az, el, radius, sigma = spec
    alt = centre_alt(spec)
    cam = look_camera(spec, H.HOLD_W)
    rv = 0.7 * radius
    layers = [L.layer(alt - 1.05 * rv, alt + 1.05 * rv, sigma)]
    return Lt.light_job(jid, cam, layers, cam.width, cam.height, Lt.flat_sun(az, el), cell=False, cellCentre=[0.0, 0.0, Lt.R + alt], cellRadius=radius,
                        cellVertical=rv, cellSigma=sigma, **over)


def look_reference(spec):
    name, az, el, radius, sigma = spec
    q = dict(run.QUALITY["full"])
    field = pr.scene_single_cloud(radius_m=radius, centre_alt=centre_alt(spec), sigma=sigma)
    cam = pr.Camera([0, -8000.0 * H.distance_scale(radius), run.F + centre_alt(spec)], 0, 0, H.HOLD_W, int(H.HOLD_W * 0.75), 16)
    sun = pr.Sun.at(cam.pos, az, el)
    res = run.renderer(field, sun, q, seed=5).render(cam)
    np.savez(os.path.join(H.REF_DIR, name + ".npz"), alpha=res.alpha, cloud=res.cloud)
    return name


def look_write_inputs():
    os.makedirs(LOOK_INPUT, exist_ok=True)
    js = look_jobs()
    with open(os.path.join(LOOK_INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": js}, f)
    return js


def look_references():
    """The T11 reference images of the deep cells (saved next to the hold-out references)."""
    import os as _os
    _os.makedirs(H.REF_DIR, exist_ok=True)
    for spec in LOOK_CELLS:
        if not _os.path.isfile(_os.path.join(H.REF_DIR, spec[0] + ".npz")):
            look_reference(spec)
            print("reference", spec[0], flush=True)


def _ref(spec):
    path = os.path.join(H.REF_DIR, spec[0] + ".npz")
    if not os.path.isfile(path):
        return None
    z = np.load(path)
    return z["alpha"], z["cloud"]


def look_collect(ctx):
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    os.makedirs(LOOK_PICTURES, exist_ok=True)
    for spec in LOOK_CELLS:
        name = spec[0]
        ref = _ref(spec)
        rows = {}
        for vname, _, _ in LIGHT_VARIANTS:
            jid = "%s_%s_0" % (name, vname)
            if not os.path.isfile(os.path.join(folder, jid + "-r.npy")):
                continue
            img = Lt.cell_img(folder, jid)[0]
            base, top = run.base_top(img)
            rows[vname] = (img, base, top)
            out.append(Metric.info("look.%s.%s.base" % (name, vname), base))
            out.append(Metric.info("look.%s.%s.top" % (name, vname), top))
            out.append(Metric.info("look.%s.%s.top_over_base" % (name, vname), top / max(base, 1e-12)))
            if ref is not None:
                ra, rc = ref
                rm = Lt.region_means(ra, rc, ra)
                om = Lt.region_means(img.alpha, img.cloud, ra)
                for region in rm:
                    out.append(Metric.info("look.%s.%s.ratio_%s" % (name, vname, region), om[region] / max(rm[region], 1e-9)))
        if ref is not None:
            rb, rt = run.base_top(type("R", (), {"alpha": ref[0], "cloud": ref[1]}))
            out.append(Metric.info("look.%s.reference.top_over_base" % name, rt / max(rb, 1e-12)))
            out.append(Metric.info("look.%s.reference.base" % name, rb))
        # The pictures: before = the shipped default, after = the long sun march; the grid holds every variant; the aids are the direct light against the
        # ambient (red | blue) and the light taps per pixel, for both.
        if "default" in rows and "sun9" in rows:
            sky = Lt.DAY_SKY
            for tag, vname in (("before", "default"), ("after", "sun9")):
                nat = Lt.natural_image(folder, "%s_%s_0" % (name, vname), sky)
                Lt.upscale(nat, 4).save(os.path.join(LOOK_PICTURES, "t14-%s-%s.png" % (name, tag)))
                sp, steps = Lt.split_image(folder, "%s_%s_4" % (name, vname))
                Lt.upscale(sp, 4).save(os.path.join(LOOK_PICTURES, "t14-%s-aid-%s-direct-ambient.png" % (name, tag)))
                Lt.upscale(steps, 4).save(os.path.join(LOOK_PICTURES, "t14-%s-aid-%s-taps.png" % (name, tag)))
            panels = [Lt.natural_image(folder, "%s_%s_0" % (name, v), sky) for v, _, _ in LIGHT_VARIANTS if v in rows]
            labels = [v for v, _, _ in LIGHT_VARIANTS if v in rows]
            if ref is not None:
                ra, rc = ref
                h, w = ra.shape
                panels.append(np.stack([Lt.tone(rc + (1 - ra) * Lt.backdrop(h, w, s, 0.22 * s)) for s in sky], axis=-1))
                labels.append("T11 reference")
            Lt.sheet(panels, labels, os.path.join(LOOK_PICTURES, "t14-%s-grid.png" % name))
    return out


def look_stages():
    look_write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "light", "-in", LOOK_INPUT], size=(1280, 720), proxy=False)]


register_series("T14-light-look", look_stages(), look_collect, exclusive=False, who="T14")
