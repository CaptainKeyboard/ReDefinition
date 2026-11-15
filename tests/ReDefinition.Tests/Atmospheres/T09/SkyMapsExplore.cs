using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B13;
using ReDefinition.Tests.Atmospheres.C25;
using ReDefinition.Tests.Atmospheres.E41;
using ReDefinition.Tests.Atmospheres.T10;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 F1: the maps the GAME hands to the layer pass for Kerbin at given times, with the game's own seed, Kerbin's shipped rules and the module's FetchStep chain (the deck rule with
    // its fog air, the meso stage), dumped as raw bytes for tools/weather_metrics/e40f1.py and tools/e40_sky_clear.py. Runs only when SKYMAPS_OUT names a directory.
    //   SKYMAPS_UTS   the universal times, comma separated (default 1881000,1686000)
    //   SKYMAPS_SEED  the body's seed (default 664216370, the game log's "body Kerbin ... seed")
    // Per time: shared cover (the field), precipitation, humidity; per layer of the stack: the generator's own layer map (raw) and the cover and type after the module's rules (cover, type).
    [TestClass]
    public class SkyMapsExplore
    {
        private static WeatherMap LayerMap(GeneratorField field, int body, double ut, double heightM)
        {
            var map = new WeatherMap();
            field.GetLayerCoverage(body, ut, heightM, map);
            field.Maps.RenderLayerNow(body, (int)Math.Round(Math.Max(0.0, heightM) / GeneratorMaps.LayerStepM));
            Assert.IsTrue(field.GetLayerCoverage(body, ut, heightM, map));
            map.PackBytes(0f, 1f);
            return map;
        }

        internal static GeneratorField KerbinField(int seed, out BodyClimate climate)
        {
            BodyFacts facts = FieldFixture.HomeFacts("Kerbin");
            BodyClimate parsed = CloudLayerSchemaTests.ParseKerbin().Climate;
            climate = new BodyClimate("Kerbin", FieldFixture.BodyIndex, 600000.0, FieldFixture.HomeYear, 21549.425, BodyStatus.Full, seed,
                parsed.Seasons, parsed.Temperature, parsed.Precip, parsed.Covers, parsed.Ice, parsed.Generator, parsed.Clouds, parsed.Look, parsed.Sound, parsed.Wind, parsed.Gameplay,
                parsed.Aurora, parsed.LightMedium, parsed.Frost, parsed.Forecast);
            var field = new GeneratorField();
            field.Say = s => { };
            field.Maps.Synchronous = true;
            field.SetState(FieldFixture.State(climate, FieldFixture.Params(climate, facts, "Kerbin")));
            return field;
        }

        private static void Dump(string folder, string name, byte[] data)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".u8"), data);
        }

        // SKYMAPS_TABLES=<file>: the model's shares per layer, per map value byte (rows) and type value in 1/16ths (columns): "<layer> visible|closed" then 256 lines of 17 numbers.
        [TestMethod, TestCategory("Explore")]
        public void DumpTheSkyClearTables()
        {
            string file = Environment.GetEnvironmentVariable("SKYMAPS_TABLES");
            if (string.IsNullOrEmpty(file)) return;
            // SKYMAPS_MEANING=1: the cover meaning on (a map value is the closed share looking up): the tables of what that would draw
            bool meaning = Environment.GetEnvironmentVariable("SKYMAPS_MEANING") == "1";
            if (meaning) Tuning.SetOverride(CloudSoftDensity.TuneCoverMeaning, 1f);
            Tuning.Instance.BeginFrame(9000001);
            using (var w = new StreamWriter(file))
            {
                foreach (CloudLayerDef l in TypeMixFixture.ShippedLayers())
                {
                    var table = new LayerTypeTable(l);
                    var visible = new double[256, 17];
                    var closed = new double[256, 17];
                    for (int c = 0; c < 256; c++)
                        for (int t = 0; t <= 16; t++) SkyClearModel.Shares(l, table, c / 255.0, t / 16.0, out visible[c, t], out closed[c, t]);
                    foreach (string kind in new[] { "visible", "closed" })
                    {
                        w.WriteLine(l.Name + " " + kind);
                        for (int c = 0; c < 256; c++)
                        {
                            var cells = new string[17];
                            for (int t = 0; t <= 16; t++) cells[t] = (kind == "visible" ? visible[c, t] : closed[c, t]).ToString("0.###", CultureInfo.InvariantCulture);
                            w.WriteLine(string.Join(" ", cells));
                        }
                    }
                }
            }
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9000002);
        }

        [TestMethod, TestCategory("Explore")]
        public void DumpTheGameMapsOfKerbin()
        {
            string folder = Environment.GetEnvironmentVariable("SKYMAPS_OUT");
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            string utText = Environment.GetEnvironmentVariable("SKYMAPS_UTS") ?? "1881000,1686000";
            string seedText = Environment.GetEnvironmentVariable("SKYMAPS_SEED");
            int seed = string.IsNullOrEmpty(seedText) ? 664216370 : int.Parse(seedText, CultureInfo.InvariantCulture);
            LayerWindOptions.SteeringOverride = LayerSteeringMode.MapsOnly;
            try
            {
                BodyClimate climate;
                GeneratorField field = KerbinField(seed, out climate);
                // the stack as the game builds it from the shipped cfg (rain deck, cumulus, cumulonimbus, cirrus; the snow deck is the rain deck's alternative)
                CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
                int body = FieldFixture.BodyIndex;
                foreach (string part in utText.Split(','))
                {
                    double ut = double.Parse(part, CultureInfo.InvariantCulture);
                    string tag = "u" + ((long)ut).ToString(CultureInfo.InvariantCulture) + "_";
                    TypeMixFixture.Shared s = TypeMixFixture.Maps(field, body, ut);
                    WeatherSample site;
                    field.Sample(body, -0.0972, -74.5577, 264.0, ut, out site);
                    Console.WriteLine("SITE ut " + ut + ": precipitation level " + site.Level01.ToString("0.000", CultureInfo.InvariantCulture) + ", cover " + site.CloudCover01.ToString("0.000", CultureInfo.InvariantCulture)
                        + ", tau " + site.OpticalDepthVertical.ToString("0.00", CultureInfo.InvariantCulture));
                    var temperature = new WeatherMap();
                    var wind = new WeatherMap();
                    Assert.IsTrue(field.GetMap(body, WeatherMapKind.SurfaceTemperature, ut, temperature));
                    Assert.IsTrue(field.GetMap(body, WeatherMapKind.WindSpeed, ut, wind));
                    int n = s.Cover.Values.Length;
                    var spread = new float[n];
                    for (int i = 0; i < n; i++) spread[i] = WeatherDeckRule.SpreadK(temperature.Values[i], s.Humidity.Values[i]);
                    Dump(folder, tag + "sharedCover", s.Cover.Bytes);
                    Dump(folder, tag + "precip", s.Precip.Bytes);
                    Dump(folder, tag + "humidity", s.Humidity.Bytes);
                    var combined = new byte[layers.Length][];
                    var finalTypes = new byte[layers.Length][];
                    var decks = new WeatherDeckRule[layers.Length];
                    for (int li = 0; li < layers.Length; li++)
                    {
                        CloudLayerDef l = layers[li];
                        decks[li] = l.Deck;
                        // SKYMAPS_FALLBACK=1: the generator has made no layer map (GetLayerCoverage false): FetchStep gives every layer the shared coverage map and no mid-level sheet
                        bool fallback = Environment.GetEnvironmentVariable("SKYMAPS_FALLBACK") == "1";
                        WeatherMap raw = fallback ? s.Cover : LayerMap(field, body, ut, CloudBridgeStack.MidHeightM(l));
                        byte[] cover = raw.Bytes, type;
                        byte[] altoMid = null, altoLow = null;
                        if (l.Deck != null)
                        {
                            var rc = new byte[n];
                            var rt = new byte[n];
                            l.Deck.Apply(cover, s.Precip.Values, spread, wind.Values, rc, rt);
                            cover = rc;
                            type = rt;
                        }
                        else
                        {
                            byte[] scratch = null, altoScratch = null;
                            type = CloudLookTuning.TypeBytes(s.Precip.Bytes, l.FairType, -1f, ref scratch, l.RainType);
                            if (l.AltoType >= 0f && !fallback)
                            {
                                altoMid = LayerMap(field, body, ut, l.TypeMidHeightM(l.AltoType)).Bytes;
                                altoLow = LayerMap(field, body, ut, l.TypeMidHeightM(l.FairType >= 0f ? l.FairType : 0f)).Bytes;
                                if (!l.HasClimate) type = CloudLookTuning.ApplyAlto(type, s.Precip.Bytes, altoMid, altoLow, l.AltoType, l.AltoFrom, l.AltoFull, ref altoScratch);
                            }
                        }
                        Dump(folder, tag + l.Name + "_raw", raw.Bytes);
                        Dump(folder, tag + l.Name + "_deck", cover);
                        var inp = new CloudMesoInputs
                        {
                            Width = GeneratorMaps.Width, Height = GeneratorMaps.Height, Precip = s.Precip.Bytes, Humidity = s.Humidity.Bytes, Surface = s.Surface.Bytes,
                            AltoMid = altoMid, AltoLow = altoLow, Ut = ut, DriftRadS = field.CloudDriftRadS(body, CloudBridgeStack.MidHeightM(l)), Seed = field.SeedOf(body)
                        };
                        Assert.IsTrue(field.CloudScalesOf(body, inp), "the field's body has its scales");
                        byte[] coverOut = null, typeOut = null;
                        CloudMesoStage.Apply(l, inp, cover, type, ref coverOut, ref typeOut);
                        Dump(folder, tag + l.Name + "_nocombine", coverOut);
                        combined[li] = (byte[])coverOut.Clone();
                        finalTypes[li] = typeOut;
                    }
                    // the module's last step of a round (OwnCloudModule.FetchStep): the layer maps brought to the field's cover, with the product's default weight
                    CloudLookTuning.MeetTheField(combined, layers.Length, s.Cover.Bytes, CloudLookTuning.CoverCombine, decks, s.Precip.Values);
                    for (int li = 0; li < layers.Length; li++)
                    {
                        Dump(folder, tag + layers[li].Name + "_cover", combined[li]);
                        Dump(folder, tag + layers[li].Name + "_type", finalTypes[li]);
                    }
                }
                using (var meta = new StreamWriter(Path.Combine(folder, "meta.txt")))
                {
                    meta.WriteLine("width " + GeneratorMaps.Width + " height " + GeneratorMaps.Height + " seed " + seed);
                    foreach (CloudLayerDef l in layers)
                    {
                        meta.Write("layer " + l.Name + " types");
                        foreach (CloudTypeDef t in l.Types) meta.Write(" '" + t.Name + "'");
                        meta.WriteLine(" deck " + (l.Deck != null));
                    }
                }
            }
            finally
            {
                LayerWindOptions.SteeringOverride = null;
            }
        }
    }
}
