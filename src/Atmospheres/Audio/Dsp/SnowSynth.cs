using System;

namespace ReDefinition.Atmospheres.Audio
{
    // The hiss of falling snow: pink-ish noise high-passed at 2.5 kHz. Amplitude (about intensity to the power 1.5) is applied by
    // the caller. Two noise streams for the two channels. No allocation, no UnityEngine.
    public sealed class SnowSynth
    {
        public const double HighPassHz = 2500.0;

        private PinkNoise pinkL, pinkR;
        private readonly Biquad highL = new Biquad(), highR = new Biquad();

        public SnowSynth(double sampleRate, uint seed)
        {
            pinkL = new PinkNoise(Xorshift32.Derive(seed, 5));
            pinkR = new PinkNoise(Xorshift32.Derive(seed, 6));
            highL.HighPass(sampleRate, HighPassHz, 0.707);
            highR.HighPass(sampleRate, HighPassHz, 0.707);
        }

        public void Next(out float left, out float right)
        {
            left = (float)highL.Process(pinkL.Next());
            right = (float)highR.Process(pinkR.Next());
        }
    }

    // The variant for sand and dust: a brighter band (about 2.2 kHz, broad) whose level is broken up by a slow random envelope
    // (about 60 Hz low-passed noise, squared), so it grates rather than hisses. No allocation, no UnityEngine.
    public sealed class GritSynth
    {
        private Xorshift32 rngL, rngR, modL, modR;
        private readonly Biquad bandL = new Biquad(), bandR = new Biquad(), envL = new Biquad(), envR = new Biquad();

        public GritSynth(double sampleRate, uint seed)
        {
            rngL = new Xorshift32(Xorshift32.Derive(seed, 7));
            rngR = new Xorshift32(Xorshift32.Derive(seed, 8));
            modL = new Xorshift32(Xorshift32.Derive(seed, 9));
            modR = new Xorshift32(Xorshift32.Derive(seed, 10));
            bandL.BandPass(sampleRate, 2200.0, 0.6);
            bandR.BandPass(sampleRate, 2600.0, 0.6);
            envL.LowPass(sampleRate, 60.0, 0.707);
            envR.LowPass(sampleRate, 60.0, 0.707);
        }

        public void Next(out float left, out float right)
        {
            double eL = envL.Process(modL.NextSigned());
            double eR = envR.Process(modR.NextSigned());
            // The low-passed noise has a small amplitude; the gain brings its square to about 0.5 on average.
            double gl = 0.25 + 28.0 * eL * eL;
            double gr = 0.25 + 28.0 * eR * eR;
            left = (float)(bandL.Process(rngL.NextSigned()) * gl);
            right = (float)(bandR.Process(rngR.NextSigned()) * gr);
        }
    }

    // The quiet ambience of deep snow in calm air: noise low-passed at 400 Hz. Level (about -40 dB) and the fade-in are the
    // caller's. No allocation, no UnityEngine.
    public sealed class QuietSnowTone
    {
        // Computed on every Decimation-th sample and interpolated linearly in between (the filter is designed for the lower rate; the
        // phase counts from the first sample, so the output does not depend on the block size). The noise gain keeps the level.
        public const int Decimation = 4;
        private const double NoiseScale = 0.5;

        private Xorshift32 rngL, rngR;
        private readonly Biquad lowL = new Biquad(), lowR = new Biquad();
        private int phase;
        private double prevL, prevR, curL, curR;

        public QuietSnowTone(double sampleRate, uint seed)
        {
            rngL = new Xorshift32(Xorshift32.Derive(seed, 11));
            rngR = new Xorshift32(Xorshift32.Derive(seed, 12));
            lowL.LowPass(sampleRate / Decimation, 400.0, 0.707);
            lowR.LowPass(sampleRate / Decimation, 400.0, 0.707);
        }

        public void Next(out float left, out float right)
        {
            if (phase == 0)
            {
                prevL = curL;
                prevR = curR;
                // The same noise power spread over a quarter of the band: scaled by 1/sqrt(Decimation) so the level stays.
                curL = lowL.Process(rngL.NextSigned() * NoiseScale);
                curR = lowR.Process(rngR.NextSigned() * NoiseScale);
            }
            double k = (phase + 1) * (1.0 / Decimation);
            phase = phase + 1 == Decimation ? 0 : phase + 1;
            left = (float)(prevL + (curL - prevL) * k);
            right = (float)(prevR + (curR - prevR) * k);
        }
    }
}
