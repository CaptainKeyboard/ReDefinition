using FidelityFX.FSR3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Raytracing;
using ReDefinition.Upscaler;

namespace ReDefinition.Tests
{
    // What Auto resolves to (AutoSettings): the technique by the card, the mode by the
    // screen and the height the profile aims for, Ray Reconstruction by the card.
    [TestClass]
    public class AutoSettingsTests
    {
        [TestMethod]
        public void TheTechniqueFollowsTheCard()
        {
            Assert.AreEqual(UpscalerBackend.Dlss, AutoSettings.Technique(GpuClass.Rtx20, false, false));
            Assert.AreEqual(UpscalerBackend.Dlss, AutoSettings.Technique(GpuClass.Rtx50, false, false));
            Assert.AreEqual(UpscalerBackend.Amd, AutoSettings.Technique(GpuClass.Radeon9000, true, false));
            Assert.AreEqual(UpscalerBackend.Amd, AutoSettings.Technique(GpuClass.Radeon7000, true, false));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.Radeon7000, false, false));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.RadeonOlder, true, true));
            Assert.AreEqual(UpscalerBackend.Xess, AutoSettings.Technique(GpuClass.Intel, false, true));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.Intel, true, false));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.NvidiaOlder, true, true));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.Unknown, true, true));
        }

        // The table's modes per profile: the target render heights 720, 960, 960, 1200 and 1440
        // at 1080p, 1440p and 4K; FSR and XeSS, and DLSS without 1.2x.
        [DataTestMethod]
        [DataRow(720, 1080, "Quality", "Quality")]
        [DataRow(720, 1440, "Performance", "Performance")]
        [DataRow(720, 2160, "UltraPerformance", "UltraPerformance")]
        [DataRow(960, 1080, "UltraQuality", "NativeAA")]
        [DataRow(960, 1440, "Quality", "Quality")]
        [DataRow(960, 2160, "Performance", "Performance")]
        [DataRow(1200, 1080, "NativeAA", "NativeAA")]
        [DataRow(1200, 1440, "UltraQuality", "NativeAA")]
        [DataRow(1200, 2160, "Balanced", "Balanced")]
        [DataRow(1440, 1080, "NativeAA", "NativeAA")]
        [DataRow(1440, 1440, "NativeAA", "NativeAA")]
        [DataRow(1440, 2160, "Quality", "Quality")]
        public void TheModeIsTheNearestToTheRenderHeightTheProfileAimsFor(int target, int screen, string others, string dlss)
        {
            Assert.AreEqual(others, AutoSettings.Mode(screen, target, false).ToString());
            Assert.AreEqual(dlss, AutoSettings.Mode(screen, target, true).ToString());
        }

        [TestMethod]
        public void RayReconstructionStartsAtFullOnAnRtx20Or30AndAtShadowsOnAnRtx40Or50()
        {
            RaytracingMode from;
            Assert.IsTrue(AutoSettings.RayReconstruction(GpuClass.Rtx30, UpscalerBackend.Dlss, out from));
            Assert.AreEqual(RaytracingMode.Full, from);
            Assert.IsTrue(AutoSettings.RayReconstruction(GpuClass.Rtx20, UpscalerBackend.Dlss, out from));
            Assert.AreEqual(RaytracingMode.Full, from);
            Assert.IsTrue(AutoSettings.RayReconstruction(GpuClass.Rtx40, UpscalerBackend.Dlss, out from));
            Assert.AreEqual(RaytracingMode.Shadows, from);
            Assert.IsTrue(AutoSettings.RayReconstruction(GpuClass.Rtx50, UpscalerBackend.Dlss, out from));
            Assert.AreEqual(RaytracingMode.Shadows, from);
        }

        [TestMethod]
        public void RayReconstructionNeedsAnRtxWithDlss()
        {
            RaytracingMode from;
            Assert.IsFalse(AutoSettings.RayReconstruction(GpuClass.Rtx40, UpscalerBackend.Fsr3, out from));
            Assert.IsFalse(AutoSettings.RayReconstruction(GpuClass.Radeon9000, UpscalerBackend.Amd, out from));
            Assert.IsFalse(AutoSettings.RayReconstruction(GpuClass.Intel, UpscalerBackend.Xess, out from));
        }

        [TestMethod]
        public void ResolveFillsInOnlyWhatIsAuto()
        {
            OwnSettings settings = new OwnSettings
            {
                BackendAuto = false, Backend = UpscalerBackend.Fsr3,
                QualityAuto = true, RenderTargetHeight = 960,
                RayReconstructionAuto = true,
            };

            AutoSettings.Resolve(settings, GpuClass.Rtx40, 1440, false, false);

            Assert.AreEqual(UpscalerBackend.Fsr3, settings.Backend, "a technique the player chose stays");
            Assert.AreEqual(Fsr3Upscaler.QualityMode.Quality, settings.Quality);
            Assert.IsFalse(settings.RayReconstruction, "Ray Reconstruction needs DLSS, and FSR 3 was chosen");
        }

        [TestMethod]
        public void TheModeFollowsTheScreenAndTheTechnique()
        {
            OwnSettings settings = new OwnSettings { RenderTargetHeight = 960 };

            AutoSettings.Resolve(settings, GpuClass.Rtx30, 1080, false, false);
            Assert.AreEqual(UpscalerBackend.Dlss, settings.Backend);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.NativeAA, settings.Quality, "DLSS has no 1.2x");
            Assert.IsTrue(settings.RayReconstruction);

            AutoSettings.Resolve(settings, GpuClass.Radeon9000, 1080, true, false);
            Assert.AreEqual(UpscalerBackend.Amd, settings.Backend);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.UltraQuality, settings.Quality);
        }

        [TestMethod]
        public void TheRowsReadAndWriteAutoAsAChoice()
        {
            OwnSettings settings = new OwnSettings();
            ModuleSetting technique = OurModules.Upscaler.Setting("technique");
            ModuleSetting mode = OurModules.Upscaler.Setting("quality");
            ModuleSetting reconstruction = OurModules.Raytracing.Setting("rayReconstruction");

            Assert.AreEqual("Auto", technique.Read(settings));
            Assert.AreEqual("Auto", mode.Read(settings));
            Assert.AreEqual("Auto", reconstruction.Read(settings));

            technique.Write(settings, "Xess");
            mode.Write(settings, "Balanced");
            reconstruction.Write(settings, "Off");
            Assert.AreEqual("Xess", technique.Read(settings));
            Assert.AreEqual("Balanced", mode.Read(settings));
            Assert.AreEqual("Off", reconstruction.Read(settings));
            Assert.IsFalse(settings.BackendAuto);
            Assert.IsFalse(settings.QualityAuto);
            Assert.IsFalse(settings.RayReconstructionAuto);

            technique.Write(settings, "Auto");
            Assert.IsTrue(settings.BackendAuto);
            Assert.AreEqual("Auto", technique.Read(settings));
        }

        [TestMethod]
        public void ThePlayersChoicesSurviveTheFile()
        {
            OwnSettings settings = new OwnSettings { LowLatency = "Off", RenderTargetHeight = 720, QualityAuto = false };

            Assert.IsTrue(settings.SetValue("lowLatency", "Auto"));
            Assert.AreEqual("Auto", settings.LowLatency);
            Assert.IsFalse(settings.SetValue("lowLatency", "Sometimes"));
            Assert.AreEqual("720", settings.ValueOf("renderTargetHeight"));
            Assert.AreEqual("False", settings.ValueOf("qualityAuto"));
        }
    }
}
