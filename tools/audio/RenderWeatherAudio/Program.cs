using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ReDefinition.Atmospheres.Audio;

namespace RenderWeatherAudio
{
    // Renders the six listening scenarios of the weather sound to 16-bit stereo WAV files:
    //   RenderWeatherAudio.exe <output folder> [--rate 48000] [--seed 1] [--seconds 30]
    //   RenderWeatherAudio.exe --levels          prints the level of each layer alone, in dBFS
    // The parameter tracks are written here. This tool compiles only the Dsp folder, which cannot reach WindNoise (it needs
    // UnityEngine), so the tracks carry their own copy of the gust form: the periods 9.0 s and 5.3 s and the weights 0.6 and 0.4 of
    // WindNoise.Wave, with a fixed phase of 1.1 for the second wave instead of the seed's phases and no travel along the wind. The
    // tests use WindNoise.Wave itself.
    internal static class Program
    {
        private delegate void Track(double t, double duration, ref AudioParams p);

        private sealed class Scenario
        {
            public string Name;
            public Track Track;
        }

        private static int Main(string[] args)
        {
            int rate = 48000;
            uint seed = 1;
            double seconds = 30.0;
            string output = null;
            bool levels = false, bench = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--rate") rate = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--seed") seed = uint.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--seconds") seconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--levels") levels = true;
                else if (args[i] == "--bench") bench = true;
                else output = args[i];
            }
            if (bench)
            {
                Bench(rate);
                return 0;
            }
            if (levels)
            {
                PrintLevels(rate, seed);
                return 0;
            }
            if (output == null)
            {
                Console.Error.WriteLine("usage: RenderWeatherAudio <output folder> [--rate 48000] [--seed 1] [--seconds 30] | --levels");
                return 2;
            }
            Directory.CreateDirectory(output);
            foreach (Scenario s in Scenarios())
            {
                string path = Path.Combine(output, s.Name + ".wav");
                Render(s, path, rate, seed, seconds);
                Console.WriteLine(path);
            }
            return 0;
        }

        private static double Wave(double t)
        {
            return 0.6 * Math.Sin(2.0 * Math.PI * t / 9.0) + 0.4 * Math.Sin(2.0 * Math.PI * t / 5.3 + 1.1);
        }

        private static double Ramp(double t, double from, double to, double a, double b)
        {
            double x = (t - from) / (to - from);
            x = x < 0 ? 0 : x > 1 ? 1 : x;
            return a + (b - a) * x;
        }

        private static IEnumerable<Scenario> Scenarios()
        {
            // 1 calm: deep snow, a breath of wind, the quiet ambience.
            yield return new Scenario
            {
                Name = "01-calm",
                Track = delegate (double t, double d, ref AudioParams p)
                {
                    p.windSpeed = 2.0f;
                    p.gust = (float)Wave(t);
                    p.gustDepth = 0.1f;
                    p.snowCover = 1f;
                }
            };
            // 2 light snowfall: the soft hiss, hardly any wind.
            yield return new Scenario
            {
                Name = "02-light-snowfall",
                Track = delegate (double t, double d, ref AudioParams p)
                {
                    p.windSpeed = 3.0f;
                    p.gust = (float)Wave(t);
                    p.gustDepth = 0.17f;
                    p.intensity = AudioMapping_SoundLevel(0.3f);
                    p.snowCover = 0.6f;
                }
            };
            // 3 blizzard with gusts: the wind rises to 18 m/s over the first ten seconds, then gusts of 40 %.
            yield return new Scenario
            {
                Name = "03-blizzard-gusts",
                Track = delegate (double t, double d, ref AudioParams p)
                {
                    p.windSpeed = (float)Ramp(t, 0, 10, 6, 18);
                    p.gust = (float)Wave(t);
                    p.gustDepth = 0.4f;
                    p.intensity = 0.95f;
                }
            };
            // 4 interior: the same blizzard; the hull closes around the listener from 10 s to 12 s and opens again at 22 s.
            yield return new Scenario
            {
                Name = "04-interior",
                Track = delegate (double t, double d, ref AudioParams p)
                {
                    p.windSpeed = 18f;
                    p.gust = (float)Wave(t);
                    p.gustDepth = 0.4f;
                    p.intensity = 0.95f;
                    p.interior = t < 10 ? 0f : t < 22 ? 1f : 0f;
                }
            };
            // 5 approaching cell: the wind and the fall build while the rumble comes from the right and moves to the centre.
            yield return new Scenario
            {
                Name = "05-approaching-cell",
                Track = delegate (double t, double d, ref AudioParams p)
                {
                    p.windSpeed = (float)Ramp(t, 0, d, 4, 14);
                    p.gust = (float)Wave(t);
                    p.gustDepth = (float)Ramp(t, 0, d, 0.1, 0.3);
                    p.intensity = (float)Ramp(t, d * 0.4, d, 0, 0.5);
                    p.cellCloseness = (float)Ramp(t, 0, d, 0, 1);
                    p.cellPan = (float)Ramp(t, 0, d, 0.8, 0.0);
                }
            };
            // 6 dust and grit: a dust fall with 14 m/s of wind.
            yield return new Scenario
            {
                Name = "06-dust-grit",
                Track = delegate (double t, double d, ref AudioParams p)
                {
                    p.windSpeed = 14f;
                    p.gust = (float)Wave(t);
                    p.gustDepth = 0.3f;
                    p.intensity = 0.6f;
                    p.grit = 1f;
                }
            };
        }

        // The sound level column of the precipitation table (linear between key points), as AudioMapping.SoundLevel has it.
        private static float AudioMapping_SoundLevel(float level)
        {
            float[] lk = { 0f, 0.1f, 0.25f, 0.4f, 0.6f, 0.8f, 1f };
            float[] sk = { 0f, 0.05f, 0.15f, 0.3f, 0.55f, 0.8f, 1f };
            if (level <= 0f) return 0f;
            for (int i = 1; i < lk.Length; i++)
            {
                if (level <= lk[i]) return sk[i - 1] + (sk[i] - sk[i - 1]) * (level - lk[i - 1]) / (lk[i] - lk[i - 1]);
            }
            return 1f;
        }

        private static void Render(Scenario s, string path, int rate, uint seed, double seconds)
        {
            Mixer mixer = new Mixer(rate, seed);
            int block = rate / 100;
            long frames = (long)(seconds * rate);
            float[] buf = new float[block * 2];
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                WriteHeader(w, rate, frames);
                AudioParams p = AudioParams.Create();
                for (long done = 0; done < frames; done += block)
                {
                    int n = (int)Math.Min(block, frames - done);
                    s.Track(done / (double)rate, seconds, ref p);
                    mixer.Process(buf, n, 2, p);
                    for (int i = 0; i < n * 2; i++)
                    {
                        double v = buf[i] * 32767.0;
                        w.Write((short)Math.Round(v < -32768 ? -32768 : v > 32767 ? 32767 : v));
                    }
                }
            }
        }

        private static void WriteHeader(BinaryWriter w, int rate, long frames)
        {
            int dataBytes = (int)(frames * 4);
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)2);
            w.Write(rate);
            w.Write(rate * 4);
            w.Write((short)4);
            w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);
        }

        // The level of each layer alone, for tuning the constants against the plan's targets.
        private static void PrintLevels(int rate, uint seed)
        {
            Console.WriteLine("layer                      rms dBFS   peak");
            double[] speeds = { 3, 5, 10, 20, 30 };
            foreach (double v in speeds)
            {
                AudioParams p = AudioParams.Create();
                p.windSpeed = (float)v;
                p.volPrecip = 0; p.volTicks = 0; p.volRumble = 0; p.volQuiet = 0;
                Measure("wind " + v + " m/s", rate, seed, p);
            }
            double[] levelsList = { 0.1, 0.3, 0.6, 1.0 };
            foreach (double v in levelsList)
            {
                AudioParams p = AudioParams.Create();
                p.intensity = (float)v;
                p.volTicks = 0;
                Measure("hiss " + v, rate, seed, p);
                p.grit = 1f;
                Measure("grit " + v, rate, seed, p);
                p = AudioParams.Create();
                p.intensity = (float)v;
                p.volPrecip = 0;
                Measure("ticks " + v, rate, seed, p);
            }
            double[] closes = { 0.2, 0.6, 1.0 };
            foreach (double v in closes)
            {
                AudioParams p = AudioParams.Create();
                p.cellCloseness = (float)v;
                Measure("rumble " + v, rate, seed, p);
            }
            AudioParams q = AudioParams.Create();
            q.snowCover = 1f;
            Measure("quiet tone", rate, seed, q);
        }

        // The cost of the mixer in an optimised build: the busiest scene (every layer on) against a ten-section filter reference.
        private static void Bench(int rate)
        {
            AudioParams p = AudioParams.Create();
            p.windSpeed = 20; p.intensity = 1; p.grit = 0.5f; p.cellCloseness = 1; p.snowCover = 1; p.interior = 0.5f;
            p.gust = 0.5f; p.gustDepth = 0.4f;
            Mixer m = new Mixer(rate, 3);
            float[] buf = new float[rate * 2];
            m.Process(buf, rate, 2, p);
            double best = double.MaxValue;
            for (int rep = 0; rep < 9; rep++)
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                m.Process(buf, rate, 2, p);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            Biquad[] bank = new Biquad[20];
            for (int i = 0; i < bank.Length; i++) { bank[i] = new Biquad(); bank[i].BandPass(rate, 300 + 100 * i, 1.0); }
            double sink = 0, refBest = double.MaxValue;
            for (int rep = 0; rep < 9; rep++)
            {
                Xorshift32 rng = new Xorshift32(1);
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (int f = 0; f < rate; f++)
                {
                    double x = rng.NextSigned();
                    for (int k = 0; k < bank.Length; k++) x = bank[k].Process(x);
                    sink += x;
                }
                refBest = Math.Min(refBest, sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("busiest scene: {0:F2} ms per second of audio = {1:F2} % of one core at {2} Hz; reference {3:F2} ms, ratio {4:F2} ({5})",
                best, best / 10.0, rate, refBest, best / refBest, sink > -1e300 ? "ok" : "?");
            // The layers one at a time (the others at volume 0), then the same scene with the wind speed moving every sample.
            string[] names = { "wind+howl", "snow hiss", "grit", "ticks", "rumble", "quiet", "interior on wind" };
            for (int i = 0; i < names.Length; i++)
            {
                AudioParams q = AudioParams.Create();
                q.volWind = q.volPrecip = q.volTicks = q.volRumble = q.volQuiet = 0f;
                q.gustDepth = 0.4f; q.gust = 0.5f;
                switch (i)
                {
                    case 0: q.volWind = 1f; q.windSpeed = 20; break;
                    case 1: q.volPrecip = 1f; q.intensity = 1; break;
                    case 2: q.volPrecip = 1f; q.intensity = 1; q.grit = 1; break;
                    case 3: q.volTicks = 1f; q.intensity = 1; break;
                    case 4: q.volRumble = 1f; q.cellCloseness = 1; break;
                    case 5: q.volQuiet = 1f; q.snowCover = 1; break;
                    default: q.volWind = 1f; q.windSpeed = 20; q.interior = 1f; break;
                }
                Mixer lm = new Mixer(rate, 3);
                lm.Process(buf, rate, 2, q);
                double lb = double.MaxValue;
                for (int rep = 0; rep < 5; rep++)
                {
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    lm.Process(buf, rate, 2, q);
                    lb = Math.Min(lb, sw.Elapsed.TotalMilliseconds);
                }
                Console.WriteLine("  layer {0,-18} {1:F2} ms per second of audio", names[i], lb);
            }
            // The wind speed moves in steps of 0.5 m/s per control interval (a hard storm change): every control step recomputes the
            // wind coefficients.
            AudioParams mv = p;
            Mixer mm = new Mixer(rate, 3);
            float[] one = new float[Mixer.ControlInterval * 2];
            int blocks = rate / Mixer.ControlInterval;
            double movingBest = double.MaxValue;
            for (int rep = 0; rep < 6; rep++)
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (int b = 0; b < blocks; b++)
                {
                    mv.windSpeed = 12f + 10f * (float)Math.Sin(b * 0.37);
                    mm.Process(one, Mixer.ControlInterval, 2, mv);
                }
                if (rep > 0) movingBest = Math.Min(movingBest, sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine("busiest scene, wind speed moving every control step: {0:F2} ms per second of audio = {1:F2} % of one core",
                movingBest, movingBest / 10.0);
        }

        private static void Measure(string label, int rate, uint seed, AudioParams p)
        {
            Mixer m = new Mixer(rate, seed);
            float[] buf = new float[rate * 2];
            double sum = 0, peak = 0;
            long n = 0;
            for (int sec = 0; sec < 12; sec++)
            {
                m.Process(buf, rate, 2, p);
                if (sec < 6) continue;
                for (int i = 0; i < buf.Length; i++)
                {
                    sum += (double)buf[i] * buf[i];
                    peak = Math.Max(peak, Math.Abs(buf[i]));
                    n++;
                }
            }
            double rms = Math.Sqrt(sum / n);
            Console.WriteLine("{0,-26} {1,8:F1}   {2:F3}", label, 20 * Math.Log10(rms + 1e-12), peak);
        }
    }
}
