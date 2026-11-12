"""The hold-out check of the cloud light's look constants (task T03v2, series T03-holdout).

The review of wave 2 (T-05) found that the parity rows of T03 passed because the fit had minimised exactly those rows against the same reference
on the same cell geometry. Here a constant is fitted on one set of scenes (the FIT set: the three image views of light.py, sun elevation 20 / 50 / 35
degrees, cell radius 800 m, extinction 0.02 per m) and CHECKED on a hold-out set the fit never saw: other sun elevations (5 .. 80 degrees), other
optical depths (extinction 0.005 .. 0.06 per m: a core of optical depth 8 to 96), other cell sizes (radius 400 m .. 1100 m), rendered by the T11
brute-force reference renderer (parity_ref.py) at the full quality.

The judged figure is the one of the fit's image-level part (light.region_means): the ratio of our mean radiance to the reference's over the cell's
core, its top, middle and bottom thirds and its rim, each within 25 % of the reference, and the top/base ratio within 25 %. A variant (here: the
decay of the diffusion term) is chosen on the FIT set and must pass on the HOLD-OUT set too; its numbers are reported for both.

  python tools/weather_metrics/light_holdout.py ref          renders the hold-out references (build/parity-ref-holdout, several minutes, all cores)
  python tools/weather_metrics/light_holdout.py jobs         writes the player jobs of the variants
  python tools/build_weather_player.py --series T03-holdout  runs the player and prints the table
  python tools/weather_metrics/light_holdout.py table        prints the table of the last run
"""
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import light as Lt  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import parity_run as run  # noqa: E402

REPO = Lt.REPO
INPUT = os.path.join(REPO, "build", "weather-series", "T03-holdout-in")
REF_DIR = os.path.join(REPO, "build", "parity-ref-holdout")
R = pr.KERBIN_RADIUS
TOLERANCE = 0.25
HOLD_W = 48

# name, sun azimuth, sun elevation, cell radius (m), extinction (per m). The fit set is light.FIT_IMAGE_VIEWS (radius 800, extinction 0.02).
HOLDOUT = (
    ("H1-el12", 30.0, 12.0, 800.0, 0.02),
    ("H2-el65", 120.0, 65.0, 800.0, 0.02),
    ("H3-thin", 200.0, 28.0, 800.0, 0.005),
    ("H4-dense", 100.0, 28.0, 800.0, 0.06),
    ("H5-small", 60.0, 35.0, 400.0, 0.02),
    ("H6-large", 150.0, 35.0, 1100.0, 0.02),
    ("H7-el80", 0.0, 80.0, 800.0, 0.02),
    ("H8-el5", 180.0, 5.0, 800.0, 0.02),
)
# The first round showed that the three fit views hold one optical depth only (extinction 0.02): the thin and the dense cell join the FIT set of the
# second round (a constant is then fitted over three optical depths), and the other six cells stay hold-out. Set T03_HOLD_ROUND=1 for the first round.
ROUND = int(os.environ.get("T03_HOLD_ROUND", "2"))
FIT_EXTRA = ("H3-thin", "H4-dense") if ROUND >= 2 else ()


def is_fit(spec):
    return spec[0] in FIT_EXTRA


# The variants. Round 1: the decay of the diffusion term (0: the saturating term of FX-T03). Round 2: the diffusion term's strength, optical depth
# scale and decay together (the term that decides the thin and the dense cell).
if ROUND >= 6:
    # Round 6: the one confirmation of the value between the two swept powder lengths that passes R06 (350 put the silver lining 0.7 points outside its 15 %).
    VARIANTS = (("k002-L0400", dict(diffusion=1.3, diffusionDecay=0.02, powderLength=400.0)),)
elif ROUND >= 5:
    # Round 5 (the series T03-light showed that the thick deck and the thin cirrus of the harness, views up at a layer, were not in the fit set: with the
    # decay 0.15 the deck came out at 0.37 of the reference, with the powder length 250 the cirrus at 0.69): both join the FIT set, and the decay and the
    # powder length are fitted over cells, deck and cirrus together.
    VARIANTS = tuple(("k%03d-L%04d" % (int(round(k * 100)), int(round(L))), dict(diffusion=1.3, diffusionDecay=k, powderLength=L))
                     for k in (0.0, 0.02, 0.05) for L in (460.0, 350.0, 250.0))
