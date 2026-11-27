"""The sky from the weather map (master plan section 34): a game picture pair with the weather map patch of the same area beside it.

  python tools/weather_metrics/wms_pairs.py --pictures <folder with the pinscreen PNGs> --maps <series-pictures/K-T2/maps> --start <start id>
         --view ground --sets plain,wms --out <png> [--header "line"] [--radius-km 120]

Per set the game's screen picture (t09-<start>-<view>-plain[_<set>]-2560x1440-pinscreen.png) and, from the capture folder of the first set that holds them
(pin-<view>-<set>-u<ut>-<start>-<utc>/wms-*.u8, written by OwnCloudModule.WriteMapSkyDump while cloud.weatherMapSky is on), the field's maps around KSC:
Bedeckung (cover), Haufenwolken-Anteil (ConvectiveAmount), Schichtwolken-Anteil (StratiformAmount), and the parts the layers drew (Gitter-Cumulus, zusammen-
haengende Schicht = Stratocumulus + Altocumulus, Regendecke, Cirrus, Gewitterzug). KSC is the cross, the view's heading the arrow, the circles 10 / 30 / 60 km.
Each map is drawn at its texels (nearest), so the 7.4 km texels show. A header line (which map, which state, what is new, what is still wrong) goes on top.
Labels German (the owner's pictures). Stdlib + numpy + Pillow + matplotlib.
"""
import argparse
import glob
import json
import math
import os
import re

import numpy as np
from PIL import Image

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

W, H = 512, 256
KSC_LAT, KSC_LON = -0.0972, -74.5577
RADIUS_KM = 600.0
HEADINGS = {"ground": 0.0, "inside": 0.0, "above": 0.0, "sky3": 0.0}


def capture(maps, view, set_name, start):
    tag = view + ("" if set_name == "plain" else "-" + set_name)
    found = sorted(glob.glob(os.path.join(maps, "pin-%s-u*-%s-*" % (tag, start))))
    found = [f for f in found if re.match(r"^pin-%s-u\d+-%s-\d{14}$" % (re.escape(tag), re.escape(start)), os.path.basename(f))]
    return found[-1] if found else None


def screen(pictures, start, view, set_name):
    name = "t09-%s-%s-plain%s-2560x1440-pinscreen.png" % (start, view, "" if set_name == "plain" else "_" + set_name)
    hits = glob.glob(os.path.join(pictures, "**", name), recursive=True)
    return hits[0] if hits else None


def u8(folder, name):
    p = os.path.join(folder, name)
    if not os.path.isfile(p):
        return None
    return np.fromfile(p, np.uint8).reshape(H, W) / 255.0


def patch(field, radius_km):
    """The texels within radius_km of KSC (latitude rows and longitude columns), and their extent in km east / north of KSC."""
    deg = radius_km / (RADIUS_KM * math.pi / 180.0)
    j0 = int(math.floor((90.0 - (KSC_LAT + deg)) / 180.0 * H))
    j1 = int(math.ceil((90.0 - (KSC_LAT - deg)) / 180.0 * H))
    i0 = int(math.floor((KSC_LON - deg + 180.0) / 360.0 * W))
    i1 = int(math.ceil((KSC_LON + deg + 180.0) / 360.0 * W))
    sub = field[j0:j1, i0:i1]
    km = RADIUS_KM * math.pi / 180.0
    west = (-180.0 + i0 * 360.0 / W - KSC_LON) * km
    east = (-180.0 + i1 * 360.0 / W - KSC_LON) * km
    north = (90.0 - j0 * 180.0 / H - KSC_LAT) * km
    south = (90.0 - j1 * 180.0 / H - KSC_LAT) * km
    return sub, (west, east, south, north)


