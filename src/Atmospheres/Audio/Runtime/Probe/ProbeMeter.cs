using System;

namespace ReDefinition.Atmospheres.Audio
{
    // What a stretch of windows says: the numbers of one scenario step, measured on the final mix at the listener.
    internal struct MeterSummary
    {
        internal int Windows;
        // Power mean over the windows, in dB relative to full scale.
        internal double RmsDb;
        internal double PeakAbs;
        // Mean energy of the three bands (linear mean squares): below 250 Hz, 250 Hz to 2 kHz, 2 to 5 kHz.
        internal double Lo, Mid, Hi;
        // Left share of the energy, 0 to 1 (0.5 is the middle).
        internal double LeftShare;
        // The spectral centroid in Hz, estimated from the three bands at 125, 700 and 3200 Hz.
        internal double CentroidHz;
    }

    // The meter of the audio probe: per window of 100 ms the mean square of each channel, the peak and the energy of three bands. It sits
    // on the audio thread (Feed, no allocation after the constructor, single writer) and the main thread reads finished windows. The final
    // mix at the listener is what it sees, other mods and music included, so a scenario compares sections of its own making.
    internal sealed class ProbeMeter
    {
        internal const double WindowSeconds = 0.1;
        internal const double ClipLevel = 0.999;

        private readonly int windowFrames;
        private readonly float[] energyL, energyR, peak, lo, mid, hi;
        private readonly Biquad loL = new Biquad(), hiPassMid = new Biquad(), lowPassMid = new Biquad(), hiHi = new Biquad(), hiTop = new Biquad();
        private readonly int capacity;
        private int framesInWindow;
        private double sumL, sumR, sumPeak, sumLo, sumMid, sumHi;
        private volatile int windowCount;
        private long nonFinite, clipped;

        internal ProbeMeter(int sampleRate, int capacityWindows)
        {
            windowFrames = Math.Max(1, (int)(sampleRate * WindowSeconds));
            capacity = Math.Max(1, capacityWindows);
            energyL = new float[capacity];
            energyR = new float[capacity];
            peak = new float[capacity];
            lo = new float[capacity];
            mid = new float[capacity];
            hi = new float[capacity];
            loL.LowPass(sampleRate, 250.0, 0.707);
            hiPassMid.HighPass(sampleRate, 250.0, 0.707);
            lowPassMid.LowPass(sampleRate, 2000.0, 0.707);
            hiHi.HighPass(sampleRate, 2000.0, 0.707);
            hiTop.LowPass(sampleRate, 5000.0, 0.707);
        }

        internal int WindowCount { get { return windowCount; } }
        internal long NonFiniteSamples { get { return nonFinite; } }
        internal long ClippedSamples { get { return clipped; } }

        // Takes a block of interleaved samples (OnAudioFilterRead of the listener). The data are only read.
        internal void Feed(float[] data, int channels)
        {
            if (channels < 1) return;
            int frames = data.Length / channels;
            for (int f = 0; f < frames; f++)
            {
                float l = data[f * channels];
                float r = channels > 1 ? data[f * channels + 1] : l;
                if (float.IsNaN(l) || float.IsInfinity(l) || float.IsNaN(r) || float.IsInfinity(r))
                {
                    nonFinite++;
                    l = 0f;
                    r = 0f;
                }
                double al = Math.Abs(l), ar = Math.Abs(r);
                if (al >= ClipLevel || ar >= ClipLevel) clipped++;
                double m = 0.5 * (l + r);
                sumL += (double)l * l;
                sumR += (double)r * r;
                double pk = al > ar ? al : ar;
                if (pk > sumPeak) sumPeak = pk;
                double low = loL.Process(m);
                double band = lowPassMid.Process(hiPassMid.Process(m));
                double high = hiTop.Process(hiHi.Process(m));
                sumLo += low * low;
                sumMid += band * band;
                sumHi += high * high;
                if (++framesInWindow >= windowFrames) Close();
            }
        }

        private void Close()
        {
            int i = windowCount;
            if (i < capacity)
            {
                double n = framesInWindow;
                energyL[i] = (float)(sumL / n);
                energyR[i] = (float)(sumR / n);
                peak[i] = (float)sumPeak;
                lo[i] = (float)(sumLo / n);
                mid[i] = (float)(sumMid / n);
                hi[i] = (float)(sumHi / n);
                windowCount = i + 1;
            }
            framesInWindow = 0;
            sumL = sumR = sumPeak = sumLo = sumMid = sumHi = 0.0;
        }

        // The summary of windows from (inclusive) to to (exclusive), clamped to the finished ones.
        internal MeterSummary Summarise(int from, int to)
        {
            int count = windowCount;
            from = Math.Max(0, from);
            to = Math.Min(to, count);
            var s = new MeterSummary();
            if (to <= from) { s.RmsDb = -120.0; return s; }
            double eL = 0, eR = 0, eLo = 0, eMid = 0, eHi = 0, pk = 0;
            for (int i = from; i < to; i++)
            {
                eL += energyL[i];
                eR += energyR[i];
                eLo += lo[i];
                eMid += mid[i];
                eHi += hi[i];
                if (peak[i] > pk) pk = peak[i];
            }
            int n = to - from;
            s.Windows = n;
            double ms = 0.5 * (eL + eR) / n;
            s.RmsDb = ms > 1e-12 ? 10.0 * Math.Log10(ms) : -120.0;
            s.PeakAbs = pk;
            s.Lo = eLo / n;
            s.Mid = eMid / n;
            s.Hi = eHi / n;
            s.LeftShare = eL + eR > 1e-18 ? eL / (eL + eR) : 0.5;
            double total = s.Lo + s.Mid + s.Hi;
            s.CentroidHz = total > 1e-18 ? (s.Lo * 125.0 + s.Mid * 700.0 + s.Hi * 3200.0) / total : 0.0;
            return s;
        }

        // The energy of the high band of one window (linear mean square), for the burst test at steps.
        internal double HighEnergy(int window)
        {
            return window >= 0 && window < windowCount ? hi[window] : 0.0;
        }

        // The mean square of one window over both channels, for envelope reading.
        internal double WindowEnergy(int window)
        {
            return window >= 0 && window < windowCount ? 0.5 * (energyL[window] + energyR[window]) : 0.0;
        }

        // dB of a linear mean square.
        internal static double Db(double meanSquare)
        {
            return meanSquare > 1e-12 ? 10.0 * Math.Log10(meanSquare) : -120.0;
        }

        // The probe verdicts that need no scenario: no non-finite sample, no clipping.
        internal string FailureReason(bool expectSound, double rmsDbWhenLoud)
        {
            if (nonFinite > 0) return "non-finite samples: " + nonFinite;
            if (clipped > 0) return "clipped samples: " + clipped;
            if (expectSound && rmsDbWhenLoud <= -100.0) return "silent although the level is above 0.3";
            return null;
        }
    }
}
