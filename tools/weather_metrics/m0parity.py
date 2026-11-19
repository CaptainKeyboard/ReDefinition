"""M0 parity (master plan phase 0 item 3): the difference of two cloud buffers per tile, game against game or game against the weather player.

    python tools/weather_metrics/m0parity.py A B --out DIR [--base A2 B2] [--tile 64] [--gain 16] [--label-a game --label-b player]
    python tools/weather_metrics/m0parity.py --selftest

A and B are the clouds' own output before the composite as the pinned capture writes it: a capture folder (its cloud.npy), or a .npy file (float32, rows bottom first as Unity
reads them back, H x W x 4 premultiplied linear radiance and alpha; the weather player's <job>-r<k>-cloud.npy has the same layout). Any H x W x 4 picture of the same layout
compares the same way (natural.npy, the composed picture).

Written into DIR:
  tiles.csv       per tile (TILE x TILE pixels, row 0 at the top of the picture): the mean and the largest absolute difference of the colour (the largest of r, g, b per pixel)
                  and of the alpha, and with --base the same numbers of the base pair and whether this pair exceeds it;
  summary.txt     the table: whole-picture mean / max / 99th percentile of colour and alpha, the share of pixels that differ by more than one half-float step, the tiles above the base;
  summary.json    the same numbers;
  crops/          with --base: for every tile where this pair's mean colour or mean alpha difference is above the base pair's in the same tile (and above one half-float step), a PNG
                  of three 1:1 crops side by side at full pixel size, never scaled: A, B, and the difference times GAIN (red: colour, green: alpha).
The pair A B is the one judged (game against player); --base A2 B2 is the game's own run-to-run difference (two starts of the same commit), the bar a region must stay under.
"""
import argparse
import csv
import json
import os
import sys

import numpy as np

TILE = 64
GAIN = 16.0
# The cloud buffer is a half float target (ARGBHalf): two values within one half-float step of the larger are the same number in the game's buffer. 2**-14 is the smallest
# normal half; below it the step is constant.
HALF_MIN_NORMAL = 2.0 ** -14


def load(path):
    """The H x W x 4 float32 array of a capture folder (cloud.npy) or a .npy file, flipped so that row 0 is the top of the picture; a single channel gets alpha 1."""
    if os.path.isdir(path):
        path = os.path.join(path, "cloud.npy")
    a = np.load(path).astype(np.float32)
    if a.ndim == 2:
        a = np.stack([a, a, a, np.ones_like(a)], axis=-1)
    if a.shape[-1] == 3:
        a = np.concatenate([a, np.ones(a.shape[:2] + (1,), np.float32)], axis=-1)
    return a[::-1].copy()


def half_step(x):
    """One half-float step at the magnitude of x (np.spacing of the half value)."""
    x = np.maximum(np.abs(x), HALF_MIN_NORMAL).astype(np.float16)
    return np.spacing(x).astype(np.float32)


def differences(a, b):
    """Per pixel: the colour difference (largest of the three channels), the alpha difference, and whether either exceeds one half-float step of the larger value."""
    if a.shape != b.shape:
        raise ValueError("the pictures differ in size: %s against %s" % (a.shape, b.shape))
    dc = np.abs(a[..., :3] - b[..., :3]).max(axis=-1)
    da = np.abs(a[..., 3] - b[..., 3])
    step_c = half_step(np.maximum(np.abs(a[..., :3]), np.abs(b[..., :3])).max(axis=-1))
    step_a = half_step(np.maximum(np.abs(a[..., 3]), np.abs(b[..., 3])))
    beyond = (dc > step_c) | (da > step_a)
    return dc, da, beyond


def tiles(dc, da, beyond, tile):
    """Per tile: (row, col, y0, x0, h, w, mean colour, max colour, mean alpha, max alpha, share beyond a half step)."""
    h, w = dc.shape
    out = []
    for r, y0 in enumerate(range(0, h, tile)):
        for c, x0 in enumerate(range(0, w, tile)):
            sc, sa, sb = dc[y0:y0 + tile, x0:x0 + tile], da[y0:y0 + tile, x0:x0 + tile], beyond[y0:y0 + tile, x0:x0 + tile]
            out.append(dict(row=r, col=c, y0=y0, x0=x0, h=sc.shape[0], w=sc.shape[1], mean_colour=float(sc.mean()), max_colour=float(sc.max()), mean_alpha=float(sa.mean()),
                            max_alpha=float(sa.max()), beyond=float(sb.mean())))
    return out


