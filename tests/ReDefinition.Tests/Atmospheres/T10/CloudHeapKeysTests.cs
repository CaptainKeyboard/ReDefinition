using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.T09;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // CLOUDR1: the per-type cfg keys heap (P2), edgePower (P1) and cavity (P3), their defaults (today's pass), the global override keys and the cover meaning that follows the exponent.
    [TestClass]
    public class CloudHeapKeysTests
    {
        private static int frame = 8100000;

        private static void Set(string key, float value)
        {
            Tuning.SetOverride(key, value);
            Tuning.Instance.BeginFrame(++frame);
        }

        [TestCleanup]
        public void Clean()
        {
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(++frame);
        }

        private const string Prof = "PROFILE{key0 = 0 0;key1 = 0.5 1;key2 = 1 0}";

        [TestMethod]
        public void TheKeysReadFromTheTypeNodeAndDefaultToTodaysPass()
        {
            BodyParseResult r = CloudLayerSchemaTests.Parse("LAYER{name = A;innerAltitudeM = 100;outerAltitudeM = 900;"
                + "TYPE{name = Plain;baseShare = 0;topShare = 1;" + Prof + "}"
                + "TYPE{name = Heap;baseShare = 0;topShare = 1;heap = billow;edgePower = 0.5;cavity = 0.7;" + Prof + "}"
                + "TYPE{name = Rough;baseShare = 0;topShare = 1;heap = Rough;" + Prof + "}"
                + "TYPE{name = Bad;baseShare = 0;topShare = 1;heap = nonsense;" + Prof + "}}");
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            CloudLayerSpec a = r.Climate.Clouds.Layers.Single();
            Assert.AreEqual(0, a.Types[0].Heap);
            Assert.AreEqual(0f, a.Types[0].EdgePower);
            Assert.AreEqual(0f, a.Types[0].Cavity);
            Assert.AreEqual(1, a.Types[1].Heap);
            Assert.AreEqual(0.5f, a.Types[1].EdgePower, 1e-6f);
            Assert.AreEqual(0.7f, a.Types[1].Cavity, 1e-6f);
            Assert.AreEqual(2, a.Types[2].Heap);
            Assert.AreEqual(0, a.Types[3].Heap, "an unknown preset is none");
            CloudLayerDef def = a.Build(1);
            Assert.AreEqual(1, def.Types[1].Heap);
            Assert.AreEqual(0.5f, def.Types[1].EdgePower, 1e-6f);
            Assert.AreEqual(0.7f, def.Types[1].Cavity, 1e-6f);
        }

        [TestMethod]
        public void WithoutTheKeysEveryTypeKeepsTheLayersExponentAndTheHeapIsOff()
        {
            foreach (CloudLayerDef layer in new[] { CloudLookFixtures.Rain(), CloudLookFixtures.Cumulus(), CloudLookFixtures.Cumulonimbus(), CloudLookFixtures.Cirrus() })
            {
                var table = new LayerTypeTable(layer);
                for (int t = 0; t < layer.Types.Count; t++)
                {
                    Assert.AreEqual(layer.EffectiveWaterPower, layer.EffectiveWaterPowerOf(layer.Types[t]), 0f, layer.Name);
                    Assert.AreEqual(0, table.HeapOfType(t));
                    Assert.AreEqual(0f, table.EdgeOfType(t));
                    Assert.AreEqual(0f, table.CavityOfType(t));
                }
                for (int i = 0; i <= 20; i++)
                    Assert.AreEqual(LayerDensity.Share(layer, i / 20.0), LayerDensity.Share(layer, i / 20.0, table, 0.5), 0.0, layer.Name + " " + i);
            }
        }

        [TestMethod]
        public void AnEdgePowerReplacesTheLayersExponentForItsTypeAndTheCoverMeaningFollows()
        {
            CloudLayerDef layer = CloudLookFixtures.Cumulus();
            var table = new LayerTypeTable(layer);
            double closedBefore = layer.SoftClosed;
            layer.Types[1].EdgePower = 0.5f;
            Assert.AreEqual(0.5f, layer.EffectiveWaterPowerOf(layer.Types[1]), 0f);
            Assert.AreEqual(layer.EffectiveWaterPower, layer.EffectiveWaterPowerOf(layer.Types[2]), 0f);
            Assert.AreEqual(0.5f, table.EdgeOfType(1), 0f);
            Assert.AreEqual(0f, table.EdgeOfType(2), 0f);
            // A type value of exactly the second type reads its exponent; between two types the exponents blend by the weight of the second (the shader's lerp).
            double v1 = 1.0 / (layer.Types.Count - 1), v2 = 2.0 / (layer.Types.Count - 1);
            Assert.AreEqual(0.5f, layer.EffectiveWaterPowerAt(table, v1), 1e-6f);
            Assert.AreEqual(0.5 * (0.5 + layer.EffectiveWaterPower), layer.EffectiveWaterPowerAt(table, 0.5 * (v1 + v2)), 1e-6);
            // The share at a map value is the share to the type's exponent: a smaller exponent makes the rim opaque sooner.
            double plain = LayerDensity.Share(layer, 0.4, table, v2), crisp = LayerDensity.Share(layer, 0.4, table, v1);
            Assert.IsTrue(plain > 0.0 && plain < 1.0);
            Assert.AreEqual(Math.Pow(Math.Pow(plain, 1.0 / layer.EffectiveWaterPower), 0.5), crisp, 1e-9);
            Assert.IsTrue(crisp > plain);
            // The water content at the outline (the cover meaning) is taken back through the type's exponent: c = (0.92 / tau)^(1 / P), so a smaller P gives a smaller content.
            Assert.IsTrue(layer.SoftClosed < closedBefore, "closed " + layer.SoftClosed + " against " + closedBefore);
            layer.Types[1].EdgePower = 0f;
            Assert.AreEqual(closedBefore, layer.SoftClosed, 1e-12, "back to the default");
        }

        [TestMethod]
        public void TheGlobalOverridesForceHeapExponentAndCavity()
        {
            CloudLayerDef layer = CloudLookFixtures.Cumulus();
            layer.Types[1].Heap = 1;
            layer.Types[1].Cavity = 0.6f;
            var table = new LayerTypeTable(layer);
            Assert.AreEqual(1, table.HeapOfType(1));
            Assert.AreEqual(0, table.HeapOfType(2));
            Assert.AreEqual(0.6f, table.CavityOfType(1), 1e-6f);
            Assert.AreEqual(0f, table.CavityOfType(2), 0f, "no heap, no cavity");
            Set(CloudLookTuning.TuneHeap, 0f);
            Assert.AreEqual(0, table.HeapOfType(1), "cloud.heap 0: none anywhere");
            Assert.AreEqual(0f, table.CavityOfType(1), 0f);
            Set(CloudLookTuning.TuneHeap, 2f);
            Assert.AreEqual(2, table.HeapOfType(0));
            Assert.AreEqual(2, table.HeapAtTypeValue(0.0));
            Set(CloudLookTuning.TuneCavity, 0.25f);
            Assert.AreEqual(0.25f, table.CavityOfType(3), 1e-6f);
            Set(CloudLookTuning.TuneEdgePower, 0.7f);
            Assert.AreEqual(0.7f, table.EdgeOfType(3), 1e-6f);
            Assert.AreEqual(0.7f, layer.EffectiveWaterPowerOf(layer.Types[3]), 1e-6f);
            Tuning.ClearOverrides();
            Tuning.Instance.BeginFrame(++frame);
            Assert.AreEqual(1, table.HeapOfType(1));
            Assert.AreEqual(0f, table.EdgeOfType(3), 0f);
        }
    }
}
