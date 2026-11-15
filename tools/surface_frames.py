#!/usr/bin/env python3
"""Compares the far terrain of the five frames of series K-S2 (SurfaceProbe).

  python tools/surface_frames.py <log> --captures <folder> [--box x0 y0 x1 y1] [--out sheet.png]
  python tools/surface_frames.py --pictures a.png b.png ... [--box x0 y0 x1 y1]

The log's lines 'surface: <pose>_<frame>: picture <file>' name the pictures (FRAMES below). The box is the far range's columns and rows in fractions of the picture (default: the
range of K-S1's cb_far picture, x 0.40 to 0.82, y 0.535 to 0.575; look at the pictures first, the two headings show other terrain). Per frame it prints the mean colour, the luminance, the chroma
((max - min) / max of the mean colour), the local contrast (the mean standard deviation of the luminance over 8 x 8 blocks) and the difference of the mean to the first frame, and
writes a sheet of the box crops stacked (frame label on the left). Needs Pillow and numpy.
"""
import argparse
import os
import re
import sys

FRAMES = ["p1_old_fog", "p1_baseline", "p1_drawn", "p1_no_weather", "p1_no_pass", "p1_no_cover", "p1_no_atmo", "p2_baseline", "p2_drawn", "p2_no_atmo"]


def pictures_from_log(path, folder):
    found = {}
    pattern = re.compile(r"surface: (\w+): picture (\S+)")
    with open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            m = pattern.search(line)
            if m and m.group(2) != "none":
                found[m.group(1)] = os.path.join(folder, os.path.basename(m.group(2).replace("\\", "/")))
    return [(f, found[f]) for f in FRAMES if f in found]


def measure(image, box):
    import numpy as np
    w, h = image.size
    x0, y0, x1, y1 = int(box[0] * w), int(box[1] * h), int(box[2] * w), int(box[3] * h)
    crop = np.asarray(image.convert("RGB").crop((x0, y0, x1, y1))).astype(float) / 255.0
    mean = crop.reshape(-1, 3).mean(axis=0)
    luma = crop @ np.array([0.2126, 0.7152, 0.0722])
    bh, bw = luma.shape[0] // 8 * 8, luma.shape[1] // 8 * 8
    blocks = luma[:bh, :bw].reshape(bh // 8, 8, bw // 8, 8).transpose(0, 2, 1, 3).reshape(-1, 64)
    contrast = float(blocks.std(axis=1).mean())
    chroma = float((mean.max() - mean.min()) / max(mean.max(), 1e-6))
    return mean, float(luma.mean()), chroma, contrast, crop


def main():
    p = argparse.ArgumentParser()
    p.add_argument("log", nargs="?")
    p.add_argument("--captures", default=".local/work/series-pictures/K-S2/captures")
    p.add_argument("--pictures", nargs="*")
    p.add_argument("--box", nargs=4, type=float, default=[0.40, 0.535, 0.82, 0.575])
    p.add_argument("--out")
    a = p.parse_args()
    from PIL import Image, ImageDraw
    if a.pictures:
        items = [(os.path.splitext(os.path.basename(f))[0], f) for f in a.pictures]
    elif a.log:
        items = pictures_from_log(a.log, a.captures)
    else:
        p.error("a log or --pictures")
    rows, crops, first = [], [], None
    for name, path in items:
        if not os.path.exists(path):
            print("missing:", name, path)
            continue
        mean, luma, chroma, contrast, crop = measure(Image.open(path), a.box)
        if first is None:
            first = mean
        rows.append((name, mean, luma, chroma, contrast, mean - first))
        crops.append((name, crop))
    print("%-12s %-22s %7s %7s %9s  %s" % ("frame", "mean rgb", "luma", "chroma", "contrast", "mean minus first"))
    for name, mean, luma, chroma, contrast, d in rows:
        print("%-12s %5.3f %5.3f %5.3f    %7.3f %7.3f %9.4f  %+.3f %+.3f %+.3f" % (name, mean[0], mean[1], mean[2], luma, chroma, contrast, d[0], d[1], d[2]))
    if a.out and crops:
        import numpy as np
        width = max(c.shape[1] for _, c in crops)
        sheet = Image.new("RGB", (width + 110, sum(c.shape[0] + 4 for _, c in crops)), (20, 20, 20))
        draw = ImageDraw.Draw(sheet)
        y = 0
        for name, crop in crops:
            sheet.paste(Image.fromarray((crop * 255).astype("uint8")), (110, y))
            draw.text((4, y + 4), name, fill=(255, 255, 255))
            y += crop.shape[0] + 4
        sheet.save(a.out)
        print("sheet:", a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
