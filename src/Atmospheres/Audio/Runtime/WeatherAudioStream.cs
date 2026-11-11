using System;
using System.Diagnostics;

namespace ReDefinition.Atmospheres.Audio
{
    // What the main thread hands to the audio thread besides AudioParams: the listener's output stage and the counters the audio thread
    // keeps. One instance lives in a field of the module; the audio thread passes `in shared.Params` to the mixer, so it follows the writer.
    internal sealed class SharedAudioState
    {
        // The mixer's parameters (a field, not a property: the mixer reads it by reference).
        internal AudioParams Params = AudioParams.Create();
        // 0 to 1: how much of the map-view muffling (gain 0.25, low-pass 2 kHz) is applied.
        internal volatile float MapBlend;
        // 0 to 1: how much of the helmet colour (gain 0.8, low-pass 4 kHz) is applied.
        internal volatile float HelmetBlend;
        // Counters of the audio thread (single writer).
        internal long Blocks;
        internal long Underruns;
        internal long TicksTotal;
        // The frames of the last block (the engine's buffer size), for the cost per block against its duration.
        internal volatile int LastFrames;
        internal volatile bool Running;
    }

    // The callback of the stream clip (output path B of RECON P5, AudioClip.Create with a PCM reader): fills the buffer from the mixer, applies
    // the output stage (map view, helmet) and keeps the cost of every block. It holds no Unity object and allocates nothing after the
    // constructor, so a test renders it for ten minutes and counts the collections.
    internal sealed class WeatherAudioStream
    {
        // The map view: x0.25 and low-passed at 2 kHz (you are not in the weather). The helmet: x0.8 and a slight low-pass.
        internal const double MapGain = 0.25;
        internal const double MapCutoffHz = 2000.0;
        internal const double HelmetGain = 0.8;
        internal const double HelmetCutoffHz = 4000.0;
        internal const double StageSmoothingS = 0.2;

        private readonly SharedAudioState shared;
        private readonly Mixer mixer;
        private readonly int sampleRate;
        private readonly Biquad mapL = new Biquad(), mapR = new Biquad(), helL = new Biquad(), helR = new Biquad();
        private Smoother sMap, sHelmet;
        private bool staged;
        private readonly double secondsPerTick = 1.0 / Stopwatch.Frequency;

        internal WeatherAudioStream(SharedAudioState shared, Mixer mixer, int sampleRate)
        {
            this.shared = shared;
            this.mixer = mixer;
            this.sampleRate = sampleRate;
            mapL.LowPass(sampleRate, MapCutoffHz, 0.707);
            mapR.LowPass(sampleRate, MapCutoffHz, 0.707);
            helL.LowPass(sampleRate, HelmetCutoffHz, 0.707);
            helR.LowPass(sampleRate, HelmetCutoffHz, 0.707);
            sMap.Init(sampleRate, StageSmoothingS);
            sHelmet.Init(sampleRate, StageSmoothingS);
            sMap.Snap(0.0);
            sHelmet.Snap(0.0);
        }

        internal Mixer Mixer { get { return mixer; } }

        // Fills data (interleaved, channels) with the next samples. Audio thread.
        internal void Fill(float[] data, int channels)
        {
            long t0 = Stopwatch.GetTimestamp();
            int frames = data.Length / channels;
            mixer.Process(data, frames, channels, in shared.Params);
            double mapTarget = shared.MapBlend;
            double helTarget = shared.HelmetBlend;
            mapTarget = mapTarget < 0.0 ? 0.0 : mapTarget > 1.0 ? 1.0 : mapTarget;
            helTarget = helTarget < 0.0 ? 0.0 : helTarget > 1.0 ? 1.0 : helTarget;
            bool stereo = channels >= 2;
            for (int f = 0; f < frames; f++)
            {
                double m = sMap.Next(mapTarget);
                double h = sHelmet.Next(helTarget);
                int o = f * channels;
                if (m > 1e-4 || h > 1e-4)
                {
                    double l = data[o];
                    l = Stage(l, helL, mapL, h, m);
                    data[o] = (float)l;
                    if (stereo)
                    {
                        double r = Stage(data[o + 1], helR, mapR, h, m);
                        data[o + 1] = (float)r;
                    }
                    staged = true;
                }
                else if (staged)
                {
                    // Idle: the filters are not run, their state is cleared once (a blend that rises from zero starts from silence).
                    mapL.ClearState();
                    mapR.ClearState();
                    helL.ClearState();
                    helR.ClearState();
                    staged = false;
                }
            }
            long t1 = Stopwatch.GetTimestamp();
            long dt = t1 - t0;
            shared.TicksTotal += dt;
            shared.Blocks++;
            shared.LastFrames = frames;
            double blockSeconds = (double)frames / sampleRate;
            if (dt * secondsPerTick > blockSeconds) shared.Underruns++;
            shared.Running = true;
        }

        private static double Stage(double x, Biquad helmet, Biquad map, double h, double m)
        {
            double a = x + (helmet.Process(x) * HelmetGain - x) * h;
            double b = a + (map.Process(a) * MapGain - a) * m;
            return b;
        }

        // The mean cost of a block in milliseconds since the start, 0 before the first block.
        internal static double MeanBlockMs(SharedAudioState s)
        {
            long n = s.Blocks;
            return n <= 0 ? 0.0 : s.TicksTotal * 1000.0 / Stopwatch.Frequency / n;
        }
    }
}
