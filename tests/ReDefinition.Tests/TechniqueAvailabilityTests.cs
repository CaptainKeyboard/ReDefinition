using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests
{
    // Which buttons of the upscaler and frame generation rows are greyed out, and the reason their
    // tooltip gives (TechniqueAvailability).
    [TestClass]
    public class TechniqueAvailabilityTests
    {
        private static TechniqueAvailability.Facts Rtx(GpuClass gpu)
        {
            return new TechniqueAvailability.Facts { Gpu = gpu, DlssOffered = true, CanGenerate = true };
        }

        [TestMethod]
        public void DlssNeedsAnRtxTheProxyAndNvidiasFile()
        {
            TechniqueAvailability.Facts facts = Rtx(GpuClass.Rtx30);
            Assert.IsNull(TechniqueAvailability.Upscaler("Dlss", facts));
            facts.DlssFileMissing = true;
            StringAssert.Contains(TechniqueAvailability.Upscaler("Dlss", facts), "nvngx_dlss.dll");
            Assert.IsNotNull(TechniqueAvailability.Upscaler("Dlss", Rtx(GpuClass.Radeon9000)));
            Assert.IsNull(TechniqueAvailability.Upscaler("Fsr3", Rtx(GpuClass.Intel)), "FSR 3 runs everywhere");
            Assert.IsNotNull(TechniqueAvailability.Upscaler("Xess", Rtx(GpuClass.Rtx40)));
            Assert.IsNull(TechniqueAvailability.Upscaler("Xess", new TechniqueAvailability.Facts { XessOffered = true }));
            StringAssert.Contains(TechniqueAvailability.Upscaler("Xess", new TechniqueAvailability.Facts
                { XessOffered = true, XessDllMissing = true }), "libxess.dll");
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", new TechniqueAvailability.Facts
                { Gpu = GpuClass.Radeon9000, AmdOffered = true }));
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", new TechniqueAvailability.Facts
                { Gpu = GpuClass.Radeon7000, AmdOffered = true }));
            StringAssert.Contains(TechniqueAvailability.Upscaler("Amd", new TechniqueAvailability.Facts
                { Gpu = GpuClass.Radeon9000, AmdOffered = true, AmdDllMissing = true }), "amd_fidelityfx_upscaler_dx12.dll");
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", new TechniqueAvailability.Facts
                { Gpu = GpuClass.Rtx30, AmdOffered = true }), "the DLL runs its FSR 3.1 on any GPU (measured on the A4500)");
        }

        [TestMethod]
        public void DlssFrameGenerationOnAnRtx30NeedsThePackage()
        {
            TechniqueAvailability.Facts facts = Rtx(GpuClass.Rtx30);
            StringAssert.Contains(TechniqueAvailability.FrameGeneration("Dlss", facts), "dlssg_for_sm86");
            facts.AmperePackage = true;
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Dlss", facts));
            facts.StreamlineMissing = true;
            StringAssert.Contains(TechniqueAvailability.FrameGeneration("Dlss", facts), "Streamline");
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Dlss", new TechniqueAvailability.Facts
                { Gpu = GpuClass.Rtx40, CanGenerate = true }), "an RTX 40 needs no package");
        }

        [TestMethod]
        public void NoFrameGenerationButOffWithoutTheProxy()
        {
            TechniqueAvailability.Facts facts = new TechniqueAvailability.Facts { Gpu = GpuClass.Rtx40 };
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Off", facts));
            Assert.IsNotNull(TechniqueAvailability.FrameGeneration("Auto", facts));
            Assert.IsNotNull(TechniqueAvailability.FrameGeneration("Fsr", facts));
        }

        [TestMethod]
        public void XessFrameGenerationNeedsIntelsFiles()
        {
            TechniqueAvailability.Facts facts = new TechniqueAvailability.Facts { Gpu = GpuClass.Intel, CanGenerate = true };
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Xess", facts));
            facts.XessFgMissing = true;
            StringAssert.Contains(TechniqueAvailability.FrameGeneration("Xess", facts), "libxess_fg.dll");
        }

        private static FrameGenerationChoice.Limits Running(int technique, int most, int dynamic)
        {
            return new FrameGenerationChoice.Limits { Technique = technique, Most = most, Dynamic = dynamic };
        }

        [TestMethod]
        public void TheStepsFollowTheTechniqueThatRuns()
        {
            FrameGenerationChoice.Limits dlss = Running(2, 6, 1);
            Assert.IsNull(TechniqueAvailability.Step("6x", "Auto", dlss));
            Assert.IsNull(TechniqueAvailability.Step("Dynamic", "Dlss", dlss));
            FrameGenerationChoice.Limits noDynamic = Running(2, 4, 0);
            Assert.IsNull(TechniqueAvailability.Step("4x", "Dlss", noDynamic));
            StringAssert.Contains(TechniqueAvailability.Step("5x", "Dlss", noDynamic), "up to 4x");
            StringAssert.Contains(TechniqueAvailability.Step("Dynamic", "Dlss", noDynamic), "595.41");
            FrameGenerationChoice.Limits fsr = Running(1, 2, -1);
            Assert.IsNull(TechniqueAvailability.Step("2x", "Auto", fsr));
            StringAssert.Contains(TechniqueAvailability.Step("3x", "Auto", fsr), "one frame between two");
            StringAssert.Contains(TechniqueAvailability.Step("Dynamic", "Auto", fsr), "DLSS frame generation only");
        }

        [TestMethod]
        public void AChosenTechniqueThatIsNotRunningYetIsJudgedByWhatItDoesEverywhere()
        {
            FrameGenerationChoice.Limits fsrRuns = Running(1, 2, -1);
            Assert.IsNull(TechniqueAvailability.Step("6x", "Dlss", fsrRuns), "DLSS runs after a restart: all steps");
            Assert.IsNull(TechniqueAvailability.Step("Dynamic", "Dlss", fsrRuns));
            StringAssert.Contains(TechniqueAvailability.Step("3x", "Fsr", Running(2, 6, 1)), "one frame between two");
            StringAssert.Contains(TechniqueAvailability.Step("5x", "Xess", Running(1, 2, -1)), "up to 4x");
        }

        [TestMethod]
        public void NothingIsGreyedBeforeTheTechniqueHasSaidWhatItAllows()
        {
            Assert.IsNull(TechniqueAvailability.Step("6x", "Auto", new FrameGenerationChoice.Limits()));
            Assert.IsNull(TechniqueAvailability.Step("Dynamic", "Auto", new FrameGenerationChoice.Limits()));
        }
    }
}
