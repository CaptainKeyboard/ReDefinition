using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // The cost of the bake on the CPU of the test machine, with a real LayerStackSource (maps as WeatherMaps, the type table, optionally the height
    // variation of T13 with its byte maps): the figures the in-game rows (T18.bake_ms_total, T18.tick_ms_worst) are read against. Offline numbers carry the
    // machine's speed (a Debug build of the tests), not the game's.
    [TestClass]
    public class RtBakeCostTests
    {
        private static LayerStackSource FourLayers(bool heights)
        {
            var source = new LayerStackSource(1, PackScenes.Kerbin, true);
            double[][] bands = { new[] { 1200.0, 3200.0, 2.0 }, new[] { 3500.0, 5500.0, 1.2 }, new[] { 7000.0, 8200.0, 0.6 }, new[] { 9500.0, 10500.0, 0.3 } };
            for (int i = 0; i < bands.Length; i++)
            {
                var layer = new CloudLayerDef("l" + i, 1, (float)bands[i][0], (float)bands[i][1], new[] { CloudTypeDef.Slab("s", 0f, 1f) },
                                              (float)(bands[i][2] / (bands[i][1] - bands[i][0])));
                layer.Coverage = 0.5f;
                var cover = new WeatherMap();
                cover.Resize(512, 256);
                Func<double, double, double> noise = PackScenes.Noise((uint)(3 + i), 10);
                for (int j = 0; j < 256; j++)
                    for (int k = 0; k < 512; k++)
                    {
                        double lat, lon;
                        RtCloudPackLayout.TexelLatLon(k, j, 512, 256, out lat, out lon);
                        cover.Values[j * 512 + k] = (float)noise(lat, lon);
                    }
                var type = new WeatherMap();
                type.Resize(512, 256);
                int at = source.Add(layer, cover, type);
                if (!heights) continue;
                var tuning = new HeightTuning();
                var maps = new HeightMaps(128, 64, tuning.RootRangeM, tuning.TopRangeM, tuning.VariationRangeM);
                Func<double, double, double> offsets = PackScenes.Noise((uint)(40 + i), 6);
                for (int j = 0; j < 64; j++)
                    for (int k = 0; k < 128; k++)
                    {
                        double lat = 90.0 - (j + 0.5) * 180.0 / 64, lon = -180.0 + (k + 0.5) * 360.0 / 128;
                        for (int c = 0; c < 3; c++) maps.Bytes[(j * 128 + k) * 3 + c] = (byte)Math.Max(0, Math.Min(255, (int)(offsets(lat, lon + 7 * c) * 255.0)));
                    }
                source.SetHeights(at, tuning, maps, 1u);
            }
            return source;
        }

        private static double Time(Action work)
        {
            var clock = Stopwatch.StartNew();
            work();
            return clock.Elapsed.TotalMilliseconds;
        }

        [TestMethod]
        public void TheBakeOfAFourLayerStackIsBoundedAndThePerFrameBudgetHolds()
        {
            foreach (bool heights in new[] { false, true })
            {
                LayerStackSource stack = FourLayers(heights);
                LayerTypeTable[] tables = stack.Tables();
                foreach (int stride in new[] { 1, 2 })
                {
                    var baker = new RtCloudTauBaker(stack, tables, 4, 512, 256, 8, stride);
                    double wholeMs = Time(() => Assert.IsTrue(baker.Step(int.MaxValue)));
                    Console.WriteLine("bake of 4 layers at 512 x 256, 8 steps, " + (heights ? "with" : "without") + " height variation, stride " + stride + ": "
                                      + wholeMs.ToString("0.0") + " ms in one call");
                }
                var driver = new RtCloudPackDriver();
                RtCloudPackSettings settings = RtCloudPackSettings.From(true, 0, (key, value) => value);
                int ticks = 0;
                while (driver.Current == null && ticks < 200000)
                {
                    driver.Tick(stack, tables, settings);
                    ticks++;
                }
                Console.WriteLine("through the driver at " + settings.BudgetMs + " ms a tick (" + (heights ? "with" : "without") + " height variation): " + ticks
                                  + " ticks, last bake " + driver.Stats.LastBakeMs.ToString("0.0") + " ms, worst tick " + driver.Stats.WorstTickMs.ToString("0.00")
                                  + " ms; pack " + RtCloudPackLayout.Bytes(512, 256, 4) / (1024 * 1024) + " MB");
                Assert.IsNotNull(driver.Current);
                Assert.IsTrue(driver.Stats.WorstTickMs < settings.BudgetMs * 8 + 5, "a tick overshoots its budget by one check interval (64 texels) at most");
            }
        }

        [TestMethod]
        public void TheHeightVariationAgreesWithTheShadowAtStrideOneAndTwo()
        {
            LayerStackSource stack = FourLayers(true);
            LayerTypeTable[] tables = stack.Tables();
            // The widened tight interval of a layer with height variation is several times the cloud's thickness, so the shadow's midpoint rule over it is coarse
            // at its default 8 steps a span. The reference here is the same rule at 64 steps (the converged value); the shadow at 8 steps against it is printed.
            CloudShadowField field = new CloudShadowField(stack, tables) { StepsPerSpan = 64, DiscSamples = 1 };
            CloudShadowField coarse = new CloudShadowField(stack, tables) { StepsPerSpan = 8, DiscSamples = 1 };
            var coarseDiffs = new List<double>();
            foreach (int stride in new[] { 1, 2 })
            {
                var baker = new RtCloudTauBaker(stack, tables, 4, 512, 256, 8, stride);
                baker.Step(int.MaxValue);
                var entries = new RtCloudLayerEntry[4];
                for (int i = 0; i < 4; i++) entries[i] = RtCloudPackDriver.EntryFor(stack, i);
                var pack = new RtCloudPack(1, stack.BodyRadiusM, true, 512, 256, entries, baker.Data, stack.Version);
                var diffs = new List<double>();
                for (double lat = -20; lat < 20; lat += 2.9)
                    for (double lon = -30; lon < 30; lon += 0.53)
                    {
                        double la = lat * Math.PI / 180.0, lo = lon * Math.PI / 180.0, r = stack.BodyRadiusM;
                        double x = Math.Cos(la) * Math.Cos(lo) * r, y = Math.Cos(la) * Math.Sin(lo) * r, z = Math.Sin(la) * r;
                        double sx, sy, sz;
                        LatLonSource.SunTowards(lat, lon, 45.0, 90.0, out sx, out sy, out sz);
                        double t04 = field.DirectTransmission(x, y, z, sx, sy, sz);
                        if (t04 > 0.05 && t04 < 0.95)
                        {
                            diffs.Add(PackScenes.Relative(RtCloudRayMath.LightTransmission(pack, x, y, z, sx, sy, sz), t04));
                            if (stride == 1) coarseDiffs.Add(PackScenes.Relative(coarse.DirectTransmission(x, y, z, sx, sy, sz), t04));
                        }
                    }
                diffs.Sort();
                if (stride == 1)
                {
                    coarseDiffs.Sort();
                    Console.WriteLine("the shadow at 8 steps a span against its 64-step value: median " + (100 * coarseDiffs[coarseDiffs.Count / 2]).ToString("0.00") + " % p90 "
                                      + (100 * coarseDiffs[(int)(coarseDiffs.Count * 0.9)]).ToString("0.00") + " % worst " + (100 * coarseDiffs[coarseDiffs.Count - 1]).ToString("0.00") + " %");
                }
                Assert.IsTrue(diffs.Count > 50, "places in the band: " + diffs.Count);
                double p90 = diffs[(int)(diffs.Count * 0.9)], worst = diffs[diffs.Count - 1];
                Console.WriteLine("height variation on, four layers, sun 45 deg, stride " + stride + ": n " + diffs.Count + " median " + (100 * diffs[diffs.Count / 2]).ToString("0.00")
                                  + " % p90 " + (100 * p90).ToString("0.00") + " % worst " + (100 * worst).ToString("0.00") + " %");
                // [proposed] from the first reference run of this scene (stride 1: p90 10.1 %, worst 58 %); never loosened.
                Assert.IsTrue(p90 <= 0.12, "stride " + stride + ": p90 " + p90);
                Assert.IsTrue(worst <= 0.65, "stride " + stride + ": worst " + worst);
            }
        }
    }
}
