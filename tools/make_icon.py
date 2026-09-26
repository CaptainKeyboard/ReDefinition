"""Draws the mod's pictures: the toolbar icon, the engineer view's button, and the
planet for a forum post.

    python tools/make_icon.py

**The toolbar icon** is a bank of sliders, in the stock toolbar's own colours: the
button's grey is #3e3d44, the glyphs are #e5e5e8, both read from a screenshot of KSP's
launcher. It says what the button opens -- every graphics mod's settings on one panel.
KSP draws the button in a square box, so the icon fills its square. It goes to
GameData/ReDefinition/Icons/ReDefinitionIcon.png at 128x128, the size Kopernicus ships
and hands to AddModApplication.

**The picture for a page** is "Re:" on a planet's night side, the atmosphere lit at the
lower right. It is too fine for a toolbar button but carries a forum post or a mod site,
where it stands large. It goes to .local/icon.

Everything is drawn at 1024 and scaled down, so the edges are smooth. In the planet the
light comes from the lower right: its rim, the wash across the letters, their shadow and
the bright edge on the side facing the sun all follow that one direction.

Needs Pillow (`pip install pillow`).
"""
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

BIG = 1024
LIGHT = (0.72, 0.69)          # towards the lower right
SOFTNESS = 0.78
FLOOR = 0.07                  # light left where the sun does not reach, so the disc reads as a circle

SPACE = (6, 8, 15, 255)
DISC_DARK = (11, 15, 25)
DISC_LIT = (20, 31, 52)
TEXT_DARK = (150, 162, 178)
TEXT_LIGHT = (252, 254, 255)
RIM_COLOUR = (186, 230, 255)

DISC_CENTRE = (512, 512)
DISC_RADIUS = 400
LABEL = "Re:"
LABEL_SIZE = 380

# KSP's toolbar, sampled from a screenshot of it.
BUTTON_GREY = (62, 61, 68, 255)
GLYPH_WHITE = (229, 229, 232, 255)
ACCENT = (120, 190, 245, 255)        # the blue this mod uses elsewhere

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IN_GAME = os.path.join(ROOT, "GameData", "ReDefinition", "Icons", "ReDefinitionIcon.png")
LARGE = os.path.join(ROOT, ".local", "icon")


def font(size):
    for name in ("bahnschrift.ttf", "segoeuib.ttf", "arialbd.ttf"):
        path = "C:/Windows/Fonts/" + name
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    raise SystemExit("no font found: the icon needs Bahnschrift, Segoe UI Bold or Arial Bold")


def direction_ramp(scale=1.0, floor=0.0):
    """A ramp along the light's direction, bright towards the sun."""
    ramp = Image.new("L", (BIG, BIG), 0)
    px = ramp.load()
    nx, ny = LIGHT
    length = (nx * nx + ny * ny) ** 0.5
    nx, ny = nx / length, ny / length
    for y in range(0, BIG, 2):
        for x in range(0, BIG, 2):
            t = ((x / float(BIG)) - 0.5) * nx + ((y / float(BIG)) - 0.5) * ny
            v = max(0.0, min(1.0, (t + 0.5 * scale) / scale))
            value = int(255 * (floor + (1.0 - floor) * (v ** 2.2)))
            for dx in (0, 1):
                for dy in (0, 1):
                    if x + dx < BIG and y + dy < BIG:
                        px[x + dx, y + dy] = value
    return ramp


def disc_mask():
    mask = Image.new("L", (BIG, BIG), 0)
    ImageDraw.Draw(mask).ellipse([DISC_CENTRE[0] - DISC_RADIUS, DISC_CENTRE[1] - DISC_RADIUS,
                                  DISC_CENTRE[0] + DISC_RADIUS, DISC_CENTRE[1] + DISC_RADIUS], fill=255)
    return mask


def atmosphere(mask, light):
    """The lit edge, grown outwards from the disc in bands from deep blue to white."""
    glow = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0))
    hard = mask.point(lambda v: 255 if v > 128 else 0)
    for blur, colour, strength in ((78, (16, 46, 104), 1.30),
                                   (40, (38, 128, 216), 1.25),
                                   (18, (96, 190, 246), 1.10),
                                   (7, (198, 238, 255), 0.95)):
        band = ImageChops.subtract(mask.filter(ImageFilter.GaussianBlur(blur)), hard)
        band = ImageChops.multiply(band, light)
        layer = Image.new("RGBA", (BIG, BIG), colour + (255,))
        layer.putalpha(band.point(lambda v: min(255, int(v * strength))))
        glow = Image.alpha_composite(glow, layer)
    return glow