def whole(dc, da, beyond):
    return dict(mean_colour=float(dc.mean()), max_colour=float(dc.max()), p99_colour=float(np.percentile(dc, 99.0)), mean_alpha=float(da.mean()), max_alpha=float(da.max()),
                p99_alpha=float(np.percentile(da, 99.0)), beyond_half_step=float(beyond.mean()))


def display(img):
    """A linear premultiplied picture for the eye: Reinhard x / (1 + x) per channel over black, then the sRGB curve; 8 bit."""
    x = np.clip(img[..., :3], 0.0, None)
    x = x / (1.0 + x)
    x = np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1.0 / 2.4) - 0.055)
    return np.clip(np.rint(x * 255.0), 0, 255).astype(np.uint8)


def crop_triple(a, b, dc, da, t, gain, path, text):
    from PIL import Image, PngImagePlugin
    ys, xs = slice(t["y0"], t["y0"] + t["h"]), slice(t["x0"], t["x0"] + t["w"])
    diff = np.zeros((t["h"], t["w"], 3), np.uint8)
    diff[..., 0] = np.clip(np.rint(dc[ys, xs] * gain * 255.0), 0, 255).astype(np.uint8)
    diff[..., 1] = np.clip(np.rint(da[ys, xs] * gain * 255.0), 0, 255).astype(np.uint8)
    gap = 4
    canvas = np.zeros((t["h"], 3 * t["w"] + 2 * gap, 3), np.uint8)
    canvas[:, :t["w"]] = display(a[ys, xs])
    canvas[:, t["w"] + gap:2 * t["w"] + gap] = display(b[ys, xs])
    canvas[:, 2 * (t["w"] + gap):] = diff
    meta = PngImagePlugin.PngInfo()
    meta.add_text("ReDefinition-m0parity", text)
    Image.fromarray(canvas, "RGB").save(path, pnginfo=meta)


class OrientationError(Exception):
    pass


def depth_of(path):
    """The scene depth beside a buffer: a capture folder's depth.npy, or <stem>-depth.npy beside a replay's <stem>-cloud.npy; None where there is none."""
    if os.path.isdir(path):
        d = os.path.join(path, "depth.npy")
    elif path.endswith("-cloud.npy"):
        d = path[:-len("-cloud.npy")] + "-depth.npy"
    elif os.path.basename(path) == "cloud.npy":
        d = os.path.join(os.path.dirname(path), "depth.npy")
    else:
        d = None
    return d if d and os.path.isfile(d) else None


