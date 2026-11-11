using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.E41
{
    // The per-body EVE_CLOUDS files that tools/gen_eve_clouds.py makes from one template: every block parses as a ConfigNode with the
    // keys EVE's layer needs, the cloud types are the table of CONTRACT.md section 5, and every level of the precipitation scale maps
    // to a type the layer holds.
    [TestClass]
    public class CloudTemplateTests
    {
        private static readonly string[] Bodies = { "Kerbin", "Duna", "Laythe" };

        private static List<ConfigNode> Objects(string body)
        {
            ConfigNode root = ConfigNode.Load(E41Support.CloudsFile(body));
            ConfigNode clouds = null;
            foreach (ConfigNode n in root.nodes)
            {
                if (n.name.StartsWith("EVE_CLOUDS", StringComparison.Ordinal)) clouds = n;
            }
            Assert.IsNotNull(clouds, body + ": no EVE_CLOUDS node");
            return clouds.GetNodes("OBJECT").ToList();
        }

        private static ConfigNode Volume(ConfigNode obj)
        {
            return obj.GetNode("layerRaymarchedVolumeV5");
        }

        private static double Num(string text)
        {
            return double.Parse(text, CultureInfo.InvariantCulture);
        }

        private static ConfigNode VolumeObject(string body)
        {
            return Objects(body).Single(o => o.GetNode("layer2D") != null);
        }

        [TestMethod]
        public void EveryBlockOfEveryBodyHasTheKeysOfAMinimumLayer()
        {
            foreach (string body in Bodies)
            {
                List<ConfigNode> objects = Objects(body);
                Assert.AreEqual(1, objects.Count, body + ": one volume layer; the pack's layers keep the particle fields");
                var names = new HashSet<string>();
                foreach (ConfigNode obj in objects)
                {
                    string name = obj.GetValue("name");
                    Assert.IsFalse(string.IsNullOrEmpty(name), body + ": name");
                    Assert.IsTrue(names.Add(name), body + ": the layer name " + name + " is there twice");
                    Assert.AreEqual(body, obj.GetValue("body"), name);
                    Assert.IsTrue(Num(obj.GetValue("altitude")) > 0, name + ": altitude");
                    ConfigNode v = Volume(obj);
                    Assert.IsNotNull(v, name + ": layerRaymarchedVolumeV5");
                    Assert.IsNotNull(v.GetNode("noise"), name + ": noise");
                    Assert.IsNotNull(v.GetNode("raymarchingSettings"), name + ": raymarchingSettings");
                    Assert.IsFalse(string.IsNullOrEmpty(v.GetNode("coverageMap").GetValue("value")), name + ": coverageMap");
                    ConfigNode types = v.GetNode("cloudTypes");
                    Assert.IsNotNull(types, name + ": cloudTypes");
                    Assert.IsTrue(types.GetNodes("Item").Length >= 1, name + ": at least one cloud type");
                }
            }
        }

        [TestMethod]
        public void TheVolumeLayerHasATypeMapAndALayer2DAndNoParticleField()
        {
            foreach (string body in Bodies)
            {
                ConfigNode volume = VolumeObject(body);
                Assert.IsNotNull(Volume(volume).GetNode("cloudTypeMap"), body + ": the painter writes a type map, and the scan needs one");
                Assert.IsNotNull(volume.GetNode("layer2D"), body + ": the orbit picture");
                Assert.IsNull(Volume(volume).GetNode("particleField"), body + ": the particle fields stay on the pack's layers");
                string fx = Volume(volume).GetNode("raymarchingSettings").GetValue("fxOnlyLayer");
                Assert.IsNull(fx, body + ": the layer raymarches");
            }
        }

        [TestMethod]
        public void KerbinsFileAppendsOurLayerToTheNodesLayerListAndTheOthersDoNot()
        {
            string kerbin = File.ReadAllText(E41Support.CloudsFile("Kerbin"));
            Assert.IsTrue(kerbin.Contains("@REDEFINITION_BODY[Kerbin]:NEEDS[StockVolumetricClouds]:FINAL"));
            Assert.IsTrue(kerbin.Contains("@weatherLayers ^= :$:, ReDefinition-Atmospheres-Kerbin:"));
            // The list in Kerbin.cfg is a comma list the reader splits; the patch's regex appends one name at its end.
            string list = E41Support.BodyNode("Kerbin").GetNode("CLOUDS").GetValue("weatherLayers");
            string patched = System.Text.RegularExpressions.Regex.Replace(list, "$", ", ReDefinition-Atmospheres-Kerbin");
            var rules = BodyRuleReaders.Clouds(new BodyNodeReader(ConfigNode.Parse("CLOUDS { weatherLayers = " + patched + " }").GetNode("CLOUDS"), "CLOUDS", new List<string>()));
            CollectionAssert.Contains(rules.WeatherLayers.ToList(), "ReDefinition-Atmospheres-Kerbin");
            Assert.AreEqual(3, rules.WeatherLayers.Count);
            Assert.IsFalse(File.ReadAllText(E41Support.CloudsFile("Duna")).Contains("@REDEFINITION_BODY"));
            Assert.IsFalse(File.ReadAllText(E41Support.CloudsFile("Laythe")).Contains("@REDEFINITION_BODY"));
        }

        [TestMethod]
        public void TheTypeTableIsTheOneOfTheContract()
        {
            foreach (string body in new[] { "Kerbin", "Laythe" })
            {
                ConfigNode[] items = Volume(VolumeObject(body)).GetNode("cloudTypes").GetNodes("Item");
                CollectionAssert.AreEqual(new[] { "Snow", "Stratus", "Storm" }, items.Select(i => i.GetValue("typeName")).ToArray(), body);
                ConfigNode light = items[0], stratus = items[1], storm = items[2];
                // SnowLight: 600 m base, 300 m thick, density 0.002 to 0.003.
                Assert.AreEqual(600.0, Num(light.GetValue("minAltitude")));
                Assert.AreEqual(300.0, Num(light.GetValue("maxAltitude")) - Num(light.GetValue("minAltitude")));
                Assert.IsTrue(Num(light.GetValue("density")) >= 0.002 && Num(light.GetValue("density")) <= 0.003);
                // SnowStratus: 400 to 1800 m, density 0.005 to 0.008.
                Assert.AreEqual(400.0, Num(stratus.GetValue("minAltitude")));
                Assert.AreEqual(1800.0, Num(stratus.GetValue("maxAltitude")));
                Assert.IsTrue(Num(stratus.GetValue("density")) >= 0.005 && Num(stratus.GetValue("density")) <= 0.008);
                // The storm type: 300 to 2300 m, density 0.012 to 0.016, the darkest.
                Assert.AreEqual(300.0, Num(storm.GetValue("minAltitude")));
                Assert.AreEqual(2300.0, Num(storm.GetValue("maxAltitude")));
                Assert.IsTrue(Num(storm.GetValue("density")) >= 0.012 && Num(storm.GetValue("density")) <= 0.016);
                Assert.IsTrue(Num(storm.GetValue("multipleScatteringBrightness")) < Num(stratus.GetValue("multipleScatteringBrightness")));
                Assert.IsTrue(Num(stratus.GetValue("multipleScatteringBrightness")) < Num(light.GetValue("multipleScatteringBrightness")));
                // The layer colour is the light grey of the lightest type.
                string[] colour = Volume(VolumeObject(body)).GetValue("color").Split(',');
                CollectionAssert.AreEqual(new[] { 190.0, 192.0, 196.0 }, colour.Take(3).Select(c => Num(c.Trim())).ToArray());
            }
        }

        [TestMethod]
        public void TheDeckThickensWithTheLevelAndEveryTypeIsOneTheRolesFind()
        {
            ConfigNode[] items = Volume(VolumeObject("Kerbin")).GetNode("cloudTypes").GetNodes("Item");
            float[] mins = items.Select(i => (float)Num(i.GetValue("minAltitude"))).ToArray();
            float[] maxs = items.Select(i => (float)Num(i.GetValue("maxAltitude"))).ToArray();
            TypeSlots slots = TypeRoles.FromNames(items.Select(i => i.GetValue("typeName")).ToList(), CloudRules.Default);
            Assert.IsTrue(slots.Has(CloudRole.SnowLight) && slots.Has(CloudRole.SnowStratus) && slots.Has(CloudRole.Storm), "the three roles are found by the default lists");
            Assert.IsTrue(slots.IndexOf(CloudRole.SnowLight) < slots.IndexOf(CloudRole.SnowStratus) && slots.IndexOf(CloudRole.SnowStratus) < slots.IndexOf(CloudRole.Storm),
                "the three types lie next to each other in the order of the levels, so the blend passes through no other type");
            float previous = -1f;
            for (int i = 0; i <= 20; i++)
            {
                float level = i / 20f;
                float thickness = CloudTypeEncoding.DeckThicknessM(mins, maxs, CloudTypeEncoding.Value(slots, level, false));
                Assert.IsTrue(thickness >= previous - 1e-3f, "the deck does not thin out as the level rises (level " + level + ")");
                previous = thickness;
            }
            Assert.AreEqual(300f, CloudTypeEncoding.DeckThicknessM(mins, maxs, CloudTypeEncoding.Value(slots, 0.1f, false)), 1f);
            Assert.AreEqual(2000f, CloudTypeEncoding.DeckThicknessM(mins, maxs, CloudTypeEncoding.Value(slots, 0.95f, false)), 1f);
        }

        [TestMethod]
        public void EveryLevelBandMapsToATypeOfTheLayer()
        {
            var bands = (PrecipBand[])Enum.GetValues(typeof(PrecipBand));
            foreach (string body in Bodies)
            {
                CloudRules rules = body == "Duna" ? new CloudRules(dustTypeNames: new[] { "dust", "Dust" }) : CloudRules.Default;
                bool dust = body == "Duna";
                ConfigNode[] items = Volume(VolumeObject(body)).GetNode("cloudTypes").GetNodes("Item");
                TypeSlots slots = TypeRoles.FromNames(items.Select(i => i.GetValue("typeName")).ToList(), rules);
                foreach (PrecipBand band in bands)
                {
                    if (band == PrecipBand.None) continue;
                    CloudRole role = CloudTypeEncoding.RoleOfBand(band, dust);
                    Assert.IsTrue(slots.Has(role), body + ": the band " + band + " selects " + role + " and the layer has no type for it");
                    float value = CloudTypeEncoding.Value(slots, CloudTypeEncoding.RepresentativeLevel(band), dust);
                    Assert.IsTrue(value >= 0f && value <= 1f);
                }
            }
        }

        [TestMethod]
        public void TheDunaLayerHoldsOneDustTypeThatTheDunaNodeNames()
        {
            ConfigNode[] items = Volume(VolumeObject("Duna")).GetNode("cloudTypes").GetNodes("Item");
            Assert.AreEqual(1, items.Length);
            Assert.AreEqual("Dust", items[0].GetValue("typeName"));
            ConfigNode node = E41Support.BodyNode("Duna");
            CloudRules rules = BodyRuleReaders.Clouds(new BodyNodeReader(node.GetNode("CLOUDS"), "CLOUDS", new List<string>()));
            Assert.IsTrue(rules.DustTypeNames.Contains("Dust"));
            Assert.IsTrue(TypeRoles.FromNames(new[] { "Dust" }, rules).Has(CloudRole.Dust));
        }

        [TestMethod]
        public void TheScaledFadeFollowsTheRadiusOfTheBody()
        {
            var radius = new Dictionary<string, double> { { "Kerbin", 600000 }, { "Duna", 320000 }, { "Laythe", 500000 } };
            foreach (string body in Bodies)
            {
                ConfigNode v = Volume(VolumeObject(body));
                Assert.AreEqual(radius[body] / 3.0, Num(v.GetValue("scaledFadeStartAltitude")), 1.0, body);
                Assert.IsTrue(Num(v.GetValue("scaledFadeEndAltitude")) > Num(v.GetValue("scaledFadeStartAltitude")));
            }
            Assert.AreEqual(200000.0, Num(Volume(VolumeObject("Kerbin")).GetValue("scaledFadeStartAltitude")), 1.0, "Kerbin keeps the numbers of the cell layer it replaces");
            Assert.AreEqual(260000.0, Num(Volume(VolumeObject("Kerbin")).GetValue("scaledFadeEndAltitude")), 1.0);
        }

        [TestMethod]
        public void TheCheckedInFilesAreTheOutputOfTheGenerator()
        {
            string script = Path.Combine(E41Support.Root(), "tools", "gen_eve_clouds.py");
            Assert.IsTrue(File.Exists(script));
            var start = new ProcessStartInfo("python", "\"" + script + "\" --check")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            Process p;
            try
            {
                p = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Inconclusive("python is not on the PATH");
                return;
            }
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            Assert.AreEqual(0, p.ExitCode, output);
        }

        [TestMethod]
        public void OurLayerIsFoundByRoleOnTheThreeBodies()
        {
            foreach (string body in Bodies)
            {
                CloudRules rules = body == "Duna" ? new CloudRules(dustTypeNames: new[] { "Dust" }) : CloudRules.Default;
                List<PackLayerInfo> layers = E41Support.Layers(E41Support.CloudsFile(body));
                LayerDiscovery found = LayerRoleDiscovery.Discover(layers, body, rules, 0);
                Assert.AreEqual(1, found.Taken.Count, body + ": " + found.Summary());
                Assert.AreEqual("ReDefinition-Atmospheres-" + body, found.Taken[0].Name);
                Assert.AreEqual(1, found.Decisions.Count, body);
            }
        }
    }
}
