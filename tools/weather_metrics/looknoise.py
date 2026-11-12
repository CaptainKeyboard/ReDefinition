"""The noise decomposition of the early look sheet (stream T): python tools/build_weather_player.py --series T-look-noise [--who <id>].

The scenes of the look sheet (tools/weather_metrics/looksheet.py), each rendered as variants of the one source of noise that is switched,
changed or averaged: the same camera, seed, map and light, the natural view and the aid view. The environment variable LOOKNOISE_SET picks
the variant set (default 'decompose'); LOOKNOISE_SCENES lists the scene ids (default all).

The measure of the noisy look (a number per picture, computed from the natural picture and the aid picture's cloud mask):
  hf       the standard deviation of the picture's high-pass (the picture minus its 5 x 5 box mean) over the mean luminance, inside the
           cloud mask eroded by 3 pixels: the pixel-scale grain, hatching and dots of the interior
  edge     the same measure in the band of 3 pixels about the mask boundary (the cloud edge's grain and fringe)
  streak   the share of the picture's mid-scale (5 x 5 box minus 17 x 17 box) energy that lies in the direction of the stronger gradient
           axis, minus 0.5 (0: isotropic, up to 0.5: all energy of one axis): the smear along one direction
A contact sheet per scene (the variants side by side, cropped) is written to build/WeatherPlayer/T-look-noise-scene/sheet-<scene>.png.
"""
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

from weather_metrics import Metric, Stage, register_series  # noqa: E402
from weather_metrics import looksheet as LS  # noqa: E402
from weather_metrics import layers as L  # noqa: E402
from weather_metrics import noise as N  # noqa: E402

REPO = os.path.dirname(os.path.dirname(HERE))
BUILD = os.path.join(REPO, "build", "weather-series")
INPUT = os.path.join(BUILD, "T-look-noise-in")


def noise_of(job, **kw):
    n = job.get("noise") or dict(disabled=0, noUntile=0, noCurl=0, sphere=0.0, seed=1, sizeLevel=1)
    n.update(kw)
    job["noise"] = n


def per_layer(job, **kw):
    for ly in job["layers"]:
        for k, v in kw.items():
            if callable(v):
                ly[k] = v(ly[k])
            else:
                ly[k] = v


def per_type(job, f):
    for ly in job["layers"]:
        for t in ly["types"]:
            t["erosion"] = f(t["erosion"])


# Each variant: job mutator. 'base' is the production state of the sheet.
SETS = {
    "decompose": [
        ("base", lambda j: None),
        ("acc16", lambda j: j.update(accumulate=16)),
        ("nojit", lambda j: j.update(jitter=0.0)),
        ("nountile", lambda j: noise_of(j, noUntile=1)),
        ("nocurl", lambda j: noise_of(j, noCurl=1)),
        ("noero", lambda j: j.update(costDebug=4)),
        ("nonoise", lambda j: noise_of(j, disabled=1)),
        ("amount x.5", lambda j: per_layer(j, noiseAmount=lambda a: a * 0.5)),
        ("soft x3", lambda j: per_layer(j, softness=lambda a: a * 3.0)),
        ("sigma x.4", lambda j: per_layer(j, sigma=lambda a: a * 0.4)),
    ],
}


def warp(a, c):
    return lambda j: noise_of(j, warpAmplitude=a, warpCell=c)


SETS["round1"] = [
    ("base", lambda j: None),
    ("ref.5", lambda j: j.update(refineBudget=0.5)),
    ("ref.25", lambda j: j.update(refineBudget=0.25)),
    ("ref.5 acc16", lambda j: j.update(refineBudget=0.5, accumulate=16)),
    ("warp 3/14", warp(3.0, 14.0)),
    ("warp 2/8", warp(2.0, 8.0)),
    ("ref.5 edge1.5", lambda j: j.update(refineBudget=0.5, edgePower=1.5)),
    ("ref.5 edge2", lambda j: j.update(refineBudget=0.5, edgePower=2.0)),
    ("ref.5 soft x2", lambda j: (j.update(refineBudget=0.5), per_layer(j, softness=lambda a: a * 2.0))),
    ("ref.5 amount x.7", lambda j: (j.update(refineBudget=0.5), per_layer(j, noiseAmount=lambda a: a * 0.7))),
]


def ref(**kw):
    def f(j):
        j.update(refineBudget=0.5)
        j.update(kw)
    return f


