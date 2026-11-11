#!/usr/bin/env python3
"""A contact sheet from a view table: one row per view, three pictures (A, B, difference) with the metrics under each. Needs numpy
and Pillow; the difference and the metrics are those of tools/atmo_ab.py (which also refuses a pair from different scenes).

  python tools/atmo_sheet.py build <view table.json> <out.png> [--cell 480x270] [--threshold 0.02] [--require-expect]
  python tools/atmo_sheet.py selftest

The view table is {"title": "...", "views": [{"name": "fog near", "a": "<image A>", "b": "<image B>", "region": "x0,y0,x1,y1",
"why": "<why this view shows the phenomenon>", "expect": "<the expectation written before the run>"}, ...]}; the paths are relative to
the table. Layout: a title band, then per view a name band, the three pictures side by side, a caption band under them and, where
the view has "why" or "expect", a text band with both (the reviewer receives expectation, pictures and metrics from one sheet). The
metrics under A and B are the mean luma; under the difference the area fraction, the mean and peak luma delta and the sign of the
change. With --require-expect the build refuses a table in which any view lacks "why" or "expect" (exit code 2).
"""
import textwrap
import argparse
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import atmo_ab  # noqa: E402

MARGIN = 8
TITLE_BAND = 22
NAME_BAND = 18
CAPTION_BAND = 30
BACKGROUND = (24, 24, 24)
TEXT = (230, 230, 230)


TEXT_LINE = 12
CHAR_WIDTH = 6  # of the default font


def view_text(view, cell):
    """The lines of the text band of a view: its reason, then its expectation, wrapped to the width of the row."""
    chars = max(20, (3 * (cell[0] + MARGIN) - MARGIN) // CHAR_WIDTH)
    lines = []
    for key, label in (("why", "why: "), ("expect", "expect: ")):
        if view.get(key):
            lines += textwrap.wrap(label + str(view[key]), chars)
    return lines


def layout(count, cell, text_lines=0):
    """The sheet size and the top-left corner of each picture: [(row, column) -> (x, y)]. text_lines is the height of the text band
    in lines (the same for every row)."""
    w, h = cell
    width = MARGIN + 3 * (w + MARGIN)
    row_height = NAME_BAND + h + CAPTION_BAND + text_lines * TEXT_LINE + MARGIN
    height = TITLE_BAND + MARGIN + count * row_height
    corners = {}
    for r in range(count):
        for c in range(3):
            corners[(r, c)] = (MARGIN + c * (w + MARGIN), TITLE_BAND + MARGIN + r * row_height + NAME_BAND)
    return (width, height), corners


def _picture(a, cell):
    img = Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8))
    return img.resize(cell, Image.LANCZOS)


def build(table_path, out, cell=(480, 270), threshold=0.02, require_expect=False):
    """Writes the sheet; returns its size and the metrics of each view. Raises atmo_ab.Refusal for a pair of different scenes, and
    with require_expect for a view without a reason ("why") or an expectation ("expect")."""
    with open(table_path, encoding="utf-8") as f:
        table = json.load(f)
    base = os.path.dirname(os.path.abspath(table_path))
    views = table["views"]
    if require_expect:
        missing = [v.get("name", "?") for v in views if not (v.get("why") and v.get("expect"))]
        if missing:
            raise atmo_ab.Refusal("views without a reason and a written expectation: " + ", ".join(missing))
    texts = [view_text(v, cell) for v in views]
    text_lines = max([len(t) for t in texts] + [0])
    size, corners = layout(len(views), cell, text_lines)
    sheet = Image.new("RGB", size, BACKGROUND)
    draw = ImageDraw.Draw(sheet)
    draw.text((MARGIN, 4), table.get("title", ""), fill=TEXT)
    figures = []
    for r, view in enumerate(views):
        fa, fb = os.path.join(base, view["a"]), os.path.join(base, view["b"])
        atmo_ab.check_identical(atmo_ab.load_sidecar(fa), atmo_ab.load_sidecar(fb), view["name"])
        a, b = atmo_ab.load_image(fa), atmo_ab.load_image(fb)
        region = atmo_ab._region(view.get("region")) if view.get("region") else None
        metrics, mask, dl = atmo_ab.difference(a, b, threshold, region)
        s = np.clip(np.abs(dl) * 4.0, 0, 1)
        heat = np.stack([np.where(dl > 0, s, 0), np.where(mask, 0.15, 0) * np.ones_like(s), np.where(dl < 0, s, 0)], axis=2)
        captions = ["A  mean luma %.4f" % atmo_ab.luma(a).mean(), "B  mean luma %.4f" % atmo_ab.luma(b).mean(),
                    "diff  area %.4g  mean %.4g  peak %.4g  sign %+.2f" % (metrics["area_fraction"], metrics["mean_delta"], metrics["peak_delta"], metrics["luma_sign"])]
        x0, y0 = corners[(r, 0)]
        draw.text((x0, y0 - NAME_BAND + 2), view["name"], fill=TEXT)
        for c, picture in enumerate((a, b, heat)):
            x, y = corners[(r, c)]
            sheet.paste(_picture(picture, cell), (x, y))
            draw.text((x, y + cell[1] + 3), captions[c], fill=TEXT)
        x, y = corners[(r, 0)]
        for i, line in enumerate(texts[r]):
            draw.text((x, y + cell[1] + CAPTION_BAND + i * TEXT_LINE), line, fill=TEXT)
        figures.append(dict(metrics, name=view["name"], why=view.get("why"), expect=view.get("expect")))
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    sheet.save(out)
    return size, corners, figures


