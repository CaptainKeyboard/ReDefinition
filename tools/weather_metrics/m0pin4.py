"""The pinned capture of the four M0 views, replayed in the weather player against the game (master plan sections 3, 23, 29; T09ProbePin.cs -t09PinViews / -t09PinLimits).

One command per step; each step reads what the step before it left.

  1. Capture in the game, twice (the second start gives the game's own run-to-run difference), K-T2 start perfNpic2560dlssqpin4:
       python tools/atmo_series.py run K-T2 --start perfNpic2560dlssqpin4
     The start pins ut 1486800, heading 0, and captures ground, inside, above and sky3 (the M0 places and aims of the fps runs) plus, in the ground view, the
     distance-limit sets of -t09PinLimits. Each capture is a folder series-pictures/maps/pin-<view>[-<limit>]-u1486800-perfNpic2560dlssqpin4-<utc>: cloud.npy (the
     clouds' own output before the composite), depth.npy (the depth the pass read), scene.npy (the camera target before the clouds' composite: the game's frame without
     our clouds), composed.npy (the same target after the composite), natural.npy, frames.json, pass.json and the maps; and the screen at 2560x1440 as
     series-pictures/t09-perfNpic2560dlssqpin4-<view>-plain[-<limit>]-2560x1440-pinscreen.png.
  2. Replay in the player (the four plain captures of the FIRST start as jobs pin-pin0..3; the player built in the Unity lock, the replay in the GPU lock):
       python tools/weather_metrics/m0pin4.py prepare --run A_MAPS_GLOB
       python tools/weather_metrics/m0pin4.py replay
  3. Compare, per view: game A against player (m0parity, tiles, 1:1 crops), with game A against game B as the base; the player's clouds composited over the game's own
     frame (scene.npy) beside the game's composite (composed.npy); full-resolution pictures into --pictures:
       python tools/weather_metrics/m0pin4.py compare --a A_MAPS_GLOB --b B_MAPS_GLOB --out build/m0pin4 --pictures .local/work/series-pictures/PIN4
  4. The straight line before the horizon (section 29): per row of the ground captures (plain and each limit set) the elevation over the true horizon, the clouds' alpha and
     their contrast in the composite over sky pixels, the row where the clouds end, and the distance at which a ray of that row reaches 1, 1.5 and 2 km and the air's
     transmittance to there (the pass's own aerial perspective, _FarA/_FarB):
       python tools/weather_metrics/m0pin4.py horizon --a A_MAPS_GLOB --pictures .local/work/series-pictures/PIN4

A_MAPS_GLOB: the capture folders of one start, e.g. ".local/work/series-pictures/K-T2/maps/pin-*-perfNpic2560dlssqpin4-2026112012*" (quoted: the tool expands it).
The composite the game draws (LayerReconstructor.Composite, pass 4 of the layer shader) at equal sizes is result = OutputStage(cloud) + scene * (1 - alpha); the tool checks
that formula against the game's own composed.npy before it uses it for the player (row "formula" of the table).
"""
import argparse
import glob
import json
import math
import os
import re
import subprocess
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from weather_metrics import m0parity  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
REPLAY = os.path.join(REPO, "build", "WeatherPlayer", "m0-pin-pin")
PIN_INPUT = os.path.join(REPO, "build", "weather-series", "m0pin-in")
VIEWS = ("ground", "inside", "above", "sky3")
TAG = re.compile(r"^pin-(?P<label>[a-z0-9]+(?:-[a-z0-9]+)?)-u(?P<ut>\d+)-(?P<start>[^-]+)-(?P<utc>\d{14})$")


def captures(pattern):
    """{label: folder} of one start's captures (label: the view, or view-limit); the newest per label where the pattern matches several."""
    out = {}
    for path in sorted(glob.glob(pattern)):
        m = TAG.match(os.path.basename(os.path.normpath(path)))
        if m and os.path.isfile(os.path.join(path, "pass.json")):
            out[m.group("label")] = path
    return out


def npy(path):
    """Row 0 at the top (Unity reads back bottom first)."""
    a = np.load(path).astype(np.float32)
    if a.ndim == 3 and a.shape[-1] == 1:
        a = a[..., 0]
    return a[::-1].copy()


def output_stage(cloud, pj):
    """The display stage of pass 4 (OutputStage in the layer shader) on a premultiplied linear cloud buffer, from the capture's uniforms."""
    v = pj["uniforms"]["vectors"]
    p, wt = [float(x) for x in v["_OutP"]], [float(x) for x in v.get("_OutW", [1, 1, 1, 0])]
    if p[3] < 0.5:
        return cloud.copy()
    w = max(p[2], 1e-4)
    a = np.maximum(cloud[..., 3:4], 1e-4)
    u = np.maximum(cloud[..., :3] / a * p[0] / (w * np.maximum(np.array(wt[:3], np.float32), 1e-3)), 0.0)
    out = cloud.copy()
    out[..., :3] = w * np.power(u, p[1]) * cloud[..., 3:4]
    return out


