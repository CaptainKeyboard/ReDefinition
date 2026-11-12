using System;

namespace ReDefinition.Atmospheres.Audio
{
    // The thunder as a procedural sound (package T07): a short crack of high-passed noise that dies within a few tens of milliseconds, then a
    // rumble of low-passed brown noise under an envelope of several overlapping swells (the arrivals from the different parts of the
    // channel), the whole rolled off by the distance's cutoff. No recording is used. The clip is a pure function of the seed and the plan, so
    // the same flash always sounds the same. Mono at 22.05 kHz: the crack has little above 8 kHz after the air has had its way.
    internal static class ThunderSynth
    {
        internal const int SampleRate = 22050;
        // The peak of the rendered clip; the source's volume carries the loudness of the plan.
        internal const float Peak = 0.9f;

        // The length of the clip in samples for a plan.
        internal static int LengthOf(ThunderPlan plan)
        {
            return (int)((plan.DurationS + 0.6f) * SampleRate);
        }

        internal static float[] Render(uint seed, ThunderPlan plan)
        {
            int n = LengthOf(plan);
            var output = new float[n];
            Render(seed, plan, output);
            return output;
        }

        // Fills a buffer of LengthOf(plan) samples.
        internal static void Render(uint seed, ThunderPlan plan, float[] output)
        {
            int n = Math.Min(output.Length, LengthOf(plan));
            double sr = SampleRate;
            double dur = Math.Max(plan.DurationS, 0.5f);
            var noise = new Xorshift32(Xorshift32.Derive(seed, 1));
            var crackNoise = new Xorshift32(Xorshift32.Derive(seed, 2));
            var lp1 = new Biquad();
            var lp2 = new Biquad();
            var dc = new Biquad();
            var hp = new Biquad();
            double fc = Math.Max(40.0, Math.Min(plan.CutoffHz, sr * 0.45));
            lp1.LowPass(sr, fc, 0.707);
            lp2.LowPass(sr, fc, 0.707);
            dc.HighPass(sr, 22.0, 0.707);
            hp.HighPass(sr, 900.0, 0.707);

            // The swells: an opening thump and the arrivals from along the channel, later ones wider and weaker.
            int count = 4 + (int)(4.0 * Unit(seed, 3)) + (int)(dur / 3.0);
            var at = new double[count];
            var width = new double[count];
            var amp = new double[count];
            at[0] = 0.04;
            width[0] = 0.10;
            amp[0] = 1.0;
            for (int i = 1; i < count; i++)
            {
                double u = Unit(seed, 10 + 3 * i);
                at[i] = 0.1 + dur * 0.85 * Math.Pow(u, 1.4);
                width[i] = (0.15 + 0.5 * Unit(seed, 11 + 3 * i)) * (1.0 + 0.12 * dur);
                amp[i] = (0.35 + 0.65 * Unit(seed, 12 + 3 * i)) * Math.Exp(-at[i] / (0.55 * dur));
            }

            double brown = 0.0;
            double peak = 1e-9;
            // The envelope is evaluated every EnvStep samples and interpolated between (the swells are slow; the exponentials dominate otherwise).
            double envA = Envelope(0.0, at, width, amp, count), envB = envA;
            for (int s = 0; s < n; s++)
            {
                if (s % EnvStep == 0)
                {
                    envA = envB;
                    envB = Envelope((s + EnvStep) / sr, at, width, amp, count);
                }
                double env = envA + (envB - envA) * (s % EnvStep) / EnvStep;
                double w = noise.NextSigned();
                brown = brown * 0.996 + w * 0.12;
                double r = dc.Process(brown);
                r = lp2.Process(lp1.Process(r));
                double v = r * env;
                output[s] = (float)v;
                double a = v < 0.0 ? -v : v;
                if (a > peak) peak = a;
            }
            double scale = (plan.CrackGain > 0.0f ? 0.75 : 1.0) * Peak / peak;
            for (int s = 0; s < n; s++) output[s] = (float)(output[s] * scale);

            // The crack on top: high-passed noise with a very short decay, then a short second tear.
            if (plan.CrackGain > 0f)
            {
                int cl = Math.Min(n, (int)(0.12 * sr));
                double tear = 0.035 + 0.03 * Unit(seed, 4);
                for (int s = 0; s < cl; s++)
                {
                    double t = s / sr;
                    double e = Math.Exp(-t / 0.011) + 0.45 * Math.Exp(-Math.Abs(t - tear) / 0.006);
                    double c = hp.Process(crackNoise.NextSigned()) * e * plan.CrackGain * 0.55;
                    output[s] += (float)c;
                }
            }

            // A short fade in and a longer one out, so that the clip neither clicks nor ends on a level; then the peak is held under 1.
            int fadeIn = Math.Min(n, (int)(0.0015 * sr));
            for (int s = 0; s < fadeIn; s++) output[s] *= (float)s / fadeIn;
            int fadeOut = Math.Min(n, (int)(0.25 * sr));
            for (int s = 0; s < fadeOut; s++) output[n - 1 - s] *= (float)s / fadeOut;
            for (int s = 0; s < n; s++)
            {
                float v = output[s];
                if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
                output[s] = v > 0.98f ? 0.98f : v < -0.98f ? -0.98f : v;
            }
            for (int s = n; s < output.Length; s++) output[s] = 0f;
        }

        private const int EnvStep = 64;

        private static double Envelope(double t, double[] at, double[] width, double[] amp, int count)
        {
            double env = 0.0;
            for (int i = 0; i < count; i++)
            {
                double x = (t - at[i]) / width[i];
                if (x > -4.0 && x < 4.0) env += amp[i] * Math.Exp(-0.5 * x * x);
            }
            return env;
        }

        private static double Unit(uint seed, int k)
        {
            return LightningSchedule.Draw(seed, 400 + k);
        }
    }
}
