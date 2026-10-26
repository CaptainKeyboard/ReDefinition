using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Modules;
using ReDefinition.Upscaler;
using ReDefinition.Vendor;

namespace ReDefinition.Tests
{
    // Which buttons of the upscaler and frame generation rows are greyed out (the card or the proxy rules the
    // technique out) and which wait for files (lit; choosing them opens the download) -- TechniqueAvailability.
    [TestClass]
    public class TechniqueAvailabilityTests
    {
        // Facts for the card given with the proxy present and every technique offered, nothing missing.
        private static TechniqueAvailability.Facts Gpu(GpuClass gpu)
        {
            return new TechniqueAvailability.Facts
            {
                Gpu = gpu, ProxyPresent = true, DlssOffered = true, CanGenerate = true, AmdOffered = true, XessOffered = true,
            };
        }

        // DLSS is offered on an RTX where the proxy offers it; on a Radeon it is not this card's, and without
        // the proxy the reason names it.
        [TestMethod]
        public void DlssNeedsAnRtxAndTheProxy()
        {
            Assert.IsNull(TechniqueAvailability.Upscaler("Dlss", Gpu(GpuClass.Rtx30)));
            Assert.AreEqual(TechniqueAvailability.NotThisCard, TechniqueAvailability.Upscaler("Dlss", Gpu(GpuClass.Radeon9000)));
            TechniqueAvailability.Facts noProxy = Gpu(GpuClass.Rtx40);
            noProxy.DlssOffered = false;
            StringAssert.Contains(TechniqueAvailability.Upscaler("Dlss", noProxy), "proxy");
        }

        // A missing DLL does not grey a technique out: it stays lit and waits for its download, while FSR 3 and
        // Auto need nothing.
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

        // FSR 4 runs only on a Radeon RX 7000 or 9000; without AMD's DLL it stays lit and waits for it.
        [TestMethod]
        public void Fsr4RunsOnARadeon7000Or9000Only()
        {
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", Gpu(GpuClass.Radeon9000)));
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", Gpu(GpuClass.Radeon7000)));
            Assert.AreEqual(TechniqueAvailability.NotThisCard, TechniqueAvailability.Upscaler("Amd", Gpu(GpuClass.Rtx30)));
            Assert.IsNotNull(TechniqueAvailability.Upscaler("Amd", Gpu(GpuClass.RadeonOlder)));
            TechniqueAvailability.Facts missing = Gpu(GpuClass.Radeon9000);
            missing.AmdDllMissing = true;
            Assert.IsNull(TechniqueAvailability.Upscaler("Amd", missing), "lit; choosing it says where to get the DLL");
            Assert.AreEqual(TechniqueAvailability.Needs.AmdDll, TechniqueAvailability.UpscalerNeeds("Amd", missing));
        }

        // DLSS frame generation on an RTX 30 is lit but waits for the package and NVIDIA's files, then for
        // Streamline's files alone; an RTX 40 needs no package.
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

        // Without the proxy only Off can be chosen, and DLSS frame generation is greyed out on a Radeon.
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

        // Without its runtime FSR frame generation is greyed out naming the DLL, which comes with ReDefinition;
        // DLSS and XeSS need neither it nor a swapchain that generates now, and with none of the three Auto says why.
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

        // Auto takes the card's own frame generation where it runs with nothing to fetch, else the next that
        // does: on an RTX DLSS, then FSR, then XeSS; on an Intel card XeSS, then FSR; on a Radeon FSR.
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

        // XeSS frame generation stays lit without Intel's files and waits for their download.
        [TestMethod]
        public void XessFrameGenerationWaitsForIntelsFiles()
        {
            TechniqueAvailability.Facts facts = Gpu(GpuClass.Intel);
            Assert.AreEqual(TechniqueAvailability.Needs.None, TechniqueAvailability.FrameGenerationNeeds("Xess", facts));
            facts.XessFgMissing = true;
            Assert.IsNull(TechniqueAvailability.FrameGeneration("Xess", facts));
            Assert.AreEqual(TechniqueAvailability.Needs.IntelFiles, TechniqueAvailability.FrameGenerationNeeds("Xess", facts));
        }

        // The limits the proxy reports for the technique that runs: its kind, its most frames, Dynamic's support.
        private static FrameGenerationChoice.Limits Running(int technique, int most, int dynamic)
        {
            return new FrameGenerationChoice.Limits { Technique = technique, Most = most, Dynamic = dynamic };
        }

        // The steps follow the limits the running technique reports: DLSS up to its most and Dynamic only with
        // the driver that supports it, FSR one generated frame between two and no Dynamic.
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

        // A technique chosen but not running yet is judged by what it allows everywhere: DLSS every step and
        // Dynamic, FSR one frame between two, XeSS up to 4x.
        [TestMethod]
        public void AChosenTechniqueThatIsNotRunningYetIsJudgedByWhatItDoesEverywhere()
        {
            FrameGenerationChoice.Limits fsrRuns = Running(1, 2, -1);
            Assert.IsNull(TechniqueAvailability.Step("6x", "Dlss", fsrRuns), "DLSS runs after a restart: all steps");
            Assert.IsNull(TechniqueAvailability.Step("Dynamic", "Dlss", fsrRuns));
            StringAssert.Contains(TechniqueAvailability.Step("3x", "Fsr", Running(2, 6, 1)), "one frame between two");
            StringAssert.Contains(TechniqueAvailability.Step("5x", "Xess", Running(1, 2, -1)), "up to 4x");
        }

        // No step is greyed out while no technique has reported what it allows.
        [TestMethod]
        public void NothingIsGreyedBeforeTheTechniqueHasSaidWhatItAllows()
        {
            Assert.IsNull(TechniqueAvailability.Step("6x", "Auto", new FrameGenerationChoice.Limits()));
            Assert.IsNull(TechniqueAvailability.Step("Dynamic", "Auto", new FrameGenerationChoice.Limits()));
        }
    }
}
