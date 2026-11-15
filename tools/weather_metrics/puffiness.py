"""Puffiness of a cloud picture (package PUFF, 2026-11-15): how much small-scale billowy detail the cloud pixels carry.

The cloud mask is the colour distance of the picture to the same view without clouds (the no-cloud frame of the series; e40pp's mask rule). Three groups of numbers:

  edge     the cloud's edge at several mask thresholds: box-counting dimension of the edge at 2-8 px and 8-64 px, and the length of the raw edge against the length of
           the same mask smoothed by sigma 4 / 8 / 16 px (1 = the edge has nothing finer than sigma; every extra tenth is edge detail below sigma)
  spectrum the luminance of the cloud interior: the structure function S(r) / mean^2 at lags 1-64 px (relative contrast per lag) and, from tiles that lie wholly in the
           interior, the radial power spectrum slope over 2-64 px and the share of the variance per octave band
  bumps    protrusions of 5-40 px width on the edge: components of (open(mask, 2.5) minus open(mask, 20)) that touch the body (open(mask, 20)), counted per 1000 px of body
           edge, and the share of the mask's edge that lies on such components

Pixels of the vessel box and of the picture's border are excluded. No reference range exists in this file; a verdict (in range / out of range / unclear) is made by
the caller against a source it has named.

    python tools/weather_metrics/puffiness.py --pair <cloud.png> <nocloud.png> [--box x0,y0,x1,y1] [--label name] [--json out.json]
"""
import argparse
import json
import sys

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

THRESHOLDS = (0.12, 0.22, 0.35)
SCALES = (2, 4, 8, 16, 32, 64)


