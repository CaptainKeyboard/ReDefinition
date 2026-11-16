"""Scale invariance of a cloud picture over several bands of scale (package CLOUDR1; the owner's target: real clouds are fractal, ours must be so in every band, not only 8-64 px).

Two measures per band of scale in pixels (2-8, 8-32, 32-128, 128-512):
  D   the box-counting dimension of the cloud's outline: the number of s x s boxes that hold an outline pixel, N(s), over the box sizes s of the band (powers of two, the grid
      shifted over four offsets and averaged); D is the slope of log N against log (1 / s). A smooth outline has D = 1, a space-filling one 2; real cloud outlines lie in between.
  S   the slope of the radially averaged power spectrum of the picture (alpha of ours, the blueness distance of a photograph, and the luma) over the wavelengths of the band:
      P(k) ~ k^S, a fractional Brownian surface of Hurst exponent H has S = -(2 H + 2) and an outline of dimension 2 - H.
The measures are made on the frame at its own size (no resize); a band whose largest box does not fit the picture gives None.

Reference: Lovejoy, "Area-perimeter relation for rain and cloud areas", Science 216 (1982) 185-187 (PubMed 17736252): satellite and radar cloud and rain areas of 1 to 1.2 million km2
have a perimeter that follows the square root of the area raised to a power D, the fractal dimension of the perimeter (about 1.35 in the cited literature; Gifford 1989 reanalysed the same
data and finds it rising for the largest clouds): that is a statement about outlines of km to thousands of km, not about the pixel bands of a photograph, so the bands' reference values here
are the real photographs' own (python scaleinv.py real 1080), measured with the same functions at the same px per radian.

    python tools/weather_metrics/scaleinv.py real 1080 1440      the reference ranges of the real photographs at these frame heights (private photos, not in the repository)
    python tools/weather_metrics/scaleinv.py selftest            fractional Brownian test surfaces
"""
import json
import math
import os
import sys

import numpy as np

BANDS = ((2, 8), (8, 32), (32, 128), (128, 512))


def band_name(band):
    return "%d_%d" % band


def outline(mask):
    """The pixels of a boolean mask that touch a pixel of the other value (4-neighbourhood)."""
    m = np.asarray(mask, dtype=bool)
    e = np.zeros_like(m)
    e[:-1, :] |= m[:-1, :] != m[1:, :]
    e[:, :-1] |= m[:, :-1] != m[:, 1:]
    return e


