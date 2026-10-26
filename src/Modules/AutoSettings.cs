using System;
using FidelityFX.FSR3;
using ReDefinition.Bridges;
using ReDefinition.Raytracing;
using ReDefinition.Settings;
using ReDefinition.Vendor;
using UnityEngine;

namespace ReDefinition.Modules
{
    // What "Auto" means for the settings that have it: the value for the player's graphics
    // card and screen, not a default of the profile. The profile says what it wants -- the
    // render size it aims for -- and Auto turns that into the technique, the mode and the
    // Ray Reconstruction that fit this card.
    //
    // The settings keep the value Auto resolved to beside the Auto flag (OwnSettings.BackendAuto
    // and the others), so everything that reads Backend, Quality or RayReconstruction reads the
    // value that runs. Resolve is called wherever settings are applied (ReDefinitionAddon.Apply)
    // and again when the screen changes.
    internal static class AutoSettings
    {
        // The value a setting's row stores and offers for Auto.
        public const string Auto = "Auto";

        // The values last resolved, for the rows to name what Auto is now.
        public static UpscalerBackend ResolvedBackend = UpscalerBackend.Fsr3;
        public static Fsr3Upscaler.QualityMode ResolvedQuality = Fsr3Upscaler.QualityMode.NativeAA;
        public static bool ResolvedRayReconstruction;
        public static RaytracingMode ResolvedRayReconstructionFrom = RaytracingMode.Shadows;

        // The modes by how far they render below the screen, AA only first.
        private static readonly Fsr3Upscaler.QualityMode[] Modes =
        {
            Fsr3Upscaler.QualityMode.NativeAA, Fsr3Upscaler.QualityMode.UltraQuality, Fsr3Upscaler.QualityMode.Quality,
            Fsr3Upscaler.QualityMode.Balanced, Fsr3Upscaler.QualityMode.Performance, Fsr3Upscaler.QualityMode.UltraPerformance,
        };

        // The technique for a card: DLSS on an RTX where the proxy offers it and NVIDIA's DLL is there,
        // FSR 4 through AMD's DLL on a Radeon RX 7000 or 9000 where the DLL is there, XeSS on an Intel
        // adapter where its DLL is, FSR 3 otherwise -- so that Auto never names a technique that would
        // fall back to FSR 3 with the mode of another.
        public static UpscalerBackend Technique(GpuClass gpu, bool dlssUsable, bool amdDllOffered, bool xessDllOffered)
        {
            switch (gpu)
            {
                case GpuClass.Rtx20:
                case GpuClass.Rtx30:
                case GpuClass.Rtx40:
                case GpuClass.Rtx50:
                    return dlssUsable ? UpscalerBackend.Dlss : UpscalerBackend.Fsr3;
                case GpuClass.Radeon7000:
                case GpuClass.Radeon9000:
                    return amdDllOffered ? UpscalerBackend.Amd : UpscalerBackend.Fsr3;
                case GpuClass.Intel:
                    return xessDllOffered ? UpscalerBackend.Xess : UpscalerBackend.Fsr3;
                default:
                    return UpscalerBackend.Fsr3;
            }
        }

        // The mode whose render height, at this screen, is nearest the height the profile aims for
        // (by the ratio of the two, so 960 of 1440 and 1920 of 2880 are equally near). DLSS has no
        // 1.2x: it runs that mode as its Quality, so it is not a choice there.
        public static Fsr3Upscaler.QualityMode Mode(int screenHeight, int targetHeight, bool dlss)
        {
            if (screenHeight <= 0 || targetHeight <= 0) return Fsr3Upscaler.QualityMode.NativeAA;
            Fsr3Upscaler.QualityMode best = Fsr3Upscaler.QualityMode.NativeAA;
            double bestDistance = double.MaxValue;
            foreach (Fsr3Upscaler.QualityMode mode in Modes)
            {
                if (dlss && mode == Fsr3Upscaler.QualityMode.UltraQuality) continue;
                double rendered = screenHeight / (double)Fsr3Upscaler.GetUpscaleRatioFromQualityMode(mode);
                double distance = Math.Abs(Math.Log(rendered / targetHeight));
                // The earlier mode, the sharper one, on a tie.
                if (distance < bestDistance - 1e-9)
                {
                    bestDistance = distance;
                    best = mode;
                }
            }
            return best;
        }

        // Ray Reconstruction on an RTX: from Full on an RTX 20 or 30, whose tensor cores take it
        // about 9 ms of the A4500's frame, from the least raytracing on an RTX 40 or 50. Off
        // where DLSS does not run, since it needs it.
        public static bool RayReconstruction(GpuClass gpu, UpscalerBackend backend, out RaytracingMode from)
        {
            from = GpuClasses.IsRtx20Or30(gpu) ? RaytracingMode.Full : RaytracingMode.Shadows;
            bool rtx = gpu == GpuClass.Rtx20 || gpu == GpuClass.Rtx30 || gpu == GpuClass.Rtx40 || gpu == GpuClass.Rtx50;
            return rtx && backend == UpscalerBackend.Dlss;
        }