def load(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255.0


def luminance(img):
    return 0.2126 * img[..., 0] + 0.7152 * img[..., 1] + 0.0722 * img[..., 2]


def valid_mask(shape, box, border=4):
    ok = np.ones(shape, bool)
    ok[:border, :] = ok[-border:, :] = False
    ok[:, :border] = ok[:, -border:] = False
    if box:
        x0, y0, x1, y1 = box
        ok[y0:y1, x0:x1] = False
    return ok


def cloud_distance(img, base):
    """How much brighter in red and green the picture is than the same view without clouds (a blue sky gains red and green under a white cloud; the sky's halo around a cloud
    changes mostly blue). Smoothed by sigma 0.8 px against the dither."""
    d = 0.5 * ((img[..., 0] - base[..., 0]) + (img[..., 1] - base[..., 1]))
    return ndi.gaussian_filter(np.maximum(d, 0.0), 0.8)


def disk(r):
    y, x = np.ogrid[-r:r + 1, -r:r + 1]
    return x * x + y * y <= r * r


def opening(m, r):
    """Opening of a boolean mask by a disk of radius r (exact, by the distance transforms)."""
    if r <= 0:
        return m
    inside = ndi.distance_transform_edt(m)
    eroded = inside > r
    outside = ndi.distance_transform_edt(~eroded)
    return outside <= r


def edge_pixels(m):
    return m & ~ndi.binary_erosion(m, structure=ndi.generate_binary_structure(2, 1), border_value=1)


def edge_length(m, ok):
    """Number of 4-neighbour pairs (inside, outside) over valid pixels: a length in px (times a constant between 1 and pi/2 for the orientation, the same in a ratio)."""
    n = 0
    a, b = m[:, :-1], m[:, 1:]
    v = ok[:, :-1] & ok[:, 1:]
    n += np.count_nonzero((a != b) & v)
    a, b = m[:-1, :], m[1:, :]
    v = ok[:-1, :] & ok[1:, :]
    n += np.count_nonzero((a != b) & v)
    return n


def box_count(edge, ok):
    """N(s): the number of s x s boxes with an edge pixel, over boxes wholly valid."""
    h, w = edge.shape
    out = {}
    for s in SCALES:
        hh, ww = h // s * s, w // s * s
        e = edge[:hh, :ww].reshape(hh // s, s, ww // s, s).any(axis=(1, 3))
        v = ok[:hh, :ww].reshape(hh // s, s, ww // s, s).all(axis=(1, 3))
        out[s] = int(np.count_nonzero(e & v))
    return out


def slope(counts, lo, hi):
    xs = [s for s in SCALES if lo <= s <= hi and counts[s] > 0]
    if len(xs) < 2:
        return float("nan")
    p = np.polyfit(np.log(xs), np.log([counts[s] for s in xs]), 1)
    return -p[0]


def edge_group(dist, ok):
    rows = {}
    for t in THRESHOLDS:
        m = (dist > t) & ok
        area = int(np.count_nonzero(m))
        row = {"area": area}
        if area < 400:
            rows[t] = row
            continue
        e = edge_pixels(m) & ok
        ok_e = ndi.binary_erosion(ok, iterations=2)
        e &= ok_e
        counts = box_count(e, ok_e)
        row["edge_px"] = int(np.count_nonzero(e))
        row["D_2_8"] = slope(counts, 2, 8)
        row["D_8_64"] = slope(counts, 8, 64)
        raw = edge_length(m, ok)
        for sig in (4, 8, 16):
            sm = ndi.gaussian_filter(m.astype(float), sig) > 0.5
            row["Lratio_s%d" % sig] = raw / max(edge_length(sm, ok), 1)
        rows[t] = row
    return rows


def structure_function(lum, interior, lags=(1, 2, 4, 8, 16, 32, 64)):
    out = {}
    mean = lum[interior].mean() if interior.any() else float("nan")
    for r in lags:
        acc, n = 0.0, 0
        for ax in (0, 1):
            a = lum[:-r, :] if ax == 0 else lum[:, :-r]
            b = lum[r:, :] if ax == 0 else lum[:, r:]
            va = interior[:-r, :] if ax == 0 else interior[:, :-r]
            vb = interior[r:, :] if ax == 0 else interior[:, r:]
            v = va & vb
            d = (a - b)[v]
            acc += float((d * d).sum())
            n += int(d.size)
        out[r] = (acc / n) ** 0.5 / mean if n > 200 else float("nan")
    return out


def tile_spectrum(lum, interior, tile=64, cover=0.97):
    """Radial power spectrum from tiles wholly in the interior; returns (tiles, band shares, slope over 2-64 px)."""
    h, w = lum.shape
    hann = np.outer(np.hanning(tile), np.hanning(tile))
    yy, xx = np.indices((tile, tile))
    fy, fx = np.fft.fftfreq(tile)[:, None], np.fft.fftfreq(tile)[None, :]
    k = np.sqrt(fx * fx + fy * fy)
    kb = np.round(k * tile).astype(int)       # radial bin = cycles per tile
    nb = tile // 2
    acc = np.zeros(nb)
    cnt = 0
    for y in range(0, h - tile + 1, tile // 2):
        for x in range(0, w - tile + 1, tile // 2):
            if interior[y:y + tile, x:x + tile].mean() < cover:
                continue
            t = lum[y:y + tile, x:x + tile]
            t = (t - t.mean()) * hann
            p = np.abs(np.fft.fft2(t)) ** 2
            for b in range(1, nb):
                acc[b] += p[kb == b].mean()
            cnt += 1
    if cnt == 0:
        return 0, {}, float("nan")
    acc /= cnt
    # cycles per tile b <-> scale tile / b px; bands: 32-64 (b 1-2), 16-32 (3-4), 8-16 (5-8), 4-8 (9-16), 2-4 (17-31)
    bands = {"32-64": (1, 2), "16-32": (3, 4), "8-16": (5, 8), "4-8": (9, 16), "2-4": (17, 31)}
    total = sum(acc[b] for lo, hi in bands.values() for b in range(lo, min(hi, nb - 1) + 1))
    shares = {name: sum(acc[b] for b in range(lo, min(hi, nb - 1) + 1)) / total for name, (lo, hi) in bands.items()}
    bs = np.arange(1, nb)
    keep = acc[1:] > 0
    p = np.polyfit(np.log(bs[keep]), np.log(acc[1:][keep]), 1)
    return cnt, shares, -p[0]


def bumps(m, ok):
    """Protrusions of 5-40 px on the edge of mask m."""
    small = opening(m, 2.5)
    big = opening(m, 20)
    body_edge = edge_pixels(big) & ok
    bump = small & ~big & ok
    lab, n = ndi.label(bump, structure=np.ones((3, 3)))
    if n == 0:
        return {"bodyEdge_px": int(np.count_nonzero(body_edge)), "attached": 0, "detached": 0, "per1000": 0.0, "edgeShare": 0.0}
    touch = ndi.binary_dilation(big, structure=np.ones((3, 3)))
    attached_ids = np.unique(lab[touch & (lab > 0)])
    sizes = ndi.sum(np.ones_like(lab), lab, index=np.arange(1, n + 1))
    keep = [i for i in range(1, n + 1) if sizes[i - 1] >= 20]
    att = [i for i in keep if i in set(attached_ids)]
    det = [i for i in keep if i not in set(attached_ids)]
    edge = edge_pixels(m) & ok
    on = np.isin(lab, att) & edge
    be = int(np.count_nonzero(body_edge))
    return {"bodyEdge_px": be, "attached": len(att), "detached": len(det),
            "per1000": 1000.0 * len(att) / max(be, 1),
            "edgeShare": float(np.count_nonzero(on)) / max(int(np.count_nonzero(edge)), 1)}


def measure(img, base, box=None):
    dist = cloud_distance(img, base)
    ok = valid_mask(dist.shape, box)
    lum = luminance(img)
    res = {"edge": edge_group(dist, ok)}
    m_in = (dist > 0.22) & ok
    interior = ndi.binary_erosion(m_in, iterations=4) & ok
    res["interiorPx"] = int(np.count_nonzero(interior))
    res["cover"] = float(np.count_nonzero(m_in)) / max(int(np.count_nonzero(ok)), 1)
    res["structure"] = structure_function(lum, interior)
    tiles, shares, sl = tile_spectrum(lum, interior)
    res["spectrum"] = {"tiles": tiles, "shares": shares, "slope": sl}
    res["bumps"] = {t: bumps((dist > t) & ok, ok) for t in (0.12, 0.22)}
    # rim width: the area between the 0.12 and the 0.35 contour per length of the 0.22 contour (px; the soft rim of the cloud)
    lo, hi, mid = (dist > 0.12) & ok, (dist > 0.35) & ok, (dist > 0.22) & ok
    res["rimPx"] = float(np.count_nonzero(lo & ~hi)) / max(edge_length(mid, ok), 1)
    return res


def table(label, r):
    lines = ["== %s: cover %.2f, interior %d px, rim %.1f px" % (label, r["cover"], r["interiorPx"], r["rimPx"])]
    for t, row in r["edge"].items():
        if "edge_px" not in row:
            lines.append("  edge t=%.2f: area %d px (too small)" % (t, row["area"]))
            continue
        lines.append("  edge t=%.2f: edge %6d px  D(2-8) %.2f  D(8-64) %.2f  L/L(s4) %.2f  L/L(s8) %.2f  L/L(s16) %.2f" % (
            t, row["edge_px"], row["D_2_8"], row["D_8_64"], row["Lratio_s4"], row["Lratio_s8"], row["Lratio_s16"]))
    lines.append("  S(r)/mean: " + "  ".join("%d:%.3f" % (k, v) for k, v in r["structure"].items()))
    sp = r["spectrum"]
    if sp["tiles"]:
        lines.append("  spectrum: %d tiles, slope %.2f, band shares " % (sp["tiles"], sp["slope"]) + "  ".join("%s:%.3f" % (k, v) for k, v in sp["shares"].items()))
    else:
        lines.append("  spectrum: no tile wholly in the interior")
    for t, b in r["bumps"].items():
        lines.append("  bumps t=%.2f: body edge %d px, attached %d, detached %d, per 1000 px %.1f, edge share %.3f" % (
            t, b["bodyEdge_px"], b["attached"], b["detached"], b["per1000"], b["edgeShare"]))
    return "\n".join(lines)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--pair", nargs=2, required=True)
    p.add_argument("--box")
    p.add_argument("--label", default="picture")
    p.add_argument("--json")
    a = p.parse_args()
    box = tuple(int(v) for v in a.box.split(",")) if a.box else None
    r = measure(load(a.pair[0]), load(a.pair[1]), box)
    print(table(a.label, r))
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(r, f, indent=1, default=str)
    return 0


if __name__ == "__main__":
    sys.exit(main())
