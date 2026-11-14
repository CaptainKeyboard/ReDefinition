"""Does the cloud light of the layer shader follow the two-stream anchors of a real cloud slab? (E40 look round 2, package D2)

The CPU mirror of the shader's source function (light_cpu.py: LightTau, OctaveSum, the diffusion term, the powder term, the ambient with its analytic occlusion
and the floor) is marched through a plane-parallel slab and compared with what a cloud of that optical depth does in nature, for a droplet's asymmetry g = 0.85
and no absorption (the two-stream results of Meador and Weaver 1980; the slab-flux photon walk of parity_ref.py agrees to 2 %):

    transmission T = 1 / (1 + 0.75 (1 - g) tau)    reflection R = 1 - T
    the radiance of the lit top   = R x (mu0 W + L_sky)   (a Lambertian top; W = the radiance of a white card facing the sun, L_sky the sky's radiance = what a card facing up shows of it)
    the radiance of the base      = T x (mu0 W + L_sky)   (a Lambertian base)

Since E40 L6 light the sky's light enters the anchors: it diffuses in the cloud like the sun's (before, the anchors had the sun alone and the model had the sky's light stop at the
first optical depth: 0.04 of the 0.30 the two-stream gives at the top of a thick cloud). Both are radiances relative to W, the display's white, at the sun elevation of the type sheets (45.6 degrees). The shader's value is the mean over twelve view
azimuths at a view elevation of 53 degrees. The table also lists the radiance with the per-type light factors of the cfg (the base factor on the direct light at the
base, the head factor at the top).

    python tools/weather_metrics/light_slab_check.py table      uniform slabs, tau 2 to 200
    python tools/weather_metrics/light_slab_check.py types      the shipped Kerbin types with their optical depth
"""
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import light_cpu as C  # noqa: E402

SUN_ELEVATION = 45.6
WHITE = 0.7486                      # the stage's white of the type sheets (the scene sun's display value)
AMBIENT_TOP, AMBIENT_BOTTOM = 0.2432, 0.1216
ASYMMETRY = 0.85
INV4PI = C.INV4PI


def transmission(tau, g=ASYMMETRY):
    return 1.0 / (1.0 + 0.75 * (1.0 - g) * tau)


def anchors(tau, mu0, sky=True):
    """(top, base) radiance relative to W of a Lambertian slab top and base. The slab is lit by the sun (a horizontal white card shows mu0 W) and, with sky, by the sheets' sky
    (a horizontal card shows AMBIENT_TOP, the radiance of the sky, as the card's irradiance is pi times it): both diffuse in the cloud alike (E40 L6 light; before it the anchors had the
    sun alone, which the model's top met at 0.82 to 0.93 and its base at 1, while the sky's light did not enter the cloud at all: 0.04 of 0.30 at the top)."""
    t = transmission(tau)
    lit = mu0 + (AMBIENT_TOP / WHITE if sky else 0.0)
    return (1.0 - t) * lit, t * lit


class Slab(object):
    """A plane-parallel slab of height h with the extinction sigma(z) (a function of the height above its base)."""

    def __init__(self, height, sigma, base_light=1.0, head_light=1.0, base_ambient=1.0, head_ambient=1.0):
        self.h, self.sigma, self.bl, self.hl, self.ba, self.ha = float(height), sigma, base_light, head_light, base_ambient, head_ambient

    def factor(self, z):
        x = min(max(z / self.h, 0.0), 1.0)
        return self.bl + (self.hl - self.bl) * x * x * (3.0 - 2.0 * x)

    def factor_ambient(self, z):
        x = min(max(z / self.h, 0.0), 1.0)
        return self.ba + (self.ha - self.ba) * x * x * (3.0 - 2.0 * x)

    def column(self, a, b, n=48):
        if b <= a:
            return 0.0
        zs = a + (np.arange(n) + 0.5) / n * (b - a)
        return float(np.sum([self.sigma(z) for z in zs]) * (b - a) / n)


def sun_tau(slab, z, mu0, steps=4, ratio=2.0, max_len=4000.0):
    """LightTau for the slab: growing taps over the path to the top, at most max_len m."""
    b = min((slab.h - z) / mu0, max_len)
    if b <= 0.0:
        return 0.0
    norm = (ratio ** steps - 1.0) / (ratio - 1.0)
    prev, gain, acc = 0.0, 1.0, 0.0
    for _ in range(steps):
        ln = b * gain / norm
        edge = prev + ln
        mid = 0.5 * (prev + edge)
        acc += slab.sigma(z + mid * mu0) * (edge - prev)
        prev, gain = edge, gain * ratio
    return acc


