"""E40 L2 E1: the static, screen-fixed pattern the step offset leaves in the history (player series e40-wv-temporal, e40-wv-cost).

    python tools/build_weather_player.py --series e40-wv-temporal
    python tools/build_weather_player.py --series e40-wv-cost
    python tools/weather_metrics/e40wv.py --sheet NAME VIEW MEDIUM 'label=case' ...    side-by-side crops (2 x, nearest) of one cloud body
    python tools/weather_metrics/e40wv.py --game                                       the measure on the game's frames (K-T2, three frames per variant)
    python tools/weather_metrics/e40wv.py --selftest

A case is a SoftTable case (tests/WeatherPlayer/Assets/Runner/SoftDensityScene.cs): the product's 48-point march (seq1) and modifiers joined by '+' (r33 the march target's
size, acc10 the history weight 0.1, clip1000 no clip of the history, jit the sub-pixel jitter (ReconSettings.Jitter), ds1 the white hash, ds2 the gradient noise).
The scene `weave` writes, per case, the last three frames and the mean of the last eight of a still camera after 120 frames, and the converged reference of the pose.

THE MEASURE OF A STATIC PATTERN. The residual of a frame is its display luma minus the converged picture's (a march of 256 steps without an offset). A pattern that is the same in
every frame survives the mean of eight frames; noise that changes every frame does not. For a mask of cloud pixels (the converged picture's alpha) and the residual r, with
hp(x) = x - gaussian(x, 3 px) (the picture's own slow shape and the 48-step bias leave):
  rms1   rms of hp(r of the last frame)             what one frame shows
  rms8   rms of hp(r of the mean of the last 8)     what is left when the changing part is averaged away
  stat   rms8 / rms1                                the share of one frame's pattern that is static: 1 = the same in every frame, 0.35 = white noise that changes every frame
  peak   the largest power of the 2D spectrum of hp(r of the mean) over a cloud-body crop (Hann window, periods under 32 px only) over the median power: a weave is a narrow peak
         (a white-noise field gives about 15 to 20, measured by --selftest), peak_period / peak_angle say where.
"""
import argparse
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import e40gc as gc  # noqa: E402
from weather_metrics import e40gn as gn  # noqa: E402
from weather_metrics import e40gt as gt  # noqa: E402

BUILD = gc.BUILD
INPUT = gc.INPUT
RESULTS = os.path.join(BUILD, "e40wv-results.json")
COST_JSON = os.path.join(BUILD, "e40wv-cost-results.json")
PREFIX = "e40-wv"
B48 = "seq1"
SIGMA_HP = 3.0
CROP = (512, 256)


def on(base, mods):
    return [base + "+" + m for m in mods]


# What the history and the target size do to the pattern, one thing at a time (the March of the product is seq1: 48 grid points, the gradient-noise offset).
CAUSE_MODS = ["", "ds1", "acc00", "acc10", "acc05", "clip1000", "clip20", "jit"]       # dp2..dp5 and sh10, sh100 (layers.ditherPhase, layers.ditherShift) are gone: no case separated from the default
CAUSE = [B48 + "+r%d%s" % (r, "+" + m if m else "") for r in (50, 33) for m in CAUSE_MODS]
CAUSE_STAGES = [("weave-cause", "ground,above", "pr50", CAUSE)]
# Filled in by the fix: the new patterns at both targets, against today.
FIX = []
FIX_STAGES = [("weave-fix", "ground,above", "pr50", FIX)] if FIX else []
# The game's camera is never still: the same cases while the camera turns 0.15 degrees a frame (9 degrees a second), 60 frames, the last frame at the reference's pose.
MOVE_MODS = ["", "ds1", "acc10", "clip1000", "jit"]
MOVE = [B48 + "+r%d%s" % (r, "+" + m if m else "") for r in (50, 33) for m in MOVE_MODS]
MOVE_STAGES = [("weave-move", "ground,above", "pr50", MOVE)]
STAGES = CAUSE_STAGES + FIX_STAGES


