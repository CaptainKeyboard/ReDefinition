"""A numpy mirror of the cloud light of the parity cell (package T03, FX-T03): the layer march of ReDefinitionAtmospheresLayers.shader and the
light of ReDefinitionAtmospheresCloudLight.cginc evaluated for the harness's analytic cell, a grey sun and a grey ambient. It exists so that the
look constants can be fitted offline over thousands of evaluations; every fit result is checked against the GPU render of the same job
(light.py series T03-light, the 'cpu_vs_gpu' figures). Grey light only; no moons, no bolts, no noise, no cache, no cone.

The structure mirrors the shader function by function: LayerSpans, CellClip, LightTau (growing taps over at most the sun march length),
OctaveSum (with the isotropic share of the octaves above the first), the diffusion term, powder, the analytic ambient occlusion, the floor and
the energy-conserving step of Hillaire 2016, section 5.6.3.
"""
import math

import numpy as np

from weather_metrics import parity_ref as pr

R = pr.KERBIN_RADIUS
INV4PI = 1.0 / (4.0 * math.pi)
SUN_E = math.pi


class Params(object):
    """The light's numbers; the names are the jobs' (light.py MODEL)."""
    DEFAULTS = dict(forwardG=0.85, backG=-0.3, forwardWeight=0.92, albedo=0.999, octaves=4, octaveA=0.3081, octaveB=0.5482, octaveC=0.898,
                    msBrightness=2.9358, diffusion=0.8732, diffusionTau=35.807, powder=0.8075, powderLength=460.05, ambientFloor=0.05,
                    ambientOcclusion=1.2765, skyTint=0.7, ambientGain=0.2447, sunSteps=4, tapRatio=2.0, msIsotropy=0.7552)

    def __init__(self, **kw):
        self.__dict__.update(self.DEFAULTS)
        self.__dict__.update(kw)


def hg(mu, g):
    return (1.0 - g * g) / (4.0 * math.pi * np.maximum(1.0 + g * g - 2.0 * g * mu, 1e-6) ** 1.5)


def phase(p, mu, ecc=1.0):
    return p.forwardWeight * hg(mu, p.forwardG * ecc) + (1.0 - p.forwardWeight) * hg(mu, p.backG * ecc)


def octave_sum(p, mu, tau):
    s = 0.0
    an = bn = cn = 1.0
    for i in range(int(min(max(p.octaves, 1), 4))):
        ph = phase(p, mu, cn)
        if i > 0:
            ph = ph + (INV4PI - ph) * p.msIsotropy
        s = s + (1.0 if i == 0 else p.msBrightness) * bn * ph * np.exp(-tau * an)
        an *= p.octaveA
        bn *= p.octaveB
        cn *= p.octaveC
    return s


def hit_sphere(o, d, r):
    """Sphere about the origin: (hit, t0, t1) for arrays of origins o (N,3) and directions d (N,3) or (3,)."""
    b = np.sum(o * d, axis=-1)
    ln = np.sqrt(np.sum(o * o, axis=-1))
    c = (ln - r) * (ln + r)
    disc = b * b - c
    hit = disc > 0
    s = np.sqrt(np.maximum(disc, 0.0))
    return hit, np.where(hit, -b - s, 0.0), np.where(hit, -b + s, 0.0)


class Cell(object):
    """The analytic cell (shader CellDensity) and the layer it sits in (inner, outer altitude)."""

    def __init__(self, centre_alt=2000.0, radius=800.0, vertical=560.0, edge=0.35, sigma=0.02, inner=1412.0, outer=2588.0, flat_base=True):
        self.c = np.array([0.0, 0.0, R + centre_alt])
        self.rh, self.rv, self.edge, self.sigma, self.inner, self.outer, self.flat = radius, vertical, edge, sigma, inner, outer, flat_base

    def density(self, pc):
        q = pc - self.c
        r = np.sqrt((q[..., 0] ** 2 + q[..., 1] ** 2) / self.rh ** 2 + q[..., 2] ** 2 / self.rv ** 2)
        x = np.clip((1.0 - r) / max(self.edge, 1e-4), 0.0, 1.0)
        v = x * x * (3.0 - 2.0 * x)
        if self.flat:
            v = np.where(q[..., 2] <= -0.5 * self.rv, 0.0, v)
        alt = np.sqrt(np.sum(pc * pc, axis=-1)) - R
        share = (alt - self.inner) / (self.outer - self.inner)
        return np.where((share < 0) | (share > 1), 0.0, self.sigma * v)

    def spans(self, o, d):
        """LayerSpans (tight altitudes = inner, outer) cut to the cell's bounding sphere (CellClip): near stretch (a0, a1) and far wall (b0, b1)."""
        n = o.shape[0]
        a0 = np.zeros(n); a1 = np.zeros(n); b0 = np.zeros(n); b1 = np.zeros(n)
        h, o0, o1 = hit_sphere(o, d, R + self.outer)
        lo, hi = np.maximum(o0, 0.0), o1
        ok = h & (hi > lo)
        hi_in, i0, i1 = hit_sphere(o, d, R + self.inner)
        no_inner = ~hi_in
        a0 = np.where(ok & no_inner, lo, a0); a1 = np.where(ok & no_inner, hi, a1)
        # a hollow hit: the near stretch up to the inner sphere, the far wall after it
        yes = ok & hi_in
        aend = np.minimum(i0, hi)
        near = yes & (aend > lo)
        a0 = np.where(near, lo, a0); a1 = np.where(near, aend, a1)
        bstart = np.maximum(i1, lo)
        far = yes & (hi > bstart)
        has_near = a1 > a0
        b0 = np.where(far & has_near, bstart, b0); b1 = np.where(far & has_near, hi, b1)
        a0 = np.where(far & ~has_near, bstart, a0); a1 = np.where(far & ~has_near, hi, a1)
        # CellClip
        oc = o - self.c
        bb = np.sum(oc * d, axis=-1)
        cc = np.sum(oc * oc, axis=-1) - self.rh * self.rh * 1.1025
        disc = bb * bb - cc
        hit = disc > 0
        s = np.sqrt(np.maximum(disc, 0.0))
        c0, c1 = -bb - s, -bb + s
        a0 = np.maximum(a0, c0); a1 = np.minimum(a1, c1)
        b0 = np.maximum(b0, c0); b1 = np.minimum(b1, c1)
        a_ok = hit & (a1 > a0)
        b_ok = hit & (b1 > b0)
        return (np.where(a_ok, a0, 0.0), np.where(a_ok, a1, 0.0), np.where(b_ok, b0, 0.0), np.where(b_ok, b1, 0.0))


