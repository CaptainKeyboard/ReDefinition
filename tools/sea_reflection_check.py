"""The sun's and the moons' reflections on the sea, as the light probe captured them,
against a physical model.

The sea's slopes are as Cox and Munk measured them, Gaussian (Beckmann's distribution).
Their mean square slope m^2 is the raytracing's (Scatterer's variance per pixel,
m = 2 alpha, or STOCK_M2 for KSP's own sea). The specular microfacet BRDF is
D G / (4 N.L N.V) with Smith's shadowing (Walter et al. 2007). A disc of solid angle
Omega and radiance L is a source of irradiance L Omega, which holds where the slopes
spread it far wider than the disc. No Fresnel term: the traced picture holds the light
before it (Raytracing.hlsl, SpecularLight).

For each shot (<name>.json, <name>-traced.pfm, <name>-alpha.pfm from
LightProbe.LogOceanTextures) it compares the traced light averaged over bins against
the model's. The bins are in the body's frame: degrees below the horizontal and azimuth
from the body's. The model's light is the average of the glints a pixel resolves or the
glow it does not.

Ratio: traced / model over the path's core (model above half its peak). Peak: depression
of the brightest bin, traced and model. Extent: the path's depressions above half the
peak, along the body's azimuth. Half: the path's half width at half the peak, degrees
across, at the peak's row. Sparkle: standard deviation / mean of the traced pixels in the
core, glints near by, an even glow far off.

With --log, the sun's colour the raytracing traced with is taken from the probe's line
for the shot ("traced RGBA(...)"), for shots whose data predate it in the JSON.

Usage:

  python tools/sea_reflection_check.py <ReDefinitionCaptures/...-probe folder> [--log KSP.log] [name ...]
  python tools/sea_reflection_check.py --noise
  python tools/sea_reflection_check.py --waves <ReDefinitionCaptures/...-probe folder>
"""
import json
import math
import os
import struct
import sys

import numpy as np

# KSP's own sea's mean square slope (RtCommon.hlsli): 24 sine waves of slope
# kStockWaveSlope, half its square each, and kRippleOctaves octaves of kRippleSlope.
STOCK_M2 = 24 * 0.5 * 0.0254 ** 2 + 6 * 0.055 ** 2
WATER_LEAST_ROUGHNESS = 0.004

# The probe's cases (LightProbe.Heights): the body's height over the horizon, the
# camera's height over the sea, how far it looks down.
CASES = {
    'case01': (5, 2, 1), 'case02': (15, 10, 5), 'case03': (25, 50, 10), 'case04': (60, 50, 50),
    'case05': (10, 300, 5), 'case06': (25, 1000, 30), 'case07': (45, 1000, 45), 'case08': (65, 2000, 55),
    'case09': (35, 5000, 30), 'case10': (5, 15000, 8),
}

BIN = 0.5  # degrees


def read_pfm(path):
    """A PFM image (colour or grey) as a float array of height x width x channels, row 0 at the bottom."""
    with open(path, 'rb') as f:
        kind = f.readline().strip()
        width, height = map(int, f.readline().split())
        scale = float(f.readline())
        channels = 3 if kind == b'PF' else 1
        data = np.frombuffer(f.read(), '<f4' if scale < 0 else '>f4')
    return data.reshape(height, width, channels)  # row 0 at the bottom


def input_colour(c, gamma):
    """A colour as the raytracing takes it: converted from gamma to linear by Unity's polynomial where the shot says
    its inputs are gamma, else as it is; negative channels clamped."""
    c = np.maximum(np.asarray(c, float), 0.0)
    return c * (c * (c * 0.305306011 + 0.682171111) + 0.012522878) if gamma else c


def g1(cos, m):
    """Smith's shadowing for Beckmann's distribution of roughness m, in Walter et al.'s rational approximation."""
    cos = np.clip(cos, 1e-4, 1.0)
    a = cos / (m * np.sqrt(1.0 - cos * cos) + 1e-12)
    return np.where(a < 1.6, (3.535 * a + 2.181 * a * a) / (1.0 + 2.276 * a + 2.577 * a * a), 1.0)


