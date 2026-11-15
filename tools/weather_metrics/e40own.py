"""E40 owner decision picture pair: what the cost switches do to the picture (series e40-owner-cost).

    python tools/weather_metrics/e40c2.py --prepare-all <folder with the baseline dumps of one start>     the game's dumped maps as scenes
    python tools/build_weather_player.py --series e40-owner-cost [--no-build]
    python tools/weather_metrics/e40own.py --selftest

The same dumped maps and views, four ways of paying for the clouds, side by side (player working view, not evidence):
  1 today's default (recon.resolution 0.33, light quality High, cloudSoftness 0.5 / 48 samples)
  2 recon.resolution 0.25
  3 recon.resolution 0.25 and cloudLightQuality Medium
  4 the owner's cloudSoftness 1.0 / cloudSamples 24 at 0.33
Each tile carries the pass ms the cost stage measured on the same maps (1707 x 960, march + resolve + composite) and whether it fits True Volumetric Clouds' ms of that view.
The picture is the pass as the game runs it (still camera, 40 frames of history, the last one). The crops are the same window of every variant, twice the size, nearest neighbour.
"""
import argparse
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Stage, register_series  # noqa: E402
from weather_metrics import e40c1 as c1  # noqa: E402
from weather_metrics import e40c2 as c2  # noqa: E402
from weather_metrics import e40c3 as c3  # noqa: E402
from weather_metrics import e40gc as gc  # noqa: E402
from weather_metrics import e40o  # noqa: E402
from weather_metrics import e40ty  # noqa: E402

BUILD = c1.BUILD
COST_JSON = os.path.join(BUILD, "e40own-cost-results.json")
RESULT_JSON = os.path.join(BUILD, "e40own-choice-results.json")
PICTURE = "e40-owner-cost-choice.png"
CROPS = "e40-owner-cost-choice-crops.png"
HEADER = "player working view (not evidence)"
# The four ways: (title, medium of the player's density = cloudSoftness, case of the soft-cost scene). The product's case is the third of the target, light High, the leap of C2.
PROD = c3.PROD
VARIANTS = [
    ("1  today: resolution 0.33, light High", "pr50", PROD),
    ("2  resolution 0.25", "pr50", PROD + "+r25"),
    ("3  resolution 0.25 + light Medium", "pr50", PROD + "+r25+lq1"),
    ("4  owner: softness 1.0 / 24 samples, 0.33", "pr100", "seq1m24+lq2+r33+lp100"),
]
MEDIA = sorted({m for _, m, _ in VARIANTS})
CASES = [c for _, _, c in VARIANTS]
# The picture's rows: the four clock views and the fair weather's ground view (the horizon of the fair day); TVC has a figure for the clock views only.
ROWS = [("clock ground", "koffground", "ground"), ("clock inside", "koffinside", "inside"), ("clock above", "koffabove", "above"),
        ("clock sky3", "koffsky3", "sky3"), ("fair horizon", "fwoffground", "ground")]
TVC = dict(c1.TVC)
BORDER = 0.3   # a cost cell's run-to-run noise (E40 C3): within it the verdict is "borderline"
TILE = (683, 384)
CROP_SRC = (320, 180)


def scene_of(prefix):
    for s in c2.scenes():
        if s.startswith(prefix):
            return s
    return None


def cost_stage():
    c2.write_inputs()
    views = [c1.tag(s, "now", v) for s in c2.scenes() for v in c2.scene_views(s)]
    return Stage("own-cost", ["-mode", "scene", "-scene", "soft-cost", "-in", c2.IN, "-views", ",".join(views), "-media", ",".join(MEDIA), "-marches", ",".join(CASES)],
                 size=(1707, 960), proxy=True, timeout=3400)


def sheet_stage():
    views = [c1.tag(s, "now", v) for s in c2.scenes() for v in c2.scene_views(s)]
    return Stage("own-sheet", ["-mode", "scene", "-scene", "soft-temporal", "-in", c2.IN, "-views", ",".join(views), "-media", ",".join(MEDIA), "-marches", ",".join(CASES)],
                 size=(1280, 720), proxy=False, timeout=3400)


def stages():
    if not c2.scenes():
        return []
    return [cost_stage(), sheet_stage()]


def verdict(view, ms):
    """'fits TVC 2.59' / 'does not fit TVC 2.59' ('borderline' within the cost cell's noise); the fair weather has no TVC figure."""
    if view not in TVC or ms is None:
        return "no TVC figure for this view"
    t = TVC[view]
    word = "borderline" if abs(ms - t) <= BORDER else ("fits TVC" if ms < t else "does not fit TVC")
    return "%s %.2f" % (word, t) if word != "borderline" else "borderline vs TVC %.2f" % t


def font(size):
    from PIL import ImageFont
    for path in (r"C:\Windows\Fonts\arial.ttf", r"C:\Windows\Fonts\segoeui.ttf"):
        if os.path.isfile(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def to_pil(img):
    from PIL import Image
    return Image.fromarray(np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8), "RGB")


def edge_window(alpha, win):
    """The window (x, y) of the given size with the most rim pixels (alpha between 0.1 and 0.9 with a neighbour across the rim), searched on a coarse grid."""
    a = np.clip(alpha, 0.0, 1.0)
    gx = np.abs(np.diff(a, axis=1, prepend=a[:, :1]))
    gy = np.abs(np.diff(a, axis=0, prepend=a[:1, :]))
    rim = ((gx + gy) > 0.04) & (a > 0.1) & (a < 0.9)
    h, w = a.shape
    best, where = -1, (0, 0)
    for y in range(0, h - win[1] + 1, 20):
        for x in range(0, w - win[0] + 1, 20):
            n = int(rim[y:y + win[1], x:x + win[0]].sum())
            if n > best:
                best, where = n, (x, y)
    return where


