using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Tests.Atmospheres.B13;
using ReDefinition.Tests.Atmospheres.E41;

namespace ReDefinition.Tests.Atmospheres.T10
{
    // The per-body layer set of the own cloud system (package T10): the reader of the LAYER nodes, the selection of the layers a pass draws,
    // and the Kerbin set against its references (the WMO ranges of base and top, the optical depths of real clouds, and the structure of the
    // benchmark inventory: how many layers, in which altitude bands).
    [TestClass]
    public class CloudLayerSchemaTests
    {
        private static string KerbinCloudsFile()
        {
            return Path.Combine(E41Support.Root(), "GameData", "ReDefinition", "Atmospheres", "Bodies", "Clouds-Kerbin.cfg");
        }

        // The body node of Kerbin with the patch of Clouds-Kerbin.cfg applied the way ModuleManager applies it: the LAYER nodes of the patch's
        // @CLOUDS are added to the CLOUDS node.
        private static ConfigNode PatchedKerbin(out int added)
        {
            ConfigNode body = E41Support.BodyNode("Kerbin");
            ConfigNode patch = ConfigNode.Load(KerbinCloudsFile()).nodes[0];
            Assert.AreEqual("@REDEFINITION_BODY[Kerbin]", patch.name);
            ConfigNode patchClouds = patch.GetNode("@CLOUDS");
            Assert.IsNotNull(patchClouds);
            ConfigNode clouds = body.GetNode("CLOUDS");
            added = 0;
            foreach (ConfigNode layer in patchClouds.GetNodes("LAYER"))
            {
                clouds.AddNode(layer);
                added++;
            }
            return body;
        }

        private static BodyParseResult ParseKerbin()
        {
            int added;
            ConfigNode node = PatchedKerbin(out added);
            return BodyConfigParser.Parse(E41Support.StockBodies().Single(s => s.Facts.Name == "Kerbin").Facts, node, null, 7);
        }

        private static CloudRules Kerbin()
        {
            return ParseKerbin().Climate.Clouds;
        }

        private static float TypeTau(CloudLayerSpec layer, CloudTypeSpec type)
        {
            float peak = 0f;
            for (int k = 0; k < type.Profile.Count; k++) peak = Math.Max(peak, type.Profile.ValueAt(k));
            return layer.SigmaPerM * (type.TopShare - type.BaseShare) * (layer.OuterAltitudeM - layer.InnerAltitudeM) * peak;
        }

        [TestMethod]
        public void TheKerbinSetReadsWithoutAWarningAndHoldsFiveLayersForFourAtOnce()
        {
            BodyParseResult r = ParseKerbin();
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            IReadOnlyList<CloudLayerSpec> layers = r.Climate.Clouds.Layers;
            Assert.AreEqual(5, layers.Count);
            Assert.AreEqual(5, layers.Select(l => l.Name).Distinct().Count());
            Assert.AreEqual(4, CloudLayerSet.MostAtOnce(layers), "designed for four visible at once");
            Assert.AreEqual(2, layers.Count(l => l.Group == "weather"), "the rain and the snow deck are exclusive");
            // The pre-existing keys of the node are untouched by the layers.
            Assert.AreEqual(CloudDiscovery.Auto, r.Climate.Clouds.Discover);
            Assert.AreEqual("Kerbin-base-layer", r.Climate.Clouds.BaseLayer);
        }

        [TestMethod]
        public void TheKerbinSetReproducesTheStructureOfTheBenchmarkInventory()
        {
            // The pack's Kerbin inventory (tvc-parity 6.3), structure only: the volumetric layers and the altitude bands they span in metres.
            // base layer 1750 to 7450, cumulonimbus layer 1750 to 11450, the two weather layers (alternating) -20 to 2150.
            var bands = new[] { new[] { 1750f, 7450f }, new[] { 1750f, 11450f }, new[] { -20f, 2150f } };
            IReadOnlyList<CloudLayerSpec> layers = Kerbin().Layers;
            foreach (float[] band in bands)
            {
                bool matched = layers.Any(l => Math.Abs(l.InnerAltitudeM - band[0]) <= 0.15f * (band[1] - band[0]) && Math.Abs(l.OuterAltitudeM - band[1]) <= 0.15f * (band[1] - band[0]));
                Assert.IsTrue(matched, "no layer of ours in the band " + band[0] + " to " + band[1] + " m");
            }
            Assert.IsTrue(layers.Count >= 4, "at least the pack's four volumetric layers");
            // Two alternating weather layers on the ground, as the pack has.
            Assert.AreEqual(2, layers.Count(l => l.InnerAltitudeM <= 0f && l.Group.Length > 0));
        }

