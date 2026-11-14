#!/usr/bin/env python3
"""The collector of the start soft59 (K-T2, E40 soft density; the probe is src/Atmospheres/Light/Cloud/Probe/T09ProbeSoft.cs, the review .local/work/e40-soft-start59-review.md).

  python tools/weather_metrics/e40sf.py LOG [LOG ...] [--pictures DIR] [--out DIR] [--no-sheets]
  python tools/weather_metrics/e40sf.py --selftest

The runner (tools/atmo_series.py, "metrics" of tests/series/K-T2.json) calls series_results(texts) with the logs of the start and prints the tables as notes; the pictures need the
files the runner collects, so they are made by this script alone, after the run, from the KSP log (or the runner's collected lines) and the picture folder.

What it reads:
  rows "T09s.<scene>.<view>.<variant>.<row>" (ATMORESULT lines of series K-T2): the grain of the three frames per variant and view, the numbers the pass ran with, the cost window's frames;
  "T09 view begin / end: <view> <tag> <WxH>" and the GPU profiler's reports between them (kt2.py's parser): tag fw_<variant> in the scene fair, k_<variant> in the scene clock;
  "T09 sun colour (...)" and the rows T09s.sun.<scene>.*.
What it makes:
  cost      per scene, view and variant: the GPU's busy time minus the busy time of the baseline window (the pass running, no layer drawn; kt2.py's busy_ms), and the pass's own label
            ("ReDefinition clouds" group); in the scene clock against TVC's four figures (busy-delta of start 47: ground 2.59, inside 4.77, above 5.30, sky3 2.58 ms at 1707 x 960):
            "fits" where the busy delta is at most TVC's, else "does not fit". The scene fair has no TVC figure of its own: it is held against the same numbers for scale only.
  grain     per scene, view and variant: temporal noise of a cloud pixel (tstd, over the pixels the clouds changed against the baseline frame), spatial grain hfx, the box measure of
            start 56 (box_tstd, box_blob), and each variant's tstd over the first variant's and over "hard"'s.
            The verdict is "fits" / "does not fit" / "undecidable (within 0.5 ms)": the run-to-run floor of the busy delta is about 0.5 ms (start 56), and TVC's figures were taken against
            a pass-off baseline while ours leave out the two blits of the pass (0.1 to 0.3 ms kinder to us), so a delta within 0.5 ms of TVC's figure decides nothing.
            The column "/ soft" is the busy delta over the variant soft's in the same view (the march target and the cut span are read as these ratios).
  sheets    e40-59-<view>-sheet.png (clock) and e40-59-fair-<view>-sheet.png (fair): the first frame of soft | hard | soft100s24 | softr33 | softcut16 side by side (the variants that
            ran), plus TVC's frame of start 47 in the clock scene; e40-59-<view>-strip.png / e40-59-fair-<view>-strip.png: frames a | b | c and the temporal noise (x 8) of a crop, one row
            per variant; e40-59-fair-<view>-dither.png: soft | softhash, a full-size crop of a cloud body enlarged 2x, for the owner's dither decision (the weave against the speckle).
Stdlib for the tables; the sheets need numpy and Pillow.
"""
import argparse
import importlib.util
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROW = re.compile(r'ATMORESULT\s*(\{.*\})')
TVC_MS = {"ground": 2.59, "inside": 4.77, "above": 5.30, "sky3": 2.58}
SCENES = (("clock", "k_"), ("fair", "fw_"))
VIEW_ORDER = ("ground", "above", "inside", "sky3")
NOTES = []
RES = "1707x960"
START = "soft59"
# A delta this close to TVC's figure decides nothing: the run-to-run floor is about 0.5 ms (start 56).
UNDECIDABLE_MS = 0.5
# The tiles of a view's sheet, in this order (the variants that ran), and the pair of the dither sheet.
SHEET_ORDER = ("soft", "hard", "soft100s24", "softr33", "softcut16")
DITHER_PAIR = ("soft", "softhash")


