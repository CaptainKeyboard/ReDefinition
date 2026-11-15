#!/usr/bin/env python3
"""The label of a picture of the cloud work (E40 PP, .local/plans/atmospheres/E40-player-parity.md): what a picture is evidence of, written into its header.

    python tools/picture_label.py <picture.png> --game N                       "game start N"
    python tools/picture_label.py <picture.png> --parity "colour 2.1 %, ..."   "player (parity checked, residual colour 2.1 %, ...)"
    python tools/picture_label.py <picture.png> --working                      "player working view (not evidence)"
    python tools/picture_label.py <picture.png> --read                         prints the label a picture carries
    python tools/picture_label.py --selftest

The rule: a frame of the game is "game start N". A picture of the weather player is "player (parity checked, residual X)" only where the parity check of
tools/weather_metrics/e40pp.py ran for that view and state (X its measured residuals against the game's frame); every other player picture (synthetic sheets, variants,
cfg candidates) is "player working view (not evidence)". The label is drawn as a band above the picture (the picture itself is not covered) and kept as the PNG text
chunk "ReDefinition-label", so a tool can read it back. A picture that already carries a band gets it replaced, not stacked.
"""
import argparse
import os
import sys

KEY = "ReDefinition-label"
BAND_KEY = "ReDefinition-label-band"
BAND = 28


def text_for(game=None, parity=None, working=False):
    if sum(x is not None and x is not False for x in (game, parity, working or None)) != 1:
        raise ValueError("exactly one of game, parity, working")
    if game is not None:
        return "game start %s" % game
    if parity is not None:
        return "player (parity checked, residual %s)" % parity
    return "player working view (not evidence)"


def kind_colour(text):
    """The band's colour: the game blue-grey, a checked replay green, a working view orange."""
    if text.startswith("game start"):
        return (40, 52, 70)
    if text.startswith("player (parity checked"):
        return (28, 84, 44)
    return (120, 64, 12)


def label_image(img, text):
    """A copy of the PIL image with the band on top (an earlier band of this tool removed first)."""
    from PIL import Image, ImageDraw
    img = img.convert("RGB")
    band = int(img.info.get(BAND_KEY, 0) or 0) if hasattr(img, "info") else 0
    if band > 0 and band < img.height:
        img = img.crop((0, band, img.width, img.height))
    out = Image.new("RGB", (img.width, img.height + BAND), kind_colour(text))
    out.paste(img, (0, BAND))
    draw = ImageDraw.Draw(out)
    font = None
    try:
        from PIL import ImageFont
        font = ImageFont.truetype("arial.ttf", 18)
    except (OSError, ImportError):
        font = None
    draw.text((8, 4), text, fill=(255, 255, 255), font=font)
    return out


def write(path, text, out=None):
    from PIL import Image, PngImagePlugin
    with Image.open(path) as src:
        src.load()
        labelled = label_image(src, text)
    meta = PngImagePlugin.PngInfo()
    meta.add_text(KEY, text)
    meta.add_text(BAND_KEY, str(BAND))
    labelled.save(out or path, pnginfo=meta)
    return out or path


def read(path):
    from PIL import Image
    with Image.open(path) as img:
        return img.info.get(KEY)


def label_array(rgb, text, path):
    """Writes a float RGB array (0..1, h x w x 3) as a labelled PNG (the series tools call this for their picture pairs)."""
    import numpy as np
    from PIL import Image, PngImagePlugin
    img = Image.fromarray(np.clip(np.rint(np.asarray(rgb) * 255.0), 0, 255).astype(np.uint8), "RGB")
    meta = PngImagePlugin.PngInfo()
    meta.add_text(KEY, text)
    meta.add_text(BAND_KEY, str(BAND))
    label_image(img, text).save(path, pnginfo=meta)
    return path


def selftest():
    import tempfile
    from PIL import Image
    assert text_for(game=62) == "game start 62"
    assert text_for(parity="colour 2.0 %") == "player (parity checked, residual colour 2.0 %)"
    assert text_for(working=True) == "player working view (not evidence)"
    for bad in (dict(), dict(game=1, working=True)):
        try:
            text_for(**bad)
        except ValueError:
            pass
        else:
            raise AssertionError(bad)
    folder = tempfile.mkdtemp()
    path = os.path.join(folder, "p.png")
    Image.new("RGB", (64, 40), (200, 10, 10)).save(path)
    write(path, text_for(working=True))
    assert read(path) == "player working view (not evidence)"
    with Image.open(path) as img:
        assert img.size == (64, 40 + BAND), img.size
        assert img.getpixel((5, 39 + BAND)) == (200, 10, 10)
    # relabelled: the band is replaced, not stacked
    write(path, text_for(game=62))
    assert read(path) == "game start 62"
    with Image.open(path) as img:
        assert img.size == (64, 40 + BAND), img.size
    print("selftest passed")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("picture", nargs="?")
    g = p.add_mutually_exclusive_group()
    g.add_argument("--game", metavar="N")
    g.add_argument("--parity", metavar="RESIDUAL")
    g.add_argument("--working", action="store_true")
    g.add_argument("--read", action="store_true")
    g.add_argument("--selftest", action="store_true")
    p.add_argument("--out")
    a = p.parse_args(argv)
    if a.selftest:
        selftest()
        return 0
    if not a.picture:
        p.error("a picture is needed")
    if a.read:
        print(read(a.picture) or "(no label)")
        return 0
    text = text_for(game=a.game, parity=a.parity, working=a.working)
    print("%s: %s" % (write(a.picture, text, a.out), text))
    return 0


if __name__ == "__main__":
    sys.exit(main())