def light_tau(cell, pc, direction, steps, ratio, sun_march=4000.0):
    """LightTau for one layer and one direction (3,): the optical depth from the points pc (N,3) towards the light."""
    n = pc.shape[0]
    d = np.broadcast_to(direction, (n, 3))
    sp = cell.spans(pc, d)
    norm = float(steps) if ratio == 1.0 else (ratio ** steps - 1.0) / (ratio - 1.0)
    tau = np.zeros(n)
    for k in range(2):
        a = sp[2 * k]
        b = sp[2 * k + 1]
        valid = b > a
        b = np.minimum(b, a + sun_march)
        prev = a.copy()
        gain = 1.0
        acc = np.zeros(n)
        for i in range(steps):
            ln = (b - a) * gain / norm
            edge = prev + ln
            mid = 0.5 * (prev + edge)
            q = pc + d * mid[:, None]
            acc += cell.density(q) * (edge - prev)
            prev = edge
            gain *= ratio
        tau += np.where(valid, acc, 0.0)
    return tau


def render(p, cell, cam, sun, sky_top, sky_bottom, steps=128, sun_col=SUN_E, sun_air=None):
    """The march of the cell for a camera (parity_ref.Camera): returns (rgb grey radiance premultiplied, alpha) as (h, w) arrays.
    sun_air: None, or a function (pc (N,3)) -> grey share of the sun reaching the point."""
    dirs = cam.ray_directions()
    n = dirs.shape[0]
    o = np.broadcast_to(cam.pos, (n, 3)).copy()
    a0, a1, b0, b1 = cell.spans(o, dirs)
    sun = np.asarray(sun, dtype=np.float64)
    rad = np.zeros(n)
    T = np.ones(n)
    tau_sum = np.zeros(n)
    mu = dirs @ sun
    for (s0, s1) in ((a0, a1), (b0, b1)):
        ln = s1 - s0
        valid = ln > 0
        count = np.clip(np.ceil(ln / 1.0), 1, steps)
        dt = np.where(valid, ln / count, 0.0)
        for i in range(steps):
            active = valid & (i < count)
            if not active.any():
                break
            t = s0 + dt * (i + 0.5)
            pc = o + dirs * t[:, None]
            dens = cell.density(pc)
            dtau = dens * dt
            ext = 1.0 - np.exp(-dtau)
            alt = np.sqrt(np.sum(pc * pc, axis=-1)) - R
            share = np.clip((alt - cell.inner) / (cell.outer - cell.inner), 0.0, 1.0)
            tau_sun = light_tau(cell, pc, sun, int(p.sunSteps), p.tapRatio)
            sc = sun_col if sun_air is None else sun_col * sun_air(pc)
            diffuse = p.diffusion * INV4PI * (1.0 - np.exp(-tau_sun / max(p.diffusionTau, 1e-3)))
            direct = sc * (octave_sum(p, mu, tau_sun) + diffuse)
            powder = 1.0 + (1.0 - np.exp(-2.0 * dens * p.powderLength) - 1.0) * min(max(p.powder, 0.0), 1.0)   # lerp(1, 1 - exp(-2 sigma L), strength)
            sky = (sky_bottom + (sky_top - sky_bottom) * share) * p.ambientGain   # grey light: the sky tint changes nothing
            tu = dens * np.maximum(cell.outer - alt, 0.0)
            open_ = np.exp(-tu * p.ambientOcclusion)
            floor = max(p.ambientFloor, 0.0301) * (sc if np.isscalar(sc) else sc) * INV4PI
            amb = np.maximum(sky * open_ * p.albedo, floor)
            src = direct * powder * p.albedo + amb
            add = np.where(active, T * src * ext, 0.0)
            rad += add
            T = np.where(active, T * (1.0 - ext), T)
            tau_sum += np.where(active, dtau, 0.0)
    alpha = 1.0 - np.exp(-tau_sum)
    h, w = cam.height, cam.width
    return rad.reshape(h, w), alpha.reshape(h, w)