def load_kt2():
    """kt2.py by its path, as the runner loads a metric script and as kt2d.py does."""
    spec = importlib.util.spec_from_file_location("atmo_kt2", os.path.join(HERE, "kt2.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def parse_rows(texts):
    """{metric: value} of the series K-T2 rows of the logs; the last line of a metric wins."""
    rows = {}
    for text in texts:
        for line in text.splitlines():
            m = ROW.search(line)
            if not m:
                continue
            try:
                obj = json.loads(m.group(1))
            except ValueError:
                continue
            if obj.get("series") != "K-T2" or not isinstance(obj.get("metric"), str):
                continue
            rows[obj["metric"]] = obj.get("value")
    return rows


def variants_of(rows, scene, view):
    """The variants that have grain or frame rows in a scene's view, in the order the probe wrote them (a dict keeps it)."""
    out = []
    prefix = "T09s.%s.%s." % (scene, view)
    for metric in rows:
        if not metric.startswith(prefix):
            continue
        parts = metric[len(prefix):].split(".")
        if len(parts) == 2 and parts[0] not in out and parts[0] != "off":
            out.append(parts[0])
    return out


def views_of(rows, scene):
    seen = set()
    for metric in rows:
        parts = metric.split(".")
        if len(parts) == 5 and parts[0] == "T09s" and parts[1] == scene:
            seen.add(parts[2])
    return [v for v in VIEW_ORDER if v in seen]


def num(value):
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else None


def fmt(value, digits=3):
    return "-" if value is None or (isinstance(value, float) and math.isnan(value)) else ("%.*f" % (digits, value))


def verdict_of(shown, tvc):
    """'fits' / 'does not fit' / 'undecidable (within 0.5 ms)' of a busy delta against TVC's figure; '-' without either."""
    if shown is None or tvc is None:
        return "-"
    if abs(shown - tvc) <= UNDECIDABLE_MS:
        return "undecidable (within %.1f ms)" % UNDECIDABLE_MS
    return "fits" if shown <= tvc else "does not fit"


def cost_rows(found, rows, kt2):
    """The cost table: [(scene, view, variant, busy_delta, label_ms, frame_delta, tvc, verdict, reports, over_soft)] and the notes it fills."""
    table = []
    for scene, prefix in SCENES:
        for view in views_of(rows, scene):
            base = found.get((prefix + "off", view, RES))
            if base is None:
                continue
            base_busy = kt2.busy_ms(base)
            soft_delta = None
            for variant in variants_of(rows, scene, view):
                v = found.get((prefix + variant, view, RES))
                if v is None:
                    continue
                busy = kt2.busy_ms(v)
                delta = None if busy is None or base_busy is None else busy - base_busy
                if variant == "soft":
                    soft_delta = delta
                over_soft = None if delta is None or soft_delta in (None, 0.0) else delta / soft_delta
                frame = None if v["frame"] is None or base["frame"] is None else v["frame"] - base["frame"]
                tvc = TVC_MS.get(view)
                shown = delta if delta is not None else frame
                verdict = verdict_of(shown, tvc)
                if scene == "fair" and verdict != "-":
                    verdict += " (same figures, other scene)"
                if delta is not None and delta < -0.1:
                    NOTES.append("cost %s %s %s: the busy delta %.2f ms is negative (NOT SENSIBLE)" % (scene, view, variant, delta))
                table.append((scene, view, variant, delta, v["labels"], frame, tvc, verdict, v["reports"], over_soft))
    return table


def grain_rows(rows):
    """[(scene, view, variant, tstd, hfx, box_tstd, box_blob, cloud_share, over_first, over_hard)]."""
    table = []
    for scene, _ in SCENES:
        for view in views_of(rows, scene):
            names = variants_of(rows, scene, view)
            first = None
            hard = num(rows.get("T09s.%s.%s.hard.tstd" % (scene, view)))
            for variant in names:
                p = "T09s.%s.%s.%s." % (scene, view, variant)
                tstd = num(rows.get(p + "tstd"))
                if first is None and tstd is not None:
                    first = tstd
                over_first = None if tstd is None or first in (None, 0.0) else tstd / first
                over_hard = None if tstd is None or hard in (None, 0.0) else tstd / hard
                table.append((scene, view, variant, tstd, num(rows.get(p + "hfx")), num(rows.get(p + "box_tstd")), num(rows.get(p + "box_blob")),
                              num(rows.get(p + "cloud_share")), over_first, over_hard))
    return table


def effective_rows(rows):
    out = []
    for scene, _ in SCENES:
        for view in views_of(rows, scene):
            for variant in variants_of(rows, scene, view):
                p = "T09s.%s.%s.%s." % (scene, view, variant)
                if p + "effective_ok" in rows:
                    out.append((scene, view, variant, num(rows.get(p + "grid_points")), num(rows.get(p + "softness")), rows.get(p + "effective_ok"),
                                num(rows.get(p + "march_w")), num(rows.get(p + "march_h")), num(rows.get(p + "layers_none")), num(rows.get(p + "layers_total")),
                                rows.get(p + "span_tight"), rows.get(p + "skip_empty")))
    return out


def sun_lines(texts):
    out = []
    for text in texts:
        for line in text.splitlines():
            i = line.find("T09 sun colour (")
            if i >= 0:
                out.append(line[i:])
    return out


def tables(texts):
    """The text tables of a start's logs, and the (metric, value, variant) lines for the runner."""
    kt2 = load_kt2()
    rows = parse_rows(texts)
    lines = []
    for t in texts:
        lines.extend(t.splitlines())
    found = kt2.parse(lines)
    out_lines, metrics = [], []
    cost = cost_rows(found, rows, kt2)
    out_lines.append("COST (ms, busy time minus the baseline window with no layer drawn; label = the pass's own group; TVC = busy-delta of start 47)")
    out_lines.append("  %-6s %-7s %-12s %8s %8s %8s %7s %6s  %s" % ("scene", "view", "variant", "busy d", "label", "frame d", "/ soft", "TVC", "against TVC (reports)"))
    for scene, view, variant, delta, label, frame, tvc, verdict, reports, over_soft in cost:
        out_lines.append("  %-6s %-7s %-12s %8s %8s %8s %7s %6s  %s (%d)" % (scene, view, variant, fmt(delta, 2), fmt(label, 2), fmt(frame, 2), fmt(over_soft, 2), fmt(tvc, 2), verdict, reports))
        base = "T09s.cost.%s.%s.%s." % (scene, view, variant)
        for name, value in (("busy_delta_ms", delta), ("label_ms", label), ("frame_delta_ms", frame), ("over_soft", over_soft)):
            if value is not None:
                metrics.append({"metric": base + name, "value": round(value, 4), "variant": None})
        if scene == "clock" and verdict in ("fits", "does not fit"):
            metrics.append({"metric": base + "fits_tvc", "value": verdict == "fits", "variant": None})
    grain = grain_rows(rows)
    out_lines.append("GRAIN (tstd: temporal noise of a cloud pixel over three frames; hfx: spatial grain beyond the scene's own; box: start 56's box measure; share: pixels the clouds changed)")
    out_lines.append("  %-6s %-7s %-12s %8s %8s %8s %8s %7s %8s %8s" % ("scene", "view", "variant", "tstd", "hfx", "box tstd", "box blob", "share", "/ first", "/ hard"))
    for scene, view, variant, tstd, hfx, bt, bb, share, of, oh in grain:
        out_lines.append("  %-6s %-7s %-12s %8s %8s %8s %8s %7s %8s %8s" % (scene, view, variant, fmt(tstd, 5), fmt(hfx, 5), fmt(bt, 5), fmt(bb, 5), fmt(share, 3), fmt(of, 2), fmt(oh, 2)))
        base = "T09s.grain.%s.%s.%s." % (scene, view, variant)
        for name, value in (("tstd", tstd), ("hfx", hfx), ("box_tstd", bt), ("tstd_over_first", of), ("tstd_over_hard", oh)):
            if value is not None:
                metrics.append({"metric": base + name, "value": round(value, 6), "variant": None})
    eff = effective_rows(rows)
    out_lines.append("WHAT THE PASS RAN WITH (grid points, soft rim width, march target, layers left out of the march; ok = the table's numbers)")
    for scene, view, variant, points, softness, ok, mw, mh, none, total, tight, skip in eff:
        target = "-" if mw is None or mh is None else "%dx%d" % (mw, mh)
        left_out = "-" if none is None or total is None else "%d of %d" % (none, total)
        out_lines.append("  %-6s %-7s %-12s %4s points  softness %s  target %-9s left out %-7s tight %s skip %s  ok %s"
                         % (scene, view, variant, fmt(points, 0), fmt(softness, 2), target, left_out, tight, skip, ok))
        if ok is False:
            NOTES.append("the pass did not run with the table's numbers: %s %s %s (%s points, softness %s)" % (scene, view, variant, fmt(points, 0), fmt(softness, 2)))
    suns = sun_lines(texts)
    out_lines.append("SUN COLOUR")
    out_lines.extend("  " + s for s in suns)
    if not suns:
        out_lines.append("  (no line)")
    return out_lines, metrics


def series_results(texts):
    """What the runner calls: {start id: log text} -> the result lines. Never raises: a log that cannot be read gives a note."""
    del NOTES[:]
    try:
        out_lines, metrics = tables(list(texts.values()))
    except Exception as e:  # a script that raises fails the run: the numbers are a report, not a verdict
        NOTES.append("e40sf: %s: %s" % (type(e).__name__, e))
        return []
    NOTES.extend(out_lines)
    return metrics


# --- The pictures -----------------------------------------------------------------------------------------------------------------------------

PICTURE = re.compile(r"^t09-%s-(?P<view>sky3|ground|above|inside)(?P<fair>_fair)?-(?P<var>.+)_(?P<l>[abc])-(?P<w>\d+)x(?P<h>\d+)-natural\.png$" % START)
TVC_PICTURE = re.compile(r"^t09-(?:gt)?tvc1707-(?P<view>sky3|ground|above|inside)-tvc-1707x960-natural(?:-half)?\.png$")


def main_repo():
    """The main repo of a worktree under .local/work (the pictures go there), else this repo."""
    root = os.path.dirname(os.path.dirname(HERE))
    norm = root.replace("\\", "/")
    i = norm.find("/.local/work/")
    return root[:i] if i >= 0 else root


def index_pictures(folder):
    """{(scene, view): {variant: {letter: path}}} of the start's frames; the baseline is the variant "off" with the letter a."""
    found = {}
    if not os.path.isdir(folder):
        return found
    for name in sorted(os.listdir(folder)):
        m = PICTURE.match(name)
        if not m:
            continue
        key = ("fair" if m.group("fair") else "clock", m.group("view"))
        found.setdefault(key, {}).setdefault(m.group("var"), {})[m.group("l")] = os.path.join(folder, name)
    return found


def tvc_pictures(folders):
    out = {}
    for folder in folders:
        if not os.path.isdir(folder):
            continue
        for name in sorted(os.listdir(folder)):
            m = TVC_PICTURE.match(name)
            if m and (m.group("view") not in out or not name.endswith("-half.png")):
                out[m.group("view")] = os.path.join(folder, name)
    return out


def dither_crop(soft, off, size=(400, 225), body=(0.3, 0.85)):
    """The corner (x, y) of the crop that holds a cloud body: the window where the clouds changed the baseline most, among those that are not wholly cloud (a rim stays in it, so the weave
    of the interleaved step offset and the speckle of the hash both show). soft and off are float images (h, w, 3); the cloud is the pixels that differ from the baseline by 0.05 or more."""
    import numpy as np
    h, w = soft.shape[:2]
    cw, ch = min(size[0], w), min(size[1], h)
    changed = (np.abs(soft - off).max(axis=2) >= 0.05).astype(np.float32)
    # The share of cloud pixels per window from an integral image, on a 20 pixel grid.
    integral = np.pad(changed, ((1, 0), (1, 0))).cumsum(axis=0).cumsum(axis=1)
    best, best_full, at, at_full = -1.0, -1.0, (0, 0), (0, 0)
    for y in range(0, h - ch + 1, 20):
        for x in range(0, w - cw + 1, 20):
            share = float(integral[y + ch, x + cw] - integral[y, x + cw] - integral[y + ch, x] + integral[y, x]) / (cw * ch)
            if body[0] <= share <= body[1] and share > best:
                best, at = share, (x, y)
            if share > best_full:
                best_full, at_full = share, (x, y)
    return at if best >= 0.0 else at_full


def dither_sheet(variants, path, np, Image, ImageDraw, font, bar=18, zoom=2):
    """soft | softhash side by side, the same crop of the frame a at full size enlarged 2x with the nearest pixel (no smoothing, so the weave and the speckle are as drawn).
    Returns the path, or None when a frame of the pair or of the baseline is missing."""
    soft_name, hash_name = DITHER_PAIR
    off = variants.get("off", {}).get("a")
    if soft_name not in variants or hash_name not in variants or "a" not in variants[soft_name] or "a" not in variants[hash_name] or off is None:
        return None

    def load(p):
        return np.asarray(Image.open(p).convert("RGB"), dtype=np.float32) / 255.0

    soft, other, base = load(variants[soft_name]["a"]), load(variants[hash_name]["a"]), load(off)
    x, y = dither_crop(soft, base)
    ch, cw = min(225, soft.shape[0]), min(400, soft.shape[1])
    cells = []
    for name, img in ((soft_name, soft), (hash_name, other)):
        crop = (np.clip(img[y:y + ch, x:x + cw], 0, 1) * 255).astype(np.uint8)
        big = Image.fromarray(crop).resize((cw * zoom, ch * zoom), Image.NEAREST)
        canvas = Image.new("RGB", (big.width, big.height + bar), (16, 16, 16))
        canvas.paste(big, (0, bar))
        ImageDraw.Draw(canvas).text((4, 3), "%s   crop %d,%d %dx%d at full size, enlarged %dx" % (name, x, y, cw, ch, zoom), fill=(235, 235, 235), font=font)
        cells.append(np.asarray(canvas))
    Image.fromarray(np.concatenate(cells, axis=1)).save(path)
    return path


def make_sheets(pictures, out_dir, extra_tvc_folders=()):
    """Writes the sheets and the strips; returns the list of files. numpy and Pillow are needed."""
    import numpy as np
    from PIL import Image, ImageDraw, ImageFont

    font = ImageFont.load_default()
    tvc = tvc_pictures([pictures] + list(extra_tvc_folders))
    index = index_pictures(pictures)
    os.makedirs(out_dir, exist_ok=True)
    written = []
    tile_w, tile_h, bar = 640, 360, 18

    def tile(path, caption):
        img = Image.open(path).convert("RGB").resize((tile_w, tile_h), Image.LANCZOS)
        canvas = Image.new("RGB", (tile_w, tile_h + bar), (16, 16, 16))
        canvas.paste(img, (0, bar))
        ImageDraw.Draw(canvas).text((4, 3), caption, fill=(235, 235, 235), font=font)
        return canvas

    for (scene, view), variants in sorted(index.items()):
        prefix = "e40-59-" + ("fair-" if scene == "fair" else "")
        names = [v for v in variants if v != "off" and "a" in variants[v]]
        # The sheet: soft | hard | soft100s24 | softr33 | softcut16 (those that ran), then TVC; the other variants are in the strips.
        tiles = [tile(variants[v]["a"], v) for v in SHEET_ORDER if v in names]
        if scene == "clock" and view in tvc:
            tiles.append(tile(tvc[view], "TVC (start 47)"))
        if tiles:
            cols = 3
            rows_n = (len(tiles) + cols - 1) // cols
            sheet = Image.new("RGB", (cols * tile_w, rows_n * (tile_h + bar)), (0, 0, 0))
            for i, t in enumerate(tiles):
                sheet.paste(t, ((i % cols) * tile_w, (i // cols) * (tile_h + bar)))
            path = os.path.join(out_dir, "%s%s-sheet.png" % (prefix, view))
            sheet.save(path)
            written.append(path)
        if scene == "fair":
            path = dither_sheet(variants, os.path.join(out_dir, "%s%s-dither.png" % (prefix, view)), np, Image, ImageDraw, font)
            if path:
                written.append(path)
        # The strips: one crop for the view, the window where the first two frames of the first variant differ most.
        have = [v for v in names if all(l in variants[v] for l in "abc")]
        if not have:
            continue

        def load(path):
            return np.asarray(Image.open(path).convert("RGB"), dtype=np.float32) / 255.0

        a0, b0 = load(variants[have[0]]["a"]), load(variants[have[0]]["b"])
        h, w = a0.shape[:2]
        cw, ch = min(480, w), min(270, h)
        diff = np.abs(a0 - b0).mean(axis=2)
        best, at = -1.0, (0, 0)
        for y in range(0, h - ch + 1, 30):
            for x in range(0, w - cw + 1, 30):
                s = float(diff[y:y + ch, x:x + cw].mean())
                if s > best:
                    best, at = s, (x, y)
        x, y = at
        strip_rows = []
        for v in have:
            frames = [load(variants[v][l]) for l in "abc"]
            crops = [f[y:y + ch, x:x + cw] for f in frames]
            noise = np.clip(np.std(np.stack(crops), axis=0).mean(axis=2) * 8.0, 0.0, 1.0)
            cells = [(np.clip(c, 0, 1) * 255).astype(np.uint8) for c in crops] + [(np.repeat(noise[..., None], 3, axis=2) * 255).astype(np.uint8)]
            row = Image.fromarray(np.concatenate(cells, axis=1))
            canvas = Image.new("RGB", (row.width, row.height + bar), (16, 16, 16))
            canvas.paste(row, (0, bar))
            ImageDraw.Draw(canvas).text((4, 3), "%s   a | b | c | temporal noise x 8   (crop %d,%d %dx%d)" % (v, x, y, cw, ch), fill=(235, 235, 235), font=font)
            strip_rows.append(np.asarray(canvas))
        path = os.path.join(out_dir, "%s%s-strip.png" % (prefix, view))
        Image.fromarray(np.concatenate(strip_rows, axis=0)).save(path)
        written.append(path)
    return written


def main(argv):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("logs", nargs="*")
    ap.add_argument("--pictures", help="the folder of the start's pictures (default: .local/work/series-pictures/K-T2 of the worktree, else of the main repo)")
    ap.add_argument("--out", help="where the sheets go (default: .local/work/series-pictures/E40 of the main repo)")
    ap.add_argument("--no-sheets", action="store_true")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args(argv)
    if a.selftest:
        selftest()
        return 0
    if not a.logs:
        ap.error("a log is needed (or --selftest)")
    texts = []
    for path in a.logs:
        with open(path, encoding="utf-8", errors="replace") as f:
            texts.append(f.read())
    del NOTES[:]
    out_lines, _metrics = tables(texts)
    print("\n".join(out_lines))
    for n in NOTES:
        print("note: " + n)
    if not a.no_sheets:
        root = os.path.dirname(os.path.dirname(HERE))
        folder = a.pictures
        if not folder:
            for candidate in (os.path.join(root, ".local", "work", "series-pictures", "K-T2"), os.path.join(main_repo(), ".local", "work", "series-pictures", "K-T2")):
                if os.path.isdir(candidate) and index_pictures(candidate):
                    folder = candidate
                    break
        if not folder:
            print("no picture folder with the frames of %s found" % START)
            return 0
        out = a.out or os.path.join(main_repo(), ".local", "work", "series-pictures", "E40")
        for path in make_sheets(folder, out, [os.path.join(main_repo(), ".local", "work", "series-pictures", "K-T2")]):
            print("wrote " + path)
    return 0


# --- Selftest --------------------------------------------------------------------------------------------------------------------------------


def _report(frame, idle, label):
    lines = ["[ReDefinition] GPU profiler: FLIGHT, 600 frames measured, GPU %.3f ms a frame from Present to Present (at most 99.000)." % frame, "  By camera or light:"]
    if label is not None:
        lines.append("    ReDefinition clouds %.3f ms (10.0 %%)" % label)
    lines.append("    frame start, GPU idle included %.3f ms (1.0 %%)" % idle)
    lines.append("  Costliest segments, per frame on average, at most, in how many frames:")
    lines.append("    0.500 ms, at most 9.5, 600: something")
    return lines


def _window(view, tag, busy, label):
    out = ["T09 view begin: %s %s %s" % (view, tag, RES)]
    out += _report(99.0, 1.0, label)           # the first report is dropped
    out += _report(busy + 1.0, 1.0, label)
    out += _report(busy + 1.0, 1.0, label)
    out.append("T09 view end: %s %s %s" % (view, tag, RES))
    return out


def _row(metric, value):
    return 'ATMORESULT {"series":"K-T2","metric":"%s","value":%s}' % (metric, json.dumps(value))


def selftest():
    import tempfile
    log = []
    # The clock scene: ground (soft 2.0 over the baseline: fits TVC's 2.59; hard 3.0: does not), above (soft 5.0: fits 5.30).
    log += _window("ground", "k_off", 10.0, None) + _window("ground", "k_soft", 12.0, 1.9) + _window("ground", "k_hard", 14.0, 2.8)
    log += _window("above", "k_off", 10.0, None) + _window("above", "k_soft", 14.0, 4.8)
    log += _window("ground", "fw_off", 9.0, None) + _window("ground", "fw_soft", 14.0, 4.7) + _window("ground", "fw_softr33", 11.5, 2.4)
    # Within 0.5 ms of TVC's 2.59 at the clock ground (2.9): undecidable, and no fits_tvc row; the ratio over soft is the march target's read.
    log += _window("ground", "k_softr33", 12.9, 1.2)
    for scene, view, variant, tstd in (("clock", "ground", "soft", 0.002), ("clock", "ground", "hard", 0.008), ("clock", "ground", "softr33", 0.003), ("clock", "above", "soft", 0.003),
                                      ("fair", "ground", "soft", 0.004), ("fair", "ground", "hard", 0.016), ("fair", "ground", "softr33", 0.005)):
        p = "T09s.%s.%s.%s." % (scene, view, variant)
        log += [_row(p + "tstd", tstd), _row(p + "hfx", -0.01), _row(p + "box_tstd", tstd * 2), _row(p + "box_blob", 0.01), _row(p + "cloud_share", 0.4),
                _row(p + "grid_points", 48), _row(p + "softness", 0.5), _row(p + "effective_ok", True),
                _row(p + "march_w", 563 if variant == "softr33" else 854), _row(p + "march_h", 317 if variant == "softr33" else 480),
                _row(p + "layers_none", 2), _row(p + "layers_total", 4), _row(p + "span_tight", False),
                _row(p + "skip_empty", variant == "softr33")]
    log.append("T09 sun colour (fair, frame 7): Scatterer loaded True, ...; the read sees the restored colour: True")
    text = "\n".join(log)
    out_lines, metrics = tables([text])
    rows = {m["metric"]: m["value"] for m in metrics}
    assert abs(rows["T09s.cost.clock.ground.soft.busy_delta_ms"] - 2.0) < 1e-9, rows
    assert rows["T09s.cost.clock.ground.soft.fits_tvc"] is True and rows["T09s.cost.clock.ground.hard.fits_tvc"] is False, rows
    assert abs(rows["T09s.cost.clock.above.soft.busy_delta_ms"] - 4.0) < 1e-9 and rows["T09s.cost.clock.above.soft.fits_tvc"] is True, rows
    assert abs(rows["T09s.cost.clock.ground.soft.label_ms"] - 1.9) < 1e-9, rows
    assert abs(rows["T09s.cost.fair.ground.soft.busy_delta_ms"] - 5.0) < 1e-9 and "T09s.cost.fair.ground.soft.fits_tvc" not in rows, rows
    # The verdict words: a delta within 0.5 ms of TVC's figure is undecidable and gets no fits_tvc row.
    assert verdict_of(1.9, 2.59) == "fits" and verdict_of(3.2, 2.59) == "does not fit", (verdict_of(1.9, 2.59), verdict_of(3.2, 2.59))
    assert verdict_of(2.59 + 0.5, 2.59).startswith("undecidable") and verdict_of(2.59 - 0.4, 2.59).startswith("undecidable") and verdict_of(None, 2.59) == "-"
    assert "T09s.cost.clock.ground.softr33.fits_tvc" not in rows and abs(rows["T09s.cost.clock.ground.softr33.busy_delta_ms"] - 2.9) < 1e-9, rows
    assert abs(rows["T09s.cost.clock.ground.softr33.over_soft"] - 2.9 / 2.0) < 1e-3 and abs(rows["T09s.cost.fair.ground.softr33.over_soft"] - 2.5 / 5.0) < 1e-3, rows
    assert any("undecidable (within 0.5 ms)" in l and "softr33" in l for l in out_lines), out_lines
    assert any("target 563x317" in l and "softr33" in l for l in out_lines) and any("left out 2 of 4" in l for l in out_lines), out_lines
    assert abs(rows["T09s.grain.clock.ground.hard.tstd_over_hard"] - 1.0) < 1e-9 and abs(rows["T09s.grain.clock.ground.soft.tstd_over_hard"] - 0.25) < 1e-9, rows
    assert abs(rows["T09s.grain.fair.ground.hard.tstd_over_first"] - 4.0) < 1e-9, rows
    assert any("T09 sun colour (fair" in l for l in out_lines)
    got = series_results({"soft59": text})
    assert got and any(l.startswith("COST") for l in NOTES), NOTES
    assert series_results({"soft59": "no rows at all"}) == [], NOTES
    # kt2.py reads the same log without a failure (the runner calls both scripts on it).
    kt2 = load_kt2()
    kt2.series_results({"soft59": text})
    # A mismatch between the table and what the pass ran with is a note.
    bad = _row("T09s.clock.ground.soft.effective_ok", False) + "\n" + _row("T09s.clock.ground.soft.grid_points", 32) + "\n" + _row("T09s.clock.ground.soft.tstd", 0.1)
    del NOTES[:]
    tables([bad])
    assert any("did not run with the table's numbers" in n for n in NOTES), NOTES
    # Sheets and strips from synthetic frames.
    import numpy as np
    from PIL import Image
    with tempfile.TemporaryDirectory() as tmp:
        pics = os.path.join(tmp, "pics")
        os.makedirs(pics)
        rng = np.random.RandomState(1)
        base = np.tile(np.linspace(0, 1, 1707, dtype=np.float32)[None, :, None], (960, 1, 3))
        for scene_suffix in ("", "_fair"):
            for variant in ("off", "soft", "hard", "softr33", "softhash"):
                for letter in "abc":
                    if variant == "off" and letter != "a":
                        continue
                    noise = rng.normal(0, 0.05 if variant == "hard" else 0.01, base.shape).astype(np.float32)
                    img = (np.clip(base + noise, 0, 1) * 255).astype(np.uint8)
                    Image.fromarray(img).save(os.path.join(pics, "t09-%s-ground%s-%s_%s-1707x960-natural.png" % (START, scene_suffix, variant, letter)))
        Image.fromarray((base * 255).astype(np.uint8)).save(os.path.join(pics, "t09-tvc1707-ground-tvc-1707x960-natural.png"))
        out = os.path.join(tmp, "out")
        written = make_sheets(pics, out)
        names = sorted(os.path.basename(p) for p in written)
        assert names == ["e40-59-fair-ground-dither.png", "e40-59-fair-ground-sheet.png", "e40-59-fair-ground-strip.png", "e40-59-ground-sheet.png", "e40-59-ground-strip.png"], names
        with Image.open(os.path.join(out, "e40-59-ground-sheet.png")) as sheet:
            size = sheet.size
        assert size == (3 * 640, 2 * (360 + 18)), size        # soft, hard, softr33 and TVC (softhash is in the strip and the dither sheet only): four tiles, two rows of three
        with Image.open(os.path.join(out, "e40-59-fair-ground-dither.png")) as dither:
            dsize = dither.size
        assert dsize == (2 * 800, 450 + 18), dsize              # soft | softhash, a 400 x 225 crop enlarged 2x
        # The crop sits on a cloud body: the window whose cloud share is inside the body band, a rim in it.
        h, w = 960, 1707
        soft_img = np.zeros((h, w, 3), np.float32)
        off_img = np.zeros((h, w, 3), np.float32)
        soft_img[300:700, 600:1100] = 0.5                       # a body of 500 x 400 pixels
        x, y = dither_crop(soft_img, off_img)
        share = float((np.abs(soft_img - off_img).max(axis=2)[y:y + 225, x:x + 400] >= 0.05).mean())
        assert 0.3 <= share <= 0.85, (x, y, share)
    print("selftest passed")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