def composite(cloud, scene, pj):
    c = output_stage(cloud, pj)
    out = scene.copy()
    out[..., :3] = c[..., :3] + scene[..., :3] * (1.0 - cloud[..., 3:4])
    return out


def png(img, path):
    """A display-encoded picture (the camera target of a Gamma-space scene) as 8-bit PNG, unscaled."""
    from PIL import Image
    x = np.clip(np.rint(np.clip(img[..., :3], 0.0, 1.0) * 255.0), 0, 255).astype(np.uint8)
    Image.fromarray(x).save(path)


def cmd_prepare(a):
    caps = captures(a.run)
    order = [caps[v] for v in VIEWS if v in caps]
    missing = [v for v in VIEWS if v not in caps]
    if missing:
        print("missing views in %s: %s" % (a.run, ", ".join(missing)))
    if not order:
        raise SystemExit("no captures match %s" % a.run)
    return subprocess.call([sys.executable, os.path.join(HERE, "e40pp.py"), "--pin-prepare"] + order)


def cmd_replay(a):
    return subprocess.call([sys.executable, os.path.join(HERE, "e40pp.py"), "--pin-run"] + (["--no-build"] if a.no_build else []))


def replay_of(folder):
    """The player's r0 cloud buffer of the job prepared from this capture folder."""
    with open(os.path.join(PIN_INPUT, "pin-index.json"), encoding="utf-8") as f:
        index = json.load(f)
    for item in index:
        if os.path.normcase(os.path.abspath(item["capture"])) == os.path.normcase(os.path.abspath(folder)):
            return os.path.join(REPLAY, item["job"] + "-r0-cloud.npy")
    return None


def cmd_compare(a):
    ca, cb = captures(a.a), captures(a.b)
    os.makedirs(a.out, exist_ok=True)
    if a.pictures:
        os.makedirs(a.pictures, exist_ok=True)
    rows = []
    for view in VIEWS:
        if view not in ca:
            rows.append((view, "no capture in run A"))
            continue
        ga = ca[view]
        with open(os.path.join(ga, "pass.json"), encoding="utf-8") as f:
            pj = json.load(f)
        player = replay_of(ga)
        line = [view]
        # The composite formula against the game's own composite (the background and the clouds of the same frame).
        if os.path.isfile(os.path.join(ga, "scene.npy")) and os.path.isfile(os.path.join(ga, "composed.npy")):
            scene, composed, cloud = npy(os.path.join(ga, "scene.npy")), npy(os.path.join(ga, "composed.npy")), npy(os.path.join(ga, "cloud.npy"))
            mine = composite(cloud, scene, pj)
            d = np.abs(mine[..., :3] - composed[..., :3]).max(axis=-1)
            line.append("formula: max %.5f, p99 %.5f against the game's composed.npy" % (float(d.max()), float(np.percentile(d, 99))))
            if a.pictures:
                png(composed, os.path.join(a.pictures, "%s-composite-game-1707x960.png" % view))
                png(scene, os.path.join(a.pictures, "%s-background-game-noclouds-1707x960.png" % view))
            if player and os.path.isfile(player):
                pc = npy(player)
                if pc.shape == cloud.shape:
                    pcomp = composite(pc, scene, pj)
                    dp = np.abs(pcomp[..., :3] - composed[..., :3]).max(axis=-1)
                    line.append("player composite over the game's frame: mean %.5f, max %.5f against the game's" % (float(dp.mean()), float(dp.max())))
                    if a.pictures:
                        png(pcomp, os.path.join(a.pictures, "%s-composite-player-on-game-frame-1707x960.png" % view))
        else:
            line.append("no scene.npy / composed.npy (a capture from before the scene copies)")
        if player and os.path.isfile(player):
            base = (ga, cb[view]) if view in cb else None
            s, text = m0parity.compare(ga, player, os.path.join(a.out, view), base=base, label_a="game A", label_b="player")
            print(text)
            w = s["whole"]
            line.append("game-player colour mean %.6f max %.6f, alpha mean %.6f, beyond half step %.3f %%" % (w["mean_colour"], w["max_colour"], w["mean_alpha"], 100 * w["beyond_half_step"]))
            if base:
                b = s["base"]["whole"]
                line.append("game A-B colour mean %.6f max %.6f, alpha mean %.6f, beyond half step %.3f %%" % (b["mean_colour"], b["max_colour"], b["mean_alpha"], 100 * b["beyond_half_step"]))
                line.append("tiles structurally above the base %d of %d, noise-only %d; crops in %s" % (s["tiles_structural"], s["tiles"], s["tiles_noise_only"], os.path.join(a.out, view, "crops")))
            else:
                line.append("no run B capture of this view: no base")
        else:
            line.append("no player replay of this capture (run prepare and replay)")
        rows.append(tuple(line))
    with open(os.path.join(a.out, "m0pin4.txt"), "w", encoding="utf-8") as f:
        for r in rows:
            f.write(" | ".join(r) + "\n")
            print(" | ".join(r))
    return 0


