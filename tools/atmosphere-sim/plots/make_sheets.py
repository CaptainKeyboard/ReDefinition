#!/usr/bin/env python3
"""Plots and sheets of the B10 proof for the independent reviewer. Reads the folders written by AtmoSim.Runner.

  python tools/atmosphere-sim/plots/make_sheets.py <root> [run-folder-name ...]

<root>/<name>/ holds the output of `AtmoSim.Runner month <body> <seed> <startDays> <frameHours> <days> <dir>`: month_meta.json,
month_<field>.f32, month_events.json, ocean.f32. Without names the folder kerbinA is used. Output goes to <root>/sheets/.
The sheets show the generator output only; the metrics are in metrics.json and the tracker table. Days are days of 86400 s.
"""
import glob
import json
import os
import sys

import numpy as np
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from PIL import Image


def month(root, name):
    d = os.path.join(root, name)
    meta = json.load(open(os.path.join(d, "month_meta.json")))
    nl, nt, nf = meta["nlon"], meta["nlat"], meta["frames"]
    fields = {}
    for f in meta["fields"]:
        fields[f] = np.fromfile(os.path.join(d, "month_%s.f32" % f), dtype=np.float32).reshape(nf, nt, nl)
    ocean = np.fromfile(os.path.join(d, "ocean.f32"), dtype=np.float32).reshape(nt, nl)
    events = json.load(open(os.path.join(d, "month_events.json")))
    return meta, fields, ocean, events


def ortho(field, lat, lon0_deg, size=240, lat0_deg=25.0):
    """Orthographic projection of an equirectangular field (rows north to south, columns 0..360)."""
    nt, nl = field.shape
    y, x = np.mgrid[-1:1:size * 1j, -1:1:size * 1j]
    y = -y
    r2 = x * x + y * y
    ok = r2 <= 1.0
    z = np.sqrt(np.clip(1.0 - r2, 0, 1))
    lat0 = np.radians(lat0_deg)
    sinphi = z * np.sin(lat0) + y * np.cos(lat0)
    phi = np.arcsin(np.clip(sinphi, -1, 1))
    lam = np.radians(lon0_deg) + np.arctan2(x, z * np.cos(lat0) - y * np.sin(lat0))
    lon = (np.degrees(lam) % 360.0)
    latdeg = np.degrees(phi)
    rows = np.abs(lat[None, None, :] - latdeg[..., None]).argmin(axis=2)
    cols = np.clip((lon / 360.0 * nl).astype(int), 0, nl - 1)
    img = field[rows, cols].astype(float)
    img[~ok] = np.nan
    return img


def per_day(meta):
    return max(1, int(round(86400.0 / meta["frame_s"])))


