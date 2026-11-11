#!/usr/bin/env python3
"""Sheets of tuning round 2 of the weather generator: every picture exists as the NATURAL view (what a viewer of the planet sees) and as an AID view next
to it (false colour: cloud cover as a colour ramp, precipitation level and type separately, systems and fronts as coloured lines).

  python tools/atmosphere-sim/plots/round2_sheets.py <command> <out dir> <data dir> [<data dir 2>]

Data: `AtmoSim.Runner frames` (720x360 maps at chosen times; frames_*.f32), `month` (the detector events of the strips), `surfmap` (snow and ice).
Commands: equirect, globe, poles, precip, surface, strips, compare (data dir = round 1, data dir 2 = round 2), all.
"""
import json
import os
import sys

import numpy as np
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.colors import ListedColormap
from scipy.ndimage import map_coordinates


# ---------------------------------------------------------------- data

def load_frames(d):
    meta = json.load(open(os.path.join(d, "frames_meta.json")))
    nx, ny, nf = meta["nlon"], meta["nlat"], meta["frames"]
    ld = lambda k: np.fromfile(os.path.join(d, "frames_%s.f32" % k), dtype=np.float32).reshape(nf, ny, nx)
    f = {k: ld(k) for k in meta["fields"]}
    f["ocean"] = np.fromfile(os.path.join(d, "frames_ocean.f32"), dtype=np.float32).reshape(ny, nx)
    return meta, f


