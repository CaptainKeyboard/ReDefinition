"""The cell diffusion of the cloud light (E40 LM2) against the photon walks of light_mc_sphere.py and light_mc_slab.py, with the T03 parity cells held.

The model is light_cpu.diffusion_terms (the shader's DirectOfLight with a layer cell width): the incidence of the sun on the lit surface from the optical depth straight up over the one
towards the sun, the far depth from the chord of an ellipsoid of the type's height and the cell width, the slab's diffusion profile, the side losses, and the octaves' isotropic share
following the incidence. The walk's sphere is symmetric about the sun's axis, so one walk serves every sun elevation: the model's 'up' (the direction of the type's top) is tilted to
the elevation E from the sun, the walk's image stays. The objective: the log of model over walk of the sphere's mean and lit tenth (P90) for the views 0 to 120 degrees, the slab's top
and base, the T03 parity cells (light_cpu_fit.figures) near the sun held at the shipped model's level and the side and front cells not below it. The views 150 and 180 are left out
of the objective: there the walk and the parity reference renderer disagree (section 2 of .local/plans/atmospheres/E40-lm2-light-result.md), and the brief holds the near-sun parity.

    python tools/weather_metrics/light_mc_cell.py report DIR [--set key=value ...] [--off]      DIR holds the walks: fit/mc_tau{20,50,100}_g0.85.npz and slab/slab_*.npz
    python tools/weather_metrics/light_mc_cell.py fit DIR [--free a,b,c] [--set ...]
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
SPHERE_W = 2.0 * S.RADIUS          # the walk's sphere: a cell as wide as high
PARITY_W = 1600.0                  # the parity cell: 1600 m wide
LAYER_W = 1.0e7                     # the walk's slab: no side
ELEVATIONS = (15, 30, 45, 60, 90)
AZIMUTHS = (0, 180)
FREE = ("cellGain", "cellBeta", "cellExtrapolation", "cellSide", "cellOctaves", "cellIncidencePower")
BOUNDS = {"cellGain": (0.0, 12.0), "cellBeta": (0.05, 1.5), "cellExtrapolation": (0.05, 10.0), "cellSide": (0.0, 20.0), "cellOctaves": (0.0, 3.0),
          "cellIncidencePower": (0.0, 3.0), "cellIncidenceEps": (0.01, 20.0)}


def source(p, mu, ts, tup, dens, height, width, sin_e):
    f, d = C.diffusion_terms(p, ts, tup, dens, height, width, sin_e)
    return C.octave_sum(p, mu, ts, f) + d


def sphere_geometry(tau, alpha, elevation, azimuth, steps=64):
    """The orthographic march of the walk's sphere with the model's inputs per pixel and step: weights, tau to the sun, tau straight up to the top plane of an 'up' at the elevation."""
    sigma = tau / (2.0 * S.RADIUS)
    c, e1, e2 = S.camera_axes(alpha)
    pix = 2.0 * S.RADIUS / S.NPIX
    g = (np.arange(S.NPIX) + 0.5) * pix - S.RADIUS
    uu, vv = np.meshgrid(g, g, indexing="ij")
    uu, vv = uu.ravel(), vv.ravel()
    r2 = uu * uu + vv * vv
    ok = r2 < S.RADIUS ** 2
    half = np.sqrt(np.maximum(S.RADIUS ** 2 - r2, 0.0))
    dt = 2.0 * half / steps
    ext = 1.0 - np.exp(-sigma * dt)
    e, f = math.radians(elevation), math.radians(azimuth)
    up = np.array([math.cos(e) * math.cos(f), math.cos(e) * math.sin(f), math.sin(e)])
    T = np.ones(len(uu))
    wts = np.zeros((steps, len(uu)))
    taus = np.zeros((steps, len(uu)))
    tups = np.zeros((steps, len(uu)))
    for i in range(steps):
        s = half - (i + 0.5) * dt
        pos = uu[:, None] * e1[None, :] + vv[:, None] * e2[None, :] + s[:, None] * c[None, :]
        taus[i] = S.tau_towards_sun(pos, sigma)
        tups[i] = sigma * np.maximum(S.RADIUS - pos @ up, 0.0)
        wts[i] = np.where(ok, T * ext, 0.0)
        T = T * (1.0 - ext)
    mu = float(c @ np.array([0.0, 0.0, -1.0]))
    return dict(w=wts, ts=taus, tup=tups, mu=mu, disc=(r2 < (0.9 * S.RADIUS) ** 2), sigma=sigma, sin_e=math.sin(e))


def sphere_image(p, g, width=SPHERE_W):
    src = source(p, g["mu"], g["ts"], g["tup"], g["sigma"], 2.0 * S.RADIUS, width, g["sin_e"])
    floor = max(p.ambientFloor, 0.0301) * math.pi * INV4PI
    return np.sum(g["w"] * (math.pi * src * p.albedo + floor), axis=0)


def slab_view(p, tau, sun, elev, az, width=LAYER_W, height=1200.0, n=160):
    sigma = tau / height
    mu0 = math.sin(math.radians(sun))
    mv = abs(math.sin(math.radians(elev)))
    ds = height / n / mv
    ext = 1.0 - math.exp(-sigma * ds)
    t = (np.arange(n) + 0.5) / n
    z = t * height if elev < 0 else (1.0 - t) * height
    ts = sigma * (height - z) / mu0
    wts = ext * (1.0 - ext) ** np.arange(n)
    src = source(p, L.view_mu(elev, az, sun), ts, sigma * (height - z), sigma, height, width, mu0)
    floor = max(p.ambientFloor, 0.0301) * math.pi * INV4PI
    return float(np.sum(wts * (math.pi * src * p.albedo + floor)))


def params(over=None, on=True):
    p = S.model_params()
    for k, v in (over or {}).items():
        setattr(p, k, v)
    p.on = on
    return p


def parity(p):
    from weather_metrics import light_cpu_fit as P
    q = C.Params(**{k: v for k, v in p.__dict__.items() if k != "on"})
    q.cellWidth = PARITY_W if getattr(p, "on", True) else 0.0
    return P.figures(q)["regions"]


class Walks(object):
    def __init__(self, directory):
        self.sph = []
        for tau in (20, 50, 100):
            d = np.load(os.path.join(directory, "fit", "mc_tau%d_g0.85.npz" % tau))
            for k, a in enumerate(S.VIEWS):
                ref = (d["img1"][k] + d["img2"][k]).ravel()
                for e in ELEVATIONS:
                    for az in AZIMUTHS:
                        g = sphere_geometry(tau, a, e, az)
                        v = ref[g["disc"]]
                        self.sph.append((tau, a, e, g, float(v.mean()), float(np.percentile(v, 90)), S.centre(ref.reshape(S.NPIX, S.NPIX))))
        self.slab = []
        folder = os.path.join(directory, "slab")
        for f in sorted(os.listdir(folder)):
            res = L.load(os.path.join(folder, f))
            for k, (e, a) in enumerate(L.VIEWS):
                self.slab.append((res["tau"], res["sun"], e, a, float(res["L1"][k] + res["L2"][k])))
        self.parity0 = None

    def width(self, p, w):
        return w if p.on else 0.0

    def sphere_table(self, p):
        rows = {}
        for tau, a, e, g, rm, rp, rc in self.sph:
            img = sphere_image(p, g, self.width(p, SPHERE_W))
            v = img[g["disc"]]
            rows.setdefault((tau, a), []).append((v.mean() / rm, np.percentile(v, 90) / rp, S.centre(img.reshape(S.NPIX, S.NPIX)) / rc))
        return {k: np.exp(np.mean(np.log(np.array(v)), axis=0)) for k, v in rows.items()}

    def report(self, p, title):
        print("== %s" % title)
        t = self.sphere_table(p)
        print(" sphere, model / walk, geometric mean over the sun elevations %s: P90 (mean) [centre]; views %s" % (ELEVATIONS, " ".join("%g" % a for a in S.VIEWS)))
        for tau in (20, 50, 100):
            print("   tau %3d " % tau + " ".join("%4.2f(%4.2f)[%4.2f]" % (t[(tau, a)][1], t[(tau, a)][0], t[(tau, a)][2]) for a in S.VIEWS))
        print(" slab, model / walk (top seen at 53 degrees with the sun behind, base from below)")
        for tau, sun, e, a, ref in self.slab:
            if (e, a) not in ((53, 0), (-53, 0)):
                continue
            m = slab_view(p, tau, sun, e, a, self.width(p, LAYER_W))
            print("   tau %3g sun %2g %-4s walk %.3f model %.3f ratio %.2f" % (tau, sun, "top" if e > 0 else "base", ref, m, m / ref))
        for case, d in parity(p).items():
            print("  parity %-4s " % case + " ".join("%s %.2f" % (k, v) for k, v in d.items()))
        return t

    def residuals(self, p, wpar=1.5):
        out = []
        vw = {0: 1.0, 30: 1.0, 60: 1.0, 90: 1.0, 120: 0.7, 150: 0.0, 180: 0.0}
        ew = {15: 0.6, 30: 1.0, 45: 1.0, 60: 1.0, 90: 0.5}
        tw = {20: 0.5, 50: 1.0, 100: 1.0}
        for tau, a, e, g, rm, rp, rc in self.sph:
            if vw[a] == 0.0:
                continue
            v = sphere_image(p, g, self.width(p, SPHERE_W))[g["disc"]]
            w = vw[a] * ew[e] * tw[tau] * 0.33
            out.append(w * math.log(v.mean() / rm))
            out.append(w * math.log(np.percentile(v, 90) / rp))
        for tau, sun, e, a, ref in self.slab:
            if (e, a) not in ((53, 0), (53, 180), (-53, 0), (90, 0), (20, 90)):
                continue
            out.append((1.2 if e > 0 else 0.8) * math.log(slab_view(p, tau, sun, e, a, self.width(p, LAYER_W)) / ref))
        if self.parity0 is None:
            self.parity0 = parity(params(on=False))
        for case, d in parity(p).items():
            for r, v in d.items():
                wr = (2.0 if case == "ns" else 1.0) * (0.5 if r == "rim" else 1.0)
                lr = math.log(max(v, 1e-6) / self.parity0[case][r])
                out.append(wpar * wr * (lr if case == "ns" else min(lr, 0.0)))
        return np.array(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=("report", "fit"))
    ap.add_argument("dir")
    ap.add_argument("--set", nargs="*")
    ap.add_argument("--off", action="store_true", help="the model without the cell diffusion (the slab-fitted diffusion term)")
    ap.add_argument("--free", default=",".join(FREE))
    ap.add_argument("--nfev", type=int, default=30)
    a = ap.parse_args()
    over = S.parse_set(a.set)
    ds = Walks(a.dir)
    p = params(over, on=not a.off)
    ds.report(p, "the shipped model" if a.off else "the cell diffusion")
    if a.cmd == "fit":
        from scipy.optimize import least_squares
        free = a.free.split(",")
        v0 = [getattr(p, n) for n in free]
        r = least_squares(lambda v: ds.residuals(params(dict(over, **dict(zip(free, map(float, v)))))), v0,
                          bounds=([BOUNDS[n][0] for n in free], [BOUNDS[n][1] for n in free]), diff_step=1e-3, max_nfev=a.nfev)
        best = dict(zip(free, [round(float(x), 4) for x in r.x]))
        print("cost %.3f  %s" % (r.cost, best))
        ds.report(params(dict(over, **best)), "fit")


if __name__ == "__main__":
    main()