        [TestMethod]
        public void EveryKerbinTypeLiesInTheWmoRangeOfItsKind()
        {
            // name -> (lowest base, highest base, lowest top, highest top) in metres above sea level: the WMO ranges of the level the cloud
            // belongs to (low: surface to 2 km, middle 2 to 7 km, high 5 to 13 km; cumulonimbus: base below 2 km, top near the tropopause).
            var wmo = new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                { "Fog", new[] { 0f, 100f, 50f, 400f } },
                { "Polar fog", new[] { 0f, 100f, 50f, 400f } },
                { "Stratus", new[] { 0f, 2000f, 100f, 2500f } },
                { "Snow stratus", new[] { 0f, 2000f, 100f, 2500f } },
                { "Nimbostratus", new[] { 0f, 2000f, 1000f, 4000f } },
                { "Snow nimbus", new[] { 0f, 2000f, 1000f, 4000f } },
                { "Stratocumulus", new[] { 300f, 2000f, 500f, 2500f } },
                { "Cumulus humilis", new[] { 600f, 2000f, 2000f, 4000f } },
                { "Cumulus mediocris", new[] { 600f, 2000f, 3000f, 5000f } },
                { "Cumulus congestus", new[] { 600f, 2000f, 5000f, 8000f } },
                { "Altocumulus", new[] { 2000f, 7000f, 2500f, 7500f } },
                { "Cumulonimbus calvus", new[] { 600f, 2000f, 6000f, 12000f } },
                { "Cumulonimbus incus", new[] { 600f, 2000f, 8000f, 16000f } },
                { "Cirrus", new[] { 5000f, 13000f, 6000f, 13000f } },
                { "Cirrostratus", new[] { 5000f, 13000f, 6000f, 13000f } }
            };
            int seen = 0;
            foreach (CloudLayerSpec layer in Kerbin().Layers)
            {
                float thickness = layer.OuterAltitudeM - layer.InnerAltitudeM;
                foreach (CloudTypeSpec type in layer.Types)
                {
                    float[] range;
                    Assert.IsTrue(wmo.TryGetValue(type.Name, out range), "no WMO range for the type " + type.Name);
                    float typeBase = layer.InnerAltitudeM + type.BaseShare * thickness, typeTop = layer.InnerAltitudeM + type.TopShare * thickness;
                    Assert.IsTrue(typeBase >= range[0] && typeBase <= range[1], type.Name + ": base " + typeBase + " m outside " + range[0] + " to " + range[1]);
                    Assert.IsTrue(typeTop >= range[2] && typeTop <= range[3], type.Name + ": top " + typeTop + " m outside " + range[2] + " to " + range[3]);
                    seen++;
                }
            }
            Assert.AreEqual(wmo.Count, seen, "every WMO row is used by a type of the set");
        }

        [TestMethod]
        public void EveryKerbinTypeHasTheOpticalDepthOfItsRealKind()
        {
            // Vertical optical depth at full density and full height (an upper bound of the type): stratus 10 to 60, nimbostratus 20 to 300,
            // cumulus 5 to 300, cumulonimbus 100 to 1000, cirrus 0.03 to 3 (the references of the file header).
            var tau = new Dictionary<string, float[]>(StringComparer.Ordinal)
            {
                { "Stratus", new[] { 10f, 60f } }, { "Snow stratus", new[] { 10f, 60f } },
                { "Nimbostratus", new[] { 20f, 300f } }, { "Snow nimbus", new[] { 20f, 300f } },
                { "Stratocumulus", new[] { 5f, 300f } }, { "Cumulus humilis", new[] { 5f, 300f } }, { "Cumulus mediocris", new[] { 5f, 300f } },
                { "Cumulus congestus", new[] { 5f, 300f } }, { "Altocumulus", new[] { 5f, 300f } },
                { "Cumulonimbus calvus", new[] { 100f, 1000f } }, { "Cumulonimbus incus", new[] { 100f, 1000f } },
                { "Cirrus", new[] { 0.03f, 3f } }, { "Cirrostratus", new[] { 0.03f, 3f } }
            };
            foreach (CloudLayerSpec layer in Kerbin().Layers)
            {
                foreach (CloudTypeSpec type in layer.Types)
                {
                    float[] band;
                    if (!tau.TryGetValue(type.Name, out band)) continue;
                    float t = TypeTau(layer, type);
                    Assert.IsTrue(t >= band[0] && t <= band[1], type.Name + ": optical depth " + t + " outside " + band[0] + " to " + band[1]);
                }
            }
        }

