#!/usr/bin/env python3
"""E40 F1: what the sky looks like from the maps, as numbers (CPU model beside the weather player's pictures).

  python tools/e40_sky_clear.py tables <tables.txt> <maps-dir> [--site LAT LON] [--uts UT ...]   per-layer and union shares at the site and around it, for the dump of SkyMapsExplore
  python tools/e40_sky_clear.py union <tables.txt> <mix-dir>                                    the same over the 24 maps of TypeMixExplore (3 seeds x 8 times): union against the field

<tables.txt> is SkyMapsExplore.DumpTheSkyClearTables (SKYMAPS_TABLES): per layer the model's share of the sky that is VISIBLE (opacity >= 0.10 looking up) and CLOSED (>= 0.90) per map
value byte and type value in 1/16ths (the product's own numbers, SkyClearModel.cs). The layers are taken as independent: union = 1 - prod(1 - share). Maps are 512 x 256, row 0 north,
column 0 at longitude -180; the means are area weighted (cos latitude).
"""
import math
import os
import sys

import numpy as np

W, H = 512, 256
R_KM = 600.0
LAYERS = ["Kerbin-weather-rain", "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus"]
SITE = (-0.0972, -74.5577)


def read_tables(path):
    tables, name, kind, rows = {}, None, None, []
    for line in open(path):
        parts = line.split()
        if len(parts) == 2 and not parts[0][0].isdigit():
            if name is not None:
                tables[(name, kind)] = np.array(rows, dtype=np.float64)
            name, kind, rows = parts[0], parts[1], []
        elif parts:
            rows.append([float(x) for x in parts])
    tables[(name, kind)] = np.array(rows, dtype=np.float64)
    return tables


def lookup(table, cover, type_):
    """The table's share at a map value and a type value (both 0..1 arrays): bilinear in the type bins, the map value to the nearest byte."""
    c = np.clip(np.rint(cover * 255.0).astype(int), 0, 255)
    t = np.clip(type_, 0.0, 1.0) * 16.0
    t0 = np.minimum(t.astype(int), 15)
    f = t - t0
    return table[c, t0] * (1.0 - f) + table[c, t0 + 1] * f


def shares_of(tables, cover, type_, kind):
    return {l: lookup(tables[(l, kind)], cover[l], type_[l]) for l in LAYERS}


def union(shares):
    keep = np.ones_like(next(iter(shares.values())))
    for l in LAYERS:
        keep = keep * (1.0 - shares[l])
    return 1.0 - keep


def weights():
    lat = np.radians(90.0 - (np.arange(H) + 0.5) * 180.0 / H)
    return np.cos(lat)[:, None] * np.ones((1, W))


def mean(a):
    w = weights()
    return float((a * w).sum() / w.sum())


def load(folder, prefix, name):
    return np.fromfile(os.path.join(folder, prefix + name + ".u8"), dtype=np.uint8).reshape(H, W).astype(np.float64) / 255.0


def sample(m, lat, lon):
    u = (lon + 180.0) / 360.0 * W - 0.5
    v = (90.0 - lat) / 180.0 * H - 0.5
    i0, j0 = int(math.floor(u)), int(math.floor(v))
    fu, fv = u - i0, v - j0

    def g(i, j):
        return m[min(max(j, 0), H - 1), i % W]

    return g(i0, j0) * (1 - fu) * (1 - fv) + g(i0 + 1, j0) * fu * (1 - fv) + g(i0, j0 + 1) * (1 - fu) * fv + g(i0 + 1, j0 + 1) * fu * fv


def disc(m, lat, lon, radius_km, step_km=7.0):
    n = np.arange(-radius_km, radius_km + 1, step_km)
    out = []
    for dn in n:
        for de in n:
            if de * de + dn * dn > radius_km ** 2:
                continue
            la = lat + math.degrees(dn / R_KM)
            lo = lon + math.degrees(de / R_KM / max(math.cos(math.radians(la)), 0.05))
            out.append(sample(m, la, lo))
    return np.array(out)


