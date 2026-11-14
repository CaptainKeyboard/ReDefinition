using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.T10;
using UnityEngine;

namespace ReDefinition.Tests.Atmospheres.T09
{
    // E40 B3: the light response of a cloud type by height (the profile texture's channels g and b), the layer's light factors for _LightL, and the white point of the output
    // stage that follows the sun at the clouds.
    [TestClass]
    public class CloudTypeLightTests
    {
        [TestMethod]
        public void ATypesLightFactorsRunSmoothlyFromTheBaseToTheTopAndAreNeutralByDefault()
        {
            var neutral = new CloudTypeDef("N", 0f, 1f, new Curve(new[] { 0f, 1f }, new[] { 1f, 1f }));
            double d, a;
            neutral.LightFactors(0.3, out d, out a);
            Assert.AreEqual(1.0, d, 1e-9);
            Assert.AreEqual(1.0, a, 1e-9);
            var t = new CloudTypeDef("T", 0f, 1f, new Curve(new[] { 0f, 1f }, new[] { 1f, 1f })) { BaseLight = 0.6f, HeadLight = 1.2f, BaseAmbient = 0.8f, HeadAmbient = 1f };
            t.LightFactors(0.0, out d, out a);
            Assert.AreEqual(0.6, d, 1e-6);
            Assert.AreEqual(0.8, a, 1e-6);
            t.LightFactors(1.0, out d, out a);
            Assert.AreEqual(1.2, d, 1e-6);
            Assert.AreEqual(1.0, a, 1e-6);
            t.LightFactors(0.5, out d, out a);
            Assert.AreEqual(0.9, d, 1e-6, "smoothstep: the middle is the mean");
            double last = 0.0;
            for (int i = 0; i <= 20; i++)
            {
                t.LightFactors(i / 20.0, out d, out a);
                Assert.IsTrue(d >= last - 1e-12, "monotone from the shaded base to the lit head");
                last = d;
            }
        }

        [TestMethod]
        public void TheTypeTableBakesTheLightFactorsOfEachTypeAndReadsThemBackBetweenTheSamples()
        {
            CloudLayerDef cumulus = CloudLookFixtures.Cumulus();
            var table = new LayerTypeTable(cumulus);
            float[] data = table.LightData;
            Assert.AreEqual(table.Count * LayerTypeTable.ProfileSamples * 2, data.Length);
            for (int type = 0; type < table.Count; type++)
                foreach (double h in new[] { 0.0, 0.13, 0.5, 0.77, 1.0 })
                {
                    double d, a, wd, wa;
                    table.LightAt(type, h, out d, out a);
                    cumulus.Types[type].LightFactors(h, out wd, out wa);
                    Assert.AreEqual(wd, d, 2e-3, cumulus.Types[type].Name + " direct at " + h);
                    Assert.AreEqual(wa, a, 2e-3, cumulus.Types[type].Name + " ambient at " + h);
                }
            double dBase, aBase, dTop, aTop;
            // Type 1 is Cumulus humilis (the altocumulus sheet is the last type, E40 L3).
            table.LightAt(1, 0.0, out dBase, out aBase);
            table.LightAt(1, 1.0, out dTop, out aTop);
            Assert.IsTrue(dBase < 1.0 && dTop > 1.0, "a cumulus has a shaded base and a bright head: " + dBase + " / " + dTop);
        }

        [TestMethod]
        public void ThePerTypeLightKeysReadFromTheTypeNodeAndDefaultToNeutral()
        {
            string typed = "TYPE{name = T;baseShare = 0;topShare = 1;baseLight = 0.5;headLight = 1.3;baseAmbient = 0.7;headAmbient = 0.9;PROFILE{key0 = 0 0;key1 = 0.5 1;key2 = 1 0}}";
            string plain = "TYPE{name = P;baseShare = 0;topShare = 1;PROFILE{key0 = 0 0;key1 = 0.5 1;key2 = 1 0}}";
            BodyParseResult r = CloudLayerSchemaTests.Parse("LAYER{name = A;innerAltitudeM = 100;outerAltitudeM = 900;" + typed + plain + "}");
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            CloudTypeSpec t = r.Climate.Clouds.Layers[0].Types[0], p = r.Climate.Clouds.Layers[0].Types[1];
            Assert.AreEqual(0.5f, t.BaseLight, 1e-6f);
            Assert.AreEqual(1.3f, t.HeadLight, 1e-6f);
            Assert.AreEqual(0.7f, t.BaseAmbient, 1e-6f);
            Assert.AreEqual(0.9f, t.HeadAmbient, 1e-6f);
            Assert.AreEqual(1f, p.BaseLight);
            Assert.AreEqual(1f, p.HeadLight);
            Assert.AreEqual(1f, p.BaseAmbient);
            Assert.AreEqual(1f, p.HeadAmbient);
            CloudTypeDef built = t.Build();
            Assert.AreEqual(0.5f, built.BaseLight, 1e-6f);
            Assert.AreEqual(0.9f, built.HeadAmbient, 1e-6f);
        }

