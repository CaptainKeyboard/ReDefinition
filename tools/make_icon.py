"""Draws the mod's pictures: the toolbar icon, the engineer view's button, the
planner's symbols, and the planet for a forum post.

    python tools/make_icon.py

**The toolbar icon** is a bank of sliders, in the stock toolbar's own colours: the
button's grey is #3e3d44, the glyphs are #e5e5e8, both read from a screenshot of KSP's
launcher. It says what the button opens -- every graphics mod's settings on one panel.
KSP draws the button in a square box, so the icon fills its square. It goes to
GameData/ReDefinition/Icons/ReDefinitionIcon.png at 128x128, the size Kopernicus ships
and hands to AddModApplication.

**The planner's symbols** are white on clear, 64x64, for KSP's buttons to show them over
their own background: GameData/ReDefinition/Icons/Planner_*.png.

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


# The planner's symbols: white on clear, so that a button's own background shows
# through and the code can tint them. Each is drawn at 512 and stored at 64.
PLANNER_ICONS = os.path.join(ROOT, "GameData", "ReDefinition", "Icons", "Planner_%s.png")
PLANNER_SIZE = 512
WHITE = (255, 255, 255, 255)


def planner_canvas():
    image = Image.new("RGBA", (PLANNER_SIZE, PLANNER_SIZE), (0, 0, 0, 0))
    return image, ImageDraw.Draw(image)


def draw_surface():
    # A flag on the ground.
    image, draw = planner_canvas()
    draw.rounded_rectangle([60, 400, 452, 440], radius=20, fill=WHITE)
    draw.rectangle([170, 90, 202, 410], fill=WHITE)
    draw.polygon([(202, 96), (380, 150), (202, 206)], fill=WHITE)
    return image


def draw_orbit():
    # A body with a ring around it and a craft on the ring.
    image, draw = planner_canvas()
    draw.ellipse([186, 186, 326, 326], fill=WHITE)
    draw.ellipse([46, 136, 466, 376], outline=WHITE, width=30)
    draw.ellipse([390, 150, 460, 220], fill=WHITE)
    return image


def draw_chute():
    # A parachute's canopy, its lines and the capsule under it.
    image, draw = planner_canvas()
    draw.pieslice([56, 40, 456, 360], 180, 360, fill=WHITE)
    for x in (80, 256, 432):
        draw.line([(x, 200), (256, 380)], fill=WHITE, width=18)
    draw.polygon([(206, 380), (306, 380), (330, 460), (182, 460)], fill=WHITE)
    return image


def draw_aerobrake():
    # A path that dips into a body's air and leaves it again.
    image, draw = planner_canvas()
    draw.pieslice([-200, 400, 712, 1200], 180, 360, fill=WHITE)
    draw.arc([-280, 300, 792, 1300], 195, 345, fill=WHITE, width=14)
    path = [(40 + i * 8, 60 + 0.0021 * (40 + i * 8 - 256) ** 2 * -1 + 280) for i in range(0, 53)]
    draw.line(path, fill=WHITE, width=30, joint="curve")
    end = path[-1]
    draw.polygon([(end[0] + 30, end[1] - 50), (end[0] - 36, end[1] - 14), (end[0] + 14, end[1] + 30)], fill=WHITE)
    return image


def lander(draw, cx, top, scale=1.0):
    w, h = 110 * scale, 90 * scale
    draw.polygon([(cx - w * 0.6, top), (cx + w * 0.6, top), (cx + w, top + h), (cx - w, top + h)], fill=WHITE)
    leg = 26 * scale
    draw.line([(cx - w * 0.8, top + h), (cx - w * 1.5, top + h * 2.1)], fill=WHITE, width=int(leg))
    draw.line([(cx + w * 0.8, top + h), (cx + w * 1.5, top + h * 2.1)], fill=WHITE, width=int(leg))


def rocket(draw, cx, top, bottom):
    half = 62
    draw.rectangle([cx - half, top, cx + half, bottom - 50], fill=WHITE)
    draw.polygon([(cx - 40, bottom - 50), (cx + 40, bottom - 50), (cx + 70, bottom), (cx - 70, bottom)], fill=WHITE)


def draw_whole():
    # The lander on top of the rest.
    image, draw = planner_canvas()
    lander(draw, 256, 40, 0.85)
    rocket(draw, 256, 230, 470)
    return image


def draw_lander():
    image, draw = planner_canvas()
    lander(draw, 256, 150, 1.4)
    return image


def draw_rest():
    # The rest, its top open where the lander sat.
    image, draw = planner_canvas()
    rocket(draw, 256, 150, 470)
    draw.line([(170, 110), (342, 110)], fill=WHITE, width=18)
    return image


def draw_dot():
    image, draw = planner_canvas()
    draw.ellipse([96, 96, 416, 416], fill=WHITE)
    return image


def draw_pin():
    # A map pin: keeps a stage for a leg.
    image, draw = planner_canvas()
    draw.ellipse([126, 40, 386, 300], fill=WHITE)
    draw.polygon([(150, 230), (362, 230), (256, 480)], fill=WHITE)
    draw.ellipse([206, 120, 306, 220], fill=(0, 0, 0, 0))
    return image


def draw_remove():
    image, draw = planner_canvas()
    draw.line([(130, 130), (382, 382)], fill=WHITE, width=56)
    draw.line([(382, 130), (130, 382)], fill=WHITE, width=56)
    return image


PLANNER_DRAWINGS = {
    "Surface": draw_surface, "Orbit": draw_orbit, "Chute": draw_chute, "Aerobrake": draw_aerobrake,
    "Whole": draw_whole, "Lander": draw_lander, "Rest": draw_rest, "Dot": draw_dot, "Pin": draw_pin,
    "Remove": draw_remove,
}


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

    for name, drawing in PLANNER_DRAWINGS.items():
        path = PLANNER_ICONS % name
        drawing().resize((64, 64), Image.LANCZOS).save(path)
        print("in the game:", os.path.relpath(path, ROOT))

    planet = draw_planet()
    os.makedirs(LARGE, exist_ok=True)
    for size in (512, 256):
        path = os.path.join(LARGE, "ReDefinition_%d.png" % size)
        planet.resize((size, size), Image.LANCZOS).save(path)
        print("for a page:", os.path.relpath(path, ROOT))


if __name__ == "__main__":
    main()
