using System;

namespace ReDefinition.Atmospheres.Audio
{
    // The howl in strong wind: noise through a resonant band-pass (Q 8 to 14) whose centre (350 to 900 Hz, by wind speed) drifts
    // with two slow oscillators (0.07 and 0.19 Hz) and a few percent of the gust. The right channel has its own noise stream and a
    // slightly different centre. Technique: a resonant band-pass on noise with a slowly modulated centre (A. Farnell, "Designing
    // Sound"). Amplitude is applied by the caller. No allocation, no UnityEngine.
    public sealed class HowlSynth
    {
        public const double LfoAHz = 0.07;
        public const double LfoBHz = 0.19;

        private readonly double sampleRate;
        private Xorshift32 rngL, rngR;
        private readonly Biquad bandL = new Biquad(), bandR = new Biquad();
        private double phaseA, phaseB;

        public HowlSynth(double sampleRate, uint seed)
        {
            this.sampleRate = sampleRate;
            rngL = new Xorshift32(Xorshift32.Derive(seed, 3));
            rngR = new Xorshift32(Xorshift32.Derive(seed, 4));
            phaseA = 0.0;
            phaseB = 1.7;
            SetControl(0.0, 0.0);
        }

        // The centre in Hz for a wind speed in m/s and a gust wave from -1 to 1.
        public static double CentreFor(double speedMs, double gust, double lfoA, double lfoB)
        {
            double t = speedMs / WindSynth.TopSpeedMs;
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            double centre = 350.0 + 550.0 * t;
            // Up to about +-10 % from the oscillators, 5 % of the gust.
            return centre * (1.0 + 0.06 * lfoA + 0.04 * lfoB + 0.05 * gust);
        }

        // Advances the oscillators by the given number of samples and sets the filters; called at the control rate.
        public void SetControl(double speedMs, double gust)
        {
            double fc = CentreFor(speedMs, gust, Math.Sin(phaseA), Math.Sin(phaseB));
            bandL.BandPass(sampleRate, fc, 10.0);
            bandR.BandPass(sampleRate, fc * 1.04, 12.0);
        }

        public void Advance(int samples)
        {
            phaseA += 2.0 * Math.PI * LfoAHz * samples / sampleRate;
            phaseB += 2.0 * Math.PI * LfoBHz * samples / sampleRate;
            if (phaseA > 2.0 * Math.PI) phaseA -= 2.0 * Math.PI;
            if (phaseB > 2.0 * Math.PI) phaseB -= 2.0 * Math.PI;
        }

        public void Next(out float left, out float right)
        {
            left = (float)bandL.Process(rngL.NextSigned());
            right = (float)bandR.Process(rngR.NextSigned());
        }
    }
}
