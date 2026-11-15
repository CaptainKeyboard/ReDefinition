"""The colour a far cloud fades into, from the body's air: the Python twin of src/Atmospheres/Light/Cloud/CloudHorizonSky.cs (E40 H1 haze).

    python tools/weather_metrics/horizon_sky.py --selftest

The same model, the same samples and the same numbers as the C# (single scattering along the horizontal view ray of the camera through the exponential air, Rayleigh per channel, Mie
with Scatterer's phase function, the sun's transmittance with the planet's shadow, Hillaire's isotropic closure for the higher orders at every third sample); the player's jobs take their far
colours from here so that the pictures show the product's colours. CloudHorizonSkyTests pins the same values on the C# side; --selftest checks them here.
"""
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

SUN_INTENSITY, HDR_SCALE, HDR_KNEE = 100.0, 0.38317, 1.413
DEFAULT_SKY_EXPOSURE, DEFAULT_MIE_G, DEFAULT_GROUND_ALBEDO = 0.2, 0.8, 0.1
VIEW_SAMPLES, ANCHOR_STRIDE, SPHERE_DIRECTIONS, SPHERE_STEPS, SUN_STEPS = 24, 3, 16, 10, 10
VIEW_SPACING, MINIMUM_SCENE_BLUE = 2.2, 0.02
MIE_EXTINCTION_DIVISOR = 0.9

# Scatterer's atmosphere nodes (StockScattererConfigs): per km and km, as the node writes them.
KERBIN = dict(radius=600000.0, beta_r=(0.0153407799, 0.0357074998, 0.0875488967), beta_m_sca=0.0121406196, hr=3.83999991, hm=1.0, mie_g=0.850000024, ground=0.100000001, sky_exposure=0.200000003)
DUNA = dict(radius=320000.0, beta_r=(0.0448499992, 0.0225000009, 0.00779999979), beta_m_sca=(0.00449999981 + 0.00899999961 + 0.00899999961) / 3.0, hr=5.0, hm=3.0, mie_g=0.699999988,
            ground=0.100000001, sky_exposure=0.200000003)


def medium(node):
    """The medium of a node dict (the units of the model: per metre, metres), with the top of the air where Scatterer cuts it off."""
    beta_r = [b / 1000.0 for b in node["beta_r"]]
    mie_sca = node["beta_m_sca"] / 1000.0
    hr, hm = node["hr"] * 1000.0, node["hm"] * 1000.0
    per_km_r = math.sqrt(sum(b * b for b in node["beta_r"]))
    per_km_m = node["beta_m_sca"] * math.sqrt(3.0)
    top = max(-hr * math.log(1e-7 / per_km_r), -hm * math.log(1e-7 / per_km_m), 8.0 * max(hr, hm) * 0.5)
    return dict(rayleigh=beta_r, mie_sca=mie_sca, mie_ext=mie_sca / MIE_EXTINCTION_DIVISOR, hr=hr, hm=hm, g=node.get("mie_g", DEFAULT_MIE_G), ground=node.get("ground", DEFAULT_GROUND_ALBEDO),
                radius=node["radius"], top=node["radius"] + top, sky_exposure=node.get("sky_exposure", DEFAULT_SKY_EXPOSURE))


def rayleigh_phase(mu):
    return 3.0 / (16.0 * math.pi) * (1.0 + mu * mu)


def mie_phase(mu, g):
    return 1.5 / (4.0 * math.pi) * (1.0 - g * g) * (1.0 + g * g - 2.0 * g * mu) ** -1.5 * (1.0 + mu * mu) / (2.0 + g * g)


def _height(m, x, y, z):
    return max(0.0, math.sqrt(x * x + y * y + z * z) - m["radius"])


def _exit(ox, oy, oz, dx, dy, dz, radius):
    b = ox * dx + oy * dy + oz * dz
    c = ox * ox + oy * oy + oz * oz - radius * radius
    disc = b * b - c
    return 0.0 if disc < 0.0 else -b + math.sqrt(disc)


