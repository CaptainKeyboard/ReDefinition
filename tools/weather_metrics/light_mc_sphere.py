"""A photon walk through a sunlit cumulus cell, and the model's light against it (E40 ML light).

The reference: a sphere of uniform extinction (radius R, optical depth of the diameter tau), a droplet's dual-lobe Henyey-Greenstein phase, no absorption
(albedo 0.9999), lit by a parallel sun. Photons enter over the sunlit disc, are followed by delta tracking through every scattering order, and each collision
sends its share to a set of orthographic cameras (next event estimation: weight x albedo x phase x transmission to the surface). The unit is W, the radiance of a white card facing the sun
(irradiance pi W): an image pixel is the radiance of the cell towards that camera in W. Two products:

    image(alpha)   the radiance image of the sphere for a camera whose direction from the cell is alpha away from the sun (0 = the sun behind the camera, the sunlit front;
                   90 = side; 180 = the camera behind the cell, the silver lining), split into single scattering and the rest
    source(alpha)  the source function J (radiance scattered per unit extinction, all orders) per bin of the optical depth towards the sun as the model's light march
                   reads it (4 taps, growing steps), for the same cameras: what the model's octave sum plus diffusion term has to equal

The model's side: the shader's source function (light_cpu.octave_sum, the diffusion term, powder, floor) marched through the same sphere with the same cameras
(orthographic), so a reference pixel and a model pixel are the same ray.

    python tools/weather_metrics/light_mc_sphere.py run --tau 50 --photons 200000 --out DIR      the reference of one optical depth
    python tools/weather_metrics/light_mc_sphere.py compare DIR [--set key=value ...]            the model against it, per view angle
"""
import argparse
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import light_cpu as C  # noqa: E402

RADIUS = 800.0                                     # m, the model's own units (powder length, sun march length are in m)
INV4PI = 1.0 / (4.0 * math.pi)
VIEWS = (0.0, 30.0, 60.0, 90.0, 120.0, 150.0, 180.0)   # degrees between the direction cell -> camera and the direction cell -> sun
NPIX = 41
BOUND = 1.05                                       # the shader clips the light march to the cell's bounding sphere (CellClip)
SUN_STEPS, TAP_RATIO, SUN_MARCH = 4, 2.0, 4000.0


def hg(mu, g):
    return (1.0 - g * g) / (4.0 * math.pi * np.maximum(1.0 + g * g - 2.0 * g * mu, 1e-6) ** 1.5)


def phase(mu, g1, g2, w1):
    return w1 * hg(mu, g1) + (1.0 - w1) * hg(mu, g2)


def sample_hg(g, u):
    if abs(g) < 1e-4:
        return 1.0 - 2.0 * u
    s = (1.0 - g * g) / (1.0 - g + 2.0 * g * u)
    return (1.0 + g * g - s * s) / (2.0 * g)


def rotate(d, cos_t, phi):
    """The direction at polar angle cos_t from d (N,3), azimuth phi."""
    sin_t = np.sqrt(np.maximum(1.0 - cos_t * cos_t, 0.0))
    a = np.where(np.abs(d[:, 2:3]) < 0.9, np.array([[0.0, 0.0, 1.0]]), np.array([[1.0, 0.0, 0.0]]))
    u = np.cross(d, a)
    u /= np.linalg.norm(u, axis=1, keepdims=True)
    v = np.cross(d, u)
    return d * cos_t[:, None] + (u * np.cos(phi)[:, None] + v * np.sin(phi)[:, None]) * sin_t[:, None]


def camera_axes(alpha_deg):
    """Direction cell -> camera c and the image plane's axes (e1 in the plane of sun and camera, e2 across)."""
    a = math.radians(alpha_deg)
    c = np.array([math.sin(a), 0.0, math.cos(a)])
    e1 = np.array([math.cos(a), 0.0, -math.sin(a)])
    e2 = np.array([0.0, 1.0, 0.0])
    return c, e1, e2


