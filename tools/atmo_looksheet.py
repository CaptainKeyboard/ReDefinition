#!/usr/bin/env python3
"""The early look sheet of the own cloud system: a fixed set of scenes (tools/weather_metrics/looksheet.py) rendered in the weather player,
each as the natural view and the aid view (optical depth in false colour) side by side, a text column with what real clouds look like in
that situation, and an index.md with links to public-domain photographs. One command regenerates it after every wave:

  python tools/atmo_looksheet.py [--no-build] [--out <folder>] [--who <id>]
      builds the player (Unity lock), renders the scenes (GPU lock, through tools/build_weather_player.py --series T-look) and composes
      the sheets into <MAINREPO>/.local/work/looksheet/<date>/: sheet-<group>.png per scene group and index.md
  python tools/atmo_looksheet.py --compose-only [--frames <folder>] [--out <folder>]
      composes from the pictures of an earlier run (default folder build/WeatherPlayer/T-look-scene)
  python tools/atmo_looksheet.py --selftest

The aid view is the left half of the player's aid picture: log10 of the optical depth along the ray, blue (0.001) over green (0.18) to red
(32 and more). The links of the photographs are read from tools/atmo_looksheet_links.json (scene id -> list of {title, url, licence,
checked}); the observations of the day from <out>/observations.md when it exists (written after looking at the sheets).
"""
import argparse
import datetime
import json
import os
import subprocess
import sys
import textwrap

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import gpu_lock  # noqa: E402

FRAMES = os.path.join(ROOT, "build", "WeatherPlayer", "T-look-scene")
LINKS = os.path.join(HERE, "atmo_looksheet_links.json")
TEXT_WIDTH = 640
MARGIN = 8
BAND = 26
BACKGROUND = (24, 24, 24)
TEXT = (230, 230, 230)
MUTED = (170, 170, 170)


