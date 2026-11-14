#!/usr/bin/env python3
"""The collector of the start soft60 (K-T2, E40 after start 59; the probe is src/Atmospheres/Light/Cloud/Probe/T09ProbeSoft60.cs, the plan and the pass / miss rules
.local/work/e40-start60-plan-review.md and e40-start60-review.md).

  python tools/weather_metrics/e40s60.py LOG [LOG ...] [--pictures DIR] [--out DIR] [--no-sheets]
  python tools/weather_metrics/e40s60.py --selftest

The runner (tools/atmo_series.py, "metrics" of tests/series/K-T2.json) calls series_results(texts) with the logs of the start; the pictures need the files the runner collects, so they are made
by this script alone, after the run, from the KSP log and the picture folder (.local/work/series-pictures/K-T2).

Tables (the cost, grain, numbers-the-pass-ran-with and sun-colour tables are e40sf.py's, run on this start's rows):
  PLAN     the probe's estimate, the sections run and left out, the type views and the types that were absent
  LIGHT    per view the variant soft's light rows: the sunlit side (P90 of the luminance of the pixels the clouds changed by 0.15 or more), the base (P10), their ratio, the median and the
           baseline's sky at the same pixels (a cumulus body wants a top over base of 2 to 6; a deck is held against the sky behind it)
  TYPES    per scene and target (humilis, congestus, cumulonimbus, cirrus): where the map holds it (km from the site, cover, whether the cell is weak) and, per view, the types under the view's centre
  WEAVE    from the frames (not from the log): the static pattern of the fair ground view per variant. Eight consecutive frames each; ref256 (a march of 256 steps) is the converged picture.
           With L the display luma of a frame, hp(x) = x - gaussian(x, 3 px), the mask the pixels the clouds changed against the baseline frame, and the crop 512 x 256 where about half the
           pixels are cloud (so a rim is in it):   rms1 = rms of hp(L(frame a) - L(ref)) over the mask;  rms8 = the same for the mean of the eight frames;  stat = rms8 / rms1 (1: the same in
           every frame, about 0.35: noise that changes every frame);  peak = the largest power of the crop's 2D spectrum of hp(mean8 - ref) (periods under 32 px, each ring divided by its median)
           over the median power, with its period and angle: a weave is a narrow peak (white noise gives 15 to 20, E1: tools/weather_metrics/e40wv.py);  tstd = the temporal noise over the
           mask. A WEAVE is called where peak is 30 or more AND stat is 0.5 or more.
Pictures (the lead looks at every one; the numbers say what they cannot):
  e40-60-<scene>-<view>-sheet.png        ours: soft | softr33 | softcut16 | soft100s24 (those that ran) and, in the clock scene, TVC's frame of start 47
  e40-60-<view>-vs-tvc.png               the clock views: soft (ours) | TVC side by side at 854 x 480
  e40-60-fair-ground-weave.png           per weave variant: a crop of frame a, of the mean of the eight, and of the static residual (x 10), 2 x, nearest pixel, the same crop for all
  e40-60-types.png                       the type views: ground | above per target, the type under the centre in the caption
  e40-60-extras.png                      horizon, fog and rain: the baseline | the variant soft
Stdlib for the tables; the pictures and the weave measure need numpy and Pillow.
"""
import argparse
import importlib.util
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
START = "soft60"
RES = "1707x960"
FRAME_LETTERS = "abcdefgh"
WEAVE_ORDER = ("soft", "softhash", "softacc10", "softacc05", "softr33", "ref256")
WEAVE_REFERENCE = "ref256"
SHEET_ORDER = ("soft", "softr33", "softcut16", "soft100s24")
TYPE_ORDER = ("humilis", "congestus", "cumulonimbus", "cirrus")
EXTRAS = (("horizon", "fair"), ("fog", "fair"), ("rain", "clock"))
SIGMA_HP = 3.0
CROP = (512, 256)
# A weave: a narrow spectral peak that the mean of eight frames keeps (E1 measured white noise at 15 to 20).
WEAVE_PEAK, WEAVE_STAT = 30.0, 0.5
NOTES = []


_SF = []