def weave_stage(label, views, media, cases, extra=()):
    return Stage(label, ["-mode", "scene", "-scene", "weave", "-in", INPUT, "-views", views, "-media", media, "-marches", ",".join(cases)] + list(extra), size=(1708, 960), proxy=False, timeout=3000)


def load(folder, name):
    """A frame the scene wrote (float32, bottom row first) as (h, w, 4), top row first."""
    return np.load(os.path.join(folder, name + ".npy")).astype(np.float64)[::-1]


def gaussian_hp(x, sigma=SIGMA_HP):
    from scipy.ndimage import gaussian_filter
    return x - gaussian_filter(x, sigma, mode="nearest")


def spectrum_peak(img, min_period=32.0):
    """(peak over median power, period in px, angle in degrees) of the 2D spectrum of a crop (Hann window) over the frequencies with a period under min_period."""
    h, w = img.shape
    win = np.outer(np.hanning(h), np.hanning(w))
    f = np.fft.fftshift(np.fft.fft2(img * win))
    p = np.abs(f) ** 2
    fy = np.fft.fftshift(np.fft.fftfreq(h))[:, None]
    fx = np.fft.fftshift(np.fft.fftfreq(w))[None, :]
    radius = np.hypot(fx, fy)
    use = radius > 1.0 / min_period
    if not use.any():
        return float("nan"), float("nan"), float("nan")
    # The picture's own spectrum falls with the frequency: every bin is divided by the median power of its ring of equal radius (48 rings), so that a field without a preferred
    # direction scores about the same everywhere and a narrow peak stands out of its ring.
    edges = np.linspace(1.0 / min_period, 0.5 * math.sqrt(2.0), 49)
    ring = np.clip(np.digitize(radius, edges) - 1, 0, 47)
    med = np.ones(48)
    for k in range(48):
        sel = use & (ring == k)
        if sel.any():
            med[k] = max(float(np.median(p[sel])), 1e-300)
    norm = np.where(use, p / med[ring], 0.0)
    i = np.argmax(norm)
    iy, ix = np.unravel_index(i, p.shape)
    period = 1.0 / max(radius[iy, ix], 1e-9)
    angle = math.degrees(math.atan2(fy[iy, 0], fx[0, ix])) % 180.0
    return float(norm[iy, ix]), float(period), float(angle)


def weave_stats(l_frame, l_mean, l_ref, mask, crop):
    """The measure of the module docstring for one case: the cloud mask, the crop (x, y, w, h)."""
    x, y, w, h = crop
    r1 = gaussian_hp(l_frame - l_ref)
    r8 = gaussian_hp(l_mean - l_ref)
    rms1 = float(np.sqrt((r1[mask] ** 2).mean()))
    rms8 = float(np.sqrt((r8[mask] ** 2).mean()))
    peak, period, angle = spectrum_peak(r8[y:y + h, x:x + w])
    return dict(rms1=rms1, rms8=rms8, stat=rms8 / rms1 if rms1 > 0 else float("nan"), peak=peak, peak_period=period, peak_angle=angle)


