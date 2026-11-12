"""The series of the lightning flash in the cloud (package T07), run through tools/build_weather_player.py --series T07-flash:

  The scene 'light' of the weather player draws the own cloud system's layer pass with the cloud light of package T03, whose hook for
  lightning is the list of bolt lights (CloudLightSettings.Bolts; _BoltPos/_BoltCol in ReDefinitionAtmospheresCloudLight.cginc). This
  series draws the harness's single cloud (an ellipsoid of 800 m radius, base at 2000 m altitude, extinction 0.02 per m) without and with
  a flash and compares the two on the cloud's pixels:

    day / dusk / night   the luminance of the cloud with a stroke at its peak over the luminance before (the row R22-lightning-flash of
                         tests/data/parity/checks.json: more than 2 times), a cloud-internal flash at the cloud's centre. The bolt light is
                         a term of its own in the march (not in the direct/ambient split of aid 4), so the aid pictures here are the ratio
                         and the added light;
    ground               a flash to the ground, its light at 0.8 of the way up to the cloud's light centre (an info figure);
    level, range         the colour of the light scales with the flash level and the light grows with its reach. The linearity row pins the
                         plumbing (the bolt term is linear by construction); the reach row checks that the falloff is applied;
    whole cloud          the share of the cloud's pixels that a cloud-internal flash brightens by more than half (the cloud lights up as a
                         whole, not in a spot).

  The numbers of a flash are those of LightningModule.FillBolts: colour (0.82, 0.88, 1.0) x CloudPeak x level, the reach 600 m plus 0.15
  of the cell's height (here the cell's whole height, 1120 m). The harness gives the sun the value pi.
  Pictures (natural and aid, unique names): build/weather-series/T07-pictures/.
"""
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import core  # noqa: E402
from weather_metrics import light as T  # noqa: E402
from weather_metrics import parity_checks as pc  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "T07-in")
PICTURES = os.path.join(BUILD, "T07-pictures")
R = T.R
CELL = T.CELL_CENTRE
FLASH_TINT = (0.82, 0.88, 1.0)
CLOUD_PEAK = 1.6          # LightningSwitches.CloudPeak, in the harness's units (its sun is pi); first value was 9.4, which blew the cloud out to white
CELL_HEIGHT_M = 1120.0    # the harness cell's vertical extent (2 x 560 m)
RANGE_M = 600.0 + 0.15 * CELL_HEIGHT_M

# id of the view in T.VIEW_TABLE -> (label, sky colour of the backdrop)
SCENES = {"day": "storm", "dusk": "sunset", "night": "night"}


def reach(top_m=CELL_HEIGHT_M):
    return 600.0 + 0.15 * top_m


def bolt(position, range_m, level, scale=1.0):
    k = CLOUD_PEAK * level * scale
    return dict(pos=list(position), range=float(range_m), col=[FLASH_TINT[0] * k, FLASH_TINT[1] * k, FLASH_TINT[2] * k])


def flash_job(jid, scene, bolts, aid=0, w=96):
    """The cell of a view of the light series with the bolts added."""
    spec = [s for s in T.VIEW_TABLE if s[0] == SCENES.get(scene, scene)][0]
    j = T.view_job(jid, spec, aid=aid, w=w)
    if bolts:
        j["boltPos"] = [c for b in bolts for c in b["pos"]]
        j["boltRange"] = [b["range"] for b in bolts]
        j["boltCol"] = [c for b in bolts for c in b["col"]]
    return j


def centre_flash(level=1.0, range_m=None):
    # LightningModule.FillBolts: a cloud-internal flash at 0.8 of the peak, at the light centre of the cloud.
    return bolt([CELL[0], CELL[1], CELL[2]], range_m if range_m else RANGE_M, level, 0.8)


def ground_flash(level=1.0):
    # A flash to the ground: FillBolts puts the light at 0.8 of the way from the strike point to the cloud's light centre (here 120 m beside
    # the cell's axis, the strike at the ground, the centre at 2000 m altitude) with the full peak.
    return bolt([120.0, 0.0, R + 0.8 * 2000.0], RANGE_M, level, 1.0)