elif ROUND >= 4:
    # Round 4 (powder length 250 fixed: the rim is then right, the interior 10 to 25 % too dark): the occlusion of the ambient and the diffusion strength.
    VARIANTS = tuple(("a%03d-d%03d" % (int(round(a * 100)), int(round(d * 100))),
                      dict(powder=0.8075, powderLength=250.0, diffusionDecay=0.15, ambientOcclusion=a, diffusion=d))
                     for a in (1.2765, 0.9, 0.5) for d in (0.8732, 1.3))
elif ROUND >= 3:
    # Round 3 (the rim was 15 to 25 % too bright and the interior 10 to 15 % too dark in all of round 2): the powder strength and length.
    VARIANTS = tuple(("p%03d-L%04d" % (int(round(p * 100)), int(round(L))), dict(powder=p, powderLength=L, diffusionDecay=0.15))
                     for p in (0.8075, 1.0) for L in (460.0, 250.0, 120.0, 60.0))
elif ROUND >= 2:
    VARIANTS = tuple(("d%03d-t%03d-k%03d" % (int(round(d * 100)), int(round(t)), int(round(k * 100))),
                      dict(diffusion=d, diffusionTau=t, diffusionDecay=k))
                     for d in (0.87, 1.3, 1.8) for t in (35.8, 60.0, 100.0) for k in (0.05, 0.15))
else:
    VARIANTS = tuple(("k%03d" % int(round(k * 100)), dict(diffusionDecay=k)) for k in (0.0, 0.03, 0.06, 0.10, 0.15))


def distance_scale(radius):
    return radius / 800.0


def hold_camera(radius, w):
    s = distance_scale(radius)
    cam = pr.Camera([0.0, -8000.0 * s, R + 2000.0], 0.0, 0.0, w, int(w * 0.75), 16.0)
    cam.forward = np.array([0.0, 1.0, 0.0])
    cam.right = np.array([1.0, 0.0, 0.0])
    cam.cam_up = np.array([0.0, 0.0, 1.0])
    return cam


def reference_case(spec):
    """One reference image (module level so that the pool can pickle it)."""
    name, az, el, radius, sigma = spec
    q = dict(run.QUALITY["full"])
    field = pr.scene_single_cloud(radius_m=radius, centre_alt=2000.0, sigma=sigma)
    cam = pr.Camera([0, -8000.0 * distance_scale(radius), run.F + 2000.0], 0, 0, HOLD_W, int(HOLD_W * 0.75), 16)
    sun = pr.Sun.at(cam.pos, az, el)
    res = run.renderer(field, sun, q, seed=5).render(cam)
    np.savez(os.path.join(REF_DIR, name + ".npz"), alpha=res.alpha, cloud=res.cloud)
    return name


def reference_all():
    import multiprocessing
    os.makedirs(REF_DIR, exist_ok=True)
    todo = [s for s in HOLDOUT if not os.path.isfile(os.path.join(REF_DIR, s[0] + ".npz"))]
    with multiprocessing.Pool(min(len(todo), 10) or 1) as pool:
        for name in pool.imap_unordered(reference_case, todo):
            print("reference", name, flush=True)


def hold_job(jid, spec, **over):
    name, az, el, radius, sigma = spec
    cam = hold_camera(radius, HOLD_W)
    rv = 0.7 * radius
    layers = [L.layer(2000.0 - 1.05 * rv, 2000.0 + 1.05 * rv, sigma)]
    # cell=False: light_job's own cell numbers are those of the 800 m cell; the job carries this cell's through over.
    return Lt.light_job(jid, cam, layers, cam.width, cam.height, Lt.flat_sun(az, el), cell=False, cellCentre=Lt.CELL_CENTRE, cellRadius=radius,
                        cellVertical=rv, cellSigma=sigma, **over)


def deck_job(jid, vid, **over):
    """The harness's view up at a deck or a cirrus layer (light.py up_job with the harness setting), with the variant's numbers."""
    base, top, tau, el = Lt.UP_VIEWS[vid]
    cam = pr.Camera([0.0, 0.0, R + 2.0], 0.0, 35.0, 16, 12, 60.0)
    return Lt.light_job(jid, cam, [L.layer(base, top, tau / (top - base))], cam.width, cam.height, Lt.flat_sun(200.0, el), **over)


