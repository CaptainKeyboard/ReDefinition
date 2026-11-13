using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // The closed share of each layer of Kerbin's stack at the map values K-T2d start 49 read (GameData/ReDefinition/Atmospheres/Bodies/Clouds-Kerbin.cfg): the expected table
    // of the per-layer frames of start 50. The ground view looks 18 degrees up (slant 3.24). Layers in the stack's order: the weather deck (rain), cumulus, cumulonimbus, cirrus.
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
            CloudLayerDef rain = Rain();
            var table = new LayerTypeTable(rain);
            // Fog at type 0: 0.1 of 2100 m times the mean of its profile (0.9 to 0.5 over 0.7, then 0.5 to 0 over 0.3: 0.7 * 0.7 + 0.3 * 0.25 = 0.565).
            Assert.AreEqual(210.0 * 0.565, CloudCoverModel.EquivalentThicknessM(rain, table, 0.0), 2.0);
            // Stratus at type 0.5: 0.4 of 2100 m times 0.74 (CloudCoverModel.StratusThicknessM is 0.4 * 2100 * 0.74).
            Assert.AreEqual(CloudCoverModel.StratusThicknessM, CloudCoverModel.EquivalentThicknessM(rain, table, 0.5), 15.0);
        }

        [TestMethod]
        public void AtTheLowCoverTheCumulusLayerIsTheMostClosedLayerAndTheRainDeckIsHalfOpen()
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
            Assert.AreEqual(0.34, p1[0], 0.06, "rain deck (Fog at type 0) at shift 0");
            // The cumulus layer is the cell field of the shipped cfg (noise amount 1, 500 m): 0.58 at shift 0 and 0.43 at coverage 0.20; the sheet of the former cfg (amount 0.35)
            // gave 0.73 and 0.30. The noise decides where the cloud stands, the map how much of the sky it fills.
            Assert.AreEqual(0.58, p1[1], 0.04, "cumulus (Stratocumulus) at shift 0");
            Assert.AreEqual(0.0, p1[2], 0.01, "cumulonimbus at map 0.11");
            Assert.AreEqual(0.0, p1[3], 0.01, "cirrus is too thin to close");
            Assert.AreEqual(0.0, c35[0], 0.02, "the rain deck opens at coverage 0.35");
            Assert.AreEqual(0.43, c35[1], 0.04, "the cumulus layer at coverage 0.20 is still closed over two fifths of the sky");
            Assert.IsTrue(p1[1] > p1[0] && c35[1] > c35[0], "the cumulus layer is the most closed layer of the low pick");
            Assert.IsTrue(p1[1] < 0.8, "and no longer a sheet");
        }

        [TestMethod]
        public void AtTheHighCoverTheRainDeckIsClosedAndTheCumulonimbusIsTheTowers()
        {
            // High pick: maps 0.859 / 0.206 / 0.661 / 0.177, type 0.229 everywhere.
            double[] map = { 0.859, 0.206, 0.661, 0.177 };
            CloudLayerDef[] layers = { Rain(), Cumulus(), Cumulonimbus(), Cirrus() };
            Assert.AreEqual(1.0, Closed(layers[0], map[0], 0.229, 0f), 0.01, "a rain deck at map 0.86 closes the sky whatever the shift");
            Assert.AreEqual(1.0, Closed(layers[0], map[0], 0.229, -0.15f), 0.01);
            // At amount 1 the noise lifts the threshold by up to 0.5, so the cumulus layer at a map of 0.21 is a few cells (0.13), not absent as it was at amount 0.35.
            Assert.AreEqual(0.13, Closed(layers[1], map[1], 0.229, 0f), 0.04, "cumulus at map 0.21 is a few cells");
            Assert.AreEqual(0.08, Closed(layers[2], map[2], 0.229, 0f), 0.05, "cumulonimbus at map 0.66 is a few towers");
            Assert.AreEqual(0.0, Closed(layers[2], map[2], 0.229, -0.15f), 0.01, "and gone at coverage 0 (the clamp)");
        }
    }
}
