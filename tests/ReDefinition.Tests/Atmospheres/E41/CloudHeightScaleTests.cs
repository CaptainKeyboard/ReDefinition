using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.B13;

namespace ReDefinition.Tests.Atmospheres.E41
{
    // The height scale (CloudHeightScale; owner 2026-11-23 "Ja, so umsetzen", .local/plans/wolken-hoehenkonzept.md): the cloud heights as shares of the measured
    // troposphere times a factor; a set in metres scales by the troposphere over its heightReferenceM; off, unmeasured or without a reference the metres stay.
    [TestClass]
    public class CloudHeightScaleTests
    {
        private const string Clouds =
            "CLOUDS { heightReferenceM = 11000; heightFactor = 1; heightVariationM = 110; heightLclMPerK = 125; "
            + "LAYER { name = lowstratiform; innerAltitudeM = 0; outerAltitudeM = 2100; noiseScaleM = 4000; TYPE { name = Stratus; baseShare = 0.1; topShare = 0.5; PROFILE { key0 = 0 1; key1 = 1 1 } } } "
            + "LAYER { name = high; innerDepths = 0.8; outerDepths = 1; TYPE { name = Cirrus; baseShare = 0.1; topShare = 0.9; PROFILE { key0 = 0 1; key1 = 1 1 } } } }";

        private static CloudRules Read(bool on, double factor, double measuredM, string clouds = Clouds, bool horizontal = true)
        {
            BodyFacts facts = BodyTestSupport.Planet("Kerbin", BodyTestSupport.HomeYear);
            facts.MeasuredTropopauseM = measuredM;
            var ctx = new CloudLayerContext { Facts = facts, Generator = BodyRuleReaders.Generator(new BodyNodeReader(null, "GENERATOR", new List<string>())),
                HeightScaleOn = on, HeightFactor = factor, HeightScaleHorizontal = horizontal };
            ConfigNode node = BodyTestSupport.Root(clouds).GetNode("CLOUDS");
            Warnings.Clear();
            return BodyRuleReaders.Clouds(new BodyNodeReader(node, "CLOUDS", Warnings), ctx);
        }

        private static readonly List<string> Warnings = new List<string>();

        private static CloudLayerSpec Layer(CloudRules r, string name)
        {
            foreach (CloudLayerSpec l in r.Layers) if (l.Name == name) return l;
            Assert.Fail("no layer " + name + " (" + r.Layers.Count + " layers, set " + r.LayerSet + "; " + string.Join(" | ", Warnings.ToArray()) + ")");
            return null;
        }

        [TestMethod]
        public void OffTheLayersAreAsWritten()
        {
            CloudRules r = Read(false, 1.0, 8800.0);
            Assert.AreEqual(2100f, Layer(r, "lowstratiform").OuterAltitudeM, 1e-3f);
            Assert.AreEqual(4000f, Layer(r, "lowstratiform").NoiseScaleM, 1e-3f);
            Assert.AreEqual(110f, r.Height.VariationM, 1e-3f);
            Assert.IsTrue(double.IsNaN(r.ScaledTroposphereM));
            Assert.AreEqual(1f, CloudHeightScale.OnDefault, "on with the factor 1 (owner 2026-11-23 16:02)");
            Assert.AreEqual(1f, CloudHeightScale.FactorDefault);
        }

        [TestMethod]
        public void OnTheMetresScaleByTheMeasuredTroposphereOverTheReference()
        {
            CloudRules r = Read(true, 1.0, 8800.0);
            const float s = 8800f / 11000f;
            Assert.AreEqual(2100f * s, Layer(r, "lowstratiform").OuterAltitudeM, 0.01f);
            Assert.AreEqual(4000f * s, Layer(r, "lowstratiform").NoiseScaleM, 0.01f, "the cloud bodies' sizes follow");
            Assert.AreEqual(110f * s, r.Height.VariationM, 0.01f);
            Assert.AreEqual(125f, r.Height.LclMPerK, 1e-3f, "the physics of the condensation level does not scale");
            Assert.AreEqual(8800f * 0.8f, Layer(r, "high").InnerAltitudeM, 0.01f, "a depth key is a share of the measured troposphere");
            Assert.AreEqual(8800.0, r.ScaledTroposphereM, 1e-6);
        }

