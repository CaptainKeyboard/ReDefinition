#!/usr/bin/env python3
"""Plots and sheets of the B10 proof for the independent reviewer. Reads the run folders written by AtmoSim.Runner.

  python tools/atmosphere-sim/plots/make_sheets.py <root>

<root> holds kerbinA, kerbinB, ... (stats.json, month_*.f32, snap*.f32, ...). Output goes into <root>/sheets/.
The sheets show the model output only; the metrics are in metrics.json and the tracker table.
"""
import json
import os
import sys

import numpy as np
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from PIL import Image


def load(root, name):
    p = os.path.join(root, name, "stats.json")
    return json.load(open(p)) if os.path.exists(p) else None


def month(root, name="kerbinA"):
    d = os.path.join(root, name)
    meta = json.load(open(os.path.join(d, "month_meta.json")))
    nl, nt, nf = meta["nlon"], meta["nlat"], meta["frames"]
    fields = {}
    for f in meta["fields"]:
        fields[f] = np.fromfile(os.path.join(d, "month_%s.f32" % f), dtype=np.float32).reshape(nf, nt, nl)
    ocean = np.fromfile(os.path.join(d, "ocean.f32"), dtype=np.float32).reshape(nt, nl)
    events = json.load(open(os.path.join(d, "month_events.json")))
    return meta, fields, ocean, events


def ortho(field, lat, lon0_deg, size=240, vmin=None, vmax=None):
    """Orthographic projection of an equirectangular field (rows north to south, columns 0..360)."""
    nt, nl = field.shape
    y, x = np.mgrid[-1:1:size * 1j, -1:1:size * 1j]
    y = -y
    r2 = x * x + y * y
    ok = r2 <= 1.0
    z = np.sqrt(np.clip(1.0 - r2, 0, 1))
    lat0 = np.radians(25.0)
    sinphi = z * np.sin(lat0) + y * np.cos(lat0)
    phi = np.arcsin(np.clip(sinphi, -1, 1))
    lam = np.radians(lon0_deg) + np.arctan2(x, z * np.cos(lat0) - y * np.sin(lat0))
    lon = (np.degrees(lam) % 360.0)
    # nearest row by latitude
    latdeg = np.degrees(phi)
    rows = np.abs(lat[None, None, :] - latdeg[..., None]).argmin(axis=2)
    cols = np.clip((lon / 360.0 * nl).astype(int), 0, nl - 1)
    img = field[rows, cols].astype(float)
    img[~ok] = np.nan
    return img