        [TestMethod]
        public void TheKerbinLayersBuildIntoALayerStackAndTheSelectionKeepsTheFourVisible()
        {
            CloudRules rules = Kerbin();
            List<CloudLayerSpec> chosen = CloudLayerSet.Select(rules.Layers, CloudLayerDef.MaxLayers);
            Assert.AreEqual(4, chosen.Count);
            Assert.AreEqual("Kerbin-weather-rain", chosen[0].Name, "the first member of the group by default");
            Assert.AreEqual("Kerbin-cirrus", chosen[3].Name, "sorted by altitude, the cirrus on top");
            var stack = new LayerStackSource(1, 600000.0, true);
            foreach (CloudLayerSpec spec in chosen)
            {
                CloudLayerDef def = spec.Build(1);
                Assert.AreEqual(spec.InnerAltitudeM, def.InnerAltitudeM);
                Assert.AreEqual(spec.OuterAltitudeM, def.OuterAltitudeM);
                Assert.AreEqual(spec.Types.Count, def.Types.Count);
                Assert.AreEqual(spec.ShadowStrength, def.ShadowStrength);
                Assert.AreEqual(spec.Coverage, def.Coverage);
                float lo, hi;
                def.TightAltitudes(out lo, out hi);
                Assert.IsTrue(lo >= def.InnerAltitudeM && hi <= def.OuterAltitudeM && hi > lo);
                stack.Add(def, null, null);
            }
            Assert.AreEqual(4, stack.LayerCount);

            // The weather picks the snow deck: the same four layers with the other group member.
            List<CloudLayerSpec> snow = CloudLayerSet.Select(rules.Layers, 4, group => group == "weather" ? 1 : 0);
            Assert.AreEqual("Kerbin-weather-snow", snow[0].Name);
            Assert.AreEqual(4, snow.Count);
            // A pick out of range counts from the start.
            Assert.AreEqual("Kerbin-weather-rain", CloudLayerSet.Select(rules.Layers, 4, group => 7)[0].Name);
            // The cap keeps the lowest layers.
            List<CloudLayerSpec> two = CloudLayerSet.Select(rules.Layers, 2);
            CollectionAssert.AreEqual(new[] { "Kerbin-weather-rain", "Kerbin-cumulus" }, two.Select(l => l.Name).ToArray());
            Assert.AreEqual(0, CloudLayerSet.Select(rules.Layers, 0).Count);
            Assert.AreEqual(4, CloudLayerSet.Select(rules.Layers, 99).Count, "never above the draw limit");
        }

        [TestMethod]
        public void TheLayersAreOurOwnAndTheCirrusIsTheCheapKind()
        {
            CloudLayerSpec cirrus = Kerbin().Layers.Single(l => l.Name == "Kerbin-cirrus");
            Assert.AreEqual(LayerKind.Cheap, cirrus.Kind);
            Assert.IsTrue(cirrus.ShadowStrength < 0.5f, "thin ice cloud shades little");
            string text = File.ReadAllText(KerbinCloudsFile());
            StringAssert.Contains(text, "WMO");
        }

        [TestMethod]
        public void ABodyWithoutLayerNodesHasNoOwnSet()
        {
            BodyParseResult r = BodyTestSupport.Run(BodyTestSupport.Planet("Plain", BodyTestSupport.HomeYear), "REDEFINITION_BODY{name = Plain;CLOUDS{discover = Off}}");
            Assert.AreEqual(0, r.Climate.Clouds.Layers.Count);
            Assert.AreEqual(1f, r.Climate.Clouds.SphereScale);
            Assert.AreEqual(0, CloudLayerSet.MostAtOnce(r.Climate.Clouds.Layers));
        }

