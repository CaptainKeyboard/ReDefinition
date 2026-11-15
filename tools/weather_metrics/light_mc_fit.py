"""Fit the model's multiple-scattering constants to the photon walks of light_mc_sphere.py and light_mc_slab.py (E40 ML light).

The geometry of every model ray (its samples' optical depth towards the sun and its weights in the march) does not depend on the light's constants, so it is built once and the
objective is a few array operations. Objective: the log of model over walk for the radiance of the sphere (mean and P90 over the disc inside 0.9 R, per view angle and optical
depth) and of the slab top and base, weighted towards the lit views.

    python tools/weather_metrics/light_mc_fit.py DIR [--fit] [--set key=value ...]      DIR holds mc_tau*.npz (spheres) and slab_*.npz (slabs)
"""
import argparse
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import light_cpu as C  # noqa: E402
from weather_metrics import light_mc_sphere as S  # noqa: E402
from weather_metrics import light_mc_slab as L  # noqa: E402

INV4PI = 1.0 / (4.0 * math.pi)
NAMES = ["octaveA", "octaveB", "octaveC", "msBrightness", "msIsotropy", "diffusion", "diffusionTau", "diffusionDecay"]
BOUNDS = [(0.05, 0.95), (0.1, 0.95), (0.5, 1.0), (0.3, 8.0), (0.0, 1.0), (0.0, 12.0), (3.0, 150.0), (0.0, 0.4)]
VIEW_WEIGHT = {0.0: 1.0, 30.0: 1.0, 60.0: 1.0, 90.0: 1.0, 120.0: 0.6, 150.0: 0.25, 180.0: 0.25}
TAU_WEIGHT = {20.0: 0.5, 50.0: 1.0, 100.0: 1.0}


def sphere_geometry(tau_diam, alpha, steps=64):
    """(weights of the march per pixel and step, tau_sun per pixel and step, mu, the disc mask) of the orthographic march of the hard sphere."""
    sigma = tau_diam / (2.0 * S.RADIUS)
    c, e1, e2 = S.camera_axes(alpha)
    pix = 2.0 * S.RADIUS / S.NPIX
    uu, vv = np.meshgrid((np.arange(S.NPIX) + 0.5) * pix - S.RADIUS, (np.arange(S.NPIX) + 0.5) * pix - S.RADIUS, indexing="ij")
    uu, vv = uu.ravel(), vv.ravel()
    r2 = uu * uu + vv * vv
    ok = r2 < S.RADIUS ** 2
    half = np.sqrt(np.maximum(S.RADIUS ** 2 - r2, 0.0))
    dt = 2.0 * half / steps
    ext = 1.0 - np.exp(-sigma * dt)
    T = np.ones(len(uu))
    wts = np.zeros((steps, len(uu)))
    taus = np.zeros((steps, len(uu)))
    for i in range(steps):
        s = half - (i + 0.5) * dt
        pos = uu[:, None] * e1[None, :] + vv[:, None] * e2[None, :] + s[:, None] * c[None, :]
        taus[i] = S.tau_towards_sun(pos, sigma)
        wts[i] = np.where(ok, T * ext, 0.0)
        T = T * (1.0 - ext)
    mu = float(c @ np.array([0.0, 0.0, -1.0]))
    g = (np.arange(S.NPIX) + 0.5) * pix - S.RADIUS
    gu, gv = np.meshgrid(g, g, indexing="ij")
    disc = ((gu * gu + gv * gv) < (0.9 * S.RADIUS) ** 2).ravel()
    return wts, taus, mu, disc


def model_sphere(p, geo):
    wts, taus, mu, disc = geo
    first = C.phase(p, mu, 1.0) * np.exp(-taus)
    s = np.zeros_like(taus)
    an = bn = cn = 1.0
    for i in range(int(min(max(p.octaves, 1), 4))):
        ph = C.phase(p, mu, cn)
        if i > 0:
            ph = ph + (INV4PI - ph) * p.msIsotropy
        s += (1.0 if i == 0 else p.msBrightness) * bn * ph * np.exp(-taus * an)
        an *= p.octaveA
        bn *= p.octaveB
        cn *= p.octaveC
    diffuse = p.diffusion * INV4PI * (1.0 - np.exp(-taus / max(p.diffusionTau, 1e-3))) / (1.0 + max(p.diffusionDecay, 0.0) * taus)
    floor = max(p.ambientFloor, 0.0301) * math.pi * INV4PI
    src = math.pi * (s + diffuse) * p.albedo + floor
    img = np.sum(wts * src, axis=0)
    return img


def disc_stats(img, disc):
    v = img[disc]
    return float(v.mean()), float(np.percentile(v, 90))


def slab_geometry(tau, sun, elev, az, height=1200.0, n=160):
    sigma = tau / height
    mu0 = math.sin(math.radians(sun))
    mv = abs(math.sin(math.radians(elev)))
    up = elev < 0
    ds = height / n / mv
    ext = 1.0 - math.exp(-sigma * ds)
    t = (np.arange(n) + 0.5) / n
    z = t * height if up else (1.0 - t) * height
    taus = sigma * (height - z) / mu0
    wts = ext * (1.0 - ext) ** np.arange(n)
    return wts, taus, L.view_mu(elev, az, sun)


