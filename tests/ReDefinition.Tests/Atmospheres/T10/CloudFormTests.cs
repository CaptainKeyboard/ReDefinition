using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // Master plan 43 step 2: one form description per type. A type's FORM { SCALE, AMOUNT } is baked into the profile channels g and b; without it the channels are 0 and the
    // layer is the layer of before (the shader's bounds and the lift's headroom are the old expressions); with it the lift's headroom follows the type's amount, so a lift of 1
    // still closes every column.
    [TestClass]
    public class CloudFormTests
    {
        private static CloudLayerDef Layer(Curve amount, Curve scale)
        {
            var type = CloudTypeDef.Slab("Deck", 0.1f, 0.6f);
            type.FormAmount = amount;
            type.FormScale = scale;
            return new CloudLayerDef("layer", 0, 0f, 2100f, new List<CloudTypeDef> { type, CloudTypeDef.Slab("Other", 0.1f, 0.6f) }, 0.045f)
            {
                Coverage = 0.5f,
                EdgeSoftness = 0.2f,
                NoiseAmount = 0.3f
            };
        }

        [TestMethod]
        public void WithoutAFormTheChannelsAreZeroAndTheHeadroomIsTheLayers()
        {
            var table = new LayerTypeTable(Layer(null, null));
            Assert.IsTrue(table.NoiseAmountData.All(v => v == 0f) && table.NoiseScaleData.All(v => v == 0f));
            Assert.AreEqual(0f, table.MaxNoiseAmount);
            CloudLayerDef layer = Layer(null, null);
            Assert.AreEqual(LayerTypeTable.LiftHeadroom(layer), LayerTypeTable.LiftHeadroom(layer, 0.0), 1e-12);
        }

        [TestMethod]
        public void NoShippedTypeHasAFormYet()
        {
            // Step 4 gives the types their forms one by one; until then the shipped sky is the sky of before.
            foreach (CloudLayerSpec spec in CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers)
            {
                var table = new LayerTypeTable(spec.Build(1));
                Assert.AreEqual(0f, table.MaxNoiseAmount, spec.Name);
                Assert.IsTrue(table.NoiseScaleData.All(v => v == 0f), spec.Name + " (without the pannus)");
            }
        }

        [TestMethod]
        public void TheFormIsBakedAndTheLargestAmountIsTheLayers()
        {
            var amount = new Curve(new[] { 0f, 0.5f, 1f }, new[] { 0f, 0f, 2f });
            var scale = new Curve(new[] { 0f, 1f }, new[] { 1f, 1f });
            var table = new LayerTypeTable(Layer(amount, scale));
            int top = LayerTypeTable.ProfileSamples - 1;
            Assert.AreEqual(2f, table.NoiseAmountData[top], 1e-6, "the deck's top");
            Assert.AreEqual(0f, table.NoiseAmountData[0], 1e-6, "the deck's base");
            Assert.AreEqual(1f, table.NoiseScaleData[top / 2], 1e-6);
            Assert.AreEqual(0f, table.NoiseAmountData[LayerTypeTable.ProfileSamples + top], 1e-6, "the other type is unchanged");
            Assert.AreEqual(2f, table.MaxNoiseAmount, 1e-6);
        }

        [TestMethod]
        public void ALiftOfOneClosesEveryColumnWithTheTypesAmount()
        {
            // The headroom with the type's amount: the share saturate((map + (n - 0.5) A (1 + b) + C - 1 - lift H) / e + 0.5) is 0 for n = 1 and map = 1 when lift = 1.
            CloudLayerDef layer = Layer(null, null);
            foreach (double b in new[] { 0.0, 1.0, 3.0 })
            {
                double h = LayerTypeTable.LiftHeadroom(layer, b);
                double best = 1.0 + 0.5 * layer.EffectiveNoiseAmount * (1.0 + b) + layer.EffectiveCoverage - 1.0 - h;
                double share = best / layer.EffectiveEdgeSoftness + 0.5;
                Assert.IsTrue(share <= 1e-9, "b " + b + ": the highest noise value stays below the threshold (" + share + ")");
            }
        }
    }
}
