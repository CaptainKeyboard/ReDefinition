#!/usr/bin/env python3
"""Cuts single footsteps out of a long walking recording, for the snow-step sounds (package B15).

  python tools/audio/slice_steps.py [--source ID] [--out DIR] [--count 12] [--classes 3]
  python tools/audio/slice_steps.py --selftest

Needs numpy, scipy and soundfile. Reads tools/audio/sources.json (the facts of each original: author, page, archived page,
licence line, date read) and the original file named there (tools/audio/originals/, not committed). Per original it
  1. high-passes a copy at 200 Hz and follows its envelope (RMS over 10 ms),
  2. finds the onsets: the envelope crosses a threshold 12 dB over the noise floor (its 20th percentile) after a quiet gap,
  3. keeps the onsets whose fixed window (PRE_S before, POST_S after) is free of the next onset and has died down at its end
     (the last TAIL_S are TAIL_MARGIN_DB under the window's level),
  4. sorts them by the loudness of that window into CLASSES intensity classes (soft, medium, hard),
  5. takes the same number from each class, spread evenly over the class,
  6. cuts the window, fades both ends over 5 ms, sets the class loudness (RMS in dBFS: -28, -24, -20) with a -1 dBFS peak cap,
  7. writes mono OGG (Vorbis, quality 0.4) at OUT_RATE as steps_snow_NN.ogg, and SOUNDS.json with one line per file.
The same input gives the same files.
"""
import argparse
import hashlib
import json
import math
import os
import sys
import tempfile

import numpy as np
import soundfile as sf
from scipy import signal

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_OUT = os.path.join(REPO, "GameData", "ReDefinition", "Atmospheres", "Sounds")

PRE_S = 0.020
POST_S = 0.600
FADE_S = 0.005
OUT_RATE = 48000
OGG_QUALITY = 0.4
CLASS_NAMES = ["soft", "medium", "hard"]
CLASS_RMS_DBFS = [-28.0, -24.0, -20.0]
PEAK_CAP = 10 ** (-1.0 / 20.0)
ONSET_OVER_FLOOR_DB = 12.0
MIN_GAP_S = 0.12
TAIL_S = 0.100
TAIL_MARGIN_DB = 10.0
HIGHPASS_HZ = 200.0
ENVELOPE_S = 0.010


def to_mono(x):
    return x.mean(axis=1) if x.ndim > 1 else x