def site_report(tables, folder, uts, site):
    for ut in uts:
        tag = "u%d_" % ut
        cover = {l: load(folder, tag, l + "_cover") for l in LAYERS}
        type_ = {l: load(folder, tag, l + "_type") for l in LAYERS}
        field = load(folder, tag, "sharedCover")
        vis, clo = shares_of(tables, cover, type_, "visible"), shares_of(tables, cover, type_, "closed")
        print("ut %d: field cover global %.3f, at the site %.3f" % (ut, mean(field), sample(field, *site)))
        print("  globally   visible: " + " ".join("%s %.2f" % (l[7:15], mean(vis[l])) for l in LAYERS) + "  union %.3f  | closed union %.3f" % (mean(union(vis)), mean(union(clo))))
        for radius in (60, 120, 250):
            f = disc(field, *site, radius).mean()
            uv = disc(union(vis), *site, radius).mean()
            uc = disc(union(clo), *site, radius).mean()
            print("  within %3d km: field %.2f | visible union %.2f (layers %s) | closed union %.2f" % (
                radius, f, uv, " ".join("%.2f" % disc(vis[l], *site, radius).mean() for l in LAYERS), uc))


def union_report(tables, folder):
    names = sorted({f.split("_")[0] + "_" + f.split("_")[1] for f in os.listdir(folder) if f.startswith("s") and f.endswith("sharedCover.u8")})
    rows = []
    for tag in names:
        prefix = tag + "_"
        cover = {l: load(folder, prefix, l + "_cover") for l in LAYERS}
        type_ = {l: load(folder, prefix, l + "_type") for l in LAYERS}
        field = mean(load(folder, prefix, "sharedCover"))
        vis, clo = shares_of(tables, cover, type_, "visible"), shares_of(tables, cover, type_, "closed")
        rows.append((field, mean(union(vis)), mean(union(clo)), [mean(vis[l]) for l in LAYERS], [mean(cover[l]) for l in LAYERS]))
    a = np.array([[r[0], r[1], r[2]] for r in rows])
    per = np.array([r[3] for r in rows])
    maps = np.array([r[4] for r in rows])
    print("%d maps: field cover %.3f | visible union %.3f | closed union %.3f" % (len(rows), a[:, 0].mean(), a[:, 1].mean(), a[:, 2].mean()))
    print("  per layer mean map value: " + " ".join("%s %.3f" % (l[7:15], maps[:, i].mean()) for i, l in enumerate(LAYERS)))
    print("  per layer visible share:  " + " ".join("%s %.3f" % (l[7:15], per[:, i].mean()) for i, l in enumerate(LAYERS)))
    print("  visible union minus field per map: mean %+.3f, min %+.3f, max %+.3f" % ((a[:, 1] - a[:, 0]).mean(), (a[:, 1] - a[:, 0]).min(), (a[:, 1] - a[:, 0]).max()))
    return a


def selftest():
    t = np.zeros((256, 17))
    t[:, :] = np.linspace(0, 1, 256)[:, None]
    tables = {(l, k): t for l in LAYERS for k in ("visible", "closed")}
    cover = {l: np.full((H, W), 0.5) for l in LAYERS}
    type_ = {l: np.zeros((H, W)) for l in LAYERS}
    s = shares_of(tables, cover, type_, "visible")
    assert abs(mean(union(s)) - (1 - 0.5 ** 4)) < 0.01
    m = np.zeros((H, W))
    m[:, :256] = 1.0
    assert abs(sample(m, 0.0, -90.0) - 1.0) < 1e-9 and abs(sample(m, 0.0, 90.0)) < 1e-9
    print("selftest passed")


if __name__ == "__main__":
    if len(sys.argv) >= 2 and sys.argv[1] == "--selftest":
        selftest()
    elif len(sys.argv) >= 4 and sys.argv[1] == "tables":
        tabs = read_tables(sys.argv[2])
        uts = [int(x) for x in sys.argv[sys.argv.index("--uts") + 1:]] if "--uts" in sys.argv else [1881000, 1686000]
        site_report(tabs, sys.argv[3], uts, SITE)
    elif len(sys.argv) >= 4 and sys.argv[1] == "union":
        union_report(read_tables(sys.argv[2]), sys.argv[3])
    else:
        print(__doc__)
