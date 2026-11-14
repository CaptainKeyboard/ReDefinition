"""Does the fitted cloud light hold for translucent clouds (E40 B3, the soft density)?

The light's constants were fitted (light_cpu_fit.py, light_holdout.py) against brute-force references of an analytic cell: a smooth ellipsoid (edge 0.35)
of extinction 0.005 to 0.06 per m, a core of optical depth 8 to 96. The soft cloud of the product has columns of optical depth 0.5 to 25 and a rim that
thins out through 0.1 to 3. This check renders references of the same cell at thin extinctions (core optical depth 0.8, 2.4, 8) and compares the CPU mirror
of the shader (light_cpu.py) with them: mean radiance and mean alpha over the pixels the reference covers (alpha above 0.02), and the top, middle and
bottom thirds of the cell. Nothing is fitted here; the figures say whether a refit has anything to fit.

  python tools/weather_metrics/light_soft_check.py ref      renders the references (build/parity-ref-soft, several minutes, all cores)
  python tools/weather_metrics/light_soft_check.py table    prints the table (mirror / reference per cell and sun)
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import light as Lt  # noqa: E402
from weather_metrics import light_cpu as C  # noqa: E402
from weather_metrics import parity_ref as pr  # noqa: E402
from weather_metrics import parity_run as run  # noqa: E402

REPO = Lt.REPO
REF_DIR = os.path.join(REPO, "build", "parity-ref-soft")
R = pr.KERBIN_RADIUS
W = 48
RADIUS = 800.0
# name, sun azimuth, sun elevation, extinction per m (the core optical depth is about 2 x 800 x sigma x 0.7 mean).
CASES = (
    ("S1-tau1-side", 90.0, 50.0, 0.0005),
    ("S2-tau1-back", 180.0, 35.0, 0.0005),
    ("S3-tau3-side", 90.0, 50.0, 0.0015),
    ("S4-tau3-back", 180.0, 35.0, 0.0015),
    ("S5-tau3-near", 0.0, 20.0, 0.0015),
    ("S6-tau8-side", 90.0, 50.0, 0.005),
)


def camera():
    cam = pr.Camera([0.0, -8000.0, R + 2000.0], 0.0, 0.0, W, int(W * 0.75), 16.0)
    cam.forward = np.array([0.0, 1.0, 0.0])
    cam.right = np.array([1.0, 0.0, 0.0])
    cam.cam_up = np.array([0.0, 0.0, 1.0])
    return cam


def reference_case(spec):
    name, az, el, sigma = spec
    field = pr.scene_single_cloud(radius_m=RADIUS, centre_alt=2000.0, sigma=sigma)
    cam = pr.Camera([0, -8000.0, run.F + 2000.0], 0, 0, W, int(W * 0.75), 16)
    sun = pr.Sun.at(cam.pos, az, el)
    res = run.renderer(field, sun, dict(run.QUALITY["full"]), seed=5).render(cam)
    np.savez(os.path.join(REF_DIR, name + ".npz"), alpha=res.alpha, cloud=res.cloud)
    return name


def reference_all():
    import multiprocessing
    os.makedirs(REF_DIR, exist_ok=True)
    todo = [s for s in CASES if not os.path.isfile(os.path.join(REF_DIR, s[0] + ".npz"))]
    with multiprocessing.Pool(min(len(todo), 10) or 1) as pool:
        for name in pool.imap_unordered(reference_case, todo):
            print("reference", name, flush=True)


def mirror(spec, params=None):
    name, az, el, sigma = spec
    p = params or C.Params()
    cell = C.Cell(sigma=sigma)
    rad, al = C.render(p, cell, camera(), Lt.flat_sun(az, el), Lt.AMBIENT_TOP, Lt.AMBIENT_BOTTOM, steps=64)
    return al.reshape(int(W * 0.75), W), rad.reshape(int(W * 0.75), W)


def figures(alpha, rad, mask):
    """Mean radiance and alpha over the mask, and the radiance of the top, middle and bottom thirds of the rows the mask covers."""
    ys = np.where(mask.any(axis=1))[0]
    y0, y1 = ys.min(), ys.max() + 1
    out = {"alpha": float(alpha[mask].mean()), "rad": float(rad[mask].mean())}
    for name, (f0, f1) in (("top", (0.0, 0.33)), ("mid", (0.33, 0.66)), ("bot", (0.66, 1.0))):
        m = np.zeros_like(mask)
        a, b = y0 + int((y1 - y0) * f0), y0 + int((y1 - y0) * f1)
        m[a:b] = mask[a:b]
        out[name] = float(rad[m].mean()) if m.any() else float("nan")
    return out


def compare(params=None):
    rows = []
    for spec in CASES:
        path = os.path.join(REF_DIR, spec[0] + ".npz")
        if not os.path.isfile(path):
            continue
        z = np.load(path)
        ra, rr = z["alpha"], z["cloud"]
        mask = ra > 0.02
        ma, mr = mirror(spec, params)
        ref = figures(ra, rr, mask)
        got = figures(ma, mr, mask)
        rows.append((spec[0], {k: got[k] / max(ref[k], 1e-9) for k in ref}, ref))
    return rows


def table():
    print("mirror / reference over the pixels the reference covers (1.00 = equal; the fit's tolerance was 0.75 .. 1.25)")
    for name, r, ref in compare():
        print("  %-14s alpha %.2f  radiance %.2f  top %.2f  mid %.2f  bottom %.2f   (reference alpha %.2f, radiance %.3f)" %
              (name, r["alpha"], r["rad"], r["top"], r["mid"], r["bot"], ref["alpha"], ref["rad"]))


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "ref":
        reference_all()
    elif cmd == "table":
        table()
    else:
        print(__doc__)
