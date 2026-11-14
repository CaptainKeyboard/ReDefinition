#!/usr/bin/env python3
"""E40 type-mix audit: sizes in km and the number of cloud types at once, from the maps TypeMixExplore dumps (tests/.../T09/TypeMixExplore.cs).

  set TYPEMIX_OUT=<dir>; python tools/dotnet_test.py --filter FullyQualifiedName~TypeMixExplore
  python tools/e40_type_mix.py stats <dir>            the numbers (text, to stdout)
  python tools/e40_type_mix.py pictures <dir> <out> [prefix]   <prefix>globe / region / compare PNGs into <out> (prefix default e40-mix-)
  python tools/e40_type_mix.py check <dir>            exits 1 where a bound of the mix is not met (the pin of the fix, see E40-type-mix-audit.md)

The maps are 512 x 256 equirectangular, row 0 north, Kerbin's radius 600 km. TVC's maps are read from the installed StockVolumetricClouds (base/coverage.dds, base/type.dds).
"""
import math
import os
import re
import sys

import numpy as np
from scipy import ndimage

W, H, R = 512, 256, 600.0
KSP = r"C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program"
TVC = os.path.join(KSP, "GameData", "StockVolumetricClouds", "Clouds", "Textures", "PluginData", "Kerbin")
LAYERS = ["Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus"]
# a layer shows its type where its map (the share of the sky it covers) is at least this
PRESENT = 0.2


def read_meta(folder):
    layers = {}
    for line in open(os.path.join(folder, "meta.txt")):
        m = re.match(r"layer (\S+) mid (\S+) types (.*) fair (\S+) alto (\S+)", line)
        if m:
            layers[m.group(1)] = dict(mid=float(m.group(2)), types=re.findall(r"'([^']*)'", m.group(3)), fair=float(m.group(4)), alto=float(m.group(5)))
    return layers


def load(folder, seed, k, name):
    return np.fromfile(os.path.join(folder, "s%d_t%d_%s.u8" % (seed, k, name)), dtype=np.uint8).reshape(H, W).astype(np.float32) / 255.0


def lat_of_rows(h):
    return 90.0 - (np.arange(h) + 0.5) * 180.0 / h


def cell_km(h, w):
    """km per texel along a row (per latitude) and along a column."""
    lat = np.radians(lat_of_rows(h))
    return 2 * math.pi * R / w * np.cos(lat), math.pi * R / h


def area_weights(h, w):
    dx, dy = cell_km(h, w)
    return (dx * dy)[:, None] * np.ones((1, w))


def label_wrap(mask):
    """Connected components (4-neighbour) with the longitude wrapped."""
    lab, n = ndimage.label(mask)
    if n == 0:
        return lab, 0
    parent = list(range(n + 1))

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for a, b in zip(lab[:, 0], lab[:, -1]):
        if a and b and find(a) != find(b):
            parent[find(a)] = find(b)
    remap = np.array([find(i) for i in range(n + 1)])
    lab = remap[lab]
    uniq = np.unique(lab[lab > 0])
    idx = np.zeros(lab.max() + 1, dtype=np.int64)
    idx[uniq] = np.arange(1, len(uniq) + 1)
    return idx[lab], len(uniq)


def components(mask, weights):
    """Area (km^2) of each connected component of a mask, equator band only is not assumed: all latitudes, spherical area weights."""
    # the bands above 60 degrees are left out (the texels there are a few km wide; the poles are one connected region by construction)
    mask = mask & (np.abs(lat_of_rows(mask.shape[0])) < 60)[:, None]
    lab, n = label_wrap(mask)
    if n == 0:
        return np.zeros(0)
    return ndimage.sum(weights, lab, index=np.arange(1, n + 1))


def band_area(mask, weights):
    """Area (km^2) of a mask below 60 degrees latitude, the area the components are taken in."""
    return float(((mask & (np.abs(lat_of_rows(mask.shape[0])) < 60)[:, None]) * weights).sum())