def specular(n, v, l, m):
    """D G / (4 N.V) for unit vectors (..., 3) and roughness m: the light reflected per
    unit of irradiance from l."""
    h = v + l
    h /= np.linalg.norm(h, axis=-1, keepdims=True)
    nh = np.clip(np.sum(n * h, -1), 1e-4, 1.0)
    nl = np.sum(n * l, -1)
    nv = np.sum(n * v, -1)
    m2 = m * m
    d = np.exp(-(1.0 - nh * nh) / (nh * nh * m2)) / (math.pi * m2 * nh ** 4)
    out = d * g1(nv, m) * g1(nl, m) / (4.0 * np.maximum(nv, 1e-4))
    return np.where((nl > 0) & (nv > 0), out, 0.0)


def luminance(c):
    """Rec. 709 luminance of linear RGB."""
    return c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722


def traced_sun_colours(log):
    """Per shot name, the colour the raytracing traced with, from the probe's 'Probe: light' lines in the log."""
    colours = {}
    for line in open(log, encoding='utf-8', errors='replace'):
        if 'Probe: light ' in line and ', traced RGBA(' in line:
            name = line.split('Probe: light ', 1)[1].split(':', 1)[0]
            values = line.split(', traced RGBA(', 1)[1].split(')', 1)[0].split(',')
            colours[name] = [float(x) for x in values[:3]]
    return colours


def screenshot(folder, name):
    """The shot's screenshot as linear light, rows from the bottom up as the PFMs, or None."""
    path = os.path.join(folder, name + '.png')
    if not os.path.exists(path):
        return None
    from PIL import Image
    img = np.asarray(Image.open(path).convert('RGB')).astype(np.float32)[::-1] / 255.0
    return np.where(img <= 0.04045, img / 12.92, ((img + 0.055) / 1.055) ** 2.4)


