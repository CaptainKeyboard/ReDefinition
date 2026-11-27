"""A photon walk through a sunlit plane-parallel slab, and the model's source function against it (E40 ML light).

The same reference as light_mc_sphere.py for the local question: what does the lit side of a thick cloud return, for a surface lit at an incidence angle and seen from a
direction? A uniform slab of optical depth tau (infinite horizontally, thickness H m), a droplet's dual-lobe Henyey-Greenstein phase, no absorption (albedo 0.9999), a parallel sun at
elevation e0. Every collision sends its share to a set of cameras above and below (next event estimation), so the walk gives, per view:

    L(view)       the radiance leaving the top (the base) in W, the radiance of a white card facing the sun (irradiance pi W): the lit side of the cloud
    J(depth, view) the source function (radiance scattered towards the view per unit extinction, all orders) per optical depth below the top

and the model's source function pi (octave sum + diffusion) of light_cpu at the same depth and view, with tau_sun = the optical depth to the top / sin(e0) (the model's light march
is exact in a uniform slab). A view is named by its elevation and by its azimuth against the sun: 0 = the camera on the sun's side of the surface normal (the sun behind it),
90 = side, 180 = the camera on the far side (it looks towards the sun: the forward lobe).

    python tools/weather_metrics/light_mc_slab.py run --tau 100 --sun 45 --photons 400000 --out DIR
    python tools/weather_metrics/light_mc_slab.py compare DIR [--set key=value ...]
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

INV4PI = 1.0 / (4.0 * math.pi)
# (view elevation deg, azimuth against the sun deg); elevation > 0 above the top looking down, < 0 below the base looking up
VIEWS = [(90, 0), (53, 0), (53, 90), (53, 180), (20, 0), (20, 90), (20, 180), (-53, 0), (-53, 180), (-90, 0)]
NBINS = 12


def cam(elev, az):
    e, a = math.radians(elev), math.radians(az)
    return np.array([math.cos(e) * math.cos(a), math.cos(e) * math.sin(a), math.sin(e)])


def run(tau, sun_elev, photons, g1=0.85, g2=-0.3, w1=0.92, albedo=0.9999, seed=5, height=1200.0, batch=200000, sky=False):
    """sky (master plan 47.1 A1, PRUEFPROTOKOLL 366): the slab under a uniform sky instead of the sun. The photons enter cosine-weighted over the upper hemisphere
    (the flux of a sky of radiance L_sky through the top is pi L_sky), and the radiances are over L_sky: what a white card facing up shows under that sky. L1 is
    then the sky's light scattered once, L2 the rest; sun_elev is not used."""
    sigma = tau / height
    rng = np.random.default_rng(seed)
    mu0 = 1.0 if sky else math.sin(math.radians(sun_elev))
    ldir = np.array([math.cos(math.radians(sun_elev)), 0.0, mu0])
    cams = [cam(e, a) for e, a in VIEWS]
    L1 = np.zeros(len(cams))
    L2 = np.zeros(len(cams))
    edges = np.linspace(0.0, tau, NBINS + 1)
    jsum = np.zeros((len(cams), NBINS))
    done = 0
    while done < photons:
        n = min(batch, photons - done)
        done += n
        z = np.full(n, height)
        if sky:
            ct = np.sqrt(rng.uniform(size=n))
            st = np.sqrt(np.maximum(1.0 - ct * ct, 0.0))
            ph = rng.uniform(size=n) * 2.0 * math.pi
            d = np.stack([st * np.cos(ph), st * np.sin(ph), -ct], axis=1)
        else:
            d = np.tile(-ldir, (n, 1))
        order = 0
        pos = np.zeros((n, 3))
        pos[:, 2] = z
        while len(pos) > 0 and order < 5000:
            step = -np.log(1.0 - rng.uniform(size=len(pos))) / sigma
            pos = pos + d * step[:, None]
            inside = (pos[:, 2] > 0.0) & (pos[:, 2] < height)
            pos, d = pos[inside], d[inside]
            if len(pos) == 0:
                break
            order += 1
            depth = (height - pos[:, 2]) * sigma
            dbin = np.clip(np.searchsorted(edges, depth, side="right") - 1, 0, NBINS - 1)
            for k, c in enumerate(cams):
                p = S.phase(d @ c, g1, g2, w1)
                dist = (height - pos[:, 2]) / c[2] if c[2] > 0 else pos[:, 2] / -c[2]
                t = np.exp(-sigma * dist)
                val = albedo * p
                s = float(np.sum(val * t))
                if order == 1:
                    L1[k] += s
                else:
                    L2[k] += s
                jsum[k] += np.bincount(dbin, weights=val, minlength=NBINS)
            cos_s = np.where(rng.uniform(size=len(pos)) < w1, S.sample_hg(g1, rng.uniform(size=len(pos))), S.sample_hg(g2, rng.uniform(size=len(pos))))
            d = S.rotate(d, cos_s, rng.uniform(size=len(pos)) * 2.0 * math.pi)
            d /= np.linalg.norm(d, axis=1, keepdims=True)
    mu_v = np.array([abs(c[2]) for c in cams])
    scale = math.pi * mu0 / photons
    # J (W per unit extinction): sum E mu0 / (N sigma dz) with dz = tau / NBINS / sigma -> E mu0 NBINS / (N tau)
    return dict(tau=tau, sun=sun_elev, L1=L1 * scale / mu_v, L2=L2 * scale / mu_v, jtab=jsum * math.pi * mu0 * NBINS / (photons * tau), edges=edges,
                photons=photons, g1=g1, g2=g2, w1=w1, albedo=albedo)