def model_slab(p, geo):
    wts, taus, mu = geo
    first = C.phase(p, mu, 1.0) * np.exp(-taus)
    s = np.zeros_like(taus)
    an = bn = cn = 1.0
    for i in range(int(min(max(p.octaves, 1), 4))):
        ph = C.phase(p, mu, cn)
        if i > 0:
            ph = ph + (INV4PI - ph) * p.msIsotropy
        s += (1.0 if i == 0 else p.msBrightness) * bn * ph * np.exp(-taus * an)
        an *= p.octaveA
        bn *= p.octaveB
        cn *= p.octaveC
    diffuse = p.diffusion * INV4PI * (1.0 - np.exp(-taus / max(p.diffusionTau, 1e-3))) / (1.0 + max(p.diffusionDecay, 0.0) * taus)
    floor = max(p.ambientFloor, 0.0301) * math.pi * INV4PI
    return float(np.sum(wts * (math.pi * (s + diffuse) * p.albedo + floor)))


class Set(object):
    def __init__(self, directory):
        self.spheres = []      # (tau, alpha, geometry, ref mean, ref p90, weight)
        self.slabs = []        # (label, geometry, ref, weight)
        for f in sorted(os.listdir(directory)):
            path = os.path.join(directory, f)
            if f.startswith("mc_") and f.endswith(".npz"):
                d = np.load(path)
                tau = float(d["tau"])
                for k, a in enumerate(S.VIEWS):
                    geo = sphere_geometry(tau, a)
                    ref = d["img1"][k] + d["img2"][k]
                    m, p90 = disc_stats(ref.ravel(), geo[3])
                    self.spheres.append((tau, a, geo, m, p90, TAU_WEIGHT.get(tau, 1.0) * VIEW_WEIGHT[a]))
            elif f.startswith("slab_") and f.endswith(".npz"):
                res = L.load(path)
                for k, (e, a) in enumerate(L.VIEWS):
                    if e not in (53, -53) or a not in (0, 180):
                        continue
                    sun = res["sun"]
                    w = 0.5 if sun == 45 else 0.05 if sun in (30, 70) else 0.0
                    if w == 0.0:
                        continue
                    ref = float(res["L1"][k] + res["L2"][k])
                    self.slabs.append(("tau%g sun%g view %d %d" % (res["tau"], sun, e, a), slab_geometry(res["tau"], sun, e, a), ref, w))

    def residuals(self, p):
        out = []
        for tau, a, geo, rm, rp, w in self.spheres:
            m, p90 = disc_stats(model_sphere(p, geo), geo[3])
            out.append(w * math.log(m / rm))
            out.append(w * math.log(p90 / rp))
        for label, geo, ref, w in self.slabs:
            out.append(w * 2.0 * math.log(model_slab(p, geo) / ref))
        return np.array(out)

    def report(self, p):
        print("spheres: model / walk (mean, P90) per optical depth and view angle")
        print("  tau   " + "   ".join("a%-3d mean  P90 " % a for a in S.VIEWS))
        taus = sorted(set(s[0] for s in self.spheres))
        for tau in taus:
            row = []
            for s in self.spheres:
                if s[0] != tau:
                    continue
                m, p90 = disc_stats(model_sphere(p, s[2]), s[2][3])
                row.append("%5.2f %5.2f " % (m / s[3], p90 / s[4]))
            print("  %-5g %s" % (tau, "  ".join(row)))
        print("slabs: model / walk")
        for label, geo, ref, w in self.slabs:
            print("  %-26s walk %.3f model %.3f ratio %.2f" % (label, ref, model_slab(p, geo), model_slab(p, geo) / ref))


def make_params(vec=None, overrides=None):
    p = S.model_params()
    if vec is not None:
        for n, v in zip(NAMES, vec):
            setattr(p, n, float(v))
    for k, v in (overrides or {}).items():
        setattr(p, k, v)
    return p


def fit(ds, start, energy_bound=4.0, restarts=3):
    from scipy.optimize import least_squares

    def f(vec):
        p = make_params(vec)
        r = ds.residuals(p)
        # the octave energy (sum of the weights) must stay below the white diffuser's bound
        energy = 1.0 + vec[3] * (vec[1] + vec[1] ** 2 + vec[1] ** 3)
        pen = max(energy - energy_bound, 0.0) * 3.0
        return np.append(r, pen)

    lo = [b[0] for b in BOUNDS]
    hi = [b[1] for b in BOUNDS]
    best = None
    rng = np.random.default_rng(3)
    starts = [np.array(start)] + [np.array(start) * rng.uniform(0.6, 1.5, size=len(start)) for _ in range(restarts)]
    for s0 in starts:
        s0 = np.clip(s0, lo, hi)
        r = least_squares(f, s0, bounds=(lo, hi), diff_step=1e-3, max_nfev=60)
        print("  start cost %.4f" % r.cost, flush=True)
        if best is None or r.cost < best.cost:
            best = r
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir")
    ap.add_argument("--fit", action="store_true")
    ap.add_argument("--set", nargs="*")
    ap.add_argument("--energy", type=float, default=4.0)
    a = ap.parse_args()
    ds = Set(a.dir)
    over = S.parse_set(a.set)
    p0 = make_params(None, over)
    print("shipped constants:")
    ds.report(p0)
    r0 = ds.residuals(p0)
    print("cost %.4f" % (0.5 * float(r0 @ r0)))
    if a.fit:
        best = fit(ds, [getattr(p0, n) for n in NAMES], a.energy)
        p = make_params(best.x, over)
        print("fit: " + "  ".join("%s %.4f" % (n, v) for n, v in zip(NAMES, best.x)))
        print("octave energy %.3f" % (1.0 + best.x[3] * (best.x[1] + best.x[1] ** 2 + best.x[1] ** 3)))
        ds.report(p)
        r = ds.residuals(p)
        print("cost %.4f" % (0.5 * float(r @ r)))


if __name__ == "__main__":
    main()