def sky_side(depth_path):
    """Where the sky is in the depth (0 = sky) after the same flip as the buffers (row 0 at the top): "top", "bottom", or "undecided" where both quarters have the same share."""
    d = np.load(depth_path).astype(np.float32)
    if d.ndim == 3:
        d = d[..., 0]
    d = d[::-1]
    q = max(d.shape[0] // 4, 1)
    top, bottom = float((d[:q] <= 0.0).mean()), float((d[-q:] <= 0.0).mean())
    return ("top" if top > bottom else "bottom" if bottom > top else "undecided"), top, bottom


def orientation(path_a, path_b, depth_a=None, depth_b=None):
    """The orientation check: the sky rows at the top on both sides. Raises OrientationError where a side has the sky at the bottom, the sides disagree, or a side has no
    depth; the text where both depths have no ground to decide by (a view of the sky alone)."""
    da, db = depth_a or depth_of(path_a), depth_b or depth_of(path_b)
    if da is None or db is None:
        raise OrientationError("no depth.npy for %s; the orientation cannot be checked (--depth-a / --depth-b, or --no-orientation)" % (path_a if da is None else path_b))
    sa, sb = sky_side(da), sky_side(db)
    text = "sky %s / %s (sky share top quarter %.3f / %.3f, bottom quarter %.3f / %.3f)" % (sa[0], sb[0], sa[1], sb[1], sa[2], sb[2])
    if sa[0] != sb[0]:
        raise OrientationError("the two sides disagree: " + text)
    if sa[0] == "bottom":
        raise OrientationError("the sky is at the bottom on both sides (a flipped read): " + text)
    return text + (" (no ground in the depth: not decided)" if sa[0] == "undecided" else "")


def compare(path_a, path_b, out, base=None, tile=TILE, gain=GAIN, label_a="A", label_b="B", check_orientation=True, depth_a=None, depth_b=None):
    oriented = orientation(path_a, path_b, depth_a, depth_b) if check_orientation else "not checked"
    a, b = load(path_a), load(path_b)
    dc, da, beyond = differences(a, b)
    rows = tiles(dc, da, beyond, tile)
    summary = dict(a=os.path.abspath(path_a), b=os.path.abspath(path_b), label_a=label_a, label_b=label_b, size=[int(a.shape[1]), int(a.shape[0])], tile=tile, gain=gain,
                   whole=whole(dc, da, beyond), orientation=oriented)
    base_rows = None
    if base:
        ba, bb = load(base[0]), load(base[1])
        bdc, bda, bbeyond = differences(ba, bb)
        base_rows = tiles(bdc, bda, bbeyond, tile)
        summary["base"] = dict(a=os.path.abspath(base[0]), b=os.path.abspath(base[1]), whole=whole(bdc, bda, bbeyond))
    os.makedirs(out, exist_ok=True)
    exceeding = []
    for i, t in enumerate(rows):
        if base_rows is None:
            continue
        bt = base_rows[i]
        t["base_mean_colour"], t["base_max_colour"], t["base_mean_alpha"], t["base_max_alpha"] = bt["mean_colour"], bt["max_colour"], bt["mean_alpha"], bt["max_alpha"]
        # Above the base in the mean or the largest difference of either quantity (a few wrong pixels in a tile move its max, not its mean), and beyond one half-float
        # step somewhere in the tile (two equal buffers' noise in the last bit is not a region).
        above = (t["mean_colour"] > bt["mean_colour"] or t["mean_alpha"] > bt["mean_alpha"] or t["max_colour"] > bt["max_colour"] or t["max_alpha"] > bt["max_alpha"])
        t["exceeds"] = int(above and t["beyond"] > 0.0)
        if t["exceeds"]:
            exceeding.append(t)
    with open(os.path.join(out, "tiles.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    summary["tiles"] = len(rows)
    summary["tiles_exceeding_base"] = len(exceeding) if base_rows is not None else None
    crops = []
    if exceeding:
        os.makedirs(os.path.join(out, "crops"), exist_ok=True)
        for t in exceeding:
            name = "tile-r%02d-c%02d.png" % (t["row"], t["col"])
            text = ("%s | %s | difference x%g (red colour, green alpha); tile r%d c%d at x %d y %d (top-left), mean colour %.5f (base %.5f), mean alpha %.5f (base %.5f)"
                    % (label_a, label_b, gain, t["row"], t["col"], t["x0"], t["y0"], t["mean_colour"], t["base_mean_colour"], t["mean_alpha"], t["base_mean_alpha"]))
            crop_triple(a, b, dc, da, t, gain, os.path.join(out, "crops", name), text)
            crops.append(name)
    summary["crops"] = crops
    with open(os.path.join(out, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=1)
    text = report(summary)
    with open(os.path.join(out, "summary.txt"), "w", encoding="utf-8") as f:
        f.write(text + "\n")
    return summary, text


def report(s):
    def line(name, w):
        return "  %-14s colour mean %.6f max %.6f p99 %.6f | alpha mean %.6f max %.6f p99 %.6f | beyond a half step %.4f %%" % (
            name, w["mean_colour"], w["max_colour"], w["p99_colour"], w["mean_alpha"], w["max_alpha"], w["p99_alpha"], 100.0 * w["beyond_half_step"])

    lines = ["M0 parity %s against %s (%d x %d, tiles of %d px)" % (s["label_a"], s["label_b"], s["size"][0], s["size"][1], s["tile"]), line("this pair", s["whole"])]
    if "base" in s:
        lines.append(line("base pair", s["base"]["whole"]))
        lines.append("  tiles above the base: %d of %d (crops: %d)" % (s["tiles_exceeding_base"], s["tiles"], len(s["crops"])))
        lines.append("VERDICT %s against %s: %d of %d tiles (%.1f %%) above the base pair's difference (mean or max, colour or alpha)" % (
            s["label_a"], s["label_b"], s["tiles_exceeding_base"], s["tiles"], 100.0 * s["tiles_exceeding_base"] / max(s["tiles"], 1)))
    if "orientation" in s:
        lines.append("  orientation: " + s["orientation"])
    return "\n".join(lines)


def selftest():
    import tempfile
    rng = np.random.default_rng(5)
    h, w = 130, 200
    # Half-float values, as the game's buffer holds them.
    a = rng.random((h, w, 4)).astype(np.float16).astype(np.float32)
    folder = tempfile.mkdtemp(prefix="m0parity-")
    # The depth beside each buffer, bottom row first as Unity reads it: ground (500 m) in the lower half of the picture, sky (0) above.
    depth = np.zeros((h, w), np.float32)
    depth[:h // 2] = 500.0
    for stem in ("a", "b", "c"):
        np.save(os.path.join(folder, stem + "-depth.npy"), depth)
    np.save(os.path.join(folder, "flipped-depth.npy"), depth[::-1].copy())
    pa = os.path.join(folder, "a-cloud.npy")
    np.save(pa, a)
    try:
        compare(pa, pa, os.path.join(folder, "flip"), depth_b=os.path.join(folder, "flipped-depth.npy"))
        raise AssertionError("a flipped side must fail the orientation")
    except OrientationError:
        pass
    # The same buffer: zero everywhere, no tile above anything.
    s, _ = compare(pa, pa, os.path.join(folder, "same"))
    assert s["whole"]["max_colour"] == 0.0 and s["whole"]["beyond_half_step"] == 0.0, s
    # A change of one tile (row 1, col 2 in picture rows from the top; the file is bottom first) above a base that differs by one half step only.
    b = a.copy()
    file_rows = slice(h - 128, h - 64)
    b[file_rows, 128:192, 0] += 0.25
    b[file_rows, 128:192, 3] += 0.125
    pb = os.path.join(folder, "b-cloud.npy")
    np.save(pb, b)
    c = a.copy()
    c[..., 0] = (a[..., 0].astype(np.float16) + np.spacing(a[..., 0].astype(np.float16))).astype(np.float32)
    pc = os.path.join(folder, "c-cloud.npy")
    np.save(pc, c)
    s, text = compare(pa, pb, os.path.join(folder, "diff"), base=(pa, pc), label_a="game", label_b="player")
    assert s["tiles_exceeding_base"] == 1 and s["crops"] == ["tile-r01-c02.png"], s
    assert abs(s["whole"]["max_colour"] - 0.25) < 1e-6 and abs(s["whole"]["max_alpha"] - 0.125) < 1e-6, s
    from PIL import Image
    crop = Image.open(os.path.join(folder, "diff", "crops", "tile-r01-c02.png"))
    assert crop.size == (3 * 64 + 8, 64), crop.size
    assert "VERDICT game against player: 1 of 12 tiles (8.3 %)" in text and "sky top / top" in s["orientation"], text
    # One pixel off in a tile moves its max, not much its mean: still a tile above the base.
    d = a.copy()
    d[h - 1, 0, 1] += 0.5
    pd = os.path.join(folder, "d-cloud.npy")
    np.save(pd, d)
    np.save(os.path.join(folder, "d-depth.npy"), depth)
    s, _ = compare(pa, pd, os.path.join(folder, "pixel"), base=(pa, pc))
    assert s["tiles_exceeding_base"] == 1, s
    # A base within one half step counts nothing as beyond.
    s, _ = compare(pa, pc, os.path.join(folder, "base"))
    assert s["whole"]["beyond_half_step"] == 0.0, s
    print(text)
    print("selftest passed")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("a", nargs="?")
    p.add_argument("b", nargs="?")
    p.add_argument("--out")
    p.add_argument("--base", nargs=2, metavar=("A2", "B2"))
    p.add_argument("--tile", type=int, default=TILE)
    p.add_argument("--gain", type=float, default=GAIN)
    p.add_argument("--label-a", default="A")
    p.add_argument("--label-b", default="B")
    p.add_argument("--depth-a")
    p.add_argument("--depth-b")
    p.add_argument("--no-orientation", action="store_true", help="leave the sky-at-the-top check out (pictures without a depth)")
    p.add_argument("--selftest", action="store_true")
    args = p.parse_args(argv)
    if args.selftest:
        selftest()
        return 0
    if not (args.a and args.b and args.out):
        p.error("A, B and --out are needed")
    try:
        _, text = compare(args.a, args.b, args.out, args.base, args.tile, args.gain, args.label_a, args.label_b, not args.no_orientation, args.depth_a, args.depth_b)
    except OrientationError as e:
        print("ORIENTATION FAILED: %s" % e, file=sys.stderr)
        return 2
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
