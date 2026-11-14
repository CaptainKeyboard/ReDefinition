using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.T10;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 B3: the shipped cfg's types have the vertical optical depth of their real counterparts. The depth is that of a full column through the type at full water content: the
    // layer's sigma times the type's height times the mean of its profile as the table bakes it (rise by height and density scale included). The ranges are the brief's (fair-weather
    // and thin stratus 0.5 to 5, stratocumulus 5 to 20, cumulus humilis 10 to 40, cirrus 0.03 to 3, towers and rain cloud opaque, fog by its visibility: 3.9 / sigma).
    [TestClass]
    public class CloudOpacityTests
    {
        private static double FullColumnTau(CloudLayerDef layer, int type)
        {
            var table = new LayerTypeTable(layer);
            CloudTypeDef t = layer.Types[type];
            double mean = 0.0;
            for (int q = 0; q < 128; q++) mean += table.ProfileAt(type, (q + 0.5) / 128.0) / 128.0;
            return layer.EffectiveSigmaPerM * (t.TopShare - t.BaseShare) * layer.ThicknessM * mean;
        }

        private static Dictionary<string, double> Shipped()
        {
            var tau = new Dictionary<string, double>();
            foreach (CloudLayerSpec spec in CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers)
            {
                CloudLayerDef layer = spec.Build(1);
                for (int i = 0; i < layer.Types.Count; i++) tau[layer.Types[i].Name] = FullColumnTau(layer, i);
            }
            return tau;
        }

        [TestMethod]
        public void TheShippedTypesHaveTheOpticalDepthOfTheirRealCounterparts()
        {
            Dictionary<string, double> tau = Shipped();
            foreach (var range in new[]
            {
                new { Name = "Stratus", Lo = 0.5, Hi = 6.0 }, new { Name = "Snow stratus", Lo = 0.5, Hi = 6.0 },
                new { Name = "Stratocumulus", Lo = 5.0, Hi = 20.0 }, new { Name = "Cumulus humilis", Lo = 10.0, Hi = 40.0 },
                new { Name = "Altocumulus", Lo = 3.0, Hi = 12.0 }, new { Name = "Cirrus", Lo = 0.03, Hi = 3.0 }, new { Name = "Cirrostratus", Lo = 0.03, Hi = 3.0 },
                new { Name = "Cumulus mediocris", Lo = 30.0, Hi = 120.0 }
            })
                Assert.IsTrue(tau[range.Name] >= range.Lo && tau[range.Name] <= range.Hi, range.Name + " has the full-column optical depth " + tau[range.Name].ToString("0.00") + ", wanted " + range.Lo + " to " + range.Hi);
            foreach (string opaque in new[] { "Nimbostratus", "Snow nimbus", "Cumulus congestus", "Cumulonimbus calvus", "Cumulonimbus incus" })
                Assert.IsTrue(tau[opaque] > 30.0, opaque + " is opaque: " + tau[opaque].ToString("0.0"));
            Assert.IsTrue(tau["Stratus"] < tau["Stratocumulus"] && tau["Stratocumulus"] < tau["Cumulus humilis"] && tau["Cumulus humilis"] < tau["Cumulus congestus"]
                          && tau["Cumulus congestus"] < tau["Cumulonimbus incus"], "the family grows with the cloud's depth");
        }

        [TestMethod]
        public void TheGroundFogHasAVisibilityOfAboutOneHundredMetres()
        {
            CloudLayerDef rain = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers.Single(l => l.Name == "Kerbin-weather-rain").Build(1);
            var table = new LayerTypeTable(rain);
            double peak = 0.0;
            for (int q = 0; q < 64; q++) peak = System.Math.Max(peak, table.ProfileAt(0, q / 63.0));
            double visibility = 3.912 / (rain.EffectiveSigmaPerM * peak);
            Assert.IsTrue(visibility > 60.0 && visibility < 200.0, "fog visibility " + visibility.ToString("0") + " m (dense fog is under 200 m)");
        }
    }
}