def body(mask, light):
    """The night side, a shade above the background and lighter towards the terminator."""
    night = Image.new("RGBA", (BIG, BIG), DISC_DARK + (255,))
    lit = Image.new("RGBA", (BIG, BIG), DISC_LIT + (255,))
    lit.putalpha(light.point(lambda v: int(v * 0.85)))
    night = Image.alpha_composite(night, lit)
    night.putalpha(ImageChops.multiply(night.split()[3], mask))
    return night


def stars(mask):
    layer = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    for x, y, r, a in ((150, 170, 5, 200), (330, 110, 4, 150), (90, 380, 4, 160), (520, 90, 4, 130)):
        draw.ellipse([x - r, y - r, x + r, y + r], fill=(235, 245, 255, a))
    layer.putalpha(ImageChops.multiply(layer.split()[3], mask.point(lambda v: 255 - v)))
    return layer


def label_mask():
    mask = Image.new("L", (BIG, BIG), 0)
    ImageDraw.Draw(mask).text(DISC_CENTRE, LABEL, font=font(LABEL_SIZE), fill=255, anchor="mm")
    return mask


def label_wash():
    """Dim where the night is, near white towards the sun."""
    wash = Image.new("RGB", (BIG, BIG))
    px = wash.load()
    nx, ny = LIGHT
    length = (nx * nx + ny * ny) ** 0.5
    nx, ny = nx / length, ny / length
    for y in range(0, BIG, 2):
        for x in range(0, BIG, 2):
            t = ((x / float(BIG)) - 0.5) * nx + ((y / float(BIG)) - 0.5) * ny
            f = max(0.0, min(1.0, t * 1.9 + 0.5))
            colour = tuple(int(TEXT_DARK[i] + (TEXT_LIGHT[i] - TEXT_DARK[i]) * f) for i in range(3))
            for dx in (0, 1):
                for dy in (0, 1):
                    if x + dx < BIG and y + dy < BIG:
                        px[x + dx, y + dy] = colour
    return wash


def label_shadow(mask):
    """Cast away from the light, so the letters lie on the planet."""
    moved = Image.new("L", (BIG, BIG), 0)
    moved.paste(mask.filter(ImageFilter.GaussianBlur(14)), (-9, -9))
    layer = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 255))
    layer.putalpha(moved.point(lambda v: int(v * 0.55)))
    return layer


def label_rim(mask):
    """The edges facing the sun: the glyph minus itself, moved away from it."""
    moved = Image.new("L", (BIG, BIG), 0)
    moved.paste(mask, (-7, -7))
    edge = ImageChops.subtract(mask, moved).filter(ImageFilter.GaussianBlur(3))
    layer = Image.new("RGBA", (BIG, BIG), RIM_COLOUR + (255,))
    layer.putalpha(edge.point(lambda v: int(v * 0.85)))
    return layer


def draw_sliders():
    """Three sliders, the middle one's knob in the accent colour.

    Each knob keeps a gap of background around it, or the knob and its track merge
    into one stroke once the icon is drawn at the size of a toolbar button.
    """
    image = Image.new("RGBA", (BIG, BIG), BUTTON_GREY)
    draw = ImageDraw.Draw(image)

    track_width, knob_w, knob_h = 44, 190, 78
    top, bottom = 225, 800
    at = (0.62, 0.30, 0.50)          # where each knob sits on its track

    for i in range(3):
        x = BIG / 4.0 * (i + 1)
        half = track_width / 2.0
        draw.rounded_rectangle([x - half, top, x + half, bottom], radius=half, fill=GLYPH_WHITE)

        cy = top + (bottom - top) * at[i]
        draw.rounded_rectangle([x - knob_w / 2.0, cy - knob_h / 2.0,
                                x + knob_w / 2.0, cy + knob_h / 2.0],
                               radius=knob_h / 2.0, fill=ACCENT if i == 1 else GLYPH_WHITE)
        draw.rounded_rectangle([x - knob_w / 2.0 - 14, cy - knob_h / 2.0 - 14,
                                x + knob_w / 2.0 + 14, cy + knob_h / 2.0 + 14],
                               radius=(knob_h + 28) / 2.0, outline=BUTTON_GREY, width=14)
    return image