def deck_row(folder, jid, vid):
    ref = Lt.up_reference(vid)
    if ref is None:
        return None
    r, g, b, a = (Lt.plane(folder, jid, c) for c in "rgba")
    return {"radiance": float(((r + g + b) / 3.0).mean() / ref[1].mean())}


def jobs():
    out = []
    if ROUND >= 5:
        for vname, over in VARIANTS:
            for vid in ("deck", "cirrus"):
                out.append(deck_job("%s-fit-%s" % (vname, vid), vid, **over))
    for vname, over in VARIANTS:
        for case, view in Lt.FIT_IMAGE_VIEWS:
            for c, az, el in Lt.FIT_CASES:
                if c == case:
                    out.append(Lt.cell_job("%s-fit-%s" % (vname, case), az, el, w=Lt.FIT_W, **over))
        for spec in HOLDOUT:
            out.append(hold_job("%s-%s-%s" % (vname, "fit" if is_fit(spec) else "hold", spec[0]), spec, **over))
    return out


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    js = jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": js}, f)
    return js


def score(folder, jid, ref_alpha, ref_cloud):
    img = Lt.cell_img(folder, jid)[0]
    rm = Lt.region_means(ref_alpha, ref_cloud, ref_alpha)
    om = Lt.region_means(img.alpha, img.cloud, ref_alpha)
    ratios = {k: om[k] / max(rm[k], 1e-9) for k in rm}
    base, top = run.base_top(img)
    rbase, rtop = run.base_top(type("R", (), {"alpha": ref_alpha, "cloud": ref_cloud}))
    ratios["top_base"] = (top / max(base, 1e-12)) / (rtop / max(rbase, 1e-12))
    return ratios


def collect_scores(folder):
    out = {}
    for vname, _ in VARIANTS:
        rows = {}
        if ROUND >= 5:
            for vid in ("deck", "cirrus"):
                row = deck_row(folder, "%s-fit-%s" % (vname, vid), vid)
                if row is not None:
                    rows["fit-" + vid] = row
        for case, view in Lt.FIT_IMAGE_VIEWS:
            ra, rc = Lt._ref_image(view)
            rows["fit-" + case] = score(folder, "%s-fit-%s" % (vname, case), ra, rc)
        for spec in HOLDOUT:
            path = os.path.join(REF_DIR, spec[0] + ".npz")
            if not os.path.isfile(path):
                continue
            z = np.load(path)
            kind = "fit" if is_fit(spec) else "hold"
            rows["%s-%s" % (kind, spec[0])] = score(folder, "%s-%s-%s" % (vname, kind, spec[0]), z["alpha"], z["cloud"])
        out[vname] = rows
    return out


def worst(rows, prefix):
    vals = [abs(v - 1.0) for k, r in rows.items() if k.startswith(prefix) for v in r.values()]
    return max(vals) if vals else float("nan")


def collect(ctx):
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    scores = collect_scores(folder)
    with open(os.path.join(INPUT, "scores.json"), "w", encoding="utf-8") as f:
        json.dump(scores, f, indent=1)
    for vname, rows in scores.items():
        out.append(Metric.info("holdout.%s.fit_worst" % vname, worst(rows, "fit-")))
        out.append(Metric.info("holdout.%s.hold_worst" % vname, worst(rows, "hold-")))
    return out


def stages():
    jobs_file = os.path.join(INPUT, "jobs.json")
    if not os.path.isfile(jobs_file) or os.path.getmtime(jobs_file) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "light", "-in", INPUT], size=(1280, 720), proxy=False)]


register_series("T03-holdout", stages(), collect, exclusive=False, who="T03v2")


def table(path=None):
    path = path or os.path.join(INPUT, "scores.json")
    with open(path, encoding="utf-8") as f:
        scores = json.load(f)
    for vname, rows in scores.items():
        print("variant", vname, "fit worst %.3f  hold-out worst %.3f" % (worst(rows, "fit-"), worst(rows, "hold-")))
        for case, r in rows.items():
            print("   %-16s %s" % (case, "  ".join("%s %.2f" % (k, v) for k, v in r.items())))


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "ref":
        reference_all()
    elif cmd == "jobs":
        print(len(write_inputs()), "jobs")
    elif cmd == "table":
        table(sys.argv[2] if len(sys.argv) > 2 else None)
    else:
        print(__doc__)