def tau_towards_sun(pos, sigma, taps=SUN_STEPS, ratio=TAP_RATIO, bound=BOUND, max_len=SUN_MARCH):
    """The model's LightTau for the hard sphere: growing taps from the point to the cell's bounding sphere along +z (the sun), at most max_len."""
    # exit of the bounding sphere along +z from pos
    rb = RADIUS * bound
    b = pos[:, 2]
    c = np.sum(pos * pos, axis=1) - rb * rb
    disc = np.maximum(b * b - c, 0.0)
    t1 = -b + np.sqrt(disc)
    t1 = np.minimum(np.maximum(t1, 0.0), max_len)
    norm = float(taps) if ratio == 1.0 else (ratio ** taps - 1.0) / (ratio - 1.0)
    prev = np.zeros(len(pos))
    gain = 1.0
    acc = np.zeros(len(pos))
    for _ in range(taps):
        ln = t1 * gain / norm
        edge = prev + ln
        mid = 0.5 * (prev + edge)
        q = pos.copy()
        q[:, 2] += mid
        inside = np.sum(q * q, axis=1) < RADIUS * RADIUS
        acc += np.where(inside, sigma, 0.0) * (edge - prev)
        prev = edge
        gain *= ratio
    return acc


TAU_EDGES = np.array([0.0, 0.5, 1.0, 2.0, 4.0, 8.0, 16.0, 32.0, 64.0, 1e9])


def run(tau_diam, photons, g1=0.85, g2=-0.3, w1=0.92, albedo=0.9999, seed=11, batch=100000):
    sigma = tau_diam / (2.0 * RADIUS)
    rng = np.random.default_rng(seed)
    nv = len(VIEWS)
    cams = [camera_axes(a) for a in VIEWS]
    img1 = np.zeros((nv, NPIX, NPIX))
    img2 = np.zeros((nv, NPIX, NPIX))
    pix = 2.0 * RADIUS / NPIX
    E = math.pi
    jsum = np.zeros((nv, len(TAU_EDGES) - 1))
    # uniform points for the volume of each tau bin
    pts = rng.uniform(-RADIUS, RADIUS, size=(400000, 3))
    pts = pts[np.sum(pts * pts, axis=1) < RADIUS * RADIUS]
    vol = np.histogram(tau_towards_sun(pts, sigma), bins=TAU_EDGES)[0] / len(pts)
    done = 0
    while done < photons:
        n = min(batch, photons - done)
        done += n
        rho = np.sqrt(rng.uniform(size=n)) * RADIUS
        ph = rng.uniform(size=n) * 2.0 * math.pi
        pos = np.stack([rho * np.cos(ph), rho * np.sin(ph), np.sqrt(RADIUS * RADIUS - rho * rho)], axis=1)
        d = np.tile(np.array([0.0, 0.0, -1.0]), (n, 1))
        w = np.full(n, E * math.pi * RADIUS * RADIUS / photons)
        order = 0
        while len(pos) > 0 and order < 3000:
            step = -np.log(1.0 - rng.uniform(size=len(pos))) / sigma
            pos = pos + d * step[:, None]
            inside = np.sum(pos * pos, axis=1) < RADIUS * RADIUS
            pos, d, w = pos[inside], d[inside], w[inside]
            if len(pos) == 0:
                break
            order += 1
            ts = tau_towards_sun(pos, sigma)
            tbin = np.clip(np.searchsorted(TAU_EDGES, ts, side="right") - 1, 0, len(TAU_EDGES) - 2)
            for k, (c, e1, e2) in enumerate(cams):
                cos_t = d @ c
                p = phase(cos_t, g1, g2, w1)
                b = pos @ c
                cc = np.sum(pos * pos, axis=1) - RADIUS * RADIUS
                texit = -b + np.sqrt(np.maximum(b * b - cc, 0.0))
                contrib = w * albedo * p * np.exp(-sigma * texit)
                iu = np.clip(((pos @ e1 + RADIUS) / pix).astype(int), 0, NPIX - 1)
                iv = np.clip(((pos @ e2 + RADIUS) / pix).astype(int), 0, NPIX - 1)
                flat = iu * NPIX + iv
                acc = np.bincount(flat, weights=contrib, minlength=NPIX * NPIX).reshape(NPIX, NPIX) / (pix * pix)
                if order == 1:
                    img1[k] += acc
                else:
                    img2[k] += acc
                jsum[k] += np.bincount(tbin, weights=w * albedo * p, minlength=len(TAU_EDGES) - 1)
            w = w * albedo
            cos_s = np.where(rng.uniform(size=len(pos)) < w1, sample_hg(g1, rng.uniform(size=len(pos))), sample_hg(g2, rng.uniform(size=len(pos))))
            d = rotate(d, cos_s, rng.uniform(size=len(pos)) * 2.0 * math.pi)
            d /= np.linalg.norm(d, axis=1, keepdims=True)
    vsph = 4.0 / 3.0 * math.pi * RADIUS ** 3
    # J per unit extinction: sum / (sigma V_bin); the photons carry the power of the whole disc
    with np.errstate(divide="ignore", invalid="ignore"):
        jtab = jsum / (sigma * vsph * vol[None, :])
    return dict(tau=tau_diam, img1=img1, img2=img2, jtab=jtab, vol=vol, photons=photons, g1=g1, g2=g2, w1=w1, albedo=albedo)