def analyse(folder, name, sun_colours=None, picture=None):
    """The shot against the model; `picture` in place of the traced light, as the
    screenshot to measure the path as the player sees it."""
    data = json.load(open(os.path.join(folder, name + '.json')))
    if sun_colours and name in sun_colours:
        data['sunColour'] = sun_colours[name]
    # The average over the frames the probe took (LightProbe.AverageTraced), where there.
    mean_path = os.path.join(folder, name + '-traced-mean.pfm')
    traced = picture if picture is not None else read_pfm(
        mean_path if os.path.exists(mean_path) else os.path.join(folder, name + '-traced.pfm'))
    height, width = traced.shape[:2]
    alpha_path = os.path.join(folder, name + '-alpha.pfm')
    alpha = read_pfm(alpha_path)[..., 0] if os.path.exists(alpha_path) and not data['stockWater'] else None

    # Each pixel's ray, world space: its point on the far plane through the inverse
    # projection (view space, -z ahead), turned by the camera's matrix.
    projection = np.array(data['projection']).reshape(4, 4)
    to_world = np.array(data['cameraToWorld']).reshape(4, 4)
    inverse = np.linalg.inv(projection)
    xs = (np.arange(width) + 0.5) / width * 2.0 - 1.0
    ys = (np.arange(height) + 0.5) / height * 2.0 - 1.0
    gx, gy = np.meshgrid(xs, ys)
    clip = np.stack([gx, gy, np.ones_like(gx), np.ones_like(gx)], -1)
    view = clip @ inverse.T
    view = view[..., :3] / view[..., 3:4]
    rays = view @ to_world[:3, :3].T
    rays /= np.linalg.norm(rays, axis=-1, keepdims=True)

    eye = np.array(data['eye'])
    centre = np.array(data['bodyCentre'])
    radius = data['bodyRadius']
    o = eye - centre
    up_eye = o / np.linalg.norm(o)
    b = rays @ o
    c = o @ o - radius * radius
    disc = b * b - c
    hit = disc > 0
    t = -b - np.sqrt(np.maximum(disc, 0.0))
    hit &= t > 0
    point = o + rays * t[..., None]
    normal = point / np.linalg.norm(point, axis=-1, keepdims=True)
    v = -rays

    if alpha is not None:
        # Scatterer's variance per pixel is that of the waves the pixel does not show; far
        # off, where it shows none, all of the sea's: that is the sea's slope.
        hit &= alpha > 0
        if not np.any(hit):
            return name, None
        total = float(np.percentile(2.0 * alpha[hit], 99))
        m = np.full((height, width), math.sqrt(total ** 2 + WATER_LEAST_ROUGHNESS ** 2))
    else:
        m = np.full((height, width), math.sqrt(STOCK_M2 + WATER_LEAST_ROUGHNESS ** 2))

    gamma = data['gammaInputs']
    sun = np.array(data['sun'])
    towards_sun = sun[:3] / np.linalg.norm(sun[:3])
    sources = []
    sun_irradiance = input_colour(data['sunColour'], gamma) * data['sunMirror']
    if np.any(sun_irradiance > 0):
        sources.append(('sun', towards_sun, max(sun[3], 0.0047), sun_irradiance))
    lights = data['lights']
    surfaces = data['surfaces']
    for i in range(min(data['lightCount'], 2)):
        l = np.array(lights[i * 8:i * 8 + 3])
        l /= np.linalg.norm(l)
        r = lights[i * 8 + 3]
        surface = np.array(surfaces[i * 4:i * 4 + 3])
        if not np.any(surface > 0):
            continue
        cos_phase = float(np.clip(np.dot(towards_sun, -l), -1, 1))
        phase_angle = math.acos(cos_phase)
        phase = (math.sin(phase_angle) + (math.pi - phase_angle) * cos_phase) / math.pi
        disc_radiance = surface * (2.0 / 3.0) * phase
        sources.append(('moon %d' % i, l, r, disc_radiance * math.pi * math.sin(r) ** 2))
    if not sources:
        return name, None
    # The brightest source, by its irradiance, is the one checked; the model sums all.
    model = np.zeros((height, width, 3))
    for _, l, _, irradiance in sources:
        model += specular(normal, v, np.broadcast_to(l, normal.shape), m)[..., None] * irradiance
    model[~hit] = 0.0
    main = max(sources, key=lambda s: float(luminance(np.asarray(s[3]))))
    l = main[1]

    measured = luminance(traced.astype(float))
    expected = luminance(model)
    measured[~hit] = np.nan
    expected[~hit] = np.nan

    # The body's frame: degrees below the horizontal, azimuth from the body's.
    north = l - np.dot(l, up_eye) * up_eye
    north /= np.linalg.norm(north)
    east = np.cross(up_eye, north)
    depression = np.degrees(np.arcsin(np.clip(-(rays @ up_eye), -1, 1)))
    azimuth = np.degrees(np.arctan2(rays @ east, rays @ north))
    elevation = math.degrees(math.asin(float(np.dot(l, up_eye))))
    altitude = float(np.linalg.norm(o) - radius)

    sel = hit & np.isfinite(measured)
    if data['stockWater']:
        # KSP's own sea's traced picture holds what the reflected rays meet, the sky, as
        # well: taken off as each row's median where the model puts no disc's light.
        rows = np.floor(depression / BIN).astype(int)
        dark = sel & (expected < 0.02 * np.nanmax(np.where(sel, expected, 0)))
        base = np.zeros_like(measured)
        for r in np.unique(rows[sel]):
            row = rows == r
            pick = measured[row & dark]
            base[row] = np.median(pick) if pick.size > 20 else 0.0
        measured = measured - base
    di = np.floor(depression[sel] / BIN).astype(int)
    ai = np.floor(azimuth[sel] / BIN).astype(int)
    d0, a0 = di.min(), ai.min()
    shape = (di.max() - d0 + 1, ai.max() - a0 + 1)
    count = np.zeros(shape)
    sum_m = np.zeros(shape)
    sum_e = np.zeros(shape)
    np.add.at(count, (di - d0, ai - a0), 1)
    np.add.at(sum_m, (di - d0, ai - a0), measured[sel])
    np.add.at(sum_e, (di - d0, ai - a0), expected[sel])
    with np.errstate(invalid='ignore', divide='ignore'):
        bin_m = np.where(count >= 4, sum_m / count, np.nan)
        bin_e = np.where(count >= 4, sum_e / count, np.nan)
    dep_axis = (np.arange(shape[0]) + d0 + 0.5) * BIN
    az_axis = (np.arange(shape[1]) + a0 + 0.5) * BIN

    def describe(grid):
        # Smoothed over 5 x 5 bins, so that glints average out.
        g = np.nan_to_num(grid)
        w = np.isfinite(grid).astype(float)
        k = np.ones(5)
        for axis in (0, 1):
            g = np.apply_along_axis(lambda r: np.convolve(r, k, 'same'), axis, g)
            w = np.apply_along_axis(lambda r: np.convolve(r, k, 'same'), axis, w)
        with np.errstate(invalid='ignore', divide='ignore'):
            s = np.where(w > 0, g / w, np.nan)
        if not np.any(np.isfinite(s)):
            return None
        peak = np.unravel_index(np.nanargmax(s), s.shape)
        top = s[peak]
        column = s[:, peak[1]]
        rows = np.where(column >= top / 2)[0]
        row = s[peak[0], :]
        across = np.where(row >= top / 2)[0]
        return dict(peak=dep_axis[peak[0]], peak_az=az_axis[peak[1]], top=top,
                    extent=(dep_axis[rows.min()], dep_axis[rows.max()]),
                    half=(az_axis[across.max()] - az_axis[across.min()] + BIN) / 2, smooth=s)

    dm, de = describe(bin_m), describe(bin_e)
    if dm is None or de is None:
        return name, None
    core = de['smooth'] >= de['top'] / 2
    with np.errstate(invalid='ignore'):
        ratio = np.nansum(dm['smooth'][core] * count[core]) / np.nansum(de['smooth'][core] * count[core])
    # The traced pixels' spread in the core, against their mean.
    core_px = sel.copy()
    core_px[sel] = core[di - d0, ai - a0]
    values = measured[core_px]
    sparkle = float(np.std(values) / max(np.mean(values), 1e-12)) if values.size else float('nan')
    # The water a pixel covers along the view at the model's peak.
    pk = np.unravel_index(np.nanargmax(np.where(sel, expected, -1)), expected.shape)
    pixel = 2.0 * math.tan(math.atan(1.0 / projection[1, 1])) / height
    footprint = float(t[pk] * pixel / max(float(np.sum(normal[pk] * v[pk])), 1e-3))
    # The light's centre and spread, by its moments: robust where few glints carry it, as
    # near by. The same pixels for both, so that the frame's edges cut both alike.
    # Over the path, where the model gives above 2 % of its peak.
    path = sel & (np.nan_to_num(expected) >= 0.02 * np.nanmax(np.where(sel, expected, 0)))

    def moments(values):
        w = np.where(path, np.maximum(values, 0.0), 0.0)
        total = w.sum()
        cd = (w * depression).sum() / total
        ca = (w * azimuth).sum() / total
        sd = math.sqrt((w * (depression - cd) ** 2).sum() / total)
        sa = math.sqrt((w * (azimuth - ca) ** 2).sum() / total)
        return cd, sd, sa, total
    mm, me = moments(np.nan_to_num(measured)), moments(np.nan_to_num(expected))
    return name, dict(moments=(mm, me), elevation=elevation, altitude=altitude, ratio=ratio, measured=dm, model=de, sparkle=sparkle,
                      footprint=footprint, m=float(np.nanmedian(m[sel])), source=main[0])


