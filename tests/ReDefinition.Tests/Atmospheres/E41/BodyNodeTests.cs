using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;
using ReDefinition.Tests.Atmospheres.B13;

namespace ReDefinition.Tests.Atmospheres.E41
{
    // The shipped nodes of Duna, Laythe, Eve and Jool, the status of the stock bodies with them, and a sand body that needs no code.
    [TestClass]
    public class BodyNodeTests
    {
        private static BodyParseResult Parse(E41Support.Stock stock, List<string> fields = null)
        {
            if (fields != null) stock.Facts.EveFields = fields;
            ConfigNode node = stock.HasNode ? E41Support.BodyNode(stock.Facts.Name) : null;
            return BodyConfigParser.Parse(stock.Facts, node, null, 7);
        }

        private static E41Support.Stock Stock(string name)
        {
            return E41Support.StockBodies().Single(s => s.Facts.Name == name);
        }

        [TestMethod]
        public void TheStatusOfEveryStockBodyEqualsTheExpectedTable()
        {
            foreach (E41Support.Stock stock in E41Support.StockBodies())
            {
                BodyParseResult r = Parse(stock);
                Assert.AreEqual(stock.Expected, r.Climate.Status.ToString(), stock.Facts.Name);
                Assert.IsTrue(r.Summary.StartsWith("body " + stock.Facts.Name + ": " + stock.Expected), "the summary line of " + stock.Facts.Name + ": " + r.Summary);
                // A star has no orbit, so its clock warns; every other body reads without a warning.
                if (!stock.Facts.IsStar) Assert.AreEqual(0, r.Warnings.Count, stock.Facts.Name + ": " + string.Join("; ", r.Warnings));
            }
        }

        [TestMethod]
        public void TheShippedChecklistEqualsTheTableTheTestsUse()
        {
            // Every body that is Full and ships a body node has its Clouds file, and the Full bodies are the four the plan names.
            List<E41Support.Stock> all = E41Support.StockBodies();
            var full = all.Where(s => s.Expected == "Full").Select(s => s.Facts.Name).OrderBy(n => n).ToArray();
            CollectionAssert.AreEqual(new[] { "Duna", "Eve", "Kerbin", "Laythe" }, full);
            foreach (string body in new[] { "Kerbin", "Duna", "Laythe" })
            {
                Assert.IsTrue(System.IO.File.Exists(E41Support.CloudsFile(body)), body);
            }
            Assert.IsFalse(System.IO.File.Exists(E41Support.CloudsFile("Eve")), "Eve keeps the pack's layers: clouds only");
        }

        [TestMethod]
        public void TheChecklistCountsFourFullBodiesAndElevenWithoutAnAtmosphere()
        {
            List<E41Support.Stock> all = E41Support.StockBodies();
            Assert.AreEqual(17, all.Count);
            Assert.AreEqual(4, all.Count(s => s.Expected == "Full"));
            Assert.AreEqual(11, all.Count(s => s.Expected == "Off(NoAtmosphere)"));
            Assert.AreEqual(1, all.Count(s => s.Expected == "Off(NoSurface)"));
            Assert.AreEqual(1, all.Count(s => s.Expected == "Off(Star)"));
        }

        [TestMethod]
        public void DunaDropsDustAndLiesUnderDustAndTheWindIsStrong()
        {
            BodyClimate c = Parse(Stock("Duna"), new List<string> { "dust-Duna" }).Climate;
            Assert.IsTrue(c.Status.WeatherRuns);
            Assert.AreEqual(1, c.Precip.Types.Count);
            Assert.AreEqual(PrecipType.Dust, c.Precip.Types[0].Kind);
            Assert.AreEqual("dust-Duna", c.Precip.Types[0].Field);
            Assert.IsFalse(c.Precip.Types[0].ColdSide);
            Assert.AreEqual(1, c.Covers.Count);
            Assert.AreEqual(CoverKind.Dust, c.Covers[0].Kind);
            Assert.IsTrue(c.Covers[0].TracksTauHours >= 24.0, "long tau");
            Assert.AreEqual(30f, c.Wind.MaxWindMs, "the wind of Duna is up to 30 m/s");
            Assert.IsTrue(c.Wind.ViolentWindMs > c.Wind.MaxWindMs);
            Assert.AreEqual(GeneratorDust.On, c.Generator.Dust);
            Assert.IsTrue(c.Look.VisibilityMinM < 60f, "dust takes the visibility lower than Kerbin's snow");
            Assert.IsFalse(c.Frost.Enabled);
            Assert.AreEqual(SeasonClockKind.Orbit, c.Seasons.Clock);
            Assert.IsFalse(c.Seasons.EccentricSeason, "the perihelion gate waits for the owner (Q7)");
            // Ochre: red above green above blue in the fog and the veils.
            Assert.IsTrue(c.Look.FogColour.r > c.Look.FogColour.g && c.Look.FogColour.g > c.Look.FogColour.b);
            Assert.IsTrue(c.Look.VeilGroundColour.r > c.Look.VeilGroundColour.b);
            Assert.IsTrue(c.Look.VeilSkyColour.r > c.Look.VeilSkyColour.b);
            Assert.AreEqual(0, c.Clouds.WeatherLayers.Count, "the layers with a dust type are found by role");
            Assert.IsTrue(c.Clouds.DustTypeNames.Contains("Dust"));
        }

