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
        private static Curve C(params float[] xy)
        {
            var x = new float[xy.Length / 2];
            var y = new float[xy.Length / 2];
            for (int i = 0; i < x.Length; i++) { x[i] = xy[2 * i]; y[i] = xy[2 * i + 1]; }
            return new Curve(x, y);
        }

        private static CloudLayerDef Layer(string name, string group, float inner, float outer, float sigma, float coverage, float edge, float noise, params CloudTypeDef[] types)
        {
            return new CloudLayerDef(name, 1, inner, outer, types, sigma) { Group = group, Coverage = coverage, EdgeSoftness = edge, NoiseAmount = noise };
        }

        private static CloudLayerDef Rain()
        {
            return Layer("Kerbin-weather-rain", CloudCoverShift.RainGroup, 0f, 2100f, 0.045f, 0.5f, 0.2f, 0.3f,
                new CloudTypeDef("Fog", 0f, 0.1f, C(0f, 0.9f, 0.7f, 0.5f, 1f, 0f), 0.2f),
                new CloudTypeDef("Stratus", 0.1f, 0.5f, C(0f, 0f, 0.12f, 0.8f, 0.5f, 1f, 0.85f, 0.7f, 1f, 0f), 0.15f),
                new CloudTypeDef("Nimbostratus", 0.05f, 1f, C(0f, 0.1f, 0.08f, 0.9f, 0.6f, 1f, 0.9f, 0.6f, 1f, 0f), 0.2f));
        }

        private static CloudLayerDef Cumulus()
        {
            return Layer("Kerbin-cumulus", "", 1700f, 7400f, 0.05f, 0.35f, 0.15f, 0.35f,
                new CloudTypeDef("Stratocumulus", 0f, 0.11f, C(0f, 0f, 0.15f, 0.9f, 0.6f, 1f, 1f, 0f), 0.3f),
                new CloudTypeDef("Cumulus humilis", 0f, 0.21f, C(0f, 0f, 0.08f, 0.9f, 0.55f, 1f, 0.9f, 0.5f, 1f, 0f), 0.45f),
                new CloudTypeDef("Cumulus mediocris", 0f, 0.47f, C(0f, 0f, 0.06f, 0.9f, 0.5f, 1f, 0.85f, 0.6f, 1f, 0f), 0.5f),
                new CloudTypeDef("Cumulus congestus", 0f, 1f, C(0f, 0f, 0.04f, 0.9f, 0.45f, 1f, 0.8f, 0.7f, 1f, 0f), 0.55f),
                new CloudTypeDef("Altocumulus", 0.54f, 0.68f, C(0f, 0f, 0.2f, 0.9f, 0.7f, 0.8f, 1f, 0f), 0.4f));
        }

        private static CloudLayerDef Cumulonimbus()
        {
            return Layer("Kerbin-cumulonimbus", "", 1700f, 11000f, 0.08f, 0.12f, 0.1f, 0.4f,
                new CloudTypeDef("Cumulonimbus calvus", 0f, 0.7f, C(0f, 0.2f, 0.05f, 0.9f, 0.5f, 1f, 0.85f, 0.7f, 1f, 0f), 0.5f),
                new CloudTypeDef("Cumulonimbus incus", 0f, 1f, C(0f, 0.2f, 0.05f, 0.8f, 0.4f, 0.9f, 0.7f, 1f, 0.88f, 0.8f, 1f, 0f), 0.5f));
        }

        private static CloudLayerDef Cirrus()
        {
            return Layer("Kerbin-cirrus", "", 9000f, 11000f, 0.0002f, 0.3f, 0.35f, 0.5f,
                new CloudTypeDef("Cirrus", 0.1f, 0.9f, C(0f, 0f, 0.35f, 0.9f, 0.65f, 0.8f, 1f, 0f), 0.7f),
                new CloudTypeDef("Cirrostratus", 0.2f, 0.8f, C(0f, 0f, 0.3f, 0.7f, 0.7f, 0.7f, 1f, 0f), 0.3f));
        }

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
        public void AtTheLowCoverTheCumulusLayerIsTheNearestToASheetAndTheRainDeckIsHalfOpen()
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
            Assert.AreEqual(0.73, p1[1], 0.06, "cumulus (Stratocumulus) at shift 0");
            Assert.AreEqual(0.0, p1[2], 0.01, "cumulonimbus at map 0.11");
            Assert.AreEqual(0.0, p1[3], 0.01, "cirrus is too thin to close");
            Assert.AreEqual(0.0, c35[0], 0.02, "the rain deck opens at coverage 0.35");
            Assert.AreEqual(0.30, c35[1], 0.06, "the cumulus layer at coverage 0.20 is still a third closed");
            Assert.IsTrue(p1[1] > p1[0] && c35[1] > c35[0], "the cumulus layer is the sheet of the low pick");
        }

        [TestMethod]
        public void AtTheHighCoverTheRainDeckIsClosedAndTheCumulonimbusIsTheTowers()
        {
            // High pick: maps 0.859 / 0.206 / 0.661 / 0.177, type 0.229 everywhere.
            double[] map = { 0.859, 0.206, 0.661, 0.177 };
            CloudLayerDef[] layers = { Rain(), Cumulus(), Cumulonimbus(), Cirrus() };
            Assert.AreEqual(1.0, Closed(layers[0], map[0], 0.229, 0f), 0.01, "a rain deck at map 0.86 closes the sky whatever the shift");
            Assert.AreEqual(1.0, Closed(layers[0], map[0], 0.229, -0.15f), 0.01);
            Assert.AreEqual(0.0, Closed(layers[1], map[1], 0.229, 0f), 0.01, "cumulus at map 0.21 is absent");
            Assert.AreEqual(0.08, Closed(layers[2], map[2], 0.229, 0f), 0.05, "cumulonimbus at map 0.66 is a few towers");
            Assert.AreEqual(0.0, Closed(layers[2], map[2], 0.229, -0.15f), 0.01, "and gone at coverage 0 (the clamp)");
        }
    }
}
