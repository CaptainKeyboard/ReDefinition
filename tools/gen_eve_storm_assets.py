#!/usr/bin/env python3
"""Writes ReDefinition Atmospheres' own storm sound and snow flake sheet for the EVE storm layer (master plan 46.3 E4; the owner 2026-11-26: nothing of
another author's pack, the sound synthesised, the snow visible).

  python tools/gen_eve_storm_assets.py            write both files
  python tools/gen_eve_storm_assets.py --check    exit 1 when a checked-in file differs from what this makes

Both are made from fixed seeds, so the output is the same on every run (numpy and Pillow; the check compares bytes).

The storm sound (Sounds/ReDefinitionStormWind.wav, 30 s, 22050 Hz, mono, 16 bit): wind is broadband noise with most energy below a few kHz and a slow
gust envelope. Built in the frequency domain with random phases over the whole length, so the loop point is continuous (the signal is periodic):
pink noise (power ~ 1/f) band-limited to 150-1500 Hz, plus a weak band at 1-4 kHz for the whistle; the gusts are a sum of slow periodic sines
(0.05-0.3 Hz, periods that divide the length, so the loop holds) giving about 8 dB of depth. (.local/work/eve-values-derivation.md, section 7.)

The flake sheet (Textures/PluginData/ReDefinitionSnowFlakes.png, 1024 x 1024, 4 x 4 cells of 256): white flakes in alpha. Flakes at game distances
are near or below a pixel, so most cells are soft discs; some are stellar dendrites with six-fold symmetry (the hexagonal habit of ice) and some
aggregates of several (falling snow is mostly aggregates). Each cell keeps a transparent border against mip bleeding. (Same file, section 8.)
"""
import argparse
import io
import os
import sys
import wave

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FOLDER = os.path.join(ROOT, "GameData", "ReDefinition", "Atmospheres", "Clouds")
SOUND = os.path.join(FOLDER, "Sounds", "ReDefinitionStormWind.wav")
SHEET = os.path.join(FOLDER, "Textures", "PluginData", "ReDefinitionSnowFlakes.png")

RATE, SECONDS = 22050, 30
CELLS, CELL = 4, 256


def band(freqs, lo, hi, edge):
    """A smooth band-pass window: 1 inside [lo, hi], cosine flanks of width `edge` (Hz)."""
    w = np.zeros_like(freqs)
    inside = (freqs >= lo) & (freqs <= hi)
    w[inside] = 1.0
    low = (freqs > lo - edge) & (freqs < lo)
    w[low] = 0.5 - 0.5 * np.cos(np.pi * (freqs[low] - (lo - edge)) / edge)
    high = (freqs > hi) & (freqs < hi + edge)
    w[high] = 0.5 + 0.5 * np.cos(np.pi * (freqs[high] - hi) / edge)
    return w


def storm_sound():
    rng = np.random.default_rng(20261126)
    n = RATE * SECONDS
    freqs = np.fft.rfftfreq(n, 1.0 / RATE)
    pink = np.zeros_like(freqs)
    pink[1:] = 1.0 / np.sqrt(freqs[1:])          # amplitude ~ f^-1/2: power ~ 1/f
    spectrum = pink * (band(freqs, 150.0, 1500.0, 100.0) + 0.12 * band(freqs, 1000.0, 4000.0, 300.0))
    phases = np.exp(2j * np.pi * rng.random(len(freqs)))
    signal = np.fft.irfft(spectrum * phases, n)
    signal /= np.max(np.abs(signal))
    # Gusts: slow sines whose periods divide the loop (k / SECONDS Hz), about 8 dB from calm to gust.
    t = np.arange(n) / RATE
    gust = np.zeros(n)
    for k in (2, 3, 5, 7, 9):                    # 0.067 - 0.3 Hz
        gust += np.sin(2 * np.pi * k / SECONDS * t + rng.random() * 2 * np.pi) / k ** 0.5
    gust = (gust - gust.min()) / (gust.max() - gust.min())
    envelope = 10 ** ((gust - 1.0) * 8.0 / 20.0)  # -8 dB .. 0 dB
    out = signal * envelope
    out = 0.8 * out / np.max(np.abs(out))
    pcm = (out * 32767).astype("<i2")
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())
    return buf.getvalue()