        [TestMethod]
        public void LaytheHasRainAndSnowByTemperatureAndNoCoverOnTheOcean()
        {
            BodyClimate c = Parse(Stock("Laythe"), new List<string> { "snow-Laythe" }).Climate;
            Assert.IsTrue(c.Status.WeatherRuns);
            PrecipTypeRule snow = c.Precip.Types.Single(t => t.Kind == PrecipType.Snow);
            PrecipTypeRule rain = c.Precip.Types.Single(t => t.Kind == PrecipType.Rain);
            Assert.IsTrue(snow.ColdSide);
            Assert.IsFalse(rain.ColdSide);
            Assert.AreEqual("snow-Laythe", snow.Field);
            Assert.AreEqual("", rain.Field, "the pack has no rain field for Laythe: our renderer draws it");
            Assert.AreEqual(1, c.Covers.Count);
            Assert.IsTrue(c.Covers[0].NoWater);
            Assert.AreEqual(SeasonClockKind.Orbit, c.Seasons.Clock);
            Assert.AreEqual(YearSourceKind.ParentOrbitPeriod, c.Seasons.YearSource);
            // Jool's year is 1.05e8 s; the default cap of three home years (27.6e6 s) applies to every year.
            Assert.AreEqual(3.0 * 9.203545e6, c.YearSeconds, 1.0, "the year is Jool's, capped at three home years");
        }

        [TestMethod]
        public void EveHasCloudsOnlyAndNothingFallsOrLies()
        {
            BodyClimate c = Parse(Stock("Eve")).Climate;
            Assert.IsTrue(c.Status.WeatherRuns);
            Assert.AreEqual(1, c.Precip.Types.Count);
            Assert.AreEqual(PrecipType.None, c.Precip.Types[0].Kind);
            Assert.AreEqual(0, c.Covers.Count);
            Assert.IsFalse(c.Frost.Enabled);
        }

        [TestMethod]
        public void JoolIsOffForWantOfASurfaceWhateverTheNodeSaysAboutItsAtmosphere()
        {
            BodyClimate c = Parse(Stock("Jool")).Climate;
            Assert.AreEqual(BodyStatusKind.Off, c.Status.Kind);
            Assert.AreEqual("NoSurface", c.Status.Reason);
            Assert.IsFalse(c.Status.WeatherRuns);
        }

        [TestMethod]
        public void ASandBodyNeedsNoCodeOnlyItsConfiguration()
        {
            // A body of a pack with a sand field and no node of its own: the type and the cover follow the field's name.
            BodyFacts hox = BodyTestSupport.Planet("Hox", 3.0e8);
            hox.EveFields = new List<string> { "sand-Hox" };
            BodyClimate c = BodyConfigParser.Parse(hox, null, null, 7).Climate;
            Assert.AreEqual(PrecipType.Sand, c.Precip.Types.Single().Kind);
            Assert.AreEqual(CoverKind.Sand, c.Covers.Single().Kind);
            // The same through a node, for a dust body that is not Duna.
            BodyFacts dry = BodyTestSupport.Planet("Dryland", 3.0e8);
            BodyClimate d = BodyTestSupport.Run(dry, "REDEFINITION_BODY { name = Dryland ; PRECIP { TYPE { kind = Dust ; coldSide = false ; field = dust-Dryland } } ; COVER { kind = Dust ; colour = 0.5, 0.4, 0.3 } ; CLOUDS { dustTypeNames = Haze } }").Climate;
            Assert.AreEqual(PrecipType.Dust, d.Precip.Types.Single().Kind);
            Assert.AreEqual(CoverKind.Dust, d.Covers.Single().Kind);
            Assert.AreEqual("Haze", d.Clouds.DustTypeNames.Single());
            // The cloud type encoding takes the dust type at every level for a dust body.
            TypeSlots slots = TypeRoles.FromNames(new[] { "Haze" }, d.Clouds);
            Assert.IsTrue(slots.Has(CloudRole.Dust));
            foreach (PrecipBand band in new[] { PrecipBand.Flurries, PrecipBand.Moderate, PrecipBand.Blizzard })
            {
                Assert.AreEqual(CloudRole.Dust, CloudTypeEncoding.RoleOfBand(band, true));
            }
        }
    }
}
