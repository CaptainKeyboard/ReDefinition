using System;
using ReDefinition.Proxy;
using ReDefinition.Settings;
using ReDefinition.Vendor;
using UnityEngine;

namespace ReDefinition.Modules
{
    // Which techniques of the upscaler row and the frame generation row the card and the proxy can run, and
    // which of those still wait for files. A button the card or the proxy rules out is greyed out, its
    // tooltip saying why. A button for a technique the card can run is lit even where its files are
    // missing: choosing it opens the download or the steps for them (TechniqueFiles), and only once they
    // are in place is the technique chosen.
    internal static class TechniqueAvailability
    {
        // What a technique waits for.
        [Flags]
        internal enum Needs
        {
            // Nothing: the technique can be chosen at once.
            None = 0,
            // NVIDIA's DLLs (DLSS, Streamline), from NVIDIA's download.
            NvidiaFiles = 1,
            // Intel's XeSS DLLs, from Intel's download.
            IntelFiles = 2,
            // AMD's upscaler DLL, which the player places in its folder.
            AmdDll = 4,
            // The dlssg_for_sm86 package that runs DLSS frame generation on an RTX 30 or 20.
            AmperePackage = 8,
        }

        // What the reasons rest on.
        internal struct Facts
        {
            // The class of the graphics card KSP renders on.
            public GpuClass Gpu;
            // The proxy is there; its DLSS; a frame generation swapchain of it presents now.
            public bool ProxyPresent;
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
            // AMD's frame generation runtime, which comes with ReDefinition, is not next to KSP_x64.exe.
            public bool AmdFgDllMissing;
        }

        // Why a technique the card cannot run is greyed out: the button's own tooltip says which cards run it.
        internal const string NotThisCard = "Not on this card.";

        // The facts last gathered and when (realtimeSinceStartup), for Current to answer from within a second.
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
                ProxyPresent = FrameGenerationBridge.Available,
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
                // The proxy loads it from next to KSP_x64.exe only (FidelityFx.cpp).
                found.AmdFgDllMissing = !System.IO.File.Exists(System.IO.Path.Combine(NvidiaDownloader.GameFolder,
                    "amd_fidelityfx_framegeneration_dx12.dll"));
            }
            catch (Exception)
            {
                // Looked for again in a second.
            }
            facts = found;
            return facts;
        }

        // Null where the card and the proxy can run the upscaler row's choice; why not otherwise.
        public static string Upscaler(string choice, Facts f)
        {
            switch (choice)
            {
                case "Dlss":
                    if (!GpuClasses.IsRtx(f.Gpu)) return NotThisCard;
                    return f.DlssOffered ? null : "Needs the dxgi.dll proxy next to KSP_x64.exe.";
                case "Amd":
                    if (f.Gpu != GpuClass.Radeon9000 && f.Gpu != GpuClass.Radeon7000)
                        return NotThisCard;
                    return f.AmdOffered ? null : "Needs the dxgi.dll proxy, presenting through Direct3D 12.";
                case "Xess":
                    return f.XessOffered ? null : "Needs the dxgi.dll proxy, presenting through Direct3D 12.";
                default:
                    return null;
            }
        }

        // What a choice the card can run still waits for; choosing it opens the way to it.
        public static Needs UpscalerNeeds(string choice, Facts f)
        {
            switch (choice)
            {
                case "Dlss": return f.DlssFileMissing ? Needs.NvidiaFiles : Needs.None;
                case "Amd": return f.AmdDllMissing ? Needs.AmdDll : Needs.None;
                case "Xess": return f.XessDllMissing ? Needs.IntelFiles : Needs.None;
                default: return Needs.None;
            }
        }

        // The techniques Auto's row asks about, for whether any of them runs here.
        private static readonly string[] AnyOrder =
            { FrameGenerationTechniqueSetting.Dlss, FrameGenerationTechniqueSetting.Fsr, FrameGenerationTechniqueSetting.Xess };

        // Null where the card and the proxy can run the frame generation row's choice; why not otherwise.
        public static string FrameGeneration(string choice, Facts f)
        {
            if (choice == FrameGenerationTechniqueSetting.Off) return null;
            const string runtimeMissing = "AMD's amd_fidelityfx_framegeneration_dx12.dll, which comes with ReDefinition, is missing"
                                          + " next to KSP_x64.exe: install ReDefinition again.";
            if (!f.ProxyPresent) return "Needs the dxgi.dll proxy next to KSP_x64.exe.";
            switch (choice)
            {
                case FrameGenerationTechniqueSetting.Auto:
                    // The best that runs here (AutoSettings.FrameGenerationTechnique): greyed out only where none does.
                    foreach (string technique in AnyOrder)
                        if (Ready(technique, f)) return null;
                    return "No frame generation runs here yet: " + (FrameGeneration(FrameGenerationTechniqueSetting.Fsr, f)
                                                                     ?? "each technique waits for its files.");
                case FrameGenerationTechniqueSetting.Dlss:
                    // Its own swapchain, from the next start on: neither AMD's runtime nor what generates now matters.
                    return GpuClasses.IsRtx(f.Gpu) ? null : NotThisCard;
                case FrameGenerationTechniqueSetting.Xess:
                    return null;
                default:
                    // FSR 3.
                    if (f.AmdFgDllMissing) return runtimeMissing;
                    return f.CanGenerate ? null : "Frame generation could not be set up: ReDefinitionProxy.log says why.";
            }
        }

        // A frame generation technique that runs here with nothing to fetch: not greyed out, nothing missing.
        public static bool Ready(string technique, Facts f)
        {
            return FrameGeneration(technique, f) == null && FrameGenerationNeeds(technique, f) == Needs.None;
        }

        // What a frame generation choice the card can run still waits for: the RTX 30/20 package and NVIDIA's
        // files for DLSS, Intel's files for XeSS; FSR 3's runtime comes with ReDefinition.
        public static Needs FrameGenerationNeeds(string choice, Facts f)
        {
            Needs needs = Needs.None;
            if (choice == FrameGenerationTechniqueSetting.Dlss)
            {
                // Without the package NVIDIA's frame generation files are not yet among the missing: they are
                // looked for again once it is in place.
                if (GpuClasses.IsRtx20Or30(f.Gpu) && !f.AmperePackage) needs |= Needs.AmperePackage | Needs.NvidiaFiles;
                if (f.StreamlineMissing) needs |= Needs.NvidiaFiles;
            }
            else if (choice == FrameGenerationTechniqueSetting.Xess && f.XessFgMissing)
            {
                needs |= Needs.IntelFiles;
            }
            return needs;
        }

        // Null where the multiplier can run with the technique chosen; why not otherwise. The technique
        // that runs is known to the proxy (limits); one chosen but not running yet is judged by what it
        // does everywhere.
        public static string Step(string step, string technique, FrameGenerationChoice.Limits limits)
        {
            int kind = FrameGenerationTechniqueSetting.KindOfNamed(technique);
            if (kind == FrameGenerationTechniqueSetting.KindNone && limits.Known) kind = limits.Technique;
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
