using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests
{
    // Which of TUFX's post-processing effects are drawn after the upscaler (TufxPostProcessing).
    [TestClass]
    public class TufxPostProcessingTests
    {
        [TestMethod]
        public void TonemappingBloomAndTheLensEffectsGoAfterFsr()
        {
            foreach (string effect in new[]
                     {
                         "ColorGrading", "Bloom", "Grain", "ChromaticAberration", "Vignette", "LensDistortion",
                         "DepthOfField", "MotionBlur",
                     })
                Assert.IsTrue(TufxPostProcessing.AfterUpscaling(effect, true, "BeforeTransparent"), effect);
        }

        [TestMethod]
        public void OcclusionReflectionsAndExposureStayBeforeFsr()
        {
            foreach (string effect in new[] { "AmbientOcclusion", "ScreenSpaceReflections", "AutoExposure" })
                Assert.IsFalse(TufxPostProcessing.AfterUpscaling(effect, true, "BeforeTransparent"), effect);
        }

        [TestMethod]
        public void AnotherModsEffectGoesWhereItInjectsItself()
        {
            Assert.IsTrue(TufxPostProcessing.AfterUpscaling("TUBISEffect", false, "AfterStack"));
            Assert.IsFalse(TufxPostProcessing.AfterUpscaling("TUBISEffect", false, "BeforeStack"));
            Assert.IsFalse(TufxPostProcessing.AfterUpscaling("Bloom", false, "BeforeTransparent"));
        }
    }
}