def load_sf():
    """e40sf.py by its path (the runner loads metric scripts by path, so no package import); loaded once, so that its NOTES are the ones this script reads."""
    if not _SF:
        spec = importlib.util.spec_from_file_location("atmo_e40sf_for_s60", os.path.join(HERE, "e40sf.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        _SF.append(module)
    return _SF[0]


# --- The tables from the log ----------------------------------------------------------------------------------------------------------------


def scene_rows(rows, scene, view, variant="soft"):
    prefix = "T09s.%s.%s.%s." % (scene, view, variant)
    return {k[len(prefix):]: v for k, v in rows.items() if k.startswith(prefix)}


def light_views(rows):
    """[(scene, view)] that have light rows, in the order the probe wrote them."""
    out = []
    for metric in rows:
        parts = metric.split(".")
        if len(parts) == 5 and parts[0] == "T09s" and parts[3] == "soft" and parts[4] == "light_top" and (parts[1], parts[2]) not in out:
            out.append((parts[1], parts[2]))
    return out


def light_table(rows, num, fmt):
    lines = ["LIGHT (the variant soft; luma of the pixels the clouds changed by 0.15 or more; top = P90, base = P10; a cumulus body wants top over base 2 to 6)",
             "  %-6s %-22s %7s %7s %7s %8s %7s %7s %9s  %s" % ("scene", "view", "cloud %", "top", "base", "top/base", "median", "sky", "deck/sky", "cumulus 2 to 6")]
    metrics = []
    for scene, view in light_views(rows):
        r = scene_rows(rows, scene, view)
        top, base, ratio = num(r.get("light_top")), num(r.get("light_base")), num(r.get("light_top_over_base"))
        cumulus = view.startswith("ty_humilis") or view.startswith("ty_congestus") or (scene == "fair" and view in ("ground", "above"))
        verdict = "-" if not cumulus or ratio is None else ("yes" if 2.0 <= ratio <= 6.0 else "no (%.2f)" % ratio)
        share = num(r.get("light_cloud_share"))
        lines.append("  %-6s %-22s %7s %7s %7s %8s %7s %7s %9s  %s" % (scene, view, fmt(None if share is None else 100.0 * share, 1), fmt(top, 3), fmt(base, 3), fmt(ratio, 2),
                                                                    fmt(num(r.get("light_median")), 3), fmt(num(r.get("light_sky")), 3), fmt(num(r.get("light_deck_over_sky")), 2), verdict))
        for name, value in (("top", top), ("base", base), ("top_over_base", ratio), ("deck_over_sky", num(r.get("light_deck_over_sky")))):
            if value is not None:
                metrics.append({"metric": "T09s.light.%s.%s.%s" % (scene, view, name), "value": round(value, 4), "variant": None})
    if len(lines) == 2:
        lines.append("  (no light rows)")
    return lines, metrics


def types_table(rows, num, fmt):
    lines = ["TYPES (where the layers' map holds the type, and what stands under the view's centre; a type that is absent within 300 km is logged, not a failure)",
             "  %-6s %-13s %-6s %8s %8s %6s  %s" % ("scene", "target", "found", "km", "cover", "weak", "type")]
    for scene in ("fair", "clock"):
        for target in TYPE_ORDER:
            p = "T09s.types.%s.%s." % (scene, target)
            if p + "found" not in rows:
                continue
            found = rows.get(p + "found")
            lines.append("  %-6s %-13s %-6s %8s %8s %6s  %s" % (scene, target, "yes" if found else "no", fmt(num(rows.get(p + "dist_km")), 0), fmt(num(rows.get(p + "cover")), 2),
                                                             rows.get(p + "weak", "-"), rows.get(p + "type", "")))
    lines.append("  views (the vessel's distance from the site, the cell's from the site, the type under the centre):")
    for scene in ("fair", "clock"):
        for target in TYPE_ORDER:
            for up in ("g", "a"):
                view = "ty_%s_%s" % (target, up)
                p = "T09s.%s.%s." % (scene, view)
                if p + "placed_ok" not in rows:
                    continue
                lines.append("    %-6s %-18s placed %-5s moved %6s km  cell %5s km  centre %s  %s" % (scene, view, rows.get(p + "placed_ok"), fmt(num(rows.get(p + "vessel_moved_km")), 1),
                                                                                                fmt(num(rows.get(p + "cell_dist_km")), 0), "match" if rows.get(p + "centre_match") else "NOT the target",
                                                                                                rows.get(p + "centre_types", "")))
    return lines


def plan_table(rows, num, fmt):
    return ["PLAN  estimate %s s, sections %s run %s left out, type views %s, types absent %s, cost windows %s, frames read %s, pictures %s" % (
        fmt(num(rows.get("T09s.plan_estimate_s")), 0), fmt(num(rows.get("T09s.sections")), 0), fmt(num(rows.get("T09s.sections_skipped")), 0), fmt(num(rows.get("T09s.type_views")), 0),
        fmt(num(rows.get("T09s.types_absent")), 0), fmt(num(rows.get("T09s.cost_windows")), 0), fmt(num(rows.get("T09s.frames_read")), 0), fmt(num(rows.get("T09.pictures.saved")), 0))]


def tables(texts):
    """The text tables of the logs of the start and the (metric, value) lines for the runner."""
    sf = load_sf()
    lines, metrics = sf.tables(texts)
    rows = sf.parse_rows(texts)
    out = plan_table(rows, sf.num, sf.fmt) + lines
    light, light_metrics = light_table(rows, sf.num, sf.fmt)
    out += light + types_table(rows, sf.num, sf.fmt)
    return out, metrics + light_metrics


def series_results(texts):
    """What the runner calls: {start id: log text}; only the logs of this start are read. Never raises: a log that cannot be read gives a note."""
    del NOTES[:]
    mine = [t for k, t in texts.items() if str(k).startswith(START)]
    if not mine:
        return []
    sf = load_sf()
    del sf.NOTES[:]
    try:
        out_lines, metrics = tables(mine)
    except Exception as e:  # a script that raises fails the run: the numbers are a report, not a verdict
        NOTES.append("e40s60: %s: %s" % (type(e).__name__, e))
        return []
    NOTES.extend(out_lines)
    NOTES.extend(sf.NOTES)
    return metrics


# --- The pictures and the weave measure -----------------------------------------------------------------------------------------------------

PICTURE = re.compile(r"^t09-%s-(?P<view>[^-]+)-(?P<var>[^-]+)_(?P<l>[a-h])-(?P<w>\d+)x(?P<h>\d+)-natural\.png$" % START)


def index_pictures(folder):
    """{(scene, view): {variant: {letter: path}}}; the baseline is the variant "off" with the letter a; a view name ending in _fair is the fair scene's."""
    found = {}
    if not os.path.isdir(folder):
        return found
    for name in sorted(os.listdir(folder)):
        m = PICTURE.match(name)
        if not m:
            continue
        view = m.group("view")
        scene = "fair" if view.endswith("_fair") else "clock"
        if scene == "fair":
            view = view[:-len("_fair")]
        found.setdefault((scene, view), {}).setdefault(m.group("var"), {})[m.group("l")] = os.path.join(folder, name)
    return found


def luma(rgb):
    return 0.2126 * rgb[..., 0] + 0.7152 * rgb[..., 1] + 0.0722 * rgb[..., 2]


def gaussian(x, sigma=SIGMA_HP):
    """A separable gaussian blur with the edge value repeated (numpy only)."""
    import numpy as np
    radius = int(math.ceil(3.0 * sigma))
    k = np.exp(-0.5 * (np.arange(-radius, radius + 1) / sigma) ** 2)
    k /= k.sum()
    out = x
    for axis in (0, 1):
        pad = [(0, 0), (0, 0)]
        pad[axis] = (radius, radius)
        p = np.pad(out, pad, mode="edge")
        acc = np.zeros_like(out)
        n = out.shape[axis]
        for i, w in enumerate(k):
            sl = [slice(None), slice(None)]
            sl[axis] = slice(i, i + n)
            acc += w * p[tuple(sl)]
        out = acc
    return out


def hp(x):
    return x - gaussian(x)


def spectrum_peak(img, min_period=32.0):
    """(peak over median power, period in px, angle in degrees) of the 2D spectrum of a crop (Hann window) over the frequencies with a period under min_period; the measure of
    tools/weather_metrics/e40wv.py (E1): every bin is divided by the median power of its ring of equal radius, so that a field with no preferred direction scores about the same everywhere."""
    import numpy as np
    h, w = img.shape
    win = np.outer(np.hanning(h), np.hanning(w))
    f = np.fft.fftshift(np.fft.fft2(img * win))
    p = np.abs(f) ** 2
    fy = np.fft.fftshift(np.fft.fftfreq(h))[:, None]
    fx = np.fft.fftshift(np.fft.fftfreq(w))[None, :]
    radius = np.hypot(fx, fy)
    use = radius > 1.0 / min_period
    if not use.any():
        return float("nan"), float("nan"), float("nan")
    edges = np.linspace(1.0 / min_period, 0.5 * math.sqrt(2.0), 49)
    ring = np.clip(np.digitize(radius, edges) - 1, 0, 47)
    med = np.ones(48)
    for k in range(48):
        sel = use & (ring == k)
        if sel.any():
            med[k] = max(float(np.median(p[sel])), 1e-300)
    norm = np.where(use, p / med[ring], 0.0)
    iy, ix = np.unravel_index(np.argmax(norm), p.shape)
    period = 1.0 / max(radius[iy, ix], 1e-9)
    angle = math.degrees(math.atan2(fy[iy, 0], fx[0, ix])) % 180.0
    return float(norm[iy, ix]), float(period), float(angle)


def load_rgb(path):
    import numpy as np
    from PIL import Image
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255.0


def body_crop(soft, off, size=CROP, body=(0.3, 0.85)):
    """The corner of the crop whose share of cloud pixels (changed against the baseline by 0.05 or more) is in the body band and highest: a rim and a body in one window. The same
    rule as e40sf.dither_crop (start 59's crops showed the sea because they were a fixed corner). Falls back to the most cloudy window."""
    import numpy as np
    h, w = soft.shape[:2]
    cw, ch = min(size[0], w), min(size[1], h)
    changed = (np.abs(soft - off).max(axis=2) >= 0.05).astype(np.float64)
    integral = np.pad(changed, ((1, 0), (1, 0))).cumsum(axis=0).cumsum(axis=1)
    best, best_full, at, at_full = -1.0, -1.0, (0, 0), (0, 0)
    for y in range(0, h - ch + 1, 20):
        for x in range(0, w - cw + 1, 20):
            share = float(integral[y + ch, x + cw] - integral[y, x + cw] - integral[y + ch, x] + integral[y, x]) / (cw * ch)
            if body[0] <= share <= body[1] and share > best:
                best, at = share, (x, y)
            if share > best_full:
                best_full, at_full = share, (x, y)
    x, y = at if best >= 0.0 else at_full
    return x, y, cw, ch


def weave_stats(frames, ref, changed, crop, default_mean=None):
    """frames: the variant's eight luma images, ref the converged luma, changed the cloud mask (bool), crop (x, y, w, h), default_mean the mean of the default variant's eight frames
    (d_soft: the rms of hp(mean8 - default_mean), the pairwise difference of E1, which does not contain the march's own bias that every variant shares). Returns the dict of the module docstring."""
    import numpy as np
    x, y, w, h = crop
    m = 2 * int(math.ceil(3.0 * SIGMA_HP))
    y0, y1, x0, x1 = max(0, y - m), min(ref.shape[0], y + h + m), max(0, x - m), min(ref.shape[1], x + w + m)
    inner = (slice(y - y0, y - y0 + h), slice(x - x0, x - x0 + w))
    mean = np.mean(np.stack(frames), axis=0)
    r1 = hp((frames[0] - ref)[y0:y1, x0:x1])[inner]
    r8 = hp((mean - ref)[y0:y1, x0:x1])[inner]
    mask = changed[y:y + h, x:x + w]
    if mask.sum() < 100:
        return dict(rms1=float("nan"), rms8=float("nan"), stat=float("nan"), peak=float("nan"), peak_period=float("nan"), peak_angle=float("nan"), tstd=float("nan"), d_soft=float("nan"), mask_share=0.0)
    rms1 = float(np.sqrt((r1[mask] ** 2).mean()))
    rms8 = float(np.sqrt((r8[mask] ** 2).mean()))
    peak, period, angle = spectrum_peak(r8)
    tstd = float(np.std(np.stack([f[y:y + h, x:x + w] for f in frames]), axis=0)[mask].mean())
    d_soft = float("nan")
    if default_mean is not None:
        d = hp((mean - default_mean)[y0:y1, x0:x1])[inner]
        d_soft = float(np.sqrt((d[mask] ** 2).mean()))
    return dict(rms1=rms1, rms8=rms8, stat=rms8 / rms1 if rms1 > 0 else float("nan"), peak=peak, peak_period=period, peak_angle=angle, tstd=tstd, d_soft=d_soft, mask_share=float(mask.mean()))


def is_weave(s):
    return s["peak"] == s["peak"] and s["stat"] == s["stat"] and s["peak"] >= WEAVE_PEAK and s["stat"] >= WEAVE_STAT


def weave_table(index):
    """(lines, metrics, data) from the fair ground frames; data is what the weave sheet draws, or None where the frames are missing."""
    variants = index.get(("fair", "ground"))
    if not variants or "off" not in variants or "soft" not in variants or WEAVE_REFERENCE not in variants:
        return ["WEAVE (no frames: the fair ground view needs the baseline, soft and %s)" % WEAVE_REFERENCE], [], None
    import numpy as np
    for name in (WEAVE_REFERENCE,):
        if not all(l in variants[name] for l in FRAME_LETTERS):
            return ["WEAVE (%s has fewer than eight frames)" % name], [], None
    soft_rgb, off_rgb = load_rgb(variants["soft"]["a"]), load_rgb(variants["off"]["a"])
    crop = body_crop(soft_rgb, off_rgb)
    changed = np.abs(soft_rgb - off_rgb).max(axis=2) >= 0.05
    ref = np.mean(np.stack([luma(load_rgb(variants[WEAVE_REFERENCE][l])) for l in FRAME_LETTERS]), axis=0)
    lines = ["WEAVE (fair ground view; crop %d,%d %dx%d; reference = the mean of the eight frames of %s; a weave: peak >= %.0f and stat >= %.1f; d_soft = rms of hp(mean8 - the default's mean8))"
             % (crop + (WEAVE_REFERENCE, WEAVE_PEAK, WEAVE_STAT)),
             "  %-11s %8s %8s %6s %7s %8s %6s %8s %8s  %s" % ("variant", "rms1", "rms8", "stat", "peak", "period", "angle", "tstd", "d_soft", "weave")]
    metrics, data = [], {"crop": crop, "ref": ref, "changed": changed, "variants": {}}
    default_mean = None
    if all(l in variants["soft"] for l in FRAME_LETTERS):
        default_mean = np.mean(np.stack([luma(load_rgb(variants["soft"][l])) for l in FRAME_LETTERS]), axis=0)
    for name in WEAVE_ORDER:
        v = variants.get(name)
        if name == WEAVE_REFERENCE or not v or not all(l in v for l in FRAME_LETTERS):
            continue
        frames = [luma(load_rgb(v[l])) for l in FRAME_LETTERS]
        s = weave_stats(frames, ref, changed, crop, default_mean)
        data["variants"][name] = (frames, s)
        lines.append("  %-11s %8.4f %8.4f %6.2f %7.1f %8.1f %6.0f %8.4f %8.4f  %s" % (name, s["rms1"], s["rms8"], s["stat"], s["peak"], s["peak_period"], s["peak_angle"], s["tstd"], s["d_soft"],
                                                                                   "WEAVE" if is_weave(s) else "no"))
        for key in ("rms1", "rms8", "stat", "peak", "peak_period", "peak_angle", "tstd", "d_soft"):
            if s[key] == s[key]:
                metrics.append({"metric": "T09s.weave.%s.%s" % (name, key), "value": round(s[key], 5), "variant": None})
    if len(data["variants"]) == 0:
        lines.append("  (no variant has eight frames)")
    return lines, metrics, data


def _canvas(Image, ImageDraw, font, arr, caption, bar=18):
    import numpy as np
    img = Image.fromarray(np.clip(np.rint(arr * 255.0), 0, 255).astype(np.uint8), "RGB")
    canvas = Image.new("RGB", (img.width, img.height + bar), (16, 16, 16))
    canvas.paste(img, (0, bar))
    ImageDraw.Draw(canvas).text((4, 3), caption, fill=(235, 235, 235), font=font)
    return canvas


def _fit(Image, path, size):
    return Image.open(path).convert("RGB").resize(size, Image.LANCZOS)


def _grid(Image, tiles, cols, bg=(0, 0, 0)):
    if not tiles:
        return None
    tw, th = tiles[0].size
    rows = (len(tiles) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * tw, rows * th), bg)
    for i, t in enumerate(tiles):
        sheet.paste(t, ((i % cols) * tw, (i // cols) * th))
    return sheet


def make_weave_sheet(data, out_dir, Image, ImageDraw, font):
    """Per variant three stacked tiles of the same 320 x 180 crop, 2 x, nearest pixel: the frame a, the mean of the eight, the static residual hp(mean - ref) x 10 (grey 0.5 = zero)."""
    import numpy as np
    x, y, w, h = data["crop"]
    cw, ch = min(320, w), min(180, h)
    x += (w - cw) // 2
    y += (h - ch) // 2
    cols = []
    for name, (frames, s) in data["variants"].items():
        mean = np.mean(np.stack(frames), axis=0)
        residual = hp(mean - data["ref"])
        tiles = []
        for caption, plane in (("%s   frame a" % name, frames[0]), ("mean of 8   stat %.2f  peak %.0f%s" % (s["stat"], s["peak"], "  WEAVE" if is_weave(s) else ""), mean),
                               ("static residual x 10   (rms8 %.4f)" % s["rms8"], np.clip(0.5 + 10.0 * residual, 0.0, 1.0))):
            crop = plane[y:y + ch, x:x + cw]
            big = np.repeat(np.repeat(crop, 2, axis=0), 2, axis=1)
            tiles.append(np.asarray(_canvas(Image, ImageDraw, font, np.repeat(big[..., None], 3, axis=2), caption)))
        cols.append(np.concatenate(tiles, axis=0))
    if not cols:
        return None
    path = os.path.join(out_dir, "e40-60-fair-ground-weave.png")
    Image.fromarray(np.concatenate(cols, axis=1)).save(path)
    return path


def make_sheets(pictures, out_dir, tvc_folders=()):
    """The sheets and pairs of the module docstring; returns the written paths."""
    import numpy as np
    from PIL import Image, ImageDraw, ImageFont
    sf = load_sf()
    font = ImageFont.load_default()
    tvc = sf.tvc_pictures([pictures] + list(tvc_folders))
    index = index_pictures(pictures)
    os.makedirs(out_dir, exist_ok=True)
    written = []
    tile_w, tile_h, bar = 640, 360, 18

    def tile(path, caption, size=(tile_w, tile_h)):
        img = _fit(Image, path, size)
        canvas = Image.new("RGB", (size[0], size[1] + bar), (16, 16, 16))
        canvas.paste(img, (0, bar))
        ImageDraw.Draw(canvas).text((4, 3), caption, fill=(235, 235, 235), font=font)
        return canvas

    # The views the table runs: ours in every variant that ran, then TVC (the clock scene).
    for (scene, view), variants in sorted(index.items()):
        if view.startswith("ty_") or view in ("horizon", "fog", "rain"):
            continue
        tiles = [tile(variants[v]["a"], v) for v in SHEET_ORDER if v in variants and "a" in variants[v]]
        if scene == "clock" and view in tvc:
            tiles.append(tile(tvc[view], "TVC (start 47)"))
        sheet = _grid(Image, tiles, 3)
        if sheet is not None:
            path = os.path.join(out_dir, "e40-60-%s-%s-sheet.png" % (scene, view))
            sheet.save(path)
            written.append(path)
        if scene == "clock" and view in tvc and "soft" in variants and "a" in variants["soft"]:
            pair = _grid(Image, [tile(variants["soft"]["a"], "ours: soft (%s)" % view, (854, 480)), tile(tvc[view], "TVC (start 47: its own sky at that clock)", (854, 480))], 2)
            path = os.path.join(out_dir, "e40-60-%s-vs-tvc.png" % view)
            pair.save(path)
            written.append(path)

    # The weave.
    _, _, data = weave_table(index)
    if data is not None:
        path = make_weave_sheet(data, out_dir, Image, ImageDraw, font)
        if path:
            written.append(path)

    # The type views: ground | above per target (the fair scene's and the clock scene's together; a target is in one scene).
    tiles = []
    for target in TYPE_ORDER:
        for up in ("g", "a"):
            view = "ty_%s_%s" % (target, up)
            for scene in ("fair", "clock"):
                v = index.get((scene, view), {}).get("soft", {})
                if "a" in v:
                    tiles.append(tile(v["a"], "%s %s (%s)" % (target, "from the ground" if up == "g" else "from above", scene)))
                    break
            else:
                tiles.append(Image.new("RGB", (tile_w, tile_h + bar), (40, 0, 0)))
                ImageDraw.Draw(tiles[-1]).text((4, 3), "%s %s: no view (the type was absent or the view left out)" % (target, "ground" if up == "g" else "above"), fill=(235, 235, 235), font=font)
    sheet = _grid(Image, tiles, 2)
    if sheet is not None:
        path = os.path.join(out_dir, "e40-60-types.png")
        sheet.save(path)
        written.append(path)

    # The extra looks: the baseline beside the variant soft.
    tiles = []
    for view, scene in EXTRAS:
        v = index.get((scene, view))
        if not v or "off" not in v or "soft" not in v or "a" not in v["soft"] or "a" not in v["off"]:
            continue
        tiles.append(tile(v["off"]["a"], "%s: baseline (no layer drawn)" % view))
        tiles.append(tile(v["soft"]["a"], "%s: ours (soft)" % view))
    sheet = _grid(Image, tiles, 2)
    if sheet is not None:
        path = os.path.join(out_dir, "e40-60-extras.png")
        sheet.save(path)
        written.append(path)
    return written


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="*")
    ap.add_argument("--pictures", help="the folder of the start's pictures (default: .local/work/series-pictures/K-T2 of the worktree, else of the main repo)")
    ap.add_argument("--out", help="where the pictures go (default: .local/work/series-pictures/E40 of the main repo)")
    ap.add_argument("--no-sheets", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        selftest()
        return 0
    if not a.logs:
        ap.error("a log is needed (or --selftest)")
    sf = load_sf()
    texts = []
    for path in a.logs:
        with open(path, encoding="utf-8", errors="replace") as f:
            texts.append(f.read())
    del sf.NOTES[:]
    out_lines, _metrics = tables(texts)
    print("\n".join(out_lines))
    for n in sf.NOTES:
        print("note: " + n)
    if a.no_sheets:
        return 0
    root = os.path.dirname(os.path.dirname(HERE))
    folder = a.pictures
    if not folder:
        for candidate in (os.path.join(root, ".local", "work", "series-pictures", "K-T2"), os.path.join(sf.main_repo(), ".local", "work", "series-pictures", "K-T2")):
            if os.path.isdir(candidate) and index_pictures(candidate):
                folder = candidate
                break
    if not folder:
        print("no picture folder with the frames of %s found" % START)
        return 0
    index = index_pictures(folder)
    lines, _wm, _data = weave_table(index)
    print("\n".join(lines))
    out = a.out or os.path.join(sf.main_repo(), ".local", "work", "series-pictures", "E40")
    for path in make_sheets(folder, out, [os.path.join(sf.main_repo(), ".local", "work", "series-pictures", "K-T2")]):
        print("wrote " + path)
    return 0


# --- Selftest --------------------------------------------------------------------------------------------------------------------------------


def _row(metric, value):
    return 'ATMORESULT {"series":"K-T2","metric":"%s","value":%s}' % (metric, json.dumps(value))


def selftest():
    import tempfile
    import numpy as np
    from PIL import Image
    # --- the log tables
    log = []
    p = "T09s.fair.ground.soft."
    for name, value in (("light_cloud_share", 0.6), ("light_top", 0.8), ("light_base", 0.2), ("light_median", 0.5), ("light_sky", 0.4), ("light_top_over_base", 4.0), ("light_deck_over_sky", 1.25)):
        log.append(_row(p + name, value))
    p = "T09s.fair.ty_humilis_g.soft."
    for name, value in (("light_cloud_share", 0.5), ("light_top", 0.9), ("light_base", 0.05), ("light_median", 0.4), ("light_sky", 0.4), ("light_top_over_base", 18.0), ("light_deck_over_sky", 1.0)):
        log.append(_row(p + name, value))
    log += [_row("T09s.types.fair.humilis.found", True), _row("T09s.types.fair.humilis.dist_km", 32.0), _row("T09s.types.fair.humilis.cover", 0.8), _row("T09s.types.fair.humilis.weak", False),
            _row("T09s.types.fair.humilis.type", "Cumulus humilis"), _row("T09s.types.fair.cirrus.found", False),
            _row("T09s.fair.ty_humilis_g.placed_ok", True), _row("T09s.fair.ty_humilis_g.vessel_moved_km", 26.2), _row("T09s.fair.ty_humilis_g.cell_dist_km", 32.0),
            _row("T09s.fair.ty_humilis_g.centre_match", True), _row("T09s.fair.ty_humilis_g.centre_types", "Kerbin-cumulus Cumulus humilis cover 0.80"),
            _row("T09s.plan_estimate_s", 1003.0), _row("T09s.sections", 14), _row("T09s.sections_skipped", 0), _row("T09s.type_views", 8), _row("T09s.types_absent", 0)]
    text = "\n".join(log)
    out_lines, metrics = tables([text])
    joined = "\n".join(out_lines)
    got = {m["metric"]: m["value"] for m in metrics}
    assert got["T09s.light.fair.ground.top_over_base"] == 4.0, got
    assert "yes" in [l for l in out_lines if " ground " in l and "0.800" in l][0], joined
    assert "no (18.00)" in joined, joined
    assert "humilis" in joined and "cirrus" in joined and "NOT" not in joined and "match" in joined, joined
    assert "estimate 1003 s" in joined and "type views 8" in joined, joined
    assert series_results({"soft60": text}) and series_results({"soft59": text}) == [], "only the logs of this start"
    # --- the weave measure: a static diagonal pattern against noise, from synthetic eight-frame sets
    rng = np.random.RandomState(3)
    h, w = 960, 1707
    yy, xx = np.mgrid[0:h, 0:w]
    base = 0.55 + 0.25 * np.sin(xx / 170.0) * np.cos(yy / 130.0)
    off = np.full((h, w), 0.4)
    weave = 0.03 * np.sin(2 * math.pi * (xx * 0.11 + yy * 0.07))

    def rgb(plane):
        return np.repeat(np.clip(plane, 0, 1)[..., None], 3, axis=2)

    with tempfile.TemporaryDirectory() as tmp:
        pics = os.path.join(tmp, "pics")
        os.makedirs(pics)

        def save(view, variant, letter, plane):
            Image.fromarray((rgb(plane) * 255).astype(np.uint8)).save(os.path.join(pics, "t09-%s-%s-%s_%s-1707x960-natural.png" % (START, view, variant, letter)))

        save("ground_fair", "off", "a", off)
        for letter in FRAME_LETTERS:
            save("ground_fair", "ref256", letter, base + rng.normal(0, 0.002, base.shape))
            save("ground_fair", "soft", letter, base + weave + rng.normal(0, 0.012, base.shape))
            save("ground_fair", "softhash", letter, base + rng.normal(0, 0.012, base.shape))
        index = index_pictures(pics)
        assert ("fair", "ground") in index and set(index[("fair", "ground")]) == {"off", "ref256", "soft", "softhash"}, index
        lines, wm, data = weave_table(index)
        stats = {n: v[1] for n, v in data["variants"].items()}
        assert is_weave(stats["soft"]) and not is_weave(stats["softhash"]), (stats, "\n".join(lines))
        assert stats["soft"]["stat"] > stats["softhash"]["stat"] and stats["soft"]["peak"] > 2.0 * stats["softhash"]["peak"], stats
        assert abs(stats["soft"]["peak_period"] - 1.0 / math.hypot(0.11, 0.07)) < 1.5, stats["soft"]
        assert stats["soft"]["d_soft"] < 1e-9 and stats["softhash"]["d_soft"] > 0.01, "d_soft is the difference of the means: nothing for the default itself, the weave for the hash"
        assert "WEAVE" in "\n".join(lines) and any(m["metric"] == "T09s.weave.soft.peak" for m in wm), lines
        # The crop sits on cloud: the changed share in the band, not the corner that would be sea.
        soft_rgb, off_rgb = rgb(base), rgb(off)
        x, y, cw, ch = body_crop(soft_rgb, off_rgb)
        assert (cw, ch) == CROP and float((np.abs(soft_rgb - off_rgb).max(axis=2)[y:y + ch, x:x + cw] >= 0.05).mean()) > 0.3
        # A body on a sky: the crop is on the body's rim.
        body = np.full((h, w), 0.4)
        body[300:700, 600:1100] = 0.8
        x, y, cw, ch = body_crop(rgb(body), rgb(off))
        share = float((np.abs(rgb(body) - rgb(off)).max(axis=2)[y:y + ch, x:x + cw] >= 0.05).mean())
        assert 0.3 <= share <= 0.85, (x, y, share)
        # --- the sheets
        for view in ("ground", "above"):
            for variant in ("soft", "softr33", "softcut16"):
                save(view, variant, "a", base)
        save("ground", "off", "a", off)
        for target in TYPE_ORDER[:2]:
            for up in ("g", "a"):
                save("ty_%s_%s_fair" % (target, up), "soft", "a", base)
        save("horizon_fair", "off", "a", off)
        save("horizon_fair", "soft", "a", base)
        Image.fromarray((rgb(base) * 255).astype(np.uint8)).save(os.path.join(pics, "t09-tvc1707-ground-tvc-1707x960-natural.png"))
        out = os.path.join(tmp, "out")
        written = sorted(os.path.basename(q) for q in make_sheets(pics, out))
        expected = ["e40-60-clock-above-sheet.png", "e40-60-clock-ground-sheet.png", "e40-60-extras.png", "e40-60-fair-ground-sheet.png", "e40-60-fair-ground-weave.png", "e40-60-ground-vs-tvc.png", "e40-60-types.png"]
        assert written == expected, written
        with Image.open(os.path.join(out, "e40-60-ground-vs-tvc.png")) as pair:
            assert pair.size == (2 * 854, 480 + 18), pair.size
        with Image.open(os.path.join(out, "e40-60-fair-ground-weave.png")) as sheet:
            assert sheet.size == (2 * 640, 3 * (360 + 18)), sheet.size          # two columns (soft, softhash; the reference is none), three tiles each: frame, mean, residual
    print("selftest passed")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