def selftest():
    import shutil
    import tempfile
    root = tempfile.mkdtemp(prefix="atmo-sheet-selftest-")
    side = {"pose": [0, 0, 0, 0, 0], "seed": 1, "ut": 0, "state": "s", "resolution": [320, 180], "upscaler": "native"}
    try:
        views = []
        base = atmo_ab._scene()
        for name, size in (("small", 12), ("medium", 30), ("none", 0)):
            planted = base.copy()
            if size:
                planted[60:60 + size, 100:100 + size] *= 0.6
            atmo_ab._write(root, name + "_a", base, side)
            atmo_ab._write(root, name + "_b", planted, side)
            views.append({"name": name, "a": name + "_a.png", "b": name + "_b.png"})
        table = os.path.join(root, "views.json")
        with open(table, "w") as f:
            json.dump({"title": "selftest", "views": views}, f)
        out = os.path.join(root, "sheet.png")
        size, corners, figures = build(table, out, cell=(160, 90))
        sheet = Image.open(out)
        expected = (MARGIN + 3 * (160 + MARGIN), TITLE_BAND + MARGIN + 3 * (NAME_BAND + 90 + CAPTION_BAND + MARGIN))
        assert sheet.size == expected == size, (sheet.size, expected)
        assert len(corners) == 9
        # The pictures sit where the layout says: picture A of the first view has the mean colour of image A.
        x, y = corners[(0, 0)]
        mean_a = np.asarray(sheet.crop((x, y, x + 160, y + 90))).reshape(-1, 3).mean(axis=0)
        mean_src = (atmo_ab.load_image(os.path.join(root, "small_a.png")).reshape(-1, 3).mean(axis=0) * 255)
        assert np.abs(mean_a - mean_src).max() < 3, (mean_a, mean_src)
        # The difference column of the view with nothing planted is empty (only the background of the picture), the others are not.
        by_name = {f["name"]: f for f in figures}
        assert by_name["none"]["pixels"] == 0 and by_name["small"]["pixels"] > 0 and by_name["medium"]["pixels"] > by_name["small"]["pixels"]
        x, y = corners[(2, 2)]
        empty = np.asarray(sheet.crop((x, y, x + 160, y + 90))).max()
        assert empty == 0, empty
        # A table with reasons and expectations: the text band grows the sheet by its lines and shows both; --require-expect refuses
        # a table in which a view lacks them.
        for v in views:
            v["why"] = "the planted patch is visible against the flat sky"
            v["expect"] = "area equals the planted share; nothing planted gives an empty difference"
        with open(table, "w") as f:
            json.dump({"title": "selftest", "views": views}, f)
        lines = max(len(view_text(v, (160, 90))) for v in views)
        size2, corners2, figures2 = build(table, os.path.join(root, "sheet2.png"), cell=(160, 90), require_expect=True)
        assert lines >= 2 and size2[1] == size[1] + 3 * lines * TEXT_LINE, (size2, size, lines)
        assert figures2[0]["expect"].startswith("area equals")
        views[1].pop("expect")
        with open(table, "w") as f:
            json.dump({"title": "selftest", "views": views}, f)
        try:
            build(table, os.path.join(root, "sheet3.png"), cell=(160, 90), require_expect=True)
            raise AssertionError("a view without an expectation was accepted")
        except atmo_ab.Refusal as e:
            assert "medium" in str(e), e
        print("sheet %dx%d, 3 views x 3 pictures, areas small %.5f medium %.5f none %.1f" % (size[0], size[1], by_name["small"]["area_fraction"],
                                                                                         by_name["medium"]["area_fraction"], by_name["none"]["area_fraction"]))
        print("selftest passed")
    finally:
        shutil.rmtree(root, ignore_errors=True)


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build")
    b.add_argument("table")
    b.add_argument("out")
    b.add_argument("--cell", default="480x270")
    b.add_argument("--threshold", type=float, default=0.02)
    b.add_argument("--require-expect", action="store_true")
    sub.add_parser("selftest")
    args = p.parse_args(argv)
    if args.cmd == "selftest":
        selftest()
        return 0
    try:
        size, _, _ = build(args.table, args.out, tuple(int(v) for v in args.cell.split("x")), args.threshold, args.require_expect)
    except atmo_ab.Refusal as e:
        print("REFUSED: %s" % e)
        return 2
    print("wrote %s (%dx%d)" % (args.out, size[0], size[1]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