        // Fills in what the Auto flags of `settings` stand for, for the card and the screen given.
        public static void Resolve(OwnSettings settings, GpuClass gpu, int screenHeight, bool dlssUsable, bool amdDllOffered,
                                   bool xessDllOffered)
        {
            // What Auto is, kept for the rows to name whether or not it is the player's choice now.
            ResolvedBackend = Technique(gpu, dlssUsable, amdDllOffered, xessDllOffered);
            if (settings.BackendAuto) settings.Backend = ResolvedBackend;
            ResolvedQuality = Mode(screenHeight, settings.RenderTargetHeight, settings.Backend == UpscalerBackend.Dlss);
            if (settings.QualityAuto) settings.Quality = ResolvedQuality;
            RaytracingMode from;
            ResolvedRayReconstruction = RayReconstruction(gpu, settings.Backend, out from);
            ResolvedRayReconstructionFrom = from;
            if (settings.RayReconstructionAuto)
            {
                settings.RayReconstruction = ResolvedRayReconstruction;
                settings.RayReconstructionFrom = from;
            }
        }

        // The frame generation technique a choice stands for: itself, or for Auto the card's own where it
        // runs here -- DLSS on an RTX, XeSS on an Intel GPU -- else FSR 3, else whichever other runs; FSR 3
        // where none does (the row greys Auto out then). Written into the proxy's ini, which makes its
        // swapchain for it when KSP starts.
        public static string FrameGenerationTechnique(string chosen, TechniqueAvailability.Facts f)
        {
            if (chosen != FrameGenerationTechniqueSetting.Auto) return chosen;
            string[] order = GpuClasses.IsRtx(f.Gpu)
                ? new[] { FrameGenerationTechniqueSetting.Dlss, FrameGenerationTechniqueSetting.Fsr, FrameGenerationTechniqueSetting.Xess }
                : f.Gpu == GpuClass.Intel
                    ? new[] { FrameGenerationTechniqueSetting.Xess, FrameGenerationTechniqueSetting.Fsr }
                    : new[] { FrameGenerationTechniqueSetting.Fsr, FrameGenerationTechniqueSetting.Xess };
            foreach (string technique in order)
                if (TechniqueAvailability.Ready(technique, f)) return technique;
            return FrameGenerationTechniqueSetting.Fsr;
        }

        // What Resolve depends on besides the settings, for the add-on to settle Auto again when
        // one changes: the card, the screen's height, and whether DLSS, AMD's DLL and Intel's can run
        // (the proxy attaches them after the settings load, a download places them).
        public static string Inputs()
        {
            TechniqueAvailability.Facts f = TechniqueAvailability.Current();
            return GpuClasses.Current + "|" + Screen.height + "|" + DlssUsable() + "|" + AmdUsable(f) + "|" + XessUsable(f);
        }

        // AMD's DLL and Intel's offered by the proxy and in their folders.
        private static bool AmdUsable(TechniqueAvailability.Facts f)
        {
            return f.AmdOffered && !f.AmdDllMissing;
        }

        // Whether XeSS can run: the proxy offers it and Intel's DLL is in its folder.
        private static bool XessUsable(TechniqueAvailability.Facts f)
        {
            return f.XessOffered && !f.XessDllMissing;
        }

        // Whether DLSS can run: the proxy has it, the GPU is NVIDIA's, and NVIDIA's DLSS file is in its
        // folder. The folder is looked at every two seconds, not in every frame.
        private static float dlssAskedAt = float.MinValue;
        private static bool dlssUsable;

        // The answer above, asked anew at most every two seconds.
        public static bool DlssUsable()
        {
            float now = Time.realtimeSinceStartup;
            if (dlssAskedAt != float.MinValue && now - dlssAskedAt < 2f) return dlssUsable;
            dlssAskedAt = now;
            dlssUsable = DlssBridge.Offered && !DlssFileMissing();
            return dlssUsable;
        }

        // Whether a DLL NVIDIA's DLSS needs is missing from the folders (licence texts do not count); false
        // on a GPU that is not NVIDIA's.
        private static bool DlssFileMissing()
        {
            foreach (NvidiaFiles.Entry entry in NvidiaDownloader.Needed())
                if (!entry.Notice && entry.Use == NvidiaFiles.Use.Dlss) return true;
            return false;
        }

        // For the card and screen KSP runs on.
        public static void Resolve(OwnSettings settings)
        {
            TechniqueAvailability.Facts f = TechniqueAvailability.Current();
            Resolve(settings, GpuClasses.Current, Screen.height, DlssUsable(), AmdUsable(f), XessUsable(f));
        }
    }
}
