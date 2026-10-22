using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests
{
    // Which buttons of the upscaler and frame generation rows are greyed out (the card or the proxy rules the
    // technique out) and which wait for files (lit; choosing them opens the download) -- TechniqueAvailability.
    [TestClass]
    public class TechniqueAvailabilityTests
    {
        private static TechniqueAvailability.Facts Gpu(GpuClass gpu)
        {
            return new TechniqueAvailability.Facts
            {
                Gpu = gpu, ProxyPresent = true, DlssOffered = true, CanGenerate = true, AmdOffered = true, XessOffered = true,
            };
        }

        [TestMethod]
        public void DlssNeedsAnRtxAndTheProxy()
        {
            Assert.IsNull(TechniqueAvailability.Upscaler("Dlss", Gpu(GpuClass.Rtx30)));
            StringAssert.Contains(TechniqueAvailability.Upscaler("Dlss", Gpu(GpuClass.Radeon9000)), "RTX");
            TechniqueAvailability.Facts noProxy = Gpu(GpuClass.Rtx40);
            noProxy.DlssOffered = false;
            StringAssert.Contains(TechniqueAvailability.Upscaler("Dlss", noProxy), "proxy");
        }

        [TestMethod]
        public void AMissingFileDoesNotGreyATechniqueOutItMakesItWaitForTheDownload()
        {
            TechniqueAvailability.Facts facts = Gpu(GpuClass.Rtx30);
            facts.DlssFileMissing = true;
            facts.XessDllMissing = true;
            Assert.IsNull(TechniqueAvailability.Upscaler("Dlss", facts), "lit");
            Assert.AreEqual(TechniqueAvailability.Needs.NvidiaFiles, TechniqueAvailability.UpscalerNeeds("Dlss", facts));
            Assert.IsNull(TechniqueAvailability.Upscaler("Xess", facts), "XeSS runs on any card with its DLL");
            Assert.AreEqual(TechniqueAvailability.Needs.IntelFiles, TechniqueAvailability.UpscalerNeeds("Xess", facts));
            Assert.AreEqual(TechniqueAvailability.Needs.None, TechniqueAvailability.UpscalerNeeds("Fsr3", facts));
            Assert.AreEqual(TechniqueAvailability.Needs.None, TechniqueAvailability.UpscalerNeeds("Auto", facts));
        }

        [TestMethod]
        public void Fsr4RunsOnARadeon7000Or9000Only()
        {
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", Gpu(GpuClass.Radeon9000)));
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", Gpu(GpuClass.Radeon7000)));
            StringAssert.Contains(TechniqueAvailability.Upscaler("Amd", Gpu(GpuClass.Rtx30)), "Radeon RX 7000 or RX 9000");
            Assert.IsNotNull(TechniqueAvailability.Upscaler("Amd", Gpu(GpuClass.RadeonOlder)));
            TechniqueAvailability.Facts missing = Gpu(GpuClass.Radeon9000);
            missing.AmdDllMissing = true;
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", missing), "lit; choosing it says where to get the DLL");
            Assert.AreEqual(TechniqueAvailability.Needs.AmdDll, TechniqueAvailability.UpscalerNeeds("Amd", missing));
        }

        [TestMethod]
        public void DlssFrameGenerationOnAnRtx30WaitsForThePackageAndStreamline()
        {
            TechniqueAvailability.Facts facts = Gpu(GpuClass.Rtx30);
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Dlss", facts));
            // NVIDIA's frame generation files are looked for again once the package is in place.
            Assert.AreEqual(TechniqueAvailability.Needs.AmperePackage | TechniqueAvailability.Needs.NvidiaFiles,
                TechniqueAvailability.FrameGenerationNeeds("Dlss", facts));
            facts.AmperePackage = true;
            facts.StreamlineMissing = true;
            Assert.AreEqual(TechniqueAvailability.Needs.NvidiaFiles, TechniqueAvailability.FrameGenerationNeeds("Dlss", facts));
            facts.StreamlineMissing = false;
            Assert.AreEqual(TechniqueAvailability.Needs.None, TechniqueAvailability.FrameGenerationNeeds("Dlss", facts));
            Assert.AreEqual(TechniqueAvailability.Needs.None, TechniqueAvailability.FrameGenerationNeeds("Dlss", Gpu(GpuClass.Rtx40)),
                "an RTX 40 needs no package");
        }

        [TestMethod]
        public void NoFrameGenerationButOffWithoutTheProxyAndNoDlssOffARadeon()
        {
            TechniqueAvailability.Facts facts = new TechniqueAvailability.Facts { Gpu = GpuClass.Rtx40 };
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Off", facts));
            Assert.IsNotNull(TechniqueAvailability.FrameGeneration("Auto", facts));
            Assert.IsNotNull(TechniqueAvailability.FrameGeneration("Fsr", facts));
            Assert.IsNotNull(TechniqueAvailability.FrameGeneration("Dlss", facts));
            Assert.IsNotNull(TechniqueAvailability.FrameGeneration("Dlss", Gpu(GpuClass.Radeon9000)));
        }

        [TestMethod]
        public void FsrFrameGenerationNamesTheMissingRuntimeThatComesWithReDefinition()
        {
            TechniqueAvailability.Facts facts = Gpu(GpuClass.Rtx40);
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Fsr", facts));
            facts.AmdFgDllMissing = true;
            StringAssert.Contains(TechniqueAvailability.FrameGeneration("Fsr", facts), "amd_fidelityfx_framegeneration_dx12.dll");
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Dlss", facts), "DLSS frame generation needs no AMD runtime");
            // Without the runtime FSR says why; DLSS and XeSS make their own swapchain from the next start on,
            // and Auto takes DLSS here.
            facts.CanGenerate = false;
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Dlss", facts));
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Xess", facts));
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Auto", facts));
            Assert.AreEqual("Dlss", AutoSettings.FrameGenerationTechnique("Auto", facts));

            // A Radeon without the runtime and without Intel's files: nothing runs, and Auto says why.
            TechniqueAvailability.Facts radeon = Gpu(GpuClass.Radeon9000);
            radeon.AmdFgDllMissing = true;
            radeon.XessFgMissing = true;
            StringAssert.Contains(TechniqueAvailability.FrameGeneration("Auto", radeon), "install ReDefinition again");
        }

        [TestMethod]
        public void AutoFrameGenerationIsTheCardsOwnWhereItRunsAndTheNextOneElse()
        {
            TechniqueAvailability.Facts rtx40 = Gpu(GpuClass.Rtx40);
            Assert.AreEqual("Dlss", AutoSettings.FrameGenerationTechnique("Auto", rtx40));
            rtx40.StreamlineMissing = true;
            Assert.AreEqual("Fsr", AutoSettings.FrameGenerationTechnique("Auto", rtx40), "DLSS-G waits for its files");
            rtx40.AmdFgDllMissing = true;
            Assert.AreEqual("Xess", AutoSettings.FrameGenerationTechnique("Auto", rtx40), "then XeSS, whose files are there");

            TechniqueAvailability.Facts rtx30 = Gpu(GpuClass.Rtx30);
            Assert.AreEqual("Fsr", AutoSettings.FrameGenerationTechnique("Auto", rtx30), "DLSS-G on an RTX 30 waits for the package");
            rtx30.AmperePackage = true;
            Assert.AreEqual("Dlss", AutoSettings.FrameGenerationTechnique("Auto", rtx30));

            TechniqueAvailability.Facts intel = Gpu(GpuClass.Intel);
            Assert.AreEqual("Xess", AutoSettings.FrameGenerationTechnique("Auto", intel));
            intel.XessFgMissing = true;
            Assert.AreEqual("Fsr", AutoSettings.FrameGenerationTechnique("Auto", intel));

            Assert.AreEqual("Fsr", AutoSettings.FrameGenerationTechnique("Auto", Gpu(GpuClass.Radeon9000)));
            Assert.AreEqual("Xess", AutoSettings.FrameGenerationTechnique("Xess", Gpu(GpuClass.Rtx40)), "a choice is itself");
        }

        [TestMethod]
        public void XessFrameGenerationWaitsForIntelsFiles()
        {
            TechniqueAvailability.Facts facts = Gpu(GpuClass.Intel);
            Assert.AreEqual(TechniqueAvailability.Needs.None, TechniqueAvailability.FrameGenerationNeeds("Xess", facts));
            facts.XessFgMissing = true;
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Xess", facts));
            Assert.AreEqual(TechniqueAvailability.Needs.IntelFiles, TechniqueAvailability.FrameGenerationNeeds("Xess", facts));
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
