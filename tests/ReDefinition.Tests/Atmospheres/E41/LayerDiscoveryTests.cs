using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Atmospheres;
using ReDefinition.Core;

namespace ReDefinition.Tests.Atmospheres.E41
{
    // The discovery of layers by role on stored pack files: three fixtures (the stock pack's layer table, a second pack that doubles
    // Kerbin, an outer-planets pack), plus our own generated files.
    [TestClass]
    public class LayerDiscoveryTests
    {
        private static List<PackLayerInfo> Stock()
        {
            return E41Support.Layers(E41Support.PackFixture("stock-volumetric-clouds.cfg"), E41Support.CloudsFile("Kerbin"), E41Support.CloudsFile("Duna"),
                E41Support.CloudsFile("Laythe"));
        }

        private static string[] Names(LayerDiscovery d)
        {
            return d.Taken.Select(l => l.Name).ToArray();
        }

        [TestMethod]
        public void TheReaderGetsEveryLayerOfThePackWithItsMapsAndFields()
        {
            List<PackLayerInfo> layers = E41Support.Layers(E41Support.PackFixture("stock-volumetric-clouds.cfg"));
            Assert.AreEqual(17, layers.Count);
            PackLayerInfo weather = layers.Single(l => l.Name == "Kerbin-Weather-1");
            Assert.AreEqual("Kerbin", weather.Body);
            Assert.AreEqual(2200.0, weather.AltitudeM);
            Assert.IsTrue(weather.HasCoverageMap && weather.HasTypeMap && !weather.FxOnly);
            CollectionAssert.AreEqual(new[] { "Stratus", "Cumulus", "Polar-snow", "Snow" }, weather.TypeNames);
            Assert.AreEqual("rain-Kerbin", weather.ParticleField);
            PackLayerInfo particles = layers.Single(l => l.Name == "Kerbin-Snow-Particles-1");
            Assert.IsTrue(particles.FxOnly);
            Assert.IsFalse(particles.HasTypeMap);
        }