def ripple_gradient_rms(samples=400000, seed=1):
    """The root mean square of the gradient of RtCommon.hlsli's RippleNoise per unit of
    its coordinate, total and along each axis, sampled: what SeaRipples divides by."""
    rng = np.random.default_rng(seed)
    n = 64
    angle = rng.uniform(0, 2 * math.pi, (n + 1, n + 1))
    g = np.stack([np.cos(angle), np.sin(angle)], -1)
    p = rng.uniform(0, n - 1, (samples, 2))
    c = np.floor(p).astype(int)
    f = p - c
    w = f ** 3 * (f * (f * 6 - 15) + 10)
    dw = 30 * f * f * (f * (f - 2) + 1)
    ga, gb = g[c[:, 0], c[:, 1]], g[c[:, 0] + 1, c[:, 1]]
    gc, gd = g[c[:, 0], c[:, 1] + 1], g[c[:, 0] + 1, c[:, 1] + 1]
    va, vb = (ga * f).sum(1), (gb * (f - [1, 0])).sum(1)
    vc, vd = (gc * (f - [0, 1])).sum(1), (gd * (f - [1, 1])).sum(1)
    wx, wy = w[:, 0:1], w[:, 1:2]
    d = (ga + wx * (gb - ga) + wy * (gc - ga) + wx * wy * (ga - gb - gc + gd)
         + dw * (np.stack([vb - va, vc - va], 1) + (va - vb - vc + vd)[:, None] * w[:, ::-1]))
    return math.sqrt((d ** 2).sum(1).mean()), d.std(0)