def contact_sheets(out, title, meta, fields, ocean, events):
    lat = np.array(meta["lat_deg"])
    nf = meta["frames"]
    pd = per_day(meta)
    days = list(range(0, nf, pd))[:30]
    cov = fields["cover"]
    fig, axs = plt.subplots(6, 5, figsize=(20, 14))
    for a, d in zip(axs.ravel(), days):
        a.imshow(cov[d], cmap="gray_r", vmin=0, vmax=1, extent=[0, 360, -90, 90], aspect="auto")
        a.contour(ocean, levels=[0.5], colors="c", linewidths=0.4, extent=[0, 360, 90, -90])
        a.set_title("day %d" % (d // pd), fontsize=8)
        a.set_xticks([]); a.set_yticks([])
    fig.suptitle("%s: cloud cover (dark = more cloud), daily frames, equirectangular (cyan = coast)" % title)
    fig.tight_layout(); fig.savefig(os.path.join(out, "contact_cloud_equirect.png"), dpi=70); plt.close(fig)

    fig, axs = plt.subplots(6, 5, figsize=(20, 14))
    pr = fields["precip"]
    for a, d in zip(axs.ravel(), days):
        a.imshow(np.log10(np.maximum(pr[d], 1e-3)), cmap="Blues", vmin=-2, vmax=1.2, extent=[0, 360, -90, 90], aspect="auto")
        a.set_xticks([]); a.set_yticks([]); a.set_title("day %d" % (d // pd), fontsize=8)
    fig.suptitle("%s: log10 precipitation rate (mm/h water equivalent), daily frames" % title)
    fig.tight_layout(); fig.savefig(os.path.join(out, "contact_precip_equirect.png"), dpi=70); plt.close(fig)

    # Orbit view: the full disc, untilted (the equator is the horizontal diameter), on a black sky, square panels, no clipping; the poles are seen on their own panels
    # (north pole up and south pole up views) so that a pinching of the latitude-longitude grid would show.
    sel = [0, 5, 10, 15, 20, 25]
    cmap = plt.get_cmap("gray_r").copy(); cmap.set_bad("black")
    fig, axs = plt.subplots(3, 6, figsize=(18, 9.6))
    for r, lon0 in enumerate([0, 120, 240]):
        for c, dd in enumerate(sel):
            d = min(nf - 1, dd * pd)
            axs[r, c].imshow(np.ma.masked_invalid(ortho(cov[d], lat, lon0, size=300, lat0_deg=0.0)), cmap=cmap, vmin=0, vmax=1, interpolation="nearest")
            axs[r, c].set_xticks([]); axs[r, c].set_yticks([]); axs[r, c].set_aspect("equal")
            axs[r, c].set_title("lon %d deg, day %d" % (lon0, dd), fontsize=8)
    fig.suptitle("%s: cloud cover, orthographic orbit view (full disc, equator horizontal) at three longitudes" % title)
    fig.tight_layout(); fig.savefig(os.path.join(out, "contact_cloud_globe.png"), dpi=70); plt.close(fig)

    fig, axs = plt.subplots(2, 6, figsize=(18, 6.4))
    for r, lat0 in enumerate([90.0, -90.0]):
        for c, dd in enumerate(sel):
            d = min(nf - 1, dd * pd)
            axs[r, c].imshow(np.ma.masked_invalid(ortho(cov[d], lat, 0, size=300, lat0_deg=lat0)), cmap=cmap, vmin=0, vmax=1, interpolation="nearest")
            axs[r, c].set_xticks([]); axs[r, c].set_yticks([]); axs[r, c].set_aspect("equal")
            axs[r, c].set_title("%s pole, day %d" % ("north" if lat0 > 0 else "south", dd), fontsize=8)
    fig.suptitle("%s: cloud cover seen from above the poles (pinching of the latitude-longitude grid would show as a star)" % title)
    fig.tight_layout(); fig.savefig(os.path.join(out, "contact_cloud_poles.png"), dpi=70); plt.close(fig)


def strips(out, title, meta, fields, ocean, events):
    nf = meta["frames"]
    pd = per_day(meta)
    cov = fields["cover"]; pa = fields["pa"]
    step = max(1, int(round(6 * 3600.0 / meta["frame_s"])))   # the strips use 6 h steps
    for k, start in enumerate([5 * pd, 15 * pd, 25 * pd]):
        start = min(start, max(0, nf - 8 * step))
        fig, axs = plt.subplots(2, 4, figsize=(20, 7))
        for a, f in zip(axs.ravel(), range(start, start + 8 * step, step)):
            f = min(f, nf - 1)
            a.imshow(cov[f], cmap="gray_r", vmin=0, vmax=1, extent=[0, 360, -90, 90], aspect="auto")
            p = pa[f] - pa[f].mean(axis=1, keepdims=True)
            a.contour(p[::-1], levels=[-1000, -500], colors=["r", "m"], linewidths=0.6, extent=[0, 360, -90, 90])
            for b in events[f].get("bands", []):
                a.plot([b["lon"] % 360], [b["lat"]], "y+", ms=8)
            for m in events[f].get("minima", []):
                a.plot([m[1] % 360], [m[0]], "ro", ms=4, mfc="none")
            a.set_title("t = %.2f d (6 h steps)" % ((meta["times_s"][f] - meta["times_s"][0]) / 86400.0), fontsize=8)
            a.set_xticks([]); a.set_yticks([])
        fig.suptitle("%s frame strip %d: cloud cover, pressure-anomaly contours (-10, -5 hPa), D1 minima (red circles), D2 band centroids (yellow +)" % (title, k + 1))
        fig.tight_layout(); fig.savefig(os.path.join(out, "strip_%d.png" % (k + 1)), dpi=70); plt.close(fig)


def zonal_plot(out, name, meta, fields):
    """Zonal means (over longitude and the month) of cover, precipitation, wind speed and temperature."""
    lat = np.array(meta["lat_deg"])
    fig, axs = plt.subplots(1, 4, figsize=(16, 4))
    for a, (key, label) in zip(axs, (("cover", "cloud cover"), ("precip", "precipitation (mm/h)"), ("wind", "10 m wind speed (m/s)"), ("temp", "air temperature (K)"))):
        a.plot(fields[key].mean(axis=(0, 2)), lat)
        a.set_xlabel(label); a.set_ylabel("latitude"); a.grid(alpha=0.3)
    fig.suptitle("%s: zonal means over the month" % name)
    fig.tight_layout(); fig.savefig(os.path.join(out, "zonal_%s.png" % name), dpi=70); plt.close(fig)


def series_plots(out, path):
    d = json.load(open(path))
    fig, axs = plt.subplots(4, 2, figsize=(14, 10), sharex=True)
    for r, s in enumerate(d["sites"]):
        axs[r, 0].plot(s["t_days"], s["level01"]); axs[r, 0].set_ylabel("level01 @ %g N" % s["lat"]); axs[r, 0].set_ylim(0, 1.05)
        axs[r, 1].plot(s["t_days"], s["wind_ms"]); axs[r, 1].set_ylabel("wind m/s @ %g N" % s["lat"])
    axs[3, 0].set_xlabel("days"); axs[3, 1].set_xlabel("days")
    fig.suptitle("%s seed %s: ground-level series (hourly)" % (d["body"], d["seed"]))
    fig.tight_layout(); fig.savefig(os.path.join(out, "ground_series.png"), dpi=70); plt.close(fig)
    fig, axs = plt.subplots(2, 2, figsize=(14, 8))
    for c, s in enumerate(d["sections"]):
        dens = np.array(s["density"]).reshape(s["nx"], s["nz"]).T
        x = np.linspace(-s["length_km"] / 2, s["length_km"] / 2, s["nx"])
        axs[0, c].imshow(dens, origin="lower", aspect="auto", cmap="gray_r", vmin=0, vmax=1.5, extent=[x[0], x[-1], 0, s["nz"] * s["dz_m"] / 1000.0])
        axs[0, c].set_title("%s section at %.1f N %.1f E (west-east)" % (s["kind"], s["lat_deg"], s["lon_deg"]), fontsize=9); axs[0, c].set_ylabel("height km")
        ax2 = axs[1, c]; ax2.plot(x, s["temp_k"], label="surface T (K)"); ax2.set_ylabel("T (K)")
        ax3 = ax2.twinx(); ax3.plot(x, s["front_k100km"], "r", label="front K/100 km"); ax3.plot(x, s["cell"], "g", label="cell strength"); ax3.legend(loc="upper right")
        ax2.set_xlabel("km east of the centre")
    fig.suptitle("cross-sections at day %.1f (cloud density, surface temperature, front strength, cell strength)" % d["section_ut_days"])
    fig.tight_layout(); fig.savefig(os.path.join(out, "cross_sections.png"), dpi=70); plt.close(fig)


def timelapse(out, meta, fields):
    cov = fields["cover"]
    step = max(1, int(round(6 * 3600.0 / meta["frame_s"])))
    frames = []
    for f in range(0, cov.shape[0], step):
        a = np.clip(cov[f], 0, 1)
        rgb = (255 * (1 - a))[..., None].repeat(3, axis=2).astype(np.uint8)
        frames.append(Image.fromarray(rgb).resize((768, 384), Image.BILINEAR))
    frames[0].save(os.path.join(out, "timelapse_cloud.gif"), save_all=True, append_images=frames[1:], duration=100, loop=0)


def surface_maps(root, out, name):
    """Snow water equivalent (land) and sea-ice thickness (sea) at the four season phases, equirectangular, and the ice seen from above each pole."""
    d = os.path.join(root, name)
    meta = json.load(open(os.path.join(d, "surf_meta.json")))
    nl, nt = meta["nlon"], meta["nlat"]
    ph = meta["phases"]
    label = ["northern midwinter", "northern spring", "northern midsummer", "northern autumn"]
    ld = lambda k: np.fromfile(os.path.join(d, "surf_%s.f32" % k), dtype=np.float32).reshape(len(ph), nt, nl)
    swe, ice, wet = ld("swe"), ld("ice_h"), ld("wet")
    ocean = np.fromfile(os.path.join(d, "surf_ocean.f32"), dtype=np.float32).reshape(nt, nl)
    land = ocean < 0.5
    lat = np.linspace(90 - 90.0 / nt, -90 + 90.0 / nt, nt)
    cm_snow = plt.get_cmap("Blues").copy(); cm_snow.set_bad("0.35")
    cm_ice = plt.get_cmap("cool").copy(); cm_ice.set_bad("0.35")
    fig, axs = plt.subplots(2, 4, figsize=(20, 7.5))
    for p in range(len(ph)):
        s = np.ma.masked_where(~land, swe[p])
        axs[0, p].imshow(np.log10(np.maximum(s, 0.5)), cmap=cm_snow, vmin=np.log10(0.5), vmax=np.log10(300.0), extent=[0, 360, -90, 90], aspect="auto")
        axs[0, p].set_title("snow water equivalent (log10 mm), %s" % label[p], fontsize=9)
        i = np.ma.masked_where(land, ice[p])
        axs[1, p].imshow(i, cmap=cm_ice, vmin=0, vmax=2.0, extent=[0, 360, -90, 90], aspect="auto")
        axs[1, p].set_title("sea-ice thickness (m, 0 to 2), %s" % label[p], fontsize=9)
        for a in (axs[0, p], axs[1, p]):
            a.set_xticks([]); a.set_yticks([-60, -30, 0, 30, 60]); a.grid(alpha=0.2)
    fig.suptitle("%s: surface layer, year %d (grey = sea in the snow row / land in the ice row)" % (meta["body"], meta["year_index"]))
    fig.tight_layout(); fig.savefig(os.path.join(out, "surface_snow_ice.png"), dpi=70); plt.close(fig)

    fig, axs = plt.subplots(2, 4, figsize=(18, 9))
    views = [(0, 90.0, "north pole, northern midwinter"), (2, 90.0, "north pole, northern midsummer"), (2, -90.0, "south pole, southern midwinter"), (0, -90.0, "south pole, southern midsummer")]
    for c, (p, lat0, nm) in enumerate(views):
        axs[0, c].imshow(np.ma.masked_invalid(ortho(np.where(land, np.nan, ice[p]), lat, 0, size=300, lat0_deg=lat0)), cmap=cm_ice, vmin=0, vmax=2.0, interpolation="nearest")
        axs[0, c].set_title("sea ice, " + nm, fontsize=9)
        axs[1, c].imshow(np.ma.masked_invalid(ortho(np.where(land, swe[p], np.nan), lat, 0, size=300, lat0_deg=lat0)), cmap=cm_snow, vmin=0, vmax=120, interpolation="nearest")
        axs[1, c].set_title("snow (0 to 120 mm), " + nm, fontsize=9)
        for a in (axs[0, c], axs[1, c]):
            a.set_xticks([]); a.set_yticks([]); a.set_aspect("equal")
    fig.suptitle("%s: ice edge and snow cover from above the poles" % meta["body"])
    fig.tight_layout(); fig.savefig(os.path.join(out, "surface_poles.png"), dpi=70); plt.close(fig)

    fig, axs = plt.subplots(1, 3, figsize=(15, 4))
    for p in range(len(ph)):
        cover = [float(np.mean(swe[p][j][land[j]] >= 2.5)) if land[j].any() else np.nan for j in range(nt)]
        icef = [float(np.mean(ice[p][j][~land[j]] >= 0.1)) if (~land[j]).any() else np.nan for j in range(nt)]
        wetm = [float(np.mean(wet[p][j])) for j in range(nt)]
        axs[0].plot(cover, lat, label=label[p]); axs[1].plot(icef, lat, label=label[p]); axs[2].plot(wetm, lat, label=label[p])
    axs[0].set_xlabel("snow-covered share of land"); axs[1].set_xlabel("ice-covered share of sea (>= 0.1 m)"); axs[2].set_xlabel("mean wet state")
    for a in axs:
        a.set_ylabel("latitude"); a.grid(alpha=0.3)
    axs[0].legend(fontsize=7)
    fig.tight_layout(); fig.savefig(os.path.join(out, "surface_zonal.png"), dpi=70); plt.close(fig)


def tracker_table(root, out, names):
    """Tracker table of the `crit-tracker` outputs: every tracker_<mode>_<run>.json found in <root>/<folder>."""
    lines = ["| run | hemisphere | D1 persistent minima (mean active) | D1 tracks | lifetime h (mean/median/max) | east drift m/s (mean/median) | straightness (mean/median) | swaps | front events (assigned) | share with system | free band tracks |",
             "|---|---|---|---|---|---|---|---|---|---|---|"]
    f = lambda v: "n/a" if v is None else "%.2f" % v
    import glob
    for p in sorted(glob.glob(os.path.join(root, "tracker_*.json"))):
        d = json.load(open(p))
        n = os.path.basename(p)[len("tracker_"):-len(".json")]
        for h, key in (("N", "north"), ("S", "south")):
            t = d["d1_tracker_" + key]
            ev = d.get("d2_events_assigned_" + key, {})
            lines.append("| %s | %s | %.2f | %d | %s/%s/%s | %s/%s | %s/%s | %d | %s | %s | %d |" % (
                n, h, d["d1_persistent_active_mean_per_hemisphere_" + key], t["count"], f(t["lifetime_h_mean"]), f(t["lifetime_h_median"]), f(t["lifetime_h_max"]),
                f(t["east_drift_ms_mean"]), f(t["east_drift_ms_median"]), f(t["straightness_mean"]), f(t["straightness_median"]), t["swaps"],
                ev.get("events", "n/a"), f(ev.get("share_with_system")), d["d2_tracker_" + key]["count"]))
    open(os.path.join(out, "tracker_table.md"), "w").write("\n".join(lines) + "\n")


def main():
    root = sys.argv[1]
    names = sys.argv[2:] or ["kerbinA"]
    out = os.path.join(root, "sheets")
    os.makedirs(out, exist_ok=True)
    for n in names:
        if not os.path.exists(os.path.join(root, n, "month_meta.json")):
            continue
        meta, fields, ocean, events = month(root, n)
        sub = out if len(names) == 1 else os.path.join(out, n)
        os.makedirs(sub, exist_ok=True)
        title = "%s (seed %s)" % (meta["body"], meta["seed"])
        contact_sheets(sub, title, meta, fields, ocean, events)
        strips(sub, title, meta, fields, ocean, events)
        timelapse(sub, meta, fields)
        zonal_plot(sub, n, meta, fields)
    for n in names:
        if os.path.exists(os.path.join(root, n, "surf_meta.json")):
            sub = out if len(names) == 1 else os.path.join(out, n)
            os.makedirs(sub, exist_ok=True)
            surface_maps(root, sub, n)
    for p in sorted(glob.glob(os.path.join(root, "series_*.json"))):
        series_plots(out, p)
    tracker_table(os.environ.get("TRACKER_DIR", root), out, names)
    print("sheets in", out)


if __name__ == "__main__":
    main()