def source(p, slab, z, mu0, mu, sun_c, light_ms=1.0, light_ambient=1.0):
    """The shader's LightSource for a grey sun: (direct, ambient) per unit extinction."""
    d = slab.sigma(z)
    tau = sun_tau(slab, z, mu0)
    diffuse = p.diffusion * INV4PI * (1.0 - math.exp(-tau / p.diffusionTau)) / (1.0 + p.diffusionDecay * tau)
    direct = sun_c * (octave_sum_ms(p, mu, tau, light_ms) + diffuse)
    powder = 1.0 + (1.0 - math.exp(-2.0 * d * p.powderLength) - 1.0) * min(1.0, p.powder)
    sky = (AMBIENT_BOTTOM + (AMBIENT_TOP - AMBIENT_BOTTOM) * min(max(z / slab.h, 0.0), 1.0)) * p.ambientGain * light_ambient
    open_up = math.exp(-d * max(slab.h - z, 0.0) * p.ambientOcclusion)
    floor = max(p.ambientFloor, 0.0301) * sun_c * INV4PI
    # The sky's light that has diffused into the cloud (E40 L6 light): the sky above's source times ambientMs / (1 + ambientDecay tau), tau the optical depth to the slab's top.
    sky_top = AMBIENT_TOP * p.ambientGain * light_ambient
    ms = p.ambientMs / (1.0 + p.ambientDecay * d * max(slab.h - z, 0.0))
    return direct * powder * p.albedo, max((sky * open_up + sky_top * ms) * p.albedo, floor)


def octave_sum_ms(p, mu, tau, ms_scale):
    s, an, bn, cn = 0.0, 1.0, 1.0, 1.0
    for i in range(int(min(max(p.octaves, 1), 4))):
        ph = C.phase(p, mu, cn)
        if i > 0:
            ph = ph + (INV4PI - ph) * p.msIsotropy
        s += (1.0 if i == 0 else p.msBrightness * ms_scale) * bn * ph * math.exp(-tau * an)
        an *= p.octaveA
        bn *= p.octaveB
        cn *= p.octaveC
    return s


def view(p, slab, up, view_elevation=53.0, azimuths=12, n=160, light_ms=1.0, light_ambient=1.0, factors=False):
    """The radiance (relative to W, direct + ambient) of a ray through the slab: up = the camera below looking up (the base), else above looking down (the top)."""
    mu0 = math.sin(math.radians(SUN_ELEVATION))
    sun_c = WHITE * math.pi
    sun = np.array([math.cos(math.radians(SUN_ELEVATION)), 0.0, mu0])
    mv = math.sin(math.radians(view_elevation))
    sv = math.sqrt(1.0 - mv * mv)
    total = 0.0
    for k in range(azimuths):
        az = 2.0 * math.pi * (k + 0.5) / azimuths
        d = np.array([sv * math.cos(az), sv * math.sin(az), mv if up else -mv])
        mu = float(d @ sun)
        ds = slab.h / n / mv
        trans, acc = 1.0, 0.0
        for i in range(n):
            t = (i + 0.5) / n
            z = t * slab.h if up else (1.0 - t) * slab.h
            dens = slab.sigma(z)
            if dens <= 0.0:
                continue
            ext = 1.0 - math.exp(-dens * ds)
            direct, ambient = source(p, slab, z, mu0, mu, sun_c, light_ms, light_ambient)
            f = slab.factor(z) if factors else 1.0
            fa = slab.factor_ambient(z) if factors else 1.0
            acc += trans * (direct * f + ambient * fa) * ext
            trans *= 1.0 - ext
        total += acc / azimuths
    return total / WHITE


def product_params(layer="Kerbin-cumulus"):
    """The light's numbers as the product has them: the twin's fitted defaults and the layer's cfg keys ambientMs and ambientDecay (CloudLayerDef's defaults where the layer has none)."""
    from weather_metrics import e40o
    with open(e40o.CFG, encoding="utf-8") as f:
        root = e40o.parse_nodes(f.read())
    clouds = e40o.find(root, "CLOUDS")
    node = [c for c in clouds["children"] if c["name"] == "LAYER" and e40o.val(c, "name") == layer][0]
    return C.Params(ambientMs=float(e40o.val(node, "ambientMs", 3.5)), ambientDecay=float(e40o.val(node, "ambientDecay", 0.11)))


def uniform_table(taus=(2, 5, 10, 25, 60, 100, 200), height=1200.0):
    p = product_params()
    mu0 = math.sin(math.radians(SUN_ELEVATION))
    rows = []
    for tau in taus:
        slab = Slab(height, lambda z, s=tau / height: s)
        top, base = view(p, slab, False), view(p, slab, True)
        a_top, a_base = anchors(tau, mu0)
        rows.append(dict(tau=tau, top=top, base=base, anchor_top=a_top, anchor_base=a_base, flux_base=base / (mu0 + AMBIENT_TOP / WHITE)))
    return rows