SETS["round2"] = [
    ("ref.5", ref()),
    ("ref.5 fixedcone", ref(fixedCone=1)),
    ("ref.5 nojit", ref(jitter=0.0)),
    ("ref.5 nojit fixedcone", ref(jitter=0.0, fixedCone=1)),
    ("ref.5 warp 1/14", lambda j: (ref()(j), noise_of(j, warpAmplitude=1.0, warpCell=14.0))),
    ("ref.5 warp 1.5/20", lambda j: (ref()(j), noise_of(j, warpAmplitude=1.5, warpCell=20.0))),
    ("ref.5 warp 3/20", lambda j: (ref()(j), noise_of(j, warpAmplitude=3.0, warpCell=20.0))),
    ("ref.5 nountile", lambda j: (ref()(j), noise_of(j, noUntile=1))),
    ("cand soft x1.5 amt x.7", lambda j: (ref()(j), per_layer(j, softness=lambda a: a * 1.5, noiseAmount=lambda a: a * 0.7))),
]


def old_state(j):
    """The sheet as it was before stream T: the grid step's entry, no fine detail octave, the warp 8 / 7, the softness and noise amount of the
    old scene table (the scene table now carries x1.5 and x0.7 of them)."""
    j.update(refineBudget=-1.0, ignDither=1)
    noise_of(j, noFine=1, noLod=1, warpAmplitude=8.0, warpCell=7.0)
    for ly in j["layers"]:
        if ly.get("name") != "cirrus":   # the cirrus layer did not change
            ly["softness"] /= 1.5
            ly["noiseAmount"] /= 0.7


SETS["final"] = [
    ("old", old_state),
    ("new", lambda j: None),
    ("new acc16", lambda j: j.update(accumulate=16)),
    ("new nofine", lambda j: noise_of(j, noFine=1)),
    ("new norefine", lambda j: j.update(refineBudget=-1.0)),
    ("new nojit", lambda j: j.update(jitter=0.0)),
    ("new nountile", lambda j: noise_of(j, noUntile=1)),
    ("new noero", lambda j: j.update(costDebug=4)),
    ("new nonoise", lambda j: noise_of(j, disabled=1)),
    ("new oldwarp", lambda j: noise_of(j, warpAmplitude=8.0, warpCell=7.0)),
]


SETS["refcost"] = [
    ("r.5 f.125", lambda j: j.update(refineBudget=0.5, refineFraction=0.125)),
    ("r1 f.25", lambda j: j.update(refineBudget=1.0, refineFraction=0.25)),
    ("r1.5 f.25", lambda j: j.update(refineBudget=1.5, refineFraction=0.25)),
    ("r1 f.5", lambda j: j.update(refineBudget=1.0, refineFraction=0.5)),
    ("r2 f.5", lambda j: j.update(refineBudget=2.0, refineFraction=0.5)),
    ("norefine", lambda j: j.update(refineBudget=-1.0)),
]

SETS["dither"] = [
    ("new", lambda j: None),
    ("new ign", lambda j: j.update(ignDither=1)),
    ("new acc16", lambda j: j.update(accumulate=16)),
    ("new nojit", lambda j: j.update(jitter=0.0)),
]


def set_name():
    return os.environ.get("LOOKNOISE_SET", "decompose")


def scene_ids():
    ids = os.environ.get("LOOKNOISE_SCENES", "")
    return [s for s in ids.split(",") if s] or [s["id"] for s in LS.scene_table()]


def variants():
    return SETS[set_name()]


def vid(variant):
    return variant.replace(" ", "_").replace(".", "p").replace("/", "-")


def write_inputs():
    os.makedirs(INPUT, exist_ok=True)
    out = []
    keep = set(scene_ids())
    for base in LS.jobs():
        sid = base["id"][3:]
        if sid not in keep:
            continue
        for name, mutate in variants():
            j = json.loads(json.dumps(base))
            j["id"] = "LN-%s-%s" % (sid, vid(name))
            noise_of(j)   # every job names its switches: the renderer keeps the last job's switches otherwise
            mutate(j)
            out.append(j)
    with open(os.path.join(INPUT, "jobs.json"), "w", encoding="utf-8") as f:
        json.dump({"jobs": out}, f)
    # the maps of the sheet were written by LS.jobs() into its own input folder; the scene reads them from -in
    src = LS.INPUT
    for fn in os.listdir(src):
        if fn.endswith(".r8"):
            dst = os.path.join(INPUT, fn)
            if not os.path.isfile(dst) or os.path.getmtime(dst) < os.path.getmtime(os.path.join(src, fn)):
                with open(os.path.join(src, fn), "rb") as a, open(dst, "wb") as b:
                    b.write(a.read())


