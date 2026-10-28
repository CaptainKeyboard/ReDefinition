using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Core;
using ReDefinition.Modules;
using ReDefinition.Raytracing;

namespace ReDefinition.Tests
{
    // Which cloud layers are weather and which shine by themselves (CloudLayerKinds), and the Night clouds row that
    // depends on the clouds' moonlight being hooked.
    [TestClass]
    public class CloudLayerKindsTests
    {
        // The shapes of EVE's cloud object and Scatterer's layer that the names are read from.
        private sealed class FakeCloudsObject
        {
            public string Name { get; set; }
        }

        private sealed class FakeLayer
        {
            public object CloudObject;
        }

        // "aurora" in a name, in any case, says the layer shines by itself; no name and no material say nothing.
        [TestMethod]
        public void ANameWithAuroraInItShinesByItself()
        {
            Assert.IsTrue(CloudLayerKinds.ShinesByName("kerbin-aurora"));
            Assert.IsTrue(CloudLayerKinds.ShinesByName("AuroraBorealis"));
            Assert.IsFalse(CloudLayerKinds.ShinesByName("cumulonimbus-layer"));
            Assert.IsFalse(CloudLayerKinds.ShinesByName(null));
            Assert.IsFalse(CloudLayerKinds.ShinesByName(""));
        }

        // The name is read from EVE's cloud object, and from Scatterer's layer through the object it holds; where there is no
        // object, there is no name.
        [TestMethod]
        public void TheNameIsReadFromTheCloudObjectAndFromTheLayerHoldingIt()
        {
            var cloudsObject = new FakeCloudsObject { Name = "Weather-1" };
            Assert.AreEqual("Weather-1", CloudLayerKinds.NameOfObject(cloudsObject));
            Assert.AreEqual("Weather-1", CloudLayerKinds.NameOfLayer(new FakeLayer { CloudObject = cloudsObject }));
            Assert.IsNull(CloudLayerKinds.NameOfLayer(new FakeLayer()));
            Assert.IsNull(CloudLayerKinds.NameOfLayer(null));
            Assert.IsNull(CloudLayerKinds.NameOfObject(null));
        }

        // Without Scatterer's cloud light hooked the moon's light reaches no cloud: the Night clouds row is greyed, and the
        // catalogue names what tells the layers apart.
        [TestMethod]
        public void TheNightCloudsRowIsGreyedWhereTheCloudLightIsNotHooked()
        {
            Assert.IsFalse(NightClouds.Hooked);
            ModuleSetting row = OurModules.Find("raytracing").Setting("nightClouds");
            Assert.IsFalse(row.Interactable(new OwnSettings()));
            Assert.IsTrue(row.Tooltip.Contains("Needs Scatterer and EVE"));
            CollectionAssert.Contains(ForeignAssumptions.All, ForeignAssumptions.EveCloudKinds);
        }
    }
}
