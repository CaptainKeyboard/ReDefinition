"""The offline fit of the cloud light's look constants (package T03, FX-T03) against the reference renderer, on the CPU mirror of the shader
(light_cpu.py). One differential-evolution run is the third sweep of the look constants; its result is judged on the GPU by the series
T03-light and checked against the mirror there. Usage:

    python tools/weather_metrics/light_cpu_fit.py fit [--seed N] [--cap 2.0] [--out fit.json]
    python tools/weather_metrics/light_cpu_fit.py eval <params.json>
"""
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import light as Lt  # noqa: E402
from weather_metrics import light_cpu as C  # noqa: E402
from weather_metrics import parity_run as run  # noqa: E402

CASES = (("near", 0.0, 8.0), ("far", 75.0, 8.0), ("side", 90.0, 50.0), ("back", 180.0, 35.0), ("ns", 0.0, 20.0))
NAMES = ("octaveA", "octaveB", "octaveC", "msBrightness", "powder", "powderLength", "ambientOcclusion", "ambientGain", "diffusion", "diffusionTau",
         "msIsotropy")
BOUNDS = dict(octaveA=(0.2, 0.98), octaveB=(0.2, 0.9), octaveC=(0.1, 0.9), msBrightness=(0.2, 3.0), powder=(0.0, 1.0), powderLength=(50.0, 600.0),
              ambientOcclusion=(0.0, 2.5), ambientGain=(0.2, 3.0), diffusion=(0.0, 4.0), diffusionTau=(2.0, 40.0), msIsotropy=(0.0, 1.0))
W, STEPS = 40, 40
cell = C.Cell()


def images(p):
    out = {}
    for name, az, el in CASES:
        cam = Lt.cell_camera(W)
        rad, al = C.render(p, cell, cam, Lt.flat_sun(az, el), Lt.AMBIENT_TOP, Lt.AMBIENT_BOTTOM, steps=STEPS)
        out[name] = Lt.Img(al, rad)
    return out


def energy(p):
    return 1.0 + p.msBrightness * sum(p.octaveB ** n for n in range(1, int(p.octaves)))


REF = Lt.reference_numbers()
REFIMG = {case: Lt._ref_image(view) for case, view in Lt.FIT_IMAGE_VIEWS}
REFREG = {case: Lt.region_means(a, c, a) for case, (a, c) in REFIMG.items()}


def figures(p):
    imgs = images(p)
    near, far = run.edge_source(imgs["near"])[0], run.edge_source(imgs["far"])[0]
    silver = near / max(far, 1e-12)
    base, top = run.base_top(imgs["side"])
    e, i = run.powder_pair(imgs["back"])
    regions = {}
    for case, _ in Lt.FIT_IMAGE_VIEWS:
        im = imgs[case]
        om = Lt.region_means(im.alpha, im.cloud, im.alpha)
        regions[case] = {r: om[r] / max(REFREG[case][r], 1e-9) for r in om}
    return dict(silver=silver, base_top=top / max(base, 1e-12), powder=e / max(i, 1e-12), regions=regions,
                levels=dict(top=top, base=base, silver_near=near, edge_j=e, interior_j=i))


def terms(f):
    t = [math.log(f["silver"] / REF["silver"]) / 0.15 / 1.0, math.log(f["base_top"] / REF["base_top"]) / 0.2, math.log(f["powder"] / REF["powder"]) / 0.25]
    for k, v in f["levels"].items():
        t.append(math.log(max(v, 1e-9) / REF[k]) / 0.25)
    for case, d in f["regions"].items():
        for r, v in d.items():
            t.append(math.log(max(v, 1e-9)) / 0.25 * (1.5 if r == "core" else 1.0))
    return t


def objective(x, cap):
    p = C.Params(**dict(zip(NAMES, [float(v) for v in x])))
    en = energy(p)
    pen = 0.0 if en <= cap else 50.0 * (en - cap)
    t = terms(figures(p))
    return float(np.sqrt(np.mean(np.square(t))) + 0.2 * max(abs(v) for v in t) + pen)


def report(p):
    f = figures(p)
    t = terms(f)
    print("energy %.2f silver %.1f (ref %.1f) base_top %.2f (%.2f) powder %.2f (%.2f)" % (energy(p), f["silver"], REF["silver"], f["base_top"], REF["base_top"], f["powder"], REF["powder"]))
    for k, v in f["levels"].items():
        print("  level %s %.3f / ref %.3f = %.2f" % (k, v, REF[k], v / REF[k]))
    for case, d in f["regions"].items():
        print("  %s " % case + " ".join("%s %.2f" % (r, v) for r, v in d.items()))
    print("  worst term (in tolerances) %.2f" % max(abs(v) for v in t))


def main():
    cmd = sys.argv[1]
    if cmd == "eval":
        kw = json.load(open(sys.argv[2]))
        report(C.Params(**kw))
        return
    from scipy.optimize import differential_evolution
    seed = int(sys.argv[sys.argv.index("--seed") + 1]) if "--seed" in sys.argv else 1
    cap = float(sys.argv[sys.argv.index("--cap") + 1]) if "--cap" in sys.argv else 2.0
    out = sys.argv[sys.argv.index("--out") + 1] if "--out" in sys.argv else "fit.json"
    res = differential_evolution(objective, [BOUNDS[n] for n in NAMES], args=(cap,), seed=seed, maxiter=40, popsize=8, tol=1e-4, polish=False,
                                 workers=1, updating="immediate", disp=True)
    best = dict(zip(NAMES, [float(v) for v in res.x]))
    json.dump(best, open(out, "w"), indent=1)
    print(best)
    report(C.Params(**best))


if __name__ == "__main__":
    main()
