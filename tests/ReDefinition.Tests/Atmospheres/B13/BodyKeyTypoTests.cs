using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.C25;

namespace ReDefinition.Tests.Atmospheres.B13
{
    // A key inside a known node of a body file that nothing reads (a typo) is said once in the log, with the nearest key; the default
    // stays. And the per-body frequency of the settings window reaches the generator.
    [TestClass]
    public class BodyKeyTypoTests
    {
        private static List<string> Unknown(BodyParseResult r)
        {
            return r.Warnings.Where(w => w.Contains(BodyNodeReader.UnknownKeyText)).ToList();
        }

        [TestMethod]
        public void AMisspelledKeyInAKnownNodeWarnsAndTheDefaultStays()
        {
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear), "REDEFINITION_BODY { name = X ; TEMPERATURE { waterCelcius = 7 } }");
            List<string> unknown = Unknown(r);
            Assert.AreEqual(1, unknown.Count, string.Join("\n", r.Warnings));
            StringAssert.Contains(unknown[0], "TEMPERATURE.waterCelcius");
            StringAssert.Contains(unknown[0], "did you mean 'waterCelsius'");
            Assert.AreEqual(0.0, r.Climate.Temperature.WaterCelsius, "the typo did not set the value");
        }

        [TestMethod]
        public void ARightKeyAndTheLoadersOwnNameKeyGiveNoWarning()
        {
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear),
                "REDEFINITION_BODY { name = X ; enabled = true ; status = Auto ; TEMPERATURE { waterCelsius = 7 } ; SEASONS { yearFactor = 2 } ; GENERATOR { LEVERS { SeasonalResponse = 0.9 } } }");
            Assert.AreEqual(0, Unknown(r).Count, string.Join("\n", r.Warnings));
            Assert.AreEqual(7.0, r.Climate.Temperature.WaterCelsius);
        }

        [TestMethod]
        public void AMisspelledKeyInTheRootNodeAndInANestedTypeNodeWarnToo()
        {
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear),
                "REDEFINITION_BODY { name = X ; stauts = Full ; PRECIP { TYPE { kind = Snow ; centerC = 2 } } }");
            List<string> unknown = Unknown(r);
            Assert.AreEqual(2, unknown.Count, string.Join("\n", r.Warnings));
            Assert.IsTrue(unknown.Any(w => w.Contains(".stauts") && w.Contains("did you mean 'status'")));
            Assert.IsTrue(unknown.Any(w => w.Contains("centerC") && w.Contains("did you mean 'centreC'")));
        }

        [TestMethod]
        public void AKeyWithNoNearNameSaysSoWithoutASuggestion()
        {
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear), "REDEFINITION_BODY { name = X ; LOOK { completelyOther = 1 } }");
            List<string> unknown = Unknown(r);
            Assert.AreEqual(1, unknown.Count);
            Assert.IsFalse(unknown[0].Contains("did you mean"));
        }

        [TestMethod]
        public void TheShippedBodyFilesHaveNoUnknownKey()
        {
            foreach (var stock in E41.E41Support.StockBodies())
            {
                if (!stock.HasNode) continue;
                string file = E41.E41Support.BodyFile(stock.Facts.Name);
                if (!File.Exists(file)) continue;
                ConfigNode node = ConfigNode.Load(file).GetNode(BodyConfigParser.NodeName);
                BodyParseResult r = BodyConfigParser.Parse(stock.Facts, node, null, 7);
                Assert.AreEqual(0, Unknown(r).Count, stock.Facts.Name + ":\n" + string.Join("\n", Unknown(r)));
            }
        }

        [TestMethod]
        public void KerbinsNodeWithItsCloudLayersAppendedHasNoUnknownKey()
        {
            // The layer file is a ModuleManager patch that adds LAYER nodes to CLOUDS; the merge is done here by hand.
            ConfigNode kerbin = E41.E41Support.BodyNode("Kerbin");
            ConfigNode patch = ConfigNode.Load(Path.Combine(E41.E41Support.Root(), "GameData", "ReDefinition", "Atmospheres", "Bodies", "Clouds-Kerbin.cfg"));
            ConfigNode patchBody = patch.nodes[0];
            ConfigNode clouds = kerbin.GetNode("CLOUDS");
            int added = 0;
            foreach (ConfigNode patchClouds in patchBody.nodes)
            {
                foreach (ConfigNode layer in patchClouds.GetNodes("LAYER"))
                {
                    clouds.AddNode(layer);
                    added++;
                }
            }
            Assert.IsTrue(added >= 4, "layers merged: " + added);
            BodyFacts facts = E41.E41Support.StockBodies().First(s => s.Facts.Name == "Kerbin").Facts;
            BodyParseResult r = BodyConfigParser.Parse(facts, kerbin, null, 7);
            Assert.IsTrue(r.Climate.Clouds.Layers.Count >= 4, "layers read: " + r.Climate.Clouds.Layers.Count);
            Assert.AreEqual(0, Unknown(r).Count, string.Join("\n", Unknown(r)));
        }

        [TestMethod]
        public void TheProviderLogsATypoOnceWhateverHowOftenTheBodyIsBuilt()
        {
            var logged = new List<string>();
            string node = "REDEFINITION_BODY { name = A ; TEMPERATURE { waterCelcius = 7 } ; LOOK { visibilityMinn = 5 } ; ICE { floeSizeKmm = 1 } }";
            var provider = new BodyConfigProvider(() => new[] { BodyTestSupport.Parse(node) }, () => new string[0], () => new Dictionary<string, BodyPlayerOptions>(), () => 7);
            provider.Say = s => { };
            provider.Warn = (kind, message) => logged.Add(kind + "|" + message);
            for (int i = 0; i < 3; i++) provider.Build(BodyTestSupport.Planet("A", 1e7), new List<string>());
            Assert.AreEqual(3, logged.Count(l => l.Contains(BodyNodeReader.UnknownKeyText)), string.Join("\n", logged));
            Assert.AreEqual(3, logged.Select(l => l.Split('|')[0]).Distinct().Count(), "each its own kind, so none is dropped by the count of the kind");
        }

        [TestMethod]
        public void TheBodysFrequencyReachesTheGeneratorsBands()
        {
            BodyFacts facts = FieldFixture.HomeFacts();
            BodyParseResult r = BodyTestSupport.Run(facts, "REDEFINITION_BODY { name = Testworld }", new BodyPlayerOptions(1.0, 2.0));
            Assert.AreEqual(2.0, r.Climate.PlayerFrequency);
            Assert.AreEqual(1.0, BodyTestSupport.Run(facts, "REDEFINITION_BODY { name = Testworld }").Climate.PlayerFrequency);

            BodyClimate climate = FieldFixture.Climate(facts);
            GeneratorState plain = FieldFixture.State(climate, FieldFixture.Params(climate, facts));
            plain.ApplyKnobs(1f, 1f);
            float[] baseline = plain.Bands;
            GeneratorState doubled = FieldFixture.State(climate, FieldFixture.Params(climate, facts));
            doubled.BodyFrequency = (float)r.Climate.PlayerFrequency;
            doubled.ApplyKnobs(1f, 1f);
            Assert.AreEqual(baseline[0] / 2f, doubled.Bands[0], baseline[0] * 1e-4f, "Flurries start at half the rate: twice as much light weather");
            Assert.AreEqual(baseline[3], doubled.Bands[3], 1e-6f, "the heavy bands are not the light-weather knob's");
            GeneratorState none = FieldFixture.State(climate, FieldFixture.Params(climate, facts));
            none.BodyFrequency = 0f;
            none.ApplyKnobs(1f, 1f);
            Assert.IsTrue(none.Bands[0] > baseline[0] * 10f, "frequency 0: nearly no light weather");
        }

        [TestMethod]
        public void TheGeneratorModuleHandsTheBodysFrequencyOver()
        {
            string source = File.ReadAllText(Path.Combine(E41.E41Support.Root(), "src", "Atmospheres", "Field", "GeneratorModule.cs"));
            int hand = source.IndexOf("state.BodyFrequency = (float)c.PlayerFrequency;", StringComparison.Ordinal);
            int set = source.IndexOf("field.SetState(state);", StringComparison.Ordinal);
            Assert.IsTrue(hand > 0 && set > hand, "the frequency has to be on the state before it enters the field (SetState applies the knobs)");
        }
    }
}