def view_mu(elev, az, sun_elev):
    """The model's mu of a view: cosine between the direction to the camera and the travel direction of the sun's light."""
    ldir = np.array([math.cos(math.radians(sun_elev)), 0.0, math.sin(math.radians(sun_elev))])
    return float(-(cam(elev, az) @ ldir))


def model_view(p, tau, sun_elev, elev, az, height=1200.0, n=200):
    """The model's radiance (W) of the slab for a view: marched through the slab with the model's source (direct + floor, no sky)."""
    sigma = tau / height
    mu0 = math.sin(math.radians(sun_elev))
    mv = abs(math.sin(math.radians(elev)))
    mu = view_mu(elev, az, sun_elev)
    up = elev < 0
    ds = height / n / mv
    ext = 1.0 - math.exp(-sigma * ds)
    trans, acc = 1.0, 0.0
    floor = max(p.ambientFloor, 0.0301) * math.pi * INV4PI
    for i in range(n):
        t = (i + 0.5) / n
        z = t * height if up else (1.0 - t) * height
        ts = sigma * (height - z) / mu0
        first, ms, diffuse = S.octave_parts(p, mu, np.array([ts]))
        src = math.pi * float(first[0] + ms[0] + diffuse[0]) * p.albedo + floor
        acc += trans * src * ext
        trans *= 1.0 - ext
    return acc


def print_reference(res):
    print("slab tau %g sun %g deg (a horizontal card shows %.3f W)   g %.2f %.2f %.2f   photons %d" % (res["tau"], res["sun"], math.sin(math.radians(res["sun"])), res["g1"], res["g2"],
                                                                                                         res["w1"], res["photons"]))


def compare(res, overrides=None, verbose=True):
    p = S.model_params(overrides)
    rows = []
    for k, (e, a) in enumerate(VIEWS):
        ref = float(res["L1"][k] + res["L2"][k])
        mod = model_view(p, res["tau"], res["sun"], e, a)
        rows.append((e, a, ref, mod, float(res["L1"][k])))
    if verbose:
        print("tau %g sun %g: radiance of the slab (W) walk / model; view = elevation, azimuth against the sun (0: sun behind the camera, 180: looking towards the sun)" % (res["tau"], res["sun"]))
        for e, a, ref, mod, r1 in rows:
            print("  view %4d %4d : walk %.3f  model %.3f  ratio %.2f   (single scattering of the walk %.3f)" % (e, a, ref, mod, mod / ref, r1))
    return rows


def compare_source(res, overrides=None):
    p = S.model_params(overrides)
    mid = 0.5 * (res["edges"][1:] + res["edges"][:-1])
    mu0 = math.sin(math.radians(res["sun"]))
    print("source function J (W per unit extinction) per optical depth below the top; tau_sun = depth / %.3f" % mu0)
    print("  depth   " + " ".join("%6.2f" % m for m in mid))
    for k, (e, a) in enumerate(VIEWS):
        if e < 0:
            continue
        mu = view_mu(e, a, res["sun"])
        mj = S.model_source(p, mu, mid / mu0)
        print(" view %3d %3d (mu %+.2f)" % (e, a, mu))
        print("   walk  " + " ".join("%6.3f" % v for v in res["jtab"][k]))
        print("   model " + " ".join("%6.3f" % v for v in mj))


def cmd_run(a):
    res = run(a.tau, a.sun, a.photons, g1=a.g1, g2=a.g2, w1=a.w1, seed=a.seed, sky=a.sky)
    os.makedirs(a.out, exist_ok=True)
    if a.sky:
        # Its own name (not slab_*: compare reads those as the sun's walks).
        np.savez(os.path.join(a.out, "sky_tau%g.npz" % a.tau), **res)
        print("tau %g under a uniform sky: radiance over the sky's (a white card facing up: 1); views %s" % (a.tau, VIEWS))
        print("  " + " ".join("%.4f" % v for v in (res["L1"] + res["L2"])))
        return
    np.savez(os.path.join(a.out, "slab_tau%g_sun%g.npz" % (a.tau, a.sun)), **res)
    print_reference(res)
    compare(res)


def load(path):
    d = np.load(path)
    res = {k: d[k] for k in d.files}
    for k in ("tau", "sun", "photons", "g1", "g2", "w1", "albedo"):
        res[k] = float(res[k])
    return res


def cmd_compare(a):
    over = S.parse_set(a.set)
    for f in sorted(os.listdir(a.dir)):
        if f.startswith("slab_") and f.endswith(".npz"):
            res = load(os.path.join(a.dir, f))
            print_reference(res)
            compare(res, over)
            if a.source:
                compare_source(res, over)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--tau", type=float, default=100.0)
    r.add_argument("--sun", type=float, default=45.0)
    r.add_argument("--photons", type=int, default=400000)
    r.add_argument("--g1", type=float, default=0.85)
    r.add_argument("--g2", type=float, default=-0.3)
    r.add_argument("--w1", type=float, default=0.92)
    r.add_argument("--seed", type=int, default=5)
    r.add_argument("--sky", action="store_true", help="a uniform sky instead of the sun (the radiances over the sky's)")
    r.add_argument("--out", required=True)
    c = sub.add_parser("compare")
    c.add_argument("dir")
    c.add_argument("--set", nargs="*")
    c.add_argument("--source", action="store_true")
    a = ap.parse_args()
    {"run": cmd_run, "compare": cmd_compare}[a.cmd](a)


if __name__ == "__main__":
    main()
