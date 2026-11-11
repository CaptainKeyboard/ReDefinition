using System;

namespace ReDefinition.Atmospheres.Audio
{
    // Ticks on the hull: a Poisson process (rate = intensity x 40 per second) of short noise bursts, each a band-pass (3 kHz, +-30 %)
    // of noise under an exponential decay of 5 to 20 ms. At most 64 bursts sound at once, from a fixed pool. The random numbers that
    // decide a tick's gain, pitch, decay and pan are drawn on every sample whether or not a tick starts, so a higher rate starts a
    // superset of the ticks of a lower rate with the same properties. Technique: granular synthesis of impacts (A. Farnell,
    // "Designing Sound"). No allocation, no UnityEngine.
    public sealed class HullTicks
    {
        public const int MaxVoices = 64;
        public const double RatePerIntensity = 40.0;

        private struct Voice
        {
            public float Env;
            public float Decay;
            public float Gain;
            public float PanL, PanR;
            public Xorshift32 Noise;
            public Biquad Band;
        }

        private readonly double sampleRate;
        private readonly Voice[] voices = new Voice[MaxVoices];
        // The indices of the sounding voices (the first `active` entries) and of the free ones (the first `free` entries).
        private readonly int[] activeList = new int[MaxVoices];
        private readonly int[] freeList = new int[MaxVoices];
        private Xorshift32 rngEvent, rngGain, rngPitch, rngDecay, rngPan, rngSeed;
        private int active;
        private int free;

        public HullTicks(double sampleRate, uint seed)
        {
            this.sampleRate = sampleRate;
            rngEvent = new Xorshift32(Xorshift32.Derive(seed, 15));
            rngGain = new Xorshift32(Xorshift32.Derive(seed, 16));
            rngPitch = new Xorshift32(Xorshift32.Derive(seed, 17));
            rngDecay = new Xorshift32(Xorshift32.Derive(seed, 18));
            rngPan = new Xorshift32(Xorshift32.Derive(seed, 19));
            rngSeed = new Xorshift32(Xorshift32.Derive(seed, 20));
            for (int i = 0; i < MaxVoices; i++)
            {
                voices[i].Band = new Biquad();
                freeList[i] = MaxVoices - 1 - i;
            }
            free = MaxVoices;
        }

        public int ActiveVoices { get { return active; } }

        // How many ticks have started since construction.
        public long Started { get; private set; }

        // intensity from 0 to 1.
        public void Next(double intensity, out float left, out float right)
        {
            // All six streams advance on every sample.
            float u = rngEvent.NextUnit();
            float g = rngGain.NextUnit();
            float pitch = rngPitch.NextSigned();
            float dec = rngDecay.NextUnit();
            float pan = rngPan.NextUnit();
            uint noiseSeed = rngSeed.NextUInt();

            double p = intensity * RatePerIntensity / sampleRate;
            if (u < p && free > 0) Spawn(g, pitch, dec, pan, noiseSeed);

            double l = 0.0, r = 0.0;
            for (int k = 0; k < active; k++)
            {
                int i = activeList[k];
                double s = voices[i].Band.Process(voices[i].Noise.NextSigned()) * voices[i].Env * voices[i].Gain;
                l += s * voices[i].PanL;
                r += s * voices[i].PanR;
                voices[i].Env *= voices[i].Decay;
                if (voices[i].Env < 0.003f)
                {
                    // The voice goes back to the free list; the last sounding voice takes its place in the list.
                    freeList[free++] = i;
                    activeList[k] = activeList[--active];
                    k--;
                }
            }
            left = (float)l;
            right = (float)r;
        }

        private void Spawn(float gain, float pitch, float dec, float pan, uint noiseSeed)
        {
            int slot = freeList[--free];
            double decaySeconds = 0.005 + 0.015 * dec;
            // The burst falls by 50 dB in the decay time.
            voices[slot].Decay = (float)Math.Exp(Math.Log(0.003) / (decaySeconds * sampleRate));
            voices[slot].Env = 1f;
            voices[slot].Gain = 0.25f + 0.75f * gain;
            double a = pan * 0.5 * Math.PI;
            voices[slot].PanL = (float)Math.Cos(a);
            voices[slot].PanR = (float)Math.Sin(a);
            voices[slot].Noise = new Xorshift32(noiseSeed);
            voices[slot].Band.ClearState();
            voices[slot].Band.BandPass(sampleRate, 3000.0 * (1.0 + 0.3 * pitch), 1.5);
            activeList[active++] = slot;
            Started++;
        }
    }
}
