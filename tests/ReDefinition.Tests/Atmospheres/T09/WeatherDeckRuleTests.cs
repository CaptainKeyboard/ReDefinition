using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.C25;
using ReDefinition.Tests.Atmospheres.T10;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The weather deck rule (WeatherDeckRule, E40 problem 1): what may draw Fog, Stratus and Nimbostratus. The pure rule, the shipped cfg, the tuning key and the rule over the
    // weather generator's own maps (the share of fair-weather deck that drew before and now).
    [TestClass]
    public class WeatherDeckRuleTests
    {
        private static readonly WeatherDeckRule Rule = new WeatherDeckRule();

        private static void Texel(float level, float spread, float wind, out float activity, out float type)
        {
            Rule.Texel(level, spread, wind, out activity, out type);
        }

        [TestMethod]
        public void FairDryWindyAirDrawsNoDeck()
        {
            float a, t;
            Texel(0f, 8f, 3f, out a, out t);
            Assert.AreEqual(0f, a, 0f);
            Texel(0.03f, 20f, 0f, out a, out t);
            Assert.AreEqual(0f, a, 0f, "a level below the band None of the HUD is no precipitation");
        }

        [TestMethod]
        public void FogNeedsSaturatedCalmAir()
        {
            float a, t;
            Texel(0f, 1f, 1f, out a, out t);
            Assert.AreEqual(1f, a, 1e-6f);
            Assert.AreEqual(0f, t, 0f, "fog is the first type");
            Texel(0f, 1f, 9f, out a, out t);
            Assert.AreEqual(0f, a, 0f, "a gale blows fog away");
            Texel(0f, 6f, 0f, out a, out t);
            Assert.AreEqual(0f, a, 0f, "a spread of 6 K is not fog");
            Texel(0f, Rule.FogSpreadK, 0f, out a, out t);
            Assert.AreEqual(0.5f, a, 1e-5f, "half at the centre of the step");
        }

        [TestMethod]
        public void PrecipitationDrawsStratusAndThenNimbostratus()
        {
            float a, t;
            Texel(0.2f, 20f, 8f, out a, out t);
            Assert.AreEqual(1f, a, 1e-6f);
            Assert.AreEqual(0.5f, t, 1e-6f, "light precipitation is stratus whatever the air");
            Texel(0.3f, 20f, 8f, out a, out t);
            Assert.AreEqual(0.5f, t, 1e-6f);
            Texel(0.8f, 20f, 8f, out a, out t);
            Assert.AreEqual(0.8f, t, 1e-6f, "from the precipitation level the type is the old one: nimbostratus rises with the level");
            Texel(1f, 20f, 8f, out a, out t);
            Assert.AreEqual(1f, t, 1e-6f);
        }

        [TestMethod]
        public void ApplyMultipliesTheCoverageAndWritesTheTypeBytes()
        {
            var cover = new byte[] { 200, 200, 200 };
            var level = new float[] { 0f, 0.5f, 0f };
            var spread = new float[] { 9f, 9f, 0.5f };
            var wind = new float[] { 1f, 1f, 1f };
            var oc = new byte[3];
            var ot = new byte[3];
            Rule.Apply(cover, level, spread, wind, oc, ot);
            Assert.AreEqual(0, oc[0]);
            Assert.AreEqual(200, oc[1]);
            Assert.AreEqual(200, oc[2]);
            Assert.AreEqual(128, ot[1], "0.5 as a byte");
            Assert.AreEqual(0, ot[2], "fog");
            Rule.Apply(cover, level, null, null, oc, ot);
            Assert.AreEqual(0, oc[2], "without a humidity map there is no fog");
        }

        [TestMethod]
        public void TheProbeCanForceThePrecipitationAndTheFogShare()
        {
            var cover = new byte[] { 200, 200 };
            var level = new float[] { 0f, 0f };
            var spread = new float[] { 9f, 9f };
            var oc = new byte[2];
            var ot = new byte[2];
            Rule.Apply(cover, level, spread, null, oc, ot, 0.6f, -1f);
            Assert.AreEqual(200, oc[0], "forced rain draws a deck in fair air");
            Assert.AreEqual((byte)Math.Round(0.6f * 255f), ot[0]);
            Rule.Apply(cover, level, spread, null, oc, ot, -1f, 1f);
            Assert.AreEqual(200, oc[1], "forced fog draws a deck in dry air");
            Assert.AreEqual(0, ot[1], "of the fog type");
            Rule.Apply(cover, level, spread, null, oc, ot, -1f, 0.25f);
            Assert.AreEqual(50, oc[0], "a quarter of the fog share is a quarter of the coverage");
        }

        [TestMethod]
        public void TheShippedWeatherDecksCarryTheRuleAndTheOthersDoNot()
        {
            var layers = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers;
            foreach (string name in new[] { "Kerbin-weather-rain", "Kerbin-weather-snow" })
            {
                WeatherDeckRule d = layers.Single(l => l.Name == name).Deck;
                Assert.IsNotNull(d, name);
                Assert.AreEqual(2.8f, d.FogSpreadK, 0f, name);
                Assert.AreEqual(PrecipScale.FlurriesFrom, d.PrecipFrom, 0f, name + " the band None of the HUD");
                Assert.AreEqual(PrecipScale.LightFrom, d.PrecipFullAt, 0f, name);
                Assert.AreEqual(0.5f, d.PrecipType, 0f, name + " stratus");
            }
            foreach (string name in new[] { "Kerbin-cumulus", "Kerbin-cumulonimbus", "Kerbin-cirrus" })
                Assert.IsNull(layers.Single(l => l.Name == name).Deck, name);
        }

        [TestMethod]
        public void TheTuningKeyForcesTheRuleOffOrOn()
        {
            var own = new WeatherDeckRule();
            Assert.AreSame(own, CloudLookTuning.DeckRuleOf("no-such-layer-deckrule", own));
            Assert.IsNull(CloudLookTuning.DeckRuleOf("no-such-layer-deckrule", null));
        }

        // The rule over the generator's own maps (24 times over the year, all 131072 texels each): the deck of fair weather and the rain case against the old rule.
        [TestMethod]
        public void OverTheGeneratorsMapsFairWeatherDrawsNoDeckAndRainIsKept()
        {
            GeneratorField field = FieldFixture.Field();
            var w = new GeneratorMaps.Weather2D();
            int n = GeneratorMaps.Width * GeneratorMaps.Height;
            var spread = new float[n];
            var cover = new byte[n];
            var oc = new byte[n];
            var ot = new byte[n];
            long total = 0, fair = 0, fairOld = 0, fairNew = 0, fogNew = 0, rain = 0, rainChanged = 0, heavy = 0, heavyTypeChanged = 0, oldDrawn = 0, newDrawn = 0;
            for (int s = 0; s < 24; s++)
            {
                double ut = 1.0e6 + s * 3.5e5 + s * 7777.0;
                Assert.IsTrue(GeneratorMaps.RenderWeather(field.StateOf(FieldFixture.BodyIndex), ut, w));
                for (int i = 0; i < n; i++)
                {
                    spread[i] = WeatherDeckRule.SpreadK(w.SurfaceTemperature[i], w.Humidity[i]);
                    cover[i] = (byte)Math.Round(w.Coverage[i] * 255.0);
                }
                Rule.Apply(cover, w.Precipitation, spread, w.WindSpeed, oc, ot);
                for (int i = 0; i < n; i++)
                {
                    total++;
                    bool before = cover[i] > 127, after = oc[i] > 127;
                    if (before) oldDrawn++;
                    if (after) newDrawn++;
                    float p = w.Precipitation[i];
                    if (p < PrecipScale.FlurriesFrom)
                    {
                        fair++;
                        if (before) fairOld++;
                        if (after) { fairNew++; fogNew++; }
                    }
                    if (p >= PrecipScale.LightFrom)
                    {
                        rain++;
                        if (oc[i] != cover[i]) rainChanged++;
                    }
                    if (p >= 0.5f)
                    {
                        heavy++;
                        if (Math.Abs(ot[i] - Math.Round(p * 255.0)) > 1) heavyTypeChanged++;
                    }
                }
            }
            Console.WriteLine("WEATHERDECK texels " + total + " fair (level < 0.04) " + fair + "; the deck drew over fair texels: before " + fairOld + " (" + (100.0 * fairOld / fair).ToString("0.0")
                + " % of the fair), now " + fairNew + " (all of them fog air: " + fogNew + ")");
            Console.WriteLine("WEATHERDECK drawn share (cover above 0.5 after the rule): before " + (oldDrawn / (double)total).ToString("0.000") + ", now " + (newDrawn / (double)total).ToString("0.000"));
            Console.WriteLine("WEATHERDECK rain texels (level >= 0.2) " + rain + ", coverage changed " + rainChanged + "; level >= 0.5 " + heavy + ", type changed " + heavyTypeChanged);
            Assert.IsTrue(fairOld > 0, "the old rule drew a deck over fair weather in this field");
            Assert.AreEqual(0L, rainChanged, "the rain case keeps its coverage");
            Assert.AreEqual(0L, heavyTypeChanged, "the type of moderate to heavy precipitation is the precipitation level, as before");
            Assert.IsTrue(fairNew <= 0.2 * fairOld, "the fair-weather deck is left only where the air is fog air: " + fairNew + " of " + fairOld);
        }
    }
}
