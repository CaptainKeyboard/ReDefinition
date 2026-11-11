using System;
using System.Runtime.CompilerServices;

namespace ReDefinition.Atmospheres.Audio
{
    // A second-order filter section (direct form II transposed, double precision) with the low-pass, high-pass and band-pass
    // responses. Technique: the coefficient formulas of the widely published "Audio EQ Cookbook" (R. Bristow-Johnson); the band-pass
    // is the constant 0 dB peak gain form. Coefficients can be replaced while the filter runs; the state is kept. No allocation.
    public sealed class Biquad
    {
        private double b0 = 1.0, b1, b2, a1, a2, z1, z2;

        // Passes the signal unchanged and clears the state.
        public void Reset()
        {
            b0 = 1.0;
            b1 = b2 = a1 = a2 = 0.0;
            z1 = z2 = 0.0;
        }

        // Clears the state only.
        public void ClearState()
        {
            z1 = z2 = 0.0;
        }

        public void LowPass(double sampleRate, double freqHz, double q)
        {
            double w0, cosw, alpha;
            Prepare(sampleRate, freqHz, q, out w0, out cosw, out alpha);
            double a0 = 1.0 + alpha;
            b0 = (1.0 - cosw) * 0.5 / a0;
            b1 = (1.0 - cosw) / a0;
            b2 = b0;
            a1 = -2.0 * cosw / a0;
            a2 = (1.0 - alpha) / a0;
        }

        public void HighPass(double sampleRate, double freqHz, double q)
        {
            double w0, cosw, alpha;
            Prepare(sampleRate, freqHz, q, out w0, out cosw, out alpha);
            double a0 = 1.0 + alpha;
            b0 = (1.0 + cosw) * 0.5 / a0;
            b1 = -(1.0 + cosw) / a0;
            b2 = b0;
            a1 = -2.0 * cosw / a0;
            a2 = (1.0 - alpha) / a0;
        }

        // Band-pass with a gain of 1 at the centre frequency.
        public void BandPass(double sampleRate, double freqHz, double q)
        {
            double w0, cosw, alpha;
            Prepare(sampleRate, freqHz, q, out w0, out cosw, out alpha);
            double a0 = 1.0 + alpha;
            b0 = alpha / a0;
            b1 = 0.0;
            b2 = -alpha / a0;
            a1 = -2.0 * cosw / a0;
            a2 = (1.0 - alpha) / a0;
        }

        // Inlined explicitly: the game's Mono JIT does not inline it by itself and the call dominated the cost there.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Process(double x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }

        // Sets a state that has decayed to nothing to zero, so that a filter fed with silence never computes with denormal
        // numbers. Called by the owner at its control rate for filters that can see silence.
        public void Flush()
        {
            if (z1 < 1e-25 && z1 > -1e-25) z1 = 0.0;
            if (z2 < 1e-25 && z2 > -1e-25) z2 = 0.0;
        }

        private static void Prepare(double sampleRate, double freqHz, double q, out double w0, out double cosw, out double alpha)
        {
            double nyquist = sampleRate * 0.5;
            if (!(freqHz >= 1.0)) freqHz = 1.0;
            if (freqHz > nyquist * 0.92) freqHz = nyquist * 0.92;
            if (!(q >= 0.1)) q = 0.1;
            w0 = 2.0 * Math.PI * freqHz / sampleRate;
            cosw = Math.Cos(w0);
            alpha = Math.Sin(w0) / (2.0 * q);
        }
    }
}