        [TestMethod]
        public void TheFactorScalesEverything()
        {
            CloudRules r = Read(true, 0.6, 8800.0);
            Assert.AreEqual(2100f * 8800f * 0.6f / 11000f, Layer(r, "lowstratiform").OuterAltitudeM, 0.01f);
            Assert.AreEqual(8800f * 0.6f, Layer(r, "high").OuterAltitudeM, 0.01f);
            CloudRules body = Read(true, 1.0, 8800.0, Clouds.Replace("heightFactor = 1", "heightFactor = 0.75"));
            Assert.AreEqual(8800f * 0.75f, Layer(body, "high").OuterAltitudeM, 0.01f, "the body's own factor");
        }

        // cloud.heightScaleHorizontal 0 (a comparison of factors at one place): the heights scale, the horizontal lengths and the pattern's depth stay as written.
        [TestMethod]
        public void HeightsOnlyKeepTheHorizontalLengths()
        {
            string clouds = Clouds.Replace("noiseScaleM = 4000", "noiseScaleDepths = 0.5");
            CloudRules all = Read(true, 1.0, 8800.0, clouds);
            CloudRules heights = Read(true, 1.0, 8800.0, clouds, false);
            CloudRules off = Read(false, 1.0, 8800.0, clouds);
            Assert.AreEqual(8800f * 0.5f, Layer(all, "lowstratiform").NoiseScaleM, 0.01f, "with the horizontal lengths a depth key follows the measured troposphere");
            Assert.AreEqual(Layer(off, "lowstratiform").NoiseScaleM, Layer(heights, "lowstratiform").NoiseScaleM, 0.01f, "heights only: the generator's depth as before");
            Assert.AreEqual(2100f * 0.8f, Layer(heights, "lowstratiform").OuterAltitudeM, 0.01f, "the heights scale");
            Assert.AreEqual(off.Height.WavelengthM, heights.Height.WavelengthM, 0.01f);
            Assert.AreEqual(110f * 0.8f, heights.Height.VariationM, 0.01f);
            Assert.IsTrue(double.IsNaN(heights.ScaledTroposphereM), "the meso stage keeps the generator's depth");
            Assert.AreEqual(0f, CloudHeightScale.HorizontalDefault, "the owner's \"Nur Höhen\" (2026-11-23 16:02)");
        }

        [TestMethod]
        public void WithoutAMeasuredTroposphereTheLayersStay()
        {
            CloudRules r = Read(true, 1.0, double.NaN);
            Assert.AreEqual(2100f, Layer(r, "lowstratiform").OuterAltitudeM, 1e-3f);
            Assert.IsTrue(double.IsNaN(r.ScaledTroposphereM));
            // Owner 2026-11-23 17:14: a set without heightReferenceM (the template's bodies) is not scaled at all; its depth keys stay on the generator's depth.
            CloudRules noReference = Read(true, 1.0, 8800.0, Clouds.Replace("heightReferenceM = 11000; ", ""));
            CloudRules noReferenceOff = Read(false, 1.0, 8800.0, Clouds.Replace("heightReferenceM = 11000; ", ""));
            Assert.AreEqual(2100f, Layer(noReference, "lowstratiform").OuterAltitudeM, 1e-3f, "metres without a reference stay");
            Assert.AreEqual(Layer(noReferenceOff, "high").OuterAltitudeM, Layer(noReference, "high").OuterAltitudeM, 0.01f, "depth keys stay on the generator's depth");
            Assert.IsTrue(double.IsNaN(noReference.ScaledTroposphereM));
        }
    }
}
