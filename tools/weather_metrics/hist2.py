"""The history's test bed (HIST2, master plan section 12.4): camera paths through the game's pinned captures, replayed in the weather player, every frame of every variant
against a reference of the same pose, and the near / far cost table.

One command per step:

  1. prepare   the pinned game captures as replay jobs (build/weather-series/hist2-in; job pin-pin<n>, the order of VIEWS):
       python tools/weather_metrics/hist2.py prepare
  2. motion    the series hist2-motion: per case (a camera path on one view) one player run under the GPU lock (shared), the variants and the reference along the same path; then
               the pictures (2560x1440, the player's 1707x960 cloud buffer composited over the game's own frame without clouds and scaled by 1.5 with Lanczos), the error
               pictures against the reference, 1:1 crop strips and one guard number per sequence; the frame dumps are deleted when the pictures are made:
       python tools/weather_metrics/hist2.py motion --cases fwd150,yaw30 --variants "rot2:,rot3:Recon.Block=3" [--no-build] [--out .local/work/series-pictures/HIST2]
  3. cost      the GPU time of the march and of the reconstruction per view and variant, alternating rounds, under the EXCLUSIVE GPU lock (the proxy's timestamps):
       python tools/weather_metrics/hist2.py cost --variants "rot2:,rot3:Recon.Block=3" [--views ground,inside,above] [--rounds 6] [--no-build]

A variant is name:setting;setting where a setting is a path of the replay's settings (LayerPassSettings: Frame.<field>, Recon.<field>, Noise.<field>) = value over the game's own
settings of the capture (which hold today's 2 x 2 rotation: Recon.Mode 1, Block 2, Rotation 1, FreshWeight 0.2, StaleWeight 0, ClipSigma 1, CatmullHistory 1). Examples:
  rot2:                                                                  today (the capture's settings unchanged)
  rot3:Recon.Block=3                                                     the rejected 3 x 3 rotation
  rot4:Recon.Block=4
  far5:Recon.Mode=2;Recon.Rotation=0;Recon.Resolution=1;Recon.FarRotateM=5000;Recon.FarRotateBlock=2   the far rotation of 2026-11-19 (full-size march with a history, beyond
                                                                         5 km a pixel marched every 4th frame)
  near5:Frame.CostCutM=5000            (cost only: the march ends at 5 km)      far5only:Frame.CostStartM=5000   (cost only: the march starts at 5 km)
The reference (variant ref, added by the tool) is the same pose and noise marched at every pixel with no history (Recon.Mode Full), the mean of --ref-frames dither phases.
Tuning keys (recon.*, cloud.*) are NOT read by the player (OwnCloudOptions is not linked); a new switch is reached through the field it sets.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import time

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
REPO = os.path.dirname(os.path.dirname(HERE))
MAIN = os.path.dirname(os.path.dirname(os.path.dirname(REPO))) if ".claude" in REPO.replace("\\", "/").split("/") else REPO
MAPS = os.path.join(MAIN, ".local", "work", "series-pictures", "K-T2", "maps")
IN = os.path.join(REPO, "build", "weather-series", "hist2-in")
OUT_DEFAULT = os.path.join(MAIN, ".local", "work", "series-pictures", "HIST2")

# The captures (K-T2 starts perfNpic2560dlssqpin1686 and perfNpic2560dlssqpin4, A4500, 2560x1440 DLSS Quality, render 1707x960): the job index is the order here.
VIEWS = [
    ("ground", "pin-ground-u1686000-perfNpic2560dlssqpin1686-20261120132521"),      # front-lit cumulus from the pad, rocket and terrain in front
    ("inside", "pin-inside-u1486800-perfNpic2560dlssqpin4-20261120121537"),         # between the layers at 4.6 km, a deck close above, cumulus below
    ("above", "pin-above-u1686000-perfNpic2560dlssqpin1686-20261120132601"),        # 8.4 km over the cumulus field, front-lit
    ("groundback", "pin-ground-u1486800-perfNpic2560dlssqpin4-20261120121446"),     # the M0 ground view, back-lit
]

# The camera paths, at 50 frames a second (the frame rate the work aims at). vel / start: forward, right, up in the capture camera's frame (m/s, m); yaw deg/s about the local up;
# wind: the game's per-layer wind times this factor (0: the pinned time's frozen pattern); depth 0: no scene depth (rays to the planet).
CASES = {
    "still": dict(view="ground"),
    "stillinside": dict(view="inside"),
    "stillabove": dict(view="above"),
    "fwd50": dict(view="ground", vel=(50, 0, 0)),
    "fwd150": dict(view="inside", vel=(150, 0, 0)),
    "fwd300": dict(view="inside", vel=(300, 0, 0)),
    "fwdabove300": dict(view="above", vel=(300, 0, 0)),
    "side150": dict(view="inside", vel=(0, 150, 0)),
    "yaw30": dict(view="ground", yaw=30.0),
    "yawinside30": dict(view="inside", yaw=30.0),
    "climb": dict(view="inside", vel=(150, 0, 120), start=(0, 0, 0)),
    "wind": dict(view="ground", wind=8.0),
    "still_back": dict(view="groundback"),
}
FPS = 50.0


def bwp():
    import build_weather_player as m
    return m


def job_of(view):
    return "pin-pin%d" % [v for v, _ in VIEWS].index(view)


def capture_of(view):
    return os.path.join(MAPS, dict(VIEWS)[view])


def cmd_prepare(a):
    from weather_metrics import e40pp
    caps = [capture_of(v) for v, _ in VIEWS]
    for c in caps:
        if not os.path.isfile(os.path.join(c, "pass.json")):
            raise SystemExit("missing capture %s" % c)
    e40pp.prepare_pin(caps, out=IN)
    with open(os.path.join(IN, "jobs.json"), encoding="utf-8") as f:
        jobs = json.load(f)["jobs"]
    for (view, _), job in zip(VIEWS, jobs):
        print("%s = %s, layers %s" % (view, job["id"], ", ".join("%s %.0f-%.0f m" % (l["name"], l["inner"], l["outer"]) for l in job["layers"])))
    return 0


def build(no_build, who):
    if no_build:
        return 0
    m = bwp()
    return m.under_lock("unity", who, 30, False, [sys.executable, m.__file__, "--stage", "build"])


def player(label, extra, size, timeout, proxy):
    """One player run (the caller holds the GPU lock); the WEATHERPLAYER lines and the output folder."""
    m = bwp()
    exe = os.path.join(m.OUTPUT, "WeatherPlayer.exe")
    if not os.path.exists(exe):
        raise SystemExit("the player is not built (%s)" % exe)
    m.write_ini()
    beside = os.path.join(m.OUTPUT, "dxgi.dll")
    if proxy:
        shutil.copy2(m.PROXY, m.OUTPUT)
    elif os.path.exists(beside):
        os.remove(beside)
    folder = m.fresh(label)
    t0 = time.time()
    lines = m.run_lines(exe, label, ["-out", folder] + list(extra), size, timeout)
    print("%s: %.0f s" % (label, time.time() - t0))
    return lines, folder


def variant_arg(variants, with_ref):
    out = [v.strip() for v in variants.split(",") if v.strip()]
    names = [v.split(":")[0] for v in out]
    if with_ref and "ref" not in names:
        out = ["ref"] + out
    return ",".join(out), (["ref"] if with_ref and "ref" not in names else []) + names


# ---------------------------------------------------------------------------------------------------------------------------- pictures
def load_half(path):
    """Row 0 at the top."""
    return np.load(path).astype(np.float32)[::-1].copy()


def composite_display(cloud, scene, pj):
    from weather_metrics import m0pin4
    return np.clip(m0pin4.composite(cloud, scene, pj)[..., :3], 0.0, 1.0)


def upscale(img, factor=1.5):
    from PIL import Image
    h, w = img.shape[:2]
    x = Image.fromarray(np.clip(np.rint(img * 255.0), 0, 255).astype(np.uint8))
    return np.asarray(x.resize((int(round(w * factor)), int(round(h * factor))), Image.LANCZOS))


def save(arr8, path):
    from PIL import Image
    os.makedirs(os.path.dirname(path), exist_ok=True)
    Image.fromarray(arr8).save(path, compress_level=6)


def label_strip(tiles, row_labels, col_labels, tile, path):
    """tiles[row][col]: uint8 (tile, tile, 3) crops at 1:1; a grid with labels left and on top."""
    from PIL import Image, ImageDraw
    lw, th = 150, 22
    rows, cols = len(tiles), len(tiles[0])
    img = Image.new("RGB", (lw + cols * (tile + 4), th + rows * (tile + 4)), (24, 24, 24))
    d = ImageDraw.Draw(img)
    for c, t in enumerate(col_labels):
        d.text((lw + c * (tile + 4) + 4, 4), t, fill=(230, 230, 230))
    for r, t in enumerate(row_labels):
        d.text((4, th + r * (tile + 4) + tile // 2 - 6), t, fill=(230, 230, 230))
        for c in range(cols):
            img.paste(Image.fromarray(tiles[r][c]), (lw + c * (tile + 4), th + r * (tile + 4)))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path)


def region_errors(err, ref_alpha, img_alpha):
    """Mean error (8-bit) by region at the render size: edge (where the reference's alpha changes by more than 0.1 within 2 px), thin (alpha 0.02-0.5), dense (above 0.5),
    border (the outer 6 % of the picture, any cover), sky (neither has cover); and the raster index of the error over the pixel phases of a 2 x 2 and a 3 x 3 grid."""
    from scipy import ndimage
    a = ref_alpha
    gx = np.abs(np.diff(a, axis=1, append=a[:, -1:]))
    gy = np.abs(np.diff(a, axis=0, append=a[-1:, :]))
    edge = ndimage.binary_dilation(np.maximum(gx, gy) > 0.05, iterations=2)
    cover = np.maximum(a, img_alpha) > 0.02
    h, w = a.shape
    by, bx = int(0.06 * h), int(0.06 * w)
    border = np.zeros_like(cover)
    border[:by] = border[-by:] = True
    border[:, :bx] = border[:, -bx:] = True
    inner = cover & ~border

    def mean(m):
        return float(err[m].mean()) if m.any() else 0.0

    out = dict(edge=mean(edge & inner), thin=mean(inner & ~edge & (a <= 0.5)), dense=mean(inner & ~edge & (a > 0.5)), border=mean(border & cover), sky=mean(~cover))
    for n in (2, 3):
        ph = []
        for py in range(n):
            for px in range(n):
                m = inner.copy()
                m[:] = False
                m[py::n, px::n] = inner[py::n, px::n]
                ph.append(mean(m))
        ph = np.array(ph)
        out["raster%d" % n] = float(ph.std() / max(ph.mean(), 1e-6))
    return out


def pick_windows(err, alpha, tile, count):
    """Crop windows (y, x at the 2560 scale) where the error against the reference is largest, at least a tile apart, inside the picture."""
    h, w = err.shape
    step = tile // 2
    cands = []
    for y in range(0, h - tile + 1, step):
        for x in range(0, w - tile + 1, step):
            if alpha[y:y + tile, x:x + tile].mean() < 0.02:
                continue
            cands.append((float(err[y:y + tile, x:x + tile].mean()), y, x))
    cands.sort(reverse=True)
    out = []
    for e, y, x in cands:
        if all(abs(y - oy) >= tile or abs(x - ox) >= tile for _, oy, ox in out):
            out.append((e, y, x))
        if len(out) >= count:
            break
    return [(y, x) for _, y, x in out]


def process_case(case, folder, names, out_root, keep_every_err=4):
    """Pictures, errors, strips and guard numbers of one case; deletes the dumps."""
    spec = CASES[case]
    view = spec["view"]
    job = job_of(view)
    cap = capture_of(view)
    with open(os.path.join(cap, "pass.json"), encoding="utf-8") as f:
        pj = json.load(f)
    from weather_metrics import m0pin4
    scene = m0pin4.npy(os.path.join(cap, "scene.npy"))
    frames = sorted({int(f.split("-f")[-1][:-4]) for f in os.listdir(folder) if f.startswith(job + "-") and f.endswith(".npy")})
    if not frames:
        print("%s: no dumps in %s" % (case, folder))
        return None
    base = os.path.join(out_root, case)
    guard = {n: [] for n in names if n != "ref"}
    regions = {}
    last = {}
    for i, k in enumerate(frames):
        ref = load_half(os.path.join(folder, "%s-ref-f%d.npy" % (job, k)))
        ref_disp = composite_display(ref, scene, pj)
        ref8 = upscale(ref_disp)
        save(ref8, os.path.join(base, "ref", "%s-%s-ref-f%02d-2560x1440.png" % (case, view, i)))
        for n in names:
            if n == "ref":
                continue
            p = os.path.join(folder, "%s-%s-f%d.npy" % (job, n, k))
            if not os.path.isfile(p):
                continue
            img = load_half(p)
            disp = composite_display(img, scene, pj)
            img8 = upscale(disp)
            save(img8, os.path.join(base, n, "%s-%s-%s-f%02d-2560x1440.png" % (case, view, n, i)))
            e = np.abs(img8.astype(np.int16) - ref8.astype(np.int16)).max(axis=-1)
            mask = np.maximum(ref[..., 3], img[..., 3]) > 0.02
            diff = (disp - ref_disp) * 255.0
            guard[n].append(float(np.sqrt((diff[mask] ** 2).mean())) if mask.any() else 0.0)
            if i >= len(frames) - 8:
                regions.setdefault(n, []).append(region_errors(np.abs(diff).max(axis=-1), ref[..., 3], img[..., 3]))
            if i % keep_every_err == 0 or i == len(frames) - 1:
                save(np.clip(e * 4, 0, 255).astype(np.uint8)[..., None].repeat(3, axis=-1),
                     os.path.join(base, n + "-error-x4", "%s-%s-%s-error-x4-f%02d-2560x1440.png" % (case, view, n, i)))
            last[n] = (img8, e, upscale(np.repeat(np.maximum(ref[..., 3:4], img[..., 3:4]), 3, axis=-1))[..., 0] / 255.0)
        last["ref"] = (ref8, None, None)
    # Strips: per window, rows = ref and each variant, columns = every 3rd dumped frame, 1:1 at 2560x1440. Windows where the LAST variant's error is largest in the last frame,
    # and one where the first variant's is.
    tile = 256
    stripframes = frames[::max(1, len(frames) // 8)][:8]
    order = ["ref"] + [n for n in names if n != "ref"]
    wins = []
    for n in reversed(order[1:]):
        if n in last:
            wins += [w for w in pick_windows(last[n][1].astype(np.float32), last[n][2], tile, 3) if w not in wins]
    for wi, (y, x) in enumerate(wins[:4]):
        rows = []
        for n in order:
            row = []
            for k in stripframes:
                p = os.path.join(base, n, "%s-%s-%s-f%02d-2560x1440.png" % (case, view, n, frames.index(k)))
                from PIL import Image
                row.append(np.asarray(Image.open(p))[y:y + tile, x:x + tile])
            rows.append(row)
        label_strip(rows, order, ["frame %d" % frames.index(k) for k in stripframes], tile,
                    os.path.join(base, "%s-%s-strip%d-y%d-x%d-1to1.png" % (case, view, wi, y, x)))
    for f in os.listdir(folder):
        if f.endswith(".npy"):
            os.remove(os.path.join(folder, f))
    lines = ["%s (%s, %d frames): RMSE against the reference over cloud pixels, 8-bit display, mean / max over frames (a guard only; judge the strips)" % (case, view, len(frames))]
    for n, g in guard.items():
        if g:
            lines.append("  %-14s %6.2f / %6.2f" % (n, float(np.mean(g)), float(np.max(g))))
    lines.append("  where (last 8 frames, mean 8-bit error at the render size 1707x960 by region; raster: spread of the error's mean over the pixel phases of a 2 x 2 / 3 x 3 grid, "
                 "std over mean, 0 = none):")
    lines.append("  %-14s %7s %7s %7s %7s %7s %8s %8s" % ("variant", "edge", "thin", "dense", "border", "sky", "raster2", "raster3"))
    for n, rs in regions.items():
        m = {k: float(np.mean([r[k] for r in rs])) for k in rs[0]}
        lines.append("  %-14s %7.2f %7.2f %7.2f %7.2f %7.2f %8.3f %8.3f" % (n, m["edge"], m["thin"], m["dense"], m["border"], m["sky"], m["raster2"], m["raster3"]))
    text = "\n".join(lines)
    with open(os.path.join(base, "%s-guard.txt" % case), "w", encoding="utf-8") as f:
        f.write(text + "\n")
    print(text)
    return text


def motion_extra(case, variants, ref_frames, warm, dump):
    s = CASES[case]
    extra = ["-mode", "scene", "-scene", "pin-motion", "-in", IN, "-jobs", job_of(s["view"]), "-variants", variants,
             "-pathFps", str(FPS), "-pathWarm", str(warm), "-pathDump", str(dump), "-refFrames", str(ref_frames)]
    if "vel" in s:
        extra += ["-pathVel", ",".join(str(v) for v in s["vel"])]
    if "start" in s:
        extra += ["-pathStart", ",".join(str(v) for v in s["start"])]
    if "yaw" in s:
        extra += ["-pathYaw", str(s["yaw"])]
    if "wind" in s:
        extra += ["-pathWind", str(s["wind"])]
    if "depth" in s:
        extra += ["-pathDepth", str(s["depth"])]
    return extra


def cmd_motion_inner(a):
    variants, names = variant_arg(a.variants, True)
    with open(os.path.join(IN, "jobs.json"), encoding="utf-8") as f:
        jobs = {j["id"]: j for j in json.load(f)["jobs"]}
    texts = []
    for case in a.cases.split(","):
        s = CASES[case]
        j = jobs[job_of(s["view"])]
        lines, folder = player("hist2-" + case, motion_extra(case, variants, a.ref_frames, a.warm, a.dump), (j["width"], j["height"]), 3000, False)
        ok = lines is not None and any(line == "all checks passed" for line in lines)
        if not ok:
            print("\n".join(lines or ["(no player lines)"]))
        texts.append(process_case(case, folder, names, a.out) or "%s: no pictures" % case)
    with open(os.path.join(a.out, "guard-%s.txt" % time.strftime("%Y%m%d-%H%M%S")), "w", encoding="utf-8") as f:
        f.write("variants: %s\n\n" % variants + "\n\n".join(texts) + "\n")
    return 0


def cmd_motion(a):
    code = build(a.no_build, "hist2-motion")
    if code:
        return code
    m = bwp()
    cmd = [sys.executable, os.path.abspath(__file__), "motion-inner", "--cases", a.cases, "--variants", a.variants, "--out", a.out, "--ref-frames", str(a.ref_frames),
           "--warm", str(a.warm), "--dump", str(a.dump)]
    return m.under_lock("gpu", "hist2-motion", 60, False, cmd)


def cmd_cost_inner(a):
    variants, names = variant_arg(a.variants, False)
    jobs = ",".join(job_of(v) for v in a.views.split(","))
    with open(os.path.join(IN, "jobs.json"), encoding="utf-8") as f:
        j = json.load(f)["jobs"][0]
    lines, folder = player("hist2-cost", ["-mode", "scene", "-scene", "pin-cost", "-in", IN, "-jobs", jobs, "-variants", variants, "-rounds", str(a.rounds),
                                          "-samples", str(a.samples)], (j["width"], j["height"]), 3000, True)
    if lines is None:
        print("the player did not finish")
        return 1
    text = cost_table(lines, names, variants, a.views)
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, "cost-%s.txt" % time.strftime("%Y%m%d-%H%M%S")), "w", encoding="utf-8") as f:
        f.write("variants: %s\nviews: %s\n\n%s\n\nplayer lines:\n%s\n" % (variants, a.views, text, "\n".join(lines)))
    return 0


def cost_table(lines, names, variants, views):
    """The table of the player's pincost lines ({pincost.<kind>.<job>.<variant>.r<round>,value,-,info})."""
    table = {}
    for line in lines:
        s = line.strip().strip("{}")
        parts = s.split(",")
        if len(parts) >= 2 and parts[0].startswith("pincost."):
            _, kind, rest = parts[0].split(".", 2)
            job_name, _rnd = rest.rsplit(".", 1)
            job, name = job_name.split(".", 1)
            try:
                table.setdefault((job, name), {}).setdefault(kind, []).append(float(parts[1]))
            except ValueError:
                pass
    view_of = {job_of(v): v for v, _ in VIEWS}
    out = ["view | variant | march ms (median of round medians, min-max) | reconstruction ms | rounds"]
    for (job, name), d in sorted(table.items(), key=lambda kv: (kv[0][0], names.index(kv[0][1]) if kv[0][1] in names else 99)):
        mm, rr = d.get("march", []), d.get("recon", [])
        out.append("%s | %s | %.2f (%.2f-%.2f) | %.2f | %d" % (view_of.get(job, job), name, float(np.median(mm)), min(mm), max(mm), float(np.median(rr)), len(mm)))
    text = "\n".join(out)
    print(text)
    return text


def cmd_cost(a):
    code = build(a.no_build, "hist2-cost")
    if code:
        return code
    m = bwp()
    cmd = [sys.executable, os.path.abspath(__file__), "cost-inner", "--variants", a.variants, "--views", a.views, "--rounds", str(a.rounds), "--samples", str(a.samples), "--out", a.out]
    return m.under_lock("gpu", "hist2-cost", 60, True, cmd)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("prepare")
    for name in ("motion", "motion-inner"):
        q = sub.add_parser(name)
        q.add_argument("--cases", default="fwd150,side150,yaw30,fwdabove300")
        q.add_argument("--variants", default="rot2:,rot3:Recon.Block=3")
        q.add_argument("--out", default=OUT_DEFAULT)
        q.add_argument("--ref-frames", type=int, default=16)
        q.add_argument("--warm", type=int, default=40)
        q.add_argument("--dump", type=int, default=24)
        q.add_argument("--no-build", action="store_true")
    for name in ("cost", "cost-inner"):
        q = sub.add_parser(name)
        q.add_argument("--variants", default="rot2:,rot3:Recon.Block=3,rot4:Recon.Block=4")
        q.add_argument("--views", default="ground,inside,above")
        q.add_argument("--rounds", type=int, default=6)
        q.add_argument("--samples", type=int, default=30)
        q.add_argument("--out", default=OUT_DEFAULT)
        q.add_argument("--no-build", action="store_true")
    a = p.parse_args()
    # A pause file (another agent's quiet-machine block): the outer commands that take a lock do not start.
    if a.cmd in ("motion", "cost") and os.path.exists(os.path.join(MAIN, ".local", "work", "hist2.pause")):
        print("hist2: paused (%s exists); nothing started" % os.path.join(MAIN, ".local", "work", "hist2.pause"))
        return 3
    return {"prepare": cmd_prepare, "motion": cmd_motion, "motion-inner": cmd_motion_inner, "cost": cmd_cost, "cost-inner": cmd_cost_inner}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