def collect_temporal(ctx, stages=None, json_path=None):
    out_dir = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    metrics, lines, results = [], [], {}
    lines.append("E40-WV static pattern (display luma against the converged picture, high-passed at %.0f px; mask = cloud; crop %dx%d of the densest rim)" % ((SIGMA_HP,) + CROP))
    lines.append("   %-8s %-6s %-26s rms1    rms8    stat   peak  period angle | tstd(3)  err" % ("view", "medium", "case"))
    for label, _, media, cases in stages or STAGES:
        folder = ctx.folder(label)
        for medium in media.split(","):
            for view in gc.VIEWS:
                ref_name = "wv-%s-%s-ref" % (view, medium)
                if not os.path.isfile(os.path.join(folder, ref_name + ".npy")):
                    continue
                alias = gc.VIEWS[view]
                ref = load(folder, ref_name)
                alpha = np.clip(ref[..., 3], 0.0, 1.0)
                mask = gt.inner(gn.masks(alpha)["cloud"])
                crop = gn.pick_crop(alpha, CROP[0], CROP[1])
                l_ref = gn.luma(gn.full_display(ref, alias))
                gn.save_png(os.path.join(out_dir, "%s-%s-%s-converged.png" % (PREFIX, view, medium)), gn.full_display(ref, alias))
                for case in cases:
                    base = "wv-%s-%s-%s" % (view, medium, case)
                    if not os.path.isfile(os.path.join(folder, base + "-mean8.npy")):
                        continue
                    frames = [load(folder, base + "-f%d" % k) for k in range(3)]
                    ls = [gn.luma(gn.full_display(f, alias)) for f in frames]
                    l_mean = gn.luma(gn.full_display(load(folder, base + "-mean8"), alias))
                    st = weave_stats(ls[0], l_mean, l_ref, mask, crop)
                    tstd = float(np.stack(ls).std(axis=0)[mask].mean())
                    d = (ls[0] - l_ref)[mask]
                    err = float(np.sqrt((d * d).mean()))
                    st.update(tstd=tstd, err=err, crop=list(crop))
                    results["%s|%s|%s" % (view, medium, case)] = st
                    for k, v in st.items():
                        if k != "crop":
                            metrics.append(Metric.info("e40wv.%s.%s.%s.%s" % (view, medium, case, k), v))
                    lines.append("   %-8s %-6s %-26s %.4f  %.4f  %.2f  %5.1f %5.1f %5.1f | %.4f  %.4f" % (view, medium, case, st["rms1"], st["rms8"], st["stat"], st["peak"],
                                                                                                       st["peak_period"], st["peak_angle"], tstd, err))
    with open(json_path or RESULTS, "w", encoding="utf-8") as f:
        json.dump(dict(results=results, lines=lines), f, indent=1)
    print("\n".join(lines))
    return metrics


def load_results():
    with open(RESULTS, encoding="utf-8") as f:
        return json.load(f)["results"]


# --------------------------------------------------------------------------------------------------------------------------------
# Pictures
# --------------------------------------------------------------------------------------------------------------------------------
def find_folder(label):
    """The folder of a stage's frames in the newest run (the series runner's output folder)."""
    root = os.path.join(gn.main_repo(), "build", "WeatherPlayer")
    cand = []
    for d in os.listdir(root):
        p = os.path.join(root, d)
        if os.path.isdir(p) and d.endswith(label):
            cand.append(p)
    return max(cand, key=os.path.getmtime) if cand else None


def crop_tile(folder, view, medium, case, crop, kind="mean8"):
    alias = gc.VIEWS[view]
    x, y, w, h = crop
    f = load(folder, "wv-%s-%s-%s-%s" % (view, medium, case, kind))
    return gn.enlarge(gn.full_display(f, alias)[y:y + h, x:x + w], 2)


def weave_map(folder, view, medium, case, crop, gain=10.0):
    """The high-passed residual of the mean of eight frames against the converged picture, grey 0.5 = zero (what stays when the changing part is averaged away)."""
    alias = gc.VIEWS[view]
    x, y, w, h = crop
    ref = load(folder, "wv-%s-%s-ref" % (view, medium))
    l_ref = gn.luma(gn.full_display(ref, alias))
    l_mean = gn.luma(gn.full_display(load(folder, "wv-%s-%s-%s-mean8" % (view, medium, case)), alias))
    r = gaussian_hp(l_mean - l_ref)[y:y + h, x:x + w]
    return gn.enlarge(np.repeat(np.clip(0.5 + r * gain, 0.0, 1.0)[..., None], 3, axis=-1), 2)


