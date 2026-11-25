using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // M2c 4a(1), the pannus: a type's cfg node PANNUS { PROFILE, LIFT } is read, the switch cloud.pannus is off by default (the type of before, bit for bit), and on it the type table
    // bakes the pannus' profile and lift for every reader.
    [TestClass]
    public class CloudPannusTests
    {
        // An override takes effect at the next tuning frame (TuningTable.BeginFrame); each step here is a frame of its own.
        private static int frame = 7700000;

        private static void Set(float pannus)
        {
            Tuning.SetOverride(CloudLookTuning.TunePannus, pannus);
            Tuning.BeginFrame(++frame);
        }

        private static void Clear()
        {
            Tuning.ClearOverrides();
            Tuning.BeginFrame(++frame);
        }

        [TestCleanup]
        public void Cleanup()
        {
            Clear();
        }

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
                    bool nimbostratus = type.Name == "Nimbostratus" || type.Name == "Snow nimbostratus";
                    Assert.AreEqual(nimbostratus, type.PannusProfile != null, type.Name + " has a pannus profile only if it is a nimbostratus");
                    Assert.AreEqual(nimbostratus, type.PannusLift != null, type.Name + " has a pannus lift only if it is a nimbostratus");
                }
            }
        }

        [TestMethod]
        public void ThePannusIsOffByDefaultAndTheTableIsTheTypeOfBefore()
        {
            Clear();
            Assert.IsFalse(CloudLookTuning.Pannus, "cloud.pannus is off by default");
            CloudLayerDef layer = Layer("Kerbin-weather-rain");
            int ns = TypeIndex(layer, "Nimbostratus");
            var table = new LayerTypeTable(layer);
            CloudTypeDef type = layer.Types[ns];
            for (int i = 0; i < LayerTypeTable.ProfileSamples; i++)
            {
                double h = i / (double)(LayerTypeTable.ProfileSamples - 1);
                Assert.AreEqual(type.Profile.Evaluate((float)h) * type.ProfileFactor(h, layer.Soft), table.ProfileData[ns * LayerTypeTable.ProfileSamples + i], 1e-6, "profile at " + h);
                Assert.AreEqual(type.BakedLiftAt(h), table.LiftData[ns * LayerTypeTable.ProfileSamples + i], 1e-6, "lift at " + h);
            }
        }

        [TestMethod]
        public void ThePannusSwitchBakesThePannusProfileAndLiftAndBack()
        {
            Clear();
            CloudLayerDef layer = Layer("Kerbin-weather-rain");
            int ns = TypeIndex(layer, "Nimbostratus");
            CloudTypeDef type = layer.Types[ns];
            var table = new LayerTypeTable(layer);
            int band = (int)(0.05 * (LayerTypeTable.ProfileSamples - 1));   // h 0.05: in the shreds' band
            int gap = (int)(0.195 * (LayerTypeTable.ProfileSamples - 1));   // h 0.195: in the gap (0.17 to 0.22)
            float offBand = table.ProfileData[ns * LayerTypeTable.ProfileSamples + band], offLift = table.LiftData[ns * LayerTypeTable.ProfileSamples + gap];

            Set(1f);
            Assert.IsTrue(CloudLookTuning.Pannus);
            double hb = band / (double)(LayerTypeTable.ProfileSamples - 1), hg = gap / (double)(LayerTypeTable.ProfileSamples - 1);
            Assert.AreEqual(type.PannusProfile.Evaluate((float)hb) * type.ProfileFactor(hb, layer.Soft), table.ProfileData[ns * LayerTypeTable.ProfileSamples + band], 1e-6,
                            "on: the pannus profile in the shreds' band");
            Assert.AreEqual(type.BakedLiftAt(hg, true), table.LiftData[ns * LayerTypeTable.ProfileSamples + gap], 1e-6, "on: the pannus lift in the gap");
            Assert.IsTrue(table.LiftData[ns * LayerTypeTable.ProfileSamples + gap] > 0.99f, "the gap above the shreds is closed (lift 1)");
            Assert.AreNotEqual(offLift, table.LiftData[ns * LayerTypeTable.ProfileSamples + gap], "the lift changed with the switch");

            Clear();
            Assert.AreEqual(offBand, table.ProfileData[ns * LayerTypeTable.ProfileSamples + band], 1e-6, "off again: the profile of before");
            Assert.AreEqual(offLift, table.LiftData[ns * LayerTypeTable.ProfileSamples + gap], 1e-6, "off again: the lift of before");
        }

        [TestMethod]
        public void ThePannusErosionIsZeroOffAndTheCurveInTheShredsBandOn()
        {
            Clear();
            CloudLayerDef layer = Layer("Kerbin-weather-rain");
            int ns = TypeIndex(layer, "Nimbostratus");
            var table = new LayerTypeTable(layer);
            int st = TypeIndex(layer, "Stratus");
            float[] stratusOff = Enumerable.Range(0, LayerTypeTable.ProfileSamples).Select(i => table.ErosionGainData[st * LayerTypeTable.ProfileSamples + i]).ToArray();
            for (int i = 0; i < LayerTypeTable.ProfileSamples; i++)
                Assert.AreEqual(0f, table.ErosionGainData[ns * LayerTypeTable.ProfileSamples + i], "off: the nimbostratus' erosion is as before");
            Set(1f);
            int band = (int)(0.05 * (LayerTypeTable.ProfileSamples - 1)), deck = (int)(0.5 * (LayerTypeTable.ProfileSamples - 1));
            Assert.AreEqual(2f, table.ErosionGainData[ns * LayerTypeTable.ProfileSamples + band], 1e-6, "on: the shreds' band is eroded 3 times harder");
            Assert.AreEqual(0f, table.ErosionGainData[ns * LayerTypeTable.ProfileSamples + deck], 1e-6, "on: the deck keeps its erosion");
            for (int i = 0; i < LayerTypeTable.ProfileSamples; i++)
                Assert.AreEqual(stratusOff[i], table.ErosionGainData[st * LayerTypeTable.ProfileSamples + i], "a type without a pannus keeps its own form");
        }

        [TestMethod]
        public void ATypeWithoutAPannusIsTheSameWithTheSwitchOn()
        {
            Clear();
            CloudLayerDef layer = Layer("Kerbin-weather-rain");
            int st = TypeIndex(layer, "Stratus");
            var table = new LayerTypeTable(layer);
            float[] before = table.ProfileData.Skip(st * LayerTypeTable.ProfileSamples).Take(LayerTypeTable.ProfileSamples).ToArray();
            float[] liftBefore = table.LiftData.Skip(st * LayerTypeTable.ProfileSamples).Take(LayerTypeTable.ProfileSamples).ToArray();
            Set(1f);
            CollectionAssert.AreEqual(before, table.ProfileData.Skip(st * LayerTypeTable.ProfileSamples).Take(LayerTypeTable.ProfileSamples).ToArray());
            CollectionAssert.AreEqual(liftBefore, table.LiftData.Skip(st * LayerTypeTable.ProfileSamples).Take(LayerTypeTable.ProfileSamples).ToArray());
        }
    }
}