def world_points(folder, name):
    """A shot's pixels as points relative to the main body's centre, world space, from its
    depth (Scatterer's ocean's where the probe wrote it; Direct3D's reversed z), and its
    camera; with the shot's data."""
    data = json.load(open(os.path.join(folder, name + '.json')))
    ocean = os.path.join(folder, name + '-ocean-depth.pfm')
    depth = read_pfm(ocean if os.path.exists(ocean) else os.path.join(folder, name + '-depth.pfm'))[..., 0].astype(float)
    height, width = depth.shape
    projection = np.array(data['projection']).reshape(4, 4)
    to_world = np.array(data['cameraToWorld']).reshape(4, 4)
    a, b = projection[2, 2], projection[2, 3]
    near, far = b / (a - 1.0), b / (a + 1.0)
    xs = (np.arange(width) + 0.5) / width * 2.0 - 1.0
    ys = (np.arange(height) + 0.5) / height * 2.0 - 1.0
    gx, gy = np.meshgrid(xs, ys)
    with np.errstate(divide='ignore'):
        distance = near * far / (depth * (far - near) + near)
    view = np.stack([gx / projection[0, 0] * distance, gy / projection[1, 1] * distance, -distance], -1)
    world = view @ to_world[:3, :3].T + to_world[:3, 3]
    return world - np.array(data['bodyCentre']), depth > 0, data


def waves_in_place(folder, prefix):
    """The light probe's check that the waves stay on the water (LightProbe.WavesInPlace).
    Each water pixel of the first shot is found in the second, taken after the camera moved,
    by its place on the body. The check gives the angle between the two shots' wave normals
    there. Waves fixed to the water give the same normal at the same place."""
    first, first_valid, _ = world_points(folder, prefix + '-a')
    second_points, second_valid, data = world_points(folder, prefix + '-b')
    n1 = read_pfm(os.path.join(folder, prefix + '-a-traced.pfm')).astype(float) * 2.0 - 1.0
    n2 = read_pfm(os.path.join(folder, prefix + '-b-traced.pfm')).astype(float) * 2.0 - 1.0
    water1 = first_valid & (np.linalg.norm(n1, axis=-1) > 0.5)
    water2 = second_valid & (np.linalg.norm(n2, axis=-1) > 0.5)
    height, width = water2.shape
    projection = np.array(data['projection']).reshape(4, 4)
    to_world = np.array(data['cameraToWorld']).reshape(4, 4)
    to_camera = np.linalg.inv(to_world)
    centre = np.array(data['bodyCentre'])
    points = first[water1] + centre
    camera = points @ to_camera[:3, :3].T + to_camera[:3, 3]
    clip = np.concatenate([camera, np.ones((len(camera), 1))], 1) @ projection.T
    ndc = clip[:, :2] / clip[:, 3:4]
    px = np.floor((ndc[:, 0] + 1.0) * 0.5 * width).astype(int)
    py = np.floor((ndc[:, 1] + 1.0) * 0.5 * height).astype(int)
    inside = (clip[:, 3] > 0) & (px >= 0) & (px < width) & (py >= 0) & (py < height)
    a_normals = n1[water1][inside]
    a_points = first[water1][inside]
    px, py = px[inside], py[inside]
    found = water2[py, px]
    # The same place: the second shot's point at that pixel within a pixel's width.
    gap = np.linalg.norm(second_points[py, px] - a_points, axis=-1)
    reach = np.linalg.norm(camera[inside], axis=-1) * 2.0 / (projection[1, 1] * height)
    same = found & (gap < 2.0 * reach + 0.01)
    a_n = a_normals[same]
    b_n = n2[py[same], px[same]]
    a_n /= np.linalg.norm(a_n, axis=-1, keepdims=True)
    b_n /= np.linalg.norm(b_n, axis=-1, keepdims=True)
    angle = np.degrees(np.arccos(np.clip(np.sum(a_n * b_n, -1), -1, 1)))
    return len(angle), angle