def box(a, k):
    pad = k // 2
    p = np.pad(a, pad, mode="edge")
    c = np.cumsum(np.cumsum(p, axis=0), axis=1)
    c = np.pad(c, ((1, 0), (1, 0)))
    h, w = a.shape
    return (c[k:k + h, k:k + w] - c[:h, k:k + w] - c[k:k + h, :w] + c[:h, :w]) / (k * k)


def erode(mask, r):
    m = np.pad(mask, r, mode="edge")
    for _ in range(r):
        m = m & np.roll(m, 1, 0) & np.roll(m, -1, 0) & np.roll(m, 1, 1) & np.roll(m, -1, 1)
    return m[r:-r, r:-r]


def measure(natural, aid):
    """The numbers of a picture pair (see the module header); the pictures are PIL images."""
    nat = np.asarray(natural.convert("RGB"), dtype=np.float64) / 255.0
    w = nat.shape[1]
    left = np.asarray(aid.convert("RGB"), dtype=np.float64)[:, :w] / 255.0
    sat = (left.max(axis=2) - left.min(axis=2)) / np.maximum(left.max(axis=2), 1e-3)
    mask = sat > 0.55
    lum = 0.2126 * nat[..., 0] + 0.7152 * nat[..., 1] + 0.0722 * nat[..., 2]
    hp = lum - box(lum, 5)
    inner = erode(mask, 3)
    ring = mask & ~erode(mask, 3)
    mean = max(float(lum[inner].mean()), 1e-3) if inner.any() else 1.0
    hf = float(hp[inner].std() / mean) if inner.sum() > 100 else float("nan")
    edge = float(hp[ring].std() / max(float(lum[ring].mean()), 1e-3)) if ring.sum() > 100 else float("nan")
    mid = box(lum, 5) - box(lum, 17)
    gy, gx = np.gradient(mid)
    ex = float((gx[inner] ** 2).sum()) if inner.any() else 0.0
    ey = float((gy[inner] ** 2).sum()) if inner.any() else 0.0
    streak = max(ex, ey) / max(ex + ey, 1e-9) - 0.5 if inner.any() else float("nan")
    cover = float(mask.mean())
    return dict(hf=hf, edge=edge, streak=streak, cover=cover, meanlum=mean, mid=float(mid[inner].std() / mean) if inner.any() else float("nan"),
                pbr=spectrum_peak(lum, inner), band=banding(lum, inner))


WINDOW = 96
PBR_LIMIT = 12.0


def spectrum_peak(lum, inner):
    """The peak-to-background ratio of the 2D power spectrum of the cloud interior: the 96 x 96 window with the highest share of interior
    pixels (at least 98 percent), the mean removed, a Hann window, the power spectrum; the ratio of each bin (radius from 3 bins up) to the mean
    of its 7 x 7 neighbourhood without the bin itself; the value is the largest ratio. A smooth falling spectrum gives a ratio of about the
    logarithm of the bin count (random phases: the exponential distribution of a bin's power), a regular pattern (a dither matrix, a banding
    period) gives discrete peaks far above it. NaN where no window of the picture lies inside a cloud."""
    h, w = lum.shape
    best, where = 0.0, None
    for y in range(0, h - WINDOW + 1, 16):
        for x in range(0, w - WINDOW + 1, 16):
            share = float(inner[y:y + WINDOW, x:x + WINDOW].mean())
            if share > best:
                best, where = share, (y, x)
    if where is None or best < 0.98:
        return float("nan")
    y, x = where
    a = lum[y:y + WINDOW, x:x + WINDOW].astype(np.float64)
    a = a - a.mean()
    win = np.outer(np.hanning(WINDOW), np.hanning(WINDOW))
    p = np.abs(np.fft.fftshift(np.fft.fft2(a * win))) ** 2
    bg = (box(p, 7) * 49.0 - p) / 48.0
    c = WINDOW // 2
    yy, xx = np.mgrid[0:WINDOW, 0:WINDOW]
    r = np.hypot(yy - c, xx - c)
    ok = (r >= 3) & (r < c - 4) & (bg > 0)
    if not ok.any():
        return float("nan")
    return float((p[ok] / bg[ok]).max())