def weighted_median(values, weights):
    """The value below which half of the total weight lies (the size of the component a random point of the mask sits in)."""
    if len(values) == 0:
        return 0.0
    o = np.argsort(values)
    cw = np.cumsum(np.asarray(weights)[o])
    return float(np.asarray(values)[o][np.searchsorted(cw, 0.5 * cw[-1])])


def spectrum(field, lat_lo=0, lat_hi=60):
    """Shares of the variance of a map (zonal, rows in a latitude band) at wavelengths under 100 km, 100 to 300 km and over 300 km."""
    lat = lat_of_rows(field.shape[0])
    rows = np.where((np.abs(lat) >= lat_lo) & (np.abs(lat) < lat_hi))[0]
    dx, _ = cell_km(field.shape[0], field.shape[1])
    acc = np.zeros(3)
    for r in rows:
        f = field[r] - field[r].mean()
        ps = np.abs(np.fft.rfft(f)) ** 2
        wl = field.shape[1] * dx[r] / np.maximum(np.arange(len(ps)), 1e-9)
        acc += [ps[(wl < 100) & (np.arange(len(ps)) > 0)].sum(), ps[(wl >= 100) & (wl < 300)].sum(), ps[wl >= 300].sum()]
    return acc / max(acc.sum(), 1e-12)


def size_report(maps, wts):
    """One line of sizes for a list of cover maps (same grid): correlation length, variance by wavelength, field and gap sizes in km."""
    zs, ms, sp = [], [], []
    res = {}
    for thr, hi in ((0.5, True), (0.15, False)):
        res[thr] = dict(d=[], a=[], big=[], share=[])
    for m in maps:
        z, mm = corr_length(m, 0, 60)
        zs.append(z)
        ms.append(mm)
        sp.append(spectrum(m))
        for thr, hi in ((0.5, True), (0.15, False)):
            mask = (m >= thr) if hi else (m < thr)
            a = components(mask, wts)
            res[thr]["d"].extend(diam(a).tolist())
            res[thr]["a"].extend(a.tolist())
            res[thr]["share"].append(band_area(mask, wts) / band_area(np.ones_like(mask), wts))
            if len(a):
                res[thr]["big"].append(float(a.max() / band_area(mask, wts)))
    sp = np.mean(sp, axis=0)
    out = "corr length (1/e) zonal %.0f / meridional %.0f km | variance at wavelength <100 km %.0f%%, 100-300 km %.0f%%, >300 km %.0f%%" % (np.mean(zs), np.mean(ms), 100 * sp[0], 100 * sp[1], 100 * sp[2])
    for thr, label in ((0.5, "field (m>=0.5)"), (0.15, "gap (m<0.15)")):
        r = res[thr]
        out += "\n%-22s   %s: share %.2f, diameter of the component a point lies in (area-weighted median) %.0f km, p90 of components %.0f km, largest = %.0f%% of its area" % (
            "", label, np.mean(r["share"]), weighted_median(r["d"], r["a"]), np.percentile(r["d"], 90) if r["d"] else 0, 100 * np.mean(r["big"]) if r["big"] else 0)
    return out


def diam(area):
    return 2.0 * np.sqrt(area / math.pi)