def smooth(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


# ---------------------------------------------------------------- natural colours

def surface_rgb(f, k):
    """Ground as seen from orbit: sea, land, cold land and cold sea whitened (from the air temperature of the frame)."""
    oc = f["ocean"]
    t = f["temp"][k]
    sea = np.array([14, 42, 92]) / 255.0
    land = np.array([84, 104, 64]) / 255.0
    dry = np.array([150, 130, 90]) / 255.0
    rgb = np.where(oc[..., None] > 0.5, sea, land)
    lat = np.linspace(90, -90, oc.shape[0])[:, None]
    desert = smooth(18, 28, np.abs(lat)) * (1 - smooth(34, 44, np.abs(lat)))
    rgb = np.where((oc[..., None] <= 0.5), rgb * (1 - 0.5 * desert[..., None]) + dry * 0.5 * desert[..., None], rgb)
    # the polar caps only (the snow and ice of the surface layer have their own sheet)
    ice = smooth(72.0, 80.0, np.abs(lat)) * np.ones_like(oc)
    return rgb * (1 - ice[..., None]) + np.array([0.92, 0.95, 0.98]) * ice[..., None]


def natural_rgb(f, k, clouds=True):
    base = surface_rgb(f, k)
    if not clouds:
        return base
    cov = np.clip(f["cover"][k], 0, 1)
    lev = np.clip(f["level"][k], 0, 1)
    alpha = np.clip(cov * 1.1, 0, 1) ** 1.1
    shade = 1.0 - 0.22 * smooth(0.05, 0.6, lev)
    cl = np.array([0.97, 0.97, 0.98])[None, None, :] * shade[..., None]
    return base * (1 - alpha[..., None]) + cl * alpha[..., None]


# ---------------------------------------------------------------- aid colours

COVER_MAP = plt.get_cmap("viridis")


def aid_cover_rgb(f, k):
    return COVER_MAP(np.clip(f["cover"][k], 0, 1))[..., :3]


LEVEL_COLORS = ["#ffffff", "#c6dbef", "#6baed6", "#2171b5", "#08306b", "#6a0dad"]
LEVEL_NAMES = ["none", "flurries", "light", "moderate", "heavy", "blizzard"]


def level_class(lev):
    return np.digitize(lev, [0.04, 0.2, 0.4, 0.6, 0.8])


def aid_precip_rgb(f, k):
    """Precipitation level classes (rain blues, snow violets by type 3 = snow, 2 = sleet), none = dark grey."""
    lev = f["level"][k]
    cls = level_class(lev)
    typ = f["type"][k]
    rain = np.array([[0.13, 0.13, 0.15], [0.78, 0.86, 0.94], [0.42, 0.68, 0.84], [0.13, 0.44, 0.71], [0.03, 0.19, 0.42], [0.1, 0.0, 0.3]])
    snow = np.array([[0.13, 0.13, 0.15], [0.90, 0.85, 0.95], [0.78, 0.62, 0.86], [0.62, 0.35, 0.78], [0.45, 0.15, 0.62], [0.25, 0.0, 0.4]])
    out = np.where((typ == 3)[..., None], snow[cls], rain[cls])
    return out


def draw_coast(ax, f, color="c", lw=0.4):
    ax.contour(f["ocean"], levels=[0.5], colors=color, linewidths=lw, extent=[0, 360, 90, -90])


def draw_systems(ax, f, k, meta):
    pa = f["pa"][k]
    p = pa - pa.mean(axis=1, keepdims=True)
    ax.contour(p[::-1], levels=[-1500, -800], colors=["red", "orange"], linewidths=0.7, extent=[0, 360, -90, 90])
    fr = f["front"][k]
    ax.contour(fr[::-1], levels=[1.2, 2.5], colors=["magenta", "white"], linewidths=0.6, extent=[0, 360, -90, 90])


# ---------------------------------------------------------------- ortho projection

def ortho_rgb(img, lon0, lat0, size=300, shade=True, coast=None, sun=(-0.55, 0.35)):
    """Orthographic view of an equirectangular RGB image (row 0 north, column 0 at longitude 0); lit sphere, thin atmosphere, black sky."""
    ny, nx = img.shape[:2]
    y, x = np.mgrid[-1:1:size * 1j, -1:1:size * 1j]
    y = -y
    r2 = x * x + y * y
    ok = r2 <= 1.0
    z = np.sqrt(np.clip(1.0 - r2, 0, 1))
    la0 = np.radians(lat0)
    sinphi = z * np.sin(la0) + y * np.cos(la0)
    phi = np.arcsin(np.clip(sinphi, -1, 1))
    lam = np.radians(lon0) + np.arctan2(x, z * np.cos(la0) - y * np.sin(la0))
    row = (90.0 - np.degrees(phi)) / 180.0 * ny - 0.5
    col = (np.degrees(lam) % 360.0) / 360.0 * nx - 0.5
    out = np.zeros((size, size, 3))
    for c in range(3):
        out[..., c] = map_coordinates(img[..., c], [row, col], order=1, mode="grid-wrap")
    if coast is not None:
        cm = map_coordinates(coast.astype(float), [row, col], order=1, mode="grid-wrap")
        out = out * (1 - 0.9 * np.clip(cm, 0, 1)[..., None]) + np.array([1.0, 0.85, 0.2]) * 0.9 * np.clip(cm, 0, 1)[..., None]
    if shade:
        sx, sy = sun
        sz = np.sqrt(max(0.0, 1 - sx * sx - sy * sy))
        lamb = np.clip(x * sx + y * sy + z * sz, 0, 1)
        lit = 0.18 + 0.82 * lamb ** 0.8
        out = out * lit[..., None]
        rim = (1 - z) ** 3.0
        out = out + rim[..., None] * np.array([0.25, 0.45, 0.9]) * (0.3 + 0.7 * lamb[..., None])
    out = np.clip(out, 0, 1)
    out[~ok] = 0.0
    return out


def coast_mask(oc):
    b = oc > 0.5
    e = np.zeros_like(b)
    e[:, :-1] |= b[:, :-1] != b[:, 1:]
    e[:-1, :] |= b[:-1, :] != b[1:, :]
    e[:, -1] |= b[:, -1] != b[:, 0]
    return e.astype(float)


# ---------------------------------------------------------------- sheets

def equirect(out, d, days=(0, 6, 12, 18), name="cloud_equirect.png"):
    meta, f = load_frames(d)
    pd = max(1, int(round(86400.0 / meta["frame_s"])))
    fig, axs = plt.subplots(len(days), 2, figsize=(18, 4.3 * len(days)))
    for r, dd in enumerate(days):
        k = min(meta["frames"] - 1, dd * pd)
        axs[r, 0].imshow(natural_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
        axs[r, 0].set_title("NATURAL: day %d (sea blue, land green, cold ground white, clouds white to grey where it rains)" % dd, fontsize=9)
        axs[r, 1].imshow(aid_cover_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
        draw_coast(axs[r, 1], f, "w", 0.35)
        draw_systems(axs[r, 1], f, k, meta)
        axs[r, 1].set_title("AID: day %d cloud cover ramp (dark blue 0 to yellow 1), coast white, pressure lows red/orange (-8, -15 hPa vs zonal mean), fronts magenta/white (1.2, 2.5 K per 100 km)" % dd, fontsize=8)
        for a in axs[r]:
            a.set_xticks([0, 90, 180, 270, 360]); a.set_yticks([-60, -30, 0, 30, 60])
    fig.suptitle("%s (seed %s): cloud cover, equirectangular, natural view and aid view" % (meta["body"], meta["seed"]), fontsize=11)
    fig.tight_layout(); fig.savefig(os.path.join(out, name), dpi=62); plt.close(fig)


def globe(out, d, day=6, name="orbit_globe.png", lons=(0, 60, 120, 180, 240, 300)):
    meta, f = load_frames(d)
    pd = max(1, int(round(86400.0 / meta["frame_s"])))
    k = min(meta["frames"] - 1, day * pd)
    nat = natural_rgb(f, k)
    aid = aid_cover_rgb(f, k)
    cm = coast_mask(f["ocean"])
    fig, axs = plt.subplots(3, len(lons), figsize=(3.2 * len(lons), 10.2))
    for c, lon in enumerate(lons):
        axs[0, c].imshow(ortho_rgb(nat, lon, 15, 320, True, None)); axs[0, c].set_title("NATURAL lon %d" % lon, fontsize=8)
        axs[1, c].imshow(ortho_rgb(nat, lon, 15, 320, True, cm)); axs[1, c].set_title("NATURAL with coast lines", fontsize=8)
        axs[2, c].imshow(ortho_rgb(aid, lon, 15, 320, False, cm)); axs[2, c].set_title("AID cover ramp, coast lines", fontsize=8)
        for r in range(3):
            axs[r, c].axis("off")
    fig.suptitle("%s: orbit globe, day %d, shaded sphere, six viewing longitudes (row 1 natural, row 2 natural with coast lines, row 3 false colour)" % (meta["body"], day), fontsize=10)
    fig.tight_layout(); fig.savefig(os.path.join(out, name), dpi=60, facecolor="black"); plt.close(fig)


def poles(out, d, days=(0, 12)):
    meta, f = load_frames(d)
    pd = max(1, int(round(86400.0 / meta["frame_s"])))
    cm = coast_mask(f["ocean"])
    fig, axs = plt.subplots(2, 2 * len(days), figsize=(4.2 * 2 * len(days), 8.6))
    for c, dd in enumerate(days):
        k = min(meta["frames"] - 1, dd * pd)
        nat = natural_rgb(f, k); aid = aid_cover_rgb(f, k)
        for r, lat0 in enumerate((90, -90)):
            axs[r, 2 * c].imshow(ortho_rgb(nat, 0, lat0, 320, True, cm)); axs[r, 2 * c].set_title("NATURAL %s pole, day %d" % ("north" if lat0 > 0 else "south", dd), fontsize=8)
            axs[r, 2 * c + 1].imshow(ortho_rgb(aid, 0, lat0, 320, False, cm)); axs[r, 2 * c + 1].set_title("AID cover ramp %s pole" % ("north" if lat0 > 0 else "south"), fontsize=8)
            axs[r, 2 * c].axis("off"); axs[r, 2 * c + 1].axis("off")
    fig.suptitle("%s: cloud cover seen from above the poles (natural view and aid view)" % meta["body"], fontsize=10)
    fig.tight_layout(); fig.savefig(os.path.join(out, "poles.png"), dpi=60, facecolor="black"); plt.close(fig)


def precip(out, d, days=(0, 6, 12, 18)):
    meta, f = load_frames(d)
    pd = max(1, int(round(86400.0 / meta["frame_s"])))
    fig, axs = plt.subplots(len(days), 2, figsize=(18, 4.3 * len(days)))
    for r, dd in enumerate(days):
        k = min(meta["frames"] - 1, dd * pd)
        axs[r, 0].imshow(natural_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
        axs[r, 0].set_title("NATURAL: day %d, the clouds as seen from orbit (grey = raining)" % dd, fontsize=9)
        axs[r, 1].imshow(aid_precip_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="nearest")
        draw_coast(axs[r, 1], f, "w", 0.3)
        axs[r, 1].set_title("AID: day %d precipitation level (rain blues, snow violets; flurries, light, moderate, heavy, blizzard darker), coast white" % dd, fontsize=8)
    fig.suptitle("%s: precipitation, natural view and aid view" % meta["body"], fontsize=11)
    fig.tight_layout(); fig.savefig(os.path.join(out, "precip_equirect.png"), dpi=62); plt.close(fig)
    # level statistics of the same frames
    cls = np.concatenate([level_class(f["level"][k]).ravel() for k in range(meta["frames"])])
    return {LEVEL_NAMES[i]: float((cls == i).mean()) for i in range(6)}


def surface(out, d):
    """Snow and sea ice at the four phases: natural (land, snow, sea, ice) and aid (snow water equivalent and ice thickness ramps)."""
    meta = json.load(open(os.path.join(d, "surf_meta.json")))
    nl, nt = meta["nlon"], meta["nlat"]
    ph = meta["phases"]
    ld = lambda k: np.fromfile(os.path.join(d, "surf_%s.f32" % k), dtype=np.float32).reshape(len(ph), nt, nl)
    swe, ice = ld("swe"), ld("ice_h")
    oc = np.fromfile(os.path.join(d, "surf_ocean.f32"), dtype=np.float32).reshape(nt, nl)
    land = oc < 0.5
    label = ["northern midwinter", "northern spring", "northern midsummer", "northern autumn"]
    sea = np.array([14, 42, 92]) / 255.0; lnd = np.array([84, 104, 64]) / 255.0; white = np.array([0.93, 0.96, 0.99])
    nat = []
    for p in range(len(ph)):
        base = np.where(land[..., None], lnd, sea)
        s = np.where(land, smooth(2.5, 40.0, swe[p]), smooth(0.02, 0.25, ice[p]))
        nat.append(base * (1 - s[..., None]) + white * s[..., None])
    cm_snow = plt.get_cmap("Blues").copy(); cm_snow.set_bad("0.3")
    cm_ice = plt.get_cmap("cool").copy(); cm_ice.set_bad("0.3"); cm_ice.set_under("midnightblue")
    fig, axs = plt.subplots(3, 4, figsize=(20, 10.5))
    for p in range(len(ph)):
        axs[0, p].imshow(nat[p], extent=[0, 360, -90, 90], aspect="auto", interpolation="nearest")
        axs[0, p].set_title("NATURAL: %s" % label[p], fontsize=9)
        axs[1, p].imshow(np.log10(np.maximum(np.ma.masked_where(~land, swe[p]), 0.5)), cmap=cm_snow, vmin=np.log10(0.5), vmax=np.log10(300.0), extent=[0, 360, -90, 90], aspect="auto")
        axs[1, p].set_title("AID: snow water equivalent on land (log10 mm, grey = sea)", fontsize=8)
        axs[2, p].imshow(np.ma.masked_where(land, ice[p]), cmap=cm_ice, vmin=0.02, vmax=2.0, extent=[0, 360, -90, 90], aspect="auto")
        axs[2, p].set_title("AID: sea-ice thickness (m, 0 to 2; dark blue = open water, grey = land)", fontsize=8)
        for r in range(3):
            axs[r, p].set_xticks([]); axs[r, p].set_yticks([-60, -30, 0, 30, 60]); axs[r, p].grid(alpha=0.2)
    fig.suptitle("%s: snow and ice, natural view and aid view" % meta["body"], fontsize=11)
    fig.tight_layout(); fig.savefig(os.path.join(out, "surface_snow_ice.png"), dpi=62); plt.close(fig)
    fig, axs = plt.subplots(2, 4, figsize=(18, 9))
    views = [(0, 90.0, "north pole, northern midwinter"), (2, 90.0, "north pole, northern midsummer"), (2, -90.0, "south pole, southern midwinter"), (0, -90.0, "south pole, southern midsummer")]
    for c, (p, lat0, nm) in enumerate(views):
        axs[0, c].imshow(ortho_rgb(nat[p], 0, lat0, 300, False)); axs[0, c].set_title("NATURAL " + nm, fontsize=8)
        both = np.where(land, np.minimum(swe[p] / 120.0, 1.0), np.minimum(ice[p] / 2.0, 1.0))
        rgb = plt.get_cmap("magma")(both)[..., :3]
        axs[1, c].imshow(ortho_rgb(rgb, 0, lat0, 300, False)); axs[1, c].set_title("AID snow (land, 0-120 mm) / ice (sea, 0-2 m) ramp", fontsize=8)
        axs[0, c].axis("off"); axs[1, c].axis("off")
    fig.suptitle("%s: ice edge and snow cover from above the poles" % meta["body"], fontsize=10)
    fig.tight_layout(); fig.savefig(os.path.join(out, "surface_poles.png"), dpi=60, facecolor="white"); plt.close(fig)


def link_tracks(events, maxdeg=14.0):
    """Links the pressure minima of consecutive frames (nearest neighbour within maxdeg degrees of arc, eastward drift allowed) into tracks: list of dict(id, frames, pts)."""
    tracks = []
    active = []
    for k, ev in enumerate(events):
        mins = [tuple(m) for m in ev.get("minima", [])]
        used = set()
        nxt = []
        for tr in active:
            la, lo = tr["pts"][-1][1], tr["pts"][-1][2]
            best, bd = None, 1e9
            for i, (mla, mlo) in enumerate(mins):
                if i in used:
                    continue
                dlo = (mlo - lo + 540) % 360 - 180
                d = np.hypot(mla - la, dlo * np.cos(np.radians(la)))
                if d < bd:
                    bd, best = d, i
            if best is not None and bd <= maxdeg:
                used.add(best); tr["pts"].append((k, mins[best][0], mins[best][1])); nxt.append(tr)
        for i, (mla, mlo) in enumerate(mins):
            if i not in used:
                tr = {"id": len(tracks), "pts": [(k, mla, mlo)]}
                tracks.append(tr); nxt.append(tr)
        active = nxt
    return tracks


def strips(out, d, month_dir, starts=(20, 60, 100), n=8, step=1):
    """Frame strips of 8 frames: natural row and aid row with coloured tracks of the detected systems (circles, trails) and fronts (bars)."""
    meta, f = load_frames(d)
    mm = json.load(open(os.path.join(month_dir, "month_meta.json")))
    events = json.load(open(os.path.join(month_dir, "month_events.json")))
    tracks = link_tracks(events)
    cmap = plt.get_cmap("tab20")
    for si, s0 in enumerate(starts):
        fig, axs = plt.subplots(4, 4, figsize=(20, 11.6))
        for q in range(n):
            k = min(meta["frames"] - 1, s0 + q * step)
            r, c = (0, q) if q < 4 else (2, q - 4)
            axs[r, c].imshow(natural_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
            axs[r, c].set_title("NATURAL t = %.2f d" % ((meta["times_s"][k] - meta["times_s"][0]) / 86400.0), fontsize=8)
            ax = axs[r + 1, c]
            ax.imshow(aid_cover_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
            draw_coast(ax, f, "w", 0.3)
            for tr in tracks:
                pts = [p for p in tr["pts"] if p[0] <= k]
                if not pts or pts[-1][0] < k - 1 or len(tr["pts"]) < 4:
                    continue
                col = cmap(tr["id"] % 20)
                la = np.array([p[1] for p in pts]); lo = np.array([p[2] % 360 for p in pts])
                for i in range(1, len(pts)):
                    if abs(lo[i] - lo[i - 1]) < 180:
                        ax.plot(lo[i - 1:i + 1], la[i - 1:i + 1], "-", color=col, lw=1.6)
                ax.plot(lo[-1], la[-1], "o", mfc="none", mec=col, mew=1.6, ms=7)
            for b in events[k].get("bands", []):
                ang = np.radians(b.get("axis_deg", 0.0))
                half = 0.5 * b.get("extent_km", 600.0) / (2 * np.pi * meta["radius_km"] / 360.0)
                lo0, la0 = b["lon"] % 360, b["lat"]
                ax.plot([lo0 - half * np.cos(ang), lo0 + half * np.cos(ang)], [la0 - half * np.sin(ang), la0 + half * np.sin(ang)], "-", color="magenta", lw=2.0, alpha=0.9)
            ax.set_title("AID cover ramp; coloured lines = tracks of the detected systems, magenta bars = detected fronts", fontsize=7)
            for a in (axs[r, c], ax):
                a.set_xticks([]); a.set_yticks([])
        fig.suptitle("%s: frame strip %d (6 h steps): top rows natural, bottom rows aid view with tracks and fronts" % (meta["body"], si + 1), fontsize=11)
        fig.tight_layout(); fig.savefig(os.path.join(out, "strip_%d.png" % (si + 1)), dpi=58); plt.close(fig)


def compare(out, d1, d2, days=(2, 9, 17)):
    m1, f1 = load_frames(d1)
    m2, f2 = load_frames(d2)
    pd = max(1, int(round(86400.0 / m1["frame_s"])))
    fig, axs = plt.subplots(len(days), 4, figsize=(26, 3.9 * len(days)))
    for r, dd in enumerate(days):
        k = min(m1["frames"] - 1, dd * pd)
        for c, (f, tag) in enumerate(((f1, "round 1"), (f2, "round 2"))):
            axs[r, 2 * c].imshow(natural_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
            axs[r, 2 * c].set_title("NATURAL %s, day %d" % (tag, dd), fontsize=9)
            axs[r, 2 * c + 1].imshow(aid_cover_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto", interpolation="bilinear")
            draw_coast(axs[r, 2 * c + 1], f, "w", 0.3)
            axs[r, 2 * c + 1].set_title("AID cover ramp %s, day %d" % (tag, dd), fontsize=9)
        for a in axs[r]:
            a.set_xticks([]); a.set_yticks([-60, -30, 0, 30, 60])
    fig.suptitle("before / after: round 1 (left pair) against round 2 (right pair), same seed, same times; natural view and aid view", fontsize=11)
    fig.tight_layout(); fig.savefig(os.path.join(out, "before_after_equirect.png"), dpi=50); plt.close(fig)
    k = min(m1["frames"] - 1, 9 * pd)
    fig, axs = plt.subplots(4, 4, figsize=(15, 15))
    for r, lon in enumerate((0, 90, 180, 270)):
        for c, (f, tag) in enumerate(((f1, "round 1"), (f2, "round 2"))):
            axs[r, 2 * c].imshow(ortho_rgb(natural_rgb(f, k), lon, 15, 300, True, None)); axs[r, 2 * c].set_title("NATURAL %s lon %d" % (tag, lon), fontsize=8)
            axs[r, 2 * c + 1].imshow(ortho_rgb(aid_cover_rgb(f, k), lon, 15, 300, False, coast_mask(f["ocean"]))); axs[r, 2 * c + 1].set_title("AID %s" % tag, fontsize=8)
            axs[r, 2 * c].axis("off"); axs[r, 2 * c + 1].axis("off")
    fig.suptitle("before / after as an orbit globe (day 9)", fontsize=10)
    fig.tight_layout(); fig.savefig(os.path.join(out, "before_after_globe.png"), dpi=55, facecolor="black"); plt.close(fig)
    # level distribution and ITCZ statistics before and after
    def stat(f):
        cov = f["cover"]
        z = cov.mean(axis=(0, 2))
        return z
    return None


def main():
    cmd, out, d = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out, exist_ok=True)
    if cmd == "equirect":
        equirect(out, d)
    elif cmd == "globe":
        globe(out, d)
    elif cmd == "poles":
        poles(out, d)
    elif cmd == "precip":
        print(json.dumps(precip(out, d)))
    elif cmd == "surface":
        surface(out, d)
    elif cmd == "strips":
        strips(out, d, sys.argv[4])
    elif cmd == "compare":
        compare(out, d, sys.argv[4])
    elif cmd == "all":
        equirect(out, d); globe(out, d); poles(out, d); print(json.dumps(precip(out, d)))



# ---------------------------------------------------------------- motion of the cloud pattern (drift direction per latitude band)

def tile_motion(a0, a1, dt_s, radius_km, lat_c, size=(24, 24)):
    """Displacement of a tile between two frames by phase correlation (pixels), converted to m/s east and north."""
    ny, nx = a0.shape
    h, w = size
    out = []
    j0 = max(0, int(round((90 - lat_c) / 180 * ny)) - h // 2)
    j1 = min(ny, j0 + h)
    res = []
    for i0 in range(0, nx, w):
        t0 = a0[j0:j1, i0:i0 + w]; 
        if t0.shape[1] < w:
            continue
        # search window wider in longitude than the tile
        pad = w // 2
        cols = [(i0 + k) % nx for k in range(-pad, w + pad)]
        s1 = a1[j0:j1][:, cols]
        best, bd = -1e9, (0, 0)
        t0c = t0 - t0.mean()
        if t0c.std() < 1e-3:
            res.append((i0 + w // 2, np.nan, np.nan)); continue
        for dy in range(-3, 4):
            for dx in range(-pad, pad + 1):
                jj0, jj1 = j0 + dy, j1 + dy
                if jj0 < 0 or jj1 > ny:
                    continue
                s = a1[jj0:jj1][:, [(i0 + dx + k) % nx for k in range(w)]]
                sc = np.sum((s - s.mean()) * t0c) / (s.std() * t0c.std() * s.size + 1e-9)
                if sc > best:
                    best, bd = sc, (dx, dy)
        kmpx = 2 * np.pi * radius_km / nx
        east = bd[0] * kmpx * np.cos(np.radians(lat_c)) * 1000.0 / dt_s
        north = -bd[1] * (np.pi * radius_km / ny) * 1000.0 / dt_s
        res.append((i0 + w // 2, east, north))
    return res


def motion_field(d, step=1):
    meta, f = load_frames(d)
    cov = f["cover"]
    dt = meta["frame_s"] * step
    ny, nx = cov.shape[1:]
    lats = np.arange(-85, 90, 10)
    rows = {}
    for k in range(0, meta["frames"] - step, 2):
        for lat in lats:
            for (i, e, n) in tile_motion(cov[k], cov[k + step], dt, meta["radius_km"], lat):
                if not np.isnan(e):
                    rows.setdefault(int(lat), []).append((i, e, n, k))
    return meta, rows


def drift_table(d, step=1):
    meta, rows = motion_field(d, step)
    lines = ["| latitude band (deg) | tiles | mean east (m/s) | mean north (m/s) | share eastward | heading of the mean motion |", "|---|---|---|---|---|---|"]
    for lat in sorted(rows):
        a = np.array([(e, n) for (_, e, n, _) in rows[lat]])
        e, n = a[:, 0].mean(), a[:, 1].mean()
        head = (np.degrees(np.arctan2(e, n)) + 360) % 360
        lines.append("| %d | %d | %.1f | %.1f | %.2f | %.0f |" % (lat, len(a), e, n, (a[:, 0] > 0).mean(), head))
    return "\n".join(lines)


def motion_strips(out, d, step=1):
    """Tropical belt and polar caps: natural row and aid row with arrows of the detected pattern motion (phase correlation of 24 x 24 tiles), 6 frames each."""
    meta, f = load_frames(d)
    cov = f["cover"]
    dt = meta["frame_s"] * step
    regions = [("tropical belt, 30 S to 30 N", (-30, 30), (-25, -15, -5, 5, 15, 25)), ("north polar cap, 60 to 90 N", (60, 90), (65, 75, 85)), ("south polar cap, 90 S to 60 S", (-90, -60), (-65, -75, -85))]
    for name, (lo, hi), lats in regions:
        fig, axs = plt.subplots(2, 4, figsize=(20, 7.5))
        for q in range(4):
            k = min(meta["frames"] - step - 1, q * 4)
            ax = axs[0, q]
            ax.imshow(natural_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto"); ax.set_ylim(lo, hi)
            ax.set_title("NATURAL t = %.1f d" % ((meta["times_s"][k] - meta["times_s"][0]) / 86400.0), fontsize=8)
            ax = axs[1, q]
            ax.imshow(aid_cover_rgb(f, k), extent=[0, 360, -90, 90], aspect="auto"); ax.set_ylim(lo, hi)
            draw_coast(ax, f, "w", 0.3)
            for lat in lats:
                for (i, e, n) in tile_motion(cov[k], cov[k + step], dt, meta["radius_km"], lat):
                    if np.isnan(e):
                        continue
                    lon = (i + 0.5) / cov.shape[2] * 360.0
                    sc = 1.0
                    ax.annotate("", xy=(lon + sc * e / 3.0, lat + sc * n / 3.0), xytext=(lon, lat), arrowprops=dict(arrowstyle="->", color="red", lw=1.6))
            ax.set_title("AID cover ramp; red arrows = detected pattern motion (length = speed, 3 m/s per degree)", fontsize=7)
            for a in (axs[0, q], ax):
                a.set_xticks([])
        fig.suptitle("%s: %s, motion of the cloud pattern between frames %g h apart" % (meta["body"], name, dt / 3600.0), fontsize=11)
        fig.tight_layout()
        fn = "motion_%s.png" % ("tropics" if lo == -30 else ("north_pole" if lo == 60 else "south_pole"))
        fig.savefig(os.path.join(out, fn), dpi=58); plt.close(fig)


def main2():
    cmd, out, d = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(out, exist_ok=True)
    if cmd == "motion":
        motion_strips(out, d, int(sys.argv[4]) if len(sys.argv) > 4 else 1)
        open(os.path.join(out, "drift_direction.md"), "w").write(drift_table(d, int(sys.argv[4]) if len(sys.argv) > 4 else 1) + "
")
    else:
        main()


if __name__ == "__main__":
    main2()