# ---- the model ----

def model_params(overrides=None):
    p = C.Params(ambientMs=0.0)
    for k, v in (overrides or {}).items():
        setattr(p, k, v)
    return p


def octave_parts(p, mu, tau):
    """(octave 0 = single scattering, octaves 1.. = multiple scattering, diffusion) of the source in units of the sun's colour."""
    first = C.phase(p, mu, 1.0) * np.exp(-tau)
    total = C.octave_sum(p, mu, tau)
    diffuse = p.diffusion * INV4PI * (1.0 - np.exp(-tau / max(p.diffusionTau, 1e-3))) / (1.0 + max(p.diffusionDecay, 0.0) * tau)
    return first, total - first, diffuse


def model_source(p, mu, tau):
    first, ms, diffuse = octave_parts(p, mu, tau)
    return math.pi * (first + ms + diffuse) * p.albedo


def model_image(p, tau_diam, alpha_deg, steps=96, split=False):
    """The model's march through the sphere for the orthographic camera, radiance in W per pixel on the reference's grid (floor included, no sky)."""
    sigma = tau_diam / (2.0 * RADIUS)
    c, e1, e2 = camera_axes(alpha_deg)
    pix = 2.0 * RADIUS / NPIX
    uu, vv = np.meshgrid((np.arange(NPIX) + 0.5) * pix - RADIUS, (np.arange(NPIX) + 0.5) * pix - RADIUS, indexing="ij")
    uu, vv = uu.ravel(), vv.ravel()
    r2 = uu * uu + vv * vv
    ok = r2 < RADIUS * RADIUS
    half = np.sqrt(np.maximum(RADIUS * RADIUS - r2, 0.0))
    mu = float(c @ np.array([0.0, 0.0, -1.0]))   # the model's mu: the cosine between the direction to the camera and the sun's travel direction
    rad = np.zeros(len(uu))
    rad1 = np.zeros(len(uu))
    T = np.ones(len(uu))
    dt = 2.0 * half / steps
    ext = 1.0 - np.exp(-sigma * dt)
    floor = max(p.ambientFloor, 0.0301) * math.pi * INV4PI
    for i in range(steps):
        s = half - (i + 0.5) * dt             # the ray runs from the camera side (+c) inwards
        pos = uu[:, None] * e1[None, :] + vv[:, None] * e2[None, :] + s[:, None] * c[None, :]
        ts = tau_towards_sun(pos, sigma)
        first, ms, diffuse = octave_parts(p, mu, ts)
        src = math.pi * (first + ms + diffuse) * p.albedo + floor
        add = T * src * ext
        rad += np.where(ok, add, 0.0)
        rad1 += np.where(ok, T * math.pi * first * p.albedo * ext, 0.0)
        T = T * (1.0 - ext)
    out = rad.reshape(NPIX, NPIX), rad1.reshape(NPIX, NPIX)
    return out


def stats(img, ref_disc=0.9):
    """The mean and the P90 of a radiance image over the pixels inside ref_disc of the radius."""
    pix = 2.0 * RADIUS / NPIX
    g = (np.arange(NPIX) + 0.5) * pix - RADIUS
    uu, vv = np.meshgrid(g, g, indexing="ij")
    m = (uu * uu + vv * vv) < (ref_disc * RADIUS) ** 2
    v = img[m]
    return float(v.mean()), float(np.percentile(v, 90)), float(v.max())