def soft_disc(size, radius, blur):
    img = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(img)
    c = size / 2
    d.ellipse((c - radius, c - radius, c + radius, c + radius), fill=255)
    return img.filter(ImageFilter.GaussianBlur(blur))


def dendrite(size, rng):
    """A stellar plate: one arm with side branches in a 30 degree wedge, mirrored and turned six times."""
    img = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(img)
    c = size / 2
    arm = size * (0.36 + 0.06 * rng.random())
    width = int(6 + 6 * rng.random())
    branches = [(0.3 + 0.55 * rng.random(), arm * (0.15 + 0.25 * rng.random())) for _ in range(rng.integers(2, 5))]
    for k in range(6):
        a = np.pi / 3 * k
        for mirror in (1, -1):
            ex, ey = c + arm * np.cos(a), c + arm * np.sin(a)
            d.line((c, c, ex, ey), fill=255, width=width)
            for pos, length in branches:
                bx, by = c + arm * pos * np.cos(a), c + arm * pos * np.sin(a)
                b = a + mirror * np.pi / 3
                d.line((bx, by, bx + length * np.cos(b), by + length * np.sin(b)), fill=255, width=max(2, width - 3))
    return img.filter(ImageFilter.GaussianBlur(1.5))


def flake_sheet():
    rng = np.random.default_rng(20261127)
    sheet = Image.new("L", (CELLS * CELL, CELLS * CELL), 0)
    border = 8
    inner = CELL - 2 * border
    for i in range(CELLS * CELLS):
        kind = "disc" if i < 8 else ("dendrite" if i < 12 else "aggregate")
        if kind == "disc":
            cell = soft_disc(inner, inner * (0.18 + 0.2 * rng.random()), 2 + 4 * rng.random())
        elif kind == "dendrite":
            cell = dendrite(inner, rng)
        else:
            cell = Image.new("L", (inner, inner), 0)
            for _ in range(rng.integers(3, 8)):
                part = dendrite(inner, rng) if rng.random() < 0.5 else soft_disc(inner, inner * (0.1 + 0.12 * rng.random()), 2)
                part = part.rotate(rng.random() * 360).resize((int(inner * 0.6), int(inner * 0.6)))
                ox = int(inner * 0.2 + (rng.random() - 0.5) * inner * 0.3)
                oy = int(inner * 0.2 + (rng.random() - 0.5) * inner * 0.3)
                cell = Image.fromarray(np.maximum(np.asarray(cell), np.asarray(_placed(part, inner, ox, oy))))
        opacity = 0.55 + 0.45 * rng.random()
        cell = Image.fromarray((np.asarray(cell, dtype=np.float32) * opacity).astype(np.uint8))
        x, y = (i % CELLS) * CELL + border, (i // CELLS) * CELL + border
        sheet.paste(cell, (x, y))
    rgba = Image.merge("RGBA", (Image.new("L", sheet.size, 255),) * 3 + (sheet,))
    buf = io.BytesIO()
    rgba.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def _placed(part, size, ox, oy):
    canvas = Image.new("L", (size, size), 0)
    canvas.paste(part, (ox, oy))
    return canvas


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()
    made = {SOUND: storm_sound(), SHEET: flake_sheet()}
    if args.check:
        bad = [p for p, data in made.items() if not os.path.isfile(p) or open(p, "rb").read() != data]
        for p in bad:
            print("differs from the generator's output: " + os.path.relpath(p, ROOT))
        return 1 if bad else 0
    for p, data in made.items():
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data)
        print("wrote %s (%d bytes)" % (os.path.relpath(p, ROOT), len(data)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