def air_tau(pj, o, d, t, n=64):
    """The blue optical depth of the pass's aerial perspective (FarAirTau, ReDefinitionAtmospheresFarView.cginc) from o along d to t, by the midpoint rule."""
    v = pj["uniforms"]["vectors"]
    fa, fb = [float(x) for x in v["_FarA"]], [float(x) for x in v["_FarB"]]
    if fa[0] < 0.5:
        return 0.0
    R = float(pj["planetRadius"])
    s = (np.arange(n) + 0.5) / n * t
    h = np.linalg.norm(o[None, :] + s[:, None] * d[None, :], axis=1) - R
    rayleigh = np.exp(-np.maximum(h, 0.0) / fa[2]).sum() * t / n
    mie = np.exp(-np.maximum(h, 0.0) / fa[3]).sum() * t / n
    return float((fb[2] * rayleigh + fb[3] * mie) * fa[1])


def sphere_exit(o, d, radius):
    b = float(o @ d)
    c = float(o @ o) - radius * radius
    disc = b * b - c
    if disc < 0:
        return None
    t1 = -b + math.sqrt(disc)
    return t1 if t1 > 0 else None


def horizon_rows(folder):
    """Per row: the elevation of the centre column's ray, the share of sky pixels, the clouds' alpha over sky pixels, their contrast in the composite over sky pixels."""
    with open(os.path.join(folder, "pass.json"), encoding="utf-8") as f:
        pj = json.load(f)
    cam = pj["camera"]
    R = float(pj["planetRadius"])
    pos = np.array(cam["position"], float)
    fwd, right, up = (np.array(cam[k], float) for k in ("forward", "right", "up"))
    W, H, fov = int(cam["width"]), int(cam["height"]), float(cam["fov"])
    depth = npy(os.path.join(folder, "depth.npy"))
    cloud = npy(os.path.join(folder, "cloud.npy"))
    contrast = None
    if os.path.isfile(os.path.join(folder, "scene.npy")) and os.path.isfile(os.path.join(folder, "composed.npy")):
        contrast = np.abs(npy(os.path.join(folder, "composed.npy"))[..., :3] - npy(os.path.join(folder, "scene.npy"))[..., :3]).max(axis=-1)
    nup = pos / np.linalg.norm(pos)
    alt = float(np.linalg.norm(pos) - R)
    dip = math.degrees(math.acos(R / (R + alt)))
    tv = math.tan(math.radians(fov) / 2.0)
    rows = []
    for y in range(H):
        ny = 1.0 - 2.0 * (y + 0.5) / H
        v = fwd + up * ny * tv
        v /= np.linalg.norm(v)
        el = math.degrees(math.asin(float(v @ nup)))
        sky = depth[y] <= 0.0
        rows.append(dict(row=y, elev=el, above_horizon=el + dip, sky=float(sky.mean()),
                         alpha=float(cloud[y, sky, 3].mean()) if sky.any() else float("nan"),
                         contrast=float(contrast[y, sky].mean()) if contrast is not None and sky.any() else float("nan"), ray=v))
    return pj, pos, alt, dip, rows


