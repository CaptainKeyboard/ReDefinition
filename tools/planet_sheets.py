"""Makes a sheet of each body's pictures from tools/planet_shots.ps1: per pose the raytracing off, on Light and on Full side by side.

Usage: python planet_sheets.py <the planets folder of ReDefinitionCaptures> [--width 900]

Writes <body>-sheet.jpg beside each body's folder.
"""
import argparse
import glob
import os
import re

from PIL import Image, ImageDraw

MODES = ("Off", "Light", "Full")


def sheet(folder, body, width):
    poses = sorted({int(re.search(r"-v(\d+)-", p).group(1)) for p in glob.glob(os.path.join(folder, body + "-v*-Off.png"))})
    if not poses:
        return None
    first = Image.open(os.path.join(folder, "%s-v%d-Off.png" % (body, poses[0])))
    height = int(width * first.height / first.width)
    out = Image.new("RGB", (width * len(MODES), (height + 22) * len(poses)), (20, 20, 20))
    draw = ImageDraw.Draw(out)
    for row, pose in enumerate(poses):
        for column, mode in enumerate(MODES):
            path = os.path.join(folder, "%s-v%d-%s.png" % (body, pose, mode))
            if not os.path.exists(path):
                continue
            picture = Image.open(path).convert("RGB").resize((width, height), Image.LANCZOS)
            out.paste(picture, (column * width, row * (height + 22) + 22))
            draw.text((column * width + 8, row * (height + 22) + 5), "%s  pose %d  %s" % (body, pose, mode), fill=(235, 235, 235))
    target = os.path.join(os.path.dirname(folder), body + "-sheet.jpg")
    out.save(target, quality=88)
    return target


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("planets")
    parser.add_argument("--width", type=int, default=900)
    args = parser.parse_args()
    for folder in sorted(glob.glob(os.path.join(args.planets, "*"))):
        if os.path.isdir(folder):
            made = sheet(folder, os.path.basename(folder), args.width)
            if made:
                print(made)


if __name__ == "__main__":
    main()