def centre(img):
    return float(img[NPIX // 2 - 1:NPIX // 2 + 2, NPIX // 2 - 1:NPIX // 2 + 2].mean())


def cmd_run(a):
    res = run(a.tau, a.photons, g1=a.g1, g2=a.g2, w1=a.w1, seed=a.seed)
    os.makedirs(a.out, exist_ok=True)
    np.savez(os.path.join(a.out, "mc_tau%g_g%g.npz" % (a.tau, a.g1)), **{k: v for k, v in res.items()})
    print_reference(res)


def print_reference(res):
    print("reference tau %g  g %.2f %.2f %.2f  photons %d   radiance in W (a white card facing the sun = 1)" % (res["tau"], res["g1"], res["g2"], res["w1"], res["photons"]))
    print(" alpha   centre  mean   P90   max   | single-scattering share of the mean")
    for k, a in enumerate(VIEWS):
        tot = res["img1"][k] + res["img2"][k]
        m, p90, mx = stats(tot)
        m1 = stats(res["img1"][k])[0]
        print(" %5.0f   %.3f  %.3f  %.3f %.3f | %.2f" % (a, centre(tot), m, p90, mx, m1 / m))


def parse_set(items):
    out = {}
    for it in items or []:
        k, v = it.split("=")
        out[k] = float(v)
    return out


def compare(res, overrides=None, verbose=True):
    p = model_params(overrides)
    rows = []
    for k, a in enumerate(VIEWS):
        tot = res["img1"][k] + res["img2"][k]
        mi, mi1 = model_image(p, res["tau"], a)
        r = stats(tot)
        m = stats(mi)
        rows.append((a, centre(tot), centre(mi), r[0], m[0], r[1], m[1], stats(res["img1"][k])[0], stats(mi1)[0]))
    if verbose:
        print("tau %g: model against the photon walk (W)   mean / P90 over the disc inside 0.9 R; single = the first order's mean" % res["tau"])
        print(" alpha | centre ref model ratio | mean ref model ratio | P90 ref model ratio | single ref model")
        for a, cr, cm, mr, mm, pr, pm, s1r, s1m in rows:
            print(" %5.0f |  %.3f %.3f %5.2f |  %.3f %.3f %5.2f |  %.3f %.3f %5.2f |  %.3f %.3f" % (a, cr, cm, cm / cr, mr, mm, mm / mr, pr, pm, pm / pr, s1r, s1m))
    return rows


def compare_source(res, overrides=None):
    """The source function per tau bin and view: the walk against the model's octave sum plus diffusion."""
    p = model_params(overrides)
    mid = np.array([0.25, 0.75, 1.5, 3.0, 6.0, 12.0, 24.0, 48.0, 96.0])
    print("source function J (W per unit extinction), walk / model, per optical depth towards the sun (bin centres %s)" % ", ".join("%g" % m for m in mid))
    for k, a in enumerate(VIEWS):
        mu = float(camera_axes(a)[0] @ np.array([0.0, 0.0, -1.0]))
        j = res["jtab"][k]
        mj = model_source(p, mu, mid)
        print(" alpha %3.0f  walk  %s" % (a, " ".join("%6.3f" % v for v in j)))
        print("            model %s" % " ".join("%6.3f" % v for v in mj))


def cmd_compare(a):
    over = parse_set(a.set)
    for f in sorted(os.listdir(a.dir)):
        if f.endswith(".npz"):
            d = np.load(os.path.join(a.dir, f))
            res = {k: d[k] for k in d.files}
            for k in ("tau", "photons", "g1", "g2", "w1", "albedo"):
                res[k] = float(res[k])
            res["photons"] = int(res["photons"])
            print_reference(res)
            compare(res, over)
            if a.source:
                compare_source(res, over)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--tau", type=float, default=50.0)
    r.add_argument("--photons", type=int, default=200000)
    r.add_argument("--g1", type=float, default=0.85)
    r.add_argument("--g2", type=float, default=-0.3)
    r.add_argument("--w1", type=float, default=0.92)
    r.add_argument("--seed", type=int, default=11)
    r.add_argument("--out", required=True)
    c = sub.add_parser("compare")
    c.add_argument("dir")
    c.add_argument("--set", nargs="*")
    c.add_argument("--source", action="store_true")
    a = ap.parse_args()
    {"run": cmd_run, "compare": cmd_compare}[a.cmd](a)


if __name__ == "__main__":
    main()
