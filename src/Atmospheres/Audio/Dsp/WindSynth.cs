using System;

namespace ReDefinition.Atmospheres.Audio
{
    // The wind bed: white noise through a broad band-pass and a low-pass whose cutoff rises with the wind speed (180 Hz at rest
    // up to 1800 Hz at 30 m/s). The right channel runs on a second noise stream through filters a third of an octave higher, so
    // the bed has width. Technique: filtered noise with a speed-driven centre (J. Strout, "Procedural Audio in Unity"; A. Farnell,
    // "Designing Sound"). Amplitude is applied by the caller. Unit-less output, no allocation, no UnityEngine.
    public sealed class WindSynth
    {
        public const double CutoffAtRestHz = 180.0;
        public const double CutoffAtTopHz = 1800.0;
        public const double TopSpeedMs = 30.0;
        private const double ThirdOctave = 1.2599210498948732;

        private readonly double sampleRate;
        private Xorshift32 rngL, rngR;
        private readonly Biquad bandL = new Biquad(), bandR = new Biquad(), lowL = new Biquad(), lowR = new Biquad();

        public WindSynth(double sampleRate, uint seed)
        {
            this.sampleRate = sampleRate;
            rngL = new Xorshift32(Xorshift32.Derive(seed, 1));
            rngR = new Xorshift32(Xorshift32.Derive(seed, 2));
            SetSpeed(0.0);
        }

        // The centre of the bed for a wind speed in m/s.
        public static double CutoffFor(double speedMs)
        {
            double t = speedMs / TopSpeedMs;
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            return CutoffAtRestHz + (CutoffAtTopHz - CutoffAtRestHz) * t;
        }

        private double lastSpeed = double.NaN;

        public void SetSpeed(double speedMs)
        {
            // A change below 0.01 m/s moves the cutoff by less than 0.1 %: the coefficients stay.
            if (Math.Abs(speedMs - lastSpeed) < 0.01) return;
            lastSpeed = speedMs;
            double fc = CutoffFor(speedMs);
            bandL.BandPass(sampleRate, fc, 0.8);
            lowL.LowPass(sampleRate, fc * 2.2, 0.707);
            bandR.BandPass(sampleRate, fc * ThirdOctave, 0.8);
            lowR.LowPass(sampleRate, fc * ThirdOctave * 2.2, 0.707);
        }

        public void Next(out float left, out float right)
        {
            // The two paths are mixed slightly into each other, so the channels are not fully decorrelated.
            double a = lowL.Process(bandL.Process(rngL.NextSigned()));
            double b = lowR.Process(bandR.Process(rngR.NextSigned()));
            left = (float)(0.85 * a + 0.15 * b);
            right = (float)(0.85 * b + 0.15 * a);
        }
    }
}
