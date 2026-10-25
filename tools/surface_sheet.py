"""The probe's surface images (RtCamera.WriteSurfaceImages) as pictures and figures per region: what the
camera's ray met at each pixel, the shine the raytracing gives it (specular colour F0, smoothness), the
reflected light it traced, and, where the same view was shot with the raytracing's light but without its
reflections (mode Light beside Full), what the reflections add to the finished picture; where it was shot
without the raytracing (mode Off), how bright each region is there, and the space centre's lawn against the
terrain beside it with the raytracing and without.

  python tools/surface_sheet.py <run folder> [prefix ...] [--out <folder>]

For each <prefix>-surface.bin in the run folder (the scene probe's views: <shot>-v<i>-Full), a sheet
<prefix>-sheet.png and a table on the console and in <prefix>-regions.txt. Regions (RT_REGION_* in
RtContract.h; the space centre's ground split by the shine it gets, its lawn from its tarmac):

  terrain        the terrain at the drawn surface
  lawn           the space centre's ground where it shines (its lawn)
  tarmac         the space centre's ground without shine
  blade          a blade or other scatter at the drawn surface
  behind blade   ground behind a blade the rays meet in front of the drawn surface
  nothing        nothing near (far away)
  other          something else at the drawn surface (buildings, the vessel)
  other missed   something else in front of the drawn surface

Needs numpy and Pillow.
"""
import os
import re
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

REGIONS = [
    (1, 'terrain', (60, 170, 60)),
    (2, 'lawn', (240, 150, 30)),
    (-2, 'tarmac', (120, 120, 120)),
    (3, 'blade', (200, 230, 80)),
    (4, 'behind blade', (230, 40, 200)),
    (5, 'nothing', (40, 40, 90)),
    (6, 'other', (80, 140, 230)),
    (7, 'other missed', (230, 60, 60)),
]


def read_bin(path):
    """One of the probe's surface images (width, height, then RGBA floats) as an array, rows from the top."""
    with open(path, 'rb') as f:
        width, height = struct.unpack('<ii', f.read(8))
        data = np.frombuffer(f.read(), dtype='<f4')
    # Rows from the bottom in the file; from the top here, as the shot's.
    return data.reshape(height, width, 4)[::-1].copy()


def linear(png, size):
    """A screenshot resized to the size, from sRGB to linear light."""
    image = Image.open(png).convert('RGB').resize(size, Image.BILINEAR)
    srgb = np.asarray(image, dtype=np.float32) / 255.0
    return np.where(srgb <= 0.04045, srgb / 12.92, ((srgb + 0.055) / 1.055) ** 2.4)


def luma(rgb):
    """Rec. 709 luminance of linear RGB."""
    return rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722


def heat(value, top):
    """0 .. top as dark blue, green, yellow, red."""
    t = np.clip(value / max(top, 1e-9), 0.0, 1.0)
    stops = np.array([[10, 20, 80], [20, 160, 60], [240, 230, 40], [230, 40, 20]], dtype=np.float32)
    position = t * (len(stops) - 1)
    low = np.floor(position).astype(int).clip(0, len(stops) - 2)
    f = (position - low)[..., None]
    return (stops[low] * (1 - f) + stops[low + 1] * f).astype(np.uint8)


def regions_of(surface, mode):
    """Each pixel's region (RT_REGION_*, -2 for the space centre's ground without shine, 0 for the sky)."""
    region = np.rint(mode[..., 2] * 8.0).astype(int)
    shine = surface[..., :3].max(axis=2)
    region[(region == 2) & (shine <= 1e-4)] = -2
    region[mode[..., 3] < 0.5] = 0  # the sky
    return region


def label(image, text):
    """The image with the text drawn white on black at its top left corner."""
    draw = ImageDraw.Draw(image)
    font = ImageFont.load_default()
    draw.rectangle([0, 0, 8 + 7 * len(text), 16], fill=(0, 0, 0))
    draw.text((4, 2), text, fill=(255, 255, 255), font=font)
    return image