def banding(lum, inner):
    """The contour alignment of the fine structure: the mean over the interior of cos^2 of the angle between the gradient of the high-pass
    (the fine lines) and the gradient of the low-pass (the shape), weighted by the fine gradient's length, minus 0.5. Random grain gives 0, lines
    that follow the shape (step banding, contour lines) give a positive value up to 0.5."""
    if inner.sum() < 100:
        return float("nan")
    low = box(lum, 17)
    hp = box(lum, 3) - box(lum, 9)
    gly, glx = np.gradient(low)
    ghy, ghx = np.gradient(hp)
    dot = glx * ghx + gly * ghy
    nl = np.hypot(glx, gly)
    nh = np.hypot(ghx, ghy)
    cos2 = dot * dot / np.maximum(nl * nl * nh * nh, 1e-18)
    wgt = nh * (nl > 1e-4)
    m = inner & (wgt > 0)
    if not m.any():
        return float("nan")
    return float((cos2[m] * wgt[m]).sum() / wgt[m].sum() - 0.5)


def collect(ctx):
    from PIL import Image, ImageDraw
    folder = ctx.folder("scene")
    out = []
    names = [v[0] for v in variants()]
    rows = {}
    for sid in scene_ids():
        sheet_cells = []
        for name in names:
            base = os.path.join(folder, "LN-%s-%s" % (sid, vid(name)))
            ok = os.path.isfile(base + ".png") and os.path.isfile(base + "-aid.png")
            out.append(Metric.flag("noise.%s.%s.pictures" % (sid, vid(name)), ok))
            if not ok:
                continue
            nat, aid = Image.open(base + ".png"), Image.open(base + "-aid.png")
            m = measure(nat, aid)
            for k in ("hf", "edge", "streak", "pbr", "band"):
                out.append(Metric.info("noise.%s.%s.%s" % (sid, vid(name), k), m[k]))
            # The row of the pattern: the production state's single frame must not show a discrete peak of the power spectrum. The limit is the
            # expectation of the largest of about 7000 independent bins (about 9, an exponential distribution of the power) plus a margin; the
            # 16-frame means of the sheet, which carry no dither, measured 5.2 to 9.1 over the scenes (the dither run of 2026-11-12).
            if name in ("new", "base") and not math.isnan(m["pbr"]):
                out.append(Metric.at_most("noise.%s.%s.pbr_row" % (sid, vid(name)), m["pbr"], PBR_LIMIT).based("new"))
            rows.setdefault(sid, {})[name] = m
            sheet_cells.append((name, nat))
        if sheet_cells:
            w, h = sheet_cells[0][1].size
            cols = 3
            rws = (len(sheet_cells) + cols - 1) // cols
            sheet = Image.new("RGB", (cols * w, rws * (h + 18)), (24, 24, 24))
            d = ImageDraw.Draw(sheet)
            for i, (name, im) in enumerate(sheet_cells):
                x, y = (i % cols) * w, (i // cols) * (h + 18)
                d.text((x + 4, y + 2), "%s %s  hf %.3f edge %.3f" % (sid, name, rows[sid][name]["hf"], rows[sid][name]["edge"]), fill=(230, 230, 230))
                sheet.paste(im, (x, y + 18))
            sheet.save(os.path.join(folder, "sheet-%s-%s.png" % (set_name(), sid)))
    with open(os.path.join(folder, "measure-%s.json" % set_name()), "w", encoding="utf-8") as f:
        json.dump(rows, f, indent=1)
    return out


def stages():
    sig = "%s|%s|%d|%d" % (set_name(), ",".join(scene_ids()), os.path.getmtime(os.path.abspath(__file__)), os.path.getmtime(os.path.join(HERE, "looksheet.py")))
    sigfile = os.path.join(INPUT, "sig.txt")
    if not (os.path.isfile(sigfile) and open(sigfile).read() == sig and os.path.isfile(os.path.join(INPUT, "jobs.json"))):
        write_inputs()
        with open(sigfile, "w") as f:
            f.write(sig)
    return [Stage("scene", ["-mode", "scene", "-scene", "layers", "-in", INPUT], size=(1280, 720), proxy=False)]


register_series("T-look-noise", stages(), collect, exclusive=False, who="LOOKNOISE")