        [TestMethod]
        public void TheShippedKerbinTypesShadeTheBaseAndLightTheHeadWhereTheLookWantsIt()
        {
            var layers = CloudLayerSchemaTests.ParseKerbin().Climate.Clouds.Layers;
            foreach (string name in new[] { "Cumulus mediocris", "Cumulus congestus", "Cumulonimbus calvus", "Cumulonimbus incus" })
            {
                CloudTypeSpec t = layers.SelectMany(l => l.Types).Single(x => x.Name == name);
                Assert.IsTrue(t.BaseLight < 0.8f, name + " has a shaded base");
                Assert.IsTrue(t.HeadLight > 1.1f, name + " has a bright head");
            }
            CloudTypeSpec humilis = layers.SelectMany(l => l.Types).Single(x => x.Name == "Cumulus humilis");
            Assert.IsTrue(humilis.BaseLight < 1f && humilis.HeadLight > 1.1f, "humilis: a shaded base under a bright head, the milder pair the model's own light needs");
            CloudTypeSpec stratocumulus = layers.SelectMany(l => l.Types).Single(x => x.Name == "Stratocumulus");
            Assert.AreEqual(1f, stratocumulus.BaseLight, "stratocumulus needs no factor on the direct light");
            Assert.AreEqual(1f, stratocumulus.HeadLight, "stratocumulus needs no factor on the direct light");
            // The decks of stratus and nimbostratus (rain and snow) have no factor: their light follows the two-stream anchors without one (E40 look round 2).
            foreach (string name in new[] { "Stratus", "Nimbostratus", "Snow stratus", "Snow nimbus" })
            {
                CloudTypeSpec t = layers.SelectMany(l => l.Types).Single(x => x.Name == name);
                Assert.AreEqual(1f, t.BaseLight, name + " base light");
                Assert.AreEqual(1f, t.HeadLight, name + " head light");
                Assert.AreEqual(1f, t.BaseAmbient, name + " base ambient");
                Assert.AreEqual(1f, t.HeadAmbient, name + " head ambient");
            }
            foreach (CloudLayerSpec deck in layers.Where(l => l.Name.StartsWith("Kerbin-weather")))
            {
                Assert.AreEqual(1f, deck.LightMs, deck.Name + " light ms");
                Assert.AreEqual(1f, deck.LightAmbient, deck.Name + " light ambient");
            }
        }

        [TestMethod]
        public void TheWhiteTintMakesTheSunAtTheCloudsNeutralByTheSunsHeightAndLeavesASunsetAlone()
        {
            var beige = new Color(1f, 0.9f, 0.69f, 1f);
            Color full = CloudOutputStage.WhiteTint(beige, 1f), none = CloudOutputStage.WhiteTint(beige, 0f);
            Assert.AreEqual(1f, none.r, 1e-6f);
            Assert.AreEqual(1f, none.b, 1e-6f);
            // A cloud lit by that sun (c = the sun's colour, W its luminance) comes out neutral with the full tint and beige without it.
            float white = beige.r * 0.2126f + beige.g * 0.7152f + beige.b * 0.0722f;
            float r = CloudOutputStage.Encode(beige.r, white, full.r, 1f, 2.2f), b = CloudOutputStage.Encode(beige.b, white, full.b, 1f, 2.2f);
            Assert.AreEqual(r, b, 1e-4f, "a sunlit cloud top is neutral");
            Assert.AreEqual(white, r, 1e-4f, "at the luminance of the stage's white");
            float rawR = CloudOutputStage.Encode(beige.r, white, 1f, 2.2f), rawB = CloudOutputStage.Encode(beige.b, white, 1f, 2.2f);
            Assert.IsTrue(rawB / rawR < 0.85f, "without the tint it is beige: " + rawB / rawR);
            // The sun's height: nothing at a sunset (below 3 degrees), all from about 24 degrees, the strength scales it.
            Assert.AreEqual(0f, CloudOutputStage.WhiteShare(Math.Sin(3.0 * Math.PI / 180.0), 1f), 1e-3f);
            Assert.AreEqual(1f, CloudOutputStage.WhiteShare(Math.Sin(30.0 * Math.PI / 180.0), 1f), 1e-6f);
            Assert.AreEqual(0.5f, CloudOutputStage.WhiteShare(Math.Sin(60.0 * Math.PI / 180.0), 0.5f), 1e-6f);
            float mid = CloudOutputStage.WhiteShare(Math.Sin(12.0 * Math.PI / 180.0), 1f);
            Assert.IsTrue(mid > 0f && mid < 1f, "in between: " + mid);
        }

        [TestMethod]
        public void TheNominalCloudAltitudeIsTheLowerQuarterOfTheLayer()
        {
            Assert.AreEqual(525f, CloudOutputStage.NominalCloudAltitudeM(0f, 2100f), 1e-3f, "the rain deck");
            Assert.AreEqual(3125f, CloudOutputStage.NominalCloudAltitudeM(1700f, 5700f), 1e-3f, "the cumulus layer");
            Assert.AreEqual(1700f, CloudOutputStage.NominalCloudAltitudeM(1700f, -5f), 1e-3f, "a negative thickness counts as none");
        }

        [TestMethod]
        public void TheSunAtAnAltitudeIsThePassSunTimesTheAirAboveItAndTheShaderAgrees()
        {
            var s = new CloudLightSettings { SunAirWhiteBalance = false };
            s.UseAir(new Vector3(0.01534f, 0.03571f, 0.08755f) * 0.001f, 3840f, 0.00001214f, 1000f);
            double sin = Math.Sin(45.0 * Math.PI / 180.0);
            Color pass = new Color(1f, 1f, 1f, 1f);
            Color low = CloudSunAir.AtAltitude(pass, sin, 500.0, s), high = CloudSunAir.AtAltitude(pass, sin, 6000.0, s);
            Assert.IsTrue(low.b < low.r, "air reddens the sun");
            Assert.IsTrue(high.b / high.r > low.b / low.r, "a higher cloud sees a whiter sun");
            Assert.AreEqual(CloudLightMath.SunAir(sin, s.SunAirTau.x, s.SunAirHeightM, s.SunAirMieTau, s.SunAirMieHeightM, 500.0, false), low.r, 1e-6);
            var off = new CloudLightSettings { SunAirHeightM = 0f };
            Assert.AreEqual(pass, CloudSunAir.AtAltitude(pass, sin, 500.0, off), "the shader's air off: the pass colour itself");
        }
    }
}
