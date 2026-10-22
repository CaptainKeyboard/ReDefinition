using System.Collections.Generic;
using ReDefinition.Bridges;
using ReDefinition.Settings;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Upscaler
{
    // Which techniques of the upscaler row and the frame generation row can run now, and for the
    // ones that cannot, why in a few words: the tooltip of their greyed button. What can be
    // downloaded or installed to enable one stands under the row (SettingsWindow).
    internal static class TechniqueAvailability
    {
        // What the reasons rest on.
        internal struct Facts
        {
            public GpuClass Gpu;
            // The proxy's DLSS and its frame generation.
            public bool DlssOffered;
            public bool CanGenerate;
            public bool AmdOffered;
            public bool XessOffered;
            // AMD's upscaler DLL and Intel's libxess.dll are not where the proxy loads them from.
            public bool AmdDllMissing;
            public bool XessDllMissing;
            // NVIDIA's nvngx_dlss.dll, and Streamline's files with the DLSS-G library, are missing.
            public bool DlssFileMissing;
            public bool StreamlineMissing;
            // The package for an RTX 30 or 20 is in place.
            public bool AmperePackage;
            // Intel's libxess_fg.dll and libxell.dll are missing.
            public bool XessFgMissing;
        }

        private static Facts facts;
        private static float askedAt = float.MinValue;

        // Asked once a second: the rows ask in every frame, and the files are on disk.
        public static Facts Current()
        {
            float now = Time.realtimeSinceStartup;
            if (askedAt != float.MinValue && now - askedAt < 1f) return facts;
            askedAt = now;
            Facts found = new Facts
            {
                Gpu = GpuClasses.Current,
                DlssOffered = DlssBridge.Offered,
                CanGenerate = FrameGenerationBridge.CanGenerate,
                AmdOffered = AmdUpscalerBridge.Offered,
                XessOffered = XessUpscalerBridge.Offered,
                AmperePackage = AmpereFrameGeneration.Installed(),
            };
            try
            {
                foreach (NvidiaFiles.Entry entry in NvidiaDownloader.Needed())
                {
                    if (entry.Notice) continue;
                    if (entry.Use == NvidiaFiles.Use.Dlss) found.DlssFileMissing = true;
                    else found.StreamlineMissing = true;
                }
                IntelFiles.Uses(IntelDownloader.Needed(), out found.XessDllMissing, out found.XessFgMissing);
                string amdFolder = ProxyIni.Get("amdUpscalerDirectory");
                found.AmdDllMissing = !System.IO.File.Exists(System.IO.Path.Combine(
                    string.IsNullOrEmpty(amdFolder) ? NvidiaDownloader.GameFolder : amdFolder, "amd_fidelityfx_upscaler_dx12.dll"));
            }
            catch (System.Exception)
            {
                // Looked for again in a second.
            }
            facts = found;
            return facts;
        }

        // Null where the upscaler row's choice can run; why not otherwise.
        public static string Upscaler(string choice, Facts f)
        {
            switch (choice)
            {
                case "Dlss":
                    if (!GpuClasses.IsRtx(f.Gpu)) return "Needs a GeForce RTX card.";
                    if (!f.DlssOffered) return "Needs the dxgi.dll proxy next to KSP_x64.exe.";
                    if (f.DlssFileMissing) return "NVIDIA's nvngx_dlss.dll is missing: download it below.";
                    return null;
                case "Amd":
                    if (!f.AmdOffered) return "Needs the dxgi.dll proxy, presenting through Direct3D 12.";
                    return f.AmdDllMissing
                        ? "Needs AMD's amd_fidelityfx_upscaler_dx12.dll next to KSP_x64.exe, from AMD's FidelityFX SDK."
                        : null;
                case "Xess":
                    if (!f.XessOffered) return "Needs the dxgi.dll proxy, presenting through Direct3D 12.";
                    return f.XessDllMissing ? "Intel's libxess.dll is missing: download it below." : null;
                default:
                    return null;
            }
        }

        // Null where the frame generation row's choice can run; why not otherwise.
        public static string FrameGeneration(string choice, Facts f)
        {
            if (choice == FrameGenerationTechniqueSetting.Off) return null;
            if (!f.CanGenerate) return "Needs the dxgi.dll proxy next to KSP_x64.exe.";
            switch (choice)
            {
                case FrameGenerationTechniqueSetting.Dlss:
                    if (!GpuClasses.IsRtx(f.Gpu)) return "Needs a GeForce RTX card.";
                    if (GpuClasses.IsRtx20Or30(f.Gpu) && !f.AmperePackage)
                        return "An RTX 30 or 20 needs the dlssg_for_sm86 package: install it below.";
                    if (f.StreamlineMissing) return "NVIDIA's Streamline files are missing: download them below.";
                    return null;
                case FrameGenerationTechniqueSetting.Xess:
                    return f.XessFgMissing ? "Intel's libxess_fg.dll and libxell.dll are missing: download them below." : null;
                default:
                    return null;
            }
        }

        // Null where the multiplier can run with the technique chosen; why not otherwise. The technique
        // that runs is known to the proxy (limits); one chosen but not running yet is judged by what it
        // does everywhere.
        public static string Step(string step, string technique, FrameGenerationChoice.Limits limits)
        {
            int kind = technique == FrameGenerationTechniqueSetting.Fsr ? 1
                : technique == FrameGenerationTechniqueSetting.Dlss ? 2
                : technique == FrameGenerationTechniqueSetting.Xess ? 3
                : limits.Known ? limits.Technique : 0;
            if (kind != 0 && limits.Known && limits.Technique == kind)
            {
                if (FrameGenerationChoice.Offered(step, limits)) return null;
                return FrameGenerationChoice.StepReason(step, limits);
            }
            // Not running (yet): what the technique does on any card.
            switch (kind)
            {
                case 1:
                    return step == "2x" ? null
                        : step == FrameGenerationChoice.Dynamic ? "Dynamic is DLSS frame generation only."
                        : "FSR 3 generates one frame between two.";
                case 3:
                    return step == FrameGenerationChoice.Dynamic ? "Dynamic is DLSS frame generation only."
                        : FrameGenerationChoice.Multiplier(step) > 4 ? "XeSS frame generation generates up to 4x." : null;
                default:
                    return null;
            }
        }
    }
}
