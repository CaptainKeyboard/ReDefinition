using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.B13
{
    // The parser of REDEFINITION_BODY nodes, the provider around it, and the Kerbin node.
    [TestClass]
    public class BodyConfigParserTests
    {
        private static readonly string[] MalformedNodes =
        {
            "REDEFINITION_BODY { name = X ; SEASONS { tilt = abc } }",
            "REDEFINITION_BODY { name = X ; SIM { gridLat = 99999 ; gridLon = -4 ; levels = x } }",
            "REDEFINITION_BODY { name = X ; PRECIP { TYPE { kind = Bogus ; centreC = hot } } }",
            "REDEFINITION_BODY { name = X ; PRECIP { bandRates = 1, 2, 3 } }",
            "REDEFINITION_BODY { name = X ; COVER { kind = Snow ; colour = red } }",
            "REDEFINITION_BODY { name = X ; SEASONS { yearFactor = -5 ; yearSource = Sideways } }",
            "REDEFINITION_BODY { name = X ; FOO { a = 1 } }",
            "REDEFINITION_BODY { name = X ; CELLS { BAND { types = Unicorn, Snow ; fromLat = 200 } ; gate = Never } }",
            "REDEFINITION_BODY { name = X ; status = Maybe ; enabled = perhaps }",
            "REDEFINITION_BODY { name = X ; TEMPERATURE { TARGET { key0 = 100 280 ; key1 = 50 270 } ; waterCelsius = NaN } }"
        };

        [TestMethod]
        public void TenMalformedNodesGiveWarningsAndAUsableClimate()
        {
            Assert.AreEqual(10, MalformedNodes.Length);
            for (int i = 0; i < MalformedNodes.Length; i++)
            {
                BodyParseResult result = null;
                try
                {
                    result = BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear), MalformedNodes[i]);
                }
                catch (Exception e)
                {
                    Assert.Fail("case " + i + " threw " + e);
                }
                Assert.IsNotNull(result.Climate, "case " + i);
                Assert.IsTrue(result.Warnings.Count >= 1, "case " + i + " gave no warning");
                Assert.AreNotEqual("exception", result.Climate.Status.Reason, "case " + i);
                Assert.IsTrue(result.Climate.Status.Kind == BodyStatusKind.Full, "case " + i + " is " + result.Climate.Status);
                Assert.IsTrue(result.Summary.Contains("warning"), "case " + i);
            }
        }

        [TestMethod]
        public void ABadValueKeepsTheDefaultAndAClampedOneIsClamped()
        {
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("X", BodyTestSupport.HomeYear), MalformedNodes[1]);
            Assert.AreEqual(BodyLimits.MaxGridLat, r.Climate.Sim.GridLat);
            Assert.AreEqual(BodyLimits.MinGridLon, r.Climate.Sim.GridLon);
            Assert.AreEqual(16, r.Climate.Sim.Levels, "an unreadable number keeps the default");
            Assert.AreEqual(0.0, BodyTestSupport.Run(BodyTestSupport.Planet("X", 1e7), MalformedNodes[0]).Climate.Seasons.TiltDegrees, "a tilt that is not a number is Auto");
        }

        [TestMethod]
        public void ANodeLessBodyHasEveryRuleAndNoBodysNumbers()
        {
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("Testworld", BodyTestSupport.HomeYear));
            BodyClimate c = r.Climate;
            Assert.AreEqual(0, r.Warnings.Count);
            Assert.IsTrue(c.Status.WeatherRuns);
            Assert.AreEqual(BodyTestSupport.HomeYear, c.YearSeconds, 1e-6);
            Assert.AreEqual(BodyClimate.SeedOf("Testworld", 7), c.Seed);
            Assert.AreNotEqual(BodyClimate.SeedOf("Otherworld", 7), c.Seed);
            Assert.IsNotNull(c.Seasons);
            Assert.IsNotNull(c.Temperature);
            Assert.IsNotNull(c.Precip);
            Assert.IsNotNull(c.Ice);
            Assert.IsNotNull(c.Cells);
            Assert.IsNotNull(c.Sim);
            Assert.IsNotNull(c.Clouds);
            Assert.IsNotNull(c.Look);
            Assert.IsNotNull(c.Sound);
            Assert.IsNotNull(c.Wind);
            Assert.IsNotNull(c.Gameplay);
            Assert.IsNotNull(c.Aurora);
            Assert.IsNotNull(c.LightMedium);
            Assert.IsNotNull(c.Frost);
            Assert.IsNotNull(c.Forecast);
            Assert.AreEqual(0, c.Covers.Count);
            Assert.AreEqual(0, c.Precip.Types.Count, "no particle fields and a temperate surface: the type follows the temperature");
            Assert.AreEqual(SeasonClockKind.Orbit, c.Seasons.Clock);
            Assert.AreEqual(0.0, c.Seasons.TiltDegrees);
        }

        [TestMethod]
        public void ThePatchedNodeIsWhatTheParserReadsAndTheLastOfTwoNodesWins()
        {
            // ModuleManager has applied @REDEFINITION_BODY[X] { @TEMPERATURE { @waterCelsius = -5 } } before the parser runs: the node
            // it hands over has the new value, and nothing but that.
            string plain = "REDEFINITION_BODY { name = X ; TEMPERATURE { waterCelsius = 0 } }";
            string patched = "REDEFINITION_BODY { name = X ; TEMPERATURE { waterCelsius = -5 } }";
            BodyFacts facts = BodyTestSupport.Planet("X", BodyTestSupport.HomeYear);
            Assert.AreEqual(0.0, BodyTestSupport.Run(facts, plain).Climate.Temperature.WaterCelsius);
            Assert.AreEqual(-5.0, BodyTestSupport.Run(facts, patched).Climate.Temperature.WaterCelsius);

            var said = new List<string>();
            var provider = new BodyConfigProvider(() => new[] { BodyTestSupport.Parse(plain), BodyTestSupport.Parse(patched) }, () => new string[0],
                () => new Dictionary<string, BodyPlayerOptions>(), () => 7);
            provider.Say = said.Add;
            provider.Warn = (kind, message) => said.Add(message);
            Assert.AreEqual(-5.0, provider.Build(facts, new List<string>()).Temperature.WaterCelsius);
            Assert.IsTrue(said.Any(s => s.Contains("more than one")), "the duplicate is reported");
        }

        [TestMethod]
        public void ANodeNamedStarIsTheDefaultOfBodiesWithAnAtmosphereOnly()
        {
            string star = "REDEFINITION_BODY { name = * ; TEMPERATURE { waterCelsius = 7 } }";
            var provider = new BodyConfigProvider(() => new[] { BodyTestSupport.Parse(star) }, () => new string[0], () => new Dictionary<string, BodyPlayerOptions>(), () => 7);
            provider.Say = s => { };
            provider.Warn = (k, m) => { };
            Assert.AreEqual(7.0, provider.Build(BodyTestSupport.Planet("A", 1e7), new List<string>()).Temperature.WaterCelsius);
            Assert.AreEqual(0.0, provider.Build(BodyTestSupport.Planet("B", 1e7, atmosphere: false), new List<string>()).Temperature.WaterCelsius);
        }

        [TestMethod]
        public void TheProvidersSummaryLineIsOnePerBodyAndNamesTheNodeSource()
        {
            var said = new List<string>();
            var provider = new BodyConfigProvider(() => new ConfigNode[0], () => new string[0], () => new Dictionary<string, BodyPlayerOptions>(), () => 7);
            provider.Say = said.Add;
            provider.Warn = (k, m) => said.Add(m);
            provider.Build(BodyTestSupport.Planet("A", 1e7), new List<string>());
            Assert.AreEqual(1, said.Count);
            StringAssert.Contains(said[0], "body A: Full");
            StringAssert.Contains(said[0], "[no node]");
        }

        [TestMethod]
        public void ThePlayersYearFactorMultipliesTheNodesYearFactor()
        {
            BodyFacts facts = BodyTestSupport.Planet("X", BodyTestSupport.HomeYear);
            BodyParseResult r = BodyTestSupport.Run(facts, "REDEFINITION_BODY { name = X ; SEASONS { yearFactor = 2 } }", new BodyPlayerOptions(1.5, 1.0));
            Assert.AreEqual(BodyTestSupport.HomeYear * 3.0, r.Climate.YearSeconds, 1e-3);
        }

        [TestMethod]
        public void TheKerbinNodeParsesWithoutAWarningAndRainFallsAboveTheCentre()
        {
            string path = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "GameData", "ReDefinition", "Atmospheres", "Bodies", "Kerbin.cfg");
            ConfigNode node = ConfigNode.Load(path).GetNode(BodyConfigParser.NodeName);
            Assert.IsNotNull(node);
            BodyFacts facts = BodyTestSupport.Planet("Kerbin", BodyTestSupport.HomeYear);
            facts.EveFields = new List<string> { "rain-Kerbin", "snow-Kerbin" };
            BodyParseResult r = BodyConfigParser.Parse(facts, node, null, 7);
            Assert.AreEqual(0, r.Warnings.Count, string.Join("\n", r.Warnings));
            BodyClimate c = r.Climate;
            Assert.IsTrue(c.Status.WeatherRuns);
            PrecipTypeRule snow = c.Precip.Types.Single(t => t.Kind == PrecipType.Snow);
            PrecipTypeRule rain = c.Precip.Types.Single(t => t.Kind == PrecipType.Rain);
            Assert.IsTrue(snow.ColdSide);
            Assert.IsFalse(rain.ColdSide);
            Assert.AreEqual(1.0, snow.CentreC);
            Assert.AreEqual(1.0, rain.CentreC);
            Assert.AreEqual("snow-Kerbin", snow.Field);
            Assert.AreEqual("rain-Kerbin", rain.Field);
            Assert.AreEqual(1, c.Covers.Count);
            Assert.AreEqual(CoverKind.Snow, c.Covers[0].Kind);
            Assert.IsTrue(c.Covers[0].NoWater);
            Assert.IsTrue(c.Sim.GridLat >= BodyLimits.MinGridLat && c.Sim.GridLat <= BodyLimits.MaxGridLat, "sim grid latitude within the limits");
            Assert.IsTrue(c.Sim.GridLon >= BodyLimits.MinGridLon && c.Sim.GridLon <= BodyLimits.MaxGridLon, "sim grid longitude within the limits");
            Assert.IsTrue(c.Sim.Levels >= BodyLimits.MinLevels && c.Sim.Levels <= BodyLimits.MaxLevels, "sim levels within the limits");
            Assert.IsTrue(c.Sim.StepSeconds >= BodyLimits.MinStepSeconds && c.Sim.StepSeconds <= BodyLimits.MaxStepSeconds, "sim step within the limits");
            Assert.AreEqual(12f, c.Wind.MaxWindMs);
            Assert.AreEqual(25f, c.Wind.ViolentWindMs);
            Assert.AreEqual(60f, c.Look.VisibilityMinM);
            Assert.AreEqual(2, c.Cells.Bands.Count);
            Assert.IsTrue(c.Sim.GridLat >= BodyLimits.MinGridLat && c.Sim.GridLat <= BodyLimits.MaxGridLat);
            Assert.IsTrue(c.Sim.GridLon >= BodyLimits.MinGridLon && c.Sim.GridLon <= BodyLimits.MaxGridLon);
            Assert.IsTrue(c.Sim.Levels >= BodyLimits.MinLevels && c.Sim.Levels <= BodyLimits.MaxLevels);
            Assert.IsTrue(c.Sim.StepSeconds >= BodyLimits.MinStepSeconds && c.Sim.StepSeconds <= BodyLimits.MaxStepSeconds);
            Assert.AreEqual(2, c.Clouds.WeatherLayers.Count);
            Assert.AreEqual("Kerbin-base-layer", c.Clouds.BaseLayer);
        }

        [TestMethod]
        public void TheKerbinNodeNamesTheSameSubNodesAsTheSchema()
        {
            string path = Path.Combine(RepoRoot.Root("src", "ReDefinition.csproj"), "GameData", "ReDefinition", "Atmospheres", "Bodies", "Kerbin.cfg");
            ConfigNode node = ConfigNode.Load(path).GetNode(BodyConfigParser.NodeName);
            var expected = new[] { "SEASONS", "TEMPERATURE", "PRECIP", "COVER", "ICE", "CELLS", "SIM", "CLOUDS", "LOOK", "WIND", "GAMEPLAY", "AURORA", "LIGHT_MEDIUM", "FROST", "FORECAST" };
            foreach (string name in expected) Assert.IsTrue(node.HasNode(name), name + " is missing in the shipped node");
        }

        [TestMethod]
        public void TheBodiesFileReadsTheOptionsAndIgnoresWhatIsBroken()
        {
            ConfigNode root = BodyTestSupport.Root("BODY { name = Alpha ; yearFactor = 2 ; frequency = 0.5 }\nBODY { yearFactor = 3 }\nBODY { name = Beta ; yearFactor = nope ; frequency = 99 }");
            var warnings = new List<string>();
            Dictionary<string, BodyPlayerOptions> options = BodiesFile.Parse(root, warnings);
            Assert.AreEqual(2, options.Count);
            Assert.AreEqual(2.0, options["alpha"].YearFactor);
            Assert.AreEqual(0.5, options["Alpha"].Frequency);
            Assert.AreEqual(1.0, options["Beta"].YearFactor, "not a number: the default");
            Assert.AreEqual(BodyPlayerOptions.MaxFrequency, options["Beta"].Frequency, "clamped");
            Assert.IsTrue(warnings.Count >= 3);
            Assert.AreEqual(0, BodiesFile.Parse(null, null).Count);
        }

        [TestMethod]
        public void TheBodiesFileWritesWhatItReads()
        {
            var options = new Dictionary<string, BodyPlayerOptions> { { "Alpha", new BodyPlayerOptions(2.5, 0.25) } };
            ConfigNode written = BodiesFile.ToNode(options);
            Dictionary<string, BodyPlayerOptions> back = BodiesFile.Parse(written, null);
            Assert.AreEqual(2.5, back["Alpha"].YearFactor);
            Assert.AreEqual(0.25, back["Alpha"].Frequency);
        }

        [TestMethod]
        public void AProviderTurnsTheParticleFieldsOfABodyIntoItsFieldList()
        {
            List<string> fields = BodyProbe.MatchingFields(new[] { "rain-Alpha", "snow-Alpha", "dust-Beta", "snow-Alpha-2", "plain", "-Alpha", "snow-" }, "alpha");
            CollectionAssert.AreEqual(new[] { "rain-Alpha", "snow-Alpha" }, fields);
        }
    }
}
