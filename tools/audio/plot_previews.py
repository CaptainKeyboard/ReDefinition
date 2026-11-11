#!/usr/bin/env python3
"""Draws a spectrogram and an RMS envelope for every WAV preview in a folder, and prints the numbers (package B15).

  python tools/audio/plot_previews.py <folder with the WAV files from RenderWeatherAudio>

Writes <name>.png next to each <name>.wav: the spectrogram in dB (0 to 12 kHz) above the 100 ms RMS envelope in dBFS and the
peak. Needs numpy, scipy and matplotlib.
"""
import glob
import os
import sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from scipy.io import wavfile


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else "."
    files = sorted(glob.glob(os.path.join(folder, "*.wav")))
    if not files:
        print("no WAV files in", folder)
        return 1
    print("%-24s %9s %9s %12s" % ("scenario", "rms dBFS", "peak dBFS", "centroid Hz"))
    for path in files:
        rate, data = wavfile.read(path)
        x = data.astype(np.float64) / 32768.0
        mono = x.mean(axis=1) if x.ndim > 1 else x
        win = rate // 10
        n = len(mono) // win
        env = np.array([math_rms(mono[i * win:(i + 1) * win]) for i in range(n)])
        env_db = 20 * np.log10(env + 1e-9)
        peak = np.abs(x).max()
        spec = np.abs(np.fft.rfft(mono * np.hanning(len(mono))))
        freqs = np.fft.rfftfreq(len(mono), 1.0 / rate)
        centroid = float((spec * freqs).sum() / spec.sum())
        print("%-24s %9.1f %9.1f %12.0f" % (os.path.basename(path)[:-4], 20 * np.log10(math_rms(mono) + 1e-9), 20 * np.log10(peak + 1e-9), centroid))

        fig, (a1, a2) = plt.subplots(2, 1, figsize=(10, 6), sharex=True, gridspec_kw={"height_ratios": [3, 1]})
        a1.specgram(mono, NFFT=2048, Fs=rate, noverlap=1024, cmap="magma", vmin=-130, vmax=-30)
        a1.set_ylim(0, 12000)
        a1.set_ylabel("Hz")
        a1.set_title(os.path.basename(path)[:-4])
        a2.plot(np.arange(n) * 0.1, env_db, lw=1)
        a2.set_ylim(-80, 0)
        a2.set_ylabel("RMS dBFS (100 ms)")
        a2.set_xlabel("seconds")
        a2.grid(alpha=0.3)
        fig.tight_layout()
        fig.savefig(path[:-4] + ".png", dpi=90)
        plt.close(fig)
    return 0


def math_rms(v):
    return float(np.sqrt(np.mean(v * v)))


if __name__ == "__main__":
    sys.exit(main())
