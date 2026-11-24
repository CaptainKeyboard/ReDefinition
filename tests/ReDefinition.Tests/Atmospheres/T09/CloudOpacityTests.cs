using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.T10;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 B3: the shipped cfg's types have the vertical optical depth of their real counterparts. The depth is that of a full column through the type at full water content: the
    // layer's sigma times the type's height times the mean of its profile as the table bakes it (rise by height and density scale included).
    // The one primary source for a range is the ISCCP cloud classification (https://isccp.giss.nasa.gov/cloudtypes.html, figure "ISCCP cloud classification"): optical-thickness
    // bins 0 to 3.6, 3.6 to 23 and 23 to 379 for the low classes cumulus / stratocumulus / stratus, the middle classes altocumulus / altostratus / nimbostratus and the high classes
    // cirrus / cirrostratus / deep convection. Its own text: the names "represent only an approximate climatological relationship" to the classical cloud types. A type is held
    // to its class's range where the class names the genus; for the cumulus species, the altocumulus, the cirrostratus and the cumulonimbus the shipped values lie outside their
    // class (humilis 26, mediocris 59, congestus 178 against cumulus below 3.6; altocumulus 9.6 against below 3.6; cirrostratus 0.4 against 3.6 to 23; cumulonimbus 456 and 552
    // against deep convection 23 to 379): no range is asserted for them until a source per genus is chosen (owner decision). The earlier ranges of these rows (cumulus humilis 10
    // to 40, mediocris 30 to 120, altocumulus 3 to 12, cirrostratus 0.03 to 3, "opaque" above 30) had no primary source.
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
                // ISCCP low class "stratus" and middle class "nimbostratus": 23 to 379; low class "stratocumulus": 3.6 to 23; high class "cirrus": below 3.6.
                new { Name = "Stratus", Lo = 23.0, Hi = 379.0 }, new { Name = "Snow stratus", Lo = 23.0, Hi = 379.0 },
                new { Name = "Nimbostratus", Lo = 23.0, Hi = 379.0 }, new { Name = "Snow nimbostratus", Lo = 23.0, Hi = 379.0 },
                new { Name = "Stratocumulus", Lo = 3.6, Hi = 23.0 }, new { Name = "Cirrus", Lo = 0.0, Hi = 3.6 }
            })
                Assert.IsTrue(tau[range.Name] >= range.Lo && tau[range.Name] <= range.Hi, range.Name + " has the full-column optical depth " + tau[range.Name].ToString("0.00") + ", wanted " + range.Lo + " to " + range.Hi);
            // The heaped family grows with the cloud's depth; the stratus is not part of it (ISCCP: thicker than stratocumulus).
            Assert.IsTrue(tau["Stratocumulus"] < tau["Cumulus humilis"] && tau["Cumulus humilis"] < tau["Cumulus congestus"]
                          && tau["Cumulus congestus"] < tau["Cumulonimbus incus"], "the family grows with the cloud's depth");
            Assert.IsTrue(tau["Stratus"] > tau["Stratocumulus"], "ISCCP's stratus is thicker than its stratocumulus");
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