def _sun_transmittance(m, x, y, z, sun_c, sun_s):
    """The sun's transmittance at a point, or None where the planet shadows it."""
    b = x * sun_c + z * sun_s
    c = x * x + y * y + z * z - m["radius"] ** 2
    if b < 0.0 and b * b - c > 0.0:
        return None
    length = _exit(x, y, z, sun_c, 0.0, sun_s, m["top"])
    col_r = col_m = 0.0
    for k in range(SUN_STEPS):
        u = (k + 0.5) / SUN_STEPS
        s, ds = length * u * u, length * 2.0 * u / SUN_STEPS
        h = _height(m, x + s * sun_c, y, z + s * sun_s)
        col_r += math.exp(-h / m["hr"]) * ds
        col_m += math.exp(-h / m["hm"]) * ds
    return [math.exp(-(m["rayleigh"][c] * col_r + m["mie_ext"] * col_m)) for c in range(3)]


def _higher_orders(m, x, y, z, sun_c, sun_s):
    l2 = [0.0] * 3
    transfer = [0.0] * 3
    golden = math.pi * (3.0 - math.sqrt(5.0))
    for n in range(SPHERE_DIRECTIONS):
        dz = 1.0 - 2.0 * (n + 0.5) / SPHERE_DIRECTIONS
        r = math.sqrt(max(0.0, 1.0 - dz * dz))
        dx, dy = r * math.cos(golden * n), r * math.sin(golden * n)
        mu = dx * sun_c + dz * sun_s
        pr, pm = rayleigh_phase(mu), mie_phase(mu, m["g"])
        b = x * dx + y * dy + z * dz
        c = x * x + y * y + z * z - m["radius"] ** 2
        disc = b * b - c
        ground = disc > 0.0 and -b - math.sqrt(disc) > 0.0
        length = -b - math.sqrt(disc) if ground else _exit(x, y, z, dx, dy, dz, m["top"])
        col_r = col_m = 0.0
        radiance = [0.0] * 3
        trans = [0.0] * 3
        for k in range(SPHERE_STEPS):
            u = (k + 0.5) / SPHERE_STEPS
            s, ds = length * u * u, length * 2.0 * u / SPHERE_STEPS
            px, py, pz = x + s * dx, y + s * dy, z + s * dz
            h = _height(m, px, py, pz)
            dr, dm = math.exp(-h / m["hr"]), math.exp(-h / m["hm"])
            col_r += dr * ds * 0.5
            col_m += dm * ds * 0.5
            sun = _sun_transmittance(m, px, py, pz, sun_c, sun_s)
            for ch in range(3):
                tc = math.exp(-(m["rayleigh"][ch] * col_r + m["mie_ext"] * col_m))
                sca = m["rayleigh"][ch] * dr + m["mie_sca"] * dm
                if sun is not None:
                    radiance[ch] += tc * sun[ch] * (m["rayleigh"][ch] * dr * pr + m["mie_sca"] * dm * pm) * ds
                trans[ch] += tc * sca * ds
            col_r += dr * ds * 0.5
            col_m += dm * ds * 0.5
        if ground:
            gx, gy, gz = x + length * dx, y + length * dy, z + length * dz
            gl = math.sqrt(gx * gx + gy * gy + gz * gz)
            sun = _sun_transmittance(m, gx, gy, gz, sun_c, sun_s)
            if sun is not None:
                cos_sun = max(0.0, (gx * sun_c + gz * sun_s) / gl)
                for ch in range(3):
                    radiance[ch] += math.exp(-(m["rayleigh"][ch] * col_r + m["mie_ext"] * col_m)) * sun[ch] * cos_sun * m["ground"] / math.pi
        for ch in range(3):
            l2[ch] += radiance[ch] / SPHERE_DIRECTIONS
            transfer[ch] += trans[ch] / SPHERE_DIRECTIONS
    return [l2[ch] / (1.0 - min(transfer[ch], 0.95)) for ch in range(3)]