def corr_length(field, lat_lo, lat_hi):
    """e-folding lengths (km) of the autocorrelation of a map, zonal (along rows) and meridional, in a latitude band, 1/e of the mean-removed correlation."""
    lat = lat_of_rows(field.shape[0])
    rows = np.where((np.abs(lat) >= lat_lo) & (np.abs(lat) < lat_hi))[0]
    f = field[rows]
    f = f - f.mean()
    dx, dy = cell_km(field.shape[0], field.shape[1])
    dxm = float(np.mean(dx[rows]))
    # zonal: periodic, FFT per row
    spec = np.abs(np.fft.rfft(f, axis=1)) ** 2
    ac = np.fft.irfft(spec.mean(axis=0))
    ac = ac / ac[0] if ac[0] > 0 else ac
    zon = first_below(ac[: f.shape[1] // 2], 1 / math.e) * dxm
    # meridional: not periodic, direct on the band, up to 40 rows
    m = []
    for lag in range(0, min(40, f.shape[0] // 2)):
        a, b = f[: f.shape[0] - lag], f[lag:]
        m.append(float((a * b).mean()))
    m = np.array(m)
    m = m / m[0] if m[0] > 0 else m
    mer = first_below(m, 1 / math.e) * dy
    return zon, mer


def first_below(ac, level):
    for i, v in enumerate(ac):
        if v < level:
            if i == 0:
                return 0.0
            return i - 1 + (ac[i - 1] - level) / max(ac[i - 1] - v, 1e-9)
    return float(len(ac))


def type_index(typ, n):
    return np.clip(np.rint(typ * (n - 1)).astype(int), 0, n - 1)


def stats_ours(folder):
    meta = read_meta(folder)
    seeds = sorted({int(m.group(1)) for f in os.listdir(folder) for m in [re.match(r"s(\d+)_t0_precip", f)] if m})
    times = sorted({int(m.group(1)) for f in os.listdir(folder) for m in [re.match(r"s1_t(\d+)_precip", f)] if m})
    wts = area_weights(H, W)
    out = {}
    lines = []
    P = lines.append
    P("OURS: seeds %s, %d times over one Kerbin day, maps %dx%d = %.1f km per texel (N-S), %.1f km (E-W at the equator)" % (seeds, len(times), W, H, math.pi * R / H, 2 * math.pi * R / W))
    # --- sizes
    for name in LAYERS + ["sharedCover"]:
        maps = [load(folder, s, k, name if name == "sharedCover" else name + "_cover") for s in seeds for k in times]
        P("%-22s %s" % (name, size_report(maps, wts)))
    # --- types at once
    P("")
    P("TYPES (nearest type of the layer's type bytes, present where the layer's map >= %.2f)" % PRESENT)
    for name in LAYERS:
        types = meta[name]["types"]
        n = len(types)
        per_pixel, globe, share, dom, plate = [], [], np.zeros(n), [], []
        for s in seeds:
            for k in times:
                c = load(folder, s, k, name + "_cover")
                t = type_index(load(folder, s, k, name + "_type"), n)
                pres = c >= PRESENT
                if not pres.any():
                    continue
                cnt = np.zeros(n)
                for i in range(n):
                    cnt[i] = float(((t == i) & pres).__mul__(wts).sum())
                    share[i] += cnt[i]
                tot = cnt.sum()
                globe.append(int((cnt / tot > 0.01).sum()))
                dom.append(float(cnt.max() / tot))
                # types within 300 km (a window of 41 x 41 texels at the equator) at one time: distinct types present
                for r in range(10, H - 10, 20):
                    for q in range(0, W, 41):
                        win = (t[r:r + 40, q:q + 41])[pres[r:r + 40, q:q + 41]]
                        if win.size > 0:
                            per_pixel.append(len(set(np.unique(win).tolist())))
                # region of one type: component of the dominant type
                i = int(cnt.argmax())
                a = components((t == i) & pres, wts)
                plate.append(float(a.max() / band_area(pres, wts)))
        sh = share / max(share.sum(), 1e-9)
        tz = [corr_length(load(folder, s, k, name + "_type"), 0, 60) for s in seeds for k in times]
        sp = np.mean([spectrum(load(folder, s, k, name + "_type")) for s in seeds for k in times], axis=0)
        P("%-22s types: %s | type map corr length zonal %.0f / meridional %.0f km, variance at <100 km %.0f%%, >300 km %.0f%%"
          % (name, ", ".join("%s %.0f%%" % (types[i], 100 * sh[i]) for i in range(n)), np.mean([a for a, b in tz]), np.mean([b for a, b in tz]), 100 * sp[0], 100 * sp[2]))
        P("%-22s distinct types (>1%% of the cloudy area) on the globe at one time: mean %.2f; dominant type = %.0f%% of the cloudy area; largest single-type component = %.0f%% of the cloudy area; mean types in a 300 km window: %.2f"
          % ("", np.mean(globe) if globe else 0, 100 * np.mean(dom) if dom else 0, 100 * np.mean(plate) if plate else 0, np.mean(per_pixel) if per_pixel else 0))
    # stacked: distinct (layer,type) pairs and layers present per pixel
    stack_types, stack_layers, clear = [], [], []
    for s in seeds:
        for k in times:
            seen = np.zeros((H, W), dtype=np.int64)
            nl = np.zeros((H, W), dtype=np.int64)
            sets = []
            for li, name in enumerate(LAYERS):
                n = len(meta[name]["types"])
                c = load(folder, s, k, name + "_cover") >= PRESENT
                t = type_index(load(folder, s, k, name + "_type"), n)
                nl += c
                sets.append(np.where(c, 1 << (li * 8 + t), 0))
            bits = np.zeros((H, W), dtype=np.int64)
            for a in sets:
                bits |= a
            cnt = np.zeros((H, W), dtype=np.int64)
            for b in range(64):
                cnt += (bits >> b) & 1
            cloudy = nl > 0
            clear.append(float((wts * (~cloudy)).sum() / wts.sum()))
            stack_types.append(float((cnt * wts)[cloudy].sum() / wts[cloudy].sum()))
            stack_layers.append(float((nl * wts)[cloudy].sum() / wts[cloudy].sum()))
    P("STACKED over a cloudy place (area-weighted mean): %.2f layers, %.2f (layer,type) pairs; clear-sky share %.2f" % (np.mean(stack_layers), np.mean(stack_types), np.mean(clear)))
    # one place over a day
    P("")
    P("ONE PLACE OVER A DAY: distinct (layer,type) pairs present at any of the %d times (mean over 400 sample places per seed, area unweighted below 60 deg)" % len(times))
    rng = np.random.RandomState(7)
    lat = lat_of_rows(H)
    for s in seeds:
        res = []
        for _ in range(400):
            r = int(rng.randint(0, H))
            if abs(lat[r]) > 60:
                continue
            q = int(rng.randint(0, W))
            pairs = set()
            for k in times:
                for name in LAYERS:
                    n = len(meta[name]["types"])
                    if load(folder, s, k, name + "_cover")[r, q] >= PRESENT:
                        pairs.add((name, int(type_index(load(folder, s, k, name + "_type"), n)[r, q])))
            res.append(len(pairs))
        P("seed %d: %.2f pairs (min %d, max %d)" % (s, np.mean(res), min(res), max(res)))
    # latitude and land / sea
    P("")
    P("LATITUDE: cumulus layer, mean map value and the type shares per 30 deg band (seed %d, all times)" % seeds[0])
    n = len(meta["Kerbin-cumulus"]["types"])
    for lo in range(-90, 90, 30):
        rows = np.where((lat >= lo) & (lat < lo + 30))[0]
        cs, ts = [], np.zeros(n)
        for k in times:
            c = load(folder, seeds[0], k, "Kerbin-cumulus_cover")[rows]
            t = type_index(load(folder, seeds[0], k, "Kerbin-cumulus_type"), n)[rows]
            cs.append(c.mean())
            for i in range(n):
                ts[i] += ((t == i) & (c >= PRESENT)).sum()
        ts = ts / max(ts.sum(), 1)
        P("  lat %4d..%4d: cover %.2f | %s" % (lo, lo + 30, np.mean(cs), ", ".join("%s %.0f%%" % (meta["Kerbin-cumulus"]["types"][i][:14], 100 * ts[i]) for i in range(n))))
    hp = [f for f in os.listdir(folder) if f.startswith("heights_")]
    if hp:
        hw, hh = [int(x) for x in re.match(r"heights_(\d+)x(\d+)", hp[0]).groups()]
        hgt = np.fromfile(os.path.join(folder, hp[0]), dtype=np.float32).reshape(hh, hw)
        sea = ndimage.zoom((hgt <= 0).astype(np.float32), (H / hh, W / hw), order=1) > 0.5
        P("LAND / SEA (share of sea %.2f): mean map value and type share, cumulus / rain deck, all seeds and times, |lat| < 60" % sea[np.abs(lat) < 60].mean())
        for name in ("Kerbin-cumulus", "Kerbin-weather-rain", "sharedCover"):
            vals = {True: [], False: []}
            for s in seeds:
                for k in times:
                    c = load(folder, s, k, name if name == "sharedCover" else name + "_cover")
                    band = np.abs(lat) < 60
                    for flag in (True, False):
                        sel = band[:, None] & (sea == flag)
                        vals[flag].append(c[sel].mean())
            P("  %-22s sea %.3f, land %.3f" % (name, np.mean(vals[True]), np.mean(vals[False])))
    return "\n".join(lines)


def read_tvc(name):
    from PIL import Image
    return np.asarray(Image.open(os.path.join(TVC, name))).astype(np.float32) / 255.0


def stats_tvc():
    from scipy.ndimage import zoom
    lines = []
    P = lines.append
    cov = read_tvc("base/coverage.dds")
    typ = read_tvc("base/type.dds")
    h, w = cov.shape
    P("TVC (StockVolumetricClouds, Kerbin base layer maps, %dx%d = %.2f km per texel, static global map; painter tiles come on top)" % (w, h, 2 * math.pi * R / w))
    wts = area_weights(h, w)
    z, m = corr_length(cov, 0, 60)
    P("coverage: corr length (1/e) zonal %.0f km, meridional %.0f km" % (z, m))
    for thr, hi in ((0.5, True), (0.15, False)):
        mask = (cov >= thr) if hi else (cov < thr)
        a = components(mask, wts)
        d = diam(a)
        P("coverage %s %.2f: share %.2f, components %d, diameter median %.0f p90 %.0f max %.0f km, largest = %.0f%% of area" % (">=" if hi else "<", thr, (mask * wts).sum() / wts.sum(), len(a), np.median(d), np.percentile(d, 90), d.max(), 100 * a.max() / band_area(mask, wts)))
    n = 6
    t = type_index(typ, n)
    pres = cov >= PRESENT
    cnt = np.array([float(((t == i) & pres).__mul__(wts).sum()) for i in range(n)])
    P("type map (6 types of the base layer: Stratus, Cumulus, Congestus 0..3; nearest of value x 5): %s" % ", ".join("%d: %.0f%%" % (i, 100 * c / cnt.sum()) for i, c in enumerate(cnt)))
    zt, mt = corr_length(typ, 0, 60)
    P("type map corr length zonal %.0f km, meridional %.0f km; distinct types > 1%% of the cloudy area: %d" % (zt, mt, int((cnt / cnt.sum() > 0.01).sum())))
    per = []
    for r in range(0, h - 650, 650):
        for q in range(0, w - 650, 650):
            win = t[r:r + 650, q:q + 650][pres[r:r + 650, q:q + 650]]
            if win.size > 0:
                per.append(len(np.unique(win)))
    P("mean types in a 300 km window: %.2f" % np.mean(per))
    i = int(cnt.argmax())
    a = components((t == i) & pres, wts)
    P("dominant type = %.0f%% of cloudy area; largest single-type component = %.0f%% of the cloudy area; type-component diameter median %.0f km" % (100 * cnt.max() / cnt.sum(), 100 * a.max() / band_area(pres, wts), np.median(diam(a))))
    # the same maps on our grid (16 x 16 block mean of the coverage, the type with the most coverage in the block): the sizes are then comparable with ours
    f = h // H
    cd = cov.reshape(H, f, W, f).mean(axis=(1, 3))
    best = np.zeros((n, H, W), dtype=np.float32)
    for i in range(n):
        best[i] = ((t == i) * cov).reshape(H, f, W, f).sum(axis=(1, 3))
    td = best.argmax(axis=0)
    wd = area_weights(H, W)
    P("ON OUR GRID (512 x 256, %.1f km per texel): coverage %s" % (math.pi * R / H, size_report([cd], wd)))
    presd = cd >= PRESENT
    cntd = np.array([float(((td == i) & presd).__mul__(wd).sum()) for i in range(n)])
    P("  types: %s; distinct > 1%%: %d; dominant %.0f%%" % (", ".join("%d: %.0f%%" % (i, 100 * c / cntd.sum()) for i, c in enumerate(cntd)), int((cntd / cntd.sum() > 0.01).sum()), 100 * cntd.max() / cntd.sum()))
    perd = []
    for r in range(10, H - 10, 20):
        for q in range(0, W, 41):
            win = (td[r:r + 40, q:q + 41])[presd[r:r + 40, q:q + 41]]
            if win.size > 0:
                perd.append(len(np.unique(win)))
    P("  mean types in a 300 km window: %.2f" % np.mean(perd))
    i = int(cntd.argmax())
    a = components((td == i) & presd, wd)
    P("  largest single-type component = %.0f%% of the cloudy area; type-component diameter median %.0f km" % (100 * a.max() / band_area(presd, wd), np.median(diam(a))))
    w1 = read_tvc("Weather1Coverage.dds")
    z1, m1 = corr_length(w1, 0, 60)
    P("weather map 1 (rain / fog deck): %dx%d, corr length zonal %.0f km, meridional %.0f km, mean %.2f" % (w1.shape[1], w1.shape[0], z1, m1, w1.mean()))
    return "\n".join(lines)


def rgb_types(t, n, cmap):
    import matplotlib.cm as cm
    return cm.get_cmap(cmap)(np.clip(np.rint(t * (n - 1)) / max(n - 1, 1), 0, 1))[..., :3]


def pictures(folder, out, prefix="e40-mix-"):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.colors import ListedColormap
    os.makedirs(out, exist_ok=True)
    meta = read_meta(folder)
    ct = meta["Kerbin-cumulus"]["types"]
    n = len(ct)
    pal = ListedColormap(["#8c6d31", "#e7ba52", "#5b9bd5", "#d6616b", "#7b4173"][:n])
    km = 2 * math.pi * R

    def draw_type(ax, c, t, title, extent=None):
        idx = type_index(t, n)
        rgb = pal(idx)[..., :3]
        alpha = np.clip((c - 0.05) / 0.3, 0, 1)[..., None]
        ax.imshow(rgb * alpha + (1 - alpha) * 0.12, extent=extent, interpolation="nearest")
        ax.set_title(title, fontsize=8)

    times = [0, 3, 6]
    # globe: cover and type of the cumulus layer, seed 1, three times, and the TVC base maps at the same km scale
    fig, axes = plt.subplots(3, 3, figsize=(15, 10.4))
    ext = [0, km, 0, math.pi * R]
    for j, k in enumerate(times):
        c = load(folder, 1, k, "Kerbin-cumulus_cover")
        t = load(folder, 1, k, "Kerbin-cumulus_type")
        axes[0, j].imshow(c, cmap="gray", vmin=0, vmax=1, extent=ext)
        axes[0, j].set_title("ours: cumulus-layer map value, seed 1, game time +%.1f h" % (k * 21549.425 / 8 / 3600), fontsize=8)
        draw_type(axes[1, j], c, t, "ours: cumulus-layer type (colour) where map >= 0.05, seed 1", ext)
    cov = read_tvc("base/coverage.dds")
    typ = read_tvc("base/type.dds")
    axes[2, 0].imshow(cov, cmap="gray", vmin=0, vmax=1, extent=ext)
    axes[2, 0].set_title("TVC: base coverage map (8192 x 4096, same km scale)", fontsize=8)
    tn = 6
    palt = ListedColormap(["#8c6d31", "#e7ba52", "#5b9bd5", "#d6616b", "#7b4173", "#2ca02c"])
    ti = type_index(typ, tn)
    alpha = np.clip((cov - 0.05) / 0.3, 0, 1)[..., None]
    axes[2, 1].imshow(palt(ti)[..., :3] * alpha + (1 - alpha) * 0.12, extent=ext, interpolation="nearest")
    axes[2, 1].set_title("TVC: base type map (6 types, colour)", fontsize=8)
    c = load(folder, 1, 3, "Kerbin-cumulonimbus_cover")
    axes[2, 2].imshow(load(folder, 1, 3, "Kerbin-cirrus_cover"), cmap="gray", vmin=0, vmax=1, extent=ext)
    axes[2, 2].set_title("ours: cirrus-layer map value, seed 1, +%.1f h" % (3 * 21549.425 / 8 / 3600), fontsize=8)
    for ax in axes.ravel():
        ax.set_xlabel("km along the equator (3770 km around)", fontsize=7)
        ax.set_ylabel("km N-S", fontsize=7)
        ax.tick_params(labelsize=6)
        ax.plot([100, 600], [60, 60], color="#ff4040", lw=3)
        ax.text(350, 100, "500 km", color="#ff4040", ha="center", fontsize=8)
    fig.suptitle("Kerbin, whole globe, equirectangular, equal km scale", fontsize=9)
    legend(fig, pal, ct, palt)
    fig.tight_layout(rect=[0, 0.04, 1, 1])
    fig.savefig(os.path.join(out, prefix + "globe.png"), dpi=100)
    plt.close(fig)

    # region: 300 x 300 km, ours (bilinear, like the shader's map sample) and TVC
    lat0, lon0 = 8.0, 100.0
    half = 150.0
    fig, axes = plt.subplots(2, 4, figsize=(16, 8.4))
    rows = int(round((90 - lat0) / 180 * H))
    cols = int(round(lon0 / 360 * W))
    nt = int(round(300 / (math.pi * R / H)))
    r0, c0 = rows - nt // 2, cols - int(round(nt / math.cos(math.radians(lat0)) / 2))
    cw = int(round(nt / math.cos(math.radians(lat0))))
    for j, k in enumerate([0, 3]):
        c = load(folder, 1, k, "Kerbin-cumulus_cover")[r0:r0 + nt, c0:c0 + cw]
        t = load(folder, 1, k, "Kerbin-cumulus_type")[r0:r0 + nt, c0:c0 + cw]
        axes[0, 2 * j].imshow(c, cmap="gray", vmin=0, vmax=1, extent=[0, 300, 0, 300], interpolation="bilinear")
        axes[0, 2 * j].set_title("ours: cumulus map value, +%.1f h" % (k * 21549.425 / 8 / 3600), fontsize=8)
        idx = type_index(t, n)
        alpha = np.clip((c - 0.05) / 0.3, 0, 1)[..., None]
        axes[0, 2 * j + 1].imshow(pal(idx)[..., :3] * alpha + (1 - alpha) * 0.12, extent=[0, 300, 0, 300], interpolation="nearest")
        axes[0, 2 * j + 1].set_title("ours: cumulus type (colour)", fontsize=8)
    th, tw = cov.shape
    px = 2 * math.pi * R / tw
    nn = int(round(300 / px))
    tr = int(round((90 - lat0) / 180 * th))
    tc = int(round(lon0 / 360 * tw))
    for j, (cr, cc) in enumerate([(tr, tc), (tr, tc + int(1300 / px))]):
        axes[1, 2 * j].imshow(cov[cr:cr + nn, cc:cc + nn], cmap="gray", vmin=0, vmax=1, extent=[0, 300, 0, 300])
        axes[1, 2 * j].set_title("TVC: base coverage, 300 x 300 km (%.2f km per texel)" % px, fontsize=8)
        sub = typ[cr:cr + nn, cc:cc + nn]
        sc = cov[cr:cr + nn, cc:cc + nn]
        alpha = np.clip((sc - 0.05) / 0.3, 0, 1)[..., None]
        axes[1, 2 * j + 1].imshow(palt(type_index(sub, tn))[..., :3] * alpha + (1 - alpha) * 0.12, extent=[0, 300, 0, 300], interpolation="nearest")
        axes[1, 2 * j + 1].set_title("TVC: base type (colour)", fontsize=8)
    for ax in axes.ravel():
        ax.set_xlabel("km", fontsize=7)
        ax.tick_params(labelsize=6)
        ax.plot([20, 120], [20, 20], color="#ff4040", lw=3)
        ax.text(70, 32, "100 km", color="#ff4040", ha="center", fontsize=8)
    fig.suptitle("300 x 300 km windows at the same scale: our maps (texel 7.4 km) above, TVC's base maps below", fontsize=9)
    legend(fig, pal, ct, palt)
    fig.tight_layout(rect=[0, 0.04, 1, 1])
    fig.savefig(os.path.join(out, prefix + "region.png"), dpi=100)
    plt.close(fig)

    # compare: where the variance of the cover map lies by wavelength, ours (per layer, 3 seeds, 8 times) against TVC's base map on our grid
    seeds = [1, 2, 3]
    fig, ax = plt.subplots(1, 1, figsize=(9, 4.5))
    labels, rows_ = [], []
    for name in LAYERS:
        labels.append(name.replace("Kerbin-", ""))
        rows_.append(np.mean([spectrum(load(folder, s, k, name + "_cover")) for s in seeds for k in range(8)], axis=0))
    f = cov.shape[0] // H
    cd = cov.reshape(H, f, W, f).mean(axis=(1, 3))
    labels.append("TVC base coverage")
    rows_.append(spectrum(cd))
    x = np.arange(len(labels))
    bottom = np.zeros(len(labels))
    for j, (lab, colr) in enumerate((("wavelength < 100 km", "#2a9d8f"), ("100 to 300 km", "#e9c46a"), ("> 300 km", "#8d99ae"))):
        v = np.array([r[j] for r in rows_]) * 100
        ax.bar(x, v, bottom=bottom, label=lab, color=colr)
        for xi, vi, bi in zip(x, v, bottom):
            if vi > 4:
                ax.text(xi, bi + vi / 2, "%.0f%%" % vi, ha="center", va="center", fontsize=8)
        bottom += v
    ax.set_xticks(x)
    ax.set_xticklabels(labels, fontsize=8)
    ax.set_ylabel("share of the variance of the map along the parallel (%)")
    ax.set_title("At which size the cover maps vary (|lat| < 60 deg, Kerbin km; both on a 7.4 km grid)", fontsize=9)
    ax.legend(fontsize=8, loc="upper right")
    fig.tight_layout()
    fig.savefig(os.path.join(out, prefix + "compare.png"), dpi=100)
    plt.close(fig)


def legend(fig, pal, ct, palt):
    from matplotlib.patches import Patch
    ours = [Patch(color=pal(i / max(len(ct) - 1, 1)), label=t) for i, t in enumerate(ct)]
    tv = [Patch(color=palt(i / 5), label="TVC type %d" % i) for i in range(6)]
    fig.legend(handles=ours + tv, loc="lower center", ncol=11, fontsize=7, frameon=False)


def check(folder):
    """The bounds the fix has to meet (E40-type-mix-audit.md section 5). Exits 1 where one is missed."""
    meta = read_meta(folder)
    wts = area_weights(H, W)
    seeds = sorted({int(m.group(1)) for f in os.listdir(folder) for m in [re.match(r"s(\d+)_t0_precip", f)] if m})
    bad = []
    for s in seeds:
        c = load(folder, s, 0, "Kerbin-cumulus_cover")
        z, m = corr_length(c, 0, 60)
        a = components(c >= 0.5, wts)
        big = a.max() / a.sum() if len(a) else 0.0
        n = len(meta["Kerbin-cumulus"]["types"])
        t = type_index(load(folder, s, 0, "Kerbin-cumulus_type"), n)
        pres = c >= PRESENT
        cnt = np.array([float(((t == i) & pres).__mul__(wts).sum()) for i in range(n)])
        types = int((cnt / cnt.sum() > 0.05).sum())
        print("seed %d: corr length %.0f / %.0f km, largest field %.0f%% of field area, types >5%% of the cloudy area: %d" % (s, z, m, 100 * big, types))
        # after E40 L3: 45 / 41 km, 26 %, 5 types (3 seeds x 8 times); before 330 / 198, 73 %, 3 (TVC's base on our grid 74 / 48, 14 %)
        if z > 100 or m > 90 or big > 0.4 or types < 3:
            bad.append(s)
    if bad:
        print("MISSED for seeds", bad)
        sys.exit(1)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    cmd, folder = sys.argv[1], sys.argv[2]
    if cmd == "stats":
        print(stats_ours(folder))
        print()
        print(stats_tvc())
    elif cmd == "pictures":
        pictures(folder, sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else "e40-mix-")
    elif cmd == "check":
        check(folder)


if __name__ == "__main__":
    main()
