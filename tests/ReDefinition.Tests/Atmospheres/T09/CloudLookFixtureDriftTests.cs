using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.T10;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The layers of CloudLookFixtures against the shipped GameData/ReDefinition/Atmospheres/Bodies/Clouds-Kerbin.cfg, read through the body parser the way the game reads it: a
    // number of the file that changes (the cumulus layer's noise scale 5000 -> 500 and amount 0.35 -> 1 did, E40) must change in the fixtures in the same commit.
    [TestClass]
    public class CloudLookFixtureDriftTests
    {
        private static void SameLayer(CloudLayerDef fixture, CloudLayerSpec shipped)
        {
            string n = fixture.Name;
            Assert.AreEqual(shipped.InnerAltitudeM, fixture.InnerAltitudeM, 0f, n + " inner altitude");
            Assert.AreEqual(shipped.OuterAltitudeM, fixture.OuterAltitudeM, 0f, n + " outer altitude");
            Assert.AreEqual(shipped.SigmaPerM, fixture.SigmaPerM, 1e-7f, n + " sigma");
            Assert.AreEqual(shipped.Coverage, fixture.Coverage, 1e-6f, n + " coverage");
            Assert.AreEqual(shipped.EdgeSoftness, fixture.EdgeSoftness, 1e-6f, n + " edge softness");
            Assert.AreEqual(shipped.NoiseAmount, fixture.NoiseAmount, 1e-6f, n + " noise amount");
            Assert.AreEqual(shipped.NoiseScaleM, fixture.NoiseScaleM, 1e-3f, n + " noise scale");
            Assert.AreEqual(shipped.Group, fixture.Group, n + " group");
            Assert.AreEqual(shipped.Kind, fixture.Kind, n + " kind");
            Assert.AreEqual(shipped.Deck == null, fixture.Deck == null, n + " deck rule present");
            if (shipped.Deck != null)
            {
                Assert.AreEqual(shipped.Deck.FogSpreadK, fixture.Deck.FogSpreadK, 1e-6f, n + " deck fog spread");
                Assert.AreEqual(shipped.Deck.FogSpreadBandK, fixture.Deck.FogSpreadBandK, 1e-6f, n + " deck fog spread band");
                Assert.AreEqual(shipped.Deck.FogWindMs, fixture.Deck.FogWindMs, 1e-6f, n + " deck fog wind");
                Assert.AreEqual(shipped.Deck.FogWindBandMs, fixture.Deck.FogWindBandMs, 1e-6f, n + " deck fog wind band");
                Assert.AreEqual(shipped.Deck.PrecipFrom, fixture.Deck.PrecipFrom, 1e-6f, n + " deck precipitation from");
                Assert.AreEqual(shipped.Deck.PrecipFullAt, fixture.Deck.PrecipFullAt, 1e-6f, n + " deck precipitation full");
                Assert.AreEqual(shipped.Deck.PrecipType, fixture.Deck.PrecipType, 1e-6f, n + " deck precipitation type");
                Assert.AreEqual(shipped.Deck.FogType, fixture.Deck.FogType, 1e-6f, n + " deck fog type");
                Assert.AreEqual(shipped.Deck.Fog, fixture.Deck.Fog, n + " deck fog");
                Assert.AreEqual(shipped.Deck.RainFloor, fixture.Deck.RainFloor, 1e-6f, n + " deck rain floor");
            }
            Assert.AreEqual(shipped.Types.Count, fixture.Types.Count, n + " type count");
            for (int i = 0; i < shipped.Types.Count; i++)
            {
                CloudTypeSpec s = shipped.Types[i];
                CloudTypeDef f = fixture.Types[i];
                string t = n + " type " + s.Name;
                Assert.AreEqual(s.Name, f.Name, t + " name");
                Assert.AreEqual(s.BaseShare, f.BaseShare, 1e-6f, t + " base");
                Assert.AreEqual(s.TopShare, f.TopShare, 1e-6f, t + " top");
                Assert.AreEqual(s.Erosion, f.Erosion, 1e-6f, t + " erosion");
                Assert.AreEqual(s.Profile.Count, f.Profile.Count, t + " profile keys");
                for (int k = 0; k < s.Profile.Count; k++)
                {
                    Assert.AreEqual(s.Profile.KeyAt(k), f.Profile.KeyAt(k), 1e-6f, t + " profile key " + k);
                    Assert.AreEqual(s.Profile.ValueAt(k), f.Profile.ValueAt(k), 1e-6f, t + " profile value " + k);
                }
            }
        }

        [TestMethod]
        public void TheFixturesCarryTheNumbersOfTheShippedKerbinFile()
        {
            var shipped = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers;
            CloudLayerDef[] fixtures = { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() };
            foreach (CloudLayerDef fixture in fixtures)
            {
                CloudLayerSpec spec = shipped.SingleOrDefault(l => l.Name == fixture.Name);
                Assert.IsNotNull(spec, fixture.Name + " is not in the shipped file");
                SameLayer(fixture, spec);
            }
        }

        [TestMethod]
        public void TheCumulonimbusScaleStaysWhereTheNadirSeriesMeasuredItRight()
        {
            // e40-fn: the towers of the map 0.8 are 14 to 16 km wide at 8000 m (cells of 10 to 30 km); 3000 m gives 7 to 9 km. A change of these two numbers is a new measurement.
            CloudLayerSpec cb = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers.Single(l => l.Name == "Kerbin-cumulonimbus");
            Assert.AreEqual(8000f, cb.NoiseScaleM, 0f);
            Assert.AreEqual(0.4f, cb.NoiseAmount, 0f);
        }

        [TestMethod]
        public void TheCumulusLayerOfTheFileIsACellFieldNotASheet()
        {
            CloudLayerSpec cumulus = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers.Single(l => l.Name == "Kerbin-cumulus");
            Assert.AreEqual(500f, cumulus.NoiseScaleM, 0f);
            Assert.AreEqual(1f, cumulus.NoiseAmount, 0f);
        }
    }
}