def evaluate(m, height_m, elevation_rad, sun_elevation_rad, azimuth_rad):
    """(far, near) radiance per unit of the sun's irradiance, per channel: the colour at the end of the air along the ray and the colour of a short path."""
    rg = m["radius"]
    height = max(1.0, min(height_m, 0.9 * (m["top"] - rg)))
    dip = math.acos(min(1.0, rg / (rg + height)))
    el = max(elevation_rad, -dip + 1e-5)
    ce, se, ca, sa = math.cos(el), math.sin(el), math.cos(azimuth_rad), math.sin(azimuth_rad)
    dx, dy, dz = ce * ca, ce * sa, se
    sun_c, sun_s = math.cos(sun_elevation_rad), math.sin(sun_elevation_rad)
    cz = rg + height
    mu = dx * sun_c + dz * sun_s
    length = _exit(0.0, 0.0, cz, dx, dy, dz, m["top"])
    pr, pm = rayleigh_phase(mu), mie_phase(mu, m["g"])

    def dens(s):
        h = _height(m, s * dx, s * dy, cz + s * dz)
        return math.exp(-h / m["hr"]), math.exp(-h / m["hm"])

    edges = [length * (i / VIEW_SAMPLES) ** VIEW_SPACING for i in range(VIEW_SAMPLES + 1)]
    col_r = col_m = 0.0
    fr0, fm0 = dens(0.0)
    sample_h, segment, transmittance, sun = [], [], [], []
    for i in range(VIEW_SAMPLES):
        s0, s1 = edges[i], edges[i + 1]
        mid, ds = 0.5 * (s0 + s1), s1 - s0
        f1r, f1m = dens(mid)
        f2r, f2m = dens(s1)
        h = ds * 0.5
        mid_r = col_r + h * (5.0 * fr0 + 8.0 * f1r - f2r) / 12.0
        mid_m = col_m + h * (5.0 * fm0 + 8.0 * f1m - f2m) / 12.0
        transmittance.append([math.exp(-(m["rayleigh"][c] * mid_r + m["mie_ext"] * mid_m)) for c in range(3)])
        col_r += ds * (fr0 + 4.0 * f1r + f2r) / 6.0
        col_m += ds * (fm0 + 4.0 * f1m + f2m) / 6.0
        fr0, fm0 = f2r, f2m
        sample_h.append(_height(m, mid * dx, mid * dy, cz + mid * dz))
        segment.append(ds)
        t = _sun_transmittance(m, mid * dx, mid * dy, cz + mid * dz, sun_c, sun_s)
        sun.append(t if t is not None else [0.0, 0.0, 0.0])
    near_r, near_m = math.exp(-height / m["hr"]), math.exp(-height / m["hm"])
    anchors = (VIEW_SAMPLES + ANCHOR_STRIDE - 1) // ANCHOR_STRIDE
    centre = ANCHOR_STRIDE // 2
    store = []
    for a in range(anchors):
        i = min(VIEW_SAMPLES - 1, a * ANCHOR_STRIDE + centre)
        store.append([0.0] * 3 if transmittance[i][2] < MINIMUM_SCENE_BLUE else _higher_orders(m, 0.0, 0.0, rg + sample_h[i], sun_c, sun_s))
    store.append(_higher_orders(m, 0.0, 0.0, cz, sun_c, sun_s))
    far = [0.0] * 3
    for i in range(VIEW_SAMPLES):
        dr, dm = math.exp(-sample_h[i] / m["hr"]), math.exp(-sample_h[i] / m["hm"])
        pos = (i - centre) / float(ANCHOR_STRIDE)
        a0 = int(max(0.0, min(anchors - 1.0, math.floor(pos))))
        a1 = min(anchors - 1, a0 + 1)
        t = max(0.0, min(1.0, pos - a0))
        for c in range(3):
            sca = m["rayleigh"][c] * dr + m["mie_sca"] * dm
            ms = store[a0][c] * (1.0 - t) + store[a1][c] * t
            far[c] += transmittance[i][c] * (sun[i][c] * (m["rayleigh"][c] * dr * pr + m["mie_sca"] * dm * pm) + sca * ms) * segment[i]
    cam_sun = _sun_transmittance(m, 0.0, 0.0, cz, sun_c, sun_s) or [0.0, 0.0, 0.0]
    near = []
    for c in range(3):
        ext = m["rayleigh"][c] * near_r + m["mie_ext"] * near_m
        sca = m["rayleigh"][c] * near_r + m["mie_sca"] * near_m
        source = cam_sun[c] * (m["rayleigh"][c] * near_r * pr + m["mie_sca"] * near_m * pm) + sca * store[anchors][c]
        near.append(source / ext if ext > 0.0 else 0.0)
    return far, near


