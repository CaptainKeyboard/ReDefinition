using System;
using ReDefinition.Bridges;
using ReDefinition.Settings;
using ReDefinition.Window;
using UnityEngine;

namespace ReDefinition.Upscaler
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
            None = 0,
            NvidiaFiles = 1,
            IntelFiles = 2,
            AmdDll = 4,
            AmperePackage = 8,
        }

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
                    if (!GpuClasses.IsRtx(f.Gpu)) return "Needs a GeForce RTX card.";
                    return f.DlssOffered ? null : "Needs the dxgi.dll proxy next to KSP_x64.exe.";
                case "Amd":
                    if (f.Gpu != GpuClass.Radeon9000 && f.Gpu != GpuClass.Radeon7000)
                        return "FSR 4 runs on a Radeon RX 7000 or RX 9000 only.";
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

        // Null where the card and the proxy can run the frame generation row's choice; why not otherwise.
        public static string FrameGeneration(string choice, Facts f)
        {
            if (choice == FrameGenerationTechniqueSetting.Off) return null;
            if (!f.CanGenerate) return "Needs the dxgi.dll proxy next to KSP_x64.exe.";
            if (choice == FrameGenerationTechniqueSetting.Dlss && !GpuClasses.IsRtx(f.Gpu)) return "Needs a GeForce RTX card.";
            return null;
        }

        public static Needs FrameGenerationNeeds(string choice, Facts f)
        {
            Needs needs = Needs.None;
            if (choice == FrameGenerationTechniqueSetting.Dlss)
            {
                if (GpuClasses.IsRtx20Or30(f.Gpu) && !f.AmperePackage) needs |= Needs.AmperePackage;
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
