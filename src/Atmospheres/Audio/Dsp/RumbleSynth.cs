using System;

namespace ReDefinition.Atmospheres.Audio
{
    // The distant rumble of a storm cell: brown noise (leaky integrated white noise) low-passed at 90 to 140 Hz (the cutoff rises
    // as the cell comes closer) with a slow 0.3 Hz swell. The two channels share part of their noise; a pan from -1 to 1 places the
    // cell. Swell and pan are set at the control rate. Amplitude is applied by the caller. No allocation, no UnityEngine.
    public sealed class RumbleSynth
    {
        public const double SwellHz = 0.3;

        private readonly double sampleRate;
        private Xorshift32 rngL, rngR;
        private double brownL, brownR;
        private readonly Biquad lowL1 = new Biquad(), lowL2 = new Biquad(), lowR1 = new Biquad(), lowR2 = new Biquad();
        private double swellPhase;
        private double gainL = 1.0, gainR = 1.0;
        private double lastCloseness = double.NaN;

        public RumbleSynth(double sampleRate, uint seed)
        {
            this.sampleRate = sampleRate;
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
                lowL1.LowPass(sampleRate, fc, 0.707);
                lowL2.LowPass(sampleRate, fc, 0.707);
                lowR1.LowPass(sampleRate, fc, 0.707);
                lowR2.LowPass(sampleRate, fc, 0.707);
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
            brownL = brownL * 0.9995 + 0.02 * rngL.NextSigned();
            brownR = brownR * 0.9995 + 0.02 * rngR.NextSigned();
            double l = lowL2.Process(lowL1.Process(brownL));
            double r = lowR2.Process(lowR1.Process(brownR));
            left = (float)((0.7 * l + 0.3 * r) * gainL);
            right = (float)((0.7 * r + 0.3 * l) * gainR);
        }
    }
}
