using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres.Audio;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.B15
{
    // The procedural weather sound (package B15): determinism, finiteness, level, monotonic growth with wind and intensity, the
    // spectra, gusts, block-size independence, the interior muffler, sample rates, allocation and cost.
    [TestClass]
    public class AudioDspTests
    {
        private const int Rate = 48000;

        private static float[] RenderConstant(int rate, uint seed, double seconds, int block, AudioParams p, Mixer mixer = null)
        {
            Mixer m = mixer ?? new Mixer(rate, seed);
            long frames = (long)(seconds * rate);
            float[] output = new float[frames * 2];
            float[] buf = new float[block * 2];
            long done = 0;
            while (done < frames)
            {
                int n = (int)Math.Min(block, frames - done);
                m.Process(buf, n, 2, p);
                Array.Copy(buf, 0, output, done * 2, n * 2);
                done += n;
            }
            return output;
        }

        private static ulong Hash(float[] data)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < data.Length; i++)
            {
                uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(data[i]), 0);
                for (int k = 0; k < 4; k++)
                {
                    h ^= (bits >> (8 * k)) & 0xFFu;
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        private static double Rms(float[] data, int from)
        {
            double sum = 0;
            for (int i = from; i < data.Length; i++) sum += (double)data[i] * data[i];
            return Math.Sqrt(sum / (data.Length - from));
        }

        private static AudioParams Params(double wind, double intensity)
        {
            AudioParams p = AudioParams.Create();
            p.windSpeed = (float)wind;
            p.intensity = (float)intensity;
            return p;
        }

        // The tail of a render after a warm-up second, left channel as mono.
        private static double[] MonoTail(float[] stereo, int rate, double skipSeconds)
        {
            int skip = (int)(skipSeconds * rate);
            double[] mono = new double[stereo.Length / 2 - skip];
            for (int i = 0; i < mono.Length; i++) mono[i] = 0.5 * (stereo[(i + skip) * 2] + stereo[(i + skip) * 2 + 1]);
            return mono;
        }

        // Averaged power spectrum with a Hann window; returns power per bin (size/2) and the bin width in Hz.
        private static double[] Spectrum(double[] mono, int rate, int size, out double binHz)
        {
            double[] power = new double[size / 2];
            double[] re = new double[size];
            double[] im = new double[size];
            int frames = 0;
            for (int start = 0; start + size <= mono.Length; start += size / 2)
            {
                for (int i = 0; i < size; i++)
                {
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size);
                    re[i] = mono[start + i] * w;
                    im[i] = 0;
                }
                Fft(re, im);
                for (int i = 0; i < size / 2; i++) power[i] += re[i] * re[i] + im[i] * im[i];
                frames++;
            }
            for (int i = 0; i < power.Length; i++) power[i] /= Math.Max(1, frames);
            binHz = (double)rate / size;
            return power;
        }

        private static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        double xr = re[b] * cr - im[b] * ci;
                        double xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
        }

        private static double Centroid(double[] power, double binHz)
        {
            double num = 0, den = 0;
            for (int i = 1; i < power.Length; i++)
            {
                num += power[i] * i * binHz;
                den += power[i];
            }
            return num / den;
        }

        private static double Energy(double[] power, double binHz, double fromHz, double toHz)
        {
            double sum = 0;
            for (int i = 1; i < power.Length; i++)
            {
                double f = i * binHz;
                if (f >= fromHz && f < toHz) sum += power[i];
            }
            return sum;
        }

        private static double CentroidOf(int rate, AudioParams p)
        {
            float[] r = RenderConstant(rate, 7, 8, 1024, p);
            double binHz;
            double[] spectrum = Spectrum(MonoTail(r, rate, 1.0), rate, 4096, out binHz);
            return Centroid(spectrum, binHz);
        }

        [TestMethod]
        public void TheSameSeedGivesTheSameTenSecondsAndAnotherSeedDoesNot()
        {
            AudioParams p = Params(15, 0.6);
            p.grit = 0.3f;
            p.cellCloseness = 0.5f;
            p.snowCover = 1f;
            p.gust = 0.4f;
            p.gustDepth = 0.3f;
            ulong a = Hash(RenderConstant(Rate, 42, 10, 1024, p));
            ulong b = Hash(RenderConstant(Rate, 42, 10, 1024, p));
            ulong c = Hash(RenderConstant(Rate, 43, 10, 1024, p));
            Assert.AreEqual(a, b);
            Assert.AreNotEqual(a, c);
        }

        [TestMethod]
        public void TenMinutesAreFiniteWithoutDenormalsBelowMinusThreeDbfsAndAllocateNothing()
        {
            Mixer mixer = new Mixer(Rate, 5);
            float[] buf = new float[1024 * 2];
            Xorshift32 rng = new Xorshift32(99);
            AudioParams p = AudioParams.Create();
            // Warm up the JIT and every path before counting allocations.
            for (int i = 0; i < 200; i++) mixer.Process(buf, 1024, 2, Walk(ref p, ref rng));
            int gen0 = GC.CollectionCount(0);
            long bytes = AllocatedBytes();
            long blocks = 10L * 60 * Rate / 1024;
            double peak = 0;
            long bad = 0, denormals = 0;
            for (long b = 0; b < blocks; b++)
            {
                mixer.Process(buf, 1024, 2, Walk(ref p, ref rng));
                for (int i = 0; i < buf.Length; i++)
                {
                    float v = buf[i];
                    if (float.IsNaN(v) || float.IsInfinity(v)) bad++;
                    float a = Math.Abs(v);
                    if (a != 0f && a < 1.17549435E-38f) denormals++;
                    if (a > peak) peak = a;
                }
            }
            // Read before any assertion: the assertions allocate themselves.
            long allocated = AllocatedBytes() - bytes;
            int collections = GC.CollectionCount(0) - gen0;
            Console.WriteLine("METRIC soak 10 min 48 kHz: peak " + peak.ToString("F4") + " (" + (20 * Math.Log10(peak)).ToString("F2") + " dBFS), NaN " + bad + ", denormals " + denormals
                + ", gen0 collections " + collections + ", bytes allocated " + allocated);
            Assert.AreEqual(0, bad, "NaN or infinite samples");
            Assert.AreEqual(0, denormals, "denormal samples");
            Assert.IsTrue(peak < 0.7079, "peak " + peak + " is not below -3 dBFS");
            Assert.AreEqual(0, collections, "a collection ran during the soak");
            Assert.AreEqual(0L, allocated, "bytes allocated on the audio thread");
        }

        private static long AllocatedBytes()
        {
            return GC.GetAllocatedBytesForCurrentThread();
        }

        // A slow random walk through the whole parameter space, a step per block.
        private static AudioParams Walk(ref AudioParams p, ref Xorshift32 rng)
        {
            p.windSpeed = Step(p.windSpeed, 0, 40, 1.5f, ref rng);
            p.gust = Step(p.gust, -1, 1, 0.3f, ref rng);
            p.gustDepth = Step(p.gustDepth, 0, 1, 0.1f, ref rng);
            p.intensity = Step(p.intensity, 0, 1, 0.1f, ref rng);
            p.grit = Step(p.grit, 0, 1, 0.05f, ref rng);
            p.interior = Step(p.interior, 0, 1, 0.2f, ref rng);
            p.snowCover = Step(p.snowCover, 0, 1, 0.1f, ref rng);
            p.cellCloseness = Step(p.cellCloseness, 0, 1, 0.1f, ref rng);
            p.cellPan = Step(p.cellPan, -1, 1, 0.2f, ref rng);
            p.volume = Step(p.volume, 0.5f, 1, 0.1f, ref rng);
            return p;
        }

        private static float Step(float v, float lo, float hi, float size, ref Xorshift32 rng)
        {
            v += rng.NextSigned() * size;
            return v < lo ? lo : v > hi ? hi : v;
        }

        [TestMethod]
        public void TheLoudestMixRemainsBelowMinusThreeDbfs()
        {
            AudioParams p = AudioParams.Create();
            p.windSpeed = 60; p.intensity = 1; p.cellCloseness = 1; p.gust = 1; p.gustDepth = 1; p.grit = 0.5f;
            float[] r = RenderConstant(Rate, 1, 10, 1024, p);
            double peak = 0;
            foreach (float v in r) peak = Math.Max(peak, Math.Abs(v));
            Console.WriteLine("METRIC loudest mix peak " + peak.ToString("F4") + " (" + (20 * Math.Log10(peak)).ToString("F2") + " dBFS)");
            Assert.IsTrue(peak < 0.7079, "peak " + peak);
        }

        [TestMethod]
        public void TheLevelDoesNotFallAsTheWindRisesFromZeroToThirtyMetresPerSecond()
        {
            double previous = -1;
            for (int i = 0; i < 20; i++)
            {
                double speed = 30.0 * i / 19.0;
                double rms = Rms(RenderConstant(Rate, 3, 6, 1024, Params(speed, 0.2)), Rate * 2);
                Assert.IsTrue(rms >= previous - 1e-9, "RMS fell at " + speed + " m/s: " + rms + " after " + previous);
                previous = rms;
            }
            Console.WriteLine("METRIC wind sweep: 20 points monotone, RMS at 30 m/s " + previous.ToString("F4"));
            Assert.IsTrue(previous > 0.05, "no growth at all");
        }

        [TestMethod]
        public void TheLevelDoesNotFallAsTheIntensityRisesFromZeroToOne()
        {
            double previous = -1;
            for (int i = 0; i < 20; i++)
            {
                double inten = i / 19.0;
                double rms = Rms(RenderConstant(Rate, 3, 6, 1024, Params(6, inten)), Rate * 2);
                Assert.IsTrue(rms >= previous - 1e-9, "RMS fell at intensity " + inten + ": " + rms + " after " + previous);
                previous = rms;
            }
            Console.WriteLine("METRIC intensity sweep: 20 points monotone, RMS at 1.0 " + previous.ToString("F4"));
        }

        [TestMethod]
        public void ThePitchOfTheWindRisesWithItsSpeed()
        {
            double c5 = CentroidOf(Rate, Params(5, 0));
            double c10 = CentroidOf(Rate, Params(10, 0));
            double c20 = CentroidOf(Rate, Params(20, 0));
            double c30 = CentroidOf(Rate, Params(30, 0));
            Console.WriteLine("METRIC centroid Hz at 5/10/20/30 m/s: " + c5.ToString("F0") + " " + c10.ToString("F0") + " " + c20.ToString("F0") + " " + c30.ToString("F0"));
            Assert.IsTrue(c5 < c10 && c10 < c20 && c20 < c30, c5 + " " + c10 + " " + c20 + " " + c30);
            Assert.IsTrue(c30 > 1.5 * c5, "5 m/s " + c5 + " Hz, 30 m/s " + c30 + " Hz");
        }

        [TestMethod]
        public void GustsMakeTheEnvelopeVaryByAtLeastThreeDecibels()
        {
            double with = EnvelopeVariance(0.4f, true);
            double without = EnvelopeVariance(0.0f, true);
            double dB = 10 * Math.Log10(with / without);
            Console.WriteLine("METRIC gust envelope variance ratio dB (depth 0.4 vs 0): " + dB.ToString("F1"));
            Assert.IsTrue(dB >= 3.0, "variance ratio " + dB + " dB");
        }

        // The variance of the 100 ms RMS envelope of one minute of wind at 15 m/s whose gust wave comes from WindNoise.
        private static double EnvelopeVariance(float depth, bool drive)
        {
            Mixer mixer = new Mixer(Rate, 11);
            int block = Rate / 50;
            float[] buf = new float[block * 2];
            AudioParams p = Params(15, 0);
            p.gustDepth = depth;
            List<double> env = new List<double>();
            double acc = 0;
            int count = 0;
            for (int b = 0; b < 50 * 60; b++)
            {
                double t = b / 50.0;
                p.gust = drive ? WindNoise.Wave(t, Vector3.zero, new Vector3(1, 0, 0), 3) : 0f;
                mixer.Process(buf, block, 2, p);
                for (int i = 0; i < buf.Length; i++) acc += (double)buf[i] * buf[i];
                count += buf.Length;
                if ((b + 1) % 5 == 0)
                {
                    if (b > 100) env.Add(Math.Sqrt(acc / count));
                    acc = 0;
                    count = 0;
                }
            }
            double mean = 0;
            foreach (double v in env) mean += v;
            mean /= env.Count;
            double var = 0;
            foreach (double v in env) var += (v - mean) * (v - mean);
            return var / env.Count;
        }

        [TestMethod]
        public void TheOutputDoesNotDependOnTheBlockSize()
        {
            AudioParams p = Params(18, 0.7);
            p.grit = 0.2f; p.cellCloseness = 0.6f; p.cellPan = -0.4f; p.snowCover = 0.5f; p.interior = 0.5f; p.gust = 0.3f; p.gustDepth = 0.3f;
            ulong reference = Hash(RenderConstant(Rate, 9, 5, 256, p));
            foreach (int block in new[] { 1024, 2048, 480, 1, 333 })
            {
                Assert.AreEqual(reference, Hash(RenderConstant(Rate, 9, 5, block, p)), "block " + block);
            }
        }

        [TestMethod]
        public void TheInteriorTakesAtLeastTenDecibelsFromTheHighBand()
        {
            AudioParams outside = Params(20, 0.9);
            AudioParams inside = outside;
            inside.interior = 1f;
            double binHz;
            double[] a = Spectrum(MonoTail(RenderConstant(Rate, 4, 8, 1024, outside), Rate, 2), Rate, 4096, out binHz);
            double[] b = Spectrum(MonoTail(RenderConstant(Rate, 4, 8, 1024, inside), Rate, 2), Rate, 4096, out binHz);
            double drop = 10 * Math.Log10(Energy(a, binHz, 2000, 20000) / Energy(b, binHz, 2000, 20000));
            Console.WriteLine("METRIC interior high band (>2 kHz) drop dB: " + drop.ToString("F1") + ", centroid " + Centroid(a, binHz).ToString("F0") + " -> " + Centroid(b, binHz).ToString("F0") + " Hz");
            Assert.IsTrue(drop >= 10.0, "high band drops by " + drop + " dB");
            Assert.IsTrue(Centroid(b, binHz) < Centroid(a, binHz), "the inside is not duller");
        }

        [TestMethod]
        public void TheWindHasTheSameCentroidAt44100AndAt48000Hertz()
        {
            double a = CentroidOf(48000, Params(15, 0));
            double b = CentroidOf(44100, Params(15, 0));
            Console.WriteLine("METRIC centroid at 15 m/s: 48 kHz " + a.ToString("F0") + " Hz, 44.1 kHz " + b.ToString("F0") + " Hz, difference " + (100 * Math.Abs(a - b) / a).ToString("F2") + " %");
            Assert.IsTrue(Math.Abs(a - b) / a <= 0.03, "48 kHz " + a + " Hz, 44.1 kHz " + b + " Hz");
        }

        [TestMethod]
        public void TheLayersHaveTheirBands()
        {
            double binHz;
            // The hiss lies above 2.5 kHz.
            AudioParams hiss = AudioParams.Create(); hiss.intensity = 1f; hiss.volTicks = 0f;
            double[] s = Spectrum(MonoTail(RenderConstant(Rate, 2, 6, 1024, hiss), Rate, 1), Rate, 4096, out binHz);
            Assert.IsTrue(Energy(s, binHz, 2000, 24000) > 0.9 * Energy(s, binHz, 0, 24000), "hiss band");
            // The rumble lies below 120 Hz plus a little.
            AudioParams rumble = AudioParams.Create(); rumble.cellCloseness = 1f;
            s = Spectrum(MonoTail(RenderConstant(Rate, 2, 8, 1024, rumble), Rate, 1), Rate, 8192, out binHz);
            Assert.IsTrue(Energy(s, binHz, 0, 200) > 0.9 * Energy(s, binHz, 0, 24000), "rumble band");
            // The ticks centre near 3 kHz.
            AudioParams ticks = AudioParams.Create(); ticks.intensity = 1f; ticks.volPrecip = 0f;
            s = Spectrum(MonoTail(RenderConstant(Rate, 2, 30, 1024, ticks), Rate, 1), Rate, 4096, out binHz);
            double centroid = Centroid(s, binHz);
            // A band-pass of noise has more power above its centre than below it in linear frequency, so the centroid sits above 3 kHz.
            Assert.IsTrue(centroid > 2500 && centroid < 5000, "tick centroid " + centroid);
            Assert.IsTrue(Energy(s, binHz, 1500, 6500) > 0.7 * Energy(s, binHz, 0, 24000), "tick band");
            // The grit is brighter than the wind and narrower than white noise.
            AudioParams grit = AudioParams.Create(); grit.intensity = 1f; grit.grit = 1f; grit.volTicks = 0f;
            s = Spectrum(MonoTail(RenderConstant(Rate, 2, 8, 1024, grit), Rate, 1), Rate, 4096, out binHz);
            Assert.IsTrue(Centroid(s, binHz) > 1500, "grit centroid " + Centroid(s, binHz));
        }

        [TestMethod]
        public void TicksComeAtAboutFortyPerSecondAndNeverFillMoreThanTheVoicePool()
        {
            HullTicks t = new HullTicks(Rate, 8);
            float l, r;
            for (int i = 0; i < Rate * 60; i++) t.Next(1.0, out l, out r);
            double perSecond = t.Started / 60.0;
            Assert.IsTrue(perSecond > 32 && perSecond < 48, "ticks per second " + perSecond);
            HullTicks dense = new HullTicks(Rate, 8);
            int most = 0;
            for (int i = 0; i < Rate * 2; i++)
            {
                dense.Next(500.0, out l, out r);
                most = Math.Max(most, dense.ActiveVoices);
            }
            Assert.AreEqual(HullTicks.MaxVoices, most);
        }

        [TestMethod]
        public void AHigherIntensityStartsASupersetOfTheTicks()
        {
            HullTicks low = new HullTicks(Rate, 8), high = new HullTicks(Rate, 8);
            float l, r;
            for (int i = 0; i < Rate * 20; i++)
            {
                low.Next(0.3, out l, out r);
                high.Next(0.6, out l, out r);
            }
            Assert.IsTrue(high.Started > low.Started);
        }

        [TestMethod]
        public void TheRecordedTexturePathPlaysAndMixesWithTheSynthesisedBed()
        {
            float[] texture = new float[Rate * 3];
            Xorshift32 rng = new Xorshift32(77);
            for (int i = 0; i < texture.Length; i++) texture[i] = rng.NextSigned() * 0.3f * (float)(1 + Math.Sin(i * 0.0002));
            AudioParams p = Params(15, 0);
            Mixer synth = new Mixer(Rate, 6);
            ulong hSynth = Hash(RenderConstant(Rate, 6, 4, 1024, p, synth));
            Mixer rec = new Mixer(Rate, 6);
            rec.SetRecordedBed(texture, Rate);
            p.bedSource = BedSource.Recorded;
            float[] recorded = RenderConstant(Rate, 6, 4, 1024, p, rec);
            Mixer mix = new Mixer(Rate, 6);
            mix.SetRecordedBed(texture, Rate);
            p.bedSource = BedSource.Mix;
            float[] mixed = RenderConstant(Rate, 6, 4, 1024, p, mix);
            Assert.AreNotEqual(hSynth, Hash(recorded));
            Assert.AreNotEqual(Hash(recorded), Hash(mixed));
            Assert.IsTrue(Rms(recorded, Rate) > 0.01, "recorded bed is silent");
            Assert.IsTrue(Rms(mixed, Rate) > 0.01, "mixed bed is silent");
            // Without a texture the recorded setting plays the synthesised bed.
            p.bedSource = BedSource.Recorded;
            Assert.AreEqual(hSynth, Hash(RenderConstant(Rate, 6, 4, 1024, p, new Mixer(Rate, 6))));
        }

        [TestMethod]
        public void BadParametersAndOddFormatsDoNotBreakTheMixer()
        {
            AudioParams p = AudioParams.Create();
            p.windSpeed = float.NaN; p.gust = float.PositiveInfinity; p.intensity = -4; p.interior = 7; p.cellCloseness = float.NaN;
            foreach (int rate in new[] { 8000, 22050, 44100, 96000 })
            {
                float[] r = RenderConstant(rate, 1, 1, 777, p);
                foreach (float v in r) Assert.IsFalse(float.IsNaN(v) || float.IsInfinity(v));
            }
            float[] mono = new float[512];
            new Mixer(Rate, 1).Process(mono, 512, 1, Params(10, 0.5));
            float[] six = new float[512 * 6];
            new Mixer(Rate, 1).Process(six, 512, 6, Params(10, 0.5));
            for (int f = 0; f < 512; f++)
            {
                Assert.AreEqual(mono[f], 0.5f * (six[f * 6] + six[f * 6 + 1]), 1e-6);
                Assert.AreEqual(six[f * 6 + 2], six[f * 6 + 5]);
            }
        }

        [TestMethod]
        public void TheMapGivesTheSoundLevelColumnAndTheTypeOfTheFall()
        {
            Assert.AreEqual(0f, AudioMapping.SoundLevel(0f));
            Assert.AreEqual(0.05f, AudioMapping.SoundLevel(0.1f), 1e-6);
            Assert.AreEqual(0.3f, AudioMapping.SoundLevel(0.4f), 1e-6);
            Assert.AreEqual(0.55f, AudioMapping.SoundLevel(0.6f), 1e-6);
            Assert.AreEqual(1f, AudioMapping.SoundLevel(1f), 1e-6);
            float previous = 0;
            for (int i = 0; i <= 100; i++)
            {
                float s = AudioMapping.SoundLevel(i / 100f);
                Assert.IsTrue(s >= previous);
                previous = s;
            }
            Assert.IsTrue(AudioMapping.IsHeard(PrecipType.Snow));
            Assert.IsTrue(AudioMapping.IsHeard(PrecipType.Dust));
            Assert.IsFalse(AudioMapping.IsHeard(PrecipType.Rain));
            Assert.AreEqual(1f, AudioMapping.GritFor(PrecipType.Sand));
            Assert.AreEqual(0f, AudioMapping.GritFor(PrecipType.Snow));
            AudioParams p = AudioParams.Create();
            AudioMapping.Fill(ref p, PrecipType.Snow, 1f, 20f, 0.5f);
            Assert.AreEqual(20f, p.windSpeed);
            Assert.AreEqual(0.5f, p.gust);
            Assert.AreEqual(WindNoise.Depth(1f), p.gustDepth);
            Assert.AreEqual(1f, p.intensity);
            AudioMapping.Fill(ref p, PrecipType.Rain, 1f, 20f, 0f);
            Assert.AreEqual(0f, p.intensity);
        }

        [TestMethod]
        public void TheWholeMixerCostsLittleNextToAReferenceLoop()
        {
            // The reference is the plan's own cost estimate: ten band-pass sections on two channels at 48 kHz, one second of audio.
            // Built for release it takes 0.13 % of one core (RenderWeatherAudio --bench), so 1 % of a core is 7.5 times the reference;
            // the worst case of the mixer (every layer on, interior half) must stay within that ratio, in whatever build the test runs.
            AudioParams p = Params(20, 1);
            p.grit = 0.5f; p.cellCloseness = 1; p.snowCover = 1; p.interior = 0.5f; p.gust = 0.5f; p.gustDepth = 0.4f;
            Mixer mixer = new Mixer(Rate, 3);
            float[] buf = new float[Rate * 2];
            mixer.Process(buf, Rate, 2, p);
            Biquad[] bank = new Biquad[20];
            for (int i = 0; i < bank.Length; i++) { bank[i] = new Biquad(); bank[i].BandPass(Rate, 300 + 100 * i, 1.0); }
            double sink = 0;
            double mixerBest = double.MaxValue, referenceBest = double.MaxValue;
            for (int rep = 0; rep < 7; rep++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                mixer.Process(buf, Rate, 2, p);
                sw.Stop();
                mixerBest = Math.Min(mixerBest, sw.Elapsed.TotalMilliseconds);
                Xorshift32 rng = new Xorshift32(1);
                sw.Restart();
                for (int f = 0; f < Rate; f++)
                {
                    double x = rng.NextSigned();
                    for (int k = 0; k < bank.Length; k++) x = bank[k].Process(x);
                    sink += x;
                }
                sw.Stop();
                referenceBest = Math.Min(referenceBest, sw.Elapsed.TotalMilliseconds);
            }
            Assert.IsFalse(double.IsNaN(sink));
            double ratio = mixerBest / referenceBest;
            Console.WriteLine("mixer " + mixerBest.ToString("F2") + " ms per second of audio (" + (mixerBest / 10).ToString("F2")
                + " % of one core), reference " + referenceBest.ToString("F2") + " ms, ratio " + ratio.ToString("F2"));
            Assert.IsTrue(ratio <= 7.5, "ratio " + ratio);
        }
    }
}
