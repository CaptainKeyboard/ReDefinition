#!/usr/bin/env python3
"""Sheets of tuning round 3 of the weather generator (every picture as NATURAL view and AID view side by side). Uses the helpers of round2_sheets.py.

  python tools/atmosphere-sim/plots/round3_sheets.py <command> <out dir> <data dir> [<data dir 2>]

Commands: closeup (data = `AtmoSim.Runner follow` frames of one system), compare (data = round-2 frames, data 2 = round-3 frames), tropics (same two dirs),
          bands (data = `diag-bands` output dir), falsecolour (data = `frames` dir), ratio (data = tracker json of the area definition, data 2 = ridge definitions).
Data: `AtmoSim.Runner frames` / `follow` / `diag-bands` (see round2_sheets.py).
"""
import json
import os
import sys

import numpy as np
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import round2_sheets as r2  # noqa: E402


def load_follow(d):
    meta = json.load(open(os.path.join(d, "frames_meta.json")))
    nx, ny, nf = meta["nlon"], meta["nlat"], meta["frames"]
    f = {k: np.fromfile(os.path.join(d, "frames_%s.f32" % k), dtype=np.float32).reshape(nf, ny, nx) for k in meta["fields"]}
    f["ocean_t"] = np.fromfile(os.path.join(d, "frames_ocean.f32"), dtype=np.float32).reshape(nf, ny, nx)
    return meta, f


def closeup(out, d, picks=(3, 4, 6, 8), title="", info=None):
    """Four ages of one system: natural row, aid row (cover ramp, pressure lows red/orange, fronts magenta/white), and a column of text with the real-reference features."""
    meta, f = load_follow(d)
    fig = plt.figure(figsize=(21, 10.8))
    gs = fig.add_gridspec(2, len(picks) + 1, width_ratios=[1] * len(picks) + [0.62])
    for c, k in enumerate(picks):
        w = meta["windows"][k]
        ext = [w[2], w[3], w[0], w[1]]
        g = {kk: v for kk, v in f.items() if kk not in ("ocean_t",)}
        g["ocean"] = f["ocean_t"][k]
        for kk in list(g.keys()):
            if kk != "ocean":
                g[kk] = g[kk][k:k + 1]
        ax = fig.add_subplot(gs[0, c])
        ax.imshow(r2.natural_rgb(g, 0), extent=ext, aspect="auto", interpolation="bilinear")
        ax.set_title("NATURAL, %.1f days after the start of the window" % ((meta["times_s"][k] - meta["times_s"][0]) / 86400.0), fontsize=9)
        ax2 = fig.add_subplot(gs[1, c])
        ax2.imshow(r2.aid_cover_rgb(g, 0), extent=ext, aspect="auto", interpolation="bilinear")
        pa = g["pa"][0]
        pa = pa - pa.mean(axis=1, keepdims=True)
        ax2.contour(pa[::-1], levels=[-1500, -800], colors=["red", "orange"], linewidths=0.8, extent=ext)
        ax2.contour(g["front"][0][::-1], levels=[1.2, 2.5], colors=["magenta", "white"], linewidths=0.7, extent=ext)
        ax2.set_title("AID cover ramp, lows red/orange, fronts magenta/white", fontsize=8)
        for a in (ax, ax2):
            a.tick_params(labelsize=6)
    axt = fig.add_subplot(gs[:, len(picks)])
    axt.axis("off")
    text = info or ""
    axt.text(0.0, 1.0, text, va="top", ha="left", fontsize=8.2, family="DejaVu Sans", wrap=True)
    fig.suptitle(title, fontsize=11)
    fig.tight_layout()
    fig.savefig(os.path.join(out, "mature_system_closeup.png"), dpi=62)
    plt.close(fig)