        private static BodyParseResult Parse(string cloudsInner)
        {
            return BodyTestSupport.Run(BodyTestSupport.Planet("Test", BodyTestSupport.HomeYear), "REDEFINITION_BODY{name = Test;CLOUDS{" + cloudsInner + "}}");
        }

        private const string GoodType = "TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 0;key1 = 0.5 1;key2 = 1 0}}";

        [TestMethod]
        public void AUnreadableLayerIsAWarningAndTheOthersStay()
        {
            string text = "LAYER{name = Good;innerAltitudeM = 100;outerAltitudeM = 900;" + GoodType + "}"
                + "LAYER{innerAltitudeM = 100;outerAltitudeM = 900;" + GoodType + "}"
                + "LAYER{name = Inverted;innerAltitudeM = 900;outerAltitudeM = 100;" + GoodType + "}"
                + "LAYER{name = NoTypes;innerAltitudeM = 100;outerAltitudeM = 900}"
                + "LAYER{name = NoProfile;innerAltitudeM = 100;outerAltitudeM = 900;TYPE{name = T;baseShare = 0;topShare = 1}}"
                + "LAYER{name = Good;innerAltitudeM = 100;outerAltitudeM = 900;" + GoodType + "}"
                + "LAYER{name = BadKey;innerAltitudeM = 100;outerAltitudeM = 900;TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 0;key1 = 2 1}}}";
            BodyParseResult r = Parse(text);
            Assert.AreEqual(1, r.Climate.Clouds.Layers.Count);
            Assert.AreEqual("Good", r.Climate.Clouds.Layers[0].Name);
            Assert.AreEqual(1f, r.Climate.Clouds.Layers[0].Coverage, "the defaults of the schema");
            Assert.IsTrue(r.Warnings.Count >= 6, string.Join("; ", r.Warnings));
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("without a name")));
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("Inverted")));
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("NoTypes")));
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("twice")));
        }

        [TestMethod]
        public void TheSetHoldsAtMostEightLayersAndEightTypes()
        {
            var layers = new List<string>();
            for (int i = 0; i < 10; i++) layers.Add("LAYER{name = L" + i + ";innerAltitudeM = " + (i * 1000) + ";outerAltitudeM = " + (i * 1000 + 800) + ";" + GoodType + "}");
            BodyParseResult r = Parse(string.Join("", layers));
            Assert.AreEqual(BodyRuleReaders.MaxLayerSpecs, r.Climate.Clouds.Layers.Count);
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("at most")));
            var types = new List<string>();
            for (int i = 0; i < 12; i++) types.Add("TYPE{name = T" + i + ";baseShare = 0;topShare = 1;PROFILE{key0 = 0 1}}");
            r = Parse("LAYER{name = Many;innerAltitudeM = 0;outerAltitudeM = 800;" + string.Join("", types) + "}");
            Assert.AreEqual(CloudLayerDef.MaxTypes, r.Climate.Clouds.Layers[0].Types.Count);
        }

        [TestMethod]
        public void ALayerSpecIsRangeCheckedAndBuildsTheDefinitionItNames()
        {
            BodyParseResult r = Parse("sphereScale = 0.5;LAYER{name = A;innerAltitudeM = 500;outerAltitudeM = 1500;sigmaPerM = 0.05;kind = Fog;coverage = 2;group = g;fadeFarM = 40000;"
                + GoodType + "}");
            Assert.AreEqual(0.5f, r.Climate.Clouds.SphereScale);
            CloudLayerSpec a = r.Climate.Clouds.Layers[0];
            Assert.AreEqual(1f, a.Coverage, "the coverage is brought into 0 to 1");
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("coverage")));
            Assert.AreEqual(LayerKind.Fog, a.Kind);
            Assert.AreEqual("g", a.Group);
            CloudLayerDef def = a.Build(3);
            Assert.AreEqual(3, def.BodyIndex);
            Assert.AreEqual(40000f, def.FadeFarM);
            Assert.AreEqual(0.05f, def.SigmaPerM);
            Assert.AreEqual(LayerKind.Fog, def.Kind);
        }
    }
}
