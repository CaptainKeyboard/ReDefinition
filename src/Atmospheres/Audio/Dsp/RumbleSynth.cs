using System;

namespace ReDefinition.Atmospheres.Audio
{
    // The distant rumble of a storm cell: brown noise (leaky integrated white noise) low-passed at 90 to 140 Hz (the cutoff rises
    // as the cell comes closer) with a slow 0.3 Hz swell. The two channels share part of their noise; a pan from -1 to 1 places the
    // cell. Swell and pan are set at the control rate. Amplitude is applied by the caller. No allocation, no UnityEngine.
    public sealed class RumbleSynth
    {
        public const double SwellHz = 0.3;

        // Everything below 140 Hz is computed on every Decimation-th sample and the samples in between are interpolated linearly
        // (the filters are designed for the lower rate). The phase counts from the first sample, so the output does not depend on
        // the block size.
        public const int Decimation = 4;

        private readonly double sampleRate;
        private readonly double lowRate;
        private readonly double leak;
        private readonly double noiseGain;
        private int phase;
        private double prevL, prevR, curL, curR;
        private Xorshift32 rngL, rngR;
        private double brownL, brownR;
        private readonly Biquad lowL1 = new Biquad(), lowL2 = new Biquad(), lowR1 = new Biquad(), lowR2 = new Biquad();
        private double swellPhase;
        private double gainL = 1.0, gainR = 1.0;
        private double lastCloseness = double.NaN;

        public RumbleSynth(double sampleRate, uint seed)
        {
            this.sampleRate = sampleRate;
            lowRate = sampleRate / Decimation;
            // The brown noise leaks with a time constant of 2000 samples at 48 kHz (0.9995 per sample); the noise gain keeps the
            // level of the full-rate form when the step is Decimation samples long.
            const double Tau = 2000.0 / 48000.0;
            double a1 = Math.Exp(-1.0 / (Tau * sampleRate));
            leak = Math.Exp(-(double)Decimation / (Tau * sampleRate));
            noiseGain = 0.02 * Math.Sqrt((1.0 - leak * leak) / (1.0 - a1 * a1));
            phase = 0;
            rngL = new Xorshift32(Xorshift32.Derive(seed, 13));
            rngR = new Xorshift32(Xorshift32.Derive(seed, 14));
            SetControl(0.0, 0.0, 0);
        }

        // closeness 0 (far) to 1 (near): cutoff 90 to 140 Hz. pan from -1 (left) to 1 (right), constant power. samples is the number
        // of samples since the last call (it advances the swell).
        public void SetControl(double closeness, double pan, int samples)
        {
            closeness = closeness < 0.0 ? 0.0 : closeness > 1.0 ? 1.0 : closeness;
            // A change below 0.002 moves the cutoff by less than 0.1 Hz: the coefficients stay.
            if (!(Math.Abs(closeness - lastCloseness) < 0.002))
            {
                lastCloseness = closeness;
                double fc = 90.0 + 50.0 * closeness;
                lowL1.LowPass(lowRate, fc, 0.707);
                lowL2.LowPass(lowRate, fc, 0.707);
                lowR1.LowPass(lowRate, fc, 0.707);
                lowR2.LowPass(lowRate, fc, 0.707);
            }
            swellPhase += 2.0 * Math.PI * SwellHz * samples / sampleRate;
            if (swellPhase > 2.0 * Math.PI) swellPhase -= 2.0 * Math.PI;
            double swell = 1.0 + 0.3 * Math.Sin(swellPhase);
            double angle = (pan < -1.0 ? -1.0 : pan > 1.0 ? 1.0 : pan) * 0.25 * Math.PI + 0.25 * Math.PI;
            gainL = swell * Math.Cos(angle) * 1.4142135623730951;
            gainR = swell * Math.Sin(angle) * 1.4142135623730951;
        }

        public void Next(out float left, out float right)
        {
            if (phase == 0)
            {
                brownL = brownL * leak + noiseGain * rngL.NextSigned();
                brownR = brownR * leak + noiseGain * rngR.NextSigned();
                prevL = curL;
                prevR = curR;
                curL = lowL2.Process(lowL1.Process(brownL));
                curR = lowR2.Process(lowR1.Process(brownR));
            }
            double k = (phase + 1) * (1.0 / Decimation);
            phase = phase + 1 == Decimation ? 0 : phase + 1;
            double l = prevL + (curL - prevL) * k;
            double r = prevR + (curR - prevR) * k;
            left = (float)((0.7 * l + 0.3 * r) * gainL);
            right = (float)((0.7 * r + 0.3 * l) * gainR);
        }
    }
}
