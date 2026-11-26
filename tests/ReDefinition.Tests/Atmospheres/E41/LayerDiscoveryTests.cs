using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.E41
{
    // The discovery of layers by role on the structure of the installed StockVolumetricClouds pack (tests/data/packs: its 19 layers with
    // their real names, altitudes, maps and cloud types), together with our own generated layer files, and the check that the scan of the
    // game runs this discovery.
    [TestClass]
    public class LayerDiscoveryTests
    {
        private static List<PackLayerInfo> Pack()
        {
            return E41Support.Layers(E41Support.PackFixture("stock-volumetric-clouds.cfg"));
        }

        private static List<PackLayerInfo> Stock()
        {
            return E41Support.Layers(E41Support.PackFixture("stock-volumetric-clouds.cfg"), E41Support.CloudsFile("Kerbin"), E41Support.CloudsFile("Duna"),
                E41Support.CloudsFile("Laythe"));
        }

        private static string[] Names(LayerDiscovery d)
        {
            return d.Taken.Select(l => l.Name).ToArray();
        }

        // The CLOUDS node of the shipped body file, read as the game reads it.
        private static CloudRules Shipped(string body)
        {
            ConfigNode node = E41Support.BodyNode(body);
            return BodyRuleReaders.Clouds(new BodyNodeReader(node.GetNode("CLOUDS"), "CLOUDS", new List<string>()));
        }

        private static SkipReason Skip(LayerDiscovery d, string layer)
        {
            return d.Decisions.Single(x => x.Layer.Name == layer).Skip;
        }

        [TestMethod]
        public void TheReaderGetsEveryLayerOfThePackWithItsMapsAndFields()
        {
            List<PackLayerInfo> layers = Pack();
            Assert.AreEqual(19, layers.Count);
            foreach (var expected in new Dictionary<string, int> { { "Kerbin", 6 }, { "Duna", 3 }, { "Eve", 2 }, { "Jool", 4 }, { "Laythe", 4 } })
            {
                Assert.AreEqual(expected.Value, layers.Count(l => l.Body == expected.Key), expected.Key);
            }
            PackLayerInfo weather = layers.Single(l => l.Name == "Kerbin-Weather-1");
            Assert.AreEqual("Kerbin", weather.Body);
            Assert.AreEqual(2200.0, weather.AltitudeM);
            Assert.IsTrue(weather.HasCoverageMap && weather.HasTypeMap && !weather.FxOnly);
            CollectionAssert.AreEqual(new[] { "Fog", "Rain", "Thunder", "Polar-fog", "Polar-snow" }, weather.TypeNames);
            Assert.AreEqual("rain-Kerbin", weather.ParticleField);
            PackLayerInfo particles = layers.Single(l => l.Name == "Kerbin-Snow-Particles-1");
            Assert.IsTrue(particles.FxOnly);
            Assert.IsFalse(particles.HasTypeMap);
            Assert.IsTrue(particles.HasCoverageMap);
            PackLayerInfo scattered = layers.Single(l => l.Name == "Duna-dust-scattered");
            Assert.IsTrue(scattered.HasCoverageMap && !scattered.HasTypeMap);
            CollectionAssert.AreEqual(new[] { "Dust" }, scattered.TypeNames);
            CollectionAssert.Contains(layers.Single(l => l.Name == "Kerbin-base-layer").TypeNames, "Congestus 1");
        }

        [TestMethod]
        public void KerbinTakesEveryLayerWithARoleInTheOrderOfAltitude()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 0);
            CollectionAssert.AreEqual(new[] { "Kerbin-Weather-1", "Kerbin-base-layer", "Kerbin-weather-2", "ReDefinition-Atmospheres-Kerbin" }, Names(d), d.Summary());
            Assert.AreEqual(LayerRoles.Snow | LayerRoles.Stratus | LayerRoles.Storm, d.Roles);
            Assert.AreEqual(LayerRoles.Snow | LayerRoles.Storm, d.Decisions.Single(x => x.Layer.Name == "Kerbin-Weather-1").Roles, "Polar-snow and Thunder");
            Assert.AreEqual(LayerRoles.Stratus, d.Decisions.Single(x => x.Layer.Name == "Kerbin-base-layer").Roles);
            Assert.AreEqual(SkipReason.NoRole, Skip(d, "Kerbin-cumulonimbus-layer"), "trail, edge and core are no role of ours");
            Assert.AreEqual(SkipReason.FxOnly, Skip(d, "Kerbin-Snow-Particles-1"));
            Assert.AreEqual(SkipReason.FxOnly, Skip(d, "Kerbin-Snow-Particles-2"));
            Assert.IsFalse(d.None);
        }

        [TestMethod]
        public void TheBaseLayerIsLeftToEveWhereTheNodeNamesIt()
        {
            var rules = new CloudRules(baseLayer: "Kerbin-base-layer");
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", rules, 0);
            Assert.AreEqual(SkipReason.BaseLayer, Skip(d, "Kerbin-base-layer"));
            CollectionAssert.AreEqual(new[] { "Kerbin-Weather-1", "Kerbin-weather-2", "ReDefinition-Atmospheres-Kerbin" }, Names(d), d.Summary());
        }

        [TestMethod]
        public void TheShippedKerbinNodeTakesTheTwoWeatherLayersOfThePackByName()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", Shipped("Kerbin"), 0);
            CollectionAssert.AreEqual(new[] { "Kerbin-Weather-1", "Kerbin-weather-2" }, Names(d), d.Summary());
            Assert.AreEqual(SkipReason.NotNamed, Skip(d, "Kerbin-base-layer"));
        }

        [TestMethod]
        public void OurLayerAndThePacksLayersOnOneBodyBothCountAndTheCapStopsTheCount()
        {
            LayerDiscovery open = LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 0);
            Assert.AreEqual(2, open.Taken.Select(l => l.Origin).Distinct().Count(), "layers of the pack file and of our file: " + open.Summary());
            LayerDiscovery capped = LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 3);
            Assert.AreEqual(3, capped.Taken.Count);
            Assert.AreEqual(open.Taken.Count - 3, capped.Decisions.Count(x => x.Skip == SkipReason.OverCap));
            Assert.AreEqual(SkipReason.OverCap, Skip(capped, "ReDefinition-Atmospheres-Kerbin"), "the highest layer falls off first");
            Assert.IsTrue(capped.Summary().Contains("cap 3"));
        }

        [TestMethod]
        public void DunaDrivesOnlyOurLayerBecauseThePacksDustLayersCannotBePainted()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Duna", Shipped("Duna"), 0);
            CollectionAssert.AreEqual(new[] { "ReDefinition-Atmospheres-Duna" }, Names(d), d.Summary());
            Assert.AreEqual(LayerRoles.Dust, d.Roles);
            Assert.AreEqual(SkipReason.NoMaps, Skip(d, "Duna-dust-scattered"), "a Dust type, but no type map");
            Assert.AreEqual(SkipReason.NoRole, Skip(d, "Duna-duststorm-big"), "Wake, Front and tornado parts name no dust type");
            Assert.AreEqual(SkipReason.NoRole, Skip(d, "Duna-rare-cirrus"));
        }

        [TestMethod]
        public void WithoutADustListNoDunaLayerHasARoleAndTheCloudsAreOurs()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Duna", CloudRules.Default, 0);
            Assert.IsTrue(d.None, d.Summary());
            Assert.AreEqual(CloudSourceKind.ReDefinition, d.EffectiveSource(CloudSourceKind.Eve));
        }

        [TestMethod]
        public void EveHasALayerTheRoleLogicWouldTakeAndTheShippedNodeSwitchesTheDiscoveryOff()
        {
            LayerDiscovery auto = LayerRoleDiscovery.Discover(Stock(), "Eve", CloudRules.Default, 0);
            CollectionAssert.AreEqual(new[] { "Eve-clouds2" }, Names(auto), "its Stratus type is a role: " + auto.Summary());
            Assert.AreEqual(SkipReason.NoRole, Skip(auto, "Eve-clouds1"), "Floor and Pillar");
            CloudRules shipped = Shipped("Eve");
            Assert.AreEqual(CloudDiscovery.Off, shipped.Discover);
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Eve", shipped, 0);
            Assert.IsTrue(d.None, d.Summary());
            Assert.IsTrue(d.Decisions.Count == 2 && d.Decisions.All(x => x.Skip == SkipReason.DiscoveryOff));
        }

        [TestMethod]
        public void LaytheTakesThePacksWeatherLayersAndOursButNotItsBaseLayer()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Laythe", Shipped("Laythe"), 0);
            CollectionAssert.AreEqual(new[] { "Laythe-Weather-1", "Laythe-weather-2", "ReDefinition-Atmospheres-Laythe" }, Names(d), d.Summary());
            Assert.AreEqual(SkipReason.BaseLayer, Skip(d, "Laythe-base-layer"));
            Assert.AreEqual(SkipReason.NoRole, Skip(d, "Laythe-pyrocumulus-layer"));
        }

        [TestMethod]
        public void JoolHoldsAStormTypeByNameAndWhetherTheBodyRunsIsTheStatusDecision()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Pack(), "Jool", CloudRules.Default, 0);
            CollectionAssert.AreEqual(new[] { "Jool-clouds2" }, Names(d), d.Summary());
            Assert.AreEqual(LayerRoles.Storm, d.Roles);
            Assert.AreEqual("Off(NoSurface)", E41Support.StockBodies().Single(s => s.Facts.Name == "Jool").Expected);
        }

        [TestMethod]
        public void ALiveLayerBecomesDiscoveryInputAndTheTakenOnesComeBack()
        {
            var ours = new EveLayer { Name = "ReDefinition-Atmospheres-Duna", BodyName = "Duna", InnerRadiusM = 322700f, TypeNames = new[] { "Dust" } };
            var cirrus = new EveLayer { Name = "Duna-rare-cirrus", BodyName = "Duna", InnerRadiusM = 332200f, TypeNames = new[] { "Cirrus", "Cirrus2" } };
            var live = new List<EveLayer> { cirrus, ours };
            List<PackLayerInfo> infos = LiveLayerInfo.From(live, 320000.0);
            Assert.AreEqual(2, infos.Count);
            Assert.AreEqual(2700.0, infos.Single(i => i.Name == ours.Name).AltitudeM, 1.0);
            LayerDiscovery d = LayerRoleDiscovery.Discover(infos, "Duna", Shipped("Duna"), 0);
            List<EveLayer> taken = LiveLayerInfo.Taken(d, live);
            Assert.AreEqual(1, taken.Count);
            Assert.AreSame(ours, taken[0]);
            Assert.AreEqual(0, LiveLayerInfo.From(null, 1.0).Count);
            Assert.AreEqual(0, LiveLayerInfo.Taken(null, live).Count);
        }

        [TestMethod]
        public void MissingLayersAreNoErrorAndTheCloudsFallBackToOurs()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(new List<PackLayerInfo>(), "Kerbin", CloudRules.Default, 0);
            Assert.IsTrue(d.None);
            Assert.AreEqual(CloudSourceKind.ReDefinition, d.EffectiveSource(CloudSourceKind.Eve));
            Assert.AreEqual(CloudSourceKind.ReDefinition, d.EffectiveSource(CloudSourceKind.Both));
            Assert.AreEqual(CloudSourceKind.ReDefinition, d.EffectiveSource(CloudSourceKind.ReDefinition));
            Assert.AreEqual(CloudSourceKind.None, d.EffectiveSource(CloudSourceKind.None));
            Assert.IsTrue(d.Summary().Contains("0 taken"));
            Assert.AreEqual(0.0, d.MapMegabytes(2048, 1024));
            LayerDiscovery nullInput = LayerRoleDiscovery.Discover(null, "Kerbin", null, 0);
            Assert.IsTrue(nullInput.None);
            Assert.AreEqual(CloudSourceKind.Eve, LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 0).EffectiveSource(CloudSourceKind.Eve),
                "a body with layers keeps the requested source");
        }

        [TestMethod]
        public void OffAndExplicitFollowTheNode()
        {
            var off = new CloudRules(discover: CloudDiscovery.Off);
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", off, 0);
            Assert.IsTrue(d.None);
            Assert.IsTrue(d.Decisions.Count > 0 && d.Decisions.All(x => x.Skip == SkipReason.DiscoveryOff));
            var named = new CloudRules(discover: CloudDiscovery.Explicit, weatherLayers: new[] { "Kerbin-cumulonimbus-layer" });
            LayerDiscovery n = LayerRoleDiscovery.Discover(Stock(), "Kerbin", named, 0);
            CollectionAssert.AreEqual(new[] { "Kerbin-cumulonimbus-layer" }, Names(n), "a named layer is taken even without a role");
            var wrongCase = new CloudRules(weatherLayers: new[] { "Kerbin-weather-1" });
            Assert.IsTrue(LayerRoleDiscovery.Discover(Stock(), "Kerbin", wrongCase, 0).None,
                "names are matched exactly, as the pack's own spellings differ in case (Kerbin-Weather-1, Kerbin-weather-2)");
        }

        [TestMethod]
        public void TheSummaryLineNamesTheLayersTheirRolesAndTheReasons()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", new CloudRules(baseLayer: "Kerbin-base-layer"), 0);
            string line = d.Summary();
            Assert.IsTrue(line.StartsWith("layers on Kerbin: 3 taken"), line);
            Assert.IsTrue(line.Contains("+ Kerbin-Weather-1 [Snow+Storm]"), line);
            Assert.IsTrue(line.Contains("- Kerbin-base-layer [Stratus] BaseLayer"), line);
            Assert.IsTrue(line.Contains("- Kerbin-Snow-Particles-1 [no role] FxOnly"), line);
            Assert.IsTrue(line.Contains("- Kerbin-cumulonimbus-layer [no role] NoRole"), line);
        }

        [TestMethod]
        public void TheMapMemoryOfTheLayersWeDriveIsFourByteGridsPerLayer()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 0);
            Assert.AreEqual(4, d.Taken.Count);
            Assert.AreEqual(32.0, d.MapMegabytes(MapPainter.DefaultWidth, MapPainter.DefaultHeight), 1e-9,
                "per layer two render targets and two readable copies of 2048 x 1024 bytes: 8 MB");
        }
    }
}
