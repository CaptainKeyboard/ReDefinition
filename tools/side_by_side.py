"""Shots of the same names from two light probe runs side by side, one picture each,
captioned: as with Scatterer and without it.

Usage: python tools/side_by_side.py <left run folder> <right run folder> <out folder>
      [--labels "with Scatterer" "without Scatterer"] [name ...]
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont


def main():
    """Write one captioned picture per shot name found in both runs (or named) into the out folder."""
    args = sys.argv[1:]
    left, right, out = args[0], args[1], args[2]
    rest = args[3:]
    labels = ['with Scatterer', 'without Scatterer']
    if rest[:1] == ['--labels']:
        labels = rest[1:3]
        rest = rest[3:]
    names = rest or sorted(f[:-4] for f in os.listdir(left)
                           if f.endswith('.png') and os.path.exists(os.path.join(right, f)))
    os.makedirs(out, exist_ok=True)
    font = ImageFont.load_default()
    for name in names:
        a = Image.open(os.path.join(left, name + '.png')).convert('RGB')
        b = Image.open(os.path.join(right, name + '.png')).convert('RGB').resize(a.size)
        # Half size each, the pair as wide as one shot, a band above for the captions.
        w, h = a.size[0] // 2, a.size[1] // 2
        band = 28
        pair = Image.new('RGB', (2 * w, h + band), (0, 0, 0))
        pair.paste(a.resize((w, h)), (0, band))
        pair.paste(b.resize((w, h)), (w, band))
        draw = ImageDraw.Draw(pair)
        draw.text((8, 8), name + ' - ' + labels[0], fill=(255, 255, 255), font=font)
        draw.text((w + 8, 8), name + ' - ' + labels[1], fill=(255, 255, 255), font=font)
        pair.save(os.path.join(out, name + '.png'))
        print(os.path.join(out, name + '.png'))


if __name__ == '__main__':
    main()
