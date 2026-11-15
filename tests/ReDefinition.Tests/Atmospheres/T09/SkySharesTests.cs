using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 F1 "the game's sky is closed where the weather says half": the sky the layers draw must follow the weather field's cover.
    // EXPECTED RESULTS, written before the first run of this file (the numbers are those of the exploration run, SkyClearModel on the 24 maps of the type-mix audit and on the game's maps of
    // start 60 at the K-T2 site):
    //   - a map value is about the share of the sky a layer shows (the cover meaning, the default): the rain deck, the cumulus layer and the cumulonimbus layer show between c - 0.05 and c + 0.2
    //     of the sky at c = 0.3, 0.5, 0.7, the cirrus layer between c - 0.1 and c + 0.2 from c = 0.5 (a thin sheet: visible, never closed); with the old mapping (cloud.coverMeaning 0) the deck
    //     shows 0.98 of the sky at c = 0.5 and the cumulonimbus layer nothing;
    //   - the layer maps of the generator carry about the field's whole cover each, so the four layers together show 0.75 of the sky where the field says 0.40 (the cause); brought to the
    //     field (CloudLookTuning.MeetTheField, the default) they show the field's cover within 0.1;
    //   - rain still has a cloud above it: where it rains heavily the weather deck is at its floor after the combination, and a deck without a floor is left alone.
    [TestClass]
    public class SkySharesTests
    {
        private static CloudLayerDef[] shipped;
        private static CloudLayerDef[] Layers() { return shipped ?? (shipped = TypeMixFixture.ShippedLayers()); }

        [TestCleanup]
        public void Clean()
        {
            CoverMeaningPin.Release();
        }

        // The share of the sky a layer shows at a map value and a type value, by the model (a vertical column; visible: opacity 0.1).
        private static double Visible(CloudLayerDef layer, double map, double typeValue)
        {
            double visible, closed;
            SkyClearModel.Shares(layer, new LayerTypeTable(layer), map, typeValue, out visible, out closed);
            return visible;
        }

        [TestMethod]
        public void AMapValueIsAboutTheShareOfTheSkyTheLayerShowsByDefault()
        {
            CloudLayerDef[] layers = Layers();
            // type values: the deck's Stratus, the cumulus layer's fair type, the calvus of the cumulonimbus layer, the cirrus
            double[] types = { 0.5, 0.25, 0.0, 0.0 };
            for (int i = 0; i < 3; i++)
                foreach (double c in new[] { 0.3, 0.5, 0.7 })
                {
                    double v = Visible(layers[i], c, types[i]);
                    Assert.IsTrue(v >= c - 0.05 && v <= c + 0.2, layers[i].Name + " shows " + v.ToString("0.00") + " of the sky at the map value " + c);
                }
            foreach (double c in new[] { 0.5, 0.7 })
            {
                double v = Visible(layers[3], c, types[3]);
                Assert.IsTrue(v >= c - 0.1 && v <= c + 0.2, layers[3].Name + " shows " + v.ToString("0.00") + " of the sky at the map value " + c);
            }
        }

        [TestMethod]
        public void WithTheOldMappingTheDeckIsAStepAndTheTowerLayerShowsNothingAtHalf()
        {
            CoverMeaningPin.Legacy();
            CloudLayerDef[] layers = Layers();
            Assert.IsTrue(Visible(layers[0], 0.5, 0.5) > 0.9, "the deck at 0.5 shows nearly all of the sky");
            Assert.IsTrue(Visible(layers[2], 0.5, 0.0) < 0.05, "the cumulonimbus layer at 0.5 shows nothing");
        }

        // Cover of the four layers for one time of a generator field: the maps as FetchStep makes them (deck rule, the meso stage), before the combination.
        private sealed class Sky
        {
            internal byte[][] Cover = new byte[4][], Type = new byte[4][];
            internal byte[] Field;
            internal float[] Level;
        }

        private static Sky MakeSky(GeneratorField field, double ut)
        {
            CloudLayerDef[] layers = Layers();
            int body = FieldFixture.BodyIndex;
            TypeMixFixture.Shared shared = TypeMixFixture.Maps(field, body, ut);
            var sky = new Sky { Field = shared.Cover.Bytes, Level = shared.Precip.Values };
            for (int l = 0; l < 4; l++) TypeMixFixture.Layer(field, body, shared, layers[l], true, out sky.Cover[l], out sky.Type[l]);
            return sky;
        }

        // The mean over the map (cos latitude weights) of the sky the layers show together (independent layers) and of the field's cover.
        private static void Union(Sky sky, byte[][] cover, out double union, out double field)
        {
            CloudLayerDef[] layers = Layers();
            var tables = new LayerTypeTable[4];
            for (int l = 0; l < 4; l++) tables[l] = new LayerTypeTable(layers[l]);
            var cache = new Dictionary<int, double>();
            int w = GeneratorMaps.Width, h = GeneratorMaps.Height;
            double sumU = 0, sumF = 0, sumW = 0;
            for (int j = 0; j < h; j++)
            {
                double weight = Math.Cos((90.0 - (j + 0.5) * 180.0 / h) * Math.PI / 180.0);
                for (int i = 0; i < w; i++)
                {
                    int k = j * w + i;
                    double keep = 1.0;
                    for (int l = 0; l < 4; l++)
                    {
                        int bin = (int)Math.Round(sky.Type[l][k] / 255.0 * 16.0);
                        int key = (l * 256 + cover[l][k]) * 17 + bin;
                        double v;
                        if (!cache.TryGetValue(key, out v))
                        {
                            double closed;
                            SkyClearModel.Shares(layers[l], tables[l], cover[l][k] / 255.0, bin / 16.0, out v, out closed, 100);
                            cache[key] = v;
                        }
                        keep *= 1.0 - v;
                    }
                    sumU += (1.0 - keep) * weight;
                    sumF += sky.Field[k] / 255.0 * weight;
                    sumW += weight;
                }
            }
            union = sumU / sumW;
            field = sumF / sumW;
        }

        [TestMethod]
        public void TheLayerMapsOfTheGeneratorCloseTheSkyTwiceOverAndTheCombinationBringsThemToTheField()
        {
            GeneratorField field = FieldFixture.Field(1);
            double before = 0, after = 0, fieldCover = 0;
            const int times = 2;
            for (int t = 0; t < times; t++)
            {
                Sky sky = MakeSky(field, 3.0e6 + t * 5.0e5);
                double u, f;
                Union(sky, sky.Cover, out u, out f);
                before += u;
                fieldCover += f;
                var combined = new byte[4][];
                for (int l = 0; l < 4; l++) combined[l] = (byte[])sky.Cover[l].Clone();
                var decks = new WeatherDeckRule[4];
                for (int l = 0; l < 4; l++) decks[l] = Layers()[l].Deck;
                CloudLookTuning.MeetTheField(combined, 4, sky.Field, 1f, decks, sky.Level);
                Union(sky, combined, out u, out f);
                after += u;
            }
            before /= times;
            after /= times;
            fieldCover /= times;
            Console.WriteLine("SKYSHARES field " + fieldCover.ToString("0.000") + ", the layers' union before " + before.ToString("0.000") + ", after " + after.ToString("0.000"));
            Assert.IsTrue(before > fieldCover + 0.25, "the maps as the generator makes them show " + before.ToString("0.00") + " of the sky where the field says " + fieldCover.ToString("0.00"));
            Assert.AreEqual(fieldCover, after, 0.1, "brought to the field the layers show " + after.ToString("0.00") + " of the sky, the field says " + fieldCover.ToString("0.00"));
        }

        [TestMethod]
        public void ARainFloorSurvivesTheCombinationAndADeckWithoutOneIsLeftAlone()
        {
            var rule = new WeatherDeckRule { RainFloor = 0.9f, RainFloorFullAt = 0.7f };
            var bare = new WeatherDeckRule();
            byte[] deck = { 0, 40, 240 }, tower = { 0, 40, 240 };
            float[] level = { 0.6f, 0.02f, 0.6f };
            rule.RaiseToFloor(deck, level);
            bare.RaiseToFloor(tower, level);
            // level 0.6: the floor is 0.9 x smoothstep(0.04, 0.7, 0.6); level 0.02: below the band, none
            double floor = 0.9 * (3 * Math.Pow((0.6 - 0.04) / 0.66, 2) - 2 * Math.Pow((0.6 - 0.04) / 0.66, 3));
            Assert.AreEqual(Math.Round(floor * 255.0), deck[0], 1.0, "a texel with no cloud under heavy rain is lifted to the floor");
            Assert.AreEqual(40, deck[1], "no rain, no floor");
            Assert.AreEqual(240, deck[2], "a deck above its floor is not touched");
            CollectionAssert.AreEqual(new byte[] { 0, 40, 240 }, tower, "a layer without a floor is left alone");
            // the combination: maps scaled to the field's cover, the deck lifted to its floor afterwards
            byte[][] maps = { new byte[] { 230 }, new byte[] { 230 } };
            byte[] shared = { 120 };
            CloudLookTuning.MeetTheField(maps, 2, shared, 1f, new[] { rule, (WeatherDeckRule)null }, new[] { 0.6f });
            Assert.IsTrue(maps[1][0] < 200, "the cumulus layer is thinned to the field's share");
            Assert.IsTrue(maps[0][0] >= Math.Round(floor * 255.0) - 1, "the deck keeps its floor");
            byte[][] untouched = { new byte[] { 230 }, new byte[] { 230 } };
            CloudLookTuning.MeetTheField(untouched, 2, shared, 0f, new[] { rule, (WeatherDeckRule)null }, new[] { 0.6f });
            CollectionAssert.AreEqual(new byte[] { 230 }, untouched[0], "weight 0 changes nothing, floors included");
        }

        [TestMethod]
        public void HeavyRainStillHasADrawnRainCloudAfterTheCombination()
        {
            GeneratorField field = FieldFixture.Field(1);
            Sky sky = MakeSky(field, 3.0e6 + 1.0e6);
            var combined = new byte[4][];
            for (int l = 0; l < 4; l++) combined[l] = (byte[])sky.Cover[l].Clone();
            var decks = new WeatherDeckRule[4];
            for (int l = 0; l < 4; l++) decks[l] = Layers()[l].Deck;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            CloudLookTuning.MeetTheField(combined, 4, sky.Field, 1f, decks, sky.Level);
            Console.WriteLine("SKYSHARES MeetTheField on 512 x 256 texels: " + clock.ElapsedMilliseconds + " ms");
            Assert.IsTrue(clock.ElapsedMilliseconds < 1500, "the combination of a round takes " + clock.ElapsedMilliseconds + " ms");
            int heavy = 0, bare = 0;
            for (int k = 0; k < sky.Level.Length; k++)
            {
                if (sky.Level[k] < 0.5f) continue;
                heavy++;
                // the rain-bearing layers are the deck and the cumulonimbus layer (RainCoherenceTests)
                if (combined[0][k] <= 128 && combined[2][k] <= 128) bare++;
            }
            Console.WriteLine("SKYSHARES heavy rain texels " + heavy + ", without a drawn rain cloud " + bare);
            Assert.AreEqual(0, bare, "no heavy rain without a drawn rain cloud over it");
        }
    }
}
