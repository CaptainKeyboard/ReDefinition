using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests
{
    // Which of TUFX's post-processing effects are drawn after the upscaler (TufxPostProcessing).
    [TestClass]
    public class TufxPostProcessingTests
    {
        // TUFX's tonemapping and colour grading, bloom and the lens and camera effects are drawn after the
        // upscaler, at display resolution.
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

        // Ambient occlusion, screen-space reflections and auto exposure work on the scene at render resolution
        // and stay before the upscaler.
        [TestMethod]
        public void OcclusionReflectionsAndExposureStayBeforeFsr()
        {
            foreach (string effect in new[] { "AmbientOcclusion", "ScreenSpaceReflections", "AutoExposure" })
                Assert.IsFalse(TufxPostProcessing.AfterUpscaling(effect, true, "BeforeTransparent"), effect);
        }

        // An effect from another mod is drawn after the upscaler only where it injects itself after the stack;
        // a built-in name counts only for PostProcessing's own effects.
        [TestMethod]
        public void AnotherModsEffectGoesWhereItInjectsItself()
        {
            Assert.IsTrue(TufxPostProcessing.AfterUpscaling("TUBISEffect", false, "AfterStack"));
            Assert.IsFalse(TufxPostProcessing.AfterUpscaling("TUBISEffect", false, "BeforeStack"));
            Assert.IsFalse(TufxPostProcessing.AfterUpscaling("Bloom", false, "BeforeTransparent"));
        }
    }
}
