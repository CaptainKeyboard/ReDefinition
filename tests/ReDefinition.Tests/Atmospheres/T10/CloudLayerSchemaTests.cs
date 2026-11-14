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
        internal static ConfigNode PatchedKerbin(out int added)
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

        internal static BodyParseResult ParseKerbin()
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

        internal static BodyParseResult Parse(string cloudsInner)
        {
            return BodyTestSupport.Run(BodyTestSupport.Planet("Test", BodyTestSupport.HomeYear), "REDEFINITION_BODY{name = Test;CLOUDS{" + cloudsInner + "}}");
        }

        private const string GoodType = "TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 0;key1 = 0.5 1;key2 = 1 0}}";

        [TestMethod]
        public void TheSoftWaterNumbersReadFromTheLayerAndTypeNodesWithTheirDefaults()
        {
            string typeWith = "TYPE{name = T;baseShare = 0;topShare = 1;baseGain = 0.7;riseShare = 0.25;densityScale = 0.4;PROFILE{key0 = 0 0;key1 = 0.5 1;key2 = 1 0}}";
            BodyParseResult r = Parse("LAYER{name = A;innerAltitudeM = 100;outerAltitudeM = 900;softnessScale = 2;waterPower = 1;" + typeWith + "}"
                + "LAYER{name = B;innerAltitudeM = 100;outerAltitudeM = 900;" + GoodType + "}");
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            CloudLayerSpec a = r.Climate.Clouds.Layers.Single(l => l.Name == "A"), b = r.Climate.Clouds.Layers.Single(l => l.Name == "B");
            Assert.AreEqual(2f, a.SoftnessScale);
            Assert.AreEqual(1f, a.WaterPower);
            Assert.AreEqual(0.7f, a.Types[0].BaseGain, 1e-6f);
            Assert.AreEqual(0.25f, a.Types[0].RiseShare, 1e-6f);
            Assert.AreEqual(0.4f, a.Types[0].DensityScale, 1e-6f);
            // Absent keys: the defaults measured in step (a) of E40 soft density.
            Assert.AreEqual(1f, b.SoftnessScale);
            Assert.AreEqual(2f, b.WaterPower);
            Assert.AreEqual(0.3f, b.Types[0].BaseGain, 1e-6f);
            Assert.AreEqual(0.5f, b.Types[0].RiseShare, 1e-6f);
            Assert.AreEqual(1f, b.Types[0].DensityScale);
            // The built layer and type carry them.
            CloudLayerDef built = a.Build(1);
            Assert.AreEqual(2f, built.SoftnessScale);
            Assert.AreEqual(0.4f, built.Types[0].DensityScale, 1e-6f);
        }

        [TestMethod]
        public void TheLayersLightResponseReadsFromTheLayerNodeAndDefaultsToTheGlobalFit()
        {
            BodyParseResult r = Parse("LAYER{name = A;innerAltitudeM = 100;outerAltitudeM = 900;lightAmbient = 0.6;lightIsotropy = 0.5;" + GoodType + "}"
                + "LAYER{name = B;innerAltitudeM = 100;outerAltitudeM = 900;" + GoodType + "}");
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            CloudLayerSpec a = r.Climate.Clouds.Layers.Single(l => l.Name == "A"), b = r.Climate.Clouds.Layers.Single(l => l.Name == "B");
            Assert.AreEqual(0.6f, a.LightAmbient, 1e-6f);
            Assert.AreEqual(1f, b.LightAmbient);
            Assert.AreEqual(0.5f, a.LightIsotropy, 1e-6f);
            Assert.AreEqual(0f, b.LightIsotropy);
            CloudLayerDef built = a.Build(1);
            Assert.AreEqual(0.6f, built.LightAmbient, 1e-6f);
            Assert.AreEqual(0.5f, built.LightIsotropy, 1e-6f);
        }

        // E40 L6 light: the sky's light inside the clouds of a lit layer (ambientMs, ambientDecay) is a layer key with the two-stream defaults.
        [TestMethod]
        public void TheSkysDiffusionReadsFromTheLayerNodeAndDefaultsToTheTwoStreamValues()
        {
            BodyParseResult r = Parse("LAYER{name = A;innerAltitudeM = 100;outerAltitudeM = 900;ambientMs = 2;ambientDecay = 0.2;lightPlateau = 0.6;" + GoodType + "}"
                + "LAYER{name = B;innerAltitudeM = 100;outerAltitudeM = 900;" + GoodType + "}");
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            CloudLayerSpec a = r.Climate.Clouds.Layers.Single(l => l.Name == "A"), b = r.Climate.Clouds.Layers.Single(l => l.Name == "B");
            Assert.AreEqual(2f, a.AmbientMs, 1e-6f);
            Assert.AreEqual(0.2f, a.AmbientDecay, 1e-6f);
            Assert.AreEqual(CloudLayerDef.DefaultAmbientMs, b.AmbientMs);
            Assert.AreEqual(CloudLayerDef.DefaultAmbientDecay, b.AmbientDecay);
            CloudLayerDef built = a.Build(1);
            Assert.AreEqual(2f, built.AmbientMs, 1e-6f);
            Assert.AreEqual(0.2f, built.AmbientDecay, 1e-6f);
            Assert.AreEqual(0.6f, a.LightPlateau, 1e-6f);
            Assert.AreEqual(0f, b.LightPlateau);
            Assert.AreEqual(0.6f, built.LightPlateau, 1e-6f);
            // The decay is the two-stream's 0.75 (1 - g) of the droplets' asymmetry g = 0.85.
            Assert.AreEqual(0.75f * (1f - 0.85f), CloudLayerDef.DefaultAmbientDecay, 0.005f);
        }

        // E40 L2 D1: the type's height-shaped threshold (LIFT, a curve like PROFILE) and the layer's fibre stretch.
        [TestMethod]
        public void TheShapeKeysReadFromTheTypeAndLayerNodesAndDefaultToNone()
        {
            string lifted = "TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 0;key1 = 0.5 1;key2 = 1 0}LIFT{key0 = 0 0;key1 = 0.4 0;key2 = 1 0.3}}";
            BodyParseResult r = Parse("LAYER{name = A;innerAltitudeM = 100;outerAltitudeM = 900;fibreStretch = 6;verticalScale = 0.4;" + lifted + "}"
                + "LAYER{name = B;innerAltitudeM = 100;outerAltitudeM = 900;" + GoodType + "}");
            Assert.AreEqual(0, r.Warnings.Count, string.Join("; ", r.Warnings));
            CloudLayerSpec a = r.Climate.Clouds.Layers.Single(l => l.Name == "A"), b = r.Climate.Clouds.Layers.Single(l => l.Name == "B");
            Assert.AreEqual(6f, a.FibreStretch);
            Assert.AreEqual(0.4f, a.VerticalScale, 1e-6f);
            Assert.AreEqual(0f, b.VerticalScale, "no vertical scale: the body's sphereScale");
            Assert.AreEqual(0.4f, a.Build(1).VerticalScale, 1e-6f);
            Assert.AreEqual(3, a.Types[0].Lift.Count);
            Assert.AreEqual(1f, b.FibreStretch);
            Assert.IsNull(b.Types[0].Lift);
            // The mid-level sheet's rule (altoType, altoFrom, altoFullAt): none by default, built into the layer, and its type's middle height is found from the table.
            r = Parse("LAYER{name = D;innerAltitudeM = 1000;outerAltitudeM = 3000;altoType = 1;altoFrom = 0.1;altoFullAt = 0.4;TYPE{name = Low;baseShare = 0;topShare = 0.2;PROFILE{key0 = 0 1}}"
                + "TYPE{name = Sheet;baseShare = 0.5;topShare = 0.7;PROFILE{key0 = 0 1}}}");
            CloudLayerSpec d = r.Climate.Clouds.Layers.Single();
            Assert.AreEqual(-1f, b.AltoType);
            Assert.AreEqual(1f, d.AltoType);
            CloudLayerDef sheet = d.Build(1);
            Assert.AreEqual(0.1f, sheet.AltoFrom, 1e-6f);
            Assert.AreEqual(0.4f, sheet.AltoFull, 1e-6f);
            Assert.AreEqual(1000.0 + 0.6 * 2000.0, sheet.TypeMidHeightM(1f), 1e-3, "the sheet's middle: 0.6 of the layer");
            Assert.AreEqual(1000.0 + 0.1 * 2000.0, sheet.TypeMidHeightM(0f), 1e-3);
            CloudLayerDef built = a.Build(1);
            Assert.AreEqual(6f, built.FibreStretch);
            Assert.AreEqual(0.0, built.Types[0].LiftAt(0.2), 1e-6);
            Assert.AreEqual(0.15, built.Types[0].LiftAt(0.7), 1e-6, "linear between the keys 0.4 -> 0 and 1 -> 0.3");
            Assert.AreEqual(0.3, built.Types[0].LiftAt(2.0), 1e-6, "clamped to the top");
            Assert.AreEqual(0.0, b.Build(1).Types[0].LiftAt(0.9), 0.0);
            // A lift that cannot be read is a warning and the type has none (the type itself stays); a stretch below 1 is brought up to 1.
            r = Parse("LAYER{name = C;innerAltitudeM = 100;outerAltitudeM = 900;fibreStretch = 0.2;TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 1}LIFT{key0 = 0 -0.2}}}");
            Assert.AreEqual(1, r.Climate.Clouds.Layers.Count);
            Assert.IsNull(r.Climate.Clouds.Layers[0].Types[0].Lift);
            Assert.AreEqual(1f, r.Climate.Clouds.Layers[0].FibreStretch);
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("lift")), string.Join("; ", r.Warnings));
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("fibreStretch")), string.Join("; ", r.Warnings));
        }

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

        // E40 L3: the meso-scale cover pattern (node MESO), the type patches (typePatchKm, typePatchBlend) and the types' climate (node CLIMATE with LATITUDE, SURFACE, HUMIDITY, PRECIPITATION).
        [TestMethod]
        public void TheMesoPatternAndTheTypeClimateAreReadBuiltAndDefaultToNone()
        {
            string climate = "CLIMATE{weight = 0.8;LATITUDE{key0 = 0 1;key1 = 1 0.2}SURFACE{key0 = 0 0.5;key1 = 1 1}HUMIDITY{key0 = 0 0;key1 = 1 1}PRECIPITATION{key0 = 0 1;key1 = 1 0}}";
            BodyParseResult r = Parse("LAYER{name = A;innerAltitudeM = 100;outerAltitudeM = 900;typePatchKm = 33;typePatchDepths = 9;typePatchBlend = 0.1;MESO{scaleKm = 60;scaleDepths = 6;octaves = 3;amount = 0.5;edge = 0.2;shoulder = 2;cells = 1;stretch = 4;rainKeep = 0.5;scatter = 0.7;scatterDepths = 2;scatterSoft = 0.2;scaleSystems = 0.6;rainKeepFrom = 0.3;rainKeepFullAt = 0.9;rainMin = 0.4;rainMinFrom = 0.05;rainMinFullAt = 0.1}"
                + "TYPE{name = T;baseShare = 0;topShare = 1;PROFILE{key0 = 0 1}" + climate + "}}"
                + "LAYER{name = B;innerAltitudeM = 100;outerAltitudeM = 900;" + GoodType + "}"
                + "LAYER{name = C;innerAltitudeM = 100;outerAltitudeM = 900;MESO{amount = 7;octaves = 9;scaleKm = 1}" + GoodType + "}");
            CloudLayerSpec a = r.Climate.Clouds.Layers.Single(l => l.Name == "A"), b = r.Climate.Clouds.Layers.Single(l => l.Name == "B"), c = r.Climate.Clouds.Layers.Single(l => l.Name == "C");
            Assert.AreEqual(33f, a.TypePatchKm);
            Assert.AreEqual(0.1f, a.TypePatchBlend, 1e-6f);
            Assert.AreEqual(60f, a.Meso.ScaleKm);
            Assert.AreEqual(6f, a.Meso.ScaleDepths);
            Assert.AreEqual(9f, a.TypePatchDepths);
            Assert.AreEqual(3, a.Meso.Octaves);
            Assert.AreEqual(0.5f, a.Meso.Amount);
            Assert.AreEqual(0.2f, a.Meso.Edge, 1e-6f);
            Assert.AreEqual(2f, a.Meso.Shoulder);
            Assert.IsTrue(a.Meso.Cells);
            Assert.AreEqual(4f, a.Meso.Stretch);
            Assert.AreEqual(0.5f, a.Meso.RainKeep);
            Assert.AreEqual(0.7f, a.Meso.Scatter, 1e-6f);
            Assert.AreEqual(2f, a.Meso.ScatterDepths, 1e-6f);
            Assert.AreEqual(0.2f, a.Meso.ScatterSoft, 1e-6f);
            Assert.AreEqual(0.6f, a.Meso.ScaleSystems, 1e-6f);
            Assert.AreEqual(0.3f, a.Meso.RainKeepFrom, 1e-6f);
            Assert.AreEqual(0.9f, a.Meso.RainKeepFullAt, 1e-6f);
            Assert.AreEqual(0.4f, a.Meso.RainMin, 1e-6f);
            Assert.AreEqual(0.05f, a.Meso.RainMinFrom, 1e-6f);
            Assert.AreEqual(0.1f, a.Meso.RainMinFullAt, 1e-6f);
            Assert.AreEqual(0.8f, a.Types[0].Climate.Weight, 1e-6f);
            Assert.AreEqual(0.6f, a.Types[0].Climate.Latitude.Evaluate(0.5f), 1e-6f);
            Assert.AreEqual(0.75f, a.Types[0].Climate.Surface.Evaluate(0.5f), 1e-6f);
            Assert.AreEqual(0.5f, a.Types[0].Climate.Humidity.Evaluate(0.5f), 1e-6f);
            Assert.AreEqual(0.5f, a.Types[0].Climate.Precipitation.Evaluate(0.5f), 1e-6f);
            CloudLayerDef built = a.Build(1);
            Assert.IsTrue(built.HasClimate);
            Assert.AreEqual(4f, built.Meso.Stretch);
            Assert.AreEqual(33f, built.TypePatchKm);
            Assert.IsNotNull(built.Types[0].Climate);
            // Nothing there: no pattern, no climate, the defaults of the patches.
            Assert.IsNull(b.Meso);
            Assert.IsNull(b.Types[0].Climate);
            Assert.AreEqual(0f, b.TypePatchKm, "no explicit size in km: the size is in depths of the body's troposphere");
            Assert.AreEqual(6.7f, b.TypePatchDepths, 1e-6f);
            Assert.AreEqual(0.06f, b.TypePatchBlend, 1e-6f);
            Assert.IsFalse(b.Build(1).HasClimate);
            // Numbers outside the range are brought into it with a warning; a MESO without amount is off.
            Assert.AreEqual(1f, c.Meso.Amount);
            Assert.AreEqual(4, c.Meso.Octaves);
            Assert.AreEqual(1f, c.Meso.ScaleKm);
            Assert.AreEqual(4.3f, c.Meso.ScaleDepths, 1e-6f, "no scaleDepths key: the default size in depths");            Assert.IsFalse(c.Meso.Cells, "no cells key: blobs");
            Assert.AreEqual(1f, c.Meso.Shoulder, "no shoulder key: a linear ramp");
            Assert.AreEqual(0f, c.Meso.Scatter, "no scatter key: the plain ramp");
            Assert.AreEqual(0f, c.Meso.ScaleSystems, "no scaleSystems key: the size is in depths");
            Assert.AreEqual(0f, c.Meso.RainMin, "no rainMin key: none");
            Assert.AreEqual(0.02f, c.Meso.RainKeepFrom, 1e-6f, "no rainKeepFrom key: the stage's old 0.02");
            Assert.AreEqual(0.25f, c.Meso.RainKeepFullAt, 1e-6f, "no rainKeepFullAt key: the stage's old 0.25");
            Assert.AreEqual(CloudMesoSpec.DefaultScatterDepths, c.Meso.ScatterDepths, 1e-6f);
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("amount")), string.Join("; ", r.Warnings));
            Assert.IsFalse(new CloudMesoSpec().On);
        }

        [TestMethod]
        public void TheRainTypeIsReadBuiltAndDefaultsToTheLastType()
        {
            BodyParseResult r = Parse("LAYER{name = A;innerAltitudeM = 0;outerAltitudeM = 2100;fairType = 0.25;rainType = 0.75;" + GoodType + "}"
                + "LAYER{name = B;innerAltitudeM = 3000;outerAltitudeM = 4000;" + GoodType + "}"
                + "LAYER{name = C;innerAltitudeM = 5000;outerAltitudeM = 6000;rainType = 3;" + GoodType + "}");
            Assert.AreEqual(0.75f, r.Climate.Clouds.Layers[0].RainType);
            Assert.AreEqual(1f, r.Climate.Clouds.Layers[1].RainType, "no key: the last type");
            Assert.AreEqual(1f, r.Climate.Clouds.Layers[2].RainType, "brought into the range");
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("rainType")));
            Assert.AreEqual(0.75f, r.Climate.Clouds.Layers[0].Build(1).RainType);
        }

        // E40 (lead investigation): the type at no precipitation is a key of the layer. Without it the type value is the precipitation level (the first type in fair
        // weather, as before); the tuning key cloud.typeFair.<name> goes before the layer's value, and 0 there gives the precipitation level again.
        [TestMethod]
        public void TheFairTypeIsReadBuiltAndYieldsToTheTuningKey()
        {
            BodyParseResult r = Parse("LAYER{name = A;innerAltitudeM = 0;outerAltitudeM = 2100;fairType = 0.5;" + GoodType + "}"
                + "LAYER{name = B;innerAltitudeM = 3000;outerAltitudeM = 4000;" + GoodType + "}"
                + "LAYER{name = C;innerAltitudeM = 5000;outerAltitudeM = 6000;fairType = 3;" + GoodType + "}");
            Assert.AreEqual(0.5f, r.Climate.Clouds.Layers[0].FairType);
            Assert.AreEqual(-1f, r.Climate.Clouds.Layers[1].FairType, "no key: the precipitation level itself");
            Assert.AreEqual(1f, r.Climate.Clouds.Layers[2].FairType, "brought into the range");
            Assert.IsTrue(r.Warnings.Any(w => w.Contains("fairType")));
            Assert.AreEqual(0.5f, r.Climate.Clouds.Layers[0].Build(1).FairType);
            Assert.AreEqual(-1f, r.Climate.Clouds.Layers[1].Build(1).FairType);
            Assert.AreEqual(0.5f, CloudLookTuning.FairType(-1f, 0.5f), "no tuning key: the layer's");
            Assert.AreEqual(0.25f, CloudLookTuning.FairType(0.25f, 0.5f), "the tuning key goes first");
            Assert.AreEqual(0f, CloudLookTuning.FairType(0f, 0.5f), "the tuning key 0 is the precipitation level again");
            Assert.AreEqual(-1f, CloudLookTuning.FairType(-1f, -1f));
            Assert.AreEqual(0.5f, CloudLookTuning.TypeValue(0f, CloudLookTuning.FairType(-1f, 0.5f), -1f), 1e-6f, "no precipitation: the fair type");
            Assert.AreEqual(1f, CloudLookTuning.TypeValue(1f, CloudLookTuning.FairType(-1f, 0.5f), -1f), 1e-6f, "full precipitation: the last type");
            Assert.AreEqual(0.3f, CloudLookTuning.TypeValue(0.3f, CloudLookTuning.FairType(0f, 0.5f), -1f), 1e-6f, "switched off by the tuning key");
        }
    }
}
