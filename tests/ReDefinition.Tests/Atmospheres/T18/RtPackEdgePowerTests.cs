using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;

namespace ReDefinition.Tests.Atmospheres.T18
{
    // CONSIST: the ray pack's density exponent follows the types' own edgePower (the visible pass and the shadow read it per type), the layer's waterPower where a type has none.
    [TestClass]
    public class RtPackEdgePowerTests
    {
        private static LatLonSource Source(float edgePower)
        {
            CloudTypeDef type = CloudTypeDef.Slab("s", 0f, 1f);
            type.EdgePower = edgePower;
            double map = 1.0;
            var s = new LatLonSource(PackScenes.Kerbin).Add("a", 1000, 3000, 1.0, (a, b) => map, 0.3, type);
            CloudLayerDef layer = s.LayerAt(0);
            layer.NoiseAmount = 1f;
            // The map value whose share is 0.5 (a rim value), by bisection on the layer's own numbers.
            double lo = 0.0, hi = 1.0;
            for (int i = 0; i < 40; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (LayerDensity.Shape(layer.ClearSkyMap(mid), layer.EffectiveCoverage, layer.EffectiveEdgeSoftness) < 0.5) lo = mid; else hi = mid;
            }
            map = 0.5 * (lo + hi);
            return s;
        }

        private static RtCloudLayerEntry Baked(LatLonSource s)
        {
            var baker = new RtCloudTauBaker(s, s.Tables(), 1, 64, 32, 8);
            Assert.IsTrue(baker.Step(int.MaxValue));
            return RtCloudPackDriver.EntryFor(s, 0, baker);
        }

        [TestMethod]
        public void ATypeWithAnEdgePowerGivesItsExponentToThePack()
        {
            LatLonSource s = Source(0.5f);
            Assert.AreEqual(2f, s.LayerAt(0).EffectiveWaterPower, "the layer's own exponent");
            RtCloudLayerEntry e = Baked(s);
            Assert.AreEqual(1f, e.OwnEdgeShare, 1e-5f);
            Assert.AreEqual(0.5f, e.OwnEdgePower, 1e-5f);
            Assert.AreEqual(0.5f, e.WaterPower, 1e-5f);
        }

        [TestMethod]
        public void WithoutAnEdgePowerThePackKeepsTheLayersExponentAndFollowsItLive()
        {
            LatLonSource s = Source(0f);
            RtCloudLayerEntry e = Baked(s);
            Assert.AreEqual(0f, e.OwnEdgeShare);
            Assert.AreEqual(2f, e.WaterPower, 1e-6f);
            s.LayerAt(0).WaterPower = 3f;
            RtCloudPackDriver.Live(e, s.LayerAt(0));
            Assert.AreEqual(3f, e.WaterPower, 1e-6f, "the layer's live exponent");
        }

        [TestMethod]
        public void TheColumnDepthOfThePackIsTheShareToTheTypesExponent()
        {
            LatLonSource s = Source(0.5f);
            var baker = new RtCloudTauBaker(s, s.Tables(), 1, 64, 32, 8);
            baker.Step(int.MaxValue);
            RtCloudLayerEntry e = RtCloudPackDriver.EntryFor(s, 0, baker);
            var pack = new RtCloudPack(1, PackScenes.Kerbin, true, 64, 32, new[] { e }, baker.Data, 1);
            double coverage, tauFull;
            pack.Sample(0, 10.0, 20.0, out coverage, out tauFull);
            double share = LayerDensity.Shape(CloudLayerDef.ClearSky((float)coverage, e.ClearSkyK), e.Coverage, e.EdgeSoftness);
            Assert.IsTrue(share > 0.0 && share < 1.0, "a rim value: " + share);
            Assert.AreEqual(Math.Sqrt(share) * tauFull, pack.ColumnTau(0, 10.0, 20.0), 1e-6 * Math.Max(1.0, tauFull));
            // The share the shadow and the pass take at the same map value, for the type's value.
            LayerTypeTable table = s.Tables()[0];
            Assert.AreEqual(Math.Sqrt(share), LayerDensity.Share(s.LayerAt(0), coverage, table, 0.0), 1e-6);
        }
    }
}