def sheet(name, label, view, medium, labelled, crop_size=(360, 200), maps=True):
    """A sheet of crops (2 x, nearest) of one cloud body: a column per (text, case): the last frame, the mean of eight frames, the static residual (x10)."""
    from PIL import Image, ImageDraw
    folder = find_folder(label)
    out_dir = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "E40")
    os.makedirs(out_dir, exist_ok=True)
    ref = load(folder, "wv-%s-%s-ref" % (view, medium))
    crop = gn.pick_crop(np.clip(ref[..., 3], 0.0, 1.0), crop_size[0], crop_size[1])
    cols = []
    for text, case in labelled:
        rows = [crop_tile(folder, view, medium, case, crop, "f0"), crop_tile(folder, view, medium, case, crop, "mean8")]
        if maps:
            rows.append(weave_map(folder, view, medium, case, crop))
        cols.append((text, np.concatenate(rows, axis=0)))
    tw = cols[0][1].shape[1]
    th = cols[0][1].shape[0]
    img = Image.new("RGB", (tw * len(cols), th + 20), (0, 0, 0))
    draw = ImageDraw.Draw(img)
    for i, (text, arr) in enumerate(cols):
        draw.text((i * tw + 6, 4), text, fill=(255, 255, 255))
        img.paste(Image.fromarray(np.clip(np.rint(arr * 255.0), 0, 255).astype(np.uint8), "RGB"), (i * tw, 20))
    path = os.path.join(out_dir, "%s-%s.png" % (PREFIX, name))
    img.save(path)
    print(path)


def strip(name, label, view, medium, labelled, crop_size=(360, 200)):
    """The three-frame strips: for each (text, case) a row of the last three frames (crops, 2 x, nearest)."""
    from PIL import Image, ImageDraw
    folder = find_folder(label)
    out_dir = os.path.join(gn.main_repo(), ".local", "work", "series-pictures", "E40")
    ref = load(folder, "wv-%s-%s-ref" % (view, medium))
    crop = gn.pick_crop(np.clip(ref[..., 3], 0.0, 1.0), crop_size[0], crop_size[1])
    rows = []
    for text, case in labelled:
        rows.append((text, np.concatenate([crop_tile(folder, view, medium, case, crop, "f%d" % k) for k in range(3)], axis=1)))
    th, tw = rows[0][1].shape[:2]
    img = Image.new("RGB", (tw, (th + 20) * len(rows)), (0, 0, 0))
    draw = ImageDraw.Draw(img)
    for i, (text, arr) in enumerate(rows):
        draw.text((6, i * (th + 20) + 4), text, fill=(255, 255, 255))
        img.paste(Image.fromarray(np.clip(np.rint(arr * 255.0), 0, 255).astype(np.uint8), "RGB"), (0, i * (th + 20) + 20))
    path = os.path.join(out_dir, "%s-%s.png" % (PREFIX, name))
    img.save(path)
    print(path)


# --------------------------------------------------------------------------------------------------------------------------------
# The game's frames (a sanity check: three frames, no converged picture)
# --------------------------------------------------------------------------------------------------------------------------------
def _common_root():
    """The main repository (the git common directory's parent): the game's frames of K-T2 live there, not in a worktree."""
    import subprocess
    try:
        out = subprocess.run(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], cwd=gn.main_repo(), capture_output=True, text=True, check=True).stdout.strip()
        return os.path.dirname(os.path.abspath(out))
    except (OSError, subprocess.CalledProcessError):
        return gn.main_repo()


GAME = os.path.join(_common_root(), ".local", "work", "series-pictures", "K-T2")
GAME_VARIANTS = ["soft", "softhash", "hard", "softr33", "softcut16", "soft100s24"]
GAME_CROP = (1100, 250)   # x, y of a 512 x 256 crop inside the dark cloud body of the fair ground view (upper right of the picture)


