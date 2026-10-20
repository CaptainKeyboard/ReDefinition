using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;

namespace ReDefinition.Tests
{
    // The frame generation row's text for what the proxy's swapchain presents with
    // (FrameGenerationBridge.TechniqueLabel, from KspFgTechnique): 1 FSR, 2 DLSS,
    // 3 XeSS, with the frames shown per rendered frame while DLSS or XeSS generates.
    [TestClass]
    public class FrameGenerationTechniqueTests
    {
        [TestMethod]
        public void FsrIsNamedWithoutAMultiplier()
        {
            Assert.AreEqual("FSR 3", FrameGenerationBridge.TechniqueLabel(1, 0));
            Assert.AreEqual("FSR 3", FrameGenerationBridge.TechniqueLabel(1, 2));
        }

        [TestMethod]
        public void DlssShowsItsMultiplierWhileItGenerates()
        {
            Assert.AreEqual("DLSS", FrameGenerationBridge.TechniqueLabel(2, 0));
            Assert.AreEqual("DLSS 2x", FrameGenerationBridge.TechniqueLabel(2, 2));
            Assert.AreEqual("DLSS 4x", FrameGenerationBridge.TechniqueLabel(2, 4));
        }

        [TestMethod]
        public void XessShowsTwoTimesWhileItGenerates()
        {
            Assert.AreEqual("XeSS", FrameGenerationBridge.TechniqueLabel(3, 0));
            Assert.AreEqual("XeSS 2x", FrameGenerationBridge.TechniqueLabel(3, 2));
            // XeSS-FG generates one frame per rendered frame here; anything else is
            // read as not generating.
            Assert.AreEqual("XeSS", FrameGenerationBridge.TechniqueLabel(3, 3));
        }

        [TestMethod]
        public void NoneOrAnUnknownTechniqueHasNoName()
        {
            Assert.IsNull(FrameGenerationBridge.TechniqueLabel(0, 0));
            Assert.IsNull(FrameGenerationBridge.TechniqueLabel(4, 2));
        }
    }
}
