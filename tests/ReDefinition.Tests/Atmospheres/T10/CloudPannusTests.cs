using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // M2c 4a(1), the pannus: a type's cfg node PANNUS { PROFILE, LIFT, EROSION, AMOUNT } is read, and the type table bakes the pannus' profile, lift, erosion and noise amount for
    // every reader wherever a type has one (no switch since 2026-11-25).
    [TestClass]
    public class CloudPannusTests
    {
        private static CloudLayerDef Layer(string name)
        {
            return CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers.Single(l => l.Name == name).Build(1);
        }

        private static int TypeIndex(CloudLayerDef layer, string name)
        {
            for (int i = 0; i < layer.Types.Count; i++)
                if (layer.Types[i].Name == name) return i;
            Assert.Fail("no type " + name + " in " + layer.Name);
            return -1;
        }

        [TestMethod]
        public void TheNimbostratusTypesCarryAPannusAndTheOthersNone()
        {
            foreach (string layerName in new[] { "Kerbin-weather-rain", "Kerbin-weather-snow" })
            {
                CloudLayerDef layer = Layer(layerName);
                foreach (CloudTypeDef type in layer.Types)
                {
                    bool nimbostratus = type.Name == "Nimbostratus" || type.Name == "Nimbostratus (snow)";
                    Assert.AreEqual(nimbostratus, type.PannusProfile != null, type.Name + " has a pannus profile only if it is a nimbostratus");
                    Assert.AreEqual(nimbostratus, type.PannusLift != null, type.Name + " has a pannus lift only if it is a nimbostratus");
                }
            }
        }

        [TestMethod]
        public void TheTableBakesThePannusProfileAndLift()
        {
            CloudLayerDef layer = Layer("Kerbin-weather-rain");
            int ns = TypeIndex(layer, "Nimbostratus");
            CloudTypeDef type = layer.Types[ns];
            var table = new LayerTypeTable(layer);
            for (int i = 0; i < LayerTypeTable.ProfileSamples; i++)
            {
                double h = i / (double)(LayerTypeTable.ProfileSamples - 1);
                Assert.AreEqual(type.PannusProfile.Evaluate((float)h) * type.ProfileFactor(h, layer.Soft), table.ProfileData[ns * LayerTypeTable.ProfileSamples + i], 1e-6,
                                "the pannus profile at " + h);
                Assert.AreEqual(type.BakedLiftAt(h), table.LiftData[ns * LayerTypeTable.ProfileSamples + i], 1e-6, "the pannus lift at " + h);
            }
            int gap = (int)(0.195 * (LayerTypeTable.ProfileSamples - 1));   // h 0.195: in the gap (0.17 to 0.22)
            Assert.IsTrue(table.LiftData[ns * LayerTypeTable.ProfileSamples + gap] > 0.99f, "the gap above the shreds is closed (lift 1)");
        }

        [TestMethod]
        public void ThePannusErodesAndThinsTheShredsBandOnly()
        {
            CloudLayerDef layer = Layer("Kerbin-weather-rain");
            int ns = TypeIndex(layer, "Nimbostratus");
            var table = new LayerTypeTable(layer);
            int band = (int)(0.05 * (LayerTypeTable.ProfileSamples - 1)), deck = (int)(0.5 * (LayerTypeTable.ProfileSamples - 1));
            Assert.AreEqual(4f, table.ErosionGainData[ns * LayerTypeTable.ProfileSamples + band], 1e-6, "the shreds' band is eroded 5 times harder");
            Assert.AreEqual(-0.9f, table.NoiseAmountData[ns * LayerTypeTable.ProfileSamples + band], 1e-6, "the coarse noise weakened to 0.1 in the shreds' band");
            Assert.AreEqual(0f, table.ErosionGainData[ns * LayerTypeTable.ProfileSamples + deck], 1e-6, "the deck keeps its erosion");
        }

        [TestMethod]
        public void ATypeWithoutAPannusBakesItsOwnForm()
        {
            CloudLayerDef layer = Layer("Kerbin-weather-rain");
            int st = TypeIndex(layer, "Stratus");
            CloudTypeDef type = layer.Types[st];
            var table = new LayerTypeTable(layer);
            for (int i = 0; i < LayerTypeTable.ProfileSamples; i++)
            {
                double h = i / (double)(LayerTypeTable.ProfileSamples - 1);
                Assert.AreEqual(type.Profile.Evaluate((float)h) * type.ProfileFactor(h, layer.Soft), table.ProfileData[st * LayerTypeTable.ProfileSamples + i], 1e-6, "profile at " + h);
                Assert.AreEqual((float)type.ErosionGainAt(h), table.ErosionGainData[st * LayerTypeTable.ProfileSamples + i], 1e-6, "its FORM's erosion at " + h);
            }
        }
    }
}
