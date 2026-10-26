using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Proxy;

namespace ReDefinition.Tests
{
    // The frame generation row's text for what the proxy's swapchain presents with
    // (FrameGenerationBridge.TechniqueLabel, from KspFgTechnique): 1 FSR, 2 DLSS,
    // 3 XeSS, with the frames shown per rendered frame while DLSS or XeSS generates.
    [TestClass]
    public class FrameGenerationTechniqueTests
    {
        // FSR's frame generation is named FSR 3, with no multiplier whatever the proxy reports.
        [TestMethod]
        public void FsrIsNamedWithoutAMultiplier()
        {
            Assert.AreEqual("FSR 3", FrameGenerationBridge.TechniqueLabel(1, 0));
            Assert.AreEqual("FSR 3", FrameGenerationBridge.TechniqueLabel(1, 2));
        }

        // DLSS is named with its multiplier while it generates, and alone while it does not.
        [TestMethod]
        public void DlssShowsItsMultiplierWhileItGenerates()
        {
            Assert.AreEqual("DLSS", FrameGenerationBridge.TechniqueLabel(2, 0));
            Assert.AreEqual("DLSS 2x", FrameGenerationBridge.TechniqueLabel(2, 2));
            Assert.AreEqual("DLSS 4x", FrameGenerationBridge.TechniqueLabel(2, 4));
        }

        // XeSS is named with its multiplier up to 4x while it generates; above, the row names it alone.
        [TestMethod]
        public void XessShowsItsMultiplierWhileItGenerates()
        {
            Assert.AreEqual("XeSS", FrameGenerationBridge.TechniqueLabel(3, 0));
            Assert.AreEqual("XeSS 2x", FrameGenerationBridge.TechniqueLabel(3, 2));
            // More than one generated frame per rendered frame on an Intel GPU
            // (xessFgFrames); beyond what the row names, read as not generating.
            Assert.AreEqual("XeSS 3x", FrameGenerationBridge.TechniqueLabel(3, 3));
            Assert.AreEqual("XeSS 4x", FrameGenerationBridge.TechniqueLabel(3, 4));
            Assert.AreEqual("XeSS", FrameGenerationBridge.TechniqueLabel(3, 5));
        }

        // No technique (0) and one the mod does not know have no text.
        [TestMethod]
        public void NoneOrAnUnknownTechniqueHasNoName()
        {
            Assert.IsNull(FrameGenerationBridge.TechniqueLabel(0, 0));
            Assert.IsNull(FrameGenerationBridge.TechniqueLabel(4, 2));
        }
    }
}