def build_jobs():
    jobs = []
    for scene in SCENES:
        jobs.append(flash_job("FL-%s-0" % scene, scene, []))
        jobs.append(flash_job("FL-%s-1" % scene, scene, [centre_flash(1.0)]))
    jobs.append(flash_job("FL-day-g", "day", [ground_flash()]))
    jobs.append(flash_job("FL-night-g", "night", [ground_flash()]))
    for level in (0.25, 0.5, 0.75):
        jobs.append(flash_job("FL-night-l%d" % round(level * 100), "night", [centre_flash(level)]))
    for r in (400.0, 800.0, 1600.0, 3200.0):
        jobs.append(flash_job("FL-night-r%d" % int(r), "night", [centre_flash(1.0, r)]))
    return jobs


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    jobs = build_jobs()
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": jobs}, f)
    return jobs


def stages():
    jobs = os.path.join(INPUT, "jobs.json")
    if not os.path.isfile(jobs) or os.path.getmtime(jobs) < os.path.getmtime(os.path.abspath(__file__)):
        write_inputs()
    return [Stage("scene", ["-mode", "scene", "-scene", "light", "-in", INPUT], size=(1280, 720), proxy=False)]


# --------------------------------------------------------------------------------------------------------------------------------
# Reading
# --------------------------------------------------------------------------------------------------------------------------------
def rgba(folder, jid):
    return tuple(T.plane(folder, jid, c) for c in "rgba")


def luma(img):
    return (img[0] + img[1] + img[2]) / 3.0


def cloud_mask(folder, scene):
    return rgba(folder, "FL-%s-0" % scene)[3] > 0.05


def lum_pair(folder, scene, flash_id=None, mask=None):
    """(luminance before, luminance with the flash), the means of the radiance over the cloud's pixels of the base picture."""
    base = rgba(folder, "FL-%s-0" % scene)
    m = cloud_mask(folder, scene) if mask is None else mask
    fl = rgba(folder, flash_id or "FL-%s-1" % scene)
    return float(luma(base)[m].mean()), float(luma(fl)[m].mean())


def ratio_data(folder, scene, flash_id=None):
    before, during = lum_pair(folder, scene, flash_id)
    return dict(flash_lum=during, base_lum=before)


def collect(ctx):
    spec = pc.load_spec()["R22-lightning-flash"]
    folder = ctx.folder("scene")
    out = list(core.player_judged("scene")(ctx))
    if not os.path.isfile(os.path.join(folder, "FL-day-0-r.npy")):
        out.append(Metric.flag("written", False))
        return out
    finite = True
    for scene in SCENES:
        for jid in ("FL-%s-0" % scene, "FL-%s-1" % scene):
            for ch in "rgba":
                finite &= bool(np.isfinite(T.plane(folder, jid, ch)).all())
    out.append(Metric.flag("flash.finite", finite))
    ratios = {}
    for scene in SCENES:
        data = ratio_data(folder, scene)
        m = pc.run_check("lightning_flash", data, spec)
        ratios[scene] = m.value
        out.append(Metric.at_least("R22-lightning-flash.%s" % scene, m.value, 2.0))
        out.append(Metric.info("flash.%s.base_lum" % scene, data["base_lum"]))
        out.append(Metric.info("flash.%s.flash_lum" % scene, data["flash_lum"]))
    # The ground flash: the underside only.
    for scene in ("day", "night"):
        b, f = lum_pair(folder, scene, "FL-%s-g" % scene)
        out.append(Metric.info("flash.%s.ground_ratio" % scene, f / max(b, 1e-12)))
    # Linearity in the level (night: the sun is off, the moon is faint, the flash is almost all the light).
    base = luma(rgba(folder, "FL-night-0"))
    m = cloud_mask(folder, "night")
    deltas = []
    for level in (25, 50, 75):
        deltas.append(float((luma(rgba(folder, "FL-night-l%d" % level)) - base)[m].mean()))
    full = float((luma(rgba(folder, "FL-night-1")) - base)[m].mean())
    dev = max(abs(d / max(full, 1e-12) - lv / 100.0) for d, lv in zip(deltas, (25, 50, 75)))
    out.append(Metric.at_most("flash.level_linearity", dev, 0.03))
    # The reach: the light the cloud gets grows with the reach.
    rs = []
    for r in (400, 800, 1600, 3200):
        rs.append(float((luma(rgba(folder, "FL-night-r%d" % r)) - base)[m].mean()))
    out.append(Metric.flag("flash.range_monotone", all(rs[i] < rs[i + 1] for i in range(len(rs) - 1))))
    # The whole cloud: the share of the cloud's pixels that the cloud-internal flash brightens by more than half at night and by day.
    for scene in ("day", "night"):
        b = luma(rgba(folder, "FL-%s-0" % scene))
        f = luma(rgba(folder, "FL-%s-1" % scene))
        mm = cloud_mask(folder, scene)
        share = float((f[mm] > b[mm] * 1.5 + 1e-4).mean())
        out.append(Metric.at_least("flash.%s.lit_share" % scene, share, 0.5))
        out.append(Metric.info("flash.%s.doubled_share" % scene, float((f[mm] > b[mm] * 2.0 + 1e-4).mean())))
    out.extend(pictures(folder))
    return out