def resample(x, sr_from, sr_to):
    if sr_from == sr_to:
        return x
    g = math.gcd(sr_from, sr_to)
    return signal.resample_poly(x, sr_to // g, sr_from // g)


def envelope(x, sr):
    sos = signal.butter(2, HIGHPASS_HZ, "hp", fs=sr, output="sos")
    h = signal.sosfilt(sos, x)
    win = max(1, int(sr * ENVELOPE_S))
    return np.sqrt(np.convolve(h * h, np.ones(win) / win, mode="same"))


def find_onsets(x, sr):
    """Sample indices where the envelope rises over the threshold after at least MIN_GAP_S below it."""
    env = envelope(x, sr)
    floor = float(np.percentile(env, 20)) + 1e-9
    threshold = floor * 10 ** (ONSET_OVER_FLOOR_DB / 20.0)
    above = env > threshold
    onsets = []
    gap = int(MIN_GAP_S * sr)
    quiet_for = gap
    for i in range(len(above)):
        if above[i]:
            if quiet_for >= gap:
                onsets.append(i)
            quiet_for = 0
        else:
            quiet_for += 1
    return onsets


def window_of(onset, sr, n):
    start = onset - int(PRE_S * sr)
    end = onset + int(POST_S * sr)
    return start, end


def rms_dbfs(x):
    return 20 * math.log10(math.sqrt(float(np.mean(x * x))) + 1e-12)


def cut_steps(x, sr):
    """Returns [(onset, start, end, rms_dbfs_of_window)] for the onsets with a free window."""
    onsets = find_onsets(x, sr)
    out = []
    for k, o in enumerate(onsets):
        start, end = window_of(o, sr, len(x))
        if start < 0 or end > len(x):
            continue
        if k + 1 < len(onsets) and onsets[k + 1] < end:
            continue
        level = rms_dbfs(x[start:end])
        # A window that is still loud at its end holds the start of another sound: the step is cut off there.
        if rms_dbfs(x[end - int(TAIL_S * sr):end]) > level - TAIL_MARGIN_DB:
            continue
        out.append((o, start, end, level))
    return out


def pick(steps, per_class, classes):
    """Sorted by loudness, split in `classes` equal parts, per_class taken evenly from each part."""
    order = sorted(steps, key=lambda s: (s[3], s[0]))
    size = len(order) / float(classes)
    chosen = []
    for c in range(classes):
        part = order[int(round(c * size)):int(round((c + 1) * size))]
        if len(part) < per_class:
            raise SystemExit("class %d has %d steps, %d wanted" % (c, len(part), per_class))
        idx = [int(round((i + 0.5) * len(part) / per_class - 0.5)) for i in range(per_class)]
        chosen.extend((c, part[i]) for i in idx)
    return chosen


def shape(x, cls):
    """Fades both ends, sets the class loudness, caps the peak."""
    y = x.astype(np.float64).copy()
    y -= np.mean(y)
    n = len(y)
    f = min(n // 2, int(FADE_S * OUT_RATE))
    ramp = np.linspace(0.0, 1.0, f, endpoint=False)
    y[:f] *= ramp
    y[n - f:] *= ramp[::-1]
    current = rms_dbfs(y)
    y *= 10 ** ((CLASS_RMS_DBFS[cls] - current) / 20.0)
    peak = float(np.max(np.abs(y)))
    if peak > PEAK_CAP:
        y *= PEAK_CAP / peak
    return y


def run(source_id, out_dir, count, classes, sources_path, originals_root):
    with open(sources_path, encoding="utf8") as fh:
        catalogue = json.load(fh)["sources"]
    src = catalogue[source_id]
    path = os.path.join(originals_root, src["original"])
    data, sr = sf.read(path)
    x = resample(to_mono(data), sr, OUT_RATE)
    sha = hashlib.sha256(open(path, "rb").read()).hexdigest()
    steps = cut_steps(x, OUT_RATE)
    if len(steps) < count:
        raise SystemExit("%s: %d usable steps, %d wanted" % (source_id, len(steps), count))
    per_class = count // classes
    chosen = pick(steps, per_class, classes)
    os.makedirs(out_dir, exist_ok=True)
    files = []
    for n, (cls, (onset, start, end, level)) in enumerate(chosen, start=1):
        name = "steps_snow_%02d.ogg" % n
        y = shape(x[start:end], cls)
        target = os.path.join(out_dir, name)
        with sf.SoundFile(target, "w", samplerate=OUT_RATE, channels=1, format="OGG", subtype="VORBIS", compression_level=OGG_QUALITY) as fh:
            fh.write(y.astype(np.float32))
        info = sf.info(target)
        files.append({
            "file": name,
            "source": source_id,
            "derivedFrom": src["title"] + " (" + os.path.basename(src["original"]) + ", sha256 " + sha + ")",
            "author": src["author"],
            "sourceUrl": src["sourceUrl"],
            "archivedUrl": src["archivedUrl"],
            "licence": src["licence"],
            "licenceLine": src["licenceLine"],
            "dateRead": src["dateRead"],
            "class": CLASS_NAMES[cls] if classes == 3 else "class%d" % cls,
            "processing": "mono; resampled to %d Hz; window %.0f ms before to %.0f ms after the onset at %.3f s; %.0f ms fades; RMS %.0f dBFS, peak cap -1 dBFS; Vorbis quality %.1f (tools/audio/slice_steps.py)" % (
                OUT_RATE, PRE_S * 1000, POST_S * 1000, onset / float(OUT_RATE), FADE_S * 1000, CLASS_RMS_DBFS[cls], OGG_QUALITY),
            "sampleRate": info.samplerate,
            "frames": int(info.frames),
            "decodedBytes": int(info.frames) * 2,
        })
    manifest = {
        "schema": 1,
        "note": "One line per shipped file. tools/audio/check_manifest.py fails a file without a complete line.",
        "files": files,
    }
    with open(os.path.join(out_dir, "SOUNDS.json"), "w", encoding="utf8") as fh:
        json.dump(manifest, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    return files


def selftest():
    """Synthetic walk: 20 decaying noise bursts, one second apart, with rising loudness."""
    rng = np.random.RandomState(3)
    sr = OUT_RATE
    x = rng.randn(sr * 24) * 1e-4
    starts = [int((1.0 + k) * sr) for k in range(20)]
    for k, s in enumerate(starts):
        n = int(0.4 * sr)
        burst = rng.randn(n) * np.exp(-np.arange(n) / (0.08 * sr)) * (0.05 + 0.02 * k)
        x[s:s + n] += burst
    steps = cut_steps(x, sr)
    assert len(steps) == 20, len(steps)
    for (o, _, _, _), s in zip(steps, starts):
        assert abs(o - s) < 0.02 * sr, (o, s)
    chosen = pick(steps, 4, 3)
    assert len(chosen) == 12
    levels = [c[1][3] for c in chosen]
    assert levels[0] < levels[4] < levels[8]
    y = shape(x[steps[0][1]:steps[0][2]], 2)
    assert np.max(np.abs(y)) <= PEAK_CAP + 1e-9
    assert abs(y[0]) < 1e-6 and abs(y[-1]) < 1e-6
    print("slice_steps selftest ok")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", default="bsb-2890")
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--count", type=int, default=12)
    ap.add_argument("--classes", type=int, default=3)
    ap.add_argument("--sources", default=os.path.join(HERE, "sources.json"))
    ap.add_argument("--originals", default=HERE)
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        selftest()
        return 0
    files = run(a.source, a.out, a.count, a.classes, a.sources, a.originals)
    total = sum(os.path.getsize(os.path.join(a.out, f["file"])) for f in files)
    decoded = sum(f["decodedBytes"] for f in files)
    print("%d files, %d bytes on disk, %d bytes decoded" % (len(files), total, decoded))
    return 0


if __name__ == "__main__":
    sys.exit(main())