        [TestMethod]
        public void KerbinTakesEveryLayerWithARoleInTheOrderOfAltitude()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 0);
            CollectionAssert.AreEqual(new[] { "Kerbin-Weather-1", "Kerbin-weather-2", "ReDefinition-Atmospheres-Kerbin", "Kerbin-cumulonimbus-layer" }.OrderBy(n => n).ToArray(),
                Names(d).OrderBy(n => n).ToArray(), d.Summary());
            Assert.AreEqual("Kerbin-cumulonimbus-layer", Names(d).Last(), "the highest layer last");
            Assert.AreEqual(LayerRoles.Snow | LayerRoles.Stratus | LayerRoles.Storm, d.Roles);
            Assert.AreEqual(SkipReason.NoRole, d.Decisions.Single(x => x.Layer.Name == "Kerbin-base-layer").Skip, "the base layer has no role of ours");
            Assert.AreEqual(SkipReason.FxOnly, d.Decisions.Single(x => x.Layer.Name == "Kerbin-Snow-Particles-1").Skip);
            Assert.IsFalse(d.None);
        }

        [TestMethod]
        public void TwoPacksOnOneBodyBothCountAndTheCapStopsTheCount()
        {
            List<PackLayerInfo> all = Stock();
            all.AddRange(E41Support.Layers(E41Support.PackFixture("second-pack-on-kerbin.cfg")));
            LayerDiscovery open = LayerRoleDiscovery.Discover(all, "Kerbin", CloudRules.Default, 0);
            Assert.IsTrue(Names(open).Contains("SecondPack-Kerbin-low") && Names(open).Contains("SecondPack-Kerbin-snow") && Names(open).Contains("SecondPack-Kerbin-storm"),
                open.Summary());
            Assert.IsTrue(Names(open).Contains("Kerbin-Weather-1"));
            Assert.AreEqual(SkipReason.NoMaps, open.Decisions.Single(x => x.Layer.Name == "SecondPack-Kerbin-nomaps").Skip);
            Assert.AreEqual(SkipReason.NoRole, open.Decisions.Single(x => x.Layer.Name == "SecondPack-Kerbin-decor").Skip);
            LayerDiscovery capped = LayerRoleDiscovery.Discover(all, "Kerbin", CloudRules.Default, 3);
            Assert.AreEqual(3, capped.Taken.Count);
            Assert.AreEqual(open.Taken.Count - 3, capped.Decisions.Count(x => x.Skip == SkipReason.OverCap));
            Assert.IsTrue(capped.Summary().Contains("cap 3"));
        }

        [TestMethod]
        public void TheCapKeepsTheLowestLayers()
        {
            LayerDiscovery capped = LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 2);
            Assert.AreEqual(2, capped.Taken.Count);
            Assert.IsTrue(capped.Taken.All(l => l.AltitudeM <= 2700.0));
        }

        [TestMethod]
        public void DunaFindsTheDustLayersWhenTheNodeNamesTheDustTypeAndNothingWhenItDoesNot()
        {
            var withDust = new CloudRules(dustTypeNames: new[] { "dust", "Dust" });
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Duna", withDust, 0);
            CollectionAssert.AreEquivalent(new[] { "Duna-duststorm-big", "ReDefinition-Atmospheres-Duna" }, Names(d), d.Summary());
            Assert.AreEqual(LayerRoles.Dust, d.Roles);
            LayerDiscovery without = LayerRoleDiscovery.Discover(Stock(), "Duna", CloudRules.Default, 0);
            Assert.IsTrue(without.None, "no dust list: no layer has a role, the clouds are ours");
        }

        [TestMethod]
        public void TheShippedDunaNodeNamesTheDustTypeAndTheLayersAreFoundByRole()
        {
            ConfigNode node = E41Support.BodyNode("Duna");
            CloudRules rules = BodyRuleReaders.Clouds(new BodyNodeReader(node.GetNode("CLOUDS"), "CLOUDS", new List<string>()));
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Duna", rules, 0);
            CollectionAssert.AreEqual(new[] { "ReDefinition-Atmospheres-Duna", "Duna-duststorm-big" }, Names(d), d.Summary());
            Assert.AreEqual(LayerRoles.Dust, d.Roles);
            Assert.AreEqual(SkipReason.NoRole, d.Decisions.Single(x => x.Layer.Name == "Duna-rare-cirrus").Skip);
        }

        [TestMethod]
        public void ALiveLayerBecomesDiscoveryInputAndTheTakenOnesComeBack()
        {
            var ours = new EveLayer { Name = "ReDefinition-Atmospheres-Duna", BodyName = "Duna", InnerRadiusM = 322700f, TypeNames = new[] { "Dust" } };
            var cirrus = new EveLayer { Name = "Duna-rare-cirrus", BodyName = "Duna", InnerRadiusM = 332200f, TypeNames = new[] { "Cirrus" } };
            var live = new List<EveLayer> { cirrus, ours };
            List<PackLayerInfo> infos = LiveLayerInfo.From(live, 320000.0);
            Assert.AreEqual(2, infos.Count);
            Assert.AreEqual(2700.0, infos.Single(i => i.Name == ours.Name).AltitudeM, 1.0);
            LayerDiscovery d = LayerRoleDiscovery.Discover(infos, "Duna", new CloudRules(dustTypeNames: new[] { "Dust" }), 0);
            List<EveLayer> taken = LiveLayerInfo.Taken(d, live);
            Assert.AreEqual(1, taken.Count);
            Assert.AreSame(ours, taken[0]);
            Assert.AreEqual(0, LiveLayerInfo.From(null, 1.0).Count);
            Assert.AreEqual(0, LiveLayerInfo.Taken(null, live).Count);
        }

        [TestMethod]
        public void LaytheAndEveAreFoundWhereTheyHaveRolesAndEveHasNone()
        {
            LayerDiscovery laythe = LayerRoleDiscovery.Discover(Stock(), "Laythe", CloudRules.Default, 0);
            CollectionAssert.AreEquivalent(new[] { "Laythe-Weather-1", "Laythe-Weather-2", "ReDefinition-Atmospheres-Laythe" }, Names(laythe), laythe.Summary());
            LayerDiscovery eve = LayerRoleDiscovery.Discover(Stock(), "Eve", CloudRules.Default, 0);
            Assert.IsTrue(eve.None, "Eve's two layers play no role of ours: " + eve.Summary());
        }

        [TestMethod]
        public void MissingLayersAreNoErrorAndTheCloudsFallBackToOurs()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(new List<PackLayerInfo>(), "Kerbin", CloudRules.Default, 0);
            Assert.IsTrue(d.None);
            Assert.AreEqual(CloudSourceKind.Ours, d.EffectiveSource(CloudSourceKind.Eve));
            Assert.AreEqual(CloudSourceKind.Ours, d.EffectiveSource(CloudSourceKind.Both));
            Assert.AreEqual(CloudSourceKind.Ours, d.EffectiveSource(CloudSourceKind.Ours));
            Assert.AreEqual(CloudSourceKind.None, d.EffectiveSource(CloudSourceKind.None));
            Assert.AreEqual(BodyStatusKind.CoverOnly, d.StatusFor(BodyStatus.Full, CloudSourceKind.Eve).Kind);
            Assert.AreEqual(BodyStatusKind.Full, d.StatusFor(BodyStatus.Full, CloudSourceKind.Ours).Kind);
            Assert.AreEqual(BodyStatusKind.Off, d.StatusFor(BodyStatus.Off("NoSurface"), CloudSourceKind.Eve).Kind);
            Assert.IsTrue(d.Summary().Contains("0 taken"));
            Assert.AreEqual(0.0, d.MapMegabytes(2048, 1024));
            LayerDiscovery nullInput = LayerRoleDiscovery.Discover(null, "Kerbin", null, 0);
            Assert.IsTrue(nullInput.None);
            Assert.AreEqual(CloudSourceKind.Eve, LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 0).EffectiveSource(CloudSourceKind.Eve));
        }

        [TestMethod]
        public void OffAndExplicitFollowTheNode()
        {
            var off = new CloudRules(discover: CloudDiscovery.Off);
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", off, 0);
            Assert.IsTrue(d.None);
            Assert.IsTrue(d.Decisions.Count > 0 && d.Decisions.All(x => x.Skip == SkipReason.DiscoveryOff));
            var named = new CloudRules(discover: CloudDiscovery.Explicit, weatherLayers: new[] { "Kerbin-base-layer" });
            LayerDiscovery n = LayerRoleDiscovery.Discover(Stock(), "Kerbin", named, 0);
            CollectionAssert.AreEqual(new[] { "Kerbin-base-layer" }, Names(n), "a named layer is taken even without a role");
            var names = new CloudRules(weatherLayers: new[] { "Kerbin-Weather-1", "Kerbin-weather-2" });
            CollectionAssert.AreEquivalent(new[] { "Kerbin-Weather-1", "Kerbin-weather-2" }, Names(LayerRoleDiscovery.Discover(Stock(), "Kerbin", names, 0)));
            var wrongCase = new CloudRules(weatherLayers: new[] { "kerbin-weather-1" });
            Assert.IsTrue(LayerRoleDiscovery.Discover(Stock(), "Kerbin", wrongCase, 0).None, "names are matched exactly, as the scan of the game does");
        }

        [TestMethod]
        public void TheOuterPlanetsPackGivesGasGiantsNoRoleAndASandBodyItsDustLayer()
        {
            List<PackLayerInfo> layers = E41Support.Layers(E41Support.PackFixture("outer-planets-pack.cfg"));
            Assert.AreEqual(4, layers.Count);
            Assert.IsTrue(LayerRoleDiscovery.Discover(layers, "Urlum", CloudRules.Default, 0).None);
            LayerDiscovery sarnus = LayerRoleDiscovery.Discover(layers, "Sarnus", CloudRules.Default, 0);
            Assert.AreEqual(LayerRoles.Storm, sarnus.Roles, "a storm-kind type is a role even on a gas giant; whether the body runs is the status's decision");
            var sandy = new CloudRules(dustTypeNames: new[] { "SandCloud" });
            LayerDiscovery hox = LayerRoleDiscovery.Discover(layers, "Hox", sandy, 0);
            CollectionAssert.AreEqual(new[] { "OuterPack-Hox-dust" }, Names(hox), hox.Summary());
            Assert.AreEqual(LayerRoles.Dust, hox.Roles);
        }

        [TestMethod]
        public void TheSummaryLineNamesTheLayersTheirRolesAndTheReasons()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 0);
            string line = d.Summary();
            Assert.IsTrue(line.StartsWith("layers on Kerbin: 4 taken"), line);
            Assert.IsTrue(line.Contains("+ Kerbin-Weather-1 [Snow+Stratus]"), line);
            Assert.IsTrue(line.Contains("- Kerbin-base-layer [no role] NoRole"), line);
            Assert.IsTrue(line.Contains("- Kerbin-Snow-Particles-1 [no role] FxOnly"), line);
        }

        [TestMethod]
        public void TheMapMemoryOfTheLayersWeDriveIsFourByteGridsPerLayer()
        {
            LayerDiscovery d = LayerRoleDiscovery.Discover(Stock(), "Kerbin", CloudRules.Default, 0);
            Assert.AreEqual(d.Taken.Count * 8.0, d.MapMegabytes(MapPainter.DefaultWidth, MapPainter.DefaultHeight), 1e-9,
                "two render targets and two readable copies of 2048 x 1024 bytes: 8 MB a layer");
        }
    }
}