# --------------------------------------------------------------------------------------------------------------------------------
# Pictures: natural (off, on) and the aid view (the light the flash adds, as a heat map of the ratio, and the direct/ambient split)
# --------------------------------------------------------------------------------------------------------------------------------
def save(img, path):
    from PIL import Image
    os.makedirs(os.path.dirname(path), exist_ok=True)
    Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).resize((img.shape[1] * 5, img.shape[0] * 5), Image.NEAREST).save(path)


def pictures(folder):
    out = []
    for scene, view in SCENES.items():
        sky = T.sky_of(view)
        off = T.natural_image(folder, "FL-%s-0" % scene, sky)
        on = T.natural_image(folder, "FL-%s-1" % scene, sky)
        save(off, os.path.join(PICTURES, "T07-flash-%s-OFF.png" % scene))
        save(on, os.path.join(PICTURES, "T07-flash-%s-ON.png" % scene))
        b = luma(rgba(folder, "FL-%s-0" % scene))
        f = luma(rgba(folder, "FL-%s-1" % scene))
        a = rgba(folder, "FL-%s-0" % scene)[3]
        ratio = np.where(a > 0.05, f / np.maximum(b, 1e-4), 0.0)
        # The aid: the ratio on a log scale from 1 (no change) to 16 as a heat map, black outside the cloud.
        heat = T.heat(np.clip(np.log2(np.maximum(ratio, 1.0)) / 4.0, 0.0, 1.0)) * (a[..., None] > 0.05)
        save(heat, os.path.join(PICTURES, "T07-flash-%s-aid-ratio.png" % scene))
        # The light the flash adds, alone (ON minus OFF in radiance, scaled to its own maximum): where in the cloud the flash's light lands.
        added = np.maximum(f - b, 0.0) * (a > 0.05)
        save(T.heat(added / max(float(added.max()), 1e-9)) * (a[..., None] > 0.05), os.path.join(PICTURES, "T07-flash-%s-aid-added.png" % scene))
        out.append(Metric.info("picture.%s.max_ratio" % scene, float(ratio.max())))
    return out


register_series("T07-flash", stages(), collect, exclusive=False, who="T07")


# --------------------------------------------------------------------------------------------------------------------------------
# The aid pictures of the geometry, the timeline, the envelope and the thunder (from the numbers LightningDumpTests writes)
# --------------------------------------------------------------------------------------------------------------------------------
GEOMETRY = os.path.join(BUILD, "T07-geometry", "geometry.json")