def compose(rows, header, tile_size, label_height=62):
    """rows: list of rows, each a list of (PIL image, footer text of up to two lines); returns one PIL image with a header band."""
    from PIL import Image, ImageDraw
    cols = max(len(r) for r in rows)
    gap = 8
    head_h = 40
    w = cols * tile_size[0] + (cols + 1) * gap
    h = head_h + len(rows) * (tile_size[1] + label_height + gap) + gap
    out = Image.new("RGB", (w, h), (24, 24, 24))
    draw = ImageDraw.Draw(out)
    draw.text((gap, 8), header, fill=(255, 255, 255), font=font(22))
    for ri, row in enumerate(rows):
        y = head_h + gap + ri * (tile_size[1] + label_height + gap)
        for ci, (img, text) in enumerate(row):
            x = gap + ci * (tile_size[0] + gap)
            out.paste(img, (x, y))
            draw.rectangle([x, y + tile_size[1], x + tile_size[0], y + tile_size[1] + label_height], fill=(0, 0, 0))
            draw.text((x + 6, y + tile_size[1] + 6), text, fill=(255, 255, 255), font=font(20))
    return out


def collect_pictures(ctx, cost_rows):
    folder = ctx.folder("own-sheet")
    tint = e40ty.white_tint(3000.0)
    out_dir = os.path.join(e40o.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    big, small, results = [], [], {}
    for title, prefix, view in ROWS:
        scene = scene_of(prefix)
        if scene is None:
            continue
        key = c1.tag(scene, "now", view)
        cam = c1.camera(view, c3.SHEET_FULL)
        ref_path = os.path.join(folder, "gs-%s-pr50-static-ref-f36.npy" % key)
        if not os.path.isfile(ref_path):
            continue
        ref = e40ty.load_lum(folder, "gs-%s-pr50-static-ref-f36" % key)
        x0, y0 = edge_window(ref[..., 3], CROP_SRC)
        big_row, small_row = [], []
        for vi, (name, medium, case) in enumerate(VARIANTS):
            path = os.path.join(folder, "gs-%s-%s-static-%s-acc20-f39.npy" % (key, medium, case))
            if not os.path.isfile(path):
                continue
            img = e40ty.picture(e40ty.load_lum(folder, "gs-%s-%s-static-%s-acc20-f39" % (key, medium, case)), cam, tint)
            row = cost_rows.get("%s|%s|%s" % (key, medium, case), {})
            ms = row.get("total")
            note = "%s   %s" % ("%.2f ms" % ms if ms is not None else "no ms", verdict(view if prefix.startswith("koff") else "fair", ms))
            note = "%s, variant %d\n%s" % (title, vi + 1, note)
            pil = to_pil(img)
            big_row.append((pil.resize(TILE, resample=1), note))
            crop = pil.crop((x0, y0, x0 + CROP_SRC[0], y0 + CROP_SRC[1])).resize((CROP_SRC[0] * 2, CROP_SRC[1] * 2), resample=0)
            small_row.append((crop, note))
            results["%s|%d" % (title, vi + 1)] = dict(ms=ms, verdict=note, window=[x0, y0])
        if big_row:
            big.append(big_row)
            small.append(small_row)
    if not big:
        print("no picture could be composed (no sheet frames)")
        return
    titles = "  |  ".join(n for n, _, _ in VARIANTS)
    head = "%s: replay of the game's dumped maps, columns: %s" % (HEADER, titles)
    compose(big, head, TILE).save(os.path.join(out_dir, PICTURE))
    compose(small, "%s: 2x crops of one cloud edge (same window in every column), %s" % (HEADER, titles), (CROP_SRC[0] * 2, CROP_SRC[1] * 2)).save(os.path.join(out_dir, CROPS))
    with open(RESULT_JSON, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)
    print("pictures", os.path.join(out_dir, PICTURE), os.path.join(out_dir, CROPS))


def collect(ctx):
    metrics = gc.collect_cost(ctx, [("own-cost", "", "", [])], COST_JSON)
    with open(COST_JSON, encoding="utf-8") as f:
        rows = json.load(f)["rows"]
    try:
        ctx.folder("own-sheet")
    except KeyError:
        return metrics
    collect_pictures(ctx, rows)
    return metrics


def selftest():
    assert len(set(CASES)) == len(CASES)
    for _, _, case in VARIANTS:
        c1_case = case.split("+")
        assert all(t for t in c1_case)
    assert verdict("ground", 2.0).startswith("fits TVC")
    assert verdict("ground", 3.5).startswith("does not fit")
    assert verdict("above", 5.2).startswith("borderline")
    assert "no TVC" in verdict("fair", 1.0)
    alpha = np.zeros((360, 640))
    alpha[100:200, 300:500] = 1.0
    alpha[100:200, 300:310] = np.linspace(0.2, 0.8, 10)
    x, y = edge_window(alpha, CROP_SRC)
    assert 0 <= x <= 320 and 0 <= y <= 180 and x <= 310 and x + CROP_SRC[0] >= 300, (x, y)
    from PIL import Image
    tile = Image.new("RGB", (60, 30), (90, 90, 200))
    img = compose([[(tile, "a"), (tile, "b")], [(tile, "c"), (tile, "d")]], HEADER, (60, 30))
    assert img.size[0] == 2 * 60 + 3 * 8
    print("selftest passed")


register_series("e40-owner-cost", stages(), collect, exclusive=True, who="e40-own")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    a = parser.parse_args()
    if a.selftest:
        selftest()