def shipped_types():
    """[(layer, type, tau, thickness m, profile function of the height share, base light, head light)] of Kerbin's cfg: the full-column optical depth as CloudOpacityTests has it."""
    from weather_metrics import e40o
    with open(e40o.CFG, encoding="utf-8") as f:
        root = e40o.parse_nodes(f.read())
    clouds = e40o.find(root, "CLOUDS")
    out = []
    for layer in [c for c in clouds["children"] if c["name"] == "LAYER"]:
        sigma = float(e40o.val(layer, "sigmaPerM"))
        thick_layer = float(e40o.val(layer, "outerAltitudeM")) - float(e40o.val(layer, "innerAltitudeM"))
        for t in [c for c in layer["children"] if c["name"] == "TYPE"]:
            keys = []
            for c in t["children"]:
                if c["name"] == "PROFILE":
                    for k, v in c["values"]:
                        x, y = v.split()
                        keys.append((float(x), float(y)))
            xs, ys = [k[0] for k in keys], [k[1] for k in keys]
            base_gain = float(e40o.val(t, "baseGain", 0.3))
            rise = float(e40o.val(t, "riseShare", 0.5))
            scale = float(e40o.val(t, "densityScale", 1.0))
            share = float(e40o.val(t, "topShare")) - float(e40o.val(t, "baseShare"))

            def profile(x, xs=xs, ys=ys, bg=base_gain, rs=rise, sc=scale):
                return float(np.interp(x, xs, ys)) * (bg + (1.0 - bg) * min(max(x / rs, 0.0), 1.0) if rs > 0 else 1.0) * sc

            h = share * thick_layer
            tau = sigma * h * float(np.mean([profile((i + 0.5) / 128.0) for i in range(128)]))
            out.append((e40o.val(layer, "name"), e40o.val(t, "name"), tau, h, sigma, profile, float(e40o.val(t, "baseLight", 1.0)), float(e40o.val(t, "headLight", 1.0)),
                        float(e40o.val(t, "baseAmbient", 1.0)) * float(e40o.val(layer, "lightAmbient", 1.0)), float(e40o.val(t, "headAmbient", 1.0)) * float(e40o.val(layer, "lightAmbient", 1.0))))
    return out


def types_table():
    params = {}
    mu0 = math.sin(math.radians(SUN_ELEVATION))
    rows = []
    for layer, name, tau, h, sigma, profile, bl, hl, ba, ha in shipped_types():
        if layer == "Kerbin-cirrus":
            continue                         # Cheap kind: no light march to mirror
        slab = Slab(h, lambda z, h=h, s=sigma, f=profile: s * f(min(max(z / h, 0.0), 1.0)), bl, hl, ba, ha)
        if layer not in params:
            params[layer] = product_params(layer)
        p = params[layer]
        a_top, a_base = anchors(tau, mu0)
        rows.append(dict(layer=layer, name=name, tau=tau, top=view(p, slab, False), base=view(p, slab, True), top_f=view(p, slab, False, factors=True),
                         base_f=view(p, slab, True, factors=True), anchor_top=a_top, anchor_base=a_base))
    return rows


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "table"
    if cmd == "table":
        print("sun %.1f deg, W = %.4f; radiance relative to W; the mirror (direct + ambient) against the Lambertian two-stream anchor (g 0.85, no absorption)" % (SUN_ELEVATION, WHITE))
        print("  tau    top   anchor  ratio |  base  anchor  ratio | flux T(base) two-stream T")
        for r in uniform_table():
            print("%5d  %.3f  %.3f  %5.2f  |  %.3f  %.3f  %5.2f | %.3f  %.3f" % (r["tau"], r["top"], r["anchor_top"], r["top"] / r["anchor_top"], r["base"], r["anchor_base"],
                                                                                r["base"] / r["anchor_base"], r["flux_base"], transmission(r["tau"])))
    else:
        print("the shipped Kerbin types as uniform-in-plane slabs of their own profile, radiance relative to W")
        print("%-22s %6s | %6s %6s  (with cfg factors %6s) | %6s %6s  (with %6s)" % ("type", "tau", "top", "anchor", "top", "base", "anchor", "base"))
        for r in types_table():
            print("%-22s %6.1f | %6.3f %6.3f  (with cfg factors %6.3f) | %6.3f %6.3f  (with %6.3f)" % (r["name"], r["tau"], r["top"], r["anchor_top"], r["top_f"], r["base"],
                                                                                                    r["anchor_base"], r["base_f"]))


if __name__ == "__main__":
    main()