def display(radiance, sky_exposure):
    """Scatterer's display value of the sky for a radiance per irradiance (CommonAtmosphere.cginc hdr)."""
    out = []
    for r in radiance:
        l = max(r, 0.0) * SUN_INTENSITY * sky_exposure
        out.append((l * HDR_SCALE) ** (1.0 / 2.2) if l < HDR_KNEE else 1.0 - math.exp(-l))
    return out


def decode(p, white, exposure=1.0, gamma=2.2):
    """CloudOutputStage.Decode: the radiance whose display value is p."""
    w = max(white, 1e-4)
    return w / max(exposure, 1e-4) * (max(p, 0.0) / w) ** gamma


def layer_elevation(height_m, layer_altitude_m, radius):
    if height_m <= layer_altitude_m:
        return 0.0
    return -math.acos(max(0.0, min(1.0, (radius + layer_altitude_m) / (radius + height_m))))


def far_near(node, height_m, layer_altitude_m, sun_elevation_deg, azimuth_deg, white, ambient=(0.0, 0.0, 0.0)):
    """The two colours the pass hands the layer shader (before the white point's tint), in the pass's radiance: CloudHazePlan.SkyInscatter for the sun alone."""
    m = medium(node)
    el = layer_elevation(height_m, layer_altitude_m, m["radius"])
    far, near = evaluate(m, height_m, el, math.radians(sun_elevation_deg), math.radians(azimuth_deg))
    far_d, near_d = display(far, m["sky_exposure"]), display(near, m["sky_exposure"])
    out_far = [max(ambient[c], decode(far_d[c], white)) for c in range(3)]
    out_near = [max(ambient[c], decode(near_d[c], white)) for c in range(3)]
    return out_far, out_near, far_d


def selftest():
    far, near = evaluate(medium(KERBIN), 92.0, 0.0, math.radians(44.0), math.radians(180.0))
    shown = display(far, 0.2)
    # the rows CloudHorizonSkyTests.ThePythonTwinGivesTheSameNumbers pins on the C# side
    expected = (0.6549, 0.7786, 0.8060)
    for c in range(3):
        assert abs(shown[c] - expected[c]) < 0.002, (c, shown, expected)
    low = display(evaluate(medium(KERBIN), 92.0, 0.0, math.radians(10.0), math.radians(180.0))[0], 0.2)
    for c, e in enumerate((0.6302, 0.6637, 0.5218)):
        assert abs(low[c] - e) < 0.002, ("sun 10", c, low, e)
    duna = display(evaluate(medium(DUNA), 92.0, 0.0, math.radians(44.0), math.radians(180.0))[0], 0.2)
    for c, e in enumerate((0.7986, 0.7170, 0.5315)):
        assert abs(duna[c] - e) < 0.002, ("duna", c, duna, e)
    near_d = display(near, 0.2)
    for c, e in enumerate((0.7176, 0.7879, 0.7973)):
        assert abs(near_d[c] - e) < 0.002, ("near", c, near_d, e)
    print("horizon_sky selftest OK: Kerbin sun 44 behind", ["%.3f" % x for x in shown], "sun 10", ["%.3f" % x for x in low], "Duna", ["%.3f" % x for x in duna])


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        selftest()
    else:
        print(__doc__)