def contact_sheets(root, out, meta, fields, ocean, events):
    lat = np.array(meta["lat_deg"])
    nf = meta["frames"]
    per_day = int(round(86400.0 / meta["frame_s"]))
    days = list(range(0, nf, per_day))[:30]
    cwp = fields["cwp"]
    vmax = float(np.percentile(cwp, 99.5))
    fig, axs = plt.subplots(6, 5, figsize=(20, 14))
    for a, d in zip(axs.ravel(), days):
        a.imshow(cwp[d], cmap="gray_r", vmin=0, vmax=vmax, extent=[0, 360, -90, 90], aspect="auto")
        a.contour(ocean, levels=[0.5], colors="c", linewidths=0.4, extent=[0, 360, 90, -90])
        a.set_title("day %d (%.1f Earth d)" % (d // per_day, meta["times_s"][d] / 86400.0 - meta["times_s"][0] / 86400.0), fontsize=8)
        a.set_xticks([]); a.set_yticks([])
    fig.suptitle("Kerbin-like seed A: cloud water path (dark = more cloud), daily frames over 30 Earth days, equirectangular (cyan = coast)")
    fig.tight_layout(); fig.savefig(os.path.join(out, "contact_cloud_equirect.png"), dpi=70); plt.close(fig)

    fig, axs = plt.subplots(6, 5, figsize=(20, 14))
    pr = fields["precip"]
    for a, d in zip(axs.ravel(), days):
        a.imshow(np.log10(np.maximum(pr[d], 1e-3)), cmap="Blues", vmin=-2, vmax=1.2, extent=[0, 360, -90, 90], aspect="auto")
        a.set_xticks([]); a.set_yticks([]); a.set_title("day %d" % (d // per_day), fontsize=8)
    fig.suptitle("Kerbin-like seed A: log10 precipitation rate (mm/h), daily frames")
    fig.tight_layout(); fig.savefig(os.path.join(out, "contact_precip_equirect.png"), dpi=70); plt.close(fig)

    sel = [0, 5, 10, 15, 20, 25]
    fig, axs = plt.subplots(3, 6, figsize=(18, 9))
    for r, lon0 in enumerate([0, 120, 240]):
        for c, dd in enumerate(sel):
            d = min(nf - 1, dd * per_day)
            axs[r, c].imshow(ortho(cwp[d], lat, lon0), cmap="gray_r", vmin=0, vmax=vmax)
            axs[r, c].set_xticks([]); axs[r, c].set_yticks([])
            axs[r, c].set_title("lon %d deg, day %d" % (lon0, dd), fontsize=8)
    fig.suptitle("Kerbin-like seed A: cloud water path, orthographic globe at three longitudes")
    fig.tight_layout(); fig.savefig(os.path.join(out, "contact_cloud_globe.png"), dpi=70); plt.close(fig)


def strips(root, out, meta, fields, ocean, events):
    nf = meta["frames"]
    per_day = int(round(86400.0 / meta["frame_s"]))
    cwp = fields["cwp"]; ps = fields["psanom"]
    vmax = float(np.percentile(cwp, 99.5))
    for k, start in enumerate([5 * per_day, 15 * per_day, 25 * per_day]):
        start = min(start, nf - 8)
        fig, axs = plt.subplots(2, 4, figsize=(20, 7))
        for a, f in zip(axs.ravel(), range(start, start + 8)):
            a.imshow(cwp[f][:, :], cmap="gray_r", vmin=0, vmax=vmax, extent=[0, 360, -90, 90], aspect="auto")
            pa = ps[f] - ps[f].mean(axis=1, keepdims=True)
            a.contour(pa[::-1], levels=[-400, -200], colors=["r", "m"], linewidths=0.6, extent=[0, 360, -90, 90])
            for b in events[f].get("bands", []):
                a.plot([b["lon"] % 360], [b["lat"]], "y+", ms=8)
            for m in events[f].get("minima", []):
                a.plot([m[1] % 360], [m[0]], "ro", ms=4, mfc="none")
            a.set_title("t = %.2f Earth d (6 h steps)" % (meta["times_s"][f] / 86400.0 - meta["times_s"][0] / 86400.0), fontsize=8)
            a.set_xticks([]); a.set_yticks([])
        fig.suptitle("Frame strip %d: cloud water path with surface-pressure-anomaly contours (-4, -2 hPa), D1 minima (red circles), D2 band centroids (yellow +)" % (k + 1))
        fig.tight_layout(); fig.savefig(os.path.join(out, "strip_%d.png" % (k + 1)), dpi=70); plt.close(fig)


def timelapse(root, out, meta, fields):
    cwp = fields["cwp"]; vmax = float(np.percentile(cwp, 99.5))
    frames = []
    for f in range(cwp.shape[0]):
        a = np.clip(cwp[f] / vmax, 0, 1)
        rgb = (255 * (1 - a))[..., None].repeat(3, axis=2).astype(np.uint8)
        frames.append(Image.fromarray(rgb).resize((768, 384), Image.BILINEAR))
    frames[0].save(os.path.join(out, "timelapse_cloud.gif"), save_all=True, append_images=frames[1:], duration=100, loop=0)


def cross_sections(root, out, meta, events):
    d = os.path.join(root, "kerbinA")
    nl, nt, K = meta["nlon"], meta["nlat"], meta["levels"]
    lat = np.array(meta["lat_deg"]); sig = np.array(meta["sigma"])
    per = int(round(meta["frame_s"] and 1))
    snaps = meta.get("snapshots_s", [])
    times = np.array(meta["times_s"])
    if not snaps:
        return
    def rd(i, n):
        return np.fromfile(os.path.join(d, "snap%d_%s.f32" % (i, n)), dtype=np.float32).reshape(K, nt, nl)
    # developing cell: the snapshot with the strongest convective maximum
    best = max(range(len(snaps)), key=lambda i: events[int(np.argmin(abs(times - snaps[i])))]["conv_max_ms"])
    ev = events[int(np.argmin(abs(times - snaps[best])))]
    T, W, QC = rd(best, "t3"), rd(best, "w3"), rd(best, "qc3")
    j = int(np.argmin(abs(lat - ev["conv_lat"]))); i0 = int(round(ev["conv_lon"] / 360.0 * nl)) % nl
    cols = [(i0 + k) % nl for k in range(-24, 25)]
    fig, axs = plt.subplots(1, 3, figsize=(18, 5))
    xs = np.arange(-24, 25) * 360.0 / nl * np.cos(np.radians(lat[j])) * 111.0
    for a, (arr, ttl, cm) in zip(axs, [(T[:, j, cols] - T[:, j, :].mean(axis=1, keepdims=True), "T anomaly (K) along the row", "RdBu_r"), (-W[:, j, cols], "upward pressure velocity (Pa/s)", "PuOr"), (QC[:, j, cols] * 1e3, "cloud water (g/kg)", "Blues")]):
        im = a.pcolormesh(xs, sig, arr, cmap=cm, shading="auto"); a.invert_yaxis(); a.set_xlabel("km east of the cell"); a.set_ylabel("sigma"); a.set_title(ttl); fig.colorbar(im, ax=a)
    fig.suptitle("Developing convective cell: lat %.1f, lon %.1f, max resolved updraft %.1f m/s (zonal section through the cell)" % (ev["conv_lat"], ev["conv_lon"], ev["conv_max_ms"]))
    fig.tight_layout(); fig.savefig(os.path.join(out, "xsection_cell.png"), dpi=70); plt.close(fig)
    # front: the snapshot with a band; normal line through the centroid
    for i in range(len(snaps)):
        e = events[int(np.argmin(abs(times - snaps[i])))]
        if e.get("bands"):
            b = max(e["bands"], key=lambda x: x["extent_km"])
            T, U, V, QC = rd(i, "t3"), rd(i, "u3"), rd(i, "v3"), rd(i, "qc3")
            ang = np.radians(b["axis_deg"]); nx, ny = -np.sin(ang), np.cos(ang)
            s = np.linspace(-800, 800, 41)
            la = np.radians(b["lat"]) + s * ny / meta.get("radius_km", 600.0)
            lo = np.radians(b["lon"]) + s * nx / (meta.get("radius_km", 600.0) * max(0.05, np.cos(np.radians(b["lat"]))))
            rr = np.array([int(np.argmin(abs(lat - np.degrees(x)))) for x in la]); cc = (np.round(np.degrees(lo) % 360.0 / 360.0 * nl).astype(int)) % nl
            fig, axs = plt.subplots(1, 2, figsize=(13, 5))
            for a, (arr, ttl, cm) in zip(axs, [(T[:, rr, cc], "temperature (K)", "RdBu_r"), (QC[:, rr, cc] * 1e3, "cloud water (g/kg)", "Blues")]):
                im = a.pcolormesh(s, sig, arr, cmap=cm, shading="auto"); a.invert_yaxis(); a.set_xlabel("km across the band"); a.set_ylabel("sigma"); a.set_title(ttl); fig.colorbar(im, ax=a)
            fig.suptitle("Front band (lat %.1f, lon %.1f, extent %.0f km, aspect %.1f, arm %s): section across the band" % (b["lat"], b["lon"] % 360, b["extent_km"], b["aspect"], b["arm"]))
            fig.tight_layout(); fig.savefig(os.path.join(out, "xsection_front.png"), dpi=70); plt.close(fig)
            break


def zonal_plots(root, out):
    names = ["kerbinA", "kerbinB", "kerbinC", "duna", "eve", "laythe", "slow", "noocean", "cold"]
    fig, axs = plt.subplots(3, 3, figsize=(15, 11))
    for a, n in zip(axs.ravel(), names):
        s = load(root, n)
        if not s:
            continue
        a.plot(s["lat_deg"], s["zonal_mean_precip_mmh"]); a.set_title("%s (%s)" % (n, s["body"])); a.set_xlabel("latitude"); a.set_ylabel("mm/h")
    fig.suptitle("Zonal-mean precipitation per set (years 2+)")
    fig.tight_layout(); fig.savefig(os.path.join(out, "zonal_precip_sets.png"), dpi=70); plt.close(fig)
    s = load(root, "kerbinA")
    if s:
        fig, axs = plt.subplots(1, 3, figsize=(16, 4.5))
        for k, lab in enumerate(["winter", "spring", "summer", "autumn"]):
            axs[0].plot(s["lat_deg"], s["row_cover_by_season_own_hemisphere"][k], label=lab)
            axs[1].plot(s["lat_deg"], s["row_ice_by_season_own_hemisphere"][k], label=lab)
        axs[0].set_title("snow cover fraction of land cells (own hemisphere's season)"); axs[1].set_title("sea-ice fraction of ocean cells")
        axs[2].plot(np.arange(100) / 100.0, s["ice_cycle_N_by_phase"], label="north"); axs[2].plot(np.arange(100) / 100.0, s["ice_cycle_S_by_phase"], label="south")
        axs[2].set_title("ice area by year phase (0 = northern midwinter)")
        for a in axs:
            a.legend(); a.set_xlabel("latitude" if a is not axs[2] else "year phase")
        fig.tight_layout(); fig.savefig(os.path.join(out, "snow_ice_kerbinA.png"), dpi=70); plt.close(fig)
        import csv
        p = os.path.join(root, "kerbinA", "sites_year2.csv")
        if os.path.exists(p):
            rows = list(csv.reader(open(p)))[1:]
            arr = np.array([[float(x) for x in r] for r in rows])
            fig, axs = plt.subplots(3, 1, figsize=(14, 8), sharex=True)
            for k, lab in enumerate(["50 N (mid-latitude)", "5 N (tropics)", "80 N (polar)"]):
                axs[k].plot((arr[:, 0] - arr[0, 0]) / 86400.0, arr[:, k + 1], lw=0.5); axs[k].set_ylabel("mm/h"); axs[k].set_title(lab)
            axs[-1].set_xlabel("Earth days in year 2")
            fig.tight_layout(); fig.savefig(os.path.join(out, "site_series.png"), dpi=70); plt.close(fig)


def ab_plots(root, out):
    ref = load(root, "kerbinA")
    if not ref:
        return
    fig, axs = plt.subplots(2, 4, figsize=(20, 8))
    for col, n in enumerate(["kerbinB", "abStep3h", "abOceanOff", "abCoriolisOff"]):
        s = load(root, n)
        if not s:
            continue
        axs[0, col].plot(ref["lat_deg"], ref["zonal_mean_precip_mmh"], label="reference"); axs[0, col].plot(s["lat_deg"], s["zonal_mean_precip_mmh"], label=n); axs[0, col].legend()
        axs[1, col].plot(s["lat_deg"], np.array(s["zonal_mean_precip_mmh"], dtype=float) - np.array(ref["zonal_mean_precip_mmh"], dtype=float)); axs[1, col].set_title("difference " + n + " - reference")
    fig.suptitle("A/B pairs: zonal-mean precipitation (mm/h) and difference")
    fig.tight_layout(); fig.savefig(os.path.join(out, "ab_zonal.png"), dpi=70); plt.close(fig)
    fa = os.path.join(root, "kerbinA", "daily_precip.f32"); fb = os.path.join(root, "kerbinB", "daily_precip.f32")
    if os.path.exists(fa) and os.path.exists(fb):
        nt = len(ref["lat_deg"]); a = np.fromfile(fa, dtype=np.float32); nl = a.size // nt // 60 if a.size % (nt * 60) == 0 else int(round(a.size / nt / 60))
        a = a.reshape(-1, nt, nl); b = np.fromfile(fb, dtype=np.float32).reshape(-1, nt, nl)
        fig, axs = plt.subplots(2, 3, figsize=(18, 8))
        for r, d in enumerate([20, 40]):
            d = min(d, a.shape[0] - 1)
            axs[r, 0].imshow(a[d], cmap="Blues", vmin=0, vmax=1.5, aspect="auto"); axs[r, 0].set_title("seed A, day %d" % (d + 10))
            axs[r, 1].imshow(b[d], cmap="Blues", vmin=0, vmax=1.5, aspect="auto"); axs[r, 1].set_title("seed B, day %d" % (d + 10))
            axs[r, 2].imshow(a[d] - b[d], cmap="RdBu", vmin=-1.5, vmax=1.5, aspect="auto"); axs[r, 2].set_title("difference")
        fig.suptitle("Seed A vs B, 24 h mean precipitation (mm/h): same climate, different weather")
        fig.tight_layout(); fig.savefig(os.path.join(out, "ab_seed_maps.png"), dpi=70); plt.close(fig)


def tracker_table(root, out):
    lines = ["| run | hemisphere | D1 persistent minima (mean active) | D1 tracks | lifetime h (mean/median/max) | east drift m/s (mean/median) | straightness (mean/median) | swaps | D2 events | D2 arms |", "|---|---|---|---|---|---|---|---|---|---|"]
    for n in ["kerbinA", "kerbinB", "kerbinC", "abCoriolisOff"]:
        s = load(root, n)
        if not s or "detectors" not in s:
            continue
        d = s["detectors"]
        for h, key in (("N", "north"), ("S", "south")):
            t = d["d1_tracker_" + key]
            f = lambda v: "n/a" if v is None else "%.2f" % v
            lines.append("| %s | %s | %.2f | %d | %s/%s/%s | %s/%s | %s/%s | %d | %d | %s |" % (n, h, d["d1_persistent_active_mean_per_hemisphere_" + key], t["count"], f(t["lifetime_h_mean"]), f(t["lifetime_h_median"]), f(t["lifetime_h_max"]),
                                                                                 f(t["east_drift_ms_mean"]), f(t["east_drift_ms_median"]), f(t["straightness_mean"]), f(t["straightness_median"]), t["swaps"], d["d2_events_" + key], json.dumps(d["d2_event_arms"])))
    open(os.path.join(out, "tracker_table.md"), "w").write("\n".join(lines) + "\n")


def main():
    root = sys.argv[1]
    out = os.path.join(root, "sheets")
    os.makedirs(out, exist_ok=True)
    if os.path.exists(os.path.join(root, "kerbinA", "month_meta.json")):
        meta, fields, ocean, events = month(root)
        meta["radius_km"] = 600.0
        contact_sheets(root, out, meta, fields, ocean, events)
        strips(root, out, meta, fields, ocean, events)
        timelapse(root, out, meta, fields)
        cross_sections(root, out, meta, events)
    zonal_plots(root, out)
    ab_plots(root, out)
    tracker_table(root, out)
    print("sheets in", out)


if __name__ == "__main__":
    main()