def font(size, bold=False):
    for name in ("arialbd.ttf" if bold else "arial.ttf", "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def scenes():
    from weather_metrics import looksheet
    return looksheet, looksheet.scene_table()


def wrap(draw, text, fnt, width):
    """The lines of text wrapped to a pixel width."""
    lines, line = [], ""
    for word in text.split():
        trial = (line + " " + word).strip()
        if draw.textlength(trial, font=fnt) <= width:
            line = trial
        else:
            lines.append(line)
            line = word
    if line:
        lines.append(line)
    return lines


def load_pair(frames, scene_id):
    """The natural and the aid picture of a scene as RGB images (the aid is the left half of the player's aid file), or (None, None)."""
    natural = os.path.join(frames, "LS-%s.png" % scene_id)
    aid = os.path.join(frames, "LS-%s-aid.png" % scene_id)
    if not (os.path.isfile(natural) and os.path.isfile(aid)):
        return None, None
    a = Image.open(natural).convert("RGB")
    b = Image.open(aid).convert("RGB")
    b = b.crop((0, 0, a.width, b.height))
    return a, b


def legend(width):
    """A strip of the false-colour scale of the aid view with its labels."""
    def heat(t):
        lo, mid, hi = np.array([0.05, 0.05, 0.30]), np.array([0.20, 0.80, 0.50]), np.array([0.95, 0.20, 0.10])
        return lo + (mid - lo) * t * 2.0 if t < 0.5 else mid + (hi - mid) * (t - 0.5) * 2.0
    strip = Image.new("RGB", (width, 36), BACKGROUND)
    draw = ImageDraw.Draw(strip)
    bar = 240
    for x in range(bar):
        c = tuple(int(255 * v) for v in heat(x / (bar - 1.0)))
        draw.line([(MARGIN + x, 4), (MARGIN + x, 18)], fill=c)
    f = font(12)
    for t, label in ((0.0, "tau 0.001"), (0.5, "0.18"), (1.0, "32+")):
        draw.text((MARGIN + t * (bar - 1) - (0 if t < 1 else 28) - (20 if t == 0.5 else 0), 20), label, font=f, fill=MUTED)
    draw.text((MARGIN + bar + 12, 3), "aid view: optical depth along the ray (log scale); grey = no cloud on the ray", font=f, fill=MUTED)
    return strip


def compose_group(frames, group, title, table, looksheet, out_path):
    """One contact sheet: a row per scene (natural, aid, text). Returns the sheet size and the ids missing."""
    rows, missing = [], []
    probe = ImageDraw.Draw(Image.new("RGB", (8, 8)))
    f_text, f_head = font(15), font(17, True)
    for s in table:
        if s["group"] != group:
            continue
        a, b = load_pair(frames, s["id"])
        if a is None:
            missing.append(s["id"])
            continue
        lines = [("head", s["id"] + "  " + s["title"])]
        lines += [("text", t) for t in wrap(probe, "Real clouds: " + looksheet.REFERENCE[s["id"]], f_text, TEXT_WIDTH)]
        lines.append(("gap", ""))
        lines += [("muted", t) for t in wrap(probe, "Expected here: " + looksheet.EXPECT[s["id"]], f_text, TEXT_WIDTH)]
        lines.append(("gap", ""))
        pos = "camera %.0f m, pitch %.0f deg, fov %.0f deg, sun az %.0f deg el %.1f deg" % (s["alt"], s["pitch"], s["fov"], s["sun"][0], s["sun"][1])
        lines += [("muted", t) for t in wrap(probe, pos, f_text, TEXT_WIDTH)]
        rows.append((s, a, b, lines))
    if not rows:
        return None, missing
    w, h = rows[0][1].size
    width = MARGIN + w + MARGIN + w + MARGIN + TEXT_WIDTH + MARGIN
    heights = [max(h, 22 * len(r[3]) + 8) for r in rows]
    height = BAND + 36 + sum(BAND + hh + MARGIN for hh in heights)
    sheet = Image.new("RGB", (width, height), BACKGROUND)
    draw = ImageDraw.Draw(sheet)
    draw.text((MARGIN, 4), title + "   (natural | aid | what real clouds look like)", font=f_head, fill=TEXT)
    sheet.paste(legend(width), (0, BAND - 2))
    y = BAND + 36
    for (s, a, b, lines), hh in zip(rows, heights):
        draw.text((MARGIN, y + 4), "natural", font=f_text, fill=MUTED)
        draw.text((MARGIN + w + MARGIN, y + 4), "aid", font=f_text, fill=MUTED)
        y += BAND
        sheet.paste(a, (MARGIN, y))
        sheet.paste(b, (MARGIN + w + MARGIN, y))
        ty = y + 2
        for kind, text in lines:
            if kind == "gap":
                ty += 8
                continue
            draw.text((MARGIN + 2 * (w + MARGIN), ty), text, font=f_head if kind == "head" else f_text, fill=MUTED if kind == "muted" else TEXT)
            ty += 22
        y += hh + MARGIN
    sheet.save(out_path)
    return sheet.size, missing


def index_text(out, date, groups, table, looksheet, links, made, missing, observations):
    lines = ["# Early look sheet, %s" % date, "",
             "Own cloud system (layer scene of the weather player), fixed camera, sun, seed and weather map per scene. Each scene as the natural view "
             "and the aid view (optical depth, false colour) side by side. Regenerate: `python tools/atmo_looksheet.py`.", ""]
    if missing:
        lines += ["Missing pictures: " + ", ".join(missing), ""]
    if observations:
        lines += ["## Observations", "", observations.strip(), ""]
    for key, title in groups:
        if key not in made:
            continue
        lines += ["## %s" % title, "", "![%s](sheet-%s.png)" % (title, key), ""]
        for s in table:
            if s["group"] != key:
                continue
            lines += ["### %s  %s" % (s["id"], s["title"]), "",
                      "Camera %.0f m altitude, pitch %.0f deg, vertical fov %.0f deg, sun azimuth %.0f deg elevation %.1f deg." % (
                          s["alt"], s["pitch"], s["fov"], s["sun"][0], s["sun"][1]), "",
                      "Real clouds: " + looksheet.REFERENCE[s["id"]], ""]
            entries = links.get(s["id"], [])
            if entries:
                lines.append("Photographs (links only; the images are not stored in the repository):")
                for e in entries:
                    lines.append("- [%s](%s) - licence: %s (checked %s)" % (e["title"], e["url"], e["licence"], e.get("checked", "")))
                lines.append("")
            else:
                lines += ["Photographs: none verified yet.", ""]
    path = os.path.join(out, "index.md")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    return path


def compose(frames, out):
    looksheet, table = scenes()
    os.makedirs(out, exist_ok=True)
    made, missing = {}, []
    for key, title in looksheet.GROUPS:
        size, miss = compose_group(frames, key, title, table, looksheet, os.path.join(out, "sheet-%s.png" % key))
        missing += miss
        if size:
            made[key] = size
    links = {}
    if os.path.isfile(LINKS):
        with open(LINKS, encoding="utf-8") as f:
            links = json.load(f)
    observations = ""
    obs = os.path.join(out, "observations.md")
    if os.path.isfile(obs):
        with open(obs, encoding="utf-8") as f:
            observations = f.read()
    date = os.path.basename(os.path.normpath(out))
    path = index_text(out, date, looksheet.GROUPS, table, looksheet, links, made, missing, observations)
    print("sheets: %s; missing: %s; index: %s" % (", ".join(sorted(made)) or "none", ", ".join(missing) or "none", path))
    return 0 if made and not missing else 1


def run_player(no_build, who):
    cmd = [sys.executable, os.path.join(HERE, "build_weather_player.py"), "--series", "T-look", "--who", who]
    if no_build:
        cmd.append("--no-build")
    return subprocess.run(cmd, cwd=ROOT).returncode


def default_out():
    return os.path.join(gpu_lock.main_repo(), ".local", "work", "looksheet", datetime.date.today().isoformat())


def selftest():
    import tempfile
    looksheet, table = scenes()
    ok = True
    with tempfile.TemporaryDirectory() as d:
        for s in table:
            Image.new("RGB", looksheet.SIZE, (90, 120, 160)).save(os.path.join(d, "LS-%s.png" % s["id"]))
            Image.new("RGB", (looksheet.SIZE[0] * 2 + 2, looksheet.SIZE[1]), (40, 40, 80)).save(os.path.join(d, "LS-%s-aid.png" % s["id"]))
        out = os.path.join(d, "out", "2000-01-01")
        code = compose(d, out)
        ok = code == 0 and all(os.path.isfile(os.path.join(out, "sheet-%s.png" % k)) for k, _ in looksheet.GROUPS) and os.path.isfile(os.path.join(out, "index.md"))
        for s in table:
            ok = ok and s["id"] in looksheet.REFERENCE and s["id"] in looksheet.EXPECT
    print("selftest: %s" % ("PASSED" if ok else "FAILED"))
    return 0 if ok else 1


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--no-build", action="store_true")
    p.add_argument("--compose-only", action="store_true")
    p.add_argument("--frames", default=FRAMES)
    p.add_argument("--out", default=None)
    p.add_argument("--who", default="LOOK")
    p.add_argument("--selftest", action="store_true")
    a = p.parse_args()
    if a.selftest:
        return selftest()
    out = a.out or default_out()
    if not a.compose_only:
        code = run_player(a.no_build, a.who)
        if code != 0:
            print("the player series failed (exit %d)" % code)
            return code
    return compose(a.frames, out)


if __name__ == "__main__":
    sys.exit(main())