# The engineer view's button in the editor's mode bar, beside KSP's parts, actions,
# crew and cargo buttons: the same kind of tile -- a grey frame, a teal face lit from
# the top, a glyph -- drawn anew here, with "Δv" as its glyph. One tile per state the
# button takes: normal, pointed at, active (the view is open), and locked.
TILE_FRAME = (103, 103, 103, 255)
TILE_STATES = {
    "Normal": ((0, 150, 166), (0, 88, 99), (14, 44, 50, 255)),
    "Hover": ((40, 205, 220), (0, 120, 132), (249, 255, 242, 255)),
    "Active": ((70, 222, 236), (0, 140, 152), (249, 255, 242, 255)),
    "Disabled": ((22, 74, 82), (10, 40, 46), (40, 60, 64, 255)),
}
ENGINEER_TILES = os.path.join(ROOT, "GameData", "ReDefinition", "Icons", "EngineerButton_%s.png")


def draw_engineer_tile(face_top, face_bottom, glyph):
    image = Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    left, top, right, bottom = 22, 14, BIG - 22, BIG - 50
    chamfer = 150
    outline = [(left, top), (right, top), (right, bottom - chamfer), (right - chamfer, bottom),
               (left + chamfer, bottom), (left, bottom - chamfer)]
    draw.polygon(outline, fill=TILE_FRAME)

    inset = 40
    face = [(left + inset, top + inset), (right - inset, top + inset), (right - inset, bottom - chamfer),
            (right - chamfer, bottom - inset), (left + chamfer, bottom - inset), (left + inset, bottom - chamfer)]
    mask = Image.new("L", (BIG, BIG), 0)
    ImageDraw.Draw(mask).polygon(face, fill=255)
    ramp = Image.new("RGBA", (BIG, BIG))
    ramp_draw = ImageDraw.Draw(ramp)
    for y in range(BIG):
        f = min(1.0, max(0.0, (y - top) / float(bottom - top)))
        colour = tuple(int(face_top[i] + (face_bottom[i] - face_top[i]) * f) for i in range(3)) + (255,)
        ramp_draw.line([(0, y), (BIG, y)], fill=colour)
    image.paste(ramp, (0, 0), mask)
    # A light band at the top of the face, as the stock tiles have.
    band = Image.new("RGBA", (BIG, BIG), (255, 255, 255, 0))
    ImageDraw.Draw(band).rectangle([left + inset, top + inset, right - inset, top + inset + 70], fill=(255, 255, 255, 46))
    band_mask = ImageChops.multiply(mask, Image.new("L", (BIG, BIG), 255))
    image = Image.alpha_composite(image, Image.composite(band, Image.new("RGBA", (BIG, BIG), (0, 0, 0, 0)), band_mask))

    draw = ImageDraw.Draw(image)
    # Δ: a triangle drawn as a thick outline; v: two strokes meeting at the foot.
    stroke = 62
    cx, base, apex = 390, 700, 300
    half = 190
    tri = [(cx, apex), (cx + half, base), (cx - half, base)]
    draw.line(tri + [tri[0]], fill=glyph, width=stroke, joint="curve")
    vx, vtop, vbottom, vhalf = 690, 470, 700, 105
    draw.line([(vx - vhalf, vtop), (vx, vbottom), (vx + vhalf, vtop)], fill=glyph, width=stroke - 8, joint="curve")
    return image


def draw_planet():
    mask = disc_mask()
    light = direction_ramp(SOFTNESS, FLOOR)

    image = Image.new("RGBA", (BIG, BIG), SPACE)
    image = Image.alpha_composite(image, stars(mask))
    image = Image.alpha_composite(image, atmosphere(mask, light))
    image = Image.alpha_composite(image, body(mask, light))

    letters = label_mask()
    image = Image.alpha_composite(image, label_shadow(letters))
    wash = label_wash().convert("RGBA")
    wash.putalpha(letters)
    image = Image.alpha_composite(image, wash)
    image = Image.alpha_composite(image, label_rim(letters))
    return image


def main():
    os.makedirs(os.path.dirname(IN_GAME), exist_ok=True)
    draw_sliders().resize((128, 128), Image.LANCZOS).save(IN_GAME)
    print("in the game:", os.path.relpath(IN_GAME, ROOT))

    for state, (face_top, face_bottom, glyph) in TILE_STATES.items():
        path = ENGINEER_TILES % state
        draw_engineer_tile(face_top, face_bottom, glyph).resize((128, 128), Image.LANCZOS).save(path)
        print("in the game:", os.path.relpath(path, ROOT))

    planet = draw_planet()
    os.makedirs(LARGE, exist_ok=True)
    for size in (512, 256):
        path = os.path.join(LARGE, "ReDefinition_%d.png" % size)
        planet.resize((size, size), Image.LANCZOS).save(path)
        print("for a page:", os.path.relpath(path, ROOT))


if __name__ == "__main__":
    main()
