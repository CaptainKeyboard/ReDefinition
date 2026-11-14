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
        private static void SameCurve(Curve shipped, Curve fixture, string what)
        {
            Assert.AreEqual(shipped == null, fixture == null, what + " present");
            if (shipped == null) return;
            Assert.AreEqual(shipped.Count, fixture.Count, what + " keys");
            for (int k = 0; k < shipped.Count; k++)
            {
                Assert.AreEqual(shipped.KeyAt(k), fixture.KeyAt(k), 1e-6f, what + " key " + k);
                Assert.AreEqual(shipped.ValueAt(k), fixture.ValueAt(k), 1e-6f, what + " value " + k);
            }
        }

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
            Assert.AreEqual(shipped.SoftnessScale, fixture.SoftnessScale, 1e-6f, n + " softness scale");
            Assert.AreEqual(shipped.WaterPower, fixture.WaterPower, 1e-6f, n + " water power");
            Assert.AreEqual(shipped.FairType, fixture.FairType, 1e-6f, n + " fair type");
            Assert.AreEqual(shipped.RainType, fixture.RainType, 1e-6f, n + " rain type");
            Assert.AreEqual(shipped.AltoType, fixture.AltoType, 1e-6f, n + " alto type");
            Assert.AreEqual(shipped.AltoFrom, fixture.AltoFrom, 1e-6f, n + " alto from");
            Assert.AreEqual(shipped.AltoFull, fixture.AltoFull, 1e-6f, n + " alto full");
            Assert.AreEqual(shipped.TypePatchKm, fixture.TypePatchKm, 1e-6f, n + " type patch km");
            Assert.AreEqual(shipped.TypePatchDepths, fixture.TypePatchDepths, 1e-6f, n + " type patch depths");
            Assert.AreEqual(shipped.TypePatchBlend, fixture.TypePatchBlend, 1e-6f, n + " type patch blend");
            Assert.AreEqual(shipped.Meso == null, fixture.Meso == null, n + " has a meso pattern");
            if (shipped.Meso != null)
            {
                Assert.AreEqual(shipped.Meso.ScaleKm, fixture.Meso.ScaleKm, 1e-6f, n + " meso scale km");
                Assert.AreEqual(shipped.Meso.ScaleDepths, fixture.Meso.ScaleDepths, 1e-6f, n + " meso scale depths");
                Assert.AreEqual(shipped.Meso.Octaves, fixture.Meso.Octaves, n + " meso octaves");
                Assert.AreEqual(shipped.Meso.Amount, fixture.Meso.Amount, 1e-6f, n + " meso amount");
                Assert.AreEqual(shipped.Meso.Edge, fixture.Meso.Edge, 1e-6f, n + " meso edge");
                Assert.AreEqual(shipped.Meso.Shoulder, fixture.Meso.Shoulder, 1e-6f, n + " meso shoulder");
                Assert.AreEqual(shipped.Meso.Cells, fixture.Meso.Cells, n + " meso cells");
                Assert.AreEqual(shipped.Meso.Stretch, fixture.Meso.Stretch, 1e-6f, n + " meso stretch");
                Assert.AreEqual(shipped.Meso.RainKeep, fixture.Meso.RainKeep, 1e-6f, n + " meso rain keep");
            }
            Assert.AreEqual(shipped.LightAmbient, fixture.LightAmbient, 1e-6f, n + " light ambient");
            Assert.AreEqual(shipped.FibreStretch, fixture.FibreStretch, 1e-6f, n + " fibre stretch");
            Assert.AreEqual(shipped.VerticalScale, fixture.VerticalScale, 1e-6f, n + " vertical scale");
            Assert.AreEqual(shipped.LightIsotropy, fixture.LightIsotropy, 1e-6f, n + " light isotropy");
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
                Assert.AreEqual(s.BaseGain, f.BaseGain, 1e-6f, t + " base gain");
                Assert.AreEqual(s.RiseShare, f.RiseShare, 1e-6f, t + " rise share");
                Assert.AreEqual(s.DensityScale, f.DensityScale, 1e-6f, t + " density scale");
                Assert.AreEqual(s.BaseLight, f.BaseLight, 1e-6f, t + " base light");
                Assert.AreEqual(s.HeadLight, f.HeadLight, 1e-6f, t + " head light");
                Assert.AreEqual(s.BaseAmbient, f.BaseAmbient, 1e-6f, t + " base ambient");
                Assert.AreEqual(s.HeadAmbient, f.HeadAmbient, 1e-6f, t + " head ambient");
                Assert.AreEqual(s.Lift == null, f.Lift == null, t + " has a lift curve");
                if (s.Lift != null)
                {
                    Assert.AreEqual(s.Lift.Count, f.Lift.Count, t + " lift keys");
                    for (int k = 0; k < s.Lift.Count; k++)
                    {
                        Assert.AreEqual(s.Lift.KeyAt(k), f.Lift.KeyAt(k), 1e-6f, t + " lift key " + k);
                        Assert.AreEqual(s.Lift.ValueAt(k), f.Lift.ValueAt(k), 1e-6f, t + " lift value " + k);
                    }
                }
                Assert.AreEqual(s.Climate == null, f.Climate == null, t + " has a climate");
                if (s.Climate != null)
                {
                    Assert.AreEqual(s.Climate.Weight, f.Climate.Weight, 1e-6f, t + " climate weight");
                    SameCurve(s.Climate.Latitude, f.Climate.Latitude, t + " climate latitude");
                    SameCurve(s.Climate.Surface, f.Climate.Surface, t + " climate surface");
                    SameCurve(s.Climate.Humidity, f.Climate.Humidity, t + " climate humidity");
                    SameCurve(s.Climate.Precipitation, f.Climate.Precipitation, t + " climate precipitation");
                }
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
            // E40 L2 D1: 5000 m (about 11 km towers by interpolation, not measured at nadir) so that the cloud is separate towers with the cfg's LIFT, not a slab; the amount stays 0.4.
            CloudLayerSpec cb = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers.Single(l => l.Name == "Kerbin-cumulonimbus");
            Assert.AreEqual(5000f, cb.NoiseScaleM, 0f);
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
