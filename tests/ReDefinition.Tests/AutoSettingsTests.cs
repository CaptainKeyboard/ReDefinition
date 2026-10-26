using FidelityFX.FSR3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ReDefinition.Bridges;
using ReDefinition.Modules;
using ReDefinition.Raytracing;
using ReDefinition.Upscaler;
using ReDefinition.Vendor;

namespace ReDefinition.Tests
{
    // What Auto resolves to (AutoSettings): the technique by the card, the mode by the
    // screen and the height the profile aims for, Ray Reconstruction by the card.
    [TestClass]
    public class AutoSettingsTests
    {
        // DLSS on an RTX where it is usable, FSR 4 on a Radeon RX 7000 or 9000 with AMD's DLL, XeSS on Intel
        // with its DLL, FSR 3 everywhere else, an RTX without DLSS's file or the proxy included.
        [TestMethod]
        public void TheTechniqueFollowsTheCard()
        {
            Assert.AreEqual(UpscalerBackend.Dlss, AutoSettings.Technique(GpuClass.Rtx20, true, false, false));
            Assert.AreEqual(UpscalerBackend.Dlss, AutoSettings.Technique(GpuClass.Rtx50, true, false, false));
            Assert.AreEqual(UpscalerBackend.Amd, AutoSettings.Technique(GpuClass.Radeon9000, true, true, false));
            Assert.AreEqual(UpscalerBackend.Amd, AutoSettings.Technique(GpuClass.Radeon7000, true, true, false));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.Radeon7000, true, false, false));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.RadeonOlder, true, true, true));
            Assert.AreEqual(UpscalerBackend.Xess, AutoSettings.Technique(GpuClass.Intel, true, false, true));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.Intel, true, true, false));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.NvidiaOlder, true, true, true));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.Unknown, true, true, true));
            Assert.AreEqual(UpscalerBackend.Fsr3, AutoSettings.Technique(GpuClass.Rtx40, false, false, false),
                "DLSS without its file or the proxy is not Auto's choice");
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

        // On an RTX 20 or 30 Ray Reconstruction runs from the Full raytracing mode up, on an RTX 40 or 50 from
        // Shadows up.
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

        // Ray Reconstruction is Auto's only on an RTX with DLSS chosen; FSR, FSR 4 or XeSS rule it out.
        [TestMethod]
        public void RayReconstructionNeedsAnRtxWithDlss()
        {
            RaytracingMode from;
            Assert.IsFalse(AutoSettings.RayReconstruction(GpuClass.Rtx40, UpscalerBackend.Fsr3, out from));
            Assert.IsFalse(AutoSettings.RayReconstruction(GpuClass.Radeon9000, UpscalerBackend.Amd, out from));
            Assert.IsFalse(AutoSettings.RayReconstruction(GpuClass.Intel, UpscalerBackend.Xess, out from));
        }

        // Resolve fills in only the settings left to Auto: a technique the player chose stays, and Ray
        // Reconstruction follows that technique, not the card's.
        [TestMethod]
        public void ResolveFillsInOnlyWhatIsAuto()
        {
            OwnSettings settings = new OwnSettings
            {
                BackendAuto = false, Backend = UpscalerBackend.Fsr3,
                QualityAuto = true, RenderTargetHeight = 960,
                RayReconstructionAuto = true,
            };

            AutoSettings.Resolve(settings, GpuClass.Rtx40, 1440, true, false, false);

            Assert.AreEqual(UpscalerBackend.Fsr3, settings.Backend, "a technique the player chose stays");
            Assert.AreEqual(Fsr3Upscaler.QualityMode.Quality, settings.Quality);
            Assert.IsFalse(settings.RayReconstruction, "Ray Reconstruction needs DLSS, and FSR 3 was chosen");
        }

        // The same render height gives another mode by the card's technique: on a 1080p screen DLSS takes
        // NativeAA, having no 1.2x, where FSR 4 takes UltraQuality.
        [TestMethod]
        public void TheModeFollowsTheScreenAndTheTechnique()
        {
            OwnSettings settings = new OwnSettings { RenderTargetHeight = 960 };

            AutoSettings.Resolve(settings, GpuClass.Rtx30, 1080, true, false, false);
            Assert.AreEqual(UpscalerBackend.Dlss, settings.Backend);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.NativeAA, settings.Quality, "DLSS has no 1.2x");
            Assert.IsTrue(settings.RayReconstruction);

            AutoSettings.Resolve(settings, GpuClass.Radeon9000, 1080, true, true, false);
            Assert.AreEqual(UpscalerBackend.Amd, settings.Backend);
            Assert.AreEqual(Fsr3Upscaler.QualityMode.UltraQuality, settings.Quality);
        }

        // The rows read Auto while the flag is set; writing a value clears the flag and writing Auto sets it
        // again, and switching the upscaler off keeps Auto for when it is on again.
        [TestMethod]
        public void TheRowsReadAndWriteAutoAsAChoice()
        {
            OwnSettings settings = new OwnSettings { Enabled = true };
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

            technique.Write(settings, "Off");
            Assert.AreEqual("Off", technique.Read(settings));
            Assert.IsTrue(settings.BackendAuto, "off keeps Auto for when it is on again");
        }

        // A technique, mode or Ray Reconstruction set by name, as the probe's ini shots set them, is no longer
        // Auto's.
        [TestMethod]
        public void AValueSetByNameIsNoLongerAutos()
        {
            OwnSettings settings = new OwnSettings();

            Assert.IsTrue(settings.SetValue("technique", "Dlss"));
            Assert.IsTrue(settings.SetValue("quality", "Performance"));
            Assert.IsTrue(settings.SetValue("rayReconstruction", "false"));

            Assert.IsFalse(settings.BackendAuto);
            Assert.IsFalse(settings.QualityAuto);
            Assert.IsFalse(settings.RayReconstructionAuto);
        }

        // Ray Reconstruction set on by name runs from the Shadows mode up, every raytracing mode, as the row's On
        // does.
        [TestMethod]
        public void RayReconstructionSetOnByNameRunsInEveryMode()
        {
            OwnSettings settings = new OwnSettings { RayReconstructionFrom = RaytracingMode.Full };

            Assert.IsTrue(settings.SetValue("rayReconstruction", "True"));

            Assert.AreEqual(RaytracingMode.Shadows, settings.RayReconstructionFrom);
        }

        // Resolve keeps what Auto would choose in ResolvedBackend even where the player chose another technique,
        // so the row can name it.
        [TestMethod]
        public void TheRowsNameWhatAutoIsWhetherOrNotItIsChosen()
        {
            OwnSettings settings = new OwnSettings { BackendAuto = false, Backend = UpscalerBackend.Fsr3, RenderTargetHeight = 960 };

            AutoSettings.Resolve(settings, GpuClass.Rtx40, 1440, true, false, false);

            Assert.AreEqual(UpscalerBackend.Fsr3, settings.Backend);
            Assert.AreEqual(UpscalerBackend.Dlss, AutoSettings.ResolvedBackend);
        }

        // A profile sets the raytracing's mode and the DLSS model, but not single raytracing settings such as
        // the moonlight, nor taste such as the sharpness.
        [TestMethod]
        public void AProfileCannotSetTheSingleRaytracingSettings()
        {
            Assert.IsFalse(ModuleProfiles.CanSet(OurModules.Raytracing.Setting("moonlight")));
            Assert.IsFalse(ModuleProfiles.CanSet(OurModules.Upscaler.Setting("sharpness")));
            Assert.IsTrue(ModuleProfiles.CanSet(OurModules.Raytracing.Setting("mode")));
            Assert.IsTrue(ModuleProfiles.CanSet(OurModules.Upscaler.Setting("dlssModel")));
        }

        // What the player chose is kept as set by name: Auto for low latency is taken and an unknown value
        // refused, and the render height and an Auto flag read back as written.
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