def sheet(folder, prefix, out):
    """Write the sheet and the region table of one view into out, and print the table."""
    names = {n: os.path.join(folder, prefix + '-' + n + '.bin')
             for n in ('surface', 'surfacemode', 'specular', 'gbuffer0', 'gbuffer1', 'sun', 'diffuse')}
    surface = read_bin(names['surface'])
    mode = read_bin(names['surfacemode'])
    height, width = surface.shape[:2]
    size = (width, height)
    optional = lambda n: read_bin(names[n]) if os.path.exists(names[n]) else np.zeros_like(surface)
    specular = optional('specular')
    # The drawn albedo (the proxy's copy, before the meadow's shade), the sun's visibility, the raytraced diffuse light.
    albedo = luma(optional('gbuffer0')[..., :3])
    sun = optional('sun')[..., 0]
    diffuse = luma(np.maximum(optional('diffuse')[..., :3], 0.0))
    meadow = mode[..., 1]
    region = regions_of(surface, mode)
    f0 = surface[..., :3].max(axis=2)
    smooth = surface[..., 3]
    spec_light = luma(np.maximum(specular[..., :3], 0.0))
    shot_png = os.path.join(folder, prefix + '.png')
    shot = linear(shot_png, size) if os.path.exists(shot_png) else np.zeros((height, width, 3), np.float32)
    # The same view in another mode: <shot>-v<i>-<mode>[-ini<n>].
    view = re.match(r'^(.*-v\d+)-([A-Za-z]+)(-ini\d+)?$', prefix)
    other = (lambda mode: os.path.join(folder, view.group(1) + '-' + mode + (view.group(3) or '') + '.png')) if view else None
    # Mode Light: the finished picture without the raytraced reflections.
    light_png = other('Light') if view and view.group(2) == 'Full' else ''
    added = luma(shot) - luma(linear(light_png, size)) if light_png and os.path.exists(light_png) else None
    # Without the raytracing (mode Off), its regions taken from this one.
    off_png = other('Off') if view else ''
    off = luma(linear(off_png, size)) if os.path.exists(off_png) else None

    lines = ['%-13s %7s %7s %7s %9s %9s %7s %7s %7s %7s %8s %8s %8s' % (
        'region', 'share', 'F0', 'smooth', 'traced', 'F0*trace', 'albedo', 'meadow', 'sun', 'diffuse', 'picture',
        'added' if added is not None else '', 'off' if off is not None else '')]
    total = max(1, int((region != 0).sum()))
    colours = np.zeros((height, width, 3), np.uint8)
    for code, name, colour in REGIONS:
        mask = region == code
        colours[mask] = colour
        n = int(mask.sum())
        if n == 0:
            continue
        row = '%-13s %6.1f%% %7.3f %7.3f %9.4f %9.5f %7.3f %7.3f %7.3f %7.3f %8.4f' % (
            name, 100.0 * n / total, f0[mask].mean(), smooth[mask].mean(), spec_light[mask].mean(),
            (f0 * spec_light)[mask].mean(), albedo[mask].mean(), meadow[mask].mean(), sun[mask].mean(), diffuse[mask].mean(),
            luma(shot)[mask].mean())
        row += ' %8.4f' % added[mask].mean() if added is not None else ' %8s' % ''
        if off is not None:
            row += ' %8.4f' % off[mask].mean()
        lines.append(row)
    # The space centre's lawn against the terrain beside it, with the raytracing and without.
    terrain, lawn = region == 1, region == 2
    if terrain.any() and lawn.any():
        ratio = luma(shot)[lawn].mean() / max(1e-6, luma(shot)[terrain].mean())
        line = 'lawn / terrain: %.3f' % ratio
        if off is not None:
            line += ', without the raytracing %.3f' % (off[lawn].mean() / max(1e-6, off[terrain].mean()))
        lines.append(line)
    table = '\n'.join(lines)
    print(prefix)
    print(table)
    with open(os.path.join(out, prefix + '-regions.txt'), 'w', encoding='utf-8') as f:
        f.write(table + '\n')

    tiles = [
        label(Image.open(shot_png).convert('RGB').resize(size) if os.path.exists(shot_png) else Image.new('RGB', size), 'picture'),
        label(Image.fromarray(colours), 'regions: terrain green, lawn orange, tarmac grey, blade yellow-green, behind blade magenta'),
        label(Image.fromarray(heat(f0, 0.25)), 'F0 (shine), 0 .. 0.25'),
        label(Image.fromarray(heat(smooth, 0.6)), 'smoothness, 0 .. 0.6'),
        label(Image.fromarray(heat(f0 * spec_light, max(1e-6, float(np.percentile(f0 * spec_light, 99))))),
              'F0 x traced reflection, to its 99th percentile'),
        label(Image.fromarray(heat(added, max(1e-6, float(np.percentile(np.abs(added), 99))))) if added is not None
              else Image.new('RGB', size), 'what the reflections add (Full - Light)' if added is not None else 'no Light shot'),
    ]
    grid = Image.new('RGB', (width * 2, height * 3))
    for i, tile in enumerate(tiles):
        grid.paste(tile, ((i % 2) * width, (i // 2) * height))
    grid.save(os.path.join(out, prefix + '-sheet.png'))


def main():
    """Make the sheet of every view named, or of every *-surface.bin in the run folder."""
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 1
    folder = args[0]
    out = folder
    prefixes = []
    rest = args[1:]
    while rest:
        if rest[0] == '--out':
            out = rest[1]
            rest = rest[2:]
        else:
            prefixes.append(rest[0])
            rest = rest[1:]
    os.makedirs(out, exist_ok=True)
    if not prefixes:
        prefixes = sorted(f[:-len('-surface.bin')] for f in os.listdir(folder) if f.endswith('-surface.bin'))
    for prefix in prefixes:
        sheet(folder, prefix, out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