def draw_patch(ax, field, title, radius_km, heading, vmax=1.0, cmap="viridis"):
    sub, ext = patch(field, radius_km)
    im = ax.imshow(sub, extent=ext, origin="upper", interpolation="nearest", vmin=0.0, vmax=vmax, cmap=cmap)
    ax.plot([0], [0], "w+", markersize=14, mew=2)
    for r in (10, 30, 60):
        ax.add_patch(plt.Circle((0, 0), r, fill=False, color="white", lw=0.6, alpha=0.7))
    a = math.radians(heading)
    ax.annotate("", xy=(25 * math.sin(a), 25 * math.cos(a)), xytext=(0, 0), arrowprops=dict(arrowstyle="->", color="red", lw=2))
    ax.set_xlim(-radius_km, radius_km)
    ax.set_ylim(-radius_km, radius_km)
    ax.set_title(title, fontsize=11)
    ax.set_xlabel("Ost [km]")
    ax.set_ylabel("Nord [km]")
    plt.colorbar(im, ax=ax, fraction=0.046, pad=0.02)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pictures", required=True)
    ap.add_argument("--maps", required=True)
    ap.add_argument("--start", required=True)
    ap.add_argument("--view", required=True)
    ap.add_argument("--sets", default="plain,wms")
    ap.add_argument("--out", required=True)
    ap.add_argument("--header", default="")
    ap.add_argument("--radius-km", type=float, default=100.0)
    a = ap.parse_args()
    sets = a.sets.split(",")
    shots = [(s, screen(a.pictures, a.start, a.view, s)) for s in sets]
    maps_folder = None
    for s in sets:
        c = capture(a.maps, a.view, s, a.start)
        if c and os.path.isfile(os.path.join(c, "wms-cover.u8")):
            maps_folder = c
            break
    heading = HEADINGS.get(a.view, 0.0)
    # Per row: the plain picture beside the map's amounts; a map-sky picture beside the parts its own capture drew. Without a plain row the amounts get a row of their own.
    amounts_row = "plain" not in sets and maps_folder is not None
    rows = len(shots) + (1 if amounts_row else 0)
    fig = plt.figure(figsize=(30, 8.6 * rows + 1.2), dpi=100)
    gs = fig.add_gridspec(rows, 4, width_ratios=[3.2, 1, 1, 1])
    amounts = [("wms-cover.u8", "Bedeckung (Karte)"), ("wms-ConvectiveAmount.u8", "Haufenwolken-Anteil (Karte)"), ("wms-StratiformAmount.u8", "Schichtwolken-Anteil (Karte)")]
    parts = [("wms-part-rising.u8", "gezeichnet: aufsteigender Cumulus"), ("SHEET", "gezeichnet: zusammenhaengende Schicht (Sc + Ac)"), ("wms-part-tall.u8", "gezeichnet: Turm-Anteil (Cb)")]

    def panels_row(r, folder, which, suffix=""):
        for c, (name, title) in enumerate(which):
            ax = fig.add_subplot(gs[r, c + 1])
            if name == "SHEET":
                sc, ac = u8(folder, "wms-part-sc.u8"), u8(folder, "wms-part-ac.u8")
                f = None if sc is None or ac is None else 1.0 - (1.0 - sc) * (1.0 - ac)
            else:
                f = u8(folder, name)
            if f is None:
                ax.axis("off")
                continue
            draw_patch(ax, f, title + suffix, a.radius_km, heading)

    for r, (s, path) in enumerate(shots):
        ax = fig.add_subplot(gs[r, 0])
        if path:
            ax.imshow(Image.open(path).convert("RGB"))
        ax.set_title("Spiel, Sicht %s, %s" % (a.view, "heute (Schalter aus)" if s == "plain" else "aus der Wetterkarte: " + s), fontsize=13)
        ax.axis("off")
        if s == "plain":
            if maps_folder:
                panels_row(r, maps_folder, amounts)
        else:
            own = capture(a.maps, a.view, s, a.start)
            if own and os.path.isfile(os.path.join(own, "wms-part-sc.u8")):
                panels_row(r, own, parts, " [%s]" % s)
    if amounts_row:
        panels_row(rows - 1, maps_folder, amounts)
    if maps_folder:
        meta = {}
        mp = os.path.join(maps_folder, "wms.json")
        if os.path.isfile(mp):
            meta = json.load(open(mp))
        note = "Karte: Paket-B-Felder des Generators, ut %s, Regeln: Schicht %s, Tiefe %s; Kreuz KSC, Pfeil Blickrichtung, Kreise 10/30/60 km, Texel 7,4 km" % (
            meta.get("ut", "?"), meta.get("stratiformRule", "?"), meta.get("depthRule", "?"))
        fig.text(0.5, 0.005, note, ha="center", fontsize=11)
    if a.header:
        fig.suptitle(a.header, fontsize=14, x=0.01, ha="left")
    fig.tight_layout(rect=(0, 0.02, 1, 0.97 if a.header else 1.0))
    fig.savefig(a.out)
    print("wrote", a.out, "maps from", maps_folder)


if __name__ == "__main__":
    main()