def box_counts(edge, sizes, offsets=4):
    """N(s) for each box size: the boxes (grid shifted over `offsets` offsets, averaged) that hold an outline pixel."""
    h, w = edge.shape
    counts = []
    for s in sizes:
        total, n = 0.0, 0
        for k in range(offsets):
            oy, ox = (k // 2) * (s // 2), (k % 2) * (s // 2)
            sub = edge[oy:, ox:]
            hh, ww = sub.shape[0] // s * s, sub.shape[1] // s * s
            if hh < s or ww < s:
                continue
            blocks = sub[:hh, :ww].reshape(hh // s, s, ww // s, s).any(axis=(1, 3))
            total += float(blocks.sum())
            n += 1
        counts.append(total / n if n else float("nan"))
    return counts


def box_dimensions(mask, bands=BANDS):
    """The box-counting dimension of the mask's outline per band; None where the band has fewer than two sizes that fit or the outline is empty."""
    edge = outline(mask)
    out = {}
    h, w = edge.shape
    for lo, hi in bands:
        sizes = [s for s in (2 ** i for i in range(1, 10)) if lo <= s <= hi and 2 * s <= min(h, w)]
        if len(sizes) < 2 or not edge.any():
            out[band_name((lo, hi))] = None
            continue
        counts = box_counts(edge, sizes)
        if min(counts) <= 4 or any(c != c for c in counts):
            out[band_name((lo, hi))] = None
            continue
        out[band_name((lo, hi))] = float(np.polyfit(np.log(1.0 / np.array(sizes, dtype=float)), np.log(counts), 1)[0])
    return out


def spectrum_slopes(field, bands=BANDS):
    """The slope of the radially averaged power spectrum of a 2D field per band of wavelength (px). The field is windowed (Hann) and cut into square tiles of the largest power of two that fits,
    the spectra of the tiles are averaged; a band whose longest wavelength exceeds the tile gives None."""
    f = np.asarray(field, dtype=np.float64)
    h, w = f.shape
    tile = 2 ** int(math.floor(math.log2(min(h, w))))
    if tile < 16:
        return {band_name(b): None for b in bands}
    win = np.hanning(tile)[:, None] * np.hanning(tile)[None, :]
    power = np.zeros((tile, tile))
    n = 0
    for y in range(0, h - tile + 1, max(tile // 2, 1)):
        for x in range(0, w - tile + 1, max(tile // 2, 1)):
            t = f[y:y + tile, x:x + tile]
            t = (t - t.mean()) * win
            power += np.abs(np.fft.fft2(t)) ** 2
            n += 1
    power /= max(n, 1)
    ky, kx = np.meshgrid(np.fft.fftfreq(tile), np.fft.fftfreq(tile), indexing="ij")
    k = np.hypot(kx, ky) * tile            # cycles per tile
    out = {}
    for lo, hi in bands:
        k_lo, k_hi = tile / hi, tile / lo  # wavelength lo..hi px
        if tile < hi or k_hi < 2:
            out[band_name((lo, hi))] = None
            continue
        ks = np.unique(np.round(np.geomspace(max(k_lo, 1.0), min(k_hi, tile / 2 - 1), 12)))
        p = []
        used = []
        for kk in ks:
            sel = (k >= kk - 0.5) & (k < kk + 0.5)
            if sel.any():
                p.append(power[sel].mean())
                used.append(kk)
        out[band_name((lo, hi))] = float(np.polyfit(np.log(used), np.log(p), 1)[0]) if len(used) >= 3 else None
    return out


def measure(alpha, luma=None, threshold=0.5):
    """The band measures of a picture: alpha is 0..1 opacity (ours) or the blueness distance (a photograph); the outline is that of alpha > threshold."""
    res = {"D": box_dimensions(np.asarray(alpha) > threshold), "S_alpha": spectrum_slopes(alpha)}
    if luma is not None:
        res["S_luma"] = spectrum_slopes(luma)
    return res


def table_lines(label, res, ref=None):
    """One text line per measure: the values per band, and the reference (median, min-max) where given."""
    def fmt(v):
        return "   -  " if v is None else "%6.2f" % v
    lines = []
    for key in ("D", "S_alpha", "S_luma"):
        if key not in res:
            continue
        row = "%-18s %-8s " % (label, key) + " ".join(fmt(res[key].get(band_name(b))) for b in BANDS)
        if ref and key in ref:
            row += "   real " + " ".join("%s" % ("-" if ref[key].get(band_name(b)) is None else "%.2f (%.2f-%.2f)" % tuple(ref[key][band_name(b)][:3])) for b in BANDS)
        lines.append(row)
    return lines


def real_reference(height, root):
    """The ranges of the real photographs at the px per radian of a frame of this height (the same preparation as run_real_size.py); writes results/scaleinv_<height>.json."""
    sys.path.insert(0, os.path.join(root, "scripts"))
    import realref as rr
    rr.PX_PER_RAD = height / (2.0 * math.tan(math.radians(30.0)))
    types = ["humilis", "mediocris", "congestus", "cumulonimbus", "altocumulus", "stratocumulus", "cirrus", "cirrostratus", "stratus"]
    ranges = {}
    for t in types:
        meta = json.load(open(os.path.join(root, t, "meta.json"), encoding="utf-8"))
        rows = []
        for m in meta:
            img = rr.prep_photo(os.path.join(root, t, m["file"]), m["sky_crop_y"], 60)
            dist = rr.dist_of(img)
            lum = rr.puff.luminance(img)
            res = measure(dist, lum, 0.22)
            rows.append(res)
        ranges[t] = {}
        for key in ("D", "S_alpha", "S_luma"):
            ranges[t][key] = {}
            for b in BANDS:
                vals = [r[key].get(band_name(b)) for r in rows if r[key].get(band_name(b)) is not None]
                ranges[t][key][band_name(b)] = [float(np.median(vals)), float(min(vals)), float(max(vals)), len(vals)] if vals else None
        print(height, t, len(rows), flush=True)
    path = os.path.join(root, "results", "scaleinv_%d.json" % height)
    json.dump(dict(height=height, ranges=ranges), open(path, "w"), indent=1)
    return path


def fbm(n, hurst, seed=1):
    """A fractional Brownian surface by spectral synthesis: P(k) ~ k^-(2 H + 2)."""
    rng = np.random.RandomState(seed)
    ky, kx = np.meshgrid(np.fft.fftfreq(n), np.fft.fftfreq(n), indexing="ij")
    k = np.hypot(kx, ky)
    k[0, 0] = 1.0
    amp = k ** (-(hurst + 1.0))
    amp[0, 0] = 0.0
    spec = amp * (rng.normal(size=(n, n)) + 1j * rng.normal(size=(n, n)))
    return np.real(np.fft.ifft2(spec))


def selftest():
    for hurst in (0.3, 0.6, 0.9):
        f = fbm(1024, hurst)
        res = measure((f - f.min()) / (f.max() - f.min()), f, 0.5)
        print("H %.1f (S %.2f, D %.2f nominal)" % (hurst, -(2 * hurst + 2), 2 - hurst))
        print("\n".join(table_lines("fbm", res)))


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "real":
        root = os.path.join(os.environ.get("ATMO_REAL_ROOT", "C:/Users/franz/IdeaProjects/ReDefinition/.local/work/real-clouds"))
        for h in [int(a) for a in sys.argv[2:]] or [1080, 1440]:
            print(real_reference(h, root))
    else:
        selftest()