def game_stats():
    """For each variant of the game start soft59 (fair ground) the same numbers without a reference: hp rms and spectrum peak of the mean of the three frames over a crop."""
    from PIL import Image
    out = []
    x, y = GAME_CROP
    for v in GAME_VARIANTS:
        paths = [os.path.join(GAME, "t09-soft59-ground_fair-%s_%s-1707x960-natural.png" % (v, k)) for k in "abc"]
        if not all(os.path.isfile(p) for p in paths):
            continue
        ims = [gn.luma(np.asarray(Image.open(p).convert("RGB"), dtype=np.float64) / 255.0) for p in paths]
        box = (slice(y, y + CROP[1]), slice(x, x + CROP[0]))
        one = gaussian_hp(ims[0])[box]
        mean = gaussian_hp(np.mean(ims, axis=0))[box]
        out.append((v, float(np.sqrt((one ** 2).mean())), float(np.sqrt((mean ** 2).mean())), spectrum_peak(one), spectrum_peak(mean)))
    return out


def selftest():
    rng = np.random.default_rng(1)
    peaks = []
    for _ in range(5):
        noise = gaussian_hp(rng.normal(size=(CROP[1], CROP[0])))
        peaks.append(spectrum_peak(noise)[0])
    yy, xx = np.mgrid[0:CROP[1], 0:CROP[0]]
    weave = gaussian_hp(0.5 * np.sin(2 * math.pi * (xx * 0.11 + yy * 0.07)) + rng.normal(size=(CROP[1], CROP[0])) * 0.5)
    wp, period, angle = spectrum_peak(weave)
    print("white noise peaks %s ; a sine in noise: %.1f, period %.1f px, angle %.0f deg" % (" ".join("%.1f" % p for p in peaks), wp, period, angle))
    assert max(peaks) < 40.0 and wp > 3.0 * max(peaks), "the peak measure does not separate a weave from noise"
    for cases in (CAUSE,):
        assert len(set(cases)) == len(cases)
    print("selftest passed")


register_series("e40-wv-temporal", [weave_stage(*s) for s in STAGES], collect_temporal, exclusive=False, who="e40-wv")
register_series("e40-wv-move", [weave_stage(*s, extra=("-yaw", "0.15", "-frames", "60")) for s in MOVE_STAGES], lambda ctx: collect_temporal(ctx, MOVE_STAGES, os.path.join(BUILD, "e40wv-move-results.json")),
                exclusive=False, who="e40-wv")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--game", action="store_true")
    parser.add_argument("--table", action="store_true")
    parser.add_argument("--sheet", nargs="+", metavar="NAME LABEL VIEW MEDIUM [TEXT=CASE ...]")
    parser.add_argument("--strip", nargs="+", metavar="NAME LABEL VIEW MEDIUM [TEXT=CASE ...]")
    a = parser.parse_args()
    if a.selftest:
        selftest()
    elif a.game:
        print("game frames, fair ground, crop %s %dx%d: hp rms of one frame | of the mean of 3 ; spectrum peak (period px, angle) of one frame | of the mean" % (GAME_CROP, CROP[0], CROP[1]))
        for v, r1, r3, p1, p3 in game_stats():
            print("%-11s %.4f | %.4f ; peak %5.1f (%.1f px, %.0f deg) | %5.1f (%.1f px, %.0f deg)" % ((v, r1, r3) + p1 + p3))
    elif a.table:
        r = load_results()
        for k in sorted(r):
            v = r[k]
            print("%-40s rms1 %.4f rms8 %.4f stat %.2f peak %.1f period %.1f angle %.0f tstd %.4f err %.4f" % (k, v["rms1"], v["rms8"], v["stat"], v["peak"], v["peak_period"], v["peak_angle"], v["tstd"], v["err"]))
    elif a.sheet:
        sheet(a.sheet[0], a.sheet[1], a.sheet[2], a.sheet[3], [tuple(s.split("=", 1)) for s in a.sheet[4:]])
    elif a.strip:
        strip(a.strip[0], a.strip[1], a.strip[2], a.strip[3], [tuple(s.split("=", 1)) for s in a.strip[4:]])
