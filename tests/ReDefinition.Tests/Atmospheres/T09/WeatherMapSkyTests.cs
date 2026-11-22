using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // WM1: the mapping of the package B column quantities onto today's Kerbin layers (WeatherMapSky) on single texels.
    [TestClass]
    public class WeatherMapSkyTests
    {
        private static WeatherMapSky.Columns One(float heap, float sheet, float baseM, float topM, float rhLow, float rhMid, float rhHigh, float precip = 0f, float deck = 0f)
        {
            return new WeatherMapSky.Columns
            {
                Width = 1, Height = 1, Cover = new[] { heap + sheet }, Heap = new[] { heap }, Sheet = new[] { sheet }, Deck = new[] { deck }, BaseM = new[] { baseM }, TopM = new[] { topM },
                RhLow = new[] { rhLow }, RhMid = new[] { rhMid }, RhHigh = new[] { rhHigh }, PrecipMmH = new[] { precip }
            };
        }

        private static WeatherMapSky.Result Build(CloudLayerDef[] layers, WeatherMapSky.Columns c)
        {
            var today = new byte[layers.Length][];
            for (int l = 0; l < layers.Length; l++) today[l] = new byte[1];
            return WeatherMapSky.Build(layers, c, today, new byte[1], new byte[1], new[] { 610f, 610f, 610f, 610f });
        }

        [TestMethod]
        public void TheRolesOfKerbinsLayersAreFound()
        {
            CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
            Assert.AreEqual(WeatherMapSky.Role.Deck, WeatherMapSky.RoleOf(layers[0]));
            Assert.AreEqual(WeatherMapSky.Role.Heap, WeatherMapSky.RoleOf(layers[1]));
            Assert.AreEqual(WeatherMapSky.Role.Tall, WeatherMapSky.RoleOf(layers[2]));
            Assert.AreEqual(WeatherMapSky.Role.High, WeatherMapSky.RoleOf(layers[3]));
        }

        [TestMethod]
        public void TheTypeValueGivesTheColumnsDepthAsTheInterpolatedThickness()
        {
            CloudLayerDef cu = TypeMixFixture.ShippedLayers()[1];
            int n = cu.Types.Count;
            foreach (float s in new[] { 0.15f, 0.3f, 0.6f, 0.9f })
            {
                float v = WeatherMapSky.TypeForThickness(cu, 0, 3, s);
                float x = v * (n - 1);
                int a = (int)x;
                float f = x - a;
                float thick = (1f - f) * (cu.Types[a].TopShare - cu.Types[a].BaseShare) + f * (cu.Types[a + 1].TopShare - cu.Types[a + 1].BaseShare);
                Assert.AreEqual(s, thick, 1e-4f, "thickness share " + s);
            }
            Assert.AreEqual(0f, WeatherMapSky.TypeForThickness(cu, 0, 3, 0f), "thinner than the thinnest type: the thinnest");
            Assert.AreEqual(0.75f, WeatherMapSky.TypeForThickness(cu, 0, 3, 2f), 1e-6f, "deeper than the deepest: the deepest of the run (congestus)");
        }

        [TestMethod]
        public void TheHeapGoesToTheTallLayerOnlyWhereItIsDeeperThanTheHeapShell()
        {
            CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
            WeatherMapSky.Result shallow = Build(layers, One(0.4f, 0f, 1700f, 2500f, 0.5f, 0.5f, 0.5f));
            Assert.AreEqual(102, shallow.Cover[1][0]);
            Assert.AreEqual(0, shallow.Cover[2][0]);
            WeatherMapSky.Result storm = Build(layers, One(0.4f, 0f, 1000f, 10400f, 0.5f, 0.5f, 0.5f));
            Assert.AreEqual(0, storm.Cover[1][0]);
            Assert.AreEqual(102, storm.Cover[2][0]);
            Assert.AreEqual(1, storm.Root[2][0], "a base 700 m under the shell's floor is clamped to the root range (610 m)");
        }

        [TestMethod]
        public void TheSheetGoesToEverySaturatedLevelElseToTheMostHumidOne()
        {
            CloudLayerDef[] layers = TypeMixFixture.ShippedLayers();
            WeatherMapSky.Result high = Build(layers, One(0f, 0.6f, 1500f, 1500f, 0.4f, 1f, 1f));
            Assert.AreEqual(0, high.Cover[0][0]);
            Assert.AreEqual(153, high.Cover[1][0]);
            Assert.AreEqual(255, high.Type[1][0], "the middle sheet is the alto type");
            Assert.AreEqual(153, high.Cover[3][0]);
            WeatherMapSky.Result low = Build(layers, One(0f, 0.6f, 1500f, 1500f, 0.8f, 0.5f, 0.5f));
            Assert.AreEqual(153, low.Cover[0][0]);
            Assert.AreEqual(128, low.Type[0][0], "stratus, the deck's precipitation type 0.5");
            Assert.AreEqual(0, low.Cover[1][0]);
            WeatherMapSky.Result nimbo = Build(layers, One(0.1f, 0.8f, 600f, 900f, 1f, 1f, 1f, 3f, 0.6f));
            Assert.AreEqual(255, nimbo.Type[0][0], "all levels saturated, rain, the deck dominating: nimbostratus");
        }

        [TestMethod]
        public void TheSkyIsTheWeatherMapsByDefaultAndTheKeyTurnsItOff()
        {
            // Owner 2026-11-22: the sky is the weather map's by default; cloud.weatherMapSky 0 gives the old layer maps.
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(9200001);
            Assert.IsTrue(WeatherMapSky.On);
            Tuning.SetOverride(WeatherMapSky.TuneKey, 0f);
            try
            {
                Tuning.Instance.BeginFrame(9200002);
                Assert.IsFalse(WeatherMapSky.On);
            }
            finally
            {
                Tuning.ClearOverrides();
                Tuning.Instance.BeginFrame(9200003);
            }
        }
    }
}