def compare(out, d1, d2, la="round 2", lb="round 3", days=(2, 9, 17)):
    m1, f1 = r2.load_frames(d1)
    m2, f2 = r2.load_frames(d2)
    pd = max(1, int(round(86400.0 / m1["frame_s"])))
    fig, axs = plt.subplots(len(days), 4, figsize=(26, 3.9 * len(days)))
    for r, dd in enumerate(days):
        k = min(m1["frames"] - 1, dd * pd)
        for c, (f, tag) in enumerate(((f1, la), (f2, lb))):
            axs[r, 2 * c].imshow(r2.natural_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
            axs[r, 2 * c].set_title("NATURAL %s, day %d" % (tag, dd), fontsize=9)
            axs[r, 2 * c + 1].imshow(r2.aid_cover_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
            r2.draw_coast(axs[r, 2 * c + 1], f, "w", 0.3)
            r2.draw_systems(axs[r, 2 * c + 1], f, k, m1)
            axs[r, 2 * c + 1].set_title("AID cover ramp %s, day %d (lows red/orange, fronts magenta/white)" % (tag, dd), fontsize=8)
        for a in axs[r]:
            a.set_xticks([]); a.set_yticks([-60, -30, 0, 30, 60])
    fig.suptitle("before / after: %s (left pair) against %s (right pair), same seed, same times; natural view and aid view" % (la, lb), fontsize=11)
    fig.tight_layout(); fig.savefig(os.path.join(out, "before_after_equirect.png"), dpi=50); plt.close(fig)
    k = min(m1["frames"] - 1, 9 * pd)
    fig, axs = plt.subplots(4, 4, figsize=(15, 15))
    for r, lon in enumerate((0, 90, 180, 270)):
        for c, (f, tag) in enumerate(((f1, la), (f2, lb))):
            axs[r, 2 * c].imshow(r2.ortho_rgb(r2.natural_rgb(f, k), lon, 15, 300, True, None)); axs[r, 2 * c].set_title("NATURAL %s lon %d" % (tag, lon), fontsize=8)
            axs[r, 2 * c + 1].imshow(r2.ortho_rgb(r2.aid_cover_rgb(f, k), lon, 15, 300, False, r2.coast_mask(f["ocean"]))); axs[r, 2 * c + 1].set_title("AID %s" % tag, fontsize=8)
            axs[r, 2 * c].axis("off"); axs[r, 2 * c + 1].axis("off")
    fig.suptitle("before / after as an orbit globe (day 9): %s left, %s right" % (la, lb), fontsize=10)
    fig.tight_layout(); fig.savefig(os.path.join(out, "before_after_globe.png"), dpi=55, facecolor="black"); plt.close(fig)


def tropics(out, d1, d2, la="round 2", lb="round 3", days=(3, 9, 15)):
    m1, f1 = r2.load_frames(d1)
    m2, f2 = r2.load_frames(d2)
    ny = f1["cover"].shape[1]
    j0, j1 = int(ny * 0.25), int(ny * 0.75)
    fig, axs = plt.subplots(len(days), 4, figsize=(26, 3.6 * len(days)))
    for r, dd in enumerate(days):
        for c, (f, tag) in enumerate(((f1, la), (f2, lb))):
            axs[r, 2 * c].imshow(r2.natural_rgb(f, dd)[j0:j1], extent=[0, 360, -45, 45], aspect="auto")
            axs[r, 2 * c].set_title("NATURAL tropics %s, day %d" % (tag, dd), fontsize=9)
            axs[r, 2 * c + 1].imshow(r2.aid_cover_rgb(f, dd)[j0:j1], extent=[0, 360, -45, 45], aspect="auto")
            axs[r, 2 * c + 1].set_title("AID cover ramp %s" % tag, fontsize=9)
    fig.suptitle("moist belt and tropics: %s (left pair) against %s (right pair)" % (la, lb), fontsize=11)
    fig.tight_layout(); fig.savefig(os.path.join(out, "tropics_before_after.png"), dpi=50); plt.close(fig)


def bands(out, d, name="detector_overlay.png"):
    """Cloud cover (natural-like grey and aid ramp) with the components of the band detector: accepted bands white, rejected components red."""
    m = json.load(open(os.path.join(d, "bandmask_meta.json")))
    nx, ny, nf = m["nlon"], m["nlat"], m["frames"]
    bm = np.fromfile(os.path.join(d, "bandmask.f32"), dtype=np.float32).reshape(nf, ny, nx)
    cv = np.fromfile(os.path.join(d, "cover.f32"), dtype=np.float32).reshape(nf, ny, nx)
    ks = [2, 8, 14, 20]
    fig, axs = plt.subplots(len(ks), 2, figsize=(18, 3.8 * len(ks)))
    for r, k in enumerate(ks):
        day = m["frame_index"][k] * m["frame_s"] / 86400.0
        axs[r, 0].imshow(cv[k], cmap="gray", extent=[0, 360, -90, 90], vmin=0, vmax=1.2, aspect="auto")
        axs[r, 0].set_title("NATURAL-like cloud (white = cloud) day %.1f" % day, fontsize=8)
        axs[r, 1].imshow(cv[k], cmap="viridis", extent=[0, 360, -90, 90], vmin=0, vmax=1, aspect="auto")
        rgba = np.zeros((ny, nx, 4)); rgba[bm[k] == 1] = [1, 0.1, 0.1, 0.9]; rgba[bm[k] == 2] = [1, 1, 1, 1]
        axs[r, 1].imshow(rgba, extent=[0, 360, -90, 90], aspect="auto")
        axs[r, 1].set_title("AID: cover ramp; detector components: accepted bands white, rejected red (belt 35 to 70 degrees)", fontsize=8)
        for a in axs[r]:
            a.set_ylim(-80, 80); a.set_xticks([])
    fig.tight_layout(); fig.savefig(os.path.join(out, name), dpi=62); plt.close(fig)


def falsecolour(out, d, day=6):
    meta, f = r2.load_frames(d)
    pd = max(1, int(round(86400.0 / meta["frame_s"])))
    k = min(meta["frames"] - 1, day * pd)
    fig, axs = plt.subplots(3, 2, figsize=(18, 13))
    panels = [
        ("cloud cover 0..1 (viridis)", f["cover"][k], "viridis", 0, 1),
        ("surface air temperature (degrees C)", f["temp"][k] - 273.15, "RdYlBu_r", -40, 40),
        ("front strength (K per 100 km)", f["front"][k], "magma", 0, 8),
        ("wave pressure anomaly minus zonal mean (hPa)", (f["pa"][k] - f["pa"][k].mean(axis=1, keepdims=True)) / 100.0, "RdBu", -30, 30),
        ("10 m wind speed (m/s)", f["wind"][k], "plasma", 0, 25),
    ]
    for a, (nm, arr, cm, lo, hi) in zip(axs.ravel()[:5], panels):
        im = a.imshow(arr, cmap=cm, vmin=lo, vmax=hi, extent=[0, 360, -90, 90], aspect="auto")
        r2.draw_coast(a, f, "k", 0.3)
        a.set_title(nm, fontsize=9)
        fig.colorbar(im, ax=a, fraction=0.025)
    axs.ravel()[5].imshow(r2.aid_precip_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="nearest")
    r2.draw_coast(axs.ravel()[5], f, "w", 0.3)
    axs.ravel()[5].set_title("precipitation level: rain blues, snow violets; none dark grey", fontsize=9)
    fig.suptitle("%s: false-colour maps of day %d" % (meta["body"], day), fontsize=11)
    fig.tight_layout(); fig.savefig(os.path.join(out, "false_colour_maps.png"), dpi=58); plt.close(fig)


def main():
    cmd, out, d = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out, exist_ok=True)
    if cmd == "closeup":
        info = open(sys.argv[4], encoding="utf-8").read() if len(sys.argv) > 4 else ""
        closeup(out, d, title=sys.argv[5] if len(sys.argv) > 5 else "", info=info)
    elif cmd == "compare":
        compare(out, d, sys.argv[4])
    elif cmd == "tropics":
        tropics(out, d, sys.argv[4])
    elif cmd == "bands":
        bands(out, d, sys.argv[4] if len(sys.argv) > 4 else "detector_overlay.png")
    elif cmd == "falsecolour":
        falsecolour(out, d)


if __name__ == "__main__":
    main()