def main():
    """Compare the waves (--waves), measure the ripple noise (--noise), or print the table of every shot."""
    args = sys.argv[1:]
    if args[:1] == ['--waves']:
        folder = args[1]
        names = sorted({f[:-len('-a.json')] for f in os.listdir(folder) if f.endswith('-a.json') and '-waves-' in f})
        for name in names:
            count, angle = waves_in_place(folder, name)
            if count == 0:
                print('%-32s no water seen in both shots' % name)
                continue
            print('%-32s %8d places: the normals %5.2f degrees apart (median), %5.2f (90 %%), %4.1f %% within 1 degree' % (
                name, count, np.median(angle), np.percentile(angle, 90), 100.0 * np.mean(angle < 1.0)))
        return
    if args[:1] == ['--noise']:
        total, axes = ripple_gradient_rms()
        print('RippleNoise gradient: %.4f in root mean square, %.4f / %.4f along x / y' % (total, axes[0], axes[1]))
        return
    folder = args.pop(0)
    sun_colours = None
    if args[:1] == ['--log']:
        args.pop(0)
        sun_colours = traced_sun_colours(args.pop(0))
    names = args or sorted(f[:-5] for f in os.listdir(folder) if f.endswith('.json'))
    print('%-11s %5s %7s %5s %6s | %6s %6s | %-13s | %-13s | %-13s | %-13s | %-23s | %-11s | %7s %7s | %-14s' % (
        'shot', 'body', 'height', 'm', 'foot', 'ratio', 'total', 'peak tr/mod', 'centre tr/mod', 'spread v', 'spread h',
        'extent traced / model', 'half tr/mod', 'sparkle', 'source', 'screen half/top'))
    for name in names:
        name, r = analyse(folder, name, sun_colours)
        if r is None:
            print('%-11s no source or no sea' % name)
            continue
        a, b = r['measured'], r['model']
        mm, me = r['moments']
        # The same on the screenshot: the path's half width as the screen shows it, and its
        # middle's brightness there, 1 white -- wider than the traced where the middle is
        # clipped.
        screen = ''
        picture = screenshot(folder, name)
        if picture is not None and picture.shape[:2] == read_pfm(os.path.join(folder, name + '-traced.pfm')).shape[:2]:
            _, f = analyse(folder, name, sun_colours, picture)
            if f is not None:
                screen = '%4.1f / %5.3f' % (f['measured']['half'], f['measured']['top'])
        print('%-11s %5.1f %7.0f %5.3f %6.2f | %6.3f %6.3f | %5.1f / %5.1f | %5.1f / %5.1f | %5.1f / %5.1f | %5.1f / %5.1f | %4.1f-%4.1f / %4.1f-%4.1f | %4.1f / %4.1f | %7.2f %7s | %s' % (
            name, r['elevation'], r['altitude'], r['m'], r['footprint'], r['ratio'], mm[3] / me[3], a['peak'], b['peak'],
            mm[0], me[0], mm[1], me[1], mm[2], me[2],
            a['extent'][0], a['extent'][1], b['extent'][0], b['extent'][1], a['half'], b['half'], r['sparkle'],
            r['source'], screen))


if __name__ == '__main__':
    main()
