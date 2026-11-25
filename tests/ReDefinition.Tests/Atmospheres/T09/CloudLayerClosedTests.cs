using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The closed share of each layer of Kerbin's stack at the map values K-T2d start 49 read (GameData/ReDefinition/Atmospheres/Bodies/Clouds-Kerbin.cfg): the expected table
    // of the per-layer frames of start 50. The ground view looks 18 degrees up (slant 3.24). Layers in the stack's order: the low stratiform weather layer (rain), cumulus, cumulonimbus, cirrus.
    [TestClass]
    public class CloudLayerClosedTests
    {
        private static CloudLayerDef Rain() { return CloudLookFixtures.Rain(); }
        private static CloudLayerDef Cumulus() { return CloudLookFixtures.Cumulus(); }
        private static CloudLayerDef Cumulonimbus() { return CloudLookFixtures.Cumulonimbus(); }
        private static CloudLayerDef Cirrus() { return CloudLookFixtures.Cirrus(); }

        private const double Slant = 3.24;

        private static double Closed(CloudLayerDef layer, double map, double type, float shift)
        {
            return CloudCoverModel.LayerClosed(layer, new LayerTypeTable(layer), map, type, CloudCoverShift.Apply(layer.Coverage, layer.Group, shift, 0f), Slant);
        }

        [TestMethod]
        public void TheEquivalentThicknessIsTheProfilesHeightIntegralOfTheInterpolatedType()
        {
            // The plain profile's integral is the table of a layer without noise; with the soft water content the table carries the rise by height (CloudSoftDensityTests).
            CloudLayerDef rain = Rain();
            rain.NoiseAmount = 0f;
            var table = new LayerTypeTable(rain);
            // Fog at type 0: 0.1 of 2100 m times the mean of its profile (0.9 to 0.5 over 0.7, then 0.5 to 0 over 0.3: 0.7 * 0.7 + 0.3 * 0.25 = 0.565), times its density scale (1).
            Assert.AreEqual(210.0 * 0.565 * rain.Types[0].DensityScale, CloudCoverModel.EquivalentThicknessM(rain, table, 0.0), 2.0);
            // Stratus at type 0.5: 0.4 of 2100 m times 0.74 (CloudCoverModel.StratusThicknessM is 0.4 * 2100 * 0.74), times the type's density scale (E40 B3: the table carries it).
            Assert.AreEqual(CloudCoverModel.StratusThicknessM * rain.Types[1].DensityScale, CloudCoverModel.EquivalentThicknessM(rain, table, 0.5), 15.0);
        }

        [TestMethod]
        public void AtTheLowCoverTheCumulusLayerIsTheMostClosedLayerAndTheRainLowStratiformIsHalfOpen()
        {
            // K-T2d start 49, low pick: maps 0.406 / 0.665 / 0.110 / 0.501, type 0 everywhere. The model's closed share (tau above 4.6) of the sky at 18 degrees up.
            double[] map = { 0.406, 0.665, 0.110, 0.501 };
            CloudLayerDef[] layers = { Rain(), Cumulus(), Cumulonimbus(), Cirrus() };
            double[] p1 = new double[4], c35 = new double[4];
            for (int k = 0; k < 4; k++)
            {
                p1[k] = Closed(layers[k], map[k], 0.0, 0f);
                c35[k] = Closed(layers[k], map[k], 0.0, -0.15f);
            }
            // The point values are the cfg's output (they were rewritten with each cfg change) and are not asserted; the orderings are the rules. The cumulus layer is a cell field
            // (noise amount 1): the noise decides where the cloud stands, the map how much of the sky it fills.
            Assert.AreEqual(0.0, p1[3], 0.01, "cirrus is too thin to close");
            for (int k = 0; k < 4; k++) Assert.IsTrue(c35[k] <= p1[k] + 1e-9, "the shift opens layer " + k + ": " + p1[k] + " -> " + c35[k]);
            Assert.IsTrue(p1[1] > p1[0] && c35[1] > c35[0], "the cumulus layer is the most closed layer of the low pick: " + p1[1] + " against " + p1[0]);
            Assert.IsTrue(p1[1] > 0.0 && p1[1] < 0.8, "the cumulus layer closes part of the sky and is no longer a sheet: " + p1[1]);
        }

        [TestMethod]
        public void AtTheHighCoverTheRainLowStratiformIsClosedAndTheCumulonimbusIsTheTowers()
        {
            // High pick: maps 0.859 / 0.206 / 0.661 / 0.177, type 0.229 everywhere.
            double[] map = { 0.859, 0.206, 0.661, 0.177 };
            CloudLayerDef[] layers = { Rain(), Cumulus(), Cumulonimbus(), Cirrus() };
            Assert.AreEqual(1.0, Closed(layers[0], map[0], 0.229, 0f), 0.01, "a low stratiform rain layer at map 0.86 closes the sky whatever the shift");
            Assert.AreEqual(1.0, Closed(layers[0], map[0], 0.229, -0.15f), 0.01);
            // At amount 1 the noise lifts the threshold by up to 0.5, so the cumulus layer at a map of 0.21 is a few cells, not absent.
            double cumulus = Closed(layers[1], map[1], 0.229, 0f), towers = Closed(layers[2], map[2], 0.229, 0f);
            Assert.IsTrue(cumulus > 0.0 && cumulus < 0.5, "cumulus at map 0.21 is a few cells: " + cumulus);
            Assert.IsTrue(towers > 0.0 && towers < 0.5, "cumulonimbus at map 0.66 is a few towers: " + towers);
            Assert.AreEqual(0.0, Closed(layers[2], map[2], 0.229, -0.15f), 0.01, "and gone at coverage 0 (the clamp)");
        }
    }
}
