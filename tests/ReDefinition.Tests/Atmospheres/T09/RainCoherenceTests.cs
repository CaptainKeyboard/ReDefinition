using System;
using System.Linq;
using AtmoSim.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;
using ReDefinition.Tests.Atmospheres.T10;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // Rain and its clouds agree (E40 look-correct): precipitation falls only under a cloud that draws as a rain-bearing one, and a rain-bearing cloud is drawn only where it rains.
    // The rain-bearing clouds of Kerbin are the weather deck (Kerbin-weather-rain) and the cumulonimbus layer; both carry the WeatherDeckRule of the shipped cfg. The maps are the
    // weather generator's own (precipitation level, the layers' coverage as the module reads it: LayerCoupling.Coverage at the layer's mid height), 8 times over the year, the rule
    // applied exactly as OwnCloudModule.FetchStep applies it.
    [TestClass]
    public class RainCoherenceTests
    {
        private const int Nx = 256, Ny = 128, Times = 8;
        // The coverage byte above which a layer draws a cloud for certain (its threshold 0.5).
        private const int Drawn = 128;
        // The level from which rain is heavy (the HUD band): there the deck is always drawn. Under the light and moderate rain of a weather system's halo the deck is broken (its floor rises with the
        // intensity, deckRainFloorFullAt; E40 L5 maps), because the generator rains over about half of the planet in one connected area and a deck under all of it is one sheet of 1800 km.
        private const float HeavyFrom = 0.5f;

        private sealed class Table
        {
            // rows: precipitation band 0 none (< 0.04), 1 flurries (< 0.08), 2 light (< 0.2), 3 moderate (< 0.5), 4 heavy; columns: nothing | deck only | Cb only | both
            internal readonly long[,] Joint = new long[5, 4];
            internal long RainNoCloud, HeavyNoCloud, CbNoRain, CbTexels, DeckNoRainNoFog, Rain;
        }

        private static int Band(float level)
        {
            return level < PrecipScale.FlurriesFrom ? 0 : level < 0.08f ? 1 : level < PrecipScale.LightFrom ? 2 : level < 0.5f ? 3 : 4;
        }

        private static Table Measure(bool shipped)
        {
            var layers = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers;
            var deckSpec = layers.Single(l => l.Name == "Kerbin-weather-rain");
            var cbSpec = layers.Single(l => l.Name == "Kerbin-cumulonimbus");
            // the rules as shipped, or the rules of before this package (no floor on the deck, no rule on the cumulonimbus layer)
            WeatherDeckRule deckRule = shipped ? deckSpec.Deck : new WeatherDeckRule();
            WeatherDeckRule cbRule = shipped ? cbSpec.Deck : null;
            double deckMid = 0.5 * (deckSpec.InnerAltitudeM + deckSpec.OuterAltitudeM), cbMid = 0.5 * (cbSpec.InnerAltitudeM + cbSpec.OuterAltitudeM);
            GeneratorField field = FieldFixture.Field();
            GeneratorState state = field.StateOf(FieldFixture.BodyIndex);
            WeatherGenerator g = state.Generator;
            LayerOptions options = LayerWindOptions.Layers();
            int n = Nx * Ny;
            var level = new float[n];
            var spread = new float[n];
            var wind = new float[n];
            var deckCover = new byte[n];
            var cbCover = new byte[n];
            var oc = new byte[n];
            var ot = new byte[n];
            var cc = new byte[n];
            var ct = new byte[n];
            var t = new Table();
            for (int s = 0; s < Times; s++)
            {
                double ut = 1.0e6 + s * 3.5e5 * 3.0 + s * 7777.0;
                for (int j = 0; j < Ny; j++)
                {
                    double lat = (90.0 - (j + 0.5) * 180.0 / Ny) * (Math.PI / 180.0);
                    for (int i = 0; i < Nx; i++)
                    {
                        double lon = (-180.0 + (i + 0.5) * 360.0 / Nx) * (Math.PI / 180.0);
                        Weather w;
                        g.Sample(lat, lon, 0.0, ut, out w);
                        int k = j * Nx + i;
                        level[k] = PrecipScale.LevelFromRate((float)w.PrecipMmH, state.Bands);
                        spread[k] = WeatherDeckRule.SpreadK((float)w.SurfaceTempK, (float)w.Humidity);
                        wind[k] = (float)w.WindSpeed;
                        deckCover[k] = (byte)Math.Round(255.0 * LayerCoupling.Coverage(g, lat, lon, ut, deckMid, options));
                        cbCover[k] = (byte)Math.Round(255.0 * LayerCoupling.Coverage(g, lat, lon, ut, cbMid, options));
                    }
                }
                deckRule.Apply(deckCover, level, spread, wind, oc, ot);
                if (cbRule != null) cbRule.Apply(cbCover, level, spread, wind, cc, ct);
                else Array.Copy(cbCover, cc, n);
                for (int k = 0; k < n; k++)
                {
                    bool deck = oc[k] > Drawn, cb = cc[k] > Drawn;
                    float p = level[k];
                    t.Joint[Band(p), (deck ? 1 : 0) + (cb ? 2 : 0)]++;
                    t.CbTexels += cb ? 1 : 0;
                    if (p >= 0.08f)
                    {
                        t.Rain++;
                        if (!deck && !cb) t.RainNoCloud++;
                        if (!deck && !cb && p >= HeavyFrom) t.HeavyNoCloud++;
                    }
                    // a rain-bearing cloud with any real thickness over dry air
                    if (p < PrecipScale.FlurriesFrom && cc[k] > 0) t.CbNoRain++;
                    if (p < PrecipScale.FlurriesFrom && oc[k] > Drawn && spread[k] > 4.5f) t.DeckNoRainNoFog++;
                }
            }
            return t;
        }

        private static void Print(string name, Table t)
        {
            string[] rows = { "no rain (<0.04)     ", "flurries (<0.08)    ", "light (<0.20)       ", "moderate (<0.50)    ", "heavy              " };
            Console.WriteLine("RAINCOHERENCE " + name + " (texels; columns: nothing drawn | deck only | cumulonimbus only | both)");
            for (int b = 0; b < 5; b++)
                Console.WriteLine("RAINCOHERENCE   " + rows[b] + t.Joint[b, 0] + " | " + t.Joint[b, 1] + " | " + t.Joint[b, 2] + " | " + t.Joint[b, 3]);
            Console.WriteLine("RAINCOHERENCE   rain (level >= 0.08) " + t.Rain + ", of which under no rain-bearing cloud " + t.RainNoCloud + "; cumulonimbus texels over dry air " + t.CbNoRain
                + "; deck over dry, not foggy air " + t.DeckNoRainNoFog);
        }

        [TestMethod]
        public void RainFallsOnlyUnderARainCloudAndARainCloudDrawsOnlyWhereItRains()
        {
            Table before = Measure(false);
            Print("before", before);
            Table now = Measure(true);
            Print("shipped", now);
            Assert.IsTrue(before.RainNoCloud > 0 && before.CbNoRain > 0, "the field has both mismatches without the rules");
            // the light and moderate rain of a halo may have a broken deck; the share is in the printed table (rain ... of which under no rain-bearing cloud)
            Assert.AreEqual(0L, now.HeavyNoCloud, "no heavy precipitation (level 0.5 and up) without a drawn rain-bearing cloud over it");
            Assert.AreEqual(0L, now.CbNoRain, "no cumulonimbus coverage over air without precipitation");
            Assert.AreEqual(0L, now.DeckNoRainNoFog, "the weather deck over dry air only where it is fog air");
        }
    }
}