def cmd_horizon(a):
    caps = captures(a.a)
    labels = [k for k in sorted(caps) if k == "ground" or k.startswith("ground-")]
    if not labels:
        raise SystemExit("no ground captures match %s" % a.a)
    out = []
    for label in labels:
        pj, pos, alt, dip, rows = horizon_rows(caps[label])
        R = float(pj["planetRadius"])
        # The sea line: the first row (from the top) whose centre ray meets the sea-level sphere; the rendered sea line: the first row with most pixels not sky.
        geo = next((r["row"] for r in rows if r["above_horizon"] < 0.0), None)
        drawn = next((r["row"] for r in rows if r["sky"] < 0.5), None)
        # The clouds' end: the last row above the geometric horizon where the composite contrast over sky pixels is at least a tenth of its median over the rows above.
        # A capture from before the scene copies has no contrast: the alpha stands in.
        key = "contrast" if any(not math.isnan(r["contrast"]) for r in rows) else "alpha"
        valid = [r for r in rows if geo is not None and r["row"] < geo and not math.isnan(r[key])]
        end = None
        if valid:
            med = float(np.median([r[key] for r in valid if r[key] > 0])) if any(r[key] > 0 for r in valid) else 0.0
            above = [r for r in valid if r[key] >= 0.1 * med]
            end = above[-1]["row"] if above else None
        line = ["%s: camera %.0f m over the sea-level sphere, horizon dip %.3f deg; geometric sea line row %s, rendered sea line row %s, clouds' contrast ends at row %s"
                % (label, alt, dip, geo, drawn, end)]
        if end is not None:
            r = rows[end]
            line.append("  that row: %.3f deg over the true horizon, %.3f deg over the horizontal; alpha over sky %.3f" % (r["above_horizon"], r["elev"], r["alpha"]))
            for h in (1000.0, 1500.0, 2000.0):
                t = sphere_exit(pos, r["ray"], R + h)
                if t:
                    tau = air_tau(pj, pos, r["ray"], t)
                    line.append("  reaches %.1f km at %.1f km along the ray; the air's blue transmittance to there %.3f" % (h / 1000.0, t / 1000.0, math.exp(-tau)))
        line.append("  rows (row, deg over horizon, sky share, alpha over sky, contrast over sky):")
        lo = max(0, (end or geo or 0) - 40)
        hi = min(len(rows), (drawn or geo or 0) + 8)
        for r in rows[lo:hi:2]:
            line.append("    %4d %+7.3f %.2f %.3f %.4f" % (r["row"], r["above_horizon"], r["sky"], r["alpha"], r["contrast"]))
        print("\n".join(line))
        out.extend(line)
    if a.pictures:
        os.makedirs(a.pictures, exist_ok=True)
        with open(os.path.join(a.pictures, "horizon-rows.txt"), "w", encoding="utf-8") as f:
            f.write("\n".join(out) + "\n")
        for label in labels:
            folder = caps[label]
            if os.path.isfile(os.path.join(folder, "composed.npy")):
                png(npy(os.path.join(folder, "composed.npy")), os.path.join(a.pictures, "%s-composite-game-1707x960.png" % label))
    return 0


def selftest():
    # The composite of an empty cloud buffer is the scene; of an opaque grey one with the stage off its colour.
    pj = dict(uniforms=dict(vectors=dict(_OutP=[1, 0.4545, 0.75, 0], _OutW=[1, 1, 1, 0])))
    scene = np.full((4, 4, 4), 0.5, np.float32)
    cloud = np.zeros((4, 4, 4), np.float32)
    assert np.allclose(composite(cloud, scene, pj), scene)
    cloud[..., :3], cloud[..., 3] = 0.8, 1.0
    assert np.allclose(composite(cloud, scene, pj)[..., :3], 0.8)
    # With the stage on, a cloud at the white's own value maps to the white (exposure 1, tint 1).
    pj["uniforms"]["vectors"]["_OutP"] = [1, 0.4545, 0.75, 1]
    cloud[..., :3] = 0.75
    assert np.allclose(composite(cloud, scene, pj)[..., :3], 0.75, atol=1e-5)
    assert TAG.match("pin-ground-haze0-u1486800-perfNpic2560dlssqpin4-20261120120000").group("label") == "ground-haze0"
    assert TAG.match("pin-sky3-u1486800-perfNpic2560dlssqpin4-20261120120000").group("label") == "sky3"
    print("selftest passed")
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = p.add_subparsers(dest="cmd")
    s = sub.add_parser("prepare")
    s.add_argument("--run", required=True, help="glob of one start's capture folders")
    s = sub.add_parser("replay")
    s.add_argument("--no-build", action="store_true")
    s = sub.add_parser("compare")
    s.add_argument("--a", required=True)
    s.add_argument("--b", required=True)
    s.add_argument("--out", default=os.path.join(REPO, "build", "m0pin4"))
    s.add_argument("--pictures")
    s = sub.add_parser("horizon")
    s.add_argument("--a", required=True)
    s.add_argument("--pictures")
    sub.add_parser("selftest")
    a = p.parse_args(argv)
    return {"prepare": cmd_prepare, "replay": cmd_replay, "compare": cmd_compare, "horizon": cmd_horizon,
            "selftest": lambda _a: selftest()}.get(a.cmd, lambda _a: p.print_help() or 2)(a)


if __name__ == "__main__":
    sys.exit(main())
