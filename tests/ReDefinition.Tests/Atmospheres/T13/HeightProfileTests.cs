using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests
{
    // Where the time of one height build goes (K-T2 start 42: 440 to 519 ms on the game's main thread). Prints per phase the best of a few
    // runs on a realistic input (the generator's Kerbin-like body); asserts nothing about time, only that the phases ran.
    [TestClass]
    public class HeightProfileTests
    {
        private const int W = 512, H = 256, Layers = 4;
        private const double R = 600000.0;

        private static double Ms(Action a, int runs = 3)
        {
            double best = double.MaxValue;
            for (int i = 0; i < runs; i++)
            {
                var w = Stopwatch.StartNew();
                a();
                best = Math.Min(best, w.Elapsed.TotalMilliseconds);
            }
            return best;
        }

        [TestMethod]
        [TestCategory("Profile")]
        public void ProfileOneHeightBuildPerPhase()
        {
            GeneratorField field = FieldFixture.Field();
            const double ut = FieldFixture.HomeYear * 0.0 + 1234567.0;
            var t = new HeightTuning { Seed = 7u };
            var map = new WeatherMap();
            double coverage = Ms(() => field.GetMap(FieldFixture.BodyIndex, WeatherMapKind.Coverage, ut, map));
            Console.WriteLine("GetMap (one 512x256 copy): " + coverage.ToString("0.00") + " ms");

            HeightInputs input = null;
            double read = Ms(() => input = HeightFieldInputs.Read(field, FieldFixture.BodyIndex, ut, W, H, 1500.0));
            Console.WriteLine("HeightFieldInputs.Read (3 maps + 64x32 field samples + bilinear spread): " + read.ToString("0.0") + " ms");

            var coarse = new WeatherMap();
            coarse.Resize(64, 32);
            double samples = Ms(() =>
            {
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        WeatherSample s;
                        field.Sample(FieldFixture.BodyIndex, 90.0 - (y + 0.5) / 32 * 180.0, (x + 0.5) / 64 * 360.0 - 180.0, 1500.0, ut, out s);
                    }
            });
            Console.WriteLine("  2048 field samples: " + samples.ToString("0.0") + " ms");
            double spread = Ms(() =>
            {
                var up = new float[W * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++) up[y * W + x] = coarse.Sample(90.0 - (y + 0.5) / H * 180.0, (x + 0.5) / W * 360.0 - 180.0);
            });
            Console.WriteLine("  bilinear spread of the updraft to 512x256: " + spread.ToString("0.0") + " ms");

            double build = Ms(() => HeightMapBuilder.Build(input, t, R, 7u));
            Console.WriteLine("HeightMapBuilder.Build (one layer, everything on): " + build.ToString("0.0") + " ms");
            double noise = Ms(() => HeightMapBuilder.SphereNoise(W, H, R, t.VariationWavelengthM, 7u));
            Console.WriteLine("  SphereNoise: " + noise.ToString("0.0") + " ms");
            float[] n = HeightMapBuilder.SphereNoise(W, H, R, t.VariationWavelengthM, 7u);
            double std = Ms(() => HeightMapBuilder.Standardise(n, H, W));
            Console.WriteLine("  Standardise: " + std.ToString("0.0") + " ms");
            double near = Ms(() => HeightMapBuilder.NeighbourhoodMean(input.Coverage, W, H, R, t.SizeRadiusM));
            Console.WriteLine("  NeighbourhoodMean: " + near.ToString("0.0") + " ms");
            double dew = Ms(() =>
            {
                double sum = 0.0;
                for (int i = 0; i < W * H; i++) sum += HeightModel.DewPointSpreadK(input.TemperatureK[i], input.Humidity[i]);
                GC.KeepAlive(sum);
            });
            Console.WriteLine("  dew point per cell: " + dew.ToString("0.0") + " ms");

            var source = new LayerStackSource(FieldFixture.BodyIndex, R, true);
            for (int l = 0; l < Layers; l++)
                source.Add(new CloudLayerDef("l" + l, FieldFixture.BodyIndex, 1000f + 2500f * l, 2500f + 2500f * l, new[] { CloudTypeDef.Slab("s", 0f, 1f) }, 0.01f), null, null);
            double full = Ms(() => HeightDriver.Update(source, field, t, ut, 99u, W, H));
            Console.WriteLine("HeightDriver.Update (" + Layers + " layers): " + full.ToString("0.0") + " ms");
            Assert.IsTrue(full > 0.0);

            // The job: the main thread's calls and the worker's time, first build (noise made) and second (noise cached).
            var job = new HeightJob { Cache = new HeightNoiseCache(16) };
            for (int round = 0; round < 2; round++)
            {
                double mainMs = 0.0, worst = 0.0;
                var wait = Stopwatch.StartNew();
                long t0 = Stopwatch.GetTimestamp();
                job.Begin(HeightPlan.Make(source, t, ut, 99u, W, H), field, source);
                double dt = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                mainMs += dt;
                worst = Math.Max(worst, dt);
                HeightResult result;
                while (true)
                {
                    t0 = Stopwatch.GetTimestamp();
                    job.Pump(0.8);
                    HeightTake take = job.TryTake(out result);
                    dt = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    mainMs += dt;
                    worst = Math.Max(worst, dt);
                    if (take != HeightTake.None) break;
                    System.Threading.Thread.Sleep(1);
                }
                t0 = Stopwatch.GetTimestamp();
                result.ApplyTo(source);
                dt = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                Console.WriteLine("job round " + round + ": worker " + result.ElapsedMs.ToString("0.0") + " ms, main thread calls " + mainMs.ToString("0.00")
                                  + " ms (worst " + worst.ToString("0.00") + "), apply " + dt.ToString("0.00") + " ms");
            }
            double pack = Ms(() => map.PackBytes(0f, 1f));
            Console.WriteLine("PackBytes 512x256: " + pack.ToString("0.00") + " ms");
        }
    }
}