def geometry_pictures(path=GEOMETRY, out_dir=PICTURES):
    """Draws bolts (side view, two projections), the event timeline of ten minutes, the envelope of a flash and the thunder envelopes."""
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    os.makedirs(out_dir, exist_ok=True)
    fig, axes = plt.subplots(1, len(doc["bolts"]), figsize=(3.2 * len(doc["bolts"]), 5.6), facecolor="#05070c")
    for ax, bolt_doc in zip(np.atleast_1d(axes), doc["bolts"]):
        ax.set_facecolor("#05070c")
        for s in bolt_doc["strands"]:
            p = np.array(s["points"])
            ax.plot(p[:, 0], p[:, 1], color=(0.85, 0.9, 1.0), lw=2.2 * s["brightness"] + 0.4, alpha=0.4 + 0.6 * s["brightness"])
        ax.set_aspect("equal")
        ax.set_title("seed %d" % bolt_doc["seed"], color="w", fontsize=8)
        ax.tick_params(colors="#889", labelsize=6)
    fig.suptitle("T07 bolt geometry, side view (m): main channel and branches", color="w", fontsize=9)
    fig.savefig(os.path.join(out_dir, "T07-bolt-geometry-aid.png"), dpi=110, facecolor=fig.get_facecolor())
    plt.close(fig)

    tl = doc["timeline"]
    fig, (a1, a2) = plt.subplots(2, 1, figsize=(9, 5), facecolor="w")
    shown = [e for e in tl if e["shown"]]
    dropped = [e for e in tl if not e["shown"]]
    a1.vlines([e["ut"] for e in shown], 0, [e["bumps"] for e in shown], color="tab:blue", lw=1.2, label="shown")
    a1.vlines([e["ut"] for e in dropped], 0, [e["bumps"] for e in dropped], color="tab:red", lw=1.2, label="dropped by the limit")
    a1.set_ylabel("strokes")
    a1.set_title("T07 event timeline of 10 minutes, one storm cell (%d flashes, %d shown)" % (len(tl), len(shown)))
    a1.legend(fontsize=7)
    a2.scatter([e["east"] / 1000 for e in tl], [e["north"] / 1000 for e in tl], c=[e["ground"] for e in tl], cmap="coolwarm", s=10)
    a2.set_xlabel("east (km)")
    a2.set_ylabel("north (km)")
    a2.set_aspect("equal")
    a2.set_title("places (red = to the ground)", fontsize=8)
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "T07-event-timeline-aid.png"), dpi=110)
    plt.close(fig)

    env = np.array(doc["envelope"])
    fig, ax = plt.subplots(figsize=(6, 2.6))
    ax.plot(env[:, 0], env[:, 1], color="tab:blue")
    ax.set_xlabel("s after the start")
    ax.set_ylabel("light")
    ax.set_title("T07 flash envelope: three strokes and the glow of a flash to the ground")
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "T07-flash-envelope-aid.png"), dpi=110)
    plt.close(fig)

    fig, axes = plt.subplots(len(doc["thunder"]), 1, figsize=(8, 1.6 * len(doc["thunder"])), sharex=True)
    for ax, t in zip(np.atleast_1d(axes), doc["thunder"]):
        e = np.array(t["env"])
        x = np.arange(len(e)) / 100.0
        ax.fill_between(x, -e, e, color="tab:gray")
        ax.set_ylabel("%d m" % t["distance"], fontsize=7)
        ax.text(0.99, 0.8, "delay %.1f s, cutoff %d Hz" % (t["delay"], t["cutoff"]), transform=ax.transAxes, ha="right", fontsize=7)
    axes[-1].set_xlabel("s after the arrival")
    fig.suptitle("T07 thunder envelopes by distance (peak per 10 ms)", fontsize=9)
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "T07-thunder-envelopes-aid.png"), dpi=110)
    plt.close(fig)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "geometry":
        geometry_pictures()
    else:
        write_inputs()
        print("wrote", os.path.join(INPUT, "jobs.json"))
